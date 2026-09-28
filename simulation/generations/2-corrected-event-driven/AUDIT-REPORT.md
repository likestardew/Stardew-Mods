# BetterJunimos 性能模拟器审计报告（sim-audit1）

> 审计时间：2026-09-28 ｜ 审计对象：`verified-sim\Program.cs` 事件驱动模拟器及其产出（`final-results\`）
> 审计副本：`E:\betterjunimos-work\sim-audit1\`（所有修改仅在此目录）
> 地面真值：游戏 1.6.15 反编译 `/tmp/junimo.cs`（JunimoHarvester）、`/tmp/pfc.cs`（PathFindController）、`agent1-audit\junimohut.cs`（JunimoHut）；补丁后 mod 源码 `official\BetterJunimos\`（HEAD d94e38a，只读）；上游 3.2.0 用 `git show faa40d4:...`（只读）核对。
> 本轮接续了前一个代理（因并发超限中断）的半成品：复核了它对 Program.cs 的全部修改，另发现了它没有发现的 **verified-sim 三个状态机/统计缺陷**，其中一个直接推翻了旧报告的核心数字。

---

## 1. 交付物

| 文件 | 说明 |
|---|---|
| `sim-audit1\Program.cs` | 修正后模拟器（延续前代理副本）。带 `JUNIMOSIM_DIAG=1`（逐局状态分解）与 `JUNIMOSIM_TRACE`（单局事件跟踪）诊断开关，不影响 RNG 流 |
| `sim-audit1\aggregate.py` | 重写的聚合脚本：ratio-of-means 主口径 + mean-of-differences + 近零/退化图防护 |
| `sim-audit1\results.csv` | 干净重跑：吞吐 3600 局（3 算法 × 3 密度 × 4 数量 × 100 配对局），新增 `doomed_arrivals` 列 |
| `sim-audit1\cpu_results.csv` | 干净重跑：600 局日 CPU（3 算法 × 2 数量 × 2 障碍率 × 50 配对局），新增 `crops` 列 |
| `sim-audit1\summary.md` | 新汇总表 |
| 本文件 | 审计报告 |

前代理遗留的 `results.csv`/`cpu_results.csv`（中断前的部分数据，且为旧代码产物：cpu CSV 含 `v4b_gate_backoff` 行、无 `crops` 列）已按计划作废并被干净重跑覆盖。

---

## 2. 发现清单（总表）

| # | 问题 | 判定 | 影响 | 处置 |
|---|---|---|---|---|
| B1 | verified-sim 收获后未重置状态 → 幻影 `remaining--` → **提前退出**，legacy_arrival 清场时间被系统性低估 | **确认 bug（重大）** | 旧结论 2 的直接来源之一 | 副本已带防护 + 状态重置；见 §4.1 |
| B2 | verified-sim 死株到达分支不重置状态 → 重复死到达循环，虚增 wasted/决策 | 确认 bug | baseline 虚高 | 已修（前代理已修，复核确认） |
| B3 | 前代理副本 CpuBenchmark 基线取 `store.Get("legacy",...)`，但 "legacy" 已不在运行网格 → 空列表 `.Average()` 抛异常，CPU 汇总与 CSV 写出崩溃 | 确认 bug（前代理引入） | CPU 结果无法产出 | 已修：基线改为 `legacy_arrival`（与 final-results 聚合口径一致） |
| B4 | aggregate.py `paired_pct()` 用"逐局 (补丁−原版)/原版 再取均值"，近零基线产生爆炸比值 | 确认 bug | 旧 Table 4 的 25% 障碍行符号翻转 | 已重写聚合脚本，见 §4.2 |
| B5 | 认领 1800t 过期未建模（CropClaims.ExpiryTicks） | 确认建模缺失 | 长日局中 v4b 的陈旧认领永不释放 | 已修（F2） |
| B6 | 死株到达的 200ms 定时戳只在"站立"时结算；真实游戏在戳出行走后仍会在 200ms 到期时**中断行走**重新决策（junimo.cs:589 + PatchTryToHarvestHere 无条件设 `___harvestTimer=200`） | 确认建模缺失 | legacy_arrival CPU 略低估；补丁算法被 20/40t 上限挡住，无影响 | 已修（F1） |
| B7 | 出生后立即无条件决策；真实出生先走 1-2 格再 `tryToHarvestHere → pokeToHarvest`（70% 门） | 确认建模偏差（小） | 所有算法同向 | 已修 70% 门（F3）；1-2 格出生行走仍不建模（文档化） |
| B8 | CPU 近零局（legacy_arrival 独有，pops 1013~6380） | **不是模拟器把"卡死"建错了，而是 B1/B2 + 无条件重生 + 2t 重试综合产物**；修正后该异常消失 | 见 §5 | 无需再修 |
| B9 | 25% 障碍下约 5-10% 的随机地图小屋被完全围死（可达自由格=0，`crops=0`），三算法都全日 0 活动且进均值 | 确认（网格设计缺陷） | 拉大方差、制造 0/0 配对 | 聚合脚本排除并按格计数（`degen` 列） |
| C1 | 配置分叉（Densities 缺 0.10、网格含 legacy/v4b_gate_backoff、CPU 基线错位） | 前代理已修 | — | 复核确认，维持 |
| C2 | 邻居枚举顺序与 pfc.cs Directions 不一致（左右上下 vs 左右下上） | 确认（仅影响同优先级平局选择，统计无关） | 噪声级 | 已修（F4，保持逐字节一致） |

---

## 3. 前代理已做修改的逐项复核（对照反编译与源码）

前代理在副本中做了 7 类修改，**全部经地面真值核对，全部维持**：

1. **统一决策触发集（去掉补丁算法的 2t 重试）** — 正确。`junimo.cs:489-593`：决策只来自 (a) 到达 `tryToHarvestHere`、(b) `harvestTimer<=0 → pokeToHarvest()`（70% 门，:589）、(c) 小屋每 10 游戏分钟 poke（junimohut.cs:286-296，行走中也被 poke）、(d) 待机随机 0.2%/帧 × 1/6 → 直接 `pathfindToNewCrop`（**无 70% 门**，:626-651 case 4）。旧代码给补丁算法在每次转移失败后 2t 重试，是对同一触发集的双标。
2. **收获改为两阶段"首次过线者得"** — 正确。`HarvestCropsAbility.PerformAction` 对作物**不做任何事**（HarvestCropsAbility.cs:42-43 "the base junimo handles this"），实际收获发生在 1000ms 过线且以 `isHarvestable()` 为条件（junimo.cs:506-519）。后到者照样开一个注定落空的 2000ms 收获并站满 2 秒。旧"预订"模型（后到者立即重决策）消除了现实中存在的扎堆代价。
3. **真实 findPath 语义** — 正确。`pfc.cs:128-131`：endFunction 变体 endPoint==Point.Zero 被换成自身所在格；:194/:227 队列优先级 = g + manhattan(节点, **起点**)（不是 Dijkstra）；:228 子节点**入队即进 closed**；:200 endFunction 在每次出队时检查（含起点）；:231-234 预算按出队计数、第 limit 个出队仍做终点检查后失败。
4. **lkc 回退不校验作物仍在、路径失败则原地站立** — 正确。3.2.0 `PatchPathfindDoWork`（faa40d4）fallback 仅判 `!lkc.Equals(Point.Zero)`；路径为 null 的 controller 下一帧被丢弃（pfc.cs:150-153），祝尼魔站立等下一个触发。
5. **小屋扫描只在有空位时运行（60t 缓存共享）** — 正确。`JunimoSpawnHelper.TrySpawnJunimo`（3.2.0 即有）在满员时先于扫描返回（JunimoHutPatches.cs:203）；`PatchSearchAroundHut` 的 60t 缓存 3.2.0 已存在（faa40d4 JunimoHutPatches.cs:28）。
6. **wander 重试：3.2.0 = 6 次（retry<=5）、补丁版 = 2 次** — 正确（faa40d4 与 HEAD 的 `PatchPathfindToRandomSpotAroundHut`）。
7. **密度补 0.10、网格改为 final-results 三算法、legacy/v4b_gate_backoff 保留可编译但退出网格** — 正确。**与游戏一致的基线是 `legacy_arrival`**（3.2.0 决策逻辑 + 真实事件驱动节奏）；`legacy`（每 2t 70% poke、行走中每 10t 重规划）是被否证的旧节奏模型，与反编译不符，仅作历史基线保留。

未量化的次要复核：出生即决策（B7，已补 70% 门）、`EndPointInFarm` 的 hut+1 偏移（模拟器用 hut，±1 格，无统计影响）、returnToJunimoHut 的 25% 回家按即时消失建模（真实是走回小屋再消失，见 §7 近似清单）。

---

## 4. 本轮新确认的 bug：证据 → 判定 → 修复与量化

### 4.1 B1：verified-sim 收获后的"幻影 remaining--"提前退出（最重要发现）

**证据**（对 verified-sim 可编译副本插桩实测）：
- `verified-sim\Program.cs` Working 分支：`_crops.Remove(TargetTile); remaining--;` 无条件执行，然后 legacy_arrival 分支做 70% poke，**但只有在 poke 成功改变状态或显式重置时才离开 Working**；70% 失败路径不重置 `State`（`if (next.State == WaitingDecision)` 恒为假，因为状态还是 Working），`NextEventTick` 却被设成了新的调度 → 下一个事件仍是 Working 分支 → `_crops.Remove` 落空但 **`remaining--` 再次执行**。
- 事件跟踪（单祝尼魔局）：连续 Working 事件间隔 0/2/44/62/70/80/84 tick——一个祝尼魔在 120t 收获计时下不可能做到；同 tick 两次 Working 事件、作物计数只减一次而 `remaining` 减两次。
- 物理界限检验：verified-sim d=1.00、jn=2 的 legacy_arrival 平均 665.96s = 39958t，而 742 株 × 120t = 89040 junimo-t > 2 × 39958 = 79916——**比它自己模型的理论下限还快 10%**；jn=1 探针 742 株仅 1319s（下限 1484s）同样违反。

**判定**：确认 bug。`remaining` 被"已收获 + 幻影"共同消耗，`while (... remaining > 0)` 提前退出，`clear_ticks` 记录的是**未清完的地图**上的时间。每株收获后 30% 概率进入幻影链（期望 ≈ 0.43 次），即基线约 30% 的"清场工作量"从未发生。

**修复**：副本 Working 分支为 `if (_crops.Remove(next.TargetTile)) remaining--;`，且进入 poke 前先 `State = WaitingDecision`（前代理已写对，本轮复核+保留）。

**量化影响**（同规模 n=30/100 对照）：
| d=1.00, jn=2 | verified-sim（bug 版） | 修正后 |
|---|---|---|
| legacy_arrival 清场 | 666s（n=30；final-results n=100 为 649.4s） | **1689.4s**（n=100） |
| 每株每祝尼魔耗时 | 108 junimo-t（违反 ≥120t 下限） | 273 junimo-t（≥120t ✓） |

### 4.2 B4：聚合统计缺陷

**证据**：对 final-results 自己的 `cpu_results.csv` 用两种口径重算——
| 格 | 旧口径 MoR（旧 summary.md 报的） | ratio-of-means（正确） |
|---|---|---|
| obs 0.25, jn=2 | cap +10.3%、v4b +28.2% | cap **−28.1%**、v4b **−8.5%** |
| obs 0.25, jn=6 | cap +25.5%、v4b +35.8% | cap **−39.0%**、v4b **−27.2%** |

即：**旧数据本身也支持"补丁全面省 CPU"，旧 Table 4 的"+10~+36% 更差"纯粹是统计口径产物**——该格有 3-4/50 个 legacy_arrival 近零局（1013~6380 pops），逐局比值 +465%~+513% 主导了均值。吞吐侧无近零基线，ROM≈MoR（+50.2 vs +50.4 等），统计口径不是吞吐结论的问题。

**判定**：确认 bug（统计层）。

**修复**（新 aggregate.py）：
- **主口径 = ratio-of-means**（Σ补丁/Σ基线−1）：对近零基线稳健（近零局按真实量级参与，不产生爆炸比值），且回答的是实际关心的问题（总 CPU/总工期之比）；
- **副口径 = mean-of-differences**（逐局差值均值 + 配对 t 检验 p 值）；
- 保留 MoR 仅作诊断列；基线为 0 的对被排除并打印排除数；
- **退化图防护**：`crops=0`（小屋被围死、无任何可能工作）的对从两侧剔除，每格报告 `degen` 数。

### 4.3 B5/B6/B7（建模补齐）与量化

- **F2 认领过期（B5）**：按 CropClaims.cs:32-56 补 `IsClaimedByOther` 读时过期丢弃 + `TryClaim` 过期清理，1800t。影响：v4b 在长日局中陈旧认领（尤其已消失祝尼魔留下的）不再永久封格。
- **F1 定时戳中断行走（B6）**：死株到达 → 立即 poke（70%）+ **无论成败**武装 +12t 的第二次 poke；第二次 poke 在行走态到达时按当前位置重决策（补丁算法被 20/40t 上限挡住 → 实际只影响 legacy_arrival，与真实一致）。影响：legacy_arrival 日 CPU 增加约 5-10% 的决策（更多次近距离搜索，多数成功）。
- **F3 出生 70% 门（B7）**：出生决策与真实 `pokeToHarvest` 一样 70% 门控；失败则进入待机随机时钟。影响：所有算法同向、幅度 <1%。
- **F4 邻居顺序（C2）**：改为 pfc.cs 的 左、右、下、上。仅平局选择变化。

以上三项合并的净效果已包含在最终数据中；单项 A/B（以 no-pinning 开关为代表）显示扎堆钉人（doomed 收获）本身只占清场时间 ~1-3%，**不是**吞吐修正的主因——主因是 B1（基线早退）与 2t 重试双标。

---

## 5. CPU"近零局"机制的判定（任务线索 1）

**现象**：final-results `cpu_results.csv` 中 legacy_arrival 在 obs=0.25 有 4-6 局 pops=1013~6380，同种子 cap_only 为 5952~74629（差 3~60 倍）。

**判定：不是"模拟器把原版卡死建错了"，也不是补丁算法多算了事件——是 verified-sim 特有的三个缺陷 + 一个真实机制的叠加，修正后异常消失：**
1. verified-sim 的重生**无条件**（despawn 60t 后必回），而真实（原版游戏 junimohut.cs:196 与 3.2.0 mod JunimoSpawnHelper:222 都一样）重生必须**小屋扫描发现有工作**；空窗期（两波作物之间 ~8000t）真实祝尼魔回家后就地待命，0 CPU。
2. B2 的死株循环让 legacy_arrival 在空窗期反复烧 70% 决策与 25% 回家掷骰，而 cap_only 被 60t 缓存的扫描门 + 40t 退避挡住——两者的空窗行为被人为拉开。
3. B1 的幻影递减进一步压低/扰乱基线绝对值。
4. 真实存在的差异只留下一条：**扫描门 + 决策上限本来就是补丁的一部分**——空图上 cap_only 每次失败决策 ~200 pops（2 次 wander A*），legacy_arrival ~700 pops（nearest + 50% lkc + 6 次 wander），方向与修正后数据一致（修正后 obs=0.25, jn=2：legacy 最低 ~20.7k、cap 最低 ~12.2k，同量级、无异常）。

另外确认了一类**三算法共同**的近零局：25% 障碍下 5-10% 的随机图小屋被围死（可达自由格 0 → 无作物 → 无出生 → 全日 0 活动）。这是网格的退化样本而非 bug，聚合时剔除并计数。

---

## 6. 修复后干净重跑结果（n=100 吞吐 / n=50 CPU，与 final-results 同规模）

### 吞吐（清场时间，越低越好；Δ% = 补丁相对 legacy_arrival 的 ratio-of-means）

| 密度 | jn | legacy_arrival | cap_only Δ% | v4b_realfilter Δ% |
|---|---|---|---|---|
| 0.10 | 2 | 272.9s | −5.5% | **−10.7%** |
| 0.10 | 8 | 93.2s | +1.8% (ns) | **−18.5%** |
| 0.75 | 2 | 1320.4s | −0.6% | −1.4% |
| 0.75 | 8 | 372.4s | −0.2% | **−9.9%** |
| 1.00 | 2 | 1689.4s | −0.7% | −2.1% |
| 1.00 | 8 | 463.4s | −0.7% | **−8.6%** |

- cap_only（限速+门控，无认领过滤）：与原版**持平**（−5.5%~+1.8%，多数格不显著）。
- v4b_realfilter（完整补丁）：**持平到更快**（−1.4%~−18.5%，全部显著）。
- 防扎堆指标（语义修正后拆成两半）：
  - 死株到达 deadArrive：legacy 18.3~42.5 → v4b 8.1~19.1（约 **2 倍**改善）；
  - **doomed（扎堆火车跟随者：到达时作物还在、但过线时已被别人收走，站满 2s 一无所获）**：legacy/cap 4.2~65.1 → v4b **0.5~3.1**（约 **10~25 倍**改善）。
  - 合并口径（旧"wasted_arrivals"的语义）约 5~15 倍改善——旧结论 4 的方向维持、口径修正。

### 日 CPU（A* 节点展开/天，36000t，4 波作物）

| 格 | legacy pops/day | cap_only Δ% | v4b Δ% | legacy peak/1s | cap_only peak Δ% | v4b peak Δ% |
|---|---|---|---|---|---|---|
| 0.05, jn=2 | 33768 | **−42.0%** | −17.0% | 1133 | −54.2% | −35.8% |
| 0.05, jn=6 | 63229 | **−41.5%** | −13.5% | 1678 | −34.7% | −1.5% (ns) |
| 0.25, jn=2 | 33029 | **−40.6%** | −17.2% | 1252 | −55.5% | −37.9% |
| 0.25, jn=6 | 64931 | **−38.1%** | −7.4% | 1959 | −39.9% | −11.1% |

- 全部格子、全部指标：补丁**只省不亏**，且 p 值除一格（v4b peak 0.05/6）外全部显著。
- 与 final-results 方向对比：cap_only（−28~−48% → −38~−42%）一致；v4b 由"+10~+36% 更差"（统计 artifact）或"−8~−38%（正确口径下的旧数据）"修正为 **−7~−17%**；peak 由"v4b +2~+22% 更差"修正为 **−1.5~−37.9%**（模型修正后 v4b 最差 1 秒窗口不再高于原版）。
- 绝对值比 final-results 低约 35-40%（如 legacy 0.05/2：57048 → 33768）：去掉了 B2 的重复死到达循环、无条件重生扰动与出生无条件决策，属向真实的收敛；比值指标才是交付物。

### 与 final-results 的总趋势对比

| 结论维度 | final-results（旧） | 本轮（修正后） | 定性 |
|---|---|---|---|
| 清场：补丁 vs 原版 | **+27~+53% 更慢** | **−5.5%~−0.2%（cap_only）/ −1.4%~−18.5%（v4b）** | **被推翻** |
| 清场：去限速（nocap） | ≈ shipped | 未在修正模型下重测（无 nocap 变体） | 不确定，需重跑 |
| CPU 稳态 | 0.62~0.89× | **0.58~0.93×** | 维持（数字略好） |
| CPU 25% 障碍 | "补丁更差 +10~+36%" | **−7~−17%** | 被推翻（统计口径修正后旧数据亦如此） |
| 峰值 1s | 与原版持平（0.99~1.05×）；去限速 1.37~1.46× | **补丁 ≤ 原版（0.45~0.99×）** | 改善（限速价值仍在， nocap 未重测） |
| 防扎堆 ~10× | wasted 280-444 → 15-44 | deadArrive ~2×、doomed ~10-25×（口径拆分） | 维持（口径修正） |

---

## 7. 仍存在的建模近似（本轮未改，判定"可接受/无法判定"，列出供后续）

1. **25% 回家 = 即时消失+60t 重生**：真实是 `returnToJunimoHut`（limit=10000 的 A*）走回小屋再消失。方向：所有算法同向；原版失败决策更多，受影响略大。未建模。
2. **出生 1-2 格 ctor 行走**不建模（已建模其 70% 决策门）。
3. **CPU 天窗 36000t（6:00-20:00）**：真实决策活跃窗口为 6:00-19:00（timeOfDay>1900 后 `pathfindToNewCrop` 直接回家，junimo.cs:369-376；19:00 后不再出生）。修正后所有算法同窗，比值近似无偏；但 19:00 后原版/补丁的一次性回家 A*（limit 10000）未建模。维持 36000t 是为与 final-results 可比。
4. **`PFC.update` 的 5 秒卡死取消、祝尼魔互相碰撞**（spawn 时 collidesWithOtherCharacters=true 之后设回 false，正常行走不碰撞）未建模——前者在开放农田罕见，后者与游戏一致。
5. **收获过线后"输家的认领"由下一次决策释放**（真实还受 1800t 过期兜底，已建模）；同一 tick 多事件顺序、.NET PriorityQueue 的非稳定堆 vs 模拟器 seq 平局——噪声级。
6. **温室/灌木/浇水/施肥/冬天/下雨/工资/下班**：不在基准地图语义内（与 final-results 相同），仅收获语义。
7. `JUNIMOSIM_DIAG/TRACE` 会输出到控制台但不触碰 RNG（诊断与正式跑互不影响）；跨进程种子随机化 → 与 final-results 逐局数字不可比，趋势与量级可比。

---

## 8. 对 PROJECT_STATE.md 关键结论 1-7 的影响评估

| # | 旧结论 | 评估 | 说明 |
|---|---|---|---|
| 1 | 决策是事件驱动（≈0.3-1 次/秒/只），不是每帧 42 次 | **不变（复核确认）** | 与 junimo.cs/junimohut.cs 逐条对上 |
| 2 | 补丁版清场比原版慢 27~53%；去限速救不回来 | **被推翻** | 基线数字受 B1（幻影 remaining-- 提早退出，基线约 30% 工作量未做即"清完"）、B2（死株循环）与 2t 重试双标污染。修正后：cap_only 持平（−5.5%~+1.8%）、v4b 持平到更快（−1.4%~−18.5%，稀疏田最高 −18.5%）。认领过滤的"舍近求远"代价被"避免扎堆钉人 2s"的收益抵消。"nocap ≈ shipped"在修正模型下未重测，暂不可引用 |
| 3 | 日稳态 CPU 0.62~0.89×；饱和期 2~3×；峰值 0.99~1.05× | **改口径后维持并变好** | 稳态 0.58~0.93×（全格显著）；"25% 障碍更差 +10~+36%"系统计 artifact，旧数据正确口径下本就是 −8~−39%；峰值 0.44~0.98×。"饱和收获期 2~3×"本轮未单独复测（旧口径产物，暂按存疑处理） |
| 4 | 防扎堆 ~10 倍 | **维持，口径修正** | 拆分为 deadArrive ~2× + doomed（火车跟随者）~10-25×；合并 ~5-15× |
| 5 | 已证伪路线（v2b/v3/v1） | 不变 | 本轮未重跑历史变体 |
| 6 | mod 侧无害小修复 | 不变 | |
| 7 | issue/fork 状态 | 不变 | 与模拟器无关 |

**对"游戏内 DLL 取舍"的影响**：旧的取舍依据（"保留补丁 = 慢 27-53% 换 CPU"）不再成立——修正模型下补丁**清场无代价、CPU 全面更省、峰值不升**；`realfilter_nocap` 的旧对照（"与 shipped 几乎相同、峰值更高"）建立在被污染的基线上，若用户要重做 DLL 决策，应先用修正后模拟器重跑 nocap 变体（需把 `realfilter_nocap` 变体加回网格，本轮未做）。

---

## 9. 复现方法

```
cd E:\betterjunimos-work\sim-audit1
set JUNIMOSIM_RUNS=100        # 吞吐配对局数（默认 20）
set JUNIMOSIM_CPU_RUNS=50     # CPU 配对局数（默认 5）
dotnet run -c Release         # ~4 秒（16 线程）
python aggregate.py           # 生成 summary.md
# 诊断（不影响结果）：
set JUNIMOSIM_DIAG=1                                # 每局状态分解（CPU 局）
set JUNIMOSIM_TRACE=0.25,2,0                        # 跟踪 obs=0.25 jn=2 run=0 的逐事件
```
验收锚点（n=100/50，跨进程允许随机差异）：legacy d1.00 jn=2 ≈ 1690s；cap_only 相对 −5%~+2%；v4b 相对 −1%~−19%；CPU cap_only −38~−42%、v4b −7~−18%。
