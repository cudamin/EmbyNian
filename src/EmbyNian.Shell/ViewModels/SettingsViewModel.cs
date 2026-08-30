using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyNian.Configuration;
using EmbyNian.Mpv;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Theming;
using Microsoft.UI.Xaml;

namespace EmbyNian.Shell.ViewModels;

/// <summary>One card on the settings page: a heading, a sentence about it, and the rows under it.</summary>
public sealed partial class SettingSection : ObservableObject
{
    internal SettingSection(string category, string title, string description, IReadOnlyList<SettingRow> rows)
    {
        Category = category;
        Title = title;
        Description = description;
        Rows = rows;
    }

    /// <summary>
    /// The entry in the left-hand list that shows this card. Kept separate from <see cref="Title"/> because
    /// one card's heading is longer than its category name — 「画质与着色器」 under 「着色器」 — and matching
    /// the two by substring, as this page used to, makes every future heading a trap.
    /// </summary>
    public string Category { get; }

    public string Title { get; }

    public string Description { get; }

    public IReadOnlyList<SettingRow> Rows { get; }

    /// <summary>
    /// 卡片牌子右端那个读数：这张卡管着几件事。
    /// <para>
    /// 数的是设置的件数，不是树上的容器数（那是 <see cref="SettingsViewModel.Containers"/> 的活）—— 一组
    /// 开关是好几件事，而包着它们的那一行本身不是一件；一行色板是一件事，不是六件。
    /// </para>
    /// </summary>
    public string Count => $"{Rows.Sum(row => row is SettingToggleGroupRow group ? group.Toggles.Count : 1)} 项";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SectionVisibility))]
    public partial bool IsVisible { get; set; }

    /// <summary>
    /// Every card is built and kept; picking a category only changes which one is shown. The page has no
    /// server data in it, so building all eight costs nothing measurable, and a card that stays alive keeps
    /// any half-typed config file across a trip through the category list.
    /// </summary>
    public Visibility SectionVisibility => IsVisible ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// The settings page, as data. Builds the eight cards and their rows out of <see cref="AppSettings"/> and
/// the mpv option catalogues, and saves the settings document on every edit.
/// <para>
/// There is no 「loading」 flag here. Each row reads its starting value in its own constructor and only
/// writes from that point on, so nothing has to suppress writes while the page is being built — which is
/// what the page-wide flag was for, and it had to be held during any programmatic refresh, suppressing
/// genuine edits along with the spurious ones.
/// </para>
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private static readonly (string Label, MpvBackendKind Value)[] Backends =
    [
        ("内置 libmpv（窗口内播放）", MpvBackendKind.BuiltInLibMpv),
        ("外部 mpv.exe（独立窗口）", MpvBackendKind.ExternalMpv)
    ];

    private static readonly (string Label, SkipSectionMode Value)[] SkipModes =
    [
        ("询问（显示跳过按钮）", SkipSectionMode.Ask),
        ("自动跳过", SkipSectionMode.Auto),
        ("关闭", SkipSectionMode.Off)
    ];

    private static readonly (string Label, SubtitleMode Value)[] SubtitleModes =
    [
        ("总是显示匹配字幕", SubtitleMode.Always),
        ("只显示强制字幕", SubtitleMode.ForcedOnly),
        ("音轨为外语时显示", SubtitleMode.ForeignAudioOnly),
        ("从不显示", SubtitleMode.Off)
    ];

    /// <summary>
    /// The stored value is the catalogue's own label, not a language code — that is what
    /// <see cref="PlaybackSettings.AudioLanguage"/> has always held, and the track matcher compares against
    /// the same catalogue.
    /// </summary>
    private static readonly (string Label, string Value)[] AudioLanguages =
    [
        ("默认音轨（不按语言挑选）", ""),
        .. TrackLanguagePriority.Catalogue.Select(item => (Label: item.Label, Value: item.Label))
    ];

    private ISettingsService? _settings;
    private ShaderStaging? _shaders;
    private FontLibrary? _fonts;
    private MpvConfigLocation? _location;
    private SettingTextRow? _mpvConfigPath;
    private SettingTextRow? _inputConfigPath;
    private SettingNumberRow? _highResThreshold;
    private SettingNumberRow? _lowResThreshold;
    private SettingFontRow? _subtitleFont;

    /// <summary>The cards, in the order they appear in the left-hand list.</summary>
    private static readonly string[] CardCategories =
        ["播放器", "配置文件", "播放行为", "字幕", "视频输出", "音频输出", "着色器", "界面"];

    /// <summary>
    /// 需求 2 的后半句：「诊断和服务器移动到设置里」，加上需求 8 的 Emby 网页控制台. Entries in the same list
    /// that are not cards but whole pages — a server list that talks to the network, a log view that tails a
    /// file, an embedded browser on the server's own console — hosted in the settings page's own frame rather
    /// than flattened into setting rows they do not fit.
    /// <para>
    /// Public and static because three places have to agree on the same names: the list built here, the
    /// page's <c>Hosted</c> switch that knows what to navigate to, and the self-check's comparison of the
    /// list against the cards — which without this would report them as entries selecting nothing.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> HostedCategories { get; } = ["服务器", "诊断", DashboardCategory];

    /// <summary>
    /// 需求 8 的那一页. Named because the page's navigation switch and the self-check both have to say it,
    /// and a string literal in three files is a rename waiting to go wrong.
    /// </summary>
    public const string DashboardCategory = "服务器控制台";

    /// <summary>The left-hand list. Order is the order of the cards, then the hosted pages.</summary>
    public IReadOnlyList<string> Categories { get; } = [.. CardCategories, .. HostedCategories];

    public ObservableCollection<SettingSection> Sections { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsHosted))]
    [NotifyPropertyChangedFor(nameof(CardsVisibility))]
    [NotifyPropertyChangedFor(nameof(HostedVisibility))]
    public partial string SelectedCategory { get; set; } = "播放器";

    /// <summary>
    /// Whether the selected entry is one of the two pages rather than one of the cards. The page watches
    /// this to navigate its frame, and the two visibilities below are the same question drawn.
    /// </summary>
    public bool ShowsHosted => HostedCategories.Contains(SelectedCategory, StringComparer.Ordinal);

    public Visibility CardsVisibility => Show(!ShowsHosted);

    public Visibility HostedVisibility => Show(ShowsHosted);

    /// <summary>
    /// Where the page goes when it is opened without a category, or with one it does not have. Not a
    /// literal 「播放器」 in three places: the first card is what this page has always opened on, and the
    /// day its order changes that should follow.
    /// </summary>
    internal static string FirstCardCategory => CardCategories[0];

    /// <summary>The 配置文件 card's editor, kept here so the page can report on it without walking rows.</summary>
    internal SettingConfigEditorRow? ConfigEditor { get; private set; }

    /// <summary>主题那一行的色板，同样是为了让自检不必去树上找它。</summary>
    internal SettingThemeRow? Themes { get; private set; }

    /// <summary>
    /// How many row containers the eight cards would put on the visual tree between them; the self-check
    /// compares this to what really rendered.
    /// </summary>
    internal int RowCount => Sections.Sum(Containers);

    /// <summary>
    /// Each card in the order they appear, and the containers it holds. What the self-check walks: it steps
    /// through the categories and checks the tree against the running total, because a collapsed card never
    /// builds its rows and a single look at this page would leave six of the eight untouched.
    /// </summary>
    internal IReadOnlyList<(string Category, int Rows)> Cards =>
        [.. Sections.Select(section => (section.Category, Containers(section)))];

    /// <summary>
    /// Row containers one card puts on the tree, which is not the same as its row count: a toggle group is one
    /// row holding an <c>ItemsControl</c>, so it realises a container for itself and another for each switch
    /// under it. Counting rows instead would leave the tree looking five containers too full.
    /// </summary>
    internal static int Containers(SettingSection section) =>
        section.Rows.Sum(row => row is SettingToggleGroupRow group ? 1 + group.Toggles.Count : 1);

    /// <summary>
    /// The three capabilities this page needs, and nothing else: the settings document with its write-back,
    /// the shader catalogue the 着色器 card offers, and the machine's fonts for the 字幕 card's picker. Named
    /// rather than handed the whole composition root — a page that edits settings has no business being able
    /// to reach the session or the player.
    /// </summary>
    internal void Attach(ISettingsService settings, ShaderStaging shaders, FontLibrary fonts)
    {
        _settings = settings;
        _shaders = shaders;
        _fonts = fonts;
    }

    /// <summary>
    /// Whether the font picker holds the machine's fonts rather than just the stored one. Only the
    /// self-check reads it: the scan lands a moment after the page does, and a report written in that
    /// moment would be reporting on a list that had not arrived yet.
    /// </summary>
    internal bool FontsReady { get; private set; }

    /// <summary>
    /// The 字幕 → 字体 picker, put through a search and back. Null when the card has not been built.
    /// </summary>
    internal SettingFontRow.Probe? MeasureFontPicker() => _subtitleFont?.Measure();

    /// <summary>
    /// Builds every card. Nothing here waits on the network — the settings document is already in memory —
    /// so this is synchronous work behind the async signature the base class defines for the pages that do
    /// have something to fetch. The one thing it does not have in hand is the font list, which is handed to
    /// the 字幕 card's picker whenever the scan comes back; the page is usable in the meantime.
    /// </summary>
    public override Task ReloadAsync()
    {
        if (_settings is null) return Task.CompletedTask;

        // Built before the cards because two of them share it: the 播放器 card's mpv.exe box changes where
        // these point, and the 配置文件 card's boxes and editor are what they point at.
        _location = new MpvConfigLocation(Settings.Mpv);

        // The page's dialog, handed down: the editor asks before discarding unsaved text, and a row has no
        // XamlRoot to raise one in. Null here is 「不问直接过」, which is what the tests and the self-check get.
        ConfigEditor = new SettingConfigEditorRow(_location) { Confirm = Confirm };

        Sections.Clear();
        Sections.Add(PlayerCard());
        Sections.Add(ConfigFileCard());
        Sections.Add(PlaybackCard());
        Sections.Add(SubtitleCard());
        Sections.Add(VideoCard());
        Sections.Add(AudioCard());
        Sections.Add(ShaderCard());
        Sections.Add(InterfaceCard());

        ShowCategory(SelectedCategory);
        IsReady = true;

        return FillFontsAsync();
    }

    /// <summary>
    /// Hands the font picker the machine's families once they have been read. Awaited by nobody — the page
    /// is shown before this returns — so the row filling itself is the only visible effect.
    /// </summary>
    private async Task FillFontsAsync()
    {
        if (_fonts is null || _subtitleFont is null)
        {
            // Nothing to wait for: without a library the row keeps the one family the settings file names.
            FontsReady = true;
            return;
        }

        var catalogue = await _fonts.LoadAsync().ConfigureAwait(true);

        // The row may have been rebuilt while the scan ran — a second visit to the page does that — so the
        // current one is asked for rather than the one captured above.
        _subtitleFont?.Fill(catalogue);
        FontsReady = true;
    }

    partial void OnSelectedCategoryChanged(string value) => ShowCategory(value);

    private void ShowCategory(string category)
    {
        foreach (var section in Sections)
            section.IsVisible = string.Equals(section.Category, category, StringComparison.Ordinal);
    }

    private AppSettings Settings => _settings!.Settings;

    private void Save() => _settings?.Save();

    // ── Cards ────────────────────────────────────────────────────────────────────────────────────────

    private SettingSection PlayerCard() =>
        new("播放器", "播放器", "选择内置播放器或外部 mpv。外部模式需要填写可执行文件路径。",
        [
            Choice("播放后端", Backends, () => Settings.Mpv.Backend, value => Settings.Mpv.Backend = value),
            PathBox("mpv.exe 路径", "mpv.exe 路径", () => Settings.Mpv.ExecutablePath, value => Settings.Mpv.ExecutablePath = value,
                after: () =>
                {
                    // Both config paths are inferred from this one when they are not set explicitly, so the
                    // two boxes on the next card and the file open in the editor can all have just moved.
                    ReseedConfigPaths();
                    ConfigEditor?.Relocate();
                }),
            Toggle("启用 IPC 进度通道", "关闭后服务器无法获得精确播放位置", () => Settings.Mpv.EnableIpc, value => Settings.Mpv.EnableIpc = value)
        ]);

    private SettingSection ConfigFileCard()
    {
        _mpvConfigPath = ConfigPathRow(MpvConfigKind.Mpv, "mpv.conf 路径");
        _inputConfigPath = ConfigPathRow(MpvConfigKind.Input, "input.conf 路径");

        return new SettingSection("配置文件", "配置文件",
            "查看和编辑外部 mpv 的配置文件。EmbyNian 播放时使用 --no-config（内置 libmpv 默认 config=no），这里的修改不会覆盖本页的客户端专用播放参数。",
            [_mpvConfigPath, _inputConfigPath, ConfigEditor!]);
    }

    /// <summary>
    /// A config path box. The commit hands back <see cref="MpvConfigLocation.Store"/>'s answer rather than
    /// what was typed, because a blank entry and an entry equal to the inferred path are both stored as
    /// 「infer it」 and have to come back on screen as the inferred path.
    /// </summary>
    private SettingTextRow ConfigPathRow(MpvConfigKind kind, string label)
    {
        var location = _location!;
        var editor = ConfigEditor!;
        return new SettingTextRow(label, "默认从 mpv.exe 同目录下的 portable_config 推断", "配置文件路径",
            location.Resolve(kind),
            typed => location.Store(kind, typed),
            Save,
            () => editor.PathChanged(kind));
    }

    private void ReseedConfigPaths()
    {
        if (_location is null) return;
        if (Settings.Mpv.ConfigPath is null) _mpvConfigPath?.Reseed(_location.Resolve(MpvConfigKind.Mpv));
        if (Settings.Mpv.InputConfigPath is null) _inputConfigPath?.Reseed(_location.Resolve(MpvConfigKind.Input));
    }

    private SettingSection PlaybackCard()
    {
        var playback = Settings.Playback;
        return new SettingSection("播放行为", "播放行为", "断点续播、进度上报、片头片尾和音轨选择。",
        [
            Toggle("向服务器汇报播放进度", "关闭后 Emby 不会记录进度", () => playback.ReportProgressToServer, value => playback.ReportProgressToServer = value),
            Toggle("从服务器保存的位置继续", "关闭后每次从头开始", () => playback.ResumeFromSavedPosition, value => playback.ResumeFromSavedPosition = value),
            Toggle("询问后再恢复播放", "关闭后直接跳到保存位置", () => playback.AskBeforeResuming, value => playback.AskBeforeResuming = value),
            Toggle("自动播放下一集", "本集正常结束后播放下一集，支持跨季", () => playback.AutoPlayNextEpisode, value => playback.AutoPlayNextEpisode = value),
            Number("标记已观看阈值（%）", 50, 100, () => playback.MarkWatchedPercent, value => playback.MarkWatchedPercent = value),
            Number("快进跨度（秒）", 1, 600, () => playback.SeekForwardSeconds, value => playback.SeekForwardSeconds = value),
            Number("快退跨度（秒）", 1, 600, () => playback.SeekBackwardSeconds, value => playback.SeekBackwardSeconds = value),
            Number("续播自动快退（秒）", 0, 120, () => playback.ResumeRewindSeconds, value => playback.ResumeRewindSeconds = value),
            Number("进度上报间隔（秒）", 1, 60, () => playback.ProgressReportIntervalSeconds, value => playback.ProgressReportIntervalSeconds = value),
            Choice("跳过片头片尾", SkipModes, () => playback.SkipSections, value => playback.SkipSections = value),

            // Two settings behind one drop-down: the language, and whether language is consulted at all.
            // 「默认音轨」 is the empty language, which is also what makes the mode ServerDefault.
            Choice("音轨语言", AudioLanguages,
                () => playback.AudioTrack == AudioTrackMode.Language ? playback.AudioLanguage : "",
                value =>
                {
                    playback.AudioLanguage = value;
                    playback.AudioTrack = string.IsNullOrWhiteSpace(value) ? AudioTrackMode.ServerDefault : AudioTrackMode.Language;
                },
                describe: StoredAudioLanguage)
        ]);
    }

    /// <summary>
    /// What the 音轨语言 box shows for a value the catalogue does not offer. The stored value is a language
    /// name, but a hand-edited file — or a settings file from a build with a shorter catalogue — can hold a
    /// raw mpv code instead, and the document keeps it: an unrecognised code is exactly how a language this
    /// client has no entry for gets named at all.
    /// <para>
    /// <see cref="TrackLanguagePriority.Describe"/> knows a good many more codes than the catalogue lists, so
    /// <c>hu</c> reads as 匈牙利语 rather than as two letters. When even it cannot name the code it hands the
    /// code straight back, and repeating it twice in one label would be noise.
    /// </para>
    /// </summary>
    private static string StoredAudioLanguage(string code)
    {
        var name = TrackLanguagePriority.Describe(code);
        return string.Equals(name, code, StringComparison.OrdinalIgnoreCase)
            ? $"{code}（设置文件中的值）"
            : $"{name}（设置文件中的 {code}）";
    }

    private SettingSection SubtitleCard()
    {
        var playback = Settings.Playback;
        return new SettingSection("字幕", "字幕", "语言优先级和字幕外观。语言按逗号分隔，越靠前越优先。",
        [
            Languages("字幕语言优先级", "简体中文, 中文, 繁体中文", () => playback.SubtitleLanguages, value => playback.SubtitleLanguages = value),
            Choice("显示模式", SubtitleModes, () => playback.SubtitleMode, value => playback.SubtitleMode = value),
            Toggle("没有匹配语言时使用默认字幕", "文件只有其他语言时仍显示默认字幕", () => playback.SubtitleFallbackToDefault, value => playback.SubtitleFallbackToDefault = value),
            Font("字体", "列出这台机器装的所有字体，可搜索；mpv 认的是字体族名，不是文件路径",
                () => playback.SubtitleFontFamily, value => playback.SubtitleFontFamily = value),
            Number("字号", 0, 160, () => playback.SubtitleFontSize, value => playback.SubtitleFontSize = value,
                "0 表示不指定，由 mpv 自己决定；要指定的话最小 16"),
            Toggle("字幕加粗", "提高复杂画面上的可读性", () => playback.SubtitleBold, value => playback.SubtitleBold = value),
            Mpv("文字颜色", MpvOutputOptions.SubtitleColors, () => playback.SubtitleColor, value => playback.SubtitleColor = value),
            Mpv("描边大小", MpvOutputOptions.SubtitleBorders, () => playback.SubtitleBorderSize, value => playback.SubtitleBorderSize = value),
            Mpv("描边颜色", MpvOutputOptions.SubtitleBorderColors, () => playback.SubtitleBorderColor, value => playback.SubtitleBorderColor = value),
            Mpv("阴影", MpvOutputOptions.SubtitleShadows, () => playback.SubtitleShadowOffset, value => playback.SubtitleShadowOffset = value),
            Mpv("背景颜色", MpvOutputOptions.SubtitleBackColors, () => playback.SubtitleBackColor, value => playback.SubtitleBackColor = value),
            Slider("背景透明度（%）", 0, 100, 5, () => playback.SubtitleBackOpacity, value => playback.SubtitleBackOpacity = value, "拖动调整 mpv sub-back-color 的透明度"),
            Mpv("字幕编码", MpvOutputOptions.SubtitleCodepages, () => playback.SubtitleCodepage, value => playback.SubtitleCodepage = value),
            Toggle("拉伸图形字幕到画面", "宽屏 PGS/VOBSUB 字幕避免落到画面外", () => playback.StretchWideImageSubtitles, value => playback.StretchWideImageSubtitles = value)
        ]);
    }

    private SettingSection VideoCard()
    {
        var video = Settings.Video;
        return new SettingSection("视频输出", "视频输出", "渲染、硬件解码、同步和网络缓冲。",
        [
            Mpv("视频渲染", MpvOutputOptions.Renderers, () => video.Renderer, value => video.Renderer = value),
            Mpv("图形接口", MpvOutputOptions.GpuApis, () => video.GpuApi, value => video.GpuApi = value),
            Mpv("硬件解码", MpvOutputOptions.HardwareDecoders, () => video.HardwareDecoding, value => video.HardwareDecoding = value),
            Mpv("色彩范围", MpvOutputOptions.OutputLevels, () => video.OutputLevels, value => video.OutputLevels = value),
            Mpv("视频同步", MpvOutputOptions.VideoSync, () => video.VideoSync, value => video.VideoSync = value),
            Toggle("启用反交错", "仅对隔行片源有意义", () => video.Deinterlace, value => video.Deinterlace = value),
            Toggle("启用插值", "补偿刷新率不匹配造成的抖动", () => video.Interpolation, value => video.Interpolation = value),
            Toggle("高帧率片源使用音频同步", "超过约 47fps 时避免丢帧", () => video.HighFrameRateAudioSync, value => video.HighFrameRateAudioSync = value),
            Slider("网络缓冲（MB）", 0, 4096, 64, () => video.NetworkCacheMegabytes, value => video.NetworkCacheMegabytes = value, "拖动调整 mpv demuxer-max-bytes；0 表示使用 mpv 默认值"),
            Mpv("抖动", MpvOutputOptions.Dithers, () => video.Dither, value => video.Dither = value),
            Mpv("去色带", MpvOutputOptions.DebandModes, () => video.Deband, value => video.Deband = value),
            Mpv("HDR 处理", MpvOutputOptions.HdrModes, () => video.HdrMode, value => video.HdrMode = value)
        ]);
    }

    private SettingSection AudioCard()
    {
        var audio = Settings.Audio;
        var passthrough = MpvOutputOptions.PassthroughCodecs
            .Select(codec => Toggle($"直通 {codec.Label}", "交给功放原样解码",
                () => audio.PassthroughCodecs.Contains(codec.Value, StringComparer.OrdinalIgnoreCase),
                value =>
                {
                    if (value && !audio.PassthroughCodecs.Contains(codec.Value, StringComparer.OrdinalIgnoreCase))
                        audio.PassthroughCodecs.Add(codec.Value);
                    if (!value)
                        audio.PassthroughCodecs.RemoveAll(item => string.Equals(item, codec.Value, StringComparison.OrdinalIgnoreCase));
                }))
            .ToList();

        return new SettingSection("音频输出", "音频输出", "声道布局、动态范围、独占模式和功放直通。",
        [
            Mpv("扬声器布局", MpvOutputOptions.Channels, () => audio.Channels, value => audio.Channels = value),
            Mpv("动态范围压缩", MpvOutputOptions.DynamicRange, () => audio.DynamicRange, value => audio.DynamicRange = value),
            Toggle("音频独占模式", "播放时占用声卡，避免系统混音", () => audio.ExclusiveMode, value => audio.ExclusiveMode = value),
            Number("全局音频延迟（毫秒）", -5000, 5000, () => audio.DelayMilliseconds, value => audio.DelayMilliseconds = value),
            new SettingToggleGroupRow("直通格式", passthrough)
        ]);
    }

    private SettingSection ShaderCard()
    {
        var shaders = Settings.Shaders;
        var video = Settings.Video;

        (string Label, string Value)[] profiles =
        [
            ("不使用着色器", ""),
            .. _shaders!.Catalog.Select(item => (Label: item.DisplayName, Value: item.Name))
        ];

        // Held so each can put the other back in step; see ReseedThresholds.
        _highResThreshold = Number("高清阈值（高度）", 720, 4320, () => shaders.HighResThresholdHeight, value => shaders.HighResThresholdHeight = value,
            "片源高度达到这个值就算高清", ReseedThresholds);
        _lowResThreshold = Number("低清阈值（高度）", 240, 1080, () => shaders.LowResThresholdHeight, value => shaders.LowResThresholdHeight = value,
            "片源高度不超过这个值就算低清；必须低于高清阈值，填高了会被自动压到它下面", ReseedThresholds);

        return new SettingSection("着色器", "画质与着色器", "画质预设和按内容、分辨率自动切换着色器配置组。",
        [
            Mpv("画质预设", MpvOutputOptions.QualityPresets, () => video.QualityPreset, value => video.QualityPreset = value),
            Toggle("所有视频默认启用", "关闭后仅自动规则命中时使用", () => shaders.ApplyToAllVideos, value => shaders.ApplyToAllVideos = value),
            Choice("默认配置组", profiles, () => shaders.DefaultProfile, value => shaders.DefaultProfile = value),
            Toggle("动画自动切换", "按 Emby 类型和标签关键词匹配", () => shaders.AutoAnimeProfile, value => shaders.AutoAnimeProfile = value),
            Choice("动画配置组", profiles, () => shaders.AnimeProfile, value => shaders.AnimeProfile = value),
            List("动画关键词", "动画, 动漫, Anime", () => shaders.AnimeKeywords, value => shaders.AnimeKeywords = value),
            Choice("高清配置组", profiles, () => shaders.HighResProfile, value => shaders.HighResProfile = value),
            _highResThreshold,
            Choice("低清配置组", profiles, () => shaders.LowResProfile, value => shaders.LowResProfile = value),
            _lowResThreshold,
            Toggle("8K 片源关闭着色器", "宽 ≥7000 或高 ≥3000 的片源不套用任何配置组，避免 GPU 过载", () => shaders.DisableForUltraHighRes, value => shaders.DisableForUltraHighRes = value)
        ]);
    }

    /// <summary>
    /// Puts both threshold boxes back in step with the settings document.
    /// <para>
    /// Saving enforces 低清阈值 &lt; 高清阈值, and the value it moves is not always the one that was edited:
    /// lowering 高清阈值 past 低清阈值 drags 低清阈值 down under it. A row redisplays whatever its own setting
    /// says after the save, which covers the box being typed into — this is what stops the other box from
    /// going on showing a number that is no longer in the file. Same idea as
    /// <see cref="ReseedConfigPaths"/> for the two config paths.
    /// </para>
    /// </summary>
    private void ReseedThresholds()
    {
        _highResThreshold?.Reseed(Settings.Shaders.HighResThresholdHeight);
        _lowResThreshold?.Reseed(Settings.Shaders.LowResThresholdHeight);
    }

    private SettingSection InterfaceCard()
    {
        var ui = Settings.Ui;
        Themes = ThemeSwatches(ui);
        return new SettingSection("界面", "界面", "配色主题、窗口和侧边栏、媒体库分页和海报尺寸。",
        [
            Themes,

            // 这两个和上面那几块色板一样：写进设置之后当场喊一声（ShellPrefs），主窗口和外壳各自跟上。设置页
            // 是另一个窗口，手上没有主窗口的 HWND 也没有那一页，所以只能这么喊。
            Toggle("锁定窗口比例大小", $"拖窗口边沿时保持 {Emby.HomeCarousel.WindowAspect:0.0}:1，主页轮播的大图不会被裁掉更多",
                () => ui.LockWindowShape,
                value =>
                {
                    ui.LockWindowShape = value;
                    ShellPrefs.Apply(ui);
                }),
            Toggle("默认收起侧边栏", "启动时侧边栏只留一条窄图标栏",
                () => ui.CollapseSidebar,
                value =>
                {
                    ui.CollapseSidebar = value;
                    ShellPrefs.Apply(ui);
                }),

            // Both ranges match what SettingsMigration.Normalize clamps these to. They have to: a box narrower
            // than its setting shows a clamped number the file does not contain and writes it back on the next
            // touch, and a box wider than its setting lets a value be typed that the save then silently moves.
            Number("每页条目数", 20, 500, () => ui.PageSize, value => ui.PageSize = value),
            Number("海报宽度（像素）", 120, 340, () => ui.PosterWidth, value => ui.PosterWidth = value),
            Toggle("显示观看状态标记", "在海报角上显示已看和收藏状态", () => ui.ShowWatchedIndicators, value => ui.ShowWatchedIndicators = value)
        ]);
    }

    /// <summary>
    /// 主题色板。点中就立刻换，不等保存 —— 一套配色是看着挑的，不是填完表格再确认的。
    /// <para>
    /// 这里原先是个下拉框。一套主题该让人看见它的颜色，而不是读它的名字，所以现在是几块各自画着自己那套
    /// 配色的方块，点哪块是哪块（见 <see cref="SettingThemeRow"/>）。
    /// </para>
    /// <para>
    /// 直接调 <see cref="ThemeHost.Apply(UiTheme)"/>：它是这次换肤的那一个执行点，同一个程序集里的静态类，
    /// 再包一层接口注进来只是为了好看。写进设置文档的仍然只有 id，落盘由行自己的 <c>Save</c> 负责。
    /// </para>
    /// <para>
    /// 认不出来的 id 走不到这里 —— <c>SettingsMigration.Normalize</c> 已经在读盘时拨回默认那套了 —— 但真漏
    /// 过来一个，角标会落在实际生效的那一套上（见 <c>SettingThemeRow.Sync</c>），而不是几块全不带角标。
    /// </para>
    /// </summary>
    private SettingThemeRow ThemeSwatches(UiSettings ui) =>
        new(
            "主题",
            $"点中即生效，{UiThemes.All.Count} 套配色都过了正文对比度 4.5:1 的门槛。",
            UiThemes.All,
            () => ui.Theme,
            value =>
            {
                ui.Theme = value;
                ThemeHost.Apply(value);
            },
            Save);

    // ── Row factories ────────────────────────────────────────────────────────────────────────────────

    private SettingChoiceRow Pick<T>(string label, string? note, IEnumerable<(string Label, T Value)> options, Func<T> read, Action<T> write, IEqualityComparer<T> comparer, Func<T, string>? describe = null)
    {
        var current = read();
        var choices = new List<SettingChoice>();
        SettingChoice? selected = null;

        foreach (var (text, value) in options)
        {
            var captured = value;
            var choice = new SettingChoice(text, () => write(captured));
            choices.Add(choice);
            if (selected is null && comparer.Equals(captured, current)) selected = choice;
        }

        // What is stored is none of the offered values — a language code this catalogue has no name for, a
        // shader group removed since it was picked. Leaving the box empty hides the setting, and worse, arms
        // it: an unselected ComboBox takes whatever the next click lands on, and the stored value is never
        // written back, so opening the list to see what it says is enough to lose it. It gets an entry of its
        // own instead, marked as having come from the file, and choosing it writes the same value again.
        if (selected is null && current?.ToString() is { Length: > 0 } stored)
        {
            selected = new SettingChoice(describe?.Invoke(current) ?? $"{stored}（设置文件中的值）", () => write(current));
            choices.Add(selected);
        }

        return new SettingChoiceRow(label, note, choices, selected, Save);
    }

    private SettingChoiceRow Choice<T>(string label, IEnumerable<(string Label, T Value)> options, Func<T> read, Action<T> write, string? note = null, Func<T, string>? describe = null) =>
        Pick(label, note, options, read, write, EqualityComparer<T>.Default, describe);

    /// <summary>
    /// A drop-down over one of the mpv option catalogues. Matched case-insensitively, because these values
    /// go into a settings file a person may well have edited by hand, and mpv itself does not care.
    /// </summary>
    private SettingChoiceRow Mpv(string label, IReadOnlyList<MpvChoice> options, Func<string> read, Action<string> write, string? note = null) =>
        Pick(label, note, options.Select(option => (Label: option.Label, Value: option.Value)), read, write, StringComparer.OrdinalIgnoreCase);

    private SettingToggleRow Toggle(string label, string note, Func<bool> read, Action<bool> write) =>
        new(label, note, read(), write, Save);

    private SettingNumberRow Number(string label, double minimum, double maximum, Func<int> read, Action<int> write, string? note = null, Action? after = null) =>
        new(label, note, minimum, maximum, read(), value => write((int)value), () => read(), Save, after);

    private SettingSliderRow Slider(string label, double minimum, double maximum, double step, Func<int> read, Action<int> write, string? note = null) =>
        new(label, note, minimum, maximum, step, read(), value => write((int)value), Save);

    /// <summary>
    /// A searchable list of the machine's font families. Kept in a field as well as returned: the scan
    /// finishes after the card is built, and this is the row it has to be handed to.
    /// <para>
    /// Whatever has already been scanned goes in immediately, which is what a second visit to the page
    /// gets — the library reads the font files once per session, so only the first visit ever sees the
    /// row hold nothing but the stored value.
    /// </para>
    /// </summary>
    private SettingFontRow Font(string label, string? note, Func<string> read, Action<string> write)
    {
        var row = new SettingFontRow(label, note, read(), write, Save);

        if (_fonts is { Ready.Families.Count: > 0 } library) row.Fill(library.Ready);

        _subtitleFont = row;
        return row;
    }

    private SettingTextRow Text(string label, string placeholder, Func<string> read, Action<string> write, string? note = null, Action? after = null) =>
        new(label, note, placeholder, read(), typed =>
        {
            var value = typed.Trim();
            write(value);
            return value;
        }, Save, after);

    /// <summary>
    /// A text box holding a filesystem path. <see cref="Text"/> with one pair of surrounding double quotes
    /// taken off as well as the whitespace: Explorer's 「复制为路径」 puts them on the clipboard, pasting one in
    /// is the ordinary way to fill such a box, and a double quote cannot occur in a Windows path — so a value
    /// wearing them is always a paste and never a filename. See <see cref="MpvConfigLocation.Clean"/>.
    /// </summary>
    private SettingTextRow PathBox(string label, string placeholder, Func<string> read, Action<string> write, string? note = null, Action? after = null) =>
        new(label, note, placeholder, MpvConfigLocation.Clean(read()), typed =>
        {
            var value = MpvConfigLocation.Clean(typed);
            write(value);
            return value;
        }, Save, after);

    /// <summary>
    /// A comma-separated list in a text box. The box is redisplayed from the parsed list, so what is on
    /// screen once the box loses focus is what was actually stored — separators tidied, blanks and
    /// duplicates gone — rather than the raw typing.
    /// </summary>
    private SettingTextRow List(string label, string placeholder, Func<List<string>> read, Action<List<string>> write) =>
        new(label, null, placeholder, string.Join(", ", read()), typed =>
        {
            var items = Split(typed);
            write(items);
            return string.Join(", ", items);
        }, Save);

    /// <summary>
    /// A list of languages in priority order. <see cref="List"/> parsed by the language catalogue instead of
    /// by punctuation alone: mpv writes a priority list with <c>&gt;</c> and so did this client's own v2
    /// settings file, so that is how a person writes one here too — and 「简体中文 &gt; 中文」 used to be stored
    /// as a single language of that name, matching nothing, with the box happily showing it back.
    /// <para>
    /// Names are canonicalised, which is also what the settings document does to this list on load, so the
    /// redisplayed box is exactly what ends up in the file. A name the catalogue does not know is kept as
    /// written and reaches mpv as a raw language code.
    /// </para>
    /// </summary>
    private SettingTextRow Languages(string label, string placeholder, Func<List<string>> read, Action<List<string>> write) =>
        new(label, null, placeholder, string.Join(", ", read()), typed =>
        {
            var items = TrackLanguagePriority.ParseList(typed);
            write(items);
            return string.Join(", ", items);
        }, Save);

    /// <summary>
    /// Splits a typed list of plain words. Both widths of comma and semicolon, plus the ideographic comma —
    /// 「动画、动漫」 is how the list this serves gets written on a Chinese keyboard, and it used to come out
    /// as one keyword. The priority marks <c>&gt;</c> and <c>→</c> are deliberately not separators here:
    /// these lists are unordered, and a word is a likelier thing to find around an arrow than a boundary.
    /// </summary>
    private static List<string> Split(string value) =>
        value.Split([',', '，', ';', '；', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
