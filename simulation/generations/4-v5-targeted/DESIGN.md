# v5 设计文档 —— Better Junimos 性能补丁的三个针对性修复

> 目的：给出可直接落成真 DLL 补丁的精确语义、伪代码与补丁点对应关系。
> 依据源码：上游 3.2.0 = `git -C official show faa40d4:BetterJunimos/...`；
> 现装补丁版 = `official/BetterJunimos`（HEAD `d94e38a`）；游戏 1.6.15 反编译 `/tmp/junimo.cs`、`/tmp/pfc.cs`、`agent1-audit/junimohut.cs`。
> 模拟验证：`sim-v5/Program.cs`（模拟器把以下每个机制按真实代码语义建模，见 REPORT.md §1）。

---

## 0. 真实行为规格（写 DLL 前必须对齐的机制，本模拟器已按此建模）

### 0.1 双发 poke（模拟器缺失项，已补）

- 原版 `JunimoHut.performTenMinuteAction`（junimohut.cs:280-296）对 `myJunimos` 里**每一只**（含行走中、收获中）调 `pokeToHarvest()`。
- mod 的 `ReplaceJunimoTimerNumber` 是**同一方法的 postfix**（HEAD JunimoHutPatches.cs:265-302），在原版本体之后再 poke 全部祝尼魔一遍。
- 即：mod 下每 430t 每只祝尼魔有**两次独立** `pokeToHarvest()`，各自独立掷 70% 门、各自过一次决策限速门。
- `pokeToHarvest`（junimo.cs:295-309）本身无 controller 检查：行走中祝尼魔两次都通过门时，第二次 `pathfindToNewCrop` 会**丢弃当前路径重新寻路**（PatchPathfindDoWork 的工作分支也没有 controller 守卫）。
- 收获中祝尼魔：`harvestTimer > 0` → 70% 门必失败；但 WorkRidiculouslyFast 的 postfix（见 0.2）仍会让它重新决策。

### 0.2 WorkFaster / WorkRidiculouslyFast（用户真实配置，本基准主场景）

出处：`git show faa40d4:BetterJunimos/Patches/JunimoHarvesterPatches.cs`（3.2.0 上游）。

| 代码点 | 语义 | 对本场景（纯收获）的影响 |
|---|---|---|
| `PatchTryToHarvestHere`（:60-84） | 收获到达恒 `time=2000`；其他能力 WRF→20ms，否则 Progression.WorkFaster→300:998；"无事可做"→`time = WorkFaster ? 5 : 200` + 立即 poke | 收获周期不变；**死到达的重 poke 计时器 200ms→5ms（≈1 帧）** |
| `PatchJunimoShake`（:85-101） | `if (WorkFaster && harvestTimer == 999) harvestTimer = 0` | **帧抖动彩票**：从 2000 递减（每帧 ~16ms），只有当跨过 1000ms 那一帧恰好落在 999（累计耗时恰为 1001ms）才触发。实战不可靠 → **建模为不改变 120t 收获周期**（详见 REPORT.md §1.2 的推演） |
| `PatchPokeToHarvest` postfix（:293-297） | `if (destroy) return; if (controller != null) return; if (!WRF) return; pathfindToNewCrop();` | **任何** pokeToHarvest 之后：无 controller 且未销毁 → 无条件再决策。后果：(a) 收获计时归零后 100% 决策（绕过 70% 门）；(b) 死到达立即 100% 决策；(c) **430t poke 打中收获中祝尼魔**（destroy=false、controller=null）→ 重新决策 |
| :254 | `rand < 0.035 && !WorkRidiculouslyFast` | WRF 移除 3.5% 闲逛 |

WRF 下"430t poke 打中收获中祝尼魔"的精确语义（已按反编译逐帧推演，模拟器照此建模）：

- **阶段 0（0-60t，作物未收走）**：从自身格搜索 → 自身格仍 actionable 且距离 0 → 路径 = [自身格]（pfc.cs reconstructPath 含起点，pfc.cs:296-304）→ 下一帧 `moveCharacter` 立即 Pop 到终点 → `endBehavior` = `tryToHarvestHere` → `harvestTimer = 2000` **重置**。祝尼魔不会走开，但本次收获进度被清零（损失已站时长）。
- **阶段 1（60-120t，作物已被 1000ms 过线收走）**：自身格无作物 → 搜索找到最近其他作物 → 祝尼魔提前结束空站、直接走向下一个目标。
- 被卡在"注定落空"格上的跟随后果相同：无认领算法会原地反复重置计时（扎堆重粘），带认领算法的过滤搜索会跳过他人认领的格子把它**踢出**去。
- 限速门仍生效：cap 未到时被 cap 拦截 → 什么也不发生（收获继续/空站继续）。

### 0.3 本模拟器同时核实的既有机制（沿用 sim-audit1）

两阶段收获（作物 60t 过线被收走、120t 恢复决策；过线判定 `isHarvestable()`，后到者站满）；认领表全局（CropClaims 是 `static (GameLocation, Point)` 表，跨小屋共享，1800t 惰性过期）；`PatchSearchAroundHut` 60t 每小屋缓存；终点出盒判定（`outsideRadius`，单小屋恒不触发、双小屋决定跨小屋行为）；findPath 语义（g+曼哈顿到起点、closed-on-enqueue、出队计数预算）。

---

## 1. 修复一：条件化去重（阈值版 k / 绕路版 D）

### 1.1 语义

认领过滤（把"被他人认领的格子当作不可达终点"）**只在活多人少时开启**：

- **k 版（推荐）**：以小屋 60t 缓存扫描得到的"盒内可动作格数" `C` 为准；`C ≤ k × 每小屋祝尼魔数` 时本小屋的本次决策**退回原版贪婪**（无过滤最近搜索；成功仍登记认领，无害）。k 从 1~3 扫参，**数据选 k=1**。
- **D 版（绕路上限）**：单次决策内，若过滤搜索找到的最近未认领作物比本次搜索弹出的最近作物（无论认领）远超过 D 格（重构路径长度差），本次决策**忽略认领**、直接取最近作物（vanilla 行为，不登记/不抢认领）。D 从 2~8 扫参——**本模型中效应微弱（拥挤且最近格被占的情形罕见），不推荐单独使用**。

说明：任务书中的"末期回退"（剩余可收 ≤ 1×/2× 祝尼魔数时整体关闭过滤）与 k 版是**同一机制**（同一阈值条件），实现与数据均逐位一致；k 即把两者统一后的参数。

### 1.2 伪代码（对照 HEAD PatchPathfindDoWork 的工作分支）

```csharp
// ---- scan gate 之后、构造 filtered controller 之前 ----
int boxCrops = PatchSearchAroundHut.CountActionable(hut);   // 见 §3，60t 缓存，阈值+1 早退
int labor    = hut.myJunimosCount();                        // 每小屋在编数
bool filterOn = !(boxCrops <= V5_K * labor);                // V5_K = 1（0 = 永远开）
```

`filterOn == false` 时跳过 `CropClaims.SuppressFilter` 的整个 filtered 路径，直接跑一次无过滤搜索并按原版语义处理（成功 → TryClaim 无害登记 → 出发）。

### 1.3 真实 DLL 对应补丁点

| 模拟器 | 真 DLL |
|---|---|
| `ScanGate(h, counting)` + `_cropsInBox[h]` | `PatchSearchAroundHut.SearchAroundHut` 增加计数输出（静态缓存里加一个 `int actionableCount` 字段） |
| `_v5K` 判定 | `PatchPathfindDoWork.Prefix` 在构造 filtered controller 前读缓存计数 |

---

## 2. 修复二：末期回退（= 修复一 k 的特例）

阈值 `m × 祝尼魔数`（m ∈ {1,2}）与 k 共用同一计数与同一判定路径；数据上 m=1 ≡ k=1、m=2 ≡ k=2。设计上保留为独立参数以便日后分别调（例如 k=3 且 m=1：中期 3 倍冗余内保持去重，真末期 1 倍内彻底关闭）。

---

## 3. 修复三：拥挤判定缓存（影子重搜节流，T）

### 3.1 语义

v4b 的拥挤决策 = 过滤搜索跑满预算（100 pops 全烧完）+ 无过滤影子重搜（再 100 pops）= 2 倍 A*。修复：**把"整个预算内全是他人认领"这一判定结果按小屋缓存 T tick**。

- 设置：某祝尼魔的过滤搜索失败（返回 null）时，`AllClaimedUntil[hut] = now + T`。
- 消费：决策开始时若 `now < AllClaimedUntil[hut]`，**跳过注定失败的过滤搜索**，直接跑无过滤搜索并把结果按影子处理（不抢认领）——目标与"过滤失败→影子重搜"完全一致，A* 减半。
- 失效：任一过滤搜索成功即清空（证明并非全被认领）；T 到期自然失效。
- 陈旧自愈：T 窗口内出现新空格时，无过滤搜索找到的终点若**未被他人认领**，则按正常路径处理（登记认领）——行为与未节流时一致。残余代价：终点在决策后、到达前被第三方抢认领 → 变成一次普通影子行走（doomed 计数轻微上升，见 REPORT.md §4）。

### 3.2 伪代码（对照 HEAD PatchPathfindDoWork:286-296）

```csharp
// 静态：Dictionary<JunimoHut, int> AllClaimedUntil;  const int ClaimVerdictTicks = 40;

bool verdictFresh = AllClaimedUntil.TryGetValue(hut, out var until) && Game1.ticks < until;
PathFindController ctrl;
bool shadow;
if (verdictFresh) {
    ctrl = NewWorkSearch(unfiltered: true); shadow = true;          // 只跑一次
} else {
    ctrl = NewWorkSearch(unfiltered: false);                        // 过滤搜索
    if (ctrl.pathToEndPoint == null) {
        AllClaimedUntil[hut] = Game1.ticks + ClaimVerdictTicks;     // 缓存判定
        ctrl = NewWorkSearch(unfiltered: true); shadow = true;      // 影子重搜（原 v4b 行为）
    } else {
        AllClaimedUntil.Remove(hut); shadow = false;                // 判定失效
    }
}
// shadow 且终点未被他人认领（verdict 陈旧）→ 按正常路径 TryClaim
```

数据：T ∈ {10,20,40,60} 扫参，**T=40** 在峰值/总 A* 上最优、doomed 代价最小化（配合 k=1 时 doomed 仍仅为原版的 1~2%）；T=20 为保守备选（峰值略高 ~5%，doomed 略低）。

### 3.3 真实 DLL 对应补丁点

`CropClaims` 增加静态 `Dictionary<JunimoHut, int> AllClaimedUntil`（换天/读档 `Clear()` 一并清空）；`PatchPathfindDoWork.Prefix` 的 filtered/shadow 段按 §3.2 改写。无任何新扫描、无每帧成本。

---

## 4. 组合与最终参数

```
v5 = v4b_realfilter（决策限速 20/40t + 60t 扫描门 + 认领过滤 + 影子重搜 + 认领 1800t 过期）
    + 修复一 k=1（盒内可动作格 ≤ 1×每小屋祝尼魔数 → 本次决策关闭过滤）
    + 修复三 T=40（"全被认领"判定按小屋缓存 40t，避免拥挤时每只各做一次全预算重搜）
```

- 双小屋：判定按小屋各自缓存；认领表全局共享（与真 DLL 的 `(location, point)` 键一致）。
- 计数扫描早退：计数目标只是判断 `≤ k×jn`，故扫描在找到 `k×jn+1` 个可动作后即可停止——作物茂盛期只扫几十格，作物稀疏期才全盒扫（与现有门控扫描在空盒时的成本相同，**不引入新的最坏情形**）。模拟器按此建模，v5 的 scan_ops 与 cap_only 同量级（20.9k vs 23.5k）。

---

## 5. 模拟器建模对应清单（sim-v5/Program.cs）

| 真实机制 | 模拟器位置 |
|---|---|
| 双发 poke | `DoHutPoke`（`for poke in 0..1` 两轮独立 70% 门 + 各自 cap） |
| WRF 收获后 100% 决策 / 死到达 100%+1t 定时戳 / 无闲逛 | `Cfg.WRF` 分支（Working 完成态、死到达、`Decide` 的 stroll 跳过） |
| WRF 430t poke 打断收获（阶段0=计时重置、阶段1=提前离场、cap 生效时免打扰） | `DoHutPoke` 的 `Working` 分支 |
| 120t 两阶段收获、60t 过线先到先得、doomed 跟随 | 主循环 `Working` case |
| 过滤搜索 skip-and-continue + 影子重搜 | `AStarNearest(skipClaimed)` + `DecideV4bRealFilter` |
| v5-k / v5-D | `DecideV5` 的 `filterOn` 判定 + `AStarNearest(detourLimit)` 的 `_firstCropPath` |
| v5-T | `DecideV5` 的 `_allClaimedUntil[h]` |
| 计数扫描（阈值+1 早退） | `ScanGate(h, counting)` 的 `cap = max(k,m)*jn + 1` |
| 双小屋（每小屋门控/lkc/生成节流/verdict；全局认领表；出盒判定） | `_hutTiles`/`_lkc[]`/`_gateScanTick[]`/`_lastSpawnTick[]`/`OutsideBox` |
| 分期统计（25/50/75/100% 清完的用时与 A*、全程峰值 60t 窗） | `RecordPhases` / `_tickPops` 滑窗 |

已声明的近似（沿用 sim-audit1 §7 并新增）：WorkFaster 的 `==999` 跳过建模为不触发；出生 1-2 格 ctor 行走不建模；25% 回家即时消失；`IsCollidingPosition`/5 秒卡死取消不建模；WRF 下 doomed 计数含"被打断后原地重开收获"的重复到达（真实游戏同样会计数）。
