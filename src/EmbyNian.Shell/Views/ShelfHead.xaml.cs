using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 卡片带上面那块牌子：标题、读数、一个挂件位，底下一条通栏发丝线。见 ShelfHead.xaml 里那段说明。
/// </summary>
/// <remarks>
/// 三个属性都是依赖属性，理由和 <see cref="PageSlate"/> 一样：卡片带的标题和条目数是页面用 <c>x:Bind</c>
/// 接上来的，普通 CLR 属性在 OneWay 绑定下只会被写一次。空字符串收起那一格而不是留出空白 —— 不是每一带
/// 都报得出条目数（详情页的 单集 那一带把季数集数写在标题里了）。
/// </remarks>
public sealed partial class ShelfHead : UserControl
{
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

    public ShelfHead() => InitializeComponent();

    /// <summary>挂件那一格的 Visibility 监听票据，见 <see cref="OnTrailingChanged"/>。</summary>
    private long _ticket;

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

        if (scrim)
        {
            head.Rule.Background = Resource<Microsoft.UI.Xaml.Media.Brush>("EgOnScrimDimBrush");
            head.Rule.Opacity = 0.35;
            return;
        }

        // Border 自己那支，不是这个 UserControl 继承来的 Control.Background —— 两个是不同的依赖属性，
        // 清错一个的症状是那条线在回到纸面之后一直是浅墨。
        head.Rule.ClearValue(Border.BackgroundProperty);
        head.Rule.ClearValue(OpacityProperty);
    }

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];

    private static void OnTitleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        SetLine(((ShelfHead)sender).TitleText, args.NewValue);

    private static void OnNoteChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        SetLine(((ShelfHead)sender).NoteText, args.NewValue);

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

    /// <summary>有字就写上并显示，没字就收起 —— 同 <see cref="PageSlate"/>。</summary>
    private static void SetLine(TextBlock block, object? value)
    {
        var text = value as string ?? string.Empty;

        block.Text = text;
        block.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
