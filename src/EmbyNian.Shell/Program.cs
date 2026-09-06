using EmbyNian.Diagnostics;
using EmbyNian.Infrastructure;
using EmbyNian.Shell.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using static EmbyNian.Infrastructure.StartupArgs;

namespace EmbyNian.Shell;

/// <summary>What <see cref="Program"/> worked out before any XAML existed, handed to the app.</summary>
public sealed record StartupOptions
{
    public required string ActivationEventName { get; init; }

    public bool StartMaximized { get; init; }

    public bool SelfCheck { get; init; }

    public bool DumpUi { get; init; }

    /// <summary>
    /// 跑测试的时候在第二屏幕跑: which monitor the window opens on. 1-based in the order Windows enumerates
    /// them (<c>--screen 2</c>), <see cref="ScreenPlacement.NotThePrimary"/> for 「any screen but the main
    /// one」, and <see cref="ScreenPlacement.WhereverWindows"/> — the default for an ordinary launch — to
    /// leave the placement where Windows put it.
    /// <para>
    /// A self-check run defaults to <see cref="ScreenPlacement.NotThePrimary"/> instead, because it drives
    /// the real window: it goes fullscreen, it drags the frame about and it reads back the pixels it drew,
    /// so for the twenty seconds it lasts it owns a screen either way. <c>--screen 1</c> puts it back on the
    /// main one.
    /// </para>
    /// </summary>
    public int Screen { get; init; }

    /// <summary>
    /// Tooling only: open the first library and drop its sort menu open. What the screenshot script
    /// uses, since a flyout cannot be captured by waiting for one.
    /// </summary>
    public bool ShowLibrary { get; init; }

    /// <summary>
    /// Tooling only: 把主页第一张卡的「更多」菜单弹开，好给它拍一张。挑的是有续播位置的那张（那一档菜单最长，
    /// 「从继续观看中移除」只在它上面出现），没有就退到第一张能播的。
    /// <para>
    /// 单独用。它留下的是一张浮层，而<see cref="ShowSettings"/>、<see cref="ShowLibrary"/> 和两个详情开关都会
    /// 抢走激活或者换页，那一下浮层自己就散了。
    /// </para>
    /// </summary>
    public bool ShowMenu { get; init; }

    /// <summary>
    /// Tooling only: 把主页右栏那一列继续观看的两条翻页条摆出来留着，好给它们拍一张（「把继续观看改成点击翻页
    /// 的」，2026-09-05）。
    /// <para>
    /// 和 <see cref="ShowMenu"/> 同一个理由 —— 悬停才浮出来的东西等不出来。**先试过挪真指针**：
    /// <c>SetCursorPos</c> 把它挪到那一栏上是挪成了（两趟都成），可拍出来的照片上翻页条还是没有 —— 用户的手也在
    /// 同一只鼠标上，指针在快门开之前就又走了。所以这一路要的不是更聪明的指针把戏，是一个不依赖指针在哪儿的开关。
    /// </para>
    /// <para>
    /// 单独用。它摆的是「指针在这一栏上」这个状态本身，所以真指针后来进出那一栏一次就会把它收掉 —— 拍照的时候
    /// 别去碰鼠标。
    /// </para>
    /// </summary>
    public bool ShowRail { get; init; }

    /// <summary>
    /// Tooling only: 把播放器的浮层摆到屏上留着，好给它拍一张 —— 控制条、标题条、音量条一起，一个字节的视频都
    /// 不播。可以跟一个词：<c>pinned</c>（置顶那颗键按下的样子）、<c>paused</c> / <c>playing</c>（正中那颗
    /// 暂停/播放徽标，停在满亮上不让它自己淡出）。
    /// <para>
    /// 和 <see cref="ShowMenu"/> 同一个理由：这几层只有指针走到对应的位置才浮上来，而这台机器注不进鼠标事件
    /// （<c>SendInput</c> 返回 1、指针不动）。没有它，播放浮层上任何看得见的改动都拍不到照 —— 而浮层正是用户
    /// 最常盯着看的一片。
    /// </para>
    /// <para>
    /// 单独用，也别和 <c>--self-check</c> 一起用：浮层会立在自检接着要走的那几页前面。故意不收起导航外壳 ——
    /// 不收，浮层就叠在当前那一页上，照片里有真内容当背景；收起来反倒是一层透明浮层背后什么都没有。
    /// </para>
    /// </summary>
    public bool ShowOsd { get; init; }

    /// <inheritdoc cref="ShowOsd"/>
    public string? OsdState { get; init; }

    /// <summary>
    /// Tooling only: 把播放层摆上来然后什么都不动，好观察「鼠标静止两秒后自动隐藏」这件事。不放任何片子。
    /// <para>
    /// 和 <see cref="ShowOsd"/> 正好相反：那一个把浮层钉住不许它收（要拍浮层），这一个让十赫兹那颗计时器照常
    /// 跑（要看它把指针藏掉）。两个别一起用。
    /// </para>
    /// <para>
    /// 也别和 <c>--self-check</c> 一起用：自检跑完就退进程，而这一路要的是留在屏上。指针在哪块屏靠现成的
    /// <c>--screen</c> / <c>--maximized</c> 凑 —— 让窗口去找指针，因为这台机器上指针挪不动。
    /// </para>
    /// </summary>
    public bool HideCursor { get; init; }

    /// <summary>
    /// Tooling only: press 设置 on the way up, on the 界面 card, so the settings window and its six theme
    /// swatches are on screen for a screenshot. Its own flag because 设置 is a second top-level window:
    /// waiting for the app to settle gets the main window and nothing else.
    /// <para>
    /// Meant to be used on its own. Combined with <see cref="ShowLibrary"/> it wins — the settings window
    /// takes activation, which light-dismisses the very flyout that flag exists to leave open.
    /// </para>
    /// </summary>
    public bool ShowSettings { get; init; }

    /// <summary>
    /// Which card <see cref="ShowSettings"/> opens on: <c>--show-settings 关于</c>. Null means 「界面」, which
    /// is what a shot of this window is usually wanted for (the theme swatches). Named as a value on the same
    /// switch rather than a second switch, so 「open the settings window」 and 「on this card」 cannot disagree.
    /// </summary>
    public string? SettingsCategory { get; init; }

    /// <summary>
    /// Tooling only: open the first library and click the first row that drills down, leaving the app on
    /// that detail page. A click rather than a navigation, because 「what does clicking a poster get you」
    /// is the question, and for a series it was once answered with a grid of season folders.
    /// </summary>
    public bool ShowDetail { get; init; }

    /// <summary>
    /// Tooling only: the same two clicks and then one more, onto the first card in 单集, leaving the app on
    /// that episode's page. 媒体信息 lives only on the page of a file, so this is the flag that shows it and
    /// <see cref="ShowDetail"/> the one that shows a page without it.
    /// </summary>
    public bool ShowEpisode { get; init; }

    /// <summary>
    /// Tooling only: with either detail flag, leave the page scrolled all the way down. At scroll 0 the
    /// hero fills the viewport by design, so a screenshot of a fresh detail page shows none of the body —
    /// and the body is what the glass panels and the strip of picture below them have to be looked at in.
    /// Driven from inside the app rather than with a wheel notch because injected pointer input is not
    /// always available (it was blocked outright on the box this was written on).
    /// </summary>
    public bool ScrollEnd { get; init; }

    /// <summary>
    /// Tooling only: with either detail flag, leave the page pulled down half a hero band —— 「往下拉之后标题
    /// 颜色要渐变，变的和下方背景一样」那一条只在这个位置上看得见。滚到 0 时标题栏那一条一点没洗，滚到底时它整条
    /// 就是正文那张纸；而那道横缝出在中间：视口上沿那一行以上只有剧照，以下多了一层罩子。见
    /// <c>DetailPage.ScrollToWash</c>。
    /// </summary>
    public bool ScrollHalf { get; init; }

    /// <summary>
    /// Tooling only: open the first library and play the first thing in it. The player is the one part of
    /// the shell that cannot be reached by waiting, and a screenshot of a stopped player says nothing.
    /// </summary>
    public bool PlayFirst { get; init; }

    /// <summary>
    /// Tooling only: 这一次运行用哪套主题（<c>--theme midnight</c> 或 <c>--theme=midnight</c>），认不出来的 id 由
    /// <c>UiThemes.Resolve</c> 拨回默认那套。
    /// <para>
    /// 五套主题里换主题的唯一入口是设置页上点一块色板 —— 于是除了默认那套，另外四套一张截图都拍不到。
    /// **只改这一次运行看到的颜色，不写进设置文件**：一次截图不该把用户挑的那套换掉。
    /// </para>
    /// <para>
    /// 这个开关从前还有一份更要紧的用处：<c>--theme daylight</c> 拍一张，「浅色主题下没登记过的画刷会是白底配白
    /// 字」这类毛病只在那一套身上看得见。**「晴昼」2026-09-05 按用户的话删了**（见 <c>UiThemes</c> 的类注释），
    /// 所以那类毛病现在没有东西逮得住了 —— 别再按旧文档去跑 <c>--theme daylight</c>，它只会回落到默认那套。
    /// </para>
    /// </summary>
    public string? Theme { get; init; }

    public required AppPaths Paths { get; init; }
}

/// <summary>
/// The entry point. Its whole job is to make the process well-behaved before any window exists:
/// one instance at a time, a log that survives a crash, per-monitor DPI settled, and no unhandled
/// exception that takes the app down without saying why.
/// <para>
/// Written by hand rather than generated (DISABLE_XAML_GENERATED_MAIN) because the data-directory
/// migration and the single-instance mutex both have to run before the framework starts.
/// </para>
/// </summary>
internal static class Program
{
    private const string Category = "启动";

    /// <summary>
    /// Deliberately the same names the WinForms shell uses. The two shells share settings.json, the
    /// image cache and mpv's IPC pipe, so they must be mutually exclusive rather than merely
    /// distinguishable — two clients writing the same settings file is the failure this prevents.
    /// </summary>
    private const string InstanceMutexName = @"Local\EmbyNian.SingleInstance.v3";

    private const string ActivationEventName = @"Local\EmbyNian.Activate.v3";

    /// <summary>The names used before the EmbyMpvClient → EmbyNian rename.</summary>
    private const string LegacyInstanceMutexName = @"Local\EmbyMpvClient.SingleInstance.v2";

    private const string LegacyActivationEventName = @"Local\EmbyMpvClient.Activate.v2";

    private static RingBufferLogSink? _logBuffer;

    [STAThread]
    private static int Main(string[] args)
    {
        // Before anything creates a window: WinUI needs per-monitor v2, and so does our own HWND.
        // Fails harmlessly if the generated manifest already set it, in which case that value stands.
        Native.SetProcessDpiAwarenessContext(Native.DpiAwarenessPerMonitorV2);

        var paths = AppPaths.Default;

        // First real statement on purpose: everything below reads settings, and under the old name — or,
        // once this is installed as an MSIX, under the unpackaged build's own folder — they live
        // somewhere else. Never throws, so a failed migration cannot stop startup. The candidate order
        // and why the packaged case is first are in AppPaths.PriorRoots.
        var migratedFrom = paths.MigrateFromAny(AppPaths.PriorRoots);

        var selfCheck = Has(args, "--self-check");

        // The self-check runs before the single-instance mutex on purpose: it has to be runnable
        // while the client is open, and two of them cannot fight over anything.
        if (!selfCheck)
        {
            if (LegacyInstanceIsRunning())
            {
                SignalRunningInstance(LegacyActivationEventName);
                return 0;
            }

            using var existing = new Mutex(true, InstanceMutexName, out var isFirst);
            if (!isFirst)
            {
                SignalRunningInstance(ActivationEventName);
                return 0;
            }

            try
            {
                return Run(args, paths, migratedFrom, selfCheck: false);
            }
            finally
            {
                existing.ReleaseMutex();
            }
        }

        return Run(args, paths, migratedFrom, selfCheck: true);
    }

    private static int Run(string[] args, AppPaths paths, string? migratedFrom, bool selfCheck)
    {
        StartLogging(paths);

        if (migratedFrom is not null)
            Log.Info(Category, $"已从旧版数据目录迁移设置与缓存：{migratedFrom} → {paths.Root}");

        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Has／Text／Number 在 Core 的 EmbyNian.Infrastructure.StartupArgs 上（using static 引进来的），由
        // StartupArgsTests 钉着：整套验证工具都要先过这三个函数，而最容易读错的几种写法屏上一次也碰不到。
        var options = new StartupOptions
        {
            ActivationEventName = ActivationEventName,
            StartMaximized = Has(args, "--maximized"),
            SelfCheck = selfCheck,
            DumpUi = Has(args, "--dump-ui"),
            Screen = Number(args, "--screen")
                ?? (selfCheck ? ScreenPlacement.NotThePrimary : ScreenPlacement.WhereverWindows),
            ShowLibrary = Has(args, "--show-library"),
            ShowMenu = Has(args, "--show-menu"),
            ShowRail = Has(args, "--show-rail"),
            ShowOsd = Has(args, "--show-osd"),
            OsdState = Text(args, "--show-osd"),
            HideCursor = Has(args, "--hide-cursor"),
            ShowSettings = Has(args, "--show-settings"),
            SettingsCategory = Text(args, "--show-settings"),
            ShowDetail = Has(args, "--show-detail"),
            ShowEpisode = Has(args, "--show-episode"),
            ScrollEnd = Has(args, "--scroll-end"),
            ScrollHalf = Has(args, "--scroll-half"),
            PlayFirst = Has(args, "--play"),
            Theme = Text(args, "--theme"),
            Paths = paths
        };

        try
        {
            Log.Info(Category, $"{AppIdentity.TitleWithVersion} 启动（WinUI 3 外壳），数据目录 {paths.Root}");

            if (args.Length > 0) Log.Debug(Category, $"命令行参数：{string.Join(' ', args)}");

            WinRT.ComWrappersSupport.InitializeComWrappers();

            Application.Start(_ =>
            {
                // Without this an `await` on the UI thread can resume on the thread pool, which in a
                // XAML app means touching a UIElement off its own thread.
                var queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));

                // Application.Current takes ownership from here; the local exists only to say so.
                var app = new App(options);
                Log.Debug(Category, $"应用对象已创建：{app.GetType().FullName}");
            });

            Log.Info(Category, "正常退出");
            return ShellSelfCheck.ExitCode;
        }
        catch (Exception error)
        {
            Log.Error(Category, "启动失败", error);
            return 1;
        }
    }

    private static void StartLogging(AppPaths paths)
    {
        _logBuffer = new RingBufferLogSink(1000);

        try
        {
            paths.EnsureCreated();
            Log.UseSink(new CompositeLogSink(new FileLogSink(paths.LogDirectory), _logBuffer));
        }
        catch (Exception)
        {
            // A read-only or missing LocalAppData must not stop the app; keep the in-memory log so
            // the diagnostics page still works this session.
            Log.UseSink(_logBuffer);
        }
    }

    /// <summary>The last 1000 log lines, for the diagnostics page.</summary>
    internal static RingBufferLogSink? LogBuffer => _logBuffer;

    private static void SignalRunningInstance(string eventName)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(eventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance is still starting up and has not created the event yet.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// True when a pre-rename build is still open. Opening the mutex without acquiring it is enough
    /// to tell: only the running instance holds it.
    /// </summary>
    private static bool LegacyInstanceIsRunning()
    {
        try
        {
            using var legacy = Mutex.OpenExisting(LegacyInstanceMutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Fully qualified: Microsoft.UI.Xaml has a type of the same name, and both are in scope here.
    private static void OnDomainException(object? sender, System.UnhandledExceptionEventArgs e) =>
        Log.Error(Category, "后台线程发生未处理异常", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Observed here so the finalizer thread does not tear the process down; the playback and
        // image paths both fire long-running tasks whose results nobody awaits.
        Log.Warn(Category, "后台任务异常未被处理", e.Exception);
        e.SetObserved();
    }
}

/// <summary>
/// Watches for a second launch and brings the existing window forward, instead of letting the user
/// end up with two clients fighting over the same mpv IPC pipe.
/// </summary>
internal static class ActivationListener
{
    public static IDisposable Start(string eventName, DispatcherQueue queue, Action activate)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        var stopping = new CancellationTokenSource();

        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { signal, stopping.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
                queue.TryEnqueue(() => activate());
        })
        {
            IsBackground = true,
            Name = "activation-listener"
        };

        thread.Start();

        return new Stopper(() =>
        {
            stopping.Cancel();
            signal.Dispose();
            stopping.Dispose();
        });
    }

    private sealed class Stopper(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}
