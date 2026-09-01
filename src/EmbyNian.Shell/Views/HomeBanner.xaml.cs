using System.ComponentModel;
using EmbyNian.Emby;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

// 上面那两个命名空间各有一个 DispatcherQueueTimer。Windows.System 在这里只为 VirtualKey 而来，计时器要的是
// Microsoft.UI 那个（WinUI 的 DispatcherQueue 发的就是它），所以指名一次，省下每处的全名。
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 主页最上面那条大图轮播 —— 「参考"主页轮播大图版-misty-4.9.css"给主页轮播功能」。
/// <para>
/// 一张幻灯片是一条剧照加压在它左边的字（<see cref="BannerSlide"/>），这个控件是那条带：谁在台上、什么时候
/// 换、换的时候怎么过去。哪些条目上得了台、一共几张、带该多高，都是 <see cref="HomeCarousel"/> 里的纯函数，
/// 由 Core 的测试盯着；这里剩下的是屏上那部分。
/// </para>
/// <para>
/// 两层 <c>Image</c> 轮着上而不是一层换 <c>Source</c>：换一张时新的淡入、旧的淡出，中间不会闪一下底色。带上
/// 同时解码的只有台上这张和它左右各一张（<see cref="Prefetch"/>），其余的 <see cref="BannerSlide.Release"/>
/// 掉 —— 一张整宽的剧照是兆字节量级的画面，八张一起留着就是几十兆。
/// </para>
/// <para>
/// 动画全在代码里搭：<c>Storyboard.TargetName</c> 只在运行时解析，写在标记里编译期一声不响、跑起来才抛，而
/// 这条带是主页第一眼看见的东西。<see cref="Probe"/> 是它在 <c>--self-check</c> 里的那一份读数。
/// </para>
/// </summary>
public sealed partial class HomeBanner : UserControl
{
    public static readonly DependencyProperty SlidesProperty = DependencyProperty.Register(
        nameof(Slides),
        typeof(IReadOnlyList<BannerSlide>),
        typeof(HomeBanner),
        new PropertyMetadata(null, OnSlidesChanged));

    /// <summary>
    /// 字从自己的位置底下多远抬起来。misty 是 30px 配 2.5 秒，桌面上那个太慢也太远。
    /// <para>不叫 <c>Drop</c>：<see cref="UIElement"/> 上已经有一个 <c>Drop</c> 事件，同名会把它藏起来。</para>
    /// </summary>
    private const double TextDrop = 22;

    /// <summary>徽标反过来，从上面落下来一点。</summary>
    private const double LogoDrop = -12;

    private const double DotWidth = 22;
    private const double DotHeight = 4;

    /// <summary>换图的那一下。两层的淡入淡出同时走，所以这也是「上一张还看得见」的时长。</summary>
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(420);

    /// <summary>一行字抬起来用多久。</summary>
    private static readonly TimeSpan Lift = TimeSpan.FromMilliseconds(520);

    /// <summary>下一行比上一行晚多久起。misty 那份是 .4/.6/.8 秒，四行错完接近一秒，这里压到四行 210 毫秒。</summary>
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(70);

    /// <summary>「鼠标移出窗口后不会自动恢复」：翻页箭头浮出来了还得收回去，理由见 <see cref="HoverWatch"/>。</summary>
    private readonly HoverWatch _watch;

    /// <summary>没人碰的时候自己走一张，每 <see cref="HomeCarousel.Dwell"/> 一步。</summary>
    private readonly DispatcherQueueTimer? _timer;

    /// <summary>底边那排小横条里那几根真正上色的条，按幻灯片的顺序。</summary>
    private readonly List<Border> _bars = [];

    private List<BannerSlide> _slides = [];

    /// <summary>台上那张的下标。<see cref="_slides"/> 空的时候是 0，也就是「没有」。</summary>
    private int _index;

    /// <summary>台上那层是 <c>LayerB</c>。两层轮着上，见 <see cref="Paint"/>。</summary>
    private bool _second;

    /// <summary>指针在带上：箭头浮出来，自动翻页停下。</summary>
    private bool _hover;

    /// <summary>台上那张，订着它的 <c>PropertyChanged</c> —— 图是解码完才到的。</summary>
    private BannerSlide? _current;

    /// <summary>
    /// 订着的那个 <c>XamlRoot</c>，也就是窗口的客户区。带高就是一屏（<see cref="HomeCarousel.Height"/>），而只拖
    /// 下边沿的那一下这条带自己的宽度一点没变 —— <c>SizeChanged</c> 因此不响，一屏有多高却已经换了一个数。存下来
    /// 是为了退订：进树时的 XamlRoot 和离树后能不能问到不是一回事。
    /// </summary>
    private XamlRoot? _viewport;

    /// <summary>
    /// 压在图上那一排货架从下往上盖住了这条带多少。主页量出来交进来（<see cref="SetShelfInset"/>）；0 是还没量到
    /// 或者这一页没有货架，那时整条带都是自己的。
    /// </summary>
    private double _shelf;

    public HomeBanner()
    {
        InitializeComponent();

        _watch = new HoverWatch(Root, up =>
        {
            _hover = up;
            SyncArrows();
            SyncTimer();
        });

        // 全名一次：UserControl 自己有个同名的 DispatcherQueue 属性，简名先落在它身上，静态方法就取不到了。
        _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.CreateTimer();

        if (_timer is not null)
        {
            _timer.Interval = HomeCarousel.Dwell;
            _timer.Tick += OnTick;
        }

        SizeChanged += OnResized;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>按下播放键的那张幻灯片。</summary>
    public event EventHandler<BannerSlide>? PlayRequested;

    /// <summary>按下详情键（或按回车）的那张幻灯片。</summary>
    public event EventHandler<BannerSlide>? OpenRequested;

    /// <summary>
    /// 这条带放哪几张。整份换掉而不是往里增删：一次读取重建一批 <see cref="BannerSlide"/>，上一批的图连着解码
    /// 出来的画面一起丢掉（<see cref="Apply"/>），这样「换了一批」就只有一个入口。
    /// </summary>
    public IReadOnlyList<BannerSlide>? Slides
    {
        get => (IReadOnlyList<BannerSlide>?)GetValue(SlidesProperty);
        set => SetValue(SlidesProperty, value);
    }

    // ---- 换一批幻灯片 -------------------------------------------------------------

    /// <summary>
    /// 换掉整批幻灯片：上一批的画面全丢掉，从第一张重新开始。一张都没有就把整条带收起来 —— 服务器上没有一个
    /// 带宽图的条目时，主页顶上留一条空白的黑带比没有轮播更难解释。
    /// <para>
    /// 自己改自己的 <c>Visibility</c>，所以页面那边不用为这条带再绑一个可见性；这也是主页的标记里它只有一行的
    /// 原因。<c>StackPanel</c> 的 <c>Spacing</c> 不给收起来的孩子留位置，所以收起来就是真的不占地方。
    /// </para>
    /// </summary>
    internal void Apply(IReadOnlyList<BannerSlide> slides)
    {
        foreach (var slide in _slides) slide.Release();

        Unwatch();

        _slides = [.. slides];
        _index = 0;

        BuildDots(_slides.Count);
        Visibility = _slides.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_slides.Count == 0)
        {
            LayerA.Source = null;
            LayerB.Source = null;
            LogoImage.Source = null;
            LogoImage.Visibility = Visibility.Collapsed;
        }
        else
        {
            Show(0);
        }

        SyncArrows();
        SyncTimer();
    }

    /// <summary>
    /// 把第 <paramref name="index"/> 张请上台：字、徽标、剧照，加上底边那根横条。
    /// <para>
    /// 字是当场就换的，剧照要等解码 —— 所以订上这张幻灯片的 <c>PropertyChanged</c>，图到了再淡进来
    /// （<see cref="OnSlideChanged"/>）。台上换人时前一张的订阅要撤掉，否则上一张的图迟到时会盖掉现在这张。
    /// </para>
    /// </summary>
    private void Show(int index)
    {
        if (_slides.Count == 0) return;

        _index = Math.Clamp(index, 0, _slides.Count - 1);
        var slide = _slides[_index];

        TitleText.Text = slide.Title;
        CaptionText.Text = slide.Caption;
        CaptionText.Visibility = slide.CaptionVisibility;
        SynopsisText.Text = slide.Synopsis;
        SynopsisText.Visibility = slide.SynopsisVisibility;
        PlayText.Text = slide.PlayText;

        // 念给读屏的人听：这条带上的字全压在剧照上，光念标题听不出这是第几张。
        AutomationProperties.SetName(Root, $"主页轮播，{HomeCarousel.Position(_index, _slides.Count)}，{slide.Title}");
        AutomationProperties.SetName(PlayButton, $"{slide.PlayText} {slide.Title}");
        AutomationProperties.SetName(OpenButton, slide.OpenTip);

        Unwatch();
        _current = slide;
        slide.PropertyChanged += OnSlideChanged;

        Mark(_index);
        Rise();
        Paint(slide.Picture);
        PaintLogo(slide);
        Prefetch();
    }

    /// <summary>台上那张的订阅撤掉。换人和离开树都走这里。</summary>
    private void Unwatch()
    {
        if (_current is null) return;

        _current.PropertyChanged -= OnSlideChanged;
        _current = null;
    }

    /// <summary>
    /// 底边那排小横条，按幻灯片的数目造。一张的时候不要 —— 「第 1 张，共 1 张」是句废话。
    /// <para>
    /// 真正上色的条只有四像素高，指头和鼠标都点不着，所以每根外面套一层透明的、十六像素高的框当点击区。颜色从
    /// 这份控件自己的 <c>Resources</c> 里取，键少一个就在自检里抛，而不是等到主页第一次画出来。
    /// </para>
    /// </summary>
    internal void BuildDots(int count)
    {
        Dots.Children.Clear();
        _bars.Clear();
        Dots.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;

        var dim = (Brush)Resources["EgBannerDotBrush"];

        for (var i = 0; i < count; i++)
        {
            var index = i;

            var bar = new Border
            {
                Width = DotWidth,
                Height = DotHeight,
                CornerRadius = new CornerRadius(DotHeight / 2),
                Background = dim,
                VerticalAlignment = VerticalAlignment.Center
            };

            var hit = new Border
            {
                Height = 16,
                Background = new SolidColorBrush(Colors.Transparent),
                Child = bar
            };

            AutomationProperties.SetName(hit, HomeCarousel.Position(index, count));
            hit.Tapped += (_, args) =>
            {
                args.Handled = true;
                Show(index);
                SyncTimer();
            };

            _bars.Add(bar);
            Dots.Children.Add(hit);
        }
    }

    /// <summary>现在是第几张：那一根亮着，其余的暗着。</summary>
    internal void Mark(int index)
    {
        var dim = (Brush)Resources["EgBannerDotBrush"];
        var on = (Brush)Resources["EgBannerDotOnBrush"];

        for (var i = 0; i < _bars.Count; i++) _bars[i].Background = i == index ? on : dim;
    }

    // ---- 屏上的样子 --------------------------------------------------------------

    /// <summary>
    /// 换剧照：新的那张放到台下那层上，两层对着淡入淡出，台上台下换个位置。
    /// <para>
    /// 已经在台上的那张（包括两边都还没有图的开场）就不动，否则解码晚到时同一张图会再淡一次。图还没解出来时
    /// 传进来的是空的，那就淡到底色上而不是把上一张的剧照留着 —— 上一张的画面配这一张的标题是句假话。
    /// </para>
    /// </summary>
    private void Paint(BitmapImage? picture)
    {
        if (ReferenceEquals(FrontLayer.Source, picture)) return;

        var rising = _second ? LayerA : LayerB;
        var falling = FrontLayer;

        rising.Source = picture;
        _second = !_second;

        var board = new Storyboard();
        board.Children.Add(FadeTo(rising, 1, Fade, TimeSpan.Zero));
        board.Children.Add(FadeTo(falling, 0, Fade, TimeSpan.Zero));

        Play(board, () =>
        {
            rising.Opacity = 1;
            falling.Opacity = 0;
        });
    }

    /// <summary>
    /// 徽标：有就落下来一点淡进来，没有就收起来。收起来而不是留一块空白 —— 「这个剧集没有徽标」和「徽标没画
    /// 出来」在屏上长得一样，而只有后一种是这份代码的错。
    /// </summary>
    private void PaintLogo(BannerSlide slide)
    {
        LogoImage.Source = slide.Logo;
        LogoImage.Visibility = slide.LogoVisibility;

        if (slide.Logo is null)
        {
            LogoImage.Opacity = 0;
            return;
        }

        LogoImage.Opacity = 0;
        LogoShift.Y = LogoDrop;

        var board = new Storyboard();
        board.Children.Add(FadeTo(LogoImage, 1, Lift, Stagger + Stagger));
        board.Children.Add(SlideTo(LogoShift, 0, Lift, Stagger + Stagger));

        Play(board, () =>
        {
            LogoImage.Opacity = 1;
            LogoShift.Y = 0;
        });
    }

    /// <summary>
    /// 四行字错着抬起来 —— misty 那份 CSS 的签名动作（<c>fadeInUp</c> 配 .4/.6/.8 秒的延迟）。这里四行错完
    /// 210 毫秒：那份是网页首屏的开场，这条带是每八秒走一次的东西，慢了就变成等它。
    /// </summary>
    internal void Rise()
    {
        var steps = Steps();
        var board = new Storyboard();

        for (var i = 0; i < steps.Length; i++)
        {
            var (element, shift) = steps[i];
            var delay = Stagger * i;

            element.Opacity = 0;
            shift.Y = TextDrop;

            board.Children.Add(FadeTo(element, 1, Lift, delay));
            board.Children.Add(SlideTo(shift, 0, Lift, delay));
        }

        Play(board, () =>
        {
            foreach (var (element, shift) in steps)
            {
                element.Opacity = 1;
                shift.Y = 0;
            }
        });
    }

    /// <summary>抬起来的那四行，从上往下。</summary>
    private (UIElement Element, TranslateTransform Shift)[] Steps() =>
    [
        (TitleText, TitleShift),
        (CaptionText, CaptionShift),
        (SynopsisText, SynopsisShift),
        (Actions, ActionsShift)
    ];

    /// <summary>
    /// 跑一段动画，没进树就直接落到终值。
    /// <para>
    /// 自检里这份控件没有 <c>XamlRoot</c>，那时 <c>Begin()</c> 既没人看也没有意义；而故事板照样是搭出来的，
    /// 所以 <c>SetTarget</c> 拿到一个空的变换（标记里漏了一个 <c>TranslateTransform</c>）在那里就抛。
    /// </para>
    /// </summary>
    private void Play(Storyboard board, Action settle)
    {
        if (XamlRoot is null)
        {
            settle();
            return;
        }

        board.Begin();
    }

    /// <summary>透明度动画。透明度和位移都由合成器自己跑，所以不需要 <c>EnableDependentAnimation</c>。</summary>
    private static DoubleAnimation FadeTo(DependencyObject target, double to, TimeSpan span, TimeSpan delay) =>
        Track(target, "Opacity", to, span, delay);

    private static DoubleAnimation SlideTo(TranslateTransform target, double to, TimeSpan span, TimeSpan delay) =>
        Track(target, "Y", to, span, delay);

    private static DoubleAnimation Track(DependencyObject target, string property, double to, TimeSpan span, TimeSpan delay)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(span),
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        // 直接把对象交给故事板，而不是写 TargetName：名字只在运行时解析，而这条带上的目标有一半是变换而不是元素。
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);

        return animation;
    }

    /// <summary>台上那层。两层轮着上，见 <see cref="Paint"/>。</summary>
    internal Image FrontLayer => _second ? LayerB : LayerA;

    // ---- 走一张 -----------------------------------------------------------------

    /// <summary>
    /// 往前或往后一张，到头再回来（<see cref="HomeCarousel.Step"/>）。翻过之后自动翻页的钟重新起算：刚自己
    /// 翻过去就被钟推走，是这类带子最惹人的一处。
    /// </summary>
    internal void Advance(int delta)
    {
        if (_slides.Count <= 1) return;

        Show(HomeCarousel.Step(_index, delta, _slides.Count));
        SyncTimer();
    }

    /// <summary>
    /// 台上这张和它左右各一张解码着，其余的丢掉。带上八张整宽剧照全留着是几十兆的画面，而
    /// <see cref="BannerSlide.Release"/> 之后再回来是一次磁盘读取 —— 字节还在图片仓库的缓存里。
    /// </summary>
    private void Prefetch()
    {
        var back = HomeCarousel.Step(_index, -1, _slides.Count);
        var next = HomeCarousel.Step(_index, 1, _slides.Count);

        for (var i = 0; i < _slides.Count; i++)
        {
            if (i == _index || i == back || i == next) _ = _slides[i].EnsureAsync();
            else _slides[i].Release();
        }
    }

    /// <summary>
    /// 箭头在不在屏上。指针在带上、并且有第二张可翻才浮出来 —— 只有一张时两个箭头都点不动，而一个点了没反应的
    /// 按钮比没有按钮更难解释（同 <see cref="EmbyNian.Infrastructure.CardStrip.ArrowsFor"/> 那条注释）。
    /// </summary>
    private void SyncArrows()
    {
        var shown = _hover && _slides.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        PrevButton.Visibility = shown;
        NextButton.Visibility = shown;
    }

    /// <summary>
    /// 自动翻页的钟。指针在带上就停 —— 正在看这张的人不该被推走；只有一张、还没进树、或者一张都没有时也不走。
    /// 每次调用都重新起算，所以人手翻一张之后是整整八秒。
    /// </summary>
    private void SyncTimer()
    {
        if (_timer is null) return;

        _timer.Stop();

        if (_slides.Count > 1 && !_hover && IsLoaded) _timer.Start();
    }

    /// <summary>
    /// 带多高、字块多宽、字块往下沉多少。带高就是一屏（<see cref="HomeCarousel.Height"/>）—— 「轮播页面占满
    /// 窗口」，而剧照在里面整张画出来站在正中，锁定比例的窗口里那正好是一张不裁切的 16:9 铺满一屏。字块不超过
    /// 带的一半多一点：右边要留出剧照本身，而横向那层暗罩到淡出点才透干净。
    /// <para>
    /// 底下那一截归货架（<see cref="SetShelfInset"/>）：继续观看那一块坐在一层亚克力上、压在大图的下半截，所以
    /// 字块沉多少、底边那排小横条和徽标站在哪儿，算的都是「一屏减掉玻璃」那一片，不是整条带。少了这一下，播放
    /// 键、小横条和徽标就都藏在货架后面。
    /// </para>
    /// <para>
    /// 一屏有多高问的是 <c>XamlRoot.Size</c>，也就是整个客户区 —— 和自检里 <c>BleedRead</c> 问的同一个数；量不到
    /// （自检里这份控件没有 XamlRoot）就是 0，那时按开窗那一档算。
    /// </para>
    /// </summary>
    internal void Resize(double width)
    {
        var viewport = XamlRoot?.Size ?? default;
        var height = HomeCarousel.Height(viewport.Height);

        // 还没量过时高度是 NaN，而 NaN 参与的比较全是假 —— 少了这一句，第一次布局就设不上高度。
        if (double.IsNaN(Root.Height) || Math.Abs(Root.Height - height) > 0.5) Root.Height = height;

        // 货架底下那一片不算：字块在剩下的那一片里居中、再按它沉一点。
        var clear = Math.Max(HomeCarousel.MinHeight / 2, height - _shelf);

        Info.MaxWidth = Math.Clamp(width * 0.54, 280, 620);
        Info.Margin = new Thickness(InfoInset, 0, 0, _shelf);
        InfoShift.Y = HomeCarousel.InfoDrop(clear);
        ShadeEdge(width, height);
    }

    /// <summary>字块离带子左沿多远。见标记里 Info 那一段：让开的是翻页箭头那条窄栏。</summary>
    private const double InfoInset = 60;

    /// <summary>
    /// 压在图上那一排货架盖住了这条带底下多少。主页量出来交进来（见 <c>HomePage.SyncShelfOverlay</c>）—— 带子自己不知道
    /// 上面压着什么，而字块、小横条和徽标都得让开那一片。
    /// </summary>
    internal void SetShelfInset(double inset)
    {
        inset = Math.Max(0, inset);
        if (Math.Abs(_shelf - inset) <= 0.5) return;

        _shelf = inset;
        if (ActualWidth > 0) Resize(ActualWidth);
    }

    /// <summary>
    /// 两层暗罩跟着剧照的边沿走。剧照按自己的 16:9 整张画出来、站在带子正中（见 HomeBanner.xaml 里
    /// LayerA/LayerB），所以左右各剩一条底色，剩多少完全由带子的形状决定：2.2:1 的带每边留一成四，锁定比例那种
    /// 更扁的带每边留两成。
    /// <para>
    /// 左边那层托着整块字：从带的左沿起就浓，过了字块才淡出，所以它的淡出点按那条留白往右推。右边那层只有一件
    /// 事 —— 把剧照右边沿那道硬边融进底色里，所以它就贴在那条留白上。徽标也跟着挪：图居中之后，带的右沿和图的
    /// 右沿差了一条留白，徽标该贴着图，不是贴着带。
    /// </para>
    /// <para>
    /// 按 16:9 算而不是问那一层元素：图是解码完才到的，而这一层的尺寸在那之前是 0。整套「不裁切」就建立在
    /// 「服务器发来的宽图是 16:9」上，所以这里用同一个假设；真到了一张别的比例的图，差的只是这两道渐变落在哪儿。
    /// </para>
    /// </summary>
    private void ShadeEdge(double width, double height)
    {
        if (width <= 0 || height <= 0) return;

        var picture = Math.Min(width, height * HomeCarousel.WindowAspect);
        var strip = Math.Max(0, (width - picture) / 2);
        var share = Math.Clamp(strip / width, 0, 0.5);

        ScrimMid.Offset = Math.Min(1, share + 0.18);
        ScrimFar.Offset = Math.Min(1, share + 0.52);

        EdgeNear.Offset = Math.Min(1, share);
        EdgeFar.Offset = Math.Min(1, share + 0.10);

        LogoImage.Margin = new Thickness(0, 0, strip + LogoInset, _shelf + LogoBaseline);
        Dots.Margin = new Thickness(0, 0, 0, _shelf + DotsBaseline);

        // 左右那两层暗罩停在那一排货架的上沿：它们是为字块和图的边沿准备的，往下伸到货架底下只会把左边压得比
        // 右边黑。整张剧照因此在货架上面那一段一点没被动过。
        ShadeSide.Margin = new Thickness(0, 0, 0, _shelf);
        ShadeEdgeLayer.Margin = new Thickness(0, 0, 0, _shelf);

        // 竖着那层反过来：它要在货架底下铺一层足够暗的底（浅墨才读得出），所以从货架的上沿开始压。上面那一段
        // 一点不压 —— 「背景，要能看到完整的轮播背景图」。
        if (height > 0)
        {
            var edge = Math.Clamp((height - _shelf) / height, 0, 1);

            FootNear.Offset = Math.Max(0, edge - 0.12);
            FootMid.Offset = edge;
        }
    }

    /// <summary>徽标离剧照右沿和带子下沿各多远，加上底边那排小横条离下沿多远。</summary>
    private const double LogoInset = 34;

    private const double LogoBaseline = 30;

    private const double DotsBaseline = 18;

    // ---- 事件 -------------------------------------------------------------------

    private void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        if (Current is { } slide) PlayRequested?.Invoke(this, slide);
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        if (Current is { } slide) OpenRequested?.Invoke(this, slide);
    }

    private void OnPrevClicked(object sender, RoutedEventArgs e) => Advance(-1);

    private void OnNextClicked(object sender, RoutedEventArgs e) => Advance(1);

    private void OnTick(DispatcherQueueTimer sender, object args) => Advance(1);

    /// <summary>
    /// 键盘翻页。箭头是悬停才浮出来的，收起来的按键接不了焦点，所以用键盘的人翻页靠这里 —— 带自己是
    /// <c>IsTabStop</c>，Tab 停在带上，左右方向键走片。
    /// </summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_slides.Count == 0) return;

        switch (e.Key)
        {
            case VirtualKey.Left:
                e.Handled = true;
                Advance(-1);
                break;
            case VirtualKey.Right:
                e.Handled = true;
                Advance(1);
                break;
            case VirtualKey.Home:
                e.Handled = true;
                Show(0);
                SyncTimer();
                break;
            case VirtualKey.End:
                e.Handled = true;
                Show(_slides.Count - 1);
                SyncTimer();
                break;
        }
    }

    /// <summary>
    /// 台上那张的图解码好了。认一下是不是台上这张：走得快的时候上一张的解码会晚到，那张图不该盖在现在这张的
    /// 标题上面。
    /// </summary>
    private void OnSlideChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _current) || _current is not { } slide) return;

        if (e.PropertyName == nameof(BannerSlide.Picture)) Paint(slide.Picture);
        else if (e.PropertyName == nameof(BannerSlide.Logo)) PaintLogo(slide);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => _watch.Enter();

    /// <summary>
    /// 指针离开。位置现查一遍，同 <see cref="ShelfStrip.OnPointerExited"/>：这个事件会从子元素冒上来，而指针踩
    /// 到刚浮出来的箭头上算「离开了带」，直接信它就会把手底下的按钮收掉。
    /// </summary>
    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root).Position;

        if (point.X >= 0 && point.Y >= 0 && point.X < Root.ActualWidth && point.Y < Root.ActualHeight)
            return;

        _watch.Leave();
    }

    private void OnResized(object sender, SizeChangedEventArgs e) => Resize(e.NewSize.Width);

    /// <summary>
    /// 窗口的客户区变了。只拖下边沿那一下带子自己没变尺寸，可它的上限变了（<see cref="_viewport"/>），所以照
    /// 现在的宽度重算一次。宽度是 0 的时候不算 —— 那是还没排过的那一帧，按 0 算会把带子压到下限，而那一下之后
    /// <c>SizeChanged</c> 自己会带着真宽度来。
    /// </summary>
    private void OnViewportChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (ActualWidth > 0) Resize(ActualWidth);
    }

    /// <summary>数据可能比 <c>Loaded</c> 先到，所以进树时再问一遍钟该不该走。</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncTimer();

        if (XamlRoot is { } root && !ReferenceEquals(_viewport, root))
        {
            UnwatchViewport(_viewport);
            _viewport = root;
            root.Changed += OnViewportChanged;

            // 进树的这一下窗口可能已经不是默认大小了（自检就会把窗口拉成几种尺寸），而带高是构造时算的。
            if (ActualWidth > 0) Resize(ActualWidth);
        }
    }

    /// <summary>
    /// 离开树：钟停掉、指针的监视撤掉、图全丢掉。主页换到别的页面时这条带整个不在屏上，而八张剧照还占着内存，
    /// 钟还每八秒解一次码。
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        _watch.Leave();
        Unwatch();

        UnwatchViewport(_viewport);
        _viewport = null;

        foreach (var slide in _slides) slide.Release();
    }

    /// <summary>把窗口那一头的订退掉。<see langword="null"/> 进来就是「本来没订」。</summary>
    private void UnwatchViewport(XamlRoot? root)
    {
        if (root is not null) root.Changed -= OnViewportChanged;
    }

    private static void OnSlidesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((HomeBanner)sender).Apply(args.NewValue as IReadOnlyList<BannerSlide> ?? []);

    /// <summary>台上那张，一张都没有时是空的。</summary>
    private BannerSlide? Current => _index >= 0 && _index < _slides.Count ? _slides[_index] : null;

    // ---- 自检 -------------------------------------------------------------------

    /// <summary>
    /// 这条带在 <c>--self-check</c> 里的那一份读数，同 <see cref="ShelfStrip.Probe"/>：自检时没有服务器，主页
    /// 一张幻灯片也没有，所以问的是不依赖数据的那几件事，加上这份标记自己能不能解析。
    /// <para>
    /// <c>new HomeBanner()</c> 就把标记走了一遍：里面每个资源键（<c>DefaultButtonStyle</c>、
    /// <c>EgOnScrimBrush</c>、四组改掉的按钮状态键、两条渐变）解析不了就在这里抛。渐变里写颜色键会在高对比度
    /// 下抛，也是这一句挡着的 —— 那本词典里只有画刷。
    /// </para>
    /// <para>
    /// 剩下五件是屏上的行为里 Core 摸不到的部分：一张都没有时整条带收起来、底边那排横条造得出来且亮在对的那
    /// 根、带高按页宽落到布局上（连字块该沉多少一起，那一位是 <c>RenderTransform</c>，Core 算得出来却证不了它
    /// 挂在了字块上）、翻页箭头和字块不在同一列、两层剧照真的轮着上。字块那段错拍动画顺带跑一遍，故事板里哪个
    /// 目标是空的就在这里抛，而不是等到主页第一次换幻灯片。
    /// </para>
    /// </summary>
    internal static (bool Ok, string Detail) Probe()
    {
        var banner = new HomeBanner();

        // 一张都没有：整条带收起来（主页顶上不留一条空黑带），钟不走，横条一根也不造。
        banner.Apply([]);
        var quiet = banner.Visibility == Visibility.Collapsed && banner.Dots.Children.Count == 0 && !banner.Ticking;

        // 三张造三根，第二根亮着。颜色是按键从 Resources 里取的，所以这一段也是那两个键的解析。
        banner.BuildDots(3);
        banner.Mark(1);
        var dots = banner.Dots.Children.Count == 3 && banner.BarOn(1) && !banner.BarOn(0) && !banner.BarOn(2);

        // 只有一张时整排不要：「第 1 张，共 1 张」是句废话。
        banner.BuildDots(1);
        var lonely = banner.Dots.Visibility == Visibility.Collapsed;

        // 带高落到布局上，而不只是算出来：这份控件没有 XamlRoot，量不到一屏有多高，所以走的是开窗那一档
        // （UnmeasuredHeight 800）。顺带问那一沉：那么高的带子该沉到上限那一档。
        banner.Resize(1100);
        var wide = banner.Info.MaxWidth;
        var drop = banner.InfoShift.Y;
        var tall = Math.Abs(banner.Root.Height - HomeCarousel.UnmeasuredHeight) < 0.01 && wide <= 620
            && Math.Abs(drop - HomeCarousel.InfoDrop(HomeCarousel.UnmeasuredHeight)) < 0.01 && drop > 0;

        // 那一排货架压住这条带底下 300 的时候，字块、小横条和徽标一起让开那一片：字块按剩下的那一片沉，另外两样
        // 把那 300 加进自己的下边距，左右两层暗罩也停在那一沿上。竖着那层反过来 —— 它要从那一沿起把底下压暗，
        // 浅墨的牌子和卡片说明才读得出来（800 的带、货架占 300，压暗从 0.625 那一档开始）。
        // 交进来之后自己再排一次：这份控件没上树，量不到自己的宽度，而 SetShelfInset 只在量到宽度时重排。
        banner.SetShelfInset(300);
        banner.Resize(1100);
        var lifted = Math.Abs(banner.Info.Margin.Bottom - 300) < 0.01
            && Math.Abs(banner.InfoShift.Y - HomeCarousel.InfoDrop(HomeCarousel.UnmeasuredHeight - 300)) < 0.01
            && Math.Abs(banner.Dots.Margin.Bottom - (300 + DotsBaseline)) < 0.01
            && Math.Abs(banner.LogoImage.Margin.Bottom - (300 + LogoBaseline)) < 0.01
            && Math.Abs(banner.ShadeSide.Margin.Bottom - 300) < 0.01
            && banner.ShadeFoot.Margin.Bottom == 0
            && Math.Abs(banner.FootMid.Offset - (HomeCarousel.UnmeasuredHeight - 300) / HomeCarousel.UnmeasuredHeight)
                < 0.001;

        banner.SetShelfInset(0);
        banner.Resize(1100);

        // 翻页箭头和字块不同列（「翻页的按钮会挡住字体」）。箭头贴着带的边沿、在竖向正中，字沉下去之后它正好
        // 落在片名那一行上 —— 所以这一条判的是列，不是高度。两边都从设死的边距和宽度上读，箭头默认是收起的、
        // 量不到位置，而这两个数收起来照样在，也正是版面里说话的那两个。
        var strip = banner.PrevButton.Margin.Left + banner.PrevButton.Width;
        var apart = banner.Info.Margin.Left >= strip + 8;

        // 两层轮着上：换两次图，两层各站过一次台；同一张再来一次不再换层，也不白跑一次淡入淡出。
        var one = new BitmapImage();
        var two = new BitmapImage();

        banner.Paint(one);
        var onB = ReferenceEquals(banner.FrontLayer, banner.LayerB) && ReferenceEquals(banner.LayerB.Source, one);

        banner.Paint(two);
        var onA = ReferenceEquals(banner.FrontLayer, banner.LayerA) && ReferenceEquals(banner.LayerA.Source, two);

        banner.Paint(two);
        var layered = onB && onA
            && ReferenceEquals(banner.FrontLayer, banner.LayerA)
            && banner.LayerA.Opacity == 1
            && banner.LayerB.Opacity == 0;

        // 字块那段错拍动画。没进树，所以故事板搭出来之后直接落到终值。
        banner.Rise();
        var risen = banner.TitleText.Opacity == 1 && banner.TitleShift.Y == 0 && banner.ActionsShift.Y == 0;

        var ok = quiet && dots && lonely && tall && lifted && layered && risen && apart;

        return (ok,
            $"没有幻灯片时{(quiet ? "整条带收起、钟不走" : "带还在屏上或钟在走")}；"
                + $"横条 3 根亮第 2 根{(dots ? "" : "（不对）")}、1 张时整排{(lonely ? "收起" : "还在")}；"
                + $"带高就是一屏，量不到窗口高时 {HomeCarousel.UnmeasuredHeight:0}"
                + $"（下限 {HomeCarousel.MinHeight:0}）；"
                + $"字块宽 页宽1100→{wide:0}、量不到→{banner.Info.MaxWidth:0}；"
                + $"字块下沉 带高{HomeCarousel.UnmeasuredHeight:0}→{drop:0}"
                + $"（最多 {HomeCarousel.InfoDrop(HomeCarousel.UnmeasuredHeight):0}）；"
                + $"货架压住 300 时{(lifted ? "字块、小横条和徽标都让开了" : "有东西没让开，会被玻璃盖住")}；"
                + $"箭头占到 {strip:0}、字块从 {banner.Info.Margin.Left:0} 起"
                + $"{(apart ? "，两边不同列" : "，压到字了")}；"
                + $"两层剧照{(layered ? "轮着上，同一张不重来" : "没换过位置")}；"
                + $"四行错 {Stagger.TotalMilliseconds:0} 毫秒、{Lift.TotalMilliseconds:0} 毫秒抬起 {TextDrop:0} 像素"
                + $"{(risen ? "后落定" : "但没落定")}，换图 {Fade.TotalMilliseconds:0} 毫秒；"
                + $"没人碰时每 {HomeCarousel.Dwell.TotalSeconds:0} 秒走一张，最多 {HomeCarousel.Slots} 张");
    }

    /// <summary>自检用：自动翻页的钟在不在走。</summary>
    private bool Ticking => _timer?.IsRunning ?? false;

    /// <summary>
    /// 自检用：这条带在真页面上的读数。<see cref="Probe"/> 拿假图证规则，这一句证的是规则之外的三件事 ——
    /// 带在 <c>ScrollView</c> 的竖排里真量到了宽度、那个宽度真变成了高度、台上那张的剧照真解码进了前面那层。
    /// 三样里少一样，屏上就是一条空带，而 <see cref="HomeViewModel.BannerSummary"/> 报的张数照旧好看。
    /// <para>
    /// 字块的高和下沉量一起报：<see cref="HomeCarousel.InfoDrop"/> 只按带高算，并不知道字块有多高，所以屏上量到
    /// 的这个高度是那条规则唯一的现实对照 ——「带高 − 字块高 − 2×下沉」的一半，就是字块底下离带底还剩的余量，
    /// 底边那排小横条占 22。
    /// </para>
    /// <para>
    /// 带自己占没占满一屏也报作诊断：它该就是窗口的客户区高（<see cref="HomeCarousel.Height"/>）。继续观看那块
    /// 货架盖住底下多少一起报 —— 那是字块和小横条让开的那一片。屏上是不是真这样，由
    /// <see cref="HomePage.FoldRead"/> 在真实页面上量。
    /// </para>
    /// </summary>
    internal string State
    {
        get
        {
            if (_slides.Count == 0) return "收起";

            var height = double.IsNaN(Root.Height) ? 0 : Root.Height;
            var viewport = _viewport?.Size.Height ?? 0;

            return $"带高 {height:0}、字块宽 {Info.MaxWidth:0}、"
                + $"字块高 {Info.ActualHeight:0} 往下沉 {InfoShift.Y:0}、"
                + $"带 {(height > 0 ? Root.ActualWidth / height : 0):0.00}:1"
                + $"（窗口高 {viewport:0}，货架盖住底下 {_shelf:0}）、"
                + $"{HomeCarousel.Position(_index, _slides.Count)}、"
                + $"剧照{(FrontLayer.Source is null ? "还在取" : "已上图")}、"
                + $"徽标{(LogoImage.Source is null ? "无" : "有")}";
        }
    }

    /// <summary>
    /// 自检：剧照真的整张画出来了没有 —— 「轮播的海报能保持16:9」。
    /// <para>
    /// 量的是台上那一层元素自己的尺寸。这只有在 <c>Stretch="Uniform"</c> 加一个不是 <c>Stretch</c> 的横向对齐
    /// 下才说得上话：那时这个元素的大小就是画出来那张图的大小。填满整格的那种拉伸会让它等于整条带，怎么裁的
    /// 都量不出来 —— 而「裁掉了三成半」在屏幕上只是一张构图不太对的图，没人能指着它说这是个错。
    /// </para>
    /// <para>
    /// 判三件事：画出来的形状就是原图的形状（没裁也没拉）、高度吃满带子（能画多大就画多大）、左右两条留白一样
    /// 宽（「轮播图移到画面中间」）。留白多宽一起报出来，左边那条是字块站的地方。图还没解码回来时没有得量，那一
    /// 档只报不判 —— 那是网络的事，不是版面的事。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) PictureRead()
    {
        var layer = FrontLayer;
        var band = (Width: Band.ActualWidth, Height: Band.ActualHeight);
        var drawn = (Width: layer.ActualWidth, Height: layer.ActualHeight);

        if (layer.Source is null || drawn.Width <= 1 || drawn.Height <= 1)
            return (true, $"剧照还没解出来，带 {band.Width:0}×{band.Height:0}");

        var shape = drawn.Width / drawn.Height;
        var source = layer.Source as BitmapImage;
        var sourceShape = source is { PixelWidth: > 0, PixelHeight: > 0 }
            ? (double)source.PixelWidth / source.PixelHeight
            : 0;

        var at = layer.TransformToVisual(Band).TransformPoint(new Windows.Foundation.Point(0, 0));
        var left = at.X;
        var right = band.Width - (at.X + drawn.Width);

        var whole = sourceShape <= 0 || Math.Abs(shape - sourceShape) < 0.02;
        var tall = Math.Abs(drawn.Height - band.Height) <= 1.5;
        var centred = Math.Abs(left - right) <= 1.5;

        return (whole && tall && centred,
            $"剧照 {drawn.Width:0}×{drawn.Height:0} = {shape:0.000}:1"
                + (sourceShape > 0
                    ? $"（原图 {source!.PixelWidth}×{source.PixelHeight} = {sourceShape:0.000}:1"
                        + (whole ? "，没裁也没拉）" : "，画出来的形状和原图不一样）")
                    : "（问不到原图尺寸）")
                + $"；带 {band.Width:0}×{band.Height:0}"
                + (tall ? "，高度吃满" : "，没吃满带高")
                + $"；左右各留 {left:0} 和 {right:0}"
                + (centred ? "，居中" : "，没居中")
                + $"（字块最宽 {Info.MaxWidth:0}）");
    }

    /// <summary>
    /// 自检：带上那三行字真解析到的字体和字号，以及三颗键真拿到的高度。
    /// <para>
    /// 和详情页头图上那条（<c>DetailPage.HeroType</c>）是同一件事，值得两处各查一遍：这两块是整个界面上最
    /// 大的两个画面，而它们的版式来自两份互不相干的标记。这一版之前带上的片名写死了 40 和 Bold 却没写字
    /// 体，于是主页最大的一块字用的是继承来的正文字，屏上看得见、截图里看不出。
    /// </para>
    /// <para>
    /// 比的是元素真解析到的族名和它该有的那个键，所以 Bahnschrift 装没装都不影响判断 —— 那件事由 字体已解析
    /// 那条管。键高一起量，是因为「整套界面只有一个按键高度」这句话在这条带上要兑现三次：播放、详情、翻页
    /// 箭头，以前它们是 40、40、44。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) TypeRead()
    {
        var display = Face("EgDisplayFontFamily");
        var data = Face("EgDataFontFamily");
        var ui = Face("EgUiFontFamily");
        var size = Number("EgDisplayFontSize");
        var height = Number("EgActionHeight");

        var ok = TitleText.FontFamily.Source == display
            && Same(TitleText.FontSize, size)
            && CaptionText.FontFamily.Source == data
            && SynopsisText.FontFamily.Source == ui
            && Same(PlayButton.Height, height)
            && Same(OpenButton.Height, height)
            && Same(NextButton.Height, height);

        return (ok,
            $"片名 {TitleText.FontFamily.Source} {TitleText.FontSize:0}、"
                + $"读数 {CaptionText.FontFamily.Source} {CaptionText.FontSize:0}、"
                + $"简介 {SynopsisText.FontFamily.Source} {SynopsisText.FontSize:0}、"
                + $"键高 {PlayButton.Height:0}/{OpenButton.Height:0}/{NextButton.Height:0}"
                + (ok
                    ? ""
                    : $" —— 要的是片名「{display}」{size:0}、读数「{data}」、简介「{ui}」、键高 {height:0}"));

        static string Face(string key) => ((FontFamily)Application.Current.Resources[key]).Source;

        static double Number(string key) => (double)Application.Current.Resources[key];

        static bool Same(double left, double right) => Math.Abs(left - right) < 0.01;
    }

    /// <summary>自检用：第几根横条亮着 —— 这条规则在屏上唯一看得见的结果就是那两个画刷。</summary>
    private bool BarOn(int index) =>
        index >= 0 && index < _bars.Count && ReferenceEquals(_bars[index].Background, Resources["EgBannerDotOnBrush"]);
}
