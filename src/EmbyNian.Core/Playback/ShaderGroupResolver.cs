using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Mpv;

namespace EmbyNian.Playback;

/// <summary>The shader group chosen for one item, and why — the reason is shown in the launch log.</summary>
/// <param name="Animated">
/// Whether the item's own metadata looked animated, which is not the same as 「用了动画配置组」: this stays
/// true when 自动动画配置组 is switched off, and it is false for a 4K animated film that the
/// high-resolution rule sent to the light group. 去色带 = 「在动画中开启」 reads it, so that one decision
/// about what the item *is* serves every rule that cares — otherwise a video could be 动画 for the
/// shader chain and live action for the deband setting.
/// </param>
public readonly record struct ShaderDecision(string? Group, string Reason, bool Animated = false)
{
    public static readonly ShaderDecision None = new(null, "未启用自动着色器");

    public bool HasGroup => !string.IsNullOrEmpty(Group);

    public override string ToString() => HasGroup ? $"{Group}（{Reason}）" : Reason;
}

/// <summary>
/// Decides which 「着色器配置组」 to apply to an item.
/// <para>
/// Rules in priority order: an 8K source gets none at all, because nothing on an iGPU decodes 8K and
/// runs a shader chain at the same time; an animated item gets the anime group; a source at least as
/// tall as the high-resolution threshold gets the light group, since on a 1440p screen a 4K source is
/// only ever downscaled and every upscaling shader in the chain would be dead weight; a source at or
/// below the low-resolution threshold gets the heavier group, which is where a 2–3× upscale finally
/// earns its cost; anything else gets the default group.
/// </para>
/// <para>
/// The groups themselves are shipped C# data (<see cref="ShaderGroupCatalog"/>) and are applied as
/// ordinary mpv options. That is what makes them work identically on the external mpv.exe, which no
/// longer reads any config file, and on the in-process libmpv, which has no profile support at all.
/// </para>
/// </summary>
public sealed class ShaderGroupResolver(ShaderAutomationSettings settings)
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
        var video = source?.PrimaryVideoStream;
        var width = video?.Width;
        var height = video?.Height;

        // The raw detection, with the 自动动画配置组 switch left out of it: that switch decides whether the
        // anime *group* is used, and ShaderAutomationSettings.Resolve applies it itself. Everything else
        // that asks 「这是动画吗」 — 去色带 = 「在动画中开启」 — wants the answer, not the switch.
        var animated = LooksAnimated(StyleHints(item, parent));
        var group = settings.Resolve(animated, width, height);

        var decision = new ShaderDecision(group, Explain(animated && settings.AutoAnimeProfile, width, height, group), animated);
        Log.Debug(Category, $"《{item.Name}》着色器决策：{decision}");
        return decision;
    }

    private string Explain(bool animated, int? width, int? height, string? group)
    {
        if (group is null)
        {
            if (settings.DisableForUltraHighRes &&
                (width >= ShaderAutomationSettings.UltraHighResWidth || height >= 3000))
                return "片源接近 8K，着色器只会拖慢解码，已全部关闭";

            return settings is { ApplyToAllVideos: false, AutoAnimeProfile: false }
                ? "两个开关都未启用"
                : "未命中任何规则，本次不应用配置组";
        }

        if (height >= settings.HighResThresholdHeight && group == settings.HighResProfile.Trim())
            return $"片源 {height}p 高于 {settings.HighResThresholdHeight}p，只会缩小，改用省电组";

        if (height is > 0 && height <= settings.LowResThresholdHeight && group == settings.LowResProfile.Trim())
            return $"片源 {height}p 不高于 {settings.LowResThresholdHeight}p，放大倍数够大，改用增强组";

        return animated ? "元数据风格含动画关键词" : "已对所有视频启用";
    }
}
