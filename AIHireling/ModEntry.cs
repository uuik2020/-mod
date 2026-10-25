using System;
using System.Collections.Generic;
using System.Linq;
using GenericModConfigMenu;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace AIHireling;

public class ModEntry : Mod
{
    public static ModEntry Instance;
    public static ModConfig Config;
    public static IModHelper SHelper;
    public static IMonitor SMonitor;
    public static SaveData Data;
    public static Texture2D SpriteTexture;
    public static Companion Companion;
    public static DialogueSystem Dialogue;

    private SButton _talkKey = SButton.E;
    private SButton _saveKey = SButton.F5;
    private double _lastMs;
    private bool _gmcmRegistered;

    public bool IsHired => Data != null && Data.已雇佣;

    public override void Entry(IModHelper helper)
    {
        Instance = this;
        SHelper = helper;
        SMonitor = Monitor;

        // 打开 Windows 控制台的 ANSI 解析，否则下面那些颜色码会显示成 ←[38;5;51m。
        // 拿不到控制台（日志重定向 / 老 conhost）时内部会自动退回纯文本。
        ConsoleArt.Init();

        // 把 AI 的身体插进原版绘制流程（drawFarmers 之后 = 和玩家同一层），
        // 这样草叶/作物/树才会像盖住玩家那样盖住他。失败会自动退回老路径。
        DrawHook.Apply();

        Config = helper.ReadConfig<ModConfig>();
        Config.Sanitize();
        Data = new SaveData();
        Dialogue = new DialogueSystem();
        ParseTalkKey();
        ParseSaveKey();

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Display.RenderedWorld += OnRenderedWorld;
        helper.Events.Display.Rendered += OnRendered;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Player.Warped += OnWarped;

        helper.ConsoleCommands.Add("ai_cfg",
            "查看/修改 AI雇工 参数。用法：ai_cfg（查看）｜ai_cfg wage 150｜ai_cfg hp 200",
            OnConfigCommand);

        // 版本号取自 manifest，别再手写（2.1.1/2.1.2 时期这里写死成 2.1.1，日志对不上版本）。
        // 控制台里分多行写：SMAPI 只给第一行加 [INFO 模组名] 前缀，后面几行原样输出，
        // 比一整行几百字被窗口硬折行好看得多。
        bool saveHotkeyOk = Config.允许随时存档
            && !string.IsNullOrEmpty(Config.存档快捷键)
            && Config.存档快捷键 != "无";

        var banner = new List<(string, string, char)>
        {
            ("找他说话", "走到他面前（农舍门外）按 " + Config.对话按键, 'p'),
            ("改参数", "两种方式，任选一种：", 'p'),
            ("", "① 游戏里按 " + Config.对话按键 + " 找他 →「设置（调参数）」，上下选行、左右改值", 'd'),
            ("", "② 在 SMAPI 那个黑窗口里输入 ai_cfg 再按回车，会把参数全列出来", 'd'),
        };
        if (saveHotkeyOk)
            banner.Insert(1, ("随时存档", "按 " + Config.存档快捷键 + "，走的是游戏原版存档", 'p'));
        banner.Add(("", ConsoleArt.AnsiOk
            ? "控制台已上色（可在「设置 → 控制台显示」里关掉）"
            : "当前控制台不支持颜色，已自动退回纯文本", 'd'));

        ConsoleArt.Banner("AI雇工 " + ModManifest.Version + " 已加载", banner.ToArray());
    }

    /// <summary>
    /// 控制台改配置。设置菜单万一在某些键位/输入设备上不灵，这条命令永远可用；
    /// 出问题时让玩家敲一次 ai_cfg，也能把当前参数全打进日志，便于远程定位。
    /// </summary>
    private void OnConfigCommand(string command, string[] args)
    {
        if (args.Length == 0)
        {
            // 分块多行打印：一行几百字被控制台硬折行看着很难受
            CLog.Info("[当前配置]"
                + $"\n    工资    日={Config.日工资}  免费={Config.免费打工天数}天"
                + $"  雇佣={Config.雇佣基础价格}  死亡涨价={Config.每次死亡涨价}  重生等={Config.死亡后等待天数}天"
                + $"\n    战斗    血量={Config.AI血量}  攻击={Config.攻击力}  间隔={Config.攻击间隔毫秒}ms"
                + $"\n    移动    速度={Config.移动速度}  范围={Config.工作范围}格"
                + $"  上班={TimeStr(Config.开始工作时间)}  睡觉={TimeStr(Config.回家睡觉时间)}"
                + $"  夜晚回家={OnOff(Config.夜晚自动回家)}"
                + $"\n    行为    清理={OnOff(Config.行为_清理)}  打怪={OnOff(Config.行为_打怪)}"
                + $"  砍树={OnOff(Config.行为_砍树)}  挖矿={OnOff(Config.行为_挖矿)}  钓鱼={OnOff(Config.行为_钓鱼)}"
                + $"\n    钓鱼    需要鱼竿={OnOff(Config.钓鱼需要鱼竿)}  耗鱼饵={OnOff(Config.钓鱼消耗鱼饵)}"
                + $"  耗体力={OnOff(Config.钓鱼消耗体力)}  体力上限={Config.AI体力上限}"
                + $"\n    挖矿    需要镐子={OnOff(Config.挖矿需要镐子)}  耗体力={OnOff(Config.挖矿消耗体力)}"
                + $"\n    提示    每天最多={Config.提示每天最多次数}次  之后静默={Config.提示静默天数}天"
                + $"\n    控制台  彩色={OnOff(Config.控制台彩色)}  图标={OnOff(Config.控制台图标)}"
                + $"  身体被遮挡={OnOff(Config.身体被作物遮挡)}"
                + $"\n    背包箱  满了自动塞箱子={OnOff(Config.背包满了自动找箱子)}"
                + $"  本机支持颜色={(ConsoleArt.AnsiOk ? "是" : "否")}"
                + $"\n    背包    容量={Config.背包容量组数}组  满了找你={OnOff(Config.背包满了自动找玩家)}"
                + $"\n    存档    随时={OnOff(Config.允许随时存档)}  快捷键={Config.存档快捷键}"
                + $"  只在睡觉时间={OnOff(Config.存档需到睡觉时间)}");
            CLog.Info("[用法]"
                + "\n    ai_cfg <键> <值>         改数值（键名见下）"
                + "\n    ai_cfg <开关> on|off     改开关"
                + "\n    ai_cfg save              立刻存一次原版存档"
                + "\n    ai_cfg fishpool          查游戏鱼表 / 无效的鱼 ID");
            CLog.Info("[键名]"
                + "\n    数值  wage free price deathprice hp atk atkdelay speed range start sleep"
                + "\n    开关  clean fight chop mine fish night");
            return;
        }

        string key = args[0].ToLowerInvariant();
        string val = args.Length > 1 ? args[1] : null;

        bool SetInt(Action<int> set, int lo, int hi, string name)
        {
            if (!int.TryParse(val, out int v))
            {
                CLog.Warn($"{name} 需要整数，例子：ai_cfg {key} 150");
                return false;
            }
            int clamped = Math.Clamp(v, lo, hi);
            set(clamped);
            CLog.Info($"{name} → {clamped}");
            return true;
        }

        bool SetFloat(Action<float> set, float lo, float hi, string name)
        {
            if (!float.TryParse(val, out float v))
            {
                CLog.Warn($"{name} 需要数字，例子：ai_cfg {key} 6.5");
                return false;
            }
            float clamped = Math.Clamp(v, lo, hi);
            set(clamped);
            CLog.Info($"{name} → {clamped:0.##}");
            return true;
        }

        bool SetBool(Action<bool> set, string name)
        {
            bool v = ParseOn(val);
            set(v);
            CLog.Info($"{name} → {(v ? "开" : "关")}");
            return true;
        }

        switch (key)
        {
            case "wage": SetInt(v => Config.日工资 = v, 0, 999999, "日工资"); break;
            case "free": SetInt(v => Config.免费打工天数 = v, 0, 365, "免费打工天数"); break;
            case "price": SetInt(v => Config.雇佣基础价格 = v, 0, 9999999, "雇佣基础价"); break;
            case "deathprice": SetInt(v => Config.每次死亡涨价 = v, 0, 9999999, "每次死亡涨价"); break;
            case "hp": SetInt(v => Config.AI血量 = v, 10, 100000, "AI血量"); break;
            case "atk": SetInt(v => Config.攻击力 = v, 1, 10000, "攻击力"); break;
            case "atkdelay": SetInt(v => Config.攻击间隔毫秒 = v, 100, 60000, "攻击间隔"); break;
            case "start": SetInt(v => Config.开始工作时间 = v, 0, 2600, "开始工作"); break;
            case "sleep": SetInt(v => Config.回家睡觉时间 = v, 600, 2600, "回家睡觉"); break;
            case "speed": SetFloat(v => Config.移动速度 = v, 0.5f, 20f, "移动速度"); break;
            case "range": SetFloat(v => Config.工作范围 = v, 1f, 60f, "工作范围"); break;
            case "clean": SetBool(v => Config.行为_清理 = v, "农场清理"); break;
            case "fight": SetBool(v => Config.行为_打怪 = v, "打怪"); break;
            case "chop": SetBool(v => Config.行为_砍树 = v, "砍树"); break;
            case "mine": SetBool(v => Config.行为_挖矿 = v, "挖矿"); break;
            case "fish": SetBool(v => Config.行为_钓鱼 = v, "钓鱼"); break;
            case "night": SetBool(v => Config.夜晚自动回家 = v, "夜晚回家"); break;
            case "fishpool":
                {
                    List<string> pool = Companion.FishPool();
                    CLog.Ok($"游戏里共有 {pool.Count} 条鱼可钓（「可钓鱼类列表」留空时就用这份表）。");
                    if (!string.IsNullOrWhiteSpace(Config.可钓鱼类列表))
                    {
                        var bad = new List<string>();
                        foreach (string raw in Config.可钓鱼类列表.Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!Companion.IsRealItemId(raw))
                                bad.Add(raw);
                        }
                        CLog.Info(bad.Count == 0
                            ? "配置里的物品 ID 全部有效。"
                            : $"配置里这些 ID 游戏里查不到、会被忽略：{string.Join(",", bad)}");
                    }
                    return;
                }
            case "save":
                if (!SaveTimeAllowed(out string saveWhy))
                {
                    CLog.Warn("没能存档：" + saveWhy);
                    return;
                }
                if (NativeSave.TrySave(out string saveReason))
                    CLog.Step("已开始写原版存档，写完会在游戏里提示「已存档」。");
                else
                    CLog.Warn("没能存档：" + saveReason);
                return;
            default:
                CLog.Warn($"未知键名：{key}。敲 ai_cfg 查看可用的键名。");
                return;
        }

        Config.Sanitize();
        _configDirty = true;
        SaveConfigNow();
        CLog.Ok("已保存到 config.json。");
    }

    private static bool ParseOn(string val)
    {
        if (string.IsNullOrEmpty(val))
            return true;
        val = val.ToLowerInvariant();
        return val == "on" || val == "1" || val == "true" || val == "开";
    }

    // =====================================================================
    // GMCM 配置菜单
    // =====================================================================

    private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
    {
        ParseTalkKey();
        RegisterGMCM();

        try
        {
            SpriteTexture = Helper.ModContent.Load<Texture2D>("assets/ai_sprite.png");
            CompanionRenderer.Init(SpriteTexture);
        }
        catch (Exception ex)
        {
            CLog.Error("加载角色贴图失败: " + ex.Message);
        }
    }

    private void RegisterGMCM()
    {
        if (_gmcmRegistered)
            return;

        var api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (api == null)
        {
            // GMCM 是可选的：没装就用内置设置菜单（对话 →「设置（调参数）」），
            // 不用在控制台提醒玩家，免得以为缺了什么东西。
            return;
        }

        try
        {
            api.Register(ModManifest,
                reset: () =>
                {
                    Config = new ModConfig();
                    Config.Sanitize();
                },
                save: () =>
                {
                    Config.Sanitize();
                    Helper.WriteConfig(Config);
                    ParseTalkKey();
                    ParseSaveKey();
                });

            RegisterGMCMMain(api);
            RegisterGMCMHire(api);
            RegisterGMCMDeath(api);
            RegisterGMCMBehavior(api);
            RegisterGMCMMove(api);
            RegisterGMCMCombat(api);
            RegisterGMCMLists(api);
            RegisterGMCMText(api);

            _gmcmRegistered = true;
            CLog.Ok("已注册 Generic Mod Config Menu 配置界面。");
        }
        catch (Exception ex)
        {
            CLog.Warn("注册 GMCM 失败: " + ex.Message);
        }
    }

    private void RegisterGMCMMain(IGenericModConfigMenuApi api)
    {
        api.AddSectionTitle(ModManifest, () => "基础信息");
        api.AddBoolOption(ModManifest,
            () => Config.启用模组, v => Config.启用模组 = v,
            () => "启用模组", () => "关闭后 AI 雇工不再出现");
        api.AddTextOption(ModManifest,
            () => Config.AI名字, v => Config.AI名字 = v,
            () => "AI 名字", () => "游戏内显示的名字");
        api.AddTextOption(ModManifest,
            () => Config.对话按键, v => Config.对话按键 = v,
            () => "对话按键", () => "靠近 AI 时按此键打开对话菜单（键名如 E、F、MouseLeft）");

        api.AddParagraph(ModManifest, () =>
            $"AI 雇工会出现在农舍门外。免费打工 {Config.免费打工天数} 天后开始按日收费。" +
            $"当前重新雇佣价格：{HirePrice()} G（已死亡 {Data?.死亡次数 ?? 0} 次）。");

        api.AddSectionTitle(ModManifest, () => "随时存档");
        api.AddBoolOption(ModManifest,
            () => Config.允许随时存档, v => Config.允许随时存档 = v,
            () => "允许随时存档", () => "关闭后快捷键失效，对话里的「立即存档」也不再显示");
        api.AddTextOption(ModManifest,
            () => Config.存档快捷键, v => Config.存档快捷键 = v,
            () => "存档快捷键", () => "键名如 F5、F9、K；填「无」表示不绑快捷键");
        api.AddBoolOption(ModManifest,
            () => Config.存档前自动备份, v => Config.存档前自动备份 = v,
            () => "存档前自动备份", () => "每次随时存档前，把当天开局存档复制到 Saves\\_AIHireling备份\\（每天一份）");
        api.AddBoolOption(ModManifest,
            () => Config.存档需到睡觉时间, v => Config.存档需到睡觉时间 = v,
            () => "只在睡觉时间能存档", () => "开启后没到「回家睡觉时间」不让存档，AI 会告诉你还差多久（模拟原版到点才能睡）");
    }

    private void RegisterGMCMHire(IGenericModConfigMenuApi api)
    {
        api.AddPage(ModManifest, "hire", () => "雇佣与工资");
        api.AddPageLink(ModManifest, "hire", () => "雇佣与工资");

        api.AddSectionTitle(ModManifest, () => "雇佣与工资");
        api.AddNumberOption(ModManifest,
            () => Config.雇佣基础价格, v => Config.雇佣基础价格 = v,
            () => "雇佣基础价格 (G)", () => "重新雇佣的基准价——首次雇佣免费；遣散或死亡后再雇才收（死亡涨价见「死亡与涨价」页）", 0, 9999999);
        api.AddNumberOption(ModManifest,
            () => Config.日工资, v => Config.日工资 = v,
            () => "日工资 (G)", () => "免费期结束后每天扣的工资", 0, 999999);
        api.AddNumberOption(ModManifest,
            () => Config.免费打工天数, v => Config.免费打工天数 = v,
            () => "免费打工天数", () => "雇佣后前几天不扣工资", 0, 365);
        api.AddBoolOption(ModManifest,
            () => Config.工资早上预付, v => Config.工资早上预付 = v,
            () => "工资早上预付", () => "开：每天起床扣当天工资；关：每天睡前结算");
        api.AddBoolOption(ModManifest,
            () => Config.欠薪自动解雇, v => Config.欠薪自动解雇 = v,
            () => "欠薪自动解雇", () => "钱不够付工资时自动解约（否则记欠薪继续干活）");
        api.AddNumberOption(ModManifest,
            () => Config.死亡后等待天数, v => Config.死亡后等待天数 = v,
            () => "死亡后等待天数", () => "AI 死亡后多少天才重新出现", 0, 30);
    }

    private void RegisterGMCMDeath(IGenericModConfigMenuApi api)
    {
        api.AddPage(ModManifest, "death", () => "死亡与涨价");
        api.AddPageLink(ModManifest, "death", () => "死亡与涨价");

        api.AddSectionTitle(ModManifest, () => "死亡与涨价");
        api.AddBoolOption(ModManifest,
            () => Config.涨价使用倍率, v => Config.涨价使用倍率 = v,
            () => "涨价使用倍率", () => "开：按倍率涨价；关：每次死亡加固定金额");
        api.AddNumberOption(ModManifest,
            () => Config.每次死亡涨价, v => Config.每次死亡涨价 = v,
            () => "每次死亡涨价 (G)", () => "固定涨价模式下，每死一次涨多少", 0, 9999999);
        api.AddNumberOption(ModManifest,
            () => Config.死亡涨价倍率, v => Config.死亡涨价倍率 = v,
            () => "死亡涨价倍率", () => "倍率模式下，每死一次价格乘以 (1+倍率)", 0, 100);
        api.AddBoolOption(ModManifest,
            () => Config.死亡掉落物品, v => Config.死亡掉落物品 = v,
            () => "死亡掉落物品", () => "AI 死亡时是否掉落矿石");
        api.AddParagraph(ModManifest, () =>
            Data == null ? "死亡次数：0"
                : $"死亡次数：{Data.死亡次数}　重新雇佣价格：{HirePrice()} G");
    }

    private void RegisterGMCMBehavior(IGenericModConfigMenuApi api)
    {
        api.AddPage(ModManifest, "behavior", () => "工作行为");
        api.AddPageLink(ModManifest, "behavior", () => "工作行为");

        api.AddSectionTitle(ModManifest, () => "工作行为开关");
        AddBool(api, () => Config.行为_跟随, v => Config.行为_跟随 = v, "跟随", "允许 AI 跟随玩家移动");
        AddBool(api, () => Config.行为_打怪, v => Config.行为_打怪 = v, "打怪", "自动攻击附近怪物");
        AddBool(api, () => Config.行为_收获, v => Config.行为_收获 = v, "收获", "收获成熟作物");
        AddBool(api, () => Config.行为_浇水, v => Config.行为_浇水 = v, "浇水", "给干渴的作物浇水");
        AddBool(api, () => Config.行为_翻地, v => Config.行为_翻地 = v, "翻地", "开垦农田");
        AddBool(api, () => Config.行为_播种, v => Config.行为_播种 = v, "播种", "消耗玩家背包里的种子播种");
        AddBool(api, () => Config.行为_采集, v => Config.行为_采集 = v, "采集", "捡起地面掉落物和机器成品");
        AddBool(api, () => Config.行为_砍树, v => Config.行为_砍树 = v, "砍树", "砍伐成年树木");
        AddBool(api, () => Config.行为_清理, v => Config.行为_清理 = v, "农场清理", "清除农场上的杂草、枯苗、树枝、石头、树桩");
        AddBool(api, () => Config.行为_挖矿, v => Config.行为_挖矿 = v, "挖矿", "在矿洞敲碎矿石容器");
        AddBool(api, () => Config.行为_钓鱼, v => Config.行为_钓鱼 = v, "钓鱼", "在水边钓鱼");
        AddBool(api, () => Config.挖矿需要镐子, v => Config.挖矿需要镐子 = v,
            "挖矿需要镐子", "关掉就是空手也能砸石头（默认开）");
        AddBool(api, () => Config.挖矿消耗体力, v => Config.挖矿消耗体力 = v,
            "挖矿消耗体力", "每敲一下扣 2 - 挖矿等级*0.1 点体力；农场清理石头也算");
        AddBool(api, () => Config.钓鱼需要鱼竿, v => Config.钓鱼需要鱼竿 = v,
            "钓鱼需要鱼竿", "关掉就是空手也能钓（默认开，和原版一样要竿）");
        AddBool(api, () => Config.钓鱼消耗鱼饵, v => Config.钓鱼消耗鱼饵 = v,
            "钓鱼消耗鱼饵", "每抛一竿扣 1 个鱼饵（先用 AI 背包里的，再用你的）；没饵仍能钓，只是咬钩慢一倍");
        AddBool(api, () => Config.钓鱼消耗体力, v => Config.钓鱼消耗体力 = v,
            "钓鱼消耗体力", "每抛一竿扣 8 - 钓鱼等级*0.1 点体力，睡一觉回满");
        api.AddNumberOption(ModManifest, () => Config.AI体力上限, v => Config.AI体力上限 = v,
            () => "AI 体力上限", () => "默认 270，和原版玩家一样", 20, 2000);

        api.AddSectionTitle(ModManifest, () => "显示效果");
        AddBool(api, () => Config.身体被作物遮挡, v => Config.身体被作物遮挡 = v,
            "身体被作物遮挡", "站进草地/作物/树后面时，身体会被挡住（和原版玩家一样）；关掉就是永远画在最上层");
        AddBool(api, () => Config.控制台彩色, v => Config.控制台彩色 = v,
            "彩色输出", "SMAPI 控制台里用颜色区分信息 / 成功 / 警告 / 错误；关掉就是纯文本");
        AddBool(api, () => Config.控制台图标, v => Config.控制台图标 = v,
            "前面加图标", "每条日志前加 ◆ ▶ √ ▲ × 这类符号；字体缺字显示成方块时关掉");

        api.AddSectionTitle(ModManifest, () => "干活受阻提示");
        api.AddNumberOption(ModManifest,
            () => Config.提示每天最多次数, v => Config.提示每天最多次数 = v,
            () => "每天最多提示几次", () => "缺鱼竿 / 缺镐子 / 没体力 这类提示，同一条一天最多说几次；0 = 完全不提示", 0, 20);
        api.AddNumberOption(ModManifest,
            () => Config.提示静默天数, v => Config.提示静默天数 = v,
            () => "之后静默几天", () => "说满次数后静默几天（0 = 只在当天不再提示，第二天恢复）", 0, 60);

        api.AddSectionTitle(ModManifest, () => "AI 背包");
        AddBool(api, () => Config.背包满了自动找玩家, v => Config.背包满了自动找玩家 = v,
            "背包满自动找玩家", "背包装满后自动跑到你面前，问是交给你还是塞进箱子");
        AddBool(api, () => Config.背包满了自动找箱子, v => Config.背包满了自动找箱子 = v,
            "背包满自动塞箱子", "背包满了、12 格内又有箱子时，自己走过去塞进去（摆好箱子就自动生效）");
        api.AddNumberOption(ModManifest,
            () => Config.背包容量组数, v => Config.背包容量组数 = v,
            () => "背包容量 (组)", () => "AI 背包装到这么多组就算满；0 = 不限", 0, 999);
    }

    private void AddBool(IGenericModConfigMenuApi api, Func<bool> get, Action<bool> set, string name, string tooltip)
    {
        api.AddBoolOption(ModManifest, get, set, () => name, () => tooltip);
    }

    private void RegisterGMCMMove(IGenericModConfigMenuApi api)
    {
        api.AddPage(ModManifest, "move", () => "作息与移动");
        api.AddPageLink(ModManifest, "move", () => "作息与移动");

        api.AddSectionTitle(ModManifest, () => "作息");
        api.AddBoolOption(ModManifest,
            () => Config.夜晚自动回家, v => Config.夜晚自动回家 = v,
            () => "夜晚自动回家", () => "到睡觉时间 AI 自动回农舍门口睡觉");
        api.AddNumberOption(ModManifest,
            () => Config.开始工作时间, v => Config.开始工作时间 = v,
            () => "开始工作时间", () => "24小时制（如 600 = 早上6点）", 0, 2600);
        api.AddNumberOption(ModManifest,
            () => Config.回家睡觉时间, v => Config.回家睡觉时间 = v,
            () => "回家睡觉时间", () => "24小时制（如 2200 = 晚上10点）", 600, 2600);

        api.AddSectionTitle(ModManifest, () => "移动");
        api.AddNumberOption(ModManifest,
            () => Config.移动速度, v => Config.移动速度 = v,
            () => "移动速度", () => "格/帧基准，越大越快", 0.5f, 20f);
        api.AddNumberOption(ModManifest,
            () => Config.跟随距离, v => Config.跟随距离 = v,
            () => "跟随距离 (格)", () => "超过这个距离 AI 开始跟随", 0.5f, 30f);
        api.AddNumberOption(ModManifest,
            () => Config.瞬移距离, v => Config.瞬移距离 = v,
            () => "瞬移距离 (格)", () => "超过这个距离 AI 直接传送到玩家身边", 2f, 100f);
        api.AddNumberOption(ModManifest,
            () => Config.工作范围, v => Config.工作范围 = v,
            () => "工作范围 (格)", () => "距离玩家超过这个范围不干活", 1f, 60f);
    }

    private void RegisterGMCMCombat(IGenericModConfigMenuApi api)
    {
        api.AddPage(ModManifest, "combat", () => "战斗");
        api.AddPageLink(ModManifest, "combat", () => "战斗");

        api.AddNumberOption(ModManifest,
            () => Config.AI血量, v => Config.AI血量 = v,
            () => "AI 血量", () => "归零后死亡（涨价并在家门口重生）", 10, 100000);
        api.AddNumberOption(ModManifest,
            () => Config.攻击力, v => Config.攻击力 = v,
            () => "攻击力", () => "每次攻击造成的伤害", 1, 10000);
        api.AddNumberOption(ModManifest,
            () => Config.索敌范围, v => Config.索敌范围 = v,
            () => "索敌范围 (格)", () => "多远内发现怪物", 1f, 40f);
        api.AddNumberOption(ModManifest,
            () => Config.攻击距离, v => Config.攻击距离 = v,
            () => "攻击距离 (格)", () => "多近才能打到怪物", 0.5f, 10f);
        api.AddNumberOption(ModManifest,
            () => Config.攻击间隔毫秒, v => Config.攻击间隔毫秒 = v,
            () => "攻击间隔 (毫秒)", () => "两次攻击的最小间隔", 100, 60000);
        api.AddNumberOption(ModManifest,
            () => Config.受伤无敌毫秒, v => Config.受伤无敌毫秒 = v,
            () => "受伤无敌 (毫秒)", () => "挨打后的无敌时间", 100, 60000);
    }

    private void RegisterGMCMLists(IGenericModConfigMenuApi api)
    {
        api.AddPage(ModManifest, "lists", () => "采集与挖矿");
        api.AddPageLink(ModManifest, "lists", () => "采集与挖矿");

        api.AddTextOption(ModManifest,
            () => Config.可钓鱼类列表, v => Config.可钓鱼类列表 = v,
            () => "可钓鱼类列表", () => "留空 = 游戏里所有鱼（推荐）；想限定就填逗号分隔的物品 ID");
        api.AddTextOption(ModManifest,
            () => Config.可挖矿石列表, v => Config.可挖矿石列表 = v,
            () => "可挖矿石列表", () => "逗号分隔的物品ID，AI 挖矿掉落从这些里随机");
        api.AddNumberOption(ModManifest,
            () => Config.钓鱼等待毫秒, v => Config.钓鱼等待毫秒 = v,
            () => "钓鱼等待 (毫秒)", () => "每次钓鱼耗时", 500, 120000);
    }

    private void RegisterGMCMText(IGenericModConfigMenuApi api)
    {
        api.AddPage(ModManifest, "text", () => "对话文本");
        api.AddPageLink(ModManifest, "text", () => "对话文本");

        api.AddParagraph(ModManifest, () =>
            "可用占位符：{name}=AI名字  {price}=当前雇佣价  {wage}=日工资  {free}=免费天数  {deaths}=死亡次数");

        api.AddTextOption(ModManifest, () => Config.文本_招呼, v => Config.文本_招呼 = v,
            () => "招呼（未雇佣时）", () => "首次见到玩家时说的话");
        api.AddTextOption(ModManifest, () => Config.文本_雇佣成功, v => Config.文本_雇佣成功 = v,
            () => "雇佣成功", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_拒绝, v => Config.文本_拒绝 = v,
            () => "拒绝雇佣", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_钱不够雇佣, v => Config.文本_钱不够雇佣 = v,
            () => "钱不够雇佣", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_工资结算, v => Config.文本_工资结算 = v,
            () => "工资结算", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_欠薪, v => Config.文本_欠薪 = v,
            () => "欠薪", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_死亡, v => Config.文本_死亡 = v,
            () => "死亡", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_重生, v => Config.文本_重生 = v,
            () => "重生", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_遣散, v => Config.文本_遣散 = v,
            () => "遣散", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_待命, v => Config.文本_待命 = v,
            () => "待命", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_菜单标题, v => Config.文本_菜单标题 = v,
            () => "菜单标题", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_自动模式, v => Config.文本_自动模式 = v,
            () => "自动模式", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_跟随模式, v => Config.文本_跟随模式 = v,
            () => "跟随模式", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_农场模式, v => Config.文本_农场模式 = v,
            () => "农场模式", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_战斗模式, v => Config.文本_战斗模式 = v,
            () => "战斗模式", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_农场清理模式, v => Config.文本_农场清理模式 = v,
            () => "农场清理模式", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_砍树模式, v => Config.文本_砍树模式 = v,
            () => "砍树模式", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_挖矿模式, v => Config.文本_挖矿模式 = v,
            () => "挖矿模式", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_钓鱼模式, v => Config.文本_钓鱼模式 = v,
            () => "钓鱼模式", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_背包满, v => Config.文本_背包满 = v,
            () => "背包满了", () => "可用 {groups}=当前组数  {cap}=容量");
        api.AddTextOption(ModManifest, () => Config.文本_没鱼竿, v => Config.文本_没鱼竿 = v,
            () => "没鱼竿", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_没镐子, v => Config.文本_没镐子 = v,
            () => "没镐子", () => null);
        api.AddTextOption(ModManifest, () => Config.文本_没体力, v => Config.文本_没体力 = v,
            () => "没体力", () => "可用 {energy}=当前体力  {max}=体力上限");
        api.AddTextOption(ModManifest, () => Config.文本_存档未到点, v => Config.文本_存档未到点 = v,
            () => "存档没到时间", () => "可用 {now}=当前时间  {sleep}=睡觉时间  {left}=还差多久  {leftmin}=还差多少分钟");
    }

    // =====================================================================
    // 生命周期
    // =====================================================================

    private void ParseTalkKey()
    {
        if (!Enum.TryParse(Config.对话按键 ?? "E", true, out SButton key))
            key = SButton.E;
        _talkKey = key;
    }

    /// <summary>解析随时存档快捷键；「无」/空 表示不绑快捷键（仍可从对话菜单存档）。</summary>
    private void ParseSaveKey()
    {
        string raw = (Config.存档快捷键 ?? "").Trim();
        if (raw.Length == 0 || raw == "无" || raw.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            _saveKey = SButton.None;
            return;
        }
        _saveKey = Enum.TryParse(raw, true, out SButton key) ? key : SButton.F5;
    }

    private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
    {
        Data = Helper.Data.ReadSaveData<SaveData>("aihireling") ?? new SaveData();
        Config = Helper.ReadConfig<ModConfig>();
        Config.Sanitize();
        ParseTalkKey();
        ParseSaveKey();
        NativeSave.OnSaveLoaded();
        Dialogue.Close();

        if (!Config.启用模组)
        {
            CLog.Warn("AI雇工已在配置中禁用（config.json 的「启用模组」= false）。");
            Companion = null;
            return;
        }

        SpawnCompanionAtHome();
        PurgeBrokenItems();
        if (IsHired)
            Game1.addHUDMessage(new HUDMessage($"{Config.AI名字} 已在门口待命，按 {Config.对话按键} 对话。"));
        else if (Companion != null)
            Game1.addHUDMessage(new HUDMessage(
                $"{Config.AI名字} 正在你家门外！按 {Config.对话按键} 与他对话（免费打工 {Config.免费打工天数} 天，之后每天 {Config.日工资}G）。"));
    }

    /// <summary>
    /// 清掉背包里的「错误物品」：物品 ID 在 Data/Objects 里查不到的占位物品。
    /// 2.1.0 及以前的钓鱼会随机出这类东西（典型是「错误物品（754）」），
    /// 捡不起、卖不掉、也放不进箱子，只能清掉。判定条件很窄——纯数字 ID 且表里没有，
    /// 模组自己注册的非数字物品一律不碰。
    /// </summary>
    private void PurgeBrokenItems()
    {
        int n = 0;
        try
        {
            if (Game1.player?.Items != null)
            {
                for (int i = 0; i < Game1.player.Items.Count; i++)
                {
                    Item slot = Game1.player.Items[i];
                    if (slot != null && Companion.IsBrokenItem(slot))
                    {
                        Game1.player.Items[i] = null;
                        n++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CLog.Trace("清理玩家背包错误物品失败: " + ex.Message);
        }

        if (Companion != null)
            n += Companion.PurgeBrokenItems();

        if (n > 0)
        {
            CLog.Ok($"清理了 {n} 个「错误物品」（物品 ID 不存在，旧版钓鱼留下的）。");
            Game1.addHUDMessage(new HUDMessage($"清理了 {n} 个错误物品（旧版钓鱼 bug 留下的）"));
        }
    }

    private void SpawnCompanionAtHome()
    {
        Vector2 home;
        GameLocation farm = Game1.getFarm() ?? Game1.currentLocation;
        try
        {
            // 优先：农场地图上农舍门的 Warp 点（门下一格 = 门外）
            Point door = Game1.getFarm().getWarpPointTo("FarmHouse", null);
            if (door.X == 0 && door.Y == 0)
                throw new Exception("door not found");
            home = new Vector2(door.X, door.Y + 1);
        }
        catch
        {
            try
            {
                // 回退：从 FarmHouse 里找回农场的 warp
                GameLocation farmHouse = Game1.getLocationFromName("FarmHouse");
                bool found = false;
                home = new Vector2(64f, 15f);
                if (farmHouse != null)
                {
                    foreach (Warp w in farmHouse.warps)
                    {
                        if (w.TargetName == "Farm")
                        {
                            home = new Vector2(w.TargetX, w.TargetY + 1);
                            found = true;
                            break;
                        }
                    }
                }
                if (!found)
                    CLog.Warn("未找到农舍门，使用默认位置。");
            }
            catch
            {
                home = new Vector2(64f, 15f);
            }
        }

        Companion = new Companion
        {
            HomeTile = home,
            Location = farm,
            Position = home * 64f,
            Health = Config.AI血量,
            Active = true,
            Dead = false,
            Mode = AiMode.自动,
            Sleeping = false,
        };

        // 读档：把上次没领走的东西装回 AI 背包
        LoadCompanionInventory();

        // 体力也存档：早上存的是满的，白天存到一半读回来就还是一半
        Companion.Energy = Data != null && Data.体力 > 0
            ? Math.Min(Data.体力, Companion.MaxEnergy)
            : Companion.MaxEnergy;
    }

    private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
    {
        Dialogue.Close();
        NativeSave.OnReturnedToTitle();

        // 背包菜单还开着就回标题：把临时箱子丢掉。
        // 不必回写物品——存档里的「背包物品」快照还是打开菜单前的状态。
        _companionBagOpen = false;
        _companionBag = null;

        Companion = null;
        Data = new SaveData();
    }

    private void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        if (!Config.启用模组 || Companion == null)
            return;

        int today = SaveData.AbsoluteDay();
        if (Data.上次处理日 == today)
            return;
        Data.上次处理日 = today;

        if (Companion.Dead)
        {
            // 死亡后等待 N 天重生
            if (Companion.DeadDay >= 0 && today >= Companion.DeadDay + Config.死亡后等待天数 + 1)
            {
                Companion.Dead = false;
                Companion.Active = true;
                Companion.Health = Companion.MaxHealth;
                Companion.Location = Game1.getFarm() ?? Game1.currentLocation;
                Companion.Position = Companion.HomeTile * 64f;
                Companion.Mode = AiMode.自动;
                Companion.Sleeping = false;
                Companion.Energy = Companion.MaxEnergy;
                Companion.Job = new Job();
                Companion.ResetFishing();
                Game1.addHUDMessage(new HUDMessage(
                    $"{Config.AI名字} 修好了自己，出现在家门口！重新雇佣价格：{HirePrice()} G"));
                // 早上刚起床时开对话框容易和日程/节日事件打架，改成飘字
                try { Game1.addHUDMessage(new HUDMessage(Format(Config.文本_重生))); } catch { }
            }
        }
        else if (IsHired)
        {
            Companion.Energy = Companion.MaxEnergy;   // 睡了一觉，体力回满
            Data.累计打工天数++;
            Data.本次雇佣天数++;
            SaveDataToDisk();
            if (Config.工资早上预付)
                ChargeDailyWage();
        }
    }

    private void OnDayEnding(object sender, DayEndingEventArgs e)
    {
        if (Config.启用模组 && Companion != null && !Companion.Dead && IsHired && !Config.工资早上预付)
            ChargeDailyWage();
    }

    private void ChargeDailyWage()
    {
        int wage = Config.日工资;
        if (wage <= 0 || Data.本次雇佣天数 <= Config.免费打工天数)
            return;

        if (Game1.player.Money >= wage)
        {
            Game1.player.Money -= wage;
            Data.欠薪 = 0;
            Game1.addHUDMessage(new HUDMessage($"支付 {Config.AI名字} 今日工资 {wage} G"));
            Game1.playSound("coin");
            // DayEnding（玩家刚点上床）时弹对话框有卡住存档流程的风险，改飘一行字
            try { Game1.addHUDMessage(new HUDMessage(Format(Config.文本_工资结算))); } catch { }
        }
        else if (Config.欠薪自动解雇)
        {
            Data.已雇佣 = false;
            Data.本次雇佣天数 = 0;
            if (Companion != null)
            {
                Companion.Mode = AiMode.待命;
                Companion.Job = new Job();
            }
            Game1.addHUDMessage(new HUDMessage($"钱不够付工资，{Config.AI名字} 解约回门口歇着了。"));
            Dialogue.OpenLine(Format(Config.文本_欠薪));
        }
        else
        {
            Data.欠薪 += wage;
            Game1.addHUDMessage(new HUDMessage($"欠薪 {Data.欠薪} G，{Config.AI名字} 在赊账干活……"));
        }
        SaveDataToDisk();
    }

    private void OnWarped(object sender, WarpedEventArgs e)
    {
        if (!Config.启用模组 || Companion == null || !Companion.Active || Companion.Dead)
            return;

        // 进屋（农舍/联机木屋）：AI 不跟进屋，传送回农舍门外待命
        if (e.NewLocation != null &&
            (e.NewLocation.Name == "FarmHouse" || e.NewLocation.Name?.StartsWith("Cabin") == true))
        {
            Companion.Location = Game1.getFarm() ?? e.NewLocation;
            Companion.Position = Companion.HomeTile * 64f;
            Companion.Job = new Job();
            Companion.ResetFishing();
            Companion.Sleeping = false;
            Dialogue.Close(); // 防止对话菜单在换图后卡住
            return;
        }

        if (!IsHired || Companion.Mode == AiMode.待命)
        {
            // 被遣散的 AI 之前若被落在别的图（比如在矿洞解雇），玩家回农场时
            // 把他一起带回农舍门口，否则他会被永久丢在那张图里。
            if (!IsHired && e.NewLocation is Farm)
            {
                Companion.Location = e.NewLocation;
                Companion.Position = Companion.HomeTile * 64f;
                Companion.Job = new Job();
                Companion.ResetFishing();
            }
            return;
        }

        // 玩家换地图时，AI 传送到玩家身边（否则会被落在旧地图）
        Companion.Location = e.NewLocation;
        Companion.Position = e.Player.Position + new Vector2(64f, 32f);
        Companion.Job = new Job();
        Companion.ResetFishing();
        Dialogue.Close(); // 防止对话菜单在换图后卡住
    }

    private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        // 随时存档：先推一把原版存档协程（存档期间世界被冻结，其它逻辑一律让路）
        NativeSave.Tick();
        if (NativeSave.IsSaving)
            return;

        // 兜底：每帧校正对话与游戏菜单的挂载状态，确保菜单永远不会
        // 卡在 Game1.activeClickableMenu 上锁死玩家移动（WASD 失灵）
        Dialogue.SyncMenuState();

        // 设置页改完、对话关掉后统一落盘，避免左右键连按触发大量写盘
        if (_configDirty && Config != null && !Dialogue.IsOpen)
            SaveConfigNow();

        // AI 背包菜单：关掉后把东西收回 AI 背包（菜单每次抓取都会重建，不能只靠 exitFunction）
        if (_companionBagOpen && Game1.activeClickableMenu is not ItemGrabMenu)
            CloseCompanionInventory();

        if (Config.启用模组 && Companion != null && !_companionBagOpen)
        {
            double now = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0.0;
            double dt = now - _lastMs;
            if (_lastMs <= 0.0 || dt < 0.0 || dt > 200.0)
                dt = 16.7;
            _lastMs = now;
            Companion.Update(dt);

            // AI 躲到树后面时，让树像"玩家在后面"那样虚化（原版只认玩家）
            CompanionRenderer.FadeCoveringTrees(Companion);
        }
    }

    private void OnRenderedWorld(object sender, RenderedWorldEventArgs e)
    {
        if (!Config.启用模组 || Companion == null || !Companion.Active)
            return;

        // 身体正常情况下已经在 drawFarmers 的补丁里画过了（这样才会被草/作物挡住）；
        // 只有两种情况需要在这里补画：补丁没挂上，或者玩家关掉了遮挡。
        if (!DrawHook.Applied || Config.身体被作物遮挡 != true)
            CompanionRenderer.DrawBody(e.SpriteBatch, Companion);

        // 头顶 UI 永远画在最上层，免得名字和血条被草叶糊掉
        CompanionRenderer.DrawOverlay(e.SpriteBatch, Companion);
    }

    private void OnRendered(object sender, RenderedEventArgs e)
    {
        // 第二个协程驱动点：存档期间 Game1.Update 会提前 return，
        // 但绘制循环一定在跑（存档画面就是绘制出来的），这里再推一次就不会卡死
        NativeSave.Tick();

        if (Config.启用模组)
            Dialogue.Draw(e.SpriteBatch);
    }

    private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
    {
        // 存档画面期间不接受任何操作（世界已冻结，这时候开菜单/对话只会错位）
        if (NativeSave.IsSaving)
            return;

        // 随时存档快捷键：跟 AI 在不在场无关，所以放在最前面判
        if (Config.启用模组 && Config.允许随时存档 && _saveKey != SButton.None
            && e.Button == _saveKey && Context.IsWorldReady && !Dialogue.IsOpen)
        {
            SaveFromHotkey();
            return;
        }

        if (!Config.启用模组 || Companion == null || !Companion.Active)
            return;

        if (Dialogue.IsOpen)
        {
            Dialogue.HandleButton(e.Button);
            return;
        }

        if (e.Button == _talkKey && Game1.activeClickableMenu == null && !Companion.Dead
            && Game1.player != null
            && Companion.Location == Game1.currentLocation   // 必须在同一张地图才能对话
            && IsPlayerNear(2.5f))
        {
            OpenTalkMenu();
        }
    }

    public bool IsPlayerNear(float tiles)
    {
        if (Companion == null || Game1.player == null)
            return false;
        return Vector2.Distance(Companion.Position, Game1.player.Position) / 64f <= tiles;
    }

    // =====================================================================
    // 随时存档（原版存档）
    // =====================================================================

    /// <summary>
    /// 存档时间限制（可选）：没到「回家睡觉时间」就不让随时存档。
    /// 打开后存档就等同于"到点睡觉存盘"，避免白天乱存导致读档后村民日程错位。
    /// 没到点时返回 false，并把「还差多久」的台词塞进 reason。
    /// </summary>
    private bool SaveTimeAllowed(out string reason)
    {
        reason = null;
        if (!Config.存档需到睡觉时间)
            return true;

        int now = Game1.timeOfDay;
        int sleep = Config.回家睡觉时间;
        if (now >= sleep)
            return true;

        int left = MinutesOfDay(sleep) - MinutesOfDay(now);
        if (left <= 0)
            left += 24 * 60;

        reason = Format(Config.文本_存档未到点)
            .Replace("{now}", TimeStr(now))
            .Replace("{sleep}", TimeStr(sleep))
            .Replace("{left}", DurationStr(left))
            .Replace("{leftmin}", left.ToString());
        return false;
    }

    /// <summary>把 2400 制的时间（如 2230）换算成"当天第几分钟"。</summary>
    private static int MinutesOfDay(int t)
    {
        int h = t / 100;
        int m = t % 100;
        return h * 60 + m;
    }

    /// <summary>布尔值在控制台里显示成「开 / 关」。</summary>
    private static string OnOff(bool v) => v ? "开" : "关";

    /// <summary>分钟数写成「3 小时 20 分钟」。</summary>
    private static string DurationStr(int minutes)
    {
        if (minutes <= 0)
            return "0 分钟";
        if (minutes < 60)
            return minutes + " 分钟";
        int h = minutes / 60;
        int m = minutes % 60;
        return m == 0 ? h + " 小时" : h + " 小时 " + m + " 分钟";
    }

    /// <summary>快捷键触发：失败时直接用 HUD 说原因，不弹对话。</summary>
    private void SaveFromHotkey()
    {
        if (!SaveTimeAllowed(out string why))
        {
            Game1.addHUDMessage(new HUDMessage(why));
            return;
        }

        if (NativeSave.TrySave(out string reason))
            return;
        Game1.addHUDMessage(new HUDMessage(reason));
    }

    /// <summary>对话里点「立即存档」：先收掉对话框（存盘要求当前没有菜单），失败再回到对话里说明。</summary>
    private void SaveFromDialogue()
    {
        Dialogue.Close();
        if (!SaveTimeAllowed(out string why))
        {
            Dialogue.OpenLine(why);
            return;
        }

        if (NativeSave.TrySave(out string reason))
            return;
        Dialogue.OpenLine(reason);
    }

    // =====================================================================
    // 对话菜单
    // =====================================================================

    private void OpenTalkMenu()
    {
        if (!IsHired)
        {
            // 本存档第一次雇佣免费；死亡/遣散后的重新雇佣才收雇佣费
            bool firstHire = !Data.曾经受雇 && !Companion.Dead;
            string hireOption = firstHire
                ? $"雇佣（免费，前 {Config.免费打工天数} 天不收工资）"
                : $"雇佣（{HirePrice()} G）";
            // 首次雇佣时把招呼里的 {price} 显示为 0，避免"要收500G"和"免费"自相矛盾
            string greeting = firstHire
                ? Format(Config.文本_招呼).Replace("{price}", "0")
                : Format(Companion.Dead ? Config.文本_重生 : Config.文本_招呼);

            // 未雇佣时也可能存着上次的收货，给个「先领取」出口，免得东西取不出来
            var hireOptions = new List<string> { hireOption };
            // 还没雇也可以先塞点东西给他（鱼竿、镐子、鱼饵）
            int idxHireBag = hireOptions.Count;
            hireOptions.Add("打开他的背包（给东西 / 取东西）");
            int idxBag = -1;
            int bagGroups = Companion?.InventoryGroups ?? 0;
            if (bagGroups > 0)
            {
                idxBag = hireOptions.Count;
                hireOptions.Add($"先领取物品（{bagGroups} 组）");
            }
            int idxHireSettings = hireOptions.Count;
            hireOptions.Add("设置（调参数）");
            if (Config.允许随时存档)
                hireOptions.Add("立即存档（原版存档）");
            hireOptions.Add("算了");

            int idxHireSave = Config.允许随时存档 ? idxHireSettings + 1 : -1;

            Dialogue.Open(new[]
            {
                new DialoguePage
                {
                    Text = greeting,
                    Options = hireOptions,
                    OnChoose = i =>
                    {
                        if (i == 0) TryHire();
                        else if (i == idxHireBag) OpenCompanionInventory();
                        else if (idxBag >= 0 && i == idxBag) ShowInventoryMenu();
                        else if (i == idxHireSettings) ShowSettingsMenu();
                        else if (idxHireSave >= 0 && i == idxHireSave) SaveFromDialogue();
                        else Dialogue.OpenLine(Format(Config.文本_拒绝));
                    }
                }
            });
            return;
        }

        // 主菜单选项按背包内容动态生成：有东西才显示「领取物品」
        var options = new List<string> { "询问状态", "切换工作模式" };
        int idxOpenBag = options.Count;
        options.Add("打开他的背包（给东西 / 取东西）");
        int idxItems = -1;
        int groups = Companion?.InventoryGroups ?? 0;
        if (groups > 0)
        {
            idxItems = options.Count;
            options.Add($"领取物品（{groups} 组）");
        }
        int idxSettings = options.Count;
        options.Add("设置（调参数）");
        int idxSave = -1;
        if (Config.允许随时存档)
        {
            idxSave = options.Count;
            options.Add("立即存档（原版存档）");
        }
        int idxDismiss = options.Count;
        options.Add("遣散");
        options.Add("再见");

        Dialogue.Open(new[]
        {
            new DialoguePage
            {
                Text = Format(Config.文本_菜单标题),
                Options = options,
                OnChoose = i =>
                {
                    if (i == 0) ShowStatus();
                    else if (i == 1) ShowModeMenu();
                    else if (i == idxOpenBag) OpenCompanionInventory();
                    else if (idxItems >= 0 && i == idxItems) ShowInventoryMenu();
                    else if (i == idxSettings) ShowSettingsMenu();
                    else if (idxSave >= 0 && i == idxSave) SaveFromDialogue();
                    else if (i == idxDismiss) Dismiss();
                    else Dialogue.Close();
                }
            }
        });
    }

    private void TryHire()
    {
        // 本存档第一次雇佣免费（不收雇佣费）；遣散/死亡后的重新雇佣才收费
        bool firstHire = !Data.曾经受雇;
        int price = firstHire ? 0 : HirePrice();

        if (price > 0 && Game1.player.Money < price)
        {
            Dialogue.OpenLine(Format(Config.文本_钱不够雇佣));
            return;
        }

        if (price > 0)
            Game1.player.Money -= price;
        Data.已雇佣 = true;
        Data.曾经受雇 = true;
        Data.本次雇佣天数 = 0;
        if (Companion != null)
        {
            Companion.Dead = false;
            Companion.Mode = AiMode.自动;
            Companion.Health = Companion.MaxHealth;
            Companion.Energy = Companion.MaxEnergy;   // 新上工，体力从头开始算
        }
        if (price > 0)
            Game1.playSound("coin");
        SaveDataToDisk();
        Dialogue.OpenLine(firstHire
            ? Format("成交！首次雇佣不收钱。接下来{free}天我免费干活，之后每天{wage}G。开始工作吧！")
            : Format(Config.文本_雇佣成功));
    }

    private void Dismiss()
    {
        Data.已雇佣 = false;
        Data.本次雇佣天数 = 0;
        if (Companion != null)
        {
            Companion.Mode = AiMode.待命;
            Companion.Job = new Job();
        }
        SaveDataToDisk();
        try
        {
            Game1.addHUDMessage(new HUDMessage($"{Config.AI名字} 会自己走回农舍门口待命。"));
        }
        catch { }
        Dialogue.OpenLine(Format(Config.文本_遣散));
    }

    private void ShowStatus()
    {
        Companion c = Companion;
        string wageInfo;
        if (Data.本次雇佣天数 > Config.免费打工天数)
            wageInfo = Data.欠薪 > 0 ? $"欠薪 {Data.欠薪} G" : $"日工资 {Config.日工资} G";
        else
            wageInfo = $"免费期内（还剩 {Math.Max(0, Config.免费打工天数 - Data.本次雇佣天数)} 天）";

        string bag = c.InventoryGroups > 0
            ? $"背包 {c.InventoryGroups} 组（对话里可免费领取）"
            : "背包是空的";
        string text =
            $"状态报告：\n" +
            $"· 血量 {c.Health}/{c.MaxHealth}\n" +
            $"· 工具：鱼竿{(c.FindFishingRod() != null ? "有" : "无")}，镐子{(c.FindPickaxe() != null ? "有" : "无")}" +
            $"（放你背包里就算有，他借来用）\n" +
            $"· 体力 {(int)c.Energy}/{c.MaxEnergy}"
            + (Config.钓鱼消耗体力 ? "（钓鱼每竿约扣 " + c.CastEnergyCost().ToString("0.#") + "）" : "") + "\n" +
            $"· 当前模式：{c.Mode}\n" +
            $"· {bag}\n" +
            $"· 死亡次数：{Data.死亡次数}（重新雇佣价 {HirePrice()} G）\n" +
            $"· 累计打工：{Data.累计打工天数} 天\n" +
            $"· 工资：{wageInfo}";
        Dialogue.OpenLine(text);
    }

    /// <summary>
    /// 工作模式菜单。改成"表驱动"：加减模式只要动这个数组，
    /// 不用再手数 case 0/1/2…（以前加一个模式得把后面的下标全改一遍，很容易错位）。
    /// </summary>
    private void ShowModeMenu()
    {
        (AiMode Mode, string Label, string Hint)[] modes =
        {
            (AiMode.自动,     "自动",     "什么都干"),
            (AiMode.跟随,     "跟随",     "只跟着你"),
            (AiMode.农场,     "农场",     "只种地"),
            (AiMode.战斗,     "战斗",     "打附近的怪"),
            (AiMode.农场清理, "农场清理", "除草/捡树枝石头"),
            (AiMode.砍树,     "砍树",     "只砍树，木头归你"),
            (AiMode.挖矿,     "挖矿",     "敲矿"),
            (AiMode.钓鱼,     "钓鱼",     "水边钓鱼"),
            (AiMode.待命,     "待命",     "站着不动"),
        };

        var options = new List<string>();
        foreach (var m in modes)
            options.Add($"{m.Label}（{m.Hint}）");
        options.Add("返回");

        Dialogue.Open(new[]
        {
            new DialoguePage
            {
                Text = "选择工作模式：",
                Options = options,
                OnChoose = i =>
                {
                    if (i < 0 || i >= modes.Length)
                    {
                        OpenTalkMenu();
                        return;
                    }
                    AiMode mode = modes[i].Mode;
                    SetMode(mode, ModeText(mode));
                }
            }
        });
    }

    /// <summary>切模式时 AI 说的那句话（可在 config.json 里改）。</summary>
    private static string ModeText(AiMode mode)
    {
        return mode switch
        {
            AiMode.自动 => Config.文本_自动模式,
            AiMode.跟随 => Config.文本_跟随模式,
            AiMode.农场 => Config.文本_农场模式,
            AiMode.战斗 => Config.文本_战斗模式,
            AiMode.农场清理 => Config.文本_农场清理模式,
            AiMode.砍树 => Config.文本_砍树模式,
            AiMode.挖矿 => Config.文本_挖矿模式,
            AiMode.钓鱼 => Config.文本_钓鱼模式,
            _ => Config.文本_待命,
        };
    }

    // ---------------------------------------------------------------------
    // 内置设置菜单（不依赖 GMCM，GMCM 装不上/版本太旧时也能调参数）
    // ---------------------------------------------------------------------

    /// <summary>设置页的一行：动态文本 + 左右键调整 + 回车操作。</summary>
    private class SettingRow
    {
        public Func<string> Text;
        public Action<int> Adjust;
        public Action Choose;
    }

    private bool _configDirty;

    /// <summary>
    /// 别的类就地纠正了配置（例如把「可钓鱼类列表」里不存在的物品 ID 剔掉）时调用，
    /// 交给 OnUpdateTicked 在对话关闭后统一落盘，避免边走边写文件。
    /// </summary>
    public static void RequestConfigSave()
    {
        if (Instance != null)
            Instance._configDirty = true;
    }

    private void SaveConfigNow()
    {
        try
        {
            Config.Sanitize();
            Helper.WriteConfig(Config);
            ParseTalkKey();
            ParseSaveKey();
            _configDirty = false;
        }
        catch (Exception ex)
        {
            CLog.Warn("保存配置失败: " + ex.Message);
        }
    }

    private static string TimeStr(int t)
    {
        int h = t / 100;
        int m = t % 100;
        return $"{h}:{m:00}";
    }

    private SettingRow BoolRow(string label, Func<bool> get, Action<bool> set)
    {
        return new SettingRow
        {
            Text = () => $"{label}：【{(get() ? "开" : "关")}】",
            Adjust = _ => set(!get()),
            Choose = () => set(!get()),
        };
    }

    private SettingRow IntRow(string label, Func<int> get, Action<int> set, int step, int lo, int hi, string suffix = "")
    {
        Action<int> adjust = d => set(Math.Clamp(get() + d * step, lo, hi));
        return new SettingRow
        {
            Text = () => $"{label}：【{get()}{suffix}】",
            Adjust = adjust,
            // 回车/鼠标点一下也能加一步——方向键在某些键位设置下可能被游戏吃掉
            Choose = () => adjust(1),
        };
    }

    private SettingRow FloatRow(string label, Func<float> get, Action<float> set, float step, float lo, float hi, string suffix = "")
    {
        Action<int> adjust = d => set(Math.Clamp(get() + d * step, lo, hi));
        return new SettingRow
        {
            Text = () => $"{label}：【{get():0.##}{suffix}】",
            Adjust = adjust,
            Choose = () => adjust(1),
        };
    }

    private void ShowSettingsMenu()
    {
        // 表驱动：加一页只改这个数组，不用再改 case 下标
        (string Label, Func<List<SettingRow>> Rows)[] pages =
        {
            ("工资与雇佣", HireRows),
            ("工作行为开关", BehaviorRows),
            ("花费与体力", CostRows),
            ("提示与背包", HintBagRows),
            ("战斗与移动", CombatRows),
            ("作息时间", ScheduleRows),
            ("存档与快捷键", SaveRows),
            ("显示效果", ConsoleRows),
        };

        var options = new List<string>();
        foreach (var page in pages)
            options.Add(page.Label);
        int idxReset = options.Count;
        options.Add("恢复全部默认值");
        options.Add("返回");

        Dialogue.Open(new[]
        {
            new DialoguePage
            {
                Text = "参数设置：左右键调数值，回车切换开关，关掉对话即自动保存。",
                Options = options,
                OnChoose = i =>
                {
                    if (i >= 0 && i < pages.Length)
                    {
                        ShowSettingsPage(pages[i].Label + "（左右键调，回车切换）", pages[i].Rows());
                        return;
                    }
                    if (i == idxReset)
                    {
                        ConfirmResetConfig();
                        return;
                    }
                    OpenTalkMenu();
                }
            }
        });
    }

    private void ShowSettingsPage(string title, List<SettingRow> rows, int cursor = 0)
    {
        var options = new List<string>();
        foreach (SettingRow row in rows)
            options.Add(row.Text());
        options.Add("返回设置");

        Dialogue.Open(new[]
        {
            new DialoguePage
            {
                Text = title,
                Options = options,
                OptionText = j => j < rows.Count ? rows[j].Text() : "返回设置",
                OnAdjust = (j, dir) =>
                {
                    if (j < rows.Count && rows[j].Adjust != null)
                    {
                        rows[j].Adjust(dir);
                        _configDirty = true;
                    }
                },
                // 原地操作：改值/翻开关不重建页面（OptionText 每帧读实时值），
                // 避免"关掉菜单再打开"造成画面闪一下
                OnActivate = j =>
                {
                    if (j < 0 || j >= rows.Count)
                    {
                        ShowSettingsMenu();
                        return;
                    }
                    if (rows[j].Choose != null)
                    {
                        rows[j].Choose();
                        _configDirty = true;
                    }
                },
                // 只有 Esc 取消时才走这里：退回设置目录
                OnChoose = j =>
                {
                    if (j < 0)
                        ShowSettingsMenu();
                }
            }
        }, cursor);
    }

    private List<SettingRow> HireRows()
    {
        return new List<SettingRow>
        {
            IntRow("日工资", () => Config.日工资, v => Config.日工资 = v, 10, 0, 999999, " G"),
            IntRow("免费打工天数", () => Config.免费打工天数, v => Config.免费打工天数 = v, 1, 0, 365, " 天"),
            IntRow("重新雇佣基础价", () => Config.雇佣基础价格, v => Config.雇佣基础价格 = v, 50, 0, 9999999, " G"),
            IntRow("每次死亡涨价", () => Config.每次死亡涨价, v => Config.每次死亡涨价 = v, 50, 0, 9999999, " G"),
            IntRow("死亡后重生等待", () => Config.死亡后等待天数, v => Config.死亡后等待天数 = v, 1, 0, 30, " 天"),
            BoolRow("工资早上预付", () => Config.工资早上预付, v => Config.工资早上预付 = v),
            BoolRow("欠薪自动停工", () => Config.欠薪自动解雇, v => Config.欠薪自动解雇 = v),
        };
    }

    /// <summary>干什么活（10 个工作开关 + 钓鱼）。一页最多 11 行——选项再多，
    /// 对话框会高到顶出屏幕（每行 26px，21 行就要 680px，1080p 以下直接溢出）。</summary>
    private List<SettingRow> BehaviorRows()
    {
        return new List<SettingRow>
        {
            BoolRow("跟随玩家", () => Config.行为_跟随, v => Config.行为_跟随 = v),
            BoolRow("主动打怪", () => Config.行为_打怪, v => Config.行为_打怪 = v),
            BoolRow("收获作物", () => Config.行为_收获, v => Config.行为_收获 = v),
            BoolRow("浇水", () => Config.行为_浇水, v => Config.行为_浇水 = v),
            BoolRow("翻地", () => Config.行为_翻地, v => Config.行为_翻地 = v),
            BoolRow("播种", () => Config.行为_播种, v => Config.行为_播种 = v),
            BoolRow("采集掉落物", () => Config.行为_采集, v => Config.行为_采集 = v),
            BoolRow("砍树", () => Config.行为_砍树, v => Config.行为_砍树 = v),
            BoolRow("农场清理", () => Config.行为_清理, v => Config.行为_清理 = v),
            BoolRow("挖矿", () => Config.行为_挖矿, v => Config.行为_挖矿 = v),
            BoolRow("钓鱼", () => Config.行为_钓鱼, v => Config.行为_钓鱼 = v),
        };
    }

    /// <summary>挖矿 / 钓鱼要花的东西（工具、鱼饵、体力）。</summary>
    private List<SettingRow> CostRows()
    {
        return new List<SettingRow>
        {
            BoolRow("挖矿要镐子", () => Config.挖矿需要镐子, v => Config.挖矿需要镐子 = v),
            BoolRow("挖矿耗体力", () => Config.挖矿消耗体力, v => Config.挖矿消耗体力 = v),
            BoolRow("钓鱼要鱼竿", () => Config.钓鱼需要鱼竿, v => Config.钓鱼需要鱼竿 = v),
            BoolRow("钓鱼耗鱼饵", () => Config.钓鱼消耗鱼饵, v => Config.钓鱼消耗鱼饵 = v),
            BoolRow("钓鱼耗体力", () => Config.钓鱼消耗体力, v => Config.钓鱼消耗体力 = v),
            IntRow("体力上限", () => Config.AI体力上限, v => Config.AI体力上限 = v, 10, 20, 2000),
        };
    }

    /// <summary>显示效果：控制台外观 + 身体遮挡。</summary>
    private List<SettingRow> ConsoleRows()
    {
        return new List<SettingRow>
        {
            BoolRow("彩色输出", () => Config.控制台彩色, v => Config.控制台彩色 = v),
            BoolRow("前面加图标", () => Config.控制台图标, v => Config.控制台图标 = v),
            BoolRow("身体被作物遮挡", () => Config.身体被作物遮挡, v => Config.身体被作物遮挡 = v),
        };
    }

    /// <summary>提示频率与 AI 背包容量。</summary>
    private List<SettingRow> HintBagRows()
    {
        return new List<SettingRow>
        {
            IntRow("提示每天最多", () => Config.提示每天最多次数, v => Config.提示每天最多次数 = v, 1, 0, 20, " 次"),
            IntRow("之后静默", () => Config.提示静默天数, v => Config.提示静默天数 = v, 1, 0, 60, " 天"),
            BoolRow("背包满自动找你", () => Config.背包满了自动找玩家, v => Config.背包满了自动找玩家 = v),
            BoolRow("背包满自动塞箱子", () => Config.背包满了自动找箱子, v => Config.背包满了自动找箱子 = v),
            IntRow("背包容量", () => Config.背包容量组数, v => Config.背包容量组数 = v, 1, 0, 999, " 组"),
        };
    }

    private List<SettingRow> CombatRows()
    {
        return new List<SettingRow>
        {
            IntRow("AI 血量", () => Config.AI血量, v => Config.AI血量 = v, 10, 10, 100000),
            IntRow("攻击力", () => Config.攻击力, v => Config.攻击力 = v, 1, 1, 10000),
            FloatRow("移动速度", () => Config.移动速度, v => Config.移动速度 = v, 0.5f, 0.5f, 20f),
            FloatRow("工作范围", () => Config.工作范围, v => Config.工作范围 = v, 1f, 1f, 60f, " 格"),
            IntRow("攻击间隔", () => Config.攻击间隔毫秒, v => Config.攻击间隔毫秒 = v, 50, 100, 60000, " 毫秒"),
            BoolRow("死亡掉落矿石", () => Config.死亡掉落物品, v => Config.死亡掉落物品 = v),
        };
    }

    private List<SettingRow> ScheduleRows()
    {
        return new List<SettingRow>
        {
            new SettingRow
            {
                Text = () => $"开始工作：【{TimeStr(Config.开始工作时间)}】",
                Adjust = d => Config.开始工作时间 = Math.Clamp(Config.开始工作时间 + d * 100, 0, 2600),
                Choose = () => Config.开始工作时间 = Math.Clamp(Config.开始工作时间 + 100, 0, 2600),
            },
            new SettingRow
            {
                Text = () => $"回家睡觉：【{TimeStr(Config.回家睡觉时间)}】",
                Adjust = d => Config.回家睡觉时间 = Math.Clamp(Config.回家睡觉时间 + d * 100, 600, 2600),
                Choose = () => Config.回家睡觉时间 = Math.Clamp(Config.回家睡觉时间 + 100, 600, 2600),
            },
            BoolRow("夜晚自动回家", () => Config.夜晚自动回家, v => Config.夜晚自动回家 = v),
        };
    }

    /// <summary>存档设置页：开关 + 快捷键循环切换 + 立刻存一次。</summary>
    private List<SettingRow> SaveRows()
    {
        return new List<SettingRow>
        {
            BoolRow("允许随时存档", () => Config.允许随时存档, v => Config.允许随时存档 = v),
            BoolRow("只在睡觉时间能存", () => Config.存档需到睡觉时间, v => Config.存档需到睡觉时间 = v),
            SaveKeyRow(),
            BoolRow("存档前自动备份", () => Config.存档前自动备份, v => Config.存档前自动备份 = v),
            new SettingRow
            {
                Text = () => "现在存一次档（原版存档）",
                Choose = SaveFromDialogue,
            },
        };
    }

    private SettingRow SaveKeyRow()
    {
        string[] keys = { "F5", "F6", "F7", "F8", "F9", "K", "无" };
        Action<int> cycle = d =>
        {
            int idx = Array.IndexOf(keys, (Config.存档快捷键 ?? "").Trim());
            if (idx < 0)
                idx = 0;
            Config.存档快捷键 = keys[(idx + d + keys.Length) % keys.Length];
            ParseSaveKey();
        };
        return new SettingRow
        {
            Text = () => $"存档快捷键：【{Config.存档快捷键}】",
            Adjust = cycle,
            Choose = () => cycle(1),
        };
    }

    private void ConfirmResetConfig()
    {
        Dialogue.Open(new[]
        {
            new DialoguePage
            {
                Text = "恢复全部默认参数？当前所有设置都会被覆盖。",
                Options = new List<string> { "确认恢复", "取消" },
                OnChoose = i =>
                {
                    if (i == 0)
                    {
                        Config = new ModConfig();
                        Config.Sanitize();
                        SaveConfigNow();
                        if (Companion != null && Companion.Health > Companion.MaxHealth)
                            Companion.Health = Companion.MaxHealth;
                        Dialogue.OpenLine("已恢复默认参数，界面上的数值已同步。");
                    }
                    else
                    {
                        ShowSettingsMenu();
                    }
                }
            }
        });
    }

    private void SetMode(AiMode mode, string line)
    {
        if (Companion != null)
        {
            Companion.Mode = mode;
            Companion.Job = new Job();
            Companion.ResetFishing();
        }
        Dialogue.OpenLine(Format(line));
    }

    // ---------------------------------------------------------------------
    // AI 背包：查看 + 免费领取
    // ---------------------------------------------------------------------

    private void ShowInventoryMenu()
    {
        Companion c = Companion;
        if (c == null || c.InventoryGroups == 0)
        {
            Dialogue.OpenLine("我身上现在没东西，等我清出点石头树枝再来找我吧。");
            return;
        }

        var items = new List<Item>();
        var options = new List<string>();
        int total = 0;
        foreach (Item item in c.Inventory)
        {
            if (item == null || item.Stack <= 0 || item is Tool)
                continue;
            items.Add(item);
            options.Add($"{item.DisplayName} × {item.Stack}");
            total += item.Stack;
        }
        if (items.Count == 0)
        {
            Dialogue.OpenLine("我身上现在没东西，等我清出点石头树枝再来找我吧。");
            return;
        }

        string text = $"我身上有 {total} 个（{items.Count} 组），全免费。点哪一行就拿哪个。";
        int idxTakeAll = items.Count;
        options.Add("全部拿走（免费）");
        options.Add("返回");

        Dialogue.Open(new[]
        {
            new DialoguePage
            {
                Text = text,
                Options = options,
                OnChoose = i =>
                {
                    if (i == idxTakeAll)
                    {
                        HandOverItems();
                        return;
                    }
                    if (i < 0 || i >= items.Count)
                    {
                        OpenTalkMenu();
                        return;
                    }

                    // 点某一行 = 只拿这一组（以前点了只会说"要哪个说全部拿走"，等于没功能）
                    Item item = items[i];
                    if (item == null || item.Stack <= 0 || !c.Inventory.Contains(item))
                    {
                        ShowInventoryMenu();
                        return;
                    }

                    int stack = item.Stack;
                    Item leftover = null;
                    try { leftover = Game1.player.addItemToInventory(item); }
                    catch { }
                    c.Inventory.Remove(item);
                    int given = stack - (leftover?.Stack ?? 0);
                    if (leftover != null)
                        c.Inventory.Add(leftover);
                    SaveDataToDisk();
                    if (given > 0)
                        Game1.playSound("coin");
                    Dialogue.OpenLine(given > 0
                        ? $"给，{item.DisplayName} × {given}。"
                        : "你背包塞不下了，先把东西腾一腾再来找我。");
                }
            }
        });
    }

    // =====================================================================
    // 打开 AI 的背包（给东西 / 取东西）
    // =====================================================================
    /// <summary>当前打开的 AI 背包（临时箱子）。关掉菜单时把里面的东西收回 AI 背包。</summary>
    private Chest _companionBag;
    private bool _companionBagOpen;

    /// <summary>AI 背包菜单是否开着（开着的时候冻结 AI，免得它边翻包边干活）。</summary>
    public bool CompanionBagOpen => _companionBagOpen;

    /// <summary>
    /// 把 AI 背包当成一个箱子打开：你可以往里塞鱼竿、鱼饵、镐子，
    /// 也能把它捡到的东西直接拿走。关掉菜单时统一写回 AI 背包并存档。
    /// </summary>
    public void OpenCompanionInventory()
    {
        Companion c = Companion;
        if (c == null)
            return;
        if (c.Dead)
        {
            Dialogue.OpenLine("我还没修好呢，等我爬起来再翻包吧。");
            return;
        }

        // 箱子上限 36 格：装不进去的留在 AI 身上。
        // 以前是 new Chest(items, ...) 一次性塞——超过 36 组时多出来的会被静默丢掉。
        var snapshot = c.Inventory.Where(i => i != null && i.Stack > 0).ToList();
        c.Inventory.Clear();
        var bag = new Chest(new List<Item>(), c.Tile);
        int overflow = 0;
        foreach (Item item in snapshot)
        {
            Item leftover = bag.addItem(item);
            if (leftover != null)
            {
                c.Inventory.Add(leftover);
                overflow++;
            }
        }

        _companionBag = bag;
        _companionBagOpen = true;

        if (overflow > 0)
        {
            try
            {
                Game1.addHUDMessage(new HUDMessage(
                    $"{Config.AI名字} 身上东西太多，这箱子只放得下 36 格，其余 {overflow} 组还在他身上。"));
            }
            catch { }
        }

        Dialogue.Close();
        try
        {
            _companionBag.ShowMenu();
            Game1.playSound("openChest");
        }
        catch (Exception ex)
        {
            CLog.Warn("打开 AI 背包失败: " + ex.Message);
        }

        // 菜单没挂上（例如正在过场）→ 立刻收回，别让东西凭空消失
        if (Game1.activeClickableMenu is not ItemGrabMenu)
            CloseCompanionInventory();
    }

    /// <summary>菜单关掉了 → 把箱子里的东西收回去，存一次档。</summary>
    private void CloseCompanionInventory()
    {
        if (!_companionBagOpen)
            return;
        _companionBagOpen = false;

        Companion c = Companion;
        Chest chest = _companionBag;
        _companionBag = null;
        if (c == null || chest == null)
            return;

        c.Inventory.Clear();
        foreach (Item item in chest.Items)
        {
            if (item == null || item.Stack <= 0)
                continue;
            // 工具（鱼竿/镐子/斧头…）不进 AI 背包：他本来就是"借玩家的用"，
            // 真塞进去反而会在存档/领取时弄丢或掉级，直接还给玩家。
            if (item is Tool)
            {
                // 工具还给玩家；玩家背包也满了就丢在 AI 脚边，绝不静默丢弃
                Item back = null;
                try { back = Game1.player?.addItemToInventory(item); } catch { }
                if (back != null)
                {
                    try { Game1.currentLocation?.debris.Add(new Debris(back, c.Position)); }
                    catch { }
                }
                continue;
            }
            c.Inventory.Add(item);
        }
        chest.Items.Clear();

        SaveDataToDisk();
    }

    /// <summary>把 AI 背包里的东西全部免费交还玩家；背包满则掉在脚边。</summary>
    private void HandOverItems()
    {
        Companion c = Companion;
        if (c == null || c.InventoryGroups == 0)
        {
            Dialogue.OpenLine("我身上现在没东西。");
            return;
        }

        int given = 0;
        bool dropped = false;
        foreach (Item item in c.Inventory)
        {
            if (item == null || item.Stack <= 0 || item is Tool)
                continue;
            int before = item.Stack;
            try
            {
                Item leftover = Game1.player.addItemToInventory(item);
                given += before - (leftover?.Stack ?? 0);
                if (leftover != null)
                {
                    dropped = true;
                    try { Game1.currentLocation?.debris.Add(new Debris(leftover, c.Position)); }
                    catch { }
                }
            }
            catch
            {
                dropped = true;
                try { Game1.currentLocation?.debris.Add(new Debris(item, c.Position)); }
                catch { }
            }
        }
        c.Inventory.Clear();
        SaveDataToDisk();   // 同步存档，避免读档后物品又冒出来

        if (given > 0)
            Game1.playSound("coin");
        Dialogue.OpenLine(dropped
            ? $"给，{given} 个拿好！你背包塞不下的那些我搁你脚边了。以后清出来的东西都先放我身上。"
            : $"给，{given} 个都拿好，一分钱不要。以后清出来的东西我先收着，你随时来找我拿。");
    }

    /// <summary>
    /// AI 背包满了、已经跑到玩家面前：说那句话，并给出「都给你 / 塞箱子 / 先背着」三个选择。
    /// 文本可在 config.json 的「文本_背包满」里改，{groups} = 当前组数，{cap} = 容量。
    /// </summary>
    public void AlertBagFull()
    {
        Companion c = Companion;
        if (c == null || c.InventoryGroups == 0)
            return;

        int groups = c.InventoryGroups;
        int cap = c.BagCapacity;
        string text = Format(Config.文本_背包满)
            .Replace("{groups}", groups.ToString())
            .Replace("{cap}", cap > 0 ? cap.ToString() : "∞");
        if (string.IsNullOrWhiteSpace(text))
            text = "我背包满了，这些是给你还是塞进箱子？";

        var options = new List<string>
        {
            "都给你（塞你背包）",
            "你塞进箱子",
            "先背着，等会儿再说",
        };

        Dialogue.Open(new[]
        {
            new DialoguePage
            {
                Text = text,
                Options = options,
                OnChoose = i =>
                {
                    if (i == 0)
                    {
                        HandOverItems();
                    }
                    else if (i == 1)
                    {
                        StoreBagToChest();
                    }
                    else if (i == 2)
                    {
                        c.PostponeBagAlert(2 * 60 * 60 * 1000.0);   // 2 小时后再提醒（和说明一致）
                        Dialogue.OpenLine("行，那我先背着。你要拿的时候随时找我。");
                    }
                }
            }
        });
    }

    /// <summary>把 AI 背包的东西塞进附近最近的箱子（12 格内）；塞不下的掉在脚边。</summary>
    private void StoreBagToChest()
    {
        Companion c = Companion;
        if (c == null)
            return;
        if (c.InventoryGroups == 0)
        {
            Dialogue.OpenLine("我身上已经空了，没东西可放。");
            return;
        }

        Chest chest = FindNearestChest(c.Position, 12f * 64f);
        if (chest == null)
        {
            Dialogue.OpenLine("这附近我没看见箱子……要不你先拿着？或者放个箱子在这儿，我下次自己塞。");
            return;
        }

        int moved = 0;
        int leftOver = 0;
        foreach (Item item in c.Inventory.ToList())
        {
            if (item == null || item.Stack <= 0 || item is Tool)
                continue;
            int before = item.Stack;
            try
            {
                Item rest = chest.addItem(item);
                int restStack = rest?.Stack ?? 0;
                moved += before - restStack;
                if (restStack > 0)
                {
                    leftOver += restStack;
                    try { Game1.currentLocation?.debris.Add(new Debris(rest, c.Position)); }
                    catch { }
                }
            }
            catch
            {
                leftOver += before;
            }
        }
        c.Inventory.Clear();
        SaveDataToDisk();

        if (moved > 0)
            Game1.playSound("coin");

        Dialogue.OpenLine(leftOver > 0
            ? $"塞进箱子 {moved} 个，箱子也满了，剩下 {leftOver} 个我搁你脚边了。"
            : $"都塞进那个箱子了，一共 {moved} 个，背包清爽了！");
    }

    /// <summary>当前地图里离 from 最近的箱子（maxDist 像素内没有就返回 null）。</summary>
    private Chest FindNearestChest(Vector2 from, float maxDist)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return null;

        Chest best = null;
        float bestDist = maxDist;
        foreach (var pair in loc.objects.Pairs)
        {
            if (pair.Value is not Chest chest)
                continue;
            float d = Vector2.Distance(from, pair.Key * 64f + new Vector2(32f, 32f));
            if (d < bestDist)
            {
                bestDist = d;
                best = chest;
            }
        }
        return best;
    }

    // =====================================================================
    // 工具
    // =====================================================================

    public int HirePrice()
    {
        int price = Config.涨价使用倍率
            ? (int)Math.Round(Config.雇佣基础价格 * (1f + Config.死亡涨价倍率 * (Data?.死亡次数 ?? 0)))
            : Config.雇佣基础价格 + Config.每次死亡涨价 * (Data?.死亡次数 ?? 0);
        return Math.Max(0, price);
    }

    public void SaveDataToDisk()
    {
        try
        {
            SyncInventoryToData();
            if (Companion != null)
                Data.体力 = (int)Math.Round(Math.Max(0f, Companion.Energy));
            Helper.Data.WriteSaveData("aihireling", Data);
        }
        catch (Exception ex)
        {
            CLog.Warn("保存AI数据失败: " + ex.Message);
        }
    }

    /// <summary>把 AI 背包的物品镜像到存档数据（只存 ID + 数量）。</summary>
    private void SyncInventoryToData()
    {
        if (Data == null)
            return;
        Data.背包物品 = new List<StoredItem>();
        if (Companion == null || Companion.Inventory.Count == 0)
            return;
        foreach (Item item in Companion.Inventory)
        {
            if (item == null || item.Stack <= 0)
                continue;
            Data.背包物品.Add(new StoredItem { ItemId = item.QualifiedItemId, Stack = item.Stack });
        }
    }

    /// <summary>读档/重生后把存档里的物品重建回 AI 背包。</summary>
    private void LoadCompanionInventory()
    {
        if (Companion == null || Data?.背包物品 == null || Data.背包物品.Count == 0)
            return;
        foreach (StoredItem si in Data.背包物品)
        {
            if (string.IsNullOrEmpty(si.ItemId) || si.Stack <= 0)
                continue;
            try
            {
                Item item = ItemRegistry.Create(si.ItemId, si.Stack, 0, true);
                if (item != null && !Companion.IsBrokenItem(item))
                    Companion.Inventory.Add(item);
                else
                    CLog.Trace($"读档时丢弃了失效的物品 ID {si.ItemId}（游戏里查不到）");
            }
            catch (Exception ex)
            {
                CLog.Warn($"恢复AI背包物品失败({si.ItemId}): {ex.Message}");
            }
        }
    }

    public string Format(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        return text
            .Replace("{name}", Config.AI名字)
            .Replace("{price}", HirePrice().ToString())
            .Replace("{wage}", Config.日工资.ToString())
            .Replace("{free}", Config.免费打工天数.ToString())
            .Replace("{deaths}", (Data?.死亡次数 ?? 0).ToString());
    }
}
