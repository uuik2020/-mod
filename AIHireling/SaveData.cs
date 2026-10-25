using System;
using System.Collections.Generic;
using StardewValley;

namespace AIHireling;

/// <summary>
/// 存档级持久化数据（Helper.Data.WriteSaveData / ReadSaveData）。
/// 旧版 AIHireling 的存档数据键为 "aihireling"，字段名一致，直接兼容。
/// </summary>
[Serializable]
public class SaveData
{
    public int 死亡次数 { get; set; }
    public int 累计打工天数 { get; set; }
    public bool 曾经受雇 { get; set; }
    public bool 已雇佣 { get; set; }
    public int 本次雇佣天数 { get; set; }
    public int 死亡当天 { get; set; } = -1;
    public int 欠薪 { get; set; }
    public int 上次处理日 { get; set; } = -1;

    /// <summary>AI 当前的体力（2.4.0）。-1 = 老存档没这个字段，读档时按满体力处理。</summary>
    public int 体力 { get; set; } = -1;

    /// <summary>「干不了」提示的计数（2.5.3）：按提示内容分别记今天说了几次、静默到哪天。</summary>
    public List<HintRecord> 提示记录 { get; set; } = new List<HintRecord>();

    /// <summary>AI 背包里待领取的物品（ItemId 形如 "(O)390"）。读档时重建，
    /// 否则玩家保存退出后 AI 捡的东西会凭空消失。</summary>
    public List<StoredItem> 背包物品 { get; set; } = new List<StoredItem>();

    /// <summary>当前游戏日的绝对序号，用于跨年/跨季比较。</summary>
    public static int AbsoluteDay()
    {
        int season = Game1.currentSeason switch
        {
            "spring" => 0,
            "summer" => 1,
            "fall" => 2,
            _ => 3,
        };
        return Game1.year * 1000 + season * 28 + Game1.dayOfMonth;
    }
}

/// <summary>一条提示的计数状态：今天说了几次、静默到哪一天（绝对日序号）。</summary>
[Serializable]
public class HintRecord
{
    /// <summary>提示的标识，如 "没鱼竿" / "没镐子" / "没体力"。</summary>
    public string Key { get; set; } = "";

    /// <summary>最后一次计数的绝对日（SaveData.AbsoluteDay()）。</summary>
    public int 天 { get; set; } = -1;

    /// <summary>这一天已经提示过几次。</summary>
    public int 次数 { get; set; }

    /// <summary>静默到这个绝对日为止（不含当天之后）：小于等于"今天"就说明静默已过。</summary>
    public int 静默到 { get; set; } = -1;
}

/// <summary>存档用的背包物品条目（只存 ID 和数量，不直接序列化 Item）。</summary>
[Serializable]
public class StoredItem
{
    public string ItemId { get; set; } = "";
    public int Stack { get; set; }
}
