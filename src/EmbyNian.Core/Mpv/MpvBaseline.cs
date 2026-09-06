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
    /// <summary>
    /// What a screenshot is called when the film has no usable title. Not <c>mpv-shot%n</c>: the whole point
    /// of naming the file is that a folder of them can be read months later.
    /// </summary>
    private const string UntitledScreenshot = "EmbyNian";

    /// <param name="shaderCacheDirectory">
    /// Where mpv may cache compiled shaders. Null skips the option, which is only right for a test:
    /// without it every playback recompiles the whole chain, which on an iGPU is seconds of black screen.
    /// </param>
    /// <param name="screenshotDirectory">
    /// Where 截图 land. Null skips all three screenshot options, which is only right for a test — see
    /// <see cref="ScreenshotTemplate"/> for why an unset directory is the reason this client had no
    /// screenshot feature at all until now.
    /// </param>
    /// <param name="title">The film's title, for the screenshot file name. Empty is handled.</param>
    /// <param name="fontsDirectory">
    /// Where this client's bundled 字幕字体 live (assets/fonts, copied next to the exe). mpv's
    /// <c>sub-fonts-dir</c> — font files there are used for subtitles without being installed into
    /// Windows, which is the whole mechanism behind 「这个字体打包进程序里」: the shipped
    /// 方正中等线简体 reaches mpv by this one option, and any future font dropped into that folder is
    /// pickable the same way. Null skips it, which is what a test wants.
    /// </param>
    public static IReadOnlyList<KeyValuePair<string, string>> Build(
        string? shaderCacheDirectory = null,
        string? screenshotDirectory = null,
        string? title = null,
        string? fontsDirectory = null)
    {
        var options = new List<KeyValuePair<string, string>>(9)
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
            // that. An ICC profile picked up from the desktop would silently override it, and on
            // Windows that profile is usually whatever the monitor's driver dropped there rather than
            // a measurement. This is the floor, not a verdict: 设置 → 视频输出 → 自动 ICC 校色
            // (VideoSettings.IccProfileAuto) is emitted after this list and lifts it to yes.
            new("icc-profile-auto", "no")
        };

        if (!string.IsNullOrWhiteSpace(shaderCacheDirectory))
        {
            options.Add(new KeyValuePair<string, string>("gpu-shader-cache-dir", shaderCacheDirectory));
        }

        if (!string.IsNullOrWhiteSpace(fontsDirectory))
        {
            options.Add(new KeyValuePair<string, string>("sub-fonts-dir", fontsDirectory));
        }

        if (!string.IsNullOrWhiteSpace(screenshotDirectory))
        {
            options.Add(new KeyValuePair<string, string>("screenshot-directory", screenshotDirectory));

            // png rather than mpv's own jpg default: a screenshot of a film is looked at to judge the
            // picture — banding, ringing, what a shader chain did to an edge — and a lossy re-encode is
            // the one thing that must not be in the way of that answer.
            options.Add(new KeyValuePair<string, string>("screenshot-format", "png"));
            options.Add(new KeyValuePair<string, string>("screenshot-template", ScreenshotTemplate(title)));
        }

        return options;
    }

    /// <summary>
    /// What one screenshot is called: the film's own title and the timecode it was taken at.
    /// <para>
    /// <b>Composed here rather than left to mpv's <c>%F</c>.</b> mpv's own specifiers name the *file* being
    /// played, and what this client plays is an Emby stream URL — <c>%F</c> would produce a GUID-shaped path
    /// segment. The title is known at launch, so it is substituted in as literal text and mpv is only asked
    /// for the part that changes while the film runs.
    /// </para>
    /// <para>
    /// <b>Two things about the timecode.</b> It is <c>%wH.%wM.%wS</c> rather than mpv's ready-made <c>%p</c>,
    /// because <c>%p</c> is <c>HH:MM:SS</c> and a colon cannot be in a Windows filename. And the title goes
    /// through <see cref="Emby.DownloadPlan.Safe"/> — the same rules the 下载到设备 filenames use, so there is
    /// one answer in this codebase to 「what may be in a filename」 — plus <c>%</c>, which that function has no
    /// reason to care about and which mpv would read as a specifier of its own.
    /// </para>
    /// <para>
    /// <b>The trailing <c>%n</c> is not decoration.</b> mpv never overwrites a screenshot that already exists,
    /// and it only goes looking for a free name when the template carries a sequence number — without one the
    /// second shot is simply not written, and the menu's 「已保存到 …」 says otherwise, because the command's
    /// failure never comes back. That is not a corner case here: the timecode is whole seconds and a paused
    /// film's does not move at all, so 截图 — 屏上这一帧 followed by 截图 — 原始画面, which is the whole point of
    /// having both rows, resolves to one name twice. Two digits rather than mpv's own four, because the number
    /// is only here to break a tie.
    /// </para>
    /// </summary>
    public static string ScreenshotTemplate(string? title)
    {
        var name = Emby.DownloadPlan.Safe(title).Replace("%", "");

        // Safe() strips trailing dots and spaces, so a title made only of those comes back empty.
        if (string.IsNullOrWhiteSpace(name)) name = UntitledScreenshot;

        return $"{name} %wH.%wM.%wS-%02n";
    }
}
