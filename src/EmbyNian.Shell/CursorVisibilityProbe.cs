using System.Diagnostics;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Playback;
using EmbyNian.Shell.Composition;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Views;
using EmbyNian.Shell.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace EmbyNian.Shell;

/// <summary>Runs the production cursor timer against isolated, unsigned-in services and optional local video.</summary>
internal static class CursorVisibilityProbe
{
    internal static int ExitCode { get; private set; } = 1;

    internal static bool IsRequested(string[] args) => args.Any(argument =>
        argument.TrimStart('-', '/').Split('=', 2)[0]
            .Equals("probe-cursor", StringComparison.OrdinalIgnoreCase));

    internal static async Task RunAsync(StartupOptions options)
    {
        Directory.CreateDirectory(options.Paths.LogDirectory);
        using var report = new StreamWriter(Path.Combine(options.Paths.LogDirectory, "cursor-probe.txt"))
        { AutoFlush = true };
        var originalForeground = Native.GetForegroundWindow();
        var restorePointer = Native.GetCursorPos(out var originalPointer);
        HostWindow? window = null;
        ShellPage? shell = null;
        IPlaybackHandle? playback = null;
        ServiceProvider? services = null;
        var clock = Stopwatch.StartNew();
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = stopping.Token;
        try
        {
            services = ShellServices.Build(options.Paths);
            var settings = services.GetRequiredService<AppSettings>();
            settings.Mpv.Pipeline = VideoPipelineKind.Integrated;
            shell = services.GetRequiredService<ShellPage>();
            shell.Attach(services);
            window = new HostWindow { Content = shell, FreeSizing = true };
            window.Closed += stopping.Cancel;
            window.Show(false, options.Screen, new WindowBounds(80, 80, 1120, 760));
            shell.AttachWindow(window);
            var page = shell.PlayerRoot;
            page.BeginCursorProbe();
            await Task.Delay(700, token);
            var picture = (PictureSurface)page.FindName("Root");
            var source = InputPointerSource.GetForIsland(picture.XamlRoot.ContentIsland);
            var inputEvents = 0;
            picture.PointerMoved += (_, _) => inputEvents++;
            Write($"隔离诊断 PID={Environment.ProcessId} HWND=0x{window.Handle:X} island=0x{window.IslandHandle:X}");
            Write("独立空数据目录；未登录、不读取真实设置、不调用 PlaybackService 播放或上报。");
            Write("使用正式 PlayerPage、HostWindow 和 10Hz 光标计时器；检查系统光标，不以内部隐藏位代替。");

            if (options.ProbeCursorFile is { Length: > 0 } file)
            {
                var media = CompositionPlaybackProbe.LocalFile(file);
                var backend = new LibMpvBackend(new MpvSettings { Pipeline = VideoPipelineKind.Integrated },
                    new PlaybackSettings(),
                    () => page.VideoSurface);
                playback = await backend.StartAsync(new PlaybackRequest
                {
                    MediaUrl = media,
                    Title = "本地光标诊断",
                    SubtitlesDisabled = true,
                    PlayerOptions =
                    [
                        new("mute", "yes"), new("loop-file", "inf"),
                        new("audio-file-auto", "no"), new("sub-auto", "no"),
                        new("access-references", "no"), new("load-unsafe-playlists", "no"),
                        new("demuxer-lavf-o", "protocol_whitelist=file")
                    ]
                }, token);
                Write($"本地测试视频：{media.LocalPath}");
            }

            foreach (var full in new[] { false, true })
            {
                window.Fullscreen = full;
                await Task.Delay(400, token);
                Native.GetClientRect(window.Handle, out var rect);
                var centre = new NativePoint { X = rect.Width / 2, Y = rect.Height / 2 };
                Native.ClientToScreen(window.Handle, ref centre);
                window.Activate();
                var eventsBefore = inputEvents;
                await Move(centre.X - 30, centre.Y);
                await Move(centre.X, centre.Y);
                if (inputEvents == eventsBefore)
                {
                    Native.NudgeCursorState();
                    await Task.Delay(200, token);
                }
                Write($"模式={(full ? "全屏" : "窗口化")} 输入事件={inputEvents - eventsBefore}");
                Require(inputEvents > eventsBefore, "XAML 收到测试移动，未以纯坐标搬运冒充输入");
                await Until(() => !page.CursorProbeState.Hidden && !Gone(), 700, "移动后系统光标可见");
                var moves = page.CursorProbeState.Moves;
                await Until(() => page.CursorProbeState.Hidden && Gone(), 2500, "静止后系统光标隐藏");
                Require(page.CursorProbeState.Watching, "隐藏期间系统光标事件监听已安装");
                await Until(() => page.CursorProbeState.AttachedQueue, 700, "已接入实际发布光标的输入线程");
                Require(page.CursorProbeState.Moves == moves, "隐藏刷新没有唤醒播放器");
                Require(page.ProbeCursorModifierPreservation(), "连接和解除输入线程时保留 Ctrl、Alt、Shift 状态");
                await Until(() => Gone(), 700, "快捷键状态验证后仍然隐藏");
                await WatchHidden(10000, "持续静止");

                for (var round = 1; round <= 2; round++)
                {
                    using var wait = InputSystemCursor.Create(InputSystemCursorShape.Wait);
                    source.Cursor = wait;
                    await Task.Delay(200, token);
                    await Until(() => page.CursorProbeState.Hidden && Gone(), 1800,
                        $"外来光标覆盖第 {round} 轮自动恢复");
                    await WatchHidden(1800, $"覆盖第 {round} 轮后");
                }

                var current = Native.CursorSnapshot() ?? throw new InvalidOperationException("系统快照读取失败");
                Require(current.At.X == centre.X && current.At.Y == centre.Y, "刷新未留下鼠标位移");
                var beforeMove = page.CursorProbeState.Moves;
                await Move(centre.X + 60, centre.Y);
                await Until(() => !page.CursorProbeState.Hidden && !Gone(), 700, "真正移动后系统光标恢复");
                Require(page.CursorProbeState.Moves > beforeMove, "真实移动被正常识别");
                Require(!page.CursorProbeState.AttachedQueue, "显示时已解除输入线程连接");
                await Until(() => page.CursorProbeState.Hidden && Gone(), 2500, "再次静止仍能隐藏");

                async Task WatchHidden(int duration, string stage)
                {
                    var start = clock.ElapsedMilliseconds;
                    var visible = 0;
                    var samples = 0;
                    var longest = 0;
                    var streak = 0;
                    while (clock.ElapsedMilliseconds - start < duration)
                    {
                        var snapshot = Native.CursorSnapshot()
                            ?? throw new InvalidOperationException("GetCursorInfo 失败");
                        Require(Native.GetAncestor(Native.WindowFromPoint(snapshot.At), Native.GaRoot) == window.Handle,
                            "指针仍在本次测试窗口上", quiet: true);
                        Require(page.CursorProbeState.Hidden, "持续静止没有被刷新误唤醒", quiet: true);
                        samples++;
                        if (!Gone()) { visible++; streak++; longest = Math.Max(longest, streak); }
                        else streak = 0;
                        await Task.Delay(25, token);
                    }
                    Write($"{stage}：{samples} 次采样，可见 {visible} 次，最长连续 {longest} 次；输入刷新 {page.CursorProbeState.Refreshes} 次");
                    Require(visible == 0, $"{stage}全程没有可见光标");
                }
            }
            shell.Shutdown();
            Require(!page.CursorProbeState.AttachedQueue && !page.CursorProbeState.Watching,
                "播放层退出后已解除输入连接和光标监听");
            await Until(() => !Gone(), 700, "退出播放层后系统光标恢复");
            shell = null;
            ExitCode = 0;
            Write("结果：全部通过");
        }
        catch (Exception error)
        {
            Write($"结果：失败：{error}");
        }
        finally
        {
            try
            {
                if (playback is not null)
                {
                    try { await playback.StopAsync(); }
                    catch (Exception error) { CleanupFailure(error); }
                    try { await playback.DisposeAsync(); }
                    catch (Exception error) { CleanupFailure(error); }
                }
                try { shell?.Shutdown(); }
                catch (Exception error) { CleanupFailure(error); }
                if (window is not null)
                {
                    window.Closed -= stopping.Cancel;
                    try { window.CursorHidden = false; }
                    catch (Exception error) { CleanupFailure(error); }
                    try { window.Dispose(); }
                    catch (Exception error) { CleanupFailure(error); }
                }
                if (services is not null)
                {
                    try { await services.DisposeAsync(); }
                    catch (Exception error) { CleanupFailure(error); }
                }
            }
            finally
            {
                if (restorePointer) Native.SetCursorPos(originalPointer.X, originalPointer.Y);
                if (originalForeground != IntPtr.Zero) Native.SetForegroundWindow(originalForeground);
                Application.Current.Exit();
            }
        }

        void CleanupFailure(Exception error)
        {
            ExitCode = 1;
            Write($"清理失败：{error}");
        }

        bool Gone()
        {
            if (Native.CursorSnapshot() is not { } snap) return false;
            return (snap.Flags & Native.CurShowing) == 0 || snap.Shape == IntPtr.Zero
                || Native.CursorIsTransparent(snap.Shape);
        }

        async Task Move(int x, int y)
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var sent = Native.MovePointerTo(x, y);
                await Task.Delay(160, token);
                Native.GetCursorPos(out var at);
                Write($"测试输入目标={x},{y}，实际={at.X},{at.Y}，SendInput={sent}");
                if (Math.Abs(at.X - x) <= 1 && Math.Abs(at.Y - y) <= 1) return;
                if (Native.SetCursorPos(x, y)) Native.NudgeCursorState();
                await Task.Delay(160, token);
                if (Native.GetCursorPos(out at) && Math.Abs(at.X - x) <= 1 && Math.Abs(at.Y - y) <= 1) return;
                await Task.Delay(500, token);
            }
            throw new InvalidOperationException("测试鼠标无法到位，未开始显隐断言");

        }

        async Task Until(Func<bool> ready, int milliseconds, string stage)
        {
            var start = clock.ElapsedMilliseconds;
            while (!ready())
            {
                if (clock.ElapsedMilliseconds - start > milliseconds)
                    throw new InvalidOperationException($"{stage}超时；系统={Native.CursorSnapshot()}");
                await Task.Delay(25, token);
            }
            Write($"通过：{stage}（{clock.ElapsedMilliseconds - start}ms）");
        }

        void Require(bool condition, string message, bool quiet = false)
        {
            if (!condition) throw new InvalidOperationException(message);
            if (!quiet) Write($"通过：{message}");
        }

        void Write(string message)
        {
            report.WriteLine($"{clock.ElapsedMilliseconds}ms {message}");
            Log.Info("光标诊断", message);
        }
    }
}
