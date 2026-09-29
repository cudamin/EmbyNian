namespace EmbyNian.Infrastructure;

/// <summary>RGB is authoritative for exact input; rounded HSV is only used when an HSV control is edited.</summary>
public sealed class HtmlColorSelection
{
    public int Rgb { get; private set; } = 0xFFFFFF;
    public int Hue { get; private set; }
    public int Saturation { get; private set; }
    public int Value { get; private set; } = 100;
    public string Hex => HtmlColor.Format(Rgb);

    public bool SetHex(string? text)
    {
        if (!HtmlColor.TryParse(text, out var rgb)) return false;
        SetRgb(rgb);
        return true;
    }

    public void SetRgb(int rgb)
    {
        Rgb = rgb & 0xFFFFFF;
        var (r, g, b) = HtmlColor.Rgb(Rgb);
        var (h, s, v) = HtmlColor.ToHsv(r, g, b);
        Hue = h % 360;
        Saturation = s;
        Value = v;
    }

    public void SetHsv(int hue, int saturation, int value)
    {
        Hue = ((hue % 360) + 360) % 360;
        Saturation = Math.Clamp(saturation, 0, 100);
        Value = Math.Clamp(value, 0, 100);
        Rgb = HtmlColor.Pack(HtmlColor.FromHsv(Hue, Saturation, Value));
    }
}
