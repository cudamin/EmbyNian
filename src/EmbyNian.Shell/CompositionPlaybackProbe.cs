using System.Diagnostics;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Views;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Xaml;

namespace EmbyNian.Shell;

/// <summary>
/// Local-only integration diagnostic, deliberately outside PlaybackService/DI. Exercises the real
/// page's composition target without attaching its VM, restoring credentials or reporting progress.
/// </summary>
internal static class CompositionPlaybackProbe
{
    internal static int ExitCode { get; private set; } = 1;

    // Has() alone misses --flag= (including an empty value). Presence must win over all other flags,
    // even --play: invalid input is a failed probe, never permission to launch the normal client.
    internal static bool IsRequested(string[] args) => args.Any(argument =>
        argument.TrimStart('-', '/').Split('=', 2)[0]
            .Equals("probe-composition", StringComparison.OrdinalIgnoreCase));

    internal static async Task RunAsync(StartupOptions options)
    {
        StreamWriter? report = null;
        HostWindow? window = null;
        PlayerPage? page = null;
        IPlaybackHandle? handle = null;
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = stopping.Token;
        var passed = false;
        try
        {
            Directory.CreateDirectory(options.Paths.LogDirectory);
            report = new StreamWriter(Path.Combine(options.Paths.LogDirectory, "composition-probe.txt"))
            { AutoFlush = true };
            Write($"本地合成诊断，PID={Environment.ProcessId}，程序={Environment.ProcessPath}");
            Write("隔离运行：未创建 ShellServices / PlaybackService / EmbySession，不读写用户设置。");
            Write("验证范围：真实 PlayerPage + CompositionVideoTarget；未接 VM，不验证完整 OSD 交互。");
            Write("重要：surface 挂树与 position 推进不等于画面正确，图像仍须留屏目视观察。");
            var media = LocalFile(options.ProbeCompositionFile);
            Write($"本地文件：{media.LocalPath}");

            // The page supports null-rooted x:Bind before Attach. Disable interaction rather than
            // fabricate a VM (and thereby acquire services); video host/layout are production code.
            page = new PlayerPage { Visibility = Visibility.Visible, IsHitTestVisible = false };
            // No VM means the loading cover's binding has no value; its XAML default is Visible.
            ((FrameworkElement)page.FindName("Cover")).Visibility = Visibility.Collapsed;
            ((Microsoft.UI.Xaml.Controls.ProgressRing)page.FindName("CoverRing")).IsActive = false;
            var surface = page.VideoSurface as CompositionVideoTarget
                ?? throw new InvalidOperationException("PlayerPage 未提供真实 CompositionVideoTarget");
            window = new HostWindow { Content = page, FreeSizing = true };
            window.Closed += stopping.Cancel;
            window.Show(false, options.Screen, new WindowBounds(80, 80, 1040, 680));
            await UntilAsync(() => page.XamlRoot is not null && surface.Size is { Width: > 0, Height: > 0 },
                "XAML 视频宿主完成布局");
            Write($"窗口 HWND=0x{window.Handle.ToInt64():X}，surface={surface.Size}");

            var backend = new LibMpvBackend(new MpvSettings { Pipeline = VideoPipelineKind.Integrated },
                new PlaybackSettings(),
                () => surface);
            var request = new PlaybackRequest
            {
                MediaUrl = media,
                Title = "本地合成诊断",
                SubtitlesDisabled = true,
                PlayerOptions =
                [
                    new("mute", "yes"),
                    new("loop-file", "inf"),
                    new("audio-file-auto", "no"),
                    new("sub-auto", "no"),
                    new("access-references", "no"),
                    new("load-unsafe-playlists", "no"),
                    new("demuxer-lavf-o", "protocol_whitelist=file")
                ]
            };

            for (var round = 1; round <= 2; round++)
            {
                Write($"第 {round} 轮起播");
                handle = await backend.StartAsync(request, token);
                var control = handle as IPlayerControl
                    ?? throw new InvalidOperationException("libmpv 未提供控制接口");
                var playbackExit = handle.WaitForExitAsync(CancellationToken.None);
                await UntilAsync(() => control.Status.Loaded && surface.HasAttachedVisual,
                    "文件已加载且真实 surface/brush/子 Visual 已挂树");
                var tracks = await handle.GetTracksAsync(token);
                Check(tracks.Any(track => track.Type == "video" && track.Selected), "存在选中视频轨");
                await AdvanceAsync("起播", 2);

                foreach (var size in new[] { (Width: 800, Height: 500), (Width: 1120, Height: 700) })
                {
                    var before = surface.Size;
                    Check(Native.SetWindowPos(window.Handle, IntPtr.Zero, 0, 0, size.Width, size.Height,
                        Native.SwpNoMove | Native.SwpNoZOrder | Native.SwpNoActivate), "窗口 Resize 请求成功");
                    await UntilAsync(() => surface.Size != before && surface.HasAttachedVisual,
                        "Resize 后尺寸改变且 Visual 仍挂树");
                    await Task.Delay(250, token); // Allow the production geometry debounce to fire.
                    var actual = surface.Size;
                    var mpvSize = await handle.GetTextAsync("d3d11-composition-size", token);
                    Check(mpvSize == $"{actual.Width}x{actual.Height}",
                        $"尺寸传到 mpv：surface={actual}，mpv={mpvSize}");
                    await AdvanceAsync("Resize 后", 1);
                }

                // 全屏往返（2026-09-17 用户报「全屏和退出的时候会卡一下」）。上面那两次窗口化 Resize 只证明
                // 了「拖着改尺寸能传到 mpv」；全屏走的是另一条路 —— 换样式位 + SetWindowPos 覆盖整块屏 ——
                // 而画面缩在左上角一小块，读出来只可能是其中之一：宿主没跟着窗口长大，或者合成器画的还是旧
                // 尺寸，或者 mpv 还没重建交换链。三样都量（客户区、宿主、视觉），外加 mpv 那一侧报的合成尺寸，
                // 谁没跟上就写在报告里。
                var windowed = surface.Size;
                var watch = Stopwatch.StartNew();
                var trace = new List<string>();

                window.Fullscreen = true;
                await UntilAsync(() => surface.Size != windowed, "全屏后宿主尺寸改变");

                // 尺寸传对了不等于看见的那一帧是对的：窗口长大和 mpv 重建交换链之间隔着几百毫秒，中间那几帧
                // 屏幕上是什么样，只有屏幕自己说得清。逐拍取三点 —— 右下八分之七（画面铺满时它有内容，画面
                // 缩在左上角一小块时它还是页面底色）、正中间（两种情形都在画面里，做对照）、左上八分之一。
                // 只把「读数变了」的那几拍写进报告，过渡有多长一眼能读出来。
                for (var tick = 0; tick <= 30; tick++)
                {
                    var line = SampleFrame(window, watch.ElapsedMilliseconds, surface.Size);
                    if (trace.Count == 0 || trace[^1][(trace[^1].IndexOf(' ') + 1)..] != line[(line.IndexOf(' ') + 1)..])
                        trace.Add($"进全屏 {line}");
                    await Task.Delay(50, token);
                }

                Write(string.Join(" | ", trace));

                var full = surface.Size;
                Native.GetClientRect(window.Handle, out var fullClient);
                Check(full.Width == fullClient.Width && full.Height == fullClient.Height,
                    $"全屏后宿主跟上客户区：客户区 {fullClient.Width}×{fullClient.Height}，宿主 {full.Width}×{full.Height}"
                        + $"（{watch.ElapsedMilliseconds}ms 后量到），视觉 {surface.VisualSize.Width:0}×{surface.VisualSize.Height:0} DIP");
                var fullMpv = await handle.GetTextAsync("d3d11-composition-size", token);
                Check(fullMpv == $"{full.Width}x{full.Height}",
                    $"全屏尺寸传到 mpv：surface={full}，mpv={fullMpv}");

                // 交换链能不能被合成器缩放 —— 2026-09-17「全屏卡一下」该往哪边修就看这一条。把缓冲故意缩到
                // 窗口的一半：屏幕右下方还有画面内容，说明合成器把交换链缩放铺满了（那只要让 SpriteVisual
                // 一直是宿主尺寸，过渡期就是「旧帧被拉大」而不是「缩在左上角一小块」），只剩页面底色，说明
                // 交换链只会被一比一贴上去，唯一的出路就是让尺寸别再变来变去。实验做完把尺寸放回去。
                //
                // 同一次实验顺带量出「mpv 接受新尺寸到画面上生效」的时间：窗口不动、防抖不参与，逐 30ms 看
                // 右下角什么时候从画面内容变成页面底色 —— 那一段是这条路的下限，省不掉。
                var small = Stopwatch.StartNew();
                await handle.SetPropertyAsync("d3d11-composition-size", "1280x720", token);
                var shrinkAt = -1L;
                for (var tick = 0; tick <= 30; tick++)
                {
                    var line = SampleFrame(window, small.ElapsedMilliseconds, surface.Size);
                    if (shrinkAt < 0 && line.Contains("#16181C/#16181C", StringComparison.Ordinal)) shrinkAt = small.ElapsedMilliseconds;
                    if (tick % 5 == 0) Write($"半尺寸实验 {line}（窗口客户区 {full.Width}×{full.Height}，缓冲 1280×720）");
                    await Task.Delay(30, token);
                }

                Write($"半尺寸实验：缓冲缩到 1280×720 后，画面从右下角退出去用了 {shrinkAt}ms（窗口没动、防抖没参与）");
                await handle.SetPropertyAsync("d3d11-composition-size", $"{full.Width}x{full.Height}", token);
                await Task.Delay(500, token);
                Write($"尺寸放回 {SampleFrame(window, 0, surface.Size)}");

                await AdvanceAsync("全屏后", 1);

                watch.Restart();
                trace.Clear();
                window.Fullscreen = false;
                await UntilAsync(() => surface.Size != full, "退出全屏后宿主尺寸改变");

                for (var tick = 0; tick <= 30; tick++)
                {
                    var line = SampleFrame(window, watch.ElapsedMilliseconds, surface.Size);
                    if (trace.Count == 0 || trace[^1][(trace[^1].IndexOf(' ') + 1)..] != line[(line.IndexOf(' ') + 1)..])
                        trace.Add($"退全屏 {line}");
                    await Task.Delay(50, token);
                }

                Write(string.Join(" | ", trace));

                var restored = surface.Size;
                Native.GetClientRect(window.Handle, out var backClient);
                Check(restored == windowed && restored.Width == backClient.Width && restored.Height == backClient.Height,
                    $"退出全屏后宿主回到窗口化尺寸：客户区 {backClient.Width}×{backClient.Height}，宿主 {restored.Width}×{restored.Height}"
                        + $"（{watch.ElapsedMilliseconds}ms 后量到），视觉 {surface.VisualSize.Width:0}×{surface.VisualSize.Height:0} DIP");
                await AdvanceAsync("退出全屏后", 1);

                await handle.SetPropertyAsync("pause", true, token);
                await UntilAsync(() => control.Status.Paused, "暂停状态已生效");
                await Task.Delay(250, token);
                var pausedAt = await PositionAsync();
                await Task.Delay(1000, token);
                Check(Math.Abs(await PositionAsync() - pausedAt) < 0.15 && surface.HasAttachedVisual,
                    "暂停一秒位置稳定，Visual 保留");
                await handle.SetPropertyAsync("pause", false, token);
                await UntilAsync(() => !control.Status.Paused, "恢复状态已生效");
                await AdvanceAsync("恢复并留屏观察", 3);

                await handle.StopAsync();
                var exit = await playbackExit.WaitAsync(TimeSpan.FromSeconds(5), token);
                Check(exit.Reason == PlaybackEndReason.Stopped && !exit.IsFailure,
                    $"停止结果：{exit.Reason}，code={exit.ExitCode}");
                await handle.DisposeAsync();
                handle = null;
                await UntilAsync(() => !surface.HasAttachedVisual, "停止后 Visual 已摘树");

                async Task<double> PositionAsync() =>
                    await handle!.GetPositionAsync(token) is { } value && double.IsFinite(value)
                        ? value : throw new InvalidOperationException("无法读取有效播放位置");

                async Task AdvanceAsync(string stage, int seconds)
                {
                    var previous = await PositionAsync();
                    for (var sample = 0; sample < seconds; sample++)
                    {
                        await Task.Delay(1000, token);
                        var current = await PositionAsync();
                        // loop-file wraps short fixtures back to zero; forward progress across that
                        // boundary still needs a known duration and a plausible elapsed interval.
                        var delta = current - previous;
                        if (delta < 0 && control.Status.Duration > 0) delta += control.Status.Duration;
                        Check(!playbackExit.IsCompleted && surface.HasAttachedVisual && delta > 0.25,
                            $"{stage}：position {previous:F3} → {current:F3}，surface={surface.Size}");
                        previous = current;
                    }
                }
            }
            passed = true;
        }
        catch (Exception error)
        {
            Write($"失败：{error}");
            Log.Error("本地合成诊断", "验证失败", error);
        }
        finally
        {
            try
            {
                if (handle is not null)
                {
                    try { await handle.StopAsync(); }
                    finally { await handle.DisposeAsync(); }
                }
                page?.ReleaseVideoSurface();
                if (window is not null)
                {
                    window.Closed -= stopping.Cancel;
                    window.Dispose();
                }
            }
            catch (Exception error)
            {
                passed = false;
                Write($"清理失败：{error}");
            }
            ExitCode = passed ? 0 : 1;
            Write(passed ? "结果：自动断言通过；画面和 OSD 未作像素判定，仍需目视确认。" : "结果：失败");
            report?.Dispose();
            Application.Current.Exit();
        }

        void Write(string message)
        {
            Log.Info("本地合成诊断", message);
            report?.WriteLine($"{DateTimeOffset.Now:O} {message}");
        }

        void Check(bool condition, string message)
        {
            Write($"{(condition ? "通过" : "失败")}：{message}");
            if (!condition) throw new InvalidOperationException(message);
        }

        async Task UntilAsync(Func<bool> ready, string message)
        {
            var clock = Stopwatch.StartNew();
            while (!ready())
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(message);
                await Task.Delay(100, token);
            }
            Write($"通过：{message}");
        }
    }

    /// <summary>
    /// One frame of the screen as three readings — the colours at 1/8, 1/2 and 7/8 of the window's client
    /// area, with the client and host sizes beside them.
    /// <para>
    /// The far corner is the one that tells the story: with the picture filling the window it holds picture
    /// content, and while the picture is still a block in the top-left it holds the page's own background.
    /// The middle is the control — it is inside the picture either way — and the top-left corner holds content
    /// either way too. Screen pixels rather than a screenshot because a composition SpriteVisual is not part
    /// of the tree a <c>RenderTargetBitmap</c> draws.
    /// </para>
    /// </summary>
    private static string SampleFrame(HostWindow window, long milliseconds, (int Width, int Height) host)
    {
        if (!Native.GetClientRect(window.Handle, out var client)) return $"{milliseconds}ms 量不到客户区";

        var device = Native.GetDC(IntPtr.Zero);
        if (device == IntPtr.Zero) return $"{milliseconds}ms 取不到屏幕 DC";

        try
        {
            var spots = new (int X, int Y)[]
            {
                (client.Width / 8, client.Height / 8),
                (client.Width / 2, client.Height / 2),
                (client.Width * 7 / 8, client.Height * 7 / 8),
            };

            var colors = new List<string>();
            foreach (var (x, y) in spots)
            {
                var point = new NativePoint { X = x, Y = y };
                Native.ClientToScreen(window.Handle, ref point);
                var color = Native.GetPixel(device, point.X, point.Y);
                colors.Add(color == 0xFFFFFFFF
                    ? "?"
                    : $"#{(color & 0xFF):X2}{((color >> 8) & 0xFF):X2}{((color >> 16) & 0xFF):X2}");
            }

            return $"{milliseconds}ms 客户区{client.Width}x{client.Height} 宿主{host.Width}x{host.Height} "
                + string.Join("/", colors);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, device);
        }
    }

    internal static Uri LocalFile(string? path)
    {
        // Accept ordinary absolute drive paths only, not URI/UNC/device/drive-relative forms.
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsAsciiLetter(path[0])
            || path[1] != ':' || path[2] is not ('\\' or '/') || !Path.IsPathFullyQualified(path)
            || path.IndexOf(':', 2) >= 0)
            throw new ArgumentException("--probe-composition 必须指定绝对本地文件，拒绝 URL、UNC 和设备路径");
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path)!;
        if (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            throw new ArgumentException("诊断只接受本地磁盘，拒绝网络映射盘");

        // Walk root-first so a junction/symlink cannot silently redirect validation onto a share.
        var current = root;
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("诊断文件路径不能经过符号链接或重解析点");
        }
        if (!File.Exists(path)) throw new FileNotFoundException("诊断文件不存在", path);
        return new Uri(path, UriKind.Absolute);
    }
}
