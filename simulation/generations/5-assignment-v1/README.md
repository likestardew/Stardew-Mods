# Generation 5 — assignment scheduler v1

**Status: superseded in code by the shipped v2 two-mode design. The simulator results below are
the reason the assignment direction was chosen; v1's real-game behavior flaws are the reason v2
exists. Docs: [DESIGN.md](DESIGN.md), [REPORT.md](REPORT.md) (Chinese).**

## What it simulates

Assignment v1, the first scheduled (as opposed to reactive) work algorithm:

- each hut runs a **periodic full-box scan** (every 5s or 10s of game time, phases staggered
  across huts so bursts never overlap),
- ripe tiles are distributed by a **regret-based global assignment** (candidate cap 32),
- each junimo gets a **nearest-neighbor route** extended from its position; direct-walk A* with
  budget 10000 (the same limit the game itself uses for `returnToJunimoHut`),
- between scans, route exhaustion and spawns **self-refill** from the assignment table
  (zero tile reads) with a ring nearest-neighbor query.

## Headline results (paired, in-sim)

- Clear time **-42.8% to -67.2% vs vanilla**, **-3.9% to -26.7% vs the claims patch** (14/14
  cells p<.001; two-hut -12~-13% vs the patch). Daily A* cost **3-4% of vanilla**, 10-21% of the
  claims patch; worst-1s window 8-20% of vanilla (the 430t double-poke storm does not exist for
  a scheduler — pokes are no-ops).
- **Scan cost proven bounded** (the thing that killed the v2b allocator in generation 1):
  tile inspections are constant at 94,080/day (120 scans x 784 tiles) regardless of crop density
  or junimo count — 2.3-3.8x *cheaper* than vanilla's incidental spawn/gate scans; assignment
  arithmetic is ~0.2-1.4% of vanilla's daily A* budget. Phase-4 (endgame) cost is only 1.65-1.9x
  phase-1, vs 7-17x for vanilla and the claims patch — the endgame collapse is explained and gone.
- Clumping/conflicts are **structurally zero** (mutually exclusive assignment, including across
  huts; doomed arrivals 0 in all 8,000 runs).
- Control group with WorkFaster=WorkRidiculouslyFast **off**: assign keeps -42~-43% while the
  claims patch shrinks to -4.9~-6.3% — most of the reactive patch's in-sim win was an artifact
  of WRF's poke-to-decision postfix doubling its re-decision rate. The scheduler's win does not
  depend on speed settings.

## Why v1 was replaced anyway (real-game behavior flaws)

Played on the real farm, v1 had two visible defects (documented in the v2 design doc):

1. **Idle standing** — 1+ junimos per hut parked for long stretches. Causes: the periodic
   full re-scan cadence, regret assignment, mid-route invalidation stranding walkers,
   candidate-truncation starvation, and sleeping after a failed self-refill.
2. **Jumpy routes** — regret assignment is spatially discontinuous: junimos drew interleaved
   tiles, producing long legs, backtracking, and mid-route direction reversals.

## What replaced it

The **two-mode work-assignment scheduler** shipped in this branch: one bulk plan per hut per day
(inventory -> serpentine -> equal-time cut into k continuous segments), event-driven maintenance
only, and a nearest-first rate-limited endgame heuristic. See `INVESTIGATION.md` at the repo root
and `BetterJunimos/Assign/WorkAssigner.cs`.
