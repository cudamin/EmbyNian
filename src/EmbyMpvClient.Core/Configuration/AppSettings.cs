using System.Text.Json.Serialization;

namespace EmbyMpvClient.Configuration;

/// <summary>
/// The persisted shape of settings.json. Unlike v1 this type is a plain document:
/// it no longer doubles as the live "currently signed-in" state, which is what made
/// the old <c>Activate</c>/<c>UpdateActiveAccount</c> dance so easy to get wrong.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Stable per-install id so Emby treats this client as one device across restarts.</summary>
    public string DeviceId { get; set; } = "";

    public List<ServerProfile> Servers { get; set; } = [];

    public string? LastServerId { get; set; }

    public string? LastAccountId { get; set; }

    public MpvSettings Mpv { get; set; } = new();

    public PlaybackSettings Playback { get; set; } = new();

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
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Emby 服务器";

    public string Url { get; set; } = "http://localhost:8096";

    public List<AccountProfile> Accounts { get; set; } = [];

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

    /// <summary>Path to libmpv-2.dll; empty means look next to this exe and next to mpv.exe.</summary>
    public string LibMpvPath { get; set; } = "";

    public string ExecutablePath { get; set; } = @"C:\mpv_config-2026.08.12\mpv.exe";

    public string ConfigPath { get; set; } = @"C:\mpv_config-2026.08.12\portable_config\mpv.conf";

    public string InputConfigPath { get; set; } = @"C:\mpv_config-2026.08.12\portable_config\input.conf";

    /// <summary>Appended verbatim to every mpv launch; parsed with the same quoting rules as a shell.</summary>
    public string ExtraArguments { get; set; } = "";

    /// <summary>
    /// Enables the named-pipe control channel. Without it the app can only report
    /// "started" and "stopped" to Emby, never a real playback position.
    /// </summary>
    public bool EnableIpc { get; set; } = true;

    /// <summary>Number of timestamped copies to keep in the backup folder per config file.</summary>
    public int ConfigBackupsToKeep { get; set; } = 20;
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

    /// <summary>Preferred audio language as a 3-letter code, e.g. "jpn"; empty means let mpv decide.</summary>
    public string PreferredAudioLanguage { get; set; } = "";

    public string PreferredSubtitleLanguage { get; set; } = "chi";

    public bool PreferForcedSubtitles { get; set; }

    /// <summary>
    /// Subtitle font: a .ttf/.ttc/.otf file under C:\Windows\Fonts (or anywhere), handed to
    /// mpv as <c>--sub-font</c>. Defaults to 微软雅黑 so subtitles look right out of the box.
    /// </summary>
    public string SubtitleFontPath { get; set; } = @"C:\Windows\Fonts\msyh.ttc";

    /// <summary>
    /// Track-language priority list passed to mpv as <c>--alang</c>, e.g.
    /// <c>Chinese&gt;English</c>. Applies when mpv picks the audio track itself (no explicit
    /// track chosen); unknown names pass through as ISO codes.
    /// </summary>
    public string AudioLanguagePriority { get; set; } = "";

    /// <summary>
    /// Track-language priority list passed to mpv as <c>--slang</c>, e.g.
    /// <c>Simplified Chinese&gt;Chinese&gt;Traditional Chinese</c>. Same rules as
    /// <see cref="AudioLanguagePriority"/>.
    /// </summary>
    public string SubtitleLanguagePriority { get; set; } = "Simplified Chinese>Chinese>Traditional Chinese";

    /// <summary>
    /// Hands the playback position back to mpv's own watch_later files. Off by default: the
    /// server is the source of truth here, and with <c>save-position-on-quit=yes</c> in mpv.conf
    /// the two records disagree the moment a file is watched on another device.
    /// </summary>
    public bool LetMpvManageResume { get; set; }
}

/// <summary>
/// Automatic mpv shader-profile selection. The groups themselves live in mpv.conf as
/// <c>[Name]</c> sections carrying a <c>glsl-shaders</c> line; this only records which one to
/// apply and when. A command-line <c>--profile=</c> is applied after mpv.conf is read, so it
/// overrides whatever <c>profile=</c> the config file activates globally.
/// </summary>
public sealed class ShaderAutomationSettings
{
    /// <summary>Apply <see cref="DefaultProfile"/> to every video.</summary>
    public bool ApplyToAllVideos { get; set; } = true;

    public string DefaultProfile { get; set; } = "2K-iGPU";

    /// <summary>Apply <see cref="AnimeProfile"/> when the item's genres/tags look animated.</summary>
    public bool AutoAnimeProfile { get; set; } = true;

    public string AnimeProfile { get; set; } = "2K-iGPU-Anime";

    /// <summary>
    /// Used instead of the other two when the source is at least
    /// <see cref="HighResThresholdHeight"/> tall. A 4K source on a 1440p screen only ever
    /// downscales, so upscaling shaders never trigger and a lighter group saves power for
    /// free. Empty disables the special case.
    /// </summary>
    public string HighResProfile { get; set; } = "2K-iGPU-Light";

    public int HighResThresholdHeight { get; set; } = 1600;

    /// <summary>Case-insensitive substrings matched against Genres and Tags.</summary>
    public List<string> AnimeKeywords { get; set; } =
        ["动画", "动漫", "国漫", "番剧", "卡通", "Anime", "Animation", "Cartoon", "アニメ"];

    /// <summary>
    /// Extra mpv config file loaded with <c>--include</c> so the client's own profile groups
    /// live outside mpv.conf: PlayKit's updater.bat cannot clobber them and a manual mpv
    /// launch is unaffected. Empty means "embympvclient.conf next to mpv.conf".
    /// </summary>
    public string IncludeFile { get; set; } = "";

    /// <summary>The profile to pass to mpv, or null to leave mpv.conf's own choice alone.</summary>
    public string? Resolve(bool looksAnimated, int? sourceHeight)
    {
        var active = looksAnimated && AutoAnimeProfile ? Trimmed(AnimeProfile) : null;
        active ??= ApplyToAllVideos ? Trimmed(DefaultProfile) : null;
        if (active is null) return null;

        if (sourceHeight >= HighResThresholdHeight && Trimmed(HighResProfile) is { } lighter) return lighter;
        return active;
    }

    private static string? Trimmed(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class UiSettings
{
    public int PageSize { get; set; } = 100;

    /// <summary>Poster card width in device-independent pixels; the grid scales around it.</summary>
    public int PosterWidth { get; set; } = 170;

    public bool ShowWatchedIndicators { get; set; } = true;

    public string? LastLibraryId { get; set; }

    public int WindowWidth { get; set; } = 1360;

    public int WindowHeight { get; set; } = 860;

    public bool WindowMaximized { get; set; }
}
