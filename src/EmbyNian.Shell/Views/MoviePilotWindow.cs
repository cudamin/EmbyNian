using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.MoviePilot;
using EmbyNian.Shell.Interop;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 「在 MoviePilot 搜索其他版本」的独立窗口 —— 用户在「更多」菜单点那一条时弹出来（<c>ShellPage.SearchMoviePilotVersions</c>）。
/// <para>
/// 照 <see cref="SettingsWindow"/> 的路子办：一个真正的第二顶层 <see cref="Window"/>（主窗口是带 mpv 子窗口的
/// 裸 <see cref="Windowing.HostWindow"/>，容不下第二个视频，而这个窗口里没有视频，交给框架自己的窗口就白得
/// 标题栏、缩放边框和 DPI 处理）。危险也一样：WinUI 应用最后一个 <see cref="Window"/> 关掉时整个进程就退，而
/// 这个应用平时一个都没有 —— 所以标题栏的 X 不能真关（<see cref="OnClosing"/> 拦下来改成隐藏），真关只有
/// <see cref="Close"/>，从 <c>ShellPage.Shutdown</c> 出进程时走一次。
/// </para>
/// <para>
/// 建一次留着、每次点重新填词再弹（<see cref="Show"/>），不是每次点新造一个 —— 既省得留下一堆关不掉的窗口撞上
/// 面那条「最后一个窗口关掉就退进程」的坑，也和设置窗口一个脾气。无主（unowned）：它不该老压在全屏播放上面，
/// 能被切到后面、再点一次「搜索版本」带回来。
/// </para>
/// </summary>
internal sealed class MoviePilotWindow
{
    private const string Category = "moviepilot窗口";

    /// <summary>Logical pixels, scaled to the owner's DPI in <see cref="Place"/>.</summary>
    private const int LogicalWidth = 900;

    private const int LogicalHeight = 680;

    private readonly Window _window;
    private readonly MoviePilotVersionsView _view;

    /// <summary>换主题时重画标题栏；存下来是为了 <see cref="Close"/> 里退订。</summary>
    private readonly Action<EmbyNian.Theming.UiTheme> _repaint;

    /// <summary>Set only by <see cref="Close"/>, so <see cref="OnClosing"/> knows to let one through.</summary>
    private bool _closing;

    private bool _open;

    private MoviePilotWindow(Window window, MoviePilotVersionsView view)
    {
        _window = window;
        _view = view;

        _repaint = _ => PaintCaption();
        ThemeHost.Changed += _repaint;

        window.AppWindow.Closing += OnClosing;
    }

    /// <summary>Whether the window is on screen. Tracked here, like <see cref="SettingsWindow"/>.</summary>
    internal bool IsOpen => _open;

    /// <summary>Outer size in physical pixels. Read by the self-check if it ever wants it.</summary>
    internal SizeInt32 Size => _window.AppWindow.Size;

    /// <summary>
    /// Builds the window, or returns null having logged why. Null is not a loud failure: the caller
    /// (<c>ShellPage.SearchMoviePilotVersions</c>) just reports it and does nothing else — a second XAML
    /// window can fail to create, and there is no in-frame fallback worth building for this.
    /// </summary>
    internal static MoviePilotWindow? TryCreate(Windowing.HostWindow? owner)
    {
        try
        {
            var view = new MoviePilotVersionsView { Background = WindowFill() };

            // 深浅跟着当前主题走（这个窗口里是框架控件 + 我们的 Eg* 样式，只认 ElementTheme）。
            ThemeHost.Register(view);

            if (Application.Current.Resources.TryGetValue("EgUiFontFamily", out var font) && font is FontFamily family)
                view.FontFamily = family;

            var window = new Window { Content = view, Title = "在 MoviePilot 搜索版本" };

            Place(window, owner);
            var created = new MoviePilotWindow(window, view);
            created.PaintCaption();

            Log.Info(Category, "MoviePilot 版本窗口已创建");
            return created;
        }
        catch (Exception error)
        {
            Log.Warn(Category, "创建 MoviePilot 版本窗口失败", error);
            return null;
        }
    }

    /// <summary>
    /// Shows the window for one item: seeds the view with the keyword the item computes to
    /// (<see cref="MoviePilotVersionQuery.Keyword"/>) and runs the search, then brings the window up.
    /// </summary>
    internal void Show(MoviePilotService service, EmbyItem item)
    {
        _view.Search(service, MoviePilotVersionQuery.Keyword(item));
        _window.AppWindow.Show();
        _window.Activate();
        _open = true;
    }

    /// <summary>Off screen, still built. What the X does. The in-flight search is cancelled on the way out.</summary>
    internal void Hide()
    {
        if (!_open) return;

        _open = false;
        _view.Release();
        _window.AppWindow.Hide();
    }

    /// <summary>
    /// The real close, from <c>ShellPage.Shutdown</c> only — a hidden window is still an open one, and left
    /// to the framework it would keep the app alive after the main window had gone.
    /// </summary>
    internal void Close()
    {
        _closing = true;
        _open = false;
        ThemeHost.Changed -= _repaint;

        try
        {
            _window.Close();
        }
        catch (Exception error)
        {
            Log.Warn(Category, "关闭 MoviePilot 版本窗口失败", error);
        }
    }

    /// <summary>Hides on the X instead of closing; see the note on the class for why that matters here.</summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_closing) return;

        e.Cancel = true;
        _open = false;
        _view.Release();
        sender.Hide();
    }

    /// <summary>
    /// Centred on the owner's monitor at <see cref="LogicalWidth"/>×<see cref="LogicalHeight"/> logical
    /// pixels. <c>MoveAndResize</c> speaks physical ones, so the numbers are scaled by the owner's DPI and
    /// clamped to the work area. Copied from <see cref="SettingsWindow"/>; same reasoning.
    /// </summary>
    private static void Place(Window window, Windowing.HostWindow? owner)
    {
        var self = Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);
        var reference = owner?.Handle is { } handle && handle != IntPtr.Zero ? handle : self;

        var dpi = Native.GetDpiForWindow(reference);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;

        var area = DisplayArea.GetFromWindowId(
            Win32Interop.GetWindowIdFromWindow(reference), DisplayAreaFallback.Nearest);
        var work = area.WorkArea;

        var width = Fit(LogicalWidth, 640, work.Width, scale);
        var height = Fit(LogicalHeight, 480, work.Height, scale);

        window.AppWindow.MoveAndResize(new RectInt32(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2),
            width,
            height));
    }

    /// <summary>One axis: the wanted size in physical pixels, short of filling the monitor, never below the minimum.</summary>
    private static int Fit(int logical, int minimum, int available, double scale) =>
        Math.Max(
            (int)Math.Round(minimum * scale),
            Math.Min((int)Math.Round(logical * scale), available - (int)Math.Round(48 * scale)));

    /// <summary>
    /// The same caption as the main window's, for the same reason: the app has one appearance. Colours come
    /// from the current theme, so this re-runs on a theme change (<see cref="_repaint"/>). Mirrors
    /// <c>HostWindow.ConfigureTitleBar</c> / <see cref="SettingsWindow"/>, minus the blank-icon trick — this
    /// window keeps the ordinary app icon in its caption.
    /// </summary>
    private void PaintCaption()
    {
        try
        {
            var theme = ThemeHost.Current;
            var handle = Win32Interop.GetWindowFromWindowId(_window.AppWindow.Id);

            var dark = theme.IsDark ? 1 : 0;
            Native.DwmSetWindowAttribute(handle, Native.DwmUseImmersiveDarkMode, ref dark, sizeof(int));

            var corners = Native.DwmCornerRound;
            Native.DwmSetWindowAttribute(handle, Native.DwmWindowCornerPreference, ref corners, sizeof(int));

            var titleBar = _window.AppWindow.TitleBar;
            if (titleBar is null) return;

            titleBar.BackgroundColor = ThemeHost.ToColor(theme.Colors.Window);
            titleBar.ForegroundColor = ThemeHost.ToColor(theme.Colors.Text);
            titleBar.ButtonBackgroundColor = titleBar.BackgroundColor;
            titleBar.ButtonForegroundColor = ThemeHost.ToColor(theme.Colors.Text);
            titleBar.ButtonHoverBackgroundColor = theme.IsDark
                ? Color.FromArgb(35, 255, 255, 255)
                : Color.FromArgb(25, 0, 0, 0);
            titleBar.ButtonPressedBackgroundColor = theme.IsDark
                ? Color.FromArgb(55, 255, 255, 255)
                : Color.FromArgb(45, 0, 0, 0);
            titleBar.InactiveBackgroundColor = titleBar.BackgroundColor;
            titleBar.InactiveForegroundColor = titleBar.ForegroundColor;
            titleBar.ButtonInactiveBackgroundColor = titleBar.ButtonBackgroundColor;
            titleBar.ButtonInactiveForegroundColor = titleBar.ButtonForegroundColor;
        }
        catch (Exception error)
        {
            Log.Warn(Category, "MoviePilot 版本窗口标题栏配色失败", error);
        }
    }

    /// <summary>
    /// What the view paints under itself. <c>EgWindowBrush</c> lives in <c>Palette.xaml</c>'s
    /// <c>ThemeDictionaries</c>, which only the XAML parser resolves — a code lookup does not see them — so
    /// this is <c>TryGetValue</c> plus the literal <c>EgWindowColor</c> dark value as the fallback, exactly
    /// as <c>SettingsWindow.WindowFill</c> does it.
    /// </summary>
    private static Brush WindowFill() =>
        Application.Current.Resources.TryGetValue("EgWindowBrush", out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Color.FromArgb(255, 0x16, 0x18, 0x1C));
}
