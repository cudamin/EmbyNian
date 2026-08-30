using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyNian.Infrastructure;
using Microsoft.UI.Xaml;
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

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial FontOption? Selected { get; set; }

    /// <summary>The line under the box: how many families were found, or that the scan is still going.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>The family this row is set to. Read by the self-check, which must not change it.</summary>
    internal string Value => Selected?.Name ?? string.Empty;

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
    /// machine that also has 「Consolas Nerd Font」. Only then the first family the search itself matched,
    /// which is deliberately not 「the first row of the list」 — the current family is pinned into
    /// <see cref="Matches"/> whatever is typed (see <see cref="Refilter"/>), so on a machine where it
    /// sorts first, 「consolas」 and Enter would otherwise have picked the font already in use.
    /// </para>
    /// </summary>
    internal FontOption? Resolve(string typed)
    {
        var text = typed.Trim();
        if (text.Length == 0) return null;

        foreach (var option in Matches)
        {
            if (string.Equals(option.Name, text, StringComparison.OrdinalIgnoreCase)) return option;
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
        _all = [.. _catalogue.Including(current).Families.Select(entry => new FontOption(entry))];

        _committing = true;
        try
        {
            Selected = _all.FirstOrDefault(option => string.Equals(option.Name, current, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _committing = false;
        }

        Refilter();
    }

    partial void OnQueryChanged(string value) => Refilter();

    partial void OnSelectedChanged(FontOption? value)
    {
        // Null is the list reporting that what was selected is no longer in it, which happens while the
        // filter is being applied. It is not a person unsetting the font — there is no way to do that —
        // so it never reaches the settings file.
        if (_committing || value is null) return;

        _write(value.Name);
        _save();
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
    /// <param name="Ok">The list arrived, the search filters, and searching cannot lose the current value.</param>
    internal sealed record Probe(bool Ok, int Total, string Detail);

    /// <summary>
    /// Types into the search box and reads back what it did, then puts the box back as it was.
    /// <para>
    /// Nothing here can reach the settings file: this row only ever writes when a family is picked, and the
    /// three queries below are chosen so the picked one is never the thing that changes. That is the point of
    /// the middle one — a query that matches no font on any machine must still leave the current value in the
    /// list, because a filter that can hide the value can also make the row report having none.
    /// </para>
    /// </summary>
    internal Probe Measure()
    {
        var saved = Query;
        var value = Value;
        var clock = Stopwatch.StartNew();

        try
        {
            Query = string.Empty;
            var all = Matches.Count;

            // A word out of the current family's own name, rather than a font this machine may not have:
            // 「YaHei」 for Microsoft YaHei. It must find at least the family it came from.
            var word = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? value;
            Query = word;
            var found = Matches.Count;
            var kept = string.Equals(Value, value, StringComparison.Ordinal);

            Query = "没有任何字体会叫这个名字";
            var pinned = Matches.Count;

            Query = string.Empty;
            var again = Matches.Count;

            // Four full passes over every family on the machine, with the list bound and live. The bound
            // is loose on purpose — it is here to catch a filter that went quadratic, not to time this
            // machine — but it has to be there: this runs on the UI thread on every keystroke.
            var spent = clock.ElapsedMilliseconds;

            var ok = value.Length > 0
                && all == Total
                && all > 1
                && found > 0
                && kept
                && pinned == 1
                && again == all
                && spent < 2000
                && !Status.Contains("正在", StringComparison.Ordinal);

            return new Probe(ok, Total,
                $"当前「{value}」，共 {Total} 个字体族；搜「{word}」得 {found} 个，"
                    + $"搜一个不存在的名字剩 {pinned} 个（应当只剩当前值），清空搜索回到 {again} 个，"
                    + $"{(kept ? "过滤没有动过选中项" : "过滤把选中项弄丢了")}；四次过滤用了 {spent} 毫秒；{Status}");
        }
        finally
        {
            Query = saved;
        }
    }
}
