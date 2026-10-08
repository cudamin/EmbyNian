using Momoka.Emby;
using Momoka.Mpv;

namespace Momoka.Playback;

/// <summary>Only the primary selections belong in Emby reports; local sidecars and unknown mappings have no server index.</summary>
internal sealed class PlaybackTrackState
{
    private readonly IReadOnlyList<(int Index, bool Audio)> _internal;
    private readonly IReadOnlyList<(int Index, Uri File)> _external;
    private readonly object _gate = new();
    private (int? Audio, int? Subtitle) _selection;
    private long _revision;

    internal PlaybackTrackState(MediaSource source, PlaybackRequest request)
    {
        _internal = [.. source.MediaStreams.Where(stream => !stream.IsExternal && (stream.IsAudio || stream.IsSubtitle))
            .Select(stream => (stream.Index, stream.IsAudio))];
        _external = [.. MpvTrackMap.Build(source).ExternalSubtitleIndexes.Zip(request.ExternalSubtitles,
            (index, file) => (index, file))];
        _selection = (request.AudioStreamIndex, request.SubtitlesDisabled ? -1 : request.SubtitleStreamIndex);
    }

    internal (int? Audio, int? Subtitle) Selection
    {
        get { lock (_gate) return _selection; }
    }

    internal long Revision
    {
        get { lock (_gate) return _revision; }
    }

    internal void Observe(IReadOnlyList<MpvTrack> tracks)
    {
        lock (_gate)
        {
            _revision++;
            Update(tracks);
        }
    }

    internal void ObserveInitial(IReadOnlyList<MpvTrack> tracks, long revision)
    {
        lock (_gate)
        {
            if (_revision != revision) return;
            _revision++;
            Update(tracks);
        }
    }

    private void Update(IReadOnlyList<MpvTrack> tracks)
    {
        if (tracks.Count == 0) return;
        _selection = (Resolve(tracks.Where(track => track.IsAudio).ToArray(), audio: true),
            Resolve(tracks.Where(track => track.IsSubtitle).ToArray(), audio: false));
    }

    private int? Resolve(IReadOnlyList<MpvTrack> tracks, bool audio)
    {
        var selected = tracks.Where(track => track.Selected).ToArray();
        if (selected.Length == 0) return -1;
        var primary = selected.Where(track => track.MainSelection == 0 || audio && track.MainSelection is null).ToArray();
        if (primary.Length != 1)
            return selected.All(track => track.MainSelection is > 0) ? -1 : null;
        var chosen = primary[0];
        if (chosen.External)
        {
            if (audio || !Uri.TryCreate(chosen.ExternalFilename, UriKind.Absolute, out var file)) return null;
            var matches = _external.Where(entry => Uri.Compare(entry.File, file, UriComponents.AbsoluteUri,
                UriFormat.UriEscaped, StringComparison.Ordinal) == 0).ToArray();
            return matches.Length == 1 ? matches[0].Index : null;
        }
        if (chosen.FfmpegIndex is not { } index) return null;
        return _internal.Count(entry => entry.Audio == audio && entry.Index == index) == 1 ? index : null;
    }
}
