using EmbyNian.Configuration;
using EmbyNian.Infrastructure;

namespace EmbyNian.Playback;

/// <summary>A glyph copy or a box behind one line of preview text.</summary>
public sealed record SubtitlePreviewLayer(double X, double Y, string Color, double Opacity,
    bool IsBox = false, double Padding = 0);

/// <summary>
/// A text-subtitle style sketch, not a libass raster. Box colors and roles follow mpv's
/// border styles; glyph metrics and physical screen size remain the UI renderer's approximation.
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
    public const int MpvDefaultFontSize = 38;
    public const double MpvDefaultBorderSize = 1.65;

    public double PlatePadding { get; init; }

    public static SubtitlePreviewPlan Plan(PlaybackSettings subtitles, double scale, string text)
    {
        var backColor = RgbOr(subtitles.SubtitleBackColor, "#000000");
        var backOpacity = Math.Clamp(subtitles.SubtitleBackOpacity, 0, 100) / 100.0;
        var style = (subtitles.SubtitleBackStyle ?? "").Trim();
        var lineBoxes = style.Equals("opaque-box", StringComparison.OrdinalIgnoreCase);
        var backgroundBox = style.Equals("background-box", StringComparison.OrdinalIgnoreCase);
        var effectiveScale = scale * Math.Clamp(subtitles.SubtitleScalePercent,
            PlaybackSettings.MinimumSubtitleScale, PlaybackSettings.MaximumSubtitleScale) / 100.0;
        var outline = Math.Max(0, Parse(subtitles.SubtitleBorderSize) ?? MpvDefaultBorderSize) * effectiveScale;
        var outlineColor = RgbOr(subtitles.SubtitleBorderColor, "#000000");
        var shadow = Math.Max(0, Parse(subtitles.SubtitleShadowOffset) ?? 0) * effectiveScale;
        var layers = new List<SubtitlePreviewLayer>();

        if (lineBoxes)
        {
            // BorderStyle=3 uses the outline color for each line's box and the back color for its shadow.
            if (shadow > 0) layers.Add(new(shadow, shadow, backColor, backOpacity, true, outline));
            if (outline > 0) layers.Add(new(0, 0, outlineColor, 1, true, outline));
        }
        else
        {
            if (shadow > 0 && !backgroundBox) layers.Add(new(shadow, shadow, backColor, backOpacity));
            if (outline > 0)
                foreach (var (x, y) in Ring(outline)) layers.Add(new(x, y, outlineColor, 1));
        }

        return new(
            FontFamilies.Resolve(subtitles.SubtitleFontFamily),
            (subtitles.SubtitleFontSize > 0 ? subtitles.SubtitleFontSize : MpvDefaultFontSize) * effectiveScale,
            subtitles.SubtitleBold,
            text,
            RgbOr(subtitles.SubtitleColor, "#FFFFFF"),
            layers,
            backgroundBox,
            backColor,
            backgroundBox ? backOpacity : 0)
        {
            PlatePadding = backgroundBox ? shadow : 0
        };
    }

    private static IEnumerable<(double X, double Y)> Ring(double radius)
    {
        for (var step = 0; step < 8; step++)
        {
            var angle = step * Math.PI / 4;
            yield return (Math.Round(radius * Math.Cos(angle), 3), Math.Round(radius * Math.Sin(angle), 3));
        }
    }

    private static string RgbOr(string? value, string fallback) =>
        HtmlColor.TryParse(value, out var rgb) ? HtmlColor.Format(rgb) : fallback;

    private static double? Parse(string? value) =>
        double.TryParse((value ?? "").Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : null;
}
