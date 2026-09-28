# Better Junimos work assignment — the full investigation

This branch carries a **work-assignment scheduler** for the junimos of
[Better Junimos](BetterJunimos/README.md) (3.2.0), plus every simulator generation built along
the way and an honest record of what worked, what didn't, and which of our own conclusions were
wrong. It supersedes the withdrawn
[hawkfalcon/Stardew-Mods#114](https://github.com/hawkfalcon/Stardew-Mods/issues/114) — that
issue's numbers came from a simulator with a wrong decision-cadence model; this branch's final
algorithm is validated by real-game measurement, not simulation.

**Epistemics up front.** The offline simulators were wrong twice and misled us twice. Everything
simulated below should be read as *directional evidence only*. The numbers we stand behind are
the real-game measurements in [§9](#9-real-game-results-of-the-final-build-single-user-small-n) —
and those come from one user's machine and farm, with 1-2 runs per configuration. Treat this as
"works well in one heavily-modded environment", not as a benchmark.

---

## 1. Background

Better Junimos 3.2.0 on a heavily modded Stardew Valley 1.6.15 single-player farm (~200 mods),
hut radius 14, up to 6 junimos per hut, fields of 576-1152 crops. The symptom that started this:
frame stutter with several active huts, and junimos that clump onto the same crop.

The junimo decision loop (decompiled 1.6.15) is **event-driven**: decisions happen on path
arrival, on harvest-timer expiry (`pokeToHarvest`, 70% gate), on the hut's 10-minute pokes —
which the mod *doubles* via a postfix (`ReplaceJunimoTimerNumber` re-pokes everyone) — and on a
rare idle random action (p=0.002/frame). Roughly 0.3-1 decisions per second per junimo, each
potentially a full A* search with budget 100 (the "nearest actionable tile" end-function variant
is effectively Dijkstra, so budget 100 reaches only ~5-6 tiles). The mod widens wander to
1 + up to 6 searches. Junimos all resolve "nearest" to the same answer, hence the clumping.

## 2. Phase 1 — wrong cadence, issue #114, retraction

The first simulator ([`simulation/generations/1-initial-wrong-cadence/`](simulation/generations/1-initial-wrong-cadence/README.md))
modeled idle junimos as re-deciding every ~2 ticks — "≈42 A* searches/second/junimo" — and
produced the claims in #114: a decision rate cap, a scan gate, and a claim-aware endpoint filter,
"CPU = 5-16% of vanilla". The mod side of that branch survives in three small fixes that are
still in this branch today (see [§11](#11-what-else-is-in-this-branch)).

Rereading the decompile exposed the cadence error (event-driven, ~10x cheaper than modeled).
**#114 was withdrawn**: title changed, comments removed, closed. Its numbers are void. The three
changes themselves (cap / gate / claims) stayed installed and remained the fallback behavior in
this branch — but their *throughput* story was about to get worse before it got better.

This phase also produced the first falsified patch designs (kept as warnings):
claims_v1 (frozen work list → deadlock), claims_v2b (allocator-style claiming → scan explosion on
dense fields), claims_v3 (pure endpoint filter without fallback → degradation at large radius).

## 3. Phase 2 — corrected cadence, and two new bugs in the corrected simulator

[`simulation/generations/2-corrected-event-driven/`](simulation/generations/2-corrected-event-driven/README.md)
rebuilt the simulator on the real event-driven cadence with the claims filter's true semantics
(skip-and-continue + unfiltered shadow re-search when crowded) and paired seeds. Its published
numbers — claims patch **+27-53% slower** to clear fields, 0.62-0.89x steady-state CPU, ~10x
fewer wasted arrivals — drove the retraction rationale ("claims make junimos take detours;
vanilla's clump-train is an efficient work-concentration strategy").

**That rationale was itself an artifact.** A later audit (report preserved verbatim as
[`AUDIT-REPORT.md`](simulation/generations/2-corrected-event-driven/AUDIT-REPORT.md)) found:

1. **Phantom `remaining--`** — after a harvest the state was not reset, so no-op working events
   decremented the remaining-crop counter again and runs exited early on never-finished maps.
   Roughly **30% of the baseline's clearing work was never simulated**. Anchor: vanilla
   d=1.00/jn=2 clear time 649s (buggy) → **1689s** (fixed, n=100). The buggy figure violated the
   simulator's own lower bound (742 crops × 120t harvest ÷ 2 junimos).
2. **Per-run ratio averaging** — aggregating mean-of-ratios lets near-zero baselines explode:
   the "patch is +10~36% worse on 25%-obstacle CPU" rows flip to **-8~-27% (better)** when the
   *same data* is aggregated as ratio-of-means.

The audit's fixed variant became the base of generations 4-5.

## 4. Phase 3 — cross-validation with an independent rebuild

[`simulation/generations/3-independent-rebuild/`](simulation/generations/3-independent-rebuild/README.md)
was written from scratch from the decompiled game and mod sources, without reading the earlier
simulators' code. It found real behaviors both older simulators had missed:

- the **double 10-minute poke** (mod postfix), which makes the mod's poked-decision rate 2x;
- **"doomed" standing** — crops are only removed at the +1000ms crossing of the 2000ms timer, and
  a follower arriving earlier starts its own full timer: vanilla's clump-train costs *parallelism*,
  not a few extra steps;
- claims expiry (1800t), wander retry counts, exact A* tie-breaking.

Its verdict: claims patch **-4~-25% clear time vs vanilla in-sim** (12/12 cells significant),
daily CPU -20~-37%, worst-1s window -37~-44%, clump-standing ~72x fewer. And it delivered the
forensic kill shot for phase 2's throughput numbers: they violated the harvest lower bound
(556 crops × 120t ÷ 4 = 16,680t > the 15,948t reported).

## 5. Phase 4 — what the real game actually said about the claims patch

Timed clears on the real farm (wall clock, in-game hours; two rounds):

| Field | Vanilla (3.2.0) | Claims patch |
|---|---|---|
| Left field | 10h / 11.5h | 10.5h / 10.5h |
| Right field | 11h / 11h | 12h / 11h |

i.e. **rough parity** — the two rounds don't even agree on direction. Clearing throughput was
*not* a selling point for the reactive patch, despite the simulators (fixed ones included)
predicting wins. Two observations *were* robust on the real machine:

- **more perceived frame spikes while clearing** (average FPS was slightly *better* with the
  patch, ~22.5 vs ~20, but hitching felt worse — filter-failure + full-budget shadow re-search
  stacking on the double-poke burst is the suspect);
- **the endgame drags** — the last stretch of a field takes visibly longer than it should.

## 6. Phase 5 — v5: targeted fixes fix the spikes, not the endgame

[`simulation/generations/4-v5-targeted/`](simulation/generations/4-v5-targeted/README.md) added
the double poke, real WorkFaster/WorkRidiculouslyFast semantics, per-quarter phase statistics,
and an overlapping two-hut scene to the audited base, then validated three targeted fixes
("v5"): a **T=40 crowding-verdict cache** (skip provably-doomed filtered searches), a **k=1
scarce-work filter-off**, and an endgame fallback (same mechanism as k).

Result: the spike problem is fixable (worst-1s window **-7~-35% vs the claims patch**, back to
~unfiltered level; daily CPU -33~-44% vs vanilla; anti-clump kept). The endgame problem is not:
the last-quarter/second-quarter time ratio stayed **1.3-3.0** (vanilla: 1.0-1.6), because the
drag comes from reactive sensing's sparse-field failure chain — nearest search fails →
lastKnownCropLocation retry fails → 1-6 wander searches → 25% walk-home-and-respawn, all for
zero crops. Tuning the filter cannot remove a failure chain that exists because sensing is tied
to per-junimo decisions. **v5 was never shipped**; its diagnosis is what pointed at scheduling.

## 7. Phase 6 — assignment v1: dominant in simulation, flawed on the farm

[`simulation/generations/5-assignment-v1/`](simulation/generations/5-assignment-v1/README.md)
simulated a scheduled algorithm (v1): per-hut periodic full-box scans (5s/10s, staggered across
huts) → **regret-based global assignment** → nearest-neighbor route extension → self-refill from
the assignment table between scans; direct-walk A* budget 10000 (the game's own
`returnToJunimoHut` limit). It also contains the **scan-cost boundedness proof**: v1's tile
inspections are constant at 94,080/day (120 scans × 784 tiles) for any density or junimo count —
2.3-3.8x *cheaper* than vanilla's incidental spawn/gate scans; assignment arithmetic ≈ 0.2-1.4%
of vanilla's daily A* budget. The generation-1 v2b "allocator scan explosion" is structurally
excluded.

In-sim, v1 dominated everything: **-42.8~-67.2% clear vs vanilla**, -3.9~-26.7% vs the claims
patch (14/14 cells), daily A* 3-4% of vanilla, clumping/conflicts structurally zero, endgame cost
flat (1.65-1.9x phase-1 vs 7-17x for the reactive algorithms). With WorkFaster/WRF off, v1 kept
-42~-43% while the claims patch's advantage shrank to -4.9~-6.3% — most of the reactive patch's
simulated win had been an artifact of WRF's poke-to-decision postfix.

On the real farm, v1 showed two behavior flaws (this is why v2 exists):

1. **Idle standing** — 1+ junimos per hut parked for long stretches (periodic-rescan cadence +
   regret assignment + mid-route invalidation stranding walkers + candidate-truncation starvation
   + sleeping after a failed self-refill);
2. **Jumpy routes** — regret assignment is spatially discontinuous: junimos drew interleaved
   tiles, producing long legs, backtracking and mid-route reversals.

## 8. Phase 7 — v2: the two-mode scheduler shipped in this branch

The design hinges on how a real farm actually ripens (the user's key insight): **the main crop
ripens all at once, and afterwards only sporadic additions appear** (forage spawns, mod growth
tweaks). So don't re-sense periodically — inventory once per day, cut it into continuous routes,
and after that only maintain. Implementation: `BetterJunimos/Assign/WorkAssigner.cs`.

**Bulk mode** (per hut, at most once per day, lazily on the day's first work trigger):

1. Full inventory of the hut's radius box using the game's own ability predicate
   (`IdentifyJunimoAbility` — every ability × cooldowns × hut supplies × progression gates), so
   every planned tile is actually serviceable. Tiles owned by a strictly-closer hut are excluded.
2. Order the ripe tiles into a **serpentine** (S-shaped row scan; snake head placed at the end
   nearer the hut).
3. Cut the snake into **k equal-time segments** with an explicit cost model
   (`cost(i) = harvest ticks [61t WorkFaster / 120t] + 20t × manhattan step`; the walk *into*
   a tile is charged to the tile's own segment, so segment times sum exactly). k = the hut's
   junimo *cap* (`MaxJunimosUnlocked`), not the current count — junimos spawn throttled ~1/s, so
   planning against the transient count would collapse k to 1.
4. One junimo, one continuous segment. **Zero re-scans, zero re-planning afterwards.**

**Event-driven maintenance only** (bulk phase): path heads pop forward on arrival (with a cheap
actionability check so a sleeping-on-budget junimo doesn't skip a live tile); a dead junimo's
remaining segment passes to the orphan queue **whole** (FIFO, never scattered) for the next
freed junimo to take over; unreachable tiles are blacklisted and skipped (v2.1: only after two
*distinct* junimos fail the tile — a single failure just skips it for that decision); at most 4
A* attempts per decision, then sleep until the next event.

**Endgame mode** (when segment work runs out): take the nearest-to-self actionable tile, one
task at a time. Work sources in order: orphan queue → hut-shared endgame pool (≤16 tiles from the
last scan, zero reads to consult) → an on-demand box scan **hard-rate-limited to ≥15s per hut**
(`EndgameScanIntervalSeconds`, gate inside the scan routine, collects near-to-far and stops at
16). No work → return to the hut (vanilla despawn semantics; the spawner re-releases when work
appears). Endgame tasks go into the same claim registry.

**Multi-hut ownership** is pure deterministic geometry: a tile belongs to the strictly closer hut
(manhattan from hut center); exact ties go to the lexicographically smaller hut tile (X then Y).
Both huts compute identical ownership independently. A global `(location, tile) → junimo` claim
registry (mutual exclusion across huts, including endgame tasks) is the second line of defense.

**No new threads.** All pathfinding stays on the main thread; the only large-budget searches are
the direct walks (budget 10000). Config: `WorkAssignment` (default on; off = the shipped
cap/gate/claims behavior, untouched), `EndgameScanIntervalSeconds` (15, clamped 5..120),
`DebugLog`. Unpaid junimos don't work, and the daily plan is refused while unpaid (this also
keeps the single daily plan out of the shrunken unpaid radius).

## 9. Real-game results of the final build (single user, small n)

**Environment (stated every time):** Stardew Valley 1.6.15 + SMAPI, single-player host, **~200
mods** installed, hut radius 14, `MaxJunimos = 6` per hut, **WorkFaster = false**,
**WorkRidiculouslyFast = false**. Baseline for comparison = **official vanilla 3.2.0** (the
unpatched official DLL, timed by the same user in the same environment: left field 10h/11.5h,
right field 11h/11h — the "Vanilla" column of the §5 table). The claims build is *not* the
baseline here, and no claims-relative percentage was measured.

| Scenario | Crops | Clear time vs official vanilla 3.2.0 | Notes |
|---|---|---|---|
| Left field, 1 hut | 576 | **~70-80%** of vanilla time (≈20-30% faster) | |
| Right field, 2 huts | 1152 total (~150-tile box overlap) | **~65-75%** of vanilla time (≈25-35% faster) | first time the two-hut mixed scenario came out *relatively better* than the single-hut one |

Also observed: FPS slightly up; **all six junimos working** (six segments cut, six claimed);
continuous serpentine paths with no backtracking; no clumping; endgame responses normal.

**Sample-size disclaimer:** 1-2 runs per configuration, one farm layout, one machine, one
season's crops. The percentages are honest readings of those runs, not averaged benchmarks.

## 10. v2.0 → v2.1: the accumulator bug and the spawn lottery

The first v2 build (assign2-plain) had a great symptom: fields cleared fast, but **only 3 of 6
junimos per hut worked**; the rest despawned right after spawning.

- **Root cause (main):** the equal-time cutter reset its accumulator at every cut while its
  threshold was cumulative (`acc >= total×(cut+1)/k`). Segment m was distorted to ≈ m×total/k:
  with k=6 and 571 ripe tiles the snake was exhausted after **3 segments** (96/191/284) —
  three junimos claimed segments, the rest found everything registered, endgamed, and went home.
  The user's SMAPI log showed `segs=3` on all four huts, even a 68-crop hut (12/23/33 — matching
  hand calculation exactly). Fix: keep the accumulator cumulative; k=6 → 6 segments (~total/k
  each), verified offline (571/567/68 × k=6 → 6/6/6).
- **Root cause (independent, half of "spawn then die"):** the **vanilla spawn lottery**.
  `JunimoHarvester`'s constructor only provides a guaranteed spawn anchor for junimo numbers
  0-2; numbers ≥3 fall into `pathfindToRandomSpotAroundHut()` — a random endpoint within radius
  14 reached with A* **budget 100**, three tries, `destroy = true` on triple failure. Vanilla
  never issues numbers ≥3, but the mod's MaxJunimos extension does, so junimos 3/4/5 roll these
  dice on every spawn; on typically fenced farms the triple-failure rate reaches double-digit
  percentages. v2.1 mitigations: a near-field fallback endpoint (≤3 tiles, ≤4 tries — short paths
  essentially always solve within budget 100, making constructor destruction unreachable), and
  `OnJunimoSpawned` only cancels the constructor's wander when it can actually hand over a
  controller (work or go-home), otherwise the wander is restored. *(The underlying vanilla
  behavior might deserve an upstream look on its own.)*
- Also in v2.1: conservative blacklisting (two distinct junimos must fail a tile before it is
  blacklisted for the day), and `DebugLog` diagnostics (spawn chain, work-seek outcomes,
  pathfail classification with budget/reason, destroy events).
- Verification: offline re-cut of the logged fields, plus a smoke test on SMAPI 4.5.2 /
  SDV 1.6.15 build 24356 — banner `build=assign2.1-plain`, zero BetterJunimos errors, zero
  Harmony patch exceptions. `InformationalVersion = 3.2.0-assign2.1-plain`.

## 11. What else is in this branch

- The shipped-behavior performance work from the #114 era — **decision rate cap** (20t/40t),
  **scan gate** (consult the 60t-cached hut scan before spending searches), **claim-aware
  endpoint filter** with unfiltered shadow re-search — is still present and is exactly what
  `WorkAssignment = false` falls back to. The claim registry it introduced is reused by the
  scheduler.
- Three small standalone fixes (ported/inspired by Aufhcegak/BetterJunimosFix, MIT): cap wander
  re-searches 6 → 2, fix the swapped bounds in `EndPointInGreenhouse` (out-of-range crash on
  non-standard greenhouse sizes), and a per-day cache for hut id lookups (previously scanned
  every location × building per call on a hot path).

## 12. Tried and abandoned — the full list

1. **Per-frame decision model and its conclusions** (§2) — "42 A*/s", "5-16% of vanilla CPU".
   Retracted with #114. Model error ~10x on vanilla cost.
2. **The corrected simulator's own bugs** (§3) — phantom `remaining--` (~30% of baseline work
   never simulated) and mean-of-ratios aggregation (sign flips on near-zero baselines).
3. **The claims patch as a throughput win** (§5) — real game: clear-time parity with vanilla,
   more perceived spikes, draggy endgame. The patch survives in this branch only as the fallback
   behavior and the claim infrastructure.
4. **v5 targeted fixes** (§6) — spikes fixed in-sim, endgame not cured (structural). Never
   shipped.
5. **Assignment v1** (§7) — dominant in simulation; real farm showed idle standing junimos and
   jumpy regret routes. Replaced by v2.
6. **v2.0's equal-time cutter** (§10) — accumulator reset bug: k=6 cut 3 segments, 3 of 6
   junimos idle/despawning. Fixed in v2.1.
7. **The vanilla spawn lottery** (§10) — junimo numbers ≥3 spawn-anchor-less, budget-100 random
   walk, three strikes and destroyed at birth. Mitigated in v2.1; the root cause is in vanilla
   code, not the mod.
8. **The async pathfinding thread layer** — a `#if ASYNC_PATHFIND` off-main-thread A* layer
   (`BetterJunimos/Assign/AsyncPath.cs`, present but never compiled in this branch — the symbol
   is deliberately not defined). A real-game probe showed it workable, but the user's test found
   **more** frame spikes than the main-thread build, so it was judged a loss and shelved
   permanently.

Also abandoned along the way (phase 1-2 era): claims_v1 (frozen work list deadlock), claims_v2b
(allocator scan explosion), claims_v3 (filter without fallback).

## 13. Repository map

| Path | What |
|---|---|
| [`BetterJunimos/`](BetterJunimos/README.md) | Final source, v2.1 (`InformationalVersion 3.2.0-assign2.1-plain`). Scheduler: [`BetterJunimos/Assign/WorkAssigner.cs`](BetterJunimos/Assign/WorkAssigner.cs) |
| [`simulation/README.md`](simulation/README.md) | The five simulator generations, with per-generation conclusions and why each was superseded |
| [`simulation/generations/2-corrected-event-driven/AUDIT-REPORT.md`](simulation/generations/2-corrected-event-driven/AUDIT-REPORT.md) | The audit that found generation 2's phantom decrement and statistics flaws (Chinese) |

*Simulation reports for generations 3-5 are in Chinese; every generation directory has an
English `README.md` summary.*
