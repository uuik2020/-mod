using System;
using System.Runtime.InteropServices;
using System.Text;
using StardewModdingAPI;

namespace AIHireling;

/// <summary>
/// 让 SMAPI 控制台（cmd / Windows Terminal / PowerShell）里的日志**变彩色 + 带图标**。
///
/// 两个坑决定了实现方式：
///  1. Windows 控制台默认**不解析 ANSI 转义**，直接塞颜色码只会看到 `←[38;5;51m` 这种乱码。
///     所以启动时先用 P/Invoke 打开 stdout 的 ENABLE_VIRTUAL_TERMINAL_PROCESSING。
///  2. 图标不能用 emoji：中文 Windows 的 cmd 默认字体（新宋体/点阵）没有 emoji 字形，
///     会显示成方块或问号。这里只用 BMP 里的几何符号（◆ ▶ √ ▲ × ★ ※ ·），等宽字体全都有。
///
/// SMAPI 只给一条日志的**第一行**加 `[INFO 模组名]` 前缀，所以多行块只有首行带图标，
/// 后面的行自己缩进对齐。
/// </summary>
internal static class ConsoleArt
{
    // ===================== ANSI 颜色（256 色，Win10+ 的 VT 都认） =====================
    public const string Reset = "\u001b[0m";
    private const string BoldOn = "\u001b[1m";

    public const string C_Grey = "\u001b[38;5;244m";
    private const string C_Silver = "\u001b[38;5;250m";
    private const string C_White = "\u001b[38;5;255m";
    private const string C_Cyan = "\u001b[38;5;51m";
    private const string C_Teal = "\u001b[38;5;44m";
    private const string C_Green = "\u001b[38;5;48m";
    private const string C_Lime = "\u001b[38;5;120m";
    private const string C_Gold = "\u001b[38;5;222m";
    private const string C_Orange = "\u001b[38;5;215m";
    private const string C_Red = "\u001b[38;5;203m";
    private const string C_Blue = "\u001b[38;5;75m";
    private const string C_Purple = "\u001b[38;5;177m";
    private const string C_Pink = "\u001b[38;5;218m";

    // ===================== 图标（全部在 BMP 内，等宽字体都覆盖） =====================
    public const string IcoDiamond = "◆";   // 标题 / 加载
    public const string IcoPlay = "▶";      // 动作 / 步骤
    public const string IcoCheck = "√";     // 成功
    public const string IcoCross = "×";     // 失败
    public const string IcoTri = "▲";       // 警告
    public const string IcoStar = "★";      // 提示 / 重点
    public const string IcoNote = "※";      // 说明
    public const string IcoSmall = "·";     // 普通 / 追踪
    public const string IcoArrow = "→";     // 参数变化
    public const string IcoRing = "○";      // 次要
    public const string IcoSquare = "■";    // 存档 / 数据
    public const string IcoHome = "◇";      // 家 / 位置

    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    /// <summary>这个控制台到底吃不吃 ANSI。false 时全部退回纯文本。</summary>
    public static bool AnsiOk { get; private set; }

    /// <summary>Entry 里尽早调一次：打开 Windows 控制台的 ANSI 解析。</summary>
    public static void Init()
    {
        AnsiOk = false;
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                AnsiOk = true;      // Linux / macOS 终端基本都吃 ANSI
                return;
            }

            IntPtr handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                return;             // 没有控制台（比如被重定向）

            if (!GetConsoleMode(handle, out uint mode))
                return;             // conhost 太老，拿不到模式

            if ((mode & EnableVirtualTerminalProcessing) != 0)
            {
                AnsiOk = true;
                return;
            }

            AnsiOk = SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch
        {
            AnsiOk = false;         // 任何异常都当不支持，别把游戏搞崩
        }
    }

    private static bool ColorOn => AnsiOk && (ModEntry.Config?.控制台彩色 ?? true);
    private static bool IconOn => ModEntry.Config?.控制台图标 ?? true;

    // ===================== 对外接口 =====================

    /// <summary>普通信息：◆ 青色图标 + 浅灰正文。</summary>
    public static void Info(string msg) => Emit(LogLevel.Info, IcoDiamond, C_Cyan, C_Silver, msg);

    /// <summary>干成了：√ 绿。</summary>
    public static void Ok(string msg) => Emit(LogLevel.Info, IcoCheck, C_Green, C_Lime, msg);

    /// <summary>动作 / 步骤：▶ 蓝。</summary>
    public static void Step(string msg) => Emit(LogLevel.Info, IcoPlay, C_Blue, C_Silver, msg);

    /// <summary>重点关注：★ 金。</summary>
    public static void Tip(string msg) => Emit(LogLevel.Info, IcoStar, C_Gold, C_Gold, msg);

    /// <summary>说明 / 补充：※ 紫。</summary>
    public static void Note(string msg) => Emit(LogLevel.Info, IcoNote, C_Purple, C_Silver, msg);

    /// <summary>警告：▲ 黄。</summary>
    public static void Warn(string msg) => Emit(LogLevel.Warn, IcoTri, C_Gold, C_Orange, msg);

    /// <summary>出错：× 红。</summary>
    public static void Error(string msg) => Emit(LogLevel.Error, IcoCross, C_Red, C_Red, msg);

    /// <summary>AI 干活的动作日志（钓鱼/寻路/砍树…）：· 青灰，`[标签]` 高亮。</summary>
    public static void Act(string msg) => Emit(LogLevel.Trace, IcoSmall, C_Teal, C_Grey, msg);

    /// <summary>调试追踪：· 暗灰。</summary>
    public static void Trace(string msg) => Emit(LogLevel.Trace, IcoSmall, C_Grey, C_Grey, msg);

    /// <summary>启动横幅：标题一行 + 若干条目（label 为空 = 缩进的子项）。</summary>
    public static void Banner(string title, params (string Label, string Text, char Kind)[] items)
    {
        IMonitor monitor = ModEntry.SMonitor;
        if (monitor == null)
            return;

        bool color = ColorOn;
        var sb = new StringBuilder();

        // ── 标题 ──
        if (color) sb.Append(C_Cyan).Append(BoldOn);
        sb.Append(IcoDiamond).Append(' ').Append(title);
        if (color) sb.Append(Reset).Append(C_Grey);
        sb.Append(' ').Append(new string('─', Math.Max(6, 48 - DisplayWidth(title))));
        if (color) sb.Append(Reset);

        // ── 条目 ──
        foreach (var (label, text, kind) in items)
        {
            sb.Append('\n');

            if (label.Length == 0)
            {
                // 子项：缩进 4 格，不画图标
                sb.Append("    ");
                if (color) sb.Append(C_Silver);
                sb.Append(text);
                if (color) sb.Append(Reset);
                continue;
            }

            (string icon, string icoColor, string body) = kind switch
            {
                'w' => (IcoTri, C_Gold, C_Orange),
                'e' => (IcoCross, C_Red, C_Red),
                'o' => (IcoCheck, C_Green, C_Lime),
                't' => (IcoStar, C_Gold, C_Gold),
                'n' => (IcoNote, C_Purple, C_Silver),
                'd' => (IcoSmall, C_Grey, C_Silver),
                _ => (IcoPlay, C_Blue, C_Silver),
            };

            sb.Append("    ");
            if (color) sb.Append(icoColor);
            sb.Append(icon).Append(' ');
            if (color) sb.Append(Reset).Append(body);
            sb.Append(PadLabel(label));
            if (color) sb.Append(Reset).Append(C_White);
            sb.Append(text);
            if (color) sb.Append(Reset);
        }

        monitor.Log(sb.ToString(), LogLevel.Info);
    }

    // ===================== 内部实现 =====================

    private static void Emit(LogLevel level, string icon, string iconColor, string bodyColor, string msg)
    {
        IMonitor monitor = ModEntry.SMonitor;
        if (monitor == null || msg == null)
            return;

        monitor.Log(Compose(icon, iconColor, bodyColor, msg), level);
    }

    private static string Compose(string icon, string iconColor, string bodyColor, string msg)
    {
        bool color = ColorOn;
        bool icons = IconOn;

        string[] lines = msg.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (i > 0)
                sb.Append('\n');

            bool first = i == 0;

            // 续行不画图标，但补两格缩进，让整块看起来是「挂在图标下面」
            if (!first && line.Length > 0 && icons)
                sb.Append("  ");

            if (first && icons)
            {
                if (color) sb.Append(iconColor);
                sb.Append(icon).Append(' ');
                if (color) sb.Append(Reset);
            }

            if (color) sb.Append(bodyColor);
            sb.Append(HighlightTag(line, color ? iconColor : null, color ? bodyColor : null));
            if (color) sb.Append(Reset);
        }

        return sb.ToString();
    }

    /// <summary>把行首的 `[钓鱼]` / `[寻路]` 这类标签染成亮色，正文保持原色。</summary>
    private static string HighlightTag(string line, string tagColor, string bodyColor)
    {
        if (tagColor == null || line.Length < 3 || line[0] != '[')
            return line;

        int end = line.IndexOf(']');
        if (end < 2 || end > 10)
            return line;

        return BoldOn + tagColor + line.Substring(0, end + 1)
            + Reset + bodyColor + line.Substring(end + 1);
    }

    /// <summary>按显示宽度补空格（中文算 2 格）。</summary>
    private static string PadLabel(string label)
    {
        const int want = 10;
        int w = DisplayWidth(label);
        string text = w > 0 ? label + "  " : label;
        for (int i = w; i < want; i++)
            text += " ";
        return text;
    }

    private static int DisplayWidth(string text)
    {
        int w = 0;
        foreach (char c in text)
            w += c > 127 ? 2 : 1;
        return w;
    }
}

/// <summary>
/// 日志快捷方式：一行写完，自动带图标和颜色。
/// 用法和原来的 `Monitor.Log(x, LogLevel.Info)` 一一对应，只是更好看。
/// </summary>
internal static class CLog
{
    public static void Info(string msg) => ConsoleArt.Info(msg);
    public static void Ok(string msg) => ConsoleArt.Ok(msg);
    public static void Step(string msg) => ConsoleArt.Step(msg);
    public static void Tip(string msg) => ConsoleArt.Tip(msg);
    public static void Note(string msg) => ConsoleArt.Note(msg);
    public static void Warn(string msg) => ConsoleArt.Warn(msg);
    public static void Error(string msg) => ConsoleArt.Error(msg);
    public static void Act(string msg) => ConsoleArt.Act(msg);
    public static void Trace(string msg) => ConsoleArt.Trace(msg);
}
