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
internal static partial class CompositionPlaybackProbe
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
        // 保险丝，不是判据：2026-09-22 加了「缩放定律」一节（两轮各约 5 秒），把上限从 60 提到 120，
        // 免得它抢在实验做完之前到点。任何一条真实的等待超时都不该靠它兜住。
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(120));
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
            window.TopMost = true;
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

                Write("阶段：缩放定律（窗口完全不动，分辨 SpriteVisual 的 Size 与 Scale）");
                await ScalingLawAsync(surface, handle, window, token, Write);

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

    /// <summary>
    /// 缩放定律实验（2026-09-22）：<b>窗口完全不动</b>，只改 mpv 的 composition-size 与 SpriteVisual 的
    /// 摆放三件套，逐段量屏幕上那条扫描线。
    /// <para>
    /// <b>为什么要在窗口不动的前提下做。</b>用户报「集成模式放大窗口时画面短时不能铺满、缩小时被裁切」。
    /// 屏幕上那块画面只有一条路：mpv 的交换链 → CompositionSurfaceBrush → SpriteVisual。所以「旧缓冲怎么
    /// 填进新客户区」完全取决于合成器认不认 <c>Size</c> 与 <c>Scale</c>。生产代码把两者按同一个假设算出来
    /// （Size＝目标矩形、Scale＝1），自己永远得不出反证；而拖动窗口那几拍里又同时混着 XAML 布局、岛裁剪和
    /// mpv 重建三件事。这里把窗口钉死，只动缓冲与摆放，读到的差异就只可能来自缩放律本身。
    /// </para>
    /// <para>
    /// 读法：素材是 lavfi 六条等宽彩条（各占画面 1/6），所以「彩条铺到哪儿、分几段」直接把画面在窗口里
    /// 占了多少读出来。<b>本节只测不判</b> —— 结论先落进报告，判据等定律明确之后再写。
    /// </para>
    /// </summary>
    private static async Task ScalingLawAsync(CompositionVideoTarget target, IPlaybackHandle playback,
        HostWindow window, CancellationToken token, Action<string> write)
    {
        // 位置：**不写死坐标**。2026-09-22 实测的教训 —— 这台机器的第二块屏原点不是 (0,0)，写死 (0,0)
        // 会把隔离窗口挪到**主屏**上去（那一轮 ①② 读到的全是桌面颜色，还顺带把后面的全屏也带到了主屏）。
        // 改成问窗口自己那块显示器的矩形，并抬到最上层：整块客户区都落在屏上、也不被别的窗口盖住。
        //
        // 1024×576 是这块副屏（1080 宽）上还放得下的 16:9 窗口。窗口是 16:9、素材也是 16:9、交换链又按
        // 客户区建，于是「缓冲 == 画面」，彩条边界就是画面边界，没有信箱黑边遮着，读法才干净。
        const int lawWidth = 1024;
        const int lawHeight = 576;
        var monitor = VideoFrameOverlay.FullscreenRect(window.Handle);
        Native.SetWindowPos(window.Handle, Native.HwndTop, monitor.Left, monitor.Top,
            Math.Min(lawWidth, monitor.Right - monitor.Left), Math.Min(lawHeight, monitor.Bottom - monitor.Top),
            Native.SwpNoActivate);
        var settle = Stopwatch.StartNew();
        while (!target.IsContentReady && settle.ElapsedMilliseconds < 4000) await Task.Delay(50, token);
        await Task.Delay(250, token);

        if (!Native.GetClientRect(window.Handle, out var client) || client.Width <= 0 || client.Height <= 0)
        {
            write("缩放定律：量不到客户区，跳过");
            return;
        }

        Native.GetWindowRect(window.Handle, out var lawWindow);
        var raster = target.RasterizationScale;
        write($"缩放定律：显示器 {monitor.Width}×{monitor.Height}@{monitor.Left},{monitor.Top}，"
            + $"窗口 {lawWindow.Width}×{lawWindow.Height}@{lawWindow.Left},{lawWindow.Top}，"
            + $"客户区 {client.Width}×{client.Height}（raster={raster:0.###}），"
            + $"缓冲={target.AttachedContentSize}，缓冲已追上={target.IsContentReady}");

        var fullWidth = client.Width / raster;
        var fullHeight = client.Height / raster;

        // ① 基准：缓冲＝客户区，视觉＝整块客户区（这就是生产代码在稳定期的写法）。
        //    六条彩条必须铺满整条横线 —— 这一拍同时充当读法与窗位的合法性检查。
        target.PinProbePlacement(0, 0, fullWidth, fullHeight, 1);
        await Task.Delay(150, token);
        Law("① 基准（缓冲＝客户区，视觉＝客户区，Scale=1）");

        // ② 关键一拍：把缓冲缩到一半、**摆放一点都不改**。若彩条仍旧铺满整条横线，说明合成器把
        //    半尺寸的交换链拉伸进了视觉矩形（那生产代码在拖动期就是对的，问题在别处）；若彩条只占
        //    左半边、其余是页面底色，说明合成器把交换链一比一贴上去 —— 拖动时那几拍看到的就是这个。
        var halfWidth = Math.Max(1, client.Width / 2);
        var halfHeight = Math.Max(1, client.Height / 2);
        await playback.SetPropertyAsync("d3d11-composition-size", $"{halfWidth}x{halfHeight}", token);
        await Task.Delay(900, token);
        Law($"② 缓冲缩到 {halfWidth}×{halfHeight}，视觉仍是整块客户区（Scale=1）—— 本条就是「铺不满/被裁切」的现场");

        // ② 之后有个坑要写下来：此刻读到的 AttachedContentSize 仍是**整块交换链** 1008×568。直接改
        //    d3d11-composition-size 时，mpv 把画面按新尺寸画进同一块缓冲的左上角，交换链描述符并不跟着
        //    变（真实拖动那条路不同：它会重挂交换链，描述符跟着走）。所以「画面此刻到底多大」从这里往下
        //    一律按**我们要求的那一半**算 —— 第一版这里用了描述符，于是 ④ 的 Scale 被算成了 1，白跑一趟。
        var ratio = (double)client.Width / halfWidth;

        // ③ 对照：把视觉钉成「要求的那一半」。它与 ② 只差一个视觉矩形 —— 两条读数若一模一样，说明
        //    SpriteVisual.Size 对画面像素毫无影响。
        var smallWidth = halfWidth / raster;
        var smallHeight = halfHeight / raster;
        target.PinProbePlacement(0, 0, smallWidth, smallHeight, 1);
        await Task.Delay(150, token);
        Law("③ 视觉＝要求的那一半（Size=半缓冲/raster，Scale=1）");

        // ④ 关键：**只加 Scale**。若这一拍铺满了，说明「像素缩放」这件事只有 Scale 干得动 ——
        //    拖动期的修法就是「Offset＋Scale 摆到目标矩形」，而不是写 Size。
        target.PinProbePlacement(0, 0, smallWidth, smallHeight, ratio);
        await Task.Delay(150, token);
        Law($"④ ③ 再乘 Scale={ratio:0.###}（Offset 仍为 0）");

        // ④b 缩放中心：Composition 的 Visual 默认 CenterPoint 若在原点，④ 的画面应当从客户区左上角起；
        //     若在 Size 中心，它会被朝左上推出去 Size×(Scale-1)/2。补一个把这段推出去的偏移再看一眼，
        //     哪一拍从 x=0 起，哪一拍的 CenterPoint 语义就是答案 —— 修法的 Offset 算法取决于此。
        target.PinProbePlacement(smallWidth * (ratio - 1) / 2, smallHeight * (ratio - 1) / 2,
            smallWidth, smallHeight, ratio);
        await Task.Delay(150, token);
        Law("④b ④ 再加 Size×(Scale-1)/2 的偏移（若这一拍才从左上角起，则 CenterPoint 在原点）");

        // ⑤ 位移：Scale 之外的另一半是 Offset。把画面朝右下挪半格（半缓冲的一半），读数应当整体右移 ——
        //    这是「按画面比例居中摆进宿主」（contain）那条路要用的能力。
        target.PinProbePlacement(halfWidth / 2 / raster, halfHeight / 2 / raster, smallWidth, smallHeight, 1);
        await Task.Delay(150, token);
        Law("⑤ 位移：Size=半缓冲、Scale=1，Offset=(半缓冲/2)");

        // ④c 另一条路线：画笔自己的 Stretch=None ＋ TransformMatrix 纯缩放（文档里「不让画笔替我缩放」的
        //     写法）。若视觉的 Scale 不管用而这一拍铺满了，修法就改成钉画笔矩阵。
        target.PinProbeBrushScale(ratio);
        await Task.Delay(150, token);
        Law($"④c 画笔路线：Stretch=None ＋ TransformMatrix=Scale({ratio:0.###})");

        // ⑥ 还原：放掉钉子、把缓冲换回客户区尺寸，后面那一段（暂停/恢复）照旧。
        target.ReleaseProbePlacement();
        await playback.SetPropertyAsync("d3d11-composition-size", $"{client.Width}x{client.Height}", token);
        await Task.Delay(900, token);
        Law("⑥ 还原（放掉钉子，缓冲换回客户区）");

        void Law(string stage)
        {
            target.RefreshPresentation();
            var placed = target.PlacedRect;
            write($"  {stage}：摆放 {placed.Left:0},{placed.Top:0} {placed.Width:0}×{placed.Height:0}"
                + $"，钉住={target.IsProbePinned}，缓冲={target.AttachedContentSize}"
                + $"｜横(x=8,25,42,50,58,75,92%) {Line(window, [0.08, 0.25, 0.42, 0.50, 0.58, 0.75, 0.92], 0.4, true)}"
                + $"｜竖(y=10,25,40,55,70,85%) {Line(window, [0.10, 0.25, 0.40, 0.55, 0.70, 0.85], 0.25, false)}");
        }
    }

    /// <summary>
    /// 客户区里一组探针点的颜色读数。素材是六条<b>等宽</b>彩条，所以横向那七点（各条中心加两端）一眼就
    /// 能读出「画面铺到哪儿、有没有被放大」：铺满整个客户区时是 <c>RGY?BMC</c>（25% 落在绿条中心）；
    /// 只占左半边时同一串会挤成 <c>RYMC...</c>，而右半边全是页面底色 <c>.</c>。
    /// <para>
    /// <b>只取十几个点、不扫整条线</b>：本机实测 <c>GetPixel</c> 约 24ms 一次（每问一次就同步一次呈现），
    /// 扫一条 1024 宽的线要十秒，会把整段实验拖到超时。字母含义见 <see cref="ColorLetter"/>。
    /// </para>
    /// <para><paramref name="horizontal"/> 为真时 <paramref name="at"/> 是 y，否则是 x。</para>
    /// </summary>
    internal static string Line(HostWindow window, double[] positions, double at, bool horizontal)
    {
        var frame = CapturePixels(window);
        return new string(positions.Select(position =>
            horizontal ? frame.At(position, at) : frame.At(at, position)).ToArray());
    }

    /// <summary>六条彩条的中心各取多点，以多数颜色避开素材里移动的对角线和时间码。</summary>
    internal static string FrameBars(HostWindow window)
    {
        var frame = CapturePixels(window);
        double[] rows = [0.18, 0.3, 0.42, 0.58, 0.7, 0.82];
        return new string(Enumerable.Range(0, 6).Select(index => rows
            .Select(y => frame.At((index + 0.5) / 6, y))
            .GroupBy(color => color).OrderByDescending(group => group.Count()).First().Key).ToArray());
    }

    /// <summary>
    /// 拖动窗口时「画面有没有真的铺到边上」的四点读数（横向 8% / 25% / 75% / 92%，y=40%）。
    /// <para>
    /// <b>最会说话的是 92% 那一点。</b>素材是六条等宽彩条，所以：
    ///  · 缓冲比客户区小（放大时还没追上）→ 那里是页面底色 <c>.</c>，正是用户报的「铺不满」；
    ///  · 缓冲比客户区大（缩小时还没追上）→ 那里是更靠左的一根彩条（比如 <c>M</c>），正是「被裁切」；
    ///  · 旧缓冲被正确缩小/放大到填满 → 那里是最后一根彩条 <c>C</c>（或对角线/过渡像素 <c>#</c>）。
    /// </para>
    /// <para>
    /// 只取四个点：<c>GetPixel</c> 一次约 24ms，点多这一拍就会跨过 mpv 追上来的那 110~150ms，
    /// 读到的就不是「旧缓冲还在」那一刻了。
    /// </para>
    /// </summary>
    internal static string EdgeRead(HostWindow window) => Line(window, [0.08, 0.25, 0.75, 0.92], 0.4, true);

    /// <summary>读数里有没有「空白」——页面底色或信箱黑边。有就是画面没铺到那儿。</summary>
    internal static bool HasBlank(string read) => read.Contains('.') || read.Contains('K');

    /// <summary>
    /// 单点颜色读数。给「逐拍看画面什么时候铺到边」这种便宜曲线用：<c>GetPixel</c> 一次约 24ms，
    /// 点上两点仍然只有五十来毫秒，够贴着一拍一拍看，而 <see cref="Line"/> 那种多点读数
    /// 一次要跨过一百多毫秒 —— 那正是 mpv 换完缓冲的时间，曲线就被它自己抹平了。
    /// </summary>
    internal static char PointRead(HostWindow window, double x, double y)
    {
        if (!Native.GetClientRect(window.Handle, out var client) || client.Width <= 0 || client.Height <= 0)
            return '?';

        var device = Native.GetDC(IntPtr.Zero);
        if (device == IntPtr.Zero) return '?';

        try
        {
            var point = new NativePoint
            {
                X = (int)Math.Round((client.Width - 1) * x),
                Y = (int)Math.Round((client.Height - 1) * y)
            };
            return Native.ClientToScreen(window.Handle, ref point)
                ? ColorLetter(Native.GetPixel(device, point.X, point.Y))
                : '?';
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, device);
        }
    }

    /// <summary>读数最右那一点是不是最后一根彩条（或过渡/对角线像素）—— 判断画面有没有被整体缩放对。</summary>
    internal static bool EndsAtLastBar(string read) =>
        read.Length == 4 && (read[3] == 'C' || read[3] == '#');

    /// <summary>把屏幕像素归到素材的六条彩条、信箱黑边与页面底色上；其余一律 <c>#</c>。</summary>
    private static char ColorLetter(uint color)
    {
        if (color == 0xFFFFFFFF) return '?';

        // GetPixel 给的是 COLORREF：低字节是红。
        var red = (int)(color & 0xFF);
        var green = (int)((color >> 8) & 0xFF);
        var blue = (int)((color >> 16) & 0xFF);

        const int on = 150;
        const int off = 90;
        const int dark = 90;

        if (red >= on && green >= on && blue < off) return 'Y';
        if (red >= on && blue >= on && green < off) return 'M';
        if (green >= on && blue >= on && red < off) return 'C';
        if (red >= on && green < off && blue < off) return 'R';
        if (green >= on && red < off && blue < off) return 'G';
        if (blue >= on && red < off && green < off) return 'B';
        // 页面底色（#16181C）比纯黑亮一档，所以先认它再认黑 —— 「信箱黑边」与「底下露出页面」
        // 在这一节里是两件完全不同的事（前者说明画面摆了、只是带黑边；后者说明画面根本没铺到那儿）。
        if (Math.Abs(red - 22) <= 10 && Math.Abs(green - 24) <= 10 && Math.Abs(blue - 28) <= 10) return '.';
        if (red < dark && green < dark && blue < dark) return 'K';
        return '#';
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
