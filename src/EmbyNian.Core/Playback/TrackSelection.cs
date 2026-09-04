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

        foreach (var language in settings.AudioLanguages)
        {
            var matches = streams.Where(stream => TrackPreference.LanguageMatches(stream, language)).ToList();
            if (matches.Count > 0) return TrackPreference.Best(matches, source.DefaultAudioStreamIndex);
        }

        return TrackPreference.Best(streams, source.DefaultAudioStreamIndex);
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

        foreach (var language in languages)
        {
            var matches = pool.Where(stream => TrackPreference.LanguageMatches(stream, language)).ToList();
            if (matches.Count == 0) continue;
            return new SubtitleChoice(TrackPreference.Best(matches, source.DefaultSubtitleStreamIndex, forcedOnly), false);
        }

        // No preferred language is present. Falling back keeps a file whose only subtitle track is
        // labelled in a language nobody listed from starting bare, which is what the older builds did.
        return settings.SubtitleFallbackToDefault
            ? new SubtitleChoice(TrackPreference.Best(pool, source.DefaultSubtitleStreamIndex, forcedOnly), false)
            : SubtitleChoice.Off;
    }
}
