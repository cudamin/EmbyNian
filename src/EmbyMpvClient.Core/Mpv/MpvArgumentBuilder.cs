using System.Globalization;
using EmbyMpvClient.Playback;

namespace EmbyMpvClient.Mpv;

/// <summary>
/// Turns a <see cref="PlaybackRequest"/> into an mpv argument list.
/// <para>
/// Returns a list rather than one command line on purpose: it is handed to
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>, which does the Windows
/// quoting itself. v1 concatenated a string and quoted by hand, so a path containing a space or
/// a subtitle title containing a quote produced an unlaunchable command.
/// </para>
/// </summary>
public static class MpvArgumentBuilder
{
    /// <param name="ipcPipe">
    /// Full pipe path for <c>--input-ipc-server</c>, or null for no control channel. Passed
    /// separately because the pipe is the backend's business, not part of what to play.
    /// </param>
    public static IReadOnlyList<string> Build(PlaybackRequest request, string? ipcPipe = null)
    {
        var arguments = new List<string>(24);

        // --include has to come first: mpv reads it in place, so a --profile that names a group
        // defined in that file would otherwise be resolved before the file has been seen.
        if (!string.IsNullOrWhiteSpace(request.IncludeFile))
            arguments.Add($"--include={request.IncludeFile}");

        // Applied after mpv.conf, which is what lets it override the global profile= line
        // without touching the user's config.
        if (!string.IsNullOrWhiteSpace(request.ShaderProfile))
            arguments.Add($"--profile={request.ShaderProfile}");

        if (request.OverrideMpvResume)
        {
            // The server owns the playback position and the track picker owns the tracks. Left
            // on, mpv's watch_later file would restore its own start/aid/sid for this URL and
            // silently disagree with both.
            arguments.Add("--resume-playback=no");
            arguments.Add("--save-position-on-quit=no");
        }

        // Always explicit, including 0: "start from the beginning" must not turn into
        // "wherever mpv last stopped".
        arguments.Add($"--start={Seconds(request.StartSeconds)}");

        // The user's mpv.conf sets idle=yes so a manually launched mpv survives the end of a
        // file. Here the process lifetime is the playback session, so it has to exit.
        arguments.Add("--idle=no");
        arguments.Add("--keep-open=no");

        if (request.AudioId is { } audioId) arguments.Add($"--aid={audioId}");

        if (request.SubtitlesDisabled) arguments.Add("--sid=no");
        else if (request.SubtitleId is { } subtitleId) arguments.Add($"--sid={subtitleId}");

        // Language priorities are only consulted when no explicit track was chosen; mpv
        // matches the list in order, so the first entry is the most preferred.
        if (!string.IsNullOrWhiteSpace(request.AudioLanguage)) arguments.Add($"--alang={request.AudioLanguage}");
        if (!string.IsNullOrWhiteSpace(request.SubtitleLanguage)) arguments.Add($"--slang={request.SubtitleLanguage}");

        if (!string.IsNullOrWhiteSpace(request.SubtitleFont)) arguments.Add($"--sub-font={request.SubtitleFont}");

        foreach (var subtitle in request.ExternalSubtitles)
            arguments.Add($"--sub-file={subtitle.AbsoluteUri}");

        foreach (var (name, value) in request.HttpHeaders)
        {
            // -append adds one item without the comma escaping the plain list option needs, so
            // a header value containing a comma cannot split into two broken headers.
            arguments.Add($"--http-header-fields-append={name}: {value}");
        }

        if (!string.IsNullOrWhiteSpace(request.Title))
            arguments.Add($"--force-media-title={request.Title}");

        if (!string.IsNullOrWhiteSpace(ipcPipe))
            arguments.Add($"--input-ipc-server={ipcPipe}");

        arguments.AddRange(request.ExtraArguments);

        // Everything after -- is a file, so a URL that happens to start with a dash cannot be
        // parsed as an option.
        arguments.Add("--");
        arguments.Add(request.MediaUrl.AbsoluteUri);

        return arguments;
    }

    /// <summary>Same list with the Emby token replaced, for the log.</summary>
    public static IReadOnlyList<string> Redact(IEnumerable<string> arguments) =>
        [.. arguments.Select(argument =>
            argument.StartsWith("--http-header-fields-append=X-Emby-Token:", StringComparison.OrdinalIgnoreCase)
                ? "--http-header-fields-append=X-Emby-Token: ***"
                : argument)];

    private static string Seconds(double value) =>
        Math.Max(0, value).ToString("0.###", CultureInfo.InvariantCulture);
}
