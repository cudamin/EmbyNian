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
/// 掉 —— 一张解码出来的剧照是兆字节量级的画面，八张一起留着就是几十兆。
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

    private const double DotWidth = 22;
    private const double DotHeight = 4;

    /// <summary>
    /// 一根横条的点击区有多高。真正上色的条只有 <see cref="DotHeight"/>，指头和鼠标都点不着，所以每根外面套一层
    /// 透明的框。这个数连着 <see cref="DotsBaseline"/>，就是底边那排自己占掉的高度。
    /// </summary>
    private const double DotHit = 16;

    /// <summary>
    /// 换图的那一下：两层交叉着淡入淡出。两下同时走，所以这也是「上一张还看得见」的时长。不叫 <c>Fade</c> 是
    /// 2026-09-09 之后的事：剧照左沿那道渐融在标记里占了 <c>Fade</c> 这个名（<c>HomeBanner.xaml</c>），一个类里
    /// 摆不下两个。
    /// </summary>
    private static readonly TimeSpan CrossFade = TimeSpan.FromMilliseconds(420);

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
    /// 订着的那个 <c>XamlRoot</c>，也就是窗口的客户区。带高照带宽算、上限一屏（<see cref="HomeCarousel.Height"/>），
    /// 而只拖下边沿的那一下这条带自己的宽度一点没变 —— <c>SizeChanged</c> 因此不响，一屏有多高却已经换了一个数。
    /// 存下来是为了退订：进树时的 XamlRoot 和离树后能不能问到不是一回事。
    /// </summary>
    private XamlRoot? _viewport;

    public HomeBanner()
    {
        InitializeComponent();

        // 字底下那层跟着字形走的影子（见 TextInk）。四行各一层，宿主是同一格里排在字前面那个空 Border。
        // 装在构造里而不是 Loaded 里：这四层是这块字的一部分，不是「进树之后才有的装饰」，而自检里那份
        // 控件（Probe 里 new 出来的那一个）也该把这条路走一遍 —— 走不通就该在闸门上红，不是在屏上少一层。
        TextInk.Attach(LogoInk, LogoImage);
        TextInk.Attach(TitleInk, TitleText, TextInk.TitleBlur);
        TextInk.Attach(CaptionInk, CaptionText);
        TextInk.Attach(SynopsisInk, SynopsisText);

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
    /// 台上换人了。<see langword="null"/> 是「台上没人」（整批换成空的）。
    /// <para>
    /// 事件而不是让页面去轮询：换一张有五个来处（钟、两颗箭头、底边那排小横条、左右方向键、换一整批），而它们
    /// 都汇到 <see cref="Show"/> 这一处。
    /// </para>
    /// <para>
    /// 右边那一列继续观看 2026-09-08 删掉之后没人再订它，但留着：整批换掉、收起整条带，这类「这一刻台上是谁」
    /// 的读数对下一个要看这条带的听者（比如一张选择条）是现成的，价钱是一个没人订的事件。
    /// </para>
    /// </summary>
    public event EventHandler<BannerSlide?>? SlideChanged;

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
            LogoRow.Visibility = Visibility.Collapsed;
            SlideChanged?.Invoke(this, null);
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
        CaptionRow.Visibility = slide.CaptionVisibility;
        SynopsisText.Text = slide.Synopsis;
        SynopsisRow.Visibility = slide.SynopsisVisibility;
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

        // 最后一句：上面那几件都是这条带自己的事，这一声是给外面的听者的。
        SlideChanged?.Invoke(this, slide);
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
                Height = DotHit,
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
        board.Children.Add(FadeTo(rising, 1, CrossFade, TimeSpan.Zero));
        board.Children.Add(FadeTo(falling, 0, CrossFade, TimeSpan.Zero));

        Play(board, () =>
        {
            rising.Opacity = 1;
            falling.Opacity = 0;
        });
    }

    /// <summary>
    /// 徽标：有就跟着字一起抬起来，没有就收起来。收起来而不是留一块空白 —— 「这个剧集没有徽标」和「徽标没画
    /// 出来」在屏上长得一样，而只有后一种是这份代码的错。
    /// <para>
    /// 抬的方向和延迟都和字块一样（<see cref="TextDrop"/>，延迟 0）：徽标 2026-09-05 从压在角上的一张图变成了
    /// 字块的第一行（「把徽标移到剧名上面」），而这一叠是从上往下错着抬起来的（<see cref="Rise"/>），所以它就是
    /// 第 0 拍。从前它反过来、从上面落下来一点（−12）、延迟两拍 —— 那一版它站在另一个角上，和这一叠没关系。
    /// </para>
    /// <para>
    /// 这一段没有并进 <see cref="Rise"/>：图是解码完才到的，晚到的那一张要单独再抬一次
    /// （<see cref="OnSlideChanged"/>），而那时字块早落定了。
    /// </para>
    /// <para>
    /// 淡入和位移都写在徽标外面那一格（<c>LogoRow</c>）上，不写在图自己身上：影子挂在那一格里，图单独淡入的话
    /// 影子会先一步整黑地站在那儿（见 <see cref="TextInk"/>）。
    /// </para>
    /// </summary>
    private void PaintLogo(BannerSlide slide)
    {
        LogoImage.Source = slide.Logo;
        LogoRow.Visibility = slide.LogoVisibility;

        if (slide.Logo is null)
        {
            LogoRow.Opacity = 0;
            return;
        }

        LogoRow.Opacity = 0;
        LogoShift.Y = TextDrop;

        var board = new Storyboard();
        board.Children.Add(FadeTo(LogoRow, 1, Lift, TimeSpan.Zero));
        board.Children.Add(SlideTo(LogoShift, 0, Lift, TimeSpan.Zero));

        Play(board, () =>
        {
            LogoRow.Opacity = 1;
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

    /// <summary>
    /// 抬起来的那四行，从上往下。**每一行给的是它外面那一格（<c>…Row</c>）而不是字自己** —— 影子挂在那一格里，
    /// 位移和淡入写在字上就会把影子留在原处（见 <see cref="TextInk"/>）。
    /// </summary>
    private (UIElement Element, TranslateTransform Shift)[] Steps() =>
    [
        (TitleRow, TitleShift),
        (CaptionRow, CaptionShift),
        (SynopsisRow, SynopsisShift),
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
    /// 带多高、渐融站在哪儿、字块多宽。带高走 <see cref="HomeCarousel.Height"/>：剧照缩到带宽的六成靠右站
    /// （「把主页的轮播图移动到右边」＋「把轮播图弄扁一些」，2026-09-09），带高就是那六成按 16:9 算出来的高，
    /// 所以图正好铺满带的上下、左沿落在「带宽 − 图宽」那条竖线上。
    /// <para>
    /// 渐融（<c>Fade</c>）正站在那条线上：贴住剧照的左沿、往图里走 <see cref="FadeShare"/> 那么宽
    /// （「给轮播图左边加上黑色渐变」）。图宽从带高换（带高×16÷9），取 <c>min</c> 兜住带宽不到 427、图连六成
    /// 宽都摆不下的那一档 —— 那时图吃满带宽，渐融贴的就是带子的左沿了。
    /// </para>
    /// <para>
    /// 字块 2026-09-05 走过两趟：先按「把红框框出来的移到右下角」挪去了右下角，同一天又按「移到左下角，然后把徽标
    /// 移到剧名上面」挪回左边、贴着下沿，徽标从压在角上的一张独立的图变成了这一叠的第一行。两趟一起丢掉的是「往下
    /// 沉一点」那条规则（从前的 <c>InfoShift</c> 加 <c>HomeCarousel.InfoDrop</c>）和 <c>PlaceLogo</c> 那一段留白
    /// 计算：站在字块里的徽标由布局给位置。2026-09-10 第三趟按「把红框里的东西移动到左上角」从下沿挪到了顶上
    /// （<see cref="InfoTop"/>，站在 PageSlate 底下）—— 仍然是布局给位置，只是换了一个角，别把那条下沉规则请回来。
    /// </para>
    /// <para>
    /// 整条带都是自己的：继续观看从前「压在图的下半截上」，后来当过第一屏右边那一栏（那一栏 2026-09-08 随着
    /// 「移除轮播图右边的媒体库」删掉了），所以字块和底边那排小横条不用让开谁，带宽就是整个页宽。
    /// </para>
    /// <para>
    /// 一屏是带高的上限（超宽屏上「带宽×六成 ÷ 16 × 9」算出来会比一屏还高），问的是 <c>XamlRoot.Size</c>，
    /// 也就是整个客户区 —— 和自检里 <c>BleedRead</c> 问的同一个数；量不到（自检里这份控件没有 XamlRoot）就是 0，
    /// 那时不封顶。
    /// </para>
    /// </summary>
    internal void Resize(double width)
    {
        var viewport = XamlRoot?.Size ?? default;
        var height = HomeCarousel.Height(viewport.Height, width);

        // 还没量过时高度是 NaN，而 NaN 参与的比较全是假 —— 少了这一句，第一次布局就设不上高度。
        if (double.IsNaN(Root.Height) || Math.Abs(Root.Height - height) > 0.5) Root.Height = height;

        // 图贴右沿、按 16:9 整张画：宽从带高换。渐融贴住图的左沿 —— 图宽用的是「画出来的那一个」，不是带子
        // 想给它的那一个，免得窄窗口那一档渐融站到图外面去。
        var picture = Math.Min(width, height * HomeCarousel.WindowAspect);
        Fade.Margin = new Thickness(Math.Max(0, width - picture), 0, 0, 0);
        Fade.Width = picture * FadeShare;

        Info.MaxWidth = Math.Clamp(width * 0.54, 280, 620);
        Info.Margin = new Thickness(InfoInset, InfoTop, 0, 0);
        Dots.Margin = new Thickness(0, 0, 0, DotsBaseline);
    }

    /// <summary>
    /// 渐融伸进剧照多深，占图宽的比例。三成半：字块最宽的那一档（60 加 620）在开窗那一档 1422 宽的带上停在 680，
    /// 渐融到 867 才散尽 —— 字块的尾巴一直走在渐融里；最小窗口 900 宽那一档算下来还剩三个像素（见
    /// <c>HomeCarousel.PictureShare</c> 那一段的另一半账）。再窄字块就站到散尽了的亮图上，再宽图就只剩一扇窗。
    /// </summary>
    private const double FadeShare = 0.35;

    /// <summary>字块离带子左沿多远。见标记里 Info 那一段：让开的是翻页箭头那条窄栏。</summary>
    private const double InfoInset = 60;

    /// <summary>
    /// 字块离带子顶多远（「把轮播图左侧的徽标、片名、剧情说明等东西移到左上角」，2026-09-10）。116 不是窗口的
    /// 绝对左上角：HomePage 的 PageSlate 从 44 开始、占着页面抬头那一块，这 116 让字块站在它下面并留一口气，
    /// 而 PageSlate 自己不动。标记里 Info 那一份和 <see cref="Resize"/> 里这一份是同一个数，<see cref="Probe"/>
    /// 两处都守，不能只改一头。
    /// </summary>
    private const double InfoTop = 116;

    /// <summary>底边那排小横条离带子下沿多远。它是这条带自己的控件，不是画面的一部分，所以不跟着谁走。</summary>
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
    internal BannerSlide? Current => _index >= 0 && _index < _slides.Count ? _slides[_index] : null;

    // ---- 自检 -------------------------------------------------------------------

    /// <summary>
    /// 这条带在 <c>--self-check</c> 里的那一份读数，同 <see cref="ShelfStrip.Probe"/>：自检时没有服务器，主页
    /// 一张幻灯片也没有，所以问的是不依赖数据的那几件事，加上这份标记自己能不能解析。
    /// <para>
    /// <c>new HomeBanner()</c> 就把标记走了一遍：里面每个资源键（<c>DefaultButtonStyle</c>、
    /// <c>EgOnScrimBrush</c>、四组改掉的按钮状态键、四层渐变）解析不了就在这里抛。渐变里写颜色键会在高对比度
    /// 下抛，也是这一句挡着的 —— 那本词典里只有画刷。
    /// </para>
    /// <para>
    /// 剩下那几件是屏上的行为里 Core 摸不到的部分：一张都没有时整条带收起来、底边那排横条造得出来且亮在对的那
    /// 根、带高按页宽落到布局上、**剧照靠带子的右沿站**（「把主页的轮播图移动到右边」，2026-09-09）、**字块贴着
    /// 左上角而徽标是它的第一行**（2026-09-05「移到左下角，然后把徽标移到剧名上面」、2026-09-10 从下沿挪到顶上）、
    /// **带上四层黑渐变各是各的形状**：顶上给标题栏垫底那条，剧照左沿那道渐融（「给轮播图左边加上黑色渐变」，
    /// 2026-09-09，判的是贴图的左沿、左头实心到底、往里走到全透明，见 <see cref="Melts"/>），加下、右两条只压
    /// 边缘的（「给轮播页面边缘加上黑色的渐变」，2026-09-05，判的是每一层的形状而不是层数，见 <see cref="Rims"/>）、
    /// 翻页箭头和字块不在同一列、两层剧照真的轮着上。字块那段错拍动画顺带跑一遍，故事板里哪个目标是空的就在
    /// 这里抛，而不是等到主页第一次换幻灯片。
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

        // 带高落到布局上，而不只是算出来：带高是「剧照缩到带宽六成」按 16:9 算的（HomeCarousel.Height），1100 宽
        // 的带就是 371 高 —— 图 660 宽靠右，左边那 440 是字块的底色。这份控件没有 XamlRoot，量不到一屏有多高，
        // 所以那道「不超过一屏」的封顶这一趟不参与。
        banner.Resize(1100);
        var height = HomeCarousel.Height(0, 1100);
        var picture = height * HomeCarousel.WindowAspect;
        var gutter = 1100 - picture;
        var wide = banner.Info.MaxWidth;
        var tall = Math.Abs(banner.Root.Height - height) < 0.01 && wide <= 620;

        // 剧照靠带子的右沿站（「把主页的轮播图移动到右边」，2026-09-09）：两层都得靠右 —— 左边让出来的那四成是
        // 字块的底色，谁把哪一层改回居中，屏上就是图压在字上。
        var docked = banner.LayerA.HorizontalAlignment == HorizontalAlignment.Right
            && banner.LayerB.HorizontalAlignment == HorizontalAlignment.Right;

        // 字块贴左上角，徽标是它的第一行（2026-09-05「移到左下角，然后把徽标移到剧名上面」，2026-09-10 从下沿
        // 挪到顶上）。徽标那一条判的是**它在字块里、而且排在片名前面**，不是它的坐标 —— 它从前是压在角上的一张
        // 独立的图，谁把它挪回去，这一条当场红。三行字的 TextAlignment 一起判：整块靠左，字也得靠左。全从标记里
        // 设死的对齐上读，量的不是坐标。每一行外面套着一层 Grid（那层是影子的落脚处，见下面 inked），所以字块的
        // 头两个孩子是那两格、不是图和字本身 —— 图和字在各自那一格里另判一次。
        var corners = banner.Info.HorizontalAlignment == HorizontalAlignment.Left
            && banner.Info.VerticalAlignment == VerticalAlignment.Top
            && banner.Info.Children.Count > 1
            && ReferenceEquals(banner.Info.Children[0], banner.LogoRow)
            && ReferenceEquals(banner.Info.Children[1], banner.TitleRow)
            && banner.LogoRow.Children.Contains(banner.LogoImage)
            && banner.TitleRow.Children.Contains(banner.TitleText)
            && banner.TitleText.TextAlignment == TextAlignment.Left
            && banner.CaptionText.TextAlignment == TextAlignment.Left
            && banner.SynopsisText.TextAlignment == TextAlignment.Left;

        // 四行字底下那层跟着字形走的影子（<see cref="TextInk"/>）。判的是**宿主排在字前面**这件事：挂上去的那层
        // 视觉画在宿主自己的内容之上，所以宿主排到字后面就是影子盖在字上 —— 而那在截图里是「字脏了」，不是
        // 「少了一层」，最难看出来。影子本身画得对不对只有亮剧照上看得出，那是截图的活。
        var inked = Inked(banner.LogoRow, banner.LogoInk, banner.LogoImage)
            && Inked(banner.TitleRow, banner.TitleInk, banner.TitleText)
            && Inked(banner.CaptionRow, banner.CaptionInk, banner.CaptionText)
            && Inked(banner.SynopsisRow, banner.SynopsisInk, banner.SynopsisText);

        // 字块 2026-09-10 挪到了顶上：Resize 写下去的顶距就是这个数 —— 标记和代码两份，这里两份都对。底边那排
        // 小横条照旧自己贴着下沿；字块挪到上方之后它俩不再共用一条避让规则，各守各的边距。
        var placed = Math.Abs(banner.Info.Margin.Top - InfoTop) < 0.01
            && Math.Abs(banner.Info.Margin.Bottom) < 0.01
            && Math.Abs(banner.Dots.Margin.Bottom - DotsBaseline) < 0.01;

        // 带上四层黑渐变，各判各的形状。顶上给标题栏垫底那条：写死高度、贴着上沿。剧照左沿那道渐融
        // （「给轮播图左边加上黑色渐变」，2026-09-09）：**正好站在剧照的左沿上** —— Margin 和 Width 都是 Resize
        // 按带高摆的，这里拿「带宽 − 图宽」这笔账再对一遍 —— 而且左头实心到底（和底色同色，图的左沿就此消失）、
        // 往图里走到全透明（Melts）。它两头都不在带子的边上，所以不跟下、右两条走 Rims 那句「半张之前散尽」——
        // 那条管的是「只压边缘」，这一条管的正是「把边缘藏掉」。下、右两条还是「贴边最浓、半张之前散尽」的边缘
        // 渐变（「给轮播页面边缘加上黑色的渐变」，2026-09-05，来回过两趟：原先三层 → 全删 → 只压边缘）。谁把某一
        // 层的形状改回去，这一条当场红，而张数、带高、字块那几行读数一个都不会动。
        var scrims = banner.Band.Children.OfType<Border>().ToList();
        var capped = scrims.Where(one => !double.IsNaN(one.Height)).ToList();
        var sized = scrims.Where(one => double.IsNaN(one.Height) && !double.IsNaN(one.Width)).ToList();
        var edges = scrims.Where(one => double.IsNaN(one.Height) && double.IsNaN(one.Width)).ToList();
        var bare = scrims.Count == 4
            && capped is [{ } top]
            && top.VerticalAlignment == VerticalAlignment.Top
            && Sinks(top)
            && sized is [{ } fade]
            && fade.HorizontalAlignment == HorizontalAlignment.Left
            && Math.Abs(fade.Margin.Left - gutter) < 0.01
            && Math.Abs(fade.Width - picture * FadeShare) < 0.01
            && Melts(fade)
            && edges.Count == 2
            && edges.TrueForAll(Rims);

        // 翻页箭头和字块不同列（「翻页的按钮会挡住字体」）。箭头贴着带的左右边沿、在竖向正中；字块贴着左上角，
        // 左箭头贴边占的那条窄栏在另一列 —— 所以这一条判的仍然是列，不是高度。
        // 两边都从设死的边距和宽度上读，箭头默认是收起的、量不到位置，而这两个数收起来照样在。
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
        var risen = banner.TitleRow.Opacity == 1 && banner.TitleShift.Y == 0 && banner.ActionsShift.Y == 0;

        var ok = quiet && dots && lonely && tall && docked && corners && inked && placed && bare && layered && risen && apart;

        return (ok,
            $"没有幻灯片时{(quiet ? "整条带收起、钟不走" : "带还在屏上或钟在走")}；"
                + $"横条 3 根亮第 2 根{(dots ? "" : "（不对）")}、1 张时整排{(lonely ? "收起" : "还在")}；"
                + $"带高＝带宽×{HomeCarousel.PictureShare:0%}÷16×9（上限一屏、下限 {HomeCarousel.MinHeight:0}）："
                + $"带宽 1100→{height:0}、图 {picture:0} 宽靠右{(docked ? "" : "（有一层没靠右）")}、左边留 {gutter:0}；"
                + $"字块宽 页宽1100→{wide:0}；"
                + (corners
                    ? "字块贴左上角、三行字靠左，徽标是它的第一行、顶在片名头上；"
                    : "字块不在左上角，或者徽标不是字块的第一行，或者有一行字没靠左；")
                + (inked
                    ? $"四行字底下各一层跟着字形走的影子（模糊 {TextInk.SmallBlur:0}，片名那行 {TextInk.TitleBlur:0}）；"
                    : "字底下那层影子的宿主不在字前面（影子会盖在字上）；")
                + (placed
                    ? $"字块从顶上 {InfoTop:0} 起（标记和 Resize 两份对得上）、小横条离下沿 {DotsBaseline:0}"
                    : "字块的顶距或小横条的下边距不对")
                + "；"
                + $"画面上的暗罩 {scrims.Count} 层"
                + (bare
                    ? $"（顶上给标题栏垫底那条 {capped[0].Height:0} 高；剧照左沿一道渐融，从 {sized[0].Margin.Left:0} 起、"
                        + $"宽 {sized[0].Width:0}，左头实心到底、往图里散尽；下、右两条各压一条边、半张之前散尽："
                        + $"{string.Join('、', edges.Select(RimRead))}）"
                    : "（该是四层：顶上写死高度那条、剧照左沿一道渐融、下和右两条只压边缘的 —— 形状或位置对不上）")
                + "；"
                + $"箭头占到 {strip:0}、字块从 {banner.Info.Margin.Left:0} 起"
                + $"{(apart ? "，两边不同列" : "，压到字了")}；"
                + $"两层剧照{(layered ? "轮着上，同一张不重来" : "没换过位置")}；"
                + $"四行错 {Stagger.TotalMilliseconds:0} 毫秒、{Lift.TotalMilliseconds:0} 毫秒抬起 {TextDrop:0} 像素"
                + $"{(risen ? "后落定" : "但没落定")}，换图 {CrossFade.TotalMilliseconds:0} 毫秒；"
                + $"没人碰时每 {HomeCarousel.Dwell.TotalSeconds:0} 秒走一张，最多 {HomeCarousel.Slots} 张");
    }

    /// <summary>
    /// 一行字底下那层影子挂对了没有：宿主在这一格里、而且排在字**前面**。挂上去的那层视觉画在宿主自己的内容之上，
    /// 所以次序反了就是影子盖在字上（见 <see cref="TextInk"/>）。
    /// </summary>
    private static bool Inked(Grid row, UIElement ink, UIElement text) =>
        row.Children.Count >= 2 && ReferenceEquals(row.Children[0], ink) && ReferenceEquals(row.Children[1], text);

    /// <summary>
    /// 顶上那一条的形状：贴着上沿那一头够浓，到另一头散尽。它靠写死的高度把自己关在边上，所以不需要
    /// <see cref="Rims"/> 那句「半张之前就散尽」。
    /// </summary>
    private static bool Sinks(Border layer) =>
        Ramp(layer) is [{ } first, .., { } last] && first.Color.A > 0x40 && last.Color.A == 0;

    /// <summary>
    /// 剧照左沿那道渐融的形状：**横向**（贴图那一头在左）、贴图那头实心到底（#FF0C0E11，和带子的底色同一个颜色
    /// —— 图的左沿在屏上就此消失），往图里走到全透明。它两头都不在带子的边上（左边是底色、右边在图里），所以
    /// 不跟下、右两条走 <see cref="Rims"/> 那句「半张之前就散尽」—— 那条管的是「只压边缘」，这一条管的正是
    /// 「把边缘藏掉」。
    /// </summary>
    private static bool Melts(Border layer) =>
        layer.Background is LinearGradientBrush { StartPoint.X: 0, EndPoint.X: 1 }
            && Ramp(layer) is [{ } first, .., { } last]
            && first.Color.A >= 0xF0
            && last.Color.A == 0;

    /// <summary>
    /// 一条边缘渐变的形状，三条一个样子：<c>Offset</c> 0 那一头（也就是它自己贴的那条边）够浓，**到半张处已经全
    /// 透明、半张之后一点不压**。后面这一句才是「只压边缘」和「压住整张画面」的分界线 —— 被删掉的那三层里横着
    /// 那一层正是从左沿一路淡到右沿的，它过不了这一条。见 HomeBanner.xaml 里那一段。
    /// </summary>
    private static bool Rims(Border layer)
    {
        var ramp = Ramp(layer);

        return ramp is [{ } first, ..]
            && first.Color.A > 0x40
            && ramp.Any(stop => stop.Color.A == 0 && stop.Offset <= 0.5)
            && ramp.All(stop => stop.Offset <= 0.5 || stop.Color.A == 0);
    }

    /// <summary>
    /// 一条边缘渐变在报告里的读数：贴的是哪条边、贴边那一头多浓、走到哪儿散尽（比如「左 55%→0.33」）。
    /// <para>
    /// 这两个数是他在屏幕前挑的（「渐变弄淡一些，面积弄少一些」，2026-09-05 —— 见 HomeBanner.xaml 里那张表），
    /// 而 <see cref="Rims"/> 故意只守「贴边那头够浓、半张之前散尽」这条更松的底线：**它钉的是「只压边缘」这件事
    /// 本身，不是他挑的浓度**。所以浓度和深度得由报告说出来，否则屏上淡了一档、报告一个字不动，等于没人看着它。
    /// 贴哪条边不从标记的次序猜，问渐变自己从哪个角起 —— 谁把三层的次序换了，这一读照旧说得对。
    /// </para>
    /// </summary>
    private static string RimRead(Border layer)
    {
        var ramp = Ramp(layer);

        if (layer.Background is not LinearGradientBrush brush || ramp.Count == 0)
        {
            return "读不出来（不是线性渐变）";
        }

        var side = brush.StartPoint.X == 0 ? "左"
            : brush.StartPoint.X == 1 ? "右"
            : brush.StartPoint.Y == 1 ? "下" : "上";
        var gone = ramp.FirstOrDefault(stop => stop.Color.A == 0);
        var where = gone is null ? "没散尽" : $"{gone.Offset:0.00}";

        return $"{side} {ramp[0].Color.A / 255d * 100:0}%→{where}";
    }

    /// <summary>一层暗罩的渐变，按 <c>Offset</c> 排好。不是线性渐变就交回空的，那时上面两条都判假。</summary>
    private static IReadOnlyList<GradientStop> Ramp(Border layer) =>
        layer.Background is LinearGradientBrush brush
            ? [.. brush.GradientStops.OrderBy(stop => stop.Offset)]
            : [];

    /// <summary>自检用：自动翻页的钟在不在走。</summary>
    private bool Ticking => _timer?.IsRunning ?? false;

    /// <summary>
    /// 自检用：这条带在真页面上的读数。<see cref="Probe"/> 拿假图证规则，这一句证的是规则之外的三件事 ——
    /// 带在 <c>ScrollView</c> 的竖排里真量到了宽度、那个宽度真变成了高度、台上那张的剧照真解码进了前面那层。
    /// 三样里少一样，屏上就是一条空带，而 <see cref="HomeViewModel.BannerSummary"/> 报的张数照旧好看。
    /// <para>
    /// 字块的高和它底下剩下的余量一起报：字块从顶上 <see cref="InfoTop"/> 往下排，而它自己有多高只有屏上量得
    /// 出来 —— 「带高 − 顶距 − 字块高」就是它底下还剩多少。这一位变成负数，就是简介和按键那一头伸出了带的
    /// 下沿、压到底边那排小横条上（下限 240 那一档正是这样，所以它继续报）。
    /// </para>
    /// <para>
    /// 带自己的形状也报作诊断：剧照贴右沿、按 16:9 整张画，图宽从带高换（带高×16÷9），带宽减它就是左边留给
    /// 字块的那一截；渐融（<c>Fade</c>）的 Margin 和 Width 是代码摆的，把站的位置连着字块的余量一起说出来。
    /// 屏上是不是真这样，由 <see cref="PictureRead"/> 在真实页面上量。
    /// </para>
    /// </summary>
    internal string State
    {
        get
        {
            if (_slides.Count == 0) return "收起";

            var height = double.IsNaN(Root.Height) ? 0 : Root.Height;
            var viewport = _viewport?.Size.Height ?? 0;
            var gutter = Fade.Margin.Left;
            var fade = double.IsNaN(Fade.Width) ? 0 : Fade.Width;

            return $"带高 {height:0}、剧照靠右从 {gutter:0} 起（渐融 {fade:0} 宽）、字块宽 {Info.MaxWidth:0}、"
                + $"字块高 {Info.ActualHeight:0} 从顶上 {InfoTop:0} 起（底下余 {height - InfoTop - Info.ActualHeight:0}）、"
                + $"带 {(height > 0 ? Root.ActualWidth / height : 0):0.00}:1"
                + $"（窗口高 {viewport:0}）、"
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
    /// 判三件事：画出来的形状就是原图的形状（没裁也没拉）、**画到了这条带里能画的最大**、**贴着带子的右沿**
    /// （「把主页的轮播图移动到右边」，2026-09-09 —— 从前那一档判的是「左右两条留白一样宽」，剧照居中；挪到
    /// 右边之后留白全在左边，是字块站的那一片）。图还没解码回来时没有得量，那一档只报不判 —— 那是网络的事，
    /// 不是版面的事。
    /// </para>
    /// <para>
    /// 「画到最大」判的是**贴住吃紧的那一边**，而不是一律要求吃满带高：带子弄扁之后正常那一档高度吃紧（底色留在
    /// 左边），带宽不到 427、下限那一档宽度吃紧（底下留一条底色）。从前这里写死了「吃满带高」，因为「锁定窗口
    /// 比例大小」把浏览区一直按在 16:9 上、两边同时吃紧；那个开关 2026-09-05 删掉之后窗口什么形状都拉得出来，
    /// 写死那一句就变成了「窗口不是 16:9 就报错」。
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
        var filledHeight = Math.Abs(drawn.Height - band.Height) <= 1.5;
        var filledWidth = Math.Abs(drawn.Width - band.Width) <= 1.5;
        var biggest = filledHeight || filledWidth;
        var docked = right <= 1.5;

        return (whole && biggest && docked,
            $"剧照 {drawn.Width:0}×{drawn.Height:0} = {shape:0.000}:1"
                + (sourceShape > 0
                    ? $"（原图 {source!.PixelWidth}×{source.PixelHeight} = {sourceShape:0.000}:1"
                        + (whole ? "，没裁也没拉）" : "，画出来的形状和原图不一样）")
                    : "（问不到原图尺寸）")
                + $"；带 {band.Width:0}×{band.Height:0}"
                + (filledHeight && filledWidth ? "，正好铺满整条带"
                    : filledHeight ? "，吃满带高（底色留在左边）"
                    : filledWidth ? "，吃满带宽（下限那一档，底下留一条底色）"
                    : "，两边都没吃满 —— 没画到能画的最大")
                + $"；左留 {left:0}、右留 {right:0}"
                + (docked ? "，贴着带子的右沿" : "，没贴到带子的右沿")
                + $"（左边那截是字块的底色，字块最宽 {Info.MaxWidth:0}）");
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
