using EmbyNian.Diagnostics;
using EmbyNian.Shell.Interop;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 设置窗口 —— 需求 1 的第二颗按键，「点击后弹出设置窗口」.
/// <para>
/// A real second top-level window, and the app's only <see cref="Window"/>: the main window is a bare
/// <see cref="Windowing.HostWindow"/> HWND with a XAML island in it, because it has to have an mpv child
/// window under the island. This one has no video in it, so it can be the framework's own window and get
/// the caption, the resize frame and the DPI handling for free.
/// </para>
/// <para>
/// Which is also the hazard: a WinUI app exits when its last <see cref="Window"/> closes, and this app
/// normally has none. So the X on this caption must not close it. <see cref="OnClosing"/> cancels and
/// hides instead, and the only real close is <see cref="Close"/> from <c>ShellPage.Shutdown</c>, on the
/// way out of the process anyway.
/// </para>
/// <para>
/// Unowned on purpose. An owned window is always in front of its owner, which is what one would want for
/// a dialog — but this one is not modal, and the app spends its time playing video full screen. A
/// settings window pinned over a full-screen playback is the same class of complaint as the task bar
/// showing over it; a window that can be sent behind and brought back by pressing 设置 again is not.
/// </para>
/// </summary>
internal sealed class SettingsWindow
{
    private const string Category = "设置窗口";

    /// <summary>Logical pixels, scaled to the owner's DPI in <see cref="Place"/>.</summary>
    private const int LogicalWidth = 1100;

    private const int LogicalHeight = 760;

    private readonly Window _window;
    private readonly Frame _frame;

    /// <summary>换主题时重画标题栏。存下来是为了 <see cref="Close"/> 里退订。</summary>
    private readonly Action<EmbyNian.Theming.UiTheme> _repaint;

    /// <summary>Set only by <see cref="Close"/>, so <see cref="OnClosing"/> knows to let one through.</summary>
    private bool _closing;

    private bool _open;

    private SettingsWindow(Window window, Frame frame)
    {
        _window = window;
        _frame = frame;

        // 标题栏那一条是 Win32 的非客户区，画刷改不到它，只能收到消息之后自己再设一遍。
        _repaint = _ => PaintCaption(window);
        ThemeHost.Changed += _repaint;

        window.AppWindow.Closing += OnClosing;
    }

    /// <summary>
    /// Whether the window is on screen. Tracked rather than asked of the OS, because 「shown」 and
    /// 「hidden」 are the two states this class puts it in and nothing else moves it between them.
    /// </summary>
    internal bool IsOpen => _open;

    /// <summary>Outer size in physical pixels. Read by the self-check.</summary>
    internal SizeInt32 Size => _window.AppWindow.Size;

    /// <summary>
    /// What is on screen inside this window: the hosted 服务器/诊断/服务器控制台 page when the settings page
    /// has one showing, otherwise the settings page itself. The self-check reads both trees through this.
    /// </summary>
    internal object? CurrentPage => (_frame.Content as SettingsPage)?.CurrentPage ?? _frame.Content;

    /// <summary>
    /// The settings page itself, whatever it happens to be showing inside. The self-check needs both: this
    /// one to drive its walk through the cards, <see cref="CurrentPage"/> to read the tree that is really
    /// on screen — which for the hosted categories is a different page altogether.
    /// </summary>
    internal SettingsPage? Page => _frame.Content as SettingsPage;

    /// <summary>
    /// Builds the window, or returns null having said why. Null is not a failure the caller has to
    /// handle loudly — <c>ShellPage.ShowSettings</c> falls back to opening the settings page in the main
    /// window's frame, which is where it lived before this class existed.
    /// </summary>
    internal static SettingsWindow? TryCreate(Windowing.HostWindow? owner)
    {
        try
        {
            var frame = new Frame
            {
                // 需求 9 的同一条：没有过渡。也让第一次导航不闪。
                Transitions = null,
                Background = WindowFill()
            };

            // 深浅跟着当前主题走，而不是写死 Dark：这个窗口里全是框架自己的控件（Expander、NumberBox、
            // ComboBox），它们只认 ElementTheme。
            ThemeHost.Register(frame);

            // Top-level in Palette.xaml, so unlike the brushes it is safe to index; see WindowFill.
            if (Application.Current.Resources.TryGetValue("EgUiFontFamily", out var font) &&
                font is FontFamily family)
                frame.FontFamily = family;

            var window = new Window { Title = "设置", Content = frame };

            Place(window, owner);
            PaintCaption(window);

            Log.Info(Category, "设置窗口已创建");
            return new SettingsWindow(window, frame);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "创建设置窗口失败，改用主窗口内的设置页", error);
            return null;
        }
    }

    /// <summary>
    /// Shows the window on a category, building or re-pointing the page inside it.
    /// <para>
    /// Navigated before it is shown, and every time: the page is cached in this frame, so the usual visit
    /// is a parameter change on a page that is already built — which is what keeps half-typed config text
    /// alive across trips to 设置, exactly as it did when the page lived in the main frame.
    /// </para>
    /// </summary>
    internal void Show(SettingsRequest request)
    {
        _frame.Navigate(typeof(SettingsPage), request);

        // Nothing in this window goes back, and a stack that grows by one per visit is a page kept alive
        // per visit with it.
        _frame.BackStack.Clear();

        _window.AppWindow.Show();
        _window.Activate();
        _open = true;
    }

    /// <summary>
    /// Off screen, still built. What the X does, and what signing out does.
    /// <para>
    /// The hosted page is released on the way out. It would otherwise go on doing whatever it was doing
    /// behind a window nobody can see, which for 需求 8's console means a live browser process — and for the
    /// server list and the log view means work whose result nothing will read. Reopening navigates the frame
    /// again, so the page comes back fresh.
    /// </para>
    /// </summary>
    internal void Hide()
    {
        if (!_open) return;

        _open = false;
        Page?.ReleaseHosted();
        _window.AppWindow.Hide();
    }

    /// <summary>
    /// The real close, from <c>ShellPage.Shutdown</c> only. Left to the framework the window would keep
    /// the app alive after the main window had gone: a hidden window is still an open one.
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
            Log.Warn(Category, "关闭设置窗口失败", error);
        }
    }

    /// <summary>
    /// Hides on the X instead of closing; see the note on the class for why that matters more here than
    /// it would in an ordinary app.
    /// </summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_closing) return;

        e.Cancel = true;
        _open = false;
        Page?.ReleaseHosted();
        sender.Hide();
    }

    /// <summary>
    /// Centred on the owner's monitor at <see cref="LogicalWidth"/>×<see cref="LogicalHeight"/> logical
    /// pixels. <c>MoveAndResize</c> speaks physical ones, so the numbers are scaled by the owner's DPI —
    /// 760 logical is 1520 physical at 200%, which is taller than a 1080p monitor, hence the clamp to the
    /// work area rather than a straight multiply.
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

    /// <summary>
    /// One axis: the wanted size in physical pixels, short of filling the monitor, and never below what
    /// the page needs to lay out — a settings page 400 pixels wide is not a smaller settings page.
    /// </summary>
    private static int Fit(int logical, int minimum, int available, double scale) =>
        Math.Max(
            (int)Math.Round(minimum * scale),
            Math.Min((int)Math.Round(logical * scale), available - (int)Math.Round(48 * scale)));

    /// <summary>
    /// The same caption as the main window's, and for the same reason: the app has one appearance, and a
    /// caption left to the system is a pale strip over a window whose every other pixel is the theme's
    /// window colour. The inactive twins are set to their active partners because an unset twin does not
    /// inherit — see <c>HostWindow.ConfigureTitleBar</c>, which this mirrors.
    /// <para>
    /// 颜色取自当前主题，所以换主题时这里要再跑一遍（<see cref="_repaint"/>）。悬停和按下那两档故意用
    /// 半透明的白／黑而不是主题里的某个面：它们压在标题栏底色上，靠透明度叠出来的那一档在六套主题里都对。
    /// </para>
    /// <para>
    /// Its own try/catch, inside <see cref="TryCreate"/>'s: a caption colour that will not take is a
    /// cosmetic loss, and falling back to the in-frame settings page over one would be a worse trade.
    /// </para>
    /// </summary>
    private static void PaintCaption(Window window)
    {
        try
        {
            var theme = ThemeHost.Current;
            var handle = Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);

            var dark = theme.IsDark ? 1 : 0;
            Native.DwmSetWindowAttribute(handle, Native.DwmUseImmersiveDarkMode, ref dark, sizeof(int));

            var corners = Native.DwmCornerRound;
            Native.DwmSetWindowAttribute(handle, Native.DwmWindowCornerPreference, ref corners, sizeof(int));

            var titleBar = window.AppWindow.TitleBar;
            if (titleBar is null) return;

            titleBar.BackgroundColor = ThemeHost.ToColor(theme.Colors.Window);
            titleBar.ForegroundColor = ThemeHost.ToColor(theme.Colors.Text);

            // 不透明，和 HostWindow 那份故意相反：那个窗口把内容铺进了标题栏，所以按键底色透明是让底下的
            // XAML 透出来；这个窗口没铺，透明背后什么都没有，DWM 就拿自己那套默认色填 —— 深色主题下看不出
            // 来，浅色主题下就是浅色标题栏右端一块黑。「--theme daylight 拍一张」找出来的就是这一块。
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
            Log.Warn(Category, "设置窗口标题栏配色失败", error);
        }
    }

    /// <summary>
    /// What the frame paints under the page. <c>EgWindowBrush</c> is asked for and usually not given:
    /// it is declared inside <c>Palette.xaml</c>'s <c>ThemeDictionaries</c>, and only the XAML parser
    /// resolves those — a code lookup does not see them (see the note at <c>EpisodeRow.xaml</c>). Hence
    /// <c>TryGetValue</c> and not the indexer, which throws, and hence the literal: it is
    /// <c>EgWindowColor</c>'s dark value, so the two agree whichever way the lookup goes.
    /// </summary>
    private static Brush WindowFill() =>
        Application.Current.Resources.TryGetValue("EgWindowBrush", out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Color.FromArgb(255, 0x16, 0x18, 0x1C));
}
