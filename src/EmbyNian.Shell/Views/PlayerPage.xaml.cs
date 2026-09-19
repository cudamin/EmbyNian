using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// The player's View: the visual tree, and every WinUI behaviour that cannot be expressed as a binding.
/// What is playing, and everything decided about it, is <see cref="PlayerViewModel"/>'s — this file's
/// job is the window, the pointer, the keyboard, and the chrome those three reveal.
/// <para>
/// It replaces five WinForms classes that between them ran to some three and a half thousand lines —
/// the transport bar, its flyout, the volume rail, the skip button and the chrome timer — and it is
/// shorter than any two of them, because the parts worth keeping were lifted out first:
/// <see cref="ChromeReveal"/> owns the reveal rule and <see cref="SkipCoordinator"/> owns the 跳过
/// offer, both in Core, both under test, neither knowing a framework exists.
/// </para>
/// <para>
/// The split across the partials is by kind of work rather than by size: <c>Input</c> is the pointer,
/// the gestures, the keys and the window commands; <c>Chrome</c> is the reveal rule's output, the hit
/// tests and the seek bar's ticks and preview; <c>Menus</c> builds the six flyouts, whose rows are made
/// on open because every one of them shows a current value; <c>Panels</c> draws the 统计 grid and the
/// 播放信息 dialog; <c>SelfCheck</c> is the probes. Kept here: the fields those share, the wiring, and
/// the view model's nine 「only the page can do this」 events.
/// </para>
/// <para>
/// Nothing in any of them talks to Emby, to mpv or to the settings file. Where a gesture means a
/// playback command the page calls one method on the view model and stops there; the seven things the
/// view model cannot do for itself — take the window over, put it back, hide a cursor, place a preview
/// box, redraw a tick layout — arrive as the events below.
/// </para>
/// </summary>
public sealed partial class PlayerPage : UserControl
{
    private const string Category = "播放";

    private const int FullscreenEnterCode = 0xE740;
    private const int FullscreenExitCode = 0xE73F;
    private const int MaximizeGlyphCode = 0xE922;
    private const int RestoreGlyphCode = 0xE923;

    /// <summary>
    /// How wide the volume rail's approach strip along the right edge is, in logical pixels. It is both the
    /// distance at which the rail starts to appear and the distance over which it gets stronger —
    /// 「鼠标指针越接近右边的中心显示越明显」 — so widening it makes the rail both easier to summon and slower
    /// to reach full strength.
    /// </summary>
    private const double RailZoneWidth = 160;

    /// <summary>How wide one chapter tick is drawn, in pixels. Odd, so it can sit centred on its mark.</summary>
    private const double ChapterTickWidth = 3;

    /// <summary>
    /// The gap the two overlays that must clear another overlay leave between themselves and it: the 统计
    /// panel under the title strip, and the 跳过 button over the transport bar. See
    /// <see cref="PlaceOverlays"/> for why that is all this needs to say — the two distances themselves are
    /// measured off what they have to clear rather than written down here.
    /// </summary>
    private const double OverlayGap = 8;

    private readonly ChromeReveal _chrome = new();
    private readonly SeekClockConverter _seekClock;

    /// <summary>
    /// The 暂停/播放 badge's fifth of a second. Held rather than looked up per pulse because it is begun on
    /// every pause and every resume, and a resource lookup on each is a dictionary walk for an object that
    /// cannot change.
    /// </summary>
    private readonly Storyboard _pulse;

    /// <summary>
    /// The two geometries the badge draws with, pause and play. Read once for the same reason
    /// <see cref="_pulse"/> is: the badge is drawn on every pause and every resume, and a resource lookup on
    /// each is a dictionary walk for an object that cannot change.
    /// <para>
    /// Two rather than four since 2026-09-05, when the grey rim behind the white shape went: two layers of one
    /// shape needed two copies of each geometry, because a WinUI <c>Geometry</c> cannot be attached to two
    /// <c>Path</c> elements at once — handing one object to both threw <c>ArgumentException: Value does not
    /// fall within the expected range</c> on the second assignment, the framework's 「this object already has a
    /// parent」. One path, one copy each.
    /// </para>
    /// </summary>
    private readonly Geometry _pauseArt;

    private readonly Geometry _playArt;

    /// <summary>
    /// 点在画面上那一下的账，and the timer that holds it back. See <see cref="PictureTap"/>: a tap is not
    /// issued the moment it arrives, because the first click of a double click raises one too.
    /// </summary>
    private readonly PictureTap _tap = new();

    private readonly DispatcherTimer _tapHold = new();

    /// <summary>
    /// When a double tap last cancelled or undid a tap, or null. For the fraction of a second after that the
    /// badge says nothing at all — the undo's own status edge is still on its way back from mpv.
    /// </summary>
    private long? _pulseMutedAt;

    /// <summary>
    /// Drives three things that expire rather than happen: the reveal rule's idle window, the 跳过
    /// offer's countdown, and the seek the user is dragging. Ten hertz, and only while the player is on
    /// screen — the WinForms shell ran a 40 ms timer for the whole session because mpv's child window
    /// swallowed the mouse messages it needed.
    /// </summary>
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private ShellPage? _shell;
    private HostWindow? _window;

    /// <summary>
    /// 集成管线的视频 Visual，一页一个；第二播放窗口使用自己的 XAML 合成树。
    /// </summary>
    private readonly CompositionVideoTarget _videoTarget;

    /// <summary>
    /// The video contract the factory reads for this play — always this page's panel target. It is
    /// an <b>集成模式</b> member only (2026-09-16 第二形态): the 独立播放 pipeline is mpv's own
    /// top-level window and asks for no surface at all — the backend branches on the pipeline
    /// setting and never calls the factory's delegate when it is in force. This property keeps its
    /// shape because the factory delegate still needs something to answer, and the answer is the
    /// same regardless of the setting.
    /// </summary>
    internal IVideoSurface? VideoSurface => _videoTarget;

    private bool _cursorHidden;

    /// <summary>
    /// What the OS said its cursor display counter was after the last <c>ShowCursor</c> this page made:
    /// below zero for hidden, back at zero for shown. Held because it is the only evidence that the call
    /// left the process — see <see cref="ProbeCursor"/>.
    /// </summary>
    private int _cursorCount;

    /// <summary>
    /// How many times the hide has restated its policy, over the life of the page. It used to count injected
    /// one-pixel round trips — real input, and therefore bounded to three per hide so it could not become ten a
    /// second. Since 2026-09-14 it counts the restatements that replaced them. See <see cref="Nudge"/>.
    /// </summary>
    private int _cursorNudges;

    /// <summary>
    /// How many restatements have gone out since this hide began. Reported because 「静止期间那一遍一遍的重申
    /// 活着吗」 is a fact only a count can state; unbounded, because three asks are spent inside the first three
    /// hundred milliseconds and a hide lasts minutes.
    /// </summary>
    private int _nudgesThisHide;

    /// <summary>
    /// Why the cursor came back. Printed on the show line, because 「鼠标隐藏了一会又会自动跑出来」 had to be
    /// diagnosed from a log that only said the cursor was back. Every path that shows it is expected to have
    /// written a name here first, and the default string is a bug report in itself: 见到此串即有路漏标.
    /// </summary>
    private string _woke = "未标注的显示路径（见到此串即有路漏标）";

    /// <summary>
    /// Where the pointer was the last time it was taken to have moved, and what was under it there.
    /// <para>
    /// The reveal rule's 「静止」 is a claim about the pointer, so it has to be measured from a real position
    /// rather than from the arrival of an event. This is that position on the XAML side, kept in the same
    /// logical coordinates the pointer events and the hit tests use so that 「did it move」 and 「what is it
    /// over」 stay one question. It does <em>not</em> drive the hide: the idle clock belongs to the rule and is
    /// stamped only by <see cref="ChromeReveal.Moved"/> and by a report the caller calls a movement.
    /// </para>
    /// </summary>
    private Point _pointerAt = new(double.NaN, double.NaN);

    private ChromePart _pointerOn = ChromePart.None;

    /// <summary>
    /// When the pointer last actually moved. The reveal rule keeps its own idle clock; this one exists to
    /// be printed — in the log line the hide writes, and in the probe — because 「how long had it been
    /// still」 is the one number that tells a cursor which should have gone from a clock that was restamped.
    /// </summary>
    private long _pointerMovedAt;

    /// <summary>
    /// Moves that moved something, moves that moved nothing, and ticks. Counters rather than a flag,
    /// because what a probe and a log line both need to say is 「how many」: one stray move is the chrome
    /// collapsing and is harmless, and forty of them in two seconds is a mouse that never holds still.
    /// </summary>
    private int _pointerMoves;

    private int _stillMoves;

    private int _tickCount;

    /// <summary>
    /// The one sensor the hide is driven by: where the OS says the cursor is, in physical screen pixels.
    /// <para>
    /// <b>This is the whole of 「has the mouse moved」.</b> The XAML <c>PointerMoved</c> channel is not asked
    /// any more, and that is the refactor of 2026-09-15: WinUI raises that event for a pointer that never
    /// moved — once per change to the tree under it — and a real film's log caught the consequence (a cursor
    /// hidden for 29.7 seconds woken by an event claiming 「5,0 逻辑像素」 while this reading had not changed by
    /// a pixel). <c>GetCursorPos</c> reports the sensor's true position; it cannot be told a story about a
    /// coordinate base, so an unchanged reading is stillness and a changed one is a hand, and no third sensor
    /// is needed to arbitrate between them. Same shape as HC-Player's <c>RegisterCursorActivity</c> and mpv's
    /// mouse-event counter: one source, one clock, one answer.
    /// </para>
    /// <para>
    /// <see cref="_polledKnown"/> is whether there has been a reading yet since the player came up. The first
    /// one seeds the reference and is not itself a movement — there is nothing to compare it with.
    /// </para>
    /// </summary>
    private NativePoint _polled;

    private bool _polledKnown;

    /// <summary>How many ticks found the cursor somewhere new. Printed on the show line, because it is the
    /// number the whole hide rests on and a hide that stops working shows up here first.
    /// </summary>
    private int _polledMoves;

    /// <summary>
    /// The one cursor position a tick works from, whether the OS gave it up, and whether a tick is currently
    /// in progress and therefore sharing it. See <see cref="CursorScreen"/> for what the sharing is for: four
    /// separate readings inside one tick can disagree with each other, and the tick then acts on two
    /// different pointers.
    /// <para>
    /// <see cref="_cursorShared"/> is what keeps that scope honest. It is set at the top of
    /// <see cref="OnTick"/> and cleared at the bottom, so a pointer event or a probe arriving in between goes
    /// back to asking the OS: a hundred-millisecond-old position is the wrong answer to 「is the pointer on
    /// the track right now」.
    /// </para>
    /// </summary>
    private NativePoint _cursorAt;

    private bool _cursorAtKnown;

    private bool _cursorShared;

    /// <summary>
    /// How many ticks found a real shape back on the queue while the cursor was supposed to be hidden. Reset
    /// at each hide and printed at the show, so an ordinary film says whether anything is fighting us for the
    /// cursor — the one explanation for 「藏了但屏幕上还有箭头」 that no probe can stage.
    /// </summary>
    private int _shapeBack;

    /// <summary>
    /// 上一次「藏匿取样」的时刻（第十四报，2026-09-15）。藏匿期每秒记一条「外面此刻什么样」，
    /// 用来判用户报的那件事实：屏幕二的 AyuGram 收消息时，屏幕一已隐藏的光标为什么冒出来 ——
    /// 而那批唤醒两轮日志里都没有路认领，所以要看的不是我们的计数，是别人那边（指针压着谁的窗、
    /// 队列形状与系统形状差在哪、窗口与虚拟屏矩形被谁搬过）。
    /// </summary>
    private long _hiddenSampleAt;

    // ---- 第二十七报（2026-09-17）的四个数与一个钟 ----
    // 慢性箭头的修复账：每拍问一次 OS「屏上此刻是什么形状」（GetCursorInfo，纯读），凡是「可见、
    // 非零、也不是我们的透明句柄」都记为外来形状。计数随显示行与每秒取样行出日志，下一次再有
    // 「藏了但屏上有箭头」的报告，第一眼就能看到我们是否看见了它、试过哪几层、各试了多少次。

    /// <summary>本段发现外来形状的拍数，累计。</summary>
    private int _foreignShapes;
    private IntPtr _screenCursorShape;
    private bool _screenCursorTransparent;
    private CursorVisibilityEvents? _cursorVisibilityEvents;

    /// <summary>连续发现外来形状的拍数。清零条件只有「某一拍屏上干净了」或藏匿结束。连续满
    /// <see cref="ForeignStreakForPoke"/> 拍升级指针处 1px 往返。</summary>
    private int _foreignStreak;

    /// <summary>本段重发布（<see cref="PictureSurface.RepublishCursor"/>）的次数。</summary>
    private int _republished;

    /// <summary>本段指针处 1px 往返（<c>PlayerPage.PokeForeignCursor</c>）的次数。</summary>
    private int _foreignPokes;

    /// <summary>上次 1px 往返的时刻，限频约每秒一次——这是真实输入，更不该十赫兹地摇。</summary>
    private long _lastPokeAt;

    /// <summary>连续异形几拍后升级 1px 往返。300ms（三拍）是给重发布留的窗口：日志里健康的
    /// 重申一两拍内就能看到形状被换回透明，救不回来说明的正是「重申的通道失灵」。</summary>
    private const int ForeignStreakForPoke = 3;

    /// <summary>1px 往返的最小间隔（毫秒）。失败也照限——失败那一拍多半连着失败，摇十次不如隔一秒摇一次。</summary>
    private const int PokeCooldown = 1000;

    /// <summary>
    /// The duration the ticks were laid out against. The marks arrive before mpv has a duration to place
    /// them on, so the drawing has to be retried once it does — and exactly once, not on every status
    /// update for the rest of the film.
    /// </summary>
    private double _ticksFor;

    /// <summary>
    /// The transport bar's height the last time it was laid out, which is what the 跳过 button is placed
    /// above. Remembered rather than read on the spot, because the offer stands outside the reveal rule —
    /// it is up while the bar is down, and a bar that is down measures nothing at all.
    /// </summary>
    private double _barHeight;

    /// <summary>
    /// What the last applied status said about <c>pause</c>, or null before the first status of a playback.
    /// The badge is for the moment pause changes, and status arrives about twenty times a second saying the
    /// same thing; null covers the other end — a file opening paused, or opening at all, is not something
    /// the user just did, so it gets no acknowledgement.
    /// </summary>
    private bool? _paused;

    /// <summary>
    /// The pointer captured for a title-bar drag, or null when no drag is running or the capture was
    /// refused. Held only to give it back: the drag's own state is <see cref="HostWindow.Dragging"/>.
    /// </summary>
    private Pointer? _dragPointer;

    /// <summary>
    /// Which of the four reasons are currently keeping the chrome on screen, if any. See <see cref="Hold"/>
    /// for why this is a set rather than the single flag the rule in Core carries.
    /// </summary>
    private ChromeHold _holds;

    /// <summary>
    /// Whether 需求 7's 字幕字体 box has the keyboard. Two things follow from it: the chrome is pinned, and
    /// the player's own single-letter keys are silenced — F, M, N, P, Space, the arrows and Backspace are
    /// all playback commands and all characters a font name is spelled with.
    /// </summary>
    private bool _typing;

    /// <summary>
    /// What the box read before it was focused, so leaving it without picking anything puts the family
    /// currently in use back rather than leaving a half-typed search sitting in the title bar.
    /// </summary>
    private string _fontTextBefore = string.Empty;

    public PlayerPage()
    {
        InitializeComponent();

        _videoTarget = new CompositionVideoTarget(VideoHost);

        // Before anything else that draws: the XAML declares the overlay's brushes empty and this fills
        // them from PlayerPalette. Unpainted they are transparent, not missing — see PlayerPage.Palette.cs.
        PaintPalette();

        // Same reasoning one step further: the pin's two states — which of the two drawn pins is showing, the
        // name a screen reader gets, the tooltip — are written by one method, so the markup carries no second
        // copy of the starting state for them to drift out of step with. The window is not attached yet, which
        // SetPinned allows for.
        SetPinned(false);

        _seekClock = (SeekClockConverter)Resources["SeekClockConverter"];
        _seekClock.Scale = PlayerViewModel.SeekScale;

        _pulse = (Storyboard)Resources["PulseStoryboard"];
        _pauseArt = Build(PulseArt.Pause);
        _playArt = Build(PulseArt.Play);

        // handledEventsToo, because the chrome is full of buttons and a pointer over one of them is
        // exactly the case the reveal rule must not miss: a control marks the event handled, and
        // without this the hand aiming at it would read as a pointer that had stopped moving.
        Root.AddHandler(PointerMovedEvent, new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
        Root.AddHandler(PointerExitedEvent, new PointerEventHandler(OnPointerExited), handledEventsToo: true);
        Root.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheel), handledEventsToo: true);
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), handledEventsToo: true);

        // The two ends of a title-bar drag. Both are needed and neither is sufficient: a release can land
        // on a control that handles it, and a capture can be taken away without any release at all.
        Root.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), handledEventsToo: true);
        Root.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnPointerCaptureLost), handledEventsToo: true);

        // Same reason again, one level down: the slider fills the track and handles every move over it,
        // so a preview listening on its own PointerMoved would only ever see the two-pixel margin.
        SeekTrack.AddHandler(PointerMovedEvent, new PointerEventHandler(OnSeekTrackHover), handledEventsToo: true);
        SeekTrack.AddHandler(PointerExitedEvent, new PointerEventHandler(OnSeekTrackLeft), handledEventsToo: true);

        // 点击画面暂停, and a double click still goes fullscreen. WinUI raises Tapped for the first click of
        // a double one and DoubleTapped for the second, so the two gestures overlap by construction. The tap
        // is therefore held back rather than issued — see PictureTap: caught inside the hold, a double click
        // issues no pause at all, which is what 「双击画面全屏的时候会触发暂停和开始」 was about. It used to
        // pause at once and let the double tap put it back, which left the net state correct and the badge
        // flashing twice.
        Tapped += OnTapped;
        DoubleTapped += OnDoubleTapped;
        KeyDown += OnKeyDown;

        // 空格总归「播放/暂停」，连被控件标了已处理的那几下也要收得到 —— 页面这一层收不到那样的键，所以
        // 挂在 Root 上带 handledEventsToo（见 OnSpaceShortcut）。点过的 chrome 按钮把焦点还给页面，别让
        // 「点一下音量」偷走此后整场的空格（见 OnChromeClick）。
        Root.AddHandler(KeyDownEvent, new KeyEventHandler(OnSpaceShortcut), handledEventsToo: true);
        // Click 的路由事件标识符在这套投影里没暴露（ButtonBase、Button 上都没有），所以这些不带菜单的
        // 按钮一颗颗订阅；带 Flyout 的六颗不订阅 —— 菜单要靠焦点接管上下键（见 OnChromeClick）。
        // 统计那颗是 ToggleButton，所以数组的类型是 ButtonBase。
        foreach (var button in new ButtonBase[] { BackButton, StatsButton, PinButton, MuteButton, SkipButton,
                     PreviousButton, PlayButton, NextButton, FullscreenButton,
                     MinimizeButton, MaximizeButton, CloseButton })
        {
            button.Click += OnChromeClick;
        }
        _spaceShortcutArmed = true;
        _chromeBlurArmed = true;

        // One-shot in effect: a DispatcherTimer keeps ticking, and the handler's first line stops it.
        _tapHold.Tick += OnTapHoldElapsed;

        _ticker.Tick += OnTick;

        // A flyout is where the pointer went, so the chrome must not read the stillness as disinterest.
        foreach (var flyout in new[] { EpisodeMenu, AudioMenu, SubtitleMenu, SpeedMenu, MoreMenu, PictureMenu })
        {
            flyout.Opened += (_, _) => Hold(true, ChromeHold.Menu);
            flyout.Closed += (_, _) => Hold(false, ChromeHold.Menu);
        }
    }

    /// <summary>
    /// The page's state, and the only object here that knows what is playing. Assigned once by
    /// <see cref="Attach"/> — before that every <c>x:Bind</c> path in the XAML is null-rooted, which the
    /// generated code answers by writing nothing at all, so the declared values stand until
    /// <c>Bindings.Update()</c> at the end of that call.
    /// </summary>
    internal PlayerViewModel ViewModel { get; private set; } = null!;

    /// <summary>The tick count every rule here is measured in. One source, so a test can hold it still.</summary>
    private static long Now => Environment.TickCount64;

    /// <summary>For the self-check, which inspects the live tree from outside.</summary>
    internal bool PlayerVisible => Visibility == Visibility.Visible;

    internal bool ChromeUp => _chrome.State.Any;

    /// <summary>
    /// Whether the mouse pointer has been handed back to the framework. For the self-check's reset gate: a probe
    /// that hid the cursor and forgot to give it back leaves the whole application without a pointer, which is
    /// the one failure here a user cannot work around.
    /// </summary>
    internal bool CursorRestored => !_cursorHidden && Root.Cursor is null;

    /// <summary>
    /// Which pieces of chrome are actually on screen, which is not the same question as
    /// <see cref="ChromeUp"/>: the rule starts out saying everything is up, because that is what should
    /// be true the moment a film begins, and nothing has rendered it yet while the player is off screen.
    /// <para>
    /// The rail is read by opacity rather than by visibility because that is how it is now shown at all —
    /// see <see cref="FadeRail"/>. Opacity is the assigned value, not an animation's current frame, so
    /// this stays the same kind of answer the other two give.
    /// </para>
    /// </summary>
    internal bool ChromeShown => Bar.Visibility == Visibility.Visible
        || TitleStrip.Visibility == Visibility.Visible
        || Rail.Opacity > 0;

    /// <summary>
    /// Whether <see cref="Attach"/> has run. Nothing can be played before it has, and no handler here
    /// may touch <see cref="ViewModel"/> until it has: the page is built by the shell's XAML and handed
    /// its state afterwards.
    /// </summary>
    internal bool Attached => _shell is not null && _window is not null;

    /// <summary>
    /// Handed its state, the shell and the window once, before anything is played. The view model comes
    /// from the container at the one place that has one — <see cref="ShellPage.AttachWindow"/> — so
    /// nothing in this file asks a container for anything.
    /// </summary>
    internal void Attach(PlayerViewModel viewModel, ShellPage shell, HostWindow window)
    {
        ViewModel = viewModel;
        _shell = shell;
        _window = window;
        _cursorVisibilityEvents?.Dispose();
        _cursorVisibilityEvents = null;
        if (ViewModel.PictureInHostWindow)
            _cursorVisibilityEvents = new CursorVisibilityEvents(window.Handle, OnSystemCursorChanged);

        ViewModel.Noticed += OnNoticed;
        ViewModel.RefreshRequested += OnRefreshRequested;
        ViewModel.PlayerShown += EnterPlayer;
        ViewModel.PlayerHidden += LeavePlayer;
        ViewModel.PlaybackStarted += OnPlaybackStarted;
        ViewModel.ChaptersChanged += OnChaptersChanged;
        ViewModel.StatusApplied += OnStatusApplied;
        ViewModel.PictureAspectChanged += OnPictureAspectChanged;
        ViewModel.StatsUpdated += OnStatsUpdated;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        if (ViewModel.CoverUp) ShowCoverPlate();

        // 着色器档位 needs to know how large the picture is being drawn, which only the window can say. Pulled
        // for the launch decision, pushed afterwards — see OnGeometryChanged.
        ViewModel.MeasureSurface = MeasureSurface;

        // 帧同步 needs the other half of 「what screen is this」: how fast it refreshes. Pulled only, and only at
        // launch — a window dragged to a slower screen mid-film keeps the sync mode it started with.
        ViewModel.MeasureRefreshHz = () => _window?.RefreshHz() ?? 0;
        window.GeometryChanged += OnGeometryChanged;

        window.ClientRectTransition += OnClientRectTransition;

        // mpv.net 的「未激活不藏」（2026-09-16 照搬）：焦点位喂给规则，藏匿条件里那一问由它回答。
        // 第二十九报（2026-09-17）起这一位不再走 WM_ACTIVATE 事件、改由 OnTick 每拍重问——判据从
        // 「窗口在前台」放宽为「前台，或指针仍停在本线程的窗口上」，失焦后把鼠标移回画面（还没点击
        // 激活）的那几秒也照藏；被动失焦不再掀光标。见 OnTick 与 ChromeReveal.WindowFocused。

        // 键盘兜底（2026-09-15「新增esc退出全屏 按空格开始播放」）：Win32 键盘焦点不在岛里时（全屏播放
        // 期间被别的应用抢过前台再回来、焦点落在宿主/视频子窗口上），OnKeyDown 和 OnSpaceShortcut 都收
        // 不到键 —— 窗口的 WH_KEYBOARD 钩子把空格和 Esc 转到本页（<see cref="IWin32KeySink"/>）。
        // Detach 时收回，独立窗口接管播放后两边不打架。
        window.SetWin32Keys(this);

        ViewModel.Connect();

        // The x:Bind paths were all null-rooted while ViewModel was, including the OneTime Command=
        // bindings the transport buttons use: without this the buttons would stay dead for the session.
        Bindings.Update();
    }

    /// <summary>
    /// On the way out. The ticker and the cursor are this page's to put back; everything in flight is
    /// the view model's, and it is told to drop it.
    /// </summary>
    internal void Shutdown()
    {
        _ticker.Stop();
        DropTapHold();

        // 十八报：与 LeavePlayer 同理 —— 藏着的时候关停也是一条显示路径，挂上名再放。
        if (_cursorHidden) _woke = "播放层关停";
        SetCursorHidden(false);
        _cursorVisibilityEvents?.Dispose();
        _cursorVisibilityEvents = null;
        if (_window is not null)
        {
            _window.GeometryChanged -= OnGeometryChanged;
            _window.ClientRectTransition -= OnClientRectTransition;
        }
        _onStage = false;
        StopPlayerMotion();
        ViewModel?.Shutdown();
        ReleaseVideoSurface();
    }

    internal void ReleaseVideoSurface() => _videoTarget.Dispose();

    /// <summary>
    /// Lets go of the view model without shutting it down, and puts this page back the way
    /// <see cref="LeavePlayer"/> leaves it. For the one case where a page must not answer a playing view
    /// model: 独占模式 runs the film with no page of ours at all — picture and uosc controls live in mpv's
    /// own window and the main window stays on whatever page the user is browsing, so this page steps aside
    /// for the whole playback and the shell hangs it back when the film is gone.
    /// <para>
    /// The reason this has to exist at all is that <see cref="Attach"/> subscribes nine events and a set of
    /// pull-delegates to a view model that is a singleton driving one real mpv session. Two pages wired to it
    /// at once both run <see cref="EnterPlayer"/>, both reshape the window they happen to hold, and both answer
    /// the same command — so exactly one of them is attached at any moment, and this is how the shell's own
    /// player steps aside. <see cref="Shutdown"/> is the other end: the process is going away.
    /// </para>
    /// <para>
    /// <see cref="PlayerViewModel.Connect"/>'s own subscriptions (<c>_playback.*</c>) are the view model's and
    /// stay put — they belong to whichever page is attached, and the view model outlives both.
    /// </para>
    /// </summary>
    internal void Detach()
    {
        if (!Attached) return;

        LeavePlayer();
        StopPlayerMotion();
        CompletePlayerExit();

        ViewModel.Noticed -= OnNoticed;
        ViewModel.RefreshRequested -= OnRefreshRequested;
        ViewModel.PlayerShown -= EnterPlayer;
        ViewModel.PlayerHidden -= LeavePlayer;
        ViewModel.PlaybackStarted -= OnPlaybackStarted;
        ViewModel.ChaptersChanged -= OnChaptersChanged;
        ViewModel.StatusApplied -= OnStatusApplied;
        ViewModel.PictureAspectChanged -= OnPictureAspectChanged;
        ViewModel.StatsUpdated -= OnStatsUpdated;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        ViewModel.MeasureSurface = null;
        ViewModel.MeasureRefreshHz = null;
        if (_window is not null)
        {
            _window.GeometryChanged -= OnGeometryChanged;
            _window.ClientRectTransition -= OnClientRectTransition;
        }

        // 键盘兜底同步摘下：接键人跟着这一次 Attach 走，别让下一任（下一次 Attach 的页）的老号码还留在线上。
        if (_window is not null) _window.SetWin32Keys(null);

        _cursorVisibilityEvents?.Dispose();
        _cursorVisibilityEvents = null;
        _shell = null;
        _window = null;
    }

    // ---- 输出尺寸 -----------------------------------------------------------------

    /// <summary>
    /// How large the picture is being drawn, and how large it would be at full screen.
    /// <para>
    /// 档位固定用全屏那套（他的拍板，2026-09-11）：无论窗口什么形状，量出来的面永远是所在显示器，
    /// Fullscreen 恒真 —— 着色器档位从开播起按显示器定死，进退全屏和拖动不再换链。ArtCNN 这类
    /// 放大器在核显上每个尺寸的冷编译要数秒，随全屏实时换链就是「画面停在旧尺寸贴在左上角」；
    /// 探针读数在 <c>work/embedprobe/</c>。外部 mpv.exe 后端同样按显示器算，无需再走降级链。
    /// </para>
    /// </summary>
    private ShaderSurface MeasureSurface()
    {
        if (_window is null) return default;

        var monitor = _window.MonitorSize();
        return new ShaderSurface(monitor.Width, monitor.Height, monitor.Width, monitor.Height, true);
    }

    /// <summary>
    /// The window resized, went full screen, or landed on another monitor. Handed straight over: what each
    /// kind of change costs is <see cref="OutputWatch"/>'s to decide, and a resize in progress costs nothing.
    /// The measurement goes over as a callback so it is skipped entirely while nothing is playing — this fires
    /// for every <c>WM_SIZE</c> the shell sees, browsing included.
    /// </summary>
    private void OnGeometryChanged()
    {
        if (Attached) ViewModel.NoteSurface(MeasureSurface);
    }

    /// <summary>
    /// 播放, from a poster, a row, a context menu or the 选集 picker. Straight through: what playing
    /// means is not a question about the visual tree.
    /// </summary>
    internal Task PlayAsync(
        EmbyItem item,
        EmbyItem? parent = null,
        PlaybackChoice? choice = null,
        IReadOnlyList<EmbyItem>? episodes = null) =>
        Attached ? ViewModel.PlayAsync(item, parent, choice, episodes) : Task.CompletedTask;

    // ---- the seven things only the page can do ------------------------------------

    private void OnNoticed(string message, InfoBarSeverity severity) => _shell?.Notify(message, severity);

    private void OnRefreshRequested() => _shell?.RefreshActive();

    /// <summary>
    /// 独立播放说明牌的唯一写手：画面不在本窗口（独立管线/外部 mpv.exe 后端）时替黑舞台说一句话，
    /// 否则收起。进场（<see cref="EnterPlayer"/>，此时多半还没有会话、按设置推算）与开播
    /// （<see cref="OnPlaybackStarted"/>，会话已就位、按后端表态）各问一次，两处曾各抄一遍同一段
    /// 三元表达式，2026-09-17 收敛到这里。
    /// </summary>
    private void UpdateStandaloneHint() =>
        StandaloneHint.Visibility = ViewModel.PictureInHostWindow
            ? Visibility.Collapsed
            : Visibility.Visible;

    /// <summary>浏览与播放交叠一小段，窗口接管与自动全屏在同一轮转完成。</summary>
    private void EnterPlayer()
    {
        if (_shell is null || _window is null || _onStage) return;

        _window.CaptureBrowseGeometry();
        _onStage = true;
        _inputSuspended = false;
        _videoTarget.RetainLastFrame = true;
        _window.FreeSizing = true;
        UpdateStandaloneHint();
        if (ViewModel.PictureInHostWindow)
            _cursorVisibilityEvents ??= new CursorVisibilityEvents(_window.Handle, OnSystemCursorChanged);
        else
        {
            _cursorVisibilityEvents?.Dispose();
            _cursorVisibilityEvents = null;
        }

        Visibility = Visibility.Visible;
        Stage.Visibility = Visibility.Visible;
        if (_cursorHidden) _woke = "播放页就位";
        _chrome.Reset(Now);
        Render();
        PlaceOverlays();
        ShowCurrentFont();
        SetPinned(ViewModel.PictureInHostWindow && ViewModel.SavedPinTopmost);
        _polledKnown = false;
        _ticker.Start();

        // 先开进场轨道：溶解发生在没动过的窗口里。收浏览层与跳全屏都挂在淡入完成那一拍 —— 页面不透明了、
        // 底下是近黑的遮罩，这时换窗口，岛面换新尺寸前那几帧旧画面残影只是一块深色；排在第 0 拍则把亮着的
        // 浏览页缩在放大后的窗口左上角闪出来（2026-09-18 用户截图）。理由全文在 TransitionPage。
        // 这一带刻意不做强制布局：进场时客户区跟浏览时一模一样，全树 Measure/Arrange 纯属白算，却要吃掉
        // 约九十毫秒（重主页实机诊断 2026-09-19），把淡入的呈现窗口挤没了——跳变那一拍的同步在
        // PublishClientRect 里，那里才是尺寸真变了的时刻。
        TransitionPage(entering: true,
            landed: () => { },
            faded: () =>
            {
                if (!_onStage) return;
                _shell?.ShowPlayer(true);
                if (_window is not null) _window.VideoVisible = true;
                EnterAutoFullscreen();
            });
        _window.PlaybackTitleBar = true;
        _videoTarget.SynchronizeGeometry();
        Focus(FocusState.Programmatic);
    }

    /// <summary>还原浏览几何只有一个落点，旧视频帧留到退场结束再释放。</summary>
    private void LeavePlayer()
    {
        if (_shell is null || _window is null || !_onStage) return;

        _onStage = false;
        _inputSuspended = true;
        _ticker.Stop();
        ResetCover();
        EndWindowDrag();
        _typing = false;
        FontBox.IsSuggestionListOpen = false;
        Hold(false, ChromeHold.Search);
        SetPinned(false);
        if (_cursorHidden) _woke = "播放退出";
        SetCursorHidden(false);
        HidePulse();
        DropTapHold();
        _pulseMutedAt = null;
        _paused = null;
        HideChapterPeek();
        RenderChapterTicks();

        // FreeSizing 在整个还原事务里保持 true，避免中间的 WM_SIZE 覆盖播放前的浏览位置。
        // RestorePlayerToBrowse 在同一拍触发 ClientRectTransition：岛布局同步与画面跑动都在那里。
        _window.RestorePlayerToBrowse();
        _window.VideoVisible = false;
        _shell.ShowPlayer(false);
        _window.FreeSizing = false;
        FullscreenGlyph.Glyph = Glyph(FullscreenEnterCode);
        TransitionPage(entering: false, CompletePlayerExit);
    }

    /// <summary>A new file is on screen, so the chrome starts its countdown from now.</summary>
    private void OnPlaybackStarted()
    {
        // Whatever the last file's pause state was, this one has not been paused by anybody yet.
        _paused = null;

        // 初次进场已处理自动全屏；这里仍覆盖换集与后端改变画面归属的情况。
        if (!ViewModel.PictureInHostWindow)
        {
            _window!.Fullscreen = false;
            SetPinned(false);
        }
        UpdateStandaloneHint();
        // 进场轨道还在飞时不在这里跳窗：快启动的片子（本地小文件）这条事件来得比换手早，抢在淡入
        // 呈现之前跳窗就是把「旧尺寸内容钉在放大后的窗口左上角」放出来。交给淡入换手上那条
        // EnterAutoFullscreen；换集时没有进场轨道（_enterAnimating 假），照旧当拍落位。
        if (ViewModel.AutoFullscreenOnPlayback && !_enterAnimating)
            SetFullscreen(true);

        // 第九报（2026-09-15）：姓名牌。用户报「屏幕一全屏播放时，屏幕二的 AyuGram 收到消息会唤起屏幕一
        // 静止隐藏的鼠标指针」排查期间，日志里成串「未标注的显示路径」三连的另一半元凶：连播换集时上一集
        // 的光标还藏着，这里 Reset+Render 无声把它放回，显示行只打默认串。挂名「新的播放开始了」——
        // 全屏内换集后光标先冒一下再藏，日志从此说得出是这条路，而不是无名路。
        if (_cursorHidden) _woke = "新的播放开始了";

        _chrome.Reset(Now);
        Render();
    }

    /// <summary>The marks were replaced: the ticks are stale and any preview is of the wrong file.</summary>
    private void OnChaptersChanged()
    {
        HideChapterPeek();
        RenderChapterTicks();
    }

    /// <summary>
    /// The four things one status snapshot means to the tree rather than to a binding: the tooltip
    /// converter's run time, the tick layout the duration was missing when the marks arrived, the chrome's
    /// 「stay up while loading」 rule, and the badge that acknowledges a pause or a resume.
    /// </summary>
    private void OnStatusApplied(PlayerStatus status)
    {
        _seekClock.DurationSeconds = status.Duration;

        // The marks were known before the duration was, and they cannot be placed without it.
        if (Math.Abs(status.Duration - _ticksFor) > 0.001) RenderChapterTicks();

        // Only 「still loading」 pins the chrome now, and even that is belt and braces: the opaque handover
        // cover is over the picture for the whole of it. Pause used to pin it as well, on the reasoning
        // that a frozen frame has nothing to watch — but 「别什么进度条标题音量条都持久显示在画面上」, and a
        // paused frame is usually exactly the thing someone stopped to look at. Pause says so with the
        // badge below instead, and then gets out of the way.
        if (_chrome.SetKeep(!status.Loaded, Now))
        {
            // A loading hold that pulls the cursor back out of a hide says so by name, not by the default.
            if (!status.Loaded && _cursorHidden) _woke = "画面加载中";

            // 第九报（2026-09-15，用户原话「你直接抄这些开源项目吧」这一轮）：加载完成的撤销也要挂名。
            // 15:42:52.924 自检现场：探针藏起光标 1.25 秒后，被探针自己的 Pump 派发的 VM 快照在这里
            // 翻掉 KeepChrome——SetKeep(false) 重盖活跃时钟 → Settle 翻转 → Render → 光标同步路
            // （SetCursorHidden(_chrome.CursorHidden)）把页面领跑的藏匿掀了，显示行打了「未标注的
            // 显示路径」，13 条下游判据连带全红（负计数锁的判据其实全绿）。常态播放里这条路和藏匿
            // 永不同现（加载中 chrome 挂着、光标本就显示），但姓名牌制度的意义正是让「未标注」
            // 可追查——见了就得修，修的第一步是让它自报家门。
            else if (_cursorHidden) _woke = "画面加载完成";

            Render();
        }

        // The badge follows the edge rather than the state: status says the same thing some twenty times a
        // second, and the first edge of a playback is the file opening rather than anyone pressing anything.
        if (!status.Loaded || _paused == status.Paused) return;

        var opening = _paused is null;
        _paused = status.Paused;
        if (!opening) Pulse(status.Paused);
    }

    /// <summary>
    /// Shows the 暂停/播放 badge for its fifth of a second — 「暂停后显示一秒暂停图标就行（开启播放也弄个一秒的
    /// 动画）」, shortened by 「把暂和开始的动画改为 0.2 秒」. The shape is the state that was just entered, not
    /// the transport button's 「what pressing me does」 — which is why the two are the opposite way round from
    /// <see cref="PlayerViewModel.PlayPauseGlyph"/>.
    /// <para>
    /// Silent for a moment after a double tap. That gesture cancels or undoes a pause, and its own status edge
    /// is still on its way back from mpv; the badge saying 「playing」 about an undo nobody asked for is exactly
    /// the flashing 「双击画面全屏的时候会触发暂停和开始」 reported.
    /// </para>
    /// <para>
    /// One white shape and nothing behind it — 「点击画面暂停和开始的图标要纯白色，去掉灰色」. What it cost is
    /// written down at <see cref="PulseArt"/>: over a nearly white frame the badge is not visible.
    /// </para>
    /// </summary>
    private void Pulse(bool paused)
    {
        if (Muted) return;

        PulseShape.Data = paused ? _pauseArt : _playArt;
        PulseBadge.Visibility = Visibility.Visible;

        // Restarted rather than layered: a second toggle inside the first fifth of a second is one new
        // acknowledgement, not two overlapping ones.
        _pulse.Stop();
        _pulse.Begin();
    }

    /// <summary>Takes the badge away now, animation and all. Shared by the way out and by the double tap.</summary>
    private void HidePulse()
    {
        _pulse.Stop();
        PulseBadge.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Whether a double tap has just been dealt with, and the badge is therefore saying nothing. Measured as a
    /// span rather than counted as a number of status edges: the undo travels to mpv and back, so it may
    /// arrive early, late, or be coalesced away entirely — and a counter that never came down would eat the
    /// badge for every real pause after it.
    /// </summary>
    private bool Muted => _pulseMutedAt is { } at && Now - at < PictureTap.PulseMuteMilliseconds;

    /// <summary>
    /// The badge's fifth of a second is up. Collapsed rather than merely left at zero opacity: a transparent
    /// element is still measured and arranged on every frame the player draws.
    /// </summary>
    private void OnPulseCompleted(object? sender, object e) => PulseBadge.Visibility = Visibility.Collapsed;

    /// <summary>
    /// 工具用（<c>--hide-cursor</c>）：把播放层摆上来、十赫兹那颗计时器照常跑，然后什么都不动 —— 两秒后指针就
    /// 该消失。一个字节的视频都不播。
    /// <para>
    /// 走 <see cref="EnterPlayer"/> 而不是自己拼一遍：那一处已经把「摆上播放层、重置显隐规则、忘掉指针位置、起
    /// 计时器、拿走焦点」一气做完，而这件事要的正是一次完整的进场。空的视频子窗口关掉 —— 没有片子，留着只是一块
    /// 黑。日志那一行印指针此刻归谁，观察脚本读它。
    /// </para>
    /// </summary>
    internal void HoldCursorForDemo()
    {
        if (!Attached) return;

        EnterPlayer();
        _window!.VideoVisible = false;

        Log.Info(Category, $"--hide-cursor：播放层已摆上，指针在窗口里={PointerInside()}，{PointerOwner()}");
    }

    /// <summary>
    /// 缩放窗口时按画面比例联动 / 窗口化时视频有黑边: the shape the window holds its client area in while an
    /// edge is dragged, and a one-off reshape so the first frame of a film is not letterboxed either.
    /// Zero is 「the picture has released the window」 — from then on the window resizes however the pointer
    /// says, and it keeps whatever shape the film left it in（「锁定窗口比例大小」曾经在这一刻把它拽回 16:9，
    /// 那个开关 2026-09-05 删掉了）。
    /// <para>
    /// 非零这一支是画面第一次碰窗口的地方，而写比例那一下本身就是「先把播放前那份几何记下来」的时刻
    /// （<see cref="HostWindow.PictureAspect"/> 的 setter 里做），所以这里只管整形。
    /// </para>
    /// </summary>
    private void OnPictureAspectChanged(double aspect)
    {
        if (_window is null) return;

        // 独立播放：画面在 mpv 自建的顶层窗口里，本窗口的形状跟它没有关系 —— 比例锁与一次性整形
        // 都只在「画面在本窗口」时有意义，否则就是把一块没有画面的窗口掰成片子的宽高比
        // （实机实录 2026-09-17：1515x851 被掰成 1515x629，用户对着黑窗问「这是什么情况」）。
        if (!ViewModel.PictureInHostWindow) return;

        _window.PictureAspect = aspect;
        if (aspect <= 0) return;

        if (_onStage && ViewModel.CoverUp && ViewModel.AutoFullscreenOnPlayback) return;

        _window.FitToPicture();
    }

    /// <summary>
    /// A fresh set of 统计 readings. An empty set with the panel closed is the panel being put away, and
    /// clears the grid; an empty set with it open is mpv having answered nothing yet, which has a row of
    /// its own — see <see cref="RenderStatRows"/>.
    /// </summary>
    private void OnStatsUpdated(IReadOnlyList<PlaybackStatRow> rows)
    {
        if (rows.Count == 0 && !ViewModel.StatsOpen)
        {
            StatsRows.Children.Clear();
            StatsRows.RowDefinitions.Clear();
            return;
        }

        RenderStatRows(rows);
    }

    private static string Glyph(int codepoint) => char.ConvertFromUtf32(codepoint);

    /// <summary>
    /// One of <see cref="PulseArt"/>'s outlines as a <c>PathGeometry</c>: a straight step becomes a
    /// <c>LineSegment</c>, an arc step becomes the clockwise minor <c>ArcSegment</c> the data promises
    /// (see <see cref="PulseStep"/> — clockwise is a contract with the point order, and this place only
    /// translates it faithfully).
    /// <para>
    /// Built here rather than declared in the markup because the numbers are worth a test: each figure carries
    /// its corner polygon, so 「the weight class the badge sits in」 is exactly <see cref="PulseArt.Bounds"/>
    /// (the rounded outline is cut inward from it and never exceeds it), and PlaybackTests pins that box —
    /// centred — against the one the glyph two generations ago was measured into.
    /// </para>
    /// </summary>
    private static Geometry Build(IReadOnlyList<PulseFigure> figures)
    {
        var geometry = new PathGeometry();

        foreach (var figure in figures)
        {
            var path = new PathFigure
            {
                StartPoint = new Point(figure.Start.X, figure.Start.Y),
                IsClosed = true
            };

            foreach (var step in figure.Steps)
            {
                var point = new Point(step.X, step.Y);
                if (step.Arc)
                    // WinUI 的 ArcSegment 只有无参构造，靠属性赋值；也没有 WPF 那个 IsStroked。
                    path.Segments.Add(new ArcSegment
                    {
                        Point = point,
                        Size = new Size(step.Radius, step.Radius),
                        RotationAngle = 0,
                        IsLargeArc = false,
                        SweepDirection = SweepDirection.Clockwise
                    });
                else
                    path.Segments.Add(new LineSegment { Point = point });
            }

            geometry.Figures.Add(path);
        }

        return geometry;
    }
}
