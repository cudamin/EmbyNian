using System.Globalization;

namespace Momoka.Mpv;

/// <summary>A menu response is meaningful only to the exact menu snapshot that created it.</summary>
public sealed class VideoMenuSnapshot<T>(IReadOnlyList<T> items)
{
    private readonly T[] _items = [.. items];
    private readonly string _id = Guid.NewGuid().ToString("N");

    public string ValueAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _items.Length);
        return _id + ":" + (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    public bool TryResolve(string value, out T item)
    {
        item = default!;
        if (!TryParse(value, out var id, out var index) || id != _id || index > _items.Length) return false;
        item = _items[index - 1];
        return true;
    }

    public static bool IsSelection(string value) => TryParse(value, out _, out _);

    private static bool TryParse(string value, out string id, out int index)
    {
        id = "";
        index = 0;
        if (value.Length < 34 || value[32] != ':' || !Guid.TryParseExact(value.AsSpan(0, 32), "N", out _)) return false;
        if (!int.TryParse(value.AsSpan(33), NumberStyles.None, CultureInfo.InvariantCulture, out index)
            || index is < 1 or > 100000) return false;
        id = value[..32];
        return true;
    }
}
