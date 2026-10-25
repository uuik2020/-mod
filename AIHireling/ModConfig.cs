using System;

namespace AIHireling;

/// <summary>
/// AI雇工全部可调参数。字段名保持与旧版 config.json 一致（中文键名），
/// 因此旧存档目录下的 config.json 可被 SMAPI 的 ReadConfig 直接读取、无缝迁移。
/// </summary>
public class ModConfig
{
    // ---------- 基础 ----------
    public bool 启用模组 { get; set; } = true;
    public string AI名字 { get; set; } = "小星";
    public string 对话按键 { get; set; } = "E";

    // ---------- 雇佣与工资 ----------
    public int 雇佣基础价格 { get; set; } = 500;
    public int 日工资 { get; set; } = 100;
    public int 免费打工天数 { get; set; } = 2;
    public bool 工资早上预付 { get; set; } = true;
    public bool 欠薪自动解雇 { get; set; } = true;
    public int 死亡后等待天数 { get; set; } = 0;

    // ---------- 死亡涨价 ----------
    public int 每次死亡涨价 { get; set; } = 200;
    public bool 涨价使用倍率 { get; set; } = false;
    public float 死亡涨价倍率 { get; set; } = 0.5f;
    public bool 死亡掉落物品 { get; set; } = false;

    // ---------- 工作行为 ----------
    public bool 行为_跟随 { get; set; } = true;
    public bool 行为_打怪 { get; set; } = true;
    public bool 行为_收获 { get; set; } = true;
    public bool 行为_浇水 { get; set; } = true;
    public bool 行为_翻地 { get; set; } = true;
    public bool 行为_播种 { get; set; } = true;
    public bool 行为_采集 { get; set; } = true;
    public bool 行为_砍树 { get; set; } = true;
    public bool 行为_清理 { get; set; } = true;
    public bool 行为_挖矿 { get; set; } = true;
    public bool 行为_钓鱼 { get; set; } = true;

    // ---------- AI 背包 ----------
    /// <summary>背包装到这么多组就算满：自动跑到玩家面前交货（0 = 不限）。</summary>
    public int 背包容量组数 { get; set; } = 12;
    /// <summary>背包满了、附近又有箱子时，自己走过去塞进去（不用玩家手动选）。</summary>
    public bool 背包满了自动找箱子 { get; set; } = true;
    /// <summary>背包满了是否自动跑到玩家面前问一句。</summary>
    public bool 背包满了自动找玩家 { get; set; } = true;

    // ---------- 作息与移动 ----------
    public bool 夜晚自动回家 { get; set; } = true;
    public int 开始工作时间 { get; set; } = 600;
    public int 回家睡觉时间 { get; set; } = 2200;
    public float 移动速度 { get; set; } = 5f;
    public float 跟随距离 { get; set; } = 3f;
    public float 瞬移距离 { get; set; } = 18f;
    public float 工作范围 { get; set; } = 14f;

    // ---------- 战斗 ----------
    public int AI血量 { get; set; } = 120;
    public int 攻击力 { get; set; } = 12;
    public float 索敌范围 { get; set; } = 9f;
    public float 攻击距离 { get; set; } = 1.6f;
    public int 攻击间隔毫秒 { get; set; } = 700;
    public int 受伤无敌毫秒 { get; set; } = 900;

    // ---------- 采集/挖矿/钓鱼 ----------
    // 留空 = 用游戏自己的鱼表（Data/Objects 里 Category == -4 的全部条目），
    // 想限定就填逗号分隔的物品 ID；填了游戏里不存在的 ID 会被自动剔除并写回配置，
    // 不会像 2.1.0 以前那样变成「错误物品（754）」塞进背包。
    public string 可钓鱼类列表 { get; set; } = "";
    public string 可挖矿石列表 { get; set; } = "378,380,382,384,386,390";
    public int 钓鱼等待毫秒 { get; set; } = 9000;

    // ---------- 钓鱼的花费（2.4.0：往原版靠，不再白嫖） ----------
    // 原版钓鱼：手里得有鱼竿、每竿要挂鱼饵、抛竿还扣体力（8 - 钓鱼等级*0.1）。
    // 这三项默认全开，AI 钓鱼才不是凭空变鱼；想图省事可以各自关掉。
    /// <summary>钓鱼必须有鱼竿（AI 背包或玩家背包里有一根就行）。</summary>
    public bool 钓鱼需要鱼竿 { get; set; } = true;
    /// <summary>每抛一竿消耗 1 个鱼饵（先用 AI 背包里的，其次用玩家背包里的）。</summary>
    public bool 钓鱼消耗鱼饵 { get; set; } = true;
    /// <summary>每抛一竿扣体力，数值同原版：8 - 玩家钓鱼等级*0.1。</summary>
    public bool 钓鱼消耗体力 { get; set; } = true;
    /// <summary>AI 体力上限（原版玩家 270）。白天干活消耗，睡一觉回满。</summary>
    public int AI体力上限 { get; set; } = 270;

    // ---------- 干活受阻的提示（2.5.3） ----------
    // 缺鱼竿 / 缺镐子 / 没体力 这类"干不了"的提示，以前每 45 秒喊一次，
    // 一直缺就一直喊（SMAPI 控制台也跟着刷屏）。现在同一句话一天最多说几次，
    // 说满之后静默若干天，过了静默期再重新允许。
    /// <summary>同一条"干不了"提示一天最多说几次（0 = 完全不提示）。</summary>
    public int 提示每天最多次数 { get; set; } = 2;
    /// <summary>说满次数后静默几天（0 = 当天不再提示，第二天恢复）。</summary>
    public int 提示静默天数 { get; set; } = 3;

    // ---------- 控制台外观（2.6.2） ----------
    /// <summary>SMAPI 控制台里用颜色区分信息 / 成功 / 警告 / 错误。</summary>
    public bool 控制台彩色 { get; set; } = true;

    /// <summary>日志前面加几何图标（◆ ▶ √ ▲ × ★）。字体缺字时关掉。</summary>
    public bool 控制台图标 { get; set; } = true;

    /// <summary>AI 站进草地/作物里时，身体会被草叶和作物挡住（同原版玩家）。</summary>
    public bool 身体被作物遮挡 { get; set; } = true;

    // ---------- 挖矿的花费（2.5.0：和钓鱼一样，不白嫖） ----------
    /// <summary>挖矿必须有镐子（AI 背包或玩家背包里有一把就行）。</summary>
    public bool 挖矿需要镐子 { get; set; } = true;
    /// <summary>每敲一下扣体力，数值同原版镐子：2 - 挖矿等级*0.1。</summary>
    public bool 挖矿消耗体力 { get; set; } = true;

    // ---------- 随时存档 ----------
    public bool 允许随时存档 { get; set; } = true;
    public string 存档快捷键 { get; set; } = "F5";
    public bool 存档前自动备份 { get; set; } = true;
    /// <summary>只在「回家睡觉时间」之后才允许随时存档（模拟原版"到点才能睡"）。</summary>
    public bool 存档需到睡觉时间 { get; set; } = false;

    // ---------- 对话文本 ----------
    public string 文本_招呼 { get; set; } = "你好，农夫！我是{name}。前{free}天我免费给你打工，之后每天收{wage}G工资。要雇佣我吗？";
    public string 文本_雇佣成功 { get; set; } = "成交！{price}G到手。接下来{free}天我免费干活，之后每天{wage}G。开始工作吧！";
    public string 文本_拒绝 { get; set; } = "好吧……想好了随时找我，我就守在门口。";
    public string 文本_钱不够雇佣 { get; set; } = "你的钱不够付{price}G的雇佣费，先去赚点钱吧。";
    public string 文本_工资结算 { get; set; } = "今天的工资{wage}G已结清，谢谢老板！";
    public string 文本_欠薪 { get; set; } = "糟糕，工资不够了……我先回门口歇着，钱够了再来找我。";
    public string 文本_死亡 { get; set; } = "呃啊……我撑不住了……";
    public string 文本_重生 { get; set; } = "我把自己修好了！不过重新雇佣现在要{price}G，比上次贵了些……要雇佣我吗？";
    public string 文本_遣散 { get; set; } = "知道了，我先回门口待命，想再雇我随时说。";
    public string 文本_待命 { get; set; } = "好的，我就在这儿待命。";
    public string 文本_菜单标题 { get; set; } = "要和{name}做什么？";
    public string 文本_自动模式 { get; set; } = "好的，进入自动模式：收获、浇水、翻地、播种、采集、砍树、农场清理、打怪我全包了！";
    public string 文本_跟随模式 { get; set; } = "好的，我只跟着你，不干活了。";
    public string 文本_农场模式 { get; set; } = "好的，专心伺候庄稼！";
    public string 文本_战斗模式 { get; set; } = "好的，进入战斗模式，我来保护你！";
    public string 文本_农场清理模式 { get; set; } = "好的，清理模式！杂草、枯苗、树枝、石头、树桩我都包了！";
    public string 文本_砍树模式 { get; set; } = "好的，砍树模式！树我来放倒，木头和种子都收进我背包，随时找我要。";
    public string 文本_挖矿模式 { get; set; } = "好的，挖矿模式！带我去矿洞吧。";
    public string 文本_钓鱼模式 { get; set; } = "好的，钓鱼模式！找个水边就行。";
    public string 文本_存档未到点 { get; set; } = "现在才 {now}，还没到睡觉的点呢——要 {sleep} 才能睡，还差 {left}。等到了我再帮你存。";
    public string 文本_背包满 { get; set; } = "老板，我背包塞满啦（{groups} 组），实在装不下新的了。这些是直接交给你，还是我找个箱子放进去？";
    public string 文本_没鱼竿 { get; set; } = "老板，我连根鱼竿都没有，总不能下手去捞吧……给我一根（放你背包里就行），我立马去钓。";
    public string 文本_没体力 { get; set; } = "累坏我了（体力 {energy}/{max}），再干下去就得打瞌睡了。让我歇一会儿吧。";
    public string 文本_没镐子 { get; set; } = "老板，我手上没镐子，石头我可砸不动……你把镐子放背包里就行，我借来用。";

    /// <summary>把越界/非法值夹回合理范围，防止手改 config.json 把游戏搞崩。</summary>
    public void Sanitize()
    {
        if (string.IsNullOrWhiteSpace(AI名字))
            AI名字 = "小星";
        if (string.IsNullOrWhiteSpace(对话按键))
            对话按键 = "E";

        雇佣基础价格 = Clamp(雇佣基础价格, 0, 9999999);
        日工资 = Clamp(日工资, 0, 999999);
        免费打工天数 = Clamp(免费打工天数, 0, 365);
        死亡后等待天数 = Clamp(死亡后等待天数, 0, 30);

        每次死亡涨价 = Clamp(每次死亡涨价, 0, 9999999);
        死亡涨价倍率 = ClampF(死亡涨价倍率, 0f, 100f);

        开始工作时间 = Clamp(开始工作时间, 0, 2600);
        回家睡觉时间 = Clamp(回家睡觉时间, 600, 2600);
        移动速度 = ClampF(移动速度, 0.5f, 20f);
        跟随距离 = ClampF(跟随距离, 0.5f, 30f);
        瞬移距离 = ClampF(瞬移距离, 2f, 100f);
        工作范围 = ClampF(工作范围, 1f, 60f);

        AI血量 = Clamp(AI血量, 10, 100000);
        攻击力 = Clamp(攻击力, 1, 10000);
        索敌范围 = ClampF(索敌范围, 1f, 40f);
        攻击距离 = ClampF(攻击距离, 0.5f, 10f);
        攻击间隔毫秒 = Clamp(攻击间隔毫秒, 100, 60000);
        受伤无敌毫秒 = Clamp(受伤无敌毫秒, 100, 60000);
        钓鱼等待毫秒 = Clamp(钓鱼等待毫秒, 500, 120000);
        背包容量组数 = Clamp(背包容量组数, 0, 999);
        AI体力上限 = Clamp(AI体力上限, 20, 2000);
        提示每天最多次数 = Clamp(提示每天最多次数, 0, 20);
        提示静默天数 = Clamp(提示静默天数, 0, 60);

        // 留空是合法的：表示"游戏里所有鱼"。真正的 ID 校验在 Companion.PickFishId 里做，
        // 因为要等 Data/Objects 加载完才知道哪些 ID 存在。
        if (string.IsNullOrWhiteSpace(可挖矿石列表))
            可挖矿石列表 = "378,390";
        if (string.IsNullOrWhiteSpace(存档快捷键))
            存档快捷键 = "F5";
    }

    private static int Clamp(int v, int lo, int hi)
    {
        if (v < lo) return lo;
        if (v > hi) return hi;
        return v;
    }

    private static float ClampF(float v, float lo, float hi)
    {
        if (v < lo) return lo;
        if (v > hi) return hi;
        return v;
    }
}
