using EmbyNian.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 卡片带的标题、数量胶囊和尾部操作，配合左侧短强调线建立清晰的阅读层级。
/// </summary>
/// <remarks>
/// 三个属性都是依赖属性：卡片带的标题和条目数是页面用 <c>x:Bind</c>
/// 接上来的，普通 CLR 属性在 OneWay 绑定下只会被写一次。空字符串收起那一格而不是留出空白 —— 不是每一带
/// 都报得出条目数（详情页的 单集 那一带把季数集数写在标题里了）。
/// </remarks>
public sealed partial class ShelfHead : UserControl
{
    private const string Category = "ui";

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(ShelfHead),
        new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note),
        typeof(string),
        typeof(ShelfHead),
        new PropertyMetadata(string.Empty, OnNoteChanged));

    public static readonly DependencyProperty TrailingProperty = DependencyProperty.Register(
        nameof(Trailing),
        typeof(object),
        typeof(ShelfHead),
        new PropertyMetadata(null, OnTrailingChanged));

    /// <summary>
    /// 这块牌子压在一张剧照上，而不是站在页面那张纸上 —— 集页把同季那一带摆在头图底下的画面里
    /// （见 <c>DetailPage.PlaceEpisodes</c>）。标题、读数和那条通栏线于是都换成压在图上那一套不随主题走的
    /// 浅墨：主题自己的墨在晴昼（唯一那套浅色）下是近黑色，压在那层黑罩子上读不出来；而那条线用的
    /// <c>EgBorderBrush</c> 在图上本来就是一条看不见的线。
    /// </summary>
    public static readonly DependencyProperty OnScrimProperty = DependencyProperty.Register(
        nameof(OnScrim),
        typeof(bool),
        typeof(ShelfHead),
        new PropertyMetadata(false, OnScrimChanged));

    /// <summary>
    /// 这一带可以进去（首页媒体库那一排），标题后面跟一个大于号、整块牌子变成按钮。
    /// 2026-09-13：用户原话「在最近添加右边添加一个大于号，点击标题后可以进入对应媒体库」。
    /// </summary>
    public static readonly DependencyProperty OfferProperty = DependencyProperty.Register(
        nameof(Offer),
        typeof(bool),
        typeof(ShelfHead),
        new PropertyMetadata(false, OnOfferChanged));

    /// <summary>
    /// 下划线的看门人（<see cref="HoverWatch"/>），看的是 <see cref="Scope"/>。事件是快路，十分之一秒一次的
    /// 轮询兜底 —— 指针从牌子上直接出窗时，Exited 报的位置是旧的，没有这道网下划线就永远亮着
    /// （「鼠标移出窗口后不会自动恢复」在同一块牌子上重演一遍）。
    /// </summary>
    private readonly HoverWatch _hover;

    /// <summary>指针此刻在不在这块牌子上，<see cref="_hover"/> 的裁决落在这里，<see cref="PaintUnderline"/> 读它。</summary>
    private bool _hovered;

    public ShelfHead()
    {
        InitializeComponent();

        // 挂在 Scope 而不是标题上的原因：点击面铺开（Offer=true）时它在最上层，指针事件整个被它收走，
        // 标题上一个也见不着；而 Scope 可见 ⇔ 这块牌子可以进去 ⇔ 正是该有下划线的场合，详情页那些
        // 进不去的牌子（Scope 收着）自然一条悬停事件都没有 —— 门在哪儿，下划线就在哪儿。
        _hover = new HoverWatch(Scope, up =>
        {
            _hovered = up;
            PaintUnderline();
        });
    }

    /// <summary>挂件那一格的 Visibility 监听票据，见 <see cref="OnTrailingChanged"/>。</summary>
    private long _ticket;

    /// <summary>
    /// 牌子被点了一下。只在 <see cref="Offer"/> 为真时发得出来 —— 详情页那些牌子不接这个，
    /// 也就没有「点了没反应」的死路。
    /// </summary>
    public event EventHandler? Invoked;

    /// <summary>这一带是什么：继续观看、最近添加、演职人员、单集。</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>右端那个读数，一般是「24 项」。</summary>
    public string Note
    {
        get => (string)GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
    }

    /// <summary>读数右边那一格，给这一带自己的控件 —— 详情页 单集 那一带的季下拉框就在这儿。</summary>
    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    /// <inheritdoc cref="OnScrimProperty"/>
    public bool OnScrim
    {
        get => (bool)GetValue(OnScrimProperty);
        set => SetValue(OnScrimProperty, value);
    }

    /// <inheritdoc cref="OfferProperty"/>
    public bool Offer
    {
        get => (bool)GetValue(OfferProperty);
        set => SetValue(OfferProperty, value);
    }

    /// <summary>
    /// 门面上两件东西的现况（大于号、点击面的可见性），给自检「屏上的牌子」那一关用：真树上的牌子从页面上
    /// 量，而这两个 x:Name 的字段是私有的，外人摸不到 —— 走这扇小窗（<see cref="HomePage.HeadOnScreen"/>）。
    /// </summary>
    internal (Visibility Glyph, Visibility Scope) DoorState => (OpenGlyph.Visibility, Scope.Visibility);

    /// <summary>
    /// 让牌子可点：大于号露出来、透明点击面铺开。
    /// </summary>
    /// <remarks>
    /// 点击面是 <c>Root</c> 里的一个兄弟节点，排在标题、读数、大于号**后面**（XAML 里写在最后，画在最上层），
    /// 只认「有没有那块地方」、不要内容。**不能把 <c>Root</c> 塞进它的 Content** —— <c>Scope</c> 自己就在
    /// <c>Root</c> 里，那样成一个环：Root → Scope → ContentPresenter → Root，XAML 要么当场抛，要么画出一棵
    /// 自己套自己的树。压在文字上面也不会挡住什么：这一带里没有要靠悬停或拖动活着的东西。
    /// </remarks>
    private static void OnOfferChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var head = (ShelfHead)sender;
        var offered = (bool)args.NewValue;

        // 开门才记一笔（DP 默认 false、绑定写 false 不触发回调，真变 true 每排最多一次）：谁再报「点了没
        // 反应」，日志先说出这扇门开没开 —— 2026-09-14 排查「继续观看」时缺的就是这一行。
        if (offered) Log.Info(Category, $"牌子开门：{head.Title}");

        head.OpenGlyph.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
        head.Scope.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;

        // 门关上的那一刻指针可能还停在牌子上（比如主页某排的东西刚取回来、牌子从不可进变成可进的反向）：
        // 把下划线一起按下去，别留一条悬在空中的。
        head.PaintUnderline();
    }

    private void OnScopeClicked(object sender, RoutedEventArgs e) => Invoked?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 下划线亮不亮：指针在牌子上、且这块牌子进得去，才亮。显隐走 <c>Opacity</c> 而不是 Visibility ——
    /// Collapsed 会在悬停的一瞬间把整条牌子撑高又缩回去，通栏线跟着跳；Opacity 0 照旧占位，出现了也不动布局。
    /// </summary>
    private void PaintUnderline() => Underline.Opacity = _hovered && Offer ? 1 : 0;

    private void OnScopePointerEntered(object sender, PointerRoutedEventArgs e) => _hover.Enter();

    /// <summary>
    /// 气泡与点击捕获会送来「人还在原地」的退出事件 —— 指针从 Scope 内部的子元素之间挪动、点下那一下的
    /// 捕获松开，都会在 Scope 上喊一声退出（<c>PosterCard.OnPointerExited</c> 同款守卫，同一批坑）：出了
    /// 点击面的范围才认。真出了窗、位置是旧的那种，<see cref="_hover"/> 的轮询兜底会收。
    /// </summary>
    private void OnScopePointerExited(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Scope).Position;

        if (point.X >= 0 && point.Y >= 0 && point.X < Scope.ActualWidth && point.Y < Scope.ActualHeight) return;

        _hover.Leave();
    }

    /// <summary>
    /// 换那一套墨。标题和读数换样式（两个键都 <c>BasedOn</c> 主题那一套、只改前景，所以字体字号不变），
    /// 那条线改成同一支浅墨压到三成半 —— 和头图尾部那条进度轨同一个写法，理由也一样。
    /// </summary>
    private static void OnScrimChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var head = (ShelfHead)sender;
        var scrim = (bool)args.NewValue;

        head.TitleText.Style = Resource<Style>(scrim ? "EgOnScrimSectionTitleStyle" : "EgSectionTitleStyle");
        head.NoteText.Style = Resource<Style>(scrim ? "EgOnScrimDataStyle" : "EgDataStyle");
        head.OpenGlyph.Foreground = Resource<Microsoft.UI.Xaml.Media.Brush>(
            scrim ? "EgOnScrimDimBrush" : "EgTextDimBrush");

        if (scrim)
        {
            head.Rule.Background = Resource<Microsoft.UI.Xaml.Media.Brush>("EgOnScrimDimBrush");
            head.Rule.Opacity = 0.35;
            head.NoteCapsule.Background = Resource<Microsoft.UI.Xaml.Media.Brush>("EgScrimBrush");
            return;
        }

        // Border 自己那支，不是这个 UserControl 继承来的 Control.Background —— 两个是不同的依赖属性，
        // 清错一个的症状是那条线在回到纸面之后一直是浅墨。
        head.Rule.ClearValue(Border.BackgroundProperty);
        head.Rule.Opacity = 0.45;
        head.NoteCapsule.ClearValue(Border.BackgroundProperty);
    }

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];

    private static void OnTitleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        SetLine(((ShelfHead)sender).TitleText, args.NewValue);

    private static void OnNoteChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var head = (ShelfHead)sender;
        SetLine(head.NoteText, args.NewValue);
        head.NoteCapsule.Visibility = head.NoteText.Visibility;
    }

    private static void OnTrailingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var head = (ShelfHead)sender;

        if (args.OldValue is UIElement gone) gone.UnregisterPropertyChangedCallback(VisibilityProperty, head._ticket);

        head.TrailingHost.Content = args.NewValue;

        // 挂件自己收起来的时候这一格也得收：详情页只有一季时那个季下拉框是收起的，而收起的元素尺寸是 0、
        // 外面这一格却照旧占着自己那 12 的外边距，读数于是离右沿 12 像素，和别的牌子对不上。所以看的是挂件
        // 报出来的 Visibility，不是它算出来的大小。
        if (args.NewValue is UIElement now)
            head._ticket = now.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => head.ShowTrailing());

        head.ShowTrailing();
    }

    /// <summary>有挂件、且挂件自己没收起来，才留出那一格。</summary>
    private void ShowTrailing()
    {
        var show = TrailingHost.Content switch
        {
            null => false,
            UIElement element => element.Visibility == Visibility.Visible,
            _ => true,
        };

        TrailingHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>有字就写上并显示，没字就收起。</summary>
    private static void SetLine(TextBlock block, object? value)
    {
        var text = value as string ?? string.Empty;

        block.Text = text;
        block.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 自检：这块牌子那扇门开不开得出来。和数据无关（不碰服务器），所以和有服务器那一支无关。
    /// </summary>
    /// <remarks>
    /// 2026-09-13 加的那一档。量的是「默认收着、<c>Offer</c> 一开大于号和点击面都出来，点一下
    /// <c>Invoked</c> 发得出来」—— 点进去落到哪一页是 <c>HomeViewModel.OpenShelf</c> 的事，那一头的去处由
    /// <c>HomeLayout.TargetOf</c> 给、再经 <c>IShellActions</c> 出去，自检没有服务器、连一排都没有，量不到。
    /// <para>
    /// 2026-09-14 之后 <c>Offer</c> 绑的还是同一个 <c>CardShelf.CanOpen</c>，只是它的判据从「这一排有库 id」
    /// 换成了「这一排有去处」（见 <c>CardShelf.Target</c>），这块牌子一行没改。
    /// </para>
    /// <para>
    /// 这里不套 <c>ApplyTemplate</c>：控制模板里的 <c>Button</c> 要等到上场才会套出来，而这把牌子是在自检的
    /// 树上现造的、没进过任何一棵真树，模板永远不会套。所以直接叫那个处理函数 —— 它做的事就是把
    /// <c>Invoked</c> 转手发出去，绕开按钮那层命中测试正好，本来就只是要问「这条线路通不通」。
    /// </para>
    /// <para>
    /// <b>量的里面没有 <c>Scope.Content</c></b>：点击面本来就该是空的，它的内容曾经是这块牌子的整个根
    /// （那就成了环），见 <see cref="OnOfferChanged"/>。
    /// </para>
    /// </remarks>
    internal static (bool Ok, string Detail) Probe()
    {
        var head = new ShelfHead { Title = "最近添加 · 电影", Note = "12 项" };
        var plainGlyph = head.OpenGlyph.Visibility;
        var plainScope = head.Scope.Visibility;
        var plainUnderline = head.Underline.Opacity;

        // 下划线的门控三态（2026-09-14「移到标题上出现下划线」）。不走 HoverWatch.Enter —— 那要真树和
        // XamlRoot，自检这块牌子没进过任何一棵树；直接摆指针在不在那个字段（同类私有，合法），量的
        // 是 PaintUnderline 自己的判据：指针在、门没开，不亮；指针在、门开了，才亮。
        head._hovered = true;
        head.PaintUnderline();
        var heldShut = head.Underline.Opacity;

        var fired = 0;
        head.Invoked += (_, _) => fired++;

        head.Offer = true;
        head.RaiseForProbe();
        var heldOpen = head.Underline.Opacity;

        var ok = plainGlyph == Visibility.Collapsed
            && plainScope == Visibility.Collapsed
            && head.OpenGlyph.Visibility == Visibility.Visible
            && head.Scope.Visibility == Visibility.Visible
            && head.Scope.Content is null
            && fired == 1
            && plainUnderline == 0
            && heldShut == 0
            && heldOpen == 1;

        return (ok, $"默认收起 {plainGlyph}/{plainScope}，可入时 {head.OpenGlyph.Visibility}/" +
            $"{head.Scope.Visibility}，衬底 {(head.Scope.Content is null ? "空" : "非空")}，点了 {fired} 次；" +
            $"下划线 默 {plainUnderline}、指针在门关 {heldShut}、指针在门开 {heldOpen}");
    }

    /// <summary>自检用：把「牌子被点了一下」这条线路走一遍，见 <see cref="Probe"/>。</summary>
    private void RaiseForProbe() => OnScopeClicked(Scope, new RoutedEventArgs());
}
