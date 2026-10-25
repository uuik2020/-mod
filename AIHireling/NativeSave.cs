using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace AIHireling;

/// <summary>
/// 「随时存档」：不自己造存档格式，而是把原版 <see cref="SaveGame.Save"/> 协程原样跑一遍，
/// 写出的就是和睡床上床完全一样的原版存档（SaveGame / SaveSerializer / _old 备份轮转全都照旧）。
///
/// 原版只在夜里跑这段协程，所以这里补上它周围的舞台效果：
/// 1. 置 <see cref="Game1.IsSaving"/> —— 原版就是靠这个标志冻结世界、把屏幕切到存档画面；
/// 2. 挂一个空白菜单画「正在存档」，顺便吃掉玩家输入（此时世界已经不再 update）；
/// 3. 每帧推一次协程，跑完把标志和菜单撤掉。
///
/// 注意：协程必须靠外部每帧推动 —— 世界被冻结时 <c>Game1.Update</c> 会直接 return，
/// 因此驱动点同时挂在 UpdateTicked 与 Rendered 上（渲染总在跑），任何一边活着都不会卡死。
/// </summary>
internal static class NativeSave
{
    /// <summary>原版存档协程（SaveGame.Save）。</summary>
    private static IEnumerator<int> _routine;

    private static SaveProgressMenu _menu;
    private static readonly Stopwatch Watch = new Stopwatch();

    /// <summary>本游戏日是否已经做过「当天开局存档」备份。</summary>
    private static int _lastBackupDay = int.MinValue;

    public static bool IsSaving { get; private set; }

    /// <summary>超时兜底：协程万一推进不下去，别让玩家永远卡在存档画面。</summary>
    private const int TimeoutMs = 60000;

    public static void OnSaveLoaded()
    {
        _lastBackupDay = int.MinValue;
        IsSaving = false;
        _routine = null;
        Watch.Reset();
    }

    public static void OnReturnedToTitle()
    {
        IsSaving = false;
        _routine = null;
        Watch.Reset();
        _menu = null;
    }

    /// <summary>
    /// 试一次随时存档。成功返回 true（存档在后台继续写，写完后弹 HUD 提示）；
    /// 失败返回 false，并通过 <paramref name="reason"/> 给出给玩家看的原因。
    /// </summary>
    public static bool TrySave(out string reason)
    {
        reason = null;

        if (IsSaving)
        {
            reason = "上一份存档还在写，稍等一下。";
            return false;
        }

        if (!Context.IsWorldReady || Game1.player == null)
        {
            reason = "还没进存档，没法存档。";
            return false;
        }

        if (Game1.IsMultiplayer)
        {
            // 原版的存档协程在联机下需要和房客做 ready 同步，单人流程跑不通（会卡在存档界面），
            // 《Save Anywhere》也是同样的限制，所以这里直接挡掉而不是冒险卡死。
            reason = "多人游戏里会卡在存档界面，随时存档只在单人模式可用。";
            return false;
        }

        if (Game1.gameMode != 3 || Game1.eventUp || Game1.fadeToBlack || Game1.IsFading())
        {
            reason = "剧情/过场里不能存档，等这一段过完再说。";
            return false;
        }

        // 节日场景里有临时物件（帐篷等），原版存档收尾会把它们清掉，节日中途存档会看着"东西凭空消失"
        if (Game1.isFestival())
        {
            reason = "节日过程中不能存档，节日结束以后随时可以存。";
            return false;
        }

        if (Game1.activeClickableMenu != null)
        {
            reason = "先把当前界面关掉再存档。";
            return false;
        }

        // AI 的状态（雇佣天数/欠薪/背包）先同步进 ModData，
        // 这样它才会跟着这次原生存档一起写进存档文件
        try { ModEntry.Instance?.SaveDataToDisk(); }
        catch (Exception ex) { CLog.Warn("[随时存档] 同步AI数据失败: " + ex.Message); }

        BackupSaveFolder();

        try
        {
            Game1.game1.IsSaving = true;   // 原版存档标志：冻结世界 + 切到存档画面
            _routine = SaveGame.Save();    // 原版存档协程
            IsSaving = true;
            Watch.Restart();
            AttachMenu();
            CLog.Info("[随时存档] 开始写入原版存档……");
            return true;
        }
        catch (Exception ex)
        {
            CLog.Error("[随时存档] 启动存档失败: " + ex);
            DetachMenu();
            try { Game1.game1.IsSaving = false; } catch { }
            IsSaving = false;
            _routine = null;
            reason = "存档启动失败，详情见 SMAPI 控制台。";
            return false;
        }
    }

    /// <summary>每帧推进存档协程（UpdateTicked 与 Rendered 都会调，重复调用无害）。</summary>
    public static void Tick()
    {
        if (!IsSaving || _routine == null)
            return;

        // 存档画面期间游戏不再 update 世界，但菜单本身可能被外部摘掉；
        // 摘掉了就重新挂上，免得玩家对着纯色背景乱按键
        if (!ReferenceEquals(Game1.activeClickableMenu, _menu))
            AttachMenu();

        bool finished;
        try
        {
            finished = !_routine.MoveNext() || _routine.Current >= 100;
        }
        catch (Exception ex)
        {
            Finish(false, ex);
            return;
        }

        if (finished)
        {
            Finish(true, null);
            return;
        }

        if (Watch.ElapsedMilliseconds > TimeoutMs)
            Finish(false, new Exception($"存档超过 {TimeoutMs / 1000} 秒没有完成，已强制退出存档画面"));
    }

    private static void Finish(bool ok, Exception ex)
    {
        IsSaving = false;
        _routine = null;
        Watch.Reset();
        DetachMenu();
        try { Game1.game1.IsSaving = false; } catch { }

        if (ok)
        {
            CLog.Info("[随时存档] 存档写入完成。");
            Game1.addHUDMessage(new HUDMessage("已存档（原版存档）"));
        }
        else
        {
            CLog.Error("[随时存档] 存档失败: " + ex);
            Game1.addHUDMessage(new HUDMessage("存档失败，详情见 SMAPI 控制台"));
        }
    }

    private static void AttachMenu()
    {
        if (Game1.activeClickableMenu == null)
        {
            _menu = new SaveProgressMenu();
            Game1.activeClickableMenu = _menu;
        }
    }

    private static void DetachMenu()
    {
        if (_menu != null && ReferenceEquals(Game1.activeClickableMenu, _menu))
            Game1.activeClickableMenu = null;
        _menu = null;
    }

    /// <summary>
    /// 存档前把「当天开始时的存档」复制一份到 Saves\_AIHireling备份\。
    /// 原版只在夜里写盘，随时存档写的是半天的状态；万一这份存档读回来不对，
    /// 这份备份就是当天起床时的干净存档（每天只备份一次）。
    /// </summary>
    private static void BackupSaveFolder()
    {
        if (ModEntry.Config == null || !ModEntry.Config.存档前自动备份)
            return;

        int today = SaveData.AbsoluteDay();
        if (_lastBackupDay == today)
            return;

        try
        {
            string saveName = Game1.GetSaveGameName(false);
            if (string.IsNullOrEmpty(saveName))
                return;

            string folder = SaveGame.FilterFileName(saveName) + "_" + Game1.uniqueIDForThisGame;
            string src = Path.Combine(Program.GetSavesFolder(), folder);
            if (!Directory.Exists(src))
                return;

            string dst = Path.Combine(Program.GetSavesFolder(), "_AIHireling备份", folder);
            Directory.CreateDirectory(dst);
            foreach (string file in new[] { "SaveGameInfo", folder })
            {
                string from = Path.Combine(src, file);
                if (File.Exists(from))
                    File.Copy(from, Path.Combine(dst, file), true);
            }

            _lastBackupDay = today;
            CLog.Info($"[随时存档] 已把当天开局存档备份到 Saves\\_AIHireling备份\\{folder}");
        }
        catch (Exception ex)
        {
            // 备份失败不影响存档本身，只提示一句
            CLog.Warn("[随时存档] 备份存档失败（不影响本次存档）: " + ex.Message);
        }
    }
}

/// <summary>存档期间挂在屏幕上的空白菜单：吃输入 + 画「正在存档」。</summary>
internal class SaveProgressMenu : IClickableMenu
{
    private static Texture2D _pixel;
    private int _frames;

    private static Texture2D Pixel
    {
        get
        {
            if (_pixel == null || _pixel.IsDisposed)
            {
                _pixel = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
                _pixel.SetData(new[] { Color.White });
            }
            return _pixel;
        }
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds) { }

    // 存档过程中不接受任何输入（Esc 也不给退，退了会留下一个冻结的世界）
    public override void receiveKeyPress(Keys key) { }

    public override void receiveLeftClick(int x, int y, bool playSound = true) { }

    public override void receiveRightClick(int x, int y, bool playSound = true) { }

    public override void update(GameTime time) { }

    public override void draw(SpriteBatch b)
    {
        _frames++;
        Viewport vp = Game1.graphics.GraphicsDevice.Viewport;

        // 原版存档时屏幕已被清成纯色，这里只压一层浅暗底，突出提示文字
        b.Draw(Pixel, new Rectangle(0, 0, vp.Width, vp.Height), null, new Color(0, 0, 0, 90));

        // 省略号动画：0.6 秒一个点
        int dots = (_frames / 36) % 4;
        string text = "正在存档（原版存档）" + new string('.', dots);
        Vector2 size = Game1.dialogueFont.MeasureString(text);
        Vector2 pos = new Vector2((vp.Width - size.X) / 2f, vp.Height - 90f);
        b.DrawString(Game1.dialogueFont, text, pos + new Vector2(2f, 2f), new Color(0, 0, 0, 200));
        b.DrawString(Game1.dialogueFont, text, pos, Color.White);

        string hint = "写完会提示「已存档」；写盘瞬间可能卡顿一两秒，属正常";
        Vector2 hintSize = Game1.smallFont.MeasureString(hint);
        b.DrawString(Game1.smallFont, hint,
            new Vector2((vp.Width - hintSize.X) / 2f, vp.Height - 48f), new Color(220, 220, 220, 220));
    }
}
