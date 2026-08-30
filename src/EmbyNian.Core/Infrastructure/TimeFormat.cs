namespace EmbyNian.Infrastructure;

public static class TimeFormat
{
    public const long TicksPerSecond = TimeSpan.TicksPerSecond;

    public static TimeSpan FromTicks(long? ticks) => ticks is null or <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(ticks.Value);

    public static long ToTicks(double seconds) => seconds <= 0 ? 0 : (long)(seconds * TicksPerSecond);

    public static double ToSeconds(long? ticks) => ticks is null or <= 0 ? 0 : ticks.Value / (double)TicksPerSecond;

    /// <summary>"1:23:45" for anything over an hour, "23:45" otherwise.</summary>
    public static string Clock(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    public static string Clock(long? ticks) => Clock(FromTicks(ticks));

    /// <summary>"2 小时 15 分" / "45 分钟" — for durations shown next to a title.</summary>
    public static string Duration(long? ticks)
    {
        var value = FromTicks(ticks);
        if (value == TimeSpan.Zero) return "";
        var hours = (int)value.TotalHours;
        var minutes = value.Minutes;
        if (hours > 0) return minutes > 0 ? $"{hours} 小时 {minutes} 分" : $"{hours} 小时";
        return minutes > 0 ? $"{minutes} 分钟" : $"{value.Seconds} 秒";
    }

    public static string FileSize(long? bytes)
    {
        if (bytes is null or <= 0) return "";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit <= 1 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
