using System.Globalization;

namespace EmbyNian.Theming;

/// <summary>
/// 一个颜色，不带任何 UI 依赖。
/// <para>
/// 为什么不用 <c>Windows.UI.Color</c>：主题的定义要能在没有窗口的地方读、写、算对比度，而
/// <c>EmbyNian.Core</c> 一个 UI 引用都不收。所以这里是四个字节加一点色彩数学，外壳那边再翻成
/// 框架的 <c>Color</c> —— 一次转换，换来主题这件事整份可以拿单元测试压。
/// </para>
/// </summary>
public readonly record struct ThemeColor(byte A, byte R, byte G, byte B)
{
    /// <summary>不透明的一个颜色。</summary>
    public static ThemeColor Rgb(byte r, byte g, byte b) => new(0xFF, r, g, b);

    /// <summary>
    /// <c>#RRGGBB</c> 或 <c>#AARRGGBB</c>，井号可省。主题目录里写的就是这个形式，所以解析失败是抛异常
    /// 而不是回退到某个颜色：一个拼错的色值应该在测试里当场炸掉，而不是变成界面上一块说不清来历的灰。
    /// </summary>
    public static ThemeColor Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        var text = hex.AsSpan().Trim();
        if (text.Length > 0 && text[0] == '#') text = text[1..];

        if (text.Length is not (6 or 8))
            throw new FormatException($"颜色 「{hex}」 既不是 #RRGGBB 也不是 #AARRGGBB");

        var value = uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return text.Length == 6
            ? new ThemeColor(0xFF, (byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : new ThemeColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>始终八位，因为读这个字符串的是自检报告，那里要看得见 alpha。</summary>
    public string ToHex() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    public ThemeColor WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>
    /// 朝 <paramref name="other"/> 挪 <paramref name="amount"/>（0 到 1），alpha 保持自己的。
    /// <para>
    /// 直接在 sRGB 上线性插值，不转到线性光再插。手调出来的那套深色面就是这么一档一档叠上去的，
    /// 感知上更均匀的算法反而对不上原来的值；而这里要的是「可预测」，不是「最正确」。
    /// </para>
    /// </summary>
    public ThemeColor Mix(ThemeColor other, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return this with
        {
            R = Lerp(R, other.R, t),
            G = Lerp(G, other.G, t),
            B = Lerp(B, other.B, t)
        };

        static byte Lerp(byte from, byte to, double t) => (byte)Math.Clamp(Math.Round(from + (to - from) * t), 0, 255);
    }

    /// <summary>WCAG 的相对亮度。alpha 不参与 —— 半透明色压在什么上面，这里无从知道。</summary>
    public double Luminance =>
        0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);

    /// <summary>
    /// WCAG 对比度，1 到 21。正文要 4.5 以上，大字号和图标要 3 以上 —— 主题目录的测试就是拿这两条
    /// 数字压的，不然「几套主题」很容易变成「其中两套读不了」。
    /// </summary>
    public static double Contrast(ThemeColor a, ThemeColor b)
    {
        var high = Math.Max(a.Luminance, b.Luminance);
        var low = Math.Min(a.Luminance, b.Luminance);
        return (high + 0.05) / (low + 0.05);
    }

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    public override string ToString() => ToHex();
}
