using System.Diagnostics;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using EmbyNian.Shell.Composition;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Views;
using EmbyNian.Shell.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell;

/// <summary>隔离的真实播放页动效诊断；本地测试视频直达后端，不登录、不上报、不改用户设置。</summary>
internal static class PlayerMotionProbe
{
    internal static int ExitCode { get; private set; } = 1;

    internal static bool IsRequested(string[] args) => args.Any(argument =>
        argument.TrimStart('-', '/').Split('=', 2)[0]
            .Equals("probe-player-motion", StringComparison.OrdinalIgnoreCase));

    /// <summary>报告初始化也在保护之内：哪怕建不了报告文件，也要走 finally 里的保证退出。</summary>
    internal static async Task RunAsync(StartupOptions options)
    {
        var foreground = Native.GetForegroundWindow();
        try
        {
            await RunScoped(options);
        }
        finally
        {
            if (foreground != IntPtr.Zero) Native.SetForegroundWindow(foreground);
            Application.Current.Exit();
        }
    }

    private static async Task RunScoped(StartupOptions options)
    {
        Directory.CreateDirectory(options.Paths.LogDirectory);
        using var report = new StreamWriter(Path.Combine(options.Paths.LogDirectory, "player-motion-probe.txt"))
            { AutoFlush = true };
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var token = stopping.Token;
        HostWindow? window = null;
        ShellPage? shell = null;
        ServiceProvider? services = null;
        IPlaybackHandle? handle = null;
        try
        {
            var media = CompositionPlaybackProbe.LocalFile(options.ProbePlayerMotionFile);
            services = ShellServices.Build(options.Paths);
            services.GetRequiredService<AppSettings>().Mpv.Pipeline = VideoPipelineKind.Integrated;
            shell = services.GetRequiredService<ShellPage>();
            shell.Attach(services);
            window = new HostWindow { Content = shell };
            window.Closed += stopping.Cancel;
            window.Show(false, options.Screen, new WindowBounds(120, 120, 1240, 880));
            shell.AttachWindow(window);
            var page = shell.PlayerRoot;
            var surface = (CompositionVideoTarget)page.VideoSurface!;
            var layoutSample = window.Content!.DispatcherQueue.CreateTimer();
            layoutSample.Interval = TimeSpan.FromMilliseconds(50);
            layoutSample.Tick += (_, _) =>
            {
                if (!page.PlayerVisible) return;
                var root = (FrameworkElement)page.FindName("Root");
                var cover = (FrameworkElement)page.FindName("Cover");
                var ring = (FrameworkElement)page.FindName("CoverRing");
                var at = ring.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point());
                Write($"几何取样：client={window.ClientSize} root={root.ActualWidth:0}x{root.ActualHeight:0}"
                    + $" cover={cover.ActualWidth:0}x{cover.ActualHeight:0} ring={at.X:0},{at.Y:0} content={surface.AttachedContentSize}");
            };
            page.ViewModel.Title = "本地动画验证";
            page.ViewModel.CoverMessage = "正在准备本地测试画面…";
            var artPath = Path.ChangeExtension(media.LocalPath, ".png");
            if (File.Exists(artPath)) page.ViewModel.CoverBackdrop = new BitmapImage(new Uri(artPath));
            await Task.Delay(600, token);
            window.CaptureBrowseGeometry();
            var browse = window.BrowseBounds;
            Write($"隔离窗口 PID={Environment.ProcessId} HWND=0x{window.Handle:X}；本地素材={media.LocalPath}");
            Write("未调用登录、PlaybackService.Play 或任何服务器播放上报；用真实页面的进退方法与窗口命令。");
            var transitions = 0;
            window.ClientRectTransition += (_, _) => transitions++;

            Write("阶段：窗口化进场");
            page.ViewModel.CoverUp = true;
            page.BeginMotionProbe(false);
            await SampleMotion("进场");
            Require(page.MotionProbeState.Active && page.MotionProbeState.Identity && window.PlaybackTitleBar,
                "进场结束无残留变换，标题栏已交给播放器");

            // 2026-09-19 用户报「进度条被裁切」的那条竞态，探针此前一直没盖住：真实播放里服务器回报的
            // 画面比例总在进场 280ms 的中途到达，FitToPicture 当场把窗口缩掉几百像素；这一拍落在还挂着
            // 驱动的轨道上，旧版就把整页留在了 Entering 姿势（Scale 1.018、Y+12）——底部控制条被推出
            // 窗外 19~25px。这里在进场的头三分之一处照演一遍，落定后变换必须回到 Identity。
            Write("阶段：进场中途按画面比例改窗口");
            page.ViewModel.CoverUp = true;
            page.BeginMotionProbe(false);
            await Task.Delay(90, token);
            window.PictureAspect = 2.0;
            window.FitToPicture();
            await Task.Delay(450, token);
            Require(page.MotionProbeState.Active && page.MotionProbeState.Identity,
                "进场中途改窗口尺寸后无残留缩放/位移");
            window.PictureAspect = 0;
            window.RestoreBrowseGeometry();

            Write("阶段：尚未加载时最大化／还原");
            layoutSample.Start();
            window.ToggleMaximize();
            await Task.Delay(360, token);
            Require(window.IsMaximized && page.MotionProbeState.Identity, "无视频最大化落定");
            window.ToggleMaximize();
            await Task.Delay(360, token);
            Require(!window.IsMaximized && page.MotionProbeState.Identity, "无视频还原落定");
            layoutSample.Stop();

            var backend = new LibMpvBackend(new MpvSettings { Pipeline = VideoPipelineKind.Integrated }, () => surface);
            handle = await backend.StartAsync(new PlaybackRequest
            {
                MediaUrl = media,
                Title = "本地动画验证",
                SubtitlesDisabled = true,
                PlayerOptions =
                [
                    new("mute", "yes"), new("loop-file", "inf"),
                    new("audio-file-auto", "no"), new("sub-auto", "no"),
                    new("access-references", "no"), new("load-unsafe-playlists", "no"),
                    new("demuxer-lavf-o", "protocol_whitelist=file")
                ]
            }, token);
            var control = (IPlayerControl)handle;
            await Until(() => control.Status.Loaded && surface.IsContentReady, "实际视频缓冲尺寸追上宿主");
            Write($"缓冲={surface.AttachedContentSize}，宿主={surface.Size}");
            page.ViewModel.CoverUp = false;
            await Task.Delay(350, token);
            Require(page.MotionProbeState.CoverHidden, "就绪后加载遮罩完全退场");

            foreach (var stage in new[] { "最大化", "还原", "最大化", "还原" })
            {
                Write($"阶段：播放中{stage}");
                var before = transitions;
                window.ToggleMaximize();
                await Until(() => surface.IsContentReady && !page.MotionProbeState.Animating, $"{stage}后缓冲与动效落定");
                Require(transitions == before + 1, $"{stage}仅发布一个几何终点");
                Require(page.MotionProbeState.Identity, $"{stage}后页面无残留缩放");
                await Task.Delay(450, token);
            }

            Write("阶段：快速全屏往返");
            page.SetFullscreen(true);
            await Task.Delay(65, token);
            page.SetFullscreen(false);
            await Task.Delay(65, token);
            page.SetFullscreen(true);
            await Until(() => surface.IsContentReady && !page.MotionProbeState.Animating, "快速往返最终全屏正确");
            await Task.Delay(500, token);
            page.SetFullscreen(false);
            await Until(() => surface.IsContentReady && !page.MotionProbeState.Animating, "快速往返最终窗口化正确");

            Write("阶段：最大化进全屏再退出");
            window.ToggleMaximize();
            await Task.Delay(350, token);
            page.SetFullscreen(true);
            await Task.Delay(350, token);
            page.SetFullscreen(false);
            await Task.Delay(350, token);
            Require(window.IsMaximized, "最大化状态在全屏往返后保留");

            Write("阶段：退出播放");
            await handle.StopAsync();
            await handle.DisposeAsync();
            handle = null;
            await Task.Delay(60, token);
            Require(!surface.HasAttachedVisual && surface.AttachedContentSize.Width > 0,
                "播放已停止，退场保留最后视频帧");
            page.EndMotionProbe();
            await Task.Delay(350, token);
            Require(!page.PlayerVisible && page.MotionProbeState.Identity && !window.PlaybackTitleBar,
                "退场后页面收起，变换和标题栏恢复");
            Require(surface.AttachedContentSize.Width == 0, "退场后最后帧引用已释放");
            Native.GetWindowRect(window.Handle, out var restored);
            Require(!window.IsMaximized && restored.Left == browse.Left && restored.Top == browse.Top
                && restored.Width == browse.Width && restored.Height == browse.Height, "退出回到播放前的浏览几何");

            Write("阶段：进场半途退出再重进");
            page.ViewModel.CoverUp = true;
            page.BeginMotionProbe(false);
            await Task.Delay(70, token);
            page.EndMotionProbe();
            await Task.Delay(65, token);
            page.BeginMotionProbe(false);
            await Task.Delay(450, token);
            Require(page.PlayerVisible && page.MotionProbeState.Active && page.MotionProbeState.Identity,
                "反向重入没有被旧完成回调收掉");
            page.EndMotionProbe();
            await Task.Delay(350, token);

            Write("阶段：加载页直接进入全屏再退出");
            layoutSample.Start();
            page.BeginMotionProbe(true);
            await Task.Delay(500, token);
            Require(window.Fullscreen && page.MotionProbeState.Identity, "直接全屏进场没有第二段缩放");
            page.EndMotionProbe();
            await Task.Delay(400, token);
            Require(!window.Fullscreen && !page.PlayerVisible && page.MotionProbeState.Identity,
                "无视频全屏退场清理完整");
            Native.GetWindowRect(window.Handle, out restored);
            Require(restored.Left == browse.Left && restored.Top == browse.Top
                && restored.Width == browse.Width && restored.Height == browse.Height,
                $"重复进出仍恢复浏览尺寸：{restored.Width}x{restored.Height}，预期 {browse.Width}x{browse.Height}");
            layoutSample.Stop();

            // 压轴：真实用户点播放的那条路 —— 集成管线＋自动全屏，跳窗挂在进场淡入完成那一拍
            // （EnterPlayer 第 0 拍只溶解、不动窗口）。放在所有阶段之后，免得这一进一退把
            // 「退出保留最后帧」那类依赖在台状态的断言搅进来。
            Write("阶段：进场淡入完成拍自动全屏");
            services.GetRequiredService<AppSettings>().Playback.AutoFullscreenOnPlayback = true;
            page.ViewModel.ProbeHoldPlayback();
            page.ViewModel.CoverUp = true;
            page.BeginMotionProbe(false);
            await Until(() => window.Fullscreen, "淡入完成那一拍窗口已全屏（跳窗不再排在进场的第 0 拍）");
            Require(page.MotionProbeState.Active, "全屏换手时页面仍在台上");
            await Task.Delay(320, token);
            Require(page.MotionProbeState.Identity, "自动全屏进场落定无残留变换");
            page.EndMotionProbe();
            await Task.Delay(400, token);
            Require(!window.Fullscreen && !page.PlayerVisible && page.MotionProbeState.Identity,
                "自动全屏进场的退场完整，回到窗口化");
            services.GetRequiredService<AppSettings>().Playback.AutoFullscreenOnPlayback = false;
            ExitCode = 0;
            Write("结果：全部通过");

            async Task SampleMotion(string stage)
            {
                var transform = (CompositeTransform)page.RenderTransform;
                var samples = new List<double>();
                for (var index = 0; index < 6; index++)
                {
                    await Task.Delay(45, token);
                    samples.Add(transform.ScaleX);
                    Write($"{stage}取样：Opacity={page.Opacity:F4} Scale={transform.ScaleX:F5} Y={transform.TranslateY:F3}");
                }
                await Task.Delay(100, token);
                if (HomeMotion.AnimationsEnabled)
                    Require(samples.Any(scale => scale > 1.0001 && scale < PlayerMotion.DepthScale - 0.0001),
                        "屏上属性确有中间帧，不是计时器结束后才归位");
            }
        }
        catch (Exception error)
        {
            Write($"结果：失败：{error}");
        }
        finally
        {
            try
            {
                if (handle is not null)
                {
                    await handle.StopAsync();
                    await handle.DisposeAsync();
                }
                shell?.Shutdown();
                if (window is not null)
                {
                    window.Closed -= stopping.Cancel;
                    window.Dispose();
                }
                if (services is not null) await services.DisposeAsync();
            }
            catch (Exception error)
            {
                ExitCode = 1;
                Write($"清理失败：{error}");
            }
        }

        void Write(string message)
        {
            report.WriteLine($"{DateTimeOffset.Now:O} {message}");
            Log.Info("播放器动效诊断", message);
        }

        void Require(bool condition, string message)
        {
            Write($"{(condition ? "通过" : "失败")}：{message}");
            if (!condition) throw new InvalidOperationException(message);
        }

        async Task Until(Func<bool> condition, string message)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds > 6000) throw new TimeoutException(message);
                await Task.Delay(40, token);
            }
            Write($"通过：{message}");
        }
    }
}
