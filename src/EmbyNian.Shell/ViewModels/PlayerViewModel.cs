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
    /// How long the volume has to sit still before it is written to the settings file. The wheel raises a
    /// change per notch and the arrow keys one per press, and every save rewrites settings.json and its
    /// backup — so it is written once the hand comes off rather than on the way.
    /// </summary>
    private const long VolumeSettleMilliseconds = 1200;

    private const int PlayGlyphCode = 0xE768;
    private const int PauseGlyphCode = 0xE769;
    private const int VolumeGlyphCode = 0xE767;
    private const int MutedGlyphCode = 0xE74F;

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
    private readonly ShaderStaging _shaderFiles;

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

    /// <summary>The volume the settings file has not been told about yet, and when it last moved.</summary>
    private int? _volumePending;
    private long _volumeTouched;

    /// <summary>When the 统计 panel last read mpv, and whether a read is still outstanding.</summary>
    private long _statsRead;
    private bool _statsBusy;

    /// <summary>The last aspect handed to the window, so an unchanged one is not written again.</summary>
    private double _aspect;

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
        ShaderStaging shaderFiles,
        FontLibrary fonts,
        IUiDispatcher ui)
    {
        _playback = playback;
        _settings = settings;
        _session = session;
        _images = images;
        _shaders = shaders;
        _shaderFiles = shaderFiles;
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

    /// <summary>音量, 0–100, two-way for the same reason the seek bar is.</summary>
    [ObservableProperty]
    public partial double Volume { get; set; }

    [ObservableProperty]
    public partial string? VolumeLabel { get; set; }

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

    /// <summary>The 着色器 group in force, which the ⚙ menu opens on. Null is 「未启用」.</summary>
    internal ShaderGroup? ActiveShader { get; private set; }

    internal IReadOnlyList<ShaderGroup> ShaderCatalog => _shaderFiles.Catalog;

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

    /// <summary>Whether mpv is drawing into our own window rather than one of its own.</summary>
    internal bool Embedded => Settings.Mpv.Backend == MpvBackendKind.BuiltInLibMpv;

    /// <summary>Whether the user has touched the seek bar recently enough for it to own its value.</summary>
    internal bool Scrubbing => Now - _seekTouched < ScrubGraceMilliseconds;

    internal bool Paused => Status.Paused;

    /// <summary>Whether a hover over the seek track has anything to preview.</summary>
    internal bool CanPeek => _nowPlaying is not null && Status.HasDuration;

    // ---- the transport, as commands ----------------------------------------------
    //
    // Synchronous on purpose, every one of them: an AsyncRelayCommand disallows a second run while the
    // first is outstanding, and 「a run」 here lasts as long as the film does — so the button bound to it
    // would grey out for the whole playback. They start the work and return. Internal rather than private
    // so the keyboard can call the same method the button binds to.

    /// <summary>停止播放.</summary>
    [RelayCommand]
    internal void Stop() => _ = StopAsync();

    /// <summary>播放/暂停.</summary>
    [RelayCommand]
    internal void TogglePause() => SetPaused(!Status.Paused);

    [RelayCommand]
    internal void PreviousEpisode() => _ = StepEpisodeAsync(-1);

    [RelayCommand]
    internal void NextEpisode() => _ = StepEpisodeAsync(1);

    /// <summary>Takes the standing 跳过 offer.</summary>
    [RelayCommand]
    internal void TakeSkip() => AcceptSkip(null);

    // ---- starting and stopping ---------------------------------------------------

    /// <summary>
    /// 播放. The one entry point: a poster, a row, a context menu and the 选集 picker all come through
    /// here, so they cannot disagree about what pressing play means.
    /// </summary>
    internal async Task PlayAsync(
        EmbyItem item,
        EmbyItem? parent = null,
        PlaybackChoice? choice = null,
        IReadOnlyList<EmbyItem>? episodes = null)
    {
        try
        {
            await StartPlaybackAsync(item, parent, choice, episodes, replaceExisting: false).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Error(Category, "播放失败", error);
            Noticed?.Invoke($"播放失败：{error.Message}", InfoBarSeverity.Error);
            if (_playerHold == 0) LeavePlayer();
        }
    }

    /// <summary>停止播放. Asks mpv to quit rather than cancelling, so the final position is still reported.</summary>
    internal async Task StopAsync()
    {
        // Before the wait: 「stop」 is one of the two ways out of the player, and the level the film was left
        // at has to reach the file whether or not another tick ever comes.
        FlushVolume(settled: false);

        if (!_playback.IsPlaying) return;
        await _playback.StopAsync().ConfigureAwait(true);
    }

    private async Task StartPlaybackAsync(
        EmbyItem item,
        EmbyItem? parent,
        PlaybackChoice? choice,
        IReadOnlyList<EmbyItem>? episodes,
        bool replaceExisting)
    {
        if (!replaceExisting && _playback.IsPlaying)
        {
            Noticed?.Invoke("已经有内容正在播放，请先停止", InfoBarSeverity.Warning);
            return;
        }

        if (_playback.Validate() is { } problem)
        {
            Noticed?.Invoke(problem, InfoBarSeverity.Error);
            return;
        }

        // Taken here, before the fetch and before the call that stops whatever is playing; released in
        // the finally once this playback has ended and any auto-advance has been decided.
        _playerHold++;
        try
        {
            // 需求 7's box, made useful before the strip it sits in can be revealed: the scan is a few
            // hundred file opens on a cold cache, and the settings window may have changed the family
            // since this row was built.
            PrepareFonts();
            SubtitleFont.Reseed(Settings.Playback.SubtitleFontFamily);

            // The player takes over the window immediately rather than after the metadata round trip.
            // There is nothing to look at on the page behind it — the user has already committed — and
            // the cover is a better place to say what is being waited for than a toast over a grid.
            EnterPlayer();
            ShowCover(replaceExisting ? "正在切换…" : "正在获取媒体信息…");

            // A card fetched for browsing carries no MediaSources, and those are what hold the tracks,
            // the container and the runtime.
            var detail = item.MediaSources.Count > 0
                ? item
                : await _session
                    .ExecuteAsync((client, token) => client.GetItemAsync(item.Id, token), _lifetime.Token)
                    .ConfigureAwait(true);

            if (detail.Type is EmbyItemType.Series or EmbyItemType.Season)
            {
                // A series or a season is not a playable item, but the play badge on its poster means
                // 「开始看这部剧」 — the next unwatched episode. Resolved here rather than by the caller so
                // the home shelves, the library, the search results and the context menu all agree on
                // what that click does, and so 「选集」 gets the sibling list for free.
                ShowCover("正在查找可播放的单集…");

                if (await ResolveNextEpisodeAsync(detail).ConfigureAwait(true) is not { } resolved)
                {
                    Noticed?.Invoke("这部剧集下没有可播放的单集", InfoBarSeverity.Warning);
                    return;
                }

                // The choice is dropped deliberately: it describes a media source of the item the user
                // was looking at, and that item is not the file about to be played. A series card is
                // the parent for the shader rule; a season card leaves it to ResolveSeriesAsync.
                await StartPlaybackAsync(
                    resolved.Episode,
                    detail.Type == EmbyItemType.Series ? detail : parent,
                    choice: null,
                    resolved.Siblings,
                    replaceExisting).ConfigureAwait(true);
                return;
            }

            if (!detail.IsPlayable)
            {
                Noticed?.Invoke("这个项目不能直接播放", InfoBarSeverity.Warning);
                return;
            }

            if (detail.MediaSources.Count == 0)
            {
                Noticed?.Invoke("服务器没有返回可用的媒体源", InfoBarSeverity.Error);
                return;
            }

            var source = choice?.Source ?? detail.MediaSources[0];
            var resumeTicks = Settings.Playback.ResumeFromSavedPosition ? detail.ResumeTicks : 0;

            Episodes = episodes ?? [];
            _parent = parent;
            PlayingItemId = detail.Id;

            // 「从继续观看点击播放后，无法切换上下集」: a shelf row is a flat set of resume points across every
            // show, so the page that started this playback had no sibling list to hand over. Asked for here
            // rather than by each caller — the search results and the mixed 最近添加 grids have the same
            // nothing to offer — and not awaited, because the file does not wait on it.
            if (Episodes.Count == 0 && detail.Type == EmbyItemType.Episode) _ = FillSiblingsAsync(detail);

            var ticket = new PlaybackTicket
            {
                Item = detail,
                Source = source,
                AudioStreamIndex = choice?.AudioStreamIndex,
                SubtitleStreamIndex = choice?.SubtitleStreamIndex,
                SubtitlesDisabled = choice?.SubtitlesDisabled ?? false,
                StartTicks = choice?.StartTicks ?? resumeTicks,
                Parent = parent ?? await ResolveSeriesAsync(detail).ConfigureAwait(true)
            };

            // What the planner picked is the group the 着色器 submenu opens on, so it shows the startup
            // decision rather than looking as though nothing had been applied.
            ActiveShader = ShaderGroupCatalog.Find(_shaders.Resolve(detail, source, ticket.Parent).Group);
            SubtitleDelay = 0;
            AudioDelay = 0;

            var result = await _playback.PlayAsync(ticket, _lifetime.Token).ConfigureAwait(true);

            // 「播放已停止」 belongs to the end of a viewing, not to the seam between two episodes: the
            // switch already says what it is doing, and two toasts stacked over a half-built player were
            // part of what 「画面错乱」 looked like.
            var following = await NextEpisodeToAutoPlayAsync(result, detail).ConfigureAwait(true);
            if (following is null)
            {
                Noticed?.Invoke(
                    result.ToChinese(),
                    result.Exit.IsFailure ? InfoBarSeverity.Error : InfoBarSeverity.Success);
            }

            // The item's watched flag and resume position have just changed on the server.
            RefreshRequested?.Invoke();

            if (following is not null)
            {
                Noticed?.Invoke($"自动播放下一集：{following.Episode.ToPlaybackTitle()}", InfoBarSeverity.Informational);
                await StartPlaybackAsync(
                        following.Episode,
                        _parent,
                        choice: null,
                        following.Siblings,
                        replaceExisting: true)
                    .ConfigureAwait(true);
            }
        }
        finally
        {
            _playerHold--;

            // The hold is what kept the player up; if this was the last one and nothing took over, here
            // is where it finally comes down. Skipped while a nested playback runs, because that one
            // holds its own.
            if (_playerHold == 0 && !_playback.IsPlaying) LeavePlayer();
        }
    }

    /// <summary>
    /// 自动播放下一集: the episode after the one that just finished, or null when nothing should follow it.
    /// The current season is enough for the usual case; only its last episode needs the series-wide list.
    /// </summary>
    private async Task<EpisodeDestination?> NextEpisodeToAutoPlayAsync(PlaybackResult result, EmbyItem played)
    {
        if (!Settings.Playback.AutoPlayNextEpisode) return null;

        // Only a file that ran out. Quitting mpv, stopping from the UI and a failed launch all mean the
        // user is done with the episode rather than waiting for the next one.
        if (result.Exit.Reason != PlaybackEndReason.EndOfFile) return null;
        if (_lifetime.IsCancellationRequested) return null;

        if (EpisodeNavigation.Step(Episodes, played.Id, 1) is { } local) return local;

        try
        {
            return await ResolveAdjacentEpisodeAsync(played, 1).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception error)
        {
            Log.Warn(Category, "自动查找跨季下一集失败", error);
            return null;
        }
    }

    /// <summary>
    /// What 「播放」 on a series or a season poster means: the first episode nobody has watched, together
    /// with its season's episode list for the 「选集」 picker. Picks the same file the detail page picks,
    /// so the badge and the page do not start different things.
    /// </summary>
    private async Task<(EmbyItem Episode, IReadOnlyList<EmbyItem> Siblings)?> ResolveNextEpisodeAsync(EmbyItem item)
    {
        var seriesId = item.Type == EmbyItemType.Series ? item.Id : item.SeriesId;
        if (string.IsNullOrEmpty(seriesId)) return null;

        // A season card already says which season; a series card has to choose one.
        var seasonId = item.Type == EmbyItemType.Season ? item.Id : null;

        if (seasonId is null)
        {
            var seasons = await _session
                .ExecuteAsync((client, token) => client.GetSeasonsAsync(seriesId, token), _lifetime.Token)
                .ConfigureAwait(true);

            // The same rule the detail page opens on — 「the first season with something left in it, 特辑
            // aside」 — read from the one place that states it, so a poster and the page it opens cannot
            // start different episodes. A show with no season rows at all keeps a null season, which asks
            // the server for every episode it has.
            if (ItemDetail.PickSeason(seasons) is { } opening) seasonId = opening.Id;
        }

        var episodes = await _session
            .ExecuteAsync((client, token) => client.GetEpisodesAsync(seriesId, seasonId, token), _lifetime.Token)
            .ConfigureAwait(true);

        if (episodes.Count == 0) return null;

        var next = episodes.FirstOrDefault(episode => episode.UserData?.Played != true) ?? episodes[0];
        return (next, episodes);
    }

    /// <summary>
    /// Fetches the series row behind an episode. Emby's episode records usually carry no genres of
    /// their own, so without this the anime shader rule would never fire on a TV show.
    /// </summary>
    private async Task<EmbyItem?> ResolveSeriesAsync(EmbyItem item)
    {
        if (item.Type != EmbyItemType.Episode || string.IsNullOrEmpty(item.SeriesId)) return null;

        try
        {
            return await _session
                .ExecuteAsync((client, token) => client.GetItemAsync(item.SeriesId!, token, EmbyFields.Browse), _lifetime.Token)
                .ConfigureAwait(true);
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"读取所属剧集信息失败：{error.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fetches the season's episode list for a playback that arrived without one, so 选集 and 上一集/下一集
    /// work wherever the play came from rather than only from a page that happened to be holding a list.
    /// <para>
    /// The list is applied only if the same episode is still the one playing: the request and the file open
    /// in parallel, and a list belonging to the previous episode would have 下一集 play something out of
    /// another show. It also has to contain the playing episode, or stepping from it has no anchor.
    /// </para>
    /// </summary>
    private async Task FillSiblingsAsync(EmbyItem episode)
    {
        if (string.IsNullOrEmpty(episode.SeriesId)) return;

        try
        {
            // A null season asks for every episode in the series, which is what an episode record with no
            // season on it deserves: a slightly wider 选集 is better than a disabled one.
            var siblings = await _session
                .ExecuteAsync(
                    (client, token) => client.GetEpisodesAsync(episode.SeriesId!, episode.SeasonId, token),
                    _lifetime.Token)
                .ConfigureAwait(true);

            if (!string.Equals(PlayingItemId, episode.Id, StringComparison.Ordinal)) return;
            if (!siblings.Any(item => string.Equals(item.Id, episode.Id, StringComparison.Ordinal))) return;

            Episodes = siblings;

            // OnNowPlayingChanged has already decided this from an empty list by the time the answer
            // arrives, so the two buttons it governs would stay hidden over a perfectly good list.
            EpisodeControlsVisible = true;

            Log.Debug(Category, $"补齐本季单集列表：{siblings.Count} 集");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"读取本季单集列表失败：{error.Message}");
        }
    }

    /// <summary>切换集数, from the 选集 picker.</summary>
    internal void SwitchEpisode(EmbyItem episode)
    {
        if (string.Equals(episode.Id, PlayingItemId, StringComparison.Ordinal)) return;
        StartEpisode(new EpisodeDestination(episode, Episodes));
    }

    /// <summary>上一集 / 下一集. The local season is walked first; only a boundary costs a server request.</summary>
    private async Task StepEpisodeAsync(int offset)
    {
        if (_episodeLookupBusy || _episodeSwitchTargetId is not null) return;

        if (_nowPlaying is not { Type: EmbyItemType.Episode } current)
        {
            Noticed?.Invoke("没有可切换的单集", InfoBarSeverity.Informational);
            return;
        }

        if (EpisodeNavigation.Step(Episodes, PlayingItemId, offset) is { } local)
        {
            StartEpisode(local);
            return;
        }

        _episodeLookupBusy = true;
        try
        {
            var playingItemId = PlayingItemId;
            var destination = await ResolveAdjacentEpisodeAsync(current, offset).ConfigureAwait(true);

            // The request ran beside playback. A second command may already have put another item on screen.
            if (!string.Equals(PlayingItemId, playingItemId, StringComparison.Ordinal)) return;

            if (destination is null)
            {
                Noticed?.Invoke(offset < 0 ? "已经是第一集" : "已经是最后一集", InfoBarSeverity.Informational);
                return;
            }

            StartEpisode(destination);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, "读取跨季单集失败", error);
            Noticed?.Invoke($"读取相邻季失败：{error.Message}", InfoBarSeverity.Warning);
        }
        finally
        {
            _episodeLookupBusy = false;
        }
    }

    private async Task<EpisodeDestination?> ResolveAdjacentEpisodeAsync(EmbyItem current, int offset)
    {
        if (string.IsNullOrEmpty(current.SeriesId)) return null;

        // A null season asks Emby for the whole series in broadcast order. EpisodeNavigation narrows the
        // destination back to its own season before it is handed to the player and 选集 menu.
        var seriesEpisodes = await _session
            .ExecuteAsync(
                (client, token) => client.GetEpisodesAsync(
                    current.SeriesId!,
                    seasonId: null,
                    cancellationToken: token),
                _lifetime.Token)
            .ConfigureAwait(true);

        return EpisodeNavigation.Step(seriesEpisodes, current.Id, offset);
    }

    private void StartEpisode(EpisodeDestination destination)
    {
        if (string.Equals(destination.Episode.Id, PlayingItemId, StringComparison.Ordinal)) return;
        if (_episodeSwitchTargetId is not null) return;

        _episodeSwitchTargetId = destination.Episode.Id;
        _ = StartEpisodeAsync(destination);
    }

    private async Task StartEpisodeAsync(EpisodeDestination destination)
    {
        try
        {
            await StartPlaybackAsync(
                    destination.Episode,
                    _parent,
                    choice: null,
                    destination.Siblings,
                    replaceExisting: true)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Error(Category, "切换单集失败", error);
            Noticed?.Invoke($"切换单集失败：{error.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            if (string.Equals(_episodeSwitchTargetId, destination.Episode.Id, StringComparison.Ordinal))
                _episodeSwitchTargetId = null;
        }
    }

    // ---- entering and leaving the player -----------------------------------------

    /// <summary>
    /// Gives the window over to the player. The visible half of this is the page's — see
    /// <see cref="PlayerShown"/>; what is here is the one flag that decides whether the 跳过 offer is live.
    /// </summary>
    private void EnterPlayer()
    {
        if (_playerUp) return;

        _playerUp = true;
        PlayerShown?.Invoke();

        Log.Debug(Category, "进入播放界面");
    }

    /// <summary>
    /// Drops everything this playback knew and asks the page to put the window back. Only ever from the
    /// one place that knows nothing is about to start in the stopped playback's place — see
    /// <c>_playerHold</c>. The state is cleared before <see cref="PlayerHidden"/> is raised, because the
    /// page answers that by redrawing the bar's ticks from <see cref="ChapterMarks"/>.
    /// </summary>
    private void LeavePlayer()
    {
        _playerUp = false;
        HideCover();

        // The other way out, and the usual one: the file ran to its end. Ticks stop with the player, so an
        // unsaved level would be lost here rather than a second late.
        FlushVolume(settled: false);

        Tracks = [];
        Episodes = [];
        _parent = null;
        _nowPlaying = null;
        PlayingItemId = "";
        _episodeLookupBusy = false;
        _episodeSwitchTargetId = null;
        _seekPending = null;
        _skips.Begin([], 0);

        // Everything the bar drew about this particular file. The stills especially: they are keyed by
        // chapter index, and the next file's chapter 3 is not this one's.
        ChapterMarks = [];
        _embyMarks = [];
        _chapterStills.Clear();
        ClearChapterPeek();
        StatsOpen = false;
        _aspect = 0;

        ApplyStatus(new PlayerStatus());
        PictureAspectChanged?.Invoke(0);
        PlayerHidden?.Invoke();

        Log.Debug(Category, "离开播放界面");
    }

    /// <summary>
    /// Covers the picture, and says why. Requirement 12: the video surface and mpv's child inside it
    /// both stay alive across an episode change — which is what stops the window collapsing and
    /// rebuilding — but it also means the last frame of the finished episode sits there while mpv tears
    /// one file down and opens the next at whatever size and format it turns out to be. Nothing is wrong
    /// with the decode; it is simply nobody's job to have painted over it. This is that job.
    /// </summary>
    private void ShowCover(string message)
    {
        CoverMessage = message;
        CoverUp = true;
    }

    /// <summary>
    /// Takes the cover away. Called the moment mpv reports the new file loaded, and from every path that
    /// leaves playback, so the cover cannot outlive what it was covering.
    /// </summary>
    private void HideCover() => CoverUp = false;

    // ---- the player's own events -------------------------------------------------

    private void OnProgressChanged(PlaybackProgress progress) => OnUi(() =>
    {
        if (progress.Title.Length > 0) Title = progress.Title;
    });

    private void OnStatusChanged(PlayerStatus status) => OnUi(() => ApplyStatus(status));

    private void OnTracksChanged(IReadOnlyList<MpvTrack> tracks) => OnUi(() => Tracks = tracks);

    private void OnNowPlayingChanged(EmbyItem? item) => OnUi(() =>
    {
        // Re-armed per playback, before anything can look at it: switching episodes comes through here,
        // and the previous episode's opening is not this one's. The server's chapter marks are the
        // starting point; RefineSkipSectionsAsync replaces them with mpv's once the file is open.
        _generation++;
        _nowPlaying = item;
        if (item is not null
            && string.Equals(_episodeSwitchTargetId, item.Id, StringComparison.Ordinal))
            _episodeSwitchTargetId = null;
        Tracks = [];

        // Converted once and kept: the 跳过 plan and the preview's still both read Emby's marks, and the
        // preview reads them on every pointer move.
        _embyMarks = item is null ? [] : SkipSectionPlanner.FromEmby(item.Chapters);
        var runtime = TimeFormat.ToSeconds(item?.RunTimeTicks);
        _skips.Begin(SkipSectionPlanner.Resolve(_embyMarks, runtime), runtime);

        // The server's marks are what the bar draws until mpv publishes its own, for the same reason the
        // 跳过 offer uses them: they are available immediately, and a bar that grew its ticks two seconds
        // in would look like a glitch rather than a refinement.
        ChapterMarks = _embyMarks;
        _chapterStills.Clear();
        ClearChapterPeek();
        ChaptersChanged?.Invoke();

        if (item is null)
        {
            // Only when nothing is coming to take its place. During a handover the picture is about to
            // be replaced, and tearing the player down for the second it takes is the whole of
            // 「切换集数的时候画面错乱」 — so the chrome keeps the episode list it is about to need and the
            // video surface keeps the window it is about to draw into.
            if (_playerHold > 0)
            {
                // The old file's readings are gone even though the surface stays: what the bar shows
                // between two episodes should be the new one loading, not the last frame of the old one.
                ApplyStatus(new PlayerStatus());
                ShowCover("正在切换…");
            }
            else
            {
                LeavePlayer();
            }

            return;
        }

        Title = item.ToPlaybackTitle();
        Subtitle = item.Type == EmbyItemType.Episode ? item.SeriesName ?? "" : item.CardSubtitle;
        SourceLabel = item.MediaSources.Count > 0 ? item.MediaSources[0].ToQualityLabel() : "";
        EpisodeControlsVisible = item.Type == EmbyItemType.Episode
            && (Episodes.Count > 1 || !string.IsNullOrEmpty(item.SeriesId));

        PlaybackStarted?.Invoke();

        _ = PopulateTracksAsync(_generation);
        _ = RefineSkipSectionsAsync(_generation);
        _ = ApplyAspectAsync(_generation);
        ApplySkipOffer();
    });

    /// <summary>
    /// Pushes one status snapshot into the chrome. Everything the bar draws comes from here and from
    /// nowhere else, so the clock and the bar beside it can never have come from different moments.
    /// </summary>
    private void ApplyStatus(PlayerStatus status)
    {
        Status = status;

        PlayPauseGlyph = Glyph(status.Paused ? PlayGlyphCode : PauseGlyphCode);
        PositionClock = status.HasPosition ? status.PositionClock : "0:00";
        DurationClock = status.HasDuration ? status.DurationClock : "0:00";
        CacheFraction = status.CacheFraction;
        SpeedLabel = $"{status.Speed.ToString("0.0#", CultureInfo.InvariantCulture)}×";
        ThinFraction = Math.Clamp(status.Fraction, 0, 1);

        _pushing = true;
        try
        {
            // The user's own drag wins for as long as it is the more current answer: mpv reports the
            // position it is still seeking away from, and letting that write the slider back would drag
            // the thumb out from under the pointer.
            if (!Scrubbing) SeekValue = status.Fraction * SeekScale;

            Volume = Math.Clamp(Math.Round(status.Volume), 0, 100);
        }
        finally
        {
            _pushing = false;
        }

        VolumeLabel = status.Muted
            ? "静音"
            : ((int)Math.Round(status.Volume)).ToString(CultureInfo.InvariantCulture);
        SoundGlyph = Glyph(status.Muted ? MutedGlyphCode : VolumeGlyphCode);

        // The new file is decoding, so there is a real picture to show and the cover has done its job.
        // Anything earlier than Loaded would uncover the seam it was put up for.
        if (status.Loaded) HideCover();

        // The tooltip's run time, the tick layout the duration decides, and 「stay up while paused」.
        StatusApplied?.Invoke(status);

        ApplySkipOffer();
    }

    /// <summary>
    /// The seek bar under the user's own hand. Coalesced rather than sent per change: a drag along the bar
    /// raises this for every pixel, and mpv would spend the drag servicing seeks to positions the pointer
    /// had already left — so <see cref="Tick"/> sends the last one.
    /// </summary>
    partial void OnSeekValueChanged(double value)
    {
        if (_pushing) return;

        _seekTouched = Now;
        _seekPending = Math.Clamp(value / SeekScale, 0, 1);

        // The clock keeps up with the thumb rather than with mpv, so a drag reads as a scrub instead of
        // as a slider that has come loose from the number beside it.
        if (Status.HasDuration)
            PositionClock = TimeFormat.Clock(TimeSpan.FromSeconds(_seekPending.Value * Status.Duration));
    }

    /// <summary>
    /// 音量 under the user's own hand — the rail, the wheel and the arrow keys all land here. Sent straight
    /// through rather than coalesced: a volume change is a single value mpv applies instantly, and the
    /// readout beside the rail has to keep up with the thumb rather than with the next status poll.
    /// </summary>
    partial void OnVolumeChanged(double value)
    {
        if (_pushing) return;

        var level = Math.Clamp(Math.Round(value), 0, 100);
        VolumeLabel = ((int)level).ToString(CultureInfo.InvariantCulture);
        _ = _playback.SetPropertyAsync("volume", level);

        // Kept for the next file as well as sent to this one. Every playback launches a fresh mpv with its
        // own config blocked, so a level nobody wrote down is 100 again by the next episode.
        _volumePending = (int)level;
        _volumeTouched = Now;
    }

    /// <summary>
    /// Writes the volume the player was left at into the settings file, once it has settled — or at once
    /// when <paramref name="settled"/> says not to wait, which is what leaving the player does: there may be
    /// no further tick to settle on.
    /// </summary>
    private void FlushVolume(bool settled = true)
    {
        if (_volumePending is not { } level) return;
        if (settled && Now - _volumeTouched < VolumeSettleMilliseconds) return;

        _volumePending = null;

        if (Settings.Audio.Volume == level) return;

        Settings.Audio.Volume = level;
        _settings.Save();
    }

    /// <summary>
    /// mpv publishes its track list only once the file is actually being decoded, so the pickers are
    /// refilled in a loop until the list stops being empty. A stuck backend runs out of attempts and
    /// leaves the placeholder rows in place.
    /// </summary>
    private async Task PopulateTracksAsync(int generation)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (generation != _generation || !_playback.IsPlaying) return;

            var tracks = await _playback.GetTracksAsync().ConfigureAwait(true);
            if (generation != _generation) return;

            if (tracks.Count > 0)
            {
                Tracks = tracks;
                return;
            }

            await Task.Delay(500).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Swaps Emby's chapter marks for mpv's own, which arrive a second or two after playback starts and
    /// are the ones a release group actually wrote 「OP」 in.
    /// </summary>
    private async Task RefineSkipSectionsAsync(int generation)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(500).ConfigureAwait(true);
            if (generation != _generation || !_playback.IsPlaying) return;

            var count = await _playback.GetNumberAsync("chapter-list/count").ConfigureAwait(true);

            // Null means the property is not there yet; a real answer of 0 or 1 means this file has
            // nothing to read and waiting longer will not change that.
            if (count is null) continue;
            if (count < 2) return;

            var chapters = new List<SkipChapter>((int)count.Value);
            for (var index = 0; index < (int)count.Value; index++)
            {
                if (generation != _generation) return;

                var start = await _playback.GetNumberAsync($"chapter-list/{index}/time").ConfigureAwait(true);
                if (start is null) return;

                var title = await _playback.GetTextAsync($"chapter-list/{index}/title").ConfigureAwait(true);
                chapters.Add(new SkipChapter(start.Value, title));
            }

            if (generation != _generation) return;

            var duration = await _playback.GetNumberAsync("duration").ConfigureAwait(true)
                           ?? _playback.Status.Duration;
            if (generation != _generation) return;

            _skips.Refine(SkipSectionPlanner.Resolve(chapters, duration), duration);

            // The ticks get the same upgrade. Emby's marks and mpv's usually agree, but a file remuxed
            // after the library scan is exactly the case where they do not, and the picture on screen is
            // the one to believe.
            ChapterMarks = chapters;
            ChaptersChanged?.Invoke();

            ApplySkipOffer();
            return;
        }
    }

    // ---- 跳过片头/片尾 -----------------------------------------------------------

    /// <summary>
    /// Puts the 跳过 offer up or takes it away for the position playback has reached, and makes the
    /// automatic jump when that is what the setting asks for.
    /// <para>
    /// Deliberately outside the reveal rule the rest of the chrome lives under: the offer stands for
    /// fifteen seconds, and hiding it because the pointer sat still would take the button away exactly
    /// when it was useful. It goes when it is used, when it lapses, when the section ends, or when
    /// playback stops — never because nobody moved the mouse.
    /// </para>
    /// </summary>
    internal void ApplySkipOffer()
    {
        _skips.Mode = Settings.Playback.SkipSections;

        var live = _playback.IsPlaying && _playback.CanControl && _playerUp;

        if (_skips.Advance(live ? Status.Position : -1, live) is { } jump)
        {
            AcceptSkip(jump);
            return;
        }

        ShowSkipPrompt(_skips.Prompt);
    }

    /// <summary>
    /// The offer's own mapping onto the button's four bindings, split out so the self-check can drive it
    /// with a prompt it made itself. Nothing is playing during a self-check, so
    /// <see cref="ApplySkipOffer"/> would always take the 「no offer」 branch and the bindings would never
    /// be looked at.
    /// </summary>
    internal void ShowSkipPrompt(SkipPrompt prompt)
    {
        SkipOffered = prompt.Visible;
        if (!prompt.Visible) return;

        SkipCaption = prompt.Caption;
        SkipTip = prompt.Tip;
        SkipRemaining = prompt.Remaining;
    }

    /// <summary>
    /// Takes the standing offer: seek to the far side of the section and say what was skipped, in
    /// chapterskip.lua's wording. Shared by the button, by <c>Y</c>, and by the automatic jump — which
    /// arrives already decided, so the offer is not consulted a second time.
    /// </summary>
    private void AcceptSkip(SkipJump? decided)
    {
        if ((decided ?? _skips.Accept()) is not { } jump) return;

        _ = _playback.CommandAsync(
            "seek",
            jump.Target.ToString("0.###", CultureInfo.InvariantCulture),
            "absolute+exact");
        _ = _playback.CommandAsync("show-text", jump.Notice, "2500");

        SkipOffered = false;
    }

    // ---- what the keys and the menus write to mpv ---------------------------------

    /// <summary>播放/暂停, from the transport button, the space bar and a tap on the picture.</summary>
    internal void SetPaused(bool paused) => _ = _playback.SetPropertyAsync("pause", paused);

    /// <summary>
    /// Whether libmpv should show a cursor over its own window, kept in step with the page's own hiding.
    /// <para>
    /// It is here because hiding a cursor is per message queue and one of the windows under a playing film is
    /// not ours: libmpv builds its own child window on its own thread, and a <c>SetCursor</c> made on the UI
    /// thread never reaches the screen while the pointer is over that one. mpv has the same setting for its
    /// own reasons — <c>always</c> is 「never show a cursor」 and <c>no</c> is 「never hide it」 — so the fix is
    /// to tell the owner rather than to shout louder from here. Costs one property write per transition, two
    /// a film, and does nothing at all when the standalone <c>mpv.exe</c> backend is playing in its own window.
    /// </para>
    /// </summary>
    internal void ShowMpvCursor(bool visible) =>
        _ = _playback.SetPropertyAsync("cursor-autohide", visible ? "no" : "always");

    /// <summary>
    /// 快进/快退 by the 跨度 from settings. Both arrow keys go through here, so the number the settings page
    /// shows is the number they move by.
    /// </summary>
    internal void SeekForward() => SeekBy(Settings.Playback.SeekForwardSeconds);

    internal void SeekBackward() => SeekBy(-Settings.Playback.SeekBackwardSeconds);

    private void SeekBy(int seconds) => _ = _playback.CommandAsync(
        "seek",
        seconds.ToString(CultureInfo.InvariantCulture),
        "relative+exact");

    /// <summary>
    /// 章节前后跳. mpv's own <c>add chapter</c> rather than a seek to a mark this class holds, because mpv
    /// knows where it is: it lands on the boundary, it clamps at both ends of the file, and it does
    /// nothing at all on a file with no chapters. The notice is the only part it will not say by itself —
    /// commands issued through libmpv produce no OSD, unlike the same command from a key binding.
    /// </summary>
    internal void StepChapter(int offset)
    {
        if (ChapterMarks.Count < 2)
        {
            _ = _playback.CommandAsync("show-text", "这个文件没有章节", "1500");
            return;
        }

        _ = _playback.CommandAsync("add", "chapter", offset.ToString(CultureInfo.InvariantCulture));

        // ${chapter} is expanded by mpv after the jump, so what the notice reads cannot disagree with
        // where playback actually landed. The 1-based number is what a viewer counts in.
        _ = _playback.CommandAsync(
            "show-text",
            "章节 ${=chapter}/" + ChapterMarks.Count + "  ${chapter-metadata/title}",
            "1500");
    }

    /// <summary>倍速微调, clamped to the range the 倍速 menu offers so the two cannot disagree.</summary>
    internal void NudgeSpeed(double delta) =>
        SetSpeed(Math.Round(Math.Clamp(Status.Speed + delta, SpeedChoices[0], SpeedChoices[^1]), 2));

    /// <summary>
    /// 倍速. The keys say what they did on the OSD; the menu does not, because the row that was just
    /// ticked and the button that now reads 「1.25×」 have already said it.
    /// </summary>
    internal void SetSpeed(double speed, bool notice = true)
    {
        _ = _playback.SetPropertyAsync("speed", speed);

        if (!notice) return;

        _ = _playback.CommandAsync(
            "show-text",
            $"倍速：{speed.ToString("0.0#", CultureInfo.InvariantCulture)}×",
            "1200");
    }

    /// <summary>
    /// 字幕/音频延迟微调 from the keyboard. Goes through the same two fields the ⚙ menu's submenus read, so
    /// the menu opens on the value the keys left rather than on zero.
    /// </summary>
    internal void NudgeDelay(bool subtitle, double delta)
    {
        var value = Math.Round((subtitle ? SubtitleDelay : AudioDelay) + delta, 3);
        SetDelay(subtitle, value);

        _ = _playback.CommandAsync(
            "show-text",
            $"{(subtitle ? "字幕延迟" : "音频延迟")}：{value.ToString("+0.0#;-0.0#;0", CultureInfo.InvariantCulture)} 秒",
            "1200");
    }

    /// <summary>
    /// Applies a delay and remembers it. Held here rather than read back from mpv because the ⚙ menu is
    /// built synchronously as it opens, and a value that had to be awaited would arrive after the rows.
    /// </summary>
    internal void SetDelay(bool subtitle, double value)
    {
        if (subtitle) SubtitleDelay = value;
        else AudioDelay = value;

        _ = _playback.SetPropertyAsync(subtitle ? "sub-delay" : "audio-delay", value);
    }

    /// <summary>
    /// 音量 from the keyboard. Writes the bound property rather than mpv directly, so the slider, the
    /// number beside it and the mpv property all move together — the same path a drag takes.
    /// </summary>
    internal void NudgeVolume(int delta) => Volume = Math.Clamp(Volume + delta, 0, 100);

    /// <summary>静音切换. mpv owns the flag; the glyph follows from the next status it reports.</summary>
    internal void ToggleMute() => _ = _playback.SetPropertyAsync("mute", !Status.Muted);

    /// <summary>
    /// Applies a track choice and marks it locally, so the picker shows the new selection the next time
    /// it opens instead of waiting for mpv's own <c>track-list</c> notification to come round.
    /// </summary>
    internal void SelectTrack(bool audio, int? id)
    {
        _ = _playback.SetPropertyAsync(audio ? "aid" : "sid", id is null ? "no" : id);

        Tracks = [.. Tracks.Select(track =>
            (audio ? track.IsAudio : track.IsSubtitle)
                ? track with { Selected = id is { } chosen && track.Id == chosen }
                : track)];
    }

    // ---- 着色器与画面菜单 ---------------------------------------------------------

    /// <summary>
    /// Switches the 着色器 group, or turns shaders off. The staged files are the resolver's business; all
    /// that is kept here is which group the ⚙ menu should open on.
    /// </summary>
    internal void ApplyShaderGroup(ShaderGroup? group)
    {
        ActiveShader = group;
        _ = _playback.SetShaderGroupAsync(group);
        Noticed?.Invoke(
            group is null ? "已关闭着色器" : $"已切换着色器：{group.Name}",
            InfoBarSeverity.Informational);
    }

    /// <summary>
    /// Runs one 画面 menu row: its commands in order, then its notice. Awaited one at a time rather than
    /// fired off together, because a 「重置」 row is six <c>set</c>s and mpv applies them in the order it
    /// receives them — and because the notice must come last: it contains <c>${property}</c>, which mpv
    /// expands when it draws the text, so a notice that overtook its own command would report the old value.
    /// </summary>
    internal async Task RunMenuNodeAsync(PlayerMenuNode node)
    {
        try
        {
            foreach (var command in node.Commands)
                await _playback.CommandAsync([.. command]).ConfigureAwait(true);

            if (node.Notice.Length > 0)
                await _playback.CommandAsync("show-text", node.Notice, "2000").ConfigureAwait(true);
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"画面菜单「{node.Label}」执行失败：{error.Message}");
        }
    }

    // ---- 播放统计 ----------------------------------------------------------------

    /// <summary>
    /// Reads the panel's properties and hands the formatted rows to the page. One round trip per property,
    /// which is what <see cref="PlaybackService.GetTextAsync"/> offers and is cheap enough at one hertz; the
    /// guard is what matters, because a read that took longer than the refresh interval would otherwise
    /// start another before the first came back and the two would interleave into the same grid.
    /// </summary>
    private async Task RefreshStatsAsync()
    {
        if (_statsBusy) return;

        _statsBusy = true;
        try
        {
            var readings = new Dictionary<string, string?>(PlaybackStats.Fields.Count, StringComparer.Ordinal);

            foreach (var field in PlaybackStats.Fields)
            {
                // Every await is a chance for the panel to have been closed or the file to have changed.
                if (!StatsOpen) return;

                readings[field] = await _playback.GetTextAsync(field).ConfigureAwait(true);
            }

            if (!StatsOpen) return;

            StatsUpdated?.Invoke(PlaybackStats.Format(readings));
            _statsRead = Now;
        }
        finally
        {
            _statsBusy = false;
        }
    }

    // ---- 章节缩略图预览 -----------------------------------------------------------

    /// <summary>
    /// Which chapter a hovered moment falls in, and its still if the server extracted one. The page owns
    /// where the box goes; this owns what is in it.
    /// </summary>
    /// <returns>Whether there is anything to preview at that moment, which there is for any open file.</returns>
    internal bool PeekChapterAt(double seconds)
    {
        if (_nowPlaying is null) return false;

        var moment = Math.Max(0, seconds);

        // Every pixel of movement, unlike the contents below: the time is the one part of the box that is
        // about where the pointer is rather than about which chapter it landed in. Written before anything
        // can return, because the readout is the half that is always available — plenty of servers extract
        // no chapters at all, and the slider's own tooltip only appears while the thumb is being dragged,
        // so hovering such a file used to show nothing whatsoever.
        ChapterClock = TimeFormat.Clock(TimeSpan.FromSeconds(moment));

        // Two lookups against two lists, both correct — see ChapterTimeline. The name comes from whichever
        // marks the bar is currently drawing, which is mpv's once it has published them; the picture is
        // indexed against Emby's own list, because that index is what the image request means.
        var named = ChapterTimeline.IndexAt(ChapterMarks, moment);
        var still = ChapterTimeline.IndexAt(_embyMarks, moment);

        // Contents only when the chapter changes: the box follows the pointer along the bar, and reloading
        // the same still for every pixel of that would be absurd.
        if (named == _peekChapter && still == _peekStill) return true;

        _peekChapter = named;
        _peekStill = still;
        ChapterCaption = ChapterTimeline.Caption(ChapterMarks, named);

        if (still >= 0 && _nowPlaying.Chapters[still] is { HasImage: true } info)
            _ = LoadChapterStillAsync(_generation, _nowPlaying.Id, still, info.ImageTag);
        else
            ChapterStill = null;

        return true;
    }

    internal void ClearChapterPeek()
    {
        _peekChapter = -1;
        _peekStill = -1;
        ChapterStill = null;
        ChapterCaption = null;
        ChapterClock = null;
    }

    /// <summary>
    /// Fetches and decodes one chapter still, once. Cached by index in <c>_chapterStills</c> including the
    /// misses, because a scrub back and forth over a chapter the server has no picture for would otherwise
    /// ask again on every pass. The index is Emby's, so what it is checked against is <c>_peekStill</c>.
    /// </summary>
    private async Task LoadChapterStillAsync(int generation, string itemId, int chapter, string? tag)
    {
        if (_chapterStills.TryGetValue(chapter, out var cached))
        {
            if (_peekStill == chapter) ChapterStill = cached;
            return;
        }

        // Nothing on screen while it arrives rather than the previous chapter's frame, which would be a
        // picture of the wrong moment — worse than no picture at all.
        ChapterStill = null;

        try
        {
            var width = EmbyImageStore.RequestWidth(ChapterPeekWidth);
            var bytes = await _images
                .GetChapterAsync(itemId, chapter, tag, width, _lifetime.Token)
                .ConfigureAwait(true);

            if (generation != _generation) return;

            var bitmap = bytes is null
                ? null
                : await PosterLoader.DecodeAsync(bytes, ChapterPeekWidth).ConfigureAwait(true);
            if (generation != _generation) return;

            _chapterStills[chapter] = bitmap;
            if (_peekStill == chapter) ChapterStill = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"读取章节缩略图失败：{error.Message}");
        }
    }

    // ---- 缩放窗口时按画面比例联动 --------------------------------------------------

    /// <summary>
    /// Finds the picture's own shape and hands it to the page, which is what keeps the window in it —
    /// 窗口化时视频有黑边 was this missing. mpv's <c>dwidth</c>/<c>dheight</c> are the displayed size — after
    /// any aspect override, rotation and panscan — so a rotated file locks to its rotated shape rather than
    /// to the stream's.
    /// <para>
    /// Polled for the same reason the track list is: the properties do not exist until a frame has been
    /// decoded, and there is no notification to wait for.
    /// </para>
    /// </summary>
    private async Task ApplyAspectAsync(int generation)
    {
        if (!Embedded) return;

        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(500).ConfigureAwait(true);
            if (generation != _generation || !_playback.IsPlaying) return;

            var displayWidth = await _playback.GetNumberAsync("dwidth").ConfigureAwait(true);
            var displayHeight = await _playback.GetNumberAsync("dheight").ConfigureAwait(true);
            if (generation != _generation) return;

            var aspect = AspectLock.Ratio(displayWidth ?? 0, displayHeight ?? 0, 0, 0);
            if (aspect <= 0) continue;

            // Only on a change, so switching episodes inside one series does not shuffle the window the
            // viewer has already placed.
            if (Math.Abs(aspect - _aspect) > 0.001)
            {
                _aspect = aspect;
                PictureAspectChanged?.Invoke(aspect);
                Log.Debug(Category, $"画面比例 {aspect.ToString("0.000", CultureInfo.InvariantCulture)}，缩放已联动");
            }

            return;
        }
    }

    // ---- the ten-hertz tick ------------------------------------------------------

    /// <summary>
    /// The share of the page's ticker that is not about drawing: the coalesced seek, the 统计 refresh and
    /// the standing 跳过 offer. Driven by the page's timer rather than one of its own, because all three are
    /// only wanted while the player is up, which is exactly when that timer runs.
    /// </summary>
    internal void Tick()
    {
        // Coalesced rather than sent per event: a drag along the bar raises a change for every pixel, and
        // mpv would spend the drag servicing seeks to positions the pointer had already left.
        if (_seekPending is { } fraction)
        {
            _seekPending = null;
            _ = _playback.SetPropertyAsync("percent-pos", fraction * 100);
        }

        // 每秒刷新一次.
        if (StatsOpen && Now - _statsRead >= PlaybackStats.RefreshMilliseconds) _ = RefreshStatsAsync();

        FlushVolume();

        ApplySkipOffer();
    }

    // ---- 播放信息 ----------------------------------------------------------------

    /// <summary>
    /// The 播放信息 body: what is playing, then what was decided about how to play it — the quality preset,
    /// the shader group and why it was chosen, the backend, and every mpv option the launch actually set,
    /// in the order mpv itself resolved them. The page puts it in a dialog; the text is all from here
    /// because every line of it is a playback fact.
    /// </summary>
    internal string MediaInfoText()
    {
        var lines = new List<string>();

        if (_nowPlaying is { } item)
        {
            lines.Add($"标题：{item.ToPlaybackTitle()}");
            if (item.MediaSources.Count > 0) lines.Add($"媒体源：{item.MediaSources[0].ToQualityLabel()}");
        }

        if (Status.HasDuration) lines.Add($"时长：{Status.DurationClock}");
        if (_playback.LaunchQualityPreset is { Length: > 0 } preset) lines.Add($"画质预设：{preset}");
        lines.Add($"着色器配置组：{ActiveShader?.Name ?? "未启用"}");
        if (_playback.LaunchShaderReason is { Length: > 0 } reason) lines.Add($"着色器判定：{reason}");
        lines.Add($"后端：{(Embedded ? "内置 libmpv" : "外部 mpv.exe")}");

        var options = _playback.LaunchOptions.Count == 0
            ? "（没有额外参数）"
            : string.Join("\n", _playback.LaunchOptions.Select(option => $"  {option.Key} = {option.Value}"));

        return string.Join("\n", lines) + "\n\nmpv 参数：\n" + options;
    }

    // ---- plumbing ---------------------------------------------------------------

    /// <summary>
    /// Marshals onto the UI thread. Every one of the player's events arrives from mpv's own loop, and all
    /// of them end in a bound property or an event the page answers by touching the visual tree.
    /// </summary>
    private void OnUi(Action action) => _ui.Run(action);

    private static string Glyph(int codepoint) => char.ConvertFromUtf32(codepoint);
}
