using System.Text.Json.Serialization;
using EmbyNian.Emby;
using EmbyNian.Mpv;
using EmbyNian.Playback;

namespace EmbyNian.Configuration;

/// <summary>
/// The persisted shape of settings.json. Unlike v1 this type is a plain document:
/// it no longer doubles as the live "currently signed-in" state, which is what made
/// the old <c>Activate</c>/<c>UpdateActiveAccount</c> dance so easy to get wrong.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 5;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Stable per-install id so Emby treats this client as one device across restarts.</summary>
    public string DeviceId { get; set; } = "";

    public List<ServerProfile> Servers { get; set; } = [];

    public string? LastServerId { get; set; }

    public string? LastAccountId { get; set; }

    public MpvSettings Mpv { get; set; } = new();

    public PlaybackSettings Playback { get; set; } = new();

    public VideoSettings Video { get; set; } = new();

    public AudioSettings Audio { get; set; } = new();

    public ShaderAutomationSettings Shaders { get; set; } = new();

    public UiSettings Ui { get; set; } = new();

    public ServerProfile? FindServer(string? id) =>
        id is null ? null : Servers.FirstOrDefault(server => server.Id == id);

    public ServerProfile? ResolveLastServer() => FindServer(LastServerId) ?? Servers.FirstOrDefault();

    public AccountProfile? ResolveLastAccount(ServerProfile? server = null)
    {
        server ??= ResolveLastServer();
        if (server is null) return null;
        return server.Accounts.FirstOrDefault(account => account.Id == LastAccountId) ?? server.Accounts.FirstOrDefault();
    }

    public void Remember(ServerProfile server, AccountProfile? account)
    {
        LastServerId = server.Id;
        LastAccountId = account?.Id;
    }

    public AppSettings EnsureDeviceId()
    {
        if (string.IsNullOrWhiteSpace(DeviceId)) DeviceId = Guid.NewGuid().ToString("N");
        return this;
    }
}

public sealed class ServerProfile
{
    /// <summary>The name a brand-new profile carries; a migrated one gets <see cref="MigratedName"/>.</summary>
    public const string DefaultName = "Emby 服务器";

    /// <summary>What v1 settings and a fresh install are called before any server has been reached.</summary>
    public const string MigratedName = "我的 Emby";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = DefaultName;

    public string Url { get; set; } = "http://localhost:8096";

    public List<AccountProfile> Accounts { get; set; } = [];

    /// <summary>
    /// True while the name is still one of the stand-ins, which is the only case where a sign-in may
    /// overwrite it with the name the server reports for itself.
    /// </summary>
    public bool HasPlaceholderName => Name.Trim() is "" or DefaultName or MigratedName;

    public AccountProfile? FindAccount(string? id) =>
        id is null ? null : Accounts.FirstOrDefault(account => account.Id == id);

    public override string ToString() => Name;
}

public sealed class AccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Username { get; set; } = "";

    /// <summary>Emby's own user id, learned at authentication time.</summary>
    public string UserId { get; set; } = "";

    /// <summary>DPAPI-wrapped password; empty when the user opted out of remembering it.</summary>
    public string ProtectedPassword { get; set; } = "";

    /// <summary>
    /// DPAPI-wrapped access token. v1 stored this in the clear next to the server URL;
    /// wrapping it means a leaked settings.json is no longer a working credential.
    /// </summary>
    public string ProtectedAccessToken { get; set; } = "";

    public bool RememberPassword { get; set; } = true;

    public DateTimeOffset? LastSignedIn { get; set; }

    [JsonIgnore]
    public bool HasSavedPassword => ProtectedPassword.Length > 0;

    [JsonIgnore]
    public bool HasSavedToken => ProtectedAccessToken.Length > 0;

    public override string ToString() => Username;
}

public sealed class MpvSettings
{
    /// <summary>Which player the client starts: the user's mpv.exe or in-process libmpv.</summary>
    public MpvBackendKind Backend { get; set; } = MpvBackendKind.BuiltInLibMpv;

    public string ExecutablePath { get; set; } = @"C:\mpv_config-2026.08.12\mpv.exe";

    /// <summary>
    /// Enables the named-pipe control channel. Without it the app can only report
    /// "started" and "stopped" to Emby, never a real playback position.
    /// </summary>
    public bool EnableIpc { get; set; } = true;

    /// <summary>
    /// Optional path shown by the configuration editor. The player deliberately starts with
    /// <c>--no-config</c>/<c>config=no</c>, so this is a user-facing editing location rather than a
    /// hidden input to playback. A null value means "infer <c>portable_config\mpv.conf</c> beside
    /// mpv.exe" and keeps fresh settings files compact.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfigPath { get; set; }

    /// <summary>Optional input.conf path; null uses the same portable-config inference as <see cref="ConfigPath"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputConfigPath { get; set; }

    // 「libmpv-2.dll、附加参数貌似没什么用，删除」 — both are gone as of v5.
    //
    // LibMpvPath pointed at a libmpv build to load instead of the bundled one. The client ships its own
    // libmpv-2.dll next to the exe and every probe order it tried found that copy first, so the field
    // could only ever select a *different* mpv ABI — which is a way to break playback, not to fix it.
    //
    // ExtraArguments appended a raw command line to the launch. Everything it was used for during
    // development has since become a real setting on this page, and being last it silently overrode
    // them; a typo in it was also fatal for mpv.exe, which exits on an unknown option before playing
    // anything. SettingsMigration drops both keys off an older settings.json.
}

public enum MpvBackendKind
{
    /// <summary>In-process libmpv-2.dll; the video renders inside the client window.</summary>
    BuiltInLibMpv,

    /// <summary>The user's own mpv.exe in its own window, controlled over a named pipe.</summary>
    ExternalMpv
}

public sealed class PlaybackSettings
{
    public bool ReportProgressToServer { get; set; } = true;

    public bool ResumeFromSavedPosition { get; set; } = true;

    /// <summary>Ask before resuming instead of silently jumping into the middle of a file.</summary>
    public bool AskBeforeResuming { get; set; } = true;

    /// <summary>Watched once playback passes this share of the runtime.</summary>
    public int MarkWatchedPercent { get; set; } = 90;

    public int ProgressReportIntervalSeconds { get; set; } = 5;

    /// <summary>Whether the audio track follows the server's default or one chosen language.</summary>
    public AudioTrackMode AudioTrack { get; set; } = AudioTrackMode.ServerDefault;

    /// <summary>
    /// The one language the audio track is picked by when <see cref="AudioTrack"/> says so — a name
    /// from <see cref="Playback.TrackLanguagePriority.Catalogue"/> such as 日语, or a raw mpv code.
    /// A file with no track in it falls back to the default track.
    /// </summary>
    public string AudioLanguage { get; set; } = "";

    /// <summary>
    /// Subtitle languages in priority order: the first one the file actually has wins. Stored as
    /// names (简体中文) rather than codes so one entry can cover the several codes and title
    /// spellings a 简体 track turns up with.
    /// </summary>
    public List<string> SubtitleLanguages { get; set; } = ["简体中文", "中文", "繁体中文"];

    /// <summary>When subtitles come on by themselves.</summary>
    public SubtitleMode SubtitleMode { get; set; } = SubtitleMode.Always;

    /// <summary>
    /// Select the file's own default subtitle when it has none of the preferred languages. On by
    /// default: a track labelled in a language nobody listed is still better than no subtitles at all.
    /// </summary>
    public bool SubtitleFallbackToDefault { get; set; } = true;

    /// <summary>
    /// 字幕字体, as a font *family* name — that is what mpv's <c>--sub-font</c> takes. v3 stored the
    /// path of a file under C:\Windows\Fonts here, which mpv quietly ignored: it went looking for a
    /// family literally called "C:\Windows\Fonts\msyh.ttc", found nothing, and fell back to sans-serif.
    /// It only ever looked right because the user's mpv.conf named a real family of its own.
    /// </summary>
    public string SubtitleFontFamily { get; set; } = "Microsoft YaHei";

    /// <summary>
    /// 字幕编码 for a text subtitle that is not valid UTF-8 (mpv's <c>sub-codepage</c>). GB18030 by
    /// default, which is what a Chinese-subtitled release from before UTF-8 was universal will be.
    /// </summary>
    public string SubtitleCodepage { get; set; } = "gb18030";

    /// <summary>字幕字号 in mpv's own units (its default is 55); 0 leaves mpv's default alone.</summary>
    public int SubtitleFontSize { get; set; } = 50;

    /// <summary>字幕加粗. On by default: at a distance it is what makes 简体 subtitles readable over a bright frame.</summary>
    public bool SubtitleBold { get; set; } = true;

    /// <summary>文字颜色 as <c>#RRGGBB</c>; empty leaves mpv's default alone.</summary>
    public string SubtitleColor { get; set; } = "#FFFFFF";

    /// <summary>字体描边 width in mpv units, as a string so 「不设置」 and 「无」 are both expressible.</summary>
    public string SubtitleBorderSize { get; set; } = "0.5";

    /// <summary>描边颜色 as <c>#RRGGBB</c>; empty leaves mpv's default alone.</summary>
    public string SubtitleBorderColor { get; set; } = "#000000";

    /// <summary>字幕阴影 offset in mpv units, as a string so 「不设置」 and 「无」 are both expressible.</summary>
    public string SubtitleShadowOffset { get; set; } = "0.5";

    /// <summary>
    /// 背景颜色 as <c>#RRGGBB</c>, or <see cref="Mpv.MpvOutputOptions.NoBackground"/> for no box at
    /// all; empty leaves mpv's default alone. The opacity below is applied to it.
    /// </summary>
    public string SubtitleBackColor { get; set; } = "";

    /// <summary>背景不透明度 in percent, applied to <see cref="SubtitleBackColor"/>.</summary>
    public int SubtitleBackOpacity { get; set; } = 60;

    /// <summary>
    /// Stretches an image subtitle (PGS/VOBSUB) to the window when the picture is wider than 16:9.
    /// Those tracks are authored for the full disc frame including its black bars, so on a cropped
    /// 2.39:1 encode their lines land below the bottom of the picture and never appear.
    /// </summary>
    public bool StretchWideImageSubtitles { get; set; } = true;

    /// <summary>How far → jumps, in seconds.</summary>
    public int SeekForwardSeconds { get; set; } = 10;

    /// <summary>How far ← jumps, in seconds.</summary>
    public int SeekBackwardSeconds { get; set; } = 10;

    /// <summary>
    /// Backs the resume position up by this many seconds, so a file continues a moment before where
    /// it stopped instead of mid-word. 0 resumes exactly.
    /// </summary>
    public int ResumeRewindSeconds { get; set; }

    /// <summary>
    /// What to do about a recognised 片头 or 片尾: offer a button, jump over it, or leave it alone.
    /// Defaults to asking — the sections come from chapter marks, and a wrong guess that only puts a
    /// button on screen costs nothing, while a wrong automatic jump skips part of the episode.
    /// </summary>
    public SkipSectionMode SkipSections { get; set; } = SkipSectionMode.Ask;

    /// <summary>
    /// Start the next episode by itself once one plays to the end. At a season boundary the player asks
    /// the server for the series order, so the last episode of one season can continue into the next.
    /// </summary>
    public bool AutoPlayNextEpisode { get; set; } = true;
}

/// <summary>
/// Video-output options handed to mpv per launch. Most fields are opt-in: the empty string (or 0, or
/// false) means 「不设置」 and leaves mpv's own compiled-in default alone. See
/// <see cref="Mpv.MpvOutputOptions"/> for the option names these map to.
/// <para>
/// The defaults are not all empty, because there is no mpv.conf behind them any more: what the
/// client does not set here, nobody sets. The shipped values are the ones the user's own config used
/// to provide.
/// </para>
/// </summary>
public sealed class VideoSettings
{
    /// <summary>
    /// 画质预设, one of <see cref="Mpv.MpvOutputOptions.QualityPresets"/>. <c>default</c> means 「不套用」
    /// and is what an install that never touched this gets, so the picture only changes when asked.
    /// </summary>
    public string QualityPreset { get; set; } = "default";

    /// <summary>mpv's <c>vo</c>: 视频渲染.</summary>
    public string Renderer { get; set; } = "gpu-next";

    /// <summary>mpv's <c>gpu-api</c>: 图形接口.</summary>
    public string GpuApi { get; set; } = "d3d11";

    /// <summary>mpv's <c>hwdec</c>: 硬件加速.</summary>
    public string HardwareDecoding { get; set; } = "";

    /// <summary>
    /// mpv's <c>video-output-levels</c>: 色彩范围. 「色彩范围默认使用 PC(0-255)」 — PC range is what a
    /// desktop monitor over DisplayPort/HDMI expects, and it is also the case a mislabelled file gets
    /// wrong most visibly (grey blacks and clipped highlights), so it is stated rather than inferred.
    /// </summary>
    public string OutputLevels { get; set; } = "full";

    /// <summary>mpv's <c>video-sync</c>: 视频显示同步.</summary>
    public string VideoSync { get; set; } = "";

    /// <summary>mpv's <c>deinterlace</c>: 反交错, for interlaced broadcast sources.</summary>
    public bool Deinterlace { get; set; }

    /// <summary>mpv's <c>interpolation</c>: smooths judder, and needs display sync to do anything.</summary>
    public bool Interpolation { get; set; }

    /// <summary>抖动算法 (mpv's <c>dither</c>); see <see cref="Mpv.MpvOutputOptions.Dithers"/>.</summary>
    public string Dither { get; set; } = "fruit";

    /// <summary>去色带 (mpv's <c>deband</c>), including the client's own 「自动」; see
    /// <see cref="Mpv.MpvOutputOptions.DebandModes"/>.</summary>
    public string Deband { get; set; } = MpvOutputOptions.Auto;

    /// <summary>What to do with an HDR source; see <see cref="Mpv.MpvOutputOptions.HdrModes"/>.</summary>
    public string HdrMode { get; set; } = "tonemap";

    /// <summary>
    /// Falls back to <c>video-sync=audio</c> for a source above about 47fps, where display sync has no
    /// spare cadence to resample into and starts dropping frames instead of smoothing them.
    /// </summary>
    public bool HighFrameRateAudioSync { get; set; } = true;

    /// <summary>Demuxer cache in MiB; 0 leaves mpv's own default alone.</summary>
    public int NetworkCacheMegabytes { get; set; }
}

/// <summary>Audio-output options handed to mpv per launch; same opt-in rule as <see cref="VideoSettings"/>.</summary>
public sealed class AudioSettings
{
    /// <summary>mpv's <c>audio-channels</c>: 扬声器布局.</summary>
    public string Channels { get; set; } = "";

    /// <summary>
    /// Codecs passed through to the receiver untouched (mpv's <c>audio-spdif</c>). Empty means mpv
    /// decodes everything itself, which is the right answer for ordinary speakers and headphones.
    /// </summary>
    public List<string> PassthroughCodecs { get; set; } = [];

    /// <summary>mpv's <c>ad-lavc-ac3drc</c>: 动态范围压缩, as a string so 「不设置」 is expressible.</summary>
    public string DynamicRange { get; set; } = "";

    /// <summary>mpv's <c>audio-exclusive</c>: takes the device over for bit-perfect output.</summary>
    public bool ExclusiveMode { get; set; }

    /// <summary>Global audio delay in milliseconds; positive means the audio comes later.</summary>
    public int DelayMilliseconds { get; set; }

    /// <summary>
    /// 音量, 0–100, as the player was last left. Not a knob on the settings page but a remembered one:
    /// every playback starts a fresh mpv with its own config blocked, so mpv's idea of the volume is
    /// always 100 and nothing but this carries a level from one file to the next
    /// (「换个媒体播放音量会变回 100」).
    /// </summary>
    public int Volume { get; set; } = 100;
}

/// <summary>
/// Automatic 「着色器配置组」 selection. The groups themselves are shipped C# data —
/// <see cref="Mpv.ShaderGroupCatalog"/> — so this only records which one to apply and when. The
/// chosen group's shader chain and scalers are handed to mpv as ordinary options, which is why it
/// works the same on the external mpv.exe and on the in-process libmpv that has no profiles at all.
/// </summary>
public sealed class ShaderAutomationSettings
{
    /// <summary>Apply <see cref="DefaultProfile"/> to every video.</summary>
    public bool ApplyToAllVideos { get; set; } = true;

    public string DefaultProfile { get; set; } = ShaderGroupCatalog.DefaultGroupName;

    /// <summary>Apply <see cref="AnimeProfile"/> when the item's genres/tags look animated.</summary>
    public bool AutoAnimeProfile { get; set; } = true;

    public string AnimeProfile { get; set; } = ShaderGroupCatalog.AnimeGroupName;

    /// <summary>
    /// Used instead of the other two when the source is at least
    /// <see cref="HighResThresholdHeight"/> tall. A 4K source on a 1440p screen only ever
    /// downscales, so upscaling shaders never trigger and a lighter group saves power for
    /// free. Empty disables the special case.
    /// </summary>
    public string HighResProfile { get; set; } = ShaderGroupCatalog.HighResGroupName;

    public int HighResThresholdHeight { get; set; } = 1600;

    /// <summary>
    /// Used instead of the other two when the source is no taller than
    /// <see cref="LowResThresholdHeight"/>. A 2–3× upscale is where a heavier upscaler finally earns
    /// its cost, which it does not at 1.33×. Empty disables the special case.
    /// </summary>
    public string LowResProfile { get; set; } = ShaderGroupCatalog.LowResGroupName;

    public int LowResThresholdHeight { get; set; } = 720;

    /// <summary>
    /// Applies no group at all to an 8K source. Nothing on an iGPU decodes 8K and runs a shader chain
    /// at the same time, and the picture is downscaled by a factor of three anyway.
    /// </summary>
    public bool DisableForUltraHighRes { get; set; } = true;

    /// <summary>Case-insensitive substrings matched against Genres and Tags.</summary>
    public List<string> AnimeKeywords { get; set; } =
        ["动画", "动漫", "国漫", "番剧", "卡通", "Anime", "Animation", "Cartoon", "アニメ"];

    /// <summary>Width above which <see cref="DisableForUltraHighRes"/> applies.</summary>
    public const int UltraHighResWidth = 7000;

    /// <summary>The group to apply, or null for none.</summary>
    public string? Resolve(bool looksAnimated, int? sourceWidth, int? sourceHeight)
    {
        if (DisableForUltraHighRes && (sourceWidth >= UltraHighResWidth || sourceHeight >= 3000)) return null;

        var active = looksAnimated && AutoAnimeProfile ? Trimmed(AnimeProfile) : null;
        active ??= ApplyToAllVideos ? Trimmed(DefaultProfile) : null;
        if (active is null) return null;

        if (sourceHeight >= HighResThresholdHeight && Trimmed(HighResProfile) is { } lighter) return lighter;

        // Height 0 means Emby did not report one; that is not a reason to treat the file as 480p.
        if (sourceHeight is > 0 && sourceHeight <= LowResThresholdHeight && Trimmed(LowResProfile) is { } heavier)
            return heavier;

        return active;
    }

    private static string? Trimmed(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class UiSettings
{
    public int PageSize { get; set; } = 100;

    /// <summary>
    /// 选中那套主题的 id，见 <see cref="Theming.UiThemes"/>。
    /// <para>
    /// 存 id 而不是存一堆色值：主题的定义在代码里，这里只记「用户选了哪一套」。认不出来的 id 由
    /// <c>SettingsMigration.Normalize</c> 拨回默认那套 —— 手改过的文件、装回旧版本都会落到这条路上。
    /// </para>
    /// </summary>
    public string Theme { get; set; } = Theming.UiThemes.DefaultId;

    /// <summary>Poster card width in device-independent pixels; the grid scales around it.</summary>
    public int PosterWidth { get; set; } = 170;

    public bool ShowWatchedIndicators { get; set; } = true;

    /// <summary>
    /// 锁定窗口比例大小: whether dragging a window edge keeps the client area at
    /// <see cref="Emby.HomeCarousel.WindowAspect"/> instead of taking whatever shape the pointer implies.
    /// <para>
    /// On by default, and the default is the reason the setting exists: the home page's banner crops a 16:9
    /// backdrop into a 2.2:1 band, and how much it crops follows the window's shape
    /// (<see cref="Emby.HomeCarousel.HeightShare"/>). Held to one shape, the picture is cropped by the same
    /// amount at every size; free, a wide short window flattens the band and cuts the picture further.
    /// Off is for anyone who would rather have the window any shape they like than have the banner keep still.
    /// </para>
    /// <para>
    /// Only while browsing. A file that is playing has its own ratio and it wins — a locked window during
    /// playback would be the letterboxing that 「缩放窗口时按画面比例联动」 was written to get rid of.
    /// </para>
    /// </summary>
    public bool LockWindowShape { get; set; } = true;

    /// <summary>
    /// 默认收起侧边栏: whether the navigation pane starts as the 48-wide rail rather than open. True is what
    /// the shell has always done (「侧边栏默认为折叠状态」); the setting is what makes the other answer possible
    /// without editing the shell. Flipping it also opens or closes the pane there and then, because a switch
    /// that only takes effect after a restart reads as a switch that does not work.
    /// </summary>
    public bool CollapseSidebar { get; set; } = true;

    public string? LastLibraryId { get; set; }

    /// <summary>
    /// The sort each library was last left on, keyed by library id. Emby remembers this per library and
    /// so does this: a music library put in 加入日期 order should not go back to sorting by name because
    /// the film library next to it is sorted that way.
    /// <para>
    /// Library roots only, deliberately. A folder or a season would add an entry every time one was
    /// opened, and this dictionary is written to settings.json.
    /// </para>
    /// </summary>
    public Dictionary<string, LibrarySort> Sort { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 需求 5：the filters each library was last left on, keyed by library id, on the same terms as
    /// <see cref="Sort"/> — library roots only, because a per-folder entry would grow this file every
    /// time one was opened.
    /// <para>
    /// The runtime type is stored directly rather than copied into a settings-only DTO. It is already a
    /// plain object of string lists for exactly this reason, and a second shape would mean a mapping to
    /// keep in step with the panel.
    /// </para>
    /// </summary>
    public Dictionary<string, ItemFilters> Filters { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The view shape (海报 / 缩略图 / 列表) each library was last left in, keyed by library id, on the
    /// same terms as <see cref="Sort"/> — library roots only. Stored as the enum's name: an int would
    /// silently mean something else the day a shape is inserted between two others.
    /// </summary>
    public Dictionary<string, LibraryView> Views { get; set; } = new(StringComparer.Ordinal);


    public int WindowWidth { get; set; } = 1360;

    public int WindowHeight { get; set; } = 860;

    public bool WindowMaximized { get; set; }
}

/// <summary>One library's remembered sort: an Emby <c>SortBy</c> value and a direction.</summary>
public sealed class LibrarySort
{
    public string By { get; set; } = "";

    public bool Descending { get; set; }
}
