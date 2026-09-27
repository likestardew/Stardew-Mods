# Junimo decision-loop simulator

A standalone console app (no game/mod dependencies) used to measure and compare
junimo work-finding algorithms for [Better Junimos](../BetterJunimos), as
described in [hawkfalcon/Stardew-Mods#114](https://github.com/hawkfalcon/Stardew-Mods/issues/114).

It is an event-driven simulation faithful to the decompiled 1.6.15 cadences:

- grid farm with blocked tiles; crops inside the hut's working radius
- junimos spawn staggered, walk 20 ticks/tile, harvest in 20 ticks,
  re-decide ~70% per frame while idle (`update()` → `pokeToHarvest()`),
  including mid-walk re-planning for the vanilla cadence
- `PathFindController` = 4-directional A* with the expansion budget (`limit`);
  the `foundCropEndFunction` variant has no heuristic (Dijkstra), so the 100-node
  budget reaches only ~5-6 tiles — modeled as such
- the `lastKnownCropLocation` fallback chain (50% lkc / 25% home / wander),
  row-major scans with early exit, crop committed to the first junimo that arrives

Compared algorithms: vanilla (`legacy`), the claim filter pre-cap (`claims_v4b`),
and the shipped combination — decision rate cap + scan gate + claims
(`v4b_gate_backoff`). Outputs per config: time to clear the field, total A* node
expansions (the CPU proxy that matters on the single-threaded game loop), wasted
arrivals on dead tiles, despawn/respawn counts, and scan tile checks. A separate
full-day mode (`CpuBenchmark` in `Program.cs`) measures steady-state A* cost with
crops ripening in waves.

## Run

```
dotnet run -c Release
```

Writes `results.csv` (throughput grid) and `cpu_results.csv` (full-day CPU).
All scenario parameters live in the `Cfg` class at the top of `Program.cs` —
radii, junimo counts, crop densities, poke probabilities, pathfinding budget,
backoff/cap intervals.

MIT, same as the rest of the repository.
