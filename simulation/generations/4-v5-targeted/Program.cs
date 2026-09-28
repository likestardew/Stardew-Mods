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
 * V5 COPY (sim-v5): derived from sim-audit1 (the audited, corrected simulator).
 * All sim-audit1 modeling is retained; this copy ADDS:
 *
 *   1. DOUBLE HUT POKE (real mod behavior, was missing): vanilla
 *      JunimoHut.performTenMinuteAction pokes every junimo of the hut AND the
 *      mod's ReplaceJunimoTimerNumber is a POSTFIX on the same method that pokes
 *      them ALL again (official/BetterJunimos/Patches/JunimoHutPatches.cs:265-302,
 *      HEAD d94e38a). Two independent pokeToHarvest calls per junimo per 430t,
 *      each with its own 70% gate and its own decision-cap check. For a WALKING
 *      junimo the second successful poke replaces the controller mid-walk
 *      (vanilla pathfindToNewCrop and PatchPathfindDoWork's work branch have no
 *      controller guard); for legacy_arrival that means a discarded path + fresh
 *      search (real semantics).
 *
 *   2. HARVEST ACCELERATION (the user's real config): JunimoImprovements
 *      .WorkFaster=true (assumed UNLOCKED in Util.Progression) and
 *      .WorkRidiculouslyFast=true. Semantics from upstream 3.2.0
 *      (git show faa40d4:BetterJunimos/Patches/JunimoHarvesterPatches.cs):
 *        - harvest arrival: time = 2000 ALWAYS (line 72-75) — the 2000ms/120t
 *          cycle with the crop taken at the 1000ms/60t crossing is unchanged;
 *        - PatchJunimoShake (85-101) zeroes harvestTimer only when it lands
 *          EXACTLY on 999 — from 2000 with ~16ms frame steps the crossing lands
 *          on 982-999 only when cumulative elapsed == 1001ms exactly (a frame
 *          jitter lottery), so WorkFaster does NOT reliably shorten the harvest
 *          cycle; we model the cycle as unchanged (120t);
 *        - "nothing to do" timer: Progression.WorkFaster ? 5ms : 200ms (line 81)
 *          -> the second poke arrives after ~1 tick, not 12;
 *        - PatchPokeToHarvest postfix (293-297): after ANY pokeToHarvest with
 *          destroy==false && controller==null, pathfindToNewCrop() runs
 *          unconditionally. Consequences modeled here:
 *            a) harvest-timer expiry re-decides at 100% (70% gate bypassed);
 *            b) "nothing to do" arrival re-decides at 100% immediately;
 *            c) a 430t poke landing on a HARVESTING junimo (harvestTimer > 0
 *               -> gate fails; postfix: destroy false, controller null) makes it
 *               pathfindToNewCrop: phase-0 harvests are ABORTED (the junimo
 *               walks off, the crop stays), phase-1 empty stands are cut short.
 *               Capped algos whose decision cap is still active keep harvesting
 *               (the capped call is dropped, controller stays null);
 *            d) the 3.5% random stroll never fires (line 254: `rand < 0.035 &&
 *               !WorkRidiculouslyFast`).
 *      JUNIMOSIM_WRF=0 restores the pre-acceleration model for A/B checks.
 *
 *   3. CLEARING PHASE STATISTICS (throughput mode): time and cumulative A*
 *      expansions at 25/50/75/100% of the map cleared, plus the worst 60t (1s)
 *      A* window over the whole run (previously day-mode only). This is the
 *      instrument that localizes the "more stutter spikes late in the clearing"
 *      and "efficiency degrades towards the end" complaints.
 *
 *   4. TWO-HUT OVERLAP SCENARIO (JUNIMOSIM_HUTS=2): huts at (28,32) and (52,32),
 *      radius 14 boxes overlap in 5 columns (~145 tiles). Per-hut gate scans /
 *      lkc / spawn pacing, ONE GLOBAL claim table (CropClaims is a static
 *      (location, tile) table — official/.../Utils/CropClaims.cs), and the real
 *      outside-the-own-radius-box endpoint discard (PatchPathfindDoWork /
 *      vanilla 3.2.0 `outsideRadius` check), which never binds with one hut (all
 *      crops are inside the box by construction) but shapes cross-hut behavior.
 *
 *   5. THE V5 ALGORITHM FAMILY: v4b_realfilter (cap + gate + real claim filter)
 *      plus three parameterized fixes (see DESIGN.md):
 *        - fix 1  conditional dedup: the claim filter is DISABLED whenever the
 *          hut's cached box scan counts <= K junimos-per-hut crops (k), or when
 *          the nearest unclaimed crop is > D tiles farther than the nearest crop
 *          of any kind (d, per-decision detour limit);
 *        - fix 2  endgame fallback: same disable when boxCrops <= M * junimos
 *          (m; same mechanism as k with a dedicated threshold for the endgame);
 *        - fix 3  crowding-verdict throttle: when a filtered search fails (whole
 *          budget claimed), the hut caches that verdict for T ticks; while fresh,
 *          later decisions skip the doomed filtered search and run the unfiltered
 *          shadow search directly (same target, half the A*). Verdict is
 *          invalidated by any filtered success; a stale verdict self-corrects
 *          (an unfiltered endpoint that turns out unclaimed is claimed normally).
 *      Algo spec strings carry the parameters: v5_k2_d0_m1_t40 (0 = off).
 *
 * retained from sim-audit1 (unchanged modeling, see its REPORT.md):
 *  - grid farm 80x65, ~5% blocked tiles (CPU cells 5%/25%), crops inside the
 *    radius box on tiles reachable from the hut; two-phase harvest (crop taken
 *    at the 60t crossing, junimo busy to 120t; doomed followers take nothing);
 *  - real PathFindController.findPath semantics: priority = g + manhattan(node,
 *    START), closed-on-enqueue, endFunction checked on every pop incl. start,
 *    budget counts pops, limit-th pop still checked then fails;
 *  - lkc fallback used as-is (no crop re-validation), null lkc path = stand;
 *  - real event-driven trigger set {arrival, harvest-timer, hut poke(s), idle
 *    random} identical for every algorithm; claim expiry 1800t; 60t scan cache;
 *  - paired seeds: (huts, radius, junimos, density|obstacles, run) ONLY.
 */

internal static class Cfg {
    public const int W = 80, H = 65;
    public const int TICKS_PER_TILE = 20;
    public const int HARVEST_TICKS = 120;        // 2000ms real cycle: crop lands at the 1000ms crossing
    public const int HARVEST_CROSSING_TICKS = 60; // 1000ms: JunimoHarvester.update harvests & frees the crop here
    public const int TIMER_POKE_TICKS = 12;      // 200ms "nothing to do" timer (WorkFaster makes it ~1t, see WRF)
    public const double POKE_PROB = 0.70;        // pokeToHarvest: 70% -> pathfindToNewCrop (junimo.cs:304)
    public const double WANDER_PROB = 0.035;     // 3.5% random stroll — DISABLED by WorkRidiculouslyFast
    public const int HUT_POKE_TICKS = 430;       // performTenMinuteAction re-pokes ALL junimos every 10 game-min
    public const int IDLE_RANDOM_MEAN_TICKS = 3000; // idle randoms: 0.2%/frame x 1/6 -> pathfindToNewCrop (no gate)
    public const int WANDER_ATTEMPTS_LEGACY = 6; // BJ 3.2.0 PatchPathfindToRandomSpotAroundHut: retry <= 5
    public const int WANDER_ATTEMPTS_PATCHED = 2; // shipped build caps the retries at 2
    public const int SPAWN_STAGGER_TICKS = 60;   // JunimoSpawnHelper.SpawnCooldownTicks = 60
    public const int SCAN_COOLDOWN_TICKS = 60;   // PatchSearchAroundHut.ScanCooldownTicks = 60
    public const int CLAIM_EXPIRY_TICKS = 1800;  // CropClaims.ExpiryTicks = 1800 (~30s)
    public const int EXP_LIMIT_LEGACY = 100;
    public const int EXP_LIMIT_CLAIMS = 1000;
    public const int MAX_WORK_TILES = 32;
    public const int SIM_CAP_TICKS = 240_000;

    public static readonly int RUNS = EnvInt("JUNIMOSIM_RUNS", 20);
    public static readonly int CPU_RUNS = EnvInt("JUNIMOSIM_CPU_RUNS", 5);

    // ---- v5 scenario switches ----
    // user's real config: WorkFaster=true (unlocked) + WorkRidiculouslyFast=true
    public static readonly bool WRF = EnvBool("JUNIMOSIM_WRF", true);
    // real mod behavior: vanilla performTenMinuteAction + ReplaceJunimoTimerNumber postfix
    public const bool DOUBLE_POKE = true;

    // hut layout: 1 hut centered; 2 huts at the measured overlap layout
    public static readonly int Huts = EnvInt("JUNIMOSIM_HUTS", 1);
    public static readonly (int x, int y)[] HutCenters = { (W / 2, H / 2), (28, 32), (52, 32) };

    public static readonly int[] Radii = { 14 };
    public static readonly int[] JunimoCounts = EnvInts("JUNIMOSIM_JUNIMOS", "2,4,6,8"); // PER HUT
    public static readonly double[] Densities = EnvDoubles("JUNIMOSIM_DENSITIES", "0.10,0.75,1.00");
    public static readonly string[] Algos =
        EnvStr("JUNIMOSIM_ALGOS", "legacy_arrival,cap_only,v4b_realfilter").Split(',');
    public static readonly string OutFile = EnvStr("JUNIMOSIM_OUT", "results.csv");

    // ---- CPU/day benchmark (steady-state churn + 4 crop waves) ----
    public const int DAY_TICKS = 36000;          // 6:00-20:00
    public const int BACKOFF_TICKS = 40;         // FailedDecisionBackoffTicks (HEAD PatchPathfindDoWork:200)
    public const int SUCCESS_CAP_TICKS = 20;     // DecisionIntervalTicks (HEAD PatchPathfindDoWork:199)
    public static readonly int[] WaveTicks = { 2000, 10000, 18000, 26000 };
    public static readonly int[] CpuRadii = { 14 };
    public static readonly int[] CpuJunimos = EnvInts("JUNIMOSIM_CPU_JUNIMOS", "2,6"); // PER HUT
    public static readonly string[] CpuAlgos =
        EnvStr("JUNIMOSIM_CPU_ALGOS", "legacy_arrival,cap_only,v4b_realfilter").Split(',');
    public static readonly double[] CpuObstacles = EnvDoubles("JUNIMOSIM_CPU_OBS", "0.05,0.25");
    public static readonly string CpuOutFile = EnvStr("JUNIMOSIM_CPU_OUT", "cpu_results.csv");

    // audit diagnostics (never touch the RNG stream)
    public static readonly bool Diag = Environment.GetEnvironmentVariable("JUNIMOSIM_DIAG") == "1";
    public static readonly string TraceSpec = Environment.GetEnvironmentVariable("JUNIMOSIM_TRACE");

    private static int EnvInt(string name, int def) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : def;

    private static bool EnvBool(string name, bool def) {
        var s = Environment.GetEnvironmentVariable(name);
        return s == null ? def : s is "1" or "true" or "True";
    }

    private static string EnvStr(string name, string def) {
        var s = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(s) ? def : s;
    }

    private static int[] EnvInts(string name, string def) {
        var s = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(s)) s = def;
        return s.Split(',').Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v > 0).ToArray();
    }

    private static double[] EnvDoubles(string name, string def) {
        var s = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(s)) s = def;
        return s.Split(',').Select(x => double.TryParse(x, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.0)
            .Where(v => v > 0).ToArray();
    }
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
        Parallel.ForEach(allRuns,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            cfg => {
                // paired methodology: the algorithm name (incl. v5 parameters) MUST
                // NOT feed the seed — same map for every algo at a given run index.
                var seed = HashCode.Combine("h" + Cfg.Huts, cfg.radius, cfg.junimos, cfg.density, cfg.run);
                var r = new Simulation(cfg.algo, cfg.radius, cfg.junimos, seed, cropDensity: cfg.density).Run();
                results.Add(cfg.algo, cfg.radius, cfg.junimos, cfg.density, cfg.run, r);
            });
        sw.Stop();
        Console.WriteLine($"[sim] {allRuns.Count} simulations in {sw.ElapsedMilliseconds} ms on {Environment.ProcessorCount} threads (huts={Cfg.Huts}, WRF={Cfg.WRF})\n");

        Report(results);
        File.WriteAllText(Cfg.OutFile, results.ToCsv());
        Console.WriteLine($"\n[sim] CSV written to {Cfg.OutFile}");

        CpuBenchmark();
    }

    // steady-state CPU over a full workday (6:00-20:00), crops ripening in 4 waves
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
        var sequential = Cfg.TraceSpec != null;
        if (sequential) {
            foreach (var cfg in runs) {
                var seed = HashCode.Combine("cpu", "h" + Cfg.Huts, cfg.radius, cfg.junimos, cfg.obs, cfg.run);
                var sim = new Simulation(cfg.algo, cfg.radius, cfg.junimos, seed, dayMode: true, obstacleRate: cfg.obs, run: cfg.run);
                store.Add(cfg.algo, cfg.radius, cfg.junimos, cfg.obs, cfg.run, sim.Run());
            }
        } else Parallel.ForEach(runs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, cfg => {
            var seed = HashCode.Combine("cpu", "h" + Cfg.Huts, cfg.radius, cfg.junimos, cfg.obs, cfg.run);
            var sim = new Simulation(cfg.algo, cfg.radius, cfg.junimos, seed, dayMode: true, obstacleRate: cfg.obs, run: cfg.run);
            var res = sim.Run();
            store.Add(cfg.algo, cfg.radius, cfg.junimos, cfg.obs, cfg.run, res);
            if (Cfg.Diag) sim.PrintDiag();
        });
        sw.Stop();

        Console.WriteLine($"\n[cpu] full-day benchmark: {runs.Count} sims in {sw.ElapsedMilliseconds} ms — radius {string.Join('/', Cfg.CpuRadii)}, waves at t={string.Join(',', Cfg.WaveTicks)}\n");
        foreach (var obs in Cfg.CpuObstacles) {
            var label = obs < 0.1 ? "open field (5% obstacles)" : "fenced farm (25% obstacles)";
            Console.WriteLine($"--- {label} ---");
            Console.WriteLine($"{"algo",-22} {"jn/hut",-6} | {"A* pops/day",-12} {"vs legacy",-10} | {"peak pops/1s",-13} {"vs legacy",-10}");
            Console.WriteLine(new string('-', 84));
            foreach (var algo in Cfg.CpuAlgos)
                foreach (var jn in Cfg.CpuJunimos) {
                    var rs = store.Get(algo, Cfg.CpuRadii[0], jn, obs);
                    if (rs.Count == 0) continue;
                    var pops = rs.Average(x => x.Expansions);
                    var peak = rs.Average(x => x.PeakWindow);
                    var baseRs = store.Get("legacy_arrival", Cfg.CpuRadii[0], jn, obs);
                    if (baseRs.Count == 0) continue;
                    var basePops = baseRs.Average(x => x.Expansions);
                    var basePeak = baseRs.Average(x => x.PeakWindow);
                    Console.WriteLine($"{algo,-22} {jn,-6} | {pops,-12:F0} {pops / basePops,-9:F2}x | {peak,-13:F0} {peak / basePeak,-9:F2}x");
                }
            Console.WriteLine();
        }
        File.WriteAllText(Cfg.CpuOutFile, store.ToCsv());
        Console.WriteLine($"[cpu] CSV written to {Cfg.CpuOutFile}");
    }

    private static void Report(ResultStore results) {
        Console.WriteLine(
            $"{"algo",-22} {"radius",-7} {"jn/hut",-6} | {"株数",-6} | {"clear(s) avg",-12} {"sd",-7} | {"A* expans.",-10} | {"peak60",-8} | {"deadArrive",-10} {"doomed",-8} | DNF");
        Console.WriteLine(new string('-', 120));
        foreach (var density in Cfg.Densities) {
            var label = density < 0.5 ? "sparse planting (10% of tiles)" : density < 0.9 ? "circular planting (~75% of tiles)" : "fully planted square (100%)";
            Console.WriteLine($"--- {label} ---");
            foreach (var algo in Cfg.Algos)
                foreach (var r in Cfg.Radii)
                    foreach (var j in Cfg.JunimoCounts) {
                        var rs = results.Get(algo, r, j, density);
                        var done = rs.Where(x => !x.Dnf).ToList();
                        if (done.Count == 0) {
                            Console.WriteLine($"{algo,-22} {r,-7} {j,-6} | {"ALL DNF",-6}");
                            continue;
                        }
                        var meanT = done.Average(x => x.ClearTicks) / 60.0;
                        var sdT = Math.Sqrt(done.Average(x => Math.Pow(x.ClearTicks / 60.0 - meanT, 2)));
                        Console.WriteLine(
                            $"{algo,-22} {r,-7} {j,-6} | {done.Average(x => x.CropCount),-6:F0} | {meanT,-12:F2} {sdT,-7:F2} | {done.Average(x => x.Expansions),-10:F0} | {done.Average(x => x.PeakWindow),-8:F0} | {done.Average(x => x.WastedArrivals),-10:F1} {done.Average(x => x.DoomedArrivals),-8:F1} | {rs.Count - done.Count}");
                    }
            Console.WriteLine();
        }
    }
}

internal readonly struct RunResult {
    public RunResult(long clearTicks, long expansions, int wastedArrivals, int respawns, long scanOps, long peakWindow,
        int cropCount, bool dnf, int doomedArrivals, long[] phaseTicks, long[] phasePops, long hut0Pops, long hut1Pops,
        int overlapHarvests, int shadowSearches, int throttleHits, int detourIgnored, int filterOffDecisions) {
        ClearTicks = clearTicks; Expansions = expansions; WastedArrivals = wastedArrivals; Respawns = respawns;
        ScanOps = scanOps; PeakWindow = peakWindow; CropCount = cropCount; Dnf = dnf; DoomedArrivals = doomedArrivals;
        PhaseTicks = phaseTicks; PhasePops = phasePops; Hut0Pops = hut0Pops; Hut1Pops = hut1Pops;
        OverlapHarvests = overlapHarvests; ShadowSearches = shadowSearches; ThrottleHits = throttleHits;
        DetourIgnored = detourIgnored; FilterOffDecisions = filterOffDecisions;
    }
    public readonly long ClearTicks;
    public readonly long Expansions;
    public readonly int WastedArrivals;
    public readonly int Respawns;
    public readonly long ScanOps;
    public readonly long PeakWindow; // max A* pops in any 60-tick (1s) window
    public readonly int CropCount;
    public readonly bool Dnf;
    public readonly int DoomedArrivals;
    public readonly long[] PhaseTicks; // cumulative ticks at 25/50/75/100% cleared (-1 = unreached)
    public readonly long[] PhasePops;  // cumulative A* expansions at the same points
    public readonly long Hut0Pops;
    public readonly long Hut1Pops;
    public readonly int OverlapHarvests;
    public readonly int ShadowSearches;    // unfiltered crowded re-searches
    public readonly int ThrottleHits;      // v5 fix-3 verdict-cache hits
    public readonly int DetourIgnored;     // v5 fix-1b detour-limit bypasses
    public readonly int FilterOffDecisions; // v5 fix-1/2 decisions with the filter disabled
}

internal sealed class CpuStore {
    private readonly object _lock = new();
    private readonly Dictionary<(string, int, int, double), List<(int run, RunResult r)>> _map = new();

    public void Add(string algo, int radius, int junimos, double obs, int run, RunResult r) {
        lock (_lock) {
            var key = (algo, radius, junimos, obs);
            if (!_map.TryGetValue(key, out var list)) _map[key] = list = new List<(int, RunResult)>();
            list.Add((run, r));
        }
    }

    public List<RunResult> Get(string algo, int radius, int junimos, double obs) {
        lock (_lock) { return _map.TryGetValue((algo, radius, junimos, obs), out var l) ? l.Select(x => x.r).ToList() : new List<RunResult>(); }
    }

    public string ToCsv() {
        var sb = new StringBuilder("algo,junimos,obstacles,huts,run,total_pops,peak_pops_1s,crops,hut0_pops,hut1_pops\n");
        foreach (var ((algo, r, j, obs), list) in _map)
            foreach (var (run, x) in list)
                sb.AppendLine($"{algo},{j},{obs},{Cfg.Huts},{run},{x.Expansions},{x.PeakWindow},{x.CropCount},{x.Hut0Pops},{x.Hut1Pops}");
        return sb.ToString();
    }
}

internal sealed class ResultStore {
    private readonly object _lock = new();
    private readonly Dictionary<(string, int, int, double), List<(int run, RunResult r)>> _map = new();

    public void Add(string algo, int radius, int junimos, double density, int run, RunResult r) {
        lock (_lock) {
            var key = (algo, radius, junimos, density);
            if (!_map.TryGetValue(key, out var list)) _map[key] = list = new List<(int, RunResult)>();
            list.Add((run, r));
        }
    }

    public List<RunResult> Get(string algo, int radius, int junimos, double density) {
        lock (_lock) { return _map.TryGetValue((algo, radius, junimos, density), out var l) ? l.Select(x => x.r).ToList() : new List<RunResult>(); }
    }

    public string ToCsv() {
        var sb = new StringBuilder(
            "algo,radius,junimos,huts,density,run,clear_ticks,crops,expansions,wasted_arrivals,respawns,scan_ops,dnf,doomed_arrivals," +
            "peak60,ph1_t,ph2_t,ph3_t,ph4_t,ph1_p,ph2_p,ph3_p,ph4_p,hut0_pops,hut1_pops,overlap_harvests," +
            "shadow_searches,throttle_hits,detour_ignored,filter_off\n");
        foreach (var ((algo, r, j, d), list) in _map)
            foreach (var (run, x) in list) {
                var ph = x.PhaseTicks;
                var pp = x.PhasePops;
                sb.AppendLine(
                    $"{algo},{r},{j},{Cfg.Huts},{d},{run},{x.ClearTicks},{x.CropCount},{x.Expansions},{x.WastedArrivals},{x.Respawns},{x.ScanOps},{x.Dnf},{x.DoomedArrivals}," +
                    $"{x.PeakWindow},{ph[0]},{ph[1]},{ph[2]},{ph[3]},{pp[0]},{pp[1]},{pp[2]},{pp[3]},{x.Hut0Pops},{x.Hut1Pops},{x.OverlapHarvests}," +
                    $"{x.ShadowSearches},{x.ThrottleHits},{x.DetourIgnored},{x.FilterOffDecisions}");
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
    public bool Retargets;        // vanilla cadence: re-plan mid-walk (legacy & claims_v4b historical models)
    public int WorkingPhase;      // 0 = until the 1000ms harvest crossing, 1 = until the 2000ms poke
    public bool PendingTimerPoke; // tryToHarvestHere "nothing to do": harvestTimer=200ms -> second poke
    public int Hut;               // owning hut index
}

internal sealed class Simulation {
    private readonly string _algo;      // full spec (may carry v5 parameters, e.g. v5_k2_d0_m1_t40)
    private readonly string _baseAlgo;  // v5 | legacy_arrival | cap_only | v4b_realfilter | ... historical
    private readonly int _v5K, _v5D, _v5M, _v5T; // v5 fix parameters (0 = disabled)
    private readonly int _radius;
    private readonly int _junimoCount;  // PER HUT
    private readonly int _run;
    private readonly Random _rnd;

    private readonly bool[] _blocked = new bool[Cfg.W * Cfg.H];
    private readonly double _obstacleRate;
    private readonly HashSet<int> _crops = new();
    private readonly List<Junimo> _junimos = new();

    // per-hut shared state (lkc / gate scan / spawn pacing / v5 verdict / crop count)
    private readonly int _hutCount;
    private readonly int[] _hutTiles;
    private readonly int[] _lkc;                 // legacy shared lastKnownCropLocation, per hut
    private readonly long[] _gateScanTick;
    private readonly bool[] _cachedHasWork;
    private readonly long[] _lastSpawnTick;      // JunimoSpawnHelper pacing, per hut
    private readonly long[] _allClaimedUntil;    // v5 fix-3 crowding verdict, per hut
    private readonly int[] _cropsInBox;          // v5 fix-1/2: actionable count from the counting gate scan
    private readonly long[] _hutExpansions;      // A* pops attribution per hut

    // ONE GLOBAL claim table — CropClaims is a static (GameLocation, Point) table
    private readonly Dictionary<int, Junimo> _claims = new(); // tile -> owner
    private readonly Dictionary<Junimo, int> _ownerClaim = new();
    private readonly Dictionary<int, long> _claimTick = new(); // tile -> tick the claim was made
    private readonly List<int> _workTiles = new();            // scan list (row-major)
    private long _lastScanTick = long.MinValue / 2;

    private long _expansions;
    private long _scanOps;
    private int _activeHut;         // pops attribution: hut of the junimo being processed
    private bool _lastSearchShadow; // AStarNearest: returned endpoint was claimed by another junimo
    private List<int> _shadowPath;  // path to the nearest claimed crop, remembered by a failed skip-claims search
    private List<int> _firstCropPath; // v5 fix-1b: nearest crop of ANY kind popped by the current search
    private bool _detourIgnored;      // v5 fix-1b: the search returned the nearest crop, ignoring claims

    // day/CPU mode
    private readonly bool _dayMode;
    private long[] _tickPops;
    private readonly List<(long tick, int tile)> _waves = new();
    private int _waveIdx;
    private readonly double _cropDensity;
    private int _cropCount;
    private int _wastedArrivals;
    private int _doomedArrivals;
    private int _respawns;
    private int _overlapHarvests;
    private int _shadowSearches;
    private int _throttleHits;
    private int _detourIgnoredCount;
    private int _filterOffDecisions;
    private long _tick;
    private long _nextPokeTick;
    private readonly Dictionary<Junimo, long> _backoff = new(); // decision cap: junimo -> next allowed tick

    // ---- clearing phase statistics (throughput mode) ----
    private readonly long[] _phaseTicks = { -1, -1, -1, -1 }; // cumulative ticks at 25/50/75/100% cleared
    private readonly long[] _phasePops = { -1, -1, -1, -1 };
    private int _phaseNext;

    // ---- audit instrumentation (no RNG use) ----
    private readonly bool _diag;
    private readonly bool _trace;
    private readonly long[] _stateTicks = new long[4];
    private long _decisions, _nearestSearches, _nearestFails, _hutPokes, _idleRandoms, _gatedOut;

    public void PrintDiag() {
        if (!_diag) return;
        var total = _stateTicks[0] + _stateTicks[1] + _stateTicks[2] + _stateTicks[3];
        Console.WriteLine(
            $"[diag] {_algo} obs={_obstacleRate} jn={_junimoCount} run={_run} crops={_cropCount} pops={_expansions} | " +
            $"dec={_decisions} gated={_gatedOut} nearest={_nearestSearches}(fail {_nearestFails}) " +
            $"pokes={_hutPokes} idleRnd={_idleRandoms} respawn={_respawns} wasted={_wastedArrivals} | " +
            $"state%: wait={100.0 * _stateTicks[0] / total:F0} walk={100.0 * _stateTicks[1] / total:F0} " +
            $"work={100.0 * _stateTicks[2] / total:F0} dead={100.0 * _stateTicks[3] / total:F0}");
    }

    public Simulation(string algo, int radius, int junimos, int seed, bool dayMode = false, double obstacleRate = 0.05, double cropDensity = 0.10, int run = -1) {
        _algo = algo;
        _baseAlgo = algo;
        _v5K = _v5D = _v5M = _v5T = 0;
        if (algo.StartsWith("v5")) {
            _baseAlgo = "v5";
            foreach (var part in algo.Split('_').Skip(1))
                if (part.Length > 1 && int.TryParse(part[1..], out var v))
                    switch (part[0]) {
                        case 'k': _v5K = v; break;
                        case 'd': _v5D = v; break;
                        case 'm': _v5M = v; break;
                        case 't': _v5T = v; break;
                    }
        }
        _radius = radius; _junimoCount = junimos; _run = run; _rnd = new Random(seed);
        _dayMode = dayMode; _obstacleRate = obstacleRate; _cropDensity = cropDensity;
        _hutCount = Cfg.Huts;
        _hutTiles = new int[_hutCount];
        _lkc = new int[_hutCount];
        _gateScanTick = new long[_hutCount];
        _cachedHasWork = new bool[_hutCount];
        _lastSpawnTick = new long[_hutCount];
        _allClaimedUntil = new long[_hutCount];
        _cropsInBox = new int[_hutCount];
        _hutExpansions = new long[_hutCount];
        for (var h = 0; h < _hutCount; h++) {
            _lkc[h] = -1;
            _gateScanTick[h] = long.MinValue / 2;
            _lastSpawnTick[h] = long.MinValue / 2;
            _allClaimedUntil[h] = long.MinValue / 2;
            var (cx, cy) = Cfg.HutCenters[_hutCount == 1 ? 0 : 1 + h];
            _hutTiles[h] = Idx(cx, cy);
        }
        _tickPops = new long[(dayMode ? Cfg.DAY_TICKS : Cfg.SIM_CAP_TICKS) + 1];
        _diag = dayMode && Cfg.Diag;
        if (dayMode && Cfg.TraceSpec != null && Cfg.TraceSpec.Split(',') is { Length: 3 } p) {
            _trace = double.TryParse(p[0], out var to) && to == obstacleRate
                  && int.TryParse(p[1], out var tj) && tj == junimos
                  && int.TryParse(p[2], out var tr) && tr == run;
        }
    }

    private void Trace(string s) { if (_trace) Console.WriteLine($"[{_tick,6}] {s}"); }

    private void AddPops(long pops) {
        _expansions += pops;
        if (_activeHut >= 0 && _activeHut < _hutExpansions.Length) _hutExpansions[_activeHut] += pops;
        if (_tick >= 0 && _tick < _tickPops.Length) _tickPops[_tick] += pops;
    }

    private static int Idx(int x, int y) => y * Cfg.W + x;
    private static int XOf(int idx) => idx % Cfg.W;
    private static int YOf(int idx) => idx / Cfg.W;
    private static int Man(int a, int b) => Math.Abs(XOf(a) - XOf(b)) + Math.Abs(YOf(a) - YOf(b));

    // record the clearing-phase thresholds (throughput mode only)
    private void RecordPhases(int remaining, int initial) {
        if (_dayMode || initial == 0) return;
        while (_phaseNext < 4) {
            var cleared = (double)(initial - remaining) / initial;
            var need = (_phaseNext + 1) * 0.25;
            if (cleared + 1e-9 < need) break;
            _phaseTicks[_phaseNext] = _tick;
            _phasePops[_phaseNext] = _expansions;
            _phaseNext++;
        }
    }

    public RunResult Run() {
        BuildMap();

        for (var i = 0; i < _junimoCount * _hutCount; i++)
            _junimos.Add(new Junimo {
                State = Junimo.St.WaitingRespawn,
                NextEventTick = 1 + i * Cfg.SPAWN_STAGGER_TICKS,
                Pos = _hutTiles[i % _hutCount],
                Retargets = _baseAlgo is "legacy" or "claims_v4b",
                Hut = i % _hutCount,
            });

        long tick = 0;
        var remaining = _crops.Count;
        var initial = remaining;
        _nextPokeTick = Cfg.HUT_POKE_TICKS;
        var lastEvent = new Dictionary<Junimo, long>();
        while (_dayMode ? tick < Cfg.DAY_TICKS : (tick < Cfg.SIM_CAP_TICKS && remaining > 0)) {
            // day mode: ripen the next wave of crops
            while (_waveIdx < _waves.Count && _waves[_waveIdx].tick <= tick) {
                if (_trace) Trace($"wave {_waveIdx}: crop at {_waves[_waveIdx].tile}");
                _crops.Add(_waves[_waveIdx].tile);
                _waveIdx++;
            }
            Junimo next = null;
            var nextTick = long.MaxValue;
            foreach (var j in _junimos) {
                if (j.NextEventTick < nextTick) { nextTick = j.NextEventTick; next = j; }
            }
            if (next == null) break;
            var eventTick = Math.Min(nextTick, _nextPokeTick);
            if (_dayMode && eventTick > Cfg.DAY_TICKS) eventTick = Cfg.DAY_TICKS;
            tick = Math.Max(tick, eventTick);
            _tick = tick;
            _activeHut = next.Hut;

            // audit: attribute elapsed time to the state the junimo was in
            if (_diag || _trace) {
                var since = tick - (lastEvent.TryGetValue(next, out var lt) ? lt : 0);
                if (since > 0) _stateTicks[(int)next.State] += since;
                lastEvent[next] = tick;
            }

            // the hut's performTenMinuteAction (+ the mod's postfix) re-pokes EVERY
            // junimo (walking ones included) every 10 game-minutes, TWICE
            if (_nextPokeTick <= nextTick) {
                _hutPokes++;
                Trace("hut poke");
                DoHutPoke(tick);
                _nextPokeTick += Cfg.HUT_POKE_TICKS;
                continue;
            }

            switch (next.State) {
                case Junimo.St.WaitingRespawn:
                    // JunimoSpawnHelper only (re)spawns a junimo when the hut's cached
                    // scan finds work, paces spawns one per ~60t, and re-checks every
                    // cache period otherwise (keeping lastKnownCropLocation fresh while
                    // a slot is open).
                    if (tick - _lastSpawnTick[next.Hut] >= Cfg.SPAWN_STAGGER_TICKS && ScanGate(next.Hut, counting: _baseAlgo == "v5")) {
                        _lastSpawnTick[next.Hut] = tick;
                        next.State = Junimo.St.WaitingDecision;
                        next.Pos = _hutTiles[next.Hut];
                        Trace($"j{_junimos.IndexOf(next)} spawn");
                        // a real spawn walks 1-2 tiles from the hut and its arrival runs
                        // tryToHarvestHere -> pokeToHarvest (70% gate; WorkRidiculouslyFast
                        // bypasses it — controller == null). The ctor walk itself stays
                        // unmodeled (delays every algo's first decision ~20-40t equally).
                        if (Cfg.WRF || _rnd.NextDouble() < Cfg.POKE_PROB) Decide(next, tick);
                        if (next.State == Junimo.St.WaitingDecision)
                            next.NextEventTick = tick + SampleIdleRandom();
                    } else {
                        next.NextEventTick = tick + Cfg.SCAN_COOLDOWN_TICKS;
                    }
                    break;


                case Junimo.St.WaitingDecision:
                    if (next.PendingTimerPoke) {
                        // the "nothing to do" harvestTimer expires -> one more
                        // pokeToHarvest (70% gate; WRF postfix bypasses it for a
                        // standing junimo). WorkFaster arms this timer at 5ms (~1t).
                        next.PendingTimerPoke = false;
                        _idleRandoms++; // (event accounting; the gate is applied below)
                        Trace($"j{_junimos.IndexOf(next)} timer-poke decide");
                        if (Cfg.WRF || _rnd.NextDouble() < Cfg.POKE_PROB) Decide(next, tick);
                    } else {
                        // spontaneous idle random: update() calls pathfindToNewCrop
                        // DIRECTLY (no 70% poke gate); the decision cap (patched
                        // algos) applies inside pathfindToNewCrop
                        _idleRandoms++;
                        Trace($"j{_junimos.IndexOf(next)} idle-random decide");
                        Decide(next, tick);
                    }
                    if (next.State == Junimo.St.WaitingDecision)
                        next.NextEventTick = tick + SampleIdleRandom();
                    break;

                case Junimo.St.Walking: {
                    if (next.PendingTimerPoke) {
                        // (F1) the "nothing to do" harvestTimer expires MID-WALK: the
                        // real game's update() calls pokeToHarvest() at harvestTimer <= 0
                        // regardless of controller state, and a passing gate REPLACES the
                        // walking controller. For capped algos the 20/40t cap set by the
                        // arrival decision blocks this retry; legacy_arrival re-decides.
                        next.PendingTimerPoke = false;
                        var progTp = (int)((tick - next.WalkStartTick) / Cfg.TICKS_PER_TILE);
                        next.Pos = progTp <= 0 ? next.Origin : next.Path[Math.Min(progTp, next.Path.Count) - 1];
                        Trace($"j{_junimos.IndexOf(next)} timer-poke mid-walk");
                        var oldPath = next.Path;
                        var oldStart = next.WalkStartTick;
                        if (Cfg.WRF || _rnd.NextDouble() < Cfg.POKE_PROB) Decide(next, tick);
                        if (next.State == Junimo.St.Walking) {
                            if (ReferenceEquals(next.Path, oldPath)) {
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
                        // arrived: tryToHarvestHere. No reservation — ANY junimo arriving
                        // while the crop still exists starts a (possibly doomed) 2000ms
                        // harvest; the crop is taken by the first 1000ms crossing.
                        var tile = next.Path != null && next.Path.Count > 0 ? next.Path[^1] : next.Pos;
                        next.Pos = tile;
                        if (_crops.Contains(tile)) {
                            // doomed arrival = the crop is still here but another junimo
                            // is already harvesting it; our crossing will find it gone.
                            if (_junimos.Exists(o => !ReferenceEquals(o, next) && o.State == Junimo.St.Working && o.TargetTile == tile))
                                _doomedArrivals++;
                            Trace($"j{_junimos.IndexOf(next)} ARRIVE crop {tile} -> work");
                            next.State = Junimo.St.Working;
                            next.WorkingPhase = 0;
                            next.NextEventTick = tick + Cfg.HARVEST_CROSSING_TICKS;
                        } else {
                            // dead tile: someone harvested it first (or the plan went stale)
                            _wastedArrivals++;
                            Trace($"j{_junimos.IndexOf(next)} ARRIVE dead {tile} (crops={_crops.Count})");
                            next.State = Junimo.St.WaitingDecision;
                            // PatchTryToHarvestHere releases the junimo's OWN claim on
                            // every "nothing to do" arrival (owner-scoped)
                            if (_baseAlgo != "legacy") ReleaseOwnClaim(next);
                            if (_baseAlgo == "claims_v2b") _workTiles.Remove(tile);
                            // pokeToHarvest() runs immediately: 70% gate; with
                            // WorkRidiculouslyFast the postfix re-decides anyway
                            // (controller == null after the walk finished).
                            if (Cfg.WRF || _rnd.NextDouble() < Cfg.POKE_PROB) Decide(next, tick);
                            // harvestTimer is armed REGARDLESS: 200ms vanilla, 5ms with
                            // WorkFaster (~1 tick). Not armed when the poke despawned
                            // the junimo (25% home roll): it is gone from myJunimos.
                            if (next.State is Junimo.St.WaitingDecision or Junimo.St.Walking) {
                                next.PendingTimerPoke = true;
                                next.NextEventTick = tick + (Cfg.WRF ? 1 : Cfg.TIMER_POKE_TICKS);
                            }
                        }
                        break;
                    }

                    // mid-walk re-planning: only the historical saturated-cadence models
                    if (next.Retargets && _rnd.NextDouble() < Cfg.POKE_PROB) {
                        var cur = progress == 0 ? next.Origin : next.Path[progress - 1];
                        next.Pos = cur;
                        Retarget(next, tick, cur);
                    }

                    if (next.State == Junimo.St.Walking) {
                        next.NextEventTick = tick + (next.Retargets ? 10 : Math.Max(1, (next.Path.Count - progress) * Cfg.TICKS_PER_TILE));
                    }
                    break;
                }

                case Junimo.St.Working: {
                    if (next.WorkingPhase == 0) {
                        // the 1000ms crossing: the crop goes to the first harvester whose
                        // crossing finds it; a doomed harvester takes nothing here
                        if (_crops.Remove(next.TargetTile)) {
                            remaining--;
                            // tryToAddItemToHut prefix releases the owner's claim
                            ReleaseOwnClaim(next);
                            if (_baseAlgo == "claims_v2b") _workTiles.Remove(next.TargetTile);
                            if (_hutCount == 2 && InBothBoxes(next.TargetTile)) _overlapHarvests++;
                        }
                        next.WorkingPhase = 1;
                        next.NextEventTick = tick + (Cfg.HARVEST_TICKS - Cfg.HARVEST_CROSSING_TICKS);
                        RecordPhases(remaining, initial);
                    } else {
                        // 2000ms reached -> harvestTimer <= 0 -> pokeToHarvest: 70% gate;
                        // WorkRidiculouslyFast's postfix bypasses it (controller == null)
                        next.State = Junimo.St.WaitingDecision; // the harvest is over; the junimo stands
                        if (Cfg.WRF || _rnd.NextDouble() < Cfg.POKE_PROB) Decide(next, tick);
                        if (next.State == Junimo.St.WaitingDecision)
                            next.NextEventTick = tick + SampleIdleRandom();
                    }
                    break;
                }
            }
        }

        if (remaining == 0) RecordPhases(0, initial); // safety net for the 100% threshold

        var peak = 0L;
        long sum = 0;
        for (var i = 0; i < _tickPops.Length; i++) {
            sum += _tickPops[i];
            if (i >= 60) sum -= _tickPops[i - 60];
            if (sum > peak) peak = sum;
        }

        return new RunResult(tick, _expansions, _wastedArrivals, _respawns, _scanOps, peak, _cropCount,
            _dayMode ? false : remaining > 0, _doomedArrivals, _phaseTicks, _phasePops,
            _hutExpansions[0], _hutCount > 1 ? _hutExpansions[1] : 0, _overlapHarvests,
            _shadowSearches, _throttleHits, _detourIgnoredCount, _filterOffDecisions);
    }

    private int HutTile() => _hutTiles[0];

    private bool InHutBox(int h, int tile) {
        var hx = XOf(_hutTiles[h]); var hy = YOf(_hutTiles[h]);
        return Math.Abs(XOf(tile) - (hx + 1)) <= _radius && Math.Abs(YOf(tile) - (hy + 1)) <= _radius;
    }

    private bool InBothBoxes(int tile) => InHutBox(0, tile) && (_hutCount < 2 || InHutBox(1, tile));

    // the real `outsideRadius` check (vanilla 3.2.0 and HEAD PatchPathfindDoWork):
    // an endpoint outside the junimo's OWN hut radius box is discarded and the
    // vanilla fallback chain runs instead. Never binds with one hut (all crops are
    // inside the box by construction); shapes cross-hut behavior with two huts.
    private bool OutsideBox(int h, int tile) {
        var hx = XOf(_hutTiles[h]); var hy = YOf(_hutTiles[h]);
        return Math.Abs(XOf(tile) - (hx + 1)) > _radius || Math.Abs(YOf(tile) - (hy + 1)) > _radius;
    }

    private void BuildMap() {
        var rng = new Random(_rnd.Next());
        for (var i = 0; i < Cfg.W * Cfg.H; i++)
            if (rng.NextDouble() < _obstacleRate) _blocked[i] = true;

        foreach (var hut in _hutTiles) {
            _blocked[hut] = true; _blocked[hut + 1] = true; _blocked[hut + Cfg.W] = true; _blocked[hut + Cfg.W + 1] = true;
        }

        // only place crops reachable from ANY hut, so every run is completable
        var reach = new bool[Cfg.W * Cfg.H];
        var queue = new Queue<int>();
        foreach (var hut in _hutTiles) { reach[hut] = true; queue.Enqueue(hut); }
        while (queue.Count > 0) {
            var cur = queue.Dequeue();
            foreach (var n in Neighbors(cur)) {
                if (reach[n] || _blocked[n]) continue;
                reach[n] = true;
                queue.Enqueue(n);
            }
        }

        var candidates = new List<int>();
        var seen = new HashSet<int>();
        for (var h = 0; h < _hutCount; h++) {
            var hut = _hutTiles[h];
            var hx = XOf(hut); var hy = YOf(hut);
            for (var x = hx + 1 - _radius; x <= hx + _radius; x++)
                for (var y = hy + 1 - _radius; y <= hy + _radius; y++) {
                    if (x < 0 || y < 0 || x >= Cfg.W || y >= Cfg.H) continue;
                    var idx = Idx(x, y);
                    if (seen.Add(idx) && !_blocked[idx] && reach[idx]) candidates.Add(idx);
                }
        }
        for (var i = candidates.Count - 1; i > 0; i--) {
            var k = rng.Next(i + 1);
            (candidates[i], candidates[k]) = (candidates[k], candidates[i]);
        }
        var target = (int)Math.Round(candidates.Count * _cropDensity);
        _cropCount = target;
        if (_dayMode) {
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
        // same order as PathFindController.Directions: left, right, down, up
        if (x > 0) yield return idx - 1;
        if (x < Cfg.W - 1) yield return idx + 1;
        if (y < Cfg.H - 1) yield return idx + Cfg.W;
        if (y > 0) yield return idx - Cfg.W;
    }

    /* ------------------------------ A* models ------------------------------ */

    // "nearest actionable tile" like foundCropEndFunction — real
    // PathFindController.findPath semantics (pfc.cs:180-246):
    //  - the ctor replaces endPoint == Point.Zero with the character's own tile,
    //    so the queue priority is g + manhattan(node, START tile);
    //  - children are added to the closed list when ENQUEUED (every node enters
    //    the queue at most once, first path wins, possibly non-shortest);
    //  - the endFunction is checked on every pop INCLUDING the start node;
    //  - the budget counts pops and a search that expands `limit` nodes fails.
    // With skipClaimed (claim filter on), tiles claimed by ANOTHER junimo fail the
    // end check and the search CONTINUES (PatchFindingCropEnd semantics); the first
    // claimed crop popped is remembered so a failed search can still shadow it.
    // detourLimit (v5 fix 1b, > 0): when the first unclaimed crop is more than
    // detourLimit tiles farther (reconstructed path length) than the nearest crop
    // of ANY kind popped by this search, return the nearest crop instead and set
    // _detourIgnored — this decision ignores claims (vanilla greedy).
    private List<int> AStarNearest(int start, int limit, Junimo self = null, bool skipClaimed = false, int detourLimit = 0) {
        var open = new PriorityQueue<int, (long f, long seq)>();
        long seq = 0;
        var gScore = new Dictionary<int, long> { [start] = 0 };
        var cameFrom = new Dictionary<int, int>();
        var closed = new HashSet<int> { start };
        open.Enqueue(start, (0, seq++));
        _shadowPath = null;
        _firstCropPath = null;
        _detourIgnored = false;
        _lastSearchShadow = false;
        _nearestSearches++;

        long pops = 0;
        while (open.Count > 0) {
            var cur = open.Dequeue();
            pops++;
            var claimedByOther = self != null && IsClaimedByOther(cur, self);
            if (_crops.Contains(cur)) {
                if (claimedByOther) {
                    if (_firstCropPath == null) _firstCropPath = Reconstruct(cameFrom, cur);
                    if (!skipClaimed) {
                        AddPops(pops);
                        _lastSearchShadow = true;
                        return Reconstruct(cameFrom, cur);
                    }
                    if (_shadowPath == null) _shadowPath = Reconstruct(cameFrom, cur);
                } else {
                    var targetPath = Reconstruct(cameFrom, cur);
                    if (skipClaimed && detourLimit > 0 && _firstCropPath != null &&
                        targetPath.Count - _firstCropPath.Count > detourLimit) {
                        AddPops(pops);
                        _detourIgnored = true;
                        return _firstCropPath;
                    }
                    AddPops(pops);
                    return targetPath;
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

    // concrete-target A* with Manhattan heuristic (PathFindController's Point ctor)
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

    // the hut work scan, cached for 60t like PatchSearchAroundHut (mod 3.2.0+):
    // called from the spawn helper (open slot) and, on the patched algos, from the
    // decision path as the scan gate. Updates the hut's lkc like SearchHutGrid does.
    // counting (v5 only): scan the WHOLE box without early exit and store the
    // actionable count — the real-DLL implementation of v5's fixes 1/2 extends
    // SearchAroundHut the same way (a few hundred cheap grid checks per 60t cache
    // miss, versus 100 A* node expansions per decision).
    private bool ScanGate(int h, bool counting) {
        if (_tick - _gateScanTick[h] >= Cfg.SCAN_COOLDOWN_TICKS) {
            // counting scans early-exit at max(k, m)*jn + 1: the conditions only need
            // to know whether boxCrops <= k*jn (resp. m*jn), so a crop-rich box stops
            // after a few dozen tiles and a crop-poor box scans the whole radius once —
            // the SAME worst case the existing gate scan already pays on an empty box.
            var cap = 1;
            if (counting) {
                var thr = Math.Max(_v5K, _v5M) * _junimoCount;
                cap = thr > 0 ? thr + 1 : 1;
            }
            ScanWorkTiles(h, cap);
            _cachedHasWork[h] = _workTiles.Count > 0;
            if (counting && cap > 1) _cropsInBox[h] = _workTiles.Count;
            _gateScanTick[h] = _tick;
        }
        return _cachedHasWork[h];
    }

    // row-major scan of the hut's radius box, early exit at `cap` actionable tiles;
    // legacy only needs the first tile (lkc), v5's counting scan counts them all
    private void ScanWorkTiles(int h, int cap) {
        _workTiles.Clear();
        var hut = _hutTiles[h];
        var hx = XOf(hut); var hy = YOf(hut);
        for (var x = hx + 1 - _radius; x <= hx + _radius; x++) {
            for (var y = hy + 1 - _radius; y <= hy + _radius; y++) {
                if (x < 0 || y < 0 || x >= Cfg.W || y >= Cfg.H) continue;
                _scanOps++;
                if (_crops.Contains(Idx(x, y))) {
                    _workTiles.Add(Idx(x, y));
                    if (_workTiles.Count >= cap) goto done;
                }
            }
        }
    done:
        _lastScanTick = _tick;
        if (_workTiles.Count > 0) _lkc[h] = _workTiles[0];
        else _lkc[h] = -1; // vanilla/patched scans reset lastKnownCropLocation to Zero when nothing is found
    }

    // CropClaims.ReleaseOwner: owner-scoped
    private void ReleaseOwnClaim(Junimo j) {
        if (!_ownerClaim.Remove(j, out var tile)) return;
        if (_claims.TryGetValue(tile, out var owner) && ReferenceEquals(owner, j)) { _claims.Remove(tile); _claimTick.Remove(tile); }
    }

    // CropClaims.IsClaimedByOther, including the 1800t expiry: an expired claim is
    // dropped on read (DropClaim) and the tile counts as unclaimed.
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

    // CropClaims.TryClaim: refresh/steal semantics, one claim per junimo
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

    /* ------------------------------ decisions ------------------------------ */

    // the shipped patch's decision-capped algos (+ the v5 family built on them)
    private bool IsCappedAlgo => _baseAlgo is "cap_only" or "v4b_gate_backoff" or "v4b_realfilter" or "v4b_shipped" or "v4b_gate" or "v5";

    private bool CapActive(Junimo j, long tick) =>
        IsCappedAlgo && _backoff.TryGetValue(j, out var until) && tick < until;

    private void Decide(Junimo j, long tick) {
        _decisions++;
        _activeHut = j.Hut;
        // decision rate cap (PatchPathfindDoWork): checked BEFORE everything — a
        // capped junimo keeps its current path / stands still, and does not even
        // roll the 3.5% stroll. >=20t after success, >=40t after failure.
        if (CapActive(j, tick)) {
            _gatedOut++;
            return;
        }

        // 3.5% random stroll — WorkRidiculouslyFast removes it
        // (faa40d4 JunimoHarvesterPatches.cs:254)
        if (!Cfg.WRF && _rnd.NextDouble() < Cfg.WANDER_PROB) {
            Wander(j, tick);
            // the real code sets NextDecisionTick on the stroll too
            if (IsCappedAlgo) _backoff[j] = tick + Cfg.SUCCESS_CAP_TICKS;
            return;
        }

        switch (_baseAlgo) {
            case "legacy": DecideLegacy(j, tick); break;
            case "claims_v2b": DecideClaims(j, tick, v2: true); break;
            case "claims_v3": DecideV3(j, tick); break;
            case "claims_v4b": DecideV4b(j, tick, gate: false, backoff: false); break;
            case "v4b_shipped": DecideV4b(j, tick, gate: false, backoff: false); break;
            case "v4b_gate": DecideV4b(j, tick, gate: true, backoff: false); break;
            case "v4b_gate_backoff": DecideV4b(j, tick, gate: true, backoff: true); break;
            case "cap_only": DecideCapOnly(j, tick); break;
            case "legacy_arrival": DecideLegacy(j, tick); break;
            case "v4b_realfilter": DecideV4bRealFilter(j, tick); break;
            case "v5": DecideV5(j, tick); break;
        }
    }

    private void DecideLegacy(Junimo j, long tick) {
        // in the real mod (3.2.0 and patched alike) the hut scan runs ONLY from
        // JunimoSpawnHelper while a spawn slot is open (60t cache); with the hut
        // full, lastKnownCropLocation is frozen. The scan also updates lkc.
        if (AnyOpenSlot) ScanGate(j.Hut, counting: false);

        var path = AStarNearest(j.Pos, Cfg.EXP_LIMIT_LEGACY);
        if (path != null && !OutsideBox(j.Hut, path.Count > 0 ? path[^1] : j.Pos)) { StartWalk(j, path, tick); return; }

        // the lkc fallback does not validate the crop and does not re-roll
        // home/wander when the lkc PATH fails (junimo.cs:386-398)
        var roll = _rnd.NextDouble();
        if (roll < 0.5 && _lkc[j.Hut] >= 0) {
            var p = AStarTo(j.Pos, _lkc[j.Hut], Cfg.EXP_LIMIT_LEGACY);
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

    private void DecideClaims(Junimo j, long tick, bool v2) {
        if (v2 && tick - _lastScanTick >= Cfg.SCAN_COOLDOWN_TICKS) ScanWorkTiles(j.Hut, Cfg.MAX_WORK_TILES);

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
            j.NextEventTick = tick + 2;
            return;
        }

        var tile = best.Value;
        _claims[tile] = j;
        _ownerClaim[j] = tile;
        var p2 = AStarTo(j.Pos, tile, v2 ? Cfg.EXP_LIMIT_CLAIMS : Cfg.EXP_LIMIT_LEGACY);
        if (p2 != null) { StartWalk(j, p2, tick); return; }

        if (_claims.TryGetValue(tile, out var o) && ReferenceEquals(o, j)) _claims.Remove(tile);
        _ownerClaim.Remove(j);
        if (v2) _workTiles.Remove(tile);
        j.State = Junimo.St.WaitingDecision;
        j.NextEventTick = tick + 2;
    }

    // claims_v4b (historical): legacy logic + same-pass shadow. Not in the grid.
    private void DecideV4b(Junimo j, long tick, bool gate, bool backoff) {
        if (gate) {
            if (!ScanGate(j.Hut, counting: false)) { VanillaTail(j, tick, allowLkc: false); return; }
        } else if (tick - _lastScanTick >= Cfg.SCAN_COOLDOWN_TICKS) {
            ScanWorkTiles(j.Hut, cap: 1);
        }

        var path = AStarNearest(j.Pos, Cfg.EXP_LIMIT_LEGACY, self: j, skipClaimed: true);
        if (path == null && _lastSearchShadow && _shadowPath != null) path = _shadowPath;
        if (path != null && OutsideBox(j.Hut, path.Count > 0 ? path[^1] : j.Pos)) path = null;
        if (path != null) {
            _backoff[j] = tick + Cfg.SUCCESS_CAP_TICKS;
            if (_lastSearchShadow) {
                StartWalk(j, path, tick);
                return;
            }
            TryClaim(j, path.Count > 0 ? path[^1] : j.Pos);
            StartWalk(j, path, tick);
            return;
        }

        ReleaseOwnClaim(j);
        if (backoff) _backoff[j] = tick + Cfg.BACKOFF_TICKS;
        VanillaFallbackTail(j, tick, tryLkc: true);
    }

    // v4b_realfilter: the SHIPPED patched build — cap + gate + the real claim
    // filter: the filtered end check skips tiles claimed by another junimo and
    // keeps searching; only when no unclaimed actionable tile exists in the whole
    // budget does the mod re-run the search unfiltered (a second full A*) and
    // shadow the nearest claimed tile (HEAD PatchPathfindDoWork:286-296).
    private void DecideV4bRealFilter(Junimo j, long tick) {
        if (!ScanGate(j.Hut, counting: false)) { VanillaTail(j, tick, allowLkc: false); return; }

        var path = AStarNearest(j.Pos, Cfg.EXP_LIMIT_LEGACY, self: j, skipClaimed: true);
        var shadow = false;
        if (path == null) {
            // crowded: unfiltered shadow re-search, exactly like PatchPathfindDoWork
            _shadowSearches++;
            shadow = true;
            path = AStarNearest(j.Pos, Cfg.EXP_LIMIT_LEGACY, self: null);
        }
        if (path != null && OutsideBox(j.Hut, path.Count > 0 ? path[^1] : j.Pos)) path = null;
        if (path != null) {
            _backoff[j] = tick + Cfg.SUCCESS_CAP_TICKS;
            if (shadow) {
                // shadowing a claimed tile: TryClaim is skipped (tile claimed by
                // other) and no ReleaseOwner runs on this path
                StartWalk(j, path, tick);
                return;
            }
            TryClaim(j, path.Count > 0 ? path[^1] : j.Pos);
            StartWalk(j, path, tick);
            return;
        }

        // search failed: free any stale claim, back off before the next attempt
        ReleaseOwnClaim(j);
        _backoff[j] = tick + Cfg.BACKOFF_TICKS;
        VanillaFallbackTail(j, tick, tryLkc: true);
    }

    // v5 = v4b_realfilter + three parameterized fixes (see header + DESIGN.md):
    //   fix 1  conditional dedup: the claim filter is disabled whenever the hut's
    //          cached box scan counts <= K * junimosPerHut actionable tiles (k);
    //          and/or per decision, when the nearest unclaimed crop is > D tiles
    //          farther than the nearest crop of any kind (d, detour limit);
    //   fix 2  endgame fallback: the filter is disabled when boxCrops <=
    //          M * junimosPerHut (m) — the "restore vanilla endgame sweeping";
    //   fix 3  crowding-verdict throttle: a failed filtered search caches "whole
    //          budget claimed" per hut for T ticks; while fresh, later decisions
    //          run ONLY the unfiltered shadow search (same target, half the A*).
    private void DecideV5(Junimo j, long tick) {
        var h = j.Hut;
        if (!ScanGate(h, counting: true)) { VanillaTail(j, tick, allowLkc: false); return; }

        // ---- fixes 1 + 2: conditional filter ----
        var filterOn = true;
        if (_v5K > 0 && _cropsInBox[h] <= _v5K * _junimoCount) filterOn = false;
        if (filterOn && _v5M > 0 && _cropsInBox[h] <= _v5M * _junimoCount) filterOn = false;
        if (!filterOn) _filterOffDecisions++;

        List<int> path = null;
        var shadow = false;
        if (filterOn) {
            // ---- fix 3: crowding-verdict throttle ----
            if (_v5T > 0 && tick < _allClaimedUntil[h]) {
                // a recent filtered search of this hut proved that the whole budget
                // finds only claimed tiles: run ONLY the unfiltered search (the same
                // target the shadow re-search would produce) — half the A* cost.
                _throttleHits++;
                path = AStarNearest(j.Pos, Cfg.EXP_LIMIT_LEGACY, self: null);
                shadow = true;
                if (path != null) {
                    // stale-verdict self-correction: if the endpoint is actually free
                    // (a claim expired/was released within the T window), behave like
                    // the normal filtered path would (claim it)
                    var end0 = path.Count > 0 ? path[^1] : j.Pos;
                    if (!IsClaimedByOther(end0, j)) shadow = false;
                }
            } else {
                path = AStarNearest(j.Pos, Cfg.EXP_LIMIT_LEGACY, self: j, skipClaimed: true, detourLimit: _v5D);
                if (path == null) {
                    // crowded: cache the verdict (no-op when T=0) and run the
                    // unfiltered shadow re-search, exactly like PatchPathfindDoWork
                    _allClaimedUntil[h] = tick + _v5T;
                    _shadowSearches++;
                    shadow = true;
                    path = AStarNearest(j.Pos, Cfg.EXP_LIMIT_LEGACY, self: null);
                } else {
                    // a successful filtered search (straight or detour-limited) proves
                    // the hut is NOT fully claimed: invalidate any cached verdict
                    _allClaimedUntil[h] = long.MinValue / 2;
                    if (_detourIgnored) {
                        // fix 1b: nearest unclaimed crop too far -> this decision ignores
                        // claims and takes the nearest crop (vanilla greedy, no claim)
                        _detourIgnoredCount++;
                        shadow = true;
                    }
                }
            }
        } else {
            path = AStarNearest(j.Pos, Cfg.EXP_LIMIT_LEGACY, self: null);
        }

        if (path != null && OutsideBox(h, path.Count > 0 ? path[^1] : j.Pos)) path = null;
        if (path != null) {
            _backoff[j] = tick + Cfg.SUCCESS_CAP_TICKS;
            if (shadow) {
                StartWalk(j, path, tick);
                return;
            }
            TryClaim(j, path.Count > 0 ? path[^1] : j.Pos);
            StartWalk(j, path, tick);
            return;
        }

        // search failed: free any stale claim, back off before the next attempt
        ReleaseOwnClaim(j);
        _backoff[j] = tick + Cfg.BACKOFF_TICKS;
        VanillaFallbackTail(j, tick, tryLkc: true);
    }

    // cap_only: the decision rate cap + scan gate WITHOUT the claim filter
    private void DecideCapOnly(Junimo j, long tick) {
        if (!ScanGate(j.Hut, counting: false)) { VanillaTail(j, tick, allowLkc: false); return; }

        var path = AStarNearest(j.Pos, Cfg.EXP_LIMIT_LEGACY);
        if (path != null && OutsideBox(j.Hut, path.Count > 0 ? path[^1] : j.Pos)) path = null;
        if (path != null) {
            _backoff[j] = tick + Cfg.SUCCESS_CAP_TICKS;
            StartWalk(j, path, tick);
            return;
        }

        _backoff[j] = tick + Cfg.BACKOFF_TICKS;
        VanillaFallbackTail(j, tick, tryLkc: true);
    }

    // the vanilla fallback tail (junimo.cs:386-398): 50% to the last known work
    // tile (when lkc != Zero; a null lkc path leaves the junimo STANDING), else
    // 25% home, else wander
    private void VanillaFallbackTail(Junimo j, long tick, bool tryLkc) {
        if (tryLkc && _rnd.NextDouble() < 0.5 && _lkc[j.Hut] >= 0) {
            var p = AStarTo(j.Pos, _lkc[j.Hut], Cfg.EXP_LIMIT_LEGACY);
            if (p != null) { StartWalk(j, p, tick); return; }
            return; // lkc controller with a null path: the junimo stands
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
        if (allowLkc && _rnd.NextDouble() < 0.5 && _lkc[j.Hut] >= 0) {
            var p = AStarTo(j.Pos, _lkc[j.Hut], Cfg.EXP_LIMIT_LEGACY);
            if (p != null) { StartWalk(j, p, tick); return; }
            return; // lkc controller with a null path: the junimo stands
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

    // JunimoHut.performTenMinuteAction + the mod's ReplaceJunimoTimerNumber
    // postfix (JunimoHutPatches.cs:265-302): EVERY junimo of the hut is poked
    // TWICE per 10 game-minutes, by two independent pokeToHarvest calls, each
    // with its own 70% gate and its own decision-cap check. Walking junimos are
    // included (a passing gate REPLACES the walking controller — vanilla
    // pathfindToNewCrop and the PatchPathfindDoWork work branch have no
    // controller guard); harvesting junimos fail the gate (harvestTimer > 0) and
    // are re-decided only through WorkRidiculouslyFast's PatchPokeToHarvest
    // postfix (destroy false, controller null) — which ABORTS a phase-0 harvest
    // (the junimo walks off, the crop stays) or cuts a phase-1 empty stand
    // short; capped algos whose cap is still active keep harvesting.
    private void DoHutPoke(long tick) {
        for (var poke = 0; poke < (Cfg.DOUBLE_POKE ? 2 : 1); poke++) {
            foreach (var j in _junimos) {
                if (j.State == Junimo.St.WaitingRespawn) continue;
                if (_baseAlgo == "legacy") continue; // saturated cadence model; a 430t poke changes nothing
                _activeHut = j.Hut;
                if (j.State == Junimo.St.Working) {
                    if (!Cfg.WRF) continue; // gate cannot pass while harvestTimer > 0; no postfix without WRF
                    if (CapActive(j, tick)) continue; // capped call dropped: controller stays null, harvest continues
                    var phase = j.WorkingPhase;
                    var eventTick = j.NextEventTick;
                    Decide(j, tick);
                    if (j.State == Junimo.St.Working) {
                        // the decision kept the harvest (e.g. standing lkc path):
                        // restore the untouched harvest schedule
                        j.WorkingPhase = phase;
                        j.NextEventTick = eventTick;
                    } else if (j.State == Junimo.St.WaitingDecision) {
                        j.NextEventTick = tick + SampleIdleRandom();
                    }
                    continue;
                }
                // 70% gate; WorkRidiculouslyFast's postfix bypasses it for a STANDING
                // junimo (controller == null). A walking junimo keeps the gate (the
                // postfix returns because controller != null).
                var pass = (Cfg.WRF && j.State == Junimo.St.WaitingDecision) || _rnd.NextDouble() < Cfg.POKE_PROB;
                if (!pass) continue;
                if (j.State == Junimo.St.Walking) {
                    if (CapActive(j, tick)) continue; // cap: keep walking
                    var progress = (int)((tick - j.WalkStartTick) / Cfg.TICKS_PER_TILE);
                    j.Pos = progress <= 0 ? j.Origin : j.Path[Math.Min(progress, j.Path.Count) - 1];
                    Decide(j, tick);
                    if (j.State == Junimo.St.WaitingDecision)
                        j.NextEventTick = tick + SampleIdleRandom(); // memoryless re-arm of the idle-random clock
                } else { // WaitingDecision: idle, standing
                    if (CapActive(j, tick)) continue; // cap: keep standing
                    Decide(j, tick);
                    if (j.State == Junimo.St.WaitingDecision)
                        j.NextEventTick = tick + SampleIdleRandom();
                }
            }
        }
    }

    // spontaneous idle decision (JunimoHarvester.update): a standing junimo rolls
    // 0.2%/frame with 1/6 -> pathfindToNewCrop DIRECTLY (no 70% gate) = one attempt
    // per ~3000 frames while standing idle
    private long SampleIdleRandom() {
        var u = _rnd.NextDouble();
        if (u <= 0.0) u = 1e-12;
        return Math.Max(1, (long)(-Cfg.IDLE_RANDOM_MEAN_TICKS * Math.Log(u)));
    }

    // claims_v3 (historical): vanilla nearest-search, but the end check skips
    // tiles claimed by another junimo. Not in the grid.
    private void DecideV3(Junimo j, long tick) {
        var path = AStarNearest(j.Pos, Cfg.EXP_LIMIT_LEGACY, self: j);
        if (path != null && !OutsideBox(j.Hut, path.Count > 0 ? path[^1] : j.Pos)) {
            ReleaseOwnClaim(j);
            var end = path.Count > 0 ? path[^1] : j.Pos;
            _claims[end] = j;
            _ownerClaim[j] = end;
            StartWalk(j, path, tick);
            return;
        }

        if (_rnd.NextDouble() < 0.25) {
            _respawns++;
            j.State = Junimo.St.WaitingRespawn;
            j.NextEventTick = tick + Cfg.SPAWN_STAGGER_TICKS;
        } else {
            Wander(j, tick);
        }
    }

    private void Wander(Junimo j, long tick) {
        // PatchPathfindToRandomSpotAroundHut: each retry is a full-budget A*; BJ 3.2.0
        // retried unreachable endpoints 6 times (retry <= 5), the shipped build caps
        // it at 2 — the cap IS part of the patch being measured.
        var attempts = _baseAlgo is "legacy" or "legacy_arrival"
            ? Cfg.WANDER_ATTEMPTS_LEGACY
            : Cfg.WANDER_ATTEMPTS_PATCHED;
        var hut = _hutTiles[j.Hut];
        var hx = XOf(hut); var hy = YOf(hut);
        for (var attempt = 0; attempt < attempts; attempt++) {
            var rx = hx + _rnd.Next(-_radius, _radius + 1);
            var ry = hy + _rnd.Next(-_radius, _radius + 1);
            if (rx < 0 || ry < 0 || rx >= Cfg.W || ry >= Cfg.H) continue;
            var p = AStarTo(j.Pos, Idx(rx, ry), Cfg.EXP_LIMIT_LEGACY);
            if (p != null) { StartWalk(j, p, tick); return; }
        }
        j.State = Junimo.St.WaitingDecision;
    }

    private void StartWalk(Junimo j, List<int> path, long tick) {
        // j.Pos must be the junimo's CURRENT tile here. An empty path (target is the
        // tile we're standing on) resolves instantly into a re-decision.
        j.Path = path;
        j.Origin = j.Pos;
        j.WalkStartTick = tick;
        j.TargetTile = path.Count > 0 ? path[^1] : j.Pos;
        j.State = Junimo.St.Walking;
        j.NextEventTick = tick + (j.Retargets ? 10 : Math.Max(1, path.Count * Cfg.TICKS_PER_TILE));
    }

    // vanilla re-plans mid-walk (~70%/frame in the real game; approximated at 10t
    // intervals here) — historical saturated-cadence models only.
    private void Retarget(Junimo j, long tick, int fromTile) {
        switch (_baseAlgo) {
            case "legacy": {
                var path = AStarNearest(fromTile, Cfg.EXP_LIMIT_LEGACY);
                if (path != null) { StartWalk(j, path, tick); return; }

                var roll = _rnd.NextDouble();
                if (roll < 0.5 && _lkc[j.Hut] >= 0 && _crops.Contains(_lkc[j.Hut])) {
                    var p = AStarTo(fromTile, _lkc[j.Hut], Cfg.EXP_LIMIT_LEGACY);
                    if (p != null) { StartWalk(j, p, tick); return; }
                    j.State = Junimo.St.WaitingDecision;
                    j.NextEventTick = tick + 2;
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
                    var end = path.Count > 0 ? path[^1] : j.Pos;
                    _claims[end] = j;
                    _ownerClaim[j] = end;
                    StartWalk(j, path, tick);
                    return;
                }

                ReleaseOwnClaim(j);
                var roll2 = _rnd.NextDouble();
                if (roll2 < 0.5 && _lkc[j.Hut] >= 0 && _crops.Contains(_lkc[j.Hut])) {
                    var p2 = AStarTo(fromTile, _lkc[j.Hut], Cfg.EXP_LIMIT_LEGACY);
                    if (p2 != null) { StartWalk(j, p2, tick); return; }
                    j.State = Junimo.St.WaitingDecision;
                    j.NextEventTick = tick + 2;
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
