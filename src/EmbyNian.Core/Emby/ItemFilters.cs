namespace EmbyNian.Emby;

/// <summary>
/// Which filters are on. The panel edits one of these, <see cref="ItemQuery"/> sends it, and
/// <c>UiSettings.Filters</c> stores one per library.
/// <para>
/// A plain mutable object with <see cref="List{T}"/> properties rather than sets or an immutable record,
/// because this is also the shape written to settings.json: a list round-trips through
/// <c>System.Text.Json</c> with no converter and reads back in the file as the user's own order, and a
/// selection of this size never needs a hash lookup.
/// </para>
/// </summary>
public sealed class ItemFilters
{
    /// <summary>
    /// <see cref="EmbyFilterOption.Id"/> values that are on. Ids rather than query values, so a stored
    /// selection survives a relabelling and 高清/标清 stay distinguishable — they share a key.
    /// </summary>
    public List<string> Toggles { get; set; } = [];

    public List<string> Genres { get; set; } = [];

    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Years as strings. They go into the query as text either way, and a settings file holding
    /// <c>["2019"]</c> is no harder to read than <c>[2019]</c> — while a list of strings means
    /// <see cref="Values"/> can hand every dynamic section the same type.
    /// </summary>
    public List<string> Years { get; set; } = [];

    /// <summary>
    /// How many individual choices are on; what the button's badge shows.
    /// <para>
    /// Unknown ids are not counted, for the same reason <see cref="Contributions"/> does not send them:
    /// a badge saying one filter is on, with nothing filtered and nothing to untick, is worse than a
    /// selection written by a later build quietly not applying.
    /// </para>
    /// </summary>
    public int Count =>
        Toggles.Count(id => EmbyFilterBy.Find(id) is not null) + Genres.Count + Tags.Count + Years.Count;

    public bool IsEmpty => Count == 0;

    /// <summary>
    /// True when nothing at all is stored, ids this build does not recognise included. The difference
    /// from <see cref="IsEmpty"/> only shows up against a selection written by a later build: that one
    /// filters nothing and shows nothing, so it is empty, but it is not blank and must not be discarded
    /// the first time an older build touches the panel.
    /// </summary>
    public bool IsBlank => Toggles.Count == 0 && Genres.Count == 0 && Tags.Count == 0 && Years.Count == 0;

    public bool Has(string id) => Toggles.Contains(id);

    /// <summary>
    /// The list behind one of <see cref="EmbyFilterBy.Lists"/>, so the panel can build all three
    /// sections in a loop instead of naming each one.
    /// </summary>
    public List<string> Values(string key) => key switch
    {
        EmbyFilterBy.Keys.Genres => Genres,
        EmbyFilterBy.Keys.Tags => Tags,
        EmbyFilterBy.Keys.Years => Years,
        // Not a user-facing failure: the keys come from EmbyFilterBy.Lists, a constant. Throwing means a
        // new section that forgot its list fails in the tests rather than filtering nothing on screen.
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "没有这个筛选分组")
    };

    /// <summary>
    /// Turns one option on or off, taking its <see cref="EmbyFilterOption.Excludes"/> with it. Returns
    /// whether anything changed, so the caller can skip a reload it does not need.
    /// </summary>
    public bool Set(EmbyFilterOption option, bool on)
    {
        var changed = false;

        if (on)
        {
            foreach (var excluded in option.Excludes)
                changed |= Toggles.Remove(excluded);

            if (!Toggles.Contains(option.Id))
            {
                Toggles.Add(option.Id);
                changed = true;
            }
        }
        else
        {
            changed = Toggles.Remove(option.Id);
        }

        return changed;
    }

    /// <summary>Turns one value of a dynamic section on or off. Returns whether anything changed.</summary>
    public bool SetValue(string key, string value, bool on)
    {
        var values = Values(key);

        if (!on) return values.Remove(value);
        if (values.Contains(value)) return false;

        values.Add(value);
        return true;
    }

    public bool Clear()
    {
        // IsBlank rather than IsEmpty, so an id this build does not recognise is also cleared. Otherwise
        // it would be unreachable: not counted, not shown, never removed.
        if (IsBlank) return false;

        Toggles.Clear();
        Genres.Clear();
        Tags.Clear();
        Years.Clear();
        return true;
    }

    /// <summary>A detached copy, so the page can hold one and settings.json another.</summary>
    public ItemFilters Clone() => new()
    {
        Toggles = [.. Toggles],
        Genres = [.. Genres],
        Tags = [.. Tags],
        Years = [.. Years]
    };

    /// <summary>
    /// Every (key, value) pair this selection contributes, unjoined —
    /// <see cref="ItemQuery.ToParameters"/> groups them by key and joins each with that key's own
    /// delimiter, because several options share a key and one of those keys wants a pipe.
    /// <para>
    /// Toggles come out in catalogue order rather than the order they were ticked, so the same selection
    /// always produces the same query string. An id the catalogue no longer knows is skipped: settings
    /// written by a later build must not become a filter nobody can see or clear.
    /// </para>
    /// </summary>
    public IEnumerable<(string Key, string Value)> Contributions()
    {
        foreach (var option in EmbyFilterBy.All)
            if (Toggles.Contains(option.Id))
                yield return (option.Key, option.Value);

        foreach (var list in EmbyFilterBy.Lists)
            foreach (var value in Values(list.Key))
                if (!string.IsNullOrWhiteSpace(value))
                    yield return (list.Key, value);
    }

    /// <summary>
    /// What is on, in words: 「未播放、收藏 · 类型：动画、科幻」. For the filter button's tooltip, where the
    /// badge only has room for a number.
    /// </summary>
    public string Describe()
    {
        var sections = new List<string>(4);

        var labels = EmbyFilterBy.All
            .Where(option => Toggles.Contains(option.Id))
            .Select(option => option.Label)
            .ToList();

        if (labels.Count > 0) sections.Add(string.Join('、', labels));

        foreach (var list in EmbyFilterBy.Lists)
        {
            var values = Values(list.Key);
            if (values.Count > 0) sections.Add($"{list.Label}：{string.Join('、', values)}");
        }

        return string.Join("  ·  ", sections);
    }
}
