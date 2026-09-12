using EmbyNian.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 一条横向卡片带，翻页而不是拖动。主页的每个分区、详情页的 单集、全部剧季、演职人员、更多类似 都是这个控件。
/// <para>
/// 三处以前各写一遍同样的 <c>ScrollView</c> + <c>ItemsRepeater</c>，底下各挂一条水平滚动条。要看后面的
/// 卡片就得把指针放到那条几像素高的条上拖 —— 「下方拖动的这种不是很好用」。现在滚动条藏掉，指针进到带上
/// 时两边浮出箭头，一次翻整整一屏卡片。
/// </para>
/// <para>
/// 翻多远是这里唯一算得上规则的东西，所以它不在这个文件里：四条规则（一屏放得下几张卡就翻几张、翻过去
/// 落在哪、哪个箭头该在屏上、要让第几张卡露出来得滚到哪儿）都在 Core 的
/// <see cref="EmbyNian.Infrastructure.CardStrip"/> 上，由单元测试钉着。这里剩下的是量：视口多宽、卡片
/// 间距多大、还有多少可滚 —— 只有屏上问得出来，所以 <see cref="Probe"/> 只问这些。
/// </para>
/// </summary>
public sealed partial class ShelfStrip : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(object),
        typeof(ShelfStrip),
        new PropertyMetadata(null, OnItemsChanged));

    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate),
        typeof(DataTemplate),
        typeof(ShelfStrip),
        new PropertyMetadata(null, OnTemplateChanged));

    public static readonly DependencyProperty ItemSpacingProperty = DependencyProperty.Register(
        nameof(ItemSpacing),
        typeof(double),
        typeof(ShelfStrip),
        new PropertyMetadata(16d, OnSpacingChanged));

    public static readonly DependencyProperty FocusIndexProperty = DependencyProperty.Register(
        nameof(FocusIndex),
        typeof(int),
        typeof(ShelfStrip),
        new PropertyMetadata(0, OnFocusIndexChanged));

    /// <summary>
    /// 这一带压在一张剧照上，而不是站在页面那张纸上 —— 集页把同季那一带摆在头图底下的画面里
    /// （见 <c>DetailPage.PlaceEpisodes</c>）。这里只负责把它传给每张卡，见
    /// <see cref="PosterCard.OnScrim"/>。
    /// </summary>
    public static readonly DependencyProperty OnScrimProperty = DependencyProperty.Register(
        nameof(OnScrim),
        typeof(bool),
        typeof(ShelfStrip),
        new PropertyMetadata(false, OnScrimSwitched));

    /// <summary>
    /// 箭头只盖卡片的图片，不盖图片下面那两行字 —— 带的高度里有 <see cref="CardSize.Chrome"/> 是那两行
    /// 的。这个下限管的是还没量过的那一瞬间：高度是 0 时按 0 减出来是负数，按钮会直接消失。
    /// </summary>
    private const double MinBandHeight = 48;

    /// <summary>指针在带上。箭头是悬停才出现的，同卡片上那排按钮。</summary>
    private bool _hover;
    private bool _live;
    private Storyboard? _prevAnimation;
    private Storyboard? _nextAnimation;

    /// <summary>
    /// 「鼠标移出窗口后不会自动恢复」：指针到底还在不在带上，按 OS 说的算，不光信 <c>PointerExited</c>。
    /// 为什么事件不够用，见 <see cref="HoverWatch"/>。
    /// </summary>
    private readonly HoverWatch _watch;

    /// <summary>
    /// 上一次量到的卡片间距（卡片宽 + 间隔）。记下来而不是每次现问：翻到后面时第一张卡已经被虚拟化掉，
    /// <c>TryGetElement(0)</c> 会返回空，而那时正是最需要知道一页多宽的时候。
    /// </summary>
    private double _pitch;

    /// <summary>自检用：最近一次翻页请求的目标位置，-1 表示还没翻过。</summary>
    private double _requested = -1;

    /// <summary>
    /// 还没落实的定位请求（第几张卡），-1 表示没有。
    /// <para>
    /// 记着而不是当场滚过去：<see cref="FocusOn"/> 是数据刚填进来的那一刻叫的，那时带上一张卡都还没长出
    /// 来，量不到卡片宽度也就算不出位置。带或卡片行的尺寸一变（正是卡片长出来的那一刻）再试一次。
    /// </para>
    /// </summary>
    private int _focus = -1;

    /// <summary>
    /// 最近一次落在这条带里的焦点项。横向 <see cref="ItemsRepeater"/> 是虚拟化的：当方向键把焦点推到
    /// 视口边缘时，下一张卡可能还没有容器，不能只依赖 WinUI 的候选查找。记下索引后，
    /// <see cref="OnNoFocusCandidateFound"/> 可以创建目标容器、让它进视口，再把焦点交给它。
    /// </summary>
    private int _focusedIndex = -1;

    /// <summary>正在等待布局完成后重试的焦点索引，-1 表示没有。</summary>
    private int _pendingFocusedIndex = -1;

    /// <summary>防止同一轮布局里重复排队焦点重试。</summary>
    private bool _focusRetryQueued;

    public ShelfStrip()
    {
        InitializeComponent();

        _watch = new HoverWatch(Root, up =>
        {
            _hover = up;
            SyncArrows();
        });

        SizeChanged += OnContentResized;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    /// <summary>卡片之间的间隔。翻页的步长按它算，所以它不只是 StackLayout 的一个数。</summary>
    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <summary>
    /// 开场先看第几张卡。详情页的 单集 带绑它：在某一集自己的页面上，这条带列的是这一集的邻居，那就该
    /// 从这一集开始，而不是让人从第一集翻过来。0 是「别动」—— 见 <see cref="ApplyFocus"/>。
    /// <para>
    /// 做成依赖属性而不是让页面去订阅 ViewModel 的 PropertyChanged：绑定写在 XAML 里，「这条带看哪张卡」
    /// 就和「这条带装哪些卡」并排摆着，读一眼就完；而卡片还没长出来时定位落实不了，
    /// <see cref="_focus"/> 那套延迟重试本来就在这个控件里。
    /// </para>
    /// </summary>
    public int FocusIndex
    {
        get => (int)GetValue(FocusIndexProperty);
        set => SetValue(FocusIndexProperty, value);
    }

    private StackLayout Rows => (StackLayout)Repeater.Layout;

    /// <inheritdoc cref="OnScrimProperty"/>
    public bool OnScrim
    {
        get => (bool)GetValue(OnScrimProperty);
        set => SetValue(OnScrimProperty, value);
    }

    /// <summary>
    /// 这一档交给每张卡（<see cref="PosterCard.OnScrim"/>）。卡片是回收着用的，所以两处都要：这里过一遍
    /// 已经建出来的，<see cref="OnElementPrepared"/> 管之后建出来的和回收回来的那些。
    /// </summary>
    private static void OnScrimSwitched(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var strip = (ShelfStrip)sender;

        for (var index = 0; index < strip.ItemCount; index++)
            if (strip.Repeater.TryGetElement(index) is { } element) strip.PaintInk(element);
    }

    /// <summary>
    /// 一张卡在这条带里该用哪套墨。卡在模板里，所以从容器往下找 —— 先看逻辑内容那一路
    /// （<c>ContentControl.Content</c>），因为刚建出来的容器还没套上自己的模板，那一刻可视树里一个孩子都没有：
    /// 只走可视树的那一版会漏掉除第一张之外的每一张卡，屏上是浅色主题下几张卡的片名整行消失。
    /// </summary>
    private void PaintInk(DependencyObject container)
    {
        if (Card(container) is { } found) found.OnScrim = OnScrim;

        static PosterCard? Card(DependencyObject? node)
        {
            switch (node)
            {
                case null: return null;
                case PosterCard card: return card;
                case ContentControl { Content: DependencyObject content } when Card(content) is { } inside:
                    return inside;
            }

            var children = VisualTreeHelper.GetChildrenCount(node);
            for (var index = 0; index < children; index++)
                if (Card(VisualTreeHelper.GetChild(node, index)) is { } inside) return inside;

            return null;
        }
    }

    // ---- 规则 -------------------------------------------------------------------
    //
    // 翻多远、落在哪、哪个箭头该在屏上、要让第几张卡露出来得滚到哪儿 —— 四条规则都在 Core 的
    // EmbyNian.Infrastructure.CardStrip 上，由单元测试钉着（CardStripTests）。留在这里的是量：视口多宽、
    // 卡片间距多大、还有多少可滚，只有屏上问得出来。

    /// <summary>
    /// 翻一页并返回请求的位置。<paramref name="direction"/> 是 -1 或 1。
    /// <para>
    /// 请求的位置记在 <see cref="_requested"/> 上：<c>ScrollTo</c> 是带动画的，问完之后立刻去读
    /// <c>HorizontalOffset</c> 读到的还是动画起点，而自检要问的正是「点下去有没有要到那个位置」。
    /// </para>
    /// </summary>
    private double Page(int direction)
    {
        var scrollable = Scroller.ScrollableWidth;
        var target = CardStrip.TargetFor(Scroller.HorizontalOffset, direction, Step, scrollable);

        _requested = target;

        // 没有余量就不去请求。翻不动的时候箭头本来就是收起的，所以这条只在一种情况下管事：自检里这份控件
        // 还没进过树，视口和内容都是 0，而 ScrollTo 要的是模板里那个 ScrollPresenter。
        if (scrollable > 0)
            Scroller.ScrollTo(target, Scroller.VerticalOffset,
                new ScrollingScrollOptions(HomeMotion.AnimationsEnabled ? ScrollingAnimationMode.Auto : ScrollingAnimationMode.Disabled));

        return target;
    }

    /// <summary>一页多远，按当前视口和量到的卡片间距。</summary>
    private double Step =>
        CardStrip.StepFor(Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : ActualWidth, Measure(), ItemSpacing);

    /// <summary>
    /// 把第 <paramref name="index"/> 张卡定位到屏上。<see cref="FocusIndex"/> 一变就叫，也就是数据刚填进来
    /// 的那一刻，落实不了就记下来等布局 —— 见 <see cref="_focus"/>。
    /// </summary>
    internal void FocusOn(int index)
    {
        _focus = index;
        ApplyFocus();
    }

    /// <summary>
    /// 落实一次定位请求。卡片还没长出来（量不到间距，或还没有可滚动的余量）就留着请求不动，下一次尺寸
    /// 变化再来 —— 那时卡片正好长出来了。落实过就把请求清掉，否则人手动翻页之后会被拽回去。
    /// </summary>
    private void ApplyFocus()
    {
        if (_focus <= 0)
        {
            _focus = -1;
            return;
        }

        var pitch = Measure();
        if (pitch <= 0 || Scroller.ScrollableWidth <= 0) return;

        ScrollToIndex(_focus);
        _focus = -1;
    }

    /// <summary>
    /// 直接滚到第 <paramref name="index"/> 张卡并返回请求的位置。不带动画：这是「页面打开时就该在这里」，
    /// 不是一次人做出来的翻页。
    /// </summary>
    private double ScrollToIndex(int index)
    {
        var viewport = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : ActualWidth;
        var target = CardStrip.OffsetFor(index, Measure(), viewport, Scroller.ScrollableWidth);

        _requested = target;

        if (target > 0)
            Scroller.ScrollTo(target, Scroller.VerticalOffset, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));

        return target;
    }

    /// <summary>
    /// 卡片间距：第一张卡的宽度加一个间隔。量到就记下来，量不到就用记着的那个 —— 见 <see cref="_pitch"/>。
    /// </summary>
    private double Measure()
    {
        if (Repeater.TryGetElement(0) is FrameworkElement first && first.ActualWidth > 0)
            _pitch = first.ActualWidth + ItemSpacing;

        return _pitch;
    }

    private void SyncArrows()
    {
        var arrows = CardStrip.ArrowsFor(Scroller.HorizontalOffset, Scroller.ScrollableWidth, _hover);

        AnimateArrow(Prev, PrevMotion, arrows.Prev);
        AnimateArrow(Next, NextMotion, arrows.Next);
    }

    /// <summary>48px 圆形按钮始终放在封面中线，不覆盖标题和续播信息。</summary>
    private void SyncBand()
    {
        var band = Math.Max(MinBandHeight, ActualHeight - CardSize.Chrome - Scroller.Padding.Top);
        var top = Scroller.Padding.Top + (band - MinBandHeight) / 2;

        Prev.Height = Next.Height = MinBandHeight;
        Prev.Margin = new Thickness(8, top, 0, 0);
        Next.Margin = new Thickness(0, top, 8, 0);
    }

    private void AnimateArrow(Button button, TranslateTransform shift, bool show)
    {
        // ViewChanged 一帧会来多次，相同目标不重复启动动画。
        if (button.IsHitTestVisible == show) return;

        var fromOpacity = button.Opacity;
        var fromX = shift.X;
        var hiddenX = ReferenceEquals(button, Prev) ? -8 : 8;
        var wasHidden = button.Visibility == Visibility.Collapsed;
        SetArrowAnimation(button, null);
        button.IsHitTestVisible = show;

        void Settle()
        {
            button.Opacity = show ? 1 : 0;
            shift.X = show ? 0 : hiddenX;
            button.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!_live || XamlRoot is null || !HomeMotion.AnimationsEnabled)
        {
            Settle();
            return;
        }

        button.Visibility = Visibility.Visible;
        var board = new Storyboard();
        Add(button, "Opacity", wasHidden ? 0 : fromOpacity, show ? 1 : 0);
        Add(shift, "X", wasHidden ? hiddenX : fromX, show ? 0 : hiddenX);
        SetArrowAnimation(button, board);
        board.Completed += (_, _) =>
        {
            if (!ReferenceEquals(ArrowAnimation(button), board)) return;
            Settle();
            SetArrowAnimation(button, null);
        };
        board.Begin();

        void Add(DependencyObject target, string path, double from, double to)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, path);
            board.Children.Add(animation);
        }
    }

    private Storyboard? ArrowAnimation(Button button) => ReferenceEquals(button, Prev) ? _prevAnimation : _nextAnimation;

    private void SetArrowAnimation(Button button, Storyboard? animation)
    {
        ArrowAnimation(button)?.Stop();
        if (ReferenceEquals(button, Prev)) _prevAnimation = animation;
        else _nextAnimation = animation;
    }

    // ---- 事件 -------------------------------------------------------------------

    private void OnPrevClicked(object sender, RoutedEventArgs e) => Page(-1);

    private void OnNextClicked(object sender, RoutedEventArgs e) => Page(1);

    /// <summary>
    /// 焦点落到横带里的卡片（包括海报内部的播放/已看/收藏按钮）时，把那张卡横着露出来。仅在焦点确实属于
    /// Repeater 项时记录索引；翻页箭头是鼠标可点但不应加入焦点历史。
    /// <para>
    /// 露出这件事在这条带自己的滚动视图里做完，一句 <c>BringIntoView</c> 都不往外发 —— 「点击主页继续观看、
    /// 媒体库、最近添加的封面之后会先跳转到页面下方，然后才会进入页面」正是往外发的那一版，理由和这一版的规则
    /// 都在 <see cref="CardStrip.RevealFor"/> 上。
    /// </para>
    /// </summary>
    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (FindRepeaterElement(e.OriginalSource as DependencyObject) is not { } element) return;

        var index = Repeater.GetElementIndex(element);
        if (index < 0) return;

        _focusedIndex = index;
        _pendingFocusedIndex = -1;
        _focusRetryQueued = false;

        BringFocusedElementIntoView(element);
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement element) return;

        // Item containers are supplied by the DataTemplate (normally a Button). Keep the accessibility
        // tree useful even when the control is virtualized: Narrator can announce both the item's place
        // in this shelf and the total count without forcing every card to be realized.
        AutomationProperties.SetPositionInSet(element, args.Index + 1);
        AutomationProperties.SetSizeOfSet(element, ItemCount);

        // 这一带压在剧照上时（集页那一带）卡片底下那两行字要换墨。放在这里而不是只在开关翻转时过一遍：
        // 容器是回收着用的，翻页翻出来的那些是「之后才建的」，漏掉就会有几张卡的字读不出来。
        PaintInk(element);
    }

    /// <summary>
    /// ItemsRepeater 不提供默认的键盘/遥控器交互策略。通常由 XYFocus 找到下一张卡；当下一张卡被
    /// 虚拟化掉时会走到这里。只接管「带内仍有目标」的左右移动，真正到达带头/带尾时保持未处理，
    /// 让外层页面有机会把焦点移到下一块内容。
    /// </summary>
    private void OnNoFocusCandidateFound(UIElement sender, NoFocusCandidateFoundEventArgs e)
    {
        if (_focusedIndex < 0 || e.Direction is not (FocusNavigationDirection.Left or FocusNavigationDirection.Right))
            return;

        var target = e.Direction == FocusNavigationDirection.Left ? _focusedIndex - 1 : _focusedIndex + 1;
        if (target < 0 || target >= ItemCount) return;

        e.Handled = true;
        FocusItem(target);
    }

    /// <summary>
    /// Home/End 是电视遥控器和键盘用户寻找横带两端的快捷路径；PageUp/PageDown 则按一整页移动，
    /// 并把焦点放在新视口里的第一/最后一张卡。普通左右键仍由 XYFocus 负责，避免每次移动都打断
    /// WinUI 自己的焦点动画。
    /// </summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_focusedIndex < 0 || ItemCount == 0) return;

        switch (e.Key)
        {
            case VirtualKey.Home:
                e.Handled = true;
                FocusItem(0);
                break;
            case VirtualKey.End:
                e.Handled = true;
                FocusItem(ItemCount - 1);
                break;
            case VirtualKey.PageUp:
                e.Handled = true;
                FocusByPage(-1);
                break;
            case VirtualKey.PageDown:
                e.Handled = true;
                FocusByPage(1);
                break;
        }
    }

    /// <summary>页面被移除时清掉虚拟化容器引用，避免下一次挂回页面时把焦点送回旧数据。</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _live = false;
        _focusedIndex = -1;
        _pendingFocusedIndex = -1;
        _focusRetryQueued = false;

        // 带自己离开了树，指针在不在它上面已经无所谓了 —— 留着的话十赫兹那一拍还会继续问一个量不到的矩形。
        _watch.Leave();
        SetArrowAnimation(Prev, null);
        SetArrowAnimation(Next, null);
        Prev.Opacity = Next.Opacity = 0;
        Prev.Visibility = Next.Visibility = Visibility.Collapsed;
        Prev.IsHitTestVisible = Next.IsHitTestVisible = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _live = true;
        // Loaded 可能发生在数据和首轮布局之后；若有排队请求，马上再试一次。
        ApplyPendingFocusedItem();
    }

    /// <summary>
    /// 当前焦点落到带内的一张卡上时，把它横着露出来 —— 在这条带自己的滚动视图里做完，见
    /// <see cref="CardStrip.RevealFor"/>。
    /// <para>
    /// 位置按索引算，不按元素量：这里最要紧的一次调用来自 <see cref="FocusItem"/>，那时目标容器可能是
    /// <c>GetOrCreateElement</c> 刚建出来的，还没量过，问它自己在哪儿问到的是 0。一条带里的卡是同一个模板，
    /// 所以第 n 张的左边沿就是 n 个间距 —— 那个数现在就算得出来。
    /// </para>
    /// </summary>
    private void BringFocusedElementIntoView(UIElement element)
    {
        if (!IsLoaded || element.XamlRoot is null)
        {
            QueueFocusRetry(_focusedIndex);
            return;
        }

        var index = Repeater.GetElementIndex(element);
        var viewport = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : ActualWidth;
        var target = CardStrip.RevealFor(
            index, Measure(), ItemSpacing, Scroller.HorizontalOffset, viewport, Scroller.ScrollableWidth);

        if (target >= 0)
            Scroller.ScrollTo(target, Scroller.VerticalOffset,
                new ScrollingScrollOptions(HomeMotion.AnimationsEnabled ? ScrollingAnimationMode.Auto : ScrollingAnimationMode.Disabled));
    }

    /// <summary>
    /// 将焦点交给指定索引。GetOrCreateElement 是 ItemsRepeater 为虚拟化项提供的官方入口，创建后
    /// 先 BringIntoView 再 Focus，确保手柄/键盘焦点不会落在视口外的不可见容器上。
    /// </summary>
    private void FocusItem(int index)
    {
        if (index < 0 || index >= ItemCount) return;

        _focusedIndex = index;

        UIElement? element;
        try
        {
            element = Repeater.TryGetElement(index) ?? Repeater.GetOrCreateElement(index);
        }
        catch (Exception)
        {
            QueueFocusRetry(index);
            return;
        }

        if (element is null)
        {
            QueueFocusRetry(index);
            return;
        }

        _pendingFocusedIndex = -1;
        BringFocusedElementIntoView(element);

        // Realised 元素通常可以立即接收焦点；若仍在布局中，下一帧再试。Focus 返回 false 而不是抛异常，
        // 所以这里必须检查返回值。
        if (!element.Focus(FocusState.Keyboard)) QueueFocusRetry(index);
    }

    private void FocusByPage(int direction)
    {
        var step = Step;
        var targetOffset = CardStrip.TargetFor(Scroller.HorizontalOffset, direction, step, Scroller.ScrollableWidth);

        if (step <= 0 || Scroller.ScrollableWidth <= 0)
        {
            FocusItem(direction < 0 ? 0 : ItemCount - 1);
            return;
        }

        // 由当前滚动位置反推一个近似索引，再用实际容器的索引修正。即使卡片宽度尚未量到，
        // FocusItem 也会在下一轮布局重试，因此不会把焦点丢到带外。
        var pitch = Measure();
        var estimate = pitch > 0
            ? (int)Math.Round(targetOffset / pitch, MidpointRounding.ToZero)
            : (direction < 0 ? _focusedIndex - 1 : _focusedIndex + 1);

        estimate = Math.Clamp(estimate, 0, ItemCount - 1);
        FocusItem(estimate);
    }

    private int ItemCount => Repeater.ItemsSourceView?.Count ?? 0;

    /// <summary>从 GotFocus 的原始元素向上找出 ItemsRepeater 的实际项容器。</summary>
    private UIElement? FindRepeaterElement(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is UIElement element && Repeater.GetElementIndex(element) >= 0) return element;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void QueueFocusRetry(int index)
    {
        if (index < 0 || index >= ItemCount) return;

        _pendingFocusedIndex = index;
        if (_focusRetryQueued) return;

        _focusRetryQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _focusRetryQueued = false;
            ApplyPendingFocusedItem();
        });
    }

    private void ApplyPendingFocusedItem()
    {
        var index = _pendingFocusedIndex;
        if (index < 0) return;

        _pendingFocusedIndex = -1;
        FocusItem(index);
    }

    /// <summary>
    /// 指针进到带上。<c>PointerMoved</c> 也接到这里：带是数据到了之后才长出卡片的，指针一直停在原处不动
    /// 的话 <c>PointerEntered</c> 早就过去了，而 <c>PointerMoved</c> 是下一次挪动的第一手消息。
    /// </summary>
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => _watch.Enter();

    /// <summary>
    /// 指针离开。位置要现查一遍，同 <see cref="PosterCard.OnPointerExited"/>：这个事件会从子元素冒上来，
    /// 而指针踩到刚浮出来的箭头上算「离开了带上的那张卡」，直接信它就会把手底下的按钮收掉。
    /// <para>
    /// 也只是快路径：指针从带上直接移出窗口时，这个事件报的位置还在带里，收不掉的那一次交给
    /// <see cref="HoverWatch"/>。
    /// </para>
    /// </summary>
    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root).Position;

        if (point.X >= 0 && point.Y >= 0 && point.X < Root.ActualWidth && point.Y < Root.ActualHeight)
            return;

        _watch.Leave();
    }

    private void OnViewChanged(ScrollView sender, object e) => SyncArrows();

    /// <summary>
    /// 带、视口或卡片行的尺寸变了。三个来源都接同一个处理器，因为要重算的东西是一样的：箭头多高（带的
    /// 高度变了）、还能不能翻（窗口宽了之后可能已经一屏放得下）。
    /// </summary>
    private void OnContentResized(object sender, SizeChangedEventArgs e)
    {
        SyncBand();
        SyncArrows();

        // 卡片刚长出来的那一刻也是这里：ApplyFocus 之前算不出位置，现在算得出。
        ApplyFocus();
        ApplyPendingFocusedItem();
    }

    private static void OnItemsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var strip = (ShelfStrip)sender;

        strip.Repeater.ItemsSource = args.NewValue;

        // 换了一批卡片，上一批量出来的间距不作数了 —— 主页每条带的卡片形状可以不一样（海报 2:3、剧照
        // 16:9），而这个控件是同一个。
        strip._pitch = 0;
        strip._focusedIndex = -1;
        strip._pendingFocusedIndex = -1;
        strip._focusRetryQueued = false;
        strip.SyncArrows();
    }

    private static void OnTemplateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ShelfStrip)sender).Repeater.ItemTemplate = args.NewValue;

    private static void OnSpacingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var strip = (ShelfStrip)sender;

        strip.Rows.Spacing = (double)args.NewValue;
        strip._pitch = 0;
    }

    private static void OnFocusIndexChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ShelfStrip)sender).FocusOn((int)args.NewValue);

    // ---- 自检 -------------------------------------------------------------------

    /// <summary>
    /// 这条带只在屏上才问得出来的那几件：这份标记解析得了、间隔真进了布局、还没量过时两个箭头是收着的、点下
    /// 去和「让第几张露出来」这两条路真的把要到的位置记下来了。
    /// <para>
    /// 翻多远、落在哪、哪个箭头该在屏上、露出第几张卡要滚到哪儿 —— 这四条规则从前也在这里，拿几个写死的数走
    /// 一遍。现在它们在 Core 的 <see cref="CardStrip"/> 上，由 <c>CardStripTests</c> 钉着：同样的数字，钉在一
    /// 个每次构建都跑、不用开窗口的地方。留在这儿的是那几个数拿不到的东西。
    /// </para>
    /// <para>
    /// 顺带证明的那件事同样值钱：<c>new ShelfStrip()</c> 会把这份标记解析一遍，里面每个资源键（
    /// <c>DefaultButtonStyle</c>、<c>EgOnScrimBrush</c>、那六个改掉的按钮状态键）解析不了就在这里抛，而
    /// 不是等到用户第一次划过某条带。
    /// </para>
    /// </summary>
    internal static (bool Ok, string Detail) Probe()
    {
        var strip = new ShelfStrip { ItemSpacing = 14 };
        var spacing = Math.Abs(strip.Rows.Spacing - 14) < 0.01;

        // 还没量过、指针也不在上面：两个箭头都该是收起的，高度退到下限而不是负数。
        strip.SyncBand();
        strip.SyncArrows();
        var quiet = !strip.PrevShown && !strip.NextShown && strip.Prev.Height >= MinBandHeight;

        // 点下去这条路本身：Page 记下要到的位置，而这一份控件没有余量可翻，所以要到的位置是 0。
        var clicked = strip.Page(1) == 0 && strip.Requested == 0;

        // 定位请求同理：这一份控件量不到卡片，所以请求留着等布局，而不是当场滚到 0 把它吞掉。
        strip.FocusOn(5);
        var pending = strip.PendingFocus == 5;

        var ok = spacing && quiet && clicked && pending;

        return (ok,
            $"标记解析通过；间隔 {strip.Rows.Spacing:0} 已进布局；还没量过时两个箭头收起、高 "
                + $"{strip.Prev.Height:0}；翻页要到 {strip.Requested:0}；定位第 6 张的请求留着（"
                + $"{strip.PendingFocus}）等布局。四条算术规则见 CardStripTests");
    }

    /// <summary>自检用：<c>Visibility</c> 是这条规则唯一看得见的结果。</summary>
    internal bool PrevShown => Prev.Visibility == Visibility.Visible;

    internal bool NextShown => Next.Visibility == Visibility.Visible;

    /// <summary>自检用：最近一次翻页要到的位置。</summary>
    internal double Requested => _requested;

    /// <summary>自检用：还没落实的定位请求，-1 表示没有。<see cref="FocusIndex"/> 是要到第几张，这是还没到的那一张。</summary>
    internal int PendingFocus => _focus;
}
