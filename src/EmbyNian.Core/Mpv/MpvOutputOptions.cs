using System.Globalization;
using EmbyNian.Configuration;

namespace EmbyNian.Mpv;

/// <summary>One selectable value of an mpv option, with the label the settings page shows for it.</summary>
/// <param name="Value">What mpv is given; the empty string means 「不设置，用 mpv 自己的默认值」.</param>
public readonly record struct MpvChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// What the client knows about the video it is about to play, for the rules that depend on it.
/// Filled in from Emby's own stream metadata, so nothing has to be probed or guessed before mpv
/// starts. Every field is 0 or false when the server did not report it, and every rule is written to
/// do nothing in that case.
/// </summary>
public readonly record struct SourceProfile(int Width, int Height, int BitDepth, double FrameRate, bool IsHdr)
{
    public double AspectRatio => Height > 0 ? (double)Width / Height : 0;
}

/// <summary>
/// The video-output, audio-output and 字幕外观 options the settings page exposes, and the translation
/// from those settings into mpv option pairs.
/// <para>
/// This is now the only place picture and sound options come from: both backends start mpv with the
/// config files switched off, so there is no mpv.conf left to inherit anything from. A value that is
/// left empty therefore means mpv's own compiled-in default, not 「whatever the user's config said」.
/// </para>
/// <para>
/// Only options that have existed in mpv for years are listed. An unknown option name is fatal for
/// mpv.exe (it exits before playing anything), so this is not the place for the newest flag in the
/// changelog.
/// </para>
/// </summary>
public static class MpvOutputOptions
{
    /// <summary>
    /// The 「不设置」 entry every list starts with: nothing is sent at all, so mpv's own compiled-in
    /// value stands.
    /// <para>
    /// 「设置里不要说什么 MPV 默认，用的啥说清楚」: this used to be labelled 「mpv 默认」 in every list,
    /// which told the user nothing about what would actually happen. Each list now names the value that
    /// ends up in force instead, so the entry reads as a choice rather than as a shrug.
    /// </para>
    /// </summary>
    public const string Inherit = "";

    /// <summary>字幕背景颜色 stand-in for 「无背景」, which is a real choice rather than 「不设置」.</summary>
    public const string NoBackground = "none";

    /// <summary>
    /// 「自动」 in a list whose other entries are mpv values. It is the client's own sentinel — the
    /// rule behind it is decided here from the source's metadata, and this string never reaches mpv.
    /// </summary>
    public const string Auto = "auto";

    /// <summary>
    /// 「在动画中开启」, the second client-side sentinel: decided from the same 动画 detection the shader
    /// rules run on Emby's metadata, and likewise never sent to mpv.
    /// </summary>
    public const string Anime = "anime";

    /// <summary>mpv's own spelling of 「关闭」, kept as a constant because it is also a sentinel elsewhere.</summary>
    private const string Off = "no";

    public static readonly MpvChoice[] Renderers =
    [
        new(Inherit, "自动挑选（Windows 上是 GPU）"),
        new("gpu-next", "GPU-Next（新，画质更好）"),
        new("gpu", "GPU（兼容性最好）"),
        new("dmabuf-wayland", "DMAbuf（仅 Wayland）")
    ];

    public static readonly MpvChoice[] GpuApis =
    [
        new(Inherit, "自动挑选（Windows 上是 Direct3D 11）"),
        new("d3d11", "Direct3D 11"),
        new("vulkan", "Vulkan"),
        new("opengl", "OpenGL")
    ];

    public static readonly MpvChoice[] HardwareDecoders =
    [
        new(Inherit, "不指定（等同纯软件解码）"),
        new("auto-safe", "自动（推荐）"),
        new("d3d11va", "D3D11VA"),
        new("d3d11va-copy", "D3D11VA（复制回内存）"),
        new("dxva2", "DXVA2"),
        new("nvdec", "NVDEC（NVIDIA）"),
        new("vulkan", "Vulkan"),
        new(Off, "关闭（纯软件解码）")
    ];

    public static readonly MpvChoice[] OutputLevels =
    [
        new(Inherit, "跟随片源标记"),
        new("full", "PC（0-255）"),
        new("limited", "电视（16-235）")
    ];

    public static readonly MpvChoice[] VideoSync =
    [
        new(Inherit, "不指定（等同音频同步）"),
        new("audio", "音频同步"),
        new("display-resample", "显示同步（重采样音频）"),
        new("display-vdrop", "显示同步（丢帧）")
    ];

    /// <summary>
    /// 抖动. mpv only dithers when <c>dither-depth</c> names a depth, so the two options always travel
    /// together and the list is written in terms of what the user actually chooses: the algorithm.
    /// </summary>
    public static readonly MpvChoice[] Dithers =
    [
        new(Inherit, "不抖动（直接截断到显示位深）"),
        new("fruit", "Fruit（推荐）"),
        new("ordered", "有序抖动（最省）"),
        new("error-diffusion", "误差扩散（最好，最费）"),
        new(Off, "关闭")
    ];

    /// <summary>
    /// 去色带. 「自动」 follows the source's bit depth: an 8-bit encode is where banding comes from, and
    /// a 10-bit one already has the precision debanding would be guessing at. 「在动画中开启」 follows the
    /// same 动画 detection the shader rules use — cel-shaded flats are where an 8-bit encode bands worst,
    /// and it is the one kind of picture where a live-action grain pass is not hiding the rings.
    /// </summary>
    public static readonly MpvChoice[] DebandModes =
    [
        new(Inherit, "不设置（保持关闭）"),
        new(Auto, "自动（8bit 片源开启）"),
        new(Anime, "在动画中开启"),
        new("yes", "始终开启"),
        new(Off, "始终关闭")
    ];

    /// <summary>
    /// HDR 处理, applied only to a source Emby reports as HDR. An SDR file is never touched by this.
    /// </summary>
    public static readonly MpvChoice[] HdrModes =
    [
        new(Inherit, "不干预（由 mpv 自己决定）"),
        new("tonemap", "映射到 SDR（推荐）"),
        new("passthrough", "直通给显示器（需要 HDR 屏）")
    ];

    public static readonly MpvChoice[] Channels =
    [
        new(Inherit, "不指定（等同自动·安全）"),
        new("auto-safe", "自动（安全）"),
        new(Auto, "自动（信任设备上报）"),
        new("stereo", "立体声 2.0"),
        new("5.1", "5.1 环绕"),
        new("7.1", "7.1 环绕")
    ];

    public static readonly MpvChoice[] DynamicRange =
    [
        new(Inherit, "不指定（等同关闭压缩）"),
        new("0", "关闭（保留原始动态范围）"),
        new("0.5", "轻度压缩"),
        new("1", "完全压缩（对白最清楚）")
    ];

    /// <summary>The codecs mpv can hand to the receiver untouched, as <c>audio-spdif</c> spells them.</summary>
    public static readonly MpvChoice[] PassthroughCodecs =
    [
        new("ac3", "AC3"),
        new("eac3", "EAC3"),
        new("dts", "DTS"),
        new("dts-hd", "DTS-HD"),
        new("truehd", "True HD")
    ];

    /// <summary>
    /// 画质预设: a whole scaler chain in one pick, the way an mpv.conf <c>profile=</c> line does it.
    /// <para>
    /// <c>default</c> and <c>high-quality</c> are mpv's own built-in profiles, compiled into the binary
    /// rather than read from a config file, so they still exist under <c>--no-config</c>. <c>HQ</c> is not
    /// — it was a <c>[HQ]</c> section in the user's own mpv.conf, so it is spelled out option by option in
    /// <see cref="QualityPresetOptions"/> instead of named.
    /// </para>
    /// <para>
    /// Presets go out first, before the per-option 视频输出 settings and before the shader group, so both
    /// of those still win: a preset is a starting point, not a lock. In particular a 着色器配置组 brings its
    /// own scalers and will replace the preset's.
    /// </para>
    /// </summary>
    public static readonly MpvChoice[] QualityPresets =
    [
        new(DefaultPreset, "default（mpv 出厂画质，不额外套用）"),
        new("high-quality", "high-quality（mpv 内置高画质预设）"),
        new(HqPreset, "HQ（ewa_lanczossharp 放大 + lanczos 缩小）")
    ];

    /// <summary>
    /// mpv's own <c>[default]</c> profile, which under <c>--no-config</c> is empty — so this preset is
    /// implemented by sending nothing at all. Behaviourally identical to <c>--profile=default</c>, and it
    /// cannot fail on an mpv build that spells the profile differently, which an unknown profile name
    /// would (mpv exits before playing anything).
    /// </summary>
    private const string DefaultPreset = "default";

    /// <summary>The user's own <c>[HQ]</c> profile, re-expressed in C#; see <see cref="QualityPresetOptions"/>.</summary>
    private const string HqPreset = "HQ";

    /// <summary>
    /// 「HQ」 spelled out. Copied from the <c>[HQ]</c> section of <c>mpv_config-2026.08.12</c> so the
    /// picture matches what that config produced: sharpened EWA Lanczos going up, plain Lanczos coming
    /// down, light antiringing on both, and sigmoid rather than linear light for the upscale.
    /// <para>
    /// <c>deband=no</c> is part of the profile and is kept, but 去色带 is emitted after this and wins
    /// whenever it is set to anything other than 「不设置」.
    /// </para>
    /// </summary>
    private static readonly KeyValuePair<string, string>[] QualityPresetOptions =
    [
        new("scale", "ewa_lanczossharp"),
        new("cscale", "bilinear"),
        new("dscale", "lanczos"),
        new("scale-antiring", "0.5"),
        new("dscale-antiring", "0.5"),
        new("linear-upscaling", Off),
        new("sigmoid-upscaling", "yes"),
        new("correct-downscaling", "yes"),
        new("linear-downscaling", Off),
        new("deband", Off)
    ];

    /// <summary>字幕文字颜色. A short list of readable ones rather than a colour picker.</summary>
    public static readonly MpvChoice[] SubtitleColors =
    [
        new(Inherit, "不设置（纯文本字幕为白色）"),
        new("#FFFFFF", "白色"),
        new("#F5F5DC", "米白"),
        new("#FFF200", "亮黄"),
        new("#FFD24A", "琥珀黄"),
        new("#B4E1FF", "淡蓝"),
        new("#000000", "黑色")
    ];

    /// <summary>字体描边 widths in mpv units; mpv's own default is 3.</summary>
    public static readonly MpvChoice[] SubtitleBorders =
    [
        new(Inherit, "不设置（纯文本字幕为中等 3）"),
        new("0", "无描边"),
        new("0.5", "极细"),
        new("1.5", "细"),
        new("3", "中等"),
        new("4.5", "粗")
    ];

    /// <summary>描边颜色. Nearly always black — the point of the outline is to survive any picture behind it.</summary>
    public static readonly MpvChoice[] SubtitleBorderColors =
    [
        new(Inherit, "不设置（纯文本字幕为黑色）"),
        new("#000000", "黑色"),
        new("#1E1E1E", "深灰"),
        new("#FFFFFF", "白色")
    ];

    /// <summary>字幕阴影 offset in mpv units; 0 is no shadow.</summary>
    public static readonly MpvChoice[] SubtitleShadows =
    [
        new(Inherit, "不设置（无阴影）"),
        new("0", "无阴影"),
        new("0.5", "轻"),
        new("1", "中等"),
        new("2", "重")
    ];

    /// <summary>
    /// 字幕编码 for a text subtitle that is not valid UTF-8. mpv checks UTF-8 first whatever this says,
    /// so naming a legacy codepage only affects the files that need it.
    /// </summary>
    public static readonly MpvChoice[] SubtitleCodepages =
    [
        new(Inherit, "自动识别（优先按 UTF-8 读）"),
        new("gb18030", "简体中文（GB18030）"),
        new("big5", "繁体中文（Big5）"),
        new("shift-jis", "日文（Shift-JIS）"),
        new("cp1251", "西里尔文（CP1251）"),
        new("cp1252", "西欧（CP1252）")
    ];

    /// <summary>字幕背景颜色, plus the explicit 「无」 that turns the box off.</summary>
    public static readonly MpvChoice[] SubtitleBackColors =
    [
        new(Inherit, "不设置（沿用字幕自带样式）"),
        new(NoBackground, "无背景"),
        new("#000000", "黑色"),
        new("#1E1E1E", "深灰"),
        new("#2B3A55", "深蓝"),
        new("#FFFFFF", "白色")
    ];

    /// <summary>Frame rate above which display sync is more trouble than it is worth.</summary>
    private const double HighFrameRateThreshold = 47;

    /// <summary>Every option pair these settings ask for, in the order they should reach mpv.</summary>
    /// <param name="source">
    /// The video about to play, for the rules that depend on it (去色带自动, HDR, 高帧率). Null skips all
    /// of them, which is what the settings page's own preview wants.
    /// </param>
    /// <param name="animated">
    /// Whether Emby's metadata says this is 动画, for 去色带 的「在动画中开启」. Decided by
    /// <see cref="Playback.ShaderGroupResolver"/>, which already has to answer the same question for the
    /// 动画配置组 rule.
    /// </param>
    public static IReadOnlyList<KeyValuePair<string, string>> Build(
        VideoSettings video,
        AudioSettings audio,
        PlaybackSettings? subtitles = null,
        SourceProfile? source = null,
        bool animated = false)
    {
        var options = new List<KeyValuePair<string, string>>(32);

        AddQualityPreset(options, video.QualityPreset);

        Add(options, "vo", video.Renderer);
        Add(options, "gpu-api", video.GpuApi);
        Add(options, "hwdec", video.HardwareDecoding);
        Add(options, "video-output-levels", video.OutputLevels);
        if (video.Deinterlace) Add(options, "deinterlace", "yes");

        // interpolation without display sync is a no-op that only logs a warning, so the sync mode
        // comes along with it unless the user asked for a specific one.
        var sync = video.VideoSync;
        if (video.Interpolation)
        {
            Add(options, "interpolation", "yes");

            // oversample is the cheap end of mpv's temporal filters: it only blends the frames that
            // straddle a display refresh instead of running a real reconstruction filter.
            Add(options, "tscale", "oversample");
            if (sync.Length == 0) sync = "display-resample";
        }

        Add(options, "video-sync", sync);

        AddDither(options, video.Dither);
        AddDeband(options, video.Deband, source, animated);
        AddHdr(options, video.HdrMode, source);
        AddHighFrameRate(options, video, source);

        if (video.NetworkCacheMegabytes > 0)
        {
            var bytes = (long)video.NetworkCacheMegabytes * 1024 * 1024;
            Add(options, "cache", "yes");
            Add(options, "demuxer-max-bytes", bytes.ToString(CultureInfo.InvariantCulture));

            // Half as much again behind the play position, so a short seek back does not re-download.
            Add(options, "demuxer-max-back-bytes", (bytes / 2).ToString(CultureInfo.InvariantCulture));
        }

        Add(options, "audio-channels", audio.Channels);

        // Filtered through the catalogue rather than joined as stored: the order stays the same however
        // the boxes were ticked, and a hand-edited settings.json cannot smuggle a codec name mpv would
        // reject. (SettingsMigration.Normalize does the same thing, but this is the seam mpv sees.)
        var passthrough = PassthroughCodecs
            .Where(codec => audio.PassthroughCodecs.Contains(codec.Value, StringComparer.OrdinalIgnoreCase))
            .Select(codec => codec.Value)
            .ToList();
        if (passthrough.Count > 0) Add(options, "audio-spdif", string.Join(",", passthrough));

        Add(options, "ad-lavc-ac3drc", audio.DynamicRange);
        if (audio.ExclusiveMode) Add(options, "audio-exclusive", "yes");

        // 音量 as the player was last left. mpv is started with its own config blocked, so 「the volume
        // nobody set」 is always 100 and this option is the only thing that carries a level from one file
        // to the next; 100 itself is left unsaid because that is already where a fresh mpv starts.
        if (audio.Volume is >= 0 and < 100)
        {
            Add(options, "volume", audio.Volume.ToString(CultureInfo.InvariantCulture));
        }

        // mpv takes seconds; the settings page asks for milliseconds, as every other player does.
        if (audio.DelayMilliseconds != 0)
        {
            Add(options, "audio-delay", (audio.DelayMilliseconds / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
        }

        if (subtitles is not null) AddSubtitleStyle(options, subtitles, source);

        return options;
    }

    /// <summary>
    /// 画质预设. <c>default</c> sends nothing (mpv's own <c>[default]</c> profile is empty without a
    /// config file), <c>HQ</c> is expanded into the option block it stood for, and anything else is
    /// handed to mpv as a <c>profile=</c> — which is how <c>high-quality</c> reaches it.
    /// </summary>
    private static void AddQualityPreset(List<KeyValuePair<string, string>> options, string preset)
    {
        var value = preset.Trim();
        if (value.Length == 0 || value.Equals(DefaultPreset, StringComparison.OrdinalIgnoreCase)) return;

        if (value.Equals(HqPreset, StringComparison.OrdinalIgnoreCase))
        {
            options.AddRange(QualityPresetOptions);
            return;
        }

        Add(options, "profile", value);
    }

    /// <summary>
    /// 抖动. <c>dither-depth</c> is what actually switches the feature on or off, however the algorithm
    /// is set, so the two always travel as a pair. <c>auto</c> asks mpv for the depth of the output
    /// surface rather than a guessed number; it is also mpv's own default here, and naming it explicitly
    /// is what makes 「关闭」 reversible without a restart.
    /// </summary>
    private static void AddDither(List<KeyValuePair<string, string>> options, string dither)
    {
        var value = dither.Trim();
        if (value.Length == 0) return;

        if (value.Equals(Off, StringComparison.OrdinalIgnoreCase))
        {
            Add(options, "dither-depth", Off);
            return;
        }

        Add(options, "dither-depth", Auto);
        Add(options, "dither", value);

        // 6 is the size the pattern repeats at. Larger is less visible and costs a bigger LUT; this is
        // the value the pattern was tuned around and mpv's own default.
        if (value.Equals("fruit", StringComparison.OrdinalIgnoreCase)) Add(options, "dither-size-fruit", "6");
    }

    /// <summary>
    /// 去色带, with the light settings a 1440p iGPU can afford: one iteration and a small radius, which
    /// removes the banding a low-bitrate 8-bit encode leaves in a gradient without visibly softening
    /// detail. mpv's own defaults are three times as expensive.
    /// </summary>
    private static void AddDeband(List<KeyValuePair<string, string>> options, string mode, SourceProfile? source, bool animated)
    {
        var value = mode.Trim();
        if (value.Length == 0) return;

        var on = value switch
        {
            // Banding is an 8-bit problem. A 10-bit source already carries the precision debanding
            // would be inventing, and Emby reports 0 when it does not know — treated as 8-bit,
            // because that is what an unlabelled file almost always is.
            Auto => (source?.BitDepth ?? 0) <= 8,

            // 「在动画中开启」: flat cel shading is where banding is most visible and where the deband
            // pass costs the least detail, so this one follows the metadata rather than the bit depth.
            Anime => animated,
            "yes" => true,
            _ => false
        };

        if (!on)
        {
            Add(options, "deband", Off);
            return;
        }

        Add(options, "deband", "yes");
        Add(options, "deband-iterations", "1");
        Add(options, "deband-threshold", "48");
        Add(options, "deband-range", "16");
        Add(options, "deband-grain", "16");
    }

    /// <summary>
    /// HDR 处理, only for a source Emby reports as HDR. Nothing is sent for an SDR file: every one of
    /// these options would be a no-op on it, and sending a tone-mapping curve for a file that has no
    /// tones to map only makes the log harder to read.
    /// </summary>
    private static void AddHdr(List<KeyValuePair<string, string>> options, string mode, SourceProfile? source)
    {
        var value = mode.Trim();
        if (value.Length == 0 || source is not { IsHdr: true }) return;

        if (value.Equals("passthrough", StringComparison.OrdinalIgnoreCase))
        {
            // Hand the display the HDR signal and let it do the mapping. clip is the only honest
            // curve here: anything else would tone-map twice.
            Add(options, "target-colorspace-hint", "yes");
            Add(options, "tone-mapping", "clip");
            Add(options, "hdr-compute-peak", Off);
            return;
        }

        Add(options, "target-colorspace-hint", Off);
        Add(options, "tone-mapping", Auto);

        // Measures each scene's real peak on the GPU instead of trusting the file's metadata, which is
        // routinely wrong by a factor of several. auto lets mpv skip it where it would cost too much.
        Add(options, "hdr-compute-peak", Auto);
    }

    /// <summary>
    /// 高帧率片源回退到音频同步. Display-sync resamples audio to the refresh rate, which works well for
    /// 24fps material on a 60Hz+ screen; once the source is near or above the refresh rate there is no
    /// spare cadence to resample into and it turns into dropped or repeated frames. Interpolation goes
    /// with it, because a 60fps source has nothing left to interpolate.
    /// </summary>
    private static void AddHighFrameRate(List<KeyValuePair<string, string>> options, VideoSettings video, SourceProfile? source)
    {
        if (!video.HighFrameRateAudioSync) return;
        if (source is not { FrameRate: > HighFrameRateThreshold }) return;

        Add(options, "video-sync", "audio");
        Add(options, "interpolation", Off);
    }

    /// <summary>
    /// 字幕外观. The colours go out in mpv's <c>r/g/b/a</c> float form rather than <c>#AARRGGBB</c>:
    /// both are accepted, and in the float form 1.0 unambiguously means opaque, which is what 背景不透明度
    /// has to control.
    /// <para>
    /// <c>sub-border-size</c> and <c>sub-border-color</c> are the pre-0.39 names of what mpv now calls
    /// <c>sub-outline-*</c> and are still accepted as aliases. The old names are the compatible ones:
    /// the new ones simply do not exist on an older mpv, and an unknown option stops playback dead.
    /// </para>
    /// </summary>
    private static void AddSubtitleStyle(List<KeyValuePair<string, string>> options, PlaybackSettings subtitles, SourceProfile? source)
    {
        Add(options, "sub-codepage", subtitles.SubtitleCodepage);

        if (subtitles.SubtitleFontSize > 0)
            Add(options, "sub-font-size", subtitles.SubtitleFontSize.ToString(CultureInfo.InvariantCulture));

        Add(options, "sub-bold", subtitles.SubtitleBold ? "yes" : Off);
        Add(options, "sub-color", ToMpvColor(subtitles.SubtitleColor, 100));
        Add(options, "sub-border-size", subtitles.SubtitleBorderSize);
        Add(options, "sub-border-color", ToMpvColor(subtitles.SubtitleBorderColor, 100));
        Add(options, "sub-shadow-offset", subtitles.SubtitleShadowOffset);

        var background = subtitles.SubtitleBackColor.Trim();
        if (background.Length > 0)
        {
            // 无背景 is a transparent box rather than an unset option: the point of choosing it is to
            // override the box a subtitle's own styling would otherwise draw.
            Add(options, "sub-back-color", background.Equals(NoBackground, StringComparison.OrdinalIgnoreCase)
                ? ToMpvColor("#000000", 0)
                : ToMpvColor(background, subtitles.SubtitleBackOpacity));
        }

        // A PGS track from a 2.39:1 disc is authored for the full frame including the black bars, so on
        // a cropped or wider-than-16:9 encode its lines land off the bottom of the picture. Stretching
        // the subtitle canvas to the window puts them back on screen.
        if (subtitles.StretchWideImageSubtitles && source is { AspectRatio: > 1.79 })
            Add(options, "stretch-image-subs-to-screen", "yes");
    }

    /// <summary><c>#RRGGBB</c> plus an opacity percentage as mpv's <c>r/g/b/a</c>; null when unset.</summary>
    internal static string? ToMpvColor(string? hex, int opacityPercent)
    {
        var value = (hex ?? "").Trim().TrimStart('#');
        if (value.Length != 6 || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            return null;

        var alpha = Math.Clamp(opacityPercent, 0, 100) / 100.0;
        return string.Join(
            "/",
            Component(packed >> 16),
            Component(packed >> 8),
            Component(packed),
            Format(alpha));

        static string Component(int channel) => Format((channel & 0xFF) / 255.0);

        // Fixed three decimals, so every component of every colour reads the same width — including
        // the whole numbers, which "0.###" would print as a bare 0 or 1.
        static string Format(double part) => part.ToString("0.000", CultureInfo.InvariantCulture);
    }

    private static void Add(List<KeyValuePair<string, string>> options, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        options.Add(new KeyValuePair<string, string>(name, value.Trim()));
    }
}
