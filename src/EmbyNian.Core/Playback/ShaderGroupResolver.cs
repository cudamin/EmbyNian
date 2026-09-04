using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Mpv;

namespace EmbyNian.Playback;

/// <summary>The shader chain chosen for one playback, and why — the reason is shown in the launch log.</summary>
/// <param name="Animated">
/// Whether the item's own metadata looked animated, which is not the same as 「用了动画那半张表」: this stays
/// true when 自动识别动画 is switched off, and when a hand-picked 档位 named the 真人 half instead. 去色带 =
/// 「在动画中开启」 reads it, so that one decision about what the item *is* serves every rule that cares —
/// otherwise a video could be 动画 for the shader chain and live action for the deband setting.
/// </param>
/// <param name="Measure">
/// The factor and tier this decision was made at. Fed back in as the 「current tier」 when the window changes
/// size, which is what makes the boundaries sticky; the tier here is the <b>measured</b> one, which a
/// hand-picked override deliberately does not move.
/// </param>
public readonly record struct ShaderDecision(
    ShaderGroup? Group,
    string Reason,
    bool Animated = false,
    UpscaleMeasure Measure = default)
{
    public static readonly ShaderDecision None = new(null, "未启用自动着色器");

    public bool HasGroup => Group is not null;

    public override string ToString() => Group is null ? Reason : $"{Group.Name}（{Reason}）";
}


/// <summary>
/// Decides which shader chain to apply to a playback.
/// <para>
/// Four inputs and no more: what the picture is (Emby's genres and tags say 动画 or not), how far it has to
/// be enlarged (<see cref="ShaderTier.Measure"/> over the source's dimensions and the output's), whether
/// the source is DVD-era (<see cref="ShaderTier.IsVintage"/>) and how fast its frames arrive
/// (<see cref="ShaderTier.IsFastMotion"/>). The table itself is <see cref="ShaderGroupCatalog"/>, and the 8K
/// exception, the 显卡档 column and the hand-picked override live on <see cref="ShaderAutomationSettings"/>.
/// </para>
/// <para>
/// The chains are applied as ordinary mpv options. That is what makes them work identically on the external
/// mpv.exe, which no longer reads any config file, and on the in-process libmpv, which has no profile support
/// at all.
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
    /// a documentary called 《动画简史》 would then get the animated chain, and over-sharpened live action
    /// looks far worse than an animated film without its own chain.
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

    /// <param name="output">
    /// The size the picture will be drawn at, in physical pixels. <c>default</c> (0×0) means nobody could say,
    /// which <see cref="ShaderTier.Measure"/> reads as 微放大档. Where it comes from and when it is re-read is
    /// <see cref="OutputWatch"/>'s business.
    /// </param>
    /// <param name="current">
    /// The tier already in force, when this is a re-measurement after the window changed size — see
    /// <see cref="ShaderTier.Hysteresis"/>. Null for a fresh playback.
    /// </param>
    public ShaderDecision Resolve(
        EmbyItem item,
        MediaSource? source,
        EmbyItem? parent = null,
        (int Width, int Height) output = default,
        UpscaleTier? current = null)
    {
        var video = source?.PrimaryVideoStream;

        // The raw detection, with the 自动识别动画 switch left out of it: that switch decides whether the
        // animated *half of the table* is used, and ShaderAutomationSettings.Resolve applies it itself.
        // Everything else that asks 「这是动画吗」 — 去色带 = 「在动画中开启」 — wants the answer, not the switch.
        var animated = LooksAnimated(StyleHints(item, parent));

        var (group, reason, measure) = settings.Resolve(
            animated,
            video?.Width ?? 0,
            video?.Height ?? 0,
            output.Width,
            output.Height,
            video?.FrameRate ?? 0,
            current);

        var decision = new ShaderDecision(group, reason, animated, measure);
        Log.Debug(Category, $"《{item.Name}》着色器决策：{decision}");
        return decision;
    }
}
