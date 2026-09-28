# sim2 — Better Junimos 性能补丁独立交叉验证报告

> 本目录（`E:\betterjunimos-work\sim2\`）是一个**从零重建**的独立事件驱动模拟器，用于交叉验证
> `final-results\summary.md`（旧模拟器）的结论。实现过程中**未阅读**旧模拟器（verified-sim / sim /
> nocap-test）的任何代码；旧模拟器的**参数取值**（地图尺寸、radius、密度、波次时刻等）按任务允许
> 被参考用于可比性，核心逻辑（决策循环、A*、认领、限速）全部从反编译/源码证据独立推导。
>
 - 运行方式：`dotnet run -c Release`（约 9 秒跑完全部基准）；聚合：`python aggregate.py`。
 - 数据文件：`results.csv`（2400 局吞吐）、`cpu_results.csv`（480 局日 CPU）、`aggregate_output.txt`。

---

## 第一章 行为规格（三种算法的真实行为，附证据出处）

证据缩写：
- **[V1]** `/tmp/junimo.cs`（1.6.15 `JunimoHarvester` 反编译），`[V1:286]` = 第 286 行
- **[V2]** `/tmp/pfc.cs`（1.6.15 `PathFindController` 反编译）
- **[V3]** `agent1-audit\junimohut.cs`（1.6.15 `JunimoHut` 反编译）
- **[M1]** `git -C official show faa40d4:BetterJunimos/Patches/JunimoHarvesterPatches.cs`（mod 3.2.0 上游，只读）
- **[M2]** 同仓库 `BetterJunimos/Patches/JunimoHutPatches.cs`
- **[M3]** 同仓库 `BetterJunimos/Abilities/JunimoAbilities.cs`（含 `Abilities/Base/*.cs`）
- **[P1]** `E:\betterjunimos-work\betterjunimos-3.2.0-perffix-claimfilter.patch`（AI 补丁 diff）

已确认 `faa40d4`（上游 3.2.0，2026-09-23 merge #113）是补丁提交 `6dd0505` 的**父提交**，
即上表 [M1]-[M3] 就是纯净的 3.2.0；60t 扫描缓存、生成节流等均为 3.2.0 自带（非补丁添加）。

### 1.1 游戏原版（1.6.15）JunimoHarvester 的决策机制

**决策触发时机（事件驱动，共 4 类）：**

1. **路径到达**：`PathFindController.moveCharacter` 走完路径后调用 `endBehaviorFunction` =
   `reachFirstDestinationFromHut` → `tryToHarvestHere()` [V1:275-293, V2:270-284]。
2. **收获计时归零**：`update()` 中 `if (harvestTimer > 0)` 倒计时（按真实毫秒），越过 0 的那一帧
   `pokeToHarvest()` [V1:489-592]。
3. **小屋 10 游戏分钟同步**：`JunimoHut.performTenMinuteAction` 对每只在场祝尼魔 `pokeToHarvest()`
   [V3:280-296]。10 游戏分钟 ≈ 430 tick。
4. **空闲随机**：无控制器且计时 ≤0 时，每帧 `p=0.002` 触发 6 选 1 随机行为，其中 case 4 直接调
   `pathfindToNewCrop()`（**不经 0.7 门**）[V1:626-651]。期望间隔 ≈ 3000 tick。

**pokeToHarvest 的 0.7 门**：`harvestTimer <= 0 && Game1.random.NextDouble() < 0.7` 才会
`pathfindToNewCrop()`；站在不可通行格则销毁 [V1:295-309]。**注意 0.7 门挂在 pokeToHarvest 上，
而不是挂在 pathfindToNewCrop 上**——空闲随机(case 4)与"到达无事"分支内部再调 poke 的路径都各有一道或
绕过这道门。

**harvestTimer 真实取值**：到达可收获格时 `harvestTimer = 2000`（毫秒）[V1:286]；倒计时跨过
1000ms 的那一帧真正收获并 `destroyCrop` [V1:506-520]；跨过 0 才 poke。即 **120 tick 周期中前 60t
是收获、后 60t 是纯站立的摆动动画窗口**。站在原格期间不移动、不决策。

**寻路预算与失败链**：所有 PathFindController `limit=100` [V1:118,382,388,721]。`findPath` 的关键细节
[V2:180-246]：终点判定在**出队时**（含起点格，起点即命中则 0 次扩展）；priority = (父g+1) +
曼哈顿启发；**closed-on-insert**（每格至多入队一次，g 取首见值）；`num` 统计"出队并扩展"次数，
≥100 即失败。作物搜索（endFunction 型）构造时 `endPoint=Point.Zero` → 折算为**起点格** [V1:382]，
故其启发函数 = 到起点的曼哈顿距离——出队顺序近似"由近及远"，先命中的 actionable 格即目标。

**pathfindToNewCrop 失败链** [V1:362-404]：搜索成功且终点在盒内 → 走；否则依次：
50% 且 `lastKnownCropLocation ≠ Zero` → 对 lkc 再搜一次（**无半径复查**）；
否则 25% → 回小屋（路径为 null 立即销毁 [V1:421]；到家 `junimoReachedHut` → 销毁 [V1:338]）；
否则 → `pathfindToRandomSpotAroundHut()`（**单次**构造，无重试 [V1:716-723]）。
1900 后只回家 [V1:369-376]。

**半径判定**：`|终点 - (tileX+1, tileY+1)|` 任一轴 > `cropHarvestRadius`（默认 8，[V3:20]）即视为
失败 [V1:384]。注意这是**事后盒判定**，搜索本身不限盒。

**作物目标怎么选**：A* 出队序下**第一个满足 `foundCropEndFunction` 的格**（成熟作物或可收灌木）
[V1:346-360]。多只祝尼魔从同一位置搜索会得到同一目标（先到先收，后到者到达时作物还在/已没，
见第二章"扎堆空等"）。

### 1.2 模组原版 Better Junimos 3.2.0

**mod 如何替换原版决策**（全部为 Harmony 补丁，事件骨架不变）：

- `PatchFindingCropEnd` **完全替换** foundCropEndFunction → `IsActionable()` [M1]。能力注册顺序为
  浇水→施肥→种植→收作物→收灌木→收牧草→清枯株→温室 [M3:RegisterDefaultAbilities]，即
  **IsActionable = 第一个可用能力存在**。本模拟场景（只有成熟作物）下等价于 readyForHarvest。
- `PatchTryToHarvestHere` 替换到达逻辑 [M1]：灌木/收作物 → `time=2000`（作物实际移除仍由原版
  update 在 1000ms crossing 完成——`HarvestCropsAbility.PerformAction` 本身是 no-op，[M3]）；
  其他能力成功 `time=998`（Progression.WorkFaster 时 300，默认未解锁为 false）；无事可做
  `time=200` 且**当场再 poke 一次**（0.7 门）。失败动作进 1 游戏小时冷却 [M3:ActionFailed]。
- `PatchPathfindDoWork` 完全替换 pathfindToNewCrop [M1]：下班时间（CanWorkInEvenings 默认
  **true** → 2400）；工资（WorkForWages 默认 true，WereJunimosPaidToday 默认 false——由日结/付费
  逻辑驱动，本模拟未建模）；`hut.noHarvest || rand<0.035` → 闲逛；否则主搜索（limit 100）+
  出圈判定 + 同款 lkc/回家/闲逛失败链。
- **闲逛被 mod 放大**：`PatchPathfindToRandomSpotAroundHut` postfix 在原版本体 1 次构造之外，
  `do{...}while(retry<=5 && path==null)` 再构造 **至多 6 次**（每次都是完整 A*）[M1]。即 mod 下每次
  闲逛 = 1 + (1..6) 次搜索；原版本体那次的结果总是被 postfix 覆盖。
- **扫描缓存是 3.2.0 自带**：`PatchSearchAroundHut` 完全替换 `areThereMatureCropsWithinRadius`，
  按小屋缓存 60t（`ScanCooldownTicks=60`）；miss 时行主序（x 外 y 内）扫 radius 盒，
  **首个 actionable 格写入 `hut.lastKnownCropLocation` 与 `lastKnownCropLocations[(hut,farm)]`**，
  全空则 lkc=Zero [M2]。3.2.0 中该扫描只被**生成检查**（`JunimoSpawnHelper.TrySpawnJunimo`：
  每 60t 一只、上限 MaxJunimos、须扫描有活）调用，**不在决策路径上**。
- **双重 poke（本次调研新发现）**：`ReplaceJunimoTimerNumber` 是 `performTenMinuteAction` 的
  **postfix**，在原版本体（已 poke 一轮）之后**再 poke 全部 myJunimos 一轮** [M2]。即 mod 下每
  430t 每只祝尼魔有**两次独立 p=0.7 的决策机会**；两次都通过时legacy 会连续做两次完整搜索
  （第二次丢弃第一次的路径）。旧模拟器只建模了一次。
- 生成：`dayUpdate` 清空 myJunimos、`cropHarvestRadius = CurrentWorkingRadius`（=配置 MaxRadius，
  默认 8，基准取 14）[M2]。

### 1.3 AI 补丁版（限速 + 门控 + 认领）

补丁 diff [P1] 精确语义：

1. **决策限速**：`PatchPathfindDoWork.Prefix` 顶部加 `NextDecisionTick` 检查——`now < nextAllowed`
   时**整个决策被丢弃**（保留当前路径/原地站，不排队）。成功搜索/闲逛 roll/门关 → `+20t`；
   失败 → `+40t`。挂在 pathfindToNewCrop 入口（0.7 门之后），故 430t 双重 poke 的第二发会被丢弃。
2. **扫描门控**：主搜索前先 `hut.areThereMatureCropsWithinRadius()`（=3.2.0 的 60t 缓存扫描）。
   为 false 时跳过主搜索，直接 `VanillaFallbackRoll(tryLkc:false)`（25% 回家 / 75% 闲逛）。
   注意：扫描**不感知认领**，且顺带把 lkc 刷新到 ≤60t 新鲜度（legacy 的 lkc 只在生成扫描时刷新）。
3. **认领过滤**（`CropClaims`，默认开，仅主游戏；farmhand 无影响 [P1]）：
   - 终点过滤：`PatchFindingCropEnd` 中 `IsActionable && !被其他祝尼魔认领`（skip-and-continue：
     被认领格可穿越、只是不可作终点）。
   - 登记：主搜索成功且终点未被他人认领 → `TryClaim`（先释放自己的旧认领，一人一格）。
   - **拥挤影子重搜**：过滤搜索返回 null（整球被认领）时，置 `SuppressFilter` **无过滤重搜一次**
     （vanilla shadowing），其终点不登记认领。
   - 失败分支先 `ReleaseOwner`。
   - 释放时机：收获（`tryToAddItemToHut` prefix，即 1000ms crossing）、到达死格（tryToHarvestHere
     "无事"分支）、销毁（pokeToHarvest destroy 分支）、换天/读档/建筑变动 Clear；**30s（1800t）
     惰性过期**。销毁后的认领实际由下一次 poke 释放（≤430t），模拟中按"销毁即释放"近似。
4. **wander 重试 6→2**：postfix `retry<=1`（每次闲逛 = 1 本体 + 至多 2 次 postfix 搜索）。
5. 温室边界修复、小屋查找按天缓存：与本基准无关（未建模）。

---

## 第二章 实现要点与建模取舍

架构：严格事件驱动。每只祝尼魔只在自己被触发的时刻出现在事件堆里
（路径到达 / 计时归零 / 空闲几何触发 / 430t 双重 poke / 60t 生成检查）；**没有任何逐帧重决策**。
限速、认领过期、扫描缓存都按绝对 tick 判定。A* 逐条复刻 [V2] 的语义
（出队判定、closed-on-insert、g+曼哈顿、100 次扩展预算、扩展数=成本"pops"）。

地图：80×65，小屋 3×2 足迹位于中心（门格 (tileX+1,tileY+1) 可通行——由 [V1:420-421] 能走回家反推；
足迹形状未确认，取 3×2 挖门近似），门格周围 3×3 强制无障碍（防高障碍率下门被围死成死局）。
障碍 5%（吞吐）/5%、25%（CPU）随机铺满全图，BFS 可达性过滤保证可完成。
作物只放在 radius 盒内可达格：密度 0.10/0.75/1.00 = 随机取 round(候选×密度)。

与旧模拟器对齐的参数取值：radius 14（29×29 盒）、80×65、~5% 障碍、CPU 日 36000t、
4 波 t=2000/10000/18000/26000、波大小 = round(盒内候选×0.10)/4、DNF 240000t、
生成节流 60t、扫描缓存 60t、闲置随机均值 3000t、DNF 口径与 CSV 列名一致。

**有意做的不同选择（相对旧模拟器的已知近似清单）：**

| 旧模拟器近似 | sim2 的做法 | 依据 |
|---|---|---|
| 收获计时恒定 120t | 同为 120t，但区分：作物在 +60t 移除、+120t 才恢复决策；120t 内到达的同伴会**一起站满计时** | [V1:286,506,589] |
| 认领过期未建模 | 建模 1800t 惰性过期 | [P1] |
| 每次闲逛至多 6/2 次搜索 | 1 次本体 + 1..6/1..2 次 postfix（本体结果被覆盖但计成本） | [V1:716][M1][P1] |
| 小屋 poke 每 430t 一次 | **双重 poke**（原版本体 + mod postfix 各一次，各自 0.7） | [V3:286][M2:ReplaceJunimoTimerNumber] |
| 移动 20 t/格 | 21.33 t/格（speed=3 px/tick） | [V1:96] |
| 旧 sim 的认领搜索预算 1000（其常量 EXP_LIMIT_CLAIMS） | 一律 100 | [V2:382][M1][P1]（真实代码无 1000 预算） |

未建模（如实声明）：下班时间/工资/冬天/雨天/灌木/温室/noHarvest/WorkRidiculouslyFast/
浇水施肥种植能力（场景中只有成熟作物，IsActionable≡readyForHarvest；真实未浇水田会多出浇水
遍历，对四个算法是近似同质的额外开销）、站立在不可通行格的销毁检查、巨大的回避收获配置。

---

## 第三章 基准数字与对比结论

配对种子：种子只含 (场景, radius, junimos, 密度/障碍, run)，**不含算法**——同 run 所有算法同一张地图。
n=50/吞吐格、n=30/CPU 格；p 值为配对 t 检验（正态近似）。
基线 `vanilla_mod` = mod 3.2.0 在真实节奏下（对应旧口径 `legacy_arrival`）。

### 3.1 吞吐（清场用时，负值 = 补丁更快）

| 密度 | junimos | vanilla mean(t) | cap_only | patch_shipped | patch_nocap |
|---|---|---|---|---|---|
| 0.10 (~84 株) | 2 | 18455 | −9.2% (.001) | **−14.4%** (.000) | −14.6% |
| 0.10 | 4 | 10459 | −4.3% (.14) | **−17.5%** | −21.7% |
| 0.10 | 6 | 7686 | −5.1% (.078) | **−19.7%** | −20.2% |
| 0.10 | 8 | 6351 | −2.2% (.36) | **−25.1%** | −21.8% |
| 0.75 (~595 株) | 2 | 77543 | −3.5% | **−4.9%** | −4.0% |
| 0.75 | 4 | 39880 | +0.1% (.91) | **−5.4%** | −6.7% |
| 0.75 | 6 | 27880 | −0.6% (.51) | **−10.2%** | −10.2% |
| 0.75 | 8 | 21976 | −1.2% (.19) | **−11.3%** | −13.3% |
| 1.00 (~740 株) | 2 | 95423 | −2.0% (.006) | **−4.0%** | −3.4% |
| 1.00 | 4 | 50687 | −1.9% (.006) | **−7.5%** | −7.9% |
| 1.00 | 6 | 35246 | −1.0% (.20) | **−11.0%** | −10.8% |
| 1.00 | 8 | 27942 | −1.2% (.17) | **−14.8%** | −14.5% |

全部 12 格 patch_shipped 显著更快（−4% ~ −25%），nocap ≈ shipped（差 0~4 个百分点，多数格 <2），0 DNF。

**机制统计**（30 配对局，SIM2_STATS）：

| 场景 | 指标 | vanilla | patch_shipped |
|---|---|---|---|
| 8 只/满田 | 扎堆空等（站满 120t 计时发现作物已被同伴收走）/局 | 135.9 | **1.9**（~72×） |
| 8 只/满田 | 到达弹回（+12t 重决策）/局 | 24.6 | 2.8（~9×） |
| 8 只/满田 | 平均工作行程 | 1.5 格 | 1.5 格（**无绕路代价**） |
| 4 只/0.75 | 扎堆空等 /局 | 31.3 | 1.2 |
| 2 只/0.10 | 扎堆空等 /局 | 5.6 | 0.4 |
| 8 只/满田 | 拥挤影子重搜 /局 | — | 34.2 |

即：**原版的"扎堆火车"在真实代码语义下的代价不是几步冤枉路，而是同伴站满 120t 收获计时**
（作物要到 +60t 才消失，后到者到达时 isHarvestable 仍为真 → 开始自己的 2000ms 计时 → 收了个寂寞）。
认领过滤把这一成本几乎清零，而"舍近求远"代价在本场景小到测不出（平均行程不变）。

### 3.2 日稳态 CPU（A* expansions，负值 = 更省）

| 障碍 | junimos | vanilla pops/日 | cap_only | patch_shipped | patch_nocap |
|---|---|---|---|---|---|
| 0.05 | 2 | 47992 | **−44.8%** | −19.9% | +17.3% |
| 0.05 | 6 | 124496 | **−54.7%** | −36.1% | −3.8% (.045) |
| 0.25 | 2 | 58943 | **−48.2%** | −26.7% | +8.7% (.004) |
| 0.25 | 6 | 153309 | **−52.9%** | −36.9% | −6.5% (.074) |

与旧值同数量级（旧 legacy：57048 / 195822 / 53326 / 169409），方向一致、幅度更大。

### 3.3 最差 1 秒窗口（peak_pops_1s）

| 障碍 | junimos | vanilla | cap_only | patch_shipped | patch_nocap |
|---|---|---|---|---|---|
| 0.05 | 2 | 1596 | −61.3% | **−43.9%** | −3.8% (.38) |
| 0.05 | 6 | 3252 | −59.0% | **−36.7%** | +7.4% (.03) |
| 0.25 | 2 | 1774 | −57.7% | **−43.5%** | −4.1% (.42) |
| 0.25 | 6 | 3740 | −56.6% | **−39.1%** | +5.4% (.15) |

### 3.4 与旧模拟器（final-results/summary.md）逐条对比

| # | 旧结论 | sim2 结果 | 判定 |
|---|---|---|---|
| 1 | 补丁版清场比原版**慢 27~53%**；原版扎堆火车是高效工作集中策略 | 补丁版清场**快 4~25%**（全格显著） | **不一致（方向相反）**，见下面根因分析 |
| 2 | nocap ≈ shipped（去限速救不回吞吐） | nocap 与 shipped 差 0~4 个百分点（多数格 <2） | **一致** |
| 3 | 日稳态 CPU：shipped = 0.62~0.89×（开阔地最好，6 只 −37.6%） | shipped = 0.63~0.80×（0.05）/ 0.63~0.73×（0.25），6 只开阔 −36.1% | **一致**（0.05 处几乎复刻旧值；旧 0.25 处为 +10~+36% 且不显著，sim2 为 −27~−37%） |
| 4 | cap_only（限速+门控）0.05 处 −28~−48% | −45~−55% | **一致** |
| 5 | 最差 1s 窗口 shipped 与原版**持平**（0.99~1.05×） | shipped **−37~−44%** | **不一致**：旧模型只建模单次 poke，低估了 legacy 的 430t 突发；真实代码是双重 poke，限速把第二发全部丢弃 → 峰值近乎减半 |
| 6 | 无限速峰值 1.37~1.46× | nocap ≈ 1.0×（−4~+7%） | **不一致**（同因：legacy 基线本身含双发突发，nocap 也含，两者相抵） |
| 7 | 防扎堆：无效到达 280-444 → 15-44（~10×） | 扎堆空等 135.9→1.9（72×）、弹回 24.6→2.8（9×）；合计重复到达 ~7× | **一致**（方向与量级；口径不同：旧数含被我计为"扎堆空等"的情形） |
| 8 | respawn：补丁版更多（3.5-10 vs 0.9-1.9） | vanilla 更多（4.3-7.0 vs 2.2-5.6） | **不一致**（连锁差异：旧模型里 vanilla 搜索几乎不失败；sim2 里 vanilla 的扎堆链使失败分支更频繁） |

**吞吐方向相反的根因分析（本次交叉验证的核心发现）：**

旧 summary 的 legacy 数字在算术上就违反其自订的收获节奏下限：density 0.75、4 只、556 株、
mean 265.8s = 15948t，而 556 株 × 120t 计时 ÷ 4 只 = **16680t > 15948t**——即便零行走、零扎堆也
到不了这个数。可推断旧模型的收获循环实际 ≈ 60t/株（在 1000ms 作物移除点就恢复决策，跳过了
1000-2000ms 的空摇窗口），且作物到达即提交（后到者立刻弹回而非陪站）。这两条近似都**单方面
抬高 vanilla 的吞吐、放大补丁的相对成本**。而 [V1:280-293,506,589] 与 [M1:PatchTryToHarvestHere]
的代码语义明确：作物在到达后 60t 才移除、后到同伴会开始自己的完整计时陪站——"扎堆火车"的真实
代价是**并行度崩塌**，不是几步冤枉路。sim2 按代码语义建模后，认领过滤的收益（消灭陪站）压倒了
它的代价（影子重搜 + 少量绕路），方向随之反转。

---

## 第四章 独立判断：补丁是否值得装

**值得装，且比旧模拟器得出的"权衡"结论更强：在本模拟覆盖的场景里它接近帕累托改进**——
清场更快（−4~−25%）、日 CPU 更省（−20~−37%）、最差帧窗口更低（−37~−44%）、扎堆空等近零、
且认领的影子重搜保证拥挤时行为不劣于原版。限速的真实价值不是省 CPU 总量，而是**砍掉 430t
双重 poke 的同步突发**（峰值近乎减半）——这一点旧模拟器因只建模单次 poke 而没能看到。

置信度与边界：
1. 方向性结论（补丁更快 + 更省 + 防扎堆）在 sim2 的全部格点一致显著，且机制可解释、可追溯
   到反编译行号；对旧结论的反驳核心是**代码证据**（[V1:506] 的 1000ms 移除点、[V1:589] 的
   2000ms 决策点、[M1] 到达即 isHarvestable 判定、[M2] 双重 poke），不依赖本模拟器本身。
2. 未建模因素（工资/下班/温室/浇水遍历等）对四算法近似同质，不太可能翻转方向；
   但"未浇水田的浇水遍历"会让 IsActionable 的目标集变大、扎堆面变宽，方向上更有利于认领过滤。
3. 若追求更强外部效度，下一步是在真实游戏里做一次可证伪的对照：同一 556+ 株农田、4 只祝尼魔，
   分别装 3.2.0 与补丁 DLL 各清场一次计时——sim2 预测补丁快 5~15%，旧模拟器预测慢 40% 左右，
   一次实测即可裁决。

## 附：文件清单

- `Program.cs` — 模拟器（行为规格注释内嵌，标注证据行号）；`SIM2_DEBUG=1` 单局追踪、`SIM2_STATS=1` 机制统计、`SIM2_RUNS`/`SIM2_CPU_RUNS`/`SIM2_OUT`/`SIM2_QUICK` 控制基准。
- `aggregate.py` — 聚合（RoM + MoD + MoR 警示列 + 配对 t）；`aggregate_output.txt` 为本次全量输出。
- `results.csv` / `cpu_results.csv` — 列名与旧格式一致。
