# Generation 2 — corrected event-driven simulator

**Status: superseded. The cadence fix was right, but this generation still contained two
significant defects (a phantom crop decrement and a statistics flaw) found by a later audit —
see [AUDIT-REPORT.md](AUDIT-REPORT.md).**

## What it is

A rebuild of the simulator on the corrected, decompile-verified **event-driven** cadence
(decisions from path arrival / harvest-timer expiry / 10-minute hut pokes / rare idle random),
with the real claim-filter semantics (skip-and-continue endpoint filter + unfiltered "shadow"
re-search when crowded) and paired seeds (same map for every algorithm within a run index).

This is the code that was originally pushed to the `perf/junimo-pathfinding` branch's
`simulation/` directory (the pushed variant additionally gained a `realfilter_nocap` algorithm
toggle) and produced the "final results" dataset of that era: 3600 paired throughput runs and
600 paired CPU-day runs (n=100 / n=50).

## What it concluded at the time (later shown to be distorted)

- Claims patch clear time **+27-53% slower than vanilla** (this drove the #114 retraction
  rationale "claims make junimos take detours; vanilla's clump-train is efficient").
- Steady-state daily CPU 0.62-0.89x vanilla; anti-clump ~10x fewer wasted arrivals.

## The two defects found later

1. **Phantom `remaining--`**: after a harvest the state was not reset, so subsequent no-op
   working events decremented the remaining-crop counter again. The run exited early on a map
   that was never fully cleared — roughly **30% of the baseline's work was never simulated**.
   Anchor: vanilla d=1.00, jn=2 clear time 649s (buggy) vs 1689s (fixed, n=100); the buggy
   number violated the simulator's own 120t-per-harvest lower bound.
2. **Per-run ratio averaging**: `aggregate.py` averaged per-run `(patch-vanilla)/vanilla`
   ratios. Near-zero baselines (a handful of legacy runs at 1k-6k pops/day) produced exploding
   ratios that flipped the sign of the 25%-obstacle CPU rows: "+10~36% worse" became
   **-8~-27% (better)** when recomputed as ratio-of-means **on the same data**.

The audit that found these (plus missing claim expiry, the interrupted-walk timer detail, and
the spawn-decision gate) is preserved verbatim in [AUDIT-REPORT.md](AUDIT-REPORT.md); the
audited/fixed variant became the base for generations 4 and 5.

## Why it was superseded

Its baseline numbers were arithmetically impossible, and its headline "patch is slower"
conclusion did not survive the fix — or the independent rebuild in
`../3-independent-rebuild/`, or the real game.
