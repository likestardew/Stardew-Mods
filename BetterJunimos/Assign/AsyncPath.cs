#if ASYNC_PATHFIND
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Pathfinding;
using BetterJunimos.Utils;

namespace BetterJunimos.Assign {
    /* AsyncPath — async direct-pathfinding layer (assign-ASYNC build only).
     *
     * Implements E:\betterjunimos-work\async-pathfind-design\DESIGN.md on top of the
     * assignment scheduler. One sentence: the main thread does policy + snapshot + apply;
     * a single BelowNormal worker thread runs an array-based A* over an immutable
     * passability bitmap; results are applied in UpdateTicking after full re-validation.
     *
     * Facts this relies on (see async-pathfind-design/ENVIRONMENT.md):
     *  - H1: the ready-path PathFindController constructor only assigns fields
     *    (pfc.cs:94-100); a worker-built Stack<Point> (endpoint pushed first → stack top
     *    = start) is byte-for-byte the layout findPath produces. endBehaviorFunction and
     *    finalFacingDirection are public fields attached on the main thread.
     *  - H1 note: game PathNode.g is byte (paths ≤255); our budget is 10000 so the
     *    worker A* uses int g. Everything else replicates findPath semantics:
     *    end-check on dequeue, closed-on-insert, g+manhattan priority, neighbour order
     *    left/right/down/up (PathFindController.Directions), expansion-count limit.
     *  - H2/H3: controller==null means the junimo just stands (no retry storm), so the
     *    1-3 tick apply latency is safe; duplicate triggers are absorbed by the
     *    in-flight table + timeout sweep.
     *  - 意外发现 1: game findPath is gated by a global Interlocked counter and static
     *    containers — it can NEVER be called from a worker; hence the array-based A*.
     *  - 意外发现 3: the game disables tiered compilation → the worker JIT-warms up
     *    before serving real jobs.
     *  - H6: SMAPI events all run on the main thread; all main-thread state below is
     *    touched only from Tick()/submit (main thread). The worker touches ONLY the
     *    data carried in its job (plain byte[]/int[]) and its own buffers.
     *
     * Passability snapshot (DESIGN §1.1): one byte per tile per location, rebuilt by
     * calling the REAL location.isCollidingPosition(62×62 rect, pathfinding:true) per
     * tile — this absorbs other mods' passability patches (e.g. "crops walkable").
     * Rebuilds are chunked per tick; the published byte[] is never mutated (rebuild
     * writes a fresh array, published by reference swap), so worker jobs hold a stable
     * immutable view. Animals move every frame, so they are NOT baked in: the current
     * animal tiles are collected on the main thread per job and OR-ed into the worker's
     * private copy (DESIGN §1.1 动物例外).
     *
     * Worker taboos (DESIGN §2): no Game1.random (H8), no SMAPI Monitor (results carry
     * error text back instead), no Net* collections, no locks on game objects.
     *
     * Safety valve: any request not answered within ~180 ticks is served synchronously
     * on the main thread (game findPath, same 10000 budget), logged once per session.
     */
    internal static class AsyncPath {
        private const int RebuildIntervalTicks = 240;   // snapshot TTL (DESIGN §1.1)
        private const int RebuildChunkTiles = 800;      // tiles per tick ≈ 1-3 ms
        private const int InFlightTimeoutTicks = 180;   // 安全阀: worker silence window
        private const int MaxResultsPerTick = 4;        // apply drain cap (DESIGN §2)
        private const int MaxGrids = 4;
        private const int SearchBudget = WorkAssigner.DirectPathBudget;

        internal static bool Active => WorkerThread != null && !Stopping;

        // ---- queues (thread-safe boundary) ----
        private static readonly ConcurrentQueue<PathJob> JobQueue = new();
        private static readonly ConcurrentQueue<PathResult> ResultQueue = new();

        // ---- main-thread-only state ----
        private sealed class Pending {
            public JunimoHarvester Junimo;
            public GameLocation Location;
            public Guid HutId;
            public Point Target;
            public int Seq;
            public int SubmittedTick;
        }

        private sealed class GridState {
            public GameLocation Location;
            public int W = -1, H = -1;
            public byte[] Blocked;      // published, immutable; null until first build completes
            public byte[] Building;     // rebuild buffer (main thread only)
            public int Cursor;          // tiles built in Building
            public int BuiltTick = -1;
        }

        private static readonly List<Pending> PendingList = new();                       // waiting for grid
        private static readonly Dictionary<JunimoHarvester, Pending> InFlight = new();   // submitted to worker
        private static readonly Dictionary<GameLocation, GridState> Grids = new();

        private static Thread WorkerThread;
        private static readonly AutoResetEvent Wake = new(false);
        private static volatile bool Stopping;
        private static int Generation;
        private static int SeqCounter;
        private static bool FallbackLogged;

        // ---------------------------------------------------------------- lifecycle

        internal static void OnWorldInvalidated() {
            // day start / save load / title: hard-cancel everything in flight (DESIGN §1.4)
            Generation++;
            PendingList.Clear();
            InFlight.Clear();
            while (JobQueue.TryDequeue(out _)) { }
            while (ResultQueue.TryDequeue(out _)) { }
            Grids.Clear();
        }

        internal static void Shutdown() {
            Stopping = true;
            try {
                Wake.Set();
                WorkerThread?.Join(500);
            } catch {
                // shutting down; nothing to recover
            }
            WorkerThread = null;
        }

        // ---------------------------------------------------------------- submit side

        internal static bool TrySubmit(JunimoHarvester j, GameLocation loc, Point target, Guid hutId) {
            EnsureWorker();
            if (WorkerThread == null) return false;   // caller falls back to the sync search

            var grid = GetOrStartGrid(loc, j);
            var p = new Pending {
                Junimo = j,
                Location = loc,
                HutId = hutId,
                Target = target,
                Seq = ++SeqCounter,
                SubmittedTick = Game1.ticks,
            };
            if (grid.Blocked == null || grid.W < 0) {
                PendingList.Add(p);   // snapshot still building (chunked); apply in a later tick
                return true;
            }
            SubmitJob(p, grid);
            return true;
        }

        private static void SubmitJob(Pending p, GridState grid) {
            // one request per junimo: overwrite any older entry — the older result is
            // dropped at apply time by its stale Seq (H3 duplicate-decision guard)
            InFlight[p.Junimo] = p;
            var tile = p.Junimo.TilePoint;
            JobQueue.Enqueue(new PathJob {
                Seq = p.Seq,
                Gen = Generation,
                W = grid.W,
                H = grid.H,
                Start = tile.Y * grid.W + tile.X,
                End = p.Target.Y * grid.W + p.Target.X,
                Limit = SearchBudget,
                Blocked = grid.Blocked,
                AnimalTiles = CollectAnimalTiles(p.Location, grid.W, grid.H),
            });
            Wake.Set();
        }

        private static void EnsureWorker() {
            if (WorkerThread != null || Stopping) return;
            try {
                WorkerThread = new Thread(WorkerLoop) {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = "BJ.PathWorker"
                };
                WorkerThread.Start();
            } catch (Exception ex) {
                BetterJunimos.SMonitor.Log($"[BJ-assign] could not start path worker: {ex.Message}", LogLevel.Warn);
                WorkerThread = null;
            }
        }

        /// <summary>Animals move every frame → never baked into the resident bitmap; their
        /// CURRENT tiles are OR-ed into the worker's working copy per job (DESIGN §1.1).</summary>
        private static int[] CollectAnimalTiles(GameLocation loc, int w, int h) {
            List<int> tiles = null;
            try {
                if (loc.animals == null || loc.animals.Count() == 0) return null;
                foreach (var animal in loc.animals.Values) {
                    var box = animal.GetBoundingBox();
                    var x0 = Math.Max(0, box.Left / 64);
                    var x1 = Math.Min(w - 1, (box.Right - 1) / 64);
                    var y0 = Math.Max(0, box.Top / 64);
                    var y1 = Math.Min(h - 1, (box.Bottom - 1) / 64);
                    for (var x = x0; x <= x1; x++) {
                        for (var y = y0; y <= y1; y++) {
                            tiles ??= new List<int>(8);
                            tiles.Add(y * w + x);
                        }
                    }
                }
            } catch {
                return null;   // snapshot approximation: worst case one wasted path (R2)
            }
            return tiles?.ToArray();
        }

        // ---------------------------------------------------------------- main tick

        internal static void Tick() {
            if (WorkerThread == null) return;
            var now = Game1.ticks;

            // ---- safety valve: worker (or snapshot build) silent for too long → serve the
            // timed-out requests synchronously on the main thread; log once per session
            List<Pending> expired = null;
            foreach (var kv in InFlight) {
                if (now - kv.Value.SubmittedTick > InFlightTimeoutTicks) (expired ??= new List<Pending>()).Add(kv.Value);
            }
            foreach (var p in PendingList) {
                if (now - p.SubmittedTick > InFlightTimeoutTicks) (expired ??= new List<Pending>()).Add(p);
            }
            if (expired != null) {
                foreach (var p in expired) {
                    InFlight.Remove(p.Junimo);
                    PendingList.RemoveAll(q => q.Seq == p.Seq);
                    if (!FallbackLogged) {
                        FallbackLogged = true;
                        BetterJunimos.SMonitor.Log(
                            "[BJ-assign] async pathfinding did not return within 180 ticks; serving timed-out requests on the main thread (logged once per session)",
                            LogLevel.Warn);
                    }
                    WorkAssigner.SyncPathFallback(p.Junimo, p.Location, p.Target, p.HutId);
                }
            }

            MaintainGrids();

            // pending → jobs once their grid finished building
            if (PendingList.Count > 0) {
                for (var i = PendingList.Count - 1; i >= 0; i--) {
                    var p = PendingList[i];
                    if (!Grids.TryGetValue(p.Location, out var g) || g.Blocked == null || g.W < 0) continue;
                    PendingList.RemoveAt(i);
                    SubmitJob(p, g);
                }
            }

            // ---- apply drain: ≤K results per tick (DESIGN §2 — no apply storms)
            var applied = 0;
            while (applied < MaxResultsPerTick && ResultQueue.TryDequeue(out var r)) {
                applied++;
                HandleResult(r);
            }
        }

        private static void HandleResult(PathResult r) {
            Pending p = null;
            foreach (var kv in InFlight) {
                if (kv.Value.Seq == r.Seq) { p = kv.Value; break; }
            }
            if (p == null) return;   // stale / superseded / already served by the fallback
            InFlight.Remove(p.Junimo);

            if (r.Error != null) {
                BetterJunimos.SMonitor.Log($"[BJ-assign] worker error (falling back to main thread): {r.Error}", LogLevel.Warn);
                WorkAssigner.SyncPathFallback(p.Junimo, p.Location, p.Target, p.HutId);
                return;
            }
            if (r.Gen != Generation) return;   // day/save changed meanwhile (DESIGN §1.4)

            var j = p.Junimo;
            var loc = p.Location;
            // application-side validation (DESIGN §4): alive, still here, still unclaimed
            if (j == null || WorkAssigner.IsDestroyed(j) || j.currentLocation != loc || j.home == null) return;
            if (j.controller != null) return;   // a newer decision already gave it a path
            if (!WorkAssigner.RouteHeadIs(j, loc, p.Target)) return;   // policy moved on

            // bedtime may have crept in while we waited: do not start new work
            var quittingTime = Util.Progression.CanWorkInEvenings ? 2400 : 1900;
            if (Game1.timeOfDay > quittingTime) return;

            // endpoint re-validation on the LIVE world (O(small) main-thread reads):
            // still actionable + still passable (TTL-stale bitmap or mods may have changed it)
            if (!Util.Abilities.IsActionable(loc, new Vector2(p.Target.X, p.Target.Y), p.HutId)) {
                WorkAssigner.OnAsyncPathFailed(j, p.Target);
                return;
            }
            var rect = new Rectangle(p.Target.X * 64 + 1, p.Target.Y * 64 + 1, 62, 62);
            if (loc.isCollidingPosition(rect, Game1.viewport, false, 0, false, j, true)) {
                WorkAssigner.OnAsyncPathFailed(j, p.Target);
                return;
            }
            if (r.Path == null || r.Path.Count == 0) {
                // unreachable within the worker budget (§6): blacklist + release, no retry storm
                WorkAssigner.OnAsyncPathFailed(j, p.Target);
                return;
            }

            WorkAssigner.AttachReadyPath(j, loc, p.Target, r.Path);
        }

        // ---------------------------------------------------------------- snapshot grids

        private static GridState GetOrStartGrid(GameLocation loc, JunimoHarvester via) {
            if (Grids.TryGetValue(loc, out var g)) return g;
            if (Grids.Count >= MaxGrids) {
                GridState oldest = null;
                foreach (var kv in Grids) {
                    if (oldest == null || kv.Value.BuiltTick < oldest.BuiltTick) oldest = kv.Value;
                }
                if (oldest != null) Grids.Remove(oldest.Location);
            }
            g = new GridState { Location = loc };
            StartBuild(g);
            Grids[loc] = g;
            return g;
        }

        private static void StartBuild(GridState g) {
            var map = g.Location?.map;
            if (map == null) return;
            g.W = map.Layers[0].LayerWidth;
            g.H = map.Layers[0].LayerHeight;
            g.Building = new byte[g.W * g.H];
            g.Cursor = 0;
        }

        private static void MaintainGrids() {
            if (Grids.Count == 0) return;
            foreach (var g in Grids.Values) {
                if (g.W < 0) continue;
                if (g.Building != null) {
                    BuildChunk(g);
                } else if (g.BuiltTick >= 0 && Game1.ticks - g.BuiltTick > RebuildIntervalTicks) {
                    // TTL expired (DESIGN §1.1): fresh buffer, chunked over the next ticks;
                    // the old published array stays valid for workers meanwhile
                    g.Building = new byte[g.W * g.H];
                    g.Cursor = 0;
                }
            }
        }

        private static void BuildChunk(GridState g) {
            var loc = g.Location;
            // a junimo is both the collision identity (shouldCollideWithBuildingLayer=true)
            // and proof somebody still needs this grid; without one, drop the rebuild and
            // keep serving the old bitmap
            var j = loc?.characters?.OfType<JunimoHarvester>().FirstOrDefault(c => !WorkAssigner.IsDestroyed(c));
            if (j == null) {
                g.Building = null;
                return;
            }

            var n = g.W * g.H;
            var end = Math.Min(g.Cursor + RebuildChunkTiles, n);
            // isCollidingPosition opens doors when character.controller != null
            // (GameLocation.cs:2854); vanilla findPath always runs with controller==null,
            // so temporarily matching that state keeps the snapshot side-effect-free
            var saved = j.controller;
            j.controller = null;
            try {
                for (var i = g.Cursor; i < end; i++) {
                    var x = i % g.W;
                    var y = i / g.W;
                    // EXACT query vanilla findPath makes per expanded node (pfc.cs:222):
                    // real isCollidingPosition → other mods' passability patches absorbed
                    g.Building[i] = loc.isCollidingPosition(new Rectangle(x * 64 + 1, y * 64 + 1, 62, 62),
                        Game1.viewport, false, 0, false, j, true) ? (byte)1 : (byte)0;
                }
            } finally {
                j.controller = saved;
            }
            g.Cursor = end;
            if (g.Cursor >= n) {
                g.Blocked = g.Building;   // publish by reference swap; old arrays are immutable
                g.Building = null;
                g.BuiltTick = Game1.ticks;
            }
        }

        // ---------------------------------------------------------------- worker

        private static void WorkerLoop() {
            // warmup: JIT the search path before the first real job (tiered compilation
            // is disabled in the game — ENVIRONMENT 意外发现 3)
            try { Warmup(); } catch { /* warming up is best-effort */ }

            while (!Stopping) {
                try {
                    if (!Wake.WaitOne(50)) continue;
                    while (!Stopping && JobQueue.TryDequeue(out var job)) {
                        if (job.Gen != Generation) continue;   // cancelled by day change/load
                        PathResult res;
                        try {
                            var path = ArrayAstar.Search(job.Blocked, job.AnimalTiles, job.W, job.H,
                                job.Start, job.End, job.Limit, out var expansions);
                            res = new PathResult { Seq = job.Seq, Gen = job.Gen, Path = path, Expansions = expansions };
                        } catch (Exception ex) {
                            res = new PathResult { Seq = job.Seq, Gen = job.Gen, Error = ex.ToString() };
                        }
                        ResultQueue.Enqueue(res);
                    }
                } catch {
                    // never let the worker die; the main-thread timeout sweep covers lost jobs
                }
            }
        }

        private static void Warmup() {
            var dummy = new byte[64 * 64];
            for (var i = 0; i < dummy.Length; i++) dummy[i] = i % 7 == 0 ? (byte)1 : (byte)0;
            for (var i = 0; i < 10; i++) {
                ArrayAstar.Search(dummy, null, 64, 64, i * 3, dummy.Length - 1 - i * 3, 300, out _);
            }
        }

        private sealed class PathJob {
            public int Seq, Gen, W, H, Start, End, Limit;
            public byte[] Blocked;
            public int[] AnimalTiles;
        }

        private sealed class PathResult {
            public int Seq, Gen, Expansions;
            public Stack<Point> Path;   // null = unreachable within budget
            public string Error;        // worker-side exception, logged on the main thread
        }

        /// <summary>
        /// Array-based A* replicating PathFindController.findPath semantics (pfc.cs:180-246):
        /// end-check on dequeue, closed-on-insert (vanilla marks closed BEFORE enqueue),
        /// g + manhattan priority, neighbour order left/right/down/up
        /// (PathFindController.Directions), expansion-count limit checked after expanding.
        /// Deviation (required): int g instead of the game's byte g (PathNode.g would
        /// overflow past 255 steps — ENVIRONMENT H1 notes the limit must raise for
        /// budget-10000 searches). Tie-breaking may differ from the game's
        /// SortedDictionary order → path shape may differ, path length never does.
        /// All buffers are worker-private and version-stamped (no per-search clearing),
        /// zero allocation per search beyond the result Stack&lt;Point&gt; (DESIGN §7).
        /// </summary>
        private static class ArrayAstar {
            private static byte[] Work;      // passability working copy (blocked + animals)
            private static int[] Heap;       // binary min-heap of tile indices
            private static int[] Prio;       // parallel priorities
            private static int[] CameFrom;   // parent tile index, −1 at start
            private static int[] GCost;
            private static int[] Seen;       // version-stamped visited/closed
            private static int Version;
            private static int Size;         // heap size (single worker thread → safe)

            public static Stack<Point> Search(byte[] blocked, int[] animalTiles, int w, int h,
                int start, int end, int limit, out int expansions) {
                expansions = 0;
                var n = w * h;
                if (Work == null || Work.Length < n) {
                    Work = new byte[n];
                    Heap = new int[n];
                    Prio = new int[n];
                    CameFrom = new int[n];
                    GCost = new int[n];
                    Seen = new int[n];
                }
                if (++Version == int.MaxValue) {
                    Seen = new int[n];
                    Version = 1;
                }

                Buffer.BlockCopy(blocked, 0, Work, 0, n);
                if (animalTiles != null) {
                    foreach (var t in animalTiles) {
                        if (t >= 0 && t < n) Work[t] = 1;
                    }
                }

                var ex = end % w;
                var ey = end / w;
                var v = Version;

                Seen[start] = v;
                CameFrom[start] = -1;
                GCost[start] = 0;
                var sx = start % w;
                var sy = start / w;
                Push(start, Math.Abs(ex - sx) + Math.Abs(ey - sy));

                while (Size > 0) {
                    var cur = Heap[0];
                    RemoveMin();
                    expansions++;
                    if (cur == end) {
                        // reconstruct exactly like PathFindController.reconstructPath:
                        // endpoint pushed first → stack top = start tile
                        var stack = new Stack<Point>();
                        for (var p = cur; p >= 0; p = CameFrom[p]) stack.Push(new Point(p % w, p / w));
                        return stack;
                    }
                    var cx = cur % w;
                    var cy = cur / w;
                    var g2 = GCost[cur] + 1;
                    for (int d = 0; d < 4; d++) {
                        var nx = cx + (d == 0 ? -1 : d == 1 ? 1 : 0);
                        var ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        var ni = ny * w + nx;
                        if (Seen[ni] == v) continue;   // closed-on-insert
                        Seen[ni] = v;
                        if (Work[ni] != 0) continue;   // blocked (vanilla also closes blocked tiles)
                        CameFrom[ni] = cur;
                        GCost[ni] = g2;
                        Push(ni, g2 + Math.Abs(ex - nx) + Math.Abs(ey - ny));
                    }
                    if (expansions >= limit) return null;
                }
                return null;
            }

            private static void Push(int node, int prio) {
                var i = Size++;
                Heap[i] = node;
                Prio[i] = prio;
                while (i > 0) {
                    var parent = (i - 1) >> 1;
                    if (Prio[parent] <= Prio[i]) break;
                    (Heap[parent], Heap[i]) = (Heap[i], Heap[parent]);
                    (Prio[parent], Prio[i]) = (Prio[i], Prio[parent]);
                    i = parent;
                }
            }

            private static void RemoveMin() {
                Size--;
                if (Size <= 0) return;
                Heap[0] = Heap[Size];
                Prio[0] = Prio[Size];
                var i = 0;
                while (true) {
                    var l = 2 * i + 1;
                    var r = l + 1;
                    var m = i;
                    if (l < Size && Prio[l] < Prio[m]) m = l;
                    if (r < Size && Prio[r] < Prio[m]) m = r;
                    if (m == i) break;
                    (Heap[m], Heap[i]) = (Heap[i], Heap[m]);
                    (Prio[m], Prio[i]) = (Prio[i], Prio[m]);
                    i = m;
                }
            }
        }
    }
}
#endif
