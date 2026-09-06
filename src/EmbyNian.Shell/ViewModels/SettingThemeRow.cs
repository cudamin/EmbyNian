using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// 一套主题在设置页上的那一小块：它自己的窗口底色、卡片色和强调色，加上名字和深浅。
/// <para>
/// 五支画刷都是在这里当场做出来的固定颜色，故意不跟当前主题走 —— 这是整个界面里唯一一处「颜色不跟着主题
/// 变」是对的地方。色板存在的意义就是一眼看见每套各是什么颜色；要是把它们接到 <c>EgAccentBrush</c> 那些
/// 角色上，几块会同时变成当前那一套的颜色 —— 几个一模一样的方块，而且看起来还在正常工作。这就是这一行
/// 唯一一种不出声的坏法，所以 <see cref="SettingThemeRow.Measure"/> 专门量它。
/// </para>
/// </summary>
public sealed partial class ThemeSwatch : ObservableObject
{
    private readonly Action<ThemeSwatch> _pick;

    internal ThemeSwatch(UiTheme theme, Action<ThemeSwatch> pick)
    {
        _pick = pick;
        Id = theme.Id;
        Name = theme.Name;
        Note = theme.Note;
        Kind = theme.IsDark ? "深色" : "浅色";

        Window = Fixed(theme.Colors.Window);
        Surface = Fixed(theme.Colors.Surface);
        Accent = Fixed(theme.Colors.Accent);
        Ink = Fixed(theme.Colors.Text);
        InkDim = Fixed(theme.Colors.TextDim);
    }

    /// <summary>存进设置文件的那个值。</summary>
    public string Id { get; }

    public string Name { get; }

    /// <summary>那一套自己的说明，挂成这一块的提示条 —— 一块色板放不下一整句话。</summary>
    public string Note { get; }

    public string Kind { get; }

    public SolidColorBrush Window { get; }

    public SolidColorBrush Surface { get; }

    public SolidColorBrush Accent { get; }

    public SolidColorBrush Ink { get; }

    public SolidColorBrush InkDim { get; }

    /// <summary>现在用的是不是这一套。右上角那颗角标只画在这一块上。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckVisibility))]
    public partial bool IsCurrent { get; set; }

    public Visibility CheckVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>自检：这一块画出来的三种颜色，连成一串好比对六块之间有没有重样的。</summary>
    internal string Roles => $"{Window.Color}/{Surface.Color}/{Accent.Color}";

    [RelayCommand]
    private void Pick() => _pick(this);

    /// <summary>
    /// 一支只属于这一块的画刷。新做一个对象而不是去取 <c>Palette.xaml</c> 里那些 —— <c>ThemeHost</c> 换主题
    /// 时改的是那些共用对象的颜色，谁引用了它们就会跟着变，而这五支正是不能跟着变的那几支。
    /// </summary>
    private static SolidColorBrush Fixed(ThemeColor color) => new(ThemeHost.ToColor(color));
}

/// <summary>
/// 主题那一行：几块色板，点哪块是哪块。
/// <para>
/// 这里原先是个下拉框。一套主题该让人看见它的颜色，而不是读它的名字 ——「石墨」和「午夜」写在一行里
/// 根本分不出谁是哪个，而并排两块颜色不用读就分出来了。写回设置的那一对读/写函数和别的行一模一样，
/// 换掉的只是屏上那个控件。
/// </para>
/// </summary>
public sealed class SettingThemeRow : SettingRow
{
    private readonly Func<string> _read;
    private readonly Action<string> _write;
    private readonly Action _save;

    internal SettingThemeRow(
        string heading,
        string note,
        IReadOnlyList<UiTheme> themes,
        Func<string> read,
        Action<string> write,
        Action save)
        : base(heading, note)
    {
        _read = read;
        _write = write;
        _save = save;

        Swatches = [.. themes.Select(theme => new ThemeSwatch(theme, Choose))];
        Sync();
    }

    public IReadOnlyList<ThemeSwatch> Swatches { get; }

    private void Choose(ThemeSwatch swatch)
    {
        // 点已经选中的那一块什么也不做：换到当前这一套是个空操作，没有理由为它写一次盘。
        if (swatch.IsCurrent) return;

        _write(swatch.Id);
        _save();
        Sync();
    }

    /// <summary>
    /// 把角标挪到现在这一套上。读的是设置文件里存着的值再解析一次，而不是直接比字符串 —— 存着的可能是
    /// 空的、或者是一套已经不在表里的旧 id，那两种情况下角标该落在真正生效的那一套上。
    /// </summary>
    private void Sync()
    {
        var current = UiThemes.Resolve(_read()).Id;
        foreach (var swatch in Swatches)
            swatch.IsCurrent = string.Equals(swatch.Id, current, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 自检：几块在不在（主题表里加了一套而这里没跟上，屏上是看不出来的）、角标是不是正好一颗且落在存着
    /// 的那一套上，以及这几块画的是不是几种不同的配色（见 <see cref="ThemeSwatch"/>）。
    /// </summary>
    internal (bool Ok, string Detail) Measure()
    {
        var picked = Swatches.Where(swatch => swatch.IsCurrent).ToArray();
        var stored = UiThemes.Resolve(_read());
        var distinct = Swatches.Select(swatch => swatch.Roles).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        var ok = Swatches.Count == UiThemes.All.Count
            && picked is [{ } one]
            && string.Equals(one.Id, stored.Id, StringComparison.OrdinalIgnoreCase)
            && distinct == Swatches.Count;

        return (ok, $"{Swatches.Count} 块（主题表 {UiThemes.All.Count} 套）、{distinct} 种配色，"
            + $"角标在 {(picked is [{ } marked] ? marked.Name : $"{picked.Length} 块上")}"
            + $"，设置里存的是 {stored.Name}（{stored.Id}）");
    }
}
