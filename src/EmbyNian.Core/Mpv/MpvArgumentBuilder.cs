using System.Globalization;
using EmbyNian.Playback;

namespace EmbyNian.Mpv;

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

        // The client sets every option it needs itself, and mpv.conf/input.conf are the user's own
        // player config: reading them meant the picture the settings page described was not the one on
        // screen, and a manual edit there could break playback here. Off it goes — first, so nothing
        // that follows can be a no-op because the file already said otherwise.
        //
        // mpv's built-in OSC, stats and select scripts and its default key bindings are compiled into
        // the binary, not loaded from the config directory, so an externally launched mpv still has its
        // own on-screen controls. What it loses is the user's uosc setup, because that is a Lua script
        // living under the config directory.
        //
        // watch_later files are blocked by this too — mpv counts them as configuration — so the client
        // is now the only thing that decides where a file starts, and mpv keeps no second record of the
        // position to disagree with the server's.
        arguments.Add("--no-config");

        // Belt and braces, as in the libmpv backend: --no-config already blocks the watch_later file,
        // and the client owning the start position is meant to be the rule rather than a side effect.
        arguments.Add("--resume-playback=no");
        arguments.Add("--save-position-on-quit=no");

        // Always explicit, including 0: "start from the beginning" must not turn into
        // "wherever mpv last stopped".
        arguments.Add($"--start={Seconds(request.StartSeconds)}");

        // mpv idles at the end of a file by default only when told to; here the process lifetime is
        // the playback session, so both of these have to be explicit.
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

        // 画质预设、视频输出 / 音频输出 settings and the 着色器配置组, in the order the planner assembled
        // them: mpv's last occurrence of an option wins, so the group has the last word over the preset.
        foreach (var (name, value) in request.PlayerOptions)
            arguments.Add($"--{name}={value}");

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
