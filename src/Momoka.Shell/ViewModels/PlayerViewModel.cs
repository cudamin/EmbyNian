using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Momoka.Configuration;
using Momoka.Diagnostics;
using Momoka.Emby;
using Momoka.Infrastructure;
using Momoka.Mpv;
using Momoka.Playback;
using Momoka.Services;
using Momoka.Shell.Media;
using Momoka.Shell.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Momoka.Shell.ViewModels;

/// <summary>
/// Everything the player does that is not drawing: what 「播放」 means, the metadata round trips a play
/// needs, every property written to mpv, the 跳过 decision, the 统计 readings, the chapter stills, and the
/// three playback settings the ⚙ menu edits. <c>PlayerPage</c> is left with the visual tree — the reveal
/// rule, the hit tests, the window commands, the menus it builds and the self-check probes.
/// <para>
/// An <see cref="ObservableObject"/> rather than a <see cref="PageViewModel"/>: the player is not a
/// navigable page, has no busy ring and no InfoBar of its own — it reports through the shell's one
/// notification channel, which is what <see cref="Noticed"/> carries.
/// </para>
/// <para>
/// The eight services arrive through the constructor, so this type never asks a container for anything —
/// the UI thread among them, which is why nothing here names a dispatcher.
/// What it cannot do itself — take the window over, move a cursor, place a preview box — it asks for by
/// raising one of the events below, and the page answers with the layout half of the same action.
/// </para>
/// <para>
/// The one piece of real machinery here is <c>_playerHold</c>, and it is the answer to
/// 「切换集数的时候画面错乱」. A hold is taken before the call that stops whatever is playing and released
/// only once that playback has ended <em>and</em> any auto-advance after it has been decided. Between
/// those two points nothing is playing at all and the player must stay up anyway; it is a counter rather
/// than a flag because an episode change nests one playback inside another's <c>finally</c>.
/// </para>
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    private const string Category = "播放";

    /// <summary>The seek slider's integer range, mirrored into the tooltip converter by the page.</summary>
    internal const double SeekScale = 1000;

    /// <summary>How long after the user last touched the seek bar its own value wins over mpv's.</summary>
    private const long ScrubGraceMilliseconds = 400;

    /// <summary>
    /// 一条已发给 mpv 的跳转等多久还到不了，就算「到不了那儿」：进度条放弃按住自己的值、重新跟着 mpv 走。
    /// 5 秒盖得住 hr-seek 在最不利的文件上的耗时，也短到一条失败的跳转不至于把进度条钉死半天。
    /// </summary>
    private const long SeekGiveUpMilliseconds = 5000;

    /// <summary>
    /// mpv 报的位置离跳转目标多近（秒）就算「已经落定」。一帧的量级是几十毫秒，这一档宽到盖过
    /// <c>DiffersFrom</c> 那 0.25 秒的合并粒度，窄到认不出任何一段真实的播放进度。
    /// </summary>
    private const double SeekLandedSeconds = 0.75;

    private const int PlayGlyphCode = 0xE768;
    private const int PauseGlyphCode = 0xE769;
    private const int VolumeGlyphCode = 0xE767;
    private const int MutedGlyphCode = 0xE74F;

    /// <summary>
    /// How many times, and how far apart, the three things that can only be learned by asking again keep
    /// asking: the track list, mpv's own chapter marks and the picture's shape. Six seconds in half-second
    /// steps, which covers a file that takes its time to open and gives up rather than polling a stuck
    /// backend forever.
    /// <para>
    /// Both numbers were written out three times each, once per poller, and they were only the same by
    /// coincidence — nothing would have complained if a fix to one had left the other two alone.
    /// </para>
    /// </summary>
    private const int PollAttempts = 12;

    private const int PollIntervalMilliseconds = 500;

    /// <summary>The chapter preview's picture width, mirrored from the XAML so the two cannot drift.</summary>
    internal const int ChapterPeekWidth = 212;

    /// <summary>倍速's own range (2026-09-25 用户令：0.1 到 1 每档 0.1、1 到 20 每档 1). The wheel offers
    /// exactly these, and the keys clamp to their ends.</summary>
    internal static readonly double[] SpeedChoices =
    [
        0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0,
        2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0,
        11.0, 12.0, 13.0, 14.0, 15.0, 16.0, 17.0, 18.0, 19.0, 20.0,
    ];

    /// <summary>The delay nudges both A/V delay submenus offer, in seconds.</summary>

    private readonly PlaybackService _playback;
    private readonly ISettingsService _settings;
    private readonly EmbySession _session;
    private readonly EmbyImageStore _images;
    private readonly ShaderGroupResolver _shaders;

    /// <summary>
    /// The UI thread. Every one of the player service's events arrives on whichever thread mpv's event loop
    /// happens to be on, and all of them end in a bound property.
    /// </summary>
    private readonly IUiDispatcher _ui;

    private readonly SkipCoordinator _skips = new();

    /// <summary>
    /// 视频窗「要一份菜单」的三道闸门（见 <see cref="Momoka.Mpv.MenuRequestGate"/>）：选集、版本、画面各一道。
    /// 收下按键的是 uosc 的控件，它一旦自激，宿主在几十秒里能收到几十万条请求 —— 这个闸门就是那一下的活口。
    /// 各占一道而不是共用一道：它们是不同的按钮/右键，用户点完一个再点另一个不该被对方吃掉。
    /// </summary>
    private readonly MenuRequestGate _episodeMenuGate = new();

    private readonly MenuRequestGate _versionMenuGate = new();

    private readonly MenuRequestGate _pictureMenuGate = new();

    /// <summary>
    /// 独占模式下最近一次推给视频窗那颗「跳过」按钮的文案（<c>""</c>＝已收摊）。状态每秒采十次都会走
    /// <see cref="ShowSkipPrompt"/>，只在文案真变了才发一条 script-message，免得把同一句「跳过片头」
    /// 一秒重发十遍。集成模式那颗 XAML 按钮不经这里（<see cref="HeadlessPlayback"/> 才推）。
    /// </summary>
    private string _skipPushed = "";

    /// <summary>
    /// Cancels anything in flight on the way out. Created here rather than per attach and deliberately
    /// never disposed: a poll waiting on a five-hundred-millisecond delay comes back and reads
    /// <c>Token</c>, and a disposed source would answer that with an exception instead of a cancellation.
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// 起播准备期的意图代次：封面等待、媒体详情、选集解析这几段网络之间，用户随时可能按停止 ——
    /// 停止要能撤掉<b>还在准备中</b>的起播，而不是只停已经建立的播放。规则在 Core 的
    /// <see cref="StartIntent"/>（新起播作废旧起播，用户停止作废全部），Transport 那一头接线。
    /// </summary>
    private readonly StartIntent _startIntent = new();

    /// <summary>
    /// The stills for the hover preview, by chapter index, decoded once each. A null value is a chapter
    /// the server had no picture for, remembered so a scrub back and forth does not keep asking.
    /// </summary>
    private readonly Dictionary<int, BitmapImage?> _chapterStills = [];

    private EmbyItem? _parent;
    private EmbyItem? _nowPlaying;

    /// <summary>See the class remarks: the reason the player survives the seam between two episodes.</summary>
    private int _playerHold;

    /// <summary>
    /// 这是第几场「开始播放」。每进一次 <c>PlayerViewModel.Transport.StartPlaybackAsync</c> 就加一，那一次调用
    /// 自己记着自己的号，收尾时要开口之前先问一句「我还是最新那一场吗」。
    /// <para>
    /// 换集、换版、挂着片子再点一部，都是**把上一场停掉、再开下一场**：上一场那条 await 返回的时候，下一场
    /// 往往已经在开了。它若照样弹一句「播放已停止」、照样让主页重新装货，屏上就是一句假话，主页还白装一次货 ——
    /// 2026-09-21「换版本后界面卡死」就是这一句把主页带进了重排暴走（见 <c>HomePage.UpdateLibraryOverlay</c>）。
    /// </para>
    /// <para>
    /// 不拿 <see cref="_generation"/> 顶这一位：那是「这个回答属于哪一场」，由新一场被宣布时（<c>RaiseNowPlaying</c>）
    /// 推进 —— 实测旧一场收尾时新一场的文件还没开（日志里「播放已停止」早于「开始播放」150 毫秒），那一刻
    /// <see cref="_generation"/> 还是旧数，判不出来。这一位在调用入口同步推进，早于停旧那一刀，判断才成立。
    /// </para>
    /// </summary>
    private int _playbackAttempt;

    /// <summary>从点下播放到收场；进场淡入完成那一拍的换手（收浏览层＋自动全屏）只认它 —— 工具预览不持它，不跳窗。</summary>
    internal bool PlaybackLifecycleActive => _playerHold > 0;

    /// <summary>
    /// 隔离探针专用（<c>--probe-player-motion</c>）：不启动真实播放也要让「播放生命周期在途」成立，
    /// 页面进场那条自动全屏支路（<see cref="Views.PlayerPage"/> 的 <c>EnterAutoFullscreen</c>）才肯走。
    /// 探针进程随即整体拆除，不设对应的释放。
    /// </summary>
    internal void ProbeHoldPlayback() => _playerHold++;

    /// <summary>
    /// Which playback an answer belongs to. Every poll checks it on both sides of every await, so an
    /// episode switch mid-poll cannot land the old file's chapter list or track list on the new one.
    /// </summary>
    private int _generation;

    /// <summary>True while a bound property is being written from mpv rather than by the user.</summary>
    private bool _pushing;

    /// <summary>Whether the page is showing the player, which is what makes the 跳过 offer live.</summary>
    private bool _playerUp;

    /// <summary>Prevents repeated boundary clicks from starting the same server lookup or replacement twice.</summary>
    private bool _episodeLookupBusy;
    private string? _episodeSwitchTargetId;

    /// <summary>
    /// 正在换的那一版（换版途中挡第二下）。存<b>对象</b>而不是源 Id：Emby 对一部分直连文件不返回源 Id，
    /// 几个版本会撞成同一个空 Id —— 同 <see cref="Playback.MediaVersionSwitch.Same"/> 的规矩。
    /// </summary>
    private MediaSource? _versionSwitchTarget;

    private long _seekTouched;
    private double? _seekPending;

    /// <summary>
    /// 上一条发给 mpv 的跳转落在哪个比例位置，null＝没有在途的跳转。mpv 在跳转落定之前会一直报
    /// 「正在离开的那个位置」，把这个回声写回进度条就是拇指倒退那一下 —— 见 <c>SeekBarFollows</c>。
    /// </summary>
    private double? _seekSent;

    /// <summary>那条跳转是什么时候发出去的。超时（<see cref="SeekGiveUpMilliseconds"/>）之后不再等它。</summary>
    private long _seekSentAt;

    private readonly VolumeMemory _volumeMemory = new();
    private Task? _volumeSaveTask;
    private long _volumeRetryAt;
    private long _audioDelayRequest;

    /// <summary>The last aspect handed to the window, so an unchanged one is not written again.</summary>
    private double _aspect;

    // ---- 着色器档位的本次播放状态 -------------------------------------------------
    //
    // 档位固定用全屏那套（他的拍板，2026-09-11）：开播时按所在显示器尺寸算一次方案，进退全屏和拖动都
    // 不再换链 —— ArtCNN 这类放大器在核显上每个尺寸的冷编译要数秒，随全屏实时换链就是「画面停在旧
    // 尺寸贴在左上角」（探针读数在 work/embedprobe/）。判定机制仍在 Core（OutputWatch 加 ShaderTier），
    // 这里只剩「这次播放是哪个文件」和「用户有没有自己钉住一条链」。

    /// <summary>What a re-measurement needs to resolve the chain again: the file, its source and its series.</summary>
    private (EmbyItem Item, MediaSource Source, EmbyItem? Parent)? _shaderContext;

    private OutputWatch? _outputWatch;
    private ShaderSurface _surface;
    private ShaderDecision _shaderPlan;
    private ShaderAutomationSettings? _shaderSettings;
    private ShaderGroupResolver? _shaderResolver;

    /// <summary>The output size the launch decision was made against, so 播放信息 can say when it has moved.</summary>
    private (int Width, int Height) _launchOutput;

    /// <summary>
    /// Set once the user picks a chain from the ⚙ menu. An A/B comparison that the window size silently undid
    /// would not be one, so from that point on this playback keeps what was picked.
    /// </summary>
    private bool _shaderPinned;

    /// <summary>Log category for everything above — the same one Core's own shader decisions use.</summary>
    private const string ShaderLog = "shader";

    /// <summary>
    /// Emby's own chapter marks for what is playing, converted once. Two things need them: the 跳过 plan
    /// the file starts with, and the hover preview's still — which is indexed against this list and no
    /// other, because that index is the whole meaning of <c>/Items/{id}/Images/Chapter/{index}</c>.
    /// </summary>
    private IReadOnlyList<SkipChapter> _embyMarks = [];

    /// <summary>
    /// Which chapter the preview is naming and which one it is showing a picture of, so a hover that stays
    /// inside both does no work. Two numbers because the two lists can disagree — see
    /// <see cref="ChapterTimeline"/>.
    /// </summary>
    private int _peekChapter = -1;
    private int _peekStill = -1;

    /// <summary>Whether <see cref="Connect"/> has taken up the player events; a second call does nothing.</summary>
    private bool _connected;

    public PlayerViewModel(
        PlaybackService playback,
        ISettingsService settings,
        EmbySession session,
        EmbyImageStore images,
        ShaderGroupResolver shaders,
        IUiDispatcher ui)
    {
        _playback = playback;
        _settings = settings;
        _session = session;
        _images = images;
        _shaders = shaders;
        _ui = ui;

        // Every number the bar shows starts from the same empty snapshot the stop path returns it to, so
        // the clock reads 0:00 and the glyph reads 播放 before anything has ever played.
        ApplyStatus(new PlayerStatus());
    }

    /// <summary>
    /// Takes up the four player events, once, before anything is played. They all end in a bound property,
    /// so there is no point listening to them until there is a page to show the result — which is why the
    /// page decides when this happens, even though it no longer has to supply the thread it happens on.
    /// </summary>
    internal void Connect()
    {
        if (_connected) return;

        _connected = true;

        // Every one of these arrives on whichever thread mpv's event loop happens to be on.
        _playback.ProgressChanged += OnProgressChanged;
        _playback.StatusUpdated += OnStatusChanged;
        _playback.TracksUpdated += OnTracksChanged;
        _playback.NowPlayingUpdated += OnNowPlayingChanged;
        _playback.VideoWindowUpdated += OnVideoWindowMessage;
        ShellPrefs.ShortcutsChanged += NativeShortcutsChanged;
    }

    /// <summary>
    /// Cancels anything in flight on the way out. The process is about to end either way; this is so a
    /// poll waiting on a five-hundred-millisecond delay does not come back to a disposed session.
    /// </summary>
    private Task? _shutdownTask;
    private EmbySessionScope? _playbackScope;
    private long _activeIntent;
    private CancellationTokenSource? _preparing;
    private TaskCompletionSource? _playbackIdle;
    private bool? _presentedHeadless;
    private bool? _preparingPictureInHost;
    internal Action<bool>? PreparePresentation { get; set; }

    internal void Shutdown() => _ = ShutdownAsync();

    internal Task ShutdownAsync() => _shutdownTask ??= ShutdownPlaybackAsync();

    private async Task ShutdownPlaybackAsync()
    {
        _startIntent.CancelAll();
        _preparing?.Cancel();
        FlushVolume(settled: false);
        _playback.ProgressChanged -= OnProgressChanged;
        _playback.StatusUpdated -= OnStatusChanged;
        _playback.TracksUpdated -= OnTracksChanged;
        _playback.NowPlayingUpdated -= OnNowPlayingChanged;
        _playback.VideoWindowUpdated -= OnVideoWindowMessage;
        ShellPrefs.ShortcutsChanged -= NativeShortcutsChanged;
        _lifetime.Cancel();
        _generation++;
        _coverGeneration++;
        await _playback.StopAsync().ConfigureAwait(true);
        if (_playbackIdle is { } idle) await idle.Task.ConfigureAwait(true);
    }

    // ---- what only the page can do ----------------------------------------------

    /// <summary>Something worth saying through the shell's one notification channel.</summary>
    internal event Action<string, InfoBarSeverity>? Noticed;

    /// <summary>The item's watched flag and resume position have changed on the server.</summary>
    internal event Action? RefreshRequested;

    /// <summary>Take the window over: video surface on, shell collapsed, chrome up, keyboard here.</summary>
    internal event Action? PlayerShown;

    /// <summary>Put the window back: fullscreen left, 置顶 dropped, cursor shown, shell returned.</summary>
    internal event Action? PlayerHidden;

    /// <summary>
    /// 独占模式右键菜单点了「播放信息…」（<see cref="VideoWindowContract.MediaInfo"/>）。弹窗归外壳：
    /// 独占播放时播放页是摘下去的、没有可挂对话框的树，主窗口还在（可浏览），由 <c>ShellPage</c> 接住
    /// 弹同一张。集成模式不经过这里 —— 页面上的菜单行自己弹。
    /// </summary>
    internal event Action? MediaInfoRequested;

    /// <summary>
    /// 右键画面菜单末尾那三行设置入口被点了（<c>字幕</c>／<c>视频输出</c>／<c>音频输出</c>，2026-10-01 用户令）。
    /// 开窗归外壳：独占模式播放页是摘下去的，集成模式的设置窗口也不归页面管 —— 两条管线因此共用这一条出口
    /// （集成侧 <c>PlayerPage.OnMoreMenuOpening</c> 的菜单行、独占侧 <see cref="VideoWindowContract.OpenSettings"/>
    /// 都是调 <see cref="RequestSettings"/>），像 <see cref="MediaInfoRequested"/> 那样由 <c>ShellPage</c> 接住。
    /// </summary>
    internal event Action<PlayerSettingsLink>? SettingsRequested;

    /// <summary>
    /// 「去设置里改这一块」：把画面菜单那一行交给外壳，由它开设置窗口并落在该行那张卡上。
    /// <para>
    /// 一个出口而不是两处各开各的：独占模式播放时播放页根本不在树上，只有 view model 这条线通到外壳；集成
    /// 模式走同一条，于是「点这三行会发生什么」在两个模式里不可能写出两种答案。
    /// </para>
    /// </summary>
    internal void RequestSettings(PlayerSettingsLink link) => SettingsRequested?.Invoke(link);

    /// <summary>停止后端前，先让浏览页真正接住画面；独占播放没有附加页面，不需要这一步。</summary>
    internal Func<Task>? PrepareStopAsync { get; set; }

    private Task _stopTask = Task.CompletedTask;

    /// <summary>A new file is on screen: the chrome starts its countdown from now.</summary>
    internal event Action? PlaybackStarted;

    /// <summary>The chapter marks were replaced — the bar's ticks and its preview are both stale.</summary>
    internal event Action? ChaptersChanged;

    /// <summary>
    /// One status snapshot has been applied. Carries the three things the bar draws that are not
    /// bindable: the tooltip converter's run time, the tick layout when the duration finally arrives, and
    /// the chrome's 「stay up while paused」 rule.
    /// </summary>
    internal event Action<PlayerStatus>? StatusApplied;

    /// <summary>The picture's own shape, for the window to keep itself in. Zero means 「stop keeping」.</summary>
    internal event Action<double>? PictureAspectChanged;

    /// <summary>
    /// 片子<b>码流自己</b>的宽高比，换片时报一次。与 <see cref="PictureAspectChanged"/> 分开是因为两者
    /// 的用途不同：那个给窗口整形（要的是「显示成什么形状」，全屏时含黑边），这个给退场保留那一帧
    /// 定位（要的是「画面本来什么形状」，与窗口无关）。零表示这一部读不到码流尺寸。
    /// </summary>
    internal event Action<double>? SourceAspectChanged;

    // ---- what the chrome shows ---------------------------------------------------

    [ObservableProperty]
    public partial string? Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleVisibility))]
    public partial string? Subtitle { get; set; }

    /// <summary>The 画质 label under the transport buttons — 「1080p HEVC」 and the like.</summary>
    [ObservableProperty]
    public partial string? SourceLabel { get; set; }

    [ObservableProperty]
    public partial string? PositionClock { get; set; }

    [ObservableProperty]
    public partial string? DurationClock { get; set; }

    [ObservableProperty]
    public partial string? RemainingClock { get; set; }

    /// <summary>播放/暂停's own glyph, so the button never has to be told which state it is in.</summary>
    [ObservableProperty]
    public partial string? PlayPauseGlyph { get; set; }

    [ObservableProperty]
    public partial string? SpeedLabel { get; set; }

    [ObservableProperty]
    public partial double SpeedValue { get; set; } = 1;

    private long _speedTouched;

    /// <summary>How much of the file mpv has buffered, drawn behind the thumb.</summary>
    [ObservableProperty]
    public partial double CacheFraction { get; set; }

    /// <summary>The one-line progress the bar leaves behind when it hides.</summary>
    [ObservableProperty]
    public partial double ThinFraction { get; set; }

    /// <summary>
    /// The seek bar's own position, on the integer scale the slider is declared with. Two-way: mpv writes
    /// it as playback advances and the user writes it by dragging, and <see cref="OnSeekValueChanged"/> is
    /// what tells the two apart.
    /// </summary>
    [ObservableProperty]
    public partial double SeekValue { get; set; }

    /// <summary>音量, 0–<see cref="AudioSettings.MaxVolume"/>, two-way for the same reason the seek bar is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeLabel))]
    public partial double Volume { get; set; }

    /// <summary>
    /// The figure above the rail. 「给音量条上方加上数字」 (2026-09-04) — the same readout that
    /// 「音量条不需要…上方的数字」 took off on 2026-09-03, so it is back deliberately rather than by accident:
    /// with a ceiling of 130 the thumb's position no longer says whether the film is at 100 or above it, and
    /// that is the one thing a viewer reaching for the wheel wants to know.
    /// <para>
    /// Rounded rather than truncated, and to a whole number: the wheel and the keys move in whole steps and
    /// mpv is told a whole number, so a decimal here could only ever be an mpv echo the slider has not caught
    /// up with.
    /// </para>
    /// </summary>
    public string VolumeLabel => Math.Round(Volume).ToString("0", CultureInfo.InvariantCulture);

    public double VolumeMaximum => AudioSettings.MaxVolume;

    /// <summary>The rail's speaker glyph, or the crossed-out one while muted.</summary>
    [ObservableProperty]
    public partial string? SoundGlyph { get; set; }

    /// <summary>Whether this playback has siblings, which is what 上一集/下一集/选集 need to exist for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EpisodeControlsVisibility))]
    public partial bool EpisodeControlsVisible { get; set; }

    /// <summary>
    /// 这个条目挂了几版文件 —— 有第二版才有了「版本」按钮（<see cref="VersionControlsVisibility"/>）。
    /// <para>
    /// Emby 上一个条目挂两版是常态（4K 与 1080p、剧场版与导演剪辑版），而绝大多数条目只有一版：
    /// 一个常年摆在那里、点开只有一行的按钮比没有这个按钮更烦人，所以它按这个数露面。
    /// 独占模式视频窗里那条菜单项不受这里管 —— uosc 的控件表是静态的，见
    /// <see cref="PushVersionMenuAsync"/>。
    /// </para>
    /// <para>
    /// **只由「手上这个条目」算出来，不从别处推**（2026-09-21）。它的三个写点全都在
    /// <see cref="CurrentItem"/> 上，于是无论谁先后动 —— 媒体信息先到、还是单集列表先补齐 —— 这一格
    /// 都是那次赋值的副产品，两处不可能互相矛盾。原先它是 <c>OnNowPlayingChanged</c> 按
    /// <c>item.MediaSources.Count</c> 直接写的，而 <see cref="FillSiblingsAsync"/> 那个 await 之后又
    /// 把「手上这个条目」换成了服务器的记录；两边一旦换了次序，控制条上就会出现一颗点开只有
    /// 「没有可切换的版本」的按钮（自检逮到过，见 <c>ProbeNarration</c> 的 <c>VersionButton</c>）。
    /// </para>
    /// <para>
    /// 与 <see cref="Episodes"/> 之间没有这样的引用关系：那两个按钮由
    /// <see cref="EpisodeControlsVisible"/> 管，而它在补齐单集列表时是被明写的一行，因为「这个条目是不是
    /// 单集」本来就不该由列表长度推——剧集详情里只有一集也是单集。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionControlsVisibility))]
    public partial bool VersionControlsVisible { get; set; }

    /// <summary>
    /// 手上这个条目 —— 正在播的那一条，带媒体信息与媒体源。三个写点：
    /// <see cref="PlayerViewModel.Transport.StartPlaybackAsync"/> 拿到详情之后一次，
    /// <see cref="PlayerViewModel.Transport.FillSiblingsAsync"/> 等待服务器记录之后一次（与
    /// <see cref="Episodes"/> 同一拍），<see cref="PlayerViewModel.Transport.SwitchVersion"/> 换版之后一次。
    /// <para>
    /// 它存在的理由是让 <see cref="VersionControlsVisible"/> 无懈可击：列表与「有几版」是同一次赋值的
    /// 两个结果，没有第二个人需要记得跟着改。见那一格的注释。
    /// </para>
    /// </summary>
    internal EmbyItem? CurrentItem
    {
        get => _currentItem;
        set
        {
            _currentItem = value;
            VersionControlsVisible = value is not null && value.MediaSources.Count > 1;

            // 独占模式视频窗里那颗「版本」按钮吃的是同一个数（用户令 2026-09-23「只有一个版本的情况下
            // 不显示…」）：uosc 的控件表是静态的，露不露面得宿主把答案告诉它。写在同一格里，「有几版」
            // 与「那颗按钮该不该在」就是同一次赋值的两个结果，两处不可能互相矛盾 —— 与上面那一格的
            // 理由一模一样。
            NoteVersionCount();
        }
    }

    private EmbyItem? _currentItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SkipVisibility))]
    public partial bool SkipOffered { get; set; }

    [ObservableProperty]
    public partial string? SkipCaption { get; set; }

    [ObservableProperty]
    public partial string? SkipTip { get; set; }

    /// <summary>How much of the offer's fifteen seconds is left, as a fraction.</summary>
    [ObservableProperty]
    public partial double SkipRemaining { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoverVisibility))]
    public partial bool CoverUp { get; set; }

    [ObservableProperty]
    public partial string? CoverMessage { get; set; }

    /// <summary>
    /// 遮罩垫底的背景图（2026-09-15「视频刚开播还在加载缓存没有正片画面时背景要用背景图」）。取图的
    /// 时机有两处：<see cref="PlayerViewModel.Transport.StartPlaybackAsync"/> 在遮罩亮起的同一刻就用
    /// 用户点的卡片开始取（媒体信息还在路上，等 <see cref="PlayerViewModel.Events.OnNowPlayingChanged"/>
    /// 才动手就晚了 —— 用户看到的加载态里图一张都还没有），详情就绪后的 OnNowPlayingChanged 再补一次
    /// 更准的。顺序 本集背景图 → 父级（季/剧）背景图 → 缩略图（<see cref="LoadCoverBackdropAsync"/>），
    /// 按条目 Id 去重、独立代际防串台；取不到时保持 null，垫底退回纯色 —— 图是添头，不是承重墙。
    /// </summary>
    [ObservableProperty]
    public partial BitmapImage? CoverBackdrop { get; set; }

    /// <summary>
    /// 同一张背景图的像素版，给起播整屏那块 DWM 覆盖层用（2026-09-25 用户令「不要黑屏，主页和背景图
    /// 无缝切换」）。
    /// <para>
    /// 与 <see cref="CoverBackdrop"/> 一次解码、一起就位：那块层显示的必须是遮罩里那张图本身，否则窗口
    /// 长大的一两拍又变成一块近黑。取不到（解码失败、这个条目根本没图）时保持 null，覆盖层退回纯色 ——
    /// 那时它与这条修复之前一模一样，不多也不少。
    /// </para>
    /// </summary>
    internal VideoFrame? CoverBackdropFrame { get; private set; }

    /// <summary>背景图正在取/已就位的条目 Id：同一部片不重复下载；真正拿到图才算数，取空就忘掉以便重试。</summary>
    private string? _coverBackdropItemId;

    /// <summary>同一条目的调用等待同一次完整解码，不能把“已经在取”误当成“已经就绪”。</summary>
    private Task _coverBackdropLoad = Task.CompletedTask;

    /// <summary>背景图自己的代际号。与 <c>_generation</c> 分开：提前取图时播放代际还没递增，跟着它会白取。</summary>
    private int _coverGeneration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChapterCaptionVisibility))]
    public partial string? ChapterCaption { get; set; }

    /// <summary>The hovered moment, as a clock. Updated on every pointer move along the seek track.</summary>
    [ObservableProperty]
    public partial string? ChapterClock { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChapterStillVisibility))]
    public partial BitmapImage? ChapterStill { get; set; }

    public Visibility SubtitleVisibility => Show(Subtitle is { Length: > 0 });

    public Visibility EpisodeControlsVisibility => Show(EpisodeControlsVisible);

    public Visibility VersionControlsVisibility => Show(VersionControlsVisible);

    public Visibility SkipVisibility => Show(SkipOffered);

    public Visibility CoverVisibility => Show(CoverUp);

    /// <summary>
    /// Whether the hover preview has a picture to show. A server that never extracted chapter images has
    /// none to send — <c>ChapterInfo.HasImage</c> is then false for every chapter, and the fetch is not even
    /// attempted — and a fixed-size <c>Image</c> with no source is an empty frame rather than nothing:
    /// 「预览没有画面」. The name and the time are still worth having, so the picture collapses and they stay.
    /// </summary>
    public Visibility ChapterStillVisibility => Show(ChapterStill is not null);

    /// <summary>
    /// Whether there is a chapter to name. A file whose marks the server never extracted — and one hovered
    /// before its first mark — has none, and an empty <c>TextBlock</c> still takes a line's height plus the
    /// stack's spacing, which would leave a gap over the clock. Collapsed, the box becomes an honest time
    /// chip instead.
    /// </summary>
    public Visibility ChapterCaptionVisibility => Show(ChapterCaption is { Length: > 0 });

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // ---- what the page's menus and probes read -----------------------------------

    /// <summary>The last snapshot mpv reported. Not itself bindable — everything drawn from it is.</summary>
    internal PlayerStatus Status { get; private set; } = new();

    /// <summary>mpv's track list, which the 音轨 and 字幕 pickers are built from.</summary>
    internal IReadOnlyList<MpvTrack> Tracks { get; private set; } = [];

    /// <summary>The siblings this playback started with, and the only list 选集 and 下一集 use.</summary>
    internal IReadOnlyList<EmbyItem> Episodes { get; private set; } = [];

    internal string PlayingItemId { get; private set; } = "";

    /// <summary>这个条目上的全部版本，顺序就是「版本」菜单里的次序。没在播时是空表。</summary>
    internal IReadOnlyList<MediaSource> Versions => MediaVersionSwitch.Versions(_nowPlaying);

    /// <summary>
    /// 正在放的那一版 —— <b>条目版本表里的那一行</b>，候选回退落定之后的那一版（见
    /// <see cref="PlaybackService.PlayingSource"/>）。菜单勾的就是它；认不出来时 null，那时菜单一行都不勾。
    /// </summary>
    internal MediaSource? PlayingSource => MediaVersionSwitch.Playing(_nowPlaying, _playback.PlayingSource);

    /// <summary>Where the chapter boundaries are, for the ticks the page draws under the slider.</summary>
    private IReadOnlyList<SkipChapter> _chapterMarks = [];
    internal TimelineChapterMap TimelineChapters { get; private set; } = TimelineChapterMap.Empty;
    internal IReadOnlyList<SkipChapter> ChapterMarks
    {
        get => _chapterMarks;
        private set
        {
            _chapterMarks = value;
            TimelineChapters = TimelineChapterMap.Build(value);
        }
    }

    /// <summary>The 着色器档位 in force, which the ⚙ menu opens on. Null is 「未启用」.</summary>
    internal ShaderGroup? ActiveShader { get; private set; }

    /// <summary>
    /// The eight chains the ⚙ menu offers: this machine's 显卡档 column, for the kind of source that is
    /// playing. The 老片源 and 高帧率 halves matter here and not in the settings page — the menu's dim right-hand
    /// column lists the files a row would actually load, which for a DVD includes <c>hdeband</c> and for a 60fps
    /// source is the ravu chain even on the 动画 rows.
    /// <para>
    /// Asked of the <b>file</b> (<see cref="ShaderAutomationSettings.Kind"/>) rather than read back off
    /// <see cref="ActiveShader"/>: there is no chain in force whenever 启用着色器 is off or the source is 8K, and
    /// the menu then fell back to the 不是老片源、不是高帧率 column. Picking 动画 · 微放大档 from it handed a 60fps
    /// file ArtCNN — 22 ms a frame, the one chain the 高帧率 axis exists to keep out of that cell — and
    /// <see cref="ApplyShaderGroup"/> pins it for the rest of the film.
    /// </para>
    /// </summary>
    internal IReadOnlyList<ShaderGroup> ShaderCatalog
    {
        get
        {
            var video = _shaderContext?.Source.PrimaryVideoStream;
            var settings = _shaderSettings ?? Settings.Shaders;
            var (vintage, fastMotion) = settings.Kind(video?.Height ?? 0, video?.FrameRate ?? 0);
            return ShaderGroupCatalog.For(settings.Gpu, vintage, fastMotion);
        }
    }


    /// <summary>
    /// How large the picture can be drawn now, and how large it would be at full screen. Supplied by the page,
    /// because neither the client area in physical pixels nor the monitor a window sits on is something a view
    /// model can ask about. A zero answer means 「nobody could say」, which the 档位 rule reads as 微放大档.
    /// <para>
    /// Read at every playback start and again whenever the window's geometry changes — see
    /// <see cref="NoteSurface"/> for what each kind of change costs.
    /// </para>
    /// </summary>
    internal Func<ShaderSurface>? MeasureSurface { get; set; }

    /// <summary>
    /// How fast the screen the picture lands on refreshes, in Hz — supplied by the page for the same reason as
    /// <see cref="MeasureSurface"/>, and 0 when nobody could say.
    /// <para>
    /// Read once per playback, for <see cref="PlaybackTicket.DisplayRefreshHz"/>: above about 120Hz 显示同步
    /// (and with it 插值) costs more than it returns, and that decision belongs to the launch rather than to
    /// every window move — see <see cref="MpvOutputOptions.ResolveSync"/>.
    /// </para>
    /// </summary>
    internal Func<double>? MeasureRefreshHz { get; set; }


    internal double SubtitleDelay { get; private set; }

    internal double AudioDelay { get; private set; }

    /// <summary>
    /// 跳过片头片尾. A setting rather than page state, and written straight through to the file: the ⚙ menu
    /// is the only place it is edited from mid-film, and the 设置 page shows the same field.
    /// </summary>
    internal SkipSectionMode SkipMode
    {
        get => Settings.Playback.SkipSections;
        set
        {
            Settings.Playback.SkipSections = value;
            _settings.Save();
            ApplySkipOffer();
        }
    }

    /// <summary>
    /// 跳过档位的行文案。集成模式右键那棵「跳过片头片尾」子菜单与独占模式推送的同名子菜单共用这一份
    /// （2026-09-29 统一右键菜单时从页面收编上来 —— 两处各写一份就会再漂开）。
    /// </summary>
    internal static string SkipModeLabel(SkipSectionMode mode) => mode switch
    {
        SkipSectionMode.Auto => "自动跳过",
        SkipSectionMode.Off => "关闭",
        _ => "询问"
    };

    /// <summary>同一档位在 <see cref="VideoWindowContract.SkipMode"/> 值域里的拼法（Parse 只认这三个词）。</summary>
    internal static string SkipModeToken(SkipSectionMode mode) => mode switch
    {
        SkipSectionMode.Auto => "auto",
        SkipSectionMode.Off => "off",
        _ => "ask"
    };

    internal bool AutoPlayNextEpisode
    {
        get => Settings.Playback.AutoPlayNextEpisode;
        set
        {
            Settings.Playback.AutoPlayNextEpisode = value;
            _settings.Save();
        }
    }

    private static long Now => Environment.TickCount64;

    private AppSettings Settings => _settings.Settings;

    /// <summary>
    /// 播放器快捷键的当前绑定（动作 Id → token），交给页面那头拼键派发（见 <c>ShortcutCatalog</c> /
    /// <c>PlayerPage.OnKeyDown</c>）。现读同一个单例设置，所以设置页里改一下、正开着的播放器当场就认新的，
    /// 不用任何通知管线。<see cref="Settings"/> 保持 private —— 页面只该够得着这一份，够不着别的设置。
    /// </summary>
    internal IReadOnlyDictionary<string, string> ShortcutBindings => Settings.Shortcuts.Bindings;

    /// <summary>
    /// 内置 libmpv 会话（进程内、可逐项钉选项），与外部 mpv.exe（named pipe 遥控）相对。
    /// <para>
    /// 这是<b>后端维度</b>的判据，只回答「这次播放是谁在跑」，不回答「画面画在哪」——后者归
    /// <see cref="PictureInHostWindow"/>。内置后端可以走集成管线（画面在本窗口）也可以走独立
    /// 管线（画面在 mpv 自建窗口），拿这一位当画面位置用，两头都会答错。
    /// </para>
    /// </summary>
    internal bool Embedded => (_playback.PlayingBackend ?? Settings.Mpv.Backend) == MpvBackendKind.BuiltInLibMpv;

    /// <summary>
    /// 「开始播放后自动全屏」, as the page reads it at the moment a playback starts.
    /// <para>
    /// Read through here rather than handed the whole settings document, which is the same rope
    /// <see cref="ShortcutBindings"/> and <see cref="AutoPlayNextEpisode"/> already hold: the page gets the one
    /// answer it needs and nothing else. Read fresh on every playback on purpose — the settings window is a
    /// second window, and a switch thrown in there has to be in force for the very next play without any
    /// notification pipe.
    /// </para>
    /// </summary>
    internal bool AutoFullscreenOnPlayback => Settings.Playback.AutoFullscreenOnPlayback;

    /// <summary>
    /// 「调整窗口大小后继续播放」（<see cref="Configuration.PlaybackSettings.ResumeAfterWindowResize"/>），
    /// 拖边收尾那一拍现读。同 <see cref="AutoFullscreenOnPlayback"/> 的规矩：设置窗口是另一扇窗，
    /// 那里改过的开关必须在<b>下一次拖边</b>就生效，而拖边收尾读的是这里、不是页面自己缓存下来的一份。
    /// </summary>
    internal bool ResumeAfterWindowResize => Settings.Playback.ResumeAfterWindowResize;

    /// <summary>
    /// 播放中、后端已明确表态「画面在宿主窗口外」的那一刻才为真 —— 全屏键交给 mpv（Esc 先退
    /// mpv 的全屏）的唯一判据。未开播或后端不表态时它是 false：还没有画面，谈不上「画面在外面」。
    /// </summary>
    internal bool NativeWindowPlayback => _playback.PictureInHostWindow == false;

    /// <summary>
    /// 画面画在哪 —— 两条管线的唯一事实源。真＝画面合成进本窗口的视觉树（集成管线）；假＝画面在
    /// mpv 自建的顶层窗口里（独立管线，或外部 mpv.exe 后端）。
    /// <para>
    /// 活着的会话说了算（<see cref="IPlaybackBackend.PictureInHostWindow"/>，后端各自表态）；还没有
    /// 会话时按设置推算本次的归属：内置后端才吃管线档位（集成→本窗口、独立→mpv 窗口），外部后端
    /// 的画面永远在它自己的窗口里，与档位无关 —— <see cref="Playback.MpvProcessBackend.PictureInHostWindow"/>
    /// 也是这么表态的，两头说的是同一句话。曾经还有一个手工缓存在开播前写、退出时清，是这条公式
    /// 的手抄副本：两处写法已经在「外部后端该答什么」上分了歧，2026-09-17 删掉，只留这一条。
    /// </para>
    /// </summary>
    internal bool PictureInHostWindow => _preparingPictureInHost ?? _playback.PictureInHostWindow
        ?? (Settings.Mpv.Backend == MpvBackendKind.BuiltInLibMpv
            && Settings.Mpv.Pipeline != VideoPipelineKind.Standalone);

    /// <summary>
    /// Whether a playback runs with <b>no page of ours attached at all</b> — exactly <b>独占模式 on the
    /// built-in backend</b>: picture and on-screen controls (装箱的 uosc，<see cref="Mpv.MpvUi"/>) both live
    /// in mpv's own top-level window, and the main window stays on whatever page the user is browsing.
    /// <para>
    /// 「无页面」是这条公式要回答的全部：真的时候 <c>ShellPage</c> 把自己的播放页摘下去
    /// （<see cref="Views.PlayerPage.Detach"/>），<see cref="PlayerShown"/> 与 <see cref="PlayerHidden"/>
    /// 对页面无话可说，浏览页纹丝不动；开播自动全屏由外壳直达 mpv（<c>ShellPage.OnHeadlessPlaybackStarted</c>），
    /// 收场回挂由外壳对账（<c>ShellPage.OnHeadlessPlaybackHidden</c>）。关 mpv 的视频窗就是停止
    /// （quit → shutdown → UserQuit）。挂着片子时再点一部，走 <see cref="PlayReplacingAsync"/> 换片；
    /// 换集经 <c>momoka-episode</c> 消息回到本视图模型的 Emby 导航，播放进度与观看上报仍归
    /// <c>PlaybackService</c>。
    /// </para>
    /// <para>
    /// 沿革：曾是「用独立窗口播放」开关（2026-09-13），09-17 并进独占管线，09-19 用户令删掉开关、
    /// 随后删掉独立控制窗（选集列表、跳过按钮、统计随窗退场，独占模式以 uosc 为唯一控制面）——
    /// 行为只剩这一条推导。集成模式画面在本窗口，永远 false；外部 mpv.exe 也 false —— 主窗口的
    /// 播放页照旧挂着出说明牌和控制条。
    /// </para>
    /// </summary>
    internal bool HeadlessPlayback => !PictureInHostWindow && Embedded;

    /// <summary>
    /// 播放页置顶的持久化偏好（<see cref="Configuration.PlaybackSettings.PinWindowTopmost"/>），页面进场时
    /// 按它把窗口立回上一回的那一档。读写分两口而不是一个属性，因为「读」在播放开始、而「写」只属于用户
    /// 拨开关那一下 —— 探针、退出播放与**播放/暂停的自动跟随**（2026-09-29 用户令「播放时自动置顶，暂停时
    /// 自动取消置顶」）也会动窗口的置顶，那些都不许碰这份记账；播放一开始，第一条状态边沿就会按「在播」
    /// 把它盖过去（见 PlayerPage.OnStatusApplied），它撑的只是进场到开播之间那一小段。
    /// </summary>
    internal bool SavedPinTopmost => Settings.Playback.PinWindowTopmost;

    /// <summary>用户拨了置顶开关，记下来。同值不落盘，拨得再勤也只是内存里的一次比较；自动跟随不记账 ——
    /// 那是播放的状态，不是用户的偏好。</summary>
    internal void SavePinTopmost(bool pinned)
    {
        if (Settings.Playback.PinWindowTopmost == pinned) return;

        Settings.Playback.PinWindowTopmost = pinned;
        _settings.Save();
    }

    /// <summary>
    /// 自检那关「置顶开关」用的钩子：<paramref name="value"/> 给了就把管线档/后端按到集成（只动内存，
    /// 不落盘），传 null 只读不写；两讫都返回改前的值，探针的退出门拿它放回去。
    /// <para>
    /// 为什么要有它：<see cref="PictureInHostWindow"/> 在还没有会话时按设置推算，而这台机器的管线档是
    /// 会被翻到独占的（「窗口命令按钮」那一条红过的环境病）—— 播放/暂停自动跟随那一关若照设置读，
    /// 翻到独占的机器上就会假红。探针要的是那条代码路，不是这台机器此刻的口味，所以把两枚设置直接
    /// 摆到集成再喂状态。
    /// </para>
    /// </summary>
    internal VideoPipelineKind ProbeForcePipeline(VideoPipelineKind? value)
    {
        var was = Settings.Mpv.Pipeline;
        if (value is { } forced) Settings.Mpv.Pipeline = forced;
        return was;
    }

    internal MpvBackendKind ProbeForceBackend(MpvBackendKind? value)
    {
        var was = Settings.Mpv.Backend;
        if (value is { } forced) Settings.Mpv.Backend = forced;
        return was;
    }

    /// <summary>
    /// Whether a file is loaded and being driven right now. The shell asks it from the player page's
    /// 「关闭」 (ClosePlayerToHome)：先停后走，而不是揣着一场在播的片子离开播放页。
    /// </summary>
    internal bool PlayingNow => _playback.IsPlaying;

    /// <summary>Whether the user has touched the seek bar recently enough for it to own its value.</summary>
    internal bool Scrubbing => TimelineBusy || Now - _seekTouched < ScrubGraceMilliseconds;

    internal bool Paused => Status.Paused;

    /// <summary>Whether a hover over the seek track has anything to preview.</summary>
    internal bool CanPeek => Status.HasDuration;

}
