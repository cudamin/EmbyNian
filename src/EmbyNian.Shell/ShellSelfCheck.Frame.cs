using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Views;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell;

/// <summary>
/// 自检里问窗口边框和标题栏的那几关：客户区从哪儿起、边框对四个点怎么回答、三颗系统按键在不在、标题栏那一条
/// 什么颜色、那一排自绘按键和窗口留的洞对不对得上。
/// <para>
/// 全是问活窗口的物理像素，不是读样式位 —— 「画面顶到边」是一句关于边框的话，照样式位去读，正是从前那条七个
/// 像素的黑带能发出去的原因。拆成几个文件的缘由见主文件 <see cref="ShellSelfCheck"/> 的类注释。
/// </para>
/// </summary>
internal static partial class ShellSelfCheck
{
    /// <summary>
    /// The frame as the window itself reports it: how far below the window's top edge the client area
    /// starts, how big that client area is, and what the frame answers for four points along the top. All
    /// physical pixels, all asked of the live window — 「the picture reaches the top edge」 is a claim about
    /// the frame, and reading it off the style bits is what let a seven-pixel black band ship.
    /// </summary>
    private static (int TopInset, int Width, int Height, string Hits, bool HitsOk) FrameGeometry(IntPtr window)
    {
        Native.GetWindowRect(window, out var outer);
        Native.GetClientRect(window, out var client);

        var origin = new NativePoint { X = 0, Y = 0 };
        Native.ClientToScreen(window, ref origin);

        var midX = outer.Left + outer.Width / 2;
        var edge = Hit(midX, outer.Top + 1);
        var strip = Hit(midX, outer.Top + 10);
        var below = Hit(midX, outer.Top + 40);
        var corner = Hit(outer.Right - 30, outer.Top + 16);

        // The top row still resizes the window, and everything below it belongs to the page: the strip it
        // draws its own three window commands into, and the corner where the system's would otherwise sit.
        var ok = edge == Native.HitTop
            && strip == Native.HitClient
            && below == Native.HitClient
            && corner == Native.HitClient;

        return (origin.Y - outer.Top, client.Width, client.Height,
            $"顶+1 {Name(edge)}、顶+10 {Name(strip)}、顶+40 {Name(below)}、右上角 {Name(corner)}", ok);

        int Hit(int x, int y) => (int)(long)Native.SendMessage(
            window, Native.WmNcHitTest, IntPtr.Zero, new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF)));

        static string Name(int code) => code switch
        {
            Native.HitClient => "客户区",
            Native.HitCaption => "标题栏",
            Native.HitTop => "上边框",
            _ => $"码 {code}"
        };
    }

    /// <summary>
    /// 窗口在客户区某一点上答什么。逻辑像素进、命中码出，缩放和客户区原点都在里面算掉：<c>WM_NCHITTEST</c>
    /// 收的是屏幕坐标，而这份自检里问的每个点都是从外壳量出来的。
    /// </summary>
    private static int HitAt(HostWindow window, double x, double y)
    {
        var dpi = Native.GetDpiForWindow(window.Handle);
        if (dpi == 0) dpi = 96;
        var scale = (int)dpi / 96.0;

        var origin = new NativePoint { X = 0, Y = 0 };
        Native.ClientToScreen(window.Handle, ref origin);

        var px = origin.X + (int)(x * scale);
        var py = origin.Y + (int)(y * scale);

        return (int)(long)Native.SendMessage(
            window.Handle, Native.WmNcHitTest, IntPtr.Zero, new IntPtr(((py & 0xFFFF) << 16) | (px & 0xFFFF)));
    }

    private static string HitName(int code) => code switch
    {
        Native.HitClient => "客户区",
        Native.HitCaption => "标题栏",
        Native.HitTop => "上边框",
        Native.HitMinButton => "最小化按钮",
        Native.HitMaxButton => "最大化按钮",
        Native.HitClose => "关闭按钮",
        _ => $"码 {code}"
    };

    /// <summary>
    /// 右上角那三颗系统按钮：最小化、最大化、关闭。问的是**窗框**，因为会坏的那半在窗框 —— 按钮是框架自己画
    /// 的，画出来不代表按得动。客户区上的区域声明只要盖过它们那一块，或者把它们那三种区域也一并声明成「什么
    /// 都不占」，按钮就只剩一张图：指针落下去答的是客户区，点击交给底下那层 XAML，然后什么也不发生
    /// （「右上角的最小化 最大化 关闭 点不了」）。
    /// <para>
    /// 三个点是从框架自己报的右侧留白里算的（等分三格，最右边那格是关闭），所以换了 DPI 或者哪天按钮宽度变了
    /// 这条检查照样问得准。
    /// </para>
    /// </summary>
    private static (bool Ok, string Detail) SystemButtons(HostWindow window)
    {
        var (barHeight, inset) = window.TitleBarMetrics;
        if (inset <= 0) return (false, "框架没有报出右侧留白 —— 浏览态本该有三颗系统按钮");

        var (minimise, maximise, close) = CaptionButtonHits(window, inset, barHeight);

        var ok = close == Native.HitClose
            && maximise == Native.HitMaxButton
            && minimise == Native.HitMinButton;

        return (ok, $"右侧留白 {inset:0} 物理像素分三格：最小化 {HitName(minimise)}、"
            + $"最大化 {HitName(maximise)}、关闭 {HitName(close)}");
    }

    /// <summary>
    /// 右侧留白等分三格，每格中点问一次 <c>WM_NCHITTEST</c>，从左到右是最小化、最大化、关闭。留白宽度和标题栏
    /// 高度单独传进来，因为播放态框架把这两项都报成 0 —— 那时候要问的仍是浏览态那三块地方现在答什么。
    /// </summary>
    private static (int Minimise, int Maximise, int Close) CaptionButtonHits(
        HostWindow window, double inset, double barHeight)
    {
        var dpi = Native.GetDpiForWindow(window.Handle);
        if (dpi == 0) dpi = 96;
        var scale = (int)dpi / 96.0;

        Native.GetClientRect(window.Handle, out var client);

        var right = client.Width / scale;
        var column = inset / scale / 3;
        var middle = (barHeight > 0 ? barHeight / scale : 32) / 2;

        return (
            HitAt(window, right - (column * 2.5), middle),
            HitAt(window, right - (column * 1.5), middle),
            HitAt(window, right - (column / 2), middle));
    }

    /// <summary>
    /// 「点击其他窗口或桌面后会变色」：the four colours the custom title bar wears beside the four it wears
    /// once the user clicks something else. Each pair has to match and none of the eight may be unset — an
    /// unset inactive colour is not the active one, it is whatever the system thinks an inactive caption
    /// looks like, which under a light system theme is a pale strip across the top of a window whose every
    /// other pixel is #16181C.
    /// <para>
    /// Asked of the properties rather than of the screen, because seeing it needs the focus taken off this
    /// window — and a self-check that hands the foreground to somebody else has already broken every probe
    /// below it that measures what is in front.
    /// </para>
    /// </summary>
    private static (bool Ok, string Detail) ReportTitleBarColours(HostWindow window)
    {
        if (window.TitleBarColours is not { } bar) return (false, "没有自定义标题栏可问颜色");

        var same = Same(bar.Fill, bar.InactiveFill)
            && Same(bar.Text, bar.InactiveText)
            && Same(bar.Button, bar.InactiveButton)
            && Same(bar.Glyph, bar.InactiveGlyph);

        return (same,
            $"底色 {Show(bar.Fill)}→{Show(bar.InactiveFill)}、文字 {Show(bar.Text)}→{Show(bar.InactiveText)}"
            + $"、按钮底 {Show(bar.Button)}→{Show(bar.InactiveButton)}"
            + $"、按钮图标 {Show(bar.Glyph)}→{Show(bar.InactiveGlyph)}"
            + (same ? "，失焦不变" : "，失焦会变"));

        // Field by field: a projected WinRT struct is not something to trust an operator to.
        static bool Same(Windows.UI.Color? active, Windows.UI.Color? inactive) =>
            active is { } a && inactive is { } b && a.A == b.A && a.R == b.R && a.G == b.G && a.B == b.B;

        static string Show(Windows.UI.Color? colour) =>
            colour is { } c ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}" : "未设";
    }

    /// <summary>
    /// 标题栏上那一排按键：外壳把它们摆成什么样，窗口在那块地方答什么。两件事一起问才算数 —— 那一排必须是
    /// 客户区（不然指针压下去变成拖窗口，按钮永远收不到点击），而同一条带子上这一排以外的地方必须还是标题栏，
    /// 否则挖洞挖成了把整条拖动区挖掉。窗口自己报的洞还要和量出来的那个矩形对得上：洞开歪了在屏幕上完全看不
    /// 出来，直到有人去点按键。
    /// </summary>
    /// <param name="captionBefore">
    /// 播放/浏览标题栏来回切之前，标题栏左端答的是什么。带进来是因为这份自检自己就切过一轮：切回浏览以后那
    /// 条拖动区必须还在，否则「看完一部片回到界面，标题栏就拖不动了」。
    /// </param>
    private static (bool Ok, string Detail) TitleBarKeys(HostWindow window, ShellPage shell, int captionBefore)
    {
        var probe = shell.ProbeTitleActions();

        var centre = HitAt(window, probe.X + probe.Width / 2, probe.Y + probe.Height / 2);

        // 洞左边那块留白，和洞右边 40 像素处：一进一出，证明挖掉的只有这一排所占的那一块。
        var edge = HitAt(window, 20, 16);
        var beyond = HitAt(window, probe.X + probe.Width + 40, probe.Y + probe.Height / 2);

        var hole = window.TitleBarHole;
        var matched = hole is { } rect
            && Math.Abs(rect.X - probe.X) <= 1
            && Math.Abs(rect.Y - probe.Y) <= 1
            && Math.Abs(rect.Width - probe.Width) <= 1
            && Math.Abs(rect.Height - probe.Height) <= 1;

        var ok = probe.Ok
            && centre == Native.HitClient
            && edge == Native.HitCaption
            && beyond == Native.HitCaption
            && captionBefore == Native.HitCaption
            && matched;

        return (ok, $"{probe.Detail}；整排中点 {HitName(centre)}、左端留白 {HitName(edge)}、"
            + $"右侧 40 像素处 {HitName(beyond)}（切播放前 {HitName(captionBefore)}）；"
            + $"窗口留的洞 {(hole is { } r ? $"({r.X:0},{r.Y:0}) {r.Width:0}×{r.Height:0}" : "没有")}"
            + $"，{(matched ? "和按键对得上" : "和按键对不上")}");
    }
}
