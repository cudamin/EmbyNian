using System.Diagnostics;
using EmbyMpvClient.App.Composition;
using EmbyMpvClient.App.Controls;
using EmbyMpvClient.App.Theme;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Emby;

namespace EmbyMpvClient.App.Views;

/// <summary>
/// What a page is allowed to ask of the window around it. An interface rather than a direct
/// <c>MainForm</c> reference so a view never reaches into the shell's controls — v1's forms called
/// each other's methods directly and every navigation change touched five files.
/// </summary>
public interface IShell
{
    AppHost Host { get; }

    /// <summary>A transient message; never blocks and never steals focus.</summary>
    void Notify(string message, ToastKind kind = ToastKind.Info);

    void Busy(string message = "正在加载…");

    void Idle();

    /// <summary>Opens the right page for an item: detail for media, a grid for a folder.</summary>
    void Open(EmbyItem item);

    /// <summary>Opens a library (or any folder) as a browsable grid; null shows the library picker.</summary>
    void OpenLibrary(EmbyItem? library);

    void OpenSearch(string term);

    /// <summary>Switches to a top-level page by its navigation key.</summary>
    void GoTo(string key);

    void GoBack();

    /// <summary>
    /// Runs one playback end to end: resolves the resume position, starts mpv and reports the
    /// outcome. <paramref name="choice"/> carries what the user picked on the detail page; null
    /// falls back to the planner's suggestions. <paramref name="episodes"/> is the sibling list
    /// behind an episode and feeds the 「选集」 picker in the now-playing bar. Lives in the shell
    /// so the now-playing bar and the server reporting survive whatever page the user
    /// navigates to next.
    /// </summary>
    Task PlayAsync(EmbyItem item, EmbyItem? parent = null, PlaybackChoice? choice = null, IReadOnlyList<EmbyItem>? episodes = null);

    void SignOut();
}

/// <summary>
/// Base class for the pages the shell swaps between. Owns the cancellation token that makes
/// leaving a page abandon its in-flight requests, and the error funnel that turns an Emby
/// exception into one toast instead of an unhandled-exception dialog.
/// </summary>
public abstract class AppView : Control
{
    private const string Category = "ui";

    private CancellationTokenSource? _lifetime;

    protected AppView(IShell shell)
    {
        Shell = shell;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = Palette.Window;
        ForeColor = Palette.Text;
        Font = Fonts.Body;
        Dock = DockStyle.Fill;
    }

    protected IShell Shell { get; }

    protected AppHost Host => Shell.Host;

    /// <summary>Cancelled when the view is disposed, so a slow request cannot outlive the page.</summary>
    protected CancellationToken Lifetime => (_lifetime ??= new CancellationTokenSource()).Token;

    /// <summary>Shown in the window's header bar while this page is visible.</summary>
    public string HeaderTitle { get; protected set; } = "";

    /// <summary>Called each time the page becomes the visible one.</summary>
    public virtual Task EnterAsync() => Task.CompletedTask;

    /// <summary>
    /// Called when another page takes over; the page is kept alive, not disposed. Deliberately
    /// hides <see cref="Control.Leave"/>: a view has no use for the focus event, and 「离开页面」
    /// is what every call site means.
    /// </summary>
    public new virtual void Leave()
    {
    }

    /// <summary>F5 / the header's refresh button.</summary>
    public virtual Task RefreshAsync() => EnterAsync();

    /// <summary>Fire-and-forget an async action with a single, uniform error path.</summary>
    protected void Run(Func<CancellationToken, Task> work, string failureMessage = "操作失败") =>
        _ = RunAsync(work, failureMessage);

    protected async Task RunAsync(Func<CancellationToken, Task> work, string failureMessage = "操作失败")
    {
        try
        {
            await work(Lifetime);
        }
        catch (OperationCanceledException)
        {
            // Leaving the page is not an error.
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, failureMessage, error);
            Shell.Notify($"{failureMessage}：{Describe(error)}", ToastKind.Error);
        }
    }

    /// <summary>Turns an exception into something worth showing a user.</summary>
    public static string Describe(Exception error) => error switch
    {
        EmbyUnreachableException => "无法连接到服务器，请检查地址和网络",
        EmbyTokenExpiredException => "登录状态已过期，请重新登录",
        EmbyAuthenticationException authentication => authentication.Message,
        EmbyApiException api => api.Message,
        UnauthorizedAccessException => "没有访问权限",
        IOException io => io.Message,
        _ => error.Message
    };

    /// <summary>
    /// Opens a path in Explorer: a file gets selected inside its folder, a folder just opens.
    /// Several pages point at mpv's config, the log folder and the data folder, so this lives here.
    /// </summary>
    protected void Reveal(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
                return;
            }

            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (directory is null || !Directory.Exists(directory))
            {
                Shell.Notify($"路径不存在：{path}", ToastKind.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error)
        {
            Log.Warn(Category, "打开目录失败", error);
            Shell.Notify($"打开目录失败：{Describe(error)}", ToastKind.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Dispose can run more than once (page disposal + form disposal), so null the source
            // out to make the second call a no-op instead of "The CancellationTokenSource has been disposed".
            var lifetime = Interlocked.Exchange(ref _lifetime, null);
            lifetime?.Cancel();
            lifetime?.Dispose();
        }

        base.Dispose(disposing);
    }
}
