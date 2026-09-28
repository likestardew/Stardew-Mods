#!/usr/bin/env python3
"""Aggregation for the junimo work-finding benchmark, v5 edition (stdlib only).

Extends the sim-audit1 aggregate (ratio-of-means PRIMARY + paired t SECONDARY,
degenerate-map guards) with:
  - generic N-algorithm comparison (v5 parameter variants are first-class algos);
  - hut-count aware cells (huts=1 single-hut grid, huts=2 overlap scenario);
  - clearing-phase statistics: per-phase durations and A* pops from the
    cumulative ph{1..4}_t / ph{1..4}_p columns (25/50/75/100% cleared), which
    localize the "stutter spikes" (pops rate per phase) and "endgame efficiency"
    (phase-4 duration/rate) complaints;
  - peak60: worst 60-tick (1s) A* window over the whole clearing run.

Usage: python aggregate.py [results.csv [cpu_results.csv]]
       (defaults to the files written by the last full benchmark run)
"""
import csv, math, os, sys
from statistics import mean, median

HERE = os.path.dirname(os.path.abspath(__file__))
SIM_CAP_S = 240000 / 60.0

def sd(xs):
    m = mean(xs)
    return math.sqrt(sum((x - m) ** 2 for x in xs) / len(xs)) if len(xs) > 1 else 0.0

def p_from_t(t):
    if t == 0:
        return 1.0
    z = abs(t)
    p = 2.0 * (1.0 - 0.5 * (1.0 + math.erf(z / math.sqrt(2.0))))
    return max(p, 1e-16)

def paired_stats(algo_vals, base_vals):
    n = len(algo_vals)
    d = [a - b for a, b in zip(algo_vals, base_vals)]
    mb, ma = mean(base_vals), mean(algo_vals)
    rom = (ma / mb - 1.0) * 100.0 if mb != 0 else float("nan")
    s = sd(d)
    t = mean(d) / (s / math.sqrt(n)) if s > 0 else (0.0 if mean(d) == 0 else math.copysign(1e9, mean(d)))
    return dict(rom=rom, mod=mean(d), mod_pct=mean(d) / mb * 100.0 if mb else float("nan"),
                t=t, p=p_from_t(t), n=n)

def fmt_p(p):
    return "n/a" if p != p else (".000" if p < 5e-4 else f"{p:.3f}")

def num(rs, key, only_done=False):
    xs = [float(r[key]) for r in rs if not (only_done and r["dnf"])]
    return mean(xs) if xs else float("nan")

def load_results(path):
    rows = []
    with open(path, newline="") as f:
        for r in csv.DictReader(f):
            rows.append(dict(
                algo=r["algo"], huts=int(r["huts"]), junimos=int(r["junimos"]),
                density=float(r["density"]), run=int(r["run"]),
                clear_s=int(r["clear_ticks"]) / 60.0,
                crops=int(r["crops"]), expansions=int(r["expansions"]),
                wasted=int(r["wasted_arrivals"]), doomed=int(r["doomed_arrivals"]),
                respawns=int(r["respawns"]), peak60=int(r["peak60"]),
                dnf=r["dnf"].strip().lower() == "true",
                # cumulative phase ticks/pops (-1 = unreached, i.e. DNF)
                ph_t=[int(r[f"ph{i}_t"]) for i in (1, 2, 3, 4)],
                ph_p=[int(r[f"ph{i}_p"]) for i in (1, 2, 3, 4)],
                shadow=int(r["shadow_searches"]), thr=int(r["throttle_hits"]),
                det=int(r["detour_ignored"]), foff=int(r["filter_off"]),
                h0=int(r["hut0_pops"]), h1=int(r["hut1_pops"]),
                overlap=int(r["overlap_harvests"]),
            ))
    return rows

def phase_deltas(rs):
    """per-phase durations (seconds) and pops, mean over runs; DNF runs excluded."""
    done = [r for r in rs if not r["dnf"] and r["ph_t"][3] >= 0]
    if not done:
        return None
    dt, dp = [], []
    for i in range(4):
        dt.append(mean((r["ph_t"][i] - (r["ph_t"][i - 1] if i else 0)) for r in done) / 60.0)
        dp.append(mean((r["ph_p"][i] - (r["ph_p"][i - 1] if i else 0)) for r in done))
    return dt, dp, len(done)

def throughput_table(rows, huts, base="legacy_arrival"):
    lines = []
    sub = [r for r in rows if r["huts"] == huts]
    if not sub:
        return lines
    huts_note = "single hut" if huts == 1 else "TWO HUTS (overlap boxes)"
    lines += [f"### Throughput — {huts_note}", "",
              "Δ% = ratio of means vs legacy_arrival (PRIMARY), (p) paired t. clear = mean clearing seconds.",
              "phase rows: per-quartile mean duration in seconds (pops = A* expansions in that quartile).",
              ""]
    algos = sorted({r["algo"] for r in sub}, key=lambda a: (a != "legacy_arrival", a != "cap_only", a != "v4b_realfilter", a))
    for density in sorted({r["density"] for r in sub}):
        dsub = [r for r in sub if r["density"] == density]
        lines.append(f"#### density {density:g}  ({mean(r['crops'] for r in dsub):.0f} crops/map avg)" + ("  [degenerate maps excluded per cell]" if False else ""))
        lines.append("")
        lines.append("| jn/hut | " + " | ".join(f"{a}" for a in algos) + " |")
        lines.append("|---|" + "---|" * len(algos))
        for jn in sorted({r["junimos"] for r in dsub}):
            jsub = {a: [r for r in dsub if r["algo"] == a and r["junimos"] == jn and r["crops"] > 0] for a in algos}
            base_runs = {r["run"]: r for r in jsub.get(base, [])}
            cells = []
            for a in algos:
                rs = jsub.get(a, [])
                if not rs:
                    cells.append("MISSING")
                    continue
                done = [r for r in rs if not r["dnf"]]
                m = mean(r["clear_s"] for r in done) if done else SIM_CAP_S
                s = f"{m:.1f}±{sd([r['clear_s'] for r in done]) if done else 0:.1f}"
                dnf = len(rs) - len(done)
                if dnf:
                    s += f" DNF={dnf}"
                if a != base:
                    brs = {r["run"]: r for r in rs}
                    shared = sorted(set(base_runs) & set(brs))
                    if shared:
                        st = paired_stats([brs[k]["clear_s"] for k in shared], [base_runs[k]["clear_s"] for k in shared])
                        s += f" **{st['rom']:+.1f}%** ({fmt_p(st['p'])})"
                cells.append(s)
            lines.append(f"| {jn} | " + " | ".join(cells) + " |")
        # phase + mechanism table for this density
        lines.append("")
        lines.append("| jn/hut | algo | p1 s (pops) | p2 s (pops) | p3 s (pops) | p4 s (pops) | pops/crop p1-2 | doomed | deadArrive | peak60 | shadow | thr | filt_off |")
        lines.append("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
        for jn in sorted({r["junimos"] for r in dsub}):
            for a in algos:
                rs = [r for r in dsub if r["algo"] == a and r["junimos"] == jn and r["crops"] > 0]
                ph = phase_deltas(rs)
                if ph is None:
                    continue
                dt, dp, n_done = ph
                crops = mean(r["crops"] for r in rs)
                p12_per_crop = (dp[0] + dp[1]) / (crops / 2) if crops else float("nan")
                lines.append(
                    f"| {jn} | {a} | {dt[0]:.1f} ({dp[0]:.0f}) | {dt[1]:.1f} ({dp[1]:.0f}) | "
                    f"{dt[2]:.1f} ({dp[2]:.0f}) | {dt[3]:.1f} ({dp[3]:.0f}) | {p12_per_crop:.2f} | "
                    f"{num(rs,'doomed'):.1f} | {num(rs,'wasted'):.1f} | {num(rs,'peak60'):.0f} | "
                    f"{num(rs,'shadow'):.1f} | {num(rs,'thr'):.1f} | {num(rs,'foff'):.1f} |")
        lines.append("")
    return lines

def load_cpu(path):
    rows = []
    with open(path, newline="") as f:
        for r in csv.DictReader(f):
            rows.append(dict(algo=r["algo"], huts=int(r["huts"]), junimos=int(r["junimos"]),
                             obs=float(r["obstacles"]), run=int(r["run"]),
                             pops=int(r["total_pops"]), peak=int(r["peak_pops_1s"]),
                             crops=int(r["crops"]), h0=int(r["hut0_pops"]), h1=int(r["hut1_pops"])))
    return rows

def cpu_table(rows, huts, base="legacy_arrival"):
    lines = []
    sub = [r for r in rows if r["huts"] == huts]
    if not sub:
        return lines
    huts_note = "single hut" if huts == 1 else "TWO HUTS (overlap boxes)"
    lines += [f"### CPU per day (36000 ticks, 4 crop waves) — {huts_note}", "",
              "Δ% = ratio of means vs legacy_arrival (PRIMARY); (p) paired t. pops = A* node expansions/day.", ""]
    algos = sorted({r["algo"] for r in sub}, key=lambda a: (a != "legacy_arrival", a != "cap_only", a != "v4b_realfilter", a))
    lines.append("| obstacles | jn/hut | " + " | ".join(f"{a} pops Δ% (p)" for a in algos if a != base) + f" | {base} pops | peak: " + " / ".join(a for a in algos) + " |")
    lines.append("|---|---|" + "---|" * len([a for a in algos if a != base]) + "---|---|")
    for obs in sorted({r["obs"] for r in sub}):
        for jn in sorted({r["junimos"] for r in sub}):
            jsub = {a: {r["run"]: r for r in sub if r["algo"] == a and r["junimos"] == jn and r["obs"] == obs and r["crops"] != 0}
                    for a in algos}
            base_runs = jsub.get(base, {})
            if not base_runs:
                continue
            cells, peaks = {}, {}
            for a in algos:
                ars = jsub.get(a, {})
                shared = sorted(set(base_runs) & set(ars))
                if not shared:
                    cells[a] = "MISSING"
                    peaks[a] = "-"
                    continue
                if a == base:
                    cells[a] = f"{mean(ars[k]['pops'] for k in shared):.0f}"
                else:
                    st = paired_stats([ars[k]["pops"] for k in shared], [base_runs[k]["pops"] for k in shared])
                    cells[a] = f"{st['rom']:+.1f}% ({fmt_p(st['p'])})"
                peaks[a] = f"{mean(ars[k]['peak'] for k in shared):.0f}"
            others = [a for a in algos if a != base]
            lines.append(f"| {obs:g} | {jn} | " + " | ".join(cells[a] for a in others) +
                         f" | {cells[base]} | " + " / ".join(peaks[a] for a in algos) + " |")
    lines.append("")
    return lines

def main():
    res_path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "results.csv")
    cpu_path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, "cpu_results.csv")
    parts = ["# Junimo v5 benchmark — corrected simulator + double poke + WRF/WFl acceleration", "",
             f"Simulator: sim-v5/Program.cs (sim-audit1 model + double hut poke + WorkFaster/WorkRidiculouslyFast",
             f"modeling + clearing-phase stats + two-hut overlap scenario + v5 fixes). Baseline: legacy_arrival.", ""]
    if os.path.exists(res_path):
        rows = load_results(res_path)
        for huts in sorted({r["huts"] for r in rows}):
            parts += throughput_table(rows, huts)
    if os.path.exists(cpu_path):
        crows = load_cpu(cpu_path)
        for huts in sorted({r["huts"] for r in crows}):
            parts += cpu_table(crows, huts)
    out = os.path.join(HERE, "summary.md")
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(parts))
    print("\n".join(parts))
    print(f"\nwritten: {out}")

if __name__ == "__main__":
    main()
