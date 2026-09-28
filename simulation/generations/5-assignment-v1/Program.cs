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
 * sim-assign: extends the sim-audit1 audited event-driven simulator with
 *   (1) the ASSIGNMENT scheduling algorithm (assign5 / assign10) designed in
 *       DESIGN.md: periodic staggered per-hut full scans + incremental diff
 *       (healthy routes keep executing; only divergers re-route; big growth
 *       triggers full re-assignment) + exclusive routes via a GLOBAL route table;
 *   (2) the mod's real WorkFaster / WorkRidiculouslyFast semantics
 *       (git show faa40d4/HEAD : BetterJunimos/* — see DESIGN.md §1);
 *   (3) the DOUBLE hut poke (ReplaceJunimoTimerNumber postfix,
 *       JunimoHutPatches.cs:265-301: two independent p=0.7 pokes per junimo
 *       per 430t; a second successful poke of a WALKING legacy junimo discards
 *       the fresh path and re-searches; capped algos hit the 20t/40t decision
 *       cap on the second poke);
 *   (4) multi-hut support (double-hut overlap benchmark) with per-hut scan
 *       caches, lkc, wander boxes, spawn pacing and the endpoint radius box
 *       check (junimo.cs:384 semantics);
 *   (5) new cost metrics: tile inspections (scans), assignment ops, per-quartile
 *       (crops cleared) pops/inspections/ops, mixed 1s peak windows, cross-hut
 *       doomed arrivals, wave-discovery lag.
 *
 * Algorithms in the grid:
 *  legacy_arrival  : BJ 3.2.0 decision logic (nearest-actionable A* limit 100,
 *                    50% -> lkc, 25% -> home/despawn, else wander limit 100 x6,
 *                    3.5% stroll unless WRF) at the REAL event-driven cadence,
 *                    now INCLUDING the double hut poke. THE vanilla baseline.
 *  cap_only        : shipped-patch decision cap (>=20t after success, >=40t after
 *                    failure) + 60t-cached scan gate, WITHOUT the claim filter.
 *  v4b_realfilter  : the SHIPPED patched build — cap + gate + the real claim-filter
 *                    semantics (filtered nearest search; crowded -> ONE unfiltered
 *                    shadow A*; 1800t claim expiry).
 *  assign5         : assignment scheduler, 5s (300t) staggered per-hut scans,
 *                    ROUTE_LEN=3.
 *  assign10        : assignment scheduler, 10s (600t) staggered per-hut scans,
 *                    ROUTE_LEN=5.
 * Historical variants (legacy saturated cadence, claims v1/v2b/v3/v4b*) were
 * removed from this copy: they are not in any grid and carrying them through
 * the multi-hut refactor is pure risk.
 *
 * Speed modes (cell-level; paired seeds are speed-independent):
 *  "fast" : WorkFaster=true && WorkRidiculouslyFast=true (the user's real config):
 *           harvest cycle 61t (PatchJunimoShake zeroes the timer at 999ms — modeled
 *           per mod intent as always firing; the both-off control brackets this),
 *           poke decisions at 100% (PatchPokeToHarvest postfix), 3.5% stroll off,
 *           "nothing to do" timer 5ms (~1t).
 *  "off"  : both false: 120t harvest cycle, 70% poke gate, 3.5% stroll, 12t timer poke.
 *
 * Pairing methodology: per-run seeds derive from (radius, junimosTotal,
 * density|obstacles, run) ONLY — never the algorithm name, speed mode or hut
 * count — so every algorithm simulates the IDENTICAL map for a given run index.
 */

internal static class Cfg {
    public const int W = 80, H = 65;
    public const int TICKS_PER_TILE = 20;           // sim-audit1 baseline (junimo base.speed=3 px/tick ≈ 21.3 t/tile raw; kept for comparability)
    public const int HARVEST_CROSSING_TICKS = 60;   // 1000ms: JunimoHarvester.update takes the crop here (junimo.cs:506)
    public const int TIMER_POKE_STD = 12;           // 200ms "nothing to do" harvestTimer (mod: time=200)
    public const int TIMER_POKE_WF = 1;             // WorkFaster: time=5 -> fires the next frame (~1t)
    public const double POKE_PROB = 0.70;           // pokeToHarvest gate (junimo.cs:304); bypassed by the WorkRidiculouslyFast postfix
    public const double WANDER_PROB = 0.035;        // 3.5% stroll (mod:254) — disabled by WorkRidiculouslyFast
    public const int HUT_POKE_TICKS = 430;          // performTenMinuteAction (10 game-min)
    public const int IDLE_RANDOM_MEAN_TICKS = 3000; // idle randoms: 0.2%/frame x 1/6 (junimo.cs:626-651)
    public const int WANDER_ATTEMPTS_LEGACY = 6;    // BJ 3.2.0 PatchPathfindToRandomSpotAroundHut: retry <= 5
    public const int WANDER_ATTEMPTS_PATCHED = 2;   // shipped build caps the retries at 2
    public const int SPAWN_STAGGER_TICKS = 60;      // JunimoSpawnHelper.SpawnCooldownTicks
    public const int SCAN_COOLDOWN_TICKS = 60;      // PatchSearchAroundHut.ScanCooldownTicks (JunimoHutPatches.cs:28)
    public const int CLAIM_EXPIRY_TICKS = 1800;     // CropClaims.ExpiryTicks (CropClaims.cs:32)
    public const int EXP_LIMIT = 100;               // vanilla PathFindController budget
    // assign target A* budget. The vanilla crop search only ever needs limit 100
    // (ring-ordered nearest search), but a DIRECT pathfind to an assigned target can
    // be a 40+ tile cross-farm walk whose g+h diamond needs thousands of pops — the
    // game itself uses limit 10000 for exactly this kind of long walk
    // (JunimoHarvester.returnToJunimoHut). Failures here would live-lock the endgame
    // (far crops retried and re-failing every scan).
    public const int EXP_LIMIT_ASSIGN = 10000;
    public const int SIM_CAP_TICKS = 240_000;

    // ---- assign parameters (sweepable via env) ----
    public const int ASSIGN_CANDIDATES = 32;  // per-junimo nearest-candidate truncation
    public static readonly double AssignX = EnvD("JUNIMOSIM_ASSIGN_X", 0.5);            // growth threshold for full re-assign
    public static readonly int AssignRoute5 = EnvInt("JUNIMOSIM_ASSIGN_ROUTE5", 3);     // route length for the 5s cadence
    public static readonly int AssignRoute10 = EnvInt("JUNIMOSIM_ASSIGN_ROUTE10", 5);   // route length for the 10s cadence
    public static readonly bool AssignHybrid = Env("JUNIMOSIM_ASSIGN_SCANMODE") == "hybrid"; // full scan every Nth, route-verify otherwise
    public static readonly int AssignFullEvery = EnvInt("JUNIMOSIM_ASSIGN_FULLEVERY", 3);

    public static readonly int RUNS = EnvInt("JUNIMOSIM_RUNS", 20);
    public static readonly int CPU_RUNS = EnvInt("JUNIMOSIM_CPU_RUNS", 5);

    public static readonly int[] Radii = { 14 };
    public static readonly double[] Densities = { 0.10, 0.75, 1.00 };
    public static readonly int[] JunimoCounts = { 2, 4, 6, 8 };
    public static readonly string[] AllAlgos = { "legacy_arrival", "cap_only", "v4b_realfilter", "assign5", "assign10" };

    // ---- CPU/day benchmark ----
    public const int DAY_TICKS = 36000;          // 6:00-20:00
    public const int BACKOFF_TICKS = 40;         // FailedDecisionBackoffTicks (JunimoHarvesterPatches.cs:200)
    public const int SUCCESS_CAP_TICKS = 20;     // DecisionIntervalTicks (JunimoHarvesterPatches.cs:199)
    public static readonly int[] WaveTicks = { 2000, 10000, 18000, 26000 };
    public static readonly double[] CpuObstacles = { 0.05, 0.25 };
    public static readonly int[] CpuJunimos = { 2, 6 };

    // ---- env filters for sweep invocations (default: full grid) ----
    public static readonly string[] Algos = EnvList("JUNIMOSIM_ALGOS", AllAlgos);
    public static readonly double[] DensityFilter = EnvListD("JUNIMOSIM_DENSITIES");
    public static readonly int[] JnFilter = EnvListI("JUNIMOSIM_JN");
    public static readonly int HutsFilter = EnvInt("JUNIMOSIM_HUTS", 0);   // 0 = both 1-hut and 2-hut grids
    public static readonly string SpeedFilter = Env("JUNIMOSIM_SPEED");    // null = both fast and off
    public static readonly bool SkipThroughput = Env("JUNIMOSIM_SKIP_THROUGHPUT") == "1";
    public static readonly bool SkipCpu = Env("JUNIMOSIM_SKIP_CPU") == "1";
    public static readonly string OutSuffix = Env("JUNIMOSIM_OUT");        // filename suffix for sweep runs
    public static readonly bool Diag = Env("JUNIMOSIM_DIAG") == "1";

    private static string Env(string n) => Environment.GetEnvironmentVariable(n);
    private static int EnvInt(string n, int def) => int.TryParse(Env(n), out var v) && v > 0 ? v : def;
    private static double EnvD(string n, double def) => double.TryParse(Env(n), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
    private static string[] EnvList(string n, string[] def) {
        var s = Env(n);
        return string.IsNullOrWhiteSpace(s) ? def : s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
    }
    private static double[] EnvListD(string n) {
        var s = Env(n);
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Split(',').Select(x => double.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }
    private static int[] EnvListI(string n) {
        var s = Env(n);
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Split(',').Select(x => int.Parse(x.Trim())).ToArray();
    }
}

internal static class Program {
    private static void Main() {
        Console.OutputEncoding = Encoding.UTF8;
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"[cfg] algos={string.Join(',', Cfg.Algos)} runs={Cfg.RUNS} cpu_runs={Cfg.CPU_RUNS} " +
                          $"assignX={Cfg.AssignX} route5={Cfg.AssignRoute5} route10={Cfg.AssignRoute10} " +
                          $"scanMode={(Cfg.AssignHybrid ? "hybrid/" + Cfg.AssignFullEvery : "full")}");
        if (!Cfg.SkipThroughput) ThroughputBenchmark();
        if (!Cfg.SkipCpu) CpuBenchmark();
        sw.Stop();
        Console.WriteLine($"\n[total] {sw.ElapsedMilliseconds} ms");
    }

    // ------------------------------------------------------------------
    // Throughput: clear-time benchmark. Main grid = WorkFaster+WorkRidiculouslyFast
    // ("fast", the user's real config); control group = both off ("off").
    // Single hut: densities {0.10, 0.75, 1.00} x junimos {2,4,6,8}.
    // Double hut: A(28,32) B(52,32) r=14, 6 junimos each, densities {0.75, 1.00}.
    // ------------------------------------------------------------------
    private static void ThroughputBenchmark() {
        var cells = new List<(string algo, int huts, int jn, int radius, double density, int run, string speed)>();
        var speeds = Cfg.SpeedFilter != null ? new[] { Cfg.SpeedFilter } : new[] { "fast", "off" };
        foreach (var speed in speeds) {
            foreach (var algo in Cfg.Algos)
                foreach (var r in Cfg.Radii) {
                    if (Cfg.HutsFilter is 0 or 1) {
                        // single hut; the both-off control group is d=1.00 jn=6 only
                        var dns = speed == "fast" ? (Cfg.DensityFilter ?? Cfg.Densities) : (Cfg.DensityFilter ?? new[] { 1.00 });
                        var jns = speed == "fast" ? (Cfg.JnFilter ?? Cfg.JunimoCounts) : (Cfg.JnFilter ?? new[] { 6 });
                        foreach (var d in dns) foreach (var jn in jns)
                            for (var run = 0; run < Cfg.RUNS; run++) cells.Add((algo, 1, jn, r, d, run, speed));
                    }
                    if (Cfg.HutsFilter is 0 or 2) {
                        // double hut, 6 junimos per hut (12 total); control = d=1.00
                        var dns = speed == "fast" ? (Cfg.DensityFilter ?? new[] { 0.75, 1.00 }) : (Cfg.DensityFilter ?? new[] { 1.00 });
                        foreach (var d in dns)
                            for (var run = 0; run < Cfg.RUNS; run++) cells.Add((algo, 2, 6, r, d, run, speed));
                    }
                }
        }

        var results = new ResultStore();
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(cells, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, c => {
            // paired: seeds exclude algo/speed/huts — identical map per (radius, junimosTotal, density, run)
            var seed = HashCode.Combine(c.radius, c.huts * c.jn, c.density, c.run);
            var sim = new Simulation(c.algo, c.radius, c.jn, huts: c.huts, seed: seed,
                speedMode: c.speed, dayMode: false, obstacleRate: 0.05, cropDensity: c.density, run: c.run);
            var r = sim.Run();
            results.Add(c.algo, c.speed, c.huts, c.radius, c.jn, c.density, c.run, r);
        });
        sw.Stop();
        Console.WriteLine($"[sim] {cells.Count} simulations in {sw.ElapsedMilliseconds} ms on {Environment.ProcessorCount} threads\n");

        Console.WriteLine(
            $"{"algo",-15} {"spd",-4} {"huts",-4} {"jn",-3} {"dens",-5} | {"clear(s)",-9} {"sd",-7} {"ROM vs leg",-10} | {"A*pops",-9} {"insp",-9} {"asgOps",-9} | {"dead",-6} {"doomed",-7} {"xHutDoom",-8} {"diverg",-7} | DNF degen");
        Console.WriteLine(new string('-', 145));
        foreach (var speed in speeds)
            foreach (var huts in new[] { 1, 2 })
                foreach (var d in (Cfg.DensityFilter ?? Cfg.Densities)) {
                    Console.WriteLine($"--- speed={speed} huts={huts} density={d} ---");
                    foreach (var algo in Cfg.Algos)
                        foreach (var jn in huts == 1 ? (Cfg.JnFilter ?? Cfg.JunimoCounts) : new[] { 6 }) {
                            var rs = results.Get(algo, speed, huts, Cfg.Radii[0], jn, d);
                            if (rs.Count == 0) continue;
                            var done = rs.Where(x => !x.Dnf).ToList();
                            var okDone = done.Where(x => !x.Degen).ToList();
                            if (okDone.Count == 0) {
                                Console.WriteLine($"{algo,-15} {speed,-4} {huts,-4} {jn,-3} {d,-5} | ALL DNF/DEGEN ({rs.Count(x => x.Dnf)} dnf, {rs.Count(x => x.Degen)} degen)");
                                continue;
                            }
                            var meanT = done.Average(x => x.ClearTicks) / 60.0;
                            var sdT = Math.Sqrt(done.Average(x => Math.Pow(x.ClearTicks / 60.0 - meanT, 2)));
                            var leg = results.Get("legacy_arrival", speed, huts, Cfg.Radii[0], jn, d).Where(x => !x.Dnf && !x.Degen).ToList();
                            var legSum = leg.Sum(x => (double)x.ClearTicks);
                            var rom = legSum > 0 ? okDone.Sum(x => (double)x.ClearTicks) / legSum - 1.0 : double.NaN;
                            Console.WriteLine(
                                $"{algo,-15} {speed,-4} {huts,-4} {jn,-3} {d,-5} | {meanT,-9:F1} {sdT,-7:F1} {rom,-10:P1} | " +
                                $"{okDone.Average(x => (double)x.Expansions),-9:F0} {okDone.Average(x => (double)x.Inspections),-9:F0} {okDone.Average(x => (double)x.AssignOps),-9:F0} | " +
                                $"{okDone.Average(x => (double)x.WastedArrivals),-6:F1} {okDone.Average(x => (double)x.DoomedArrivals),-7:F1} " +
                                $"{okDone.Average(x => (double)x.DoomedForeign),-8:F1} {okDone.Average(x => (double)x.Diverged),-7:F1} | " +
                                $"{rs.Count - done.Count,3} {rs.Count(x => x.Degen),5}");
                        }
                    Console.WriteLine();
                }
        File.WriteAllText($"results{Cfg.OutSuffix}.csv", results.ToCsv());
        Console.WriteLine($"[sim] CSV written to results{Cfg.OutSuffix}.csv");
    }

    // ------------------------------------------------------------------
    // CPU/day benchmark: A* pops + scan inspections + assignment ops over a
    // 36000t workday with 4 crop waves; peaks over 1s (60t) windows.
    // ------------------------------------------------------------------
    private static void CpuBenchmark() {
        var cells = new List<(string algo, int huts, int jn, int radius, double obs, int run)>();
        foreach (var algo in Cfg.Algos)
            foreach (var r in Cfg.Radii) {
                if (Cfg.HutsFilter is 0 or 1)
                    foreach (var jn in Cfg.JnFilter ?? Cfg.CpuJunimos)
                        foreach (var obs in Cfg.CpuObstacles)
                            for (var run = 0; run < Cfg.CPU_RUNS; run++) cells.Add((algo, 1, jn, r, obs, run));
                if (Cfg.HutsFilter is 0 or 2)
                    foreach (var obs in Cfg.CpuObstacles) {
                        var runs = Math.Min(Cfg.CPU_RUNS, 30);
                        for (var run = 0; run < runs; run++) cells.Add((algo, 2, 6, r, obs, run));
                    }
            }

        var store = new CpuStore();
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(cells, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, c => {
            var seed = HashCode.Combine("cpu", c.radius, c.huts * c.jn, c.obs, c.run);
            var sim = new Simulation(c.algo, c.radius, c.jn, huts: c.huts, seed: seed,
                speedMode: "fast", dayMode: true, obstacleRate: c.obs, cropDensity: 0.10, run: c.run);
            var res = sim.Run();
            store.Add(c.algo, c.huts, c.jn, c.obs, c.run, res);
            if (Cfg.Diag) sim.PrintDiag();
        });
        sw.Stop();
        Console.WriteLine($"\n[cpu] full-day benchmark: {cells.Count} sims in {sw.ElapsedMilliseconds} ms — waves at t={string.Join(',', Cfg.WaveTicks)}\n");

        foreach (var huts in new[] { 1, 2 })
            foreach (var obs in Cfg.CpuObstacles) {
                var label = huts == 2 ? $"DOUBLE hut, {(obs < 0.1 ? "open field (5% obstacles)" : "fenced farm (25% obstacles)")}"
                                      : $"{(obs < 0.1 ? "open field (5% obstacles)" : "fenced farm (25% obstacles)")}";
                Console.WriteLine($"--- {label} (speed=fast) ---");
                Console.WriteLine($"{"algo",-15} {"jn",-3} | {"pops/day",-10} {"vs leg",-8} | {"insp/day",-10} {"vs leg",-8} | {"asgOps/day",-10} | {"peak p/1s",-10} {"vs leg",-8} | {"peak mixed",-10} {"vs leg",-8}");
                Console.WriteLine(new string('-', 135));
                foreach (var algo in Cfg.Algos)
                    foreach (var jn in huts == 1 ? (Cfg.JnFilter ?? Cfg.CpuJunimos) : new[] { 6 }) {
                        var rs = store.Get(algo, huts, jn, obs);
                        if (rs.Count == 0) continue;
                        var b = store.Get("legacy_arrival", huts, jn, obs);
                        var pops = rs.Average(x => (double)x.Expansions);
                        var insp = rs.Average(x => (double)x.Inspections);
                        var ops = rs.Average(x => (double)x.AssignOps);
                        var peak = rs.Average(x => (double)x.PeakPops);
                        var peakM = rs.Average(x => (double)x.PeakMixed);
                        string rp = "n/a", ri = "n/a", rpk = "n/a", rpkm = "n/a";
                        if (b.Count > 0) {
                            var bp = b.Average(x => (double)x.Expansions);
                            var bi = b.Average(x => (double)x.Inspections);
                            var bpk = b.Average(x => (double)x.PeakPops);
                            var bpkm = b.Average(x => (double)x.PeakMixed);
                            rp = $"{pops / bp:F2}"; ri = $"{insp / bi:F2}"; rpk = $"{peak / bpk:F2}"; rpkm = $"{peakM / bpkm:F2}";
                        }
                        Console.WriteLine(
                            $"{algo,-15} {jn,-3} | {pops,-10:F0} {rp,-8} | {insp,-10:F0} {ri,-8} | {ops,-10:F0} | " +
                            $"{peak,-10:F0} {rpk,-8} | {peakM,-10:F0} {rpkm,-8}");
                    }
                Console.WriteLine();
            }
        File.WriteAllText($"cpu_results{Cfg.OutSuffix}.csv", store.ToCsv());
        Console.WriteLine($"[cpu] CSV written to cpu_results{Cfg.OutSuffix}.csv");
    }
}

internal readonly struct RunResult {
    public RunResult(long clearTicks, long expansions, long inspections, long assignOps, int scans,
        int wastedArrivals, int doomedArrivals, int doomedForeign, long diverged, int respawns,
        int cropCount, bool dnf, bool degen, long[] popsQ, long[] inspQ, long[] opsQ,
        long waveLagSum, int waveLagN, long peakPops = 0, long peakInsp = 0, long peakMixed = 0, int cleared = 0) {
        ClearTicks = clearTicks; Expansions = expansions; Inspections = inspections; AssignOps = assignOps; Scans = scans;
        WastedArrivals = wastedArrivals; DoomedArrivals = doomedArrivals; DoomedForeign = doomedForeign; Diverged = diverged;
        Respawns = respawns; CropCount = cropCount; Dnf = dnf; Degen = degen;
        PopsQ = popsQ; InspQ = inspQ; OpsQ = opsQ; WaveLagSum = waveLagSum; WaveLagN = waveLagN;
        PeakPops = peakPops; PeakInsp = peakInsp; PeakMixed = peakMixed; Cleared = cleared;
    }
    public readonly long ClearTicks;
    public readonly long Expansions;
    public readonly long Inspections;   // tile reads: assign scans + gate/spawn scans + route-advance checks
    public readonly long AssignOps;     // basic ops of the assignment computation (distance evals, candidate scans)
    public readonly int Scans;          // assign scan events
    public readonly int WastedArrivals;
    public readonly int DoomedArrivals;
    public readonly int DoomedForeign;  // doomed where the working junimo belongs to another hut
    public readonly long Diverged;      // scan-time reroutes (route tile lost, mostly cross-hut overlap)
    public readonly int Respawns;
    public readonly int CropCount;
    public readonly bool Dnf;
    public readonly bool Degen;
    public readonly long[] PopsQ;       // per quartile of crops cleared (throughput) / per wave-quarter (day)
    public readonly long[] InspQ;
    public readonly long[] OpsQ;
    public readonly long WaveLagSum;    // day mode: sum over discovered crops of (discovery tick - wave tick)
    public readonly int WaveLagN;
    public readonly long PeakPops;      // day mode: max A* pops in any 60t window
    public readonly long PeakInsp;
    public readonly long PeakMixed;     // max (pops + insp/10 + ops/200) in any 60t window
    public readonly int Cleared;        // crops harvested (day mode: the churn throughput)
}

internal sealed class CpuStore {
    private readonly object _lock = new();
    private readonly Dictionary<(string, int, int, double), List<(int run, RunResult r)>> _map = new();

    public void Add(string algo, int huts, int junimos, double obs, int run, RunResult r) {
        lock (_lock) {
            var key = (algo, huts, junimos, obs);
            if (!_map.TryGetValue(key, out var list)) _map[key] = list = new List<(int, RunResult)>();
            list.Add((run, r));
        }
    }

    public List<RunResult> Get(string algo, int huts, int junimos, double obs) {
        lock (_lock) {
            return _map.TryGetValue((algo, huts, junimos, obs), out var l)
                ? l.OrderBy(x => x.run).Select(x => x.r).ToList() : new List<RunResult>();
        }
    }

    public string ToCsv() {
        var sb = new StringBuilder("algo,huts,junimos,obstacles,run,total_pops,total_insp,total_ops,scans,peak_pops_1s,peak_insp_1s,peak_mixed_1s,crops,cleared,degen," +
                                   "pops_q1,pops_q2,pops_q3,pops_q4,insp_q1,insp_q2,insp_q3,insp_q4,ops_q1,ops_q2,ops_q3,ops_q4,wave_lag_sum,wave_lag_n\n");
        foreach (var ((algo, huts, j, obs), list) in _map)
            foreach (var (run, x) in list) {
                sb.AppendLine($"{algo},{huts},{j},{obs.ToString(System.Globalization.CultureInfo.InvariantCulture)},{run},{x.Expansions},{x.Inspections},{x.AssignOps},{x.Scans},{x.PeakPops},{x.PeakInsp},{x.PeakMixed},{x.CropCount},{x.Cleared},{x.Degen}," +
                              $"{x.PopsQ[0]},{x.PopsQ[1]},{x.PopsQ[2]},{x.PopsQ[3]},{x.InspQ[0]},{x.InspQ[1]},{x.InspQ[2]},{x.InspQ[3]}," +
                              $"{x.OpsQ[0]},{x.OpsQ[1]},{x.OpsQ[2]},{x.OpsQ[3]},{x.WaveLagSum},{x.WaveLagN}");
            }
        return sb.ToString();
    }
}

internal sealed class ResultStore {
    private readonly object _lock = new();
    private readonly Dictionary<(string, string, int, int, int, double), List<(int run, RunResult r)>> _map = new();

    public void Add(string algo, string speed, int huts, int radius, int junimos, double density, int run, RunResult r) {
        lock (_lock) {
            var key = (algo, speed, huts, radius, junimos, density);
            if (!_map.TryGetValue(key, out var list)) _map[key] = list = new List<(int, RunResult)>();
            list.Add((run, r));
        }
    }

    public List<RunResult> Get(string algo, string speed, int huts, int radius, int junimos, double density) {
        lock (_lock) {
            return _map.TryGetValue((algo, speed, huts, radius, junimos, density), out var l)
                ? l.OrderBy(x => x.run).Select(x => x.r).ToList() : new List<RunResult>();
        }
    }

    public string ToCsv() {
        var sb = new StringBuilder("algo,speed,huts,radius,junimos,density,run,clear_ticks,crops,dnf,degen,expansions,inspections,assign_ops,scans," +
                                   "pops_q1,pops_q2,pops_q3,pops_q4,insp_q1,insp_q2,insp_q3,insp_q4,ops_q1,ops_q2,ops_q3,ops_q4," +
                                   "wasted_arrivals,doomed_arrivals,doomed_foreign,diverged,respawns,wave_lag_sum,wave_lag_n\n");
        foreach (var ((algo, speed, huts, r, j, d), list) in _map)
            foreach (var (run, x) in list) {
                sb.AppendLine($"{algo},{speed},{huts},{r},{j},{d.ToString(System.Globalization.CultureInfo.InvariantCulture)},{run},{x.ClearTicks},{x.CropCount},{x.Dnf},{x.Degen},{x.Expansions},{x.Inspections},{x.AssignOps},{x.Scans}," +
                              $"{x.PopsQ[0]},{x.PopsQ[1]},{x.PopsQ[2]},{x.PopsQ[3]},{x.InspQ[0]},{x.InspQ[1]},{x.InspQ[2]},{x.InspQ[3]}," +
                              $"{x.OpsQ[0]},{x.OpsQ[1]},{x.OpsQ[2]},{x.OpsQ[3]}," +
                              $"{x.WastedArrivals},{x.DoomedArrivals},{x.DoomedForeign},{x.Diverged},{x.Respawns},{x.WaveLagSum},{x.WaveLagN}");
            }
        return sb.ToString();
    }
}

/* ------------------------------- simulation ------------------------------- */

internal sealed class HutState {
    public int Tile, X, Y;
    public long GateScanTick = long.MinValue / 2;  // 60t-cached work-scan timestamp (spawn gate / decision gate)
    public bool CachedHasWork;
    public int Lkc = -1;                           // per-hut lastKnownCropLocation
    public long LastSpawnTick = long.MinValue / 2; // per-hut spawn pacing
    public long NextScanTick;                      // assign: next staggered scan
    public long ScanCount;                         // assign: scans since the last full scan (hybrid pacing)
    public HashSet<int> ReachSet;                  // free tiles reachable from this hut
    public int BoxCandidates;                      // reachable free tiles inside the box (degeneracy detection)
    public long WaveLagSum;
    public int WaveLagN;
}

internal sealed class Junimo {
    public enum St { WaitingDecision, Walking, Working, WaitingRespawn }

    public St State;
    public long NextEventTick;
    public int TargetTile;
    public int Pos;
    public List<int> Path;        // steps of the current walk; Path[^1] == TargetTile
    public int Origin;
    public long WalkStartTick;
    public int WorkingPhase;      // 0 = until the 1000ms harvest crossing, 1 = until the 2000ms poke
    public bool PendingTimerPoke; // tryToHarvestHere "nothing to do": second poke
    public int HomeHut;
    public List<int> Route = new(); // assign: remaining assigned targets (exclusive ownership)
}

internal sealed class Simulation {
    private readonly string _algo;
    private readonly int _radius;
    private readonly int _junimosPerHut;
    private readonly int _hutCount;
    private readonly int _run;
    private readonly Random _rnd;
    private readonly bool _workFaster;          // Progression.WorkFaster (config + unlocked; user has it)
    private readonly bool _workRidiculouslyFast;
    private readonly bool _isAssign;
    private readonly int _assignPeriod;
    private readonly int _assignRouteLen;
    private readonly double _assignX;
    private readonly bool _hybrid;
    private readonly int _fullEvery;

    private readonly bool[] _blocked = new bool[Cfg.W * Cfg.H];
    private readonly double _obstacleRate;
    private readonly HashSet<int> _crops = new();
    private readonly List<Junimo> _junimos = new();
    private readonly List<HutState> _huts = new();

    // shared state for the claim-filter algos
    private readonly List<int> _workTiles = new();
    private readonly Dictionary<int, Junimo> _claims = new();
    private readonly Dictionary<Junimo, int> _ownerClaim = new();
    private readonly Dictionary<int, long> _claimTick = new();

    // assign scheduler state
    private readonly Dictionary<int, Junimo> _routeTargets = new(); // tile -> junimo whose route holds it (GLOBAL across huts)
    private readonly HashSet<int> _pool = new();                   // ripe, unassigned, discovered crops (global)
    private readonly HashSet<int> _unreachable = new();            // target A* failures, cleared at each scan

    private long _expansions;
    private long _inspections;
    private long _assignOps;
    private int _scans;
    private bool _lastSearchShadow;
    private List<int> _shadowPath;

    // day/CPU mode
    private readonly bool _dayMode;
    private long[] _tickPops, _tickInsp, _tickOps;
    private readonly List<(long tick, int tile)> _waves = new();
    private readonly Dictionary<int, long> _waveTickOf = new();
    private int _waveIdx;
    private readonly double _cropDensity;
    private int _cropCount;
    private int _wastedArrivals;
    private int _doomedArrivals;
    private int _doomedForeign;
    private long _diverged;
    private int _respawns;
    private long _tick;
    private long _nextPokeTick;
    private bool _degen;

    // quartile segmentation by crops cleared
    private readonly long[] _popsQ = new long[4];
    private readonly long[] _inspQ = new long[4];
    private readonly long[] _opsQ = new long[4];
    private int _segIdx;
    private int _clearedTotal;

    // audit instrumentation (no RNG use)
    private readonly bool _diag;
    private long _decisions, _nearestSearches, _nearestFails, _hutPokes, _idleRandoms, _gatedOut;
    private readonly long[] _stateTicks = new long[4];
    private readonly Dictionary<Junimo, long> _backoff = new();

    public void PrintDiag() {
        if (!_diag) return;
        var total = _stateTicks[0] + _stateTicks[1] + _stateTicks[2] + _stateTicks[3];
        if (total == 0) total = 1;
        Console.WriteLine(
            $"[diag] {_algo} obs={_obstacleRate} jn={_junimosPerHut}x{_hutCount} run={_run} crops={_cropCount} pops={_expansions} insp={_inspections} | " +
            $"dec={_decisions} gated={_gatedOut} nearest={_nearestSearches}(fail {_nearestFails}) pokes={_hutPokes} idleRnd={_idleRandoms} " +
            $"respawn={_respawns} wasted={_wastedArrivals} doomed={_doomedArrivals} | " +
            $"state%: wait={100.0 * _stateTicks[0] / total:F0} walk={100.0 * _stateTicks[1] / total:F0} work={100.0 * _stateTicks[2] / total:F0} dead={100.0 * _stateTicks[3] / total:F0}");
    }

    public Simulation(string algo, int radius, int junimosPerHut, int huts, int seed, string speedMode,
        bool dayMode = false, double obstacleRate = 0.05, double cropDensity = 0.10, int run = -1) {
        _algo = algo; _radius = radius; _junimosPerHut = junimosPerHut; _hutCount = huts; _run = run;
        _rnd = new Random(seed); _dayMode = dayMode; _obstacleRate = obstacleRate; _cropDensity = cropDensity;
        _workFaster = speedMode == "fast";
        _workRidiculouslyFast = speedMode == "fast";
        _isAssign = algo.StartsWith("assign5") || algo.StartsWith("assign10");
        _assignPeriod = algo.StartsWith("assign5") ? 300 : 600;
        _assignRouteLen = algo.StartsWith("assign5") ? Cfg.AssignRoute5 : Cfg.AssignRoute10;
        _assignX = Cfg.AssignX;
        _hybrid = Cfg.AssignHybrid;
        _fullEvery = Cfg.AssignFullEvery;
        // sweep overrides encoded in the algo name: assign5_r4_x25_hyb3
        // (r=route length, x=growth threshold percent, hyb=hybrid scan full-every).
        // Seeds exclude the algo name, so all variants stay paired on the same maps.
        if (_isAssign) {
            var rest = algo.Substring(algo.StartsWith("assign5") ? 7 : 8);
            foreach (var tok in rest.Split('_'))
                if (tok.Length > 1)
                    switch (tok[0]) {
                        case 'r' when int.TryParse(tok.AsSpan(1), out var rr): _assignRouteLen = rr; break;
                        case 'x' when double.TryParse(tok.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture, out var xx): _assignX = xx / 100.0; break;
                        case 'h' when tok.StartsWith("hyb") && int.TryParse(tok.AsSpan(3), out var hh): _hybrid = true; _fullEvery = hh; break;
                    }
        }
        if (dayMode) { _tickPops = new long[Cfg.DAY_TICKS + 1]; _tickInsp = new long[Cfg.DAY_TICKS + 1]; _tickOps = new long[Cfg.DAY_TICKS + 1]; }
        _diag = Cfg.Diag;
    }

    private int HarvestTicks => _workFaster ? Cfg.HARVEST_CROSSING_TICKS + 1 : 120; // WF: PatchJunimoShake zeroes the timer right after the 1000ms crossing
    private int TimerPokeTicks => _workFaster ? Cfg.TIMER_POKE_WF : Cfg.TIMER_POKE_STD;
    private double WanderProb => _workRidiculouslyFast ? 0.0 : Cfg.WANDER_PROB;

    // pokeToHarvest: 70% gate; the WorkRidiculouslyFast postfix (mod:386-395)
    // re-searches whenever the poke left no controller -> decision at 100%.
    private bool PokeRoll() => _workRidiculouslyFast || _rnd.NextDouble() < Cfg.POKE_PROB;

    private void AddPops(long pops) {
        _expansions += pops;
        _popsQ[_segIdx] += pops;
        if (_dayMode && _tick >= 0 && _tick < _tickPops.Length) _tickPops[_tick] += pops;
    }

    private void AddInsp(long n) {
        _inspections += n;
        _inspQ[_segIdx] += n;
        if (_dayMode && _tick >= 0 && _tick < _tickInsp.Length) _tickInsp[_tick] += n;
    }

    private void AddOps(long n) {
        _assignOps += n;
        _opsQ[_segIdx] += n;
        if (_dayMode && _tick >= 0 && _tick < _tickOps.Length) _tickOps[_tick] += n;
    }

    private void UpdateSeg() => _segIdx = Math.Min(3, _clearedTotal * 4 / Math.Max(1, _cropCount));

    private static int Idx(int x, int y) => y * Cfg.W + x;
    private static int XOf(int idx) => idx % Cfg.W;
    private static int YOf(int idx) => idx / Cfg.W;
    private static int Man(int a, int b) => Math.Abs(XOf(a) - XOf(b)) + Math.Abs(YOf(a) - YOf(b));

    private int HutTile(int h) => _huts[h].Tile;

    // radius box of a hut: [X+1-r, X+r] x [Y+1-r, Y+r] (the mod's EndPointInFarm +1 offset)
    private bool InBox(int h, int t) {
        var x = XOf(t); var y = YOf(t);
        return x >= _huts[h].X + 1 - _radius && x <= _huts[h].X + _radius
            && y >= _huts[h].Y + 1 - _radius && y <= _huts[h].Y + _radius;
    }

    public RunResult Run() {
        BuildMap();

        var total = _junimosPerHut * _hutCount;
        for (var i = 0; i < total; i++)
            _junimos.Add(new Junimo {
                State = Junimo.St.WaitingRespawn,
                NextEventTick = 1 + i * Cfg.SPAWN_STAGGER_TICKS,
                Pos = HutTile(i % _hutCount),
                HomeHut = i % _hutCount,
            });

        long tick = 0;
        var remaining = _crops.Count;
        _nextPokeTick = Cfg.HUT_POKE_TICKS;
        for (var i = 0; i < _huts.Count; i++)
            _huts[i].NextScanTick = _isAssign ? Math.Max(1, i * (long)(_assignPeriod / _hutCount)) : long.MaxValue / 2;

        var lastEvent = new Dictionary<Junimo, long>();
        while (_dayMode ? tick < Cfg.DAY_TICKS : (tick < Cfg.SIM_CAP_TICKS && remaining > 0)) {
            // day mode: ripen the next wave of crops
            while (_waveIdx < _waves.Count && _waves[_waveIdx].tick <= tick) {
                _crops.Add(_waves[_waveIdx].tile);
                _waveIdx++;
            }
            Junimo next = null;
            var nextTick = long.MaxValue;
            foreach (var j in _junimos) {
                if (j.NextEventTick < nextTick) { nextTick = j.NextEventTick; next = j; }
            }
            if (next == null) break;

            var minScan = long.MaxValue;
            var scanHut = -1;
            if (_isAssign)
                for (var i = 0; i < _huts.Count; i++)
                    if (_huts[i].NextScanTick < minScan) { minScan = _huts[i].NextScanTick; scanHut = i; }

            var limit = _dayMode ? Cfg.DAY_TICKS : Cfg.SIM_CAP_TICKS;
            var eventTick = Math.Min(nextTick, Math.Min(_nextPokeTick, minScan));
            if (eventTick > limit) eventTick = limit;
            tick = Math.Max(tick, eventTick);
            _tick = tick;

            if (_diag) {
                var since = tick - (lastEvent.TryGetValue(next, out var lt) ? lt : 0);
                if (since > 0) _stateTicks[(int)next.State] += since;
                lastEvent[next] = tick;
            }

            // priority at equal ticks: hut pokes, then assign scans, then junimo events
            if (_nextPokeTick <= nextTick && _nextPokeTick <= minScan && _nextPokeTick <= limit) {
                DoHutPoke(tick);
                _nextPokeTick += Cfg.HUT_POKE_TICKS;
                continue;
            }
            if (_isAssign && minScan <= nextTick && minScan <= limit) {
                DoAssignScan(scanHut);
                _huts[scanHut].NextScanTick += _assignPeriod;
                continue;
            }

            switch (next.State) {
                case Junimo.St.WaitingRespawn: {
                    var hut = _huts[next.HomeHut];
                    // JunimoSpawnHelper: one spawn per 60t pacing, only when the hut's
                    // cached scan (or, for assign, the assignment table) finds work.
                    if (tick - hut.LastSpawnTick >= Cfg.SPAWN_STAGGER_TICKS && HasSpawnWork(next.HomeHut)) {
                        hut.LastSpawnTick = tick;
                        next.State = Junimo.St.WaitingDecision;
                        next.Pos = HutTile(next.HomeHut);
                        if (_isAssign) {
                            // assignment mode: a spawn immediately self-assigns from the
                            // table (the pool was filled by an earlier scan); if the table
                            // has nothing reachable it sleeps to the next scan
                            if (!SelfAssign(next))
                                next.NextEventTick = hut.NextScanTick + 1;
                        } else if (PokeRoll()) {
                            Decide(next, tick);
                            if (next.State == Junimo.St.WaitingDecision)
                                next.NextEventTick = tick + SampleIdleRandom();
                        } else {
                            next.NextEventTick = tick + SampleIdleRandom();
                        }
                    } else {
                        next.NextEventTick = tick + Cfg.SCAN_COOLDOWN_TICKS;
                    }
                    break;
                }

                case Junimo.St.WaitingDecision:
                    if (_isAssign) {
                        // assignment mode: an idle junimo sleeps until its hut's next
                        // scan; scans do ALL target selection. No idle randoms, no pokes.
                        next.NextEventTick = _huts[next.HomeHut].NextScanTick + 1;
                        break;
                    }
                    if (next.PendingTimerPoke) {
                        // the 200ms (WF: 5ms) harvestTimer set by the mod's
                        // tryToHarvestHere "nothing to do" branch expires here
                        next.PendingTimerPoke = false;
                        _idleRandoms++;
                        if (PokeRoll()) Decide(next, tick);
                    } else {
                        // spontaneous idle random: update() calls pathfindToNewCrop
                        // DIRECTLY (no 70% gate); the decision cap applies inside
                        _idleRandoms++;
                        Decide(next, tick);
                    }
                    if (next.State == Junimo.St.WaitingDecision)
                        next.NextEventTick = tick + SampleIdleRandom();
                    break;

                case Junimo.St.Walking: {
                    if (next.PendingTimerPoke) {
                        // the 200ms "nothing to do" harvestTimer expires MID-WALK:
                        // the real update() pokes and the fresh pathfindToNewCrop
                        // REPLACES the walking controller (junimo.cs:589). For capped
                        // algos the 20/40t cap blocks the retry.
                        next.PendingTimerPoke = false;
                        var progTp = (int)((tick - next.WalkStartTick) / Cfg.TICKS_PER_TILE);
                        next.Pos = progTp <= 0 ? next.Origin : next.Path[Math.Min(progTp, next.Path.Count) - 1];
                        var oldPath = next.Path;
                        var oldStart = next.WalkStartTick;
                        if (PokeRoll()) Decide(next, tick);
                        if (next.State == Junimo.St.Walking) {
                            if (ReferenceEquals(next.Path, oldPath)) {
                                // still the old walk (decision was capped out): re-arm to its arrival
                                var prog2 = (int)((tick - oldStart) / Cfg.TICKS_PER_TILE);
                                next.NextEventTick = tick + Math.Max(1, (next.Path.Count - prog2) * Cfg.TICKS_PER_TILE);
                            } // else: StartWalk already scheduled the new walk's arrival
                        } else if (next.State == Junimo.St.WaitingDecision) {
                            next.NextEventTick = tick + SampleIdleRandom();
                        }
                        break;
                    }
                    var elapsed = tick - next.WalkStartTick;
                    var progress = (int)(elapsed / Cfg.TICKS_PER_TILE);
                    if (next.Path == null || progress >= next.Path.Count) {
                        // arrived: tryToHarvestHere
                        var tile = next.Path != null && next.Path.Count > 0 ? next.Path[^1] : next.Pos;
                        next.Pos = tile;
                        if (_crops.Contains(tile)) {
                            // no reservation in the real game: ANY junimo arriving while
                            // the crop still exists starts a (possibly doomed) harvest;
                            // the first 1000ms crossing wins.
                            var otherWorking = _junimos.Exists(o => !ReferenceEquals(o, next) && o.State == Junimo.St.Working && o.TargetTile == tile);
                            if (otherWorking) {
                                _doomedArrivals++;
                                if (_junimos.Exists(o => !ReferenceEquals(o, next) && o.State == Junimo.St.Working && o.TargetTile == tile && o.HomeHut != next.HomeHut))
                                    _doomedForeign++;
                            }
                            next.State = Junimo.St.Working;
                            next.WorkingPhase = 0;
                            next.NextEventTick = tick + Cfg.HARVEST_CROSSING_TICKS;
                        } else {
                            // dead tile: someone harvested it first (or the plan went stale)
                            _wastedArrivals++;
                            next.State = Junimo.St.WaitingDecision;
                            if (_isAssign) {
                                // reroute along the remaining route (structurally rare:
                                // exclusive assignment + global route table)
                                if (next.Route.Count > 0 && next.Route[0] == tile) {
                                    next.Route.RemoveAt(0);
                                    _routeTargets.Remove(tile);
                                }
                                if (next.Route.Count > 0 && _crops.Contains(next.Route[0])) {
                                    var p = AStarTo(next.Pos, next.Route[0], Cfg.EXP_LIMIT_ASSIGN);
                                    if (p != null) { StartWalk(next, p, tick); break; }
                                }
                                ReleaseRoute(next);
                                if (SelfAssign(next)) break;
                                next.NextEventTick = _huts[next.HomeHut].NextScanTick + 1;
                                break;
                            }
                            if (_algo != "legacy_arrival") ReleaseOwnClaim(next);
                            if (PokeRoll()) Decide(next, tick);
                            if (next.State is Junimo.St.WaitingDecision or Junimo.St.Walking) {
                                next.PendingTimerPoke = true;
                                next.NextEventTick = tick + TimerPokeTicks;
                            }
                        }
                        break;
                    }
                    // no mid-walk replanning: vanilla's reactive redirects only come
                    // from the poke/idle triggers, which are all modeled as events;
                    // the next event IS the arrival
                    next.NextEventTick = tick + (next.Path.Count - progress) * Cfg.TICKS_PER_TILE;
                    break;
                }

                case Junimo.St.Working: {
                    if (next.WorkingPhase == 0) {
                        // the 1000ms crossing (junimo.cs:506): the crop goes to the
                        // first harvester whose crossing finds it
                        if (_crops.Remove(next.TargetTile)) {
                            remaining--;
                            _clearedTotal++;
                            UpdateSeg();
                            if (_isAssign) {
                                if (next.Route.Count > 0 && next.Route[0] == next.TargetTile) {
                                    next.Route.RemoveAt(0);
                                    _routeTargets.Remove(next.TargetTile);
                                }
                            } else {
                                ReleaseOwnClaim(next);
                            }
                        }
                        next.WorkingPhase = 1;
                        next.NextEventTick = tick + (HarvestTicks - Cfg.HARVEST_CROSSING_TICKS);
                    } else {
                        // 2000ms reached (WF: 61t) -> pokeToHarvest
                        if (_isAssign) {
                            // route advance: walk to the next assigned target; the
                            // ripeness re-check is one tile read
                            if (next.Route.Count > 0) {
                                var nxt = next.Route[0];
                                AddInsp(1);
                                if (_crops.Contains(nxt)) {
                                    var p = AStarTo(next.Pos, nxt, Cfg.EXP_LIMIT_ASSIGN);
                                    if (p != null) { StartWalk(next, p, tick); break; }
                                }
                                // dead or unreachable: release and re-assign below
                                if (!_crops.Contains(nxt)) _routeTargets.Remove(nxt);
                                else _unreachable.Add(nxt);
                                ReleaseRoute(next);
                            }
                            // route exhausted: top up DIRECTLY from the assignment table
                            // (zero tile reads, bounded ops) instead of sleeping until the
                            // next scan — removes the route/period quantization idle gap.
                            // Perception still happens ONLY at scans; this just consumes it.
                            if (SelfAssign(next)) break;
                            next.State = Junimo.St.WaitingDecision;
                            next.NextEventTick = _huts[next.HomeHut].NextScanTick + 1;
                            break;
                        }
                        next.State = Junimo.St.WaitingDecision;
                        if (PokeRoll()) Decide(next, tick);
                        if (next.State == Junimo.St.WaitingDecision)
                            next.NextEventTick = tick + SampleIdleRandom();
                    }
                    break;
                }
            }
        }

        var peakPops = 0L; var peakInsp = 0L; var peakMixed = 0L;
        if (_dayMode) {
            long sp = 0, si = 0, so = 0;
            for (var i = 0; i < _tickPops.Length; i++) {
                sp += _tickPops[i]; si += _tickInsp[i]; so += _tickOps[i];
                if (i >= 60) { sp -= _tickPops[i - 60]; si -= _tickInsp[i - 60]; so -= _tickOps[i - 60]; }
                if (sp > peakPops) peakPops = sp;
                if (si > peakInsp) peakInsp = si;
                var mixed = sp + si / 10 + so / 200;
                if (mixed > peakMixed) peakMixed = mixed;
            }
        }

        if (!_dayMode && remaining > 0 && Cfg.Diag) {
            // DNF post-mortem: where are the remaining crops and what are the junimos doing?
            var inRoutes = _routeTargets.Keys.Count(t => _crops.Contains(t));
            var inPool = _pool.Count(t => _crops.Contains(t));
            Console.WriteLine($"[dnf] {_algo} spd={(_workFaster ? "fast" : "off")} huts={_hutCount} jn={_junimosPerHut} d={_cropDensity} run={_run}: " +
                              $"remaining={remaining} cropsAlive={_crops.Count} routedAlive={inRoutes} poolAlive={inPool} unreachable={_unreachable.Count}");
            foreach (var j in _junimos)
                Console.WriteLine($"[dnf]   j(hut{j.HomeHut}) state={j.State} pos={j.Pos}({XOf(j.Pos)},{YOf(j.Pos)}) route=[{string.Join(',', j.Route)}] target={j.TargetTile} nextEvt={j.NextEventTick} tick={_tick}");
            foreach (var t in _crops) {
                var reachInfo = _huts.Any(hh => hh.ReachSet.Contains(t));
                var astar = _junimos.Count > 0 ? AStarTo(_junimos[0].Pos, t, Cfg.EXP_LIMIT_ASSIGN) : null;
                Console.WriteLine($"[dnf]   crop {t}({XOf(t)},{YOf(t)}) hutReach={reachInfo} inPool={_pool.Contains(t)} routed={_routeTargets.ContainsKey(t)} astarFromJ0={(astar != null ? "OK" : "FAIL")}");
            }
        }
        if (!_dayMode && Cfg.Diag && _run >= 0) {
            var total2 = _stateTicks[0] + _stateTicks[1] + _stateTicks[2] + _stateTicks[3];
            if (total2 > 0)
                Console.WriteLine($"[util] {_algo} d={_cropDensity} run={_run}: wait={100.0 * _stateTicks[0] / total2:F0}% walk={100.0 * _stateTicks[1] / total2:F0}% " +
                                  $"work={100.0 * _stateTicks[2] / total2:F0}% dead={100.0 * _stateTicks[3] / total2:F0}% scans={_scans} clear={(tick / 60.0):F0}s");
        }

        return new RunResult(tick, _expansions, _inspections, _assignOps, _scans,
            _wastedArrivals, _doomedArrivals, _doomedForeign, _diverged, _respawns,
            _cropCount, _dayMode ? false : remaining > 0, _degen,
            (long[])_popsQ.Clone(), (long[])_inspQ.Clone(), (long[])_opsQ.Clone(),
            _huts.Sum(h => h.WaveLagSum), _huts.Sum(h => h.WaveLagN),
            peakPops, peakInsp, peakMixed, _clearedTotal);
    }

    private void BuildMap() {
        var rng = new Random(_rnd.Next()); // FIRST RNG draw: identical map for every algo at a given seed
        for (var i = 0; i < Cfg.W * Cfg.H; i++)
            if (rng.NextDouble() < _obstacleRate) _blocked[i] = true;

        // hut footprints (2x2 blocked, matching sim-audit1) at the configured centers
        int[] hutX, hutY;
        if (_hutCount == 2) { hutX = new[] { 28, 52 }; hutY = new[] { 32, 32 }; }
        else { hutX = new[] { Cfg.W / 2 }; hutY = new[] { Cfg.H / 2 }; }
        for (var i = 0; i < hutX.Length; i++) {
            var hut = Idx(hutX[i], hutY[i]);
            _huts.Add(new HutState { Tile = hut, X = hutX[i], Y = hutY[i] });
            _blocked[hut] = true; _blocked[hut + 1] = true; _blocked[hut + Cfg.W] = true; _blocked[hut + Cfg.W + 1] = true;
        }

        // per-hut BFS reachability
        for (var h = 0; h < _huts.Count; h++) {
            var hut = _huts[h];
            var reach = new bool[Cfg.W * Cfg.H];
            var queue = new Queue<int>();
            reach[hut.Tile] = true;
            queue.Enqueue(hut.Tile);
            while (queue.Count > 0) {
                var cur = queue.Dequeue();
                foreach (var n in Neighbors(cur)) {
                    if (reach[n] || _blocked[n]) continue;
                    reach[n] = true;
                    queue.Enqueue(n);
                }
            }
            hut.ReachSet = new HashSet<int>();
            var cand = 0;
            for (var i = 0; i < Cfg.W * Cfg.H; i++)
                if (reach[i] && !_blocked[i]) {
                    hut.ReachSet.Add(i);
                    if (InBox(h, i)) cand++;
                }
            hut.BoxCandidates = cand;
        }
        _degen = _huts.Any(h => h.BoxCandidates == 0);

        // crops only on tiles inside SOME hut's box AND reachable from THAT hut,
        // so every placed crop is legally workable by at least one hut
        var candSet = new List<int>();
        for (var h = 0; h < _huts.Count; h++)
            foreach (var t in _huts[h].ReachSet)
                if (InBox(h, t)) candSet.Add(t);
        candSet = candSet.Distinct().ToList();
        for (var i = candSet.Count - 1; i > 0; i--) {
            var k = rng.Next(i + 1);
            (candSet[i], candSet[k]) = (candSet[k], candSet[i]);
        }
        var target = (int)Math.Round(candSet.Count * _cropDensity);
        _cropCount = target;
        if (_dayMode) {
            var per = Math.Max(1, target / Cfg.WaveTicks.Length);
            for (var w = 0; w < Cfg.WaveTicks.Length; w++)
                foreach (var t in candSet.Skip(w * per).Take(per)) {
                    _waves.Add((Cfg.WaveTicks[w], t));
                    _waveTickOf[t] = Cfg.WaveTicks[w];
                }
        } else {
            foreach (var t in candSet.Take(target)) _crops.Add(t);
        }
    }

    private IEnumerable<int> Neighbors(int idx) {
        var x = XOf(idx); var y = YOf(idx);
        // same order as PathFindController.Directions: left, right, down, up
        if (x > 0) yield return idx - 1;
        if (x < Cfg.W - 1) yield return idx + 1;
        if (y < Cfg.H - 1) yield return idx + Cfg.W;
        if (y > 0) yield return idx - Cfg.W;
    }

    /* ------------------------------ A* models ------------------------------ */

    // "nearest actionable tile" like foundCropEndFunction — real PathFindController
    // semantics (pfc.cs:180-246): priority = g + manhattan(node, START tile);
    // closed-at-enqueue; end check on every pop incl. the start; the budget counts
    // pops and a search that expands `limit` nodes fails. With skipClaimed, tiles
    // claimed by ANOTHER junimo fail the end check and the search continues
    // (PatchFindingCropEnd semantics); the first claimed crop popped is remembered
    // so a failed search can still shadow it.
    private List<int> AStarNearest(int start, int limit, Junimo self = null, bool skipClaimed = false) {
        var open = new PriorityQueue<int, (long f, long seq)>();
        long seq = 0;
        var gScore = new Dictionary<int, long> { [start] = 0 };
        var cameFrom = new Dictionary<int, int>();
        var closed = new HashSet<int> { start };
        open.Enqueue(start, (0, seq++));
        _shadowPath = null;
        _lastSearchShadow = false;
        _nearestSearches++;

        long pops = 0;
        while (open.Count > 0) {
            var cur = open.Dequeue();
            pops++;
            var claimedByOther = self != null && IsClaimedByOther(cur, self);
            if (_crops.Contains(cur)) {
                if (claimedByOther) {
                    if (!skipClaimed) {
                        AddPops(pops);
                        _lastSearchShadow = true;
                        return Reconstruct(cameFrom, cur);
                    }
                    if (_shadowPath == null) _shadowPath = Reconstruct(cameFrom, cur);
                } else {
                    AddPops(pops);
                    return Reconstruct(cameFrom, cur);
                }
            }

            foreach (var n in Neighbors(cur)) {
                if (_blocked[n] || closed.Contains(n)) continue;
                closed.Add(n); // real findPath closes children at enqueue time
                gScore[n] = gScore[cur] + 1;
                cameFrom[n] = cur;
                open.Enqueue(n, (gScore[n] + Man(start, n), seq++));
            }
            if (pops >= limit) { AddPops(pops); _nearestFails++; if (_shadowPath != null) _lastSearchShadow = true; return null; }
        }
        AddPops(pops);
        _nearestFails++;
        if (_shadowPath != null) _lastSearchShadow = true;
        return null;
    }

    // concrete-target A* with Manhattan heuristic (PathFindController's Point ctor);
    // same closed-at-enqueue + pop-budget semantics as the real findPath
    private List<int> AStarTo(int start, int target, int limit) {
        var open = new PriorityQueue<int, (long f, long seq)>();
        long seq = 0;
        var gScore = new Dictionary<int, long> { [start] = 0 };
        var cameFrom = new Dictionary<int, int>();
        var closed = new HashSet<int> { start };
        open.Enqueue(start, (Man(start, target), seq++));

        long pops = 0;
        while (open.Count > 0) {
            var cur = open.Dequeue();
            pops++;
            if (cur == target) { AddPops(pops); return Reconstruct(cameFrom, cur); }

            foreach (var n in Neighbors(cur)) {
                if (_blocked[n] || closed.Contains(n)) continue;
                closed.Add(n);
                gScore[n] = gScore[cur] + 1;
                cameFrom[n] = cur;
                open.Enqueue(n, (gScore[n] + Man(n, target), seq++));
            }
            if (pops >= limit) { AddPops(pops); return null; }
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

    private bool AnyOpenSlot => _junimos.Count > 0 && _junimos.Exists(j => j.State == Junimo.St.WaitingRespawn);

    private bool HasSpawnWork(int h) {
        if (_isAssign) {
            // the assignment table knows where the work is: zero tile reads,
            // cached for the same 60t period as the real spawn-helper scan
            var hut = _huts[h];
            if (_tick - hut.GateScanTick < Cfg.SCAN_COOLDOWN_TICKS) return hut.CachedHasWork;
            var has = false;
            foreach (var t in _pool)
                if (InBox(h, t) && hut.ReachSet.Contains(t)) { has = true; break; }
            hut.CachedHasWork = has;
            hut.GateScanTick = _tick;
            return has;
        }
        return ScanGate(h);
    }

    // the hut work scan, cached for 60t like PatchSearchAroundHut (mod 3.2.0+):
    // called from the spawn helper (open slot) and, on the patched algos, from the
    // decision path as the scan gate. Updates the hut's lkc like SearchHutGrid does.
    private bool ScanGate(int h) {
        var hut = _huts[h];
        if (_tick - hut.GateScanTick >= Cfg.SCAN_COOLDOWN_TICKS) {
            ScanWorkTiles(cap: 1, h);
            hut.CachedHasWork = _workTiles.Count > 0;
            hut.GateScanTick = _tick;
        }
        return hut.CachedHasWork;
    }

    // row-major scan of the hut's radius box with early exit at `cap` actionable tiles
    private void ScanWorkTiles(int cap, int h) {
        _workTiles.Clear();
        var hut = _huts[h];
        for (var x = hut.X + 1 - _radius; x <= hut.X + _radius && _workTiles.Count < cap; x++)
            for (var y = hut.Y + 1 - _radius; y <= hut.Y + _radius; y++) {
                if (x < 0 || y < 0 || x >= Cfg.W || y >= Cfg.H) continue;
                AddInsp(1);
                if (_crops.Contains(Idx(x, y))) {
                    _workTiles.Add(Idx(x, y));
                    if (_workTiles.Count >= cap) break;
                }
            }
        if (_workTiles.Count > 0) hut.Lkc = _workTiles[0];
        else hut.Lkc = -1; // scans reset lastKnownCropLocation to Zero when nothing is found
    }

    private void ReleaseOwnClaim(Junimo j) {
        if (!_ownerClaim.Remove(j, out var tile)) return;
        if (_claims.TryGetValue(tile, out var owner) && ReferenceEquals(owner, j)) { _claims.Remove(tile); _claimTick.Remove(tile); }
    }

    private bool IsClaimedByOther(int tile, Junimo self) {
        if (_claims.Count == 0) return false;
        if (!_claims.TryGetValue(tile, out var owner)) return false;
        if (!ReferenceEquals(owner, self)) {
            if (_claimTick.TryGetValue(tile, out var t) && _tick - t > Cfg.CLAIM_EXPIRY_TICKS) {
                if (_ownerClaim.TryGetValue(owner, out var oc) && oc == tile) _ownerClaim.Remove(owner);
                _claims.Remove(tile); _claimTick.Remove(tile);
                return false;
            }
            return true;
        }
        return false;
    }

    private void TryClaim(Junimo j, int tile) {
        if (_claims.TryGetValue(tile, out var existing) && _claimTick.TryGetValue(tile, out var t0) && _tick - t0 > Cfg.CLAIM_EXPIRY_TICKS) {
            if (_ownerClaim.TryGetValue(existing, out var oc) && oc == tile) _ownerClaim.Remove(existing);
            _claims.Remove(tile); _claimTick.Remove(tile);
        }
        if (IsClaimedByOther(tile, j)) return;
        ReleaseOwnClaim(j);
        _claims[tile] = j;
        _ownerClaim[j] = tile;
        _claimTick[tile] = _tick;
    }

    /* --------------------------- assign scheduler --------------------------- */

    // The periodic staggered per-hut scan (DESIGN.md §2): full-box perception,
    // incremental diff (healthy routes keep executing; divergers re-route; new
    // crops trigger re-assignment per the X threshold), then assignment.
    private void DoAssignScan(int h) {
        _unreachable.Clear();
        _scans++;
        var hut = _huts[h];
        // hybrid mode: the FIRST scan and every _fullEvery-th scan are full; the
        // scans in between verify route tiles only.
        // ScanCount = scans since the last full scan (0 = due for a full one).
        var fullScan = !_hybrid || hut.ScanCount == 0 || hut.ScanCount >= _fullEvery;
        hut.ScanCount = fullScan ? 1 : hut.ScanCount + 1;

        if (fullScan) {
            var ripeSet = new HashSet<int>();
            for (var x = hut.X + 1 - _radius; x <= hut.X + _radius; x++)
                for (var y = hut.Y + 1 - _radius; y <= hut.Y + _radius; y++) {
                    if (x < 0 || y < 0 || x >= Cfg.W || y >= Cfg.H) continue;
                    AddInsp(1);
                    var idx = Idx(x, y);
                    if (_crops.Contains(idx)) ripeSet.Add(idx);
                }

            // growth detection: ripe tiles unknown to the scheduler (new wave / day start)
            var growth = 0;
            if (_dayMode)
                foreach (var t in ripeSet)
                    if (!_routeTargets.ContainsKey(t) && !_pool.Contains(t) && _waveTickOf.TryGetValue(t, out var wt)) {
                        growth++;
                        hut.WaveLagSum += _tick - wt;
                        hut.WaveLagN++;
                    }
            _pool.RemoveWhere(t => _routeTargets.ContainsKey(t) || (InBox(h, t) && !ripeSet.Contains(t)));
            foreach (var t in ripeSet)
                if (!_routeTargets.ContainsKey(t)) _pool.Add(t);

            // route verification (diff against expected state) + idler collection.
            // Idle junimos (no route) ALWAYS enter R — scans are their only source of
            // work; the scope decision only controls whether healthy walkers' tails
            // are re-optimized.
            var R = new List<Junimo>();
            var pinned = new List<Junimo>();
            foreach (var j in _junimos) {
                if (j.HomeHut != h || j.State == Junimo.St.WaitingRespawn || j.State == Junimo.St.Working) continue;
                if (j.Route.Count == 0) {
                    if (j.State == Junimo.St.WaitingDecision) R.Add(j);
                    continue;
                }
                var broken = false;
                foreach (var t in j.Route)
                    if (!ripeSet.Contains(t)) { broken = true; break; }
                if (!broken) continue;
                // diverger: its route was (partly) harvested by someone else — release
                // and re-route from the current position
                _diverged++;
                ReleaseRoute(j);
                if (j.State == Junimo.St.Walking) {
                    j.Pos = CurrentTile(j);
                    j.State = Junimo.St.WaitingDecision;
                }
                R.Add(j);
            }

            // scope decision: big growth (> X of the active workforce) -> full re-assign
            // (healthy walkers pin route[0], release the tail, get re-optimized)
            var active = 0;
            foreach (var j in _junimos)
                if (j.HomeHut == h && j.State != Junimo.St.WaitingRespawn && j.State != Junimo.St.Working) active++;
            var fullReassign = growth > 0 && growth > _assignX * Math.Max(1, active);
            if (fullReassign) {
                foreach (var j in _junimos) {
                    if (j.HomeHut != h || j.State != Junimo.St.Walking || j.Route.Count == 0 || R.Contains(j)) continue;
                    pinned.Add(j);
                    for (var i = j.Route.Count - 1; i >= 1; i--) {
                        var t = j.Route[i];
                        _routeTargets.Remove(t);
                        if (ripeSet.Contains(t)) _pool.Add(t);
                        j.Route.RemoveAt(i);
                    }
                }
            }
            RunAssignment(h, R, pinned);
        } else {
            // hybrid partial scan: verify route tiles only (cheap); growth/pool
            // discovery is deferred to the next full scan (honest latency cost)
            foreach (var j in _junimos) {
                if (j.HomeHut != h || j.State == Junimo.St.WaitingRespawn) continue;
                if (j.Route.Count == 0 || j.State == Junimo.St.Working) continue;
                var broken = false;
                foreach (var t in j.Route) {
                    AddInsp(1);
                    if (!_crops.Contains(t)) { broken = true; break; }
                }
                if (!broken) continue;
                _diverged++;
                ReleaseRoute(j);
                if (j.State == Junimo.St.Walking) {
                    j.Pos = CurrentTile(j);
                    j.State = Junimo.St.WaitingDecision;
                }
                RunAssignment(h, new List<Junimo> { j }, new List<Junimo>());
            }
        }
    }

    // exclusive assignment for R (idle/diverged junimos) + tail re-optimization for
    // pinned walkers. Regret-greedy over m-nearest candidates (Chebyshev-ring query)
    // + round-robin nearest-neighbor route extension. Deterministic (ties broken by
    // tile index). All costs = walk (manhattan x 20t) + pick (harvest ticks).
    private void RunAssignment(int h, List<Junimo> R, List<Junimo> pinned) {
        if (R.Count == 0 && pinned.Count == 0) return;
        var hut = _huts[h];
        var alive = new HashSet<int>();
        foreach (var t in _pool) {
            if (_routeTargets.ContainsKey(t)) continue;
            if (!InBox(h, t) || !hut.ReachSet.Contains(t) || _unreachable.Contains(t)) continue;
            alive.Add(t);
        }

        // ---- regret-greedy first targets ----
        var firstT = new Dictionary<Junimo, int>();
        var cand = new Dictionary<Junimo, List<(long cost, int tile)>>();
        foreach (var j in R) {
            var list = new List<(long cost, int tile)>();
            NearestM(j.Pos, alive, Cfg.ASSIGN_CANDIDATES, list);
            cand[j] = list;
        }
        var open = new List<Junimo>(R);
        while (open.Count > 0 && alive.Count > 0) {
            Junimo pick = null;
            long bestRegret = long.MinValue;
            var bestTile = -1;
            foreach (var j in open) {
                var list = cand[j];
                long c1, c2;
                int t1;
                var idx = 0;
                while (idx < list.Count && !alive.Contains(list[idx].tile)) idx++;
                AddOps(idx);
                if (idx >= list.Count) {
                    // candidate list exhausted: one absolute-nearest ring query
                    var (cc, tt) = NearestOne(j.Pos, alive);
                    if (tt < 0) continue; // starved this scan
                    c1 = cc; t1 = tt;
                    c2 = c1 + 1_000_000; // unique candidate -> high regret
                } else {
                    c1 = list[idx].cost; t1 = list[idx].tile;
                    var idx2 = idx + 1;
                    while (idx2 < list.Count && !alive.Contains(list[idx2].tile)) idx2++;
                    AddOps(idx2 - idx - 1);
                    c2 = idx2 < list.Count ? list[idx2].cost : c1 + 1_000_000;
                }
                var regret = c2 - c1;
                if (regret > bestRegret) { bestRegret = regret; pick = j; bestTile = t1; }
            }
            if (pick == null) break;
            firstT[pick] = bestTile;
            alive.Remove(bestTile);
            open.Remove(pick);
        }

        foreach (var j in R)
            if (firstT.TryGetValue(j, out var t0)) j.Route = new List<int> { t0 };

        // ---- round-robin nearest-neighbor route extension ----
        var ext = new List<Junimo>();
        foreach (var j in R)
            if (j.Route.Count > 0) ext.Add(j);
        ext.AddRange(pinned);
        for (var step = 1; step < _assignRouteLen; step++)
            foreach (var j in ext) {
                if (j.Route.Count != step) continue;
                var nn = NearestOne(j.Route[^1], alive);
                if (nn.tile >= 0) { j.Route.Add(nn.tile); alive.Remove(nn.tile); }
            }

        // ---- register routes (global exclusive table) ----
        foreach (var j in ext)
            foreach (var t in j.Route) {
                _routeTargets[t] = j;
                _pool.Remove(t);
            }

        // ---- pathfind: one A* per (re)assigned standing junimo ----
        foreach (var j in R) {
            if (j.Route.Count == 0) {
                j.NextEventTick = _huts[h].NextScanTick + 1; // nothing to assign: sleep to next scan
                continue;
            }
            if (j.State != Junimo.St.WaitingDecision) continue;
            var p = AStarTo(j.Pos, j.Route[0], Cfg.EXP_LIMIT_ASSIGN);
            if (p == null) {
                _unreachable.Add(j.Route[0]);
                ReleaseRoute(j);
                j.Route = new List<int>();
                j.NextEventTick = _huts[h].NextScanTick + 1;
                continue;
            }
            StartWalk(j, p, _tick);
        }
    }

    private long PickTicks => HarvestTicks;

    // Route-exhaustion top-up: assign ONE nearest free crop directly from the
    // assignment table (no tile reads — the scan is still the only perception
    // layer). Cost per call: one ring query over the free pool + at most one
    // target A*; fires once per harvest completion at most. Returns false when
    // the table has no reachable work for this junimo's hut (caller sleeps to
    // the next scan, where the collective assignment runs).
    private bool SelfAssign(Junimo j) {
        var hut = _huts[j.HomeHut];
        var alive = new HashSet<int>();
        foreach (var t in _pool) {
            if (_routeTargets.ContainsKey(t)) continue;
            if (!InBox(j.HomeHut, t) || !hut.ReachSet.Contains(t) || _unreachable.Contains(t)) continue;
            alive.Add(t);
        }
        var nn = NearestOne(j.Pos, alive);
        if (nn.tile < 0) return false;
        j.Route = new List<int> { nn.tile };
        _routeTargets[nn.tile] = j;
        _pool.Remove(nn.tile);
        var p = AStarTo(j.Pos, nn.tile, Cfg.EXP_LIMIT_ASSIGN);
        if (p == null) {
            _unreachable.Add(nn.tile);
            ReleaseRoute(j);
            return false;
        }
        StartWalk(j, p, _tick);
        return true;
    }

    // m nearest alive tiles from pos via outward Chebyshev rings (a real mod would
    // run the same spatial query); probes counted as ops; ties by (cost, tile)
    private void NearestM(int pos, HashSet<int> alive, int m, List<(long cost, int tile)> outList) {
        if (alive.Count == 0) return;
        var found = new List<(long cost, int tile)>();
        var px = XOf(pos); var py = YOf(pos);
        for (var r = 0; r <= 2 * _radius && found.Count < m + 4; r++) {
            for (var dx = -r; dx <= r; dx++)
                for (var dy = -r; dy <= r; dy++) {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var x = px + dx; var y = py + dy;
                    if (x < 0 || y < 0 || x >= Cfg.W || y >= Cfg.H) continue;
                    AddOps(1);
                    var idx = Idx(x, y);
                    if (alive.Contains(idx))
                        found.Add(((long)(Math.Abs(dx) + Math.Abs(dy)) * Cfg.TICKS_PER_TILE + PickTicks, idx));
                }
        }
        found.Sort((a, b) => a.cost != b.cost ? a.cost.CompareTo(b.cost) : a.tile.CompareTo(b.tile));
        for (var i = 0; i < found.Count && i < m; i++) outList.Add(found[i]);
    }

    private (long cost, int tile) NearestOne(int pos, HashSet<int> alive) {
        if (alive.Count == 0) return (long.MaxValue, -1);
        var px = XOf(pos); var py = YOf(pos);
        for (var r = 0; r <= 2 * _radius; r++) {
            long bc = long.MaxValue;
            var bt = -1;
            for (var dx = -r; dx <= r; dx++)
                for (var dy = -r; dy <= r; dy++) {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var x = px + dx; var y = py + dy;
                    if (x < 0 || y < 0 || x >= Cfg.W || y >= Cfg.H) continue;
                    AddOps(1);
                    var idx = Idx(x, y);
                    if (!alive.Contains(idx)) continue;
                    var c = (long)(Math.Abs(dx) + Math.Abs(dy)) * Cfg.TICKS_PER_TILE;
                    if (c < bc || (c == bc && idx < bt)) { bc = c; bt = idx; }
                }
            if (bt >= 0) return (bc + PickTicks, bt); // first ring with a hit holds the nearest
        }
        return (long.MaxValue, -1);
    }

    // release a junimo's route: registrations removed; still-ripe tiles return to the pool
    private void ReleaseRoute(Junimo j) {
        foreach (var t in j.Route) {
            if (_routeTargets.TryGetValue(t, out var owner) && ReferenceEquals(owner, j)) _routeTargets.Remove(t);
            if (_crops.Contains(t)) _pool.Add(t);
        }
        j.Route = new List<int>();
    }

    private int CurrentTile(Junimo j) {
        var prog = (int)((_tick - j.WalkStartTick) / Cfg.TICKS_PER_TILE);
        return prog <= 0 || j.Path == null || j.Path.Count == 0 ? j.Pos : j.Path[Math.Min(prog, j.Path.Count) - 1];
    }

    /* ------------------------------ decisions ------------------------------ */

    private bool IsCappedAlgo => _algo is "cap_only" or "v4b_realfilter";

    private void Decide(Junimo j, long tick) {
        _decisions++;
        // decision rate cap (PatchPathfindDoWork, JunimoHarvesterPatches.cs:250):
        // checked BEFORE everything — a capped junimo keeps its current path / stands
        if (IsCappedAlgo && _backoff.TryGetValue(j, out var until) && tick < until) {
            _gatedOut++;
            return;
        }

        if (_rnd.NextDouble() < WanderProb) {
            Wander(j, tick);
            if (IsCappedAlgo) _backoff[j] = tick + Cfg.SUCCESS_CAP_TICKS;
            return;
        }

        switch (_algo) {
            case "legacy_arrival": DecideLegacy(j, tick); break;
            case "cap_only": DecideCapOnly(j, tick); break;
            case "v4b_realfilter": DecideV4bRealFilter(j, tick); break;
        }
    }

    // BJ 3.2.0 decision logic: nearest-actionable search (limit 100), then the real
    // fallback chain (50% lkc — a null lkc path leaves the junimo STANDING; 25% home;
    // else wander x6). The found endpoint must lie in the HOME hut's radius box
    // (junimo.cs:384 semantics; a no-op with one hut, binding with two).
    private void DecideLegacy(Junimo j, long tick) {
        // in the real mod the hut scan runs ONLY from JunimoSpawnHelper while a slot
        // is open (60t cache); with the hut full, lkc is frozen
        if (AnyOpenSlot) ScanGate(j.HomeHut);

        var path = AStarNearest(j.Pos, Cfg.EXP_LIMIT);
        if (path != null && path.Count > 0 && InBox(j.HomeHut, path[^1])) { StartWalk(j, path, tick); return; }

        var roll = _rnd.NextDouble();
        var lkc = _huts[j.HomeHut].Lkc;
        if (roll < 0.5 && lkc >= 0) {
            var p = AStarTo(j.Pos, lkc, Cfg.EXP_LIMIT);
            if (p != null) { StartWalk(j, p, tick); return; }
            return; // lkc controller with a null path: the junimo stands
        }

        roll = _rnd.NextDouble();
        if (roll < 0.25) {
            _respawns++;
            j.State = Junimo.St.WaitingRespawn;
            j.NextEventTick = tick + Cfg.SPAWN_STAGGER_TICKS;
        } else {
            Wander(j, tick);
        }
    }

    // the shipped patch's cap + scan gate, vanilla target choice (no claim filter)
    private void DecideCapOnly(Junimo j, long tick) {
        if (!ScanGate(j.HomeHut)) { VanillaTail(j, tick, allowLkc: false); return; }

        var path = AStarNearest(j.Pos, Cfg.EXP_LIMIT);
        if (path != null && path.Count > 0 && InBox(j.HomeHut, path[^1])) {
            _backoff[j] = tick + Cfg.SUCCESS_CAP_TICKS;
            StartWalk(j, path, tick);
            return;
        }

        _backoff[j] = tick + Cfg.BACKOFF_TICKS;
        VanillaFallbackTail(j, tick, tryLkc: true);
    }

    // v4b_realfilter: cap + gate + the REAL claim-filter semantics: the filtered end
    // check skips tiles claimed by another junimo and keeps searching; only when no
    // unclaimed actionable tile exists in the whole budget does the mod re-run the
    // search unfiltered (a second full A*) and shadow the nearest claimed tile.
    private void DecideV4bRealFilter(Junimo j, long tick) {
        if (!ScanGate(j.HomeHut)) { VanillaTail(j, tick, allowLkc: false); return; }

        var path = AStarNearest(j.Pos, Cfg.EXP_LIMIT, self: j, skipClaimed: true);
        var shadow = false;
        if (path == null) {
            shadow = true;
            path = AStarNearest(j.Pos, Cfg.EXP_LIMIT, self: null);
        }
        if (path != null && path.Count > 0 && InBox(j.HomeHut, path[^1])) {
            _backoff[j] = tick + Cfg.SUCCESS_CAP_TICKS;
            if (shadow) {
                // shadowing a claimed tile: TryClaim is skipped (tile claimed by other)
                StartWalk(j, path, tick);
                return;
            }
            TryClaim(j, path[^1]);
            StartWalk(j, path, tick);
            return;
        }

        ReleaseOwnClaim(j);
        _backoff[j] = tick + Cfg.BACKOFF_TICKS;
        VanillaFallbackTail(j, tick, tryLkc: true);
    }

    // the vanilla fallback tail (JunimoHarvester.pathfindToNewCrop:386-398):
    // 50% to the last known work tile (when lkc != Zero; a null lkc path leaves the
    // junimo STANDING), else 25% home, else wander
    private void VanillaFallbackTail(Junimo j, long tick, bool tryLkc) {
        var lkc = _huts[j.HomeHut].Lkc;
        if (tryLkc && _rnd.NextDouble() < 0.5 && lkc >= 0) {
            var p = AStarTo(j.Pos, lkc, Cfg.EXP_LIMIT);
            if (p != null) { StartWalk(j, p, tick); return; }
            return;
        }
        var roll = _rnd.NextDouble();
        if (roll < 0.25) {
            _respawns++;
            j.State = Junimo.St.WaitingRespawn;
            j.NextEventTick = tick + Cfg.SPAWN_STAGGER_TICKS;
        } else {
            Wander(j, tick);
        }
    }

    private void VanillaTail(Junimo j, long tick, bool allowLkc) {
        var lkc = _huts[j.HomeHut].Lkc;
        if (allowLkc && _rnd.NextDouble() < 0.5 && lkc >= 0) {
            var p = AStarTo(j.Pos, lkc, Cfg.EXP_LIMIT);
            if (p != null) { StartWalk(j, p, tick); return; }
            return;
        }
        var roll = _rnd.NextDouble();
        if (roll < 0.25) {
            _respawns++;
            j.State = Junimo.St.WaitingRespawn;
            j.NextEventTick = tick + Cfg.SPAWN_STAGGER_TICKS;
        } else {
            Wander(j, tick);
        }
    }

    // JunimoHut.performTenMinuteAction pokes EVERY junimo of the hut every 10
    // game-minutes; ReplaceJunimoTimerNumber (JunimoHutPatches.cs:265-301) is a
    // postfix that pokes all myJunimos a SECOND time -> two independent p=0.7
    // decisions per junimo per 430t. Walking junimos are interrupted (the fresh
    // pathfind replaces the controller — junimo.cs:589); harvesting junimos are
    // ignored (harvestTimer > 0); assign-mode junimos are no-ops (their routes are
    // not re-planned by pokes — that is where its peak-window win comes from).
    private void DoHutPoke(long tick) {
        if (_isAssign) return;
        _hutPokes++;
        for (var round = 0; round < 2; round++) {
            for (var i = 0; i < _junimos.Count; i++) {
                var j = _junimos[i];
                if (j.State == Junimo.St.WaitingRespawn || j.State == Junimo.St.Working) continue;
                if (!PokeRoll()) continue;
                if (IsCappedAlgo && _backoff.TryGetValue(j, out var until) && tick < until) continue;
                if (j.State == Junimo.St.Walking) {
                    var progress = (int)((tick - j.WalkStartTick) / Cfg.TICKS_PER_TILE);
                    j.Pos = progress <= 0 ? j.Origin : j.Path[Math.Min(progress, j.Path.Count) - 1];
                }
                Decide(j, tick);
                if (j.State == Junimo.St.WaitingDecision)
                    j.NextEventTick = tick + SampleIdleRandom();
            }
        }
    }

    // spontaneous idle decision (JunimoHarvester.update): a standing junimo rolls
    // 0.2%/frame with 1/6 -> pathfindToNewCrop directly (no 70% gate)
    private long SampleIdleRandom() {
        var u = _rnd.NextDouble();
        if (u <= 0.0) u = 1e-12;
        return Math.Max(1, (long)(-Cfg.IDLE_RANDOM_MEAN_TICKS * Math.Log(u)));
    }

    private void Wander(Junimo j, long tick) {
        // PatchPathfindToRandomSpotAroundHut: each retry is a full-budget A*; BJ 3.2.0
        // retried 6 times, the shipped build caps it at 2
        var attempts = _algo == "legacy_arrival" ? Cfg.WANDER_ATTEMPTS_LEGACY : Cfg.WANDER_ATTEMPTS_PATCHED;
        var hut = _huts[j.HomeHut];
        for (var attempt = 0; attempt < attempts; attempt++) {
            var rx = hut.X + _rnd.Next(-_radius, _radius + 1);
            var ry = hut.Y + _rnd.Next(-_radius, _radius + 1);
            if (rx < 0 || ry < 0 || rx >= Cfg.W || ry >= Cfg.H) continue;
            var p = AStarTo(j.Pos, Idx(rx, ry), Cfg.EXP_LIMIT);
            if (p != null) { StartWalk(j, p, tick); return; }
        }
        j.State = Junimo.St.WaitingDecision;
    }

    private void StartWalk(Junimo j, List<int> path, long tick) {
        j.Path = path;
        j.Origin = j.Pos;
        j.WalkStartTick = tick;
        j.TargetTile = path.Count > 0 ? path[^1] : j.Pos;
        j.State = Junimo.St.Walking;
        j.NextEventTick = tick + path.Count * Cfg.TICKS_PER_TILE;
    }
}
