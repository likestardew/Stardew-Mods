# Generation 4 — v5 targeted fixes to the reactive claims patch

**Status: superseded. It fixed the frame-spike problem in-sim but could not fix the endgame
degradation, which is structural to reactive scheduling. v5 was never shipped as a DLL.**

## What it is

The audited generation-2 simulator plus five modeling additions (all source-verified, see
[DESIGN.md](DESIGN.md) and [REPORT.md](REPORT.md), Chinese):

- the **double 10-minute poke** (mod postfix),
- real **WorkFaster / WorkRidiculouslyFast** semantics (harvest period unchanged; poke becomes a
  guaranteed decision; 3.5% wander removed; "nothing to do" timer 200ms -> 5ms),
- **per-quarter phase statistics** (time and A* cost split by cleared-fraction 0-25/25-50/50-75/75-100%),
- an **overlapping two-hut scenario** (two huts, 6 junimos each, shared claim table),
- a counted scan variant with early exit so v5's "count actionable tiles" need does not break the
  existing scan-cost bound.

## The three v5 fixes it validated

1. **k=1 conditional filter-off** — when the hut's cached scan counts <= 1 actionable tile per
   junimo, disable the claim filter for that decision (fall back to vanilla greed).
2. **T=40 crowding-verdict cache** — "the filtered search exhausted its budget against other
   junimos' claims" is cached per hut for 40t; later decisions skip the doomed filtered search
   and go straight to the (identical-target) unfiltered shadow search.
3. **m endgame fallback** — same mechanism as k, kept as a separate knob.

## Headline results (paired, in-sim)

- Clear time vs vanilla **-24% to -70%** (v5 within 4pp of the claims patch, mostly <=1pp).
- Daily CPU **-33% to -44%** vs vanilla (claims patch: -20~-34%).
- Worst-1s window **-7~-35% vs the claims patch**, back to roughly the unfiltered level — this
  matched and addressed the real-game observation of "more frame spikes while clearing"
  (the claims build had higher average FPS, 22.5 vs 20, but *more* perceived spikes; the average
  gain comes from the decision cap, the spikes from filter-failure + full-budget shadow re-search
  stacking on the double-poke burst).
- Anti-clump retained (doomed arrivals 0.2-7.8% of vanilla).

## The honest negative

The real-game complaint "**the later the slower**" (endgame drag) was only partly reproduced and
**not cured**: the last-quarter/second-quarter time ratio stayed 1.3-3.0 for the patched
algorithms vs 1.0-1.6 for vanilla (worst on sparse fields). The cause is reactive scheduling's
sparse-field failure chain: nearest-search fails -> lastKnownCropLocation retry fails -> 1-6
wander searches -> 25% chance to walk home and respawn — all burning CPU for zero crops.

## Why it was superseded

A failure chain that only exists because sensing is tied to per-junimo reactive decisions cannot
be fixed by tuning the filter. That diagnosis led directly to scheduled work assignment
(`../5-assignment-v1/`) and finally to the two-mode design shipped in this branch
(`BetterJunimos/Assign/WorkAssigner.cs`).
