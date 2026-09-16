using EmbyNian.Diagnostics;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Views;

namespace EmbyNian.Shell.Windowing;

/// <summary>
/// 独立播放窗口 —— 「在设置中新增功能，打开后点击播放后弹出一个独立窗口来播放」（用户的话，2026-09-13）.
/// <para>
/// A second top-level window that plays the film, the main window's <em>sibling</em> rather than its
/// child: no visual stacking between them, a focus of its own, and the main window free to move,
/// resize, minimise or hide while the film keeps running — which is the point, on a second monitor.
/// It is <b>not</b> a <see cref="Microsoft.UI.Xaml.Window"/>: it is built exactly the way the main
/// window is — a second <see cref="HostWindow"/> instance, a bare <c>CreateWindowEx</c> HWND with a
/// XAML island filling it — and it gets fullscreen, the caption, the DPI handling and the
/// picture-aspect lock from that class for free.
/// </para>
/// <para>
/// The film runs whichever pipeline the engine setting picked (2026-09-16 双管线)：集成模式 composites
/// into this window's page panel exactly as in the main window; 独立播放 is mpv's own top-level
/// window — no surface, no geometry from the client — appearing beside this one while this window's
/// page keeps the chrome. What 「独立播放」 buys is unchanged either way — a whole window that belongs to
/// the film, the main window's sibling rather than its child.
/// </para>
/// <para>
/// Two <see cref="HostWindow"/>s in one process are supported by construction: the class registers itself
/// once and keeps its instances in maps keyed by HWND, so nothing about it is single-instance. The one piece
/// of genuinely shared state is the <see cref="ViewModels.PlayerViewModel"/>, which is a singleton because the
/// film has to keep playing while the user browses. Two pages wired to it would both answer every command and
/// both drive the same real mpv session, so exactly one is attached at a time: the shell's own
/// <see cref="Views.PlayerPage"/> hands the view model over with <c>Detach</c> when this window opens, and takes it
/// back when the window closes. See <c>PlayerPage.Detach</c> for that half.
/// </para>
/// <para>
/// Closing this window is stopping playback, which is the arrangement the user picked: the window <em>is</em>
/// the playback, and <c>ShellPage</c> turns its close into <c>StopAsync</c> plus getting the shell's player
/// back. That also means it must not go through the 「hide on the X」 dance
/// <see cref="SettingsWindow"/> needs — this one really closes, and the shell is what makes that safe.
/// </para>
/// </summary>
internal sealed class PlayerWindow
{
    private const string Category = "独立播放窗口";

    private readonly HostWindow _window;
    private readonly PlayerPage _page;

    /// <summary>Set once when the window is being torn down on purpose, so <see cref="Closed"/> is not read
    /// as the user having closed it — the shell closes this window itself when playback ends by other means
    /// (Escape, stop, a file that will not play).</summary>
    private bool _closing;

    private PlayerWindow(HostWindow window, PlayerPage page)
    {
        _window = window;
        _page = page;
        _window.Closed += OnWindowClosed;
    }

    /// <summary>Raised once the window has gone, however it went. What the shell hangs 「give the player
    /// back」 on, since both the X and an in-film stop land here.</summary>
    internal event Action? Closed;

    /// <summary>The window itself, for the shell and for the self-check — the geometry, the fullscreen state
    /// and the video surface all live on this.</summary>
    internal HostWindow Window => _window;

    /// <summary>The player inside this window. The shell attaches the view model to it.</summary>
    internal PlayerPage Page => _page;

    /// <summary>
    /// Builds the window and its player, or returns null having said why. Null is not loud: the caller
    /// (<c>ShellPage</c>) falls back to playing in the main window, which is what happens with this setting
    /// off anyway, so a machine that cannot open a second window loses the feature rather than playback.
    /// </summary>
    /// <param name="title">
    /// What the caption says — the item's playback title, so the taskbar and Alt-Tab name the film rather
    /// than 「EmbyNian」. Set from the caption rather than from the XAML content because the video page draws
    /// no header of its own.
    /// </param>
    internal static PlayerWindow? TryCreate(string title)
    {
        try
        {
            // Built first and handed to the window before Show, so the island comes up already carrying the
            // player rather than flashing an empty client area for a frame.
            var page = new PlayerPage();

            // 深浅跟着当前主题走。主窗口那一份是 ShellPage 在自己的构造函数里登记的，这个窗口里没有外壳，
            // 整棵树就只有这一个 PlayerPage —— 它的六颗浮出菜单（MenuFlyout 是框架自己的控件，只认
            // ElementTheme）不登记就一直是系统那一档，深色主题下弹出来是一块白。
            ThemeHost.Register(page);

            var window = new HostWindow { Content = page };

            // The film fills the window, so the caption goes: the same state the main window is put in while
            // playing. Nothing else claims a title bar here — there is no shell in this window to draw one.
            window.PlaybackTitleBar = true;

            // 这个窗口天生只为播放存在，最小尺寸限制也只为浏览窗口而设（见 HostWindow.FreeSizing）：
            // 开窗即解开，想缩多小缩多小。
            window.FreeSizing = true;

            window.Show(maximized: false);

            // 开窗即记一行「这个窗口的标题栏现在是什么样」。这条路上坏掉的方式是「看得见、按不动」——
            // 2026-09-14 那次就是：框架的标题栏没被收掉（PreferredHeightOption 还是 Standard），
            // 右上角于是站着三颗系统按钮，而播放态的区域声明又说那三颗「哪儿都不在」，点下去什么都不接。
            // 一行读数（框架报的标题栏高／右侧留白 + 右上角答什么）就能把这件事钉死在每份日志里。
            var (barHeight, barInset) = window.TitleBarMetrics;
            var client = window.ClientSize;

            Log.Info(
                Category,
                $"独立播放窗口已创建 hwnd=0x{window.Handle:X}「{title}」（播放标题栏={window.PlaybackTitleBar}，"
                + $"框架标题栏 {barHeight:0}×{barInset:0}，客户区 {client.Width}×{client.Height}，"
                + $"右上角答{CornerAnswer(window)}）");

            return new PlayerWindow(window, page);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "创建独立播放窗口失败，改用主窗口播放", error);
            return null;
        }
    }

    /// <summary>
    /// 关闭那颗按钮自己的中点，窗口答什么。播放态的三颗是页面自己画的（标题条最右一列，46×32 贴着右上角），
    /// 所以那里必须答「客户区」，指针落下去才交给 XAML；答成关闭／最大化／最小化按钮，或者答成标题栏，那一颗
    /// 就只剩下一张图。
    /// <para>
    /// 逻辑像素进、命中码出，缩放和客户区原点都在里面算掉 —— <c>WM_NCHITTEST</c> 收的是屏幕坐标。
    /// </para>
    /// </summary>
    private static string CornerAnswer(HostWindow window)
    {
        if (window.Handle == IntPtr.Zero) return "无窗口";

        var dpi = Native.GetDpiForWindow(window.Handle);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96.0;

        Native.GetClientRect(window.Handle, out var client);

        var origin = new NativePoint { X = 0, Y = 0 };
        Native.ClientToScreen(window.Handle, ref origin);

        var x = origin.X + client.Width - (int)(23 * scale);
        var y = origin.Y + (int)(16 * scale);

        var answer = (int)(long)Native.SendMessage(
            window.Handle, Native.WmNcHitTest, IntPtr.Zero, new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF)));

        return answer switch
        {
            Native.HitClient => "客户区",
            Native.HitCaption => "标题栏",
            Native.HitMinButton => "最小化按钮",
            Native.HitMaxButton => "最大化按钮",
            Native.HitClose => "关闭按钮",
            Native.HitTop => "上边框",
            _ => $"码 {answer}"
        };
    }

    /// <summary>
    /// Takes the view model over: the player in here is attached to it and the window is brought forward.
    /// <para>
    /// Called after <see cref="TryCreate"/> rather than from it, because attaching needs the shared
    /// <see cref="ViewModels.PlayerViewModel"/> and the shell, and the shell is the one that has both.
    /// </para>
    /// </summary>
    internal void Attach(ViewModels.PlayerViewModel viewModel, ShellPage shell)
    {
        _page.Attach(viewModel, shell, _window);
        _window.Activate();
    }

    /// <summary>
    /// Takes the window down on purpose. Silent — <see cref="Closed"/> still fires, and the shell is already
    /// the one asking, so it must not be told a second time.
    /// </summary>
    internal void Close()
    {
        if (_closing) return;
        _closing = true;

        try
        {
            _window.Close();
        }
        catch (Exception error)
        {
            Log.Warn(Category, "关闭独立播放窗口失败", error);
        }
    }

    private void OnWindowClosed()
    {
        _window.Closed -= OnWindowClosed;
        _window.Dispose();

        // Not fired when this class did the closing: the shell asked, so it already knows, and letting it
        // run its 「the user closed the film」 path would have it stop a playback it just stopped.
        if (_closing) return;

        Log.Info(Category, "独立播放窗口已关闭");
        Closed?.Invoke();
    }
}
