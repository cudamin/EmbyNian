using System.Text.Json;
using EmbyNian.Infrastructure;
using EmbyNian.Mpv;
using EmbyNian.Playback;

namespace EmbyNian.Configuration;

/// <summary>
/// Reads any settings.json this app has ever written and returns the current shape.
/// Kept as a pure function over a JSON string so the migration paths are unit-testable
/// without touching the disk.
/// </summary>
public static class SettingsMigration
{
    public static AppSettings FromJson(string json, ISecretProtector protector)
    {
        if (string.IsNullOrWhiteSpace(json)) return NewDefaults();

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return NewDefaults();

        var version = ReadInt(root, "SchemaVersion") ?? 1;
        var settings = version >= 2 ? ReadCurrent(json) : ReadLegacy(root, protector);

        // v2 stored the track languages as two typed-in priority strings. v3 keeps the subtitle
        // languages as an ordered multi-select and the audio language as a single choice, so the old
        // fields — which the current shape no longer has properties for — are read from the raw
        // document and translated here.
        if (version < 3) UpgradeTrackLanguages(settings, root);

        // v3 and earlier read the user's own mpv.conf and input.conf, so most picture and 字幕 fields
        // were left at 「不设置」 on purpose — the config file was doing the work. With those files no
        // longer read, an empty field would now mean mpv's bare default and the picture would visibly
        // change, so v4 fills each one in with what that config used to provide.
        if (version < 4) UpgradeToSelfContainedConfig(settings, root);

        // v5 states 色彩范围 instead of leaving it to the file's own flags. An install that never touched
        // the field gets PC range like a fresh one does; one that chose 电视（16-235） keeps it.
        //
        // v5 also drops Mpv.LibMpvPath and Mpv.ExtraArguments. Neither needs code here: the properties
        // are gone, and the deserializer ignores a key it has nowhere to put.
        if (version < 5 && string.IsNullOrWhiteSpace(settings.Video.OutputLevels)) settings.Video.OutputLevels = "full";

        // v6 is the first version in which anything reads the window size back. Up to v5 the three
        // Ui.Window* fields were written on every save and read by nobody — v1's WinForms shell restored
        // them, the WinUI shell never did — so whatever a v5 file holds is a number from a shell that no
        // longer exists, or v5's own default of 1360×860 that no window was ever that size for. Cleared
        // rather than carried over: 0 means 「never recorded」, so the first launch after the upgrade opens
        // at the computed default and starts remembering from there. Keeping them would restore a size the
        // user never chose and call it their preference.
        if (version < 6)
        {
            settings.Ui.WindowLeft = 0;
            settings.Ui.WindowTop = 0;
            settings.Ui.WindowWidth = 0;
            settings.Ui.WindowHeight = 0;
            settings.Ui.WindowMaximized = false;
        }

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        return Normalize(settings);
    }

    public static AppSettings NewDefaults()
    {
        var settings = new AppSettings();
        settings.Servers.Add(new ServerProfile { Name = ServerProfile.MigratedName });
        settings.LastServerId = settings.Servers[0].Id;
        return Normalize(settings);
    }

    /// <summary>Clamps anything a hand-edited file could put out of range.</summary>
    public static AppSettings Normalize(AppSettings settings)
    {
        settings.EnsureDeviceId();
        settings.Ui.PageSize = Math.Clamp(settings.Ui.PageSize, 20, 500);
        settings.Ui.PosterWidth = Math.Clamp(settings.Ui.PosterWidth, 120, 340);

        // 主题 id 不在目录里就写回默认那套，而不是留着一个认不出来的字符串：留着的话每次启动都要再判一次，
        // 而且设置里那个下拉框会显示成空的。这里换掉，用户下次保存就落盘成一个真的 id。
        settings.Ui.Theme = Theming.UiThemes.Resolve(settings.Ui.Theme).Id;

        // 记下来的窗口尺寸只做「不是垃圾数字」这一道校验，摆到哪块屏、要不要缩进工作区由
        // ScreenPlacement.Restore 在开窗那一刻定 —— 只有那时候才知道现在接着几块屏、各自多大。0 是
        // 「还没记过」，必须原样留着：夹成 400×300 就等于替用户宣布他拉过一个 400 宽的窗口。
        if (settings.Ui.WindowWidth != 0 || settings.Ui.WindowHeight != 0)
        {
            settings.Ui.WindowWidth = Math.Clamp(settings.Ui.WindowWidth, 400, 8000);
            settings.Ui.WindowHeight = Math.Clamp(settings.Ui.WindowHeight, 300, 8000);
        }

        settings.Playback.MarkWatchedPercent = Math.Clamp(settings.Playback.MarkWatchedPercent, 50, 100);
        settings.Playback.ProgressReportIntervalSeconds = Math.Clamp(settings.Playback.ProgressReportIntervalSeconds, 1, 60);
        settings.Playback.SeekForwardSeconds = Math.Clamp(settings.Playback.SeekForwardSeconds, 1, 600);
        settings.Playback.SeekBackwardSeconds = Math.Clamp(settings.Playback.SeekBackwardSeconds, 1, 600);
        settings.Playback.ResumeRewindSeconds = Math.Clamp(settings.Playback.ResumeRewindSeconds, 0, 120);
        settings.Playback.SubtitleBackOpacity = Math.Clamp(settings.Playback.SubtitleBackOpacity, 0, 100);
        if (settings.Playback.SubtitleFontSize != 0)
            settings.Playback.SubtitleFontSize = Math.Clamp(settings.Playback.SubtitleFontSize, 16, 160);
        settings.Video.NetworkCacheMegabytes = Math.Clamp(settings.Video.NetworkCacheMegabytes, 0, 4096);
        settings.Audio.DelayMilliseconds = Math.Clamp(settings.Audio.DelayMilliseconds, -5000, 5000);
        settings.Audio.Volume = Math.Clamp(settings.Audio.Volume, 0, 100);
        settings.Shaders.HighResThresholdHeight = Math.Clamp(settings.Shaders.HighResThresholdHeight, 720, 4320);
        settings.Shaders.LowResThresholdHeight = Math.Clamp(settings.Shaders.LowResThresholdHeight, 240, 1080);

        // The two ranges overlap on 720–1080, and ShaderAutomationSettings.Resolve tests 高清 first. So with
        // 低清阈值 at or above 高清阈值, every height that should have been 低清 comes out 高清 instead and the
        // 低清配置组 can never apply — a group that is set, reads as set, and silently never runs. 低清 is the
        // one pushed rather than 高清 because its own range lies entirely below the other's, so one below
        // 高清阈值 is always still a legal 低清阈值 (720−1 = 719, well inside 240–1080).
        if (settings.Shaders.LowResThresholdHeight >= settings.Shaders.HighResThresholdHeight)
            settings.Shaders.LowResThresholdHeight = settings.Shaders.HighResThresholdHeight - 1;

        if (!Enum.IsDefined(settings.Playback.SkipSections)) settings.Playback.SkipSections = SkipSectionMode.Ask;
        if (!Enum.IsDefined(settings.Playback.SubtitleMode)) settings.Playback.SubtitleMode = SubtitleMode.Always;
        if (!Enum.IsDefined(settings.Playback.AudioTrack)) settings.Playback.AudioTrack = AudioTrackMode.ServerDefault;

        settings.Playback.AudioLanguage = TrackLanguagePriority.Canonical(settings.Playback.AudioLanguage);
        settings.Playback.SubtitleLanguages = TrackLanguagePriority.CleanList(settings.Playback.SubtitleLanguages);
        settings.Audio.PassthroughCodecs = CleanCodecs(settings.Audio.PassthroughCodecs);

        // Every one of these reaches mpv as an option value, and mpv exits rather than plays when it
        // does not understand one. A hand-edited file may only pick from the lists the page offers.
        settings.Video.QualityPreset = Preset(settings.Video.QualityPreset);
        settings.Video.Renderer = Choice(MpvOutputOptions.Renderers, settings.Video.Renderer);
        settings.Video.GpuApi = Choice(MpvOutputOptions.GpuApis, settings.Video.GpuApi);
        settings.Video.HardwareDecoding = Choice(MpvOutputOptions.HardwareDecoders, settings.Video.HardwareDecoding);
        settings.Video.OutputLevels = Choice(MpvOutputOptions.OutputLevels, settings.Video.OutputLevels);
        settings.Video.VideoSync = Choice(MpvOutputOptions.VideoSync, settings.Video.VideoSync);
        settings.Video.Dither = Choice(MpvOutputOptions.Dithers, settings.Video.Dither);
        settings.Video.Deband = Choice(MpvOutputOptions.DebandModes, settings.Video.Deband);
        settings.Video.HdrMode = Choice(MpvOutputOptions.HdrModes, settings.Video.HdrMode);
        settings.Audio.Channels = Choice(MpvOutputOptions.Channels, settings.Audio.Channels);
        settings.Audio.DynamicRange = Choice(MpvOutputOptions.DynamicRange, settings.Audio.DynamicRange);
        settings.Playback.SubtitleCodepage = Choice(MpvOutputOptions.SubtitleCodepages, settings.Playback.SubtitleCodepage);
        settings.Playback.SubtitleColor = Choice(MpvOutputOptions.SubtitleColors, settings.Playback.SubtitleColor);
        settings.Playback.SubtitleBorderSize = Choice(MpvOutputOptions.SubtitleBorders, settings.Playback.SubtitleBorderSize);
        settings.Playback.SubtitleBorderColor = Choice(MpvOutputOptions.SubtitleBorderColors, settings.Playback.SubtitleBorderColor);
        settings.Playback.SubtitleShadowOffset = Choice(MpvOutputOptions.SubtitleShadows, settings.Playback.SubtitleShadowOffset);
        settings.Playback.SubtitleBackColor = Choice(MpvOutputOptions.SubtitleBackColors, settings.Playback.SubtitleBackColor);

        // A group name that no longer exists would silently mean 「no shaders at all」, which is a
        // hard thing to notice; falling back to the shipped default keeps the picture working.
        settings.Shaders.DefaultProfile = Group(settings.Shaders.DefaultProfile, ShaderGroupCatalog.DefaultGroupName);
        settings.Shaders.AnimeProfile = Group(settings.Shaders.AnimeProfile, ShaderGroupCatalog.AnimeGroupName);
        settings.Shaders.HighResProfile = Group(settings.Shaders.HighResProfile, ShaderGroupCatalog.HighResGroupName);
        settings.Shaders.LowResProfile = Group(settings.Shaders.LowResProfile, ShaderGroupCatalog.LowResGroupName);

        // 字幕字体 is a family name for mpv; a v3 file may still hold the path of a font file here.
        settings.Playback.SubtitleFontFamily = ResolveFontFamily(settings.Playback.SubtitleFontFamily);

        foreach (var server in settings.Servers)
        {
            server.Name = string.IsNullOrWhiteSpace(server.Name) ? "Emby 服务器" : server.Name.Trim();
            server.Url = server.Url.Trim();
        }

        // Keep the remembered pair consistent: the remembered account has to live on
        // the remembered server, or the login page opens on a mismatched selection.
        if (settings.LastAccountId is not null && settings.FindServer(settings.LastServerId)?.FindAccount(settings.LastAccountId) is null)
        {
            var owner = settings.Servers.FirstOrDefault(server => server.FindAccount(settings.LastAccountId) is not null);
            if (owner is not null) settings.LastServerId = owner.Id;
            else settings.LastAccountId = null;
        }

        if (settings.FindServer(settings.LastServerId) is null)
        {
            settings.LastServerId = settings.Servers.FirstOrDefault()?.Id;
            settings.LastAccountId = settings.ResolveLastAccount()?.Id;
        }

        return settings;
    }

    private static AppSettings ReadCurrent(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, SettingsSerializer.ReadOptions) ?? NewDefaults();

    /// <summary>
    /// Carries the v2 track-language settings over: the <c>&gt;</c>-separated subtitle priority string
    /// becomes the ordered list, the audio priority string keeps only its first language (the audio
    /// track no longer has a priority list), and 「优先强制字幕」 becomes the forced-only mode.
    /// </summary>
    private static void UpgradeTrackLanguages(AppSettings settings, JsonElement root)
    {
        if (!root.TryGetProperty("Playback", out var playback) || playback.ValueKind != JsonValueKind.Object) return;

        var subtitles = Tokens(ReadString(playback, "SubtitleLanguagePriority"));
        if (subtitles.Count == 0) subtitles = Tokens(ReadString(playback, "PreferredSubtitleLanguage"));
        if (subtitles.Count > 0) settings.Playback.SubtitleLanguages = subtitles;

        var audio = Tokens(ReadString(playback, "PreferredAudioLanguage"));
        if (audio.Count == 0) audio = Tokens(ReadString(playback, "AudioLanguagePriority"));
        if (audio.Count > 0)
        {
            settings.Playback.AudioTrack = AudioTrackMode.Language;
            settings.Playback.AudioLanguage = audio[0];
        }

        if (ReadBool(playback, "PreferForcedSubtitles") == true) settings.Playback.SubtitleMode = SubtitleMode.ForcedOnly;
    }

    /// <summary>
    /// v4 took over from the user's mpv.conf. Every setting a v3 file left at 「不设置」 is filled in
    /// with what that config used to supply, so the picture on screen after the upgrade is the one
    /// from before it. Anything the user had actually chosen is left alone: an explicit value always
    /// beat mpv.conf anyway, so it is already what they are seeing.
    /// <para>
    /// input.conf needs no migration at all. The embedded player has always run with
    /// <c>input-default-bindings=no</c> and the bundled libmpv is built without Lua, so none of those
    /// bindings or their uosc scripts were ever loaded — the file only ever applied to an mpv the user
    /// launched themselves, which is untouched by any of this.
    /// </para>
    /// </summary>
    private static void UpgradeToSelfContainedConfig(AppSettings settings, JsonElement root)
    {
        var video = settings.Video;
        Fill(value => video.Renderer = value, video.Renderer, "gpu-next");
        Fill(value => video.GpuApi = value, video.GpuApi, "d3d11");
        Fill(value => video.Dither = value, video.Dither, "fruit");
        Fill(value => video.Deband = value, video.Deband, MpvOutputOptions.Auto);
        Fill(value => video.HdrMode = value, video.HdrMode, "tonemap");

        var playback = settings.Playback;
        Fill(value => playback.SubtitleCodepage = value, playback.SubtitleCodepage, "gb18030");
        Fill(value => playback.SubtitleColor = value, playback.SubtitleColor, "#FFFFFF");
        Fill(value => playback.SubtitleBorderSize = value, playback.SubtitleBorderSize, "0.5");
        Fill(value => playback.SubtitleBorderColor = value, playback.SubtitleBorderColor, "#000000");
        Fill(value => playback.SubtitleShadowOffset = value, playback.SubtitleShadowOffset, "0.5");
        if (playback.SubtitleFontSize == 0) playback.SubtitleFontSize = 50;

        if (!root.TryGetProperty("Playback", out var stored) || stored.ValueKind != JsonValueKind.Object) return;

        // v3's own default was true, so a false here was a deliberate choice and stays.
        if (ReadBool(stored, "SubtitleBold") is null) playback.SubtitleBold = true;

        // 字幕字体 used to be the path of a font file, which mpv silently ignored.
        if (ReadString(stored, "SubtitleFontPath") is { Length: > 0 } fontPath)
            playback.SubtitleFontFamily = FontFamilies.FromFileName(fontPath) ?? FontFamilies.Default;

        static void Fill(Action<string> set, string current, string replacement)
        {
            if (string.IsNullOrWhiteSpace(current)) set(replacement);
        }
    }

    /// <summary>A v2 priority string as canonical language names, in order and without duplicates.</summary>
    private static List<string> Tokens(string? priority) => TrackLanguagePriority.ParseList(priority);

    /// <summary>Passthrough is a fixed set of codec names; anything else would be a fatal mpv option.</summary>
    private static List<string> CleanCodecs(List<string>? codecs)
    {
        var cleaned = new List<string>(codecs?.Count ?? 0);
        foreach (var choice in MpvOutputOptions.PassthroughCodecs)
        {
            if (codecs?.Contains(choice.Value, StringComparer.OrdinalIgnoreCase) == true) cleaned.Add(choice.Value);
        }

        return cleaned;
    }

    /// <summary>The value if the settings page offers it, otherwise 「不设置」 — i.e. mpv's own default.</summary>
    private static string Choice(IReadOnlyList<MpvChoice> choices, string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0) return "";

        return choices.Any(choice => string.Equals(choice.Value, trimmed, StringComparison.OrdinalIgnoreCase))
            ? trimmed
            : "";
    }

    /// <summary>
    /// 画质预设. Unlike the option lists this one has no 「不设置」 entry — its own first entry, <c>default</c>,
    /// already means 「不套用」 — so an unknown value lands there rather than on an empty string that would
    /// then have to be interpreted all over again at launch time.
    /// </summary>
    private static string Preset(string? value)
    {
        var chosen = Choice(MpvOutputOptions.QualityPresets, value);
        return chosen.Length > 0 ? chosen : MpvOutputOptions.QualityPresets[0].Value;
    }

    /// <summary>
    /// A 着色器配置组 name that the shipped catalogue actually has. An empty value is kept as-is: for
    /// 高清片源 / 低清片源 that means 「不特殊处理」, which is a real choice rather than a broken one.
    /// </summary>
    private static string Group(string? value, string fallback)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0) return "";

        return ShaderGroupCatalog.Find(trimmed) is not null ? trimmed : fallback;
    }

    /// <summary>
    /// 字幕字体 as something mpv can look up. Empty falls back to the shipped default; a leftover font
    /// file path is mapped to the family it stands for; anything else is taken at its word, because a
    /// family this table has never heard of may still be installed.
    /// </summary>
    private static string ResolveFontFamily(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0) return FontFamilies.Default;

        var looksLikeFile = trimmed.Contains('\\') || trimmed.Contains('/')
            || trimmed.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

        return looksLikeFile ? FontFamilies.FromFileName(trimmed) ?? FontFamilies.Default : trimmed;
    }

    /// <summary>
    /// v1 kept the signed-in server/user at the document root and, in later builds, also
    /// a <c>Servers</c> array. Prefer the array when present, otherwise synthesise one
    /// profile from the flat fields so the user does not have to re-enter anything.
    /// </summary>
    private static AppSettings ReadLegacy(JsonElement root, ISecretProtector protector)
    {
        var settings = new AppSettings
        {
            LastServerId = ReadString(root, "LastServerId"),
            LastAccountId = ReadString(root, "LastAccountId")
        };

        settings.Mpv.ExecutablePath = ReadString(root, "MpvPath") ?? settings.Mpv.ExecutablePath;
        // v1's MpvConfigPath / InputConfigPath are deliberately dropped: the client no longer reads
        // either file, so carrying the paths forward would only preserve a setting nothing consults.

        if (root.TryGetProperty("Servers", out var servers) && servers.ValueKind == JsonValueKind.Array && servers.GetArrayLength() > 0)
        {
            foreach (var element in servers.EnumerateArray())
            {
                settings.Servers.Add(ReadLegacyServer(element, protector));
            }
        }
        else
        {
            var server = new ServerProfile
            {
                Name = ServerProfile.MigratedName,
                Url = ReadString(root, "ServerUrl") ?? "http://localhost:8096"
            };

            var username = ReadString(root, "Username");
            if (!string.IsNullOrWhiteSpace(username))
            {
                server.Accounts.Add(new AccountProfile
                {
                    Username = username.Trim(),
                    UserId = ReadString(root, "UserId") ?? "",
                    ProtectedAccessToken = ProtectPlainText(protector, ReadString(root, "AccessToken"))
                });
            }

            settings.Servers.Add(server);
        }

        settings.LastServerId ??= settings.Servers.FirstOrDefault()?.Id;
        settings.LastAccountId ??= settings.Servers.FirstOrDefault()?.Accounts.FirstOrDefault()?.Id;
        return settings;
    }

    private static ServerProfile ReadLegacyServer(JsonElement element, ISecretProtector protector)
    {
        var server = new ServerProfile
        {
            Name = ReadString(element, "Name") ?? "Emby 服务器",
            Url = ReadString(element, "Url") ?? "http://localhost:8096"
        };

        if (ReadString(element, "Id") is { Length: > 0 } id) server.Id = id;

        if (!element.TryGetProperty("Accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
            return server;

        foreach (var item in accounts.EnumerateArray())
        {
            var account = new AccountProfile
            {
                Username = ReadString(item, "Username") ?? "",
                UserId = ReadString(item, "UserId") ?? "",
                // v1 already wrapped the password with the same DPAPI entropy, so it carries over as-is.
                ProtectedPassword = ReadString(item, "ProtectedPassword") ?? "",
                ProtectedAccessToken = ProtectPlainText(protector, ReadString(item, "AccessToken"))
            };

            if (ReadString(item, "Id") is { Length: > 0 } accountId) account.Id = accountId;
            account.RememberPassword = account.ProtectedPassword.Length > 0;
            server.Accounts.Add(account);
        }

        return server;
    }

    private static string ProtectPlainText(ISecretProtector protector, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try
        {
            return protector.Protect(value);
        }
        catch
        {
            return "";
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
