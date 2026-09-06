using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 页头那块牌子：标题、底下一行说明、再底下一条通栏发丝线。见 PageSlate.xaml 里那段说明。
/// </summary>
/// <remarks>
/// 六个属性全是依赖属性，因为页面都用 <c>x:Bind</c> 把视图模型的标题和条目数接上来 —— 普通 CLR 属性
/// 在 OneWay 绑定下只会被写一次。空字符串一律收起那一行而不是留一行空白：不是每一页都有读数，页头的
/// 高度不该因为「有没有」而差出一行。
/// </remarks>
public sealed partial class PageSlate : UserControl
{
    public static readonly DependencyProperty EyebrowProperty = DependencyProperty.Register(
        nameof(Eyebrow),
        typeof(string),
        typeof(PageSlate),
        new PropertyMetadata(string.Empty, OnEyebrowChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(PageSlate),
        new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note),
        typeof(string),
        typeof(PageSlate),
        new PropertyMetadata(string.Empty, OnNoteChanged));

    public static readonly DependencyProperty TrailingProperty = DependencyProperty.Register(
        nameof(Trailing),
        typeof(object),
        typeof(PageSlate),
        new PropertyMetadata(null, OnTrailingChanged));

    public static readonly DependencyProperty TrailingBelowProperty = DependencyProperty.Register(
        nameof(TrailingBelow),
        typeof(bool),
        typeof(PageSlate),
        new PropertyMetadata(false, OnTrailingBelowChanged));

    public static readonly DependencyProperty OnScrimProperty = DependencyProperty.Register(
        nameof(OnScrim),
        typeof(bool),
        typeof(PageSlate),
        new PropertyMetadata(false, OnScrimChanged));

    public PageSlate() => InitializeComponent();

    /// <summary>这一区的代号，拉丁大写：HOME、LIBRARY、SETTINGS。和 <see cref="Note"/> 合成说明那一行。</summary>
    public string Eyebrow
    {
        get => (string)GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
    }

    /// <summary>这一页具体是什么 —— 库名、页名、剧名。</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>那个读数：条目数、服务器名。和 <see cref="Eyebrow"/> 合成标题底下那一行说明。</summary>
    public string Note
    {
        get => (string)GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
    }

    /// <summary>标题右边那一格，放页面自己的工具条。</summary>
    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    /// <summary>
    /// 打开后把 <see cref="Trailing"/> 那一格从标题右边搬到通栏线下面靠右排。窗口窄到标题和工具条挤在
    /// 一行放不下时用，由页面自己的 <c>VisualState</c> 来开。
    /// </summary>
    public bool TrailingBelow
    {
        get => (bool)GetValue(TrailingBelowProperty);
        set => SetValue(TrailingBelowProperty, value);
    }

    /// <summary>
    /// 打开后整块牌子换成压在剧照上的那一份：三行字取 <c>EgOnScrim</c> 那几支画刷，通栏线收起。主页的大图
    /// 铺满顶上那一整块之后，页眉就浮在图上（见 HomePage.xaml），而跟主题走的墨色在亮剧照上会看不见。
    /// </summary>
    public bool OnScrim
    {
        get => (bool)GetValue(OnScrimProperty);
        set => SetValue(OnScrimProperty, value);
    }

    private static void OnEyebrowChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((PageSlate)sender).SyncNote();

    private static void OnTitleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var slate = (PageSlate)sender;
        SetLine(slate.TitleText, args.NewValue);
    }

    private static void OnNoteChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((PageSlate)sender).SyncNote();

    /// <summary>
    /// 说明那一行：代号和读数合成一句，中间一个「·」，两个都空就整行收起（页头的高度不该因为「有没有读数」
    /// 而差出一行）。两个属性各自会变，所以两个回调都落到这里 —— 合成的规则只有一处。
    /// <para>
    /// 分隔符和这套界面别处那个「·」是同一个（详情页那行年份 · 时长 · 分辨率），所以两处读起来是同一种句子。
    /// </para>
    /// </summary>
    private void SyncNote()
    {
        var parts = new[] { Eyebrow, Note }.Where(part => !string.IsNullOrWhiteSpace(part));

        SetLine(NoteText, string.Join(" · ", parts));
    }

    private static void OnTrailingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var slate = (PageSlate)sender;

        slate.TrailingHost.Content = args.NewValue;
        slate.TrailingHost.Visibility = args.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnTrailingBelowChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var slate = (PageSlate)sender;
        var below = (bool)args.NewValue;
        var host = slate.TrailingHost;

        // 同一个 ContentPresenter 换格子，不是两份拷贝：工具条的键盘顺序和飞出菜单都只有一套。
        // 标题现在是第 0 行（2026-09-06 按 Gallery 那套重排），所以「跟着标题」那一档是 0 而不是 1。
        Grid.SetRow(host, below ? 3 : 0);
        Grid.SetColumn(host, below ? 0 : 1);
        Grid.SetColumnSpan(host, below ? 2 : 1);
        host.HorizontalAlignment = below ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        host.Margin = below ? new Thickness(0, 10, 0, 0) : new Thickness(12, 0, 0, 0);
    }

    /// <summary>
    /// 换的是两个 <c>Style</c>，不是两支 <c>Foreground</c>：字体字号一档不动，只有颜色跟着底走，所以图上
    /// 那块牌子和页面里那块是同一块牌子。样式键是顶层键（不在 <c>ThemeDictionaries</c> 里），所以按键取得到。
    /// </summary>
    private static void OnScrimChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var slate = (PageSlate)sender;
        var scrim = (bool)args.NewValue;

        Dress(slate.NoteText, scrim ? "EgOnScrimDataStyle" : "EgDataStyle");
        Dress(slate.TitleText, scrim ? "EgOnScrimPageTitleStyle" : "EgPageTitleStyle");
        slate.Rule.Visibility = scrim ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>按键换一行字的样式。取不到就不动 —— 少一个键该由自检报出来，而不是让这行字掉回默认字。</summary>
    private static void Dress(TextBlock block, string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Style style)
            block.Style = style;
    }

    /// <summary>
    /// 有字就写上并显示，没字就整行收起。三行都走这里，所以「空的那一行不占地方」这条规则只有一处实现。
    /// </summary>
    private static void SetLine(TextBlock block, object? value)
    {
        var text = value as string ?? string.Empty;

        block.Text = text;
        block.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
