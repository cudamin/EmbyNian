using EmbyNian.Diagnostics;
using EmbyNian.Infrastructure;
using EmbyNian.Shell.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

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
    /// Tooling only: 这一次运行用哪套主题（<c>--theme misty</c> 或 <c>--theme=misty</c>），认不出来的 id 由
    /// <c>UiThemes.Resolve</c> 拨回默认那套。
    /// <para>
    /// 六套主题里换主题的唯一入口是设置页上点一块色板 —— 于是除了默认那套，另外五套一张截图都拍不到，
    /// 而其中「晴昼」是唯一的浅色，「浅色主题下没登记过的画刷会是白底配白字」这类毛病只在它身上看得见。
    /// **只改这一次运行看到的颜色，不写进设置文件**：一次截图不该把用户挑的那套换掉。
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

        // First real statement on purpose: everything below reads settings, and under the old name
        // they live in a different folder. Never throws, so a failed migration cannot stop startup.
        var migratedFrom = paths.MigrateFrom(AppPaths.LegacyDefaultRoot);

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

        var options = new StartupOptions
        {
            ActivationEventName = ActivationEventName,
            StartMaximized = Has(args, "--maximized"),
            SelfCheck = selfCheck,
            DumpUi = Has(args, "--dump-ui"),
            Screen = Number(args, "--screen")
                ?? (selfCheck ? ScreenPlacement.NotThePrimary : ScreenPlacement.WhereverWindows),
            ShowLibrary = Has(args, "--show-library"),
            ShowSettings = Has(args, "--show-settings"),
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

    private static bool Has(string[] args, string flag) =>
        args.Any(argument => string.Equals(
            argument.TrimStart('-', '/'), flag.TrimStart('-'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The value given to <paramref name="flag"/>, written either way round — <c>--theme misty</c> or
    /// <c>--theme=misty</c> — or null when the flag is absent or nothing follows it. A separate token that
    /// itself looks like a flag does not count as the value, so <c>--theme --dump-ui</c> reads as 「主题开关
    /// 没给值」 rather than as a theme called <c>--dump-ui</c>.
    /// </summary>
    private static string? Text(string[] args, string flag)
    {
        var name = flag.TrimStart('-');

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index].TrimStart('-', '/');

            if (argument.StartsWith($"{name}=", StringComparison.OrdinalIgnoreCase))
                return Whole(argument[(name.Length + 1)..]);

            if (!string.Equals(argument, name, StringComparison.OrdinalIgnoreCase)) continue;

            if (index + 1 >= args.Length) return null;

            var next = args[index + 1];
            return next.StartsWith('-') || next.StartsWith('/') ? null : Whole(next);
        }

        return null;

        static string? Whole(string value) => value.Length == 0 ? null : value;
    }

    /// <summary>
    /// The number given to <paramref name="flag"/>, written either way round — <c>--screen 2</c> or
    /// <c>--screen=2</c> — or null when the flag is absent or what follows it is not a number. Its own
    /// small parser rather than an option table, because this and <c>--theme</c> are the only arguments
    /// the shell takes that are not plain switches.
    /// </summary>
    private static int? Number(string[] args, string flag) =>
        int.TryParse(Text(args, flag), out var value) ? value : null;

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
