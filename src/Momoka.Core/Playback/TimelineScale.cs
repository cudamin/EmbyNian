namespace Momoka.Playback;

/// <summary>Seek geometry shared by pointer gestures, readouts and regression checks.</summary>
public static class TimelineScale
{
    public static double Fraction(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    public static double At(double x, double width) => width > 0 && double.IsFinite(width) ? Fraction(x / width) : 0;

    public static double Remaining(double fraction, double duration, double speed) =>
        double.IsFinite(duration) && duration > 0
            ? Math.Round(duration * (1 - Fraction(fraction)) / (double.IsFinite(speed) && speed > 0 ? speed : 1), 6)
            : 0;

    public static bool PreferKeyframes(double previous, double current, double duration, long elapsedMilliseconds) =>
        elapsedMilliseconds > 0 && duration > 0 && double.IsFinite(duration)
        && Math.Abs(Fraction(current) - Fraction(previous)) * duration * 1000 / elapsedMilliseconds > 30;

    public static string Clock(double seconds, double duration)
    {
        var negative = seconds < 0;
        var whole = (long)Math.Floor(double.IsFinite(seconds) ? Math.Min(Math.Abs(seconds), TimeSpan.MaxValue.TotalSeconds - 1) : 0);
        var body = duration >= 3600 ? $"{whole / 3600:00}:{whole / 60 % 60:00}:{whole % 60:00}"
            : duration >= 60 ? $"{whole / 60:00}:{whole % 60:00}" : $"{whole}";
        return negative ? "−" + body : body;
    }

    public static double TimeAt(double x, double width, double duration) =>
        double.IsFinite(duration) && duration > 0 && width > 1 ? At(x - 0.5, width - 1) * duration : 0;

    public static double XAt(double seconds, double width, double duration) =>
        width > 1 && double.IsFinite(duration) && duration > 0
            ? 0.5 + (width - 1) * Fraction(seconds / duration) : 0;

    public static double ChapterRadius(double height, double fullHeight) =>
        Math.Min(Math.Max(1, Math.Max(0, height - 1) * 0.8), Math.Max(fullHeight / 10, 3));

    public static int ChapterAt(double x, double y, double width, double height, double fullHeight,
        double duration, IReadOnlyList<TimelineChapter> chapters)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || width <= 1 || !double.IsFinite(duration) || duration <= 0) return -1;
        var radius = ChapterRadius(height, fullHeight) * 2;
        var nearest = radius * radius;
        var found = -1;
        for (var i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].Start > duration) continue;
            var dx = x - XAt(chapters[i].Start, width, duration);
            var distance = dx * dx + y * y;
            if (distance > nearest || found >= 0 && distance >= nearest) continue;
            nearest = distance;
            found = i;
        }
        return found;
    }

    public static double TextOpacity(double height, double fontSize = 18) =>
        Math.Clamp((height - fontSize * 0.8) / (fontSize * 0.4), 0, 1);

    public static double SnapChapter(double fraction, double width, double duration, IReadOnlyList<SkipChapter> chapters)
    {
        fraction = Fraction(fraction);
        if (width <= 0 || duration <= 0) return fraction;
        var closest = 6d;
        var result = fraction;
        foreach (var chapter in chapters)
        {
            if (!double.IsFinite(chapter.Start) || chapter.Start < 0 || chapter.Start >= duration) continue;
            var distance = Math.Abs(chapter.Start / duration - fraction) * width;
            if (distance > closest) continue;
            closest = distance;
            result = chapter.Start / duration;
        }
        return result;
    }
}
