using EmbyNian.Configuration;
using EmbyNian.Infrastructure;

namespace EmbyNian.Playback;

/// <summary>
/// One copy of the sample text in the 字幕示例 preview: an offset, a colour and an opacity. The outline
/// ring and the shadow are copies of the same line drawn behind the text itself, which is how this client
/// can show an outline without a text-rendering stack that has one — see
/// <see cref="SubtitlePreviewPlan"/>.
/// </summary>
public sealed record SubtitlePreviewLayer(double X, double Y, string Color, double Opacity);

/// <summary>
/// The 字幕示例 preview as it should be drawn, straight off the 字幕外观 settings — 「参考图2新增字幕外观
/// 功能」（2026-09-06）: the 字幕 card's top strip draws the sample line with the current font, size,
/// bold, colours, outline, shadow and plate, and redraws when any of those rows change.
/// <para>
/// A pure function so the mapping is pinned by unit tests, and the one place that answers 「这张卡上的
/// 十几行合在一起是什么样子」. It follows the card's own row semantics rather than libass internals:
/// 描边大小/描边颜色 draw the ring around the glyphs, 底板颜色 colours both the shadow and (when
/// <c>sub-border-style</c> names a box) the plate behind the line, and the opaque box hides the shadow
/// because a plate drawn at full opacity would cover it anyway. The one thing it deliberately does not
/// show is true size on screen — that depends on the video's resolution, so the caller picks a scale
/// (pixels per mpv unit) and the strip is a zoomed-in sketch, which the row's own note says.
/// </para>
/// <para>
/// The three constants below are mpv's own compiled-in defaults, each already written down beside the
/// setting it belongs to: 字号 38 (the 字号 row's note), 描边 1.65 (the 描边大小 list, measured off the
/// shipped libmpv's option table), and the plate/shadow black at about 69% opacity (the 底板颜色 comment
/// on <c>PlaybackSettings</c>). They are stated here as fallbacks for 「不设置」 so the preview never
/// draws nothing where playback would draw mpv's default.
/// </para>
/// </summary>
public sealed record SubtitlePreviewPlan(
    string FontFamily,
    double FontSize,
    bool Bold,
    string Text,
    string TextColor,
    IReadOnlyList<SubtitlePreviewLayer> Layers,
    bool Plate,
    string PlateColor,
    double PlateOpacity)
{
    /// <summary>
    /// mpv's own 字号 when the setting is 0 (「不指定，由 mpv 自己决定」). Public because the settings
    /// page's 字号 slider has to show <i>something</i> for a stored 0 — a slider cannot say 「不指定」 —
    /// and 38 is the value that renders identically.
    /// </summary>
    public const int MpvDefaultFontSize = 38;

    /// <summary>
    /// mpv's own 描边宽度 when the setting is 「不设置」, same story as <see cref="MpvDefaultFontSize"/>:
    /// the 描边大小 slider seeds a stored empty string with this, which is the same look under another name.
    /// </summary>
    public const double MpvDefaultBorderSize = 1.65;

    /// <summary>mpv's own 底板/阴影 black, to the opacity measured off the shipped libmpv.</summary>
    private const string MpvBackColor = "#000000";
    private const double MpvBackOpacity = 0.69;

    /// <summary>
    /// Builds the plan at <paramref name="scale"/> pixels per mpv unit. <paramref name="text"/> comes from
    /// the caller (the sample string lives with the UI that shows it); everything else is read off the
    /// settings this card writes.
    /// </summary>
    public static SubtitlePreviewPlan Plan(PlaybackSettings subtitles, double scale, string text)
    {
        // A plate colour the user picked carries the row's own 不透明度; 「不设置」 falls to mpv's own black,
        // which the same row's empty state has always meant.
        var backSet = HtmlColor.TryParse(subtitles.SubtitleBackColor, out var back);
        var backColor = backSet ? HtmlColor.Format(back) : MpvBackColor;
        var backOpacity = backSet ? Math.Clamp(subtitles.SubtitleBackOpacity, 0, 100) / 100.0 : MpvBackOpacity;

        var style = (subtitles.SubtitleBackStyle ?? "").Trim();

        // The opaque box is drawn at full opacity and replaces the shadow (a shadow behind a solid plate is
        // invisible anyway); the background box rides at the row's opacity and keeps the shadow beside it.
        // 「关闭」 draws neither and lets the outline and shadow carry the line, which is the look this
        // client has always shipped.
        var opaqueBox = style.Equals("opaque-box", StringComparison.OrdinalIgnoreCase);
        var backgroundBox = style.Equals("background-box", StringComparison.OrdinalIgnoreCase);
        var plate = opaqueBox || backgroundBox;

        var outline = Math.Max(0, Parse(subtitles.SubtitleBorderSize) ?? MpvDefaultBorderSize) * scale;
        var outlineColor = RgbOr(subtitles.SubtitleBorderColor, "#000000");
        var shadow = Math.Max(0, Parse(subtitles.SubtitleShadowOffset) ?? 0) * scale;

        var layers = new List<SubtitlePreviewLayer>();

        if (shadow > 0 && !opaqueBox)
            layers.Add(new(shadow, shadow, backColor, backOpacity));

        if (outline > 0)
        {
            foreach (var (x, y) in Ring(outline)) layers.Add(new(x, y, outlineColor, 1));
        }

        return new(
            FontFamilies.Resolve(subtitles.SubtitleFontFamily),
            Math.Max(0, subtitles.SubtitleFontSize > 0 ? subtitles.SubtitleFontSize : MpvDefaultFontSize) * scale,
            subtitles.SubtitleBold,
            text,
            RgbOr(subtitles.SubtitleColor, "#FFFFFF"),
            layers,
            plate,
            backColor,
            opaqueBox ? 1 : backgroundBox ? backOpacity : 0);
    }

    /// <summary>
    /// Eight equally-spaced offsets at <paramref name="radius"/>: the ring a drawn outline follows. Eight
    /// rather than four because a 0.5-unit hairline skipped over by a four-point compass shows gaps at the
    /// diagonals, and more than eight doubles the copies for a softness nobody can see at this size.
    /// </summary>
    private static IEnumerable<(double X, double Y)> Ring(double radius)
    {
        for (var step = 0; step < 8; step++)
        {
            var angle = step * Math.PI / 4;
            yield return (Math.Round(radius * Math.Cos(angle), 3), Math.Round(radius * Math.Sin(angle), 3));
        }
    }

    /// <summary>The stored value when it is a colour, otherwise mpv's own for that row.</summary>
    private static string RgbOr(string? value, string fallback) =>
        HtmlColor.TryParse(value, out var rgb) ? HtmlColor.Format(rgb) : fallback;

    private static double? Parse(string? value) =>
        double.TryParse((value ?? "").Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
