using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JunimoSim;

/*
 * Offline simulation of JunimoHarvester work-finding algorithms.
 *
 * Fidelity notes (from decompiled game + mod sources):
 *  - grid farm 80x65, hut in the middle, ~5% blocked tiles, crops inside the radius box
 *  - junimos spawn staggered (1 per 60 ticks), walk 20 ticks/tile (speed 3),
 *    harvest a crop in 20 ticks (WorkFaster), idle junimos re-decide every ~2 ticks
 *    (update() -> pokeToHarvest every tick while idle, 70% chance to pathfind)
 *  - PathFindController: 4-directional A* with expansion budget = `limit`.
 *    The endFunction variant ("nearest actionable tile") is called with heuristic 0
 *    -> effectively Dijkstra, so limit 100 reaches only ~5-6 tiles.
 *    The concrete-target variant uses a Manhattan heuristic (admissible).
 *
 * Algorithms:
 *  legacy    : official 3.2.0 behavior — nearest-actionable A* (limit 100), fallback
 *              50% -> shared lastKnownCropLocation (limit 100), 25% -> return home
 *              (despawn + respawn), else random wander (limit 100, 2 attempts)
 *  claims_v1 : the claim system as shipped — work list frozen after the spawn-time
 *              scan, pathfind limit 100, claim survives a dead-tile arrival
 *  claims_v2b: fixed allocator — work list refreshed from the decision path (60 tick
 *              cooldown, capped at 32 tiles), pathfind limit 1000, harvested/unreachable
 *              tiles pruned from the list, claim released on dead-tile arrival,
 *              idle instead of wander when every known tile is claimed
 *  claims_v3 : vanilla nearest-search with a claim filter — the A* end check skips
 *              tiles claimed by another junimo; no work list, no scans, the claim
 *              table alone suffices; fallback tail like legacy (home / wander)
 */
internal static class Cfg {
    public const int W = 80, H = 65;
    public const int TICKS_PER_TILE = 20;
    public const int HARVEST_TICKS = 20;
    public const int DECISION_RETRY_TICKS = 2;
    public const int RETARGET_INTERVAL = 10;     // vanilla re-plans mid-walk ~70%/frame; approximated at 10t
    public const double POKE_PROB = 0.70;
    public const double WANDER_PROB = 0.035;
    public const int SPAWN_STAGGER_TICKS = 60;
    public const int SCAN_COOLDOWN_TICKS = 60;
    public const int LKC_REFRESH_TICKS = 120;
    public const int EXP_LIMIT_LEGACY = 100;
    public const int EXP_LIMIT_CLAIMS = 1000;
    public const int MAX_WORK_TILES = 32;
    public const int SIM_CAP_TICKS = 600_000;
    public const int RUNS = 30;
    public static readonly int[] Radii = { 8, 14, 20 };
    public static readonly int[] JunimoCounts = { 1, 2, 3, 4, 6, 8 };
    public static readonly string[] Algos = { "legacy", "claims_v4b", "v4b_gate_backoff" };
    // crop density: fraction of free tiles in the working box that hold a crop
    // 0.75 ≈ circular planting inside the box, 1.00 ≈ fully planted square
    public static readonly double[] Densities = { 0.10, 0.75, 1.00 };

    // ---- CPU/day benchmark (steady-state churn is where the lag lives) ----
    public const int DAY_TICKS = 36000;          // 6:00-20:00 at 430 ticks / 10 game-min
    public const int BACKOFF_TICKS = 40;         // 0.66s decision backoff after a failed search
    public const int SUCCESS_CAP_TICKS = 20;     // 0.33s between decisions after a successful one
    public const int CPU_RUNS = 10;
    public static readonly int[] WaveTicks = { 2000, 10000, 18000, 26000 };
    public static readonly int[] CpuRadii = { 14 };
    public static readonly int[] CpuJunimos = { 2, 6 };
    public static readonly string[] CpuAlgos = { "legacy", "v4b_shipped", "v4b_gate", "v4b_gate_backoff" };
    // obstacle rates: open field vs fenced/decorated farm (searches fail -> storm)
    public static readonly double[] CpuObstacles = { 0.05, 0.25 };
}

internal static class Program {
    private static void Main() {
        Console.OutputEncoding = Encoding.UTF8;
        var allRuns = new List<(string algo, int radius, int junimos, int run, double density)>();
        foreach (var density in Cfg.Densities)
            foreach (var algo in Cfg.Algos)
                foreach (var r in Cfg.Radii)
                    foreach (var j in Cfg.JunimoCounts)
                        for (var run = 0; run < Cfg.RUNS; run++)
                            allRuns.Add((algo, r, j, run, density));

        var results = new ResultStore();
        var sw = Stopwatch.StartNew();
        // multithreaded: each simulation is independent; a single simulation is
        // inherently sequential (game-loop semantics), so parallelism is across runs
        Parallel.ForEach(allRuns,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            cfg => {
                var seed = HashCode.Combine(cfg.algo, cfg.radius, cfg.junimos, cfg.density, cfg.run);
                var r = new Simulation(cfg.algo, cfg.radius, cfg.junimos, seed, cropDensity: cfg.density).Run();
                results.Add(cfg.algo, cfg.radius, cfg.junimos, cfg.density, r);
            });
        sw.Stop();
        Console.WriteLine($"[sim] {allRuns.Count} simulations in {sw.ElapsedMilliseconds} ms on {Environment.ProcessorCount} threads\n");

        Report(results);
        File.WriteAllText("results.csv", results.ToCsv());
        Console.WriteLine("\n[sim] CSV written to results.csv");

        CpuBenchmark();
    }

    // steady-state CPU over a full workday (6:00-20:00), crops ripening in 4 waves:
    // measures total A* node expansions (= the single-threaded frame cost driver)
    // and the worst 1-second window (frame spike proxy)
    private static void CpuBenchmark() {
        var runs = new List<(string algo, int radius, int junimos, int run, double obs)>();
        foreach (var algo in Cfg.CpuAlgos)
            foreach (var r in Cfg.CpuRadii)
                foreach (var jn in Cfg.CpuJunimos)
                    foreach (var obs in Cfg.CpuObstacles)
                        for (var run = 0; run < Cfg.CPU_RUNS; run++)
                            runs.Add((algo, r, jn, run, obs));

        var store = new CpuStore();
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(runs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, cfg => {
            var seed = HashCode.Combine("cpu", cfg.algo, cfg.radius, cfg.junimos, cfg.obs, cfg.run);
            var res = new Simulation(cfg.algo, cfg.radius, cfg.junimos, seed, dayMode: true, obstacleRate: cfg.obs).Run();
            store.Add(cfg.algo, cfg.radius, cfg.junimos, cfg.obs, res);
        });
        sw.Stop();

        Console.WriteLine($"\n[cpu] full-day benchmark: {runs.Count} sims in {sw.ElapsedMilliseconds} ms — radius {string.Join('/', Cfg.CpuRadii)}, waves at t={string.Join(',', Cfg.WaveTicks)}\n");
        foreach (var obs in Cfg.CpuObstacles) {
            var label = obs < 0.1 ? "open field (5% obstacles)" : "fenced farm (25% obstacles)";
            Console.WriteLine($"--- {label} ---");
            Console.WriteLine($"{"algo",-18} {"junimos",-8} | {"A* pops/day",-12} {"vs legacy",-10} | {"peak pops/1s",-13} {"vs legacy",-10}");
            Console.WriteLine(new string('-', 80));
            foreach (var algo in Cfg.CpuAlgos)
                foreach (var jn in Cfg.CpuJunimos) {
                    var rs = store.Get(algo, Cfg.CpuRadii[0], jn, obs);
                    var pops = rs.Average(x => x.Expansions);
                    var peak = rs.Average(x => x.PeakWindow);
                    var basePops = store.Get("legacy", Cfg.CpuRadii[0], jn, obs).Average(x => x.Expansions);
                    var basePeak = store.Get("legacy", Cfg.CpuRadii[0], jn, obs).Average(x => x.PeakWindow);
                    Console.WriteLine($"{algo,-18} {jn,-8} | {pops,-12:F0} {pops / basePops,-9:F2}x | {peak,-13:F0} {peak / basePeak,-9:F2}x");
                }
            Console.WriteLine();
        }
        File.WriteAllText("cpu_results.csv", store.ToCsv());
    }

    private static void Report(ResultStore results) {
        Console.WriteLine(
            $"{"algo",-10} {"radius",-7} {"junimos",-8} | {"株数",-6} | {"clear(s) avg",-12} {"sd",-7} | {"A* expans.",-10} | {"deadArrive",-10} | {"respawns",-9} | DNF");
        Console.WriteLine(new string('-', 112));
        foreach (var density in Cfg.Densities) {
            var label = density < 0.5 ? "sparse planting (10% of tiles)" : density < 0.9 ? "circular planting (~75% of tiles)" : "fully planted square (100%)";
            Console.WriteLine($"--- {label} ---");
            foreach (var algo in Cfg.Algos)
                foreach (var r in Cfg.Radii)
                    foreach (var j in Cfg.JunimoCounts) {
                        var rs = results.Get(algo, r, j, density);
                        var done = rs.Where(x => !x.Dnf).ToList();
                        if (done.Count == 0) {
                            Console.WriteLine($"{algo,-10} {r,-7} {j,-8} | {"ALL DNF",-6}");
                            continue;
                        }
                        var meanT = done.Average(x => x.ClearTicks) / 60.0;
                        var sdT = Math.Sqrt(done.Average(x => Math.Pow(x.ClearTicks / 60.0 - meanT, 2)));
                        Console.WriteLine(
                            $"{algo,-10} {r,-7} {j,-8} | {done.Average(x => x.CropCount),-6:F0} | {meanT,-12:F2} {sdT,-7:F2} | {done.Average(x => x.Expansions),-10:F0} | {done.Average(x => x.WastedArrivals),-10:F1} | {done.Average(x => x.Respawns),-9:F1} | {rs.Count - done.Count}");
                    }
            Console.WriteLine();
        }
    }
}

internal readonly struct RunResult {
    public RunResult(long clearTicks, long expansions, int wastedArrivals, int respawns, long scanOps, long peakWindow, int cropCount, bool dnf) {
        ClearTicks = clearTicks; Expansions = expansions; WastedArrivals = wastedArrivals; Respawns = respawns; ScanOps = scanOps; PeakWindow = peakWindow; CropCount = cropCount; Dnf = dnf;
    }
    public readonly long ClearTicks;
    public readonly long Expansions;
    public readonly int WastedArrivals;
    public readonly int Respawns;
    public readonly long ScanOps;
    public readonly long PeakWindow; // day mode: max A* pops in any 60-tick (1s) window
    public readonly int CropCount;
    public readonly bool Dnf;
}

internal sealed class CpuStore {
    private readonly object _lock = new();
    private readonly Dictionary<(string, int, int, double), List<RunResult>> _map = new();

    public void Add(string algo, int radius, int junimos, double obs, RunResult r) {
        lock (_lock) {
            var key = (algo, radius, junimos, obs);
            if (!_map.TryGetValue(key, out var list)) _map[key] = list = new List<RunResult>();
            list.Add(r);
        }
    }

    public List<RunResult> Get(string algo, int radius, int junimos, double obs) {
        lock (_lock) { return _map.TryGetValue((algo, radius, junimos, obs), out var l) ? l : new List<RunResult>(); }
    }

    public string ToCsv() {
        var sb = new StringBuilder("algo,junimos,obstacles,run,total_pops,peak_pops_1s\n");
        foreach (var ((algo, r, j, obs), list) in _map)
            for (var i = 0; i < list.Count; i++)
                sb.AppendLine($"{algo},{j},{obs},{i},{list[i].Expansions},{list[i].PeakWindow}");
        return sb.ToString();
    }
}

internal sealed class ResultStore {
    private readonly object _lock = new();
    private readonly Dictionary<(string, int, int, double), List<RunResult>> _map = new();

    public void Add(string algo, int radius, int junimos, double density, RunResult r) {
        lock (_lock) {
            var key = (algo, radius, junimos, density);
            if (!_map.TryGetValue(key, out var list)) _map[key] = list = new List<RunResult>();
            list.Add(r);
        }
    }

    public List<RunResult> Get(string algo, int radius, int junimos, double density) {
        lock (_lock) { return _map.TryGetValue((algo, radius, junimos, density), out var l) ? l : new List<RunResult>(); }
    }

    public string ToCsv() {
        var sb = new StringBuilder("algo,radius,junimos,density,run,clear_ticks,crops,expansions,wasted_arrivals,respawns,scan_ops,dnf\n");
        foreach (var ((algo, r, j, d), list) in _map)
            for (var i = 0; i < list.Count; i++) {
                var x = list[i];
                sb.AppendLine($"{algo},{r},{j},{d},{i},{x.ClearTicks},{x.CropCount},{x.Expansions},{x.WastedArrivals},{x.Respawns},{x.ScanOps},{x.Dnf}");
            }
        return sb.ToString();
    }
}

/* ------------------------------- simulation ------------------------------- */

internal sealed class Junimo {
    public enum St { WaitingDecision, Walking, Working, WaitingRespawn }

    public St State;
    public long NextEventTick;
    public int TargetTile;
    public int Pos;
    public List<int> Path;        // steps of the current walk; Path[^1] == TargetTile
    public int Origin;            // tile the walk started from
    public long WalkStartTick;
    public bool Retargets;        // vanilla cadence: re-plan mid-walk (legacy & v4b pre-cap)
}

internal sealed class Simulation {
    private readonly string _algo;
    private readonly int _radius;
    private readonly int _junimoCount;
    private readonly Random _rnd;

    private readonly bool[] _blocked = new bool[Cfg.W * Cfg.H];
    private readonly double _obstacleRate;
    private readonly HashSet<int> _crops = new();
    private readonly HashSet<int> _reserved = new();  // tiles whose crop is committed to a harvesting junimo
    private readonly List<Junimo> _junimos = new();

    // shared per-hut state
    private readonly List<int> _workTiles = new();            // claims v1/v2b: scan list (row-major, capped)
    private readonly Dictionary<int, Junimo> _claims = new(); // tile -> owner
    private readonly Dictionary<Junimo, int> _ownerClaim = new();
    private long _lastScanTick = long.MinValue / 2;
    private int _lkc = -1;                                    // legacy shared lastKnownCropLocation

    private long _expansions;
    private long _scanOps;
    private bool _lastSearchShadow; // AStarNearest(self!=null): endpoint was claimed by another junimo

    // day/CPU mode
    private readonly bool _dayMode;
    private long[] _tickPops;
    private readonly List<(long tick, int tile)> _waves = new();
    private int _waveIdx;
    private long _gateScanTick = long.MinValue / 2;
    private bool _cachedHasWork;
    private readonly Dictionary<Junimo, long> _backoff = new();
    private readonly double _cropDensity;
    private int _cropCount;
    private int _wastedArrivals;
    private int _respawns;
    private long _tick;

    public Simulation(string algo, int radius, int junimos, int seed, bool dayMode = false, double obstacleRate = 0.05, double cropDensity = 0.10) {
        _algo = algo; _radius = radius; _junimoCount = junimos; _rnd = new Random(seed); _dayMode = dayMode; _obstacleRate = obstacleRate; _cropDensity = cropDensity;
        if (dayMode) _tickPops = new long[Cfg.DAY_TICKS + 1];
    }

    private void AddPops(long pops) {
        _expansions += pops;
        if (_dayMode && _tick >= 0 && _tick < _tickPops.Length) _tickPops[_tick] += pops;
    }

    private static int Idx(int x, int y) => y * Cfg.W + x;
    private static int XOf(int idx) => idx % Cfg.W;
    private static int YOf(int idx) => idx / Cfg.W;
    private static int Man(int a, int b) => Math.Abs(XOf(a) - XOf(b)) + Math.Abs(YOf(a) - YOf(b));

    public RunResult Run() {
        BuildMap();

        for (var i = 0; i < _junimoCount; i++)
            _junimos.Add(new Junimo { State = Junimo.St.WaitingRespawn, NextEventTick = 1 + i * Cfg.SPAWN_STAGGER_TICKS, Pos = HutTile(), Retargets = _algo is "legacy" or "claims_v4b" });

        // spawn-time scan (areThereMatureCropsWithinRadius on the first spawn driver tick)
        if (_algo != "legacy") ScanWorkTiles(Cfg.MAX_WORK_TILES);

        long tick = 0;
        var remaining = _crops.Count;
        while (_dayMode ? tick < Cfg.DAY_TICKS : (tick < Cfg.SIM_CAP_TICKS && remaining > 0)) {
            // day mode: ripen the next wave of crops
            while (_waveIdx < _waves.Count && _waves[_waveIdx].tick <= tick) {
                _crops.Add(_waves[_waveIdx].tile);
                _waveIdx++;
            }
            Junimo next = null;
            var nextTick = long.MaxValue;
            foreach (var j in _junimos) {
                if (j.State == Junimo.St.WaitingRespawn && j.NextEventTick <= tick) {
                    // respawn: appear at the hut, ready to decide
                    j.State = Junimo.St.WaitingDecision;
                    j.NextEventTick = tick;
                    j.Pos = HutTile();
                }
                if (j.NextEventTick < nextTick) { nextTick = j.NextEventTick; next = j; }
            }
            if (next == null) break;
            tick = Math.Max(tick, nextTick);
            _tick = tick;

            switch (next.State) {
                case Junimo.St.WaitingDecision:
                    if (_rnd.NextDouble() >= Cfg.POKE_PROB) { next.NextEventTick = tick + Cfg.DECISION_RETRY_TICKS; break; }
                    Decide(next, tick);
                    break;

                case Junimo.St.Walking: {
                    var elapsed = tick - next.WalkStartTick;
                    var progress = (int)(elapsed / Cfg.TICKS_PER_TILE);
                    if (next.Path == null || progress >= next.Path.Count) {
                        // arrived: first junimo to reach a crop commits it (the real
                        // game harvests at the animation crossing; followers find nothing)
                        var tile = next.Path != null && next.Path.Count > 0 ? next.Path[^1] : next.Pos;
                        next.Pos = tile;
                        if (_crops.Contains(tile) && !_reserved.Contains(tile)) {
                            _reserved.Add(tile);
                            next.State = Junimo.St.Working;
                            next.NextEventTick = tick + Cfg.HARVEST_TICKS;
                        } else {
                            // dead tile: someone harvested it first (or the list is stale)
                            _wastedArrivals++;
                            if (_algo != "legacy" && _claims.TryGetValue(tile, out var owner) && ReferenceEquals(owner, next)) {
                                _claims.Remove(tile);
                                _ownerClaim.Remove(next);
                                if (_algo == "claims_v2b") _workTiles.Remove(tile);
                            }
                            next.State = Junimo.St.WaitingDecision;
                            next.NextEventTick = tick + Cfg.DECISION_RETRY_TICKS;
                        }
                        break;
                    }

                    // mid-walk re-planning: vanilla re-runs the work search ~70%/frame
                    // while walking, constantly redirecting toward the current nearest
                    // crop — this is what builds the converging trains
                    if (next.Retargets && _rnd.NextDouble() < Cfg.POKE_PROB) {
                        var cur = progress == 0 ? next.Origin : next.Path[progress - 1];
                        next.Pos = cur;
                        Retarget(next, tick, cur);
                    }

                    if (next.State == Junimo.St.Walking) {
                        next.NextEventTick = tick + Cfg.RETARGET_INTERVAL;
                    }
                    break;
                }

                case Junimo.St.Working: {
                    _crops.Remove(next.TargetTile);
                    remaining--;
                    _reserved.Remove(next.TargetTile);
                    _claims.Remove(next.TargetTile);
                    _ownerClaim.Remove(next);
                    if (_algo == "claims_v2b") _workTiles.Remove(next.TargetTile); // prune harvested tile so nobody chases it
                    next.State = Junimo.St.WaitingDecision;
                    next.NextEventTick = tick + Cfg.DECISION_RETRY_TICKS;
                    break;
                }
            }
        }

        var peak = 0L;
        if (_dayMode) {
            long sum = 0;
            for (var i = 0; i < _tickPops.Length; i++) {
                sum += _tickPops[i];
                if (i >= 60) sum -= _tickPops[i - 60];
                if (sum > peak) peak = sum;
            }
        }

        return new RunResult(tick, _expansions, _wastedArrivals, _respawns, _scanOps, peak, _cropCount, _dayMode ? false : remaining > 0);
    }

    private int HutTile() => Idx(Cfg.W / 2, Cfg.H / 2);

    private void BuildMap() {
        var rng = new Random(_rnd.Next());
        for (var i = 0; i < Cfg.W * Cfg.H; i++)
            if (rng.NextDouble() < _obstacleRate) _blocked[i] = true;

        var hut = HutTile();
        _blocked[hut] = true; _blocked[hut + 1] = true; _blocked[hut + Cfg.W] = true; _blocked[hut + Cfg.W + 1] = true;

        // only place crops reachable from the hut, so every run is completable
        var reach = new bool[Cfg.W * Cfg.H];
        var queue = new Queue<int>();
        reach[hut] = true;
        queue.Enqueue(hut);
        while (queue.Count > 0) {
            var cur = queue.Dequeue();
            foreach (var n in Neighbors(cur)) {
                if (reach[n] || _blocked[n]) continue;
                reach[n] = true;
                queue.Enqueue(n);
            }
        }

        var candidates = new List<int>();
        var hx = XOf(hut); var hy = YOf(hut);
        for (var x = hx + 1 - _radius; x <= hx + _radius; x++)
            for (var y = hy + 1 - _radius; y <= hy + _radius; y++) {
                if (x < 0 || y < 0 || x >= Cfg.W || y >= Cfg.H) continue;
                var idx = Idx(x, y);
                if (!_blocked[idx] && reach[idx]) candidates.Add(idx);
            }
        for (var i = candidates.Count - 1; i > 0; i--) {
            var k = rng.Next(i + 1);
            (candidates[i], candidates[k]) = (candidates[k], candidates[i]);
        }
        var target = (int)Math.Round(candidates.Count * _cropDensity);
        _cropCount = target;
        if (_dayMode) {
            // crops ripen in waves so the day has idle periods (the churn source)
            var per = Math.Max(1, target / Cfg.WaveTicks.Length);
            for (var w = 0; w < Cfg.WaveTicks.Length; w++)
                foreach (var t in candidates.Skip(w * per).Take(per))
                    _waves.Add((Cfg.WaveTicks[w], t));
        } else {
            foreach (var t in candidates.Take(target)) _crops.Add(t);
        }
    }

    private IEnumerable<int> Neighbors(int idx) {
        var x = XOf(idx); var y = YOf(idx);
        if (x > 0) yield return idx - 1;
        if (x < Cfg.W - 1) yield return idx + 1;
        if (y > 0) yield return idx - Cfg.W;
        if (y < Cfg.H - 1) yield return idx + Cfg.W;
    }

    /* ------------------------------ A* models ------------------------------ */

    // "nearest actionable tile" like foundCropEndFunction: no heuristic (Dijkstra),
    // endFunction checked on pop, expansion budget = limit
    private List<int> AStarNearest(int start, int limit, Junimo self = null) {
        var open = new PriorityQueue<int, (long g, long seq)>();
        long seq = 0;
        var gScore = new Dictionary<int, long> { [start] = 0 };
        var cameFrom = new Dictionary<int, int>();
        var closed = new HashSet<int>();
        open.Enqueue(start, (0, seq++));

        long pops = 0;
        while (open.Count > 0) {
            var cur = open.Dequeue();
            if (closed.Contains(cur)) continue;
            closed.Add(cur);
            pops++;
            if (pops > limit) { AddPops(pops); return null; }

            var claimedByOther = self != null && _claims.TryGetValue(cur, out var owner) && !ReferenceEquals(owner, self);
            if (cur != start && _crops.Contains(cur)) {
                AddPops(pops);
                _lastSearchShadow = claimedByOther;
                return Reconstruct(cameFrom, cur);
            }

            foreach (var n in Neighbors(cur)) {
                if (_blocked[n] || closed.Contains(n)) continue;
                var g = gScore[cur] + 1;
                if (gScore.TryGetValue(n, out var old) && g >= old) continue;
                gScore[n] = g;
                cameFrom[n] = cur;
                open.Enqueue(n, (g, seq++));
            }
        }
        AddPops(pops);
        return null;
    }

    // concrete-target A* with Manhattan heuristic (like PathFindController's Point ctor)
    private List<int> AStarTo(int start, int target, int limit) {
        var open = new PriorityQueue<int, (long f, long seq)>();
        long seq = 0;
        var gScore = new Dictionary<int, long> { [start] = 0 };
        var cameFrom = new Dictionary<int, int>();
        var closed = new HashSet<int>();
        open.Enqueue(start, (Man(start, target), seq++));

        long pops = 0;
        while (open.Count > 0) {
            var cur = open.Dequeue();
            if (closed.Contains(cur)) continue;
            closed.Add(cur);
            pops++;
            if (pops > limit) { AddPops(pops); return null; }
            if (cur == target) { AddPops(pops); return Reconstruct(cameFrom, cur); }

            foreach (var n in Neighbors(cur)) {
                if (_blocked[n] || closed.Contains(n)) continue;
                var g = gScore[cur] + 1;
                if (gScore.TryGetValue(n, out var old) && g >= old) continue;
                gScore[n] = g;
                cameFrom[n] = cur;
                open.Enqueue(n, (g + Man(n, target), seq++));
            }
        }
        AddPops(pops);
        return null;
    }

    private List<int> Reconstruct(Dictionary<int, int> cameFrom, int end) {
        var path = new List<int>();
        var cur = end;
        while (cameFrom.TryGetValue(cur, out var prev)) { path.Add(cur); cur = prev; }
        path.Reverse();
        return path;
    }

    /* --------------------------- scans / claims ---------------------------- */

    // row-major scan of the radius box with early exit at `cap` actionable tiles;
    // legacy only needs the first tile (lkc), claim allocators build the work list
    private void ScanWorkTiles(int cap) {
        _workTiles.Clear();
        var hut = HutTile();
        var hx = XOf(hut); var hy = YOf(hut);
        for (var x = hx + 1 - _radius; x <= hx + _radius && _workTiles.Count < cap; x++)
            for (var y = hy + 1 - _radius; y <= hy + _radius; y++) {
                if (x < 0 || y < 0 || x >= Cfg.W || y >= Cfg.H) continue;
                _scanOps++;
                if (_crops.Contains(Idx(x, y))) {
                    _workTiles.Add(Idx(x, y));
                    if (_workTiles.Count >= cap) break;
                }
            }
        _lastScanTick = _tick;
        if (_workTiles.Count > 0) _lkc = _workTiles[0];
    }

    private void ReleaseOwnClaim(Junimo j) {
        if (!_ownerClaim.TryGetValue(j, out var tile)) return;
        _ownerClaim.Remove(j);
        if (_claims.TryGetValue(tile, out var owner) && ReferenceEquals(owner, j)) _claims.Remove(tile);
    }

    /* ------------------------------ decisions ------------------------------ */

    private void Decide(Junimo j, long tick) {
        if (_rnd.NextDouble() < Cfg.WANDER_PROB) { Wander(j, tick); return; }

        switch (_algo) {
            case "legacy": DecideLegacy(j, tick); break;
            case "claims_v2b": DecideClaims(j, tick, v2: true); break;
            case "claims_v3": DecideV3(j, tick); break;
            case "claims_v4b": DecideV4b(j, tick, gate: false, backoff: false); break;
            case "v4b_shipped": DecideV4b(j, tick, gate: false, backoff: false); break;
            case "v4b_gate": DecideV4b(j, tick, gate: true, backoff: false); break;
            case "v4b_gate_backoff": DecideV4b(j, tick, gate: true, backoff: true); break;
        }
    }

    private void DecideLegacy(Junimo j, long tick) {
        // legacy: the scan only refreshes via the spawn driver; approximate 120 ticks;
        // it only needs the FIRST actionable tile (lkc), like the real early-exit scan
        if (tick - _lastScanTick >= Cfg.LKC_REFRESH_TICKS) ScanWorkTiles(cap: 1);

        var path = AStarNearest(j.Pos, Cfg.EXP_LIMIT_LEGACY);
        if (path != null) { StartWalk(j, path, tick); return; }

        var roll = _rnd.NextDouble();
        if (roll < 0.5 && _lkc >= 0 && _crops.Contains(_lkc)) {
            var p = AStarTo(j.Pos, _lkc, Cfg.EXP_LIMIT_LEGACY);
            if (p != null) { StartWalk(j, p, tick); return; }
            roll = _rnd.NextDouble(); // fall through to the next roll
        } else {
            roll = _rnd.NextDouble();
        }

        if (roll < 0.25) {
            // "unlucky, send Junimo home" -> returnToJunimoHut -> despawn + respawn
            _respawns++;
            j.State = Junimo.St.WaitingRespawn;
            j.NextEventTick = tick + Cfg.SPAWN_STAGGER_TICKS;
        } else {
            Wander(j, tick);
        }
    }

    private void DecideClaims(Junimo j, long tick, bool v2) {
        if (v2 && tick - _lastScanTick >= Cfg.SCAN_COOLDOWN_TICKS) ScanWorkTiles(Cfg.MAX_WORK_TILES);
        // v1: list stays frozen (the shipped bug)

        int? best = null;
        var bestDist = int.MaxValue;
        foreach (var t in _workTiles) {
            if (_claims.TryGetValue(t, out var owner) && !ReferenceEquals(owner, j)) continue;
            var d = Man(j.Pos, t);
            if (d < bestDist) { bestDist = d; best = t; }
        }

        if (best == null) {
            if (!v2) { Wander(j, tick); return; }            // v1 as shipped
            j.State = Junimo.St.WaitingDecision;             // v2b: wait, don't pile on
            j.NextEventTick = tick + Cfg.DECISION_RETRY_TICKS;
            return;
        }

        var tile = best.Value;
        _claims[tile] = j;
        _ownerClaim[j] = tile;
        var p2 = AStarTo(j.Pos, tile, v2 ? Cfg.EXP_LIMIT_CLAIMS : Cfg.EXP_LIMIT_LEGACY);
        if (p2 != null) { StartWalk(j, p2, tick); return; }

        // unreachable with the budget: v2b drops the tile so nobody retries it this scan
        if (_claims.TryGetValue(tile, out var o) && ReferenceEquals(o, j)) _claims.Remove(tile);
        _ownerClaim.Remove(j);
        if (v2) _workTiles.Remove(tile);
        j.State = Junimo.St.WaitingDecision;
        j.NextEventTick = tick + Cfg.DECISION_RETRY_TICKS;
    }

    // claims_v4b: legacy logic + soft claim filter. The nearest-search skips tiles
    // claimed by another junimo; if the whole search ball is claimed (crowded), it
    // shadows the nearest claimed tile exactly like vanilla — no failure, no extra
    // search (the same Dijkstra pass already knows where it is). lkc fallback chain
    // unchanged from legacy.
    // gate: consult the cached 60t hut work scan before any pathfinding. scan=false
    // => no actionable tile inside the radius box, and the search discards endpoints
    // outside the box anyway, so the searches below could never succeed — vanilla
    // burned 2-3 full-budget A* per frame per idle junimo on exactly this case.
    // backoff: after a failed work search, wait 0.5s before searching again (the
    // decision loop otherwise retries ~70%/frame per idle junimo).
    private void DecideV4b(Junimo j, long tick, bool gate, bool backoff) {
        if (gate) {
            if (tick - _gateScanTick >= Cfg.SCAN_COOLDOWN_TICKS) {
                ScanWorkTiles(cap: 1);
                _cachedHasWork = _workTiles.Count > 0;
                _gateScanTick = tick;
            }
            if (!_cachedHasWork) { VanillaTail(j, tick, allowLkc: false); return; }
        } else if (tick - _lastScanTick >= Cfg.LKC_REFRESH_TICKS) {
            ScanWorkTiles(cap: 1); // legacy lkc refresh (spawn-driver scans)
        }

        if (backoff && _backoff.TryGetValue(j, out var until) && tick < until) {
            j.State = Junimo.St.WaitingDecision;
            j.NextEventTick = tick + Cfg.DECISION_RETRY_TICKS;
            return;
        }

        var path = AStarNearest(j.Pos, Cfg.EXP_LIMIT_LEGACY, self: j);
        if (path != null) {
            // shipped build: cap decisions after success too (0.33s), not only failures
            _backoff[j] = tick + Cfg.SUCCESS_CAP_TICKS;
            if (_lastSearchShadow) {
                // crowded: shadow another junimo, exactly like vanilla
                StartWalk(j, path, tick);
                return;
            }
            ReleaseOwnClaim(j);
            var end = path[^1];
            _claims[end] = j;
            _ownerClaim[j] = end;
            StartWalk(j, path, tick);
            return;
        }

        // search failed: free any stale claim, back off before the next attempt
        ReleaseOwnClaim(j);
        if (backoff) _backoff[j] = tick + Cfg.BACKOFF_TICKS;

        var roll = _rnd.NextDouble();
        if (roll < 0.5 && _lkc >= 0 && _crops.Contains(_lkc)) {
            var p = AStarTo(j.Pos, _lkc, Cfg.EXP_LIMIT_LEGACY);
            if (p != null) { StartWalk(j, p, tick); return; }
            roll = _rnd.NextDouble();
        } else {
            roll = _rnd.NextDouble();
        }

        if (roll < 0.25) {
            _respawns++;
            j.State = Junimo.St.WaitingRespawn;
            j.NextEventTick = tick + Cfg.SPAWN_STAGGER_TICKS;
        } else {
            Wander(j, tick);
        }
    }

    // the vanilla fallback tail: 50% lkc (when there is work), 25% home, else wander
    private void VanillaTail(Junimo j, long tick, bool allowLkc) {
        var roll = _rnd.NextDouble();
        if (allowLkc && roll < 0.5 && _lkc >= 0 && _crops.Contains(_lkc)) {
            var p = AStarTo(j.Pos, _lkc, Cfg.EXP_LIMIT_LEGACY);
            if (p != null) { StartWalk(j, p, tick); return; }
            roll = _rnd.NextDouble();
        } else {
            roll = _rnd.NextDouble();
        }
        if (roll < 0.25) {
            _respawns++;
            j.State = Junimo.St.WaitingRespawn;
            j.NextEventTick = tick + Cfg.SPAWN_STAGGER_TICKS;
        } else {
            Wander(j, tick);
        }
    }

    // claims_v3: vanilla nearest-search, but the end check skips tiles claimed by
    // another junimo. No work list, no scans — the claim table alone suffices.
    private void DecideV3(Junimo j, long tick) {
        var path = AStarNearest(j.Pos, Cfg.EXP_LIMIT_LEGACY, self: j);
        if (path != null) {
            ReleaseOwnClaim(j);
            var end = path[^1];
            _claims[end] = j;
            _ownerClaim[j] = end;
            StartWalk(j, path, tick);
            return;
        }

        // nothing unclaimed within reach: 25% go home, else wander (like legacy's tail)
        if (_rnd.NextDouble() < 0.25) {
            _respawns++;
            j.State = Junimo.St.WaitingRespawn;
            j.NextEventTick = tick + Cfg.SPAWN_STAGGER_TICKS;
        } else {
            Wander(j, tick);
        }
    }

    private void Wander(Junimo j, long tick) {
        var hut = HutTile();
        var hx = XOf(hut); var hy = YOf(hut);
        for (var attempt = 0; attempt < 2; attempt++) {
            var rx = hx + _rnd.Next(-_radius, _radius + 1);
            var ry = hy + _rnd.Next(-_radius, _radius + 1);
            if (rx < 0 || ry < 0 || rx >= Cfg.W || ry >= Cfg.H) continue;
            var p = AStarTo(j.Pos, Idx(rx, ry), Cfg.EXP_LIMIT_LEGACY);
            if (p != null) { StartWalk(j, p, tick); return; }
        }
        j.State = Junimo.St.WaitingDecision;
        j.NextEventTick = tick + Cfg.DECISION_RETRY_TICKS;
    }

    private void StartWalk(Junimo j, List<int> path, long tick) {
        // j.Pos must be the junimo's CURRENT tile here. An empty path (target is the
        // tile we're standing on) resolves instantly into a re-decision.
        j.Path = path;
        j.Origin = j.Pos;
        j.WalkStartTick = tick;
        j.TargetTile = path.Count > 0 ? path[^1] : j.Pos;
        j.State = Junimo.St.Walking;
        j.NextEventTick = tick + (j.Retargets ? Cfg.RETARGET_INTERVAL : path.Count * Cfg.TICKS_PER_TILE);
    }

    // vanilla re-plans mid-walk (~70%/frame in the real game; approximated at 10t
    // intervals here): re-run the work search from the current tile and redirect.
    // This is what makes vanilla junimos converge into trains, and it is the CPU
    // storm that the decision rate cap removes.
    private void Retarget(Junimo j, long tick, int fromTile) {
        switch (_algo) {
            case "legacy": {
                var path = AStarNearest(fromTile, Cfg.EXP_LIMIT_LEGACY);
                if (path != null) { StartWalk(j, path, tick); return; }

                var roll = _rnd.NextDouble();
                if (roll < 0.5 && _lkc >= 0 && _crops.Contains(_lkc)) {
                    var p = AStarTo(fromTile, _lkc, Cfg.EXP_LIMIT_LEGACY);
                    if (p != null) { StartWalk(j, p, tick); return; }
                    // vanilla: the lkc controller replaces the walk even when its path is
                    // null — the junimo stands and re-pokes, it does NOT re-roll home
                    j.State = Junimo.St.WaitingDecision;
                    j.NextEventTick = tick + Cfg.DECISION_RETRY_TICKS;
                    return;
                }
                roll = _rnd.NextDouble();

                if (roll < 0.25) {
                    _respawns++;
                    j.State = Junimo.St.WaitingRespawn;
                    j.NextEventTick = tick + Cfg.SPAWN_STAGGER_TICKS;
                } else {
                    Wander(j, tick);
                }
                break;
            }

            case "claims_v4b": {
                var path = AStarNearest(fromTile, Cfg.EXP_LIMIT_LEGACY, self: j);
                if (path != null) {
                    ReleaseOwnClaim(j);
                    var end = path[^1];
                    _claims[end] = j;
                    _ownerClaim[j] = end;
                    StartWalk(j, path, tick);
                    return;
                }

                ReleaseOwnClaim(j);
                var roll2 = _rnd.NextDouble();
                if (roll2 < 0.5 && _lkc >= 0 && _crops.Contains(_lkc)) {
                    var p2 = AStarTo(fromTile, _lkc, Cfg.EXP_LIMIT_LEGACY);
                    if (p2 != null) { StartWalk(j, p2, tick); return; }
                    j.State = Junimo.St.WaitingDecision;
                    j.NextEventTick = tick + Cfg.DECISION_RETRY_TICKS;
                    return;
                }
                roll2 = _rnd.NextDouble();

                if (roll2 < 0.25) {
                    _respawns++;
                    j.State = Junimo.St.WaitingRespawn;
                    j.NextEventTick = tick + Cfg.SPAWN_STAGGER_TICKS;
                } else {
                    Wander(j, tick);
                }
                break;
            }
        }
    }
}
