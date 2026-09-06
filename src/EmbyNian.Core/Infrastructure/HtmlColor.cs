using System.Globalization;

namespace EmbyNian.Infrastructure;

/// <summary>
/// HTML 颜色代码 as this client stores and shows it: <c>#RRGGBB</c>, uppercase, with an empty string
/// meaning 「不设置，跟随 mpv 自己的默认」. The RGB↔HSV arithmetic the picker on the settings page is
/// drawn from lives here too, because that is a judgment with one right answer per input and Core is
/// where such a thing can be pinned by a test.
/// <para>
/// The reference for both halves is the HTML 颜色选择器 the user pointed at (rapidtables): type or
/// pick a colour, read it back as R/G/B, H/S/V and the six-digit code. Its numbers are the ones the
/// tests hold this file to — #AC5D5D is H 0°, S 46%, V 67% — so the picker on screen and the page it
/// was modelled on answer the same question the same way.
/// </para>
/// </summary>
public static class HtmlColor
{
    /// <summary>Strictly <c>#RRGGBB</c> (or the same without the hash): six hex digits, nothing else.</summary>
    public static bool TryParse(string? text, out int rgb)
    {
        rgb = 0;
        var value = (text ?? "").Trim().TrimStart('#');
        if (value.Length != 6) return false;
        if (!int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed)) return false;

        rgb = packed & 0xFFFFFF;
        return true;
    }

    /// <summary><c>#RRGGBB</c> in uppercase — the one spelling the settings file and the picker both show.</summary>
    public static string Format(int rgb) =>
        string.Create(CultureInfo.InvariantCulture, $"#{(rgb & 0xFFFFFF):X6}");

    /// <summary>Red 0–255, green 0–255, blue 0–255 out of a packed <c>RRGGBB</c>.</summary>
    public static (byte R, byte G, byte B) Rgb(int rgb) =>
        ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

    /// <summary>Packed <c>RRGGBB</c> out of the three channels.</summary>
    public static int Pack(byte r, byte g, byte b) => (r << 16) | (g << 8) | b;

    /// <summary>Packed <c>RRGGBB</c> straight out of <see cref="FromHsv"/>'s answer.</summary>
    public static int Pack((byte R, byte G, byte B) rgb) => Pack(rgb.R, rgb.G, rgb.B);

    /// <summary>
    /// RGB as hue 0–360, saturation 0–100 and value 0–100 — the H/S/V rows of the picker. H is round
    /// rather than truncated so pure red stays 0 and 360 never appears; S and V round half up, which is
    /// what makes #AC5D5D come out 46/67 the way the reference page shows it.
    /// </summary>
    public static (int H, int S, int V) ToHsv(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        var hue = delta == 0
            ? 0
            : max switch
            {
                _ when max == r => 60 * (g - b) / (double)delta,
                _ when max == g => 60 * (2 + (b - r) / (double)delta),
                _ => 60 * (4 + (r - g) / (double)delta)
            };

        // 360 and 0 are the same colour; the reference page spells pure red 0°.
        hue %= 360;
        if (hue < 0) hue += 360;

        return ((int)Math.Round(hue),
            max == 0 ? 0 : (int)Math.Round(delta * 100.0 / max),
            (int)Math.Round(max * 100.0 / 255.0));
    }

    /// <summary>The colour H 0–360, S 0–100, V 0–100 names, with each axis clamped into range.</summary>
    public static (byte R, byte G, byte B) FromHsv(int h, int s, int v)
    {
        var hue = ((h % 360) + 360) % 360;
        var saturation = Math.Clamp(s, 0, 100) / 100.0;
        var value = Math.Clamp(v, 0, 100) / 100.0 * 255.0;

        var chroma = value * saturation;
        var sector = hue / 60.0;
        var second = chroma * (1 - Math.Abs(sector % 2 - 1));
        var match = value - chroma;

        var (r, g, b) = ((int)sector) switch
        {
            0 => (chroma, second, 0.0),
            1 => (second, chroma, 0.0),
            2 => (0.0, chroma, second),
            3 => (0.0, second, chroma),
            4 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second)
        };

        return ((byte)Math.Round(r + match), (byte)Math.Round(g + match), (byte)Math.Round(b + match));
    }
}
