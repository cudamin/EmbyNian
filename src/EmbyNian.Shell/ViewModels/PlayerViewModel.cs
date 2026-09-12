using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Mpv;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Shell.Media;
using EmbyNian.Shell.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.ViewModels;

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

    /// <summary>
    /// How long the volume has to sit still before it is written to the settings file. The wheel raises a
    /// change per notch and the arrow keys one per press, and every save rewrites settings.json and its
    /// backup — so it is written once the hand comes off rather than on the way.
    /// </summary>
    private const long VolumeSettleMilliseconds = 1200;

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

    /// <summary>倍速's own range. The menu offers exactly these, and the keys clamp to their ends.</summary>
    internal static readonly double[] SpeedChoices = [0.5, 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 2.0];

    /// <summary>The delay nudges both A/V delay submenus offer, in seconds.</summary>
    internal static readonly double[] DelayNudges = [-1, -0.1, 0.1, 1];

    private readonly PlaybackService _playback;
    private readonly ISettingsService _settings;
    private readonly EmbySession _session;
    private readonly EmbyImageStore _images;
    private readonly ShaderGroupResolver _shaders;

    /// <summary>The machine's installed families, for the title strip's 字幕字体 box. Shared with the settings page.</summary>
    private readonly FontLibrary _fonts;

    /// <summary>
    /// The UI thread. Every one of the player service's events arrives on whichever thread mpv's event loop
    /// happens to be on, and all of them end in a bound property.
    /// </summary>
    private readonly IUiDispatcher _ui;

    private readonly SkipCoordinator _skips = new();

    /// <summary>
    /// Cancels anything in flight on the way out. Created here rather than per attach and deliberately
    /// never disposed: a poll waiting on a five-hundred-millisecond delay comes back and reads
    /// <c>Token</c>, and a disposed source would answer that with an exception instead of a cancellation.
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();

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

    private long _seekTouched;
    private double? _seekPending;

    /// <summary>
    /// 上一条发给 mpv 的跳转落在哪个比例位置，null＝没有在途的跳转。mpv 在跳转落定之前会一直报
    /// 「正在离开的那个位置」，把这个回声写回进度条就是拇指倒退那一下 —— 见 <c>SeekBarFollows</c>。
    /// </summary>
    private double? _seekSent;

    /// <summary>那条跳转是什么时候发出去的。超时（<see cref="SeekGiveUpMilliseconds"/>）之后不再等它。</summary>
    private long _seekSentAt;

    /// <summary>The volume the settings file has not been told about yet, and when it last moved.</summary>
    private int? _volumePending;
    private long _volumeTouched;

    /// <summary>When the 统计 panel last read mpv, and whether a read is still outstanding.</summary>
    private long _statsRead;
    private bool _statsBusy;

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

    /// <summary>Whether the 字幕字体 box has been handed the machine's families, or asked for them.</summary>
    private bool _fontsAsked;

    public PlayerViewModel(
        PlaybackService playback,
        ISettingsService settings,
        EmbySession session,
        EmbyImageStore images,
        ShaderGroupResolver shaders,
        FontLibrary fonts,
        IUiDispatcher ui)
    {
        _playback = playback;
        _settings = settings;
        _session = session;
        _images = images;
        _shaders = shaders;
        _fonts = fonts;
        _ui = ui;

        // 需求 7: the same row type the 设置 → 字幕 card uses, over the same setting, so there is one search
        // and one filter in the app rather than a second one written for the OSD. What differs is what
        // picking does — here it also has to reach the film that is playing, which is the point of putting
        // the box on the player at all.
        SubtitleFont = new SettingFontRow(
            "字幕字体",
            "输入任意一段名字搜索，回车或点一下就换",
            Settings.Playback.SubtitleFontFamily,
            ApplySubtitleFont,
            _settings.Save);

        // Every number the bar shows starts from the same empty snapshot the stop path returns it to, so
        // the clock reads 0:00 and the glyph reads 播放 before anything has ever played.
        ApplyStatus(new PlayerStatus());
    }

    /// <summary>
    /// 需求 7 的字幕字体选择栏, bound by the title strip. Built with the settings file's family and filled
    /// with the machine's own once <see cref="PrepareFonts"/> has been called.
    /// </summary>
    internal SettingFontRow SubtitleFont { get; }

    /// <summary>
    /// Whether <see cref="SubtitleFont"/> holds the machine's families rather than just the stored one.
    /// Read by the self-check, which cannot report on a list that has not landed yet.
    /// </summary>
    internal bool FontsReady { get; private set; }

    /// <summary>
    /// What the settings file says the subtitle family is. For the self-check, which has no other way to
    /// tell a picker reading the right setting from one reading nothing at all.
    /// </summary>
    internal string SubtitleFontSetting => Settings.Playback.SubtitleFontFamily;

    /// <summary>
    /// Hands the 字幕字体 box the installed families, once. Called as playback starts and again when the box
    /// takes the keyboard, so a machine whose font scan is slow still gets a full list by the time anyone
    /// can read it — and so nothing scans fonts at startup for a player that may never be opened.
    /// </summary>
    internal void PrepareFonts()
    {
        if (_fontsAsked) return;

        _fontsAsked = true;

        // The scan is shared with the settings page, so a user who has been there already pays nothing and
        // — this is the half the self-check depends on — the list is in hand synchronously.
        if (_fonts.Ready is { Families.Count: > 0 } ready)
        {
            SubtitleFont.Fill(ready);
            FontsReady = true;
            return;
        }

        _ = FillFontsAsync();
    }

    private async Task FillFontsAsync()
    {
        var catalogue = await _fonts.LoadAsync().ConfigureAwait(true);

        SubtitleFont.Fill(catalogue);
        FontsReady = true;
    }

    /// <summary>
    /// What picking a family in the title strip's box does: the settings file, then the film that is
    /// playing. mpv re-renders text subtitles from the next frame, so this is visible while it is watched
    /// rather than at the next play — which is the whole reason the box is on the player.
    /// </summary>
    private void ApplySubtitleFont(string family)
    {
        Settings.Playback.SubtitleFontFamily = family;

        _ = _playback.SetPropertyAsync("sub-font", family);
        _ = _playback.CommandAsync("show-text", $"字幕字体：{family}", "1200");

        Log.Info(Category, $"字幕字体改为「{family}」");
    }

    /// <summary>
    /// A search in the 字幕字体 box that matched no family. On the OSD rather than in the shell's InfoBar:
    /// the box putting the old family back is otherwise indistinguishable from a pick that silently failed,
    /// and a notification card over a film for a typo is more than the mistake is worth.
    /// </summary>
    internal void NoticeNoFont(string typed) =>
        _ = _playback.CommandAsync("show-text", $"没有找到字体「{typed}」", "1500");

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
        _playback.StatusChanged += OnStatusChanged;
        _playback.TracksChanged += OnTracksChanged;
        _playback.NowPlayingChanged += OnNowPlayingChanged;
    }

    /// <summary>
    /// Cancels anything in flight on the way out. The process is about to end either way; this is so a
    /// poll waiting on a five-hundred-millisecond delay does not come back to a disposed session.
    /// </summary>
    internal void Shutdown()
    {
        _playback.ProgressChanged -= OnProgressChanged;
        _playback.StatusChanged -= OnStatusChanged;
        _playback.TracksChanged -= OnTracksChanged;
        _playback.NowPlayingChanged -= OnNowPlayingChanged;

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
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

    /// <summary>A fresh set of 统计 rows to draw.</summary>
    internal event Action<IReadOnlyList<PlaybackStatRow>>? StatsUpdated;

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

    /// <summary>播放/暂停's own glyph, so the button never has to be told which state it is in.</summary>
    [ObservableProperty]
    public partial string? PlayPauseGlyph { get; set; }

    [ObservableProperty]
    public partial string? SpeedLabel { get; set; }

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

    /// <summary>音量, 0–<see cref="VolumeMaximum"/>, two-way for the same reason the seek bar is.</summary>
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

    /// <summary>
    /// How far the rail goes, so the slider in the markup does not state a ceiling of its own. It used to say
    /// <c>Maximum="100"</c>, one of six independent places that pinned the volume at 100 — and a rail whose top
    /// disagrees with what gets stored is a rail that lies about how loud the film is going to be.
    /// </summary>
    public double VolumeMaximum => AudioSettings.MaxVolume;

    /// <summary>The rail's speaker glyph, or the crossed-out one while muted.</summary>
    [ObservableProperty]
    public partial string? SoundGlyph { get; set; }

    /// <summary>Whether this playback has siblings, which is what 上一集/下一集/选集 need to exist for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EpisodeControlsVisibility))]
    public partial bool EpisodeControlsVisible { get; set; }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChapterCaptionVisibility))]
    public partial string? ChapterCaption { get; set; }

    /// <summary>The hovered moment, as a clock. Updated on every pointer move along the seek track.</summary>
    [ObservableProperty]
    public partial string? ChapterClock { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChapterStillVisibility))]
    public partial BitmapImage? ChapterStill { get; set; }

    /// <summary>
    /// Whether the 统计 panel is up. Two-way from its own toggle button, and deliberately outside the reveal
    /// rule the rest of the chrome lives under — the panel is up because someone asked for it, and a numbers
    /// readout that vanished when the pointer stopped moving would be unreadable, which is the whole reason
    /// mpv's own <c>stats.lua</c> is a toggle too.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsVisibility))]
    public partial bool StatsOpen { get; set; }

    public Visibility SubtitleVisibility => Show(Subtitle is { Length: > 0 });

    public Visibility EpisodeControlsVisibility => Show(EpisodeControlsVisible);

    public Visibility SkipVisibility => Show(SkipOffered);

    public Visibility CoverVisibility => Show(CoverUp);

    public Visibility StatsVisibility => Show(StatsOpen);

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

    /// <summary>
    /// Opening reads immediately rather than waiting up to a second for the next refresh: an empty panel
    /// that filled in a beat later would look like it had failed to open. Closing hands the page an empty
    /// row set, which is what clears the grid.
    /// </summary>
    partial void OnStatsOpenChanged(bool value)
    {
        if (!value)
        {
            StatsUpdated?.Invoke([]);
            return;
        }

        _statsRead = 0;
        _ = RefreshStatsAsync();
    }

    // ---- what the page's menus and probes read -----------------------------------

    /// <summary>The last snapshot mpv reported. Not itself bindable — everything drawn from it is.</summary>
    internal PlayerStatus Status { get; private set; } = new();

    /// <summary>mpv's track list, which the 音轨 and 字幕 pickers are built from.</summary>
    internal IReadOnlyList<MpvTrack> Tracks { get; private set; } = [];

    /// <summary>The siblings this playback started with, and the only list 选集 and 下一集 use.</summary>
    internal IReadOnlyList<EmbyItem> Episodes { get; private set; } = [];

    internal string PlayingItemId { get; private set; } = "";

    /// <summary>Where the chapter boundaries are, for the ticks the page draws under the slider.</summary>
    internal IReadOnlyList<SkipChapter> ChapterMarks { get; private set; } = [];

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
            var (vintage, fastMotion) = Settings.Shaders.Kind(video?.Height ?? 0, video?.FrameRate ?? 0);
            return ShaderGroupCatalog.For(Settings.Shaders.Gpu, vintage, fastMotion);
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

    /// <summary>Whether mpv is drawing into our own window rather than one of its own.</summary>
    internal bool Embedded => Settings.Mpv.Backend == MpvBackendKind.BuiltInLibMpv;

    /// <summary>Whether the user has touched the seek bar recently enough for it to own its value.</summary>
    internal bool Scrubbing => Now - _seekTouched < ScrubGraceMilliseconds;

    internal bool Paused => Status.Paused;

    /// <summary>Whether a hover over the seek track has anything to preview.</summary>
    internal bool CanPeek => _nowPlaying is not null && Status.HasDuration;

}
