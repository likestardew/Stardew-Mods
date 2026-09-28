# Generation 3 — independent rebuild (cross-validation)

**Status: superseded as a tool (generations 4-5 extend the audited generation-2 base instead),
but its cross-validation verdict stood and its forensic finding killed generation 2's numbers.**

## What it is

An event-driven simulator **written from scratch** from the decompiled 1.6.15 game code and the
3.2.0 mod sources, without reading any earlier simulator's code — deliberately, to cross-check
them. Parameter values (map size, radius, densities, wave times) were kept comparable on purpose;
all logic was rederived. Full behavior spec with decompile line references is in
[REPORT.md](REPORT.md) (Chinese).

## What it found that the older simulators missed

- **Double poke**: the mod's `ReplaceJunimoTimerNumber` is a postfix of `performTenMinuteAction`,
  so every junimo gets **two** independent 70%-gated decision opportunities every ~430 ticks.
- **"Doomed" standing**: crops are removed only at the +1000ms crossing of the 2000ms harvest
  timer, and a follower arriving before that starts its **own** full 2000ms timer — vanilla's
  clump-train cost is lost parallelism, not a few extra steps.
- Wander = 1 base search + up to 6 postfix re-searches (vanilla) / up to 2 (patched);
  claims expire after 1800t; movement is 21.3 t/tile raw.

## Headline results (paired, in-sim)

- Claims patch clear time **-4% to -25% vs vanilla** (all 12 cells significant), daily CPU
  -20~-37%, worst-1s A* window -37~-44%; clump-standing ~72x fewer. `nocap ≈ shipped`.
- **Forensic kill shot** for generation 2's throughput numbers: they violate their own harvest
  lower bound (e.g. 556 crops x 120t / 4 junimos = 16,680t > the 15,948t reported) — the old
  model had been resuming decisions at the +60t removal point and bouncing followers instantly,
  inflating vanilla's throughput.

## Honest caveat recorded at the time

The in-sim advantage of the claims patch was **larger than the real game later showed**
(real-game clearing was roughly at parity). Unmodeled factors (watering/fertilizing ability
traversals enlarging the actionable set, 10-minute-granularity timing, junimo collisions)
compress the gap; cross-model absolute numbers do not transfer.

## Why it was superseded

It models the *reactive* algorithms only. The investigation moved on to targeted fixes
(`../4-v5-targeted/`) and then to scheduled work assignment (`../5-assignment-v1/`), which
needed the audited base plus WF/WRF speed semantics, phase statistics, and multi-hut scenes.
