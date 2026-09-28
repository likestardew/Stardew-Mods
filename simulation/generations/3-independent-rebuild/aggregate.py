#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
sim2 聚合脚本 —— 正确的配对汇总。

要点（与旧模拟器 aggregate 的差异）：
  * CPU/吞吐的算法间比较**同时**给出：
      - RoM: ratio-of-means = (mean_A - mean_V) / mean_V     ← 主口径
      - MoD: mean-of-differences（逐 run 配对差值的均值，附 std 与配对 t 检验 p 值）
      - MoR: 逐 run 比值 (A/V) 的均值 —— 仅作警示列。当 V 的个别 run 接近 0 时
        该口径会产生爆炸比值（旧模拟器曾因此得到 +465% 的失真结论），
        本脚本显式打印它与 RoM/MoD 的差异并在符号不一致时给 WARNING。
  * 吞吐：mean±sd / median / DNF 计数 + 配对 t 检验（双侧，正态近似）。
用法： python aggregate.py [results.csv] [cpu_results.csv]
"""
import csv
import math
import sys
from collections import defaultdict


def load(path):
    rows = []
    with open(path, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            rows.append(r)
    return rows


def mean(xs):
    return sum(xs) / len(xs)


def sd(xs):
    if len(xs) < 2:
        return 0.0
    m = mean(xs)
    return math.sqrt(sum((x - m) ** 2 for x in xs) / (len(xs) - 1))


def median(xs):
    s = sorted(xs)
    n = len(s)
    return s[n // 2] if n % 2 else (s[n // 2 - 1] + s[n // 2]) / 2


def paired_t_p(a, b):
    """逐元素配对 t 检验，双侧 p 值用正态近似（n>=30 时足够）。"""
    d = [x - y for x, y in zip(a, b)]
    n = len(d)
    m = mean(d)
    s = sd(d)
    if s == 0:
        return (0.0 if m != 0 else 1.0), m
    t = m / (s / math.sqrt(n))
    p = 2 * (1 - 0.5 * (1 + math.erf(abs(t) / math.sqrt(2))))
    return p, m


def group(rows, key_fields, value_field, runs_excluded=()):
    g = defaultdict(dict)  # key -> run -> value
    for r in rows:
        if r.get("dnf", "False") == "True":
            continue
        key = tuple(r[k] for k in key_fields)
        run = int(r["run"])
        g[key][run] = float(r[value_field])
    return g


def paired_cells(g, base_algo):
    """返回 {(cell_fields): {algo: [(run, value)]}}，只保留基线算法也存在的 run。"""
    out = {}
    algos = sorted({k[0] for k in g})
    cells = sorted({k[1:] for k in g})
    for cell in cells:
        base = g.get((base_algo,) + cell, {})
        if not base:
            continue
        entry = {}
        for a in algos:
            gg = g.get((a,) + cell, {})
            pairs = [(run, v) for run, v in sorted(gg.items()) if run in base]
            if pairs:
                entry[a] = pairs
        out[cell] = entry
    return out


def fmt_p(p):
    return ".000" if p < 0.0005 else f"({p:.3f})"


def main():
    res_path = sys.argv[1] if len(sys.argv) > 1 else "results.csv"
    cpu_path = sys.argv[2] if len(sys.argv) > 2 else "cpu_results.csv"
    BASE = "vanilla_mod"

    print("=" * 100)
    print(f"吞吐（clear_ticks；基线 = {BASE}；配对 run）")
    print("=" * 100)
    rows = load(res_path)
    g = group(rows, ["algo", "radius", "junimos", "density"], "clear_ticks")
    cells = paired_cells(g, BASE)
    hdr = (f"{'radius':>6} {'jn':>3} {'dens':>5} | {'base mean±sd (med)':>26} | "
           f"{'algo':>13} {'mean±sd (med)':>26} {'ΔRoM':>8} {'ΔMoD':>9} {'p':>6} {'MoR':>8} {'DNF':>4}")
    print(hdr)
    print("-" * len(hdr))
    for cell, entry in sorted(cells.items(), key=lambda kv: (kv[0][2], kv[0][0], float(kv[0][1]))):
        radius, jn, dens = cell
        base_pairs = entry[BASE]
        bv = [v for _, v in base_pairs]
        b_mean, b_sd, b_med = mean(bv), sd(bv), median(bv)
        print(f"{radius:>6} {jn:>3} {dens:>5} | {b_mean:>9.0f}±{b_sd:<7.0f} ({b_med:>6.0f}) |")
        for a, pairs in entry.items():
            if a == BASE:
                continue
            av = [v for _, v in pairs]
            vv = [v for r, v in pairs]
            bb = [dict(base_pairs)[r] for r, _ in pairs]
            a_mean, a_sd, a_med = mean(av), sd(av), median(av)
            rom = (a_mean - b_mean) / b_mean * 100
            p, mod = paired_t_p(vv, bb)
            modpct = mod / b_mean * 100
            mor = mean([v / b if b > 0 else float("inf") for v, b in zip(vv, bb)])
            mor_pct = (mor - 1) * 100
            dnf = sum(1 for r in rows if r["algo"] == a and tuple(r[k] for k in ("radius", "junimos", "density")) == cell and r["dnf"] == "True")
            flag = " <WARN 符号不一致>" if (rom > 0) != (mod > 0) else ""
            print(f"{radius:>6} {jn:>3} {dens:>5} | {'':>26} | {a:>13} {a_mean:>9.0f}±{a_sd:<7.0f} ({a_med:>6.0f}) "
                  f"{rom:>+7.1f}% {modpct:>+7.1f}% {fmt_p(p):>6} {mor_pct:>+7.1f}% {dnf:>4}{flag}")
        print()

    print("=" * 100)
    print(f"CPU 日（total_pops / peak_pops_1s；基线 = {BASE}；配对 run）")
    print("=" * 100)
    rows = load(cpu_path)
    for field, label in (("total_pops", "总量"), ("peak_pops_1s", "峰值/1s")):
        g = group(rows, ["algo", "junimos", "obstacles"], field)
        cells = paired_cells(g, BASE)
        print(f"\n--- {label} ({field}) ---")
        hdr = (f"{'obs':>5} {'jn':>3} | {'base mean±sd':>18} | {'algo':>13} {'mean±sd':>18} "
               f"{'ΔRoM':>8} {'ΔMoD':>9} {'p':>6} {'MoR':>8}")
        print(hdr)
        print("-" * len(hdr))
        for cell, entry in sorted(cells.items(), key=lambda kv: (float(kv[0][1]), kv[0][0])):
            obs, jn = cell
            base_pairs = entry[BASE]
            bv = [v for _, v in base_pairs]
            b_mean, b_sd = mean(bv), sd(bv)
            print(f"{obs:>5} {jn:>3} | {b_mean:>9.0f}±{b_sd:<7.0f} |")
            for a, pairs in entry.items():
                if a == BASE:
                    continue
                vv = [v for _, v in pairs]
                bb = [dict(base_pairs)[r] for r, _ in pairs]
                a_mean, a_sd = mean(vv), sd(vv)
                rom = (a_mean - b_mean) / b_mean * 100
                p, mod = paired_t_p(vv, bb)
                modpct = mod / b_mean * 100
                mor = mean([v / b if b > 0 else float("inf") for v, b in zip(vv, bb)])
                mor_pct = (mor - 1) * 100
                flag = " <WARN 符号不一致>" if (rom > 0) != (mod > 0) else ""
                print(f"{obs:>5} {jn:>3} | {'':>18} | {a:>13} {a_mean:>9.0f}±{a_sd:<7.0f} "
                      f"{rom:>+7.1f}% {modpct:>+7.1f}% {fmt_p(p):>6} {mor_pct:>+7.1f}%{flag}")

    print("\n附：wasted_arrivals / respawns 汇总（吞吐）")
    rows = load(res_path)
    for field in ("wasted_arrivals", "respawns"):
        g = group(rows, ["algo", "radius", "junimos", "density"], field)
        agg = defaultdict(list)
        for (a, radius, jn, dens), runs in g.items():
            agg[(a, jn, dens)].append(mean(runs.values()))
        print(f"\n--- {field}（按 junimos×density 平均）---")
        print(f"{'jn':>3} {'dens':>5} | " + " ".join(f"{a:>13}" for a in sorted({k[0] for k in agg})))
        for (jn, dens) in sorted({(k[1], k[2]) for k in agg}, key=lambda t: (t[0], float(t[1]))):
            line = f"{jn:>3} {dens:>5} | "
            for a in sorted({k[0] for k in agg}):
                v = agg.get((a, jn, dens))
                line += f"{mean(v):>13.1f} " if v else f"{'—':>13} "
            print(line)


if __name__ == "__main__":
    main()
