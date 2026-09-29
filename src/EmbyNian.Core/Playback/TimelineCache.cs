using System.Text.Json;

namespace EmbyNian.Playback;

/// <summary>The demuxer's seekable islands, not a prefix ending at its furthest read position.</summary>
public sealed record TimelineCache(
    IReadOnlyList<TimelineRange> Ranges,
    bool BeginningCached = false,
    bool EndCached = false,
    bool EndOfFile = false,
    double? Ahead = null)
{
    public static TimelineCache Empty { get; } = new([]);

    public static TimelineCache Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return Empty;
        var ranges = new List<TimelineRange>();
        if (value.TryGetProperty("seekable-ranges", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray().Take(4096))
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var start = Number(item, "start") ?? 0;
                var end = Number(item, "end") ?? double.PositiveInfinity;
                if (end > start) ranges.Add(new(start, end));
            }
        }
        return new(ranges.ToArray(), Flag(value, "bof-cached"), Flag(value, "eof-cached"),
            Flag(value, "eof"), Number(value, "cache-duration"));
    }

    public bool IsVisible(double duration, string mode, bool network) =>
        double.IsFinite(duration) && duration > 0 && (Ranges.Count > 0 || mode == "yes" || (mode == "auto" && network));

    public double? BufferedSeconds(double speed) => !EndOfFile && Ahead is >= 0 && double.IsFinite(Ahead.Value)
        ? Ahead / (double.IsFinite(speed) && speed > 0 ? speed : 1) : null;

    public IReadOnlyList<TimelineRange> Uncached(double duration, string mode, bool network)
    {
        if (!IsVisible(duration, mode, network)) return [];
        var cached = Ranges.Where(range => double.IsFinite(range.Start) && !double.IsNaN(range.End) && range.End > range.Start)
            .Select(range => new TimelineRange(Math.Clamp(range.Start, 0, duration), Math.Clamp(range.End, 0, duration)))
            .Where(range => range.End > range.Start).OrderBy(range => range.Start).ToArray();
        if (cached.Length > 0)
        {
            if (BeginningCached) cached[0] = cached[0] with { Start = 0 };
            if (EndCached) cached[^1] = cached[^1] with { End = duration };
        }

        var gaps = new List<TimelineRange>();
        var edge = 0d;
        foreach (var range in cached)
        {
            AddGap(edge, range.Start);
            edge = Math.Max(edge, range.End);
        }
        AddGap(edge, duration);
        return gaps;

        void AddGap(double start, double end)
        {
            if (end <= start) return;
            if (gaps.Count > 0 && gaps[^1].End + 0.5 > start)
                gaps[^1] = gaps[^1] with { End = end };
            else if (end - start > 0.5) gaps.Add(new(start, end));
        }
    }

    public bool DiffersFrom(TimelineCache? other)
    {
        if (other is null || BeginningCached != other.BeginningCached || EndCached != other.EndCached
            || EndOfFile != other.EndOfFile || Ahead.HasValue != other.Ahead.HasValue || Ranges.Count != other.Ranges.Count)
            return true;
        if (Ahead is { } ahead && other.Ahead is { } old && Math.Abs(ahead - old) >= 0.5) return true;
        for (var i = 0; i < Ranges.Count; i++)
            if (Math.Abs(Ranges[i].Start - other.Ranges[i].Start) >= 0.25
                || Math.Abs(Ranges[i].End - other.Ranges[i].End) >= 0.25) return true;
        return false;
    }

    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var item)
        && item.ValueKind == JsonValueKind.True;

    private static double? Number(JsonElement value, string name) => value.TryGetProperty(name, out var item)
        && item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}
