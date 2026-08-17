using EmbyMpvClient.Emby;

namespace EmbyMpvClient.Mpv;

/// <summary>
/// Translates Emby stream indexes into mpv track ids.
/// <para>
/// The two numbering schemes are not the same and v1 passed one as the other, which is why
/// picking the third audio track sometimes selected the second. Emby's
/// <see cref="MediaStream.Index"/> counts every stream in the container — video, audio,
/// subtitle, attached pictures — while mpv's <c>--aid</c>/<c>--sid</c> count 1-based within a
/// single track type. External streams are not in the container at all: they have to be handed
/// to mpv as extra files, and mpv numbers those after the internal ones in the order they
/// appear on the command line.
/// </para>
/// </summary>
public sealed class MpvTrackMap
{
    private readonly Dictionary<int, int> _audioIds = [];
    private readonly Dictionary<int, int> _subtitleIds = [];

    private MpvTrackMap()
    {
    }

    /// <summary>Emby indexes of the subtitle streams that must be passed as <c>--sub-file</c>, in order.</summary>
    public List<int> ExternalSubtitleIndexes { get; } = [];

    /// <summary>
    /// Streams that exist on the server but cannot be handed to mpv, so the picker can grey
    /// them out instead of selecting something that silently does nothing.
    /// </summary>
    public List<int> UnavailableIndexes { get; } = [];

    public static MpvTrackMap Build(MediaSource source)
    {
        var map = new MpvTrackMap();

        var internalAudio = 0;
        var internalSubtitles = 0;

        // Container order, which is what mpv sees. Emby normally sends streams in index
        // order already, but a sort makes the mapping independent of that.
        foreach (var stream in source.MediaStreams.Where(stream => !stream.IsExternal).OrderBy(stream => stream.Index))
        {
            if (stream.IsAudio) map._audioIds[stream.Index] = ++internalAudio;
            else if (stream.IsSubtitle) map._subtitleIds[stream.Index] = ++internalSubtitles;
        }

        // mpv numbers files added on the command line after the container's own tracks, in the
        // order they appear — so this loop and the one that emits --sub-file must agree.
        foreach (var stream in source.MediaStreams.Where(stream => stream.IsExternal).OrderBy(stream => stream.Index))
        {
            // Emby can only serve a sidecar subtitle as a standalone file when it is text; an
            // external PGS/VobSub has no endpoint that returns something mpv can read.
            if (stream.IsSubtitle && stream.IsTextSubtitle)
            {
                map._subtitleIds[stream.Index] = ++internalSubtitles;
                map.ExternalSubtitleIndexes.Add(stream.Index);
            }
            else
            {
                // Including external audio: Emby exposes no endpoint that extracts one track,
                // so there is nothing to give --audio-file.
                map.UnavailableIndexes.Add(stream.Index);
            }
        }

        return map;
    }

    /// <summary>The <c>--aid</c> value for an Emby stream index, or null when it is not an audio stream.</summary>
    public int? AudioId(int? embyStreamIndex) => Lookup(_audioIds, embyStreamIndex);

    /// <summary>The <c>--sid</c> value for an Emby stream index, or null when it is not a subtitle stream.</summary>
    public int? SubtitleId(int? embyStreamIndex) => Lookup(_subtitleIds, embyStreamIndex);

    /// <summary>True when mpv can actually select this stream.</summary>
    public bool CanSelect(int embyStreamIndex) =>
        _audioIds.ContainsKey(embyStreamIndex) || _subtitleIds.ContainsKey(embyStreamIndex);

    private static int? Lookup(Dictionary<int, int> table, int? index) =>
        index is { } value && table.TryGetValue(value, out var id) ? id : null;
}

/// <summary>Chooses the audio and subtitle track to start with.</summary>
public static class TrackPreference
{
    /// <summary>
    /// Picks by language, honouring the server's own default as the tie-breaker. Language codes
    /// arrive in every shape (<c>chi</c>, <c>zho</c>, <c>zh-CN</c>, <c>Chinese</c>), so matching
    /// is a case-insensitive prefix test in both directions rather than equality.
    /// </summary>
    public static MediaStream? Choose(
        IEnumerable<MediaStream> streams,
        string? preferredLanguage,
        int? serverDefaultIndex,
        bool preferForced = false)
    {
        var candidates = streams.ToList();
        if (candidates.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredLanguage))
        {
            var matches = candidates.Where(stream => LanguageMatches(stream, preferredLanguage!)).ToList();
            if (matches.Count > 0) candidates = matches;
        }

        var forced = candidates.Where(stream => stream.IsForced == preferForced).ToList();
        if (forced.Count > 0) candidates = forced;

        return candidates.FirstOrDefault(stream => stream.Index == serverDefaultIndex)
               ?? candidates.FirstOrDefault(stream => stream.IsDefault)
               ?? candidates[0];
    }

    internal static bool LanguageMatches(MediaStream stream, string preferred)
    {
        var wanted = preferred.Trim();
        if (wanted.Length == 0) return false;

        foreach (var candidate in new[] { stream.Language, stream.DisplayLanguage })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var value = candidate.Trim();
            if (value.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) return true;
            if (wanted.StartsWith(value, StringComparison.OrdinalIgnoreCase)) return true;
            if (Equivalent(value, wanted)) return true;
        }

        return false;
    }

    /// <summary>The codes that mean the same language but share no common prefix.</summary>
    private static bool Equivalent(string left, string right)
    {
        string[][] groups =
        [
            ["chi", "zho", "zh", "chs", "cht", "chinese", "中文", "简体", "繁体"],
            ["jpn", "jap", "ja", "jp", "japanese", "日语", "日文"],
            ["eng", "en", "english", "英语", "英文"],
            ["kor", "ko", "korean", "韩语"]
        ];

        foreach (var group in groups)
        {
            var hasLeft = group.Any(code => left.StartsWith(code, StringComparison.OrdinalIgnoreCase));
            var hasRight = group.Any(code => right.StartsWith(code, StringComparison.OrdinalIgnoreCase));
            if (hasLeft && hasRight) return true;
        }

        return false;
    }
}
