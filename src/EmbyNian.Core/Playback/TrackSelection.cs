using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Mpv;

namespace EmbyNian.Playback;

/// <summary>When subtitles are turned on by themselves.</summary>
public enum SubtitleMode
{
    /// <summary>Always show the best match from the language priority list.</summary>
    Always,

    /// <summary>Only a track marked forced — signs and songs over foreign-language audio.</summary>
    ForcedOnly,

    /// <summary>Only when the audio is not already in one of the preferred subtitle languages.</summary>
    ForeignAudioOnly,

    /// <summary>Never; start with subtitles off.</summary>
    Off
}

/// <summary>
/// The subtitle decision: a stream to select, or nothing. <see cref="Disabled"/> separates 「本片没有
/// 合适的字幕，交给 mpv」 from 「明确不要字幕」 — the first leaves mpv's own <c>slang</c> matching in
/// charge, the second passes <c>--sid=no</c>.
/// </summary>
public readonly record struct SubtitleChoice(MediaStream? Stream, bool Disabled)
{
    public static SubtitleChoice None => new(null, false);

    public static SubtitleChoice Off => new(null, true);
}

/// <summary>What the client would start this file with if the user picked nothing.</summary>
public readonly record struct AutoTracks(MediaStream? Audio, SubtitleChoice Subtitle);

/// <summary>
/// Picks the starting audio and subtitle track from the user's preferences.
/// <para>
/// This runs in the client rather than being left to mpv's own <c>alang</c>/<c>slang</c> matching for
/// two reasons: mpv cannot tell 简体 from 繁体 when both tracks are labelled <c>chi</c> and only their
/// titles differ, and the index the client reports back to Emby has to be the track that is actually
/// playing. mpv still receives the language lists as a second line of defence.
/// </para>
/// </summary>
public static class TrackSelection
{
    /// <summary>The tracks <paramref name="source"/> should start with under <paramref name="settings"/>.</summary>
    public static AutoTracks Resolve(PlaybackSettings settings, MediaSource source)
    {
        var audio = ChooseAudio(settings, source);
        return new AutoTracks(audio, ChooseSubtitle(settings, source, audio));
    }

    /// <summary>
    /// The first language in <see cref="PlaybackSettings.AudioLanguages"/> that the file actually has, or
    /// whatever is marked default. A file with none of the listed languages falls back to the default rather
    /// than to silence.
    /// <para>
    /// 「音轨不需要优先级，选默认或者单选特定语言就好」 was the earlier answer here, and it was one language for
    /// that reason. It has since become a list for the same reason the subtitle side is one: 「日语 &gt; 粤语 &gt;
    /// 英语」 is a real preference and a single slot cannot hold it. A one-language list is that list's
    /// degenerate case, so nothing about the single-language behaviour changed.
    /// </para>
    /// </summary>
    public static MediaStream? ChooseAudio(PlaybackSettings settings, MediaSource source)
    {
        var streams = source.AudioStreams.ToList();
        if (streams.Count == 0) return null;

        var formatRules = settings.AudioFormatRules;

        foreach (var language in settings.AudioLanguages)
        {
            var matches = streams.Where(stream => TrackPreference.LanguageMatches(stream, language)).ToList();
            // 格式这一关只在同一语言内部作用：语言优先级仍是第一位的，选中的这批日语轨里再按编码／声道挑，
            // 而不是让一条英语 Atmos 越过日语。语言全没匹配时才落到下面的兜底。
            if (matches.Count > 0) return BestByFormat(matches, formatRules, source.DefaultAudioStreamIndex);
        }

        // 语言全对不上（或压根没设语言）：格式偏好照样作用于全部音轨那一批。
        return BestByFormat(streams, formatRules, source.DefaultAudioStreamIndex);
    }

    /// <summary>
    /// 格式这一关：从同一语言（或兜底）选出的那批音轨里，按格式规则打分留下最高的那些，再交给
    /// <see cref="TrackPreference.Best"/> 按默认轨定夺。打分的文本是轨的编码／声道信息拼起来的
    /// （TrueHD、DTS-HD、Atmos、7.1、5.1 这些词落在那里），和字幕的 <see cref="BestByTitle"/> 同一套
    /// <see cref="KeywordFilter"/>，只是取的字段不同。
    /// </summary>
    private static MediaStream? BestByFormat(IReadOnlyList<MediaStream> pool, IReadOnlyList<KeywordRule>? rules, int? defaultIndex)
    {
        var ranked = KeywordFilter.Rank(pool, rules, FormatText);
        return TrackPreference.Best(ranked.ToList(), defaultIndex);
    }

    /// <summary>
    /// 一条音轨用来匹配格式关键词的文本：编码、profile、声道布局和标题拼在一起。声道数额外补成
    /// 「8ch」「6ch」，因为服务器有时只给声道数不给「7.1」这样的布局名，而用户按 7.1／5.1 的说法设词。
    /// 大小写、子串匹配由 <see cref="KeywordFilter"/> 负责，这里只管把该看的字段凑齐。
    /// </summary>
    private static string FormatText(MediaStream stream)
    {
        var parts = new List<string>(5);
        if (!string.IsNullOrWhiteSpace(stream.Codec)) parts.Add(stream.Codec!);
        if (!string.IsNullOrWhiteSpace(stream.Profile)) parts.Add(stream.Profile!);
        if (!string.IsNullOrWhiteSpace(stream.ChannelLayout)) parts.Add(stream.ChannelLayout!);
        if (stream.Channels is { } channels && channels > 0) parts.Add($"{channels}ch");
        if (!string.IsNullOrWhiteSpace(stream.Title)) parts.Add(stream.Title!);
        else if (!string.IsNullOrWhiteSpace(stream.DisplayTitle)) parts.Add(stream.DisplayTitle!);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// The first language in <see cref="PlaybackSettings.SubtitleLanguages"/> that the file actually
    /// has, subject to <see cref="PlaybackSettings.SubtitleMode"/>.
    /// </summary>
    public static SubtitleChoice ChooseSubtitle(PlaybackSettings settings, MediaSource source, MediaStream? audio)
    {
        if (settings.SubtitleMode == SubtitleMode.Off) return SubtitleChoice.Off;

        var streams = source.SubtitleStreams.ToList();
        if (streams.Count == 0) return SubtitleChoice.None;

        var languages = settings.SubtitleLanguages;

        // 仅在音频为外语时显示：听得懂的语言不需要字幕。
        if (settings.SubtitleMode == SubtitleMode.ForeignAudioOnly
            && audio is not null
            && languages.Any(language => TrackPreference.LanguageMatches(audio, language)))
        {
            return SubtitleChoice.Off;
        }

        var forcedOnly = settings.SubtitleMode == SubtitleMode.ForcedOnly;
        var pool = forcedOnly ? streams.Where(stream => stream.IsForced).ToList() : streams;

        // 只要强制字幕，而这个文件一条都没有：什么都不选，而不是退回整轨字幕。
        if (pool.Count == 0) return SubtitleChoice.Off;

        // 用户设的标题规则，外加隐式的那条：优先级里有简体中文时，自动把标题带「繁/繁体」的压成候补。这条策略
        // 留在这儿（字幕域），通用的合并动作交给 KeywordFilter.WithExclusions。
        var rules = settings.SubtitleTitleRules;
        if (languages.Any(name => TrackLanguagePriority.Canonical(name) == "简体中文"))
        {
            rules = [.. KeywordFilter.WithExclusions(rules, "繁", "繁体")];
        }

        foreach (var language in languages)
        {
            // 语言这一关只看语言字段，不看标题（includeTitle: false）——「先确定哪几条字幕是中文」。选中的这批再
            // 交给标题这一关，那才是「决定不要双语和特效」的地方。
            var matches = pool.Where(stream => TrackPreference.LanguageMatches(stream, language, includeTitle: false)).ToList();
            if (matches.Count == 0) continue;
            return new SubtitleChoice(BestByTitle(matches, rules, source.DefaultSubtitleStreamIndex, forcedOnly), false);
        }

        // No preferred language is present. Falling back keeps a file whose only subtitle track is
        // labelled in a language nobody listed from starting bare, which is what the older builds did.
        // 标题偏好照样作用于兜底那批：语言全对不上时，「不要双语/特效」仍算数。
        return settings.SubtitleFallbackToDefault
            ? new SubtitleChoice(BestByTitle(pool, rules, source.DefaultSubtitleStreamIndex, forcedOnly), false)
            : SubtitleChoice.Off;
    }

    /// <summary>
    /// 标题这一关：从同一语言（或兜底）选出的那批轨里，按标题规则打分留下最高的那些，再交给
    /// <see cref="TrackPreference.Best"/> 按默认轨/强制标记定夺。打分用轨的 <c>Title</c>，没有就退到 <c>DisplayTitle</c>
    /// —— 和语言那一关分家（见 <see cref="KeywordFilter"/>）。
    /// </summary>
    private static MediaStream? BestByTitle(IReadOnlyList<MediaStream> pool, IReadOnlyList<KeywordRule>? rules, int? defaultIndex, bool forcedOnly)
    {
        var ranked = KeywordFilter.Rank(pool, rules, stream => stream.Title ?? stream.DisplayTitle);
        return TrackPreference.Best(ranked.ToList(), defaultIndex, forcedOnly);
    }
}
