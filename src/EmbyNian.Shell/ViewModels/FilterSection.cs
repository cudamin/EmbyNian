using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Shell.Diagnostics;
using Microsoft.UI.Xaml;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// One tickable box in the filter panel — an on/off option out of <see cref="EmbyFilterBy.All"/>, or one
/// genre, tag or year the server reported.
/// <para>
/// One type for both, because the two differ only in what a tick writes: the option's id goes into
/// <see cref="ItemFilters.Toggles"/>, a value into one of the three lists. That difference is a pair of
/// delegates handed over at construction, which is the shape <see cref="SettingRow"/> already uses and
/// for the same reason — the access stays compiled and checked, so a renamed member is a build error
/// rather than a box that silently stops filtering.
/// </para>
/// </summary>
public sealed partial class FilterCheck : ObservableObject
{
    private readonly Func<bool> _read;
    private readonly Action<bool> _write;
    private readonly bool _seeded;
    private bool _reseeding;

    internal FilterCheck(string label, string? query, Func<bool> read, Action<bool> write)
    {
        Label = label;
        Query = query;
        _read = read;
        _write = write;

        IsChecked = read();

        // Set last, so seeding the box above did not count as ticking it. A flag per box rather than one
        // 「syncing」 flag over the whole panel, which is the choice SettingChoiceRow explains: the
        // panel-wide version also had to be reasoned about globally, and was left on across a rebuild
        // that awaited anything.
        _seeded = true;
    }

    /// <summary>What the box says: the option's Chinese label, or the genre/tag/year itself.</summary>
    public string Label { get; }

    /// <summary>
    /// The query this box contributes, as <c>Key=Value</c>, for the tooltip — the one place the panel
    /// admits what it is really sending, which is worth having when a filter comes back empty. Null for a
    /// genre, tag or year, where the label is already the value and a tooltip would only repeat it.
    /// </summary>
    public string? Query { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>
    /// Puts the box back in step with the model without writing to it — for a box some other box turned
    /// off. 已播放 and 未播放 together ask for items that are both, so ticking one drops the other in the
    /// model, and this is how the drop reaches the screen. Same idea as
    /// <see cref="SettingNumberRow.Reseed"/>.
    /// </summary>
    internal void Reseed() => Reseed(_read());

    private void Reseed(bool value)
    {
        _reseeding = true;
        try { IsChecked = value; }
        finally { _reseeding = false; }
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_seeded || _reseeding) return;
        _write(value);
    }
}

/// <summary>
/// One block of the filter panel. Either a heading with a grid of on/off boxes under it, or an
/// <see cref="Expander"/> whose choices are read off the server the first time it is opened.
/// <para>
/// The blocks are data and the shapes are markup, which is the arrangement the settings page already
/// uses: <see cref="Views.FilterSectionTemplates"/> picks the shape for each. Before this the panel built
/// every <c>CheckBox</c>, <c>Grid</c> and <c>Expander</c> in code and pushed them into a named
/// <c>StackPanel</c>, so the layout could only be looked at on screen and the tick handlers had to be
/// attached and detached by hand.
/// </para>
/// </summary>
public abstract class FilterSection : ObservableObject
{
    /// <summary>
    /// The narrowest a box may be before the row wraps. With <c>ItemsStretch="Fill"</c> on the layout the
    /// boxes then share the row equally, so this decides the column count without naming it: 150 leaves
    /// room for two across the 360-wide panel and not three.
    /// </summary>
    protected const double TwoUp = 150;

    /// <summary>Years are four characters and fit three across; a genre or a tag can be a sentence.</summary>
    protected const double ThreeUp = 95;

    protected FilterSection(string label, double columnWidth)
    {
        Label = label;
        ColumnWidth = columnWidth;
    }

    /// <summary>The group name, or the list's name — 状态, 画质, 类型, 年份.</summary>
    public string Label { get; }

    /// <inheritdoc cref="TwoUp"/>
    public double ColumnWidth { get; }

    /// <summary>What this block is offering, for the self-check to report on.</summary>
    internal abstract IEnumerable<string> Labels { get; }
}

/// <summary>
/// A heading and the on/off boxes under it: one of <see cref="EmbyFilterBy.Groups"/>, holding whichever
/// of its options this grid can actually answer.
/// </summary>
public sealed class FilterToggleGroup : FilterSection
{
    internal FilterToggleGroup(string heading, IReadOnlyList<FilterCheck> toggles)
        : base(heading, TwoUp) => Toggles = toggles;

    public IReadOnlyList<FilterCheck> Toggles { get; }

    internal override IEnumerable<string> Labels => Toggles.Select(toggle => toggle.Label);
}

/// <summary>
/// One of 类型/标签/年份: an <see cref="Expander"/> whose contents are not in any catalogue, because they
/// are whatever the rows this grid is listing happen to have. Asked of the server the first time it is
/// opened, and collapsed unless something in it is already on — a section the user chose from is one they
/// need to be able to see and untick, and everything else stays out of the way.
/// </summary>
public sealed partial class FilterValueSection : FilterSection
{
    private const string Category = "筛选";

    private readonly EmbyFilterList _list;
    private readonly ItemFilters _selection;
    private readonly FilterPanelViewModel _owner;

    internal FilterValueSection(EmbyFilterList list, ItemFilters selection, FilterPanelViewModel owner)
        : base(list.Label, list.Key == EmbyFilterBy.Keys.Years ? ThreeUp : TwoUp)
    {
        _list = list;
        _selection = selection;
        _owner = owner;

        ShowHeader();

        // Last, because the setter starts the request. Everything above it is what that request needs.
        IsExpanded = selection.Values(list.Key).Count > 0;
    }

    public ObservableCollection<FilterCheck> Values { get; } = [];

    /// <summary>The label, plus how many of it are on — the count the collapsed header has to carry.</summary>
    [ObservableProperty]
    public partial string Header { get; set; } = "";

    /// <summary>
    /// 正在读取…, a failure, or 这里没有可选的类型. Empty once there are boxes to show instead, and the
    /// <see cref="TextBlock"/> collapses with it rather than leaving a blank line above the boxes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoteVisibility))]
    public partial string Note { get; set; } = "";

    /// <summary>
    /// Two-way, and the request starts from the setter rather than from <c>Expander.Expanding</c>: that
    /// way the section this class opens itself and the one the user opens take the same path.
    /// </summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public Visibility NoteVisibility => Note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    internal override IEnumerable<string> Labels => [Label];

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _ = FillAsync();
    }

    /// <summary>
    /// Fills this section from the server. Reentrant on purpose — a section that opens itself is opened
    /// again by the user soon enough — and both callers await the one request, which the panel keeps.
    /// </summary>
    private async Task FillAsync()
    {
        if (_owner.Lookup(_list.Key) is not { } request) return;

        if (!request.IsCompleted)
        {
            Values.Clear();
            Note = "正在读取…";
        }

        try
        {
            Render(await request.ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
            _owner.Forget(_list.Key);
        }
        catch (Exception error)
        {
            _owner.Forget(_list.Key);
            Log.Warn(Category, $"读取「{_list.Label}」的可选值失败", error);

            Values.Clear();
            Note = Failure.Describe(error);
        }
    }

    private void Render(IReadOnlyList<string> values)
    {
        Values.Clear();

        // Anything already ticked that the server did not offer goes in anyway: a value that vanished
        // from the library — renamed, or filtered out by something else — must stay untickable-off.
        var chosen = _selection.Values(_list.Key);
        var all = values.Concat(chosen.Where(value => !values.Contains(value))).ToList();

        if (all.Count == 0)
        {
            Note = $"这里没有可选的{Label}";
            return;
        }

        Note = "";

        foreach (var value in all) Values.Add(Check(value));
    }

    private FilterCheck Check(string value) => new(
        value,
        null,
        () => _selection.Values(_list.Key).Contains(value),
        on =>
        {
            if (!_selection.SetValue(_list.Key, value, on)) return;

            ShowHeader();
            _owner.Notify();
        });

    private void ShowHeader()
    {
        var count = _selection.Values(_list.Key).Count;
        Header = count == 0 ? Label : $"{Label} · 已选 {count}";
    }
}
