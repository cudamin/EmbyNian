using System.Globalization;
using EmbyNian.Configuration;
using EmbyNian.Infrastructure;

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
        // 「跟随插值」 rather than the 「等同音频同步」 this used to say: with 启用插值 on it is not equivalent to
        // audio sync, it resolves to display-resample. Which of the two is in force is on the row's own note,
        // from ResolveSync — the label states that the choice defers, and the note states what it defers to.
        new(Inherit, "不指定（跟随插值）"),
        new("audio", "音频同步"),
        new("display-resample", "显示同步（重采样音频）"),
        new("display-vdrop", "显示同步（丢帧）")
    ];

    /// <summary>
    /// 插值算法 (mpv's <c>tscale</c>): how <c>interpolation</c> blends two neighbouring frames across one
    /// display refresh. 「为当前的插值功能设置更多的可选项」(2026-09-10) — up to then this client always sent
    /// <c>oversample</c>, so that value is the shipped default and an install that never touches the row keeps
    /// exactly the picture it had.
    /// <para>
    /// <c>oversample</c> is the cheap end and the honest one: it only mixes on the refreshes a frame boundary
    /// actually straddles, so each output frame is (at worst) a mix of two real frames and motion cannot smear.
    /// Every other entry is a true reconstruction filter — mpv synthesises the in-between frame from the whole
    /// neighbourhood, which smooths motion further at the cost of possible ringing around sharp moving edges.
    /// mpv's own default is <c>mitchell</c>; it is offered here like any other, not shipped, because 「mpv's
    /// default」 is not a reason on a machine where nobody sent <c>tscale</c> before.
    /// </para>
    /// <para>
    /// No 「不设置」 entry: <see cref="Build"/> names the kernel on every interpolation launch, so an unknown
    /// stored value falls back to the first entry (SettingsMigration) rather than to an empty string that would
    /// leave mpv on mitchell while the page promised something else. Only sent with 启用插值 on — the option
    /// does nothing without it.
    /// </para>
    /// </summary>
    public static readonly MpvChoice[] InterpolationKernels =
    [
        new("oversample", "过采样（最省，运动最干净）"),
        new("mitchell", "Mitchell（mpv 自己的默认，均衡）"),
        new("catmull_rom", "Catmull-Rom（更锐一档）"),
        new("bicubic", "双三次"),
        new("spline36", "Spline36（更锐，轻微振铃）"),
        new("lanczos", "Lanczos（最锐，振铃最明显）")
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

    /// <summary>
    /// 音量均衡 — the cross-codec 「对白听不清」 control, as an <c>af</c> chain.
    /// <para>
    /// It exists because <see cref="DynamicRange"/> is not that control and reads as though it were.
    /// <c>ad-lavc-ac3drc</c> is an option <b>on the AC-3 decoder</b>, and it needs the source to carry DRC
    /// metadata on top of that, so a DTS, TrueHD, AAC or FLAC track does not react to it at all. The row for it
    /// now says so; this row is what actually works on every codec.
    /// </para>
    /// <para>
    /// <b>The two filter strings are this file's, and the player's 「切换 下混滤镜」 row reads them from here</b>
    /// — see <see cref="PlayerMenuCatalog"/>. Written out twice they would drift, and 「the menu and the settings
    /// page disagree about what 音量均衡 means」 is exactly the class of defect this project keeps paying for.
    /// The <c>@label:</c> prefix is mpv's own filter-label syntax: it is what lets the menu's
    /// <c>cycle-values</c> compare one entry against another, and it also makes <c>${af}</c> readable on screen.
    /// </para>
    /// <para>
    /// Three entries, not the four 任务书 2.2 lists: 「不启用」 and 「保留原样」 are the same thing here. Every
    /// playback is a fresh mpv under <c>--no-config</c>, so there is no <c>af</c> residue for an explicit
    /// 「off」 to clear that 「send nothing」 would leave behind — two labels for one behaviour is the 「无」 /
    /// <c>default</c> mistake again, and this file already carries the note about that.
    /// </para>
    /// </summary>
    public static readonly MpvChoice[] VolumeNormalizers =
    [
        new(Inherit, "不启用（保留原始响度）"),
        new(DynAudNorm, "连续跟随（夜里看，安静处自动抬起来）"),
        new(LoudNorm, "对齐到固定响度（一集接一集，各集之间不用再调）")
    ];

    /// <summary>
    /// <c>dynaudnorm</c> rides the level continuously over a moving window — quiet dialogue comes up, a
    /// sudden explosion comes down, and it never needs to know how long the file is.
    /// </summary>
    public const string DynAudNorm = "@dynaudnorm:lavfi=[dynaudnorm=f=500:g=31:p=0.5:m=5:r=0.9]";

    /// <summary>
    /// <c>loudnorm</c> aims at a fixed target (EBU R128, −16 LUFS) instead of following the picture, which is
    /// what makes one episode start at the same loudness as the last.
    /// </summary>
    public const string LoudNorm = "@loudnorm:lavfi=[loudnorm=I=-16:TP=-1.5:LRA=11]";

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
    /// <b>All three are mpv's own built-in profiles</b>, compiled into the binary rather than read from a
    /// config file, so they still exist under <c>--no-config</c> and no option list has to be hand-copied
    /// here. Read out of the shipped <c>libmpv-2.dll</c> (v0.41.0-923) and confirmed against the external
    /// <c>mpv.exe</c> (v0.41.0-922) with <c>--show-profile</c>, so this is what they contain rather than what
    /// their names suggest:
    /// </para>
    /// <para>
    /// <c>fast</c> — <c>scale=bilinear dscale=bilinear dither=no correct-downscaling=no
    /// linear-downscaling=no sigmoid-upscaling=no hdr-compute-peak=no allow-delayed-peak-detect=yes</c>.
    /// <c>high-quality</c> — <c>scale=ewa_lanczossharp scale-antiring=0.6 hdr-peak-percentile=99.995
    /// hdr-contrast-recovery=0.30</c>. <c>default</c> is empty without a config file, so it is implemented by
    /// sending nothing at all.
    /// </para>
    /// <para>
    /// <b>「default」 is the first entry and stays spelled that way</b> — it was briefly relabelled 「无」 with an
    /// empty value on 2026-09-04, and the user sent it straight back (「改回 default」). Both spellings send
    /// nothing at all, so this is a naming question and his to settle: the row now reads like its two
    /// neighbours, all three being names mpv itself uses. <b>Don't 「tidy」 it into the empty-string 「不设置」
    /// the other lists here use.</b> The one consequence to know is that 播放信息 and the 诊断 page print the
    /// value back, so a playback with no preset applied shows 「画质预设：default」 — which is the same word the
    /// settings page shows, so the two agree.
    /// </para>
    /// <para>
    /// <b>Both of the non-empty ones set scalers the 着色器档位 then overwrites</b>, because the chain goes out
    /// last and nobody can edit someone else's built-in profile. So with a chain running, what is left of
    /// 「fast」 is the dither, light-space and HDR-peak work — not the scaling, and not the shaders, which are
    /// the expensive part. <b>「fast」 only means 「fast」 with 着色器 switched off</b> (or on an 8K source, where
    /// the chain steps aside). That is not a bug to route around: it is the same overlap
    /// <c>high-quality</c> has always had, and a C# branch second-guessing a built-in profile would be a
    /// worse lie than the overlap.
    /// </para>
    /// <para>
    /// <b>A preset must not name <c>scale</c>, <c>cscale</c> or <c>dscale</c> itself.</b> Those three belong
    /// to the 着色器档位 — see <see cref="ShaderGroupCatalog"/>. The deleted 「HQ」 entry (a <c>[HQ]</c> section
    /// hand-copied out of the user's own mpv.conf) did, and the settings page then read 「画质预设 = HQ」 while
    /// the chain's <c>cscale=spline36</c> was what the picture actually came out of: the page was not
    /// describing the picture.
    /// </para>
    /// <para>
    /// Presets go out first, before the per-option 视频输出 settings and before the chain, so both of those
    /// win. The list runs cheapest to most expensive; <c>default</c> stays first because
    /// <c>SettingsMigration</c> falls back to entry 0 for a value it does not recognise.
    /// </para>
    /// </summary>
    public static readonly MpvChoice[] QualityPresets =
    [
        new(DefaultPreset, "default（mpv 出厂画质，不额外套用）"),
        new("fast", "fast（mpv 内置省算力预设）"),
        new("high-quality", "high-quality（mpv 内置高画质预设）")
    ];

    /// <summary>
    /// mpv's own <c>[default]</c> profile, which under <c>--no-config</c> is empty — so this preset is
    /// implemented by sending nothing at all. Behaviourally identical to <c>--profile=default</c>, and it
    /// cannot fail on an mpv build that spells the profile differently, which an unknown profile name
    /// would (mpv exits before playing anything).
    /// </summary>
    private const string DefaultPreset = "default";

    /// <summary>
    /// 描边大小、阴影这两行共用的输入范围，mpv 自己的上下限：mpv.exe 拿到范围外的选项值是不播放而不是
    /// 夹住，所以上限不许松。
    /// </summary>
    public const double SubtitleUnitMinimum = 0;
    public const double SubtitleUnitMaximum = 10;

    /// <summary>
    /// 描边大小（<c>sub-border-size</c>）和阴影（<c>sub-shadow-offset</c>）这两行的自由数字输入共用的
    /// 解析 —「这个不用弄成固定的选项，改成输入数字」（2026-09-06），取代原先各自一张的固定档位表。
    /// <para>
    /// 空串还是「不设置，mpv 自己说了算」（描边是 <b>1.65</b> —— 从自带的 libmpv 选项表上量的
    /// v0.41.0-923、2026-09-05，不是抄文档，文档上的 3 早过时了；阴影默认没有）。数字照收，逗号当小数点，
    /// 范围外的拉回 0–10 里 —— 字号那一行「填更小的会被抬上来」是同一条规矩；不是数的退「不设置」，和
    /// 颜色那三行同一条。存发都还是 <c>sub-border-size</c> 这个旧名，mpv 把它留作 <c>sub-outline-size</c>
    /// 的别名。
    /// </para>
    /// </summary>
    public static string ClampSubtitleUnit(string? value)
    {
        var text = (value ?? "").Trim().Replace(',', '.');
        if (text.Length == 0) return "";

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number))
            return "";

        return Math.Clamp(number, SubtitleUnitMinimum, SubtitleUnitMaximum)
            .ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 字幕编码 for a text subtitle that is not valid UTF-8. mpv checks UTF-8 first whatever this says,
    /// so a modern subtitle file is unaffected either way.
    /// <para>
    /// <b>Naming a codepage switches mpv's detection off</b>, which is why 「自动识别」 is the shipped
    /// default and <c>gb18030</c> — what this client shipped until 2026-09-05 — no longer is. Rendered
    /// both ways that day: a Big5 file read as GB18030 comes out as 「硂琁□∽代刚□辊」, and mpv's own
    /// <c>auto</c> (uchardet, compiled into the bundled libmpv) reads it correctly. The same test on a
    /// GBK 简体 file gave byte-identical frames either way, so detection costs nothing on the files the
    /// old default was chosen for. The manual entries stay for the file detection gets wrong.
    /// </para>
    /// </summary>
    public static readonly MpvChoice[] SubtitleCodepages =
    [
        new(Inherit, "自动识别（先按 UTF-8，再交给 mpv 判断）"),
        new("gb18030", "简体中文（GB18030）"),
        new("big5", "繁体中文（Big5）"),
        new("shift-jis", "日文（Shift-JIS）"),
        new("cp1251", "西里尔文（CP1251）"),
        new("cp1252", "西欧（CP1252）")
    ];

    /// <summary>
    /// 字幕底板. <b>The option that decides whether 底板颜色 below paints a plate at all</b>, and the
    /// reason those two rows used to do nothing: from mpv 0.39 on, <c>sub-back-color</c> and
    /// <c>sub-shadow-color</c> are one value, and whether it is drawn as a drop shadow or as a plate
    /// depends solely on <c>sub-border-style</c> — which this client never sent, so mpv's own
    /// <c>outline-and-shadow</c> stood and every 背景颜色 the user picked went into the shadow.
    /// <para>
    /// Verified by rendering, 2026-09-05: same options, one frame with this unset and one with
    /// <c>background-box</c>. Without it there is no plate on screen at any colour or opacity.
    /// </para>
    /// </summary>
    public static readonly MpvChoice[] SubtitleBackStyles =
    [
        new(Inherit, "关闭（只有描边和阴影）"),
        new("background-box", "贴着字的底板"),
        new("opaque-box", "整行不透明方框")
    ];

    // 底板颜色 no longer has a preset list here: since 2026-09-06 it is a free-form #RRGGBB through the
    // HTML 颜色选择器, validated by SettingsMigration.Rgb on the way in. It is also the 阴影 colour —
    // mpv aliases the two — and 「无背景」, which used to be an entry of the old list, is said by 「关闭」
    // on SubtitleBackStyles above.

    /// <summary>
    /// 外观应用范围, i.e. mpv's <c>sub-ass-override</c>. <b>Everything from 字体 down to 底板 is
    /// ignored on an ASS/SSA subtitle unless this says otherwise</b>: mpv's default is <c>scale</c>,
    /// which lets only <c>sub-scale</c> through and leaves the script's own font, size and colours in
    /// charge. Fansubbed 番剧 and most 压制组 releases carry ASS, so that default quietly emptied nine
    /// rows of this card; a Blu-ray's PGS track is a picture and takes none of them either way.
    /// <para>
    /// Two entries out of mpv's five. <c>no</c> and <c>yes</c> differ from 「跟随」 and 「强制」 only in
    /// corners nobody would pick from a settings page, and <c>strip</c> tears the styling off signs and
    /// karaoke as well — the player's 右键 → 字幕 → 兼容性 still cycles all five for the one film that
    /// needs it.
    /// </para>
    /// </summary>
    public static readonly MpvChoice[] SubtitleStyleScopes =
    [
        new(Inherit, "跟随字幕自带样式（ASS/SSA 用它自己的字体和颜色）"),
        new("force", "强制用下面这些外观（ASS/SSA 也一样）")
    ];

    /// <summary>
    /// Aspect ratio above which a source is 「wider than 16:9」, i.e. letterboxed on this screen. 1.79 rather
    /// than 16÷9 = 1.777… so that a file whose stored dimensions round oddly (1920×1082, a 1440×1080 anamorphic
    /// encode) is not called wide by a rounding error.
    /// <para>
    /// Two settings share it, and they are two halves of the same fact — the picture does not fill the frame:
    /// 字幕拉伸到全屏 (a PGS track authored for the full frame including the bars) and 宽片裁切填充.
    /// </para>
    /// </summary>
    public const double WideAspect = 1.79;

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
    /// <param name="displayRefreshHz">
    /// The refresh rate of the screen the picture will be drawn on, for the refresh-rate half of the
    /// 高帧率回退 rule (<see cref="Configuration.VideoSettings.HighRefreshRateLimitHz"/>).
    /// 0 means 「nobody could say」 and switches that rule off, which is what a test and the settings page get.
    /// </param>
    public static IReadOnlyList<KeyValuePair<string, string>> Build(
        VideoSettings video,
        AudioSettings audio,
        PlaybackSettings? subtitles = null,
        SourceProfile? source = null,
        bool animated = false,
        double displayRefreshHz = 0)
    {
        var options = new List<KeyValuePair<string, string>>(32);

        AddQualityPreset(options, video.QualityPreset);

        Add(options, "vo", video.Renderer);
        Add(options, "gpu-api", video.GpuApi);
        Add(options, "hwdec", video.HardwareDecoding);
        Add(options, "video-output-levels", video.OutputLevels);
        if (video.Deinterlace) Add(options, "deinterlace", "yes");

        // 视频同步 and 插值 are decided together by ResolveSync — one writer, so 「设置页显示的值」 and
        // 「真正发出去的值」 cannot drift apart.
        var (sync, interpolation, _) = ResolveSync(video, source, displayRefreshHz);

        if (interpolation)
        {
            Add(options, "interpolation", "yes");

            // The kernel is the user's pick since 2026-09-10 (InterpolationKernels); oversample is what this
            // always sent before and stays the shipped default. SettingsMigration guarantees a catalogue value,
            // and a hand-built VideoSettings defaults to oversample — but a belt for "" costs one line and
            // keeps the launch from silently falling back to mpv's mitchell.
            var kernel = video.Tscale.Trim();
            Add(options, "tscale", kernel.Length == 0 ? "oversample" : kernel);
        }
        else if (video.Interpolation)
        {
            // 插值开着，但这个片源或这块屏被上面那两条规则否掉了。必须显式发 no 而不是什么都不发：
            // 「不发」在一个复用的 mpv 实例上等于沿用上一部片子的 yes。
            Add(options, "interpolation", Off);
        }

        Add(options, "video-sync", sync);

        AddDither(options, video.Dither);
        AddDeband(options, video.Deband, source, animated);
        AddHdr(options, video.HdrMode, source);

        // 宽于 16:9 的片源默认裁切填充. Only for a source that is actually letterboxed here — sent for a 16:9 file
        // it would crop the picture for nothing. mpv's own default is 0, and every playback is a fresh mpv, so
        // 「off」 needs nothing sent; the player menu's 开/关 裁切填充 row still overrides it per film.
        if (video.FillWideSources && source is { AspectRatio: > WideAspect }) Add(options, "panscan", "1.0");

        // 自动 ICC 校色, written next to HDR because the two interact: with a display profile loaded mpv maps
        // into that profile's space, so HDR 直通 stops being 直通. Only ever sent as 「on」 — MpvBaseline
        // states icc-profile-auto=no on every launch, so 「off」 is already the floor and sending it again
        // here would be two layers writing one option for no gain. Same shape as every other bool here.
        if (video.IccProfileAuto) Add(options, "icc-profile-auto", "yes");

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

        // 音频输出设备 goes out before 独占模式 for readability only — mpv takes them in either order. Empty is
        // 「跟随系统默认」, i.e. mpv's own auto, so nothing is sent.
        Add(options, "audio-device", audio.Device);
        if (audio.ExclusiveMode) Add(options, "audio-exclusive", "yes");

        // 音量均衡 and 5.1 下混归一化 are two separate options on purpose, and this is the one place worth
        // saying so: af is a filter *list*, and folding the downmix switch into it as a second filter would
        // mean one of the two silently replacing the other. audio-normalize-downmix is a flag on mpv's own
        // conversion stage, so the two compose without either knowing about the other.
        Add(options, "af", audio.VolumeNormalize);
        if (audio.NormalizeDownmix) Add(options, "audio-normalize-downmix", "yes");

        // 音量 as the player was last left. mpv is started with its own config blocked, so 「the volume
        // nobody set」 is always 100 and this option is the only thing that carries a level from one file
        // to the next; 100 itself is left unsaid because that is already where a fresh mpv starts.
        //
        // 「not 100」 rather than 「< 100」, which is what this said while the ceiling was 100 and what made
        // raising the ceiling a lie: a stored 130 matched neither branch, so nothing was sent, mpv started at
        // 100 and the rail on screen sat at 130. A regression test covers 130 / 100 / 0.
        //
        // The upper bound stays, and it is not a duplicate of SettingsMigration's clamp: this is the seam mpv
        // sees, and a value above volume-max is not a harmless one — mpv.exe exits on an out-of-range option
        // value rather than playing the file. Above the ceiling nothing is sent, which leaves mpv at its own
        // 100.
        if (audio.Volume is >= 0 and <= AudioSettings.MaxVolume and not 100)
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
    /// 画质预设. <c>default</c> sends nothing at all — mpv's own <c>[default]</c> profile is empty without a
    /// config file, and naming it would be a no-op that could still fail on a build spelling it differently.
    /// Everything else is handed to mpv as a <c>profile=</c>, which is how <c>fast</c> and
    /// <c>high-quality</c> reach it; <c>SettingsMigration</c> guarantees the value is one of
    /// <see cref="QualityPresets"/>, and an unknown profile name would stop mpv before it played anything.
    /// </summary>
    private static void AddQualityPreset(List<KeyValuePair<string, string>> options, string preset)
    {
        var value = preset.Trim();
        if (value.Length == 0 || value.Equals(DefaultPreset, StringComparison.OrdinalIgnoreCase)) return;

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
    /// 字幕外观 as mpv options. The colours go out in mpv's <c>r/g/b/a</c> float form rather than
    /// <c>#AARRGGBB</c>: both are accepted, and in the float form 1.0 unambiguously means opaque, which
    /// is what 底板不透明度 has to control.
    /// <para>
    /// <c>sub-border-size</c> and <c>sub-border-color</c> are the pre-0.39 names of what mpv now calls
    /// <c>sub-outline-*</c> and are still accepted as aliases. The old names are the compatible ones:
    /// the new ones simply do not exist on an older mpv, and an unknown option stops playback dead.
    /// </para>
    /// <para>
    /// <b>Separate from the two file-level options next door</b> (<c>sub-codepage</c> and the image-sub
    /// stretch), and that is the whole reason this is its own function: everything here can be pushed at
    /// a film that is already playing, and neither of those two can — a codepage is used when the
    /// subtitle is decoded, and the stretch depends on the film's shape rather than on this card. See
    /// <see cref="SubtitleStyleOptions"/>.
    /// </para>
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> SubtitleAppearance(PlaybackSettings subtitles)
    {
        var options = new List<KeyValuePair<string, string>>(12);

        // 外观应用范围 goes first for readability: it decides whether any of what follows reaches an
        // ASS/SSA subtitle at all.
        Add(options, "sub-ass-override", subtitles.SubtitleAssOverride);

        Add(options, "sub-font", FontFamilies.Resolve(subtitles.SubtitleFontFamily));

        if (subtitles.SubtitleFontSize > 0)
            Add(options, "sub-font-size", subtitles.SubtitleFontSize.ToString(CultureInfo.InvariantCulture));

        // 字幕缩放 multiplies 字号 for a text subtitle, and is the only size control an ASS subtitle
        // honours without 强制 above. 100 is mpv's own 1.0, so it is left unsaid.
        if (subtitles.SubtitleScalePercent != 100)
        {
            Add(options, "sub-scale",
                (subtitles.SubtitleScalePercent / 100.0).ToString("0.##", CultureInfo.InvariantCulture));
        }

        Add(options, "sub-bold", subtitles.SubtitleBold ? "yes" : Off);
        Add(options, "sub-color", ToMpvColor(subtitles.SubtitleColor, 100));
        Add(options, "sub-border-size", subtitles.SubtitleBorderSize);
        Add(options, "sub-border-color", ToMpvColor(subtitles.SubtitleBorderColor, 100));
        Add(options, "sub-shadow-offset", subtitles.SubtitleShadowOffset);

        // 字幕底板 and its colour. The style is what makes the colour a plate instead of a shadow, so
        // it is sent whether or not a colour was picked: 底板 on with 颜色 不设置 is a plate in mpv's
        // own black, which is a perfectly good answer and used to be unreachable.
        Add(options, "sub-border-style", subtitles.SubtitleBackStyle);
        Add(options, "sub-back-color", ToMpvColor(subtitles.SubtitleBackColor, subtitles.SubtitleBackOpacity));

        return options;
    }

    /// <summary>
    /// Every mpv option <see cref="SubtitleAppearance"/> can emit, whether or not it emits one today.
    /// <para>
    /// This list is what makes 「改一行，正在播的片子立刻跟上」 honest. An option the settings leave at
    /// 「不设置」 is not sent at all, which is right at launch — mpv's own default stands — and wrong on
    /// a running player, where 「not sent」 means 「keep the value I sent a moment ago」. So the live path
    /// walks this list and asks mpv for the default of anything the settings no longer name. A test
    /// compares the two directions, because a new appearance row that forgets to appear here would only
    /// misbehave when it is switched back off.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> SubtitleStyleOptions { get; } =
    [
        "sub-ass-override", "sub-font", "sub-font-size", "sub-scale", "sub-bold", "sub-color",
        "sub-border-size", "sub-border-color", "sub-shadow-offset", "sub-border-style", "sub-back-color"
    ];

    private static void AddSubtitleStyle(List<KeyValuePair<string, string>> options, PlaybackSettings subtitles, SourceProfile? source)
    {
        options.AddRange(SubtitleAppearance(subtitles));

        // 字幕编码 is decided when the subtitle is decoded rather than when it is drawn, which is why it
        // is here and not in the appearance list above.
        Add(options, "sub-codepage", subtitles.SubtitleCodepage);

        // A PGS track from a 2.39:1 disc is authored for the full frame including the black bars, so on
        // a cropped or wider-than-16:9 encode its lines land off the bottom of the picture. Stretching
        // the subtitle canvas to the window puts them back on screen.
        if (subtitles.StretchWideImageSubtitles && source is { AspectRatio: > WideAspect })
            Add(options, "stretch-image-subs-to-screen", "yes");
    }

    /// <summary>
    /// 视频同步 and 插值 as they will actually be sent — the one place that decides either, so the settings page
    /// can state the value in force instead of the value that was stored.
    /// <para>
    /// Four things decide them and this is the whole of the rule. <b>启用插值 raises 视频同步 by itself</b>:
    /// mpv's interpolation without display sync is a no-op that only logs a warning, so the sync mode has to come
    /// along with it unless the user named one. <b>高帧率片源 overrides both</b>: display sync resamples audio to
    /// the refresh rate, which works for 24fps material on a 60Hz+ screen, but once the source approaches the
    /// refresh rate there is no spare cadence to resample into and it turns into dropped or repeated frames —
    /// and a 60fps source has nothing left to interpolate anyway. <b>高刷新率屏幕 overrides both the same way</b>,
    /// for the cost measured on <see cref="Configuration.VideoSettings.HighRefreshRateLimitHz"/> — the user's own
    /// number since 2026-09-10, 120 out of the box: the whole final pass runs once per refresh under display sync,
    /// and on a screen that fast there is next to no judder left for it to remove.
    /// </para>
    /// <para>
    /// Both overrides are one switch — 设置 → 视频输出 → 高帧率或高刷新率时使用音频同步 — because they are one
    /// judgement: 「display sync is being charged for more than it returns here」. Turning it off gives display sync
    /// back on any screen, which is the escape hatch for a 60Hz projector or a screen this rule reads wrongly.
    /// </para>
    /// <para>
    /// It exists because 「视频同步 = 不指定（等同音频同步）」 was on screen while <c>display-resample</c> was in
    /// force — the same 「界面在骗人」 shape as 画质预设 writing <c>scale</c> under a chain that overwrote it. The
    /// cure there and here is the same: one writer, and the page reads what the writer decided. A unit test
    /// compares this against what <see cref="Build"/> actually emits, in both directions.
    /// </para>
    /// </summary>
    /// <param name="source">
    /// The film about to play, when it is known. Null is the settings page's case — nobody is playing anything,
    /// so the frame-rate override cannot apply and the page says so in words instead.
    /// </param>
    /// <param name="displayRefreshHz">
    /// The screen the picture will be drawn on. 0 is 「not known」 — the settings page again, whose own window may
    /// not even be on the monitor the film will play on, so that row states this rule in words too.
    /// </param>
    /// <returns>
    /// The two values mpv will be given, plus why display sync stood down when it did — one sentence for the log
    /// and for the settings page, null when nothing was overridden. Kept in the same return as the decision so a
    /// second function cannot drift away from the rule it is explaining.
    /// </returns>
    public static (string Sync, bool Interpolation, string? StandDown) ResolveSync(
        VideoSettings video,
        SourceProfile? source = null,
        double displayRefreshHz = 0)
    {
        var chosen = (video.VideoSync ?? "").Trim();

        if (video.HighFrameRateAudioSync)
        {
            if (source is { FrameRate: > HighFrameRateThreshold } fast)
            {
                return ("audio", false,
                    $"片源 {fast.FrameRate:0.###}fps 超过 {HighFrameRateThreshold:0}，显示同步已经没有多余的节拍可以重采样，"
                    + "本次回到音频同步");
            }

            // The threshold is the user's own number since 2026-09-10 (VideoSettings.HighRefreshRateLimitHz);
            // the measurement behind it is on that field. 120 is where it shipped, from the user's own mpv.conf.
            if (displayRefreshHz > video.HighRefreshRateLimitHz)
            {
                return ("audio", false,
                    $"屏幕 {displayRefreshHz:0.###}Hz 超过 {video.HighRefreshRateLimitHz:0}，显示同步要按刷新率重跑最后一趟渲染、"
                    + "而这个刷新率下几乎没有抖动可补，本次回到音频同步");
            }
        }

        if (!video.Interpolation) return (chosen, false, null);

        return (chosen.Length == 0 ? "display-resample" : chosen, true, null);
    }

    /// <summary>
    /// What one of these catalogues calls a value — 「显示同步（重采样音频）」 for <c>display-resample</c>.
    /// For the settings page, so a row can name a value it did not itself offer.
    /// </summary>
    public static string Describe(IReadOnlyList<MpvChoice> catalogue, string? value)
    {
        var trimmed = (value ?? "").Trim();
        foreach (var choice in catalogue)
        {
            if (string.Equals(choice.Value, trimmed, StringComparison.OrdinalIgnoreCase)) return choice.Label;
        }

        return trimmed.Length == 0 ? "" : trimmed;
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
