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

            var backend = new LibMpvBackend(new MpvSettings { Pipeline = VideoPipelineKind.Integrated },
                new PlaybackSettings(), () => surface);
            // 真实那条路（PlayerViewModel.PrepareShaderPlans）在这里会报一次片子的码流比例；探针直接
            // new 了后端、绕过了那个入口，所以补上 —— 否则「保留帧按画面真实比例摆放」那条修正
            // 在探针里等于没验。素材是 16:9。
            page.ProbeSourceAspect(16d / 9d);
            var request = new PlaybackRequest
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
            };
            // 2026-09-21 用户报「背景图 → 一段黑屏 → 正片」：遮罩揭得比首帧早。判据是
            // 「屏幕底下还没有真画面（HasPicture 为假）的那几拍里，遮罩必须一直是不透明的」——
            // 旧代码在 file-loaded 那一拍就揭，而首帧在几百毫秒之后，那一段里遮罩已经淡到 0。
            // 这里只读不写：HasPicture 由后端真实的那一位（mpv 的 playback-restart）驱动。
            var coverWatch = window.Content!.DispatcherQueue.CreateTimer();
            coverWatch.Interval = TimeSpan.FromMilliseconds(16);
            var coverTicks = 0;
            var earlyFades = 0;
            var dimmest = 1d;
            var firstPictureTick = -1;
            coverWatch.Tick += (_, _) =>
            {
                if (!page.PlayerVisible) return;
                var state = page.CoverProbeState;
                coverTicks++;
                if (state.HasPicture)
                {
                    if (firstPictureTick < 0) firstPictureTick = coverTicks;
                    return;
                }
                if (state.Opacity < 0.99) earlyFades++;
                dimmest = Math.Min(dimmest, state.Opacity);
            };
            coverWatch.Start();

            handle = await backend.StartAsync(request, token);
            var control = (IPlayerControl)handle;

            // 真实那一路 VM 的规矩就是这一句：file-loaded 且不在缓冲，就把遮罩交出去（HideCover → CoverUp=false），
            // 什么时候真的淡走则由页面按「屏幕底下有没有真画面」定。探针照这条走，验的才是用户看到的那条路。
            await Until(() => control.Status.Loaded, "file-loaded");
            page.ViewModel.CoverUp = false;

            var waited = 0;
            var signalled = false;
            while (waited < 4000 && !(surface.HasPicture && surface.IsContentReady))
            {
                // 「首帧已经交给视频输出」这句从真实后端读（它由 mpv 的 playback-restart 填），再手动递给页面 ——
                // 探针自己 new 了后端，绕过了 PlayerViewModel 那条状态订阅（真实那条路见 OnStatusApplied）。
                if (!signalled && control.Status.PictureStarted)
                {
                    signalled = true;
                    page.ProbePictureStarted();
                    Write($"mpv 报告首帧已交给视频输出（已装载={control.Status.Loaded}）");
                }
                await Task.Delay(50, token);
                waited += 50;
                if (waited % 500 == 0)
                    Write($"等首帧：HasPicture={surface.HasPicture}，押过帧={surface.PresentsSinceArm}，"
                        + $"挂链={surface.HasAttachedVisual}，缓冲={surface.AttachedContentSize}，宿主={surface.Size}，"
                        + $"缓冲已追上宿主={surface.IsContentReady}");
            }
            Require(signalled, "mpv 报告首帧已交给视频输出");
            Require(surface.HasPicture && surface.IsContentReady, "屏幕底下有真画面且缓冲追上宿主");
            Write($"首帧时序：缓冲={surface.AttachedContentSize}，宿主={surface.Size}");

            await Task.Delay(320, token);
            coverWatch.Stop();
            Write($"遮罩时序：揭遮罩之后取样 {coverTicks} 拍，首帧落在第 {firstPictureTick} 拍；"
                + $"「还没画面就先淡了」{earlyFades} 拍（最低不透明度 {dimmest:0.###}）");
            Require(earlyFades == 0, "遮罩不许在首帧之前开始淡出（背景图与正片之间那段黑屏）");
            Require(page.MotionProbeState.CoverHidden, "就绪后加载遮罩完全退场");

            foreach (var stage in new[] { "最大化", "还原", "最大化", "还原" })
            {
                Write($"阶段：播放中{stage}");
                var before = transitions;
                page.ToggleMaximizeRequested();
                await page.WindowChange;
                await Until(() => surface.IsContentReady && !page.MotionProbeState.Animating, $"{stage}后缓冲与动效落定");
                Require(transitions == before + 1, $"{stage}仅发布一个几何终点");
                Require(page.MotionProbeState.Identity, $"{stage}后页面无残留缩放");
                await Task.Delay(450, token);
            }

            // 2026-09-20「拖动窗口边缘时画面闪烁」的那一条。以前没有连续拖动的逐帧证据，所以这一条
            // 一直挂着没动代码 —— 这里补上：以 16ms 一拍连续改窗口尺寸（模拟一次真实拖动），每一拍
            // 读一次画面<b>实际占的矩形</b>（PlacedRect，从 SpriteVisual 反算），看它有没有在
            // 「铺满」与「按比例缩进去留黑边」之间来回跳。
            //
            // 判据是「不许回退」：拖动期间窗口被 WM_SIZING 按画面比例锁着，窗口形状就是画面形状，
            // 画面就该一直铺满。缓冲追赶期间掉进 contain 分支（旧版就是这样）会让余白忽有忽无 ——
            // 那正是用户报的闪烁。跑完这一趟再等缓冲落定。
            Write("阶段：连续拖动窗口边缘（逐帧看画面有没有回退成留边）");
            Native.GetWindowRect(window.Handle, out var dragStart);
            var dragRaster = surface.RasterizationScale;
            var dragFrames = 0;
            var shrinkFrames = 0;
            var pendingFrames = 0;
            var dragDetail = new List<string>();
            for (var step = 1; step <= 12; step++)
            {
                // 宽高同步缩一点：窗口比例不变（画面比例锁着），只是整体变小。
                var scale = 1d - step * 0.015;
                var width = (int)Math.Round(dragStart.Width * scale);
                var height = (int)Math.Round(dragStart.Height * scale);
                Native.SetWindowPos(
                    window.Handle, Native.HwndTop,
                    dragStart.Left, dragStart.Top, width, height,
                    Native.SwpNoZOrder | Native.SwpNoActivate);
                await Task.Delay(16, token);

                var placed = surface.PlacedRect;
                var host = surface.HostSize;
                var hostWidth = host.Width * dragRaster;
                var hostHeight = host.Height * dragRaster;
                if (placed.Width <= 0 || placed.Height <= 0 || hostWidth <= 0 || hostHeight <= 0) continue;

                if (surface.IsResizePending) pendingFrames++;
                dragFrames++;
                // 「铺满」＝两个方向都贴边（±4%）。掉进 contain 就会有一个方向明显缩进去。
                var fillsWidth = Math.Abs(placed.Width / hostWidth - 1) <= 0.04;
                var fillsHeight = Math.Abs(placed.Height / hostHeight - 1) <= 0.04;
                if (!fillsWidth || !fillsHeight)
                {
                    shrinkFrames++;
                    if (dragDetail.Count < 4)
                        dragDetail.Add($"step{step}: 画面 {placed.Width:0}x{placed.Height:0}"
                            + $" / 宿主 {hostWidth:0}x{hostHeight:0}"
                            + $" pending={surface.IsResizePending}");
                }
            }
            foreach (var line in dragDetail) Write($"拖动异常帧：{line}");
            Write($"拖动取样：{dragFrames} 拍，缓冲追赶中 {pendingFrames} 拍，画面未铺满 {shrinkFrames} 拍");

            Require(dragFrames >= 8, $"拖动逐帧取样拿到足够样点（实得 {dragFrames}）");
            Require(shrinkFrames == 0,
                $"拖动全程画面始终铺满，不出现忽有忽无的黑边：未铺满 {shrinkFrames} 拍");
            await Until(() => surface.IsContentReady && !surface.IsResizePending, "拖动结束后缓冲追上新尺寸");
            Native.SetWindowPos(
                window.Handle, Native.HwndTop,
                dragStart.Left, dragStart.Top, dragStart.Width, dragStart.Height,
                Native.SwpNoZOrder | Native.SwpNoActivate);
            await Until(() => surface.IsContentReady && !surface.IsResizePending, "摆回原尺寸后缓冲再次落定");

            Write("阶段：快速全屏往返");
            page.SetFullscreen(true);
            await page.WindowChange;
            await RequireSettled("进全屏");
            await Task.Delay(65, token);
            page.SetFullscreen(false);
            await page.WindowChange;
            await RequireSettled("退全屏");
            await Task.Delay(65, token);
            page.SetFullscreen(true);
            await page.WindowChange;
            await Until(() => window.Fullscreen && surface.IsContentReady && !page.MotionProbeState.Animating, "快速往返最终全屏正确");
            await Task.Delay(500, token);
            page.SetFullscreen(false);
            await page.WindowChange;
            await Until(() => !window.Fullscreen && surface.IsContentReady && !page.MotionProbeState.Animating, "快速往返最终窗口化正确");

            // 本地 Visual 属性只验证交接后的几何；切换中的像素空白由外部连续抓屏检查。
            async Task RequireSettled(string stage)
            {
                var samples = 0;
                var loose = 0;
                var stretched = 0;
                string? first = null;
                for (var step = 0; step < 16; step++)
                {
                    var placed = surface.PlacedRect;
                    var client = window.ClientSize;
                    var content = surface.AttachedContentSize;
                    samples++;

                    // ① 贴在窗口里：至少一个方向贴边 ±4%。
                    var off = placed.Width <= 0 || placed.Height <= 0 || client.Width <= 0 || client.Height <= 0
                        || (Math.Abs(placed.Width / client.Width - 1) > 0.04
                            && Math.Abs(placed.Height / client.Height - 1) > 0.04);
                    // ② 还是画面自己的比例（没被新窗口掰变形）——「铺满」那条错法就死在这一条上。
                    var bent = content is { Width: > 0, Height: > 0 }
                        && Math.Abs(placed.Width / placed.Height / ((double)content.Width / content.Height) - 1) > 0.02;

                    if (off) loose++;
                    if (bent) stretched++;
                    if (off || bent)
                        first ??= $"画面 {placed.Width:0}x{placed.Height:0} @窗口 {client.Width}x{client.Height}"
                            + $" 缓冲 {content.Width}x{content.Height}";
                    await Task.Delay(16, token);
                }
                Write($"{stage}取样：{samples} 拍，未贴住窗口 {loose} 拍，被掰变形 {stretched} 拍"
                    + (first is null ? "" : $"（首帧 {first}）"));
                Require(loose == 0 && stretched == 0,
                    $"{stage}当场落位：跳变后每一拍画面都已贴在窗口里、比例也没被掰弯，"
                    + "不是从旧矩形爬过去、也没被拉变形");
            }

            Write("阶段：取帧期间快速反向");
            page.SetFullscreen(true);
            await Task.Delay(10, token);
            page.SetFullscreen(false);
            await page.WindowChange;
            Require(!window.Fullscreen, "快速反向以最后请求为准");
            Require(!page.FullscreenFrameVisible, "快速反向后保留帧已收回");

            Write("阶段：暂停时全屏往返");
            await handle.SetPropertyAsync("pause", true, token);
            await Until(() => control.Status.Paused, "本地素材已暂停");
            foreach (var fullscreen in new[] { true, false })
            {
                page.SetFullscreen(fullscreen);
                await page.WindowChange;
                Require(window.Fullscreen == fullscreen && surface.IsContentReady && !page.FullscreenFrameVisible,
                    $"暂停时{(fullscreen ? "进" : "退")}全屏落定且保留帧已收回");
            }
            await handle.SetPropertyAsync("pause", false, token);

            Write("阶段：最大化进全屏再退出");
            page.ToggleMaximizeRequested();
            await page.WindowChange;
            await Task.Delay(350, token);
            page.SetFullscreen(true);
            await page.WindowChange;
            await Task.Delay(350, token);
            page.SetFullscreen(false);
            await page.WindowChange;
            await Task.Delay(350, token);
            Require(window.IsMaximized, "最大化状态在全屏往返后保留");

            Write("阶段：退出播放");
            // 退场底、压暗旋钮、铺满旗三样在退场之前必须是收着的。播放中留着任何一样都是实打实的观感
            // 事故：一层底盖住整趟播放、一部永远偏暗的片子、或者一张被裁掉边角的画面。先钉住「进场／
            // 播放时不在」—— 2026-09-20 晚新加的那面「铺满」旗没有这一步，就会悄悄跟着下一趟播放走。
            Require(!page.ExitProbeState.Backdrop && page.ExitProbeState.Dim == 0 && !page.ExitProbeState.Fill,
                "播放中退场底收着、留帧压暗为 0、铺满旗关着");
            await handle.StopAsync();
            await handle.DisposeAsync();
            handle = null;
            await Task.Delay(60, token);
            Require(!surface.HasAttachedVisual, "播放已停止，交换链已交还（画面不再由 mpv 供帧）");
            Require(surface.IsRetained, "退场进入保留态（最后一帧在屏上、缓冲已交还）");

            // 2026-09-20 用户报「退出播放时下方瞬间出现大片空白」的那一段：拆链在前、还原浏览几何在后，
            // 窗口一口气变矮变宽，而画面上没有任何东西会自己跟过来 —— 旧版在这里把呈现计时器停掉、
            // 又把刷新路径全堵死，那一帧于是冻在旧尺寸，右下露出的就是 Stage 的深色。
            //
            // 这一段必须**逐拍取样**才盖得住：窗口还原是瞬时的，等它落定再看读数已经晚了（这正是
            // 旧探针 Delay(350) 漏掉它的原因）。取的是 PlacedRect —— 从 SpriteVisual 反算出来的
            // 「实际摆成什么样」，把宿主尺寸算在内；余白一出现它立刻就不是画面比例了。
            var aspect = 0d;
            var retainedSamples = 0;
            var blankSamples = 0;
            var sampled = 0;
            var dimmedSamples = 0;
            var backdropSamples = 0;
            var shapeMismatchSamples = 0;
            var shortBackdropSamples = 0;
            var shortStageSamples = 0;

            // 2026-09-20 晚「重新设计退场」补的四个：铺满旗立着几拍、压暗过程里最亮的那一档是多少、
            // 有几拍的压暗停在**中途**（既不是 0 也不是 1）、以及底边那条细进度线有没有被收掉。
            // 中间那个数是「一路溶解」与「一步压到 15%」唯一分得开的地方 —— 一步到位那种写法，
            // 中途档位一个都不会有；最后那个数盯的是「判据写了却没人调 Render()」那种死代码。
            var fillSamples = 0;
            var brightestDim = double.MaxValue;
            var partialDimSamples = 0;
            var thinLineSamples = 0;

            var shortDetail = new List<string>();
            var shapeDetail = new List<string>();
            var insideSample = new List<string>();
            page.EndMotionProbe();
            for (var tick = 0; tick < 30; tick++)
            {
                await Task.Delay(16, token);
                if (!surface.IsRetained && tick > 4) break;
                var placed = surface.PlacedRect;
                var host = surface.HostSize;
                var hostWidth = host.Width * surface.RasterizationScale;
                var hostHeight = host.Height * surface.RasterizationScale;

                // 退场那 240ms 里另外几样必须一直成立：这一页在淡出、最后一帧在溶解、并且它是**铺满**
                // 新宿主的（铺不满就会在窗口变形的那一帧露出底）。少任何一样，屏上留下的就是「缩在
                // 角落的最后一帧 + 一圈窗口色」（2026-09-20 用户截图）。先读它们，再读几何：几何那一支
                // 在宿主尺寸为 0 时会 continue。
                var exit = page.ExitProbeState;
                if (exit.Backdrop) backdropSamples++;
                if (exit.Fill) fillSamples++;
                if (exit.Dim > 0) dimmedSamples++;
                // 只统计**正数**里最小的那个：「一路溶解」的那一趟会读到 0.0x 这一档；而旧那套
                // 一步压到 0.85 的写法只读得到 0.85，于是这一条当场翻红。把 0 也算进来的话，
                // 起手那个 0 会把最亮值压成 0，这条判据就成了摆设。
                if (exit.Dim > 0 && exit.Dim < brightestDim) brightestDim = exit.Dim;
                if (exit.Dim > 0 && exit.Dim < 1) partialDimSamples++;
                if (exit.ThinLine) thinLineSamples++;

                // 2026-09-20 补的第四条：底**立着**不够，还得**铺满**。
                //
                // 用户第二张截图（「怎么退出播放页面返回主页的时候还是这样」，点左上角返回箭头，退出的
                // 瞬间截图）量出来的形状是上 65% 这一页、下 30% 一整块 #16181C，中间一条硬分界 ——
                // 那不是「底没立起来」（它立起来了，上面那条断言一直是绿的），是**底只铺到了窗口变高之前
                // 的高度**。LeavePlayer 里 RestorePlayerToBrowse 让窗口一口气变高，这一页的 Measure/Arrange
                // 要等下一帧才跟上，中间那几拍新露出来的那一条既没有舞台也没有退场底。
                //
                // 原先这里只读 PlacedRect（画面那一半）与 ExitProbeState（底在不在），**没有一处问过它
                // 铺了多高**，所以这个错法能全绿通过。现在补上：底与舞台的高必须贴住这一刻的客户区高。
                // 判据给 2px 容差（ActualHeight 是布局值，客户区是整数，两者在 DPI 缩放下未必逐位相等）；
                // 那次「只铺到旧高度」的错法差的是 137px（760-623），离容差差着两个数量级，杀得掉。
                var cover = page.ExitCoverProbeState;
                if (cover.WindowHeight > 0)
                {
                    if (cover.BackdropHeight > 0 && cover.WindowHeight - cover.BackdropHeight > 2)
                    {
                        shortBackdropSamples++;
                        if (shortDetail.Count < 4)
                            shortDetail.Add($"退场底只铺到 {cover.BackdropHeight:0}，客户区 {cover.WindowHeight:0}"
                                + $"（差 {cover.WindowHeight - cover.BackdropHeight:0}px）");
                    }
                    if (cover.StageHeight > 0 && cover.WindowHeight - cover.StageHeight > 2)
                    {
                        shortStageSamples++;
                        if (shortDetail.Count < 4)
                            shortDetail.Add($"舞台只铺到 {cover.StageHeight:0}，客户区 {cover.WindowHeight:0}"
                                + $"（差 {cover.WindowHeight - cover.StageHeight:0}px）");
                    }
                }

                if (placed.Width <= 0 || placed.Height <= 0 || hostWidth <= 0 || hostHeight <= 0) continue;

                sampled++;
                if (aspect <= 0) aspect = placed.Width / placed.Height;

                // 2026-09-20 用户截图那条「丑」的正主：保留态的形状一度被宿主污染 —— 窗口被掰成竖形时
                // 呈现矩形与 PictureAspect 都是竖的，最后一帧于是被按竖形 contain 成一条窄带
                // （竖屏档实测 shape=1080x1872 对 16:9 的视频，屏上留下 42% 宽的带、上下大片黑）。
                //
                // 判据用「形状等不等于广告里的那个数」：探针已经用 ProbeSourceAspect 把素材的 16:9
                // 写了进去，所以保留态的形状就必须是 16:9（±4%）。宿主形状在这里是竖的，两者天差地别，
                // 拿窗口比例顶替一眼就能看出来 —— 这是我第一版判据漏掉的那条（那时只比「与宿主不同形」，
                // 形状与摆放一起错也能过）。
                var shape = page.RetainedShapeProbeState;
                if (shape.Width > 0 && shape.Height > 0)
                {
                    var shapeAspect = shape.Width / shape.Height;
                    if (Math.Abs(shapeAspect / (16d / 9d) - 1) > 0.04)
                    {
                        shapeMismatchSamples++;
                        if (shapeDetail.Count < 4)
                            shapeDetail.Add($"形状 {shape.Width:0.###}x{shape.Height:0.###}"
                                + $"（{shapeAspect:F3}）≠ 素材的 {16d / 9d:F3}"
                                + $"，宿主 {hostWidth:0}x{hostHeight:0}");
                    }
                }

                // 这一帧在宿主里的「覆盖度」：画面占了宿主面积的多大一块。留边是正常的（浏览窗口
                // 未必同比例），但整块画面若一直缩在旧尺寸里，覆盖度会明显偏小、且四周余白持续存在。
                var coverX = placed.Width / hostWidth;
                var coverY = placed.Height / hostHeight;
                var coversSide = Math.Min(coverX, coverY);
                if (surface.IsRetained) retainedSamples++;
                // contain 摆放的正确判据：至少一个方向贴满（±3%），另一个方向按比例留边。
                var fillsOneSide = Math.Abs(coverX - 1) <= 0.03 || Math.Abs(coverY - 1) <= 0.03;
                if (!fillsOneSide && surface.IsRetained) blankSamples++;
                if (tick % 6 == 0)
                    insideSample.Add($"{placed.Width:0}x{placed.Height:0}@host{hostWidth:0}x{hostHeight:0}"
                        + $" cover={coversSide:F3} retained={surface.IsRetained}"
                        + $" shape={shape.Width:0.###}x{shape.Height:0.###}");
            }
            foreach (var line in shapeDetail) Write($"退场形状不符：{line}");
            foreach (var line in shortDetail) Write($"退场面不够高：{line}");
            foreach (var line in insideSample) Write($"退场取样：{line}");
            Write($"退场取样：{sampled} 拍，其中保留态 {retainedSamples} 拍，未贴边 {blankSamples} 拍"
                + $"，形状不符 {shapeMismatchSamples} 拍"
                + $"，退场底立着 {backdropSamples} 拍，留帧压暗 {dimmedSamples} 拍"
                + $"，面不够高 {shortBackdropSamples + shortStageSamples} 拍"
                + $"（底 {shortBackdropSamples} / 舞台 {shortStageSamples}）"
                + $"，铺满 {fillSamples} 拍，溶解中途 {partialDimSamples} 拍，起手最亮 {brightestDim:F3}"
                + $"，细线立着 {thinLineSamples} 拍");

            Require(retainedSamples > 0, "退场逐拍取样确实落在保留态里（不是等落定才看）");
            Require(blankSamples == 0,
                $"保留期每一拍画面都按视频比例铺满宿主的一条边（余白不成片）：未贴边 {blankSamples} 拍");
            Require(shapeMismatchSamples == 0,
                $"保留态的形状是画面自己的比例（最后一帧不会被缩成窄带）：不符 {shapeMismatchSamples} 拍");
            Require(aspect > 0, "保留期画面保持了可用的形状（比例来自视频而非浏览窗口）");
            Require(backdropSamples > 0, "退场期间退场底立着（整页淡出有一块面可以淡）");
            Require(dimmedSamples > 0, "退场期间留帧已被压暗");
            // 2026-09-20 晚「重新设计退场」的三条。前两条说的是**铺满**（窗口变形那一帧不许缩成信匣），
            // 后两条说的是**溶解**（起手还亮着、中途有档位）—— 这正是「一路退去」与旧那套「一步压到
            // 15% 再让一块不透明的底淡掉」唯一分得开的地方。
            Require(fillSamples > 0, "退场期间留帧是铺满新宿主的（窗口变形那一帧不缩成信匣）");
            Require(partialDimSamples >= 3,
                $"退场留帧的压暗停在中途的拍数够得上「一路溶解」：只有 {partialDimSamples} 拍");
            Require(brightestDim < 0.5,
                $"退场起手那一帧画面还亮着（不是一步压到 15%）：读到最暗的是 {brightestDim:F3}");
            // 2026-09-20 第四张用户截图里那条 3px 的白线：细线画在**页面**的底边上，页面淡掉它还在。
            // 判据在 `Render()` 里早就写了，可退场这条路上从来没有人调过 `Render()` —— 于是它一直亮着，
            // 而 `ProbeThinLine` 自己会调 `Render()`，所以自检一直是绿的。这条读的是**真实退场那一趟**。
            Require(thinLineSamples == 0,
                $"退场期间底边那条细进度线已收掉（页面淡掉之后不会还亮着一条）：立着 {thinLineSamples} 拍");
            // 2026-09-20 加的这一条才是用户第二张截图的正主：「立着」与「铺满」是两件事。
            // 「面不够高」的那两拍组合里，舞台那一支是**同一块面**的另一半证据 —— 底铺到哪、舞台就铺到哪，
            // 两个数一起贴住客户区，新露出来的那一条才既有舞台也有底，才不会露出 Mica / 窗口底色。
            Require(shortBackdropSamples == 0,
                $"退场期间退场底每一拍都铺满客户区（窗口变高时底立刻跟上，不会只铺到旧高度）：不够高 {shortBackdropSamples} 拍");
            Require(shortStageSamples == 0,
                $"退场期间舞台每一拍都铺满客户区（新露出的那一条有舞台垫着）：不够高 {shortStageSamples} 拍");

            await Task.Delay(350, token);
            Require(!page.PlayerVisible && page.MotionProbeState.Identity && !window.PlaybackTitleBar,
                "退场后页面收起，变换和标题栏恢复");
            Require(!page.ExitProbeState.Backdrop && page.ExitProbeState.Dim == 0,
                "退场收摊后退场底收起、留帧压暗归零（不会留在下一趟播放上）");
            // 2026-09-20 用户报「给这个返回主页的页面也加上背景图」：遮罩那块垫底图**退场不许被丢掉** ——
            // 丢了的话下一趟遮罩亮起来时是一整片纯色（换片那几趟遮罩是在 `_playerHold > 0` 手里亮的，
            // 新片那张图还在路上，用户实测屏上整块 `#0C0E11`、一个像素的图都没有）。
            // 探针进场前就把 `CoverBackdrop` 摆好了（见 RunScoped 开头），走到这里它必须还在。
            Require(page.ViewModel.CoverBackdrop is not null,
                "退场不会把遮罩垫底图丢下（下一趟遮罩不会是一片纯色）");
            Require(surface.AttachedContentSize.Width == 0, "退场收摊后缓冲尺寸已清空");
            Require(surface.PlacedRect.Width == 0, "退场收摊后 SpriteVisual 已拆掉（画面不留残影）");
            Require(!surface.IsRetained, "退场收摊后保留态结束");
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

            // 隔离进程未登录，返回目标是登录页；用本地背景标记它，不加载任何服务器图片。
            shell.SignInRoot.Background = new ImageBrush
            {
                ImageSource = page.ViewModel.CoverBackdrop,
                Stretch = Stretch.UniformToFill
            };
            foreach (var fullscreen in new[] { false, true })
            {
                Write($"阶段：{(fullscreen ? "全屏" : "窗口化")}关闭前直接返回浏览页");
                page.ViewModel.CoverUp = true;
                page.BeginMotionProbe(fullscreen);
                await page.WindowChange;
                await Task.Delay(400, token);
                handle = await backend.StartAsync(request, token);
                control = (IPlayerControl)handle;
                await Until(() => control.Status.PictureStarted, "关闭回归的本地视频首帧已上屏");
                page.ProbePictureStarted();
                await Until(() => surface.HasPicture && surface.IsContentReady, "关闭回归的本地视频已显示");
                page.ViewModel.CoverUp = false;
                await Task.Delay(350, token);

                Require(page.ViewModel.PrepareStopAsync is not null, "停止前的页面交接已经接线");
                await page.ViewModel.PrepareStopAsync!();
                Require(control.Status.Loaded, "先返回浏览页时后端仍有视频，尚未发送停止");
                Require(!page.PlayerVisible && shell.SignInVisible && !window.PlaybackTitleBar && !window.Fullscreen,
                    "停止前浏览页已显示，播放器及全屏已收回");
                Require(page.MotionProbeState.Identity && !page.MotionProbeState.Animating,
                    "直接返回没有残留缩放或退场计时器");
                Native.GetWindowRect(window.Handle, out restored);
                Require(restored.Left == browse.Left && restored.Top == browse.Top
                    && restored.Width == browse.Width && restored.Height == browse.Height,
                    "直接返回恢复原浏览窗口");

                await handle.StopAsync();
                await handle.DisposeAsync();
                handle = null;
                // 超过旧留帧的 1200ms 保险丝，模拟停止上报慢；浏览页不能因此退回纯色播放层。
                await Task.Delay(1400, token);
                page.EndMotionProbe();
                Require(!page.PlayerVisible && shell.SignInVisible && !surface.IsRetained,
                    "后端拆除和迟到的退出通知不会重新盖住浏览页");
            }
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
