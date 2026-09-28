using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Netcode;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Pathfinding;
using BetterJunimos.Utils;

namespace BetterJunimos.Assign {
    /* WorkAssigner — two-mode assignment scheduling ("两模式分配制", v2).
     *
     * Implements E:\betterjunimos-work\build-assign2\DESIGN-v2.md (user-finalised design).
     * Farm reality it models: crops ripen once in a mass, then only sporadic stragglers
     * appear. So there are NO periodic re-scans and NO re-assignments, ever:
     *
     *   Bulk phase    : once per day, at the first trigger that needs work (spawn gate or
     *                   a junimo seeking work), one full box inventory → one serpentine
     *                   (S-shaped row-by-row) walk over every actionable tile → cut into
     *                   k = MaxJunimosUnlocked slices of roughly EQUAL TIME
     *                   (cost model = v1 Cost: 20t per serpentine step + 120t/61t harvest
     *                   per tile). Each junimo claims one whole contiguous slice. After
     *                   the plan: zero re-scan, zero re-assign. Only three maintenance
     *                   rules, all event-driven (v1's JunimoDecision skeleton):
     *                     a) consumed path head → pop & advance (arrival / harvest-timer
     *                        expiry / 10-min poke / idle roll);
     *                     b) junimo death → its remaining slice moves WHOLE into the hut's
     *                        orphan list; taken over whole (never split) by a respawner or
     *                        the first junimo to finish;
     *                     c) unreachable tile (budget 10000) → blacklist, pop, continue —
     *                        no retry storm (≤4 pathfind attempts per decision).
     *   Wind-down     : when a junimo's slice runs empty and no orphans/unassigned slices
     *                   remain, it runs ONE wind-down inventory (box scan for unregistered
     *                   actionable tiles, ownership-filtered) and chains them nearest-first
     *                   into a fresh continuous path. Once per junimo per day.
     *   Endgame phase : a junimo whose work always comes up empty enters endgame mode:
     *                   nearest-to-SELF heuristic, one task at a time. Scans are on-demand
     *                   (only when someone has nothing to do) and hard-rate-limited per hut
     *                   (EndgameScanIntervalSeconds, default 15s); results (≤16 tiles,
     *                   near-to-far, early exit) go into a hut-shared pool that every idle
     *                   junimo drains with zero tile reads. Junimos with nothing to do go
     *                   HOME (vanilla returnToJunimoHut → despawn), never stand in the field;
     *                   the spawn gate (HasWorkFor) lets them out again when work appears.
     *   Mutual excl.  : the v1 global one-tile-one-owner registry is kept verbatim and also
     *                   covers endgame self-assignments; rows drop on consumption / whiff /
     *                   death / day rollover.
     *   Dual huts     : each hut plans its own box; at every scan a tile belongs to the
     *                   strictly nearer hut, ties (equal distance) to the lexicographically
     *                   smaller hut tile (X then Y) — pure deterministic geometry, both huts
     *                   compute the same verdict independently. A hut built/moved mid-season
     *                   just re-computes ownership at its next plan; walks already in
     *                   progress are never interrupted (they harvest real crops, harmless).
     *   Greenhouse    : Handles() keeps the v1 semantics — only junimos on their own hut's
     *                   work location are managed; greenhouse junimos keep shipped behaviour.
     *   Config off    : WorkAssignment=false bypasses every hook (Active/Handles), exactly
     *                   the shipped throttle/gate/claim behaviour.
     *
     *   No async layer (judged a loss in measurement, permanently shelved): direct
     *   pathfinding runs on the main thread with the game's own returnToJunimoHut budget
     *   (10000). The dormant Assign/AsyncPath.cs from v1 stays in the tree but is only
     *   compiled under ASYNC_PATHFIND, which assign2 never defines.
     */
    internal static class WorkAssigner {
        // anti-confusion build id (see banner in BetterJunimos.Entry)
        internal const string BuildId = "assign2.1-plain";

        // direct pathfinding budget: the game's own value for returnToJunimoHut (v1 §8 rev 2)
        internal const int DirectPathBudget = 10000;
        // cost model (v1): 20 t per tile of walking + the real harvest placeholder
        private const int TicksPerTile = 20;
        private const int HarvestTicksWorkFaster = 61;   // WF skips the last second (mod intent)
        private const int HarvestTicksVanilla = 120;     // 2000 ms minus the 1000 ms crossing
        // endgame scan result cap: near-to-far sweep stops once the shared pool holds this many
        private const int EndgamePoolCap = 16;
        // pathfind attempts per decision before the junimo sleeps until the next event (§3c)
        private const int MaxConsecutivePathFails = 4;

        // master gate: config switch + master game only (farmhands keep vanilla behaviour)
        internal static bool Active => BetterJunimos.Config.WorkAssignment && Context.IsMainPlayer;

        private static int EndgameIntervalTicks {
            get {
                var s = BetterJunimos.Config.EndgameScanIntervalSeconds;
                if (s < 5) s = 5;
                if (s > 120) s = 120;
                return s * 60;
            }
        }

        private sealed class Segment {
            public readonly List<Point> Tiles = new();
            public JunimoHarvester Owner;   // null = unclaimed
        }

        private sealed class HutState {
            public JunimoHut Hut;
            public Guid Id;
            public GameLocation Location;
            public bool Planned;                  // today's bulk inventory + snake cut done
            public bool BulkEndedLogged;
            public readonly List<Segment> Segments = new();       // snake slices, snake order
            public readonly List<List<Point>> Orphans = new();    // dead junimos' remaining slices (whole, FIFO)
            public readonly List<Point> EndgamePool = new();      // shared known-unharvested tiles (≤16)
            public readonly HashSet<Point> Unreachable = new();   // pathfind failures (§3c)
            // v2.1: per-tile pathfail tally — a tile is only blacklisted once ≥2 DISTINCT
            // junimos failed to path to it; a single failure is treated as transient
            public readonly Dictionary<Point, HashSet<JunimoHarvester>> PathFails = new();
            public int NextScanTick;                              // endgame scan hard rate gate
        }

        // main-thread-only state
        private static readonly Dictionary<JunimoHut, HutState> Huts = new();
        private static bool HutsDirty = true;
        private static int HutRefreshTick = -61;
        private static int LastSweepTick;

        // junimo → its remaining path (head = current walking target) + the location it is on
        private static readonly Dictionary<JunimoHarvester, (GameLocation Loc, List<Point> Tiles)> Routes = new();
        // global one-tile-one-owner registry, cross-hut (v1 §3.3, kept verbatim)
        private static readonly Dictionary<(GameLocation, Point), JunimoHarvester> RouteRegistry = new();
        // junimos that entered endgame mode / already did their wind-down inventory (per day)
        private static readonly HashSet<JunimoHarvester> EndgameJunimos = new();
        private static readonly HashSet<JunimoHarvester> DidWindDown = new();
        // several triggers can fire in the same frame; one decision per junimo per tick (v1)
        private static readonly Dictionary<JunimoHarvester, int> LastDecisionTick = new();

        // ---------------------------------------------------------------- lifecycle

        internal static void OnDayStarted() {
            // day rollover: junimos are removed by BetterJunimos.OnDayStarted; drop everything
            Routes.Clear();
            RouteRegistry.Clear();
            LastDecisionTick.Clear();
            EndgameJunimos.Clear();
            DidWindDown.Clear();
            Huts.Clear();
            HutsDirty = true;
        }

        // save loaded / returned to title / day ending / building list changed
        internal static void OnWorldInvalidated() => OnDayStarted();

        // ---------------------------------------------------------------- main tick

        internal static void OnUpdateTicking(object sender, UpdateTickingEventArgs e) {
            if (!Active || !Context.IsWorldReady) return;

            RefreshHuts();

            // defensive sweep once a second: junimos that vanished without an event
            var now = Game1.ticks;
            if (now - LastSweepTick >= 60) {
                LastSweepTick = now;
                SweepStale();
                LogBulkEnded();
            }
        }

        private static void RefreshHuts() {
            var now = Game1.ticks;
            if (!HutsDirty && now - HutRefreshTick < 60) return;   // re-check once a second
            HutRefreshTick = now;
            HutsDirty = false;

            var live = Util.GetAllHuts();
            foreach (var stale in Huts.Keys.Where(h => !live.Contains(h)).ToList()) {
                foreach (var j in stale.myJunimos.ToList()) {
                    ReleaseRoute(j);
                    ForgetJunimo(j);
                }
                Huts.Remove(stale);
            }

            foreach (var hut in live) {
                if (Huts.ContainsKey(hut)) continue;
                Huts[hut] = new HutState {
                    Hut = hut,
                    Id = Util.GetHutIdFromHut(hut),
                };
            }

            BetterJunimos.SMonitor.Log($"[BJ-assign2] active: huts={live.Count}, " +
                                       $"endgameScan={EndgameIntervalTicks / 60}s, build={BuildId}", LogLevel.Trace);
        }

        /// <summary>Defensive release for junimos that vanished without an event (v1 §6,
        /// now orphaning instead of dropping). Bounded by route count, once a second.</summary>
        private static void SweepStale() {
            List<JunimoHarvester> dead = null;
            foreach (var kv in Routes) {
                var j = kv.Key;
                if (j == null || IsDestroyed(j) || j.currentLocation == null || j.home == null) {
                    (dead ??= new List<JunimoHarvester>()).Add(j);
                }
            }
            if (dead != null) {
                foreach (var j in dead) OrphanRoute(j);
            }

            List<(GameLocation, Point)> staleRows = null;
            foreach (var kv in RouteRegistry) {
                var o = kv.Value;
                if (o == null || IsDestroyed(o) || o.currentLocation == null) {
                    (staleRows ??= new List<(GameLocation, Point)>()).Add(kv.Key);
                }
            }
            if (staleRows != null) {
                foreach (var k in staleRows) RouteRegistry.Remove(k);
            }
        }

        // one-time-per-day log: this hut's bulk phase is over
        private static void LogBulkEnded() {
            foreach (var st in Huts.Values) {
                if (st.BulkEndedLogged || !st.Planned) continue;
                var hut = st.Hut;
                if (hut == null) continue;
                var allEndgame = hut.myJunimos.Count == 0 || hut.myJunimos.All(m => EndgameJunimos.Contains(m));
                var nothingBulkLeft = st.Orphans.Count == 0 && st.Segments.All(s => s.Owner != null) && st.EndgamePool.Count == 0;
                if (!allEndgame || !nothingBulkLeft) continue;
                st.BulkEndedLogged = true;
                BetterJunimos.SMonitor.Log($"[BJ-assign2] hut@({hut.tileX.Value},{hut.tileY.Value}): " +
                                           "bulk phase complete — endgame mode only until tomorrow", LogLevel.Trace);
            }
        }

        // ---------------------------------------------------------------- ownership

        /// <summary>Huts on the same location as `st` (pure geometry rivals, rebuilt per scan).</summary>
        private static List<Point> BuildRivals(HutState st, GameLocation loc) {
            var rivals = new List<Point>();
            var name = loc.NameOrUniqueName;
            foreach (var other in Util.GetAllHuts()) {
                if (ReferenceEquals(other, st.Hut)) continue;
                var ol = other.GetParentLocation();
                if (ol == null || ol.NameOrUniqueName != name) continue;
                rivals.Add(new Point(other.tileX.Value, other.tileY.Value));
            }
            return rivals;
        }

        /// <summary>Deterministic pure-geometry ownership: the strictly nearer hut wins;
        /// equidistant tiles belong to the lexicographically smaller hut tile (X then Y).
        /// No RNG, no iteration order — both huts compute the identical verdict (DESIGN §5).</summary>
        private static bool OwnedBy(HutState st, Point tile, List<Point> rivals) {
            var cx = st.Hut.tileX.Value;
            var cy = st.Hut.tileY.Value;
            var myDist = Math.Abs(tile.X - (cx + 1)) + Math.Abs(tile.Y - (cy + 1));
            foreach (var r in rivals) {
                var od = Math.Abs(tile.X - (r.X + 1)) + Math.Abs(tile.Y - (r.Y + 1));
                if (od < myDist) return false;
                if (od == myDist && (r.X < cx || (r.X == cx && r.Y < cy))) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- bulk plan

        /// <summary>The day's single box inventory + serpentine equal-time cut. Runs at the
        /// first trigger that needs work after a reset; never again for the rest of the day.</summary>
        private static void EnsurePlanned(HutState st) {
            var hut = st.Hut;
            if (hut == null) return;
            var loc = hut.GetParentLocation();
            if (loc == null) return;
            st.Location = loc;
            if (st.Planned) return;
            // don't lock the day's only plan to the shrunken unpaid radius: shipped behaviour
            // keeps junimos idle until paid anyway, so plan at the first paid trigger instead
            if (BetterJunimos.Config.JunimoPayment.WorkForWages && !Util.Payments.WereJunimosPaidToday) return;
            PlanBulk(st);
        }

        private static void PlanBulk(HutState st) {
            st.Planned = true;
            st.BulkEndedLogged = false;
            st.Segments.Clear();
            st.Orphans.Clear();
            st.EndgamePool.Clear();
            st.Unreachable.Clear();
            st.PathFails.Clear();

            var hut = st.Hut;
            var loc = st.Location;
            if (hut == null || loc == null) return;
            var id = st.Id = Util.GetHutIdFromHut(hut);

            // ---- box inventory (game-real predicate, ownership-filtered; v1 §2.1 box convention)
            var radius = Util.CurrentWorkingRadius;
            var cx = hut.tileX.Value + 1;
            var cy = hut.tileY.Value + 1;
            var rivals = BuildRivals(st, loc);
            var reads = 0;
            var t0 = Game1.ticks;
            var ripe = new List<Point>();
            if (!hut.noHarvest.Value) {
                for (var x = cx - radius; x <= cx + radius; x++) {
                    for (var y = cy - radius; y <= cy + radius; y++) {
                        reads++;
                        var p = new Point(x, y);
                        if (!OwnedBy(st, p, rivals)) continue;
                        if (Util.Abilities.IdentifyJunimoAbility(loc, new Vector2(x, y), id) != null) {
                            ripe.Add(p);
                        }
                    }
                }
            }

            // ---- serpentine + equal-time cut
            var snake = BuildSnake(ripe, cx, cy);
            var k = Math.Max(1, Util.Progression.MaxJunimosUnlocked);
            st.Segments.AddRange(CutSegments(snake, k));

            if (BetterJunimos.Config.DebugLog) {
                var sizes = string.Join(",", st.Segments.Select(s => s.Tiles.Count));
                BetterJunimos.SMonitor.Log(
                    $"[BJ-assign2] bulk plan hut@({hut.tileX.Value},{hut.tileY.Value}): " +
                    $"reads={reads}, ticks={Game1.ticks - t0}, ripe={ripe.Count}, k={k}, segs=[{sizes}]", LogLevel.Debug);
            } else {
                BetterJunimos.SMonitor.Log(
                    $"[BJ-assign2] bulk plan hut@({hut.tileX.Value},{hut.tileY.Value}): " +
                    $"ripe={ripe.Count}, k={k}, segs={st.Segments.Count}", LogLevel.Trace);
            }
        }

        /// <summary>S-shaped row-by-row order over the ripe tiles; the snake head is placed
        /// at the end nearer the hut to keep the first junimo's initial walk short (DESIGN §2.2).</summary>
        private static List<Point> BuildSnake(List<Point> ripe, int cx, int cy) {
            var snake = new List<Point>(ripe.Count);
            if (ripe.Count == 0) return snake;

            var rows = new SortedDictionary<int, List<int>>();
            foreach (var t in ripe) {
                if (!rows.TryGetValue(t.Y, out var xs)) rows[t.Y] = xs = new List<int>();
                xs.Add(t.X);
            }
            foreach (var xs in rows.Values) xs.Sort();

            var ys = rows.Keys.ToList();
            // traverse rows starting from the one nearer the hut row
            var topDown = Math.Abs(ys[0] - cy) <= Math.Abs(ys[ys.Count - 1] - cy);
            if (!topDown) ys.Reverse();

            // first row runs from the x-end nearer the hut column; rows then alternate
            var first = rows[ys[0]];
            var dirAsc = Math.Abs(first[0] - cx) <= Math.Abs(first[first.Count - 1] - cx);
            for (var i = 0; i < ys.Count; i++) {
                var xs = rows[ys[i]];
                var asc = i % 2 == 0 ? dirAsc : !dirAsc;
                if (asc) {
                    foreach (var x in xs) snake.Add(new Point(x, ys[i]));
                } else {
                    for (var n = xs.Count - 1; n >= 0; n--) snake.Add(new Point(xs[n], ys[i]));
                }
            }
            return snake;
        }

        /// <summary>Greedy equal-time cut of the snake into k contiguous slices (DESIGN §2.3):
        /// cost(tile_i) = harvestTicks + 20t × manhattan(tile_i, tile_{i−1}); the entering walk
        /// of a tile counts toward the slice containing that tile, so Σ slice times = total and
        /// the cut points are deterministic. v2.1: the accumulator is CUMULATIVE across cuts —
        /// the assign2-plain build reset it per segment while keeping the progressive
        /// thresholds total×(cuts+1)/k, which biased slice m to hold ≈m×total/k and collapsed
        /// k=6 to 3 slices (field log 2026-09-29: ripe=571 → segs=3 on all four huts).</summary>
        private static List<Segment> CutSegments(List<Point> snake, int k) {
            var segs = new List<Segment>();
            if (snake.Count == 0) return segs;
            var harvestTicks = Util.Progression.WorkFaster ? HarvestTicksWorkFaster : HarvestTicksVanilla;

            var total = 0L;
            for (var i = 0; i < snake.Count; i++) {
                total += harvestTicks + (i == 0 ? 0 : TicksPerTile * Dist(snake[i], snake[i - 1]));
            }

            var seg = new Segment();
            var acc = 0L;
            for (var i = 0; i < snake.Count; i++) {
                acc += harvestTicks + (i == 0 ? 0 : TicksPerTile * Dist(snake[i], snake[i - 1]));
                seg.Tiles.Add(snake[i]);
                if (segs.Count < k - 1 && acc >= (long)((double)total * (segs.Count + 1) / k)) {
                    segs.Add(seg);
                    seg = new Segment();
                }
            }
            if (seg.Tiles.Count > 0) segs.Add(seg);
            return segs;
        }

        // ---------------------------------------------------------------- decision entry

        /// <summary>Does assignment mode own this junimo's decisions? Only on the hut's own
        /// work location; greenhouse junimos keep the shipped reactive branch verbatim (v1).</summary>
        internal static bool Handles(JunimoHarvester j, JunimoHut hut) {
            if (!Active) return false;
            var loc = hut?.GetParentLocation();
            if (loc == null || j.currentLocation == null) return false;
            return j.currentLocation.NameOrUniqueName == loc.NameOrUniqueName;
        }

        /// <summary>Event-driven path execution (v1 skeleton). Called from
        /// PatchPathfindDoWork for every decision trigger while the junimo is idle.</summary>
        internal static void JunimoDecision(JunimoHarvester j, JunimoHut hut) {
            if (j.controller != null) return;   // already walking: poke is a no-op
            var now = Game1.ticks;
            if (LastDecisionTick.TryGetValue(j, out var last) && last == now) return;
            LastDecisionTick[j] = now;
            if (!Huts.TryGetValue(hut, out var st)) { HutsDirty = true; return; }
            var loc = j.currentLocation;
            if (loc == null) return;

            EnsurePlanned(st);
            if (st.Location == null) return;

            var failBudget = MaxConsecutivePathFails;
            if (Routes.TryGetValue(j, out var entry) && entry.Tiles.Count > 0) {
                var head = entry.Tiles[0];
                if (Util.Abilities.IsActionable(entry.Loc, new Vector2(head.X, head.Y), st.Id)) {
                    // untried head → this is a budget-out resume, not a consumption: walk it,
                    // don't pop (a popped head here would silently skip a live tile)
                } else {
                    // consumed (harvested) or whiffed (shadow-harvested while walking): pop it
                    entry.Tiles.RemoveAt(0);
                    DropRegistry(entry.Loc, head, j);
                }
                if (AdvanceInto(j, st, ref failBudget)) return;
            } else if (Routes.ContainsKey(j)) {
                Routes.Remove(j);   // defensive: empty entry
            }

            WorkSeek(j, st, failBudget);
        }

        /// <summary>Spawn goes straight to work. Called from Util.SpawnJunimoAtPosition.
        /// v2.1 birth guarantee: the constructor's stroll controller is only cancelled when
        /// WorkSeek can immediately hand the junimo a work/home path; otherwise it is
        /// RESTORED, so the junimo always leaves the spawn tile within ticks and the stroll's
        /// arrival (a decision trigger) retries work-seeking.</summary>
        internal static void OnJunimoSpawned(JunimoHarvester j) {
            if (!Active) return;
            var hut = Util.GetHutFromId(j.HomeId);
            if (hut == null || !Huts.TryGetValue(hut, out var st)) { HutsDirty = true; return; }

            var stroll = j.controller;   // constructor's random stroll — our fallback
            j.controller = null;         // cancel it while we look for work
            LastDecisionTick[j] = Game1.ticks;
            EnsurePlanned(st);
            var failBudget = MaxConsecutivePathFails;
            WorkSeek(j, st, failBudget);

            if (j.controller == null) {
                // no work path (and no go-home path) could be given: restore the stroll so
                // the junimo still walks off the spawn tile; the stroll's endBehavior fires
                // tryToHarvestHere → pokeToHarvest → pathfindToNewCrop → JunimoDecision,
                // which re-enters WorkSeek on arrival
                j.controller = stroll;
                if (j.controller?.pathToEndPoint == null) j.pathfindToRandomSpotAroundHut();
                Dlog($"spawn j#{j.whichJunimoFromThisHut} hut@({hut.tileX.Value},{hut.tileY.Value}) " +
                     $"tile={j.TilePoint} -> no work yet, stroll restored (path={(j.controller?.pathToEndPoint != null)})");
            } else {
                Dlog($"spawn j#{j.whichJunimoFromThisHut} hut@({hut.tileX.Value},{hut.tileY.Value}) " +
                     $"tile={j.TilePoint} -> work/home path given");
            }
        }

        // ---------------------------------------------------------------- work seeking

        /// <summary>Unified work-seeking order (DESIGN §1.3): orphan slices → unassigned bulk
        /// slices → wind-down inventory (once) → endgame. Called with the junimo idle and
        /// routeless; `failBudget` bounds total pathfind attempts for this decision.</summary>
        private static void WorkSeek(JunimoHarvester j, HutState st, int failBudget) {
            var hut = st.Hut;
            if (hut == null || hut.noHarvest.Value) return;
            var loc = j.currentLocation;
            if (loc == null || st.Location == null || loc.NameOrUniqueName != st.Location.NameOrUniqueName) return;

            // 1) orphan slices: taken over WHOLE, FIFO (death takeover, never split)
            while (st.Orphans.Count > 0) {
                var tiles = st.Orphans[0];
                st.Orphans.RemoveAt(0);
                if (ClaimAndWalk(j, st, tiles, ref failBudget)) {
                    Dlog($"workseek j#{j.whichJunimoFromThisHut} hut@({hut.tileX.Value},{hut.tileY.Value}) -> orphan claimed ({tiles.Count} tiles)");
                    return;
                }
                if (Routes.ContainsKey(j)) {
                    Dlog($"workseek j#{j.whichJunimoFromThisHut} hut@({hut.tileX.Value},{hut.tileY.Value}) -> budget out on orphan, route held ({Routes[j].Tiles.Count} left), sleeping");
                    return;
                }
            }

            // 2) unclaimed bulk slices, snake order (first-finisher / late-spawn takeover)
            for (var i = 0; i < st.Segments.Count; i++) {
                var seg = st.Segments[i];
                if (seg.Owner != null) continue;
                seg.Owner = j;
                if (ClaimAndWalk(j, st, seg.Tiles, ref failBudget)) {
                    Dlog($"workseek j#{j.whichJunimoFromThisHut} hut@({hut.tileX.Value},{hut.tileY.Value}) -> segment {i + 1}/{st.Segments.Count} claimed ({seg.Tiles.Count} tiles)");
                    return;
                }
                if (Routes.ContainsKey(j)) {
                    Dlog($"workseek j#{j.whichJunimoFromThisHut} hut@({hut.tileX.Value},{hut.tileY.Value}) -> budget out on segment {i + 1}, route held ({Routes[j].Tiles.Count} left), sleeping");
                    return;
                }
                // fully-unreachable slice: consumed (tiles emptied + released), try the next one
            }

            // 3) wind-down inventory (bulk-era transition ritual, once per junimo per day):
            // skipped once every other junimo of the hut is already endgame (endgame era) —
            // there the rate-limited endgame scan is the cheaper straggler finder
            var bulkEra = hut.myJunimos.Any(m => !ReferenceEquals(m, j) && !EndgameJunimos.Contains(m));
            if (bulkEra && DidWindDown.Add(j)) {
                var tiles = WindDownInventory(st, j);
                if (tiles.Count > 0 && ClaimAndWalk(j, st, tiles, ref failBudget)) {
                    Dlog($"workseek j#{j.whichJunimoFromThisHut} hut@({hut.tileX.Value},{hut.tileY.Value}) -> wind-down chain ({tiles.Count} tiles)");
                    return;
                }
                if (Routes.ContainsKey(j)) return;
            }

            // 4) endgame mode
            EndgameJunimos.Add(j);
            EndgameSeek(j, st, failBudget);
        }

        /// <summary>Register `tiles` to the junimo as its path and start walking (DESIGN §3).</summary>
        private static bool ClaimAndWalk(JunimoHarvester j, HutState st, List<Point> tiles, ref int failBudget) {
            PruneForeign(tiles, st.Location, j);
            if (tiles.Count == 0) return false;
            Routes[j] = (st.Location, tiles);
            foreach (var t in tiles) RouteRegistry[(st.Location, t)] = j;
            return AdvanceInto(j, st, ref failBudget);
        }

        /// <summary>Walk the path onwards from the head: skip dead heads (one cheap read each),
        /// pathfind to the first live head; on failure skip + continue, bounded by
        /// failBudget (§3c). v2.1: a failed head is only BLACKLISTED once ≥2 distinct junimos
        /// have failed to path to it (NotePathFail); a first failure is treated as transient —
        /// the head is popped for this decision and the tile stays serviceable for the next
        /// scan, so one stuck junimo can never burn a batch of good crops. Returns true when
        /// the junimo is walking or safely parked for this decision (budget out with route
        /// remaining — resumes at the next decision).</summary>
        private static bool AdvanceInto(JunimoHarvester j, HutState st, ref int failBudget) {
            while (Routes.TryGetValue(j, out var entry) && entry.Tiles.Count > 0) {
                var head = entry.Tiles[0];
                if (!Util.Abilities.IsActionable(entry.Loc, new Vector2(head.X, head.Y), st.Id)) {
                    // head harvested by someone else meanwhile: skip it
                    entry.Tiles.RemoveAt(0);
                    DropRegistry(entry.Loc, head, j);
                    continue;
                }
                if (PathfindToTarget(j, st, head)) return true;
                NotePathFail(j, st, head);
                entry.Tiles.RemoveAt(0);
                DropRegistry(entry.Loc, head, j);
                if (--failBudget < 0) return Routes.ContainsKey(j);
            }
            ReleaseRoute(j);
            return false;
        }

        /// <summary>v2.1 conservative blacklist verdict (§3c revised): count pathfind failures
        /// per tile; only when a SECOND distinct junimo also fails does the tile enter
        /// st.Unreachable (scans then skip it for the rest of the day). A single junimo's
        /// failure — e.g. its own start being blocked — merely skips the tile this decision.</summary>
        private static void NotePathFail(JunimoHarvester j, HutState st, Point tile) {
            if (!st.PathFails.TryGetValue(tile, out var who)) st.PathFails[tile] = who = new HashSet<JunimoHarvester>();
            who.Add(j);
            if (who.Count >= 2) {
                st.PathFails.Remove(tile);
                st.Unreachable.Add(tile);
                Dlog($"pathfail verdict hut@({st.Hut.tileX.Value},{st.Hut.tileY.Value}) tile={tile}: blacklisted " +
                     $"after {who.Count} distinct junimo failures");
            } else {
                Dlog($"pathfail j#{j.whichJunimoFromThisHut} hut@({st.Hut.tileX.Value},{st.Hut.tileY.Value}) tile={tile}: " +
                     "1st distinct failure — skipped, NOT blacklisted (stays serviceable)");
            }
        }

        /// <summary>Drop tiles another junimo already registered (rare equidistant-tie race
        /// across huts; same-hut conflicts are structurally impossible — slices are disjoint).</summary>
        private static void PruneForeign(List<Point> tiles, GameLocation loc, JunimoHarvester self) {
            for (var i = tiles.Count - 1; i >= 0; i--) {
                if (RouteRegistry.TryGetValue((loc, tiles[i]), out var owner) && !ReferenceEquals(owner, self)) {
                    tiles.RemoveAt(i);
                }
            }
        }

        // ---------------------------------------------------------------- endgame

        /// <summary>Endgame work-seeking (DESIGN §4): shared pool (zero reads) → rate-gated
        /// box scan → home. Nearest-to-self, one task at a time; results are hut-shared.</summary>
        private static void EndgameSeek(JunimoHarvester j, HutState st, int failBudget) {
            if (TryWorkFromPool(j, st, ref failBudget)) {
                Dlog($"endgame j#{j.whichJunimoFromThisHut} hut@({st.Hut.tileX.Value},{st.Hut.tileY.Value}) -> pool target claimed");
                return;
            }

            if (Game1.ticks >= st.NextScanTick) {
                EndgameScan(st, j.TilePoint);
                if (TryWorkFromPool(j, st, ref failBudget)) {
                    Dlog($"endgame j#{j.whichJunimoFromThisHut} hut@({st.Hut.tileX.Value},{st.Hut.tileY.Value}) -> post-scan target claimed (pool={st.EndgamePool.Count})");
                    return;
                }
            }

            // nothing to do: vanilla return-home semantics (walk home → despawn on arrival);
            // the spawn gate lets junimos out again when work appears — no standing in fields
            Dlog($"endgame j#{j.whichJunimoFromThisHut} hut@({st.Hut.tileX.Value},{st.Hut.tileY.Value}) -> no work, returning home");
            if (j.currentLocation != null) j.returnToJunimoHut(j.currentLocation);
        }

        /// <summary>Take the nearest valid pool tile (zero tile reads), register it, walk to it.</summary>
        private static bool TryWorkFromPool(JunimoHarvester j, HutState st, ref int failBudget) {
            while (failBudget > 0) {
                var target = TakeNearestPoolTile(j, st);
                if (target == null) return false;
                Routes[j] = (st.Location, new List<Point> { target.Value });
                RouteRegistry[(st.Location, target.Value)] = j;
                if (PathfindToTarget(j, st, target.Value)) return true;
                NotePathFail(j, st, target.Value);   // v2.1: conservative — see NotePathFail
                ReleaseRoute(j);
                failBudget--;
            }
            return false;
        }

        private static Point? TakeNearestPoolTile(JunimoHarvester j, HutState st) {
            Point? best = null;
            var bd = int.MaxValue;
            var bi = -1;
            for (var i = 0; i < st.EndgamePool.Count; i++) {
                var t = st.EndgamePool[i];
                if (RouteRegistry.ContainsKey((st.Location, t))) {
                    // claimed since the scan: drop from the shared pool
                    st.EndgamePool.RemoveAt(i--);
                    continue;
                }
                if (st.Unreachable.Contains(t)) continue;
                var d = Dist(t, j.TilePoint);
                if (d < bd) {
                    bd = d;
                    best = t;
                    bi = i;
                }
            }
            if (bi >= 0) st.EndgamePool.RemoveAt(bi);
            return best;
        }

        /// <summary>Lightweight box scan (DESIGN §4): near-to-far from the origin junimo,
        /// collect ≤EndgamePoolCap results then early-exit; every read counted; the pool is
        /// shared by every idle junimo of this hut until the next scan.</summary>
        private static void EndgameScan(HutState st, Point origin) {
            st.NextScanTick = Game1.ticks + EndgameIntervalTicks;   // hard rate gate (any caller)
            var hut = st.Hut;
            if (hut == null || st.Location == null) return;
            st.EndgamePool.Clear();
            if (hut.noHarvest.Value) return;

            var loc = st.Location;
            var radius = Util.CurrentWorkingRadius;
            var cx = hut.tileX.Value + 1;
            var cy = hut.tileY.Value + 1;
            var rivals = BuildRivals(st, loc);

            // deterministic sweep order: distance from the origin, ties by tile coordinates
            var order = new List<Point>((2 * radius + 1) * (2 * radius + 1));
            for (var x = cx - radius; x <= cx + radius; x++) {
                for (var y = cy - radius; y <= cy + radius; y++) {
                    order.Add(new Point(x, y));
                }
            }
            order.Sort((a, b) => {
                var da = Math.Abs(a.X - origin.X) + Math.Abs(a.Y - origin.Y);
                var db = Math.Abs(b.X - origin.X) + Math.Abs(b.Y - origin.Y);
                return da != db ? da.CompareTo(db) : a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y);
            });

            var reads = 0;
            var t0 = Game1.ticks;
            foreach (var p in order) {
                reads++;
                if (st.Unreachable.Contains(p)) continue;
                if (RouteRegistry.ContainsKey((loc, p))) continue;
                if (!OwnedBy(st, p, rivals)) continue;
                if (Util.Abilities.IdentifyJunimoAbility(loc, new Vector2(p.X, p.Y), st.Id) != null) {
                    st.EndgamePool.Add(p);
                    if (st.EndgamePool.Count >= EndgamePoolCap) break;   // early exit
                }
            }

            if (BetterJunimos.Config.DebugLog) {
                BetterJunimos.SMonitor.Log(
                    $"[BJ-assign2] endgame scan hut@({hut.tileX.Value},{hut.tileY.Value}): " +
                    $"reads={reads}, ticks={Game1.ticks - t0}, results={st.EndgamePool.Count}", LogLevel.Debug);
            }
        }

        /// <summary>Wind-down inventory (DESIGN §1.3 step 3, once per junimo per day): scan the
        /// box for unregistered, ownership-owned actionable tiles and chain them nearest-first
        /// from the junimo into one continuous path.</summary>
        private static List<Point> WindDownInventory(HutState st, JunimoHarvester j) {
            var hut = st.Hut;
            var result = new List<Point>();
            if (hut == null || hut.noHarvest.Value || st.Location == null) return result;
            var loc = st.Location;
            var radius = Util.CurrentWorkingRadius;
            var cx = hut.tileX.Value + 1;
            var cy = hut.tileY.Value + 1;
            var rivals = BuildRivals(st, loc);

            var reads = 0;
            var t0 = Game1.ticks;
            var strays = new List<Point>();
            for (var x = cx - radius; x <= cx + radius; x++) {
                for (var y = cy - radius; y <= cy + radius; y++) {
                    reads++;
                    var p = new Point(x, y);
                    if (st.Unreachable.Contains(p)) continue;
                    if (RouteRegistry.ContainsKey((loc, p))) continue;
                    if (!OwnedBy(st, p, rivals)) continue;
                    if (Util.Abilities.IdentifyJunimoAbility(loc, new Vector2(x, y), st.Id) != null) {
                        strays.Add(p);
                    }
                }
            }
            st.Unreachable.Clear();   // fresh slate for the wind-down era
            st.PathFails.Clear();     // v2.1: tally cleared with the verdicts

            if (BetterJunimos.Config.DebugLog) {
                BetterJunimos.SMonitor.Log(
                    $"[BJ-assign2] wind-down hut@({hut.tileX.Value},{hut.tileY.Value}): " +
                    $"reads={reads}, ticks={Game1.ticks - t0}, strays={strays.Count}", LogLevel.Debug);
            }

            // nearest-neighbour chain from the junimo's position
            var remaining = new List<Point>(strays);
            var cur = j.TilePoint;
            while (remaining.Count > 0) {
                var bi = 0;
                var bd = int.MaxValue;
                for (var i = 0; i < remaining.Count; i++) {
                    var d = Dist(remaining[i], cur);
                    if (d < bd) {
                        bd = d;
                        bi = i;
                    }
                }
                cur = remaining[bi];
                result.Add(cur);
                remaining.RemoveAt(bi);
            }
            return result;
        }

        // ---------------------------------------------------------------- spawn gating

        /// <summary>Replacement for areThereMatureCropsWithinRadius in the spawn helper:
        /// segment/orphan/pool queries with zero tile reads; may fire the rate-gated shared
        /// endgame scan (this is how despawned-at-home junimos come back out for stragglers).</summary>
        internal static bool HasWorkFor(JunimoHut hut) {
            if (!Active) return false;
            // shipped semantics: unpaid junimos don't work at all (the decision path strikes
            // them); this also keeps the day's single bulk plan off the shrunken unpaid radius
            if (BetterJunimos.Config.JunimoPayment.WorkForWages && !Util.Payments.WereJunimosPaidToday) return false;
            var loc = hut.GetParentLocation();
            if (loc == null) return false;
            if (!Huts.TryGetValue(hut, out var st)) { HutsDirty = true; return false; }
            EnsurePlanned(st);
            if (st.Location == null) return false;

            foreach (var s in st.Segments) {
                if (s.Owner == null) return true;
            }
            if (st.Orphans.Count > 0) return true;
            foreach (var t in st.EndgamePool) {
                if (st.Unreachable.Contains(t)) continue;
                if (RouteRegistry.ContainsKey((st.Location, t))) continue;
                return true;
            }
            if (Game1.ticks >= st.NextScanTick) {
                EndgameScan(st, new Point(hut.tileX.Value + 1, hut.tileY.Value + 1));
                return st.EndgamePool.Count > 0;
            }
            return false;
        }

        // ---------------------------------------------------------------- death & release

        /// <summary>Called from PatchPokeToHarvest's destroy branch and the defensive sweep:
        /// the junimo's remaining path moves WHOLE into its hut's orphan list (DESIGN §3b),
        /// its registry rows drop, its mode flags clear.</summary>
        internal static void ReleaseJunimo(JunimoHarvester j) => OrphanRoute(j);

        private static void OrphanRoute(JunimoHarvester j) {
            if (Routes.TryGetValue(j, out var entry)) {
                Routes.Remove(j);
                foreach (var t in entry.Tiles) {
                    DropRegistry(entry.Loc, t, j);
                }
                var hut = Util.GetHutFromId(j.HomeId);
                if (hut != null && Huts.TryGetValue(hut, out var st) && entry.Tiles.Count > 0) {
                    // a copy: the same list object must never enter the orphan list twice
                    // (claim → death → takeover → death would otherwise alias one list)
                    st.Orphans.Add(new List<Point>(entry.Tiles));
                }
            }
            ForgetJunimo(j);
        }

        private static void ForgetJunimo(JunimoHarvester j) {
            EndgameJunimos.Remove(j);
            DidWindDown.Remove(j);
            LastDecisionTick.Remove(j);
        }

        // ---------------------------------------------------------------- pathfinding

        private static bool PathfindToTarget(JunimoHarvester j, HutState st, Point target) {
            var loc = j.currentLocation;
            if (loc == null) return false;
            // no async layer (permanently shelved): direct main-thread search,
            // budget 10000 = the game's own returnToJunimoHut value
            var c = new PathFindController(j, loc, target, -1, j.reachFirstDestinationFromHut, DirectPathBudget);
            if (c.pathToEndPoint == null || c.pathToEndPoint.Count == 0) {
                if (BetterJunimos.Config.DebugLog) {
                    Dlog($"pathfail j#{j.whichJunimoFromThisHut} hut@({st.Hut.tileX.Value},{st.Hut.tileY.Value}) " +
                         $"from={j.TilePoint} to={target} budget={DirectPathBudget} why={ClassifyPathFail(loc, j.TilePoint, target)}");
                }
                return false;
            }
            j.controller = c;
            FireAnimation(j, 0);
            return true;
        }

        /// <summary>v2.1 diagnostics: classify a pathfind failure for the log. Note the A*
        /// never inspects the START tile (pfc.cs findPath only expands neighbours), so
        /// start-in-building still paths out via a free neighbour; the classes are
        /// informational (DebugLog only).</summary>
        private static string ClassifyPathFail(GameLocation loc, Point start, Point target) {
            var startInBuilding = false;
            var targetInBuilding = false;
            if (loc.buildings != null) {
                foreach (var b in loc.buildings) {
                    if (b == null) continue;
                    if (b.occupiesTile(new Vector2(start.X, start.Y))) startInBuilding = true;
                    if (b.occupiesTile(new Vector2(target.X, target.Y))) targetInBuilding = true;
                }
            }
            if (startInBuilding) return targetInBuilding ? "start-in-building+target-in-building" : "start-in-building";
            if (targetInBuilding) return "target-in-building";
            return "no-route-within-budget";
        }

        // ---------------------------------------------------------------- plumbing

        private static void ReleaseRoute(JunimoHarvester j) {
            if (!Routes.TryGetValue(j, out var entry)) return;
            Routes.Remove(j);
            foreach (var t in entry.Tiles) {
                DropRegistry(entry.Loc, t, j);
            }
        }

        private static void DropRegistry(GameLocation loc, Point t, JunimoHarvester owner) {
            var key = (loc, t);
            if (RouteRegistry.TryGetValue(key, out var o) && ReferenceEquals(o, owner)) {
                RouteRegistry.Remove(key);
            }
        }

        private static int Dist(Point t, Point p) {
            return Math.Abs(t.X - p.X) + Math.Abs(t.Y - p.Y);
        }

        /// <summary>destroy is a protected field; reflect once per call site (small).</summary>
        internal static bool IsDestroyed(JunimoHarvester j) {
            try {
                return Util.Reflection.GetField<bool>(j, "destroy").GetValue();
            } catch {
                return false;
            }
        }

        /// <summary>v2.1 diagnostics: DebugLog-gated birth/decision chain logging (Debug level).</summary>
        private static void Dlog(string msg) {
            if (!BetterJunimos.Config.DebugLog) return;
            BetterJunimos.SMonitor.Log($"[BJ-assign2] {msg}", LogLevel.Debug);
        }

        internal static void FireAnimation(JunimoHarvester j, int animId) {
            try {
                var f = Util.Reflection.GetField<NetEvent1Field<int, NetInt>>(j, "netAnimationEvent");
                f.GetValue()?.Fire(animId);
            } catch (Exception ex) {
                BetterJunimos.SMonitor.Log($"[BJ-assign2] FireAnimation failed: {ex.Message}", LogLevel.Trace);
            }
        }
    }
}
