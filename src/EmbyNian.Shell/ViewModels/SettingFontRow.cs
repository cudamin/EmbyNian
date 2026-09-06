using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyNian.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// One family in the font picker: the name the setting is written as, the name the font calls itself in
/// its own script, and the family itself so the line can be drawn in the font it offers.
/// </summary>
public sealed class FontOption
{
    private readonly FontEntry _entry;

    private FontFamily? _preview;

    internal FontOption(FontEntry entry)
    {
        _entry = entry;
    }

    /// <summary>What goes into the settings file and on to mpv's <c>sub-font</c>.</summary>
    public string Name => _entry.Name;

    /// <summary>微软雅黑 under Microsoft YaHei, or nothing when the font states no such name.</summary>
    public string Localized => _entry.Localized;

    public Visibility LocalizedVisibility => Localized.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// The family, for drawing this line in it — the point of a font list being that you can see the
    /// fonts. Built on demand and kept: the list virtualises, so only the visible dozen ever ask, and
    /// asking again on every scroll would rebuild the same object per frame.
    /// </summary>
    public FontFamily Preview => _preview ??= new FontFamily(Name);

    internal bool Matches(IReadOnlyList<string> tokens) => _entry.Matches(tokens);

    /// <summary>Whether this family is the one <paramref name="name"/> names, alias spellings included.</summary>
    internal bool AnswersTo(string name) => _entry.AnswersTo(name);

    public override string ToString() => Name;
}

/// <summary>
/// The 字幕 → 字体 row: a search box over every family installed on the machine, and a list where
/// picking one is what writes the setting.
/// <para>
/// Selection is the only way this row commits, deliberately. Every other text setting on this page waits
/// for the box to lose focus before writing, because writing per keystroke would hand mpv 「Microsoft Ya」
/// on the way to 「Microsoft YaHei」; here the box is not the value at all — it only filters — so the
/// keystroke problem cannot arise and the value is always a family that exists.
/// </para>
/// <para>
/// The box has two things to say, on the same model as the player's strip (which is the other instance of
/// this row type): at rest it reads as the family in use — 「输入栏要显示当前正在使用的字体」
/// （2026-09-06）— and taking the keyboard empties it into a search box. <see cref="BoxText"/> is what the
/// box shows and <see cref="Query"/> is what the filter runs on; they are two properties rather than one
/// because the resting display is a family name, and filtering on it would open the row onto a list of
/// one. The list under it starts folded away — 「默认收起来不要展开」 — and only ever opens when the box
/// is clicked or typed into.
/// </para>
/// <para>
/// The catalogue arrives late: scanning a few hundred font files is not something the page can do while
/// it is being built. Until it lands the row holds exactly one family, the one the settings file names,
/// so the row is never empty and never lies about what is set.
/// </para>
/// </summary>
public sealed partial class SettingFontRow : SettingRow
{
    private readonly Action<string> _write;
    private readonly Action _save;

    private FontCatalogue _catalogue = FontCatalogue.Empty;
    private IReadOnlyList<FontOption> _all = [];
    private bool _committing;

    /// <summary>
    /// True while the box's text is being placed programmatically — the resting display of the family in
    /// use, or the emptying that begins a search. Neither is a person typing, so neither may run the
    /// filter or open the list.
    /// </summary>
    private bool _placing;

    private string _value = "";

    internal SettingFontRow(string label, string? note, string value, Action<string> write, Action save)
        : base(label, note)
    {
        _write = write;
        _save = save;

        Status = "正在读取这台机器装的字体…";
        Load(value);
    }

    /// <summary>The families the search box currently allows through, in name order.</summary>
    public ObservableCollection<FontOption> Matches { get; } = [];

    /// <summary>What the search filter runs on. Written by the row itself; the box's text is <see cref="BoxText"/>.</summary>
    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    /// <summary>The text in the search box: the family in use while the row is at rest, empty while it is being searched.</summary>
    [ObservableProperty]
    public partial string BoxText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial FontOption? Selected { get; set; }

    /// <summary>The line under the box: how many families were found, or that the scan is still going.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>
    /// Whether the family list under the search box is on screen. Folded away by default and after every
    /// pick or search someone walked away from — the 252-pixel-tall list is only for someone actually
    /// choosing — and opened by the box being clicked or typed into.
    /// </summary>
    [ObservableProperty]
    public partial Visibility ListVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>
    /// The search box was clicked or tabbed into: the list comes back for browsing, the box empties (so
    /// the whole list is there to scroll, and so typing into the end of a family name cannot produce
    /// 「Microsoft YaHeiconsolas」), and the filter resets to match.
    /// </summary>
    internal void OpenList()
    {
        ListVisibility = Visibility.Visible;

        _placing = true;
        try { BoxText = string.Empty; }
        finally { _placing = false; }

        Query = string.Empty;
    }

    /// <summary>
    /// The box gave the keyboard back. A search nobody finished is not a value: the family in use goes
    /// back into the box and the list folds away.
    /// <para>
    /// Called from the box's <c>LostFocus</c>, which also fires when a family in the list is clicked —
    /// focus moves on the press and the pick lands on the release — so this asks where the focus went
    /// first, and folding the list under a pending pick would make the row unuseable.
    /// </para>
    /// </summary>
    internal void CloseList()
    {
        if (FocusIsInsideTheList()) return;

        ListVisibility = Visibility.Collapsed;
        Query = string.Empty;
        PlaceCurrentFont();
    }

    /// <summary>
    /// Whether the element that just took the keyboard sits inside the family list. Walks up from
    /// <see cref="FocusManager.GetFocusedElement"/> rather than comparing types, because a click lands
    /// focus somewhere inside the item container.
    /// </summary>
    private static bool FocusIsInsideTheList()
    {
        if (FocusManager.GetFocusedElement() is not DependencyObject element) return false;

        while (element is not null)
        {
            if (element is ListView) return true;
            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    /// <summary>Puts the family in use into the box, without running the filter.</summary>
    private void PlaceCurrentFont()
    {
        _placing = true;
        try { BoxText = _value; }
        finally { _placing = false; }
    }

    /// <summary>
    /// The family this row is set to, as the settings file spells it — which may be one of the
    /// family's aliases rather than its primary name (方正中等线简体 next to a catalogue whose row for
    /// the same file reads FZZhongDengXian-Z07S). Held separately from <see cref="Selected"/> for
    /// exactly that: the selection points at the family, this is what was stored, and the two only
    /// agree on the spelling a pick has actually written. Read by the self-check, which must not change it.
    /// </summary>
    internal string Value => _value;

    /// <summary>How many families are on offer, filtering aside.</summary>
    internal int Total => _all.Count;

    /// <summary>
    /// Takes the finished scan, keeping the current value selected. Called on the UI thread once the
    /// library comes back; safe to call again, which is what a second visit to the page does.
    /// </summary>
    internal void Fill(FontCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        _catalogue = catalogue;
        Load(Value);

        Status = catalogue.Families.Count == 0
            ? "没有在 Windows 字体目录里读到字体，下面这一个来自设置文件"
            : $"机器上共 {catalogue.Families.Count} 个字体族（{catalogue.FileCount} 个字体文件）；搜名字的任意一段，点一下就用它";
    }

    /// <summary>
    /// Takes the value the settings file now holds, without writing anything back or asking for the scan
    /// again. For the second picker over this same setting — the player's title strip — which has to open
    /// on whatever the settings window may have changed while the player was not looking.
    /// </summary>
    internal void Reseed(string value)
    {
        if (string.Equals(Value, value, StringComparison.OrdinalIgnoreCase)) return;

        Load(value);
    }

    /// <summary>
    /// The family a typed line means, for a box whose text is the search rather than the value — the
    /// player's, where there is no room for a list beside it and Enter has to mean something.
    /// <para>
    /// An exact name wins first: someone who typed the whole of 「Consolas」 means that family even on a
    /// machine that also has 「Consolas Nerd Font」. Exact means the family answers to the line by any of
    /// its names, so typing 方正中等线简体 is exact for the row the catalogue calls FZZhongDengXian-Z07S.
    /// Only then the first family the search itself matched, which is deliberately not 「the first row of
    /// the list」 — the current family is pinned into <see cref="Matches"/> whatever is typed (see
    /// <see cref="Refilter"/>), so on a machine where it sorts first, 「consolas」 and Enter would
    /// otherwise have picked the font already in use.
    /// </para>
    /// </summary>
    internal FontOption? Resolve(string typed)
    {
        var text = typed.Trim();
        if (text.Length == 0) return null;

        foreach (var option in Matches)
        {
            if (option.AnswersTo(text)) return option;
        }

        var tokens = FontCatalogue.Tokenize(text);
        foreach (var option in Matches)
        {
            if (option.Matches(tokens)) return option;
        }

        return null;
    }

    /// <summary>
    /// Rebuilds the offered families around one value, which is kept whether or not it is installed —
    /// a picker that cannot show the current setting reads as having no setting, and the way out of
    /// that would be to pick something else.
    /// </summary>
    private void Load(string current)
    {
        _value = current;

        _all = [.. _catalogue.Including(current).Families.Select(entry => new FontOption(entry))];

        _committing = true;
        try
        {
            // The stored value may be the family's alias rather than its primary name — 方正中等线简体
            // next to a catalogue that names the same file FZZhongDengXian-Z07S. Either spelling selects
            // the same row; what must never happen is a value this list plainly holds reading as unset.
            Selected = _all.FirstOrDefault(option => option.AnswersTo(current));
        }
        finally
        {
            _committing = false;
        }

        // The row starts, and restarts after every reseed, folded away with the family in use in the box.
        ListVisibility = Visibility.Collapsed;
        PlaceCurrentFont();
        Query = string.Empty;
        Refilter();
    }

    partial void OnSelectedChanged(FontOption? value)
    {
        // Null is the list reporting that what was selected is no longer in it, which happens while the
        // filter is being applied. It is not a person unsetting the font — there is no way to do that —
        // so it never reaches the settings file.
        if (_committing || value is null) return;

        _write(value.Name);
        _value = value.Name;
        _save();

        // The pick is done; the list it was picked from folds away and the box goes back to reading as
        // the family now in use. The status line stays, so how many families the machine has and what
        // the search does is still said on the row itself.
        ListVisibility = Visibility.Collapsed;
        PlaceCurrentFont();
        Query = string.Empty;
        Refilter();
    }

    partial void OnQueryChanged(string value) => Refilter();

    partial void OnBoxTextChanged(string value)
    {
        if (_placing) return;

        // A person is typing: this is browsing beginning again, so the list comes with it and the filter
        // runs on what was typed.
        Query = value;
        ListVisibility = Visibility.Visible;
    }

    private void Refilter()
    {
        var tokens = FontCatalogue.Tokenize(Query);
        var selected = Selected;

        var wanted = new List<FontOption>();
        foreach (var option in _all)
        {
            // The current font stays visible whatever is typed: this list is also where the row says
            // what the setting is, and a search that hides it would make the row look unset.
            if (option.Matches(tokens) || ReferenceEquals(option, selected)) wanted.Add(option);
        }

        // Applied as a difference rather than cleared and refilled. Clearing makes the bound list drop
        // its selection, and coming back from a search with nothing selected is exactly the state this
        // row must never be in.
        //
        // The set is what keeps this linear. There are a few thousand families on a machine with fonts
        // installed, and 「is this row still wanted」 asked of a list instead would be a few million
        // comparisons per keystroke.
        var keep = new HashSet<FontOption>(wanted);
        for (var index = Matches.Count - 1; index >= 0; index--)
        {
            if (!keep.Contains(Matches[index])) Matches.RemoveAt(index);
        }

        for (var index = 0; index < wanted.Count; index++)
        {
            if (index < Matches.Count && ReferenceEquals(Matches[index], wanted[index])) continue;

            Matches.Insert(index, wanted[index]);
        }
    }

    /// <summary>What the self-check measured about the picker.</summary>
    /// <param name="Ok">The row rests folded with the family in use in the box, the search filters, and searching cannot lose the current value.</param>
    internal sealed record Probe(bool Ok, int Total, string Detail);

    /// <summary>
    /// 自检：点选一个字体之后列表收得起、点搜索框之后展得开，而且静止时输入栏里是当前在用的那个字体 ——
    /// 「默认收起来不要展开」「输入栏要显示当前正在使用的字体」（2026-09-06）。就地造一个假行拨一遍，
    /// 不碰设置文件、也不碰屏上那一页（同 <see cref="SettingChoiceRow.Probe"/> 的做法）。坏法在屏上看得见
    /// 却没有读数：列表收不掉只是多占 252 像素，展不开则更像「这个框坏了」，输入栏不显示当前字体的话
    /// 这一行的样子和一个没设过值的行没有分别。
    /// </summary>
    internal static (bool Ok, string Detail) CollapseProbe()
    {
        var saved = "";
        var row = new SettingFontRow("探针", null, "Consolas", value => saved = value, () => { });

        // 静止的样子：列表收着、输入栏里是当前字体、筛子是空的（整张名单都在）。
        var atRest = row.ListVisibility == Visibility.Collapsed
            && row.BoxText == "Consolas"
            && row.Query.Length == 0
            && row.Matches.Count > 0;

        row.OpenList();
        var opened = row.ListVisibility == Visibility.Visible && row.BoxText.Length == 0;

        // Picking the row that is already selected is silently nothing — re-chosen sameness must not
        // save the file twice — so the probe clears the selection first to stand in for「挑了一个别的」.
        row.Selected = null;
        row.Selected = row.Matches[0];
        var closedOnPick = row.ListVisibility == Visibility.Collapsed && saved.Length > 0 && row.BoxText == saved;

        row.OpenList();
        var reopened = row.ListVisibility == Visibility.Visible;

        row.BoxText = "字";
        var stayedOpen = row.ListVisibility == Visibility.Visible;

        // 走开没挑：输入栏放回当前字体，列表收回去。
        row.CloseList();
        var folded = row.ListVisibility == Visibility.Collapsed && row.BoxText == "Consolas" && row.Query.Length == 0;

        return (atRest && opened && closedOnPick && reopened && stayedOpen && folded,
            $"假行：静止时{(atRest ? "收起且显示「Consolas」" : "没收起或没显示当前字体")}、"
                + $"点搜索框{(opened ? "展开并清空" : "没展开或没清空")}、"
                + $"选中后{(closedOnPick ? $"收起、写了一次（{saved}）、输入栏跟着改" : "没收起、没写盘或输入栏没跟")}、"
                + $"再点{(reopened ? "又展开" : "没展开")}、敲字后{(stayedOpen ? "保持展开" : "又收了")}、"
                + $"走开{(folded ? "收回并放回当前字体" : "没收回或没放回")}");
    }

    /// <summary>
    /// Types into the search box and reads back what it did, then puts the row back as it was.
    /// <para>
    /// Nothing here can reach the settings file: this row only ever writes when a family is picked, and the
    /// three queries below are chosen so the picked one is never the thing that changes. That is the point of
    /// the middle one — a query that matches no font on any machine must still leave the current value in the
    /// list, because a filter that can hide the value can also make the row report having none.
    /// </para>
    /// </summary>
    internal Probe Measure()
    {
        var clock = Stopwatch.StartNew();
        var value = Value;

        try
        {
            // 静止时输入栏里应当是当前字体 —— 「输入栏要显示当前正在使用的字体」的那一半。
            var atRest = string.Equals(BoxText, value, StringComparison.Ordinal)
                && ListVisibility == Visibility.Collapsed
                && Query.Length == 0;

            // A word out of the current family's own name, rather than a font this machine may not have:
            // 「YaHei」 for Microsoft YaHei. It must find at least the family it came from. Typed through
            // BoxText, which is what a person types into — Query follows from it.
            var word = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? value;
            BoxText = word;
            var found = Matches.Count;
            var kept = string.Equals(Value, value, StringComparison.Ordinal);

            BoxText = "没有任何字体会叫这个名字";
            var pinned = Matches.Count;

            // 清掉搜索，整张名单回来 —— 「过滤完回不去」是这一行最不能有的坏法。
            BoxText = string.Empty;
            var again = Matches.Count;

            // Four full passes over every family on the machine, with the list bound and live. The bound
            // is loose on purpose — it is here to catch a filter that went quadratic, not to time this
            // machine — but it has to be there: this runs on the UI thread on every keystroke.
            var spent = clock.ElapsedMilliseconds;

            var ok = value.Length > 0
                && atRest
                && again == Total
                && again > 1
                && found > 0
                && kept
                && pinned == 1
                && spent < 2000
                && !Status.Contains("正在", StringComparison.Ordinal);

            return new Probe(ok, Total,
                $"静止时输入栏{(atRest ? $"显示着「{value}」" : "没显示当前字体")}，共 {Total} 个字体族；"
                    + $"搜「{word}」得 {found} 个，搜一个不存在的名字剩 {pinned} 个（应当只剩当前值），"
                    + $"清空搜索回到 {again} 个，{(kept ? "过滤没有动过选中项" : "过滤把选中项弄丢了")}；"
                    + $"四次过滤用了 {spent} 毫秒；{Status}");
        }
        finally
        {
            ListVisibility = Visibility.Collapsed;
            PlaceCurrentFont();
            Query = string.Empty;
            Refilter();
        }
    }
}
