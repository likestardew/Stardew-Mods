// =============================================================================
// sim2 — Better Junimos 性能补丁独立交叉验证模拟器（事件驱动，从零重建）
//
// 本实现不参考旧模拟器代码；所有决策/搜索/认领/限速逻辑均从以下证据独立推导：
//   [V1] /tmp/junimo.cs            (1.6.15 JunimoHarvester 反编译)
//   [V2] /tmp/pfc.cs               (1.6.15 PathFindController 反编译)
//   [V3] agent1-audit/junimohut.cs (1.6.15 JunimoHut 反编译)
//   [M1] official git faa40d4:BetterJunimos/Patches/JunimoHarvesterPatches.cs (mod 3.2.0)
//   [M2] official git faa40d4:BetterJunimos/Patches/JunimoHutPatches.cs       (mod 3.2.0)
//   [M3] official git faa40d4:BetterJunimos/Abilities/JunimoAbilities.cs      (mod 3.2.0)
//   [P1] betterjunimos-3.2.0-perffix-claimfilter.patch (AI 补丁 diff)
// 证据标注 [V1:362] = junimo.cs 第 362 行。
//
// 建模范围声明（详见 REPORT.md）：
//   - 只建模"成熟作物收获"场景（田里只有：成熟作物 / 障碍 / 小屋），故 IsActionable == readyForHarvest。
//   - 不建模：下班时间、工资、冬天、雨天、灌木、温室、noHarvest、WorkRidiculouslyFast、Progression 解锁。
//   - 收获计时 2000ms=120t；作物在 +60t（1000ms crossing [V1:506]）移除；120t 后 timer 过期 poke。
//   - 移动速度 speed=3 px/tick（[V1:96]）→ 64/3 ≈ 21.33 t/格。
//   - mod 的 performTenMinuteAction postfix 再 poke 一次（[M2] ReplaceJunimoTimerNumber）→ 每 430t 双重 poke。
//   - mod 的 wander = vanilla 本体 1 次构造（结果被 postfix 覆盖）+ postfix 1..R 次（[M1]）。
// =============================================================================

namespace Sim2;

public static class Cfg
{
    public const int W = 80, H = 65;
    public const double TicksPerTile = 64.0 / 3.0;   // [V1:96] speed=3 px/tick
    public const int HarvestTicks = 120;             // [V1:286] 2000ms
    public const int CropGoneTicks = 60;             // [V1:506] 1000ms crossing
    public const int NothingArrivalTicks = 12;       // [M1] 200ms（Progression.WorkFaster 默认 false）
    public const double PokeProb = 0.70;             // [V1:304]
    public const double WanderProb = 0.035;          // [V1:377][M1]
    public const int HutPokeTicks = 430;             // [V3:280] 每 10 游戏分钟
    public const int IdleMeanTicks = 3000;           // [V1:626][V1:649] 0.002×1/6，直接 pathfind（无 0.7 门）
    public const int SpawnStaggerTicks = 60;         // [M2] SpawnCooldownTicks=60
    public const int ScanCooldownTicks = 60;         // [M2] ScanCooldownTicks=60
    public const int WanderAttemptsLegacy = 6;       // [M1] retry <= 5
    public const int WanderAttemptsPatched = 2;      // [P1] retry <= 1
    public const int ExpLimit = 100;                 // [V2:382][M1]
    public const int SimCapTicks = 240_000;
    public const int DayTicks = 36_000;
    public static readonly int[] WaveTicks = { 2000, 10000, 18000, 26000 };
    public const double CpuDayDensity = 0.10;
    public const int ClaimExpiryTicks = 1800;        // [P1] ~30s
    public const int CapSuccessTicks = 20;           // [P1]
    public const int CapFailTicks = 40;              // [P1]
    public const int Radius = 14;
}

public enum Algo { VanillaMod, CapOnly, PatchShipped, PatchNocap }

public static class AlgoExt
{
    public static Algo Parse(string s) => s switch
    {
        "vanilla_mod" => Algo.VanillaMod,
        "cap_only" => Algo.CapOnly,
        "patch_shipped" => Algo.PatchShipped,
        "patch_nocap" => Algo.PatchNocap,
        _ => throw new ArgumentException(s)
    };
    public static string Name(this Algo a) => a switch
    {
        Algo.VanillaMod => "vanilla_mod",
        Algo.CapOnly => "cap_only",
        Algo.PatchShipped => "patch_shipped",
        _ => "patch_nocap"
    };
    public static bool HasCap(this Algo a) => a is Algo.CapOnly or Algo.PatchShipped;
    public static bool HasGate(this Algo a) => a != Algo.VanillaMod;
    public static bool HasClaims(this Algo a) => a is Algo.PatchShipped or Algo.PatchNocap;
    public static int WanderAttempts(this Algo a) =>
        a == Algo.VanillaMod ? Cfg.WanderAttemptsLegacy : Cfg.WanderAttemptsPatched;
}

// =============================================================================
// 地图
// =============================================================================
public class Map
{
    public int W, H;
    public bool[] blocked;
    public int[] cropReadyAt;      // -1 无作物；否则成熟 tick
    public int hutX, hutY, doorX, doorY;
    public List<int> boxCandidates = new();

    public int Idx(int x, int y) => y * W + x;
    public int XOf(int i) => i % W;
    public int YOf(int i) => i / W;

    public static Map Build(int seed, double obstacleRate, int radius)
    {
        var rng = new Random(seed);
        var m = new Map { W = Cfg.W, H = Cfg.H };
        m.blocked = new bool[Cfg.W * Cfg.H];
        m.cropReadyAt = new int[Cfg.W * Cfg.H];
        for (int i = 0; i < m.cropReadyAt.Length; i++) m.cropReadyAt[i] = -1;

        for (int i = 0; i < m.blocked.Length; i++)
            if (rng.NextDouble() < obstacleRate) m.blocked[i] = true;

        // 小屋足迹：3×2 挖去门格 (tileX+1, tileY+1)。
        m.hutX = Cfg.W / 2 - 1; m.hutY = Cfg.H / 2 - 1;
        m.doorX = m.hutX + 1; m.doorY = m.hutY + 1;
        for (int dx = 0; dx < 3; dx++)
            for (int dy = 0; dy < 2; dy++)
            {
                int x = m.hutX + dx, y = m.hutY + dy;
                if (x == m.doorX && y == m.doorY) continue;
                if (x < 0 || y < 0 || x >= Cfg.W || y >= Cfg.H) continue;
                m.blocked[m.Idx(x, y)] = true;
            }
        // 门格周围 3×3 强制无障碍（小屋足迹格除外）：高障碍率下防止门被围死导致死局。
        // 真实农场小屋门口通常留有活动空间；此处为地图生成约定，写进 REPORT。
        for (int x = m.doorX - 1; x <= m.doorX + 1; x++)
            for (int y = m.doorY - 1; y <= m.doorY + 1; y++)
            {
                if (x < 0 || y < 0 || x >= Cfg.W || y >= Cfg.H) continue;
                if (x >= m.hutX && x < m.hutX + 3 && y >= m.hutY && y < m.hutY + 2 && !(x == m.doorX && y == m.doorY))
                    continue; // 小屋足迹保持阻挡
                m.blocked[m.Idx(x, y)] = false;
            }

        // 可达性 BFS（门格出发）
        var reach = new bool[Cfg.W * Cfg.H];
        var q = new Queue<int>();
        int door = m.Idx(m.doorX, m.doorY);
        reach[door] = true; q.Enqueue(door);
        int[] dxs = { -1, 1, 0, 0 }, dys = { 0, 0, -1, 1 };
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            int cx = m.XOf(cur), cy = m.YOf(cur);
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + dxs[d], ny = cy + dys[d];
                if (nx < 0 || ny < 0 || nx >= Cfg.W || ny >= Cfg.H) continue;
                int ni = m.Idx(nx, ny);
                if (reach[ni] || m.blocked[ni]) continue;
                reach[ni] = true; q.Enqueue(ni);
            }
        }

        for (int x = m.doorX - radius; x <= m.doorX + radius; x++)
            for (int y = m.doorY - radius; y <= m.doorY + radius; y++)
            {
                if (x < 0 || y < 0 || x >= Cfg.W || y >= Cfg.H) continue;
                int i = m.Idx(x, y);
                if (m.blocked[i] || !reach[i]) continue;
                m.boxCandidates.Add(i);
            }
        return m;
    }
}

// =============================================================================
// 忠实 A*（[V2:180-246]）：
//   - 终点判定在出队时 [V2:200]；命中即返回，不计该次扩展
//   - 越界格弃（终点恰在图外亦弃，因碰撞检测同样拒绝）[V2:215][V2:222]
//   - 碰撞（blocked）对包括终点在内的所有邻居生效 [V2:222]
//   - closed-on-insert：每格至多入队一次，g 取首见值 [V2:228]
//   - priority = (父g+1) + manhattan(邻居, endPoint) [V2:227]
//   - num 统计"出队并扩展"次数，>= limit 失败 [V2:231]
// 作物搜索（endFunction 型）的 endPoint=Point.Zero → 起点格 [V1:382][M1]，
// 故启发函数 = manhattan(node, 起点)。
// =============================================================================
public static class Search
{
    public static bool FindPath(Map m, int start, Func<int, bool> isEnd, int hEnd, List<int> pathOut, out int pops)
    {
        pops = 0;
        if (m.blocked[start]) return false;
        var open = new PriorityQueue<int, int>();
        var g = new Dictionary<int, int>();
        var parent = new Dictionary<int, int>();
        var seen = new HashSet<int>();
        int hx = m.XOf(hEnd), hy = m.YOf(hEnd);
        int sx = m.XOf(start), sy = m.YOf(start);

        g[start] = 0;
        open.Enqueue(start, Math.Abs(hx - sx) + Math.Abs(hy - sy)); // [V2:194]

        int num = 0;
        int[] dxs = { -1, 1, 0, 0 }, dys = { 0, 0, -1, 1 };
        while (open.TryDequeue(out int node, out _))
        {
            if (isEnd(node)) // [V2:200]
            {
                pathOut.Clear();
                int cur = node;
                while (true) { pathOut.Add(cur); if (cur == start) break; cur = parent[cur]; }
                pathOut.Reverse();
                pops = num;
                return true;
            }
            int ng = g[node] + 1;
            int nx0 = m.XOf(node), ny0 = m.YOf(node);
            for (int d = 0; d < 4; d++)
            {
                int nx = nx0 + dxs[d], ny = ny0 + dys[d];
                if (nx < 0 || ny < 0 || nx >= m.W || ny >= m.H) continue; // [V2:215]
                int ni = m.Idx(nx, ny);
                if (seen.Contains(ni)) continue;               // [V2:211]
                if (m.blocked[ni]) { seen.Add(ni); continue; } // [V2:222] 含终点
                g[ni] = ng;
                parent[ni] = node;
                seen.Add(ni);                                  // [V2:228] 先 closed 再入队
                open.Enqueue(ni, ng + (Math.Abs(hx - nx) + Math.Abs(hy - ny))); // [V2:227]
            }
            num++;
            if (num >= Cfg.ExpLimit) { pops = num; return false; } // [V2:232]
        }
        pops = num;
        return false;
    }
}

// =============================================================================
// 模拟器
// =============================================================================
public class Sim
{
    enum EvType { PathArrival, TimerPoke, CropGone, IdleTrigger, HutPoke, SpawnCheck }
    readonly struct Ev
    {
        public Ev(long tick, int seq, EvType type, int junimo, int token, int timerSeq, int tile)
        { Tick = tick; Seq = seq; Type = type; Junimo = junimo; Token = token; TimerSeq = timerSeq; Tile = tile; }
        public long Tick { get; } public int Seq { get; } public EvType Type { get; }
        public int Junimo { get; } public int Token { get; } public int TimerSeq { get; } public int Tile { get; }
    }

    sealed class Junimo
    {
        public int id;
        public int tile;
        public bool alive = true;
        public List<int> path; public int workPurpose; // 0=wander/spawn 1=work 2=home
        public long timerUntil = -1; public int timerSeq; public int timerTile = -1;
        public long nextDecision = -1;
        public int claimedTile = -1;
        public int stateToken;
    }

    readonly Algo _algo;
    readonly int _junimoCap;
    readonly int _radius;
    readonly Map _m;
    readonly Random _rng;
    readonly bool _dayMode;
    readonly List<int> _crops = new();
    readonly long _endTick;

    readonly List<Junimo> _junimos = new();
    readonly PriorityQueue<Ev, (long, int)> _events = new();
    int _seq;
    long _now;
    long _clearTick = -1; int _remaining;
    long _expansions;
    long _wastedArrivals;
    int _despawns;
    long _scanOps;
    int _harvested;
    int _alive;

    bool _scanFound; long _scanTick = -1; bool _scanValid;
    int _lkc = -1;

    // 调试计数
    internal static bool Debug;
    long _shadowStands;        // 收获计时走到头发现作物已被同伴收走（扎堆空等）
    long _workWalks; long _workWalkTiles;   // 工作行程次数/总格数（平均行程长度）
    long _claimShadowSearches;
    long _dbgSpawnChecks, _dbgScansFromSpawn, _dbgScansFromDecide, _dbgDecisions, _dbgDecideDropped,
         _dbgWanderRolls, _dbgSearchMain, _dbgSearchShadow, _dbgSearchLkc, _dbgSearchHome, _dbgSearchWander,
         _dbgWalksWork, _dbgWalksWander, _dbgWalksHome, _dbgIdleTriggers, _dbgCapDrops;

    readonly Dictionary<int, (int owner, long tick)> _claims = new();
    bool _suppressFilter;

    long[] _tickPops;

    public Sim(Algo algo, int junimos, int radius, int mapSeed, double obstacleRate,
               double cropDensity, bool dayMode)
    {
        _algo = algo; _junimoCap = junimos; _radius = radius; _dayMode = dayMode;
        _endTick = dayMode ? Cfg.DayTicks : Cfg.SimCapTicks;
        if (dayMode) _tickPops = new long[Cfg.DayTicks + 1];

        var mRng = new Random(mapSeed);
        _m = Map.Build(mRng.Next(), obstacleRate, radius);

        var cand = new List<int>(_m.boxCandidates);
        for (int i = cand.Count - 1; i > 0; i--) { int k = mRng.Next(i + 1); (cand[i], cand[k]) = (cand[k], cand[i]); }

        if (dayMode)
        {
            int target = (int)Math.Round(cand.Count * Cfg.CpuDayDensity);
            int per = Math.Max(1, target / Cfg.WaveTicks.Length);
            for (int w = 0; w < Cfg.WaveTicks.Length; w++)
                for (int i = w * per; i < Math.Min(cand.Count, (w + 1) * per); i++)
                {
                    _m.cropReadyAt[cand[i]] = Cfg.WaveTicks[w];
                    _crops.Add(cand[i]);
                }
        }
        else
        {
            int target = (int)Math.Round(cand.Count * cropDensity);
            for (int i = 0; i < target; i++) { _m.cropReadyAt[cand[i]] = 0; _crops.Add(cand[i]); }
        }
        _remaining = _crops.Count;
        _rng = new Random((int)((uint)mapSeed ^ 0x9E3779B9u));

        Schedule(Cfg.HutPokeTicks, EvType.HutPoke);
        Schedule(0, EvType.SpawnCheck);
    }

    void Schedule(long tick, EvType t, int junimo = -1, int token = -1, int timerSeq = -1, int tile = -1)
    {
        _events.Enqueue(new Ev(tick, _seq++, t, junimo, token, timerSeq, tile), (tick, _seq - 1));
    }

    public void Run()
    {
        while (_events.TryPeek(out var e, out _))
        {
            if (e.Tick > _endTick) break;
            _events.Dequeue();
            _now = e.Tick;
            switch (e.Type)
            {
                case EvType.SpawnCheck: OnSpawnCheck(); break;
                case EvType.HutPoke: OnHutPoke(); Schedule(_now + Cfg.HutPokeTicks, EvType.HutPoke); break;
                case EvType.PathArrival: OnPathArrival(e); break;
                case EvType.TimerPoke: OnTimerPoke(e); break;
                case EvType.CropGone: OnCropGone(e); break;
                case EvType.IdleTrigger: OnIdleTrigger(e); break;
            }
            if (!_dayMode && _remaining == 0) break;   // 清场即停（与旧口径一致，避免清场后空转污染指标）
        }
    }

    // ---------- 生成 [M2] JunimoSpawnHelper：cap → 60t pacing → 扫描有活 → 出生 ----------
    void OnSpawnCheck()
    {
        Schedule(_now + Cfg.SpawnStaggerTicks, EvType.SpawnCheck);
        _dbgSpawnChecks++;
        if (_alive >= _junimoCap) return;
        if (!HutScan()) return;
        _dbgScansFromSpawn++;
        Spawn();
    }

    void Spawn()
    {
        var j = new Junimo { id = _junimos.Count, tile = _m.Idx(_m.doorX, _m.doorY) };
        _junimos.Add(j); _alive++;
        // 构造函数走到附近空格后 tryToHarvestHere [V1:118]；模型：+2t 到达出生格
        Schedule(_now + 2, EvType.PathArrival, junimo: j.id, token: j.stateToken, tile: j.tile);
    }

    // ---------- 小屋 10 分钟 poke：[V3:286] vanilla 本体 + [M2] postfix = 双重 poke ----------
    void OnHutPoke()
    {
        for (int round = 0; round < 2; round++)
            for (int i = 0; i < _junimos.Count; i++)
            {
                var j = _junimos[i];
                if (!j.alive) continue;
                PokeToHarvest(j);
            }
    }

    void PokeToHarvest(Junimo j)
    {
        if (TimerActive(j)) return;                     // [V1:304] harvestTimer > 0 → 不决策
        if (_rng.NextDouble() < Cfg.PokeProb) Decide(j);
        else BecomeIdleIfIdle(j);
    }

    bool TimerActive(Junimo j) => j.timerUntil > _now;

    // ---------- 到达 [V1:275-293] tryToHarvestHere（mod 版 [M1] PatchTryToHarvestHere） ----------
    void OnPathArrival(Ev e)
    {
        var j = _junimos[e.Junimo];
        if (!j.alive || j.stateToken != e.Token) return;
        j.tile = e.Tile;
        j.path = null;
        TryToHarvestHere(j);
    }

    void TryToHarvestHere(Junimo j)
    {
        int t = j.tile;
        if (_m.cropReadyAt[t] >= 0 && _m.cropReadyAt[t] <= _now)
        {
            // [V1:286] harvestTimer=2000ms；[V1:506] 1000ms crossing 收获；到期 poke
            j.timerUntil = _now + Cfg.HarvestTicks; j.timerSeq++;
            j.timerTile = t;
            Schedule(_now + Cfg.CropGoneTicks, EvType.CropGone, junimo: j.id, timerSeq: j.timerSeq, tile: t);
            Schedule(_now + Cfg.HarvestTicks, EvType.TimerPoke, junimo: j.id, timerSeq: j.timerSeq);
            return;
        }

        // [M1] "nothing to do"（活没了 / 闲逛到达 / 出生格）
        if (_algo.HasClaims()) ReleaseOwner(j);          // [P1] 释放认领
        if (j.workPurpose == 1) _wastedArrivals++;       // 工作目标落空（deadArrive）
        if (j.workPurpose == 2) { Despawn(j); return; }  // [V1:338] junimoReachedHut → destroy

        j.timerUntil = _now + Cfg.NothingArrivalTicks; j.timerSeq++;  // 200ms [M1]
        j.timerTile = -1;
        Schedule(_now + Cfg.NothingArrivalTicks, EvType.TimerPoke, junimo: j.id, timerSeq: j.timerSeq);
        if (_rng.NextDouble() < Cfg.PokeProb) Decide(j); // [M1] 分支内先 pokeToHarvest（0.7）
        else BecomeIdleIfIdle(j);
    }

    void OnCropGone(Ev e)
    {
        var j = _junimos[e.Junimo];
        if (!j.alive || j.timerSeq != e.TimerSeq) return;
        int t = e.Tile;
        if (_m.cropReadyAt[t] >= 0 && _m.cropReadyAt[t] <= _now)
        {
            _m.cropReadyAt[t] = -1;                      // destroyCrop [V1:517]
            _harvested++; _remaining--;
            if (_remaining == 0 && _clearTick < 0) _clearTick = _now;
            if (_algo.HasClaims() && j.claimedTile == t) ReleaseOwner(j); // [P1] 收获释放
        }
        else if (j.workPurpose == 1) _shadowStands++;   // 站满计时发现被同伴收走（扎堆空等）
    }

    void OnTimerPoke(Ev e)
    {
        var j = _junimos[e.Junimo];
        if (!j.alive || j.timerSeq != e.TimerSeq) return;
        j.timerUntil = -1;
        PokeToHarvest(j);                                // [V1:589]
    }

    void OnIdleTrigger(Ev e)
    {
        var j = _junimos[e.Junimo];
        if (!j.alive || j.stateToken != e.Token) return;
        if (j.path != null || TimerActive(j)) return;
        _dbgIdleTriggers++;
        Decide(j);                                       // [V1:649] 无 0.7 门
    }

    void BecomeIdleIfIdle(Junimo j)
    {
        if (j.path != null || TimerActive(j)) return;
        j.stateToken++;
        Schedule(_now + Geom(Cfg.IdleMeanTicks), EvType.IdleTrigger, junimo: j.id, token: j.stateToken);
    }

    int Geom(int mean)
    {
        double u = _rng.NextDouble();
        if (u <= 1e-12) u = 1e-12;
        return Math.Max(1, (int)Math.Round(Math.Log(1 - u) / Math.Log(1 - 1.0 / mean)));
    }

    void Despawn(Junimo j)
    {
        j.alive = false; _alive--; _despawns++;
        j.stateToken++;
        if (_algo.HasClaims() && j.claimedTile >= 0) ReleaseOwner(j); // 近似：游戏内 ≤430t 后经 poke 释放
    }

    // =============================================================================
    // 决策 = pathfindToNewCrop（legacy [M1] / 补丁 [P1]）
    // =============================================================================
    void Decide(Junimo j)
    {
        if (!j.alive) return;

        // ---- 限速 [P1]：now < NextDecisionTick → 本次决策丢弃（保留当前路径/原地）----
        _dbgDecisions++;
        if (_algo.HasCap() && _now < j.nextDecision) { _dbgCapDrops++; return; }

        // ---- 3.5% 闲逛 [V1:377][M1]（补丁中位于限速之后、门控之前 [P1]）----
        if (_rng.NextDouble() < Cfg.WanderProb)
        {
            _dbgWanderRolls++;
            if (_algo.HasCap()) j.nextDecision = _now + Cfg.CapSuccessTicks;
            Wander(j);
            return;
        }

        // ---- 扫描门控 [P1]：缓存 60t 的 areThereMatureCropsWithinRadius ----
        if (_algo.HasGate())
        {
            if (!HutScan())
            {
                if (_algo.HasCap()) j.nextDecision = _now + Cfg.CapSuccessTicks;
                FallbackRoll(j, tryLkc: false);
                return;
            }
            _dbgScansFromDecide++;
        }

        // ---- 主搜索 [V1:382][M1] endFunction=IsActionable, limit 100 ----
        bool claims = _algo.HasClaims();
        var path = new List<int>();
        _dbgSearchMain++;
        bool found = Search.FindPath(_m, j.tile, t => IsActionableEnd(t, j, claims), j.tile, path, out int pops1);
        _expansions += pops1; RecordPops(pops1);
        bool outside = found && !InRadius(path[^1]);

        if (claims && !found)
        {
            // ---- 拥挤影子重搜 [P1]：全被认领时无过滤重搜 ----
            _dbgSearchShadow++; _claimShadowSearches++;
            _suppressFilter = true;
            path = new List<int>();
            found = Search.FindPath(_m, j.tile, t => IsActionableEnd(t, j, false), j.tile, path, out int pops2);
            _expansions += pops2; RecordPops(pops2);
            _suppressFilter = false;
            outside = found && !InRadius(path[^1]);
        }

        if (found && !outside)
        {
            if (_algo.HasCap()) j.nextDecision = _now + Cfg.CapSuccessTicks;
            int end = path[^1];
            if (claims && !IsClaimedByOther(end, j)) TryClaim(j, end);
            StartWalk(j, path, purpose: 1);
            return;
        }

        // ---- 搜索失败 [V1:384][P1] ----
        if (_algo.HasCap()) j.nextDecision = _now + Cfg.CapFailTicks;
        if (claims) ReleaseOwner(j);
        FallbackRoll(j, tryLkc: true);
    }

    // [V1:386-398] / [P1] VanillaFallbackRoll：50% lkc → 25% 回家 → 闲逛
    void FallbackRoll(Junimo j, bool tryLkc)
    {
        bool lkcRoll = tryLkc && _rng.NextDouble() < 0.5;   // 0.5 先掷（短路语义同源码）
        if (lkcRoll && _lkc >= 0)
        {
            _dbgSearchLkc++;
            var path = new List<int>();
            bool ok = Search.FindPath(_m, j.tile, t => t == _lkc, _lkc, path, out int pops);
            _expansions += pops; RecordPops(pops);
            if (ok) { StartWalk(j, path, purpose: 1); return; }  // lkc 终点无半径复查 [M1]
            BecomeIdleIfIdle(j);
            return;
        }
        if (_rng.NextDouble() < 0.25)
        {
            _dbgSearchHome++;
            var path = new List<int>();
            int door = _m.Idx(_m.doorX, _m.doorY);
            bool ok = Search.FindPath(_m, j.tile, t => t == door, door, path, out int pops);
            _expansions += pops; RecordPops(pops);
            if (ok) { StartWalk(j, path, purpose: 2); return; }
            Despawn(j);                                      // [V1:421] path null → destroy
            return;
        }
        Wander(j);
    }

    // [V1:716] + [M1]：vanilla 本体 1 次构造（被 postfix 覆盖）+ postfix do-while 1..R 次
    void Wander(Junimo j)
    {
        _dbgSearchWander++;
        {   // vanilla body（结果被 postfix 无条件覆盖，但计入 A* 成本）
            int end = RandomInBox();
            Search.FindPath(_m, j.tile, t => t == end, end, new List<int>(), out int pops);
            _expansions += pops; RecordPops(pops);
        }
        int R = _algo.WanderAttempts();
        for (int t = 0; t < R; t++)
        {
            int end = RandomInBox();
            var path = new List<int>();
            bool ok = Search.FindPath(_m, j.tile, x => x == end, end, path, out int pops);
            _expansions += pops; RecordPops(pops);
            if (ok) { StartWalk(j, path, purpose: 0); return; }
        }
        BecomeIdleIfIdle(j);
    }

    int RandomInBox()
    {
        int x = _m.doorX + _rng.Next(-_radius, _radius + 1);   // [V1:721]
        int y = _m.doorY + _rng.Next(-_radius, _radius + 1);
        return _m.Idx(Math.Clamp(x, 0, _m.W - 1), Math.Clamp(y, 0, _m.H - 1));
    }

    bool InRadius(int tile)
    {
        int dx = Math.Abs(_m.XOf(tile) - _m.doorX), dy = Math.Abs(_m.YOf(tile) - _m.doorY);
        return dx <= _radius && dy <= _radius;                 // [V1:384]
    }

    bool IsActionableEnd(int tile, Junimo self, bool useClaims)
    {
        bool ok = _m.cropReadyAt[tile] >= 0 && _m.cropReadyAt[tile] <= _now;  // [M1][M3]
        if (!ok) return false;
        if (useClaims && _algo.HasClaims() && !_suppressFilter)
            return !IsClaimedByOther(tile, self);              // [P1] PatchFindingCropEnd
        return true;
    }

    void StartWalk(Junimo j, List<int> path, int purpose)
    {
        if (purpose == 0) _dbgWalksWander++; else if (purpose == 1) { _dbgWalksWork++; _workWalkTiles += path.Count - 1; } else _dbgWalksHome++;
        j.workPurpose = purpose;
        j.path = path;
        j.stateToken++;
        long ticks = Math.Max(1, (long)Math.Round((path.Count - 1) * Cfg.TicksPerTile));
        Schedule(_now + ticks, EvType.PathArrival, junimo: j.id, token: j.stateToken, tile: path[^1]);
    }

    // ---------- 小屋扫描 [M2]：60t 缓存；仅 miss 时全格行主序扫并更新 lkc ----------
    bool HutScan()
    {
        if (_scanValid && _now - _scanTick < Cfg.ScanCooldownTicks) return _scanFound;
        _scanFound = false; _lkc = -1;
        int r = _radius;
        for (int x = _m.doorX - r; x <= _m.doorX + r && !_scanFound; x++)
            for (int y = _m.doorY - r; y <= _m.doorY + r; y++)
            {
                _scanOps++;
                if (x < 0 || y < 0 || x >= _m.W || y >= _m.H) continue;
                int i = _m.Idx(x, y);
                if (_m.cropReadyAt[i] >= 0 && _m.cropReadyAt[i] <= _now)
                { _scanFound = true; _lkc = i; break; }
            }
        _scanTick = _now; _scanValid = true;
        return _scanFound;
    }

    // ---------- 认领表 [P1] CropClaims ----------
    bool IsClaimedByOther(int tile, Junimo self)
    {
        if (_claims.Count == 0) return false;
        if (!_claims.TryGetValue(tile, out var c)) return false;
        if (_now - c.tick > Cfg.ClaimExpiryTicks) { _claims.Remove(tile); return false; }
        return c.owner != self.id;
    }

    void TryClaim(Junimo j, int tile)
    {
        if (_claims.TryGetValue(tile, out var c) && _now - c.tick > Cfg.ClaimExpiryTicks) _claims.Remove(tile);
        if (IsClaimedByOther(tile, j)) return;
        ReleaseOwner(j);                                    // 一只祝尼魔同时只认领一格
        _claims[tile] = (j.id, _now);
        j.claimedTile = tile;
    }

    void ReleaseOwner(Junimo j)
    {
        if (j.claimedTile < 0) return;
        if (_claims.TryGetValue(j.claimedTile, out var c) && c.owner == j.id) _claims.Remove(j.claimedTile);
        j.claimedTile = -1;
    }

    void RecordPops(int pops)
    {
        if (_dayMode && _now >= 0 && _now < _tickPops.Length) _tickPops[_now] += pops;
    }

    // ---------- 结果 ----------
    public (long clearTicks, int crops, long expansions, long wasted, int despawns, long scanOps, bool dnf, int harvested) Result
    {
        get
        {
            bool dnf = !_dayMode && _clearTick < 0;
            return ((long)(_clearTick < 0 ? Cfg.SimCapTicks : _clearTick), _crops.Count, _expansions,
                    _wastedArrivals, _despawns, _scanOps, dnf, _harvested);
        }
    }

    public string StatsLine() =>
        $"shadowStands={_shadowStands} workWalks={_dbgWalksWork} workTiles={_workWalkTiles} " +
        $"shadowSearches={_claimShadowSearches} wastedBounce={_wastedArrivals}";

    public string DebugLine() =>
        $"spawnchecks={_dbgSpawnChecks} scanSpawn={_dbgScansFromSpawn} scanDecide={_dbgScansFromDecide} " +
        $"decisions={_dbgDecisions} capDrops={_dbgCapDrops} wanderRolls={_dbgWanderRolls} idle={_dbgIdleTriggers} " +
        $"search main/shadow/lkc/home/wander={_dbgSearchMain}/{_dbgSearchShadow}/{_dbgSearchLkc}/{_dbgSearchHome}/{_dbgSearchWander} " +
        $"walks work/wander/home={_dbgWalksWork}/{_dbgWalksWander}/{_dbgWalksHome}";

    public long PeakPops1s()
    {
        if (!_dayMode) return 0;
        long peak = 0, sum = 0;
        for (int i = 0; i < _tickPops.Length; i++)
        {
            sum += _tickPops[i];
            if (i >= 60) sum -= _tickPops[i - 60];
            if (sum > peak) peak = sum;
        }
        return peak;
    }
}

public static class Program
{
    // 稳定跨进程哈希（FNV-1a），保证同参数同 run 可复现
    static uint Mix(uint h, uint v) { h ^= v; h *= 16777619; return h; }
    static uint Hash(uint h, params int[] vs) { foreach (var v in vs) h = Mix(h, (uint)v); return h; }

    public static void Main(string[] args)
    {
        int runs = int.TryParse(Environment.GetEnvironmentVariable("SIM2_RUNS"), out var r1) ? r1 : 50;
        int cpuRuns = int.TryParse(Environment.GetEnvironmentVariable("SIM2_CPU_RUNS"), out var r2) ? r2 : 30;
        string outDir = Environment.GetEnvironmentVariable("SIM2_OUT") ?? AppContext.BaseDirectory;
        bool quick = Environment.GetEnvironmentVariable("SIM2_QUICK") == "1";
        Sim.Debug = Environment.GetEnvironmentVariable("SIM2_DEBUG") == "1";
        if (Environment.GetEnvironmentVariable("SIM2_STATS") == "1") { RunStats(runs); return; }
        Directory.CreateDirectory(outDir);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string resPath = Path.Combine(outDir, "results.csv");
        string cpuPath = Path.Combine(outDir, "cpu_results.csv");

        using (var res = new StreamWriter(resPath))
        using (var cpu = new StreamWriter(cpuPath))
        {
            res.WriteLine("algo,radius,junimos,density,run,clear_ticks,crops,expansions,wasted_arrivals,respawns,scan_ops,dnf");
            cpu.WriteLine("algo,junimos,obstacles,run,total_pops,peak_pops_1s");

            foreach (var algo in new[] { Algo.VanillaMod, Algo.CapOnly, Algo.PatchShipped, Algo.PatchNocap })
            {
                if (Sim.Debug)
                {
                    uint dseed = Hash(2166136261, 1, (int)algo, Cfg.Radius, 4, 75, 2);
                    var dsim = new Sim(algo, 4, Cfg.Radius, (int)dseed, 0.05, 0.75, false);
                    dsim.Run();
                    Console.WriteLine($"DEBUG {algo.Name()} seed={dseed}: {dsim.Result}");
                    Console.WriteLine($"  {dsim.DebugLine()}");
                    continue;
                }
                // ---- 吞吐 ----
                foreach (var density in CfgThroughputDensities(quick))
                    foreach (int jn in CfgThroughputJunimos(quick))
                        for (int run = 0; run < runs; run++)
                        {
                            // 配对种子：不含算法——同 run 所有算法看到同一张地图
                            uint seed = Hash(2166136261, 1, Cfg.Radius, jn, (int)Math.Round(density * 100), run);
                            var sim = new Sim(algo, jn, Cfg.Radius, (int)seed, obstacleRate: 0.05, cropDensity: density, dayMode: false);
                            sim.Run();
                            var x = sim.Result;
                            res.WriteLine($"{algo.Name()},{Cfg.Radius},{jn},{density},{run},{x.clearTicks},{x.crops},{x.expansions},{x.wasted},{x.despawns},{x.scanOps},{x.dnf}");
                        }
                // ---- CPU 日 ----
                if (!quick) foreach (int jn in new[] { 2, 6 })
                        foreach (double obs in new[] { 0.05, 0.25 })
                            for (int run = 0; run < cpuRuns; run++)
                            {
                                uint seed = Hash(2166136261, 2, Cfg.Radius, jn, (int)Math.Round(obs * 100), run);
                                var sim = new Sim(algo, jn, Cfg.Radius, (int)seed, obstacleRate: obs, cropDensity: 0, dayMode: true);
                                sim.Run();
                                cpu.WriteLine($"{algo.Name()},{jn},{obs},{run},{sim.Result.expansions},{sim.PeakPops1s()}");
                            }
                Console.WriteLine($"[{algo.Name()}] done at {sw.ElapsedMilliseconds} ms");
            }
        }
        Console.WriteLine($"sim2 finished in {sw.Elapsed.TotalSeconds:F1}s -> {resPath}");
    }

    static void RunStats(int runs)
    {
        foreach (var (jn, dens) in new[] { (4, 0.75), (8, 1.00), (2, 0.10) })
        {
            Console.WriteLine($"\n[stats] junimos={jn} density={dens} ({runs} paired runs)");
            foreach (var algo in new[] { Algo.VanillaMod, Algo.CapOnly, Algo.PatchShipped, Algo.PatchNocap })
            {
                long ss = 0, ww = 0, wt = 0, cs = 0, wa = 0; double ct = 0;
                for (int run = 0; run < runs; run++)
                {
                    uint seed = Hash(2166136261, 1, Cfg.Radius, jn, (int)Math.Round(dens * 100), run);
                    var sim = new Sim(algo, jn, Cfg.Radius, (int)seed, 0.05, dens, false);
                    sim.Run();
                    var parts = sim.StatsLine();
                    ss += long.Parse(parts.Split("shadowStands=")[1].Split(' ')[0]);
                    ww += long.Parse(parts.Split("workWalks=")[1].Split(' ')[0]);
                    wt += long.Parse(parts.Split("workTiles=")[1].Split(' ')[0]);
                    cs += long.Parse(parts.Split("shadowSearches=")[1].Split(' ')[0]);
                    wa += long.Parse(parts.Split("wastedBounce=")[1].Split(' ')[0]);
                    ct += sim.Result.clearTicks;
                }
                Console.WriteLine($"  {algo.Name(),-13} clear={ct / runs:F0}t shadowStands={ss / (double)runs:F1} workWalks={ww / (double)runs:F1} meanTrip={(ww > 0 ? (double)wt / ww : 0):F1}t shadowSearches={cs / (double)runs:F1} wastedBounce={wa / (double)runs:F1}");
            }
        }
    }

    static double[] CfgThroughputDensities(bool quick) => quick ? new[] { 0.75 } : new[] { 0.10, 0.75, 1.00 };
    static int[] CfgThroughputJunimos(bool quick) => quick ? new[] { 4 } : new[] { 2, 4, 6, 8 };
}
