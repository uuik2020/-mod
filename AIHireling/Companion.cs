using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace AIHireling;

public enum AiMode
{
    自动,
    跟随,
    农场,
    战斗,
    农场清理,
    砍树,
    挖矿,
    钓鱼,
    待命,
}

public enum JobType
{
    无,
    回家,
    打怪,
    挖矿,
    砍树,
    钓鱼,
    采集,
    收获,
    浇水,
    翻地,
    播种,
    清理,
    送背包,
    存背包,
}

/// <summary>
/// 钓鱼的流程阶段。以前只有一个 FishStartedAt 计时，站没站到岸边、
/// 有没有抛竿、鱼咬没咬钩全都是同一个"钓鱼中"标签，玩家看到的像是卡住了。
/// 拆成阶段后：逻辑按阶段推进，渲染器按阶段播抛竿/浮漂/水花动画。
/// </summary>
public enum FishPhase
{
    无,
    走去水边,
    抛竿,
    等待,
    咬钩,
    收竿,
}

public class Job
{
    public JobType Type { get; set; } = JobType.无;
    public Vector2 Tile { get; set; } = Vector2.Zero;
    public Monster TargetMonster { get; set; }
    public Debris TargetDebris { get; set; }
    public StardewValley.Object TargetObject { get; set; }
    public TerrainFeature TargetFeature { get; set; }
    public int Progress { get; set; }
    public double CreatedAt { get; set; }
}

/// <summary>
/// AI 雇工实体。不挂到 GameLocation.characters（避免被游戏 AI/日程系统接管），
/// 由 ModEntry 在 UpdateTicked 驱动、在 RenderedWorld 手工绘制，
/// 移动直接用 Position 插值 + isCollidingPosition 碰撞检测。
/// </summary>
public class Companion
{
    // ---- 状态 ----
    public Vector2 Position;
    public Vector2 HomeTile;

    /// <summary>AI 当前所在地图。只有玩家在同一张地图时才更新/绘制，
    /// 否则 AI 会按别的地图的坐标穿墙乱走（典型 bug：追进屋里的农舍）。</summary>
    public GameLocation Location;

    public int Health;
    public int Facing = 2;
    public bool Active;
    public bool Dead;
    public AiMode Mode = AiMode.自动;
    public Job Job = new Job();

    /// <summary>AI 背包：干活捡到的东西先放身上，玩家对话免费领取。
    /// 内存态，存档时由 ModEntry.SaveDataToDisk 同步进 SaveData.背包物品。</summary>
    public List<Item> Inventory = new List<Item>();

    public bool Sleeping;
    public int DeadDay = -1;

    // ---- 计时 ----
    public double Clock;
    public double NextThink;
    public double NextAttack;
    public double InvulnerableUntil;
    public double AttackAnimUntil;
    public double ActionCooldownUntil;
    public double FishStartedAt = -1.0;
    public Vector2 FishTile;

    /// <summary>体力（2.4.0）。原版玩家是 270，AI 抛一竿扣 8 - 钓鱼等级*0.1，
    /// 睡一觉回满、站着歇着慢慢回。没体力就不去钓鱼（不再无限白嫖）。</summary>
    public float Energy;

    /// <summary>这一竿有没有真的挂上鱼饵：没饵也能钓，但咬钩时间翻倍（同原版）。</summary>
    public bool FishHadBait;

    // ---- 钓鱼流程（渲染器依赖这些字段画动画，改动请同步 CompanionRenderer）----
    /// <summary>当前钓鱼阶段。</summary>
    public FishPhase Phase = FishPhase.无;
    /// <summary>当前阶段开始的时刻（Clock 毫秒）。</summary>
    public double PhaseStartedAt;
    /// <summary>本次"等鱼上钩"的总时长（毫秒），进度条/剩余秒数用。</summary>
    public double FishWaitMs = 1.0;
    /// <summary>刚放弃过一片水时的休息截止时间：这段时间不再接钓鱼活，
    /// 免得头顶一直挂着「去水边」看起来像卡死。</summary>
    private double _fishRestUntil;
    /// <summary>"走去水边"期间原地不动的累计时长（毫秒），超过 2.5 秒就放弃这片水。</summary>
    private double _fishWalkStuck;
    /// <summary>连续放弃了几次（每次都把休息时间拉长，别在原地无限重试）。</summary>
    private int _fishFailStreak;
    /// <summary>走路诊断日志的节流时间点。</summary>
    private double _fishLogAt;
    private Item _pendingCatch;

    private Vector2 _lastPos;
    private double _stuckTimer;
    private double _noMoveTotal;
    private double _walkAnimTime;

    // ---- 寻路 ----
    /// <summary>当前这次接近里"已经挪了几次"（跳满 3 次就认输）。</summary>
    private int _hopCount;

    /// <summary>当前规划路径（瓦片序列，从近到远）。</summary>
    private List<Vector2> _path = new List<Vector2>();
    private Vector2 _pathGoal = new Vector2(-1f, -1f);
    private double _nextPathTime;

    /// <summary>走不到的目标：拉黑一段时间，避免"卡住→放弃→又选同一个→再卡"的死循环。</summary>
    private readonly Dictionary<Vector2, double> _avoidUntil = new Dictionary<Vector2, double>();

    // 砍树计数（诊断 + 兜底强制放倒）
    private Vector2 _chopTile = new Vector2(-1f, -1f);
    private int _chopSwings;

    // 靠近目标的超时保护
    private Vector2 _approachTile = new Vector2(-1f, -1f);
    private double _approachSince;
    private float _approachBest;

    /// <summary>
    /// 靠近目标的超时保护：一路在动但 6 秒都靠不近（绕着树丛打转、被围住），
    /// 就把目标拉黑换一个。UpdateStuck 只在"完全没动"时生效，这里补上"走得动但走不到"。
    /// </summary>
    private bool ApproachTimeout(Vector2 tile, double limitMs = 6000.0)
    {
        if (tile != _approachTile)
        {
            _approachTile = tile;
            _approachSince = Clock;
            _approachBest = float.MaxValue;
        }

        float d = Vector2.Distance(Tile, tile);
        if (d < _approachBest - 0.05f)
        {
            _approachBest = d;
            _approachSince = Clock;   // 有进展就重新计时
            _hopCount = 0;
        }

        // 2.5 秒一点都没靠近 → 先朝目标挪一小步（最多 3 次），别急着放弃/别卡死。
        // 这一条是"任何位置都不卡"的保底：墙角抖动、门口挤不进去都能靠它脱身。
        if (_hopCount < 3 && Clock - _approachSince > 2500.0)
        {
            Vector2 from = Tile;
            if (HopToward(tile, 2))
            {
                _hopCount++;
                _approachBest = Vector2.Distance(Tile, tile);
                _approachSince = Clock;
                CLog.Act($"[寻路] 卡住了，朝目标挪一步 ({from.X},{from.Y}) → ({Tile.X},{Tile.Y})，"
                    + $"还差 {_approachBest:0.#} 格");
                return false;
            }
        }

        if (Clock - _approachSince > limitMs)
        {
            // 关键：调用方传进来的是"要站过去的那一格(stand)"，而目标搜索是按
            // "目标格(Job.Tile)"过滤黑名单的——只黑 stand 等于没黑，下一次决策又会
            // 选中同一个目标，日志里就出现"每 6 秒重复同一格"的死循环。两个都黑掉。
            Avoid(tile);
            Avoid(Job.Tile);
            Job.Type = JobType.无;
            _path.Clear();
            _approachTile = new Vector2(-1f, -1f);
            CLog.Act($"[寻路] {(int)(limitMs / 1000)} 秒靠不近 ({tile.X},{tile.Y})，"
                + $"拉黑目标 ({Job.Tile.X},{Job.Tile.Y}) 换活");
            return true;
        }
        return false;
    }

    /// <summary>
    /// 卡死时的保底手段：找一个"能站、且比现在更靠近目标"的格子，直接挪过去（最多 2 格）。
    /// 手写 BFS + 碰撞盒在墙角/门口/栅栏夹缝里经常迈不出第一步（帧间来回抖动），
    /// 与其继续调超时，不如在**判定真的没进展**时朝目标挪一小步——不穿墙、不进水，
    /// 但能保证任何地形都不会永久卡住。
    /// </summary>
    private bool HopToward(Vector2 targetTile, int maxTiles)
    {
        Vector2 here = Tile;
        float bestScore = Vector2.Distance(here, targetTile);
        Vector2 best = Vector2.Zero;

        for (int r = 1; r <= maxTiles; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        continue;
                    Vector2 t = here + new Vector2(dx, dy);
                    if (t.X < 0f || t.Y < 0f)
                        continue;
                    if (!CanStandAt(t))
                        continue;
                    float d = Vector2.Distance(t, targetTile);
                    if (d < bestScore - 0.5f)
                    {
                        bestScore = d;
                        best = t;
                    }
                }
            }
            if (best != Vector2.Zero)
                break;
        }

        if (best == Vector2.Zero)
            return false;

        Position = best * 64f;
        _path.Clear();
        _pathGoal = new Vector2(-1f, -1f);
        _lastPos = Position;
        return true;
    }

    private bool IsAvoided(Vector2 tile)
    {
        return _avoidUntil.TryGetValue(tile, out double until) && Clock < until;
    }

    private void Avoid(Vector2 tile)
    {
        if (tile == Vector2.Zero)
            return;
        _avoidUntil[tile] = Clock + 60000.0;
        if (_avoidUntil.Count > 40)
        {
            // 顺手清理过期记录，避免长局游戏里越攒越多
            List<Vector2> stale = _avoidUntil.Where(kv => kv.Value <= Clock).Select(kv => kv.Key).ToList();
            foreach (Vector2 k in stale)
                _avoidUntil.Remove(k);
        }
    }

    public int MaxHealth => ModEntry.Config.AI血量;

    /// <summary>体力上限（配置里可调，默认同原版 270）。</summary>
    public int MaxEnergy => Math.Max(20, ModEntry.Config.AI体力上限);

    /// <summary>体力比例 0..1（渲染体力条用）。</summary>
    public float EnergyRatio => MathHelper.Clamp(Energy / MaxEnergy, 0f, 1f);

    /// <summary>「干不了活（没工具 / 没体力）」提示的节流时间点（45 秒一次，别刷屏）。</summary>
    private double _warnUntil;

    /// <summary>背包容量（组）。0 = 不限，永不喊满。</summary>
    public int BagCapacity => ModEntry.Config.背包容量组数;

    /// <summary>背包是不是塞满了（到容量上限）。</summary>
    public bool IsBagFull => BagCapacity > 0 && InventoryGroups >= BagCapacity;

    /// <summary>玩家说过"先放着"之后，过一段时间再提醒（毫秒）。</summary>
    public void PostponeBagAlert(double ms)
    {
        _bagTellCooldown = Clock + Math.Max(1000.0, ms);
    }

    private double _bagTellCooldown;

    /// <summary>自动往箱子里塞东西的冷却（避免箱子满/被拆时每帧重试）。</summary>
    private double _bagStoreCooldown;

    public Vector2 Tile => new Vector2((int)(Position.X / 64f), (int)(Position.Y / 64f));

    /// <summary>攻击动画进度 0..1，用于渲染挥砍弧线。</summary>
    public double AttackAnimPhase
    {
        get
        {
            if (Clock >= AttackAnimUntil) return -1;
            double span = 220.0;
            return Math.Max(0, 1 - (AttackAnimUntil - Clock) / span);
        }
    }

    // =====================================================================
    // 钓鱼阶段查询（纯渲染用，不参与决策）
    // =====================================================================

    /// <summary>当前阶段已过多久（毫秒）。</summary>
    public double PhaseElapsed => Math.Max(0.0, Clock - PhaseStartedAt);

    /// <summary>阶段内进度 0..1（抛竿 420ms / 咬钩 450ms / 收竿 350ms）。</summary>
    public float PhaseRatio(double spanMs)
    {
        if (spanMs <= 1.0)
            return 1f;
        return (float)Math.Clamp(PhaseElapsed / spanMs, 0.0, 1.0);
    }

    /// <summary>钓鱼总进度 0..1：抛竿 15% + 等待 65% + 收竿 20%，给头顶进度条用。</summary>
    public float FishOverallRatio
    {
        get
        {
            switch (Phase)
            {
                case FishPhase.走去水边:
                case FishPhase.无:
                    return 0f;
                case FishPhase.抛竿:
                    return PhaseRatio(420.0) * 0.15f;
                case FishPhase.等待:
                    return 0.15f + (float)Math.Clamp(PhaseElapsed / Math.Max(1.0, FishWaitMs), 0.0, 1.0) * 0.65f;
                case FishPhase.咬钩:
                    return 0.80f + PhaseRatio(450.0) * 0.05f;
                case FishPhase.收竿:
                    return 0.85f + PhaseRatio(350.0) * 0.15f;
                default:
                    return 0f;
            }
        }
    }

    /// <summary>等待上钩还剩几秒（头顶标签显示倒计时）。</summary>
    public double FishWaitSecondsLeft
    {
        get
        {
            if (Phase != FishPhase.等待)
                return 0.0;
            return Math.Max(0.0, (FishWaitMs - PhaseElapsed) / 1000.0);
        }
    }

    /// <summary>清空钓鱼状态（换图/放弃/钓完都走这里，避免半截状态残留）。</summary>
    public void ResetFishing()
    {
        FishStartedAt = -1.0;
        FishTile = Vector2.Zero;
        Phase = FishPhase.无;
        PhaseStartedAt = 0.0;
        _fishWalkStuck = 0.0;
        _pendingCatch = null;
    }

    private static void PlaySound(string cue)
    {
        // 音效名写错时原版会抛异常，这里静音兜底，别让一个音效卡死整条钓鱼流程
        try { Game1.playSound(cue); } catch { }
    }

    public void Update(double dtMs)
    {
        Clock += dtMs;
        _walkAnimTime += dtMs;

        if (!Active || Dead || Game1.player == null || Game1.currentLocation == null)
            return;

        // AI 不在玩家所在地图：停止思考与行动（防止跨地图按裸坐标移动/穿墙）
        if (Location != null && Game1.currentLocation != Location)
            return;

        if (Clock >= NextThink)
        {
            NextThink = Clock + 350.0;
            Job previous = Job;
            try { Think(); }
            catch (Exception ex)
            {
                CLog.Warn($"AI思考异常: {ex}");
            }
            // 重新选中同一个目标时继承打击进度。否则挖矿/清理这种要打两下的
            // 动作会被每 350ms 的重新决策无限重置进度，永远打不完（经典 bug）。
            if (Job.Type != JobType.无 && Job.Type == previous.Type
                && Job.Tile != Vector2.Zero && Job.Tile == previous.Tile)
            {
                Job.Progress = previous.Progress;
            }
        }

        // 钓鱼状态只在"钓鱼任务"里有效：切模式、回家、换目标都要收竿，
        // 否则渲染器会一直挂着一根鱼竿、头顶一直显示钓鱼相关文案。
        if (Job.Type != JobType.钓鱼 && Phase != FishPhase.无)
            ResetFishing();

        try { ExecuteJob(dtMs); }
        catch (Exception ex)
        {
            CLog.Warn($"AI执行异常: {ex}");
            Job.Type = JobType.无;
        }

        UpdateMonsterDamage();
        UpdateStuck(dtMs);
        RegenStamina(dtMs);
    }

    // =====================================================================
    // 钓鱼的花费（2.4.0）：鱼竿 / 鱼饵 / 体力
    // 原版钓鱼不是白来的：手里得有竿、每竿要挂饵、抛竿还扣体力。
    // AI 以前三样都不要，站 9 秒就凭空摸出一条鱼，玩家看着就是作弊。
    // =====================================================================

    /// <summary>找一根能用的鱼竿：AI 自己背包优先，其次玩家背包。没有返回 null。</summary>
    public FishingRod FindFishingRod()
    {
        foreach (Item item in Inventory)
        {
            if (item is FishingRod rod)
                return rod;
        }
        if (Game1.player != null)
        {
            foreach (Item item in Game1.player.Items)
            {
                if (item is FishingRod rod)
                    return rod;
            }
        }
        return null;
    }

    /// <summary>原版一次抛竿的体力消耗：8 - 钓鱼等级*0.1（等级取玩家的，AI 借用玩家的竿）。</summary>
    public float CastEnergyCost()
    {
        float level = Game1.player?.fishingLevel?.Value ?? 0;
        return Math.Max(1f, 8f - level * 0.1f);
    }

    /// <summary>现在能不能钓鱼；不能就把原因（"没鱼竿" / "没体力"）返回，能钓返回 null。</summary>
    public string FishingBlockReason()
    {
        if (ModEntry.Config.钓鱼需要鱼竿 && FindFishingRod() == null)
            return "没鱼竿";
        if (ModEntry.Config.钓鱼消耗体力 && Energy < CastEnergyCost())
            return "没体力";
        return null;
    }

    private static bool IsBait(Item item)
    {
        return item is StardewValley.Object obj
            && obj.Category == StardewValley.Object.baitCategory
            && obj.Stack > 0;
    }

    /// <summary>AI 背包 + 玩家背包里一共有几个鱼饵。</summary>
    public int CountBait()
    {
        int n = 0;
        foreach (Item item in Inventory)
        {
            if (IsBait(item))
                n += item.Stack;
        }
        if (Game1.player != null)
        {
            foreach (Item item in Game1.player.Items)
            {
                if (IsBait(item))
                    n += item.Stack;
            }
        }
        return n;
    }

    /// <summary>扣 1 个鱼饵：AI 背包优先，其次玩家背包（用玩家的竿就得用玩家的饵）。</summary>
    private bool TryConsumeBait(out int left)
    {
        for (int i = 0; i < Inventory.Count; i++)
        {
            if (!IsBait(Inventory[i]))
                continue;
            if (Inventory[i].Stack > 1)
                Inventory[i].Stack--;
            else
                Inventory.RemoveAt(i);
            left = CountBait();
            return true;
        }

        if (Game1.player != null)
        {
            var items = Game1.player.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (!IsBait(items[i]))
                    continue;
                if (items[i].Stack > 1)
                    items[i].Stack--;
                else
                    Game1.player.removeItemFromInventory(items[i]);
                left = CountBait();
                return true;
            }
        }

        left = 0;
        return false;
    }

    /// <summary>
    /// 体力恢复：睡觉直接回满；待命 2 点/秒、跟随 1 点/秒（都是站着不动才算）。
    /// 干活时（砍树挖矿钓鱼）不恢复——原版玩家也是靠吃东西/睡觉回的。
    /// </summary>
    private void RegenStamina(double dtMs)
    {
        int max = MaxEnergy;
        if (Energy >= max)
        {
            Energy = max;
            return;
        }
        if (Sleeping)
        {
            Energy = max;
            return;
        }
        if (WasMoving || Dead)
            return;

        if (Mode == AiMode.待命)
            Energy += (float)(dtMs * 0.002);
        else if (Mode == AiMode.跟随)
            Energy += (float)(dtMs * 0.001);
        else if (Job.Type == JobType.无)
            Energy += (float)(dtMs * 0.0008);   // 自动模式下没活可干、站着歇

        if (Energy > max)
            Energy = max;
    }

    /// <summary>
    /// 工具类干活花费（2.5.0）：镐子同鱼竿——AI 背包优先，其次玩家背包。
    /// </summary>
    public Pickaxe FindPickaxe()
    {
        foreach (Item item in Inventory)
        {
            if (item is Pickaxe pick)
                return pick;
        }
        if (Game1.player != null)
        {
            foreach (Item item in Game1.player.Items)
            {
                if (item is Pickaxe pick)
                    return pick;
            }
        }
        return null;
    }

    /// <summary>原版敲一下镐子的体力消耗：2 - 挖矿等级*0.1（Pickaxe.cs 的公式，按基础镐算）。</summary>
    public float MiningEnergyCost()
    {
        float level = Game1.player?.miningLevel?.Value ?? 0;
        return Math.Max(1f, 2f - level * 0.1f);
    }

    /// <summary>现在能不能挖矿；不能就返回原因（"没镐子" / "没体力"）。</summary>
    public string MiningBlockReason()
    {
        if (ModEntry.Config.挖矿需要镐子 && FindPickaxe() == null)
            return "没镐子";
        if (ModEntry.Config.挖矿消耗体力 && Energy < MiningEnergyCost())
            return "没体力";
        return null;
    }

    /// <summary>
    /// 这条「干不了」提示现在能不能弹：按「一天最多 N 次、说满后静默 X 天」计数。
    /// 缺鱼竿 / 缺镐子 / 没体力 共用这套规则——2.5.3 之前是每 45 秒喊一次，
    /// 一直缺就一直喊，HUD 和 SMAPI 控制台跟着刷屏（玩家截图反馈）。
    /// </summary>
    private bool HintAllowed(string why)
    {
        ModConfig cfg = ModEntry.Config;
        int limit = cfg.提示每天最多次数;
        if (limit <= 0)
            return false;                     // 干脆不想看提示

        int today = SaveData.AbsoluteDay();
        SaveData data = ModEntry.Data;
        if (data == null)
            return true;
        if (data.提示记录 == null)
            data.提示记录 = new List<HintRecord>();

        HintRecord rec = data.提示记录.FirstOrDefault(r => r != null && r.Key == why);
        if (rec == null)
        {
            rec = new HintRecord { Key = why, 天 = -1, 静默到 = -1 };
            data.提示记录.Add(rec);
        }

        // 静默期没过：不弹、也不记账
        if (rec.静默到 > today)
            return false;

        // 新的一天：次数归零
        if (rec.天 != today)
        {
            rec.天 = today;
            rec.次数 = 0;
        }

        // 今天已经说满 → 开始静默
        if (rec.次数 >= limit)
        {
            rec.静默到 = today + Math.Max(0, cfg.提示静默天数);
            ModEntry.Instance?.SaveDataToDisk();
            CLog.Act($"[提示] 「{why}」今天已说 {rec.次数} 次，静默到第 {rec.静默到} 天（游戏日序号）");
            return false;
        }

        rec.次数++;
        ModEntry.Instance?.SaveDataToDisk();
        return true;
    }

    /// <summary>
    /// 干不了活（没工具 / 没体力）时跟玩家说一声。
    /// 1 分钟内不重复喊；同一条一天最多 N 次，说满后静默 X 天（可在设置里调）。
    /// </summary>
    private void WarnBlocked(string why)
    {
        if (Clock < _warnUntil)
            return;
        _warnUntil = Clock + 60000.0;

        if (!HintAllowed(why))
            return;

        string raw = why switch
        {
            "没鱼竿" => ModEntry.Config.文本_没鱼竿,
            "没镐子" => ModEntry.Config.文本_没镐子,
            _ => ModEntry.Config.文本_没体力,
        };
        string text = (raw ?? "")
            .Replace("{energy}", ((int)Energy).ToString())
            .Replace("{max}", MaxEnergy.ToString());
        if (string.IsNullOrWhiteSpace(text))
            text = why switch
            {
                "没鱼竿" => "我没有鱼竿，钓不了。",
                "没镐子" => "我没有镐子，砸不动石头。",
                _ => "我没体力了，得歇会儿。",
            };

        try
        {
            Game1.addHUDMessage(new HUDMessage(ModEntry.Config.AI名字 + "：" + text));
        }
        catch { }
        CLog.Info($"[干活] 干不了：{why}（体力 {Energy:0}/{MaxEnergy}，鱼饵 {CountBait()} 个）");
    }

    // =====================================================================
    // 决策
    // =====================================================================

    private void Think()
    {
        Job = new Job { CreatedAt = Clock };

        // 跨地图保护：玩家不在这张图时 AI 什么都不做
        if (Location != null && Game1.currentLocation != Location)
            return;

        if (!ModEntry.Instance.IsHired || Dead || !Active)
            return;

        ModConfig cfg = ModEntry.Config;

        if (OutsideWorkHours())
        {
            if (cfg.夜晚自动回家)
                Job.Type = JobType.回家;
            return;
        }

        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return;

        Vector2 playerTile = new Vector2((int)(Game1.player.Position.X / 64f), (int)(Game1.player.Position.Y / 64f));
        float distToPlayer = Vector2.Distance(Tile, playerTile);

        // 背包满了：附近有箱子就自己去塞（玩家造好箱子后自动生效，不用再对话选一次）；
        // 没箱子才跑去问玩家。
        if (IsBagFull)
        {
            if (Mode != AiMode.待命 && cfg.背包满了自动找箱子
                && Clock >= _bagStoreCooldown && FindChestForBag(12f) != null)
            {
                Job.Type = JobType.存背包;
                return;
            }

            // 待命/跟随模式下不主动跑去问，但玩家就在旁边时照样会说一句。
            if (cfg.背包满了自动找玩家 && Clock >= _bagTellCooldown)
            {
                bool playerNearby = distToPlayer <= 3f;
                if (playerNearby || (Mode != AiMode.待命 && Mode != AiMode.跟随))
                {
                    Job.Type = JobType.送背包;
                    return;
                }
            }
        }

        if (Mode == AiMode.待命 || Mode == AiMode.跟随)
            return;

        float workR = cfg.工作范围;
        bool inRange = distToPlayer <= workR;

        // 打怪（自动/战斗/挖矿/农场清理模式下优先保命）
        if (cfg.行为_打怪 && (Mode == AiMode.自动 || Mode == AiMode.战斗 || Mode == AiMode.挖矿
            || Mode == AiMode.农场清理 || Mode == AiMode.砍树))
        {
            Monster m = NearestMonster(cfg.索敌范围);
            if (m != null)
            {
                Job.Type = JobType.打怪;
                Job.TargetMonster = m;
                return;
            }
        }

        // 挖矿（敲碎可破坏物件）
        if (Mode == AiMode.挖矿 || (Mode == AiMode.自动 && cfg.行为_挖矿 && IsMine(loc)))
        {
            // 2.5.0：和钓鱼一样要镐子、要体力，空手砸石头不可能
            string mineBlocked = MiningBlockReason();
            if (mineBlocked != null)
            {
                WarnBlocked(mineBlocked);
                if (Mode == AiMode.挖矿)
                    return;   // 专门的挖矿模式下就歇着，别乱跑
            }
            else
            {
                var ore = NearestBreakableObject(workR);
                if (ore.HasValue)
                {
                    Job.Type = JobType.挖矿;
                    Job.TargetObject = ore.Value.Value;
                    Job.Tile = ore.Value.Key;
                    return;
                }
            }
        }

        // 农场清理（专用模式）：只清农场上的杂草/枯苗/树枝/石头/树桩
        if (Mode == AiMode.农场清理)
        {
            if (cfg.行为_清理 && loc is Farm)
            {
                var junk = FindCleanupTarget(workR);
                if (junk.HasValue)
                {
                    Job.Type = JobType.清理;
                    Job.Tile = junk.Value.Key;
                    object target = junk.Value.Value;
                    if (target is TerrainFeature tf) Job.TargetFeature = tf;
                    else if (target is StardewValley.Object co) Job.TargetObject = co;
                }
            }
            return;
        }

        // 农活（收获 > 浇水 > 播种 > 翻地）
        if ((Mode == AiMode.自动 || Mode == AiMode.农场) && inRange)
        {
            Job farm = FindFarmJob(playerTile, workR);
            if (farm != null)
            {
                Job = farm;
                return;
            }
        }

        // 采集（地面掉落物 + 机器成品）
        if (Mode == AiMode.自动 && inRange && cfg.行为_采集)
        {
            Debris d = NearestDebris(workR);
            if (d != null)
            {
                Job.Type = JobType.采集;
                Job.TargetDebris = d;
                return;
            }
            var machine = NearestMachine(workR);
            if (machine.HasValue)
            {
                Job.Type = JobType.采集;
                Job.TargetObject = machine.Value.Value;
                Job.Tile = machine.Value.Key;
                return;
            }
        }

        // 农场清理（自动模式）：先清地面（树枝/石头/杂草/枯苗/树桩），
        // 清完了才去砍树——不然一上来就砍树，地面一直乱七八糟。
        if (cfg.行为_清理 && inRange && loc is Farm)
        {
            var junk = FindCleanupTarget(workR);
            if (junk.HasValue)
            {
                Job.Type = JobType.清理;
                Job.Tile = junk.Value.Key;
                object target = junk.Value.Value;
                if (target is TerrainFeature tf) Job.TargetFeature = tf;
                else if (target is StardewValley.Object co) Job.TargetObject = co;
                return;
            }
        }

        // 砍树（自动模式里是"地面清干净了再动手"，而且只在农场砍——
        // 否则跟着玩家去镇上/森林时会把公共地图的树也放倒；专用砍树模式不限地图）
        bool wantChop = cfg.行为_砍树
            && (Mode == AiMode.砍树 || (Mode == AiMode.自动 && inRange && loc is Farm));
        if (wantChop)
        {
            var tree = NearestTree(workR);
            if (tree.HasValue)
            {
                Job.Type = JobType.砍树;
                Job.TargetFeature = tree.Value.Value;
                Job.Tile = tree.Value.Key;
                return;
            }
        }

        // 钓鱼
        if (cfg.行为_钓鱼 && (Mode == AiMode.自动 || Mode == AiMode.钓鱼) && Clock >= _fishRestUntil)
        {
            // 2.4.0：没鱼竿 / 没体力就别往水边跑了——原版没竿根本甩不出去。
            // 这里只提醒一句然后正常跟随，不会顶着「去水边」站着发呆。
            string blocked = FishingBlockReason();
            if (blocked != null)
            {
                WarnBlocked(blocked);
                return;
            }

            // 已经抛竿（竿在水里）→ 锁死目标，绝不能中途换。
            // Think 每 350ms 跑一次，以前这里每次都重选"最近的水格"，AI 在岸边
            // 两片水之间来回改主意，鱼竿永远抛不出去（玩家看到的就是"一直钓鱼中"）。
            if (FishStartedAt >= 0.0 && FishTile != Vector2.Zero
                && Phase != FishPhase.无 && Phase != FishPhase.走去水边)
            {
                Job.Type = JobType.钓鱼;
                Job.Tile = FishTile;
                return;
            }

            Vector2 water = FindWaterTile(workR);

            // 还在往水边走的路上：目标很远（>4 格）时允许"接力"到明显更近的水面。
            // 否则会出现 2.1.3 日志里的死磕：我在 (28,82)，非要走去 20 格外的 (14,96)，
            // 一路被城镇的房子挡住，6 秒就被判超时。走到哪就近钓哪才对。
            if (water != Vector2.Zero && Phase == FishPhase.走去水边 && FishTile != Vector2.Zero)
            {
                float dNew = Vector2.Distance(Tile, water);
                float dOld = Vector2.Distance(Tile, FishTile);
                if (dOld < 4f || dNew > dOld - 2f)
                    water = FishTile;   // 新的不够近 → 维持原目标，避免左右横跳
            }

            if (water != Vector2.Zero)
            {
                Job.Type = JobType.钓鱼;
                Job.Tile = water;
            }
        }
    }

    private void ExecuteJob(double dtMs)
    {
        switch (Job.Type)
        {
            case JobType.无:
                // 钓鱼模式下两竿之间的冷却里别朝玩家挪（否则刚收竿就走两步、
                // 下一竿又走回岸边，看着像在原地抽动）
                if (Mode == AiMode.钓鱼 && Clock < ActionCooldownUntil)
                    break;
                FollowPlayer(dtMs);
                break;
            case JobType.回家:
                GoHome(dtMs);
                break;
            case JobType.打怪:
                DoCombat(dtMs);
                break;
            case JobType.挖矿:
                DoMining(dtMs);
                break;
            case JobType.钓鱼:
                DoFishing(dtMs);
                break;
            case JobType.砍树:
                DoChop(dtMs);
                break;
            case JobType.采集:
                DoGather(dtMs);
                break;
            case JobType.清理:
                DoCleanup(dtMs);
                break;
            case JobType.送背包:
                DoDeliverBag(dtMs);
                break;
            case JobType.存背包:
                DoStoreBag(dtMs);
                break;
            default:
                DoFarm(dtMs);
                break;
        }
    }

    // =====================================================================
    // 各任务执行
    // =====================================================================

    private void DoCombat(double dtMs)
    {
        Monster target = Job.TargetMonster;
        if (target == null || target.Health <= 0 || !SameLocation(target))
        {
            Job.Type = JobType.无;
            return;
        }

        if (Vector2.Distance(Position, target.Position) / 64f > ModEntry.Config.攻击距离)
        {
            MoveToward(target.Position, ModEntry.Config.移动速度 * 1.25f, dtMs);
            return;
        }

        if (Clock >= NextAttack)
        {
            NextAttack = Clock + ModEntry.Config.攻击间隔毫秒;
            AttackAnimUntil = Clock + 220.0;
            FaceTowards(target.Position);
            try
            {
                target.takeDamage(
                    ModEntry.Config.攻击力,
                    (int)Math.Sign(target.Position.X - Position.X),
                    (int)Math.Sign(target.Position.Y - Position.Y),
                    false, 0.0, Game1.player);
            }
            catch { }
            Game1.playSound("dirtyHit");
        }
    }

    private void DoMining(double dtMs)
    {
        GameLocation loc = Game1.currentLocation;
        Vector2 tile = Job.Tile;
        if (loc == null || !loc.objects.ContainsKey(tile))
        {
            Job.Type = JobType.无;
            return;
        }

        // 目标占着格，站到旁边的可站格再敲；够不着就换目标，避免挖矿卡位置。
        Vector2 stand = FindStandSpot(tile);
        if (stand == Vector2.Zero)
        {
            Avoid(tile);
            Job.Type = JobType.无;
            return;
        }

        if (Vector2.Distance(Tile, stand) > 1.2f)
        {
            if (ApproachTimeout(stand))
                return;
            MoveToward(stand * 64f + new Vector2(32f, 32f), ModEntry.Config.移动速度, dtMs);
            return;
        }

        if (Clock >= ActionCooldownUntil && Clock >= NextAttack)
        {
            // 2.5.0：每敲一下都花体力；体力见底就收手（回体力后 Think 会重新接活）
            if (ModEntry.Config.挖矿消耗体力)
            {
                float cost = MiningEnergyCost();
                if (Energy < cost)
                {
                    WarnBlocked("没体力");
                    Job.Type = JobType.无;
                    ActionCooldownUntil = Clock + 1500.0;
                    return;
                }
                Energy = Math.Max(0f, Energy - cost);
            }

            NextAttack = Clock + ModEntry.Config.攻击间隔毫秒;
            AttackAnimUntil = Clock + 220.0;
            FaceTowards(tile * 64f);
            Job.Progress++;
            Game1.playSound("hammer");
            if (Job.Progress >= 2)
            {
                loc.removeObject(tile, false);
                DropOreAt(tile);
                ActionCooldownUntil = Clock + 400.0;
                Job.Type = JobType.无;
            }
        }
    }

    private void DoChop(double dtMs)
    {
        TerrainFeature f = Job.TargetFeature;
        if (f is not Tree tree || tree.stump.Value || tree.growthStage.Value < 5 || tree.GetType().Name == "FruitTree")
        {
            Job.Type = JobType.无;
            return;
        }

        Vector2 tile = Job.Tile;
        GameLocation loc = Game1.currentLocation;

        // 目标必须是这张图上真实存在的那棵树（树可能已经被玩家砍了，别对着空气挥斧）
        if (loc == null
            || !loc.terrainFeatures.TryGetValue(tile, out TerrainFeature live)
            || !ReferenceEquals(live, tree))
        {
            Job.Type = JobType.无;
            return;
        }

        // 树本身占着格，AI 必须站到树旁边的可站格才能砍。
        // 先找一个能站到的格子；树四周都被围死（找不到可站格）就说明这棵树现在够不着，
        // 直接换一棵，别让 AI 一直朝树中心冲、卡在原地（这正是"砍树不好使"的常见原因）。
        Vector2 stand = FindStandSpot(tile);
        if (stand == Vector2.Zero)
        {
            Avoid(tile);
            Job.Type = JobType.无;
            return;
        }

        // 站到可站格附近（1.9 格内）即可挥斧：直邻 1.0 / 对角 1.41 都够得着
        if (Vector2.Distance(Tile, stand) > 1.2f)
        {
            if (ApproachTimeout(stand))
                return;
            MoveToward(stand * 64f + new Vector2(32f, 32f), ModEntry.Config.移动速度, dtMs);
            return;
        }

        if (Clock >= ActionCooldownUntil && Clock >= NextAttack)
        {
            NextAttack = Clock + ModEntry.Config.攻击间隔毫秒;
            AttackAnimUntil = Clock + 220.0;
            FaceTowards(tile * 64f);

            float before = tree.health.Value;
            bool fallen = false;

            // 最后一刀交给原版处理：树会播放"倒下"动画，并按原版规则掉木头/种子。
            // 只扣血+设 stump 的话树是"瞬间变成树桩"，看着就像没砍成。
            if (before <= ModEntry.Config.攻击力)
            {
                Tool axe = CompanionRenderer.FindTool("Axe");
                if (axe != null)
                {
                    tree.health.Value = 1f;   // 让原版这一下正好打空
                    try
                    {
                        tree.performToolAction(axe, 0, tile);
                        fallen = tree.stump.Value || tree.health.Value <= 0f;
                    }
                    catch (Exception ex)
                    {
                        CLog.Act($"[砍树] 原版砍倒调用失败，改手动放倒: {ex.Message}");
                    }
                }
            }

            if (!fallen)
            {
                if (before > ModEntry.Config.攻击力)
                    tree.health.Value = before - ModEntry.Config.攻击力;
                Game1.playSound("axe");
            }

            // 砍了几刀还没倒（血量规则和预想不一样）→ 记一笔，够刀数就强制放倒，
            // 免得玩家看到"一直在砍但树纹丝不动"
            if (tile != _chopTile)
            {
                _chopTile = tile;
                _chopSwings = 0;
            }
            _chopSwings++;
            if (_chopSwings <= 2 || tree.health.Value <= 0f || fallen)
            {
                CLog.Act($"[砍树] ({tile.X},{tile.Y}) 血量 {before:0.#} → {tree.health.Value:0.#}" +
                    $"（第 {_chopSwings} 刀）{(fallen ? " 原版砍倒" : "")}");
            }
            if (_chopSwings >= 15 && !fallen && tree.health.Value > 0f)
            {
                CLog.Warn($"[砍树] ({tile.X},{tile.Y}) 砍了 {_chopSwings} 刀仍未倒下（剩余 {tree.health.Value:0.#}），强制放倒");
                tree.health.Value = 0f;
            }

            if (!fallen && tree.health.Value <= 0f)
            {
                tree.health.Value = 0f;
                tree.stump.Value = true;
                try
                {
                    Game1.createObjectDebris("388", (int)tile.X, (int)tile.Y, Game1.player.UniqueMultiplayerID, Game1.currentLocation);
                }
                catch { }
                fallen = true;
            }

            if (fallen)
            {
                _chopSwings = 0;
                _chopTile = new Vector2(-1f, -1f);
                ActionCooldownUntil = Clock + 400.0;
                Job.Type = JobType.无;
            }
        }
    }

    // ---------------------------------------------------------------------
    // 背包满了 → 找箱子自己塞（玩家把箱子一摆好就自动生效）
    // ---------------------------------------------------------------------

    /// <summary>附近能塞东西的箱子（跳过被拉黑的格子，走不到的箱子不再选）。</summary>
    private Chest FindChestForBag(float maxTiles)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return null;

        float bestDist = maxTiles * 64f;
        Chest best = null;

        foreach (var pair in loc.objects.Pairs)
        {
            if (pair.Value is not Chest chest)
                continue;
            if (IsAvoided(pair.Key))
                continue;

            float d = Vector2.Distance(Position, pair.Key * 64f + new Vector2(32f, 32f));
            if (d < bestDist)
            {
                bestDist = d;
                best = chest;
            }
        }
        return best;
    }

    /// <summary>
    /// 背包满了、附近又有箱子：自己走过去把东西塞进去。
    ///
    /// 以前只有对话里的「你塞进箱子」一项，而且是**当时**就找箱子：
    /// 玩家背包满的那一刻地上没箱子，只能选「先背着」，之后把箱子造好摆下来，
    /// AI 也不会再去（2.7.1 修的就是这个）。
    /// 现在只要 12 格内有箱子，背包一满就自动去放；塞不下的留在背包里。
    /// </summary>
    private void DoStoreBag(double dtMs)
    {
        if (!IsBagFull || !ModEntry.Config.背包满了自动找箱子)
        {
            Job.Type = JobType.无;
            return;
        }

        Chest chest = FindChestForBag(12f);
        if (chest == null)
        {
            // 箱子被拆了，或者这一带走不过去 → 别再死磕，回头去问玩家
            Job.Type = JobType.无;
            _bagStoreCooldown = Clock + 3000.0;
            return;
        }

        Vector2 chestTile = chest.TileLocation;
        Job.Tile = chestTile;

        if (Vector2.Distance(Tile, chestTile) > 1.6f)
        {
            if (ApproachTimeout(chestTile))
            {
                Job.Type = JobType.无;
                _bagStoreCooldown = Clock + 5000.0;
                return;
            }
            MoveToward(chestTile * 64f + new Vector2(32f, 32f), ModEntry.Config.移动速度, dtMs);
            return;
        }

        // 站到箱子边上了：一件件往里塞
        FaceTowards(chestTile * 64f + new Vector2(32f, 32f));

        int moved = 0;
        foreach (Item item in Inventory.ToList())
        {
            // 工具本来就是"借玩家的"，不往里塞
            if (item == null || item.Stack <= 0 || item is Tool)
                continue;

            int before = item.Stack;
            try
            {
                Item rest = chest.addItem(item);
                int restStack = rest?.Stack ?? 0;
                moved += before - restStack;

                // 塞不下的剩余：addItem 有时会返回新对象，补回背包别丢了
                if (restStack > 0 && rest != null && !ReferenceEquals(rest, item) && !Inventory.Contains(rest))
                    Inventory.Add(rest);
            }
            catch { }
        }
        Inventory.RemoveAll(i => i == null || i.Stack <= 0);

        Job.Type = JobType.无;
        _bagStoreCooldown = Clock + 4000.0;

        if (moved > 0)
        {
            Game1.playSound("openChest");
            try
            {
                Game1.addHUDMessage(new HUDMessage(
                    $"{ModEntry.Config.AI名字}：{moved} 样东西塞进箱子里了。"));
            }
            catch { }
            CLog.Act($"[背包] 自动塞进箱子 {moved} 个，背包还剩 {InventoryGroups} 组");
            ModEntry.Instance?.SaveDataToDisk();
        }
        else if (IsBagFull)
        {
            // 箱子也是满的：隔一会儿再试，别每帧重复
            _bagStoreCooldown = Clock + 30000.0;
            try
            {
                Game1.addHUDMessage(new HUDMessage(
                    $"{ModEntry.Config.AI名字}：这箱子也满了，塞不进去，你来找我拿吧。"));
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------------
    // 背包满了 → 跑到玩家面前交货
    // ---------------------------------------------------------------------
    private void DoDeliverBag(double dtMs)
    {
        if (Game1.player == null)
        {
            Job.Type = JobType.无;
            return;
        }
        // 玩家已经领走了 / 关掉了自动提醒 → 继续干活
        if (!IsBagFull || !ModEntry.Config.背包满了自动找玩家)
        {
            Job.Type = JobType.无;
            return;
        }

        float dist = Vector2.Distance(Position, Game1.player.Position) / 64f;
        if (dist > 1.6f)
        {
            if (dist > ModEntry.Config.瞬移距离)
            {
                Position = Game1.player.Position + new Vector2(48f, 32f);
                _path.Clear();
                return;
            }
            MoveToward(Game1.player.Position, ModEntry.Config.移动速度 * 1.2f, dtMs);
            return;
        }

        // 到跟前了：说那句话。玩家直接按 Esc 关掉（没选任何选项）时，
        // 60 秒后才会再来问一次——以前是 8 秒，等于贴着人反复弹。
        FaceTowards(Game1.player.Position);
        _bagTellCooldown = Clock + 60000.0;
        Job.Type = JobType.无;
        ModEntry.Instance?.AlertBagFull();
    }

    // ---------------------------------------------------------------------
    // 钓鱼：走去水边 → 抛竿 → 等鱼 → 咬钩 → 收竿
    // 每个阶段都有明确时长，渲染器据此播动画，头顶标签也跟着变。
    // 以前只有"钓/没钓"两态：站没站到岸边、竿抛没抛出去，玩家看到的
    // 全都是"钓鱼中"，加上目标没锁死，就成了永远钓鱼中 + 没有任何动画。
    // ---------------------------------------------------------------------
    private const double CastMs = 420.0;
    private const double BiteMs = 450.0;
    private const double ReelMs = 350.0;

    private void DoFishing(double dtMs)
    {
        Vector2 water = Job.Tile;
        if (water == Vector2.Zero)
        {
            ResetFishing();
            Job.Type = JobType.无;
            return;
        }

        // 新一竿（或换了一片水）→ 流程倒回"走去水边"
        if (Phase == FishPhase.无 || FishTile != water)
        {
            FishTile = water;
            FishStartedAt = Clock;
            FishWaitMs = Math.Max(500, ModEntry.Config.钓鱼等待毫秒);   // 抛竿时按有无鱼饵重算
            FishHadBait = false;
            Phase = FishPhase.走去水边;
            PhaseStartedAt = Clock;
            _stuckTimer = 0.0;
            _fishWalkStuck = 0.0;
            _approachTile = new Vector2(-1f, -1f);   // 靠不近的超时从头算
            _hopCount = 0;
            _path.Clear();
            CLog.Act($"[钓鱼] 我在 ({Tile.X},{Tile.Y})，目标水面 ({water.X},{water.Y})，"
                + $"距离 {(int)(Vector2.Distance(Tile, water) + 0.5f)} 格");
        }

        Vector2 waterPos = water * 64f + new Vector2(32f, 32f);

        switch (Phase)
        {
            case FishPhase.走去水边:
            {
                float dist = Vector2.Distance(Tile, water);

                // 已经够近（≤4.5 格，目标可能在水里 2~3 格深）：再试着靠近一点点，
                // 最多 1.5 秒；挪不动就直接抛竿。
                if (dist <= 4.5f)
                {
                    if (dist > 1.5f && _fishWalkStuck < 1500.0)
                    {
                        _fishWalkStuck += dtMs;   // 这里是"尝试时长"，不看位移
                        MoveToward(waterPos, ModEntry.Config.移动速度, dtMs);
                        return;
                    }
                    _fishWalkStuck = 0.0;
                    BeginCast(water, waterPos);
                    return;
                }

                // ---- 离水还远，需要走过去 ----

                // ApproachTimeout 里内置了"卡住 2.5 秒就朝目标挪一小步"的兜底，
                // 所以墙角抖动、门口挤不进去都不会再耗十几秒；挪满 3 次还靠不近才认输。
                double limit = Math.Min(6000.0 + dist * 400.0, 12000.0);
                if (ApproachTimeout(water, limit))
                {
                    GiveUpAndFollow(water);
                    return;
                }

                if (Clock >= _fishLogAt)
                {
                    _fishLogAt = Clock + 1500.0;
                    CLog.Act($"[钓鱼] 走路中 我({Tile.X},{Tile.Y}) 水面({water.X},{water.Y}) "
                        + $"差 {dist:0.#} 格 位移={(WasMoving ? "有" : "无")} 路径={_path.Count} 步");
                }
                MoveToward(waterPos, ModEntry.Config.移动速度, dtMs);
                return;
            }

            case FishPhase.抛竿:
                FaceTowards(waterPos);
                if (PhaseElapsed >= CastMs)
                {
                    Phase = FishPhase.等待;
                    PhaseStartedAt = Clock;
                    _stuckTimer = 0.0;
                }
                return;

            case FishPhase.等待:
                FaceTowards(waterPos);
                if (PhaseElapsed >= FishWaitMs)
                {
                    _pendingCatch = PickCatch(water);
                    Phase = FishPhase.咬钩;
                    PhaseStartedAt = Clock;
                    _stuckTimer = 0.0;
                    PlaySound("fishBite");
                }
                return;

            case FishPhase.咬钩:
                if (PhaseElapsed >= BiteMs)
                {
                    Phase = FishPhase.收竿;
                    PhaseStartedAt = Clock;
                    _stuckTimer = 0.0;
                }
                return;

            case FishPhase.收竿:
                if (PhaseElapsed >= ReelMs)
                    FinishFishing();
                return;
        }
    }

    /// <summary>
    /// 站定了：先结这一竿的账（鱼竿 / 鱼饵 / 体力），再进入"抛竿"阶段。
    /// 原版钓鱼本来就要这三样；以前 AI 什么都不花，站 9 秒就摸出一条鱼。
    /// </summary>
    private void BeginCast(Vector2 water, Vector2 waterPos)
    {
        string blocked = FishingBlockReason();
        if (blocked != null)
        {
            WarnBlocked(blocked);
            ResetFishing();
            Job.Type = JobType.无;
            _fishRestUntil = Clock + 20000.0;   // 歇一会儿再说，别每 350ms 重跑一趟
            return;
        }

        // ---- 鱼饵：一竿 1 个，先用 AI 背包里的，再用玩家背包里的 ----
        bool hasBait = false;
        int baitLeft = CountBait();
        if (ModEntry.Config.钓鱼消耗鱼饵)
        {
            hasBait = TryConsumeBait(out baitLeft);
            if (hasBait && baitLeft == 0)
            {
                try
                {
                    Game1.addHUDMessage(new HUDMessage(
                        ModEntry.Config.AI名字 + "：鱼饵正好用完了，后面没饵会钓得慢一些。"));
                }
                catch { }
            }
        }
        FishHadBait = !ModEntry.Config.钓鱼消耗鱼饵 || hasBait;   // 关掉鱼饵消耗就别在头顶写「无饵」

        // ---- 体力：同原版 8 - 钓鱼等级*0.1 ----
        if (ModEntry.Config.钓鱼消耗体力)
            Energy = Math.Max(0f, Energy - CastEnergyCost());

        // ---- 咬钩时间：有饵更快，没饵翻倍；再抖 ±20%，别像流水线 ----
        double baseMs = Math.Max(500.0, ModEntry.Config.钓鱼等待毫秒);
        double mult = 1.0;
        if (ModEntry.Config.钓鱼消耗鱼饵)
            mult = hasBait ? 0.85 : 1.9;
        double jitter = 0.8 + Game1.random.NextDouble() * 0.4;
        FishWaitMs = baseMs * mult * jitter;

        FaceTowards(waterPos);
        Phase = FishPhase.抛竿;
        PhaseStartedAt = Clock;
        _stuckTimer = 0.0;
        _fishWalkStuck = 0.0;
        _fishFailStreak = 0;
        _hopCount = 0;
        PlaySound("cast");
        CLog.Act($"[钓鱼] 站到 ({Tile.X},{Tile.Y})，朝水面 ({water.X},{water.Y}) 抛竿"
            + $"（差 {Vector2.Distance(Tile, water):0.#} 格，体力 {Energy:0}/{MaxEnergy}，"
            + $"鱼饵{(ModEntry.Config.钓鱼消耗鱼饵 ? (hasBait ? $"剩 {baitLeft}" : "无") : "不管")}，"
            + $"咬钩约 {FishWaitMs / 1000.0:0.0}s）");
    }

    /// <summary>
    /// 连续 3 秒一步都没挪动 → 别再在这片水边硬耗：拉黑这片水，直接切到「跟随」模式。
    /// 玩家反馈："卡 3 秒换跟随就行了"——比起反复超时重试，跟着人走更符合预期。
    /// </summary>
    private void GiveUpAndFollow(Vector2 water)
    {
        Avoid(water);

        int whereX = (int)Tile.X;
        int whereY = (int)Tile.Y;
        ResetFishing();
        Job.Type = JobType.无;
        _fishWalkStuck = 0.0;
        _fishFailStreak = 0;
        _fishRestUntil = 0.0;

        if (Mode != AiMode.钓鱼)
            return;

        Mode = AiMode.跟随;
        try
        {
            Game1.addHUDMessage(new HUDMessage(
                ModEntry.Config.AI名字 + "：这边走不过去，我先跟着你。"));
        }
        catch { }
        CLog.Info($"[钓鱼] 在 ({whereX},{whereY}) 卡了 3 秒走不过去，自动切到跟随模式");
    }

    /// <summary>
    /// 这一竿去不了：把这片水连周边 3x3 一起拉黑（同一段岸边换个水格照样走不过去），
    /// 再歇 8 秒才重新接钓鱼活。不这么做的话，每次决策又会选回同一片水，
    /// 头顶就一直「去水边」，玩家看到的就是"卡住了"。
    /// </summary>
    private void FailFishing(Vector2 water)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                Vector2 t = water + new Vector2(dx, dy);
                if (t.X >= 0f && t.Y >= 0f)
                    Avoid(t);
            }
        }

        _fishFailStreak = Math.Min(_fishFailStreak + 1, 5);
        double rest = 8000.0 + (_fishFailStreak - 1) * 5000.0;   // 8s → 13s → 18s → 23s → 28s
        _fishRestUntil = Clock + rest;
        CLog.Act($"[钓鱼] 过不去 ({water.X},{water.Y})，拉黑这片水，歇 {(int)(rest / 1000)} 秒再找地方"
            + $"（连续第 {_fishFailStreak} 次）");
        ResetFishing();
        Job.Type = JobType.无;
    }

    /// <summary>收竿结束：发鱼（或空军的感慨）、清状态、进冷却。</summary>
    private void FinishFishing()
    {
        Item caught = _pendingCatch;
        ResetFishing();
        Job.Type = JobType.无;
        ActionCooldownUntil = Clock + 800.0;

        if (caught != null)
        {
            GiveItem(caught);
            PlaySound("coin");
            CLog.Act($"[钓鱼] 钓到 1 条 {caught.ItemId}（{caught.DisplayName}）");
            return;
        }

        string[] 感慨 =
        {
            "水太浑了，啥也没看见……",
            "唉，差一点就钓上了。",
            "鱼都跑光了，下次再来。",
            "这水也太浅了，钓不到鱼。",
            "算了，换个地方试试。",
            "看来今天运气不太好。",
            "鱼饵都白买了……",
            "这鱼精着呢，不跟我打交道。"
        };
        // 以前这里是 OpenLine：会弹出一个要玩家点的对话框，把人卡在原地。
        // AI 是自动钓鱼的，空军只该飘一行字，不该打断玩家。
        try
        {
            Game1.addHUDMessage(new HUDMessage(
                ModEntry.Config.AI名字 + "：" + 感慨[Game1.random.Next(感慨.Length)]));
        }
        catch { }
        CLog.Act("[钓鱼] 没钓上");
    }

    private void DoGather(double dtMs)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
        {
            Job.Type = JobType.无;
            return;
        }

        if (Job.TargetDebris != null)
        {
            Debris d = Job.TargetDebris;
            if (!loc.debris.Contains(d) || d.item == null)
            {
                Job.Type = JobType.无;
                return;
            }

            Vector2 dpos = DebrisPos(d);
            if (Vector2.Distance(Position, dpos) / 64f > 0.9f)
            {
                MoveToward(dpos, ModEntry.Config.移动速度, dtMs);
                return;
            }

            try
            {
                if (d.collect(Game1.player))
                    loc.debris.Remove(d);
                else
                {
                    ActionCooldownUntil = Clock + 1500.0;
                    Job.Type = JobType.无;
                }
            }
            catch
            {
                Job.Type = JobType.无;
            }
            return;
        }

        // 机器成品
        if (Job.TargetObject == null)
            return;

        Vector2 tile = Job.Tile;
        if (!loc.objects.ContainsKey(tile))
        {
            Job.Type = JobType.无;
            return;
        }

        StardewValley.Object obj = loc.objects[tile];
        var output = obj.heldObject.Value;
        if (output == null)
        {
            Job.Type = JobType.无;
            return;
        }

        if (Vector2.Distance(Tile, tile) > 1.4f)
        {
            MoveToward(tile * 64f + new Vector2(32f, 32f), ModEntry.Config.移动速度, dtMs);
            return;
        }

        GiveItem(output);
        obj.heldObject.Value = null;
        Game1.playSound("coin");
        ActionCooldownUntil = Clock + 300.0;
        Job.Type = JobType.无;
    }

    private void DoFarm(double dtMs)
    {
        GameLocation loc = Game1.currentLocation;
        Vector2 tile = Job.Tile;
        if (loc == null)
        {
            Job.Type = JobType.无;
            return;
        }

        loc.terrainFeatures.TryGetValue(tile, out TerrainFeature feature);

        if (Vector2.Distance(Tile, tile) > 1.6f)
        {
            if (ApproachTimeout(tile))
                return;
            MoveToward(tile * 64f + new Vector2(32f, 32f), ModEntry.Config.移动速度, dtMs);
            return;
        }

        if (Clock < ActionCooldownUntil)
            return;

        switch (Job.Type)
        {
            case JobType.收获:
                if (feature is HoeDirt dirt && dirt.crop != null && dirt.readyForHarvest())
                {
                    if (SafeHarvest(dirt, tile))
                        Game1.playSound("cropper");
                    ActionCooldownUntil = Clock + 450.0;
                }
                Job.Type = JobType.无;
                break;

            case JobType.浇水:
                if (feature is HoeDirt dirt2 && dirt2.needsWatering())
                {
                    dirt2.state.Value = 1;
                    Game1.playSound("wateringCan");
                    ActionCooldownUntil = Clock + 400.0;
                }
                Job.Type = JobType.无;
                break;

            case JobType.翻地:
                if (feature == null && CanTill(tile))
                {
                    loc.terrainFeatures[tile] = new HoeDirt(0, loc);
                    Game1.playSound("hoeHit");
                    ActionCooldownUntil = Clock + 500.0;
                }
                Job.Type = JobType.无;
                break;

            case JobType.播种:
                if (feature is HoeDirt dirt3 && dirt3.crop == null)
                {
                    StardewValley.Object seed = FindSeedInInventory();
                    if (seed != null && dirt3.canPlantThisSeedHere(seed.ItemId, false))
                    {
                        dirt3.plant(seed.ItemId, Game1.player, false);
                        ConsumeSeed(seed);
                        Game1.playSound("dirtyHit");
                        ActionCooldownUntil = Clock + 500.0;
                    }
                }
                Job.Type = JobType.无;
                break;
        }
    }

    // =====================================================================
    // 农场清理：杂草 / 枯苗 / 树枝 / 石头 / 树桩
    // =====================================================================

    private void DoCleanup(double dtMs)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
        {
            Job.Type = JobType.无;
            return;
        }

        Vector2 tile = Job.Tile;

        // 目标已消失（玩家自己动手清了）→ 换下一个
        if (Job.TargetFeature != null && !loc.terrainFeatures.ContainsKey(tile))
        {
            Job.Type = JobType.无;
            return;
        }
        if (Job.TargetObject != null && (!loc.objects.ContainsKey(tile) || !ReferenceEquals(loc.objects[tile], Job.TargetObject)))
        {
            Job.Type = JobType.无;
            return;
        }
        if (Job.TargetFeature == null && Job.TargetObject == null)
        {
            Job.Type = JobType.无;
            return;
        }

        // 目标占着格，AI 要站到它旁边的可站格；被围死够不着就换下一个，
        // 不朝目标中心硬冲，避免清理时卡位置。
        Vector2 stand = FindStandSpot(tile);
        if (stand == Vector2.Zero)
        {
            Avoid(tile);
            Job.Type = JobType.无;
            return;
        }

        if (Vector2.Distance(Tile, stand) > 1.2f)
        {
            if (ApproachTimeout(stand))
                return;
            MoveToward(stand * 64f + new Vector2(32f, 32f), ModEntry.Config.移动速度, dtMs);
            return;
        }

        if (Clock < ActionCooldownUntil || Clock < NextAttack)
            return;

        // 清理石头和矿洞里敲石头是一回事：扣体力、要镐子
        if (Job.TargetObject != null && Job.TargetObject.ItemId == "390")
        {
            string stoneBlocked = MiningBlockReason();
            if (stoneBlocked != null)
            {
                WarnBlocked(stoneBlocked);
                Job.Type = JobType.无;
                return;
            }
            if (ModEntry.Config.挖矿消耗体力)
                Energy = Math.Max(0f, Energy - MiningEnergyCost());
        }

        NextAttack = Clock + ModEntry.Config.攻击间隔毫秒;
        AttackAnimUntil = Clock + 220.0;
        FaceTowards(tile * 64f);
        Job.Progress++;

        // 第一下只是挥臂，第二下真清掉（有打击感）
        if (Job.Progress < 2)
            return;

        if (Job.TargetFeature is Tree)
        {
            // 树桩：清掉，木头收进背包（玩家对话免费领取）
            loc.terrainFeatures.Remove(tile);
            Game1.playSound("axe");
            AddToInventory("388", 1);
        }
        else if (Job.TargetFeature is HoeDirt dirt && dirt.crop != null && dirt.crop.dead.Value)
        {
            dirt.crop = null;
            Game1.playSound("cropper");
        }
        else if (Job.TargetObject != null)
        {
            string itemId = Job.TargetObject.ItemId;
            bool isWeed = false;
            try { isWeed = Job.TargetObject.IsWeeds(); } catch { }
            loc.objects.Remove(tile);
            if (isWeed)
            {
                // 杂草：直接清掉，无掉落
                Game1.playSound("dirtyHit");
            }
            else
            {
                // 石头用镐子音效，树枝用斧头音效；有用的收进背包等玩家来领
                Game1.playSound(itemId == "390" ? "hammer" : "axe");
                AddToInventory(itemId, 1);
            }
        }

        ActionCooldownUntil = Clock + 250.0;
        Job.Type = JobType.无;
    }

    /// <summary>
    /// 找农场范围内最近的清理目标：杂草(IsWeeds)、枯苗、树枝(294)、石头(390)、树桩。
    /// 返回 KeyValuePair(瓦片, TerrainFeature|Object)，找不到返回 null。
    /// </summary>
    private KeyValuePair<Vector2, object>? FindCleanupTarget(float radius)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null || !(loc is Farm))
            return null;

        Vector2 bestTile = Vector2.Zero;
        object bestTarget = null;
        float bestDist = radius * 64f;

        // 地形类目标：枯苗（HoeDirt 里死掉的作物）、树桩
        foreach (var pair in loc.terrainFeatures.Pairs)
        {
            bool isTarget =
                (pair.Value is HoeDirt dirt && dirt.crop != null && dirt.crop.dead.Value)
                || (pair.Value is Tree tree && tree.stump.Value);
            if (!isTarget || IsAvoided(pair.Key))
                continue;
            // 被围死、AI 站不过去的目标跳过，保证按"能实际够得着"的顺序逐个清理，不卡位置
            if (!HasStandSpot(pair.Key))
                continue;

            float dist = Vector2.Distance(Tile * 64f, pair.Key * 64f);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestTile = pair.Key;
                bestTarget = pair.Value;
            }
        }

        // 物件类目标：杂草、树枝(294)、石头(390)
        bool stoneBlocked = MiningBlockReason() != null;
        foreach (var pair in loc.objects.Pairs)
        {
            StardewValley.Object o = pair.Value;
            if (o == null)
                continue;

            bool isWeed = false;
            try { isWeed = o.IsWeeds(); } catch { }
            if (!isWeed && o.ItemId != "294" && o.ItemId != "390")
                continue;
            // 石头（390）和矿洞里一样要镐子 + 体力：没工具就先不清石头，
            // 免得「挖矿要镐子」在农场清理里被绕过。
            // 这个判断在循环里只算一次（FindPickaxe 要扫背包，别对每个物件都扫一遍）
            if (o.ItemId == "390" && stoneBlocked)
                continue;
            if (IsAvoided(pair.Key))
                continue;
            // 被围死、AI 站不过去的目标跳过
            if (!HasStandSpot(pair.Key))
                continue;

            float dist = Vector2.Distance(Tile * 64f, pair.Key * 64f);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestTile = pair.Key;
                bestTarget = o;
            }
        }

        if (bestTarget == null)
            return null;
        return new KeyValuePair<Vector2, object>(bestTile, bestTarget);
    }

    // =====================================================================
    // 跟随 / 回家
    // =====================================================================

    private void FollowPlayer(double dtMs)
    {
        if (Game1.player == null)
            return;

        // 未雇佣（含刚被遣散）：自己走回农舍门口守着。
        // 以前这里是 `Mode == 待命` 直接 return，而遣散又会把模式切成待命 →
        // 人被解雇后就钉在原地，看着像被丢下了；只有进屋/读档才会突然回到门口。
        // 只在农场里走（别的图没有"家门口"这个概念，乱走会走向奇怪的坐标）。
        if (!ModEntry.Instance.IsHired)
        {
            if (Game1.currentLocation is Farm)
                GoHome(dtMs);
            return;
        }

        if (Mode == AiMode.待命)
            return;

        // 玩家在别的地图（比如进屋了）：不按裸坐标追（会穿墙进屋），原地待命
        if (Location != null && Game1.currentLocation != Location)
            return;

        float dist = Vector2.Distance(Position, Game1.player.Position) / 64f;
        if (dist > ModEntry.Config.瞬移距离)
        {
            Position = Game1.player.Position + new Vector2(64f, 64f);
            _followStuckMs = 0.0;
        }
        else if (dist > ModEntry.Config.跟随距离)
        {
            // 跟着你的时候也会被树/石头围住：连续 2 秒一步都没挪动，
            // 就朝你那边挪一格出来（和钓鱼用同一个 HopToward），别杵在原地装木头。
            _followStuckMs = WasMoving ? 0.0 : _followStuckMs + dtMs;
            if (_followStuckMs >= 2000.0)
            {
                _followStuckMs = 0.0;
                Vector2 playerTile = new Vector2(
                    (int)(Game1.player.Position.X / 64f), (int)(Game1.player.Position.Y / 64f));
                if (HopToward(playerTile, 2))
                {
                    ModEntry.SMonitor?.Log(
                        $"[跟随] 被挡住了，朝玩家挪一步 → ({Tile.X},{Tile.Y})，还差 {dist:0.#} 格",
                        LogLevel.Trace);
                    return;
                }
            }
            MoveToward(Game1.player.Position, ModEntry.Config.移动速度, dtMs);
        }
        else
        {
            _followStuckMs = 0.0;
        }
    }

    /// <summary>走回家用的"一步没挪"计时：被树丛/围栏夹住时兜底传送。</summary>
    private double _homeStuckMs;

    /// <summary>跟随时的"一步没挪"计时（毫秒）：被围住超过 2 秒就朝玩家挪一格。</summary>
    private double _followStuckMs;

    private void GoHome(double dtMs)
    {
        Vector2 target = HomeTile * 64f + new Vector2(32f, 32f);
        if (Vector2.Distance(Tile, HomeTile) > 1.2f)
        {
            // 兜底：走 4 秒一步都没挪动（被围住/卡墙）就直接回去。
            // 遣散后一直杵在野地里，比"瞬移回家门口"更奇怪。
            _homeStuckMs = WasMoving ? 0.0 : _homeStuckMs + dtMs;
            if (_homeStuckMs >= 4000.0)
            {
                _homeStuckMs = 0.0;
                Position = target;
                _path.Clear();
                _pathGoal = new Vector2(-1f, -1f);
                Sleeping = Game1.timeOfDay >= ModEntry.Config.回家睡觉时间;
                return;
            }

            MoveToward(target, ModEntry.Config.移动速度, dtMs);
            if (Game1.timeOfDay >= ModEntry.Config.回家睡觉时间 + 300)
                Position = target;
        }
        else
        {
            _homeStuckMs = 0.0;
        }
        Sleeping = Game1.timeOfDay >= ModEntry.Config.回家睡觉时间;
    }

    // =====================================================================
    // 目标搜索
    // =====================================================================

    private Monster NearestMonster(float radius)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return null;

        Monster best = null;
        float bestDist = radius * 64f;
        foreach (NPC c in loc.characters)
        {
            if (c is Monster m && m.Health > 0)
            {
                float d = Vector2.Distance(Position, c.Position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = m;
                }
            }
        }
        return best;
    }

    private Job FindFarmJob(Vector2 playerTile, float workR)
    {
        ModConfig cfg = ModEntry.Config;
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return null;

        float maxSq = (workR * 64f) * (workR * 64f);
        Vector2 playerWorld = playerTile * 64f;

        HoeDirt harvestDirt = null; Vector2 harvestTile = Vector2.Zero; float harvestSq = float.MaxValue;
        HoeDirt waterDirt = null; Vector2 waterTile = Vector2.Zero; float waterSq = float.MaxValue;
        HoeDirt seedDirt = null; Vector2 seedTile = Vector2.Zero; float seedSq = float.MaxValue;

        foreach (var pair in loc.terrainFeatures.Pairs)
        {
            Vector2 key = pair.Key;
            if (Vector2.DistanceSquared(key * 64f, playerWorld) > maxSq)
                continue;
            if (pair.Value is not HoeDirt dirt)
                continue;

            if (cfg.行为_收获 && dirt.crop != null && dirt.readyForHarvest())
            {
                float sq = Vector2.DistanceSquared(key * 64f, Position);
                if (sq < harvestSq) { harvestSq = sq; harvestDirt = dirt; harvestTile = key; }
            }
            else if (cfg.行为_浇水 && dirt.crop != null && dirt.needsWatering())
            {
                float sq = Vector2.DistanceSquared(key * 64f, Position);
                if (sq < waterSq) { waterSq = sq; waterDirt = dirt; waterTile = key; }
            }
            else if (cfg.行为_播种 && dirt.crop == null && HasSeed())
            {
                float sq = Vector2.DistanceSquared(key * 64f, Position);
                if (sq < seedSq) { seedSq = sq; seedDirt = dirt; seedTile = key; }
            }
        }

        if (harvestDirt != null)
            return new Job { Type = JobType.收获, Tile = harvestTile, TargetFeature = harvestDirt, CreatedAt = Clock };
        if (waterDirt != null)
            return new Job { Type = JobType.浇水, Tile = waterTile, TargetFeature = waterDirt, CreatedAt = Clock };
        if (seedDirt != null)
            return new Job { Type = JobType.播种, Tile = seedTile, TargetFeature = seedDirt, CreatedAt = Clock };

        // 翻地：找已有 HoeDirt 旁边可开垦的空地
        if (cfg.行为_翻地)
        {
            Vector2 bestTile = Vector2.Zero;
            float bestSq = float.MaxValue;
            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is not HoeDirt)
                    continue;
                if (Vector2.DistanceSquared(pair.Key * 64f, playerWorld) > maxSq)
                    continue;
                foreach (Vector2 n in Neighbors(pair.Key))
                {
                    if (!loc.terrainFeatures.ContainsKey(n) && CanTill(n))
                    {
                        float sq = Vector2.DistanceSquared(n * 64f, Position);
                        if (sq < bestSq) { bestSq = sq; bestTile = n; }
                    }
                }
            }
            if (bestTile != Vector2.Zero)
                return new Job { Type = JobType.翻地, Tile = bestTile, CreatedAt = Clock };
        }

        return null;
    }

    private static IEnumerable<Vector2> Neighbors(Vector2 tile)
    {
        yield return tile + new Vector2(1f, 0f);
        yield return tile + new Vector2(-1f, 0f);
        yield return tile + new Vector2(0f, 1f);
        yield return tile + new Vector2(0f, -1f);
    }

    private Debris NearestDebris(float radius)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return null;

        Debris best = null;
        float bestDist = radius * 64f;
        foreach (Debris d in loc.debris)
        {
            if (d?.item == null)
                continue;
            float dist = Vector2.Distance(Position, DebrisPos(d));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = d;
            }
        }
        return best;
    }

    private KeyValuePair<Vector2, StardewValley.Object>? NearestMachine(float radius)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return null;

        KeyValuePair<Vector2, StardewValley.Object>? best = null;
        float bestDist = radius * 64f;
        foreach (var pair in loc.objects.Pairs)
        {
            if (pair.Value?.heldObject.Value == null)
                continue;
            float dist = Vector2.Distance(Tile * 64f, pair.Key * 64f);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = pair;
            }
        }
        return best;
    }

    private KeyValuePair<Vector2, StardewValley.Object>? NearestBreakableObject(float radius)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return null;

        // 矿洞里的桶、箱子、矿石等可敲碎物件（bigCraftable 或矿石节点）
        string[] skip = { "ladder", "chest", "elevator", "shaft", "hopper", "torch", "cask", "seed", "machine" };

        KeyValuePair<Vector2, StardewValley.Object>? best = null;
        float bestDist = radius * 64f;
        foreach (var pair in loc.objects.Pairs)
        {
            StardewValley.Object o = pair.Value;
            if (o == null)
                continue;

            string name = (o.Name ?? "").ToLowerInvariant();
            bool isSkip = skip.Any(b => name.Contains(b));
            if (isSkip || o.bigCraftable.Value || IsAvoided(pair.Key) || !HasStandSpot(pair.Key))
                continue;

            float dist = Vector2.Distance(Tile * 64f, pair.Key * 64f);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = pair;
            }
        }
        return best;
    }

    private KeyValuePair<Vector2, TerrainFeature>? NearestTree(float radius)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return null;

        KeyValuePair<Vector2, TerrainFeature>? best = null;
        float bestDist = radius * 64f;
        foreach (var pair in loc.terrainFeatures.Pairs)
        {
            if (pair.Value is not Tree tree)
                continue;
            if (tree.stump.Value || tree.growthStage.Value < 5 || tree.GetType().Name == "FruitTree")
                continue;
            if (IsAvoided(pair.Key))
                continue;
            // 只选 AI 能站到旁边砍的树：被树丛/石头围死、站不过去的树直接跳过，
            // 这样 AI 会按"从近到远、逐个够得着的"顺序砍，不会卡在原地反复换目标。
            if (!HasStandSpot(pair.Key))
                continue;

            float dist = Vector2.Distance(Tile * 64f, pair.Key * 64f);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = pair;
            }
        }
        return best;
    }

    /// <summary>
    /// 找一片"能钓"的水：水格本身是水面，且四周至少有一格陆地/码头可以站过去。
    /// 这里**不**预挑"岸边站位"——2.1.2 就是先挑站位（还要 CanStandAt），
    /// 一旦挑到被石头/围栏围住的格子，AI 一辈子走不过去，头顶就永久卡在「去水边」。
    /// 现在改成"朝水格走，贴到旁边就下竿"：路走不通交给移动逻辑，
    /// 真走不动就拉黑这一小片水、歇几秒换地方（见 FailFishing）。
    /// </summary>
    private Vector2 FindWaterTile(float radius)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return Vector2.Zero;

        Vector2 here = Tile;
        int r = Math.Min((int)Math.Ceiling(radius), 20);

        for (int i = 1; i <= r; i++)
        {
            Vector2 best = Vector2.Zero;
            float bestDist = float.MaxValue;

            for (int j = -i; j <= i; j++)
            {
                for (int k = -i; k <= i; k++)
                {
                    if (Math.Max(Math.Abs(j), Math.Abs(k)) != i)
                        continue;

                    Vector2 t = here + new Vector2(j, k);
                    if (t.X < 0f || t.Y < 0f)
                        continue;
                    if (IsAvoided(t))
                        continue;
                    if (!loc.isWaterTile((int)t.X, (int)t.Y))
                        continue;
                    if (!HasLandNeighbor(loc, t))
                        continue;

                    float d = Vector2.Distance(here, t);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = t;
                    }
                }
            }

            // 由近到远一圈一圈找，最近一圈里有能下竿的水就用它
            if (best != Vector2.Zero)
                return DeepenWaterTile(best, here);
        }
        return Vector2.Zero;
    }

    /// <summary>
    /// 把水格往水面深处推。
    /// 星露谷物语的岸边贴图（沙滩/草岸）会盖住水格的一部分，甚至岸边的水格整格都
    /// 画成沙滩——浮漂落在这种格子的中心，看着就像"鱼钩扔在岸上"（玩家连提两次）。
    /// 所以沿"AI → 水"的主轴一直往里推（最多 3 格），
    /// 直到落在**四面都是水的格**（那一定是纯水面贴图）为止。
    /// </summary>
    private Vector2 DeepenWaterTile(Vector2 water, Vector2 from)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return water;

        Vector2 dir = water - from;
        if (dir == Vector2.Zero)
            return water;

        Vector2 step = Math.Abs(dir.X) > Math.Abs(dir.Y)
            ? new Vector2(Math.Sign(dir.X), 0f)
            : new Vector2(0f, Math.Sign(dir.Y));
        if (step == Vector2.Zero)
            return water;

        Vector2 best = water;
        Vector2 t = water;
        for (int i = 0; i < 3; i++)
        {
            Vector2 next = t + step;
            if (next.X < 0f || next.Y < 0f)
                break;
            if (IsAvoided(next))
                break;
            if (!loc.isWaterTile((int)next.X, (int)next.Y))
                break;                       // 到头了（或者这本来就是一格宽的水面）
            if (Vector2.Distance(from, next) > 6f)
                break;                       // 别把目标推到太远的地方

            t = next;
            best = next;
            if (IsInteriorWater(loc, next))
                break;                       // 四面都是水 → 肯定是纯水面，收工
        }
        return best;
    }

    /// <summary>四面（上下左右）都是水：这种格子的贴图就是纯水面，不会有岸边沙滩。</summary>
    private static bool IsInteriorWater(GameLocation loc, Vector2 t)
    {
        int x = (int)t.X;
        int y = (int)t.Y;
        return loc.isWaterTile(x, y)
            && loc.isWaterTile(x - 1, y)
            && loc.isWaterTile(x + 1, y)
            && loc.isWaterTile(x, y - 1)
            && loc.isWaterTile(x, y + 1);
    }

    /// <summary>水格四周（含对角）至少有一格不是水，才有地方站着下竿。</summary>
    private static bool HasLandNeighbor(GameLocation loc, Vector2 waterTile)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0)
                    continue;
                Vector2 n = waterTile + new Vector2(dx, dy);
                if (n.X < 0f || n.Y < 0f)
                    continue;
                if (!loc.isWaterTile((int)n.X, (int)n.Y))
                    return true;
            }
        }
        return false;
    }
    // =====================================================================
    // 农活辅助
    // =====================================================================

    private bool CanTill(Vector2 tile)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null || tile.X < 0f || tile.Y < 0f)
            return false;
        if (loc.terrainFeatures.ContainsKey(tile))
            return false;
        if (loc.objects.ContainsKey(tile))
            return false;
        if (loc.isWaterTile((int)tile.X, (int)tile.Y))
            return false;
        if (loc.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Water", "Back", false) != null)
            return false;
        return loc.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Diggable", "Back", false) != null;
    }

    private bool HasSeed() => FindSeedInInventory() != null;

    private StardewValley.Object FindSeedInInventory()
    {
        if (Game1.player?.Items == null)
            return null;

        foreach (Item item in Game1.player.Items)
        {
            if (item is StardewValley.Object o && !o.bigCraftable.Value && o.Category == -74 && o.Stack > 0)
                return o;
        }
        return null;
    }

    private void ConsumeSeed(StardewValley.Object seed)
    {
        try
        {
            seed.Stack--;
            if (seed.Stack <= 0)
                Game1.player.removeItemFromInventory(seed);
        }
        catch { }
    }

    private bool SafeHarvest(HoeDirt dirt, Vector2 tile)
    {
        try
        {
            if (dirt.crop.harvest((int)tile.X, (int)tile.Y, dirt))
            {
                try
                {
                    if (dirt.crop != null && !dirt.crop.RegrowsAfterHarvest())
                        dirt.destroyCrop(true);
                }
                catch { }
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // =====================================================================
    // 物品 ID 校验（钓鱼/挖矿掉落）
    // =====================================================================
    // 踩过的坑：2.1.0 及以前「可钓鱼类列表」是手写死的，里面混进了 753、754 这种
    // 游戏里根本不存在的 ID。ItemRegistry.Create 遇到不存在的 ID 不抛异常，而是
    // 吐一个「错误物品（754）」——玩家的背包里就多出一格既捡不起也卖不掉的东西，
    // 屏幕上还弹红色报错框。现在所有"按 ID 造物品"的地方都先过下面两道校验：
    //   IsRealItemId  → 这个 ID 在 Data/Objects 里真的存在
    //   IsBrokenItem  → 造出来的东西不是游戏用来占位的错误物品
    // 配置留空则直接用游戏自己的鱼表（FishPool），再也不会造出错物品。

    private static List<string> _fishPool;
    private static bool _fishListReported;

    /// <summary>游戏里所有鱼：Data/Objects 中 Category == -4 的条目（含模组加的鱼）。</summary>
    public static List<string> FishPool()
    {
        if (_fishPool != null)
            return _fishPool;

        var pool = new List<string>();
        try
        {
            int fishCat = StardewValley.Object.FishCategory;
            if (fishCat == 0)
                fishCat = -4;
            var data = Game1.objectData;
            if (data != null)
            {
                foreach (KeyValuePair<string, StardewValley.GameData.Objects.ObjectData> pair in data)
                {
                    if (pair.Value == null || pair.Value.Category != fishCat)
                        continue;
                    if (string.IsNullOrWhiteSpace(pair.Key))
                        continue;
                    pool.Add(pair.Key);
                }
            }
        }
        catch (Exception ex)
        {
            CLog.Warn("[钓鱼] 读取游戏物品表失败: " + ex.Message);
        }

        pool.Sort((a, b) => string.CompareOrdinal(a.PadLeft(8, '0'), b.PadLeft(8, '0')));
        if (pool.Count > 0)
            _fishPool = pool;
        return pool;
    }

    /// <summary>该物品 ID 在这局游戏里是否真的存在（含模组注册的物品）。</summary>
    public static bool IsRealItemId(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return false;
        try
        {
            var data = Game1.objectData;
            return data != null && data.ContainsKey(itemId.Trim());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 是不是游戏用来占位的「错误物品」：纯数字 ID（= 属于 Data/Objects）但表里查不到。
    /// 模组自己注册的非数字 ID 一律不判为损坏，避免误删别人的物品。
    /// </summary>
    public static bool IsBrokenItem(Item item)
    {
        if (item == null || string.IsNullOrEmpty(item.ItemId))
            return true;
        if (!(item is StardewValley.Object))
            return false;
        foreach (char ch in item.ItemId)
        {
            if (!char.IsDigit(ch))
                return false;
        }
        return !IsRealItemId(item.ItemId);
    }

    private static string[] SplitIds(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return Array.Empty<string>();
        return csv.Split(new[] { ',', '，', ' ', '\n', '\r', '\t', ';', '；' },
            StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// 玩家在配置里点名的鱼（已剔除游戏里查不到的 ID）。没配 / 全无效时返回 null，
    /// 表示"交给游戏自己挑"。
    /// </summary>
    private List<string> PickConfiguredFishIds()
    {
        var good = new List<string>();
        var bad = new List<string>();
        foreach (string raw in SplitIds(ModEntry.Config.可钓鱼类列表))
        {
            string id = raw.Trim();
            if (id.Length == 0)
                continue;
            if (IsRealItemId(id))
            {
                if (!good.Contains(id))
                    good.Add(id);
            }
            else if (!bad.Contains(id))
            {
                bad.Add(id);
            }
        }

        if (bad.Count > 0)
        {
            ModEntry.Config.可钓鱼类列表 = string.Join(",", good);
            ModEntry.RequestConfigSave();
            if (!_fishListReported)
            {
                _fishListReported = true;
                CLog.Info($"[钓鱼] 配置「可钓鱼类列表」里有 {bad.Count} 个游戏里不存在、已自动剔除的 ID：{string.Join(",", bad)}");
            }
        }

        return good.Count > 0 ? good : null;
    }

    /// <summary>
    /// 从水里捞一条鱼出来。优先级：
    ///   1. 玩家在配置里点名的鱼（只认真实存在的 ID）
    ///   2. 游戏自己的钓获逻辑（当前地图 / 季节 / 天气 / 时间真的能钓到的鱼）
    ///   3. Data/Objects 里的鱼表兜底
    /// 任何一步都保证不会返回「错误物品」。
    /// </summary>
    private Item PickCatch(Vector2 bobberTile)
    {
        // 原版钓鱼有失败率（取决于钓鱼等级、鱼的大小/稀有度），这里用简化公式：
        // 成功率 = 70% + 钓鱼等级 * 5%（等级 1=75%, 5=95%）
        float successRate = 0.7f + Game1.player.fishingLevel.Value * 0.05f;
        if (successRate > 0.95f) successRate = 0.95f;
        if (Game1.random.Next(100) >= (int)(successRate * 100))
            return null; // 没钓上，AI 需要重新抛竿

        List<string> configured = PickConfiguredFishIds();
        if (configured != null)
            return TryMakeFish(configured[Game1.random.Next(configured.Count)]);

        // 问游戏要"这地方现在真能钓到什么"。万一只捞到破烂（原版运气差/水太浅就是这样），
        // 再问一次；还是破烂就退回鱼表——雇工钓上来的起码是条鱼，不是一堆破鞋。
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Item vanilla = TryVanillaFish(bobberTile);
            if (vanilla == null)
                break;
            if (!IsJunkItem(vanilla))
                return vanilla;
            CLog.Act($"[钓鱼] 这次只捞到杂物 {vanilla.ItemId}，再抛一竿");
        }

        List<string> pool = FishPool();
        if (pool.Count > 0)
            return TryMakeFish(pool[Game1.random.Next(pool.Count)]);
        return null;
    }

    /// <summary>是不是原版钓鱼会钓上来的"垃圾"（破鞋、报纸那类，category = -20）。</summary>
    private static bool IsJunkItem(Item item)
    {
        if (!(item is StardewValley.Object obj))
            return false;
        int junk = StardewValley.Object.junkCategory;
        if (junk == 0)
            junk = -20;   // 常量没初始化时按原版值兜底
        return obj.Category == junk;
    }

    /// <summary>
    /// 直接问游戏「这个水花位置现在能钓到什么」——走原版 getFish，
    /// 所以季节、天气、水深、钓鱼等级、当地鱼种全对得上（也会钓上原版会钓到的杂物）。
    /// </summary>
    private Item TryVanillaFish(Vector2 bobberTile)
    {
        try
        {
            GameLocation loc = Game1.currentLocation;
            if (loc == null || Game1.player == null)
                return null;

            // 别去捞玩家鱼塘里的鱼：原版 getFish 遇到鱼塘格会直接 CatchFish() 扣掉一条。
            // loc.buildings 是 NetCollection（构造时就初始化，永远是空集合而不是 null），
            // 所以直接遍历，不要写 `!= null`（SMAPI 会警告隐式转换）。
            foreach (StardewValley.Buildings.Building b in loc.buildings)
            {
                if (b is StardewValley.Buildings.FishPond pond && pond.isTileFishable(bobberTile))
                    return null;
            }

            // 参数照原版收杆时的取值：500ms 反应时间、没挂鱼饵、水深按 3 格算
            Item item = loc.getFish(500f, null, 3, Game1.player, 0.0, bobberTile, null);
            if (item == null)
                return null;
            if (IsBrokenItem(item))
            {
                CLog.Warn($"[钓鱼] 游戏返回了失效物品 {item.ItemId}，已丢弃");
                return null;
            }
            return item;
        }
        catch (Exception ex)
        {
            CLog.Act("[钓鱼] 调用游戏钓获逻辑失败，改用鱼表兜底: " + ex.Message);
            return null;
        }
    }

    /// <summary>按 ID 造一条鱼；ID 失效就返回 null，绝不把「错误物品」交给玩家。</summary>
    private Item TryMakeFish(string fishId)
    {
        if (!IsRealItemId(fishId))
        {
            CLog.Act($"[钓鱼] 跳过游戏里不存在的物品 ID {fishId}");
            return null;
        }
        try
        {
            Item item = ItemRegistry.Create("(O)" + fishId, 1, 0, true);
            if (item == null || IsBrokenItem(item))
            {
                CLog.Warn($"[钓鱼] {fishId} 生成失败，已丢弃");
                return null;
            }
            return item;
        }
        catch (Exception ex)
        {
            CLog.Act($"[钓鱼] 生成鱼失败({fishId}): {ex.Message}");
            return null;
        }
    }

    private void DropOreAt(Vector2 tile)
    {
        var list = new List<string>();
        foreach (string raw in SplitIds(ModEntry.Config.可挖矿石列表))
        {
            string id = raw.Trim();
            if (id.Length > 0 && IsRealItemId(id))
                list.Add(id);
        }
        if (list.Count == 0)
            return;
        try
        {
            Game1.createObjectDebris(list[Game1.random.Next(list.Count)], (int)tile.X, (int)tile.Y, Game1.player.UniqueMultiplayerID, Game1.currentLocation);
        }
        catch { }
    }

    /// <summary>清掉 AI 背包里的错误物品（旧版钓鱼遗留），返回清掉的组数。</summary>
    public int PurgeBrokenItems()
    {
        int n = 0;
        for (int i = Inventory.Count - 1; i >= 0; i--)
        {
            if (IsBrokenItem(Inventory[i]))
            {
                Inventory.RemoveAt(i);
                n++;
            }
        }
        return n;
    }

    public void GiveItem(Item item)
    {
        // 「错误物品」塞进背包就删不掉，宁可扔掉
        if (item == null || IsBrokenItem(item))
            return;
        try
        {
            Item leftover = Game1.player.addItemToInventory(item);
            if (leftover != null)
                Game1.currentLocation?.debris.Add(new Debris(leftover, Position));
        }
        catch
        {
            try { Game1.currentLocation?.debris.Add(new Debris(item, Position)); }
            catch { }
        }
    }

    /// <summary>背包里有效物品的组数（对话菜单「领取物品」用）。</summary>
    public int InventoryGroups
    {
        // 工具不算"货物"：AI 的竿/镐都是跟玩家借的，不该占背包容量、
        // 也不该被「背包满了交货 / 塞箱子 / 领取物品」顺走。
        get { return Inventory.Count(x => x != null && x.Stack > 0 && x is not Tool); }
    }

    /// <summary>
    /// 把物品（按物品 ID）收进 AI 背包，自动按 maximumStackSize 堆叠。
    /// </summary>
    public void AddToInventory(string itemId, int count)
    {
        if (string.IsNullOrEmpty(itemId) || count <= 0)
            return;
        try
        {
            Item probe = ItemRegistry.Create("(O)" + itemId, 1);
            if (probe == null)
                return;
            int max = Math.Max(1, probe.maximumStackSize());
            while (count > 0)
            {
                Item same = Inventory.FirstOrDefault(x => x != null
                    && x.ItemId == probe.ItemId && x.Stack < max);
                if (same != null)
                {
                    int move = Math.Min(max - same.Stack, count);
                    same.Stack += move;
                    count -= move;
                }
                else
                {
                    int move = Math.Min(max, count);
                    Inventory.Add(ItemRegistry.Create("(O)" + itemId, move));
                    count -= move;
                }
            }
        }
        catch (Exception ex)
        {
            CLog.Warn($"AI背包收纳失败({itemId}): {ex.Message}");
        }
    }

    /// <summary>死亡时把背包物品掉在脚边（玩家可捡回）。</summary>
    public void DropInventory()
    {
        if (Inventory.Count == 0)
            return;
        GameLocation loc = Location ?? Game1.currentLocation;
        if (loc == null)
            return;
        foreach (Item item in Inventory)
        {
            if (item == null || item.Stack <= 0)
                continue;
            try { loc.debris.Add(new Debris(item, Position)); }
            catch { }
        }
        Inventory.Clear();
    }

    // =====================================================================
    // 移动 / 战斗 / 生命
    // =====================================================================

    public static Vector2 DebrisPos(Debris d)
    {
        try
        {
            if (d != null && d.Chunks.Count > 0)
                return d.Chunks[0].position.Value;
        }
        catch { }
        return Vector2.Zero;
    }

    private bool IsMine(GameLocation loc)
    {
        string name = loc.Name;
        if (string.IsNullOrEmpty(name))
            return false;
        if (name.Contains("UndergroundMine") || name.Contains("VolcanoDungeon"))
            return true;
        return name.Contains("SkullCave");
    }

    private bool SameLocation(Monster m) => m.currentLocation == Game1.currentLocation;

    public void MoveToward(Vector2 target, float speed, double dtMs)
    {
        Vector2 delta = target - Position;
        float dist = delta.Length();
        if (dist < 3f)
        {
            _path.Clear();
            return;
        }

        Vector2 dir = delta;
        dir.Normalize();
        float step = Math.Min(speed * (float)dtMs / 16.6667f, dist);

        // 1) 先走直线（TryStep 内部带轴向滑动）
        if (TryStep(dir * step))
        {
            _path.Clear();
            FaceBy(dir);
            return;
        }

        // 2) 直线被挡：在网格上做一次小范围 BFS，沿路点绕过去。
        //    注意别把「路径为空」也当成重算条件——BFS 失败时会返回空表，
        //    那样就会变成每帧跑 240 次碰撞检测，直接掉帧。
        Vector2 goalTile = new Vector2((int)(target.X / 64f), (int)(target.Y / 64f));
        if (Clock >= _nextPathTime || goalTile != _pathGoal)
        {
            _nextPathTime = Clock + 600.0;
            _pathGoal = goalTile;
            _path = BuildPath(Tile, goalTile);
        }

        // 丢掉已经踩过的路点
        while (_path.Count > 0
               && Vector2.Distance(Position, _path[0] * 64f + new Vector2(32f, 32f)) < 20f)
        {
            _path.RemoveAt(0);
        }
        if (_path.Count == 0)
        {
            // BFS 也没找到路（目标太远/地形太绕）→ 沿垂直方向贴着障碍侧滑一步。
            // 以前这里直接 return，玩家看到的就是 AI 顶着「去水边」一动不动。
            Vector2 side = new Vector2(-dir.Y, dir.X);
            if (TryStep(side * step))
            {
                FaceBy(side);
                return;
            }
            TryStep(new Vector2(-side.X, -side.Y) * step);
            return;
        }

        Vector2 wp = _path[0] * 64f + new Vector2(32f, 32f);
        Vector2 d2 = wp - Position;
        if (d2.LengthSquared() < 0.01f)
            return;
        d2.Normalize();
        TryStep(d2 * step);
        FaceBy(d2);
    }

    /// <summary>从 from 开始按环形向外找一个能站的格子，找不到返回 Vector2.Zero。</summary>
    private Vector2 FindNearestStandable(Vector2 from, int radius)
    {
        for (int r = 1; r <= radius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        continue;
                    Vector2 t = from + new Vector2(dx, dy);
                    if (t.X < 0f || t.Y < 0f)
                        continue;
                    if (CanStandAt(t))
                        return t;
                }
            }
        }
        return Vector2.Zero;
    }

    private void FaceBy(Vector2 dir)
    {
        if (Math.Abs(dir.X) > Math.Abs(dir.Y) + 0.01f)
            Facing = dir.X > 0f ? 1 : 3;
        else if (Math.Abs(dir.Y) > 0.01f)
            Facing = dir.Y > 0f ? 2 : 0;
    }

    /// <summary>按"先试整步，再试 X/Y 单轴"的顺序移动，成功返回 true。</summary>
    private bool TryStep(Vector2 move)
    {
        return TryMove(move) || TryMove(new Vector2(move.X, 0f)) || TryMove(new Vector2(0f, move.Y));
    }

    /// <summary>某个瓦片能不能站人（用与移动相同的碰撞盒判断，保证一致）。</summary>
    /// <summary>
    /// 这一格是不是建筑（农舍、谷仓、棚屋…）的占位。
    ///
    /// `isCollidingPosition` 对农舍台阶/门廊那几格是放行的（玩家要走到门口按门），
    /// 但建筑的贴图会盖住整个台阶 —— AI 站上去就只剩一个草帽在外面（玩家截图）。
    /// 原版玩家根本走不进去，所以这里也一律排除。
    /// </summary>
    private static bool HasBuildingAt(GameLocation loc, Vector2 tile)
    {
        if (loc == null || tile.X < 0f || tile.Y < 0f)
            return false;
        try { return loc.getBuildingAt(tile) != null; }
        catch { return false; }
    }

    private bool CanStandAt(Vector2 tile)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return false;

        if (HasBuildingAt(loc, tile))
            return false;

        Rectangle box = new Rectangle((int)(tile.X * 64f) + 8, (int)(tile.Y * 64f) + 16, 48, 48);
        try
        {
            return !loc.isCollidingPosition(box, Game1.viewport, Game1.player);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>目标瓦片四周是否有 AI 能站的格子（4 方向之一可站即算）。
    /// 用于过滤"被树/石头围死、根本站不过去"的目标，防止 AI 反复去够够不着的目标而卡位置。</summary>
    private bool HasStandSpot(Vector2 tile)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return false;
        foreach (Vector2 n in new[]
        {
            tile + new Vector2(1f, 0f), tile + new Vector2(-1f, 0f),
            tile + new Vector2(0f, 1f), tile + new Vector2(0f, -1f),
        })
        {
            if (n.X < 0f || n.Y < 0f)
                continue;
            if (CanStandAt(n))
                return true;
        }
        return false;
    }

    /// <summary>目标瓦片周围最近的可站格（先试 4 方向直邻，再试 8 方向对角）。
    /// 同类里挑"离 AI 现在最近的"，避免选到被石头/围栏隔开的另一侧而永远走不到。
    /// 四周全被占（目标被围死）时返回 Vector2.Zero。</summary>
    private Vector2 FindStandSpot(Vector2 tile)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return Vector2.Zero;

        Vector2 here = Tile;

        Vector2 Pick(Vector2[] list)
        {
            Vector2 best = Vector2.Zero;
            float bestDist = float.MaxValue;
            foreach (Vector2 n in list)
            {
                if (n.X < 0f || n.Y < 0f)
                    continue;
                if (!CanStandAt(n))
                    continue;
                float d = Vector2.Distance(here, n);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = n;
                }
            }
            return best;
        }

        Vector2 spot = Pick(new[]
        {
            tile + new Vector2(1f, 0f), tile + new Vector2(-1f, 0f),
            tile + new Vector2(0f, 1f), tile + new Vector2(0f, -1f),
        });
        if (spot != Vector2.Zero)
            return spot;

        return Pick(new[]
        {
            tile + new Vector2(1f, 1f), tile + new Vector2(-1f, 1f),
            tile + new Vector2(1f, -1f), tile + new Vector2(-1f, -1f),
        });
    }

    /// <summary>
    /// 小范围 BFS 寻路：从 start 出发找一条到 goal 相邻格的路径（goal 本身常被石头/树占着，
    /// 走不进去）。节点上限 800、范围 ±22 格。
    /// 原来是 240 节点：BFS 从起点一圈圈扩散，240 个节点只覆盖半径 7~8 格，
    /// 于是"直线被房子挡住、水在 10 格开外"这种情况永远找不到路 → AI 原地不动
    /// （玩家看到的就是头顶「去水边」但人一步不走）。只在直线被挡时才跑，且 600ms 最多一次。
    /// </summary>
    private List<Vector2> BuildPath(Vector2 start, Vector2 goal)
    {
        List<Vector2> result = new List<Vector2>();
        if (start == goal)
            return result;

        Dictionary<Vector2, Vector2> cameFrom = new Dictionary<Vector2, Vector2>();
        Queue<Vector2> queue = new Queue<Vector2>();
        queue.Enqueue(start);
        cameFrom[start] = start;

        Vector2 found = new Vector2(-1f, -1f);
        int visited = 0;
        while (queue.Count > 0 && visited < 800)
        {
            Vector2 cur = queue.Dequeue();
            visited++;

            // 走到目标旁边就算到（相邻上下左右任一格）
            if (Vector2.Distance(cur, goal) <= 1.05f)
            {
                found = cur;
                break;
            }

            foreach (Vector2 n in new[]
            {
                cur + new Vector2(1f, 0f), cur + new Vector2(-1f, 0f),
                cur + new Vector2(0f, 1f), cur + new Vector2(0f, -1f),
            })
            {
                if (cameFrom.ContainsKey(n))
                    continue;
                if (Math.Abs(n.X - start.X) > 22f || Math.Abs(n.Y - start.Y) > 22f)
                    continue;
                if (n.X < 0f || n.Y < 0f)
                    continue;
                if (!CanStandAt(n))
                    continue;

                cameFrom[n] = cur;
                queue.Enqueue(n);
            }
        }

        if (found.X < 0f)
            return result;

        // 回溯路径（去掉起点自己）
        Vector2 node = found;
        while (node != start)
        {
            result.Add(node);
            if (!cameFrom.TryGetValue(node, out Vector2 prev) || prev == node)
                break;
            node = prev;
        }
        result.Reverse();
        return result;
    }

    private bool TryMove(Vector2 delta)
    {
        if (delta.LengthSquared() < 0.001f)
            return false;

        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return false;

        Vector2 next = Position + delta;

        // 建筑占位格（农舍台阶、门廊）不许踏进去：
        // 那里 isCollidingPosition 放行，但建筑贴图会把人盖掉大半个身子。
        // 例外：已经站在建筑格上时允许移动，否则被锁死走不出来。
        if (!HasBuildingAt(loc, Tile)
            && HasBuildingAt(loc, new Vector2((int)(next.X / 64f), (int)(next.Y / 64f))))
            return false;

        // 正常碰撞盒（48x48，覆盖人物躯干+脚）
        if (CanMoveTo(next, 8, 16, 48, 48))
        {
            Position = next;
            return true;
        }

        // 贴墙逃生：大盒子和房子/围栏一直互相重叠时，人可以"永远迈不出第一步"
        // （玩家看到的就是头顶有任务、人却钉在原地）。退化用脚下小盒再试一次，
        // 最多视觉上贴得近一点，但能走出来。
        if (CanMoveTo(next, 18, 34, 28, 26))
        {
            Position = next;
            return true;
        }

        return false;
    }

    /// <summary>把"能不能挪到这个位置"独立出来（两套盒子的判定要完全一致）。</summary>
    private static bool CanMoveTo(Vector2 pos, int ox, int oy, int w, int h)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return false;
        Rectangle box = new Rectangle((int)pos.X + ox, (int)pos.Y + oy, w, h);
        try
        {
            return !loc.isCollidingPosition(box, Game1.viewport, Game1.player);
        }
        catch
        {
            return false;
        }
    }

    public void FaceTowards(Vector2 worldPos)
    {
        Vector2 delta = worldPos - Position;
        if (Math.Abs(delta.X) > Math.Abs(delta.Y))
            Facing = delta.X > 0f ? 1 : 3;
        else
            Facing = delta.Y > 0f ? 2 : 0;
    }

    private void UpdateMonsterDamage()
    {
        if (Clock < InvulnerableUntil || Dead)
            return;

        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return;

        foreach (NPC c in loc.characters)
        {
            if (c is Monster m && m.Health > 0 && Vector2.Distance(Position, c.Position) / 64f < 1.1f)
            {
                TakeDamage(m.DamageToFarmer);
                break;
            }
        }
    }

    public void TakeDamage(int dmg)
    {
        if (Clock < InvulnerableUntil || Dead || !Active)
            return;

        InvulnerableUntil = Clock + ModEntry.Config.受伤无敌毫秒;
        Health -= dmg;
        if (Health <= 0)
            Die();
    }

    public void Die()
    {
        Dead = true;
        DeadDay = SaveData.AbsoluteDay();
        ModEntry.Data.死亡次数++;
        ModEntry.Data.已雇佣 = false;
        ModEntry.Data.本次雇佣天数 = 0;

        // 背包里的东西掉在脚边，玩家可以捡回来（干活的收获不能凭空消失）。
        // 必须在 SaveDataToDisk 之前清空，否则存档里还留着这批物品，读档会复制一份。
        DropInventory();
        if (ModEntry.Config.死亡掉落物品)
            DropOreAt(Tile);

        ModEntry.Instance.SaveDataToDisk();

        string price = ModEntry.Instance.HirePrice().ToString();
        Game1.showRedMessage($"AI {ModEntry.Config.AI名字} 倒下了！重新雇佣价格变为 {price} G", true);
    }

    private void UpdateStuck(double dtMs)
    {
        // 真实位移检测：供渲染器切换行走动画（站着砍树/钓鱼时不播走路帧）
        WasMoving = Vector2.DistanceSquared(Position, _lastPos) > 4f;

        if (WasMoving)
            _noMoveTotal = 0.0;
        else
            _noMoveTotal += dtMs;

        // 兜底：连一步都挪不动，又站在一个"本来就不能站"的格子上
        // （被机器/新长出来的树/玩家摆的物件压住了）→ 挪到最近的空位脱困
        if (_noMoveTotal > 2500.0 && !Sleeping && !CanStandAt(Tile))
        {
            Vector2 free = FindNearestStandable(Tile, 4);
            if (free != Vector2.Zero)
            {
                Position = free * 64f;
                _noMoveTotal = 0.0;
                _path.Clear();
                _lastPos = Position;
            }
        }

        if (Job.Type != JobType.无)
        {
            // 不同活计的"合理静止时长"不一样：
            // 钓鱼本来就要站着等（默认 9 秒），挥斧/敲石时也会站着不动。
            // 以前一律 2.5 秒算卡住，结果钓鱼每 2.5 秒被打断一次，永远钓不上来。
            double grace = Job.Type switch
            {
                JobType.钓鱼 => ModEntry.Config.钓鱼等待毫秒 + 6000.0,
                JobType.无 => 0.0,
                // 砍树要站着连挥好几刀（尤其攻击力调低时），5 秒会把"正在砍"误判成"卡住"
                JobType.砍树 => 9000.0,
                // 走到箱子边、往里塞东西也会有一小段时间不动
                JobType.存背包 => 9000.0,
                _ => 5000.0,
            };

            if (!WasMoving)
            {
                _stuckTimer += dtMs;
                if (_stuckTimer > grace)
                {
                    _stuckTimer = 0.0;
                    // 走不到就把这个目标拉黑 1 分钟，否则下一次决策又会挑中它，
                    // 玩家看到的就是"一直卡在原地、头顶写着清理中"
                    // 钓鱼也要拉黑：连水格带岸边一起黑，否则永远在"钓鱼中"里打转。
                    if (Job.Type == JobType.钓鱼)
                    {
                        Avoid(FishTile);
                        ResetFishing();
                    }
                    else
                    {
                        Avoid(Job.Tile);
                    }
                    Job.Type = JobType.无;
                    _path.Clear();
                }
            }
            else
            {
                _stuckTimer = 0.0;
            }
        }
        else
        {
            _stuckTimer = 0.0;
        }
        _lastPos = Position;
    }

    // =====================================================================
    // 状态查询
    // =====================================================================

    public bool OutsideWorkHours()
    {
        if (Game1.timeOfDay >= ModEntry.Config.开始工作时间)
            return Game1.timeOfDay >= ModEntry.Config.回家睡觉时间;
        return true;
    }

    /// <summary>本帧位置是否真的在变化（按上一帧位移计算）。</summary>
    public bool WasMoving;

    /// <summary>当前是否在行走（供渲染器切换行走动画帧）。站着干活时为 false。</summary>
    public bool IsWalking
    {
        get { return WasMoving && !Sleeping && !Dead; }
    }

    /// <summary>行走动画循环计时（毫秒）。</summary>
    public double WalkAnimTime => _walkAnimTime;

    /// <summary>呼吸/浮动动画相位（毫秒计时），供渲染器使用。</summary>
    public double BobPhase() => _walkAnimTime;
}
