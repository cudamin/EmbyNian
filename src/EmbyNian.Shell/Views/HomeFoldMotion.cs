using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 矮窗档那一段行程的动画 —— 一个对象就是一趟。矮窗档是什么、界线在哪儿见 <see cref="HomeFold"/>；
/// 判定与搬进搬出在 <see cref="HomePage.UpdateLibraryOverlay"/>（量几何）和 <see cref="ViewModels.HomeViewModel"/>
/// （改集合），这里只有屏上那两百多毫秒。
/// <para>
/// 一趟里三样东西一起走、同一段时间、同一条缓动，读起来才是「这一排升上轮播、下面几排跟着补位」而不是三件互不相干
/// 的事：媒体库那一排自己（<c>HomePage</c> 的宿主，横排里那份由它托着）、它下面那几排（补位或让位）、轮播里那块字
/// （<c>HomeBanner</c> 要让出底下一段给压上来的排）。
/// </para>
/// <para>
/// <b>位移一律是「先补回旧落点、再滑到零」。</b>布局那一跳没法做动画 —— 几何是一次算完的，元素说换位置就换位置；
/// 能动画的是它画出来的那一下：先把位移设成「新旧之差」，让它停在旧位置上，再放它滑回零。补位那几排走的正是这条路子，
/// 所以「量旧落点」必须在布局跳之前做（<c>HomePage.MeasureBeforeFold</c>）。
/// </para>
/// <para>
/// 只动 <c>Opacity</c> 与 <c>TranslateTransform.Y</c>，只走 XAML Storyboard —— 为什么不用 composition，见
/// <see cref="HomeMotion.Reveal"/> 里 2026-09-16 那一段（<c>GetElementVisual</c> 与框架自己的 <c>Scale</c>
/// 抢同一份 visual，界面线程当场带走了进程）。同一个元素上同一支属性只允许有一支动画，所以一趟只开一份
/// <see cref="Storyboard"/>、收尾只有 <see cref="Stop"/> 一处。
/// </para>
/// </summary>
internal sealed class HomeFoldMotion
{
    /// <summary>这一趟走多久。比播放页那 220 毫秒长一点：那一段溶解的是两层界面，这一段是一排卡要挪两百多像素，
    /// 太短会像被谁拽过去的。</summary>
    private static readonly TimeSpan Span = TimeSpan.FromMilliseconds(260);

    /// <summary>这一趟走多少毫秒（日志读数用，别处别拿它当布局尺寸）。</summary>
    internal static int DurationMilliseconds => (int)Span.TotalMilliseconds;

    /// <summary>落地前那一下换手有多长：滑到位的那一份淡出、等在落点上的那一份淡入，两下重叠着走。</summary>
    private static readonly TimeSpan HandoffSpan = TimeSpan.FromMilliseconds(110);

    private readonly Storyboard _board = new();
    private readonly List<TranslateTransform> _drifts = [];
    private readonly List<FrameworkElement> _faded = [];

    /// <summary>系统里关掉了动画（辅助功能那一档）的机器上什么都不走：调用方量完直接落定。</summary>
    internal static bool Enabled => HomeMotion.AnimationsEnabled;

    /// <summary>
    /// 一支位移从 <paramref name="from"/> 走到 <paramref name="to"/>（像素，正值向下，相对它自己排定的位置）。
    /// 本地值先写死成起点 —— 动画起来之前那一帧就该站在旧落点上，不然会先闪一下新位置。
    /// </summary>
    internal void Slide(TranslateTransform drift, double from, double to)
    {
        drift.Y = from;
        _drifts.Add(drift);
        Move(drift, "Y", from, to, TimeSpan.Zero, Span);
    }

    /// <summary>
    /// 淡入／淡出同一支不透明度，和位移同一条缓动。<paramref name="delay"/>、<paramref name="duration"/>
    /// 不给就是全程：翻档里大多数淡变走全程，落地前换手那两下各压一小段（见 <see cref="FadeLate"/> 与
    /// <c>HomePage</c> 迟一拍那一趟）。
    /// </summary>
    internal void Fade(FrameworkElement element, double from, double to, TimeSpan? delay = null, TimeSpan? duration = null)
    {
        element.Opacity = from;
        if (!_faded.Contains(element)) _faded.Add(element);
        Move(element, "Opacity", from, to, delay ?? TimeSpan.Zero, duration ?? Span);
    }

    /// <summary>
    /// 落地前的淡出：压在最后一百来毫秒里。回默认那一趟宿主托着那一排卡一路滑到横排，到了才让位 ——
    /// 一路不透明（半路上淡出会露出底下还没就位的东西），最后一下交出去。
    /// </summary>
    internal void FadeLate(FrameworkElement element) =>
        Fade(element, 1, 0, Span - HandoffSpan, HandoffSpan);

    /// <summary>从暗里浮出来：给「迟一拍才实到」的那一份用，接住正落下来的宿主。</summary>
    internal void FadeIn(FrameworkElement element) =>
        Fade(element, 0, 1, delay: TimeSpan.Zero, duration: Span - HandoffSpan);

    /// <summary>
    /// 开跑。走到头喊一声 <paramref name="landed"/> —— 半路被 <see cref="Stop"/> 收掉的不喊：那种时候页面上另一次
    /// 摆放已经在路上了，落地该做的事对它没意义。
    /// </summary>
    internal void Play(Action? landed = null)
    {
        if (_board.Children.Count == 0)
        {
            landed?.Invoke();
            return;
        }

        _board.Completed += (_, _) =>
        {
            Stop();
            landed?.Invoke();
        };
        _board.Begin();
    }

    /// <summary>
    /// 收工：停在落点上。走到一半被打断（窗口又拉回去了、页面走了）也走这里。
    /// <para>
    /// **本地值必须写死一遍**：<c>Storyboard.Stop()</c> 会把动画值放掉、让属性回落到进场时写的那些「旧落点」——
    /// 不清就是停在半路上跳一格。先放动画、再写值，顺序反过来白写。
    /// </para>
    /// </summary>
    internal void Stop()
    {
        _board.Stop();
        foreach (var drift in _drifts) drift.Y = 0;
        foreach (var element in _faded) element.Opacity = 1;
        _drifts.Clear();
        _faded.Clear();
    }

    /// <summary>
    /// 一个元素身上那支位移。没有就装一支；它自己已经摆了别的变换（缩放之类）就交回空 —— 那一件这一趟不动位置，
    /// 与 <see cref="HomeMotion"/> 的进场动画同一条规矩：不抢别人的变换（抢了就是两处各按各的理解写同一个属性）。
    /// <para>
    /// **「没有」在 WinUI 3 里几乎不存在**：RenderTransform 出厂就是一支单位 MatrixTransform 而不是空，所以
    /// 「见空就装一支」实际永远走不进去 —— 真正要动位置的元素（宿主、货架模板根、轮播的字块）都得在标记里
    /// 把 TranslateTransform 写出来，这里只是把它认领回来。这个坑 2026-09-18 用「宿主变换=MatrixTransform」
    /// 那行日志实锤过。
    /// </para>
    /// </summary>
    internal static TranslateTransform? DriftOf(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform existing) return existing;
        if (element.RenderTransform is not null) return null;

        var drift = new TranslateTransform();
        element.RenderTransform = drift;
        return drift;
    }

    /// <summary>
    /// 一排货架的排卡上沿落在哪儿（量在 <paramref name="space"/> 这个坐标系里）—— 压上那一趟拿它当起飞点，
    /// 回默认那一趟拿它当落点。
    /// <para>
    /// 从这一排的子树里把那条横向带子（<see cref="ShelfStrip"/>）翻出来直接量：一排的顶、牌子的高、牌子和排卡之间
    /// 那道空当叠出来的算式在 <c>HomePage</c> 那条界线里已经有一份，这里要的是同一个答案，不再抄第二份。
    /// 模板还没上手、那一排还没实到屏上时交回空，调用方各有退路。
    /// </para>
    /// </summary>
    internal static double? StripTop(FrameworkElement row, UIElement space) =>
        Find<ShelfStrip>(row) is { } strip
            ? strip.TransformToVisual(space).TransformPoint(new Windows.Foundation.Point(0, 0)).Y
            : null;

    /// <summary>子树里第一件 <typeparamref name="T"/>。一排的模板根往里找 —— 带子在哪一层由模板说了算，别写死。</summary>
    internal static T? Find<T>(DependencyObject node) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(node, index);
            if (child is T found) return found;
            if (Find<T>(child) is { } deeper) return deeper;
        }

        return null;
    }

    private void Move(DependencyObject target, string property, double from, double to, TimeSpan delay, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            BeginTime = delay,
            Duration = new Duration(duration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        _board.Children.Add(animation);
    }
}
