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
using Windows.UI.ViewManagement;

// 上面那两个命名空间各有一个 DispatcherQueueTimer。Windows.System 在这里只为 VirtualKey 而来，计时器要的是
// Microsoft.UI 那个（WinUI 的 DispatcherQueue 发的就是它），所以指名一次，省下每处的全名。
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 主页最上面那条大图轮播 —— 「参考"主页轮播大图版-misty-4.9.css"给主页轮播功能」。
/// <para>
/// 一张幻灯片是一条剧照（铺满整条带）、上面压一道给字垫底的渐变幕、左边站着一块字（<see cref="BannerSlide"/>），
/// 这个控件是那条带：谁在台上、什么时候换、换的时候怎么过去。哪些条目上得了台、一共几张、带该多高，都是
/// <see cref="HomeCarousel"/> 里的纯函数，由 Core 的测试盯着；这里剩下的是屏上那部分。
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
    private const double DotHit = 24;

    /// <summary>
    /// 换图的那一下：两层交叉着淡入淡出。两下同时走，所以这也是「上一张还看得见」的时长。不叫 <c>Fade</c> 是
    /// 2026-09-09 之后的事：左边那道渐变幕在标记里占了 <c>Fade</c> 这个名（<c>HomeBanner.xaml</c>），一个类里
    /// 摆不下两个。
    /// </summary>
    private static readonly TimeSpan CrossFade = TimeSpan.FromMilliseconds(650);

    /// <summary>一行字抬起来用多久。</summary>
    private static readonly TimeSpan Lift = TimeSpan.FromMilliseconds(600);

    /// <summary>下一行比上一行晚多久起。misty 那份是 .4/.6/.8 秒，四行错完接近一秒，这里压到四行 210 毫秒。</summary>
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(70);

    /// <summary>
    /// 每张剧照的慢镜头时长。与交叉淡入分开：图片七秒缓慢舒展，字和按钮的位置保持不动。
    /// <para>
    /// 只动背景图层的 ScaleTransform，以中心为原点；画框裁掉溢出的边缘，不触发布局。
    /// </para>
    /// </summary>
    private static readonly TimeSpan Push = TimeSpan.FromSeconds(7);

    /// <summary>推近的那一点点。见 <see cref="Push"/>。</summary>
    private const double PushScale = 0.045;

    /// <summary>「鼠标移出窗口后不会自动恢复」：翻页箭头浮出来了还得收回去，理由见 <see cref="HoverWatch"/>。</summary>
    private readonly HoverWatch _watch;

    /// <summary>没人碰的时候自己走一张，每 <see cref="HomeCarousel.Dwell"/> 一步。</summary>
    private readonly DispatcherQueueTimer? _timer;

    /// <summary>底边那排小横条里那几根真正上色的条，按幻灯片的顺序。</summary>
    private readonly List<Border> _bars = [];

    // 每一种动画只有一个所有者：换片先收尾，卸载全部停止，避免上一张的 Completed 改到下一张。
    private readonly Dictionary<string, (Storyboard Board, Action Settle)> _motions = [];
    private readonly UISettings _uiSettings = new();
    private bool _settingsObserved;
    private bool _focusWithin;
    private bool _active;

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
        StopMotions();
        Unwatch();
        foreach (var slide in _slides) slide.Release();

        _slides = [.. slides];
        _index = 0;

        BuildDots(_slides.Count);
        Visibility = _slides.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_slides.Count == 0)
        {
            _second = false;
            LayerA.Opacity = 0;
            LayerB.Opacity = 0;
            Brush(LayerA).ImageSource = null;
            Brush(LayerB).ImageSource = null;
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
        SynopsisRow.Visibility = Root.Height < 340 ? Visibility.Collapsed : slide.SynopsisVisibility;
        PlayText.Text = slide.PlayText;
        SlideCounter.Text = $"{_index + 1:00}  /  {_slides.Count:00}";
        SlideStatus.Visibility = _slides.Count > 1 && ActualWidth >= 800 ? Visibility.Visible : Visibility.Collapsed;

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

            var hit = new Button
            {
                Height = DotHit,
                MinHeight = 0,
                MinWidth = 0,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                Content = bar
            };

            AutomationProperties.SetName(hit, HomeCarousel.Position(index, count));
            AutomationProperties.SetAutomationId(hit, $"BannerSlide{index + 1}");
            hit.Click += (_, _) =>
            {
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
        if (ReferenceEquals(FrontPicture, picture)) return;

        StopMotion("image");

        var rising = _second ? LayerA : LayerB;
        var falling = FrontLayer;

        Brush(rising).ImageSource = picture;
        _second = !_second;

        var board = new Storyboard();
        board.Children.Add(FadeTo(rising, 1, CrossFade, TimeSpan.Zero));
        board.Children.Add(FadeTo(falling, 0, CrossFade, TimeSpan.Zero));

        Play(board, () =>
        {
            rising.Opacity = 1;
            falling.Opacity = 0;
        }, "image");

        Nudge();
    }

    /// <summary>
    /// 新剧照由 104.5% 缓慢回到 100%。一张图一段动画，快速连续翻页时先停止上一段并落定。
    /// </summary>
    private void Nudge()
    {
        StopMotion("camera");
        var scale = _second ? PictureScaleB : PictureScaleA;
        scale.ScaleX = 1 + PushScale;
        scale.ScaleY = 1 + PushScale;

        // 只慢推背景图，文字和按钮始终稳定；裁剪留在画框，边缘不会溢到下面的媒体内容。
        var ease = new SineEase { EasingMode = EasingMode.EaseOut };
        var board = new Storyboard();
        board.Children.Add(ScaleBand(scale, "ScaleX", 1, Push, TimeSpan.Zero, ease));
        board.Children.Add(ScaleBand(scale, "ScaleY", 1, Push, TimeSpan.Zero, ease));

        Play(board, () =>
        {
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }, "camera");
    }

    /// <summary>一段缩放。</summary>
    private static DoubleAnimation ScaleBand(
        DependencyObject subject, string path, double to, TimeSpan span, TimeSpan delay, EasingFunctionBase ease)
    {
        var one = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(span),
            BeginTime = delay,
            EasingFunction = ease,
        };

        Storyboard.SetTarget(one, subject);
        Storyboard.SetTargetProperty(one, path);

        return one;
    }

    /// <summary>
    /// 台上那层。两层轮着上，见 <see cref="Paint"/>。空 Grid 一层，图在它的背景画刷里（标记里那一段写着为什么
    /// 不是 <c>Image</c>）。
    /// </summary>
    internal Grid FrontLayer => _second ? LayerB : LayerA;

    /// <summary>台上那张图。读的地方有几处（换图前先比一比、自检报「还在取还是已上图」），所以收成一句。</summary>
    internal ImageSource? FrontPicture => Brush(FrontLayer).ImageSource;

    /// <summary>一层的背景画刷。标记里那两支 <c>ImageBrush</c> 是写死的，所以这里取不到就是标记被人改了。</summary>
    private static ImageBrush Brush(Panel layer) => (ImageBrush)layer.Background;

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
        StopMotion("logo");
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
        }, "logo");
    }

    /// <summary>
    /// 精选标识、标题、元信息、简介与操作五行错拍上浮。系统关闭动画时同一条路径立即落定。
    /// </summary>
    internal void Rise()
    {
        StopMotion("text");
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
        }, "text");
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
    /// 跑一段有明确所有者的动画。没进树或系统关闭动画时直接落定；完成后撤掉故事板，再写回终值。
    /// <para>
    /// 自检里这份控件没有 <c>XamlRoot</c>，那时 <c>Begin()</c> 既没人看也没有意义；而故事板照样是搭出来的，
    /// 所以 <c>SetTarget</c> 拿到一个空的变换（标记里漏了一个 <c>TranslateTransform</c>）在那里就抛。
    /// </para>
    /// <para>
    /// **<c>Completed</c> 上的那一遍 settle 不是装饰**：换一张的那一拍里，同一个元素可能先被淡出动画
    /// （上一张）再被淡入动画（这一张）碰过，两个故事板都活着时后到的 <c>Begin</c> 不保证把前一个的
    /// HoldEnd 掰掉 —— 09-10 那版左翼正是这么黑着的：图设上了、Opacity 停在 0（实拍＋日志量到）。落一遍
    /// 终值把这事兜死。
    /// </para>
    /// </summary>
    private void Play(Storyboard board, Action settle, string channel)
    {
        if (!_active || XamlRoot is null || !_uiSettings.AnimationsEnabled)
        {
            settle();
            return;
        }

        _motions[channel] = (board, settle);
        board.Completed += (_, _) =>
        {
            if (!_motions.TryGetValue(channel, out var current) || !ReferenceEquals(current.Board, board)) return;
            _motions.Remove(channel);
            board.Stop();
            settle();
        };
        board.Begin();
    }

    private void StopMotion(string channel)
    {
        if (!_motions.Remove(channel, out var motion)) return;
        motion.Board.Stop();
        motion.Settle();
    }

    private void StopMotions()
    {
        foreach (var channel in _motions.Keys.ToArray()) StopMotion(channel);
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
        var shown = (_hover || _focusWithin) && _slides.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        PrevButton.Visibility = shown;
        NextButton.Visibility = shown;
    }

    /// <summary>
    /// 自动翻页的钟。指针在带上就停 —— 正在看这张的人不该被推走；只有一张、还没进树、或者一张都没有时也不走。
    /// 每次调用都重新起算，所以人手翻一张之后是整整八秒。
    /// </summary>
    private void SyncTimer()
    {
        StopMotion("countdown");
        CountdownScale.ScaleX = 0;
        if (_timer is null) return;

        _timer.Stop();

        if (_slides.Count <= 1 || _hover || _focusWithin || !_active || !_uiSettings.AnimationsEnabled) return;

        _timer.Start();
        var board = new Storyboard();
        var countdown = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(HomeCarousel.Dwell)
        };
        Storyboard.SetTarget(countdown, CountdownScale);
        Storyboard.SetTargetProperty(countdown, "ScaleX");
        board.Children.Add(countdown);
        Play(board, () => CountdownScale.ScaleX = 1, "countdown");
    }

    /// <summary>
    /// 带多高、渐变幕站在哪儿、字块多宽。带高走 <see cref="HomeCarousel.Height"/>：带宽按 16:9 算出来的高的六成
    /// （「把轮播图弄扁一些」，2026-09-09），也就是带子自己的形状 2.96:1；**剧照的几何这里一句都不写** ——
    /// 两层是 <c>Stretch="UniformToFill"</c>、对齐没有写（默认 Stretch），它们自己撑满 Band 的内沿、图按自己
    /// 的比例放大到盖住整格。
    /// <para>
    /// **这里是 2026-09-11「去掉首页封面轮播图的黑边」砍得最狠的一处**：从前 <c>Resize</c> 要算图宽（带高÷0.9×
    /// 16÷9、再和带宽取 min）、两层的显式高（带高÷<c>KeptShare</c>）、左右各让出多宽（<c>gutter</c>），还有渐变
    /// 幕公式里的第一项。图铺满之后那三样全没了 —— 只剩「两层就是 Band 的内沿那么大」这一条，裁多少、裁在哪
    /// 交给 <c>UniformToFill</c> 自己算，而它算得对不对由 <see cref="PictureRead"/> 报出来。**别把图宽加回来**：
    /// 图一旦比带子窄（哪怕只窄一条边），屏上就是他要去掉的那条黑边。
    /// </para>
    /// <para>
    /// 渐变幕（左头近实心、走进图里散尽）从带子自己的左沿起、宽取「字块左沿 ＋ 字块最宽 ＋
    /// <see cref="ScrimBreath"/>」—— 字整块压在剧照上，盖不住就是亮底上读简介，所以 <c>Info.MaxWidth</c> 得先
    /// 算出来。从前还要和「左边纯色那一片的宽 ＋ 图宽的三成半」取大的那个（那一项是揉开纯色底和剧照之间那条
    /// 硬边用的），纯色底没了它也就没了。
    /// </para>
    /// <para>
    /// 字块 2026-09-05 走过两趟：先按「把红框框出来的移到右下角」挪去了右下角，同一天又按「移到左下角，然后把徽标
    /// 移到剧名上面」挪回左边、贴着下沿，徽标从压在角上的一张独立的图变成了这一叠的第一行。两趟一起丢掉的是「往下
    /// 沉一点」那条规则（从前的 <c>InfoShift</c> 加 <c>HomeCarousel.InfoDrop</c>）和 <c>PlaceLogo</c> 那一段留白
    /// 计算：站在字块里的徽标由布局给位置。2026-09-10 又两趟：先按「把红框里的东西移动到左上角」挪到顶上，再按
    /// 「字块放到封面居中的位置」改成竖向居中 —— 还是布局给位置（对齐说了话，连顶距的常数都没了），别把那条
    /// 下沉规则请回来。
    /// </para>
    /// <para>
    /// 整条带都是自己的：继续观看从前「压在图的下半截上」，后来当过第一屏右边那一栏（那一栏 2026-09-08 随着
    /// 「移除轮播图右边的媒体库」删掉了），所以字块和底边那排小横条不用让开谁。带宽就是整个页宽（HomePage 标记
    /// 里那一条负的上边距把带子顶到窗口的顶边）—— 2026-09-10 到 09-11 之间它曾经是「页宽减四边各 24」，那一条
    /// 随「占满窗口的上半部分（包括窗口标题）」作废。
    /// </para>
    /// <para>
    /// 一屏是带高的上限（超宽屏上「带宽 × 六成 ÷ 16 × 9」算出来会比一屏还高），问的是 <c>XamlRoot.Size</c>，
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
        Frame.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, width), height) };

        // 两层剧照不在这里给尺寸：空 Grid 由对齐撑满 Band 的内沿，图在背景那支 ImageBrush 里按 UniformToFill
        // 铺满它。**别把尺寸或 Image 控件加回来** —— 2026-09-11 第一趟试过 <c>Image Stretch="UniformToFill"</c>
        // （宽高都按内沿给死）：自检量到元素 1014×570 而带子 1014×341，实拍里图从带子顶上开始铺、下沿那截花田
        // 整片被裁掉，也就是裁在了顶上而不是居中。Image 按内容取尺寸这条脾气，标记里那一段写着。
        //
        // 几何这一层 2026-09-11 简化过一道：框成卡片那一版这里要按 Frame 那圈发丝线的内沿算（差一圈就是图左沿
        // 那道两像素的亮缝），现在 Frame 又是个没有线、没有留白的 Grid，内沿就是控件自己的宽，那一整段连同
        // 「按内沿算」的说法一起删了。
        Info.MaxWidth = Math.Min(Math.Max(0, width - InfoInset - 28), Math.Clamp(width * 0.48, 280, 620));
        Info.Spacing = height < 360 ? 8 : 12;
        LogoImage.MaxHeight = height < 360 ? 32 : 44;
        TitleText.MaxLines = height >= 440 ? 2 : 1;
        SynopsisRow.Visibility = height < 340 ? Visibility.Collapsed : Current?.SynopsisVisibility ?? Visibility.Collapsed;
        SlideStatus.Visibility = _slides.Count > 1 && width >= 800 ? Visibility.Visible : Visibility.Collapsed;

        Fade.Margin = new Thickness(0, 0, 0, 0);
        Fade.Width = Math.Min(width, InfoInset + Info.MaxWidth + ScrimBreath);

        Info.Margin = new Thickness(InfoInset, 0, 0, 0);
        Dots.Margin = new Thickness(0, 0, 0, DotsBaseline);
    }

    /// <summary>
    /// 渐变幕盖过字块尾巴多少。幕是那块字的底衬（见 <see cref="Resize"/> 那一句），右边留一口气再散尽，
    /// 免得最后一行字的右端正好停在幕的尽头、看着像被切了一刀。字块整个压在剧照上，幕必须自己兑现「盖住字」
    /// 这件事 —— 09-11 起尤其如此：带里没有纯色底了，字底下就是图。
    /// </summary>
    private const double ScrimBreath = 96;

    /// <summary>字块离带子左沿多远。见标记里 Info 那一段：让开的是翻页箭头那条窄栏。</summary>
    private const double InfoInset = 60;

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

    private void OnBannerGotFocus(object sender, RoutedEventArgs e)
    {
        _focusWithin = true;
        SyncArrows();
        SyncTimer();
    }

    private void OnBannerLostFocus(object sender, RoutedEventArgs e)
    {
        // 焦点从一颗按钮走到另一颗时仍留在轮播里，等新的焦点落稳后再决定是否恢复。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_active) return;
            var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
            _focusWithin = false;
            for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (!ReferenceEquals(node, Root)) continue;
                _focusWithin = true;
                break;
            }

            SyncArrows();
            SyncTimer();
        });
    }

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
        _active = true;
        if (!_settingsObserved)
        {
            _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
            _settingsObserved = true;
        }

        // 控件返回可视树时重新订阅当前图片；Unloaded 释放过的图允许从磁盘缓存重读。
        if (_slides.Count > 0)
        {
            Show(_index);
            if (FrontPicture is not null) Nudge();
        }
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
        _active = false;
        _focusWithin = false;
        _timer?.Stop();
        _watch.Leave();
        Unwatch();
        StopMotions();
        if (_settingsObserved)
        {
            _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
            _settingsObserved = false;
        }

        UnwatchViewport(_viewport);
        _viewport = null;

        foreach (var slide in _slides) slide.Release();
        Brush(LayerA).ImageSource = null;
        Brush(LayerB).ImageSource = null;
        LogoImage.Source = null;
    }

    private void OnAnimationsEnabledChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_active) return;
        StopMotions();
        SyncTimer();
    });

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
    /// <c>EgOnScrimBrush</c>、四组改掉的按钮状态键、三层渐变）解析不了就在这里抛。渐变里写颜色键会在高对比度
    /// 下抛，也是这一句挡着的 —— 那本词典里只有画刷。
    /// </para>
    /// <para>
    /// 剩下那几件是屏上的行为里 Core 摸不到的部分：一张都没有时整条带收起来、底边那排横条造得出来且亮在对的那
    /// 根、带高按页宽落到布局上、**外面一圈圆角发丝框**（「弄个框把轮播图框起来（圆角）」，2026-09-10）、
    /// **两层剧照 <c>UniformToFill</c> 撑满整格**（「去掉首页封面轮播图的黑边」，2026-09-11 —— 判的是拉伸方式、
    /// 对齐和「尺寸正好是 Band 的内沿」三样，因为这三样少一样，屏上就回到「图比带子窄、一边露底色」那一版）、
    /// **字块竖向居左而徽标是它的第一行**（2026-09-05「移到左下角，然后把徽标移到剧名上面」、2026-09-10 居中）、
    /// **带上三层黑渐变各是各的形状**：左边那道渐变幕（2026-09-10，判的是从带沿起、左头九成浓、往图里走到全
    /// 透明，见 <see cref="Melts"/>），加下、右两条只压边缘的（「给轮播页面边缘加上黑色的渐变」，2026-09-05，判的
    /// 是每一层的形状而不是层数，见 <see cref="Rims"/>）、翻页箭头和字块不在同一列、两层剧照真的轮着上。字块那段
    /// 错拍动画顺带跑一遍，故事板里哪个目标是空的就在这里抛，而不是等到主页第一次换幻灯片。
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

        // 带高落到布局上，而不只是算出来：扩展到原快捷区域后，1100 宽的封面为 470 高。
        // 这份控件没有 XamlRoot，量不到一屏有多高，所以那道「不超过一屏」的封顶这一趟不参与。
        banner.Resize(1100);
        var height = HomeCarousel.Height(0, 1100);
        var width = 1100d;
        var wide = banner.Info.MaxWidth;
        var tall = Math.Abs(banner.Root.Height - height) < 0.01 && wide <= 620;

        // 框没有了（「占满窗口的上半部分（包括窗口标题）」，2026-09-11）：带子铺到窗口的三条边上，贴着窗口的
        // 边画圆角会在窗口角上剪出三角形的缺口，整圈发丝线也没有边可收，卡片底色更是要遮住剧照 —— 2026-09-10
        // 到 09-11 之间那套（Frame 是个 Border，圆角走词表、整圈走 EgHairline）整个作废。现在 Frame 是个没有
        // 圆角、没有线、没有留白的 Grid，只剩带子自己那支底色（图没解码到的那一瞬兜底）。
        var framed = banner.Frame.CornerRadius == default(CornerRadius)
            && banner.Frame.BorderThickness == default(Thickness);

        // 两层剧照铺满整条带（「去掉首页封面轮播图的黑边」，2026-09-11）：背后是 <c>ImageBrush Stretch="UniformToFill"</c>
        // —— 画刷按图自己的比例放大到盖住整格、多出来的一截对半裁掉（AlignmentX/Y 默认 Center），空 Grid 由对齐
        // 撑满整格，一个像素都不拉、两边也不再露底色。**判的是这三样**：网格里没有别的东西（有孩子就不再是「空壳
        // 撑满」）、背景是那支画刷、画刷的拉伸方式是 UniformToFill。裁多少、裁在哪由 PictureRead 在真实页面上量。
        var filled = BrushStretch(banner.LayerA) == Stretch.UniformToFill
            && BrushStretch(banner.LayerB) == Stretch.UniformToFill
            && banner.LayerA.Children.Count == 0 && banner.LayerB.Children.Count == 0
            && banner.LayerA.HorizontalAlignment == HorizontalAlignment.Stretch
            && banner.LayerA.VerticalAlignment == VerticalAlignment.Stretch
            && banner.LayerB.HorizontalAlignment == HorizontalAlignment.Stretch
            && banner.LayerB.VerticalAlignment == VerticalAlignment.Stretch
            && double.IsNaN(banner.LayerA.Width) && double.IsNaN(banner.LayerA.Height)
            && double.IsNaN(banner.LayerB.Width) && double.IsNaN(banner.LayerB.Height);

        // 字块竖向居中站在图上（「字块放到封面居中的位置」，2026-09-10），徽标是它的第一行（2026-09-05「移到
        // 左下角，然后把徽标移到剧名上面」）。徽标那一条判的是**它在字块里、而且排在片名前面**，不是它的坐标 ——
        // 它从前是压在角上的一张独立的图，谁把它挪回去，这一条当场红。三行字的 TextAlignment 一起判：整块靠左，
        // 字也得靠左。全从标记里设死的对齐上读，量的不是坐标。每一行外面套着一层 Grid（那层是影子的落脚处，见下
        // 面 inked），所以字块的头两个孩子是那两格、不是图和字本身 —— 图和字在各自那一格里另判一次。
        var corners = banner.Info.HorizontalAlignment == HorizontalAlignment.Left
            && banner.Info.VerticalAlignment == VerticalAlignment.Center
            && banner.Info.Children.Count > 2
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

        // 字块 2026-09-10 竖向居中：顶距这一份没有了（对齐说了话），左右两份还在 —— 左边距让开箭头那条窄栏，
        // 字块自己的上下 Margin 该是 0。底边那排小横条照旧自己贴着下沿，各守各的边距。
        var placed = Math.Abs(banner.Info.Margin.Left - InfoInset) < 0.01
            && Math.Abs(banner.Info.Margin.Top) < 0.01
            && Math.Abs(banner.Info.Margin.Bottom) < 0.01
            && Math.Abs(banner.Dots.Margin.Bottom - DotsBaseline) < 0.01;

        // 带上四层黑渐变，各判各的形状。左边那道渐变幕（2026-09-10）：从带子自己的左沿起（Margin.Left 是 0）、
        // 宽按「字块左沿 + 字块最宽 + ScrimBreath」算（Width 是 Resize 按这笔账写的，这里再对一遍）—— 而且左头
        // 近实心（#E6、九成压住，图还透得出来）、往图里走到全透明（Melts）。它一头在带子的边上、一头按字块算，
        // 所以不跟下、右两条走 Rims 那句「半张之前散尽」。**它 09-11 没跟另外两条一起动**：那两条压的是带子的边
        // （他说的「黑边」是这两条底下的纯色底，不是幕），幕压的是字底下的图。
        //
        // **顶上那条 120 高的 `Top` 是 2026-09-10 删掉、09-11 找回来的**（通栏之后标题栏又浮在剧照上），它跟下、
        // 右两条一样是「贴边最浓、半张之前散尽」的边缘渐变，所以照样进 edges、照样走 Rims —— Rims 量的就是这件事
        // 本身，不关心那一层在带子的哪条边上。下、右两条还是 2026-09-05「给轮播页面边缘加上黑色的渐变」那一版
        // （来回过两趟：原先三层 → 全删 → 只压边缘）。
        //
        // 谁把某一层的形状改回去，或者多加／少加一层压在带子里的 Border，这一条当场红，而张数、带高、字块那几行
        // 读数一个都不会动。
        var scrims = banner.Band.Children.OfType<Border>().ToList();
        var sized = scrims.Where(one => double.IsNaN(one.Height) && !double.IsNaN(one.Width)).ToList();
        var edges = scrims.Where(one => double.IsNaN(one.Height) && double.IsNaN(one.Width)).ToList();
        var topped = scrims.Where(one => !double.IsNaN(one.Height) && double.IsNaN(one.Width)).ToList();
        var bare = scrims.Count == 4
            && sized is [{ } fade]
            && fade.HorizontalAlignment == HorizontalAlignment.Left
            && Math.Abs(fade.Margin.Left) < 0.01
            && Math.Abs(fade.Width - Math.Min(width, InfoInset + wide + ScrimBreath)) < 0.01
            && Melts(fade)
            && topped is [{ } top]
            && top.VerticalAlignment == VerticalAlignment.Top
            && Caps(top)
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
        var onB = ReferenceEquals(banner.FrontLayer, banner.LayerB) && ReferenceEquals(banner.FrontPicture, one);

        banner.Paint(two);
        var onA = ReferenceEquals(banner.FrontLayer, banner.LayerA) && ReferenceEquals(banner.FrontPicture, two);

        banner.Paint(two);
        var layered = onB && onA
            && ReferenceEquals(banner.FrontLayer, banner.LayerA)
            && banner.LayerA.Opacity == 1
            && banner.LayerB.Opacity == 0;

        // 字块那段错拍动画。没进树，所以故事板搭出来之后直接落到终值。
        banner.Rise();
        var risen = banner.TitleRow.Opacity == 1 && banner.TitleShift.Y == 0 && banner.ActionsShift.Y == 0;

        var ok = quiet && dots && lonely && tall && framed && filled && corners && inked
            && placed && bare && layered && risen && apart;

        return (ok,
            $"没有幻灯片时{(quiet ? "整条带收起、钟不走" : "带还在屏上或钟在走")}；"
                + $"横条 3 根亮第 2 根{(dots ? "" : "（不对）")}、1 张时整排{(lonely ? "收起" : "还在")}；"
                + $"带高＝带宽×{HomeCarousel.BandHeightShare:0%}÷16×9（上限一屏、下限 {HomeCarousel.MinHeight:0}）："
                + $"带宽 1100→{height:0}；"
                + (framed
                    ? "带子没有框：圆角、发丝线、卡片边一概没有，铺到窗口的三条边上；"
                    : "带子上还留着一圈框（圆角或边线没删干净）；")
                + (filled
                    ? "剧照两层是空的格子加一支 UniformToFill 的画刷（按图自己的比例放大到盖住整格、多出来的一截对半裁掉；两边不留底色）；"
                    : "剧照两层没有铺满整格：网格里有别的东西、背景不是画刷、或者拉伸方式不是 UniformToFill；")
                + $"字块宽 页宽1100→{wide:0}；"
                + (corners
                    ? "字块竖向居中、三行字靠左，精选标识、徽标与片名依次排列；"
                    : "字块没有竖向居中，或者精选标识、徽标、片名次序不对，或者有一行字没靠左；")
                + (inked
                    ? $"四行字底下各一层跟着字形走的影子（模糊 {TextInk.SmallBlur:0}，片名那行 {TextInk.TitleBlur:0}）；"
                    : "字底下那层影子的宿主不在字前面（影子会盖在字上）；")
                + (placed
                    ? $"字块离左沿 {InfoInset:0}、上下零边距（竖向居中由对齐管），小横条离下沿 {DotsBaseline:0}"
                    : "字块的左右边距或小横条的下边距不对")
                + "；"
                + $"画面上的暗罩 {scrims.Count} 层"
                + (bare
                    ? $"（一道渐变幕，从 {sized[0].Margin.Left:0} 起、"
                        + $"宽 {sized[0].Width:0} ＝ min(页宽, 字块右沿 {InfoInset:0}+{wide:0}＋{ScrimBreath:0})，"
                        + $"左头九成浓往图里散尽；顶上给标题栏垫底的一条 {topped[0].Height:0} 高；下、右两条各压一条边、半张之前散尽："
                        + $"{string.Join('、', edges.Select(RimRead))}）"
                    : "（该是四层：顶上一条给标题栏垫底、一道渐变幕、下和右两条只压边缘的 —— 形状或位置对不上）")
                + "；"
                + $"箭头占到 {strip:0}、字块从 {banner.Info.Margin.Left:0} 起"
                + $"{(apart ? "，两边不同列" : "，压到字了")}；"
                + $"两层剧照{(layered ? "轮着上，同一张不重来" : "没换过位置")}；"
                + $"五行错 {Stagger.TotalMilliseconds:0} 毫秒、{Lift.TotalMilliseconds:0} 毫秒抬起 {TextDrop:0} 像素"
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
    /// 左边那道渐变幕的形状：**横向**（浓的那一头在左）、贴沿那头近实心（#E6、九成 —— 字块站的那一头压得够黑，
    /// 图还透得出来），往图里走到全透明。它左头在带子的边上、右头过图中线也没到半张带，所以不跟下、右两条走
    /// <see cref="Rims"/> 那句「半张之前就散尽」—— 这一条管的正是「给那块字垫底」。
    /// </summary>
    private static bool Melts(Border layer) =>
        layer.Background is LinearGradientBrush { StartPoint.X: 0, EndPoint.X: 1 }
            && Ramp(layer) is [{ } first, .., { } last]
            && first.Color.A >= 0xD0
            && last.Color.A == 0;

    /// <summary>
    /// 顶边那条暗罩（<c>Top</c>，「让首页轮播图占满窗口的上半部分（包括窗口标题）」，2026-09-11）的形状：
    /// **竖向**（浓的那一头在窗口顶边）、贴沿那头够浓、往下一路散尽。它压的是标题栏那一整段，所以要伸到 120
    /// 像素那么深 —— 比 <see cref="Rims"/> 那两句「半张之前散尽」深得多，判法因此单独一条：只要求浓头在
    /// StartPoint 那一侧、有一处到 0，**不限制 0 出现在哪儿**。
    ///
    /// 不把它塞进 Rims：那一条管的是下、右两条「半张之前散尽、之后一点不压」的收边层，顶上这条是另一种东西
    /// （给浅墨垫底用的压顶），硬套只会让两种形状互相迁就。
    /// </summary>
    private static bool Caps(Border layer) =>
        layer.Background is LinearGradientBrush { StartPoint.Y: 0, EndPoint.Y: 1 }
            && Ramp(layer) is [{ } first, ..]
            && first.Color.A > 0x40
            && Ramp(layer).Any(stop => stop.Color.A == 0);

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
    /// 自检用：这条带在真页面上的读数。<see cref="Probe"/> 拿假图证规则，这一句证的是规则之外的事 ——
    /// 带在 <c>ScrollView</c> 的竖排里真量到了宽度、那个宽度真变成了高度、台上那张的剧照真解码进了前面那层。
    /// 少一样，屏上就是一条空带，而 <see cref="HomeViewModel.BannerSummary"/> 报的张数照旧好看。
    /// <para>
    /// 字块竖向居中之后「它有没有超出带子」只有屏上量得出来：报字块的高和「带高 − 字块高」上下各摊的一半。
    /// 这一位变成负数，就是简介和按键那一头伸出带的下沿、压到底边那排小横条上（下限 240 那一档正是这样，
    /// 所以它继续报）。
    /// </para>
    /// <para>
    /// 带自己的形状也报作诊断：剧照铺满整条带，图按自己的比例放大到盖住整格、多出来的一截对半裁掉 —— 这一句把
    /// 「原图的比例进这条带要裁掉多少」算出来（16:9 进 2.96:1 是上下各两成，屏上那一版就是他说的没有黑边了）。
    /// 渐变幕（<c>Fade</c>）的 Width 是代码摆的，连着它到没到字块的尾巴一起说。屏上是不是真这样，由
    /// <see cref="PictureRead"/> 在真实页面上量。
    /// </para>
    /// </summary>
    internal string State
    {
        get
        {
            if (_slides.Count == 0) return "收起";

            var height = double.IsNaN(Root.Height) ? 0 : Root.Height;
            var viewport = _viewport?.Size.Height ?? 0;
            var fade = double.IsNaN(Fade.Width) ? 0 : Fade.Width;
            var spare = (height - Info.ActualHeight) / 2;
            var bandShape = Band.ActualHeight > 0 ? Band.ActualWidth / Band.ActualHeight : 0;

            return $"带高 {height:0}、剧照左侧采色延长 10% 后铺满整条带{Describe(FrontPicture as BitmapImage, bandShape)}"
                + $"（渐变幕 {fade:0} 宽）、字块宽 {Info.MaxWidth:0}、"
                + $"字块高 {Info.ActualHeight:0} 竖向居中（上下各 {spare:0}）、"
                + $"带 {bandShape:0.00}:1"
                + $"（窗口高 {viewport:0}）、"
                + $"{HomeCarousel.Position(_index, _slides.Count)}、"
                + $"剧照{(FrontPicture is null ? "还在取" : "已上图")}、"
                + $"徽标{(LogoImage.Source is null ? "无" : "有")}";
        }
    }

    /// <summary>
    /// 「这张图铺进这条带要裁掉多少」的一句话读数，给 <see cref="State"/> 用。图还没到、或者它自己的尺寸问不到
    /// （不是 <c>BitmapImage</c>）就不说 —— 那是网络和类型的事，不是版面的事。
    /// </summary>
    private static string Describe(BitmapImage? source, double bandShape)
    {
        if (source is not { PixelWidth: > 0, PixelHeight: > 0 } || bandShape <= 0) return "";

        var shape = (double)source.PixelWidth / source.PixelHeight;
        var (cropX, cropY) = Crop(shape, bandShape);

        return $"（背景位图 {source.PixelWidth}×{source.PixelHeight} = {shape:0.00}:1，"
            + (cropX > 0 ? $"左右各裁 {cropX:P0}" : $"上下各裁 {cropY:P0}")
            + "，比例不变）";
    }

    /// <summary>
    /// 图铺满整格时被裁掉多少，按两条轴各报一个比例（0 就是这一轴一个像素都没裁）。
    /// <para>
    /// <c>UniformToFill</c> 不给外面看它裁在哪儿，而能算的原因是两边的比例都问得到：把它放大到刚好盖住整格，
    /// 富余的那一轴就是对半裁掉的那一轴。带子 2.96:1 比 16:9 的剧照宽，所以裁的是上下；哪天真来一张比带子还宽
    /// 的图（5:1 的横幅），被裁的就该换成左右。
    /// </para>
    /// </summary>
    private static (double X, double Y) Crop(double sourceShape, double bandShape) =>
        sourceShape <= 0 || bandShape <= 0 ? (0, 0)
            : sourceShape > bandShape ? (1 - bandShape / sourceShape, 0)
            : (0, 1 - sourceShape / bandShape);

    /// <summary>
    /// 自检：剧照真铺满了整条带 —— 「去掉首页封面轮播图的黑边」（2026-09-11）。
    /// <para>
    /// 量的是台上那一层**空 Grid** 的尺寸和它在带子里的位置：那两层由对齐撑满整格，图在背景那支
    /// <c>ImageBrush Stretch="UniformToFill"</c> 里按自己的比例放大到盖住整格、多出来的一截对半裁掉。格子比带子
    /// 小、或者站偏了，屏上就是一侧露出底色 —— 那正是他要拿掉的那两条黑边，而在截图里它长得像「照片没占满」，
    /// 不像一个错。**这一读之所以量得到，是因为图在画刷里而不是在 <c>Image</c> 控件里**：后者的
    /// <c>ActualWidth/Height</c> 报的是画出来那张图的大小（2026-09-11 第一趟就是它报的 1014×570，而带子
    /// 1014×341），照它判会把「图铺到带子外面去了」当成「铺满了」。
    /// </para>
    /// <para>
    /// 裁掉多少是**算出来的，不是量出来的**：整格铺满之后尺寸就是整格尺寸，图裁在哪儿从元素上读不到，但这件
    /// 事本来就只由两个比例决定（带子的、原图的），所以这一读把它们算出来报给人看 —— 「16:9 的剧照进 2.96:1 的
    /// 带，上下各裁两成」。判的是**只有一条轴被裁**：两条都裁说明这个拉伸方式被换掉了（<c>Fill</c> 会同时裁两轴
    /// 还把图拉变形），一条都不裁只可能是带子和图正好同形。
    /// </para>
    /// <para>
    /// 图还没解码回来时没有得量，那一档只报不判 —— 那是网络的事，不是版面的事。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) PictureRead()
    {
        var layer = FrontLayer;
        var band = (Width: Band.ActualWidth, Height: Band.ActualHeight);
        var drawn = (Width: layer.ActualWidth, Height: layer.ActualHeight);
        var picture = FrontPicture;

        if (picture is null || drawn.Width <= 1 || drawn.Height <= 1)
            return (true, $"剧照还没解出来，带 {band.Width:0}×{band.Height:0}");

        var at = layer.TransformToVisual(Band).TransformPoint(new Windows.Foundation.Point(0, 0));
        var left = at.X;
        var scale = layer.RenderTransform as ScaleTransform;
        var paintedWidth = drawn.Width * (scale?.ScaleX ?? 1);
        var paintedHeight = drawn.Height * (scale?.ScaleY ?? 1);
        var right = band.Width - (at.X + paintedWidth);

        // 布局格子仍然占满画框；慢镜头允许图超出框，但任何时刻都不能露出底色。
        var covers = Math.Abs(drawn.Width - band.Width) <= 1.5
            && Math.Abs(drawn.Height - band.Height) <= 1.5
            && left <= 1.5 && right <= 1.5
            && at.Y <= 1.5 && at.Y + paintedHeight >= band.Height - 1.5;

        var source = picture as BitmapImage;
        var sourceShape = source is { PixelWidth: > 0, PixelHeight: > 0 }
            ? (double)source.PixelWidth / source.PixelHeight
            : 0;

        var bandShape = band.Height > 0 ? band.Width / band.Height : 0;
        var (cropX, cropY) = Crop(sourceShape, bandShape);

        var oneAxis = sourceShape > 0 && (cropX <= 0.001 || cropY <= 0.001);

        return (covers && oneAxis,
            $"剧照铺满整条带 {drawn.Width:0}×{drawn.Height:0}（画刷那层就是整格，左 {left:0.#}、上 {at.Y:0.#}）"
                + (sourceShape > 0
                    ? $"；背景位图 {source!.PixelWidth}×{source!.PixelHeight} = {sourceShape:0.00}:1（已含左侧 10% 采色延长）"
                        + (cropX > 0
                            ? $" 比带子宽，左右各裁 {cropX:P0}"
                            : cropY > 0 ? $" 比带子窄，上下各裁 {cropY:P0}" : " 和带子同形，一个像素都不裁")
                        + "（比例不变，多出来的一截对半裁）"
                    : "；问不到原图尺寸")
                + $"；带 {band.Width:0}×{band.Height:0} = {bandShape:0.00}:1（左右余量 {left:0.#}/{right:0.#}，不大于 0 表示铺满）"
                + (covers ? "" : " —— 有一边没铺满，屏上就是一条底色边")
                + (oneAxis ? "" : "；裁的不止一轴或没裁，拉伸方式可能被换掉了")
                + $"；字块最宽 {Info.MaxWidth:0}");
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

    /// <summary>自检用：一层剧照那支背景画刷的拉伸方式。标记里写死的，所以取不到就抛。</summary>
    private static Stretch BrushStretch(Panel layer) => Brush(layer).Stretch;
}
