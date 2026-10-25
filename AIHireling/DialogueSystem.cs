using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace AIHireling;

/// <summary>一页对话：文本 + 可选选项列表。</summary>
public class DialoguePage
{
    public string Text = "";
    public List<string> Options = new List<string>();
    public Action<int> OnChoose;

    /// <summary>动态行文本（如设置页要实时显示当前值）。返回 null 表示用 Options 里的静态文本。</summary>
    public Func<int, string> OptionText;

    /// <summary>左右键回调：参数为 -1（减）/ +1（加）。设置页用它调数值。</summary>
    public Action<int, int> OnAdjust;

    /// <summary>「原地操作」回调：设了它，回车/鼠标点击只调用这里，不翻页也不关闭对话
    /// （设置页翻开关/加步长用它，避免每次点选都关掉菜单再重开导致画面闪）。</summary>
    public Action<int> OnActivate;
}

/// <summary>
/// 自绘对话 UI（星露谷物语风格的底部对话框）。不依赖游戏 NPC Dialogue 数据，
/// 直接挂在 Game1.activeClickableMenu 上阻挡输入，由 ModEntry 转发按键。
/// </summary>
public class DialogueSystem
{
    private readonly List<DialoguePage> _pages = new List<DialoguePage>();
    private int _index;
    private int _option;
    private BlankMenu _menu;
    private bool _closeRequested;

    /// <summary>每次 Open 递增，用来判断回调里是否已经重开了新对话。</summary>
    private int _openEpoch;

    // 选项区的屏幕范围（Draw 时更新，供鼠标点击命中判断）
    private Rectangle _optionArea;
    private Rectangle _boxRect;
    private int _optionRowH = 26;

    private static Texture2D _pixel;

    public bool IsOpen { get; private set; }

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

    public void Open(IEnumerable<DialoguePage> pages, int startOption = 0)
    {
        _pages.Clear();
        _pages.AddRange(pages.Where(p => p != null && !string.IsNullOrEmpty(p.Text)));
        if (_pages.Count == 0)
        {
            Close();
            return;
        }

        // 如果当前挂着别的菜单（不是我们的空白菜单），不抢占：
        // 直接放弃本次对话，避免两个菜单错位后永远摘不掉、锁死玩家移动
        if (Game1.activeClickableMenu != null && !ReferenceEquals(Game1.activeClickableMenu, _menu))
        {
            CLog.Trace("检测到其他菜单已打开，本次AI对话取消。");
            IsOpen = false;
            _pages.Clear();
            return;
        }

        _index = 0;
        _option = Math.Max(0, startOption);
        _openEpoch++;
        IsOpen = true;
        AttachMenu();
    }

    public void OpenLine(string text)
    {
        Open(new[] { new DialoguePage { Text = text ?? "" } });
    }

    public void Close()
    {
        IsOpen = false;
        _pages.Clear();
        _closeRequested = false;
        DetachMenu();
    }

    /// <summary>请求关闭对话（供右键等场景调用，实际的摘除在下一帧 SyncMenuState 里做，避免重入）。</summary>
    public void RequestClose()
    {
        if (IsOpen)
            _closeRequested = true;
    }

    /// <summary>
    /// 每帧校正对话与游戏菜单的挂载状态。这是防止 WASD 被锁死的关键兜底：
    /// 无论游戏因为什么原因（ESC、传送、读档、异常）关掉了我们的空白菜单，
    /// 或者我们的对话已经结束但菜单还挂着，这里都会把状态掰正。
    /// </summary>
    public void SyncMenuState()
    {
        if (_closeRequested)
        {
            Close();
            return;
        }

        if (IsOpen)
        {
            // 对话开着，但菜单被游戏侧关掉/顶掉 → 同步关闭对话
            if (!ReferenceEquals(Game1.activeClickableMenu, _menu))
            {
                CLog.Trace("对话菜单被外部关闭，同步结束对话。");
                IsOpen = false;
                _pages.Clear();
                _menu = null;
            }
        }
        else if (_menu != null)
        {
            // 对话已结束但菜单还挂着 → 立刻摘除，防止锁死玩家移动
            DetachMenu();
        }
    }

    private void AttachMenu()
    {
        if (Game1.activeClickableMenu == null)
        {
            _menu = new BlankMenu();
            Game1.activeClickableMenu = _menu;
        }
    }

    private void DetachMenu()
    {
        if (_menu != null && ReferenceEquals(Game1.activeClickableMenu, _menu))
            Game1.activeClickableMenu = null;
        _menu = null;
    }

    public void HandleButton(SButton button)
    {
        if (!IsOpen || _pages.Count == 0)
            return;

        DialoguePage page = _pages[_index];

        if (page.Options.Count > 0)
        {
            // 选项页：方向键（上下左右）/WASD 移动光标，E/回车 确认，Esc/N 取消
            int count = page.Options.Count;
            bool goLeft = button == SButton.Left || button == SButton.DPadLeft || button == SButton.A;
            bool goRight = button == SButton.Right || button == SButton.DPadRight || button == SButton.D;

            // 设置页：左右键（或 A/D）改数值/开关，不移动光标
            if (page.OnAdjust != null && (goLeft || goRight))
            {
                try { page.OnAdjust(_option, goRight ? 1 : -1); }
                catch (Exception ex) { CLog.Warn($"对话调整回调异常: {ex}"); }
                return;
            }

            if (button == SButton.Down || button == SButton.DPadDown || button == SButton.S)
                _option = (_option + 1) % count;
            else if (button == SButton.Up || button == SButton.DPadUp || button == SButton.W)
                _option = (_option - 1 + count) % count;
            else if (button == SButton.E || button == SButton.Enter || button == SButton.Space
                     || button == SButton.ControllerA)
                Choose(_option);
            else if (button == SButton.Y && count > 0)
                Choose(0);
            else if (button == SButton.N || button == SButton.Escape)
                Cancel();
        }
        else
        {
            // 纯文本页：E/回车 继续，Esc 关闭
            if (button == SButton.E || button == SButton.Enter || button == SButton.Space
                || button == SButton.ControllerA)
                Next();
            else if (button == SButton.Escape)
                Close();
        }
    }

    /// <summary>鼠标左键：点哪一行就操作哪一行（数值行=加一步，菜单行=选它）。</summary>
    public void HandleClick(int x, int y)
    {
        if (!IsOpen || _pages.Count == 0)
            return;

        DialoguePage page = _pages[_index];
        if (page.Options.Count == 0)
        {
            Next();
            return;
        }

        // 精确命中：直接算点到了第几行
        if (_optionArea.Contains(x, y))
        {
            int idx = (y - _optionArea.Y) / _optionRowH;
            if (idx >= 0 && idx < page.Options.Count)
            {
                _option = idx;
                Choose(idx);
                return;
            }
        }

        // 兜底：不同 UI 缩放/分辨率下坐标空间可能对不上，
        // 只要点在我们这个对话框里，就当作"确认当前选中项"
        if (_boxRect.Contains(x, y))
            Choose(_option);
    }

    private void Next()
    {
        _option = 0;
        if (_index + 1 < _pages.Count)
            _index++;
        else
            Close();
    }

    private void Choose(int i)
    {
        DialoguePage page = _pages[_index];

        // 设置页那种「原地操作」的页面：不翻页也不关闭，只调值，
        // 否则每按一下都要 Close + Open，中间空一帧 → 画面闪一下
        if (page.OnActivate != null)
        {
            try { page.OnActivate(i); }
            catch (Exception ex) { CLog.Warn($"对话操作回调异常: {ex}"); }
            return;
        }

        // 回调里通常会自己 Open 新页。以前的写法是先 Next()（会 Close）再回调，
        // 导致每次点选都要"拆菜单→建菜单"，屏幕上就会闪。现在先跑回调，
        // 只要回调没重开对话（版本号没变且还开着），才按原语义翻页。
        int epoch = _openEpoch;
        try { page.OnChoose?.Invoke(i); }
        catch (Exception ex)
        {
            CLog.Warn($"对话回调异常: {ex}");
        }

        if (_openEpoch == epoch && IsOpen)
            Next();
    }

    private void Cancel()
    {
        DialoguePage page = _pages[_index];
        Next();
        try { page.OnChoose?.Invoke(-1); }
        catch (Exception ex)
        {
            CLog.Warn($"对话回调异常: {ex}");
        }
    }

    public void Draw(SpriteBatch b)
    {
        if (!IsOpen || _pages.Count == 0)
            return;

        DialoguePage page = _pages[_index];
        Viewport vp = Game1.graphics.GraphicsDevice.Viewport;
        int width = Math.Min(vp.Width - 60, 920);
        // 选项多时（如 8 项的模式菜单）自动增高对话框，防止选项画出框外
        int optionsH = page.Options.Count > 0 ? page.Options.Count * 26 + 26 : 0;
        int height = Math.Max(230, 110 + optionsH);
        Rectangle box = new Rectangle((vp.Width - width) / 2, vp.Height - height - 30, width, height);
        _boxRect = box;

        b.Draw(Pixel, box, null, new Color(24, 20, 15, 240));
        DrawBorder(b, box, new Color(226, 186, 110), 3);

        // 左侧头像框
        Rectangle portrait = new Rectangle(box.X + 14, box.Y + 16, 76, 100);
        b.Draw(Pixel, portrait, null, new Color(52, 44, 32));
        DrawBorder(b, portrait, new Color(160, 130, 80), 2);
        if (ModEntry.SpriteTexture != null)
        {
            b.Draw(ModEntry.SpriteTexture, portrait.Location.ToVector2() + new Vector2(6f, 2f),
                new Rectangle(0, 0, 16, 24), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
        }

        b.DrawString(Game1.dialogueFont, ModEntry.Config.AI名字,
            new Vector2(box.X + 104, box.Y + 12), Color.White);

        // 选项区需要的高度：每项 26px + 底部留白
        Rectangle textArea = new Rectangle(box.X + 104, box.Y + 50, width - 130,
            Math.Max(24, height - 70 - optionsH));
        List<string> lines = WrapText(page.Text ?? "", Game1.smallFont, textArea.Width);
        float lineHeight = Game1.smallFont.LineSpacing + 2;
        for (int i = 0; i < lines.Count; i++)
        {
            float y = textArea.Y + i * lineHeight;
            if (y + lineHeight > textArea.Bottom)
                break;
            b.DrawString(Game1.smallFont, lines[i], new Vector2(textArea.X + 1, y + 1), new Color(0, 0, 0, 180));
            b.DrawString(Game1.smallFont, lines[i], new Vector2(textArea.X, y), Color.White);
        }

        if (page.Options.Count > 0)
        {
            float optY = box.Y + height - 24 - page.Options.Count * 26;
            // 记录选项区，鼠标点击时用得到
            _optionRowH = 26;
            _optionArea = new Rectangle(textArea.X, (int)optY - 3, width - 130,
                page.Options.Count * _optionRowH + 4);
            for (int j = 0; j < page.Options.Count; j++)
            {
                bool selected = j == _option;
                if (selected)
                {
                    // 选中行背景高亮条（配合黄色文字，任何屏幕上都能看清光标在哪）
                    b.Draw(Pixel, new Rectangle(textArea.X + 2, (int)optY + j * 26 - 3,
                        width - 140, 24), null, new Color(90, 74, 36, 170));
                }
                Color color = selected ? new Color(255, 230, 120) : new Color(220, 220, 220);
                // 注意：中文字体没有 ▶ 字形，用全角书名号代替
                string prefix = selected ? "》" : "　";
                string label = page.OptionText != null ? page.OptionText(j) : null;
                if (label == null)
                    label = page.Options[j];
                b.DrawString(Game1.smallFont, prefix + label,
                    new Vector2(textArea.X + 10, optY + j * 26), color);
            }
            // 注意：中文字体没有 ↑↓ 字形，用文字描述
            string hint = page.OnAdjust != null
                ? "上下选行 左右/A/D调值 回车加一步 鼠标点行也行 右键关闭"
                : "上下左右键选择    E/回车/左键确认    右键/Esc关闭";
            b.DrawString(Game1.smallFont, hint,
                new Vector2(box.Right - 360, box.Y + height - 22), new Color(200, 200, 200, 180));
        }
        else
        {
            b.DrawString(Game1.smallFont, "按 E / 回车 / 左键 继续（右键关闭）",
                new Vector2(box.Right - 300, box.Y + height - 24), new Color(200, 200, 200, 180));
        }
    }

    private static void DrawBorder(SpriteBatch b, Rectangle r, Color color, int thickness)
    {
        b.Draw(Pixel, new Rectangle(r.X, r.Y, r.Width, thickness), null, color);
        b.Draw(Pixel, new Rectangle(r.X, r.Bottom - thickness, r.Width, thickness), null, color);
        b.Draw(Pixel, new Rectangle(r.X, r.Y, thickness, r.Height), null, color);
        b.Draw(Pixel, new Rectangle(r.Right - thickness, r.Y, thickness, r.Height), null, color);
    }

    private static List<string> WrapText(string text, SpriteFont font, int maxWidth)
    {
        List<string> lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add("");
            return lines;
        }

        string current = "";
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                lines.Add(current);
                current = "";
            }
            else if (current.Length > 0 && font.MeasureString(current + c).X > maxWidth)
            {
                lines.Add(current);
                current = c.ToString();
            }
            else
            {
                current += c;
            }
        }
        if (current.Length > 0)
            lines.Add(current);
        return lines;
    }
}

/// <summary>空白菜单，仅用于占据 Game1.activeClickableMenu 阻挡游戏输入。</summary>
internal class BlankMenu : IClickableMenu
{
    public BlankMenu()
    {
        // 游戏侧关闭菜单时（如菜单自身 close()）同步结束对话
        exitFunction = () => ModEntry.Dialogue?.Close();
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds) { }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        // 交给对话系统判断点到了哪一行
        ModEntry.Dialogue?.HandleClick(x, y);
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        // 右键关闭：星露谷物语里通用的“关掉当前界面”手势
        ModEntry.Dialogue?.RequestClose();
    }

    public override void receiveKeyPress(Keys key) { }

    public override void update(GameTime time) { }
}
