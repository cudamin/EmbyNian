using EmbyNian.Infrastructure;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyNian.Configuration;
using EmbyNian.Mpv;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    /// server data in it, so building all of them costs nothing measurable, and a card that stays alive
    /// keeps any half-typed text across a trip through the category list.
    /// </summary>
    public Visibility SectionVisibility => IsVisible ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// The settings page, as data. Builds the cards and their rows out of <see cref="AppSettings"/> and
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

    private ISettingsService? _settings;
    private ShaderStaging? _shaders;
    private FontLibrary? _fonts;
    private AppPaths? _paths;
    private Platform.ISystemLauncher? _launcher;
    private SettingFontRow? _subtitleFont;
    private AudioDeviceCatalogue? _audioDevices;
    private SettingChoiceRow? _audioDevice;

    /// <summary>The cards, in the order they appear in the left-hand list.</summary>
    private static readonly string[] CardCategories =
        ["播放器", "播放行为", "字幕", "视频输出", "音频输出", "着色器", "主页", "界面", "关于", "恢复默认"];

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

    /// <summary>主题那一行的色板，同样是为了让自检不必去树上找它。</summary>
    internal SettingThemeRow? Themes { get; private set; }

    /// <summary>
    /// How many row containers the cards would put on the visual tree between them; the self-check
    /// compares this to what really rendered.
    /// </summary>
    internal int RowCount => Sections.Sum(Containers);

    /// <summary>
    /// Each card in the order they appear, and the containers it holds. What the self-check walks: it steps
    /// through the categories and checks the tree against the running total, because a collapsed card never
    /// builds its rows and a single look at this page would leave every card but one untouched.
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
    /// The capabilities this page needs, and nothing else: the settings document with its write-back, the
    /// shader catalogue the 着色器 card offers, the machine's fonts for the 字幕 card's picker, and — for the
    /// 关于 card — where this app keeps its files and how to show a folder. Named rather than handed the whole
    /// composition root — a page that edits settings has no business being able to reach the session or the
    /// player.
    /// </summary>
    internal void Attach(
        ISettingsService settings,
        ShaderStaging shaders,
        FontLibrary fonts,
        AppPaths paths,
        Platform.ISystemLauncher launcher,
        AudioDeviceCatalogue audioDevices)
    {
        _settings = settings;
        _shaders = shaders;
        _fonts = fonts;
        _paths = paths;
        _launcher = launcher;
        _audioDevices = audioDevices;
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

        Sections.Clear();
        Sections.Add(PlayerCard());
        Sections.Add(PlaybackCard());
        Sections.Add(SubtitleCard());
        Sections.Add(VideoCard());
        Sections.Add(AudioCard());
        Sections.Add(ShaderCard());
        Sections.Add(HomeCard());
        Sections.Add(InterfaceCard());
        Sections.Add(AboutCard());
        Sections.Add(ResetCard());

        ShowCategory(SelectedCategory);
        IsReady = true;

        return Task.WhenAll(FillFontsAsync(), FillAudioDevicesAsync());
    }

    /// <summary>
    /// Hands the 音频输出设备 row the machine's real devices once they have been enumerated. Same shape and same
    /// reason as <see cref="FillFontsAsync"/>: the list comes out of a throwaway libmpv context, which is tens
    /// of milliseconds of native work, and the page is worth more than that one row being complete on the first
    /// frame. Until it lands the row holds 「跟随系统默认设备」 plus whatever the settings file names.
    /// </summary>
    private async Task FillAudioDevicesAsync()
    {
        if (_audioDevices is null || _audioDevice is null) return;

        var devices = await _audioDevices.LoadAsync().ConfigureAwait(true);
        if (devices.Count == 0) return;

        // The row may have been rebuilt while the read ran — a second visit to the page does that — so the
        // current one is asked for rather than the one captured above.
        var row = _audioDevice;
        if (row is null) return;

        var audio = Settings.Audio;
        var (choices, selected) = DeviceChoices(audio, devices);
        row.Fill(choices, selected);
    }

    /// <summary>
    /// The 音频输出设备 drop-down: 「跟随系统默认设备」 first, then whatever mpv found. Passing an empty list is
    /// the normal first-frame state and the permanent state on a machine where libmpv could not enumerate —
    /// the row is still usable, it just offers the default and whatever the settings file names.
    /// <para>
    /// mpv's own <c>auto</c> entry never reaches here: <see cref="AudioDeviceCatalogue.Selectable"/> drops it,
    /// in Core, where a unit test can hold it down. Leaving it in put the same behaviour on the list twice —
    /// 「跟随系统默认设备」 and, right underneath, mpv's English 「Autoselect device」. Two rows for one answer is
    /// bad enough; picking the second one also stored <c>auto</c> instead of the empty string, so the row
    /// afterwards read 「Autoselect device」 to somebody who believed he had chosen the system default.
    /// </para>
    /// </summary>
    private (List<SettingChoice> Choices, SettingChoice? Selected) DeviceChoices(
        AudioSettings audio,
        IReadOnlyList<AudioDevice> devices)
    {
        (string Label, string Value)[] options =
        [
            ("跟随系统默认设备", ""),
            .. devices.Select(device => (Label: device.Label, Value: device.Name))
        ];

        return Options(
            options,
            () => audio.Device,
            value => audio.Device = value,
            StringComparer.OrdinalIgnoreCase,
            stored => $"{stored}（设置文件中的值，这台机器上没找到）");
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

            // 这句说明是这一行存在的第二个理由，而且它是安全性的一句实话，不是介绍。外部 mpv.exe 那条路把
            // X-Emby-Token 写在 --http-header-fields-append= 上，也就是写在另一个进程的命令行上 —— 任务管理器、
            // 任何进程工具、崩溃转储都读得到。内置 libmpv 不经过命令行（那个头是在进程里用 mpv_set_option_string
            // 设的），所以默认后端没有这件事。
            //
            // 为什么是「把话说出来」而不是「把代码改掉」：两条真修法各有代价，而且这台机器上一条都验不了（验证
            // 时不许真实播放，本机也没装外部 mpv.exe）。写一份临时 mpv 配置文件传 header 等于把令牌明文落到磁盘
            // 上，正好抵掉「settings.json 泄了也不是一个可用凭据」这个 DPAPI 换来的性质；改成先连上 IPC 再注入
            // （--idle=once 加 loadfile）安全上最干净，但会长出第二条起播路径、一种新的卡死方式（通道建不起来
            // 就永远待机），而且「关掉 IPC」那一档就没法播了。三条路里只有这一条是验得住的，而它把决定交回给
            // 真正要走这条路的人 —— 这个后端本来就要用户自己填路径才用得上。要真修，选 IPC 那条。
            PathBox("mpv.exe 路径", "mpv.exe 路径", () => Settings.Mpv.ExecutablePath, value => Settings.Mpv.ExecutablePath = value,
                "只有「外部 mpv.exe」这个后端要它。走这个后端时，访问令牌会出现在 mpv 的进程命令行上（任务管理器、"
                    + "进程工具、崩溃转储都读得到）；内置播放器不经过命令行，没有这件事。"),

            Toggle("启用 IPC 进度通道", "关闭后服务器无法获得精确播放位置", () => Settings.Mpv.EnableIpc, value => Settings.Mpv.EnableIpc = value)
        ]);

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

            // 音轨语言优先级, the same row shape and the same parser as the 字幕 card's. It was a single-pick
            // drop-down up to v9, which could not express 「日语 > 粤语 > 英语」 at all — and mpv's own alang has
            // always taken a list. An empty box is 「跟随服务器默认音轨」, which is what the old 「默认音轨」 entry
            // and its companion mode both meant.
            Languages("音轨语言优先级", "日语, 粤语, 英语", () => playback.AudioLanguages, value => playback.AudioLanguages = value)
        ]);
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
                () => playback.SubtitleFontFamily, value => playback.SubtitleFontFamily = value, "sub-font"),
            Number("字号", 0, 160, () => playback.SubtitleFontSize, value => playback.SubtitleFontSize = value,
                "0 表示不指定，由 mpv 自己决定；要指定的话最小 16", null, "sub-font-size"),
            Toggle("字幕加粗", "提高复杂画面上的可读性", () => playback.SubtitleBold, value => playback.SubtitleBold = value,
                "sub-bold"),
            Mpv("文字颜色", MpvOutputOptions.SubtitleColors, () => playback.SubtitleColor, value => playback.SubtitleColor = value,
                "sub-color"),
            Mpv("描边大小", MpvOutputOptions.SubtitleBorders, () => playback.SubtitleBorderSize, value => playback.SubtitleBorderSize = value,
                "sub-border-size"),
            Mpv("描边颜色", MpvOutputOptions.SubtitleBorderColors, () => playback.SubtitleBorderColor, value => playback.SubtitleBorderColor = value,
                "sub-border-color"),
            Mpv("阴影", MpvOutputOptions.SubtitleShadows, () => playback.SubtitleShadowOffset, value => playback.SubtitleShadowOffset = value,
                "sub-shadow-offset"),
            Mpv("背景颜色", MpvOutputOptions.SubtitleBackColors, () => playback.SubtitleBackColor, value => playback.SubtitleBackColor = value,
                "sub-back-color"),
            Slider("背景透明度（%）", 0, 100, 5, () => playback.SubtitleBackOpacity, value => playback.SubtitleBackOpacity = value,
                "上面那一行颜色的透明度，同一个选项", "sub-back-color"),
            Mpv("字幕编码", MpvOutputOptions.SubtitleCodepages, () => playback.SubtitleCodepage, value => playback.SubtitleCodepage = value,
                "sub-codepage"),
            Toggle("拉伸图形字幕到画面", "宽屏 PGS/VOBSUB 字幕避免落到画面外", () => playback.StretchWideImageSubtitles, value => playback.StretchWideImageSubtitles = value,
                "stretch-image-subs-to-screen")
        ]);
    }

    private SettingSection VideoCard()
    {
        var video = Settings.Video;

        // 视频同步 states the value in force, not the value stored, so it has two writers to follow: 启用插值
        // two rows below, and its own drop-down. Both restate it — the row is held in a local so each can.
        // The two rows are five lines apart on screen and used to contradict each other: this one said
        // 「不指定（等同音频同步）」 while display-resample was what mpv got. Missing the second writer put the
        // same lie back the other way round: pick 显示同步 here and the line underneath still said 音频同步.
        SettingChoiceRow? sync = null;
        sync = Mpv("视频同步", MpvOutputOptions.VideoSync, () => video.VideoSync, value => video.VideoSync = value,
            "video-sync", SyncNote(video), () => sync!.Restate(SyncNote(video)));

        return new SettingSection("视频输出", "视频输出", "渲染、硬件解码、同步和网络缓冲。",
        [
            Mpv("视频渲染", MpvOutputOptions.Renderers, () => video.Renderer, value => video.Renderer = value,
                "vo", "着色器档位是照 GPU-Next 调的；GPU 那一档只当回退"),
            Mpv("图形接口", MpvOutputOptions.GpuApis, () => video.GpuApi, value => video.GpuApi = value,
                "gpu-api", "装机默认是 Vulkan：带 compute pass 的链（ArtCNN 那几档）在 Direct3D 11 上慢五倍左右"),
            Mpv("硬件解码", MpvOutputOptions.HardwareDecoders, () => video.HardwareDecoding, value => video.HardwareDecoding = value,
                "hwdec", "装机默认是「自动」；选「不指定」等同于纯软件解码"),
            Mpv("色彩范围", MpvOutputOptions.OutputLevels, () => video.OutputLevels, value => video.OutputLevels = value,
                "video-output-levels"),
            sync!,
            Toggle("宽于 16:9 的片源裁切填充",
                "2.35:1 的电影铺满 16:9 的屏幕，代价是每一帧的左右两边被裁掉（贴边的字幕也会跟着没）。"
                + "只对真的有黑边的片源出手；播放器右键菜单里可以对单部片子临时改",
                () => video.FillWideSources, value => video.FillWideSources = value,
                "panscan"),
            Toggle("启用反交错", "仅对隔行片源有意义", () => video.Deinterlace, value => video.Deinterlace = value,
                "deinterlace"),
            Toggle("启用插值",
                "补偿刷新率不匹配造成的抖动：沿时间轴混合相邻两帧，不是电视上那种运动补偿。它必须靠显示同步才生效，"
                + "而开销出在显示同步那一头 —— 那时 mpv 最后一趟渲染改成按刷新率跑，这台机器上实测 24.7% 变 50.1% 显卡；"
                + "高刷屏上它能补的抖动本来也很小，所以超过 120Hz 时下面那一项会把两者一起收回",
                () => video.Interpolation,
                value =>
                {
                    video.Interpolation = value;

                    // 这一项一变，上面那一行「实际生效」就变了。页面没有整体刷新，也不该有 —— 只有因果关系
                    // 明确的这几处自己去改那一行。
                    sync!.Restate(SyncNote(video));
                },
                "interpolation、tscale"),
            Toggle("高帧率或高刷新率时使用音频同步",
                "片源超过约 47fps，或播放窗口所在屏幕超过 120Hz，就回到音频同步、插值不生效：这两种情况下显示同步"
                + "只剩算力开销。关掉它可以强行让显示同步在任何屏幕上生效",
                () => video.HighFrameRateAudioSync,
                value =>
                {
                    video.HighFrameRateAudioSync = value;

                    // 第三个写手：它改不了「此刻生效」那半句（这一页上没有片子、也不知道是哪块屏），可它决定
                    // 那一行末尾还讲不讲那两条例外。
                    sync!.Restate(SyncNote(video));
                },
                "video-sync、interpolation"),
            Slider("网络缓冲（MB）", 0, 4096, 64, () => video.NetworkCacheMegabytes, value => video.NetworkCacheMegabytes = value,
                "0 表示使用 mpv 默认值", "demuxer-max-bytes"),
            Mpv("抖动", MpvOutputOptions.Dithers, () => video.Dither, value => video.Dither = value,
                "dither、dither-depth", "色深抖动，和上面的插值无关：落到显示器位深时撒一层噪声，免得渐变上出现色带"),
            Mpv("去色带", MpvOutputOptions.DebandModes, () => video.Deband, value => video.Deband = value,
                "deband", "大倍数档（放大 2.2 倍以上）改用链里的 hdeband，那时候这一项不生效"),
            Mpv("HDR 处理", MpvOutputOptions.HdrModes, () => video.HdrMode, value => video.HdrMode = value,
                "tone-mapping、target-colorspace-hint"),
            Toggle("自动 ICC 校色", "按系统给这块屏设的 ICC 配置文件校色；屏幕没校准过开了会偏色，开着也会让上面的 HDR 直通失效",
                () => video.IccProfileAuto, value => video.IccProfileAuto = value, "icc-profile-auto")
        ]);
    }

    /// <summary>
    /// 视频同步 那一行的说明：此刻真正生效的值，加上会改变它的两条规则。
    /// <para>
    /// The value comes from <see cref="MpvOutputOptions.ResolveSync"/> — the same function that decides what mpv
    /// is actually sent — so the page cannot say one thing while the player does another. That was the bug: the
    /// drop-down read 「不指定（等同音频同步）」 with 插值 on, and <c>display-resample</c> was in force.
    /// </para>
    /// <para>
    /// 高帧率 and 高刷新率 are both stated in words rather than resolved, for the same reason: neither is knowable
    /// from this page. No film is playing while it is open, and this window is not the player's — it may not even
    /// be on the monitor the film will land on. What they resolve to is written to the log at every launch
    /// (<c>PlaybackPlanner</c>) and the 诊断 page lists the options mpv was actually given.
    /// </para>
    /// </summary>
    private static string SyncNote(VideoSettings video)
    {
        var (value, _, _) = MpvOutputOptions.ResolveSync(video);

        // An empty resolved value is 「没发这个选项」, and mpv's own default is audio sync. Naming that rather
        // than echoing the catalogue's 「不指定」 label back: 「此刻生效：不指定」 answers nothing.
        var live = value.Length == 0
            ? "此刻生效：音频同步（mpv 不收到这个选项时的默认）"
            : $"此刻生效：{MpvOutputOptions.Describe(MpvOutputOptions.VideoSync, value)}";

        var because = video.Interpolation && (video.VideoSync ?? "").Trim().Length == 0
            ? "因为「启用插值」开着"
            : "";

        var exception = video.HighFrameRateAudioSync
            ? "片源超过约 47fps、或屏幕超过 120Hz 时一律回到音频同步"
            : "";

        return string.Join("；", new[] { live, because, exception }.Where(part => part.Length > 0));
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
                },
                "audio-spdif"))
            .ToList();

        return new SettingSection("音频输出", "音频输出", "输出设备、声道布局、响度、独占模式和功放直通。",
        [
            // 音频输出设备. Built from whatever has already been enumerated — nothing on the first frame — and
            // refilled by FillAudioDevicesAsync a moment later. Held in a field for exactly that.
            _audioDevice = DeviceRow(audio),
            Mpv("扬声器布局", MpvOutputOptions.Channels, () => audio.Channels, value => audio.Channels = value,
                "audio-channels"),

            // 「只对 AC-3 / E-AC-3 有效」 is the whole point of this note. The option itself is fine and stays —
            // it really does work on an AC-3 track that carries DRC metadata — but the row used to read as the
            // general 「让对白清楚一点」 control, and on a DTS or TrueHD track it does exactly nothing. That is
            // the 「界面在骗人」 class of defect, so the row now names its own range and points at the next one.
            Mpv("动态范围压缩", MpvOutputOptions.DynamicRange, () => audio.DynamicRange, value => audio.DynamicRange = value,
                "ad-lavc-ac3drc",
                "只对 AC-3 / E-AC-3 音轨有效，而且要片源自带 DRC 信息；DTS、TrueHD、AAC、FLAC 一律没有反应，"
                + "那些请用下面的「音量均衡」"),
            Mpv("音量均衡", MpvOutputOptions.VolumeNormalizers, () => audio.VolumeNormalize, value => audio.VolumeNormalize = value,
                "af", "对所有编码都有效，代价是动态范围被压窄；播放器右键菜单里可以当场试听这三档"),
            Toggle("5.1 下混到两声道时归一化",
                "两声道听 5.1 片时对白不再被爆炸声压过去，代价是整体变轻一档（这是上游自己写的代价）。"
                + "只在下混由 mpv 完成时有效，这台机器上量过确实如此",
                () => audio.NormalizeDownmix, value => audio.NormalizeDownmix = value,
                "audio-normalize-downmix"),
            Toggle("音频独占模式", "播放时占用声卡，避免系统混音", () => audio.ExclusiveMode, value => audio.ExclusiveMode = value,
                "audio-exclusive"),
            Number("全局音频延迟（毫秒）", -5000, 5000, () => audio.DelayMilliseconds, value => audio.DelayMilliseconds = value,
                null, null, "audio-delay"),
            new SettingToggleGroupRow("直通格式", passthrough)
        ]);
    }

    /// <summary>
    /// 音频输出设备. Its own factory rather than an inline <see cref="Pick"/> call because the note is the point
    /// of the row: 独占模式 without this could only ever take over 「whatever Windows calls the default right
    /// now」, and the whole reason to choose a device by hand is to say which one that is.
    /// </summary>
    private SettingChoiceRow DeviceRow(AudioSettings audio)
    {
        var (choices, selected) = DeviceChoices(audio, _audioDevices?.Known ?? []);

        return new SettingChoiceRow(
            "音频输出设备",
            Annotate("插上耳机之后独占模式该占哪一个，由这一行说。设备列表是开设置时从 mpv 读的，"
                + "拔掉的设备会退回系统默认而不是变成没声音", "audio-device"),
            choices,
            selected,
            Save);
    }

    /// <summary>
    /// 画质与着色器. Four dropdowns of group names and two resolution thresholds until 2026-09-03; now the
    /// chain is computed (放大倍数 × 片源类型 × 显卡档) and what is left here is the three answers that are
    /// genuinely the user's — how much GPU there is, whether to pin one chain by hand, and whether animated
    /// content should use the animated half of the table at all.
    /// </summary>
    private SettingSection ShaderCard()
    {
        var shaders = Settings.Shaders;
        var video = Settings.Video;

        // Named in plain terms rather than by any measurement: this is the one thing on the page that only
        // the person in front of the machine can answer, and 「核显」 is a word they know.
        (string Label, GpuTier Value)[] gpuTiers =
        [
            ("低档 — 核显或入门老卡（Vega、Iris Xe、GTX 1050）", GpuTier.Low),
            ("中档 — 入门独显（GTX 1650、RX 6500 XT、Arc A380）", GpuTier.Medium),
            ("高档 — RTX 3060 / RX 6700 及以上", GpuTier.High)
        ];

        // The eight ids never change with 显卡档 — that setting swaps what each one loads, not which ones
        // exist — so a pinned choice survives changing it and this list needs no reseeding.
        (string Label, string Value)[] chains =
        [
            ("自动（按放大倍数挑）", ""),
            .. _shaders!.Catalog.Select(item => (Label: item.DisplayName, Value: item.Id))
        ];

        return new SettingSection("着色器", "画质与着色器", "按放大倍数、片源类型和显卡档自动挑一条着色器链。",
        [
            Toggle("启用着色器", "关掉之后缩放完全交给 mpv 自己", () => shaders.Enabled, value => shaders.Enabled = value,
                "glsl-shaders"),
            Choice("显卡档位", gpuTiers, () => shaders.Gpu, value => shaders.Gpu = value,
                "决定每一档用多重的链，和片源无关。装机默认是低档"),
            Choice("手动指定档位", chains, () => shaders.ManualGroup, value => shaders.ManualGroup = value,
                "留在「自动」就按放大倍数挑；想前后对比时在这里钉住一条。每一档具体挂了哪几个着色器，在播放器的 更多 → 着色器 菜单里逐行写着"),
            Mpv("画质预设", MpvOutputOptions.QualityPresets, () => video.QualityPreset, value => video.QualityPreset = value,
                "profile", "fast 省算力、high-quality 更细腻。两个都是 mpv 自己内置的；开着着色器时缩放器归档位链，预设只剩它没碰的那几项"),
            Toggle("自动识别动画", "按 Emby 类型和标签关键词匹配，命中就走动画那半张表", () => shaders.AutoAnimeProfile, value => shaders.AutoAnimeProfile = value),
            List("动画关键词", "动画, 动漫, Anime", () => shaders.AnimeKeywords, value => shaders.AnimeKeywords = value),
            Toggle("老片源修复", "片源高度不超过 576 线（DVD 那一代）时，链的最前面加去色带；中高档还加轻度降噪",
                () => shaders.RestoreVintageSources, value => shaders.RestoreVintageSources = value),
            Toggle("8K 片源关闭着色器", "宽 ≥7000 或高 ≥3000 的片源不套用任何链，避免 GPU 过载", () => shaders.DisableForUltraHighRes, value => shaders.DisableForUltraHighRes = value)

        ]);
    }

    /// <summary>
    /// 主页：那几排的次序和显示与否 —— 「把媒体库的列表也添加到主页之中，新增页里拖拽决定这些列表的顺序，勾选
    /// 显示或者不勾选取消显示」。
    /// <para>
    /// 表里那几项从设置文件里那份版面读（<see cref="Emby.HomeLayout"/> 归一化过的那一份，主页每次读完都写回来），
    /// 所以这一头不用问服务器有哪几个媒体库 —— 设置窗口连不上服务器时这张卡照样写得出每一排叫什么。改一下（拖过
    /// 或者点过勾）就立刻写回设置并喊一声（<see cref="ShellPrefs"/>），主页那一头照新的重排。
    /// </para>
    /// </summary>
    private SettingSection HomeCard()
    {
        var ui = Settings.Ui;
        var plan = Emby.HomeLayout.Plan(ui.HomeRows, null);

        HomeRows = new SettingHomeLayoutRow(
            "主页上排哪几排",
            "按住一行往上下拖、或者按右边那两颗箭头决定次序，取消勾选就不显示。排在第一的那一排压在主页顶上那张大图"
                + "上。媒体库那几排装的是那个库最近添加的内容。",
            plan.Select(row => new HomeRowChoice(row.Key, row.Title, row.Visible)),
            rows =>
            {
                ui.HomeRows = [.. rows.Select(row => new Configuration.HomeRowSetting
                {
                    Key = row.Key,
                    Title = row.Title,
                    Visible = row.Visible
                })];

                Save();
                ShellPrefs.Apply(ui);
            });

        return new SettingSection("主页", "主页", "主页上那几排的次序和显示与否，包括每个媒体库自己那一排。",
        [
            HomeRows
        ]);
    }

    /// <summary>自检用：那张可拖拽的表这一次建出来的那一行。</summary>
    internal SettingHomeLayoutRow? HomeRows { get; private set; }

    /// <summary>
    /// 自检用：「图片缓存上限（MB）」那一行。
    /// <para>
    /// 存着它是因为这件事真会坏的地方不在这一页上：设置页开在另一个窗口里，它只把数字写进设置文档然后喊一声
    /// （<see cref="ShellPrefs"/>），是主窗口那一头把新预算交给图片仓库的。那根绳子断了的样子是「三处读数全对、
    /// 缓存照旧按旧上限清」—— 屏上没有任何东西说得出现在生效的是哪个数。
    /// </para>
    /// </summary>
    internal SettingNumberRow? ImageBudget { get; private set; }

    /// <summary>
    /// 自检：把这一行拨动一格，看图片仓库的预算跟不跟得上，再拨回去。
    /// <para>
    /// **只拨一格（±1 MB）**，不是拨到上限。两个理由：拨到上限对「上限本来就是上限」的人是一次空操作
    /// （<c>SettingNumberRow.Value</c> 是 <c>[ObservableProperty]</c>，相等就直接返回，<c>after</c> 一次都不跑），
    /// 这一关于是变成一句永远为真的空话；而万一还原那一步没走到，留在设置文件里的差别只有 1 MB，不是「上限没了」。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail)? MeasureImageBudget(Emby.EmbyImageStore images)
    {
        if (ImageBudget is not { } row) return null;

        var before = Settings.Ui.ImageCacheMegabytes;
        var probe = before < Emby.ImageCachePolicy.MaxMegabytes ? before + 1 : before - 1;

        var agreedBefore = images.MaxBytes == Emby.ImageCachePolicy.BudgetBytes(before);

        row.Value = probe;
        var moved = Settings.Ui.ImageCacheMegabytes == probe;
        var followed = images.MaxBytes == Emby.ImageCachePolicy.BudgetBytes(probe);

        row.Value = before;
        var restored = Settings.Ui.ImageCacheMegabytes == before
            && images.MaxBytes == Emby.ImageCachePolicy.BudgetBytes(before);

        return (agreedBefore && moved && followed && restored,
            $"设置里 {before} MB、仓库 {images.MaxBytes / 1024 / 1024} MB（一致={agreedBefore}）"
            + $"；拨到 {probe} MB → 设置{(moved ? "跟上" : "没跟上")}、仓库{(followed ? "跟上" : "没跟上")}"
            + $"；拨回 {before} MB → {(restored ? "两头都还原了" : "没还原")}"
            + $"；范围 {Emby.ImageCachePolicy.MinMegabytes}–{Emby.ImageCachePolicy.MaxMegabytes}");
    }

    /// <summary>
    /// 自检：那张表和设置文件里那一份对得上没有 —— 存着几排，表里就该有几排，钥匙和次序都一样。
    /// <para>
    /// 这一条盯的是设置窗口这一头拿不到服务器那份媒体库列表：媒体库那几排的名字只能从存档里记着的那句标题来
    /// （见 <see cref="Emby.HomeLayout.Plan"/> 里那一手）。那一手断掉的样子是屏上一张只有四行固定排的表 ——
    /// 看着完全正常，而用户在上面随手拖一下，就把媒体库那几排从设置文件里抹掉了。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail)? MeasureHomeRows()
    {
        if (HomeRows is not { } row) return null;

        var saved = Settings.Ui.HomeRows;
        var keys = row.Rows.Select(choice => choice.Key).ToList();
        var ok = keys.Count == saved.Count
            && saved.Select(entry => entry.Key).SequenceEqual(keys, StringComparer.Ordinal);

        return (ok, $"表里 {keys.Count} 排、存档 {saved.Count} 排"
            + $"：{(keys.Count == 0 ? "无" : string.Join('、', row.Rows.Select(choice =>
                $"{choice.Title}{(choice.Visible ? "✓" : "✗")}")))}");
    }

    private SettingSection InterfaceCard()
    {
        var ui = Settings.Ui;
        Themes = ThemeSwatches(ui);

        // Held as well as placed, the same way the two shader thresholds are: the self-check drives this row to
        // prove that a changed budget really reaches the image store, and the row is the only end of that rope
        // it can reach from here.
        ImageBudget = Number("图片缓存上限（MB）",
            Emby.ImageCachePolicy.MinMegabytes,
            Emby.ImageCachePolicy.MaxMegabytes,
            () => ui.ImageCacheMegabytes,
            value => ui.ImageCacheMegabytes = value,
            $"海报和剧照在磁盘上最多占多少，装机是 {Emby.ImageCachePolicy.DefaultMegabytes} MB。这一行是填进去的，"
                + "不是用箭头拨的；填小了当场就会把最久没看过的那些删到新上限以下。缓存删掉不影响任何设置，"
                + "只是下次看到那些封面时要重新下载一遍。",
            after: () => ShellPrefs.Apply(ui));

        return new SettingSection("界面", "界面", "配色主题、窗口和侧边栏、媒体库分页、海报尺寸和图片缓存上限。",
        [
            Themes,

            // 这两个和上面那几块色板一样：写进设置之后当场喊一声（ShellPrefs），主窗口和外壳各自跟上。设置页
            // 是另一个窗口，手上没有主窗口的 HWND 也没有那一页，所以只能这么喊。
            Toggle("锁定窗口比例大小",
                $"拖窗口边沿时侧边栏右边那一片保持 {Emby.HomeCarousel.WindowAspectLabel}，主页那张轮播图正好铺满第一屏、不被裁切",
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

            // 「新增可在设置中调整图片缓存大小的功能」. Built above so the self-check can drive it; see ImageBudget.
            ImageBudget,

            // 「加入显示评分改为豆瓣评分的功能，可在设置使用豆瓣、tmdb、烂番茄等平台的评分」. The catalogue is
            // ItemScore's, so the four names exist in exactly one place — and what each option can actually do is in
            // that class's remarks, which the note below says in the user's own words.
            Choice("评分来源", Emby.ItemScore.Catalogue,
                () => ui.ScoreSource,
                value => ui.ScoreSource = value,
                note: "详情页那个分显示哪一家的。「烂番茄」读的是服务器上的「影评指数」，是唯一真正独立的第二个分；"
                    + "「豆瓣」和「TMDB」换的是分数旁边那个署名 —— 服务器上三家的大众分都写在同一个字段里，"
                    + "客户端换不出来，只能在服务器认出这个条目属于哪一家时把那一家的名字写上去，认不出来就写"
                    + "「公众评分」。豆瓣要服务器上装了豆瓣刮削插件才认得出来。"),

            Toggle("显示观看状态标记", "在海报角上显示已看和收藏状态", () => ui.ShowWatchedIndicators, value => ui.ShowWatchedIndicators = value)
        ]);
    }

    /// <summary>
    /// 关于：这份程序是哪一版、拿哪个内核在放、它的东西放在磁盘上哪儿。
    /// <para>
    /// 在这张卡之前，版本号只写进日志和自检报告 —— 界面上一次都没出现过，所以「你用的是哪一版」这句话答不上来；
    /// 而设置文件和缓存目录也没有一处能一键打开（诊断页那颗按钮只开日志）。三行读数由 Core 那边算
    /// （<see cref="AboutFacts"/>，读不到就明说读不到），四行目录各带一颗按钮。
    /// </para>
    /// <para>
    /// 没有 <see cref="AppPaths"/> 或者打不开资源管理器的时候（测试和自检的那一路）这张卡照旧建出来，只是那四颗
    /// 按钮不画：一张空卡比一张会抛的卡好，而「这一版是哪一版」不该因为拿不到路径就说不出来。
    /// </para>
    /// </summary>
    private SettingSection AboutCard()
    {
        var home = AppContext.BaseDirectory;
        var rows = new List<SettingRow>
        {
            Fact("客户端版本", "改动记在 PROGRESS.md 里，版本号不随每次改动走", AboutFacts.Client),
            Fact("构建时间", "这份 exe 落到磁盘上的时间", AboutFacts.BuiltAt(Path.Combine(home, "EmbyNian.exe"))),
            Fact("播放内核", "这个文件的版本不要换", AboutFacts.PlaybackCore(home))
        };

        if (_paths is { } paths)
        {
            rows.Add(Fact("设置文件", "所有设置都在这一份 JSON 里，token 是 DPAPI 包过的", paths.SettingsFile,
                "打开所在文件夹", () => _launcher?.OpenFolder(paths.Root)));
            rows.Add(Fact("日志目录", "自检报告和界面树也在这儿", paths.LogDirectory,
                "打开", () => _launcher?.OpenFolder(paths.LogDirectory)));
            rows.Add(Fact("缓存目录", "海报和着色器缓存，删掉不会丢设置", Path.GetDirectoryName(paths.ImageCacheDirectory) ?? paths.Root,
                "打开", () => _launcher?.OpenFolder(Path.GetDirectoryName(paths.ImageCacheDirectory) ?? paths.Root)));

            // 播放器右键菜单 → 截屏 的落点。这一行不是装饰：截图这个功能从前根本没做，理由正是
            // 「--no-config 之下没有 screenshot-directory，文件会落到 exe 旁边而不告诉用户」——
            // 所以「告诉用户落在哪儿」和截图本身是同一件事的两半。
            rows.Add(Fact("截图目录", "播放器右键菜单 → 截屏 存到这儿，文件名是片名加时间码", paths.ScreenshotDirectory,
                "打开", () => _launcher?.OpenFolder(paths.ScreenshotDirectory)));
        }

        return new SettingSection("关于", "关于", "版本、播放内核，和这个程序在磁盘上的几个位置。", rows);
    }

    /// <summary>
    /// 恢复默认设置，一张卡一行。
    /// <para>
    /// <b>为一行开一张卡，理由是「够不着」。</b> 它本来是「关于」卡的第八行 —— 而那张卡在设置窗口里第七行就到底了，
    /// 屏上根本看不见这一行（拍出来只剩「截图目录」露半行）。设置页是能滚的，所以功能不算缺；可整页最难找到的位置
    /// 放着用户点名要的那一件事，跟没做差不多。**一张只有一行的卡永远不会掉到折线下面**，而左边那份名单里多一个
    /// 「恢复默认」，正是找它的人会去看的地方。
    /// </para>
    /// <para>
    /// 排在「关于」后面：这两张卡讲的都不是某一组设置，而是这份程序自己。左边名单本来也不以「关于」收尾 ——
    /// 后面还跟着三个内嵌页面。
    /// </para>
    /// <para>
    /// 说明里把两件事都写清 —— 哪些回默认、哪些不动 —— 而不是只写前一半：这是整页唯一一件不可逆的操作，而按下它的
    /// 人最想知道的是「会不会把我的服务器和账号也弄掉」。按下之后还要再问一次（<see cref="RestoreDefaultsAsync"/>）。
    /// </para>
    /// <para>
    /// 牌子、这一行的标签、按钮上那几个字刻意各说一句话，而不是三处都写「恢复默认设置」—— 那样一张卡上同一句话
    /// 排三遍，读的人得挨个看完才知道它们是同一件事。牌子说这是哪儿，标签说要做什么，按钮上是那个动词。
    /// </para>
    /// </summary>
    private SettingSection ResetCard() =>
        new("恢复默认", "恢复默认设置", "只影响设置本身。服务器、账号和登录状态一律不动。",
        [
            new SettingActionRow(
                "把所有设置还原为装机时的默认值",
                "会改回装机时的样子：播放器、播放行为、字幕、视频输出、音频输出、画质与着色器、主页版面，"
                    + "以及界面那一组（主题、每页条目数、海报宽度、图片缓存上限、评分来源、窗口比例锁、侧边栏）。\n"
                    + "不会动：服务器和账号（不会退出登录，密码和令牌都还在）、窗口上次的位置和大小、"
                    + "各媒体库各自的排序筛选和视图、播放器上次的音量。\n"
                    + "按下之后会先问一次；确认之后这一步不能撤销。",
                "恢复默认",
                RestoreDefaultsAsync)
        ]);

    /// <summary>
    /// 「恢复默认设置」按下之后。哪些回默认、哪些不动由 Core 那一头判（<see cref="SettingsReset.Restore"/>，
    /// 单测钉着），这里剩下的是「问一次」和「改完让屏上跟上」。
    /// <para>
    /// <b>三件善后一件都不能少，而少了哪一件屏上都只是「设置了但没用」。</b> 主题要当场重刷，不然颜色要等到下次
    /// 启动才回默认；<see cref="ShellPrefs"/> 要喊一声，那是窗口比例锁、侧边栏、图片缓存上限、主页版面这四件
    /// 改完当场生效的唯一一根线（设置页开在另一个窗口里，手上没有主窗口的 HWND，也没有主页那一页）；整页要重建，
    /// 因为每一行只在造出来的时候读一次设置、此后只写（见类注释），所以不重建的话文件已经是默认值而屏上六十行
    /// 还是旧的。
    /// </para>
    /// <para>
    /// 重建走的是 <see cref="ReloadAsync"/> 本身，不是另写一段：那是页面第一次打开走的同一段，自检每一轮都把它
    /// 连着每张卡片走一遍，所以这里只剩一个调用点会错。字体和音频设备两份名单都是按进程缓存的，所以重建一次
    /// 不会再去扫字体、也不会再开一个 libmpv 句柄。
    /// </para>
    /// </summary>
    private async Task RestoreDefaultsAsync()
    {
        if (_settings is null) return;

        var agreed = await ConfirmAsync(
            "恢复默认设置",
            "所有设置都会改回装机时的样子，这一步不能撤销。\n\n"
                + "服务器和账号不会动（不会退出登录），窗口位置和大小、各媒体库的排序筛选视图、播放器音量也都保留。",
            "恢复默认").ConfigureAwait(true);

        if (!agreed) return;

        SettingsReset.Restore(_settings.Settings);
        _settings.Save();

        var ui = Settings.Ui;
        ThemeHost.Apply(ui.Theme);
        ShellPrefs.Apply(ui);

        await ReloadAsync().ConfigureAwait(true);

        // 说一声。屏上多半看得出来（配色可能整套换了、六十行读数都动了），可本来就都在默认值上的人按一下会什么都
        // 看不见 —— 一颗看起来没反应的按钮，下一步就是再按一遍。顺带把「没动的是哪些」再讲一遍，那是关于一次
        // 恢复默认最该让人放心的一句。
        Notify(null, "设置已改回装机时的样子。服务器、账号、窗口位置和各媒体库的排序筛选都没有动。",
            InfoBarSeverity.Success);
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

    private SettingChoiceRow Pick<T>(string label, string? note, IEnumerable<(string Label, T Value)> options, Func<T> read, Action<T> write, IEqualityComparer<T> comparer, Func<T, string>? describe = null, Action? after = null)
    {
        var (choices, selected) = Options(options, read, write, comparer, describe);
        return new SettingChoiceRow(label, note, choices, selected, Save, after);
    }

    /// <summary>
    /// The entries of one drop-down and which of them is current. Split out of <see cref="Pick"/> for the one
    /// row whose list arrives after the page does — 音频输出设备, whose devices have to be read out of a
    /// throwaway libmpv context — so that refilling it goes through exactly the same arithmetic, the fallback
    /// entry below included, rather than a second copy of it.
    /// </summary>
    private static (List<SettingChoice> Choices, SettingChoice? Selected) Options<T>(
        IEnumerable<(string Label, T Value)> options,
        Func<T> read,
        Action<T> write,
        IEqualityComparer<T> comparer,
        Func<T, string>? describe)
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
        // shader group removed since it was picked, a pair of headphones that has been unplugged. Leaving the
        // box empty hides the setting, and worse, arms it: an unselected ComboBox takes whatever the next click
        // lands on, and the stored value is never written back, so opening the list to see what it says is
        // enough to lose it. It gets an entry of its own instead, marked as having come from the file, and
        // choosing it writes the same value again.
        if (selected is null && current?.ToString() is { Length: > 0 } stored)
        {
            selected = new SettingChoice(describe?.Invoke(current) ?? $"{stored}（设置文件中的值）", () => write(current));
            choices.Add(selected);
        }

        return (choices, selected);
    }

    private SettingChoiceRow Choice<T>(string label, IEnumerable<(string Label, T Value)> options, Func<T> read, Action<T> write, string? note = null, Func<T, string>? describe = null) =>
        Pick(label, note, options, read, write, EqualityComparer<T>.Default, describe);

    /// <summary>
    /// A drop-down over one of the mpv option catalogues. Matched case-insensitively, because these values
    /// go into a settings file a person may well have edited by hand, and mpv itself does not care.
    /// <para>
    /// <paramref name="mpvOption"/> is required rather than optional: every row built by this helper exists to
    /// set one named mpv option, so the one that forgets to say which is a compile error rather than a row the
    /// reader has to guess at. See <see cref="Annotate"/>.
    /// </para>
    /// </summary>
    private SettingChoiceRow Mpv(string label, IReadOnlyList<MpvChoice> options, Func<string> read, Action<string> write, string mpvOption, string? note = null, Action? after = null) =>
        Pick(label, Annotate(note, mpvOption), options.Select(option => (Label: option.Label, Value: option.Value)), read, write, StringComparer.OrdinalIgnoreCase, after: after);

    private SettingToggleRow Toggle(string label, string note, Func<bool> read, Action<bool> write, string mpvOption = "") =>
        new(label, Annotate(note, mpvOption) ?? "", read(), write, Save);

    private SettingNumberRow Number(string label, double minimum, double maximum, Func<int> read, Action<int> write, string? note = null, Action? after = null, string mpvOption = "") =>
        new(label, Annotate(note, mpvOption), minimum, maximum, read(), value => write((int)value), () => read(), Save, after);

    private SettingSliderRow Slider(string label, double minimum, double maximum, double step, Func<int> read, Action<int> write, string? note = null, string mpvOption = "") =>
        new(label, Annotate(note, mpvOption), minimum, maximum, step, read(), value => write((int)value), Save);

    /// <summary>
    /// A row's note with the mpv option it writes named at the end — 「补偿刷新率不匹配造成的抖动（mpv：
    /// interpolation）」, or just 「mpv：gpu-api」 on a row that had no note of its own.
    /// <para>
    /// Composed here rather than typed into two dozen note strings, so the shape cannot drift row to row. It
    /// exists because the page could not answer 「哪一行是 interpolation」 — the notes said what each row does
    /// and never what mpv calls it, which is the name every piece of mpv documentation is indexed by. Rows whose
    /// setting is this client's own behaviour rather than an mpv option pass nothing and read as before.
    /// </para>
    /// </summary>
    private static string? Annotate(string? note, string mpvOption)
    {
        var option = mpvOption.Trim();
        if (option.Length == 0) return note;

        return string.IsNullOrWhiteSpace(note) ? $"mpv：{option}" : $"{note}（mpv：{option}）";
    }

    /// <summary>
    /// A searchable list of the machine's font families. Kept in a field as well as returned: the scan
    /// finishes after the card is built, and this is the row it has to be handed to.
    /// <para>
    /// Whatever has already been scanned goes in immediately, which is what a second visit to the page
    /// gets — the library reads the font files once per session, so only the first visit ever sees the
    /// row hold nothing but the stored value.
    /// </para>
    /// </summary>
    private SettingFontRow Font(string label, string? note, Func<string> read, Action<string> write, string mpvOption = "")
    {
        var row = new SettingFontRow(label, Annotate(note, mpvOption), read(), write, Save);

        if (_fonts is { Ready.Families.Count: > 0 } library) row.Fill(library.Ready);

        _subtitleFont = row;
        return row;
    }

    private SettingTextRow Text(string label, string placeholder, Func<string> read, Action<string> write, string? note = null) =>
        new(label, note, placeholder, read(), typed =>
        {
            var value = typed.Trim();
            write(value);
            return value;
        }, Save);

    /// <summary>
    /// 一行读数，见 <see cref="SettingFactRow"/>。不接设置，所以不带 <see cref="Save"/> —— 它只是把一件事
    /// 说出来，顺带给一个去处。
    /// </summary>
    private static SettingFactRow Fact(string label, string? note, string value, string? actionLabel = null, Action? act = null) =>
        new(label, note, value, actionLabel, act);

    /// <summary>
    /// A text box holding a filesystem path. <see cref="Text"/> with one pair of surrounding double quotes
    /// taken off as well as the whitespace: Explorer's 「复制为路径」 puts them on the clipboard, pasting one in
    /// is the ordinary way to fill such a box, and a double quote cannot occur in a Windows path — so a value
    /// wearing them is always a paste and never a filename. See <see cref="TypedPath.Clean"/>.
    /// </summary>
    private SettingTextRow PathBox(string label, string placeholder, Func<string> read, Action<string> write, string? note = null) =>
        new(label, note, placeholder, TypedPath.Clean(read()), typed =>
        {
            var value = TypedPath.Clean(typed);
            write(value);
            return value;
        }, Save);

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
