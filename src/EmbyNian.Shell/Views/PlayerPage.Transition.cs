using EmbyNian.Diagnostics;
using EmbyNian.Playback;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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
    /// 自动全屏只溶解、不缩放整页，窗口几何由进场完成后的交接单独处理。
    /// </summary>
    private void TransitionPage(bool entering, Action landed,
        Action<double>? progress = null, bool fullscreenEntry = false)
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

        var transition = fullscreenEntry
            ? PlayerMotion.FullscreenEnter(from, now)
            : PlayerMotion.Page(entering, from, now);
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

        // Mica 到这一拍才打开：整页与退场底都收干净了，底下是已经画好的浏览页，开它谁也看不见。
        // 早开一步就会在窗口改尺寸的那几拍里漏出一块壁纸采样面（2026-09-20 用户第三张截图）。
        RestoreBrowseBackdrop();

        // 客户区刚变过，这一拍读到的才是「浏览页该排多高、岛该多大」。
        SynchronizePlaybackLayout();
    }

    /// <summary>
    /// 用户关闭播放页时直接交回现成的浏览页。必须先呈现再停 mpv：保留交换链引用并不能阻止
    /// mpv 的 stop/quit 把里面的像素清空，等后端退出以后再做留帧动画已经来不及。
    /// </summary>
    internal async Task ReturnToBrowseBeforeStopAsync()
    {
        if (!_onStage || _window is not { } window) return;
        var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(this).Compositor;

        LeavePlayer();
        var generation = _windowChangeGeneration;
        CancelPageTransition();
        // 浏览页先在当前尺寸上屏，随后才还原窗口，避免新窗口里闪出旧尺寸的播放画面。
        _restoreBrowseOnExit = false;
        CompletePlayerExit();
        try
        {
            await compositor.RequestCommitAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(250));
            VideoFrameOverlay.Flush();
        }
        finally
        {
            if (!_onStage && _window == window && generation == _windowChangeGeneration)
            {
                window.RestorePlayerToBrowse();
                window.FreeSizing = false;
                SynchronizePlaybackLayout();
            }
        }
        await compositor.RequestCommitAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(250));
        VideoFrameOverlay.Flush();
    }

    private void StopPlayerMotion()
    {
        _inputSuspended = true;
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
