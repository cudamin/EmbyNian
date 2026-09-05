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

        // Repair before anything reads the result: every step below — the version upgrades and
        // Normalize — walks into settings.Playback, settings.Video and the lists, and a document
        // holding a JSON null for one of them deserializes to a real null there.
        var settings = Repair(version >= 2 ? ReadCurrent(json) : ReadLegacy(root, protector));

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

        // v7 replaces the four 着色器配置组 names and the two resolution thresholds with the 档位表: the chain
        // is now computed from the scale factor, the picture's kind and 显卡档. Those six keys need no code —
        // the properties are gone and the deserializer ignores a key it has nowhere to put — but the one
        // switch that does have a successor is carried over by hand: 「所有视频默认启用」 became 「启用着色器」,
        // and someone who had switched it off meant 「不要着色器」 both times.
        if (version < 7 && root.TryGetProperty("Shaders", out var shaders)
            && shaders.TryGetProperty("ApplyToAllVideos", out var applyToAll)
            && applyToAll.ValueKind is JsonValueKind.False)
        {
            settings.Shaders.Enabled = false;
        }

        // v8 moves 图形接口 to Vulkan. 「自动挑选」 and d3d11 are the same thing on Windows — mpv picks d3d11 for
        // auto — and on this machine that path runs ArtCNN's compute passes about five times slower: 2026-09-04,
        // same card, same film, same chain at 1080p→1440p, 8.7 fps against 45, where the film needs 24. Both of
        // those values are carried over rather than only the empty one, because nobody arrived at d3d11 by
        // choosing it: it was the shipped default. opengl is left alone — that one was picked.
        if (version < 8
            && (string.IsNullOrWhiteSpace(settings.Video.GpuApi) || settings.Video.GpuApi == "d3d11"))
        {
            settings.Video.GpuApi = MpvRenderCheck.PreferredApi;
        }

        // v9 turns 高帧率或高刷新率时使用音频同步 back on for anyone who had switched it off. Up to v8 that switch
        // only meant 「高帧率片源」, and on 2026-09-04 it grew a second half — 「屏幕超过 120Hz」 — so an off
        // stored before v9 is an answer about frame rates being read as an answer about screens, which it never
        // was. On this machine that silently declined the one rule measured to halve GPU load on the 144Hz panel
        // (24.7% against 50.1%), and nothing said so: the log only speaks when the rule fires.
        //
        // Same call as v8's 图形接口 move immediately above, for the same reason — a value nobody chose for the
        // meaning it now carries is not a preference. It runs exactly once: the settings row names both halves,
        // so switching it off from here on is a real choice and nothing touches it again.
        if (version < 9 && !settings.Video.HighFrameRateAudioSync) settings.Video.HighFrameRateAudioSync = true;

        // v10 turns the single 音轨语言 into a priority list, the way the subtitle side has always been. The
        // property it replaces is gone, so the stored value has to be read from the raw document — and the old
        // 「按语言挑 / 跟随默认」 mode has to be respected while doing it: a file can perfectly well hold a
        // leftover language alongside AudioTrack = ServerDefault, and that language was being ignored. Reviving
        // it here would silently change which track plays.
        if (version < 10) UpgradeAudioLanguage(settings, root);

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        return Normalize(settings);
    }

    /// <summary>
    /// v9's <c>Playback.AudioLanguage</c> (one name) as v10's <c>AudioLanguages</c> (a priority list), but only
    /// when v9's <c>AudioTrack</c> said the language was in use at all — <c>1</c> was 「按语言挑」 and <c>0</c>
    /// 「跟随服务器默认」.
    /// </summary>
    private static void UpgradeAudioLanguage(AppSettings settings, JsonElement root)
    {
        if (settings.Playback.AudioLanguages.Count > 0) return;
        if (!root.TryGetProperty("Playback", out var playback) || playback.ValueKind != JsonValueKind.Object) return;
        if ((ReadInt(playback, "AudioTrack") ?? 0) != 1) return;

        if (ReadString(playback, "AudioLanguage") is { Length: > 0 } language)
            settings.Playback.AudioLanguages = TrackLanguagePriority.CleanList([language]);
    }

    public static AppSettings NewDefaults()
    {
        var settings = new AppSettings();
        settings.Servers.Add(new ServerProfile { Name = ServerProfile.MigratedName });
        settings.LastServerId = settings.Servers[0].Id;
        return Normalize(settings);
    }

    /// <summary>
    /// Puts back anything the document said was <c>null</c>. Every section and every list here has a
    /// property initialiser, but <c>"Playback": null</c> in the file overwrites that initialiser with a
    /// real null — the deserializer sets what the JSON says.
    /// <para>
    /// <b>Without this, such a file stops the app from launching at all.</b> <see cref="Normalize"/> reads
    /// <c>settings.Playback.MarkWatchedPercent</c> two dozen lines in and throws a
    /// <see cref="NullReferenceException"/>, which is not one of the exceptions <see cref="SettingsStore"/>
    /// treats as a corrupt file — so the backup, the quarantine and the fall back to defaults were all
    /// skipped and the window never appeared. A hand-edited settings.json is a case this file already takes
    /// seriously (see <see cref="Normalize"/>'s summary); this is the same case one step earlier.
    /// </para>
    /// <para>
    /// Repairing rather than rejecting, because the rest of the file is still the user's: a null
    /// <c>Playback</c> section costs the playback defaults, not the server list and the saved token.
    /// </para>
    /// <para>
    /// <b>Two boundaries, both deliberate.</b> A list that came back null becomes an empty list rather than
    /// the shipped default: <c>null</c> and <c>[]</c> are indistinguishable once deserialized, and 「一个都
    /// 不选」 is a legitimate answer for every list here. And a null <em>field</em> inside a section — a
    /// server whose <c>Url</c> is null — is not repaired at all; that would mean listing every string
    /// property of every settings class, a list nothing keeps in step with new fields. Those land in
    /// <see cref="SettingsStore"/>'s corrupt-file path instead, which is why that catch is as wide as it is.
    /// </para>
    /// </summary>
    private static AppSettings Repair(AppSettings settings)
    {
        if (settings.Mpv is null) settings.Mpv = new MpvSettings();
        if (settings.Playback is null) settings.Playback = new PlaybackSettings();
        if (settings.Video is null) settings.Video = new VideoSettings();
        if (settings.Audio is null) settings.Audio = new AudioSettings();
        if (settings.Shaders is null) settings.Shaders = new ShaderAutomationSettings();
        if (settings.Ui is null) settings.Ui = new UiSettings();

        if (settings.Playback.AudioLanguages is null) settings.Playback.AudioLanguages = [];
        if (settings.Playback.SubtitleLanguages is null) settings.Playback.SubtitleLanguages = [];
        if (settings.Audio.PassthroughCodecs is null) settings.Audio.PassthroughCodecs = [];
        if (settings.Shaders.AnimeKeywords is null) settings.Shaders.AnimeKeywords = [];

        // A list may also hold nulls — `"Servers": [null]` — and one of those is an entry nothing can be
        // read off. Dropped rather than replaced with a blank: a profile with no URL and no account is not
        // a server anybody can pick, and Normalize would go on to name it 「Emby 服务器」.
        if (settings.Servers is null) settings.Servers = [];
        settings.Servers.RemoveAll(server => server is null);

        foreach (var server in settings.Servers)
        {
            if (server.Accounts is null) server.Accounts = [];
            server.Accounts.RemoveAll(account => account is null);
        }

        if (settings.Ui.HomeRows is null) settings.Ui.HomeRows = [];
        settings.Ui.HomeRows.RemoveAll(row => row is null);

        if (settings.Ui.Sort is null) settings.Ui.Sort = new(StringComparer.Ordinal);
        if (settings.Ui.Filters is null) settings.Ui.Filters = new(StringComparer.Ordinal);
        if (settings.Ui.Views is null) settings.Ui.Views = new(StringComparer.Ordinal);

        // Only these two: Views holds an enum, so a null value there is a JsonException on the way in —
        // which SettingsStore already treats as a corrupt file.
        DropNullValues(settings.Ui.Sort);
        DropNullValues(settings.Ui.Filters);

        return settings;
    }

    /// <summary>
    /// Drops the keys whose value came back null. A library id pointing at nothing is not a remembered
    /// choice, and the pages that read these dictionaries expect the object to be there.
    /// </summary>
    private static void DropNullValues<T>(Dictionary<string, T> map) where T : class
    {
        foreach (var key in map.Where(entry => entry.Value is null).Select(entry => entry.Key).ToList())
            map.Remove(key);
    }

    /// <summary>Clamps anything a hand-edited file could put out of range.</summary>
    public static AppSettings Normalize(AppSettings settings)
    {
        settings.EnsureDeviceId();
        settings.Ui.PageSize = Math.Clamp(settings.Ui.PageSize, 20, 500);
        settings.Ui.PosterWidth = Math.Clamp(settings.Ui.PosterWidth, 120, 340);

        // 海报缓存的磁盘上限。范围和「MB 换字节」都在淘汰规则那一头（Emby.ImageCachePolicy），设置页那一行也读
        // 同一对常量 —— 一个比设置窄的框会显示一个文件里没有的数并在下次触碰时写回去，一个比设置宽的框会让人填进
        // 一个保存时被悄悄搬走的数。缺键（从旧版本升上来）读出来是 0，ClampMegabytes 把 0 当「按装机默认」。
        settings.Ui.ImageCacheMegabytes = Emby.ImageCachePolicy.ClampMegabytes(settings.Ui.ImageCacheMegabytes);

        // 评分来源。枚举存的是整数，所以手改过的文件、或者装回一个档数更少的旧版本，都能留下一个认不出来的数字；
        // 留着它的下场是设置里那个下拉显示成「设置文件中的值」，而屏上按哪一档走谁也说不清。
        if (!Enum.IsDefined(settings.Ui.ScoreSource)) settings.Ui.ScoreSource = Emby.ScoreSource.Community;

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
        settings.Audio.Volume = Math.Clamp(settings.Audio.Volume, 0, AudioSettings.MaxVolume);

        // 显卡档位 is stored as a plain integer, so a hand-edited file can hold anything. Low rather than a
        // clamp to High: an unrecognised number means 「nobody chose」, and the cheap column is the safe
        // reading of that on a machine whose graphics nobody has vouched for.
        if (!Enum.IsDefined(settings.Shaders.Gpu)) settings.Shaders.Gpu = GpuTier.Low;

        if (!Enum.IsDefined(settings.Playback.SkipSections)) settings.Playback.SkipSections = SkipSectionMode.Ask;
        if (!Enum.IsDefined(settings.Playback.SubtitleMode)) settings.Playback.SubtitleMode = SubtitleMode.Always;

        settings.Playback.AudioLanguages = TrackLanguagePriority.CleanList(settings.Playback.AudioLanguages);
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
        settings.Audio.VolumeNormalize = Choice(MpvOutputOptions.VolumeNormalizers, settings.Audio.VolumeNormalize);

        // 音频输出设备没有目录可比（存的是一串 GUID），可 mpv 自己那一项 auto 要归到空串上。设置里那一行提供的是
        // 「跟随系统默认设备」，存的就是空串，而 auto 已经不在下拉里了（AudioDeviceCatalogue.Selectable）——
        // 留着它的话这一行会走 Options 的「设置文件中的值」兜底分支，显示成「auto（设置文件中的值，这台机器上没
        // 找到）」，一句不实的话：auto 恰恰是永远找得到的那一个。0.0.1 那个版本的下拉里还并排放着英文的
        // 「Autoselect device」，点过它的设置文件里就存着这个值，所以这不是假想的状态。
        if (string.Equals(settings.Audio.Device, AudioDeviceCatalogue.AutoDevice, StringComparison.OrdinalIgnoreCase))
            settings.Audio.Device = "";
        settings.Playback.SubtitleCodepage = Choice(MpvOutputOptions.SubtitleCodepages, settings.Playback.SubtitleCodepage);
        settings.Playback.SubtitleColor = Choice(MpvOutputOptions.SubtitleColors, settings.Playback.SubtitleColor);
        settings.Playback.SubtitleBorderSize = Choice(MpvOutputOptions.SubtitleBorders, settings.Playback.SubtitleBorderSize);
        settings.Playback.SubtitleBorderColor = Choice(MpvOutputOptions.SubtitleBorderColors, settings.Playback.SubtitleBorderColor);
        settings.Playback.SubtitleShadowOffset = Choice(MpvOutputOptions.SubtitleShadows, settings.Playback.SubtitleShadowOffset);
        settings.Playback.SubtitleBackColor = Choice(MpvOutputOptions.SubtitleBackColors, settings.Playback.SubtitleBackColor);

        // A hand-picked 档位 that this table no longer has would silently mean 「按自动挑」 anyway, but going
        // through here makes it so on the next save as well, and keeps the settings dropdown from showing a
        // selection nothing matches.
        settings.Shaders.ManualGroup = Chain(settings.Shaders.ManualGroup, settings.Shaders.Gpu);

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
    /// becomes the ordered list, the audio priority string becomes the audio list, and 「优先强制字幕」 becomes
    /// the forced-only mode.
    /// <para>
    /// The audio side used to keep only its first language, because v3 through v9 had a single slot for it.
    /// v10 made it a list again, so a v2 file's whole priority string now survives — which is what it always
    /// meant.
    /// </para>
    /// </summary>
    private static void UpgradeTrackLanguages(AppSettings settings, JsonElement root)
    {
        if (!root.TryGetProperty("Playback", out var playback) || playback.ValueKind != JsonValueKind.Object) return;

        var subtitles = Tokens(ReadString(playback, "SubtitleLanguagePriority"));
        if (subtitles.Count == 0) subtitles = Tokens(ReadString(playback, "PreferredSubtitleLanguage"));
        if (subtitles.Count > 0) settings.Playback.SubtitleLanguages = subtitles;

        var audio = Tokens(ReadString(playback, "PreferredAudioLanguage"));
        if (audio.Count == 0) audio = Tokens(ReadString(playback, "AudioLanguagePriority"));
        if (audio.Count > 0) settings.Playback.AudioLanguages = audio;

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
    /// A 着色器档位 id the shipped table actually has, or the empty string. Empty means 「自动按放大倍数挑」 —
    /// a real choice, and the one every install starts on, so it is never replaced by a fallback.
    /// </summary>
    private static string Chain(string? value, GpuTier gpu)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0) return "";

        return ShaderGroupCatalog.Find(trimmed, gpu)?.Id ?? "";
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
