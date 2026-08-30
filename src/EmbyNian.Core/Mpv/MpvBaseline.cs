namespace EmbyNian.Mpv;

/// <summary>
/// The handful of mpv options the client always sets, whatever is on the settings page.
/// <para>
/// This is what took over from the globally-active part of the user's <c>mpv.conf</c>. Both backends
/// now start mpv with the config files switched off — <c>--no-config</c> for mpv.exe, and libmpv is
/// already <c>config=no</c> by default — so anything that used to arrive from that file has to be
/// named here or it is simply gone. Only a few lines qualify: the rest of mpv.conf was either window
/// and session behaviour the client owns itself, or picture settings that belong on the settings page
/// where the user can see them.
/// </para>
/// <para>
/// Emitted first, so every one of these can be overridden by 视频输出 / 音频输出, by a shader group, or
/// by 附加参数 — on the command line and on repeated libmpv option calls alike, the last value wins.
/// </para>
/// </summary>
public static class MpvBaseline
{
    /// <param name="shaderCacheDirectory">
    /// Where mpv may cache compiled shaders. Null skips the option, which is only right for a test:
    /// without it every playback recompiles the whole chain, which on an iGPU is seconds of black screen.
    /// </param>
    public static IReadOnlyList<KeyValuePair<string, string>> Build(string? shaderCacheDirectory = null)
    {
        var options = new List<KeyValuePair<string, string>>(6)
        {
            // The client's seek bar hands mpv exact timestamps. Without this a click lands on the
            // preceding keyframe, up to several seconds from where the user aimed.
            new("hr-seek", "yes"),

            // Everything here is streamed over HTTP from Emby, and the tracks are chosen by the
            // client. Left on, mpv would probe the URL's directory for sibling subtitle and audio
            // files on every launch: a burst of 404s against the server that can never find anything.
            new("sub-auto", "no"),
            new("audio-file-auto", "no"),

            // The client asks Emby which display profile a file has and decides tone mapping from
            // that. An ICC profile picked up from the desktop would silently override it.
            new("icc-profile-auto", "no")
        };

        if (!string.IsNullOrWhiteSpace(shaderCacheDirectory))
        {
            options.Add(new KeyValuePair<string, string>("gpu-shader-cache-dir", shaderCacheDirectory));
        }

        return options;
    }
}
