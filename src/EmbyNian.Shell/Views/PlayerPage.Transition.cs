using EmbyNian.Diagnostics;
using EmbyNian.Playback;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace EmbyNian.Shell.Views;

public sealed partial class PlayerPage
{
    private bool _onStage;
    private bool _inputSuspended;
    private bool _enterAnimating;
    private bool _startupHandoverPending;
    private PlayerMotion.Transition? _pageTransition;
    private DispatcherQueueTimer? _poseDriver;
    private const int PoseTickMilliseconds = 16;

    /// <summary>
    /// 变换由同一颗钟逐拍写入，反向时从当前姿势续接。不能用 Storyboard 驱动 RenderTransform：
    /// 岛在进场中途重新布局时，该动画曾停在起点且不发 Completed，控制条因此被推出窗口。
    /// <para>
    /// 只管「小窗里的淡入淡出」这一种转场。起播开了自动全屏的那条不走这里 —— 它「一上来就整屏」，
    /// 由 <see cref="EnterFullscreenAtOnce"/> 直接整屏、纯色层盖住长大那一拍（用户令 2026-09-25）。
    /// </para>
    /// </summary>
    private void TransitionPage(bool entering, Action landed, Action<double>? progress = null)
    {
        var now = Now;
        var from = _pageTransition?.At(now)
            ?? (entering ? PlayerMotion.Pose.Entering : PlayerMotion.Pose.Visible);
        CancelPageTransition();

        if (!HomeMotion.AnimationsEnabled || XamlRoot is null)
        {
            ApplyPagePose(PlayerMotion.Pose.Visible);
            progress?.Invoke(1);
            landed();
            return;
        }

        var transition = PlayerMotion.Page(entering, from, now);
        _pageTransition = transition;
        ApplyPagePose(transition.From);
        _enterAnimating = entering;

        var driver = DispatcherQueue.CreateTimer();
        driver.IsRepeating = true;
        driver.Interval = TimeSpan.FromMilliseconds(PoseTickMilliseconds);
        _poseDriver = driver;
        driver.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_poseDriver, driver)) { driver.Stop(); return; }

            var elapsed = Now - transition.StartedAt;
            if (elapsed < transition.Duration)
            {
                progress?.Invoke(Math.Clamp(elapsed / (double)transition.Duration, 0, 1));
                ApplyPagePose(transition.At(Now));
                return;
            }

            CancelPageTransition();
            ApplyPagePose(PlayerMotion.Pose.Visible);
            progress?.Invoke(1);
            landed();
        };
        driver.Start();
        Log.Debug(Category, $"播放页{(entering ? "进场" : "退场")}：{transition.Duration}ms");
    }

    /// <summary>先提交不透明加载层，再改窗口；首帧淡入必须等这一交接完成，不能靠两个计时器碰运气。</summary>
    private async Task CompletePlayerEntranceAsync()
    {
        if (!_onStage || _window is not { } window) return;
        var generation = _windowChangeGeneration;
        var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(this).Compositor;
        try
        {
            await compositor.RequestCommitAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(350));
            if (!Current()) return;
            VideoFrameOverlay.Flush();
            _shell?.ShowPlayer(true);
            window.VideoVisible = true;
            EnterAutoFullscreen();
            await _windowChange;
            if (!Current()) return;
            await compositor.RequestCommitAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(350));
            VideoFrameOverlay.Flush();
        }
        catch (Exception error)
        {
            Log.Warn(Category, "起播窗口交接未能确认呈现，保留加载层等待首帧", error);
            if (Current())
            {
                _shell?.ShowPlayer(true);
                window.VideoVisible = true;
                EnterAutoFullscreen();
            }
        }
        finally
        {
            if (Current())
            {
                _startupHandoverPending = false;
                if (!ViewModel.CoverUp) HideCoverPlate();
            }
        }

        bool Current() => generation == _windowChangeGeneration && _onStage && _window == window;
    }

    /// <summary>
    /// 「一上来就整屏」（用户令 2026-09-25）：起播开了自动全屏时，不在小窗里淡入再跳全屏，而是当拍
    /// 直接把窗口整屏、播放页从第一帧就整屏，最贴近「播放中按 F 切全屏」那种即时感。
    /// <para>
    /// 窗口从浏览几何长到整屏那一拍，岛的合成提交晚 <c>SetWindowPos</c> 一两拍；浏览页此刻也还停在小窗
    /// 尺寸。两者都用加载遮罩同色的纯色顶层层盖住（<see cref="VideoFrameOverlay"/> 由 DWM 直接合成、不吃
    /// 岛的滞后），等整屏加载层提交上屏再撤，于是从头到尾看不到「先小窗、再放大」。
    /// </para>
    /// </summary>
    private void EnterFullscreenAtOnce()
    {
        if (_window is not { } window) return;

        // 播放页立刻不透明（不走淡入），铺满整屏的黑舞台＋加载层。
        ApplyPagePose(PlayerMotion.Pose.Visible);

        // 纯色层先盖满整个显示器：盖住「浏览页还在小窗」这一瞬，也盖住紧接着窗口长大的那一拍。
        // 2026-09-25 起铺的是遮罩那张背景图（图没到手才退回纯色，见 StartupCover）—— 从前一律铺近黑，
        // 用户看到的因此是「先全屏黑一片、再切到背景图」。
        var cover = StartupCover(window.Handle);
        cover.Show(VideoFrameOverlay.FullscreenRect(window.Handle), topmost: true);
        VideoFrameOverlay.Flush();

        _shell?.ShowPlayer(true);
        window.VideoVisible = true;
        // 同步整屏：Fullscreen 的 setter 里 SynchronizeContentLayout 已把整屏 Cover 排到位。
        window.Fullscreen = true;

        // 没有异步窗口交接了 —— 遮罩揭开判据 PictureReady 读这一位，置假它才会在画面就绪时揭开。
        _startupHandoverPending = false;

        _ = RevealAfterInstantFullscreenAsync(cover);
    }

    /// <summary>
    /// 整屏 Cover 上屏之后立刻撤纯色层 —— 它是「窗口长到整屏那一拍」的临时垫层，不是加载遮罩。
    /// <para>
    /// <b>为什么不能等 <c>RequestCommitAsync</c> 的回执。</b>原先这里等的是它、上限 350ms，而它在这条路上
    /// 实测不兑现（日志原话：<c>起播整屏覆盖层提交未确认，照撤：The operation has timed out.</c>）——
    /// 那 350ms 的纯色层盖住的不只是窗口长大的那一两帧，还有<b>已经到位的遮罩背景图</b>，用户看到的因此
    /// 是「先黑一片、再切到背景图」（2026-09-25 用户报）。这里要的其实只是「这次布局出过帧了没有」，
    /// 那是渲染回调答得了的，不是提交回执答得了的。
    /// </para>
    /// </summary>
    private async Task RevealAfterInstantFullscreenAsync(VideoFrameOverlay cover)
    {
        var started = Now;
        await WaitFramesAsync(2);
        VideoFrameOverlay.Flush();
        cover.Dispose();

        // 这一行是给验收用的：它直接回答「起播自动全屏那一下到底黑了多少毫秒」——图还没到位的那一段
        // 遮罩自己也在纯色上，所以这个数只说明覆盖层那一截，不是用户看到的全部。
        Log.Debug(Category, $"起播整屏覆盖层撤下：盖了 {Now - started}ms");
    }

    /// <summary>
    /// 等合成器出过 <paramref name="frames"/> 帧。没有渲染回调的场合（窗口被最小化、显示器熄了）也要走得动，
    /// 所以每一帧都兜一张 32ms 的网 —— 这一处的等待只能短不能长：它就是覆盖层的寿命。
    /// </summary>
    private static async Task WaitFramesAsync(int frames)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnRendering(object? sender, object args)
            {
                CompositionTarget.Rendering -= OnRendering;
                rendered.TrySetResult();
            }

            CompositionTarget.Rendering += OnRendering;
            _ = Task.Delay(32).ContinueWith(
                _ => rendered.TrySetResult(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            await rendered.Task;
        }
    }

    private void ApplyPagePose(PlayerMotion.Pose pose)
    {
        Opacity = pose.Opacity;
        PageTransform.ScaleX = PageTransform.ScaleY = pose.Scale;
        PageTransform.TranslateY = pose.OffsetY;
    }

    private void CancelPageTransition()
    {
        var driver = _poseDriver;
        _poseDriver = null;
        _pageTransition = null;
        _enterAnimating = false;
        EndBrowseWait();
        driver?.Stop();
    }

    /// <summary>
    /// 给 <paramref name="target"/> 的 <paramref name="property"/> 挂一条 DoubleAnimation。只许喂
    /// <b>Opacity 这类它真跑得动</b>的属性——换集遮罩的淡出（<see cref="PlayerPage.Cover.cs"/>）用它；
    /// <b>RenderTransform 别走这条路</b>，整页进退的 pose 由 <see cref="TransitionPage"/> 的 16ms 计时器
    /// 逐拍驱动，理由写在那里（2026-09-18/19 两次实机翻车：SetTarget 变换对象冻在起点、属性路径指向
    /// 元素也到不了终点）。
    /// </summary>
    private static void Animate(Storyboard board, DependencyObject target, string property,
        double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        board.Children.Add(animation);
    }

    /// <summary>
    /// 这一次 <see cref="CompletePlayerExit"/> 要不要顺手把窗口还回浏览几何。
    /// <para>
    /// 由 <c>LeavePlayer</c> 立起（它才是「真的退出一场播放」那条路）。<b>不能只看「跑到了落定拍」</b>：
    /// <c>PlayerPage.Detach</c> 那条路是 <c>LeavePlayer(); StopPlayerMotion(); CompletePlayerExit();</c>
    /// 三连，页面可能根本没上过台 —— 那时把窗口掰回上一次记下的浏览几何是无事生非（用户只是从浏览页
    /// 点了一部片子走独占模式）。落定拍消费掉它，所以一次退场只会换来一次还原。
    /// </para>
    /// </summary>
    private bool _restoreBrowseOnExit;

    /// <summary>
    /// 这一趟退场<b>不留溶解</b>：片子停下之前从播放页走的那条路（<see cref="ReturnToBrowseBeforeStopAsync"/>）
    /// 已经把那最后一帧交在一块不随窗口变形的覆盖层上，本页收姿势那 240ms 溶解只剩一块空舞台在淡 ——
    /// 用户要的是「直接切回主页」（2026-09-25 用户令），所以那一趟当拍收摊。立起与消费都在
    /// <see cref="LeavePlayer"/> 一处，别的收摊路（Detach、真收摊）看不到它。
    /// </summary>
    private bool _exitSnap;

    /// <summary>
    /// 这一趟退场落定没有 —— 等在 <see cref="ReturnToBrowseBeforeStopAsync"/> 那一头的人拿它当信号（停播放
    /// 排在动画之后，见那一段）。非空即「正等」，由 <see cref="CompletePlayerExit"/> 放行并清掉。
    /// </summary>
    private TaskCompletionSource? _exitLanded;

    /// <summary>
    /// 退场等待浏览页报就绪的那颗表；非空即「最后一帧还按着，溶解没起」。
    /// <para>
    /// <b>为什么要等（2026-09-20 晚用户令「退出的时候不第一时间去掉画面，等背景图加载出来之后再无缝替换」）。</b>
    /// <c>LeavePlayer</c> 的 ① 已经把浏览页放回屏上，但它那张背景图（主页轮播、详情页背景）要一次网络往返
    /// 才到；溶解全长只有 <see cref="PlayerMotion.ExitMilliseconds"/>，图没到就溶解完，露出来的是那张图的
    /// 位置上的空白 —— 用户截图里那块发灰的空页面。所以先按住最后一帧不放，等浏览页把内容画出来（或等到
    /// <see cref="ExitReadyWaitMilliseconds"/> 那个上限）再起同一条溶解轨道。
    /// </para>
    /// <para>
    /// <b>为什么兜一个上限。</b>就绪是个外部条件 —— 服务器慢、图挂了、或者这次退场根本没有背景图要等 ——
    /// 没有上限的话「等就绪」会变成「永远不退场」，而屏上停着的是一张按着不放的旧画面加一个已经收摊的
    /// 输入层。上限到点就当它已就绪：宁可露出那块灰，也不能卡在退场里出不来。
    /// </para>
    /// </summary>
    private DispatcherQueueTimer? _browseWait;

    /// <summary>
    /// 等浏览页就绪的宽限。240ms 是溶解全长，等它三倍多的余量：一次正常的背景图往返在本机是一两帧到
    /// 几十毫秒，给它 800ms 是「图确实在路上」与「这张图不会来了」之间那条线。
    /// </summary>
    private const int ExitReadyWaitMilliseconds = 800;

    /// <summary>
    /// ③「等背景图加载出来之后再无缝替换」：按住最后一帧，等浏览页报就绪（或等到宽限上限）才起溶解。
    /// <para>
    /// 判定只用 <c>ContentHost</c> 的呈现尺寸 —— 浏览页被 <c>ShowPlayer(false)</c> 放回来的同一拍就
    /// <c>UpdateLayout()</c> 强制过一版，所以「有尺寸」在这里等于「这一拍真的排过版了」。这比去问每一张
    /// 背景图「你的 Source 到了没有」简单且够用：那些图各自是哪个控件、什么时候算到，是浏览页自己的事，
    /// 这一页管的是「别在它还是空的时候把画面收掉」。宽限到点同样放行（见 <c>_browseWait</c> 的注释）。
    /// </para>
    /// <para>
    /// 落定拍走 <see cref="CompletePlayerExit"/>，它会把 <c>_browseWait</c> 一并收掉 —— 提前退场、换向、
    /// <c>Detach</c> 那几条路都从这里过，所以「按着不放」的标记不会漏到下一趟。
    /// </para>
    /// </summary>
    private void ExitWhenBrowseReady()
    {
        _browseWait?.Stop();
        _browseWait = null;

        var wait = DispatcherQueue.CreateTimer();
        wait.Interval = TimeSpan.FromMilliseconds(PoseTickMilliseconds);
        wait.IsRepeating = true;
        // 宽限用的是本页那颗钟（`Now` ＝ Environment.TickCount64），与 pose 轨道、光标那几处同一个
        // 时间源；DispatcherQueueTimer 自己没有 Elapsed，所以截止时刻在这儿先算好。
        var deadline = Now + ExitReadyWaitMilliseconds;
        wait.Tick += (_, _) =>
        {
            // 退场等待期间页面已离台且输入已暂停；重新进场或收摊才取消等待。
            if (_onStage || !_inputSuspended || !_restoreBrowseOnExit)
            {
                EndBrowseWait();
                return;
            }

            // 就绪问的是「此刻框里那一页自己报没报就绪」——没有页面的场合（探针那条没导航过的路）
            // 一律算就绪，于是立即溶解，与探针/自检同一条路（见 ShellPage.ActiveContentReady）。
            var ready = _shell is null || _shell.ActiveContentReady;

            if (!ready && Now < deadline) return;

            EndBrowseWait();
            if (ready) Log.Info(Category, "退场：浏览页已就绪，开始溶解");
            else Log.Warn(Category, $"退场：等浏览页就绪超时（{ExitReadyWaitMilliseconds}ms），照常溶解");
            BeginPlayerExitDissolve();
        };

        _browseWait = wait;
        wait.Start();
    }

    /// <summary>收掉等待表与它的标记，起溶解与收摊两条路都要过这里。</summary>
    private void EndBrowseWait()
    {
        _browseWait?.Stop();
        _browseWait = null;
    }

    /// <summary>
    /// 溶解真正起跑的那一拍：整页姿势逐拍走到落定，画面的压暗与它同拍（<see cref="DrivePlayerExit"/> 由
    /// <c>progress</c> 回调驱动）。与 <c>LeavePlayer</c> 当初排在 ② 的是同一条轨道，只是现在它可以在
    /// 浏览页就绪之后才起。
    /// </summary>
    private void BeginPlayerExitDissolve() =>
        TransitionPage(
            entering: false,
            landed: CompletePlayerExit,
            progress: DrivePlayerExit);

    private void CompletePlayerExit()
    {
        EndBrowseWait();
        Visibility = Visibility.Collapsed;
        Stage.Visibility = Visibility.Collapsed;
        EndPlayerExit();
        CompleteCoverExit();
        ApplyPagePose(PlayerMotion.Pose.Visible);
        _inputSuspended = false;

        // 画面先放掉，窗口再动 —— 反过来的话，最后那一帧会以旧尺寸出现在新窗口里。
        _videoTarget.RetainLastFrame = false;

        // **窗口到这里才改尺寸**（2026-09-20 晚，用户令「就不能直接切到主页，然后再调整窗口大小吗」）。
        // 走到这一拍，屏上只剩已经排好版的浏览页，于是：
        //   ① 「新窗口尺寸 + 旧内容」那一拍再也吐不出「定在旧尺寸上的最后一帧」；
        //   ② 合成器若仍旧先吐一拍旧内容，旧内容是**浏览页**，垫底那一条是窗口底色 #16181C ——
        //      与浏览页同一个量级的深色（2026-09-20 实测两者底边条亮度都是 32），中间那条硬分界
        //      也就不存在了。
        if (_restoreBrowseOnExit)
        {
            _restoreBrowseOnExit = false;
            _window?.RestorePlayerToBrowse();
            if (_window is not null) _window.FreeSizing = false;
        }

        // **窗口几何到这一拍才算落定**，播放层这一位因此收在这里、而且收在下面那次强制布局之前：
        // 上面那两次 SetWindowPos（退全屏 → 还浏览几何）给浏览页的量测全是中间态，中途放开门，主页
        // 会先按播放几何翻一趟档、再按浏览几何翻回来 —— 用户看到的就是媒体库上下各跑一次 260ms 的动画
        // （2026-09-25 实测日志：视口 867 → 压上轮播、13ms 后视口 1271 → 回默认）。放开门之后的这一次
        // SynchronizePlaybackLayout 会给主页一次尺寸变化，该判的档在那一拍一次判完。
        _window?.SetPlayerLayer(false);

        // Mica 到这一拍才打开：整页与退场底都收干净了，底下是已经画好的浏览页，开它谁也看不见。
        // 早开一步就会在窗口改尺寸的那几拍里漏出一块壁纸采样面（2026-09-20 用户第三张截图）。
        RestoreBrowseBackdrop();

        // 客户区刚变过，这一拍读到的才是「浏览页该排多高、岛该多大」。
        SynchronizePlaybackLayout();

        // **退场落定**：等在 <see cref="ReturnToBrowseBeforeStopAsync"/> 那一头的人放行 —— 停播放要排在
        // 动画走完之后，留帧与那层 SpriteVisual 在动画期间才活着（见那一段）。幂等：Detach 一类不经这条
        // 路进来的收摊，这里放的是空的。
        var landed = _exitLanded;
        _exitLanded = null;
        landed?.TrySetResult();
    }

    /// <summary>
    /// 用户关闭播放页时直接交回现成的浏览页。必须先呈现再停 mpv：保留交换链引用并不能阻止
    /// mpv 的 stop/quit 把里面的像素清空。
    /// <para>
    /// <b>2026-09-25 第三轮起，这一趟不留溶解、也不再让画面跟着窗口缩</b>（用户令「退出播放窗口化的一瞬间
    /// 会有视频的残留画面，不能直接切回主页吗」）。第二轮这一趟走的是「等浏览页就绪 → 最后一帧逐拍溶解」，
    /// 而窗口在那之前就已经缩回浏览几何 —— 画面跟着缩进小窗的那一截，用户读作「残留画面」。现在那一帧
    /// 交在一块<b>不随窗口变形</b>的覆盖层上（见 ①②），本页当拍收摊，撤层时屏上就是主页。
    /// </para>
    /// <para>
    /// 停播放仍旧排在落定之后：调用方（<c>StopPlaybackAsync</c>）等这一趟返回才停 mpv，而这一趟在覆盖层
    /// 撤掉之前不会返回 —— mpv 那一刀清像素时，屏上早就不靠它的像素活着了。
    /// </para>
    /// </summary>
    internal async Task ReturnToBrowseBeforeStopAsync()
    {
        if (!_onStage || _window is not { } window) return;

        // ① 先抓一帧，铺成一块**不随窗口变形**的整屏覆盖层（2026-09-25 用户令「退出播放窗口化的一瞬间会有
        //    视频的残留画面，不能直接切回主页吗」）。从前这一趟是让画面跟着窗口一起缩、缩完再逐拍溶解 ——
        //    全屏视频缩进小窗的那一截就是用户说的「残留画面」。覆盖层由 DWM 直接合成、定位在屏幕上，
        //    窗口怎么缩它都不动，于是「窗口化」这件事发生在一张静止的画底下，谁也看不见。
        //    抓不到帧照常往下走：屏上剩的是退场底（没有画面可残留，也就没有这一条）。
        VideoFrameOverlay? handoff = null;
        if (_videoTarget.HasAttachedVisual)
        {
            try
            {
                var frame = await _videoTarget.CaptureFrameAsync().WaitAsync(TimeSpan.FromMilliseconds(750));
                if (frame is not null)
                {
                    handoff = new VideoFrameOverlay(window.Handle, frame);
                    handoff.Show(VideoFrameOverlay.FullscreenRect(window.Handle), topmost: true);
                    VideoFrameOverlay.Flush();
                }
            }
            catch (Exception error)
            {
                Log.Warn(Category, "退场抓帧不成，这一趟没有覆盖层遮挡", error);
            }
        }

        // ② 岛里那一帧立刻放掉。屏上由覆盖层接着，于是接下来几步里没有任何东西会跟着窗口一起缩 ——
        //    这是「不再有残留画面」的全部机制。放掉之后本页是一块空舞台，所以下面那一趟也不留溶解。
        _videoTarget.RetainLastFrame = false;

        // ③ 窗口先收回浏览几何，再让浏览页露出来 —— 关键在这一步要早于 LeavePlayer 里的
        // ShowPlayer(false)+强制排版（2026-09-25，用户报「退出播放页面会触发媒体库上移」，以及更早那条
        // 「全屏退出主页轮播图先大后小」）。
        //
        // 少了这一步，浏览页会先按<b>整屏宽度</b>排一次版：媒体库那种响应式网格因此列数变多、内容变矮，
        // ScrollView 把竖直滚动位夹小；等窗口缩回小窗、网格又变高，可偏移已经被夹过 —— 同一像素偏移落在
        // 更靠上的内容上，看起来就是「整个媒体库往上跳了一截」。主页轮播图则是先按整屏宽了一下。收回窗口
        // 之后浏览页只在浏览尺寸下排这一次版，两样都不再发生。
        window.RestorePlayerToBrowse();
        window.FreeSizing = false;

        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _exitLanded = landed;

        _exitSnap = true;
        LeavePlayer();

        // `_restoreBrowseOnExit` **不能在这里清**：窗口上面已经还原过，CompletePlayerExit 里那一次
        // RestorePlayerToBrowse() 是空操作（见它自己的守卫）。
        try
        {
            // 落定上限 3 秒：这一趟是当拍收摊（_exitSnap），正常几乎是立刻回来。到点照走 —— 停播放不能被
            // 一趟收摊卡死，那种卡法没有出路（画面还挂着、窗口已经交回浏览页）。
            await landed.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            Log.Warn(Category, "退场没在 3 秒内落定，照常停播放");
        }

        // ④ 等合成器出两帧再撤覆盖层：浏览页刚被放回来，它要排完版、画上屏。撤早了露出的是还没画东西的
        //    窗口底色（2026-09-20 用户第三张截图里那两截色）。这里等「布局出过帧了没有」比等提交回执可靠，
        //    理由与 RevealAfterInstantFullscreenAsync 同一段。撤了之后屏上就是主页 —— 再没有任何东西挡着。
        await WaitFramesAsync(2);
        handoff?.Dispose();
        VideoFrameOverlay.Flush();

        // ⑤ 退到主页这一趟，让主页重走一遍它平时那趟「回主页」（2026-09-25 用户报「退出时媒体还是没有
        //    动画」，要「和从电影页面返回到主页的动画一样」）。放这一句的位置就是全部要点：必须在播放层
        //    收摊之后（上面那一步）—— 早一句，主页的矮窗档判定会被播放层那道门挡掉，翻档的动画根本不会
        //    发生。是不是真的回主页由外壳判断（从详情页进播放的退出后该回详情页，那一档不动）。
        _shell?.ReturnToHomeAfterPlayer();
    }

    private void StopPlayerMotion()
    {
        _inputSuspended = true;
        // 收摊这条路绕过 CompletePlayerExit，播放层这一位要在这里还 —— 一笔没人还的账就是「主页从此不再翻档」。
        _window?.SetPlayerLayer(false);
        CancelPageTransition();
        ResetCover();
        ApplyPagePose(PlayerMotion.Pose.Visible);
        EndPlayerExit();
        // 这条路会绕过 CompletePlayerExit，Mica 的那一句要在这里补上，否则「没走完退场就收摊」之后
        // 浏览态会一直少一层底（窗口露 BaseBrush）。
        RestoreBrowseBackdrop();
        _videoTarget.RetainLastFrame = false;
    }
}
