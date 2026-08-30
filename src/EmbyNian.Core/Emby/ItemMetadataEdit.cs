using System.Globalization;
using System.Text.Json.Nodes;

namespace EmbyNian.Emby;

/// <summary>
/// 编辑元数据: the fields the editor offers, as the user typed them.
/// <para>
/// Every property is a string, including the numbers and the date. That is deliberate — a form has three
/// states per field where a typed model has two, and the third one matters most: 「left blank」 means
/// 「the server should have no value here」, which an <c>int</c> cannot say and a <c>int?</c> can only say
/// by conflating it with 「could not be parsed」. Keeping the text means the dialog can hand back exactly
/// what was typed, <see cref="Problem"/> can explain what is wrong with it, and nothing is silently
/// rounded to zero on the way through.
/// </para>
/// <para>
/// The counterpart rule is in <see cref="ApplyTo"/>: edits are written onto the JSON the server sent
/// rather than onto a rebuilt object. Emby's update endpoint takes a whole item and treats every field it
/// knows how to edit as authoritative, so a field this type has never heard of must still arrive back
/// unchanged — otherwise saving a corrected title would quietly wipe the cast list.
/// </para>
/// </summary>
public sealed record ItemMetadataEdit
{
    /// <summary>What a list field is joined with when read, and every character it may be split on.</summary>
    private const string ListJoin = "、";

    private static readonly char[] ListSplit = [',', '，', '、', ';', '；'];

    /// <summary>The date formats accepted on input. What Emby sends is parsed separately, as ISO 8601.</summary>
    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "yyyy/M/d", "yyyy-M-d", "yyyyMMdd", "yyyy年M月d日"];

    public string Name { get; init; } = "";

    /// <summary>原始标题, i.e. the title in the original language.</summary>
    public string OriginalTitle { get; init; } = "";

    /// <summary>
    /// 排序名称. The override, not the effective value: Emby computes a sort name from the title and
    /// <c>ForcedSortName</c> is what replaces it, so a blank box means 「go back to the computed one」.
    /// </summary>
    public string SortName { get; init; } = "";

    public string Overview { get; init; } = "";

    /// <summary>家长分级, a server-defined string such as <c>PG-13</c> rather than anything numeric.</summary>
    public string OfficialRating { get; init; } = "";

    /// <summary>公众评分, out of 10.</summary>
    public string CommunityRating { get; init; } = "";

    /// <summary>影评指数, out of 100 — the same scale the sort key uses.</summary>
    public string CriticRating { get; init; } = "";

    public string ProductionYear { get; init; } = "";

    /// <summary>发行日期, as <c>yyyy-MM-dd</c>. See <see cref="DateFormats"/> for what else is accepted.</summary>
    public string PremiereDate { get; init; } = "";

    /// <summary>类型, joined with 、.</summary>
    public string Genres { get; init; } = "";

    /// <summary>标签, joined with 、.</summary>
    public string Tags { get; init; } = "";

    /// <summary>集号 for an episode, 季号 for a season. Blank for everything else.</summary>
    public string IndexNumber { get; init; } = "";

    /// <summary>季号 as an episode sees it, so a misfiled episode can be moved without a re-scan.</summary>
    public string ParentIndexNumber { get; init; } = "";

    /// <summary>Reads the form's starting values out of the item the server sent.</summary>
    public static ItemMetadataEdit Read(JsonObject item) => new()
    {
        Name = Text(item, "Name"),
        OriginalTitle = Text(item, "OriginalTitle"),
        SortName = Text(item, "ForcedSortName"),
        Overview = Text(item, "Overview"),
        OfficialRating = Text(item, "OfficialRating"),
        CommunityRating = Number(item, "CommunityRating"),
        CriticRating = Number(item, "CriticRating"),
        ProductionYear = Number(item, "ProductionYear"),
        PremiereDate = Date(item, "PremiereDate"),
        Genres = List(item, "Genres"),
        Tags = List(item, "Tags"),
        IndexNumber = Number(item, "IndexNumber"),
        ParentIndexNumber = Number(item, "ParentIndexNumber")
    };

    /// <summary>
    /// What is wrong with what was typed, in one sentence, or null when the form can be saved. Checked
    /// before the dialog closes: a rejected save that has already dismissed the form has thrown the
    /// user's typing away.
    /// </summary>
    public string? Problem
    {
        get
        {
            if (Name.Trim().Length == 0) return "名称不能为空";
            if (Rating(CommunityRating) is null && CommunityRating.Trim().Length > 0)
                return "公众评分要填 0 到 10 之间的数字";
            if (Rating(CriticRating, max: 100) is null && CriticRating.Trim().Length > 0)
                return "影评指数要填 0 到 100 之间的数字";
            if (Year(ProductionYear) is null && ProductionYear.Trim().Length > 0)
                return "发行年份要填 1800 到 2200 之间的整数";
            if (Index(IndexNumber) is null && IndexNumber.Trim().Length > 0) return "集号要填非负整数";
            if (Index(ParentIndexNumber) is null && ParentIndexNumber.Trim().Length > 0) return "季号要填非负整数";
            if (Moment(PremiereDate) is null && PremiereDate.Trim().Length > 0)
                return "发行日期要填成 2024-05-01 这样的年-月-日";
            return null;
        }
    }

    /// <summary>
    /// Writes the edits onto <paramref name="item"/>, in place, and says whether any of them changed
    /// anything. False means there is nothing to send: the user opened the dialog, looked, and saved.
    /// </summary>
    /// <remarks>
    /// A blank box writes a JSON null rather than an empty string. The two are not the same to Emby —
    /// null is 「no value」 and is what a cleared field has to mean, while "" is a value that happens to
    /// be empty and would, for a sort name, sort the item before everything else on the server.
    /// </remarks>
    public bool ApplyTo(JsonObject item)
    {
        if (Problem is not null) throw new InvalidOperationException($"表单还有问题：{Problem}");

        var changed = false;

        changed |= SetText(item, "Name", Name);
        changed |= SetText(item, "OriginalTitle", OriginalTitle);
        changed |= SetText(item, "ForcedSortName", SortName);
        changed |= SetText(item, "Overview", Overview);
        changed |= SetText(item, "OfficialRating", OfficialRating);

        changed |= SetNumber(item, "CommunityRating", Rating(CommunityRating));
        changed |= SetNumber(item, "CriticRating", Rating(CriticRating, max: 100));
        changed |= SetNumber(item, "ProductionYear", Year(ProductionYear));
        changed |= SetNumber(item, "IndexNumber", Index(IndexNumber));
        changed |= SetNumber(item, "ParentIndexNumber", Index(ParentIndexNumber));

        changed |= SetDate(item, "PremiereDate", Moment(PremiereDate));
        changed |= SetList(item, "Genres", Split(Genres));
        changed |= SetList(item, "Tags", Split(Tags));

        return changed;
    }

    // ---- reading ---------------------------------------------------------------

    private static string Text(JsonObject item, string key) =>
        item[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    /// <summary>
    /// A number as text, with no reformatting: a rating that came back as <c>7.8</c> has to go back as
    /// <c>7.8</c> and not as <c>7.80</c>, or every save looks like a change.
    /// </summary>
    private static string Number(JsonObject item, string key) =>
        item[key] is JsonValue value ? value.ToString() : "";

    private static string Date(JsonObject item, string key) =>
        Parse(item[key]) is { } moment ? moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";

    private static string List(JsonObject item, string key) => item[key] is JsonArray array
        ? string.Join(ListJoin, array.Select(entry => entry?.ToString() ?? "").Where(entry => entry.Length > 0))
        : "";

    private static DateTimeOffset? Parse(JsonNode? node) =>
        node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
            ? moment
            : null;

    // ---- validating ------------------------------------------------------------

    private static double? Rating(string text, double max = 10)
    {
        if (text.Trim().Length == 0) return null;
        return Fraction(text) is { } value && value >= 0 && value <= max ? value : null;
    }

    private static int? Year(string text)
    {
        if (text.Trim().Length == 0) return null;
        return Whole(text) is { } value && value is >= 1800 and <= 2200 ? value : null;
    }

    private static int? Index(string text)
    {
        if (text.Trim().Length == 0) return null;
        return Whole(text) is { } value && value >= 0 ? value : null;
    }

    private static DateTimeOffset? Moment(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;

        return DateTimeOffset.TryParseExact(
            trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exact)
            ? exact
            : DateTimeOffset.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.AssumeUniversal, out var loose)
                ? loose
                : null;
    }

    /// <summary>
    /// Invariant first, then the user's own culture. Both, because the number came off the server as
    /// <c>7.8</c> and may be typed back as <c>7,8</c> by somebody whose keyboard says so.
    /// </summary>
    private static double? Fraction(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant) ? invariant
        : double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var local) ? local
        : null;

    private static int? Whole(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// Splits a list field. Duplicates are dropped rather than sent: Emby keeps them, and a genre listed
    /// twice is the kind of thing an editor should not be able to produce by accident.
    /// </summary>
    private static List<string> Split(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var part in text.Split(ListSplit, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (seen.Add(part))
                result.Add(part);

        return result;
    }

    // ---- writing ---------------------------------------------------------------

    private static bool SetText(JsonObject item, string key, string value)
    {
        var trimmed = value.Trim();
        return Set(item, key, trimmed.Length == 0 ? null : JsonValue.Create(trimmed));
    }

    private static bool SetNumber(JsonObject item, string key, double? value) =>
        Set(item, key, value is null ? null : JsonValue.Create(value.Value));

    private static bool SetNumber(JsonObject item, string key, int? value) =>
        Set(item, key, value is null ? null : JsonValue.Create(value.Value));

    /// <summary>
    /// Writes a date only when the day itself moved. The server sends a full timestamp and the form only
    /// shows the date, so rewriting it on every save would throw away a time somebody may have set on
    /// purpose and would make an untouched form look edited.
    /// </summary>
    private static bool SetDate(JsonObject item, string key, DateTimeOffset? value)
    {
        if (value is null) return Set(item, key, null);

        var existing = Parse(item[key]);
        if (existing?.UtcDateTime.Date == value.Value.UtcDateTime.Date) return false;

        var text = value.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
        return Set(item, key, JsonValue.Create(text));
    }

    private static bool SetList(JsonObject item, string key, List<string> values)
    {
        if (List(item, key) == string.Join(ListJoin, values)) return false;

        item[key] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        return true;
    }

    /// <summary>
    /// One assignment, with the 「did anything change」 answer. Compared as JSON text rather than by node
    /// identity: <c>JsonNode</c> has no value equality, and two nodes holding 7.8 are never the same node.
    /// </summary>
    private static bool Set(JsonObject item, string key, JsonNode? value)
    {
        var before = item[key];

        // Absent and null are the same thing to Emby's binder, and treating them as different would make
        // every clear-a-blank-field save look like an edit.
        if (before is null && value is null) return false;
        if (before is not null && value is not null && before.ToJsonString() == value.ToJsonString()) return false;

        item[key] = value;
        return true;
    }
}
