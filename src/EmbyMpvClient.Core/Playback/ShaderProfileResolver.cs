using EmbyMpvClient.Configuration;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Emby;

namespace EmbyMpvClient.Playback;

/// <summary>The shader group chosen for one item, and why — the reason is shown in the launch log.</summary>
public readonly record struct ShaderDecision(string? Profile, string Reason)
{
    public static readonly ShaderDecision None = new(null, "未启用自动着色器");

    public bool HasProfile => !string.IsNullOrEmpty(Profile);

    public override string ToString() => HasProfile ? $"{Profile}（{Reason}）" : Reason;
}

/// <summary>
/// Decides which mpv 「着色器配置组」 to activate for an item.
/// <para>
/// Three rules, in priority order: an animated item gets the anime group; a source at least as
/// tall as the high-resolution threshold gets the light group, because on a 1440p screen a 4K
/// source is only ever downscaled and every upscaling shader in the chain would be dead weight;
/// anything else gets the default group. The group is applied with a command-line
/// <c>--profile=</c>, which mpv processes after mpv.conf, so nothing in the user's config has to
/// be rewritten and a manually launched mpv keeps behaving exactly as before.
/// </para>
/// </summary>
public sealed class ShaderProfileResolver(ShaderAutomationSettings settings)
{
    private const string Category = "shader";

    /// <summary>
    /// Style hints for an item. Episodes usually carry no genres of their own, so the caller
    /// passes the parent series as <paramref name="fallback"/>.
    /// </summary>
    public static IEnumerable<string> StyleHints(EmbyItem item, EmbyItem? fallback = null)
    {
        foreach (var hint in Hints(item)) yield return hint;
        if (fallback is null) yield break;
        foreach (var hint in Hints(fallback)) yield return hint;
    }

    /// <summary>
    /// Only the metadata fields Emby shows as 类型/风格 and 标签. Deliberately not the title:
    /// a documentary called 《动画简史》 would then get Anime4K, and over-sharpened live action
    /// looks far worse than an animated film without its shader group.
    /// </summary>
    private static IEnumerable<string> Hints(EmbyItem item)
    {
        foreach (var genre in item.Genres) yield return genre;
        foreach (var tag in item.Tags) yield return tag;
    }

    public bool LooksAnimated(IEnumerable<string> styleHints)
    {
        var keywords = settings.AnimeKeywords.Where(keyword => !string.IsNullOrWhiteSpace(keyword)).ToList();
        if (keywords.Count == 0) return false;

        foreach (var hint in styleHints)
        {
            if (string.IsNullOrWhiteSpace(hint)) continue;
            foreach (var keyword in keywords)
            {
                if (hint.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }

    public ShaderDecision Resolve(EmbyItem item, MediaSource? source, EmbyItem? parent = null)
    {
        var height = source?.PrimaryVideoStream?.Height;
        var animated = settings.AutoAnimeProfile && LooksAnimated(StyleHints(item, parent));
        var profile = settings.Resolve(animated, height);

        var decision = new ShaderDecision(profile, Explain(animated, height, profile));
        Log.Debug(Category, $"《{item.Name}》着色器决策：{decision}");
        return decision;
    }

    private string Explain(bool animated, int? height, string? profile)
    {
        if (profile is null)
        {
            return settings is { ApplyToAllVideos: false, AutoAnimeProfile: false }
                ? "两个开关都未启用"
                : "未命中任何规则，沿用 mpv.conf 自身的配置组";
        }

        if (height >= settings.HighResThresholdHeight && profile == settings.HighResProfile.Trim())
            return $"片源 {height}p 高于 {settings.HighResThresholdHeight}p，只会缩小，改用省电组";

        return animated ? "元数据风格含动画关键词" : "已对所有视频启用";
    }
}
