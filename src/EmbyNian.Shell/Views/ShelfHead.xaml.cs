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
