# Generation 1 — initial simulator (WRONG decision cadence)

**Status: historical only. Its headline numbers were retracted. Do not cite them.**

## What it is

The first offline simulator, written alongside the initial profiling of Better Junimos 3.2.0
that led to [hawkfalcon/Stardew-Mods#114](https://github.com/hawkfalcon/Stardew-Mods/issues/114).
It models a grid farm (80x65, ~5% blocked tiles, crops inside the hut radius box) and compares
vanilla work-finding against early claim/allocator variants (`claims_v1`, `claims_v2b`, `claims_v3`).

## Its fatal modeling error

It assumed idle junimos re-decide **every ~2 ticks** (`update()` -> `pokeToHarvest()` every frame
while idle, 70% chance to pathfind) — i.e. the "≈42 A* searches per second per junimo" figure
quoted in #114. Reading the decompiled 1.6.15 `JunimoHarvester` showed the real decision loop is
**event-driven**: path arrival, harvest-timer expiry, the hut's 10-minute pokes (doubled by a mod
postfix), and a rare idle random action — roughly 0.3-1 decisions/second/junimo, an order of
magnitude below the model.

## What it concluded (retracted)

- "Patch CPU = 5-16% of vanilla" and similar CPU/throughput figures quoted in #114 — all products
  of inflating vanilla's per-frame cost ~10x.
- Useful byproduct: this generation *did* correctly falsify three early patch designs
  (claims_v1 frozen work list deadlock; claims_v2b allocator scan explosion on dense fields;
  claims_v3 filter-without-fallback degradation at large radius).

## Why it was superseded

Issue #114 was withdrawn and its numbers retracted once the cadence error was discovered.
Generation 2 rebuilt the simulator on the corrected event-driven cadence
(see `../2-corrected-event-driven/`).
