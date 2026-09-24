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
    /// Full pipe path for <c>--input-ipc-server</c>. Required even when progress/control is disabled:
    /// authentication and loading always travel over the verified startup channel, never argv.
    /// </param>
    public static IReadOnlyList<string> Build(PlaybackRequest request, string ipcPipe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ipcPipe);
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

        // 播放统计（参考项目那一套统计项的中文覆盖版，见 MpvStats）。--no-config 让 mpv 不再读用户配置目录
        // 里那棵 scripts/，所以这一份必须自己交出去；`--script`（单数）重复出现是累加，不与别的脚本互相顶掉。
        // 装箱缺失只是没有统计面板，不是起播失败 —— 缺席即跳过。
        if (MpvStats.Exists(AppContext.BaseDirectory))
            arguments.Add($"--script={MpvStats.ScriptPath(AppContext.BaseDirectory)}");

        // Belt and braces, as in the libmpv backend: --no-config already blocks the watch_later file,
        // and the client owning the start position is meant to be the rule rather than a side effect.
        arguments.Add("--resume-playback=no");
        arguments.Add("--save-position-on-quit=no");

        // Wait for the first loadfile, then exit at EOF/error instead of leaving an empty window.
        // start travels over IPC immediately before that first load, not a command-line playlist entry.
        arguments.Add("--idle=once");
        arguments.Add("--keep-open=no");
        // mpv diagnostics may echo HTTP options. Do not create a log or expose its terminal output.
        arguments.Add("--terminal=no");

        if (request.AudioId is { } audioId) arguments.Add($"--aid={audioId}");

        if (request.SubtitlesDisabled) arguments.Add("--sid=no");
        else if (request.SubtitleId is { } subtitleId) arguments.Add($"--sid={subtitleId}");

        // Language priorities are only consulted when no explicit track was chosen; mpv
        // matches the list in order, so the first entry is the most preferred.
        if (!string.IsNullOrWhiteSpace(request.AudioLanguage)) arguments.Add($"--alang={request.AudioLanguage}");
        if (!string.IsNullOrWhiteSpace(request.SubtitleLanguage)) arguments.Add($"--slang={request.SubtitleLanguage}");

        // External subtitles are also deferred: they may need the same HTTP headers as the video.

        if (!string.IsNullOrWhiteSpace(request.Title))
            arguments.Add($"--force-media-title={request.Title}");

        arguments.Add($"--input-ipc-server={ipcPipe}");

        // Preserve the planner's last-value-wins order for picture/audio/shader options. Transport,
        // logging and preload options may not undo the startup boundary, even in a malformed request.
        foreach (var (name, value) in request.PlayerOptions)
        {
            if (OwnsStartup(name))
                throw new InvalidOperationException("播放器选项不能覆盖安全起播通道、认证或文件加载设置");
            arguments.Add($"--{name}={value}");
        }

        return arguments;
    }

    /// <summary>
    /// Ordered commands for a verified pipe. JSON arrays preserve commas and Unicode in HTTP headers
    /// and subtitle URLs; every command must succeed before the next one can be sent.
    /// </summary>
    public static IReadOnlyList<object?[]> LoadCommands(PlaybackRequest request) =>
    [
        ["set_property", "http-header-fields", request.HttpHeaders.Select(header => $"{header.Key}: {header.Value}").ToArray()],
        ["set_property", "options/sub-files", request.ExternalSubtitles.Select(uri => uri.AbsoluteUri).ToArray()],
        ["set_property", "options/start", Seconds(request.StartSeconds)],
        ["loadfile", request.MediaUrl.AbsoluteUri, "replace"]
    ];

    private static bool OwnsStartup(string name) =>
        name.StartsWith("http-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("input-ipc-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("sub-file", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("audio-file", StringComparison.OrdinalIgnoreCase) && name != "audio-file-auto"
        || name is "idle" or "keep-open" or "start" or "config" or "config-dir" or "include"
            or "playlist" or "log-file" or "terminal" or "msg-level" or "user-agent" or "referrer"
            or "cookies" or "cookies-file" or "resume-playback" or "save-position-on-quit";

    private static string Seconds(double value) =>
        Math.Max(0, value).ToString("0.###", CultureInfo.InvariantCulture);
}
