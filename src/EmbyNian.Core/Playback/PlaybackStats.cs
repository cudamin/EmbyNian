using System.Globalization;

namespace EmbyNian.Playback;

/// <summary>One line of the 统计 panel: what it is called, and what it currently reads.</summary>
public readonly record struct PlaybackStatRow(string Label, string Value);

/// <summary>
/// The 统计 panel's content: which mpv properties it reads and what it makes of them.
/// <para>
/// The panel is drawn by the client rather than by mpv's own <c>stats.lua</c>, and not by choice. The
/// bundled <c>libmpv-2.dll</c> is compiled without Lua, so <c>script-binding
/// stats/display-stats-toggle</c> names a binding that was never registered — the dll's string table
/// still carries the script names, which is what made that dead end look alive. Reading the same
/// properties ourselves also gets the panel onto the external <c>mpv.exe</c> backend, where a script
/// running inside mpv would have drawn into mpv's own window instead of ours.
/// </para>
/// <para>
/// Split from the panel that shows it because the interesting half is the formatting — 「1920×1080」
/// out of two properties, a bare byte count as 「2.4 MB/s」, an absent property as no row at all — and
/// that half is testable without a player, a window or a file. <see cref="Fields"/> is the list of
/// properties to ask for; <see cref="Format"/> turns the answers into rows.
/// </para>
/// </summary>
public static class PlaybackStats
{
    /// <summary>How often the panel refreshes itself, in milliseconds.</summary>
    public const int RefreshMilliseconds = 1000;

    /// <summary>
    /// Every mpv property the panel needs, deduplicated — several rows are built from more than one.
    /// The caller reads these and hands the answers back to <see cref="Format"/>.
    /// </summary>
    public static IReadOnlyList<string> Fields { get; } =
    [
        "time-pos",
        "duration",
        "speed",
        "demuxer-cache-duration",
        "cache-speed",
        "avsync",
        "video-codec",
        "audio-codec-name",
        "width",
        "height",
        "container-fps",
        "estimated-vf-fps",
        "video-bitrate",
        "audio-bitrate",
        "video-params/pixelformat",
        "hwdec-current",
        "current-vo",
        "frame-drop-count",
        "decoder-frame-drop-count",
        "audio-params/channel-count",
        "audio-params/samplerate"
    ];

    /// <summary>
    /// The panel's rows for one set of property readings. Keys absent from
    /// <paramref name="readings"/>, or present with a null or empty value, produce no row: mpv answers
    /// nothing at all for a property that does not apply to the file being played — there is no
    /// <c>audio-params</c> on a silent file and no <c>hwdec-current</c> under a software decoder — and
    /// a row reading 「未知」 is worse than no row, because it looks like a reading that failed.
    /// </summary>
    public static IReadOnlyList<PlaybackStatRow> Format(IReadOnlyDictionary<string, string?> readings)
    {
        var rows = new List<PlaybackStatRow>(13);

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(new PlaybackStatRow(label, value));
        }

        string? Text(string key) =>
            readings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        double? Number(string key) =>
            Text(key) is { } text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

        // 位置 carries the duration with it. Two clocks in one row rather than two rows, because the
        // pair is read as a fraction and a reader comparing rows would have to find them first.
        Add("位置", Number("time-pos") is { } position
            ? Number("duration") is { } duration && duration > 0
                ? $"{Clock(position)} / {Clock(duration)}"
                : Clock(position)
            : null);

        Add("倍速", Number("speed") is { } speed
            ? speed.ToString("0.00", CultureInfo.InvariantCulture) + "×"
            : null);

        // 缓存: how many seconds are read ahead, and how fast they are arriving. The speed only exists
        // while something is actually being fetched, so a fully buffered local file shows the seconds
        // alone rather than 「0 B/s」, which would read as a stall.
        Add("缓存", Number("demuxer-cache-duration") is { } cache
            ? Number("cache-speed") is { } feed && feed > 0
                ? $"{cache.ToString("0.0", CultureInfo.InvariantCulture)} 秒 · {Rate(feed)}"
                : $"{cache.ToString("0.0", CultureInfo.InvariantCulture)} 秒"
            : null);

        // Signed on purpose: which way audio is drifting is the whole of what this number is for.
        Add("音视频同步", Number("avsync") is { } sync
            ? sync.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture) + " 秒"
            : null);

        Add("编码", Join(" · ", Text("video-codec"), Text("audio-codec-name")));

        Add("分辨率", Number("width") is { } width && Number("height") is { } height && width > 0 && height > 0
            ? $"{(int)width}×{(int)height}"
            : null);

        // The container's declared rate and what the filter chain is really managing. Only one row when
        // they agree to a hundredth, which is the ordinary case and not worth two numbers.
        var declared = Number("container-fps");
        var actual = Number("estimated-vf-fps");
        Add("帧率", declared is { } source
            ? actual is { } measured && Math.Abs(measured - source) >= 0.01
                ? $"{source.ToString("0.###", CultureInfo.InvariantCulture)} → {measured.ToString("0.##", CultureInfo.InvariantCulture)}"
                : source.ToString("0.###", CultureInfo.InvariantCulture)
            : actual?.ToString("0.##", CultureInfo.InvariantCulture));

        Add("码率", Join(" · ",
            Number("video-bitrate") is { } videoRate && videoRate > 0 ? "视频 " + BitRate(videoRate) : null,
            Number("audio-bitrate") is { } audioRate && audioRate > 0 ? "音频 " + BitRate(audioRate) : null));

        Add("像素格式", Text("video-params/pixelformat"));

        // mpv answers "no" rather than nothing when it decoded in software, and 「硬件解码：no」 is not a
        // sentence. The row says which it was either way, because that is the question being asked.
        Add("硬件解码", Text("hwdec-current") is { } decoder
            ? decoder is "no" or "No" ? "软件解码" : decoder
            : null);

        Add("视频输出", Text("current-vo"));

        // Two counters, and they mean different things: the output dropped frames it had decoded to
        // keep up with the clock, the decoder dropped them before that. Always shown once either is
        // readable, including at zero — 「丢帧 0」 is the reassuring answer to why playback stutters.
        var late = Number("frame-drop-count");
        var early = Number("decoder-frame-drop-count");
        Add("丢帧", late is null && early is null
            ? null
            : $"输出 {(long)(late ?? 0)} · 解码 {(long)(early ?? 0)}");

        Add("声道与采样率", Join(" · ",
            Number("audio-params/channel-count") is { } channels && channels > 0 ? $"{(int)channels} 声道" : null,
            Number("audio-params/samplerate") is { } rate && rate > 0
                ? (rate / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " kHz"
                : null));

        return rows;
    }

    /// <summary>
    /// Clock for the panel. Its own rather than <c>TimeFormat.Clock</c>'s, because a statistics readout
    /// wants the tenth of a second: this is the number someone lines a subtitle track up against.
    /// </summary>
    private static string Clock(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds / 100}"
            : $"{value.Minutes}:{value.Seconds:00}.{value.Milliseconds / 100}";
    }

    /// <summary>Bytes per second as the panel writes it. 1024, matching the rest of the client.</summary>
    private static string Rate(double bytesPerSecond)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        var value = bytesPerSecond;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>
    /// Bits per second as the panel writes it. Decimal thousands, and deliberately not the 1024 of
    /// <see cref="Rate"/>: a bit rate has always been quoted in decimal kilobits, so a file everyone
    /// else calls 8000 kbps must not read as 7812 here.
    /// </summary>
    private static string BitRate(double bitsPerSecond) => bitsPerSecond >= 1_000_000
        ? (bitsPerSecond / 1_000_000).ToString("0.00", CultureInfo.InvariantCulture) + " Mbps"
        : (bitsPerSecond / 1_000).ToString("0", CultureInfo.InvariantCulture) + " kbps";

    /// <summary>Joins the parts that have something to say, or nothing at all when none of them do.</summary>
    private static string? Join(string separator, params string?[] parts)
    {
        var present = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return present.Length == 0 ? null : string.Join(separator, present);
    }
}
