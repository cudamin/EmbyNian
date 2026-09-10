using EmbyNian.Emby;
using EmbyNian.Infrastructure;

namespace EmbyNian.Playback;

/// <summary>
/// What the user asked to play, after the track picker has run. A record so a caller can vary
/// one field of an existing ticket — "same episode, other audio track" — with <c>with</c>.
/// </summary>
public sealed record PlaybackTicket
{
    public required EmbyItem Item { get; init; }

    public required MediaSource Source { get; init; }

    /// <summary>Emby stream index of the chosen audio track; null lets mpv/alang decide.</summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>Emby stream index of the chosen subtitle track; null lets mpv/slang decide.</summary>
    public int? SubtitleStreamIndex { get; init; }

    /// <summary>Explicitly start with subtitles off, which is not the same as "no preference".</summary>
    public bool SubtitlesDisabled { get; init; }

    public long StartTicks { get; init; }

    /// <summary>The series row for an episode, so genre-based rules can see the show's metadata.</summary>
    public EmbyItem? Parent { get; init; }

    /// <summary>
    /// How large the picture will be drawn, in physical pixels — the other half of the 放大倍数 that decides
    /// the shader chain (<see cref="Mpv.ShaderTier.Measure"/>). 0 when nobody could say, which reads as
    /// 微放大档.
    /// <para>
    /// <b>The render target as it stands when playback starts</b> — the client area while windowed, the
    /// monitor while full screen. Where that number comes from when it cannot be read, and what happens to
    /// it when the window changes afterwards, is <see cref="ShaderSurface"/> and
    /// <see cref="OutputWatch"/>: a settled resize inside one tier only updates the recorded size, one that
    /// crosses a tier rebuilds the chain, and full screen swaps to the plan prepared at launch.
    /// </para>
    /// <para>
    /// The two readings that were tried and dropped: the monitor at full screen always (a film playing in a
    /// quarter of the screen then gets a chain built for all of it, and wasted GPU on an iGPU drops frames
    /// for the whole film) and re-measuring on every <c>WM_SIZE</c> (the chain recompiles dozens of times a
    /// second while an edge is dragged). What is here is the first with a 400 ms debounce over it.
    /// </para>
    /// <para>
    /// Getting it wrong is survivable in one direction: ravu, ArtCNN, SSimDownscaler and SSimSuperRes each
    /// gate on <c>OUTPUT</c> versus their own input, so a chain filed under the wrong tier stands aside
    /// rather than upscaling a picture that is being shrunk.
    /// </para>
    /// </summary>
    public int OutputWidth { get; init; }

    /// <inheritdoc cref="OutputWidth"/>
    public int OutputHeight { get; init; }

    /// <summary>
    /// The refresh rate of the screen the picture will be drawn on, in Hz. 0 when nobody could say, which every
    /// rule that reads it treats as 「不知道」 rather than as a number.
    /// <para>
    /// Only <see cref="Mpv.MpvOutputOptions.ResolveSync"/> wants it, and only to decide whether 显示同步 (and with
    /// it 插值) is worth its cost on this screen — the threshold it is compared against is
    /// <see cref="Configuration.VideoSettings.HighRefreshRateLimitHz"/>, and the measurement behind that is on
    /// that field.
    /// </para>
    /// <para>
    /// <b>Read once, at launch.</b> Dragging the window to a 60 Hz screen mid-film does not re-decide it, unlike
    /// the output size, which <see cref="OutputWatch"/> follows. That is deliberate: <c>video-sync</c> can be set
    /// at runtime, but switching it mid-playback re-clocks audio and video, and a second sync mode arriving from a
    /// window drag is a worse surprise than a stale one. Starting the next episode picks up the new screen.
    /// </para>
    /// </summary>
    public double DisplayRefreshHz { get; init; }
}

/// <summary>
/// What the user settled on before mpv is launched, when they were given the chance to settle on
/// anything. Separate from <see cref="PlaybackTicket"/> because the shell still has to resolve the
/// parent series and the shader group afterwards, and null everywhere the play came from a poster —
/// which is most of them.
/// </summary>
public sealed record PlaybackChoice(
    MediaSource Source,
    int? AudioStreamIndex,
    int? SubtitleStreamIndex,
    bool SubtitlesDisabled,
    long StartTicks);

/// <summary>
/// What the most recent playback was launched with, kept for the diagnostics page after that playback
/// has ended. <see cref="Options"/> is the exact list mpv was given, in order.
/// </summary>
public sealed record LaunchRecord(
    DateTimeOffset StartedAt,
    string Title,
    string? ShaderProfile,
    string? ShaderReason,
    string? QualityPreset,
    IReadOnlyList<KeyValuePair<string, string>> Options);

/// <summary>
/// A fully resolved launch description: everything needed to start playback, with no Emby or
/// settings types left in it. This is the seam a libmpv backend would consume unchanged.
/// </summary>
public sealed record PlaybackRequest
{
    public required Uri MediaUrl { get; init; }

    /// <summary>Shown by mpv as the media title instead of the opaque stream URL.</summary>
    public required string Title { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> HttpHeaders { get; init; } = [];

    public double StartSeconds { get; init; }

    public int? AudioId { get; init; }

    public int? SubtitleId { get; init; }

    public bool SubtitlesDisabled { get; init; }

    /// <summary>Server-extracted subtitle files, in the order mpv will number them.</summary>
    public IReadOnlyList<Uri> ExternalSubtitles { get; init; } = [];

    /// <summary>
    /// mpv <c>--slang</c> value derived from the settings' subtitle-language priority, or
    /// null for none. Only consulted when no explicit subtitle was chosen.
    /// </summary>
    public string? SubtitleLanguage { get; init; }

    /// <summary>mpv <c>--alang</c> value, same rules as <see cref="SubtitleLanguage"/>.</summary>
    public string? AudioLanguage { get; init; }

    /// <summary>
    /// The 着色器档位 applied, for the log and for the player's own menu. The options it consists of are
    /// already part of <see cref="PlayerOptions"/>; this is the label, not the mechanism.
    /// </summary>
    public string? ShaderProfile { get; init; }

    /// <summary>
    /// How many entries at the end of <see cref="PlayerOptions"/> came from the shader chain. What
    /// <c>PlaybackService.SetShaderGroupAsync</c> needs to answer 「这个选项在挂上着色器之前是什么值」 when a
    /// chain is switched mid-film: everything before this many entries is the baseline, the 画质预设 and the
    /// settings page.
    /// <para>
    /// Carried rather than recomputed. It used to be worked out by looking the chain up again by its label,
    /// which is not an identity — two cells of the table share a name across 显卡档 columns, and the count of
    /// options differs between them.
    /// </para>
    /// </summary>
    public int ShaderOptionCount { get; init; }

    /// <summary>
    /// Plain mpv <c>name=value</c> options: the client's own baseline, then the 画质预设 and the
    /// 视频输出 / 音频输出 / 字幕外观 settings, then the chosen shader group — later entries overriding
    /// earlier ones, which is how mpv itself resolves a repeated option. There is no config file behind
    /// them any more, and (since v5) no 附加参数 after them: what is not here is mpv's own compiled-in
    /// default.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> PlayerOptions { get; init; } = [];

    public long RunTimeTicks { get; init; }

    public string ItemId { get; init; } = "";

    public string? MediaSourceId { get; init; }

    /// <summary>Emby stream indexes, kept for the playback reports rather than for mpv.</summary>
    public int? AudioStreamIndex { get; init; }

    public int? SubtitleStreamIndex { get; init; }

    public string? ShaderReason { get; init; }
}

public enum PlaybackEndReason
{
    /// <summary>mpv exited without saying why — the IPC channel was off or already gone.</summary>
    Unknown,
    /// <summary>Played to the end.</summary>
    EndOfFile,
    /// <summary>The user closed mpv or pressed q.</summary>
    UserQuit,
    /// <summary>The client asked mpv to stop, e.g. because the app is shutting down.</summary>
    Stopped,
    /// <summary>mpv could not play the file.</summary>
    Error
}

public sealed record PlaybackExit(PlaybackEndReason Reason, double? PositionSeconds, int ExitCode, string? Message)
{
    public bool IsFailure => Reason == PlaybackEndReason.Error;
}

/// <summary>
/// One entry of mpv's <c>track-list</c>, used to rebuild the track pickers while playback runs.
/// The id is mpv's own track id, which is what <c>aid</c>/<c>sid</c> want — not the Emby stream
/// index the launch ticket carries.
/// <para>
/// Everything past <c>Selected</c> is there to be shown rather than acted on. The pickers used to
/// carry the language code alone, which made a file with three Chinese audio tracks a list of three
/// identical rows: what tells them apart is the codec, the channel layout and the bitrate.
/// </para>
/// </summary>
public sealed record MpvTrack(int Id, string Type, string? Language, string? Title, bool Default, bool Selected)
{
    /// <summary>mpv's own codec name — <c>eac3</c>, <c>hdmv_pgs_subtitle</c> — not a pretty one.</summary>
    public string? Codec { get; init; }

    /// <summary>mpv's <c>demux-channels</c> layout string, e.g. <c>5.1(side)</c>. Audio only.</summary>
    public string? Channels { get; init; }

    /// <summary>mpv's <c>demux-channel-count</c>; 0 when unknown.</summary>
    public int ChannelCount { get; init; }

    /// <summary>Hertz, from <c>demux-samplerate</c>; 0 when unknown.</summary>
    public int SampleRate { get; init; }

    /// <summary>Bits per second, from <c>demux-bitrate</c>; 0 when unknown.</summary>
    public int BitRate { get; init; }

    /// <summary>A subtitle track meant to be burned in over foreign dialogue only.</summary>
    public bool Forced { get; init; }

    /// <summary>Loaded from a separate file rather than found in the container.</summary>
    public bool External { get; init; }

    /// <summary>A picture-based subtitle (PGS, VobSub): it cannot be restyled or scaled by mpv.</summary>
    public bool Image { get; init; }

    public bool HearingImpaired { get; init; }

    public bool IsAudio => Type == "audio";

    public bool IsSubtitle => Type == "sub";

    /// <summary>
    /// The shortest useful name for the track: the language, or the title when there is none, and an
    /// empty string when neither exists. This is what the collapsed 音频/字幕 buttons show, where there
    /// is room for about a dozen characters.
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            var language = TrackLanguagePriority.Describe(Language);
            if (language.Length > 0) return language;
            return string.IsNullOrWhiteSpace(Title) ? "" : Title!.Trim();
        }
    }

    /// <summary>
    /// The row's own text in a picker: the language and the track's title, then the flags that change
    /// what choosing it means. Empty when mpv told us nothing at all about the track, which is the
    /// caller's cue to fall back to 「轨道 n」.
    /// </summary>
    public string DisplayName
    {
        get
        {
            var parts = new List<string>(3);
            var language = TrackLanguagePriority.Describe(Language);
            if (language.Length > 0) parts.Add(language);

            // Only when it says something the language did not: muxers routinely set the title to the
            // language's own name — 「英语 · English」 is one word of information written twice — so a
            // title that merely names the same language again is dropped. Both are put through the
            // same lookup to catch 「English」, 「eng」 and 「英語」 alike.
            var title = (Title ?? "").Trim();
            var names = title.Length > 0 && language.Length > 0
                && TrackLanguagePriority.Describe(title).Equals(language, StringComparison.OrdinalIgnoreCase);
            if (title.Length > 0 && !names) parts.Add(title);

            var name = string.Join(" · ", parts);
            if (name.Length == 0) return "";

            if (Default) name += "（默认）";
            if (Forced) name += "（强制）";
            if (HearingImpaired) name += "（听障）";
            if (External) name += "（外挂）";
            return name;
        }
    }

    /// <summary>
    /// What the track technically is, for the faint right-hand column of a picker row: the codec, then
    /// the channel layout, then the bitrate for audio or the 图形/文本 distinction for subtitles.
    /// <para>
    /// The bitrate falls back to the sample rate rather than being shown next to it. Both would be
    /// four items on one row of a panel a few hundred pixels wide, and the one that tells two
    /// otherwise identical tracks apart is the bitrate.
    /// </para>
    /// </summary>
    public string DisplayDetail
    {
        get
        {
            var parts = new List<string>(3);
            var codec = CodecLabel();
            if (codec.Length > 0) parts.Add(codec);

            if (IsAudio)
            {
                var layout = ChannelLabel();
                if (layout.Length > 0) parts.Add(layout);

                if (BitRate > 0) parts.Add($"{Math.Max(1, BitRate / 1000)} kbps");
                else if (SampleRate > 0) parts.Add(SampleRateLabel());
            }
            else if (IsSubtitle && Image)
            {
                parts.Add("图形");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// mpv's codec name, spelled the way a player usually spells it. Anything unrecognised is
    /// upper-cased and left alone rather than hidden: an unfamiliar codec name is still information.
    /// </summary>
    private string CodecLabel()
    {
        var codec = (Codec ?? "").Trim();
        if (codec.Length == 0) return "";

        var known = codec.ToLowerInvariant() switch
        {
            "eac3" or "e-ac-3" => "E-AC3",
            "ac3" => "AC3",
            "truehd" => "TrueHD",
            "dts" => "DTS",
            "dtshd" or "dts-hd" => "DTS-HD",
            "aac" => "AAC",
            "opus" => "Opus",
            "vorbis" => "Vorbis",
            "flac" => "FLAC",
            "alac" => "ALAC",
            "mp3" => "MP3",
            "mp2" => "MP2",
            "subrip" or "srt" => "SRT",
            "ass" => "ASS",
            "ssa" => "SSA",
            "webvtt" or "vtt" => "WebVTT",
            "mov_text" => "MOV 文本",
            "hdmv_pgs_subtitle" or "pgs" => "PGS",
            "dvd_subtitle" or "vobsub" => "VobSub",
            "dvb_subtitle" => "DVB",
            "dvb_teletext" => "图文电视",
            "eia_608" or "cc" => "CC",
            "text" => "文本",
            _ => ""
        };

        // PCM comes in a dozen spellings (pcm_s16le, pcm_f32be …) and the tail is of no interest here.
        if (known.Length == 0 && codec.StartsWith("pcm", StringComparison.OrdinalIgnoreCase)) known = "PCM";

        return known.Length > 0 ? known : codec.ToUpperInvariant();
    }

    /// <summary>
    /// The channel layout in the words a listener uses. mpv's own string is preferred for anything
    /// unusual — it is the only one that can describe <c>5.1(side)</c> — but the ordinary cases are
    /// named, because 「立体声」 reads and 「2 声道」 does not.
    /// </summary>
    private string ChannelLabel() => ChannelCount switch
    {
        1 => "单声道",
        2 => "立体声",
        6 => "5.1 声道",
        8 => "7.1 声道",
        > 0 => $"{ChannelCount} 声道",
        _ => (Channels ?? "").Trim()
    };

    private string SampleRateLabel()
    {
        var kilohertz = SampleRate / 1000d;
        return kilohertz % 1 == 0 ? $"{kilohertz:0} kHz" : $"{kilohertz:0.#} kHz";
    }
}

/// <summary>The outcome the UI shows once mpv is gone.</summary>
public sealed record PlaybackResult(PlaybackExit Exit, long PositionTicks, bool MarkedWatched, bool ProgressReported)
{
    public string ToChinese() => Exit.Reason switch
    {
        PlaybackEndReason.EndOfFile => MarkedWatched ? "播放完毕，已标记为已观看" : "播放完毕",
        PlaybackEndReason.UserQuit => MarkedWatched ? "已退出播放，已标记为已观看" : "已退出播放",
        PlaybackEndReason.Stopped => "播放已停止",
        PlaybackEndReason.Error => $"播放失败：{Exit.Message}",
        _ => "播放结束"
    };
}

/// <summary>
/// Everything the player chrome draws, as one immutable snapshot. It exists because the bar used
/// to ask the backend for each value separately, once every five seconds: the clock only moved
/// on progress reports and the seek bar had nothing to draw between them. The backend now pushes
/// this whole record whenever mpv says something changed, so the UI never polls and never shows a
/// value that came from a different moment than the one next to it.
/// </summary>
public readonly record struct PlayerStatus
{
    /// <summary>
    /// A struct's field initializers only run for <c>new PlayerStatus()</c>, never for
    /// <c>default</c>; every status in this codebase starts from this constructor, so the
    /// "nothing known yet" values below are the ones the UI actually sees.
    /// </summary>
    public PlayerStatus()
    {
    }

    /// <summary>Seconds into the file; negative means "not known yet".</summary>
    public double Position { get; init; } = -1;

    public double Duration { get; init; }

    /// <summary>How far the demuxer has read ahead, in seconds from the start of the file.</summary>
    public double CacheEnd { get; init; }

    public bool Paused { get; init; }

    /// <summary>Stalled waiting for data — drawn differently from a user pause.</summary>
    public bool Buffering { get; init; }

    public bool Muted { get; init; }

    /// <summary>mpv's own scale, where 100 is unattenuated.</summary>
    public double Volume { get; init; } = 100;

    public double Speed { get; init; } = 1;

    /// <summary>Set once mpv has the file open; before that the bar shows 「正在打开…」.</summary>
    public bool Loaded { get; init; }

    public bool HasPosition => Position >= 0;

    public bool HasDuration => Duration > 0.05;

    public double Fraction => HasDuration && HasPosition ? Math.Clamp(Position / Duration, 0, 1) : 0;

    public double CacheFraction => HasDuration ? Math.Clamp(CacheEnd / Duration, 0, 1) : 0;

    public string PositionClock => TimeFormat.Clock(TimeSpan.FromSeconds(Math.Max(0, Position)));

    public string DurationClock => TimeFormat.Clock(TimeSpan.FromSeconds(Math.Max(0, Duration)));

    /// <summary>
    /// True when the two snapshots differ in anything the chrome draws. The position is compared
    /// at a quarter of a second, which is finer than the bar can show and far coarser than mpv's
    /// per-frame notifications — without it every frame would cost a marshalled UI update.
    /// </summary>
    public bool DiffersFrom(PlayerStatus other) =>
        Paused != other.Paused
        || Buffering != other.Buffering
        || Muted != other.Muted
        || Loaded != other.Loaded
        || HasPosition != other.HasPosition
        || Math.Abs(Volume - other.Volume) > 0.5
        || Math.Abs(Speed - other.Speed) > 0.005
        || Math.Abs(Duration - other.Duration) > 0.05
        || Math.Abs(Position - other.Position) >= 0.25
        || Math.Abs(CacheEnd - other.CacheEnd) >= 1;
}
