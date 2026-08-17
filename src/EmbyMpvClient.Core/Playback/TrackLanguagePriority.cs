namespace EmbyMpvClient.Playback;

/// <summary>
/// Turns a human-readable track-language priority list into the value mpv's
/// <c>slang</c>/<c>alang</c> options expect. The settings page asks for something like
/// <c>Simplified Chinese&gt;Chinese&gt;Traditional Chinese</c>; mpv wants a comma-separated
/// list of ISO codes in match order. Unknown names pass through untouched, so a user who
/// knows mpv can type <c>jpn</c> and it lands in the list verbatim.
/// </summary>
public static class TrackLanguagePriority
{
    /// <summary>
    /// Recognized names mapped to the codes mpv matches against, in the order they should
    /// be tried. Keys are matched case-insensitively against each token of the input.
    /// </summary>
    private static readonly Dictionary<string, string[]> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["simplified chinese"] = ["zh-Hans", "zh_hans", "zh-CN", "zh_CN", "zhs", "sc", "chs", "chi-Hans"],
            ["简体中文"] = ["zh-Hans", "zh_hans", "zh-CN", "zh_CN", "zhs", "sc", "chs", "chi-Hans"],
            ["簡體中文"] = ["zh-Hans", "zh_hans", "zh-CN", "zh_CN", "zhs", "sc", "chs", "chi-Hans"],
            ["简体"] = ["zh-Hans", "zh_hans", "zh-CN", "zh_CN", "zhs", "sc", "chs", "chi-Hans"],
            ["簡體"] = ["zh-Hans", "zh_hans", "zh-CN", "zh_CN", "zhs", "sc", "chs", "chi-Hans"],
            ["chinese"] = ["zh", "chi", "zho"],
            ["中文"] = ["zh", "chi", "zho"],
            ["traditional chinese"] = ["zh-Hant", "zh_hant", "zh-TW", "zh_TW", "zh-HK", "zh_HK", "zht", "tc", "chi-Hant"],
            ["繁體中文"] = ["zh-Hant", "zh_hant", "zh-TW", "zh_TW", "zh-HK", "zh_HK", "zht", "tc", "chi-Hant"],
            ["繁体中文"] = ["zh-Hant", "zh_hant", "zh-TW", "zh_TW", "zh-HK", "zh_HK", "zht", "tc", "chi-Hant"],
            ["繁体"] = ["zh-Hant", "zh_hant", "zh-TW", "zh_TW", "zh-HK", "zh_HK", "zht", "tc", "chi-Hant"],
            ["繁體"] = ["zh-Hant", "zh_hant", "zh-TW", "zh_TW", "zh-HK", "zh_HK", "zht", "tc", "chi-Hant"],
            ["mandarin"] = ["cmn", "zh-CN", "zh"],
            ["普通话"] = ["cmn", "zh-CN", "zh"],
            ["普通話"] = ["cmn", "zh-CN", "zh"],
            ["cantonese"] = ["yue", "zh-HK"],
            ["粤语"] = ["yue", "zh-HK"],
            ["粵語"] = ["yue", "zh-HK"],
            ["english"] = ["eng", "en"],
            ["英语"] = ["eng", "en"],
            ["英語"] = ["eng", "en"],
            ["japanese"] = ["jpn", "ja"],
            ["日语"] = ["jpn", "ja"],
            ["日語"] = ["jpn", "ja"],
            ["korean"] = ["kor", "ko"],
            ["韩语"] = ["kor", "ko"],
            ["韓語"] = ["kor", "ko"],
            ["french"] = ["fra", "fre", "fr"],
            ["法语"] = ["fra", "fre", "fr"],
            ["法語"] = ["fra", "fre", "fr"],
            ["german"] = ["deu", "ger", "de"],
            ["德语"] = ["deu", "ger", "de"],
            ["德語"] = ["deu", "ger", "de"],
            ["spanish"] = ["spa", "es"],
            ["西班牙语"] = ["spa", "es"],
            ["西班牙語"] = ["spa", "es"],
            ["russian"] = ["rus", "ru"],
            ["俄语"] = ["rus", "ru"],
            ["俄語"] = ["rus", "ru"],
            ["italian"] = ["ita", "it"],
            ["意大利语"] = ["ita", "it"],
            ["意大利語"] = ["ita", "it"],
            ["portuguese"] = ["por", "pt"],
            ["葡萄牙语"] = ["por", "pt"],
            ["葡萄牙語"] = ["por", "pt"]
        };

    /// <summary>
    /// Converts a priority list to the mpv option value, or null when nothing usable was
    /// given. Tokens are split on <c>&gt;</c>、<c>＞</c>、<c>→</c>、<c>,</c>、<c>，</c>、<c>、</c> and
    /// matched against the known names; anything unrecognized is kept as a raw mpv code.
    /// </summary>
    public static string? ToMpvValue(string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority)) return null;

        var codes = new List<string>(8);
        foreach (var token in priority.Split([">", "＞", "→", ",", "，", "、", ";", "；"], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0) continue;

            if (Known.TryGetValue(trimmed, out var mapped))
            {
                foreach (var code in mapped)
                {
                    if (!ContainsIgnoreCase(codes, code)) codes.Add(code);
                }
            }
            else if (!ContainsIgnoreCase(codes, trimmed))
            {
                // Unknown token: the user typed an mpv code themselves, so it passes
                // through verbatim.
                codes.Add(trimmed);
            }
        }

        return codes.Count == 0 ? null : string.Join(",", codes);
    }

    private static bool ContainsIgnoreCase(List<string> codes, string value) =>
        codes.Any(code => string.Equals(code, value, StringComparison.OrdinalIgnoreCase));
}