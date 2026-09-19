using EmbyNian.Diagnostics;
using EmbyNian.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace EmbyNian.Shell.Views;

public sealed partial class PlayerPage
{
    private bool _onStage;
    private bool _inputSuspended;
    private PlayerMotion.Transition? _pageTransition;

    /// <summary>驱动整页进退姿势的 16ms 计时器；非空即「轨道还在飞」。收摊与换向都在
    /// <see cref="CancelPageTransition"/>，别处的读数（探针的 Animating）也只认它。</summary>
    private DispatcherQueueTimer? _poseDriver;

    /// <summary>进场淡入完成那一拍的换手（收浏览层、交全屏）。一次性计时器，随轨道取消一起收。</summary>
    private DispatcherQueueTimer? _fadeHandover;

    /// <summary>逐拍驱动 pose 的节拍。与 <c>CompositionVideoTarget</c> 的画面跑动、<c>HomeFoldMotion</c>
    /// 的折档同款节拍：一条 16ms 的 <c>DispatcherQueue</c> 计时器逐拍赋本地值。</summary>
    private const int PoseTickMilliseconds = 16;

    /// <summary>
    /// 淡入到点与换手之间留出的呈现宽限。轨道的「名义到点」在 UI 线程的时钟上，compositor 真正把
    /// 最后一帧摆上屏还要排队；宽限为零时（60fps 连拍实况，2026-09-19）跳窗抢在淡入尾段呈现之前，
    /// 旧画面以七八成可见度被拉进全屏，鬼影走了六七帧才散。三帧（50ms）之后切，切在已呈现的
    /// 全不透明遮罩上，残影就是纯深色。
    /// </summary>
    private const int FadeHandoverGraceMilliseconds = 50;

    /// <summary>
    /// 进退共用一条轨道；反向操作接当前帧，不重置成全透明或全不透明。
    /// <para>
    /// <paramref name="faded"/> 是淡入完成、页面已经不透明的那一拍（进场独有；退场的淡出与位移同长，
    /// 用不上），比整条轨道落定早 <see cref="PlayerMotion.EnterMilliseconds"/> −
    /// <see cref="PlayerMotion.EnterFadeMilliseconds"/> 毫秒。<b>收浏览层＋跳全屏排在它上面，不排在进场的
    /// 第 0 拍</b>：溶解发生在没动过的窗口里，岛面换新尺寸前按旧尺寸呈现的那几帧（周围是补的底色）留在
    /// 原窗口里谁也看不见；等页面不透明了、底下垫的是近黑的加载遮罩再换窗口，旧画面的残影只是一块深色。
    /// 跳窗若排在第 0 拍，那几帧闪出的就是亮着的浏览页缩在放大后的窗口左上角（2026-09-18 用户截图）。
    /// </para>
    /// <para>
    /// <b>为什么是计时器逐拍，而不是一条代码 Storyboard（2026-09-19 定案）。</b>这半页曾经就是一条
    /// Storyboard：Opacity 一条跑了，RenderTransform 的三条属性路径却静默没跑——实机（00:45 发布构建，
    /// 窗口化与全屏各一张用户截图）页面带着 <see cref="PlayerMotion.Pose.Entering"/> 的姿势活在屏上
    /// （Scale 1.018、Y+12 不跟窗口高走地钉着），底部控制条被推出窗外 19~25px，用户看到的就是「进度条
    /// 被裁切」。这正是 2026-09-18 18:20 记下的铁律「动 RenderTransform 的动画不能用代码 Storyboard」
    /// 的第二种死法：第一次是 <c>SetTarget(变换对象)</c> 冻在起点，这次是属性路径指向元素也不到终点、
    /// 连 <c>Completed</c> 都不再落位。计时器逐拍赋本地值没有这套阴影：泵停摆只会让每一拍迟到，迟到
    /// 的那一拍照常落在 <c>At(now)</c> 该在的位置，终点拍无条件写死 <see cref="PlayerMotion.Pose.Visible"/>
    /// ——结构上就留不下残留。曲线同一（<see cref="PlayerMotion.Ease"/> 就是 XAML CubicEase.EaseOut），
    /// 60fps 连拍里逐拍取样验证过手感不变。
    /// </para>
    /// </summary>
    private void TransitionPage(bool entering, Action landed, Action? faded = null)
    {
        var now = Now;
        var from = _pageTransition?.At(now)
            ?? (entering ? PlayerMotion.Pose.Entering : PlayerMotion.Pose.Visible);
        CancelPageTransition();

        if (!HomeMotion.AnimationsEnabled || XamlRoot is null)
        {
            ApplyPagePose(PlayerMotion.Pose.Visible);
            faded?.Invoke();
            landed();
            return;
        }

        var transition = PlayerMotion.Page(entering, from, now);
        _pageTransition = transition;
        ApplyPagePose(from);
        _enterAnimating = entering && faded is not null;

        var driver = DispatcherQueue.CreateTimer();
        driver.IsRepeating = true;
        driver.Interval = TimeSpan.FromMilliseconds(PoseTickMilliseconds);
        _poseDriver = driver;
        if (faded is not null && transition.FadeDuration < transition.Duration)
            ScheduleFadeHandover(transition.FadeDuration + FadeHandoverGraceMilliseconds, driver, faded);
        driver.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_poseDriver, driver)) { driver.Stop(); return; }

            var tickNow = Now;
            if (tickNow - transition.StartedAt < transition.Duration)
            {
                ApplyPagePose(transition.At(tickNow));
                return;
            }

            // 落定。终点姿势必须无条件写死——这正是上一个版本漏掉的那一步：中途任何一拍都可能是
            // 最后一拍（泵停摆、窗口在转场中途被 FitToPicture 拉走，都是实机发生过的事）。
            _poseDriver = null;
            driver.Stop();
            CancelPageTransition();
            _enterAnimating = false;
            landed();
            ApplyPagePose(PlayerMotion.Pose.Visible);
        };
        driver.Start();
        Log.Debug(Category, $"播放页{(entering ? "进场" : "退场")}：{transition.Duration}ms");
    }

    /// <summary>进场轨道还在飞（淡入未走完）的标记；<see cref="PlayerPage.xaml.cs"/> 的
    /// OnPlaybackStarted 里那条自动全屏路见它在飞就让位，免得快启动的片子抢在淡入呈现之前跳窗。</summary>
    private bool _enterAnimating;

    /// <summary>换手发现淡入还没真正呈现时的重排上限。50ms 一班，二十班（一秒）后无论成败都放行——
    /// 宁可带着残影跳窗，也不把用户晾在不全屏的播放页里。</summary>
    private const int FadeHandoverRequeueLimit = 20;

    /// <summary>换手一次落空后重排的间隔。</summary>
    private const int FadeHandoverRequeueMilliseconds = 50;

    /// <summary>
    /// 淡入完成那一拍的一次性闹钟。轨道被换向或取消（<see cref="CancelPageTransition"/> 清空
    /// <see cref="_poseDriver"/>）之后绝不响；页面已收摊（<see cref="_onStage"/> 假）同样不响。
    /// <para>
    /// <b>响之前先验呈现，不验时间。</b>换手与 pose 驱动都在等 UI 线程：泵被占住的那几百毫秒里（实机：
    /// mpv 起链、字体扫描尾段）两班人马一起停摆，泵一恢复换手可能先于 pose 的最近几拍被派发——名义
    /// 时间到了，淡入却还没画到九成。所以 tick 先读 <c>Opacity</c>：淡入真的画到了九成以上才交窗；
    /// 没画到就改 50ms 后再问，到上限为止。
    /// </para>
    /// </summary>
    private void ScheduleFadeHandover(int milliseconds, DispatcherQueueTimer driver, Action faded)
    {
        _fadeHandover?.Stop();
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        var requeues = 0;
        timer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_poseDriver, driver) || !_onStage) return;
            if (Opacity < 0.9 && ++requeues <= FadeHandoverRequeueLimit)
            {
                timer.Interval = TimeSpan.FromMilliseconds(FadeHandoverRequeueMilliseconds);
                timer.Start();
                return;
            }
            _enterAnimating = false;
            Log.Debug(Category, $"淡入换手：重排 {requeues} 次后交窗");
            faded();
        };
        _fadeHandover = timer;
        timer.Start();
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
        _fadeHandover?.Stop();
        _fadeHandover = null;
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

    private void CompletePlayerExit()
    {
        Visibility = Visibility.Collapsed;
        Stage.Visibility = Visibility.Collapsed;
        _inputSuspended = false;
        _videoTarget.RetainLastFrame = false;
        CompleteCoverExit();
        ApplyPagePose(PlayerMotion.Pose.Visible);
    }

    private void StopPlayerMotion()
    {
        _inputSuspended = true;
        CancelPageTransition();
        ResetCover();
        ApplyPagePose(PlayerMotion.Pose.Visible);
        _videoTarget.RetainLastFrame = false;
    }
}
