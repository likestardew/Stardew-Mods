# Junimo work-finding simulators — five generations

Offline console simulators (net8.0, no game/mod dependencies) used during the Better Junimos
performance investigation to compare work-finding algorithms: vanilla decision logic, the
reactive "claims" patch, and scheduled work assignment. They model the decompiled 1.6.15
decision cadences, a 4-directional A* `PathFindController` with expansion budgets, hut scans
with their 60t cache, and paired seeds (every algorithm plays the same map within a run index).
A* node expansions ("pops") are the CPU proxy on the single-threaded game loop.

**Read this first:** these simulators were wrong twice, in two different ways (generation 1's
decision cadence, generation 2's phantom decrement + statistics). Their conclusions are
**directional only** — see the honesty notes in [INVESTIGATION.md](../INVESTIGATION.md).
The authoritative data is the real-game measurement at the end of that document
(single user, small n). Each generation documents its own replacement below.

## Generations

| # | Directory | What it was | Headline conclusion | Why superseded |
|---|---|---|---|---|
| 1 | [`1-initial-wrong-cadence/`](generations/1-initial-wrong-cadence/) | First simulator; idle junimos re-deciding every ~2 ticks ("42 A*/s") | "Patch CPU = 5-16% of vanilla" (quoted in #114); also falsified claims_v1/v2b/v3 designs | Cadence model wrong ~10x on vanilla cost; #114 retracted. Historical only |
| 2 | [`2-corrected-event-driven/`](generations/2-corrected-event-driven/) | Event-driven cadence rebuild; the code originally pushed to `perf/junimo-pathfinding` | "Claims patch +27-53% slower clear, 0.62-0.89x CPU" — drove the retraction rationale | Two later-found defects: phantom `remaining--` (baseline ~30% of work never simulated; 649s -> 1689s corrected) and per-run-ratio averaging that flipped CPU signs. Audit: [AUDIT-REPORT.md](generations/2-corrected-event-driven/AUDIT-REPORT.md) |
| 3 | [`3-independent-rebuild/`](generations/3-independent-rebuild/) | From-scratch cross-validation; found double poke, doomed standing, cadence lower-bound violations | Claims patch in-sim: -4~-25% clear, -20~-37% CPU, -37~-44% peak | Cross-validation served; reactive algorithms gave way to scheduled assignment |
| 4 | [`4-v5-targeted/`](generations/4-v5-targeted/) | Audited base + double poke + WF/WRF semantics + phase stats + two-hut scene; validates the v5 fixes (crowding-verdict cache T=40, scarce-work filter k=1) | Peaks fixed (-7~-35% vs claims patch, ~unfiltered level); daily CPU -33~-44%; **endgame degradation NOT cured** | Endgame failure chain is structural to reactive sensing; led to assignment design. Never shipped |
| 5 | [`5-assignment-v1/`](generations/5-assignment-v1/) | Assignment v1: periodic staggered full scans + regret global assignment + nearest-neighbor routes + self-refill; includes the scan-cost boundedness proof | Dominant in-sim: -43~-67% vs vanilla, -4~-27% vs claims patch, CPU 3-4% of vanilla, clumping structurally zero, endgame flat (1.65-1.9x) | v1's real-game flaws: idle standing junimos and spatially jumpy regret routes. Replaced by the two-mode design shipped in `BetterJunimos/Assign/WorkAssigner.cs` |

Each generation directory contains its `Program.cs`, project file, `aggregate.py` where present,
its original report/design docs, and a short English `README.md` (what it is / conclusions /
why superseded). The original reports for generations 3-5 are in Chinese.

## Run

Each generation is standalone:

```
cd simulation/generations/<gen>
dotnet run -c Release        # env vars JUNIMOSIM_RUNS / JUNIMOSIM_CPU_RUNS control run counts
python aggregate.py          # where present; writes summary.md
```

Defaults are small; the published runs used n=30-100 per throughput cell and n=30-50 per CPU cell
(see each generation's report for its exact grid and env vars). Expect seconds, not minutes.

## Context

- Investigation history, final algorithm, and real-game results: [INVESTIGATION.md](../INVESTIGATION.md)
- Shipped implementation: [`BetterJunimos/Assign/WorkAssigner.cs`](../BetterJunimos/Assign/WorkAssigner.cs)
- Superseded issue: [hawkfalcon/Stardew-Mods#114](https://github.com/hawkfalcon/Stardew-Mods/issues/114)
  (withdrawn; its numbers came from generation 1)
