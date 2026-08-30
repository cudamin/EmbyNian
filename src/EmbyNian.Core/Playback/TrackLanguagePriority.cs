namespace EmbyNian.Playback;

/// <summary>
/// One language the client knows by name: the names a user may type or pick for it, the codes mpv
/// and Emby label tracks with, and the words a track <em>title</em> uses when its language field
/// says nothing useful (「简体&amp;日语」 on a stream whose Language is only <c>chi</c>).
/// </summary>
/// <param name="Family">
/// Languages sharing a family are the same tongue at different specificity: 简体中文, 繁体中文,
/// 普通话 and 粤语 are all <c>zh</c>.
/// </param>
/// <param name="Generic">
/// True for the entry that means the whole family (中文) rather than one variant. Only a generic
/// entry matches its siblings' codes, which is what keeps 简体中文 from selecting a 繁体 track.
/// </param>
public sealed record LanguageEntry(string[] Names, string[] Codes, string[] Hints, string Family, bool Generic)
{
    /// <summary>The name shown in the settings page and stored in settings.json.</summary>
    public string Label => Names[0];
}

/// <summary>
/// Turns track-language names into the values mpv's <c>slang</c>/<c>alang</c> options expect, and
/// answers whether a given track is in a given language.
/// <para>
/// The settings page stores names such as 简体中文; mpv wants a comma-separated list of ISO codes in
/// match order. Unknown names pass through untouched, so a user who knows mpv can type <c>jpn</c>
/// and it lands in the list verbatim.
/// </para>
/// </summary>
public static class TrackLanguagePriority
{
    /// <summary>
    /// Every language the client can name, in the order the settings page lists them. The code lists
    /// are also the priority order handed to mpv, so <c>zh-Hans</c> is tried before <c>chs</c>.
    /// </summary>
    private static readonly LanguageEntry[] Table =
    [
        new(["简体中文", "簡體中文", "简体", "簡體", "Simplified Chinese"],
            ["zh-Hans", "zh_hans", "zh-CN", "zh_CN", "zhs", "sc", "chs", "chi-Hans"],
            ["简体", "簡體", "简中", "簡中", "chs", "hans", "gb2312"],
            "zh", false),

        new(["中文", "Chinese"],
            ["zh", "chi", "zho"],
            ["中文", "中字", "chinese"],
            "zh", true),

        new(["繁体中文", "繁體中文", "繁体", "繁體", "Traditional Chinese"],
            ["zh-Hant", "zh_hant", "zh-TW", "zh_TW", "zh-HK", "zh_HK", "zht", "tc", "chi-Hant"],
            ["繁体", "繁體", "繁中", "cht", "hant", "big5"],
            "zh", false),

        new(["普通话", "普通話", "Mandarin"],
            ["cmn", "zh-CN", "zh"],
            ["普通话", "普通話", "国语", "國語", "mandarin"],
            "zh", false),

        new(["粤语", "粵語", "Cantonese"],
            ["yue", "zh-HK"],
            ["粤语", "粵語", "粤配", "cantonese"],
            "zh", false),

        new(["英语", "英語", "English"],
            ["eng", "en"],
            ["英语", "英語", "英文", "english"],
            "en", true),

        new(["日语", "日語", "Japanese"],
            ["jpn", "ja"],
            ["日语", "日語", "日文", "japanese"],
            "ja", true),

        new(["韩语", "韓語", "Korean"],
            ["kor", "ko"],
            ["韩语", "韓語", "韩文", "korean"],
            "ko", true),

        new(["法语", "法語", "French"],
            ["fra", "fre", "fr"],
            ["法语", "法語", "french"],
            "fr", true),

        new(["德语", "德語", "German"],
            ["deu", "ger", "de"],
            ["德语", "德語", "german"],
            "de", true),

        new(["西班牙语", "西班牙語", "Spanish"],
            ["spa", "es"],
            ["西班牙", "spanish"],
            "es", true),

        new(["俄语", "俄語", "Russian"],
            ["rus", "ru"],
            ["俄语", "俄語", "russian"],
            "ru", true),

        new(["意大利语", "意大利語", "Italian"],
            ["ita", "it"],
            ["意大利", "italian"],
            "it", true),

        new(["葡萄牙语", "葡萄牙語", "Portuguese"],
            ["por", "pt"],
            ["葡萄牙", "portuguese"],
            "pt", true)
    ];

    /// <summary>Names only. Expansion to mpv codes goes through this, so a raw code stays a raw code.</summary>
    private static readonly Dictionary<string, LanguageEntry> ByName = BuildByName();

    /// <summary>
    /// Names and codes. Matching goes through this, so a settings file holding <c>chi</c> — or a
    /// hand-typed <c>zh-CN</c> — still recognises the language it names.
    /// </summary>
    private static readonly Dictionary<string, LanguageEntry> ByToken = BuildByToken();

    /// <summary>
    /// Names for languages that are not worth a place in the priority list — 匈牙利语 is not a language
    /// anyone here picks subtitles by — but which still turn up on a track. Display only: these are
    /// never offered as a choice and never expanded into mpv codes, so a code that is missing here
    /// costs nothing but the raw code in a menu row.
    /// <para>
    /// Both the two- and three-letter forms are listed, because a muxer may have written either.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> ExtraNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hu"] = "匈牙利语", ["hun"] = "匈牙利语",
        ["th"] = "泰语", ["tha"] = "泰语",
        ["vi"] = "越南语", ["vie"] = "越南语",
        ["id"] = "印尼语", ["ind"] = "印尼语",
        ["ms"] = "马来语", ["msa"] = "马来语", ["may"] = "马来语",
        ["hi"] = "印地语", ["hin"] = "印地语",
        ["ar"] = "阿拉伯语", ["ara"] = "阿拉伯语",
        ["nl"] = "荷兰语", ["nld"] = "荷兰语", ["dut"] = "荷兰语",
        ["pl"] = "波兰语", ["pol"] = "波兰语",
        ["tr"] = "土耳其语", ["tur"] = "土耳其语",
        ["sv"] = "瑞典语", ["swe"] = "瑞典语",
        ["da"] = "丹麦语", ["dan"] = "丹麦语",
        ["no"] = "挪威语", ["nor"] = "挪威语", ["nb"] = "挪威语", ["nob"] = "挪威语",
        ["fi"] = "芬兰语", ["fin"] = "芬兰语",
        ["cs"] = "捷克语", ["ces"] = "捷克语", ["cze"] = "捷克语",
        ["sk"] = "斯洛伐克语", ["slk"] = "斯洛伐克语", ["slo"] = "斯洛伐克语",
        ["el"] = "希腊语", ["ell"] = "希腊语", ["gre"] = "希腊语",
        ["he"] = "希伯来语", ["heb"] = "希伯来语", ["iw"] = "希伯来语",
        ["uk"] = "乌克兰语", ["ukr"] = "乌克兰语",
        ["ro"] = "罗马尼亚语", ["ron"] = "罗马尼亚语", ["rum"] = "罗马尼亚语",
        ["bg"] = "保加利亚语", ["bul"] = "保加利亚语",
        ["hr"] = "克罗地亚语", ["hrv"] = "克罗地亚语",
        ["sr"] = "塞尔维亚语", ["srp"] = "塞尔维亚语",
        ["sl"] = "斯洛文尼亚语", ["slv"] = "斯洛文尼亚语",
        ["et"] = "爱沙尼亚语", ["est"] = "爱沙尼亚语",
        ["lv"] = "拉脱维亚语", ["lav"] = "拉脱维亚语",
        ["lt"] = "立陶宛语", ["lit"] = "立陶宛语",
        ["fa"] = "波斯语", ["fas"] = "波斯语", ["per"] = "波斯语",
        ["bn"] = "孟加拉语", ["ben"] = "孟加拉语",
        ["ta"] = "泰米尔语", ["tam"] = "泰米尔语",
        ["te"] = "泰卢固语", ["tel"] = "泰卢固语",
        ["tl"] = "菲律宾语", ["fil"] = "菲律宾语",
        ["mul"] = "多语言",
        ["und"] = "未标注",
        ["zxx"] = "无语言"
    };

    /// <summary>The languages the settings page offers, in display order.</summary>
    public static IReadOnlyList<LanguageEntry> Catalogue => Table;

    /// <summary>
    /// The canonical name for <paramref name="token"/>, so a settings file written by an older build
    /// (or by hand) lines up with the catalogue instead of appearing twice in the list.
    /// </summary>
    public static string Canonical(string? token)
    {
        var trimmed = (token ?? "").Trim();
        if (trimmed.Length == 0) return "";
        return ByToken.TryGetValue(trimmed, out var entry) ? entry.Label : trimmed;
    }

    /// <summary>
    /// A track's language as a name to show the user: the catalogue's name where it has one, then
    /// <see cref="ExtraNames"/>, and the code itself when neither knows it — a menu row reading
    /// <c>hu</c> is poor, and one reading nothing at all is worse.
    /// <para>
    /// A region subtag is dropped as a last resort, so <c>pt-BR</c> is at least 葡萄牙语 rather than a
    /// pair of unexplained letters.
    /// </para>
    /// </summary>
    public static string Describe(string? token)
    {
        var trimmed = (token ?? "").Trim();
        if (trimmed.Length == 0) return "";
        if (ByToken.TryGetValue(trimmed, out var entry)) return entry.Label;

        var code = Normalize(trimmed);
        if (ExtraNames.TryGetValue(code, out var name)) return name;

        var dash = code.IndexOf('-');
        if (dash <= 0) return trimmed;
        var stem = code[..dash];
        if (ByToken.TryGetValue(stem, out entry)) return entry.Label;
        return ExtraNames.TryGetValue(stem, out name) ? name : trimmed;
    }

    /// <summary>
    /// The mpv codes <paramref name="token"/> stands for, in match order; a single-element list
    /// holding the token itself when it is not a name this table knows.
    /// </summary>
    public static IReadOnlyList<string> Codes(string? token)
    {
        var trimmed = (token ?? "").Trim();
        if (trimmed.Length == 0) return [];
        return ByName.TryGetValue(trimmed, out var entry) ? entry.Codes : [trimmed];
    }

    /// <summary>
    /// Every separator a written priority list may use. mpv spells its own with <c>&gt;</c> and so did v2 of
    /// this client's settings file; a Chinese keyboard produces the full-width forms without being asked.
    /// One list, used everywhere a person or an older file can hand over a priority string — the settings
    /// page used to accept only four of these, so 「简体中文 &gt; 中文」 typed into the box was stored as one
    /// language named 「简体中文 &gt; 中文」, which matches nothing and says nothing about why.
    /// </summary>
    private static readonly string[] Separators = [">", "＞", "→", ",", "，", "、", ";", "；"];

    /// <summary>
    /// Converts a priority list to the mpv option value, or null when nothing usable was
    /// given. Tokens are split on <see cref="Separators"/> and matched against the known names; anything
    /// unrecognized is kept as a raw mpv code.
    /// </summary>
    public static string? ToMpvValue(string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority)) return null;
        return FromTokens(priority.Split(Separators, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A written priority list as canonical language names, in order, without blanks or duplicates.
    /// <para>
    /// A name this catalogue does not know is kept as it was written rather than dropped: it reaches mpv as
    /// a raw language code, which is how a language the list does not offer gets named at all. See
    /// <see cref="Canonical"/>.
    /// </para>
    /// </summary>
    public static List<string> ParseList(string? priority) =>
        string.IsNullOrWhiteSpace(priority)
            ? []
            : CleanList(priority.Split(Separators, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The same tidying for an already-split list, which is how the priority is stored.</summary>
    public static List<string> CleanList(IEnumerable<string>? languages)
    {
        var cleaned = new List<string>();
        foreach (var language in languages ?? [])
        {
            var name = Canonical(language);
            if (name.Length == 0) continue;
            if (!cleaned.Contains(name, StringComparer.OrdinalIgnoreCase)) cleaned.Add(name);
        }

        return cleaned;
    }

    /// <summary>
    /// The same conversion for an already-split list, which is how the subtitle-language priority is
    /// stored now that it is a multi-select rather than a typed-in string.
    /// </summary>
    public static string? FromTokens(IEnumerable<string>? tokens)
    {
        if (tokens is null) return null;

        var codes = new List<string>(8);
        foreach (var token in tokens)
        {
            foreach (var code in Codes(token))
            {
                if (!ContainsIgnoreCase(codes, code)) codes.Add(code);
            }
        }

        return codes.Count == 0 ? null : string.Join(",", codes);
    }

    /// <summary>
    /// Whether a track labelled with these fields is in the language <paramref name="token"/> names.
    /// <para>
    /// Codes are compared one-directionally — the track's code has to start with one of the
    /// language's — so 简体中文 matches <c>chs</c> and <c>zh-Hans</c> but not <c>cht</c> and not a bare
    /// <c>zh</c>, which is the whole point of an ordered priority list. 中文, being the generic entry
    /// of its family, still matches every Chinese track whatever variant it claims.
    /// </para>
    /// </summary>
    public static bool Matches(string? token, string? language, string? displayLanguage, string? title)
    {
        var trimmed = (token ?? "").Trim();
        if (trimmed.Length == 0) return false;

        var entry = ByToken.GetValueOrDefault(trimmed);
        var codes = entry?.Codes ?? [trimmed];
        if (CodeMatches(language, codes) || CodeMatches(displayLanguage, codes)) return true;

        var hints = entry?.Hints ?? [];
        if (ContainsHint(language, hints) || ContainsHint(displayLanguage, hints) || ContainsHint(title, hints)) return true;

        // 中文 names the family, so a 简体 or 繁体 track answers to it as well.
        if (entry is null || !entry.Generic) return false;
        return SameFamily(language, entry.Family) || SameFamily(displayLanguage, entry.Family);
    }

    private static bool SameFamily(string? value, string family) =>
        Table.Any(entry => entry.Family == family && CodeMatches(value, entry.Codes));

    private static bool CodeMatches(string? value, IReadOnlyList<string> codes)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0) return false;

        foreach (var code in codes)
        {
            var wanted = Normalize(code);
            if (wanted.Length == 0) continue;
            if (normalized.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return true;

            // zh-Hans also answers for zh-Hans-CN, but never the other way round.
            if (normalized.StartsWith(wanted + "-", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool ContainsHint(string? value, IReadOnlyList<string> hints)
    {
        if (string.IsNullOrWhiteSpace(value) || hints.Count == 0) return false;
        return hints.Any(hint => value.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Underscores are how Emby and some muxers spell a subtag separator.</summary>
    private static string Normalize(string? value) => (value ?? "").Trim().Replace('_', '-');

    private static Dictionary<string, LanguageEntry> BuildByName()
    {
        var map = new Dictionary<string, LanguageEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Table)
        {
            foreach (var name in entry.Names) map.TryAdd(name, entry);
        }

        return map;
    }

    private static Dictionary<string, LanguageEntry> BuildByToken()
    {
        var map = BuildByName();

        // First entry wins: zh-CN belongs to 简体中文 rather than 普通话, and a bare zh to 中文.
        foreach (var entry in Table)
        {
            foreach (var code in entry.Codes) map.TryAdd(code, entry);
        }

        return map;
    }

    private static bool ContainsIgnoreCase(List<string> codes, string value) =>
        codes.Any(code => string.Equals(code, value, StringComparison.OrdinalIgnoreCase));
}
