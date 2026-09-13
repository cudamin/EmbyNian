using EmbyNian.Diagnostics;
using EmbyNian.Shell.Views;

namespace EmbyNian.Shell.Windowing;

/// <summary>
/// 独立播放窗口 —— 「在设置中新增功能，打开后点击播放后弹出一个独立窗口来播放」（用户的话，2026-09-13）.
/// <para>
/// A second top-level window that plays the film, so the main window can stay on the page the user was
/// looking at. It is <b>not</b> a <see cref="Microsoft.UI.Xaml.Window"/>: mpv draws into a child HWND handed to
/// it as <c>wid</c>, and a real framework window hides child HWNDs (fact 2 in <see cref="HostWindow"/>'s
/// remarks), so a film would be invisible in one. It is therefore built exactly the way the main window is —
/// a second <see cref="HostWindow"/> instance, which is a bare <c>CreateWindowEx</c> HWND with a XAML island
/// over a video child — and it gets fullscreen, the caption, the DPI handling and the picture-aspect lock
/// from that class for free.
/// </para>
/// <para>
/// Two <see cref="HostWindow"/>s in one process are supported by construction: the class registers itself
/// once and keeps its instances in maps keyed by HWND, so nothing about it is single-instance. The one piece
/// of genuinely shared state is the <see cref="ViewModels.PlayerViewModel"/>, which is a singleton because the
/// film has to keep playing while the user browses. Two pages wired to it would both answer every command and
/// both drive the same real mpv session, so exactly one is attached at a time: the shell's own
/// <see cref="PlayerPage"/> hands the view model over with <c>Detach</c> when this window opens, and takes it
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

    /// <summary>The window itself, for the shell and for the self-check — the geometry, the video child and
    /// the fullscreen state all live on this.</summary>
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

            window.Show(maximized: false);

            Log.Info(Category, $"独立播放窗口已创建 hwnd=0x{window.Handle:X}「{title}」");
            return new PlayerWindow(window, page);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "创建独立播放窗口失败，改用主窗口播放", error);
            return null;
        }
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
