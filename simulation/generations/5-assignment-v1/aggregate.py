#!/usr/bin/env python3
"""Aggregation for the sim-assign benchmark (stdlib only).

Reads results.csv (throughput) and cpu_results.csv (day benchmark) written by
sim-assign/Program.cs, computes paired statistics vs legacy_arrival, and writes
summary.md. Pairing is by run index: all algorithms simulate the IDENTICAL map
for a given (radius, junimosTotal, density|obstacles, run); seeds exclude the
algorithm name, speed mode and hut count.

Statistics per paired cell (same methodology as sim-audit1):
  - PRIMARY: ratio of means, ROM = mean(algo)/mean(base) - 1 (robust to
    near-zero baselines; answers "total patched cost / work time vs vanilla").
  - SECONDARY: mean of paired differences with a paired t-test (two-sided p,
    normal approximation).
Direct assign-vs-v4b comparisons are emitted as a separate table (the paired
design makes any two algorithms comparable, not only vs the baseline).

Guards:
  - Degenerate maps (degen column: some hut has no reachable free tile in its
    box) are excluded from both sides; counts reported.
  - DNF runs keep clear_ticks = SIM_CAP (240000, censored); DNF counts reported.

Cost conversion for the scan/assign overhead (conservative bounds, see
REPORT.md): one A* pop ~ 10-30 tile inspections ~ 200-600 basic assignment ops.
"eq_hi" values inspections at 10/pop and ops at 200/pop (the MOST EXPENSIVE
reading for the assign algorithm); "eq_lo" uses 30 and 600.
"""
import csv, math, os
from statistics import mean, median

HERE = os.path.dirname(os.path.abspath(__file__))
SIM_CAP_S = 240000 / 60.0
BASE = "legacy_arrival"
THROUGHPUT_ALGOS = ["legacy_arrival", "cap_only", "v4b_realfilter", "assign5", "assign10"]
CPU_ALGOS = THROUGHPUT_ALGOS

def sd(xs):
    m = mean(xs)
    return math.sqrt(sum((x - m) ** 2 for x in xs) / len(xs)) if len(xs) > 1 else 0.0

def p_from_t(t):
    if t == 0:
        return 1.0
    z = abs(t)
    p = 2.0 * (1.0 - 0.5 * (1.0 + math.erf(z / math.sqrt(2.0))))
    return max(p, 1e-16)

def paired(a_vals, b_vals):
    n = len(a_vals)
    d = [a - b for a, b in zip(a_vals, b_vals)]
    mb, ma = mean(b_vals), mean(a_vals)
    rom = (ma / mb - 1.0) * 100.0 if mb != 0 else float("nan")
    s = sd(d)
    t = mean(d) / (s / math.sqrt(n)) if s > 0 else (0.0 if mean(d) == 0 else math.copysign(1e9, mean(d)))
    return dict(rom=rom, mod_pct=mean(d) / mb * 100.0 if mb else float("nan"),
                t=t, p=p_from_t(t), n=n)

def fmt_p(p):
    return "n/a" if p != p else (".000" if p < 5e-4 else f"{p:.3f}")

def load(fn):
    with open(os.path.join(HERE, fn), newline="") as f:
        return list(csv.DictReader(f))

def group(rows, keys):
    g = {}
    for r in rows:
        g.setdefault(tuple(r[k] for k in keys), []).append(r)
    return g

def by_run(rs):
    return {r["run"]: r for r in rs}

def ok(rs):
    """usable runs: not degenerate (and keep DNF, censored)"""
    return [r for r in rs if not r["degen"]]

def throughput_tables():
    rows = load("results.csv")
    for r in rows:
        r["huts"] = int(r["huts"]); r["junimos"] = int(r["junimos"])
        r["density"] = float(r["density"]); r["run"] = int(r["run"])
        r["clear_s"] = int(r["clear_ticks"]) / 60.0
        r["crops"] = int(r["crops"])
        r["wasted_arrivals"] = int(r["wasted_arrivals"])
        r["doomed_arrivals"] = int(r["doomed_arrivals"])
        r["doomed_foreign"] = int(r["doomed_foreign"])
        r["diverged"] = int(r["diverged"])
        r["expansions"] = int(r["expansions"]); r["inspections"] = int(r["inspections"])
        r["assign_ops"] = int(r["assign_ops"])
        r["dnf"] = r["dnf"].strip().lower() == "true"
        r["degen"] = r["degen"].strip().lower() == "true"
    g = group(rows, ["speed", "huts", "density", "junimos", "algo"])
    lines = []
    for speed in ["fast", "off"]:
        for huts in [1, 2]:
            densities = sorted({r["density"] for r in rows if r["speed"] == speed and r["huts"] == huts})
            for density in densities:
                title = (f"Table T-{speed}/{huts}h: throughput (clear time, s) — "
                         + ("double hut A(28,32) B(52,32) r=14, 6 junimos each" if huts == 2 else "single hut")
                         + f", density {density}, n=100 paired runs" if speed == "fast" else
                         f"Table T-{speed}/{huts}h: CONTROL WorkFaster=WorkRidiculouslyFast=false — density {density}, n=100")
                lines += [f"### {title}", "",
                          "| jn | crops | legacy mean±sd [DNF] | cap Δ%(p) | v4b Δ%(p) | assign5 Δ%(p) | assign10 Δ%(p) | assign5 mean | dead L/V/A5 | doomed L/V/A5 | xHutDoom L/V/A5 | diverged A5 |",
                          "|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
                jns = sorted({r["junimos"] for r in rows if r["speed"] == speed and r["huts"] == huts})
                for jn in jns:
                    cells, extra = {}, {}
                    base_all = by_run(ok(g.get((speed, huts, density, jn, BASE), [])))
                    base_done = {k: v for k, v in base_all.items() if not v["dnf"]}
                    crops_mean = mean([v["crops"] for v in base_all.values()]) if base_all else 0
                    for algo in THROUGHPUT_ALGOS:
                        ars_all = by_run(ok(g.get((speed, huts, density, jn, algo), [])))
                        ars_done = {k: v for k, v in ars_all.items() if not v["dnf"]}
                        if not ars_done:
                            cells[algo] = "MISSING"
                            continue
                        vals = [v["clear_s"] for v in ars_done.values()]
                        dnf = len(ars_all) - len(ars_done)
                        m = f"{mean(vals):.1f}±{sd(vals):.1f}" + (f" DNF={dnf}" if dnf else "")
                        if algo != BASE and base_done:
                            shared = sorted(set(base_done) & set(ars_done))
                            st = paired([ars_done[k]["clear_s"] for k in shared],
                                        [base_done[k]["clear_s"] for k in shared])
                            m += f" | **{st['rom']:+.1f}%** ({fmt_p(st['p'])})"
                        cells[algo] = m
                        extra[algo] = (mean(v["wasted_arrivals"] for v in ars_done.values()),
                                       mean(v["doomed_arrivals"] for v in ars_done.values()),
                                       mean(v["doomed_foreign"] for v in ars_done.values()),
                                       mean(v["diverged"] for v in ars_done.values()))
                    L, V, A = extra.get("legacy_arrival"), extra.get("v4b_realfilter"), extra.get("assign5")
                    lines.append(
                        f"| {jn} | {crops_mean:.0f} | {cells[BASE]} | {cells['cap_only']} | {cells['v4b_realfilter']} | {cells['assign5']} | {cells['assign10']} | "
                        f"{cells['assign5'].split('±')[0] if 'assign5' in cells else '-'} | "
                        f"{L[0]:.1f}/{V[0]:.1f}/{A[0]:.1f} | {L[1]:.1f}/{V[1]:.1f}/{A[1]:.1f} | "
                        f"{L[2]:.1f}/{V[2]:.1f}/{A[2]:.1f} | {A[3]:.2f} |")
                lines.append("")
    # direct assign vs v4b
    lines += ["### Direct paired comparison: assign5 vs v4b_realfilter (negative = assign faster)", "",
              "| speed | huts | density | jn | clear Δ% (p) | pops Δ% (p) | insp Δ% (p) | eq_hi* Δ% (p) |",
              "|---|---|---|---|---|---|---|---|"]
    g5 = group(rows, ["speed", "huts", "density", "junimos", "algo"])
    keys5 = sorted({k[:4] for k in g5})
    for (speed, huts, density, jn) in keys5:
        v4 = {k: v for k, v in by_run(g5.get((speed, huts, density, jn, "v4b_realfilter"), [])).items() if not v["dnf"]}
        a5 = {k: v for k, v in by_run(g5.get((speed, huts, density, jn, "assign5"), [])).items() if not v["dnf"]}
        if not v4 or not a5:
            continue
        shared = sorted(set(v4) & set(a5))

        def eq_hi(r):
            return r["expansions"] + r["inspections"] / 10.0 + r["assign_ops"] / 200.0
        stc = paired([a5[k]["clear_s"] for k in shared], [v4[k]["clear_s"] for k in shared])
        stp = paired([a5[k]["expansions"] for k in shared], [v4[k]["expansions"] for k in shared])
        sti = paired([a5[k]["inspections"] for k in shared], [v4[k]["inspections"] for k in shared])
        ste = paired([eq_hi(a5[k]) for k in shared], [eq_hi(v4[k]) for k in shared])
        lines.append(f"| {speed} | {huts} | {density} | {jn} | **{stc['rom']:+.1f}%** ({fmt_p(stc['p'])}) | "
                     f"{stp['rom']:+.1f}% ({fmt_p(stp['p'])}) | {sti['rom']:+.1f}% ({fmt_p(sti['p'])}) | "
                     f"{ste['rom']:+.1f}% ({fmt_p(ste['p'])}) |")
    lines.append("")
    lines.append("*eq_hi = A* pops + inspections/10 + assignment ops/200 — the reading of the conversion that is LEAST favorable to assign (1 A* pop treated as cheap as 10 tile reads / 200 basic ops).")
    lines.append("")
    return lines

def cpu_tables():
    rows = load("cpu_results.csv")
    for r in rows:
        r["huts"] = int(r["huts"]); r["junimos"] = int(r["junimos"])
        r["obs"] = float(r["obstacles"]); r["run"] = int(r["run"])
        r["degen"] = r["degen"].strip().lower() == "true"
        r["pops"] = int(r["total_pops"]); r["insp"] = int(r["total_insp"]); r["ops"] = int(r["total_ops"])
        r["peak"] = int(r["peak_pops_1s"]); r["peakm"] = int(r["peak_mixed_1s"])
        r["cleared"] = int(r["cleared"]); r["scans"] = int(r["scans"])
        n = int(r["wave_lag_n"])
        r["wlag"] = int(r["wave_lag_sum"]) / n if n else None
        # pop-equivalents (two conversion brackets)
        r["eq_hi"] = r["pops"] + r["insp"] / 10.0 + r["ops"] / 200.0
        r["eq_lo"] = r["pops"] + r["insp"] / 30.0 + r["ops"] / 600.0
    g = group(rows, ["huts", "obs", "junimos", "algo"])
    lines = ["### CPU per day (36000t, 4 crop waves; speed=fast) — A* pops, tile inspections, assignment ops, peaks", "",
             "ROM vs legacy_arrival (p) from the paired t-test; degen maps excluded. eq = pop-equivalent total with inspections at 10..30/pop and assign ops at 200..600/pop.",
             "",
             "| huts | obs | jn | legacy pops/day | cap Δ% | v4b Δ% | a5 Δ% | a10 Δ% | a5 insp/day | a5 eq_lo..hi | legacy peak/1s | a5 peak Δ% | a10 peak Δ% | a5 eq peak* Δ% |",
             "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
    for huts in [1, 2]:
        for obs in sorted({r["obs"] for r in rows}):
            for jn in sorted({r["junimos"] for r in rows if r["huts"] == huts}):
                base = by_run(ok(g.get((huts, obs, jn, BASE), [])))
                if not base:
                    continue
                row = {}
                for metric in ["pops", "peak", "peakm"]:
                    for algo in CPU_ALGOS:
                        ars = by_run(ok(g.get((huts, obs, jn, algo), [])))
                        if not ars:
                            continue
                        shared = sorted(set(base) & set(ars))
                        st = paired([ars[k][metric] for k in shared], [base[k][metric] for k in shared])
                        row[(metric, algo)] = f"{st['rom']:+.1f}% ({fmt_p(st['p'])})"
                        if algo == BASE:
                            row[(metric, "base_mean")] = mean([base[k][metric] for k in shared])
                a5 = by_run(ok(g.get((huts, obs, jn, "assign5"), [])))
                a5m = mean([a5[k]["insp"] for k in a5]) if a5 else float("nan")
                a5eq = (mean([a5[k]["eq_lo"] for k in a5]), mean([a5[k]["eq_hi"] for k in a5])) if a5 else (float("nan"), float("nan"))
                lines.append(
                    f"| {huts} | {obs:.2f} | {jn} | {row[('pops','base_mean')]:.0f} | {row.get(('pops','cap_only'),'-')} | {row.get(('pops','v4b_realfilter'),'-')} | "
                    f"{row.get(('pops','assign5'),'-')} | {row.get(('pops','assign10'),'-')} | {a5m:.0f} | {a5eq[0]:.0f}..{a5eq[1]:.0f} | "
                    f"{row[('peak','base_mean')]:.0f} | {row.get(('peak','assign5'),'-')} | {row.get(('peak','assign10'),'-')} | {row.get(('peakm','assign5'),'-')} |")
    lines.append("")
    lines.append("*eq peak = peak (pops + insp/10 + ops/200) per 1s window — worst-case-cost reading of the scan/assign burst.")
    lines.append("")
    # churn throughput + wave lag
    lines += ["### Day-mode churn: crops harvested/day and wave-discovery lag (assign only)", "",
              "| huts | obs | jn | legacy cleared/day | v4b cleared | a5 cleared | a10 cleared | a5 wave lag t | a10 wave lag t |",
              "|---|---|---|---|---|---|---|---|---|"]
    for huts in [1, 2]:
        for obs in sorted({r["obs"] for r in rows}):
            for jn in sorted({r["junimos"] for r in rows if r["huts"] == huts}):
                cells = {}
                for algo in CPU_ALGOS:
                    ars = ok(g.get((huts, obs, jn, algo), []))
                    cells[algo] = mean([r["cleared"] for r in ars]) if ars else float("nan")
                a5l = [r["wlag"] for r in ok(g.get((huts, obs, jn, "assign5"), [])) if r["wlag"] is not None]
                a10l = [r["wlag"] for r in ok(g.get((huts, obs, jn, "assign10"), [])) if r["wlag"] is not None]
                lines.append(f"| {huts} | {obs:.2f} | {jn} | {cells[BASE]:.1f} | {cells['v4b_realfilter']:.1f} | {cells['assign5']:.1f} | {cells['assign10']:.1f} | "
                             f"{mean(a5l):.0f} | {mean(a10l):.0f} |")
    lines.append("")
    return lines

def phase_tables():
    """Per-quartile (crops cleared) cost split — throughput mode, key cells."""
    rows = load("results.csv")
    for r in rows:
        r["speed"] = r["speed"]; r["huts"] = int(r["huts"]); r["junimos"] = int(r["junimos"])
        r["density"] = float(r["density"]); r["degen"] = r["degen"].strip().lower() == "true"
        r["dnf"] = r["dnf"].strip().lower() == "true"
        for i in range(4):
            r[f"pops_q{i}"] = int(r[f"pops_q{i+1}"]); r[f"insp_q{i}"] = int(r[f"insp_q{i+1}"]); r[f"ops_q{i}"] = int(r[f"ops_q{i+1}"])
        r["eq_q"] = [r[f"pops_q{i}"] + r[f"insp_q{i}"] / 10.0 + r[f"ops_q{i}"] / 200.0 for i in range(4)]
    g = group(rows, ["speed", "huts", "density", "junimos", "algo"])
    lines = ["### Phase cost split (throughput mode): pop-equivalent cost per quartile of crops cleared (eq = pops + insp/10 + ops/200)", "",
             "| cell | algo | Q1 0-25% | Q2 25-50% | Q3 50-75% | Q4 75-100% | total |", "|---|---|---|---|---|---|---|"]
    for (speed, huts, density, jn) in [(s, h, d, j) for s in ["fast"] for h in [1, 2] for d in [0.10, 1.00] for j in [2, 6]]:
        for algo in THROUGHPUT_ALGOS:
            rs = ok(g.get((speed, huts, density, jn, algo), []))
            if not rs:
                continue
            qs = [mean(r["eq_q"][i] for r in rs if not r["dnf"]) for i in range(4)]
            tot = sum(qs)
            lines.append(f"| {speed}/{huts}h d{density} jn{jn} | {algo} | {qs[0]:.0f} | {qs[1]:.0f} | {qs[2]:.0f} | {qs[3]:.0f} | {tot:.0f} |")
    lines.append("")
    return lines

def main():
    parts = ["# sim-assign benchmark summary", "",
             "Grid: radius 14, farm 80x65; speed=fast = WorkFaster+WorkRidiculouslyFast (user's real config; harvest cycle 61t, poke decisions at 100%);",
             "n=100 paired runs per throughput cell, n=50 per CPU cell (double-hut CPU n=30). Seeds exclude algo/speed/huts.",
             "Baseline legacy_arrival = BJ 3.2.0 decision logic at the real event-driven cadence INCLUDING the double hut poke.",
             ""]
    parts += throughput_tables()
    parts += cpu_tables()
    parts += phase_tables()
    out = os.path.join(HERE, "summary.md")
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(parts))
    print("\n".join(parts))
    print(f"\nwritten: {out}")

if __name__ == "__main__":
    main()
