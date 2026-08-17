using EmbyMpvClient.App.Composition;
using EmbyMpvClient.App.Theme;
using EmbyMpvClient.App.Views;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Infrastructure;

namespace EmbyMpvClient.App;

/// <summary>
/// The entry point. Its whole job is to make the process well-behaved before any window exists:
/// one instance at a time, a log that survives a crash, and no unhandled exception that takes the
/// app down without saying why.
/// </summary>
internal static class Program
{
    private const string Category = "启动";

    /// <summary>Session-scoped names, so a second launch under the same account finds the first.</summary>
    private const string InstanceMutexName = @"Local\EmbyMpvClient.SingleInstance.v2";

    private const string ActivationEventName = @"Local\EmbyMpvClient.Activate.v2";

    private static RingBufferLogSink? _logBuffer;

    [STAThread]
    private static int Main(string[] args)
    {
        var paths = AppPaths.Default;

        // Before the single-instance mutex on purpose: the check has to be runnable while the client
        // is open, and it never shows a window, so two of them cannot fight over anything.
        if (Has(args, "--self-check"))
        {
            StartLogging(paths);
            ApplicationConfiguration.Initialize();
            return SelfCheck.Run(paths, _logBuffer!, Has(args, "--dump-ui"));
        }

        using var instance = new Mutex(true, InstanceMutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            SignalRunningInstance();
            return 0;
        }

        StartLogging(paths);

        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Application.ThreadException += OnThreadException;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // Sets visual styles, PerMonitorV2 DPI and the default font from the csproj properties.
        ApplicationConfiguration.Initialize();

        try
        {
            using var host = AppHost.Create();
            Log.Info(Category, $"{AppInfo.TitleWithVersion} 启动，数据目录 {paths.Root}");

            using var window = new MainForm(host, _logBuffer!);
            using var activation = ListenForActivation(window);

            Application.Run(window);
            Log.Info(Category, "正常退出");
            return 0;
        }
        catch (Exception error)
        {
            Fatal("启动失败", error);
            return 1;
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    private static bool Has(string[] args, string flag) =>
        args.Any(argument => string.Equals(argument.TrimStart('-', '/'), flag.TrimStart('-'), StringComparison.OrdinalIgnoreCase));

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
            // A read-only or missing LocalAppData must not stop the app; keep the in-memory log
            // so the diagnostics page still works this session.
            Log.UseSink(_logBuffer);
        }
    }

    /// <summary>
    /// Watches for a second launch and brings this window forward instead of letting the user
    /// end up with two clients fighting over the same mpv IPC pipe.
    /// </summary>
    private static IDisposable ListenForActivation(MainForm window)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        var stopping = new CancellationTokenSource();

        var thread = new Thread(() =>
        {
            var handles = new[] { signal, stopping.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                if (!window.IsHandleCreated) continue;
                try
                {
                    window.BeginInvoke(window.ActivateFromSecondInstance);
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "activation-listener"
        };

        thread.Start();
        return new Disposer(() =>
        {
            stopping.Cancel();
            signal.Dispose();
            stopping.Dispose();
        });
    }

    private static void SignalRunningInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ActivationEventName);
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

    private static void OnThreadException(object? sender, ThreadExceptionEventArgs e) =>
        Fatal("界面线程发生未处理异常", e.Exception);

    private static void OnDomainException(object? sender, UnhandledExceptionEventArgs e) =>
        Log.Error(Category, "后台线程发生未处理异常", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Observed here so the finalizer thread does not tear the process down; the playback and
        // image paths both fire long-running tasks whose results nobody awaits.
        Log.Warn(Category, "后台任务异常未被处理", e.Exception);
        e.SetObserved();
    }

    private static void Fatal(string message, Exception error)
    {
        Log.Error(Category, message, error);

        try
        {
            MessageBox.Show(
                $"{message}：{error.Message}\n\n详细信息已写入日志：\n{AppPaths.Default.LogDirectory}",
                AppInfo.Title,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            // Nothing left to do if even the message box fails.
        }
    }

    private sealed class Disposer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
