using System.Globalization;

namespace EmbyNian.Mpv;

/// <summary>How a checkable 画面菜单 row reads mpv's reported value to decide whether it is the current one.</summary>
public enum PlayerMenuMatch
{
    /// <summary>Ticked when the property reads back exactly the row's value (hwdec, 声道布局, loop-file 的 inf).</summary>
    Equals,

    /// <summary>Ticked when the property reads truthy — mpv 的 flag 回 「yes」/「no」（抖动补偿, 反交错, 去色带…）.</summary>
    Bool,

    /// <summary>Ticked when the property reads a number above zero（裁切填充：panscan 0↔1）.</summary>
    Positive,

    /// <summary>
    /// Ticked when the property (a float aspect) matches the row's ratio; the row's 「no」 matches mpv 的关闭态
    /// （负数）. video-aspect-override 读回来是 「1.777778」 而不是 「16:9」，纯字符串比对永远命不中。
    /// </summary>
    Ratio
}

/// <summary>
/// What makes one 画面菜单 row 「the current one」, so the menu can tick it: which mpv property to read, how to
/// read the answer, and whether the row is one of a mutually-exclusive set (<see cref="Radio"/> — a WinUI
/// <c>RadioMenuFlyoutItem</c> vs a <c>ToggleMenuFlyoutItem</c>; uosc highlights either the same way).
/// <para>
/// Explicit, not read off <see cref="PlayerMenuNode.Commands"/>, because the verb does not tell you which value
/// is current: <c>cycle</c> drives both a boolean（抖动补偿）and a multi-value rotate（色域映射模式）, and only
/// some <c>set</c> rows are a radio choice. So the checkable rows say what they are; every other row carries no
/// state and stays unticked（rotates, 视频滤镜 链开关, 数值微调, 纯动作）.
/// </para>
/// </summary>
public sealed record PlayerMenuState(string Property, PlayerMenuMatch Match, string? Value = null, bool Radio = false)
{
    /// <summary>Whether the row is current, given the raw string mpv reports（null/空 = 读不到 = 不打勾）.</summary>
    public bool IsChecked(string? current)
    {
        if (string.IsNullOrEmpty(current)) return false;

        return Match switch
        {
            PlayerMenuMatch.Equals => string.Equals(current, Value, StringComparison.OrdinalIgnoreCase),
            PlayerMenuMatch.Bool => IsTruthy(current),
            PlayerMenuMatch.Positive => TryNumber(current, out var number) && number > 0.0001,
            PlayerMenuMatch.Ratio => RatioMatches(current),
            _ => false
        };
    }

    private static bool IsTruthy(string value) =>
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || (TryNumber(value, out var number) && Math.Abs(number) > 0.0001);

    private bool RatioMatches(string current)
    {
        if (!TryNumber(current, out var number)) return false;
        if (string.Equals(Value, "no", StringComparison.OrdinalIgnoreCase)) return number <= 0;
        return number > 0 && TryRatio(Value, out var want) && Math.Abs(number - want) <= 0.01;
    }

    private static bool TryRatio(string? text, out double ratio)
    {
        ratio = 0;
        if (string.IsNullOrEmpty(text)) return false;

        var colon = text.IndexOf(':');
        if (colon < 0) return TryNumber(text, out ratio);

        // 「16:9」→ 16/9; both sides must be positive numbers or it is not a ratio.
        if (TryNumber(text[..colon], out var width) && TryNumber(text[(colon + 1)..], out var height) && height > 0)
        {
            ratio = width / height;
            return true;
        }

        return false;
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

