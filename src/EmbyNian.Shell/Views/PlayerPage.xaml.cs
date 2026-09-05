using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>
    /// How far the pointer has to travel, in logical pixels, before a showing cursor treats it as somebody
    /// moving the mouse. Two, which is under a millimetre and over any jitter a resting mouse produces —
    /// and it applies only while the cursor is visible, so nothing here delays bringing it back.
    /// </summary>
    private const double PointerNoise = 2;

    private readonly ChromeReveal _chrome = new();
    private readonly SeekClockConverter _seekClock;

    /// <summary>
    /// The 暂停/播放 badge's fifth of a second. Held rather than looked up per pulse because it is begun on
    /// every pause and every resume, and a resource lookup on each is a dictionary walk for an object that
    /// cannot change.
    /// </summary>
    private readonly Storyboard _pulse;

    /// <summary>
    /// The four geometries the badge draws with: pause and play, each in two copies — one for the white shape
    /// and one for the darker rim behind it. Read once for the same reason <see cref="_pulse"/> is.
    /// <para>
    /// Four rather than two, and not by choice: a WinUI <c>Geometry</c> cannot be attached to two <c>Path</c>
    /// elements at once. Handing one object to both threw <c>ArgumentException: Value does not fall within the
    /// expected range</c> on the second assignment — the framework's 「this object already has a parent」 —
    /// which is why the numbers live in <see cref="PulseArt"/> and are built into four separate objects rather
    /// than written out twice in the markup.
    /// </para>
    /// </summary>
    private readonly (Geometry Shape, Geometry Rim) _pauseArt;

    private readonly (Geometry Shape, Geometry Rim) _playArt;

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

    private bool _cursorHidden;

    /// <summary>
    /// What the OS said its cursor display counter was after the last <c>ShowCursor</c> this page made:
    /// below zero for hidden, back at zero for shown. Held because it is the only evidence that the call
    /// left the process — see <see cref="ProbeCursor"/>.
    /// </summary>
    private int _cursorCount;

    /// <summary>
    /// How many times the hide has asked the OS to work out the cursor again — the same-point nudge in
    /// <see cref="SetCursorHidden"/>, counted only when it left the process. It is the half of hiding that no
    /// reading of the OS's own can confirm after the fact, and the half whose absence was 「鼠标指针还是不会
    /// 自动隐藏」 with every other reading saying hidden, so the self-check asserts on this count.
    /// </summary>
    private int _cursorNudges;

    /// <summary>
    /// Where the pointer was the last time it was taken to have moved, and what was under it there.
    /// <para>
    /// Held because 「静止不动」 is a claim about the pointer and WinUI's <c>PointerMoved</c> is not one. It is
    /// also raised when the tree under a hand that never twitched changes — which the chrome collapsing at
    /// 650 ms does, under a pointer resting in the middle of the picture — and a mouse sitting on a desk
    /// can rattle a physical pixel with nobody touching it. Each of those used to restamp the idle clock,
    /// and that clock is what the cursor's two seconds are counted from, so any of them repeating inside
    /// two seconds meant 「鼠标指针还是不会自动隐藏」 with the chrome hiding perfectly well: a move in the
    /// middle of the picture reveals nothing, so a restamp there is invisible except to the cursor.
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
    /// Where the OS said the cursor was on the last tick that took it to have moved, and whether there has
    /// been such a tick yet since the player came up.
    /// <para>
    /// The reveal rule's 「静止」 used to be entirely a claim about WinUI events, and the events are the one
    /// layer here that nothing can check: a probe running on the UI thread cannot make WinUI deliver pointer
    /// input to it, because input dispatch will not re-enter a dispatch already in progress — measurably so,
    /// which is why the live probe reports 真移动 0 次 for a pointer it demonstrably moved. Polling the OS ten
    /// times a second moves the load-bearing half of the question onto a path that both a probe and a film
    /// take: a changed position is movement, an unchanged one is stillness, and neither answer depends on
    /// whether an event was raised for it.
    /// </para>
    /// </summary>
    private NativePoint _polled;

    private bool _polledKnown;

    /// <summary>How many ticks found the cursor somewhere new. Printed beside the event count, because which
    /// of the two paths noticed a movement is the whole difference between the last two rounds of this bug.
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
        _pauseArt = (Build(PulseArt.Pause), Build(PulseArt.Pause));
        _playArt = (Build(PulseArt.Play), Build(PulseArt.Play));

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

        ViewModel.Noticed += OnNoticed;
        ViewModel.RefreshRequested += OnRefreshRequested;
        ViewModel.PlayerShown += EnterPlayer;
        ViewModel.PlayerHidden += LeavePlayer;
        ViewModel.PlaybackStarted += OnPlaybackStarted;
        ViewModel.ChaptersChanged += OnChaptersChanged;
        ViewModel.StatusApplied += OnStatusApplied;
        ViewModel.PictureAspectChanged += OnPictureAspectChanged;
        ViewModel.StatsUpdated += OnStatsUpdated;

        // 着色器档位 needs to know how large the picture is being drawn, which only the window can say. Pulled
        // for the launch decision, pushed afterwards — see OnGeometryChanged.
        ViewModel.MeasureSurface = MeasureSurface;

        // 帧同步 needs the other half of 「what screen is this」: how fast it refreshes. Pulled only, and only at
        // launch — a window dragged to a slower screen mid-film keeps the sync mode it started with.
        ViewModel.MeasureRefreshHz = () => _window?.RefreshHz() ?? 0;
        window.GeometryChanged += OnGeometryChanged;

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
        SetCursorHidden(false);
        if (_window is not null) _window.GeometryChanged -= OnGeometryChanged;
        ViewModel?.Shutdown();
    }

    // ---- 输出尺寸 -----------------------------------------------------------------

    /// <summary>
    /// The last render-target size that could actually be read, for 任务书 2.3's middle rung. Held here rather
    /// than in the view model because it is a fact about this window, and deliberately never written from the
    /// monitor fallback — one unreadable moment would otherwise pin every later measurement to the monitor's
    /// native resolution, which is the thing 2.3 forbids.
    /// </summary>
    private (int Width, int Height) _lastTarget;

    /// <summary>
    /// How large the picture is being drawn, and how large it would be at full screen.
    /// <para>
    /// The render target is our client area, which is exactly what the video child window fills. The external
    /// mpv.exe backend draws into a window of its own that is not ours to measure, so it reports nothing and
    /// takes the monitor fallback — with a line in the log saying so, which is what 任务书 2.3 asks of a
    /// backend that cannot answer.
    /// </para>
    /// </summary>
    private ShaderSurface MeasureSurface()
    {
        if (_window is null) return default;

        var target = ViewModel.Embedded ? _window.ClientSize : default;
        var surface = ShaderSurface.Resolve(target, _lastTarget, _window.MonitorSize(), _window.Fullscreen);

        if (!surface.Fallback) _lastTarget = (surface.Width, surface.Height);
        return surface;
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
    /// Gives the window over to the player: the backdrop off so the video child shows through the
    /// transparent chrome, the navigation shell collapsed so its own opaque background is not painting
    /// over the same region, and the focus here so the keyboard reaches the keys below rather than the
    /// pane behind.
    /// </summary>
    private void EnterPlayer()
    {
        if (_shell is null || _window is null || Visibility == Visibility.Visible) return;

        if (ViewModel.Embedded) _window.VideoVisible = true;

        Visibility = Visibility.Visible;
        _shell.ShowPlayer(true);

        _chrome.Reset(Now);
        Render();

        // Both overlays that have to clear another one are placed from what they clear, and this is the one
        // moment before a film where the measurement can be taken: the bar has just been made visible by the
        // line above but nothing has been laid out yet, so it still measures nothing of its own.
        PlaceOverlays();

        // 需求 7's box reads as the family in use whenever nobody is searching with it, and a film may have
        // been started after the settings window changed that family.
        ShowCurrentFont();

        // Nothing known about the pointer yet, so the first tick's poll seeds it rather than measuring a
        // movement against wherever the cursor happened to be during the last film. Reset covers the other
        // half — the rule starts out believing the pointer is nowhere, and 「nowhere」 is a state the cursor
        // never hides in, which for a playback begun from a click means the hide waits for the first poll.
        _polledKnown = false;

        _ticker.Start();
        Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Puts the window back the way it was: fullscreen left, 置顶 dropped, cursor visible, Mica back on,
    /// the navigation shell returned. The view model has already dropped what this playback knew, which
    /// is why the ticks below redraw as none.
    /// </summary>
    private void LeavePlayer()
    {
        if (_shell is null || _window is null) return;

        _ticker.Stop();

        // A drag can outlive the film it started over — Escape stops playback with the button still held —
        // and the hold it took on the chrome is not something ChromeReveal.Reset gives back, so the next
        // film would play with its controls pinned open.
        EndWindowDrag();

        // Same for the 字幕字体 box: a film can end while it still has the keyboard — it ends by itself at the
        // credits — and its hold and its claim on the player's keys are both ours to give back.
        _typing = false;
        FontBox.IsSuggestionListOpen = false;
        Hold(false, ChromeHold.Search);

        _window.Fullscreen = false;
        SetPinned(false);
        FullscreenGlyph.Glyph = Glyph(FullscreenEnterCode);
        SetCursorHidden(false);
        _window.VideoVisible = false;

        // Stopping playback stops it paused often enough, and a badge left mid-fade would be drawn over
        // whatever page the shell comes back to. The tap's own timer goes with it: a tap 150 ms before Escape
        // would otherwise fire its pause into the next film, or into nothing at all.
        HidePulse();
        DropTapHold();
        _pulseMutedAt = null;
        _paused = null;

        Visibility = Visibility.Collapsed;
        _shell.ShowPlayer(false);

        HideChapterPeek();
        RenderChapterTicks();
    }

    /// <summary>A new file is on screen, so the chrome starts its countdown from now.</summary>
    private void OnPlaybackStarted()
    {
        // Whatever the last file's pause state was, this one has not been paused by anybody yet.
        _paused = null;

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
        if (_chrome.SetKeep(!status.Loaded, Now)) Render();

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
    /// The rim carries its own copy of the same geometry — one object cannot be attached to two Paths — so
    /// they are assigned together and from the same pair, which is what keeps a rim of the wrong shape (worse
    /// than no rim at all) out of reach.
    /// </para>
    /// </summary>
    private void Pulse(bool paused)
    {
        if (Muted) return;

        var art = paused ? _pauseArt : _playArt;
        PulseShape.Data = art.Shape;
        PulseRim.Data = art.Rim;
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
    /// Zero is 「the picture has released the window」. If the browsing ratio lock is enabled, hand the
    /// window straight back to that shape so returning home immediately gets the strict first-screen layout.
    /// </summary>
    private void OnPictureAspectChanged(double aspect)
    {
        if (_window is null) return;

        _window.PictureAspect = aspect;
        if (aspect > 0) _window.FitToPicture();
        else _window.FitToShape();
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
    /// One of <see cref="PulseArt"/>'s point lists as a <c>PathGeometry</c>: closed straight-line figures and
    /// nothing else, because the rounded corners come from the stroke's round joins rather than from arcs.
    /// <para>
    /// Built here rather than declared in the markup for two reasons. A <c>Geometry</c> cannot be attached to
    /// two <c>Path</c> elements at once, and this badge is two layers of one shape — so the same numbers would
    /// have had to be written out four times. And the numbers themselves are worth a test: 「what does this come
    /// to on screen once it is stroked」 is arithmetic, and the answer has to match the outer box of the glyph it
    /// replaced or the badge quietly changes size.
    /// </para>
    /// </summary>
    private static Geometry Build(IReadOnlyList<IReadOnlyList<(double X, double Y)>> figures)
    {
        var geometry = new PathGeometry();

        foreach (var points in figures)
        {
            if (points.Count == 0) continue;

            var figure = new PathFigure
            {
                StartPoint = new Point(points[0].X, points[0].Y),
                IsClosed = true
            };

            for (var index = 1; index < points.Count; index++)
                figure.Segments.Add(new LineSegment { Point = new Point(points[index].X, points[index].Y) });

            geometry.Figures.Add(figure);
        }

        return geometry;
    }
}
