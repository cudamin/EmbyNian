namespace EmbyNian.Playback;

/// <summary>
/// Which piece of player chrome a point lands on. Distinguished rather than lumped into one
/// "on the chrome" flag because each piece reveals itself independently — the pointer being on the
/// transport bar is no reason to light up the strip at the other end of the picture.
/// </summary>
public enum ChromePart
{
    None,
    Bar,
    Title,
    Volume,

    /// <summary>The skip-intro button, which shows on its own schedule rather than on proximity.</summary>
    Skip
}

/// <summary>
/// Which pieces of the player chrome should be on screen. Whether, not how much: the rail's strength is
/// <see cref="ChromeReveal.RailStrength"/>, kept off this record so that 「is it up」 stays one bit and a
/// test can still write down a whole expected state.
/// </summary>
public readonly record struct ChromeState(bool Bar, bool Title, bool Rail)
{
    /// <summary>True when any of the three is showing, which is what the cursor rule turns on.</summary>
    public bool Any => Bar || Title || Rail;
}

/// <summary>
/// The reveal rule for the player chrome, as a state machine with no UI in it.
/// <para>
/// The rule is about where the pointer <em>is</em>, not merely that it moved: the transport bar is on
/// screen while the pointer is in the bottom fifth of the picture, the top strip while it is in the top
/// fifth, and a moment of stillness takes both away. The middle three fifths are therefore a dead zone
/// where neither is on screen, which is the point — that is where the subtitles are. Neither of those two
/// fades: 「不要淡入淡出了，鼠标移动到对应位置直接显示」, so each arrives at once and at full strength. A
/// control that is half there is slower to reach and harder to read than one that simply arrives.
/// </para>
/// <para>
/// The volume rail is the exception, and it no longer rides with the transport bar:
/// 「显示进度条的时候不需要同步显示音量条」 replaced the earlier 「显示进度条的时候音量条也要显示」, so it comes
/// up for its own approach strip along the right edge, for a hand already resting on it, and for a moment
/// after a wheel or key change — 「鼠标滚轮调整音量时要显示音量条」. It also arrives by degrees:
/// <see cref="RailStrength"/> rises as the pointer closes on the middle of that edge —
/// 「加大音量条的尺寸，显示方式改为淡入淡出，鼠标指针越接近右边的中心显示越明显」 — which is a strength for the
/// page to fade to rather than a second visibility flag. The other two stay flips, because the request
/// that made them flips was about them: a bar you are aiming at should not need to catch up with you.
/// </para>
/// <para>
/// Extracted from the WinForms <c>VideoSurface</c>, which had the identical rule welded to a 40 ms
/// <c>Cursor.Position</c> poll: mpv's child window swallowed every mouse message that landed on the
/// video, so the only way to see movement was to sample the cursor. In the WinUI 3 shell the XAML
/// island sits <em>above</em> the video child, so real pointer events arrive and the poll is gone —
/// which also makes 「直接显示」 a matter of one event rather than one tick. What is left is this
/// arithmetic, and it is now something a test can hold still and interrogate.
/// </para>
/// </summary>
public sealed class ChromeReveal
{
    /// <summary>
    /// How long the pointer has to hold still before the chrome goes away. Short on purpose
    /// (「鼠标静止后自动隐藏的速度再快些」): the chrome comes back the instant the pointer moves into the
    /// band it belongs to, so hiding early costs nothing but a movement, while chrome left standing over
    /// a picture nobody is pointing at is in the way for as long as it stays. A pointer resting <em>on</em>
    /// a control counts as activity, so this can be short without the controls dropping out from under a
    /// hand that is aiming at them.
    /// </summary>
    public const long IdleMilliseconds = 650;

    /// <summary>
    /// The same window for a pointer that came to rest <em>on</em> a control. Long enough that a hand
    /// hesitating over a button — or reading the preview above the seek bar — never has it vanish from
    /// under it, and short enough that an abandoned cursor stops pinning the chrome over the picture.
    /// <para>
    /// It exists because 「parked」 used to mean 「exempt」 rather than 「patient」: while the last recorded
    /// position was on any control the idle countdown was skipped entirely, so the chrome — and with it
    /// the cursor, which only hides once nothing is on screen to aim at — stayed up for as long as the
    /// pointer stayed put. Windowed that was invisible: the shell notices the pointer leaving the client
    /// area and clears the parked position for us. Full screen the client area <em>is</em> the screen,
    /// there is nowhere to leave to, and the latch never opened:
    /// 「全屏时最下方的进度条不会自动隐藏，鼠标也不会自动隐藏」.
    /// </para>
    /// <para>
    /// Equal to <see cref="CursorIdleMilliseconds"/> on purpose, so 「鼠标静止不动两秒之后要自动隐藏」 holds
    /// wherever the pointer stopped: parked, the chrome and the cursor go together on the same beat, and a
    /// hand left resting on the seek bar does not keep the picture covered.
    /// </para>
    /// </summary>
    public const long ParkedIdleMilliseconds = 2000;

    /// <summary>
    /// How long the pointer has to hold still before the mouse cursor itself goes —
    /// 「全屏播放且鼠标在画面上时，鼠标静止不动两秒之后要自动隐藏」.
    /// <para>
    /// Longer than <see cref="IdleMilliseconds"/> and asked separately from it because the two hide for
    /// different reasons. The chrome is a request: it belongs to the band the pointer is in, so leaving that
    /// band is reason enough to take it away, and 650 ms of stillness in the dead zone is a clear 「not now」.
    /// The cursor is the pointer, and taking it away in the middle of aiming leaves nothing on screen to
    /// explain where it went — so it waits for a stillness long enough to mean the mouse has been put down.
    /// </para>
    /// </summary>
    public const long CursorIdleMilliseconds = 2000;

    /// <summary>
    /// How long a readout with no pointer behind it stays up — the volume rail after a wheel or key
    /// change, the whole chrome after a keyboard command. Longer than the pointer's idle window because
    /// there is nothing to move: the number has to be readable before it goes.
    /// </summary>
    public const long GraceMilliseconds = 1200;

    /// <summary>
    /// How far the pointer has to travel before what arrived counts as somebody moving the mouse: two, which
    /// is under a millimetre and over anything a resting mouse — or the player's own ask — produces.
    /// <para>
    /// One pixel is the number to keep it above, and there are two of them. A mouse lying on a desk rattles a
    /// pixel, and a hide ends with a real one-pixel round trip through the input queue
    /// (<c>Native.NudgeCursorState</c>, the only lever that makes WinUI re-read the transparent
    /// <c>ProtectedCursor</c>). While a single pixel was enough to bring the cursor back — 「once the cursor is
    /// hidden, any move at all brings it back」, written when the worry was a hand being asked for a second
    /// pixel — that round trip could wake the player out of its own hide, which is what 「鼠标隐藏了一会又会
    /// 自动跑出来」 turned out to be. A hand that means it covers two pixels inside its first event.
    /// </para>
    /// <para>
    /// The same two in both cursor states and on both paths — XAML pointer events, which report logical pixels,
    /// and the OS poll, which reports physical ones. The caller keeps the anchor still under the threshold, so a
    /// slow hand accumulates its way past it instead of having its movement thrown away one pixel at a time.
    /// </para>
    /// </summary>
    public const double MovePixels = 2;

    /// <summary>
    /// Whether a reported position change of <paramref name="dx"/>,<paramref name="dy"/> is somebody moving the
    /// mouse rather than noise — see <see cref="MovePixels"/> for what is at stake and why the threshold is
    /// where it is. Extracted because two very different callers ask it: the page's pointer-event filter, in
    /// logical pixels, and its ten-hertz poll of the OS, in physical ones. A rule that has to hold in both
    /// places is a rule that can be pinned by a test instead of by two comments agreeing with each other.
    /// </summary>
    public static bool Travelled(double dx, double dy) =>
        (dx > 0 || dy > 0) && (dx >= MovePixels || dy >= MovePixels);

    /// <summary>The fraction of the picture's height each edge band occupies.</summary>
    private const int Bands = 5;

    /// <summary>
    /// How strong the volume rail is at its dimmest, as a fraction of full. The floor exists because
    /// 「越接近右边的中心显示越明显」 is about degrees of a thing that is <em>there</em>: a rail the rule has
    /// decided to show, drawn at five percent because the pointer entered its strip at the very bottom, is
    /// not a subtle hint but a bug someone would report. The remaining fraction is what proximity spends.
    /// </summary>
    public const double RailFloor = 0.35;

    private long _lastActivity;

    /// <summary>Until this tick count the whole chrome shows whatever the pointer is doing.</summary>
    private long _forceUntil;

    /// <summary>Until this tick count the rail shows even though the pointer is not on it.</summary>
    private long _railUntil;

    /// <summary>Where the pointer was last seen, as a fraction of the picture's height, or -1 for away.</summary>
    private double _pointerY = -1;

    /// <summary>
    /// How deep into the rail's approach strip the pointer is: 0 at the strip's inner boundary, 1 hard
    /// against the right edge, and -1 for a pointer that is not in the strip at all — the same 「-1 means
    /// away」 as <see cref="_pointerY"/>, so that 「in the strip」 and 「how far in」 can be one number
    /// instead of a flag plus a number that has to agree with it.
    /// </summary>
    private double _railNear = -1;

    private ChromePart _part;
    private bool _holdChrome;
    private bool _keepChrome;

    /// <summary>
    /// Starts fully revealed: playback has just begun, the pointer may be anywhere, and the first thing
    /// the user needs is to see that there are controls at all. Every field agrees so a layout pass that
    /// runs before the first pointer event cannot read a state where the bar is up with no volume beside it.
    /// </summary>
    public ChromeState State { get; private set; } = new(true, true, true);

    /// <summary>
    /// How strongly the volume rail should be drawn, from <see cref="RailFloor"/> to 1, and 0 when it is
    /// not up at all. The page fades to this rather than flipping to it —
    /// 「显示方式改为淡入淡出，鼠标指针越接近右边的中心显示越明显」.
    /// <para>
    /// It is a strength and not a probability: full whenever the rail is a readout rather than an approach
    /// — a wheel notch, a keyboard command, a hand already on the slider, a pinned chrome — because a
    /// number nobody is pointing at still has to be legible. Proximity only grades the case proximity
    /// caused, and it grades it on both axes at once: how far into the strip along the right edge, and how
    /// near the middle of that edge, so the corners of the picture stay at the floor.
    /// </para>
    /// </summary>
    public double RailStrength { get; private set; } = 1;

    /// <summary>
    /// Pins the chrome open regardless of the pointer. Set while one of the bar's flyouts is up: the
    /// pointer is then over a popup of its own, and the user is mid-choice.
    /// </summary>
    public bool HoldChrome => _holdChrome;

    /// <summary>
    /// Pins the chrome open because there is no picture yet — a file that is still loading has nothing to
    /// cover, and the shell's own handover cover is over the whole of it anyway.
    /// <para>
    /// A paused file used to pin it too, on the reasoning that a frozen frame has nothing to watch so the
    /// way out of it should stay visible. It is off that list now: 「别什么进度条标题音量条都持久显示在画面
    /// 上」 — a paused frame is usually the exact thing someone stopped to look at, and three overlays over
    /// it is not how you say 「paused」. The player says that with a one-second badge instead, and the rule
    /// here goes back to being about the pointer.
    /// </para>
    /// </summary>
    public bool KeepChrome => _keepChrome;

    /// <summary>
    /// Opens or closes the flyout hold. Takes the clock like every other mutator here, and a latch for the
    /// same reason <see cref="SetKeep"/> is one: being told again what it already holds is not news.
    /// </summary>
    public bool SetHold(bool held, long now)
    {
        if (_holdChrome == held) return Settle(now);

        _holdChrome = held;

        // Releasing the hold must not read as 「the pointer has been still for ages」: the popup was
        // where the pointer was, and it has only just gone away.
        if (!held) _lastActivity = now;

        return Settle(now);
    }

    /// <summary>
    /// Turns the loading hold on or off.
    /// <para>
    /// A latch, and it has to be one: this is called from every status snapshot mpv publishes — four or
    /// more times a second for the length of the film — and 「the hold is off, so that counts as activity」
    /// applied to each of them restamps the idle clock four times a second forever. Nothing can then ever
    /// go idle. Not the chrome: a pointer resting in the bottom fifth keeps the transport bar over the
    /// picture for the whole film, which is 「别什么进度条标题音量条都持久显示在画面上」. And not the cursor,
    /// which needs two uninterrupted seconds it was never allowed to accumulate —
    /// 「鼠标指针还是不会自动隐藏」. Only a real change of the hold is news; the countdown that follows one is
    /// still measured from the change, which is why the stamp stays here rather than going away.
    /// </para>
    /// </summary>
    public bool SetKeep(bool kept, long now)
    {
        if (_keepChrome == kept) return Settle(now);

        _keepChrome = kept;
        if (!kept) _lastActivity = now;
        return Settle(now);
    }

    /// <summary>
    /// Whether the mouse cursor should be hidden: the pointer is over the picture, has stopped, and
    /// there is nothing on screen to point at.
    /// <para>
    /// It waits for the pointer to actually stop — for <see cref="CursorIdleMilliseconds"/>, its own window,
    /// not the chrome's — rather than merely for the chrome to be down. The middle three fifths of the
    /// picture reveal nothing, so the chrome goes as soon as the pointer crosses into them, and a cursor
    /// that vanished there would disappear in the middle of a movement with nothing on screen to explain
    /// where it had gone.
    /// </para>
    /// </summary>
    public bool CursorHidden { get; private set; }

    /// <summary>
    /// True while something is still due to expire, so the caller knows to keep ticking. A pointer that is
    /// over the picture with the cursor still showing counts: its hide is due even when every piece of
    /// chrome is already down.
    /// </summary>
    public bool Pending(long now) =>
        !HoldChrome && !KeepChrome
        && (State.Any || now < _forceUntil || now < _railUntil || (_pointerY >= 0 && !CursorHidden));

    /// <summary>
    /// The pointer is over the picture at <paramref name="y"/> of <paramref name="height"/>, on
    /// <paramref name="part"/>, and <paramref name="railNear"/> says how deep into the rail's approach
    /// strip it is — -1 for 「not in it」, 0 at the strip's inner boundary, 1 hard against the right edge.
    /// <para>
    /// The strip is asked about separately from the hit test rather than being one of its answers. A hit
    /// test has to pick a single winner, and the skip-intro button and the top strip both overlap the
    /// right edge — while the hit test was the only source of truth a pointer there resolved to Skip or
    /// Title and the rail silently refused to come up:
    /// 「音量条判定有问题，我鼠标移到窗口右边有时候不会显示」. Reveal and grab are different questions, so
    /// they get different tests.
    /// </para>
    /// <para>
    /// Depth rather than a yes: the same reading answers 「should the rail be up」 and 「how strongly」, and
    /// two numbers that had to agree with each other would be one more thing to keep in step.
    /// </para>
    /// </summary>
    /// <returns>
    /// True when <see cref="State"/>, <see cref="RailStrength"/> or <see cref="CursorHidden"/> changed.
    /// </returns>
    public bool Pointer(double y, double height, ChromePart part, double railNear, long now)
    {
        _pointerY = height > 0 ? Math.Clamp(y / height, 0, 1) : 0;
        _part = part;
        _railNear = railNear < 0 ? -1 : Math.Clamp(railNear, 0, 1);

        // Anything the pointer is resting on counts as activity even without movement, which is what
        // keeps a short idle window from pulling a control out from under a hand aiming at it.
        _lastActivity = now;

        // A deliberate move cancels a keyboard grace window: the pointer's own position is a better
        // answer than a countdown started before it arrived.
        _forceUntil = 0;

        return Settle(now);
    }

    /// <summary>
    /// The mouse moved and the caller cannot say where to. Nothing about what the pointer is over changes —
    /// only that it is not still.
    /// <para>
    /// It exists because the two halves of 「静止两秒」 were kept in different places and drifted apart. The
    /// OS poll that decides stillness records the moment it saw movement, then hands the new position on to
    /// be translated into the picture's own coordinates, and that translation has ways of failing that
    /// movement does not: a window with no size yet, a position that will not convert. When it failed, the
    /// poll's clock had moved on and this one had not, so the rule measured its two seconds from a stamp it
    /// never received and hid the cursor out from under a hand that was moving the mouse — 「静止 156ms」 in
    /// the log, from a rule whose window is two thousand.
    /// </para>
    /// </summary>
    /// <returns>True when <see cref="State"/> or <see cref="CursorHidden"/> changed.</returns>
    public bool Moved(long now)
    {
        _lastActivity = now;
        return Settle(now);
    }

    /// <summary>
    /// The pointer left the picture — onto the caption, another window, or another monitor. It is asking
    /// for nothing, so the idle countdown runs from here rather than the chrome staying up forever.
    /// </summary>
    public bool PointerLeft(long now)
    {
        _pointerY = -1;
        _part = ChromePart.None;
        _railNear = -1;
        return Settle(now);
    }

    /// <summary>
    /// Activity with nothing to point at: shows all of the chrome for the length of the grace window
    /// wherever the pointer happens to be. Used for keyboard-driven playback commands, which otherwise
    /// get no feedback at all if the pointer is resting in the middle of the picture.
    /// </summary>
    public bool WakeFully(long now)
    {
        _lastActivity = now;
        _forceUntil = now + GraceMilliseconds;
        return Settle(now);
    }

    /// <summary>
    /// Shows the volume rail for the length of the grace window without touching the other two. This is
    /// how the wheel and the volume keys get a readout: neither has a pointer on the rail, and the wheel
    /// works anywhere over the picture.
    /// </summary>
    public bool FlashRail(long now)
    {
        _lastActivity = now;
        _railUntil = now + GraceMilliseconds;
        return Settle(now);
    }

    /// <summary>One expiry check. Cheap enough to run on a timer and idempotent between changes.</summary>
    public bool Tick(long now) => Settle(now);

    /// <summary>
    /// Puts the chrome back the way it looks at the start of a playback. Called on each new file, because
    /// the pointer may be anywhere and the state left behind belongs to the file that just ended.
    /// </summary>
    public void Reset(long now)
    {
        _lastActivity = now;
        _forceUntil = now + GraceMilliseconds;
        _railUntil = 0;
        _pointerY = -1;
        _part = ChromePart.None;
        _railNear = -1;
        State = new ChromeState(true, true, true);
        RailStrength = 1;
        CursorHidden = false;
    }

    /// <summary>Applies the rule to the recorded pointer state and reports whether anything moved.</summary>
    private bool Settle(long now)
    {
        var next = Decide(now);
        var strength = next.Rail ? Loudness(now) : 0;

        // The cursor is a process-wide resource, so it only ever hides while the pointer is genuinely
        // over this picture with nothing on screen to aim at — and on its own, longer window, so that
        // crossing the dead zone takes the chrome away without the cursor going with it.
        var hide = !next.Any
            && _pointerY >= 0
            && !HoldChrome
            && !KeepChrome
            && now - _lastActivity >= CursorIdleMilliseconds;

        // The strength is quantised at the source rather than compared with a tolerance here, so that
        // 「did anything change」 stays an equality — a pointer sliding along the right edge moves it in
        // hundredths, and every hundredth is a repaint the page has asked to hear about.
        if (next == State && strength == RailStrength && hide == CursorHidden) return false;

        State = next;
        RailStrength = strength;
        CursorHidden = hide;
        return true;
    }

    /// <summary>
    /// Whether the pointer's last known position is on a control, or in the rail's approach strip. Such a
    /// pointer counts as activity even though no event is arriving: the WinForms rule got that from its
    /// 40 ms poll, which refreshed the idle clock on every tick the pointer was on chrome, and with real
    /// pointer events a still pointer produces nothing at all, so the same promise has to be stated rather
    /// than inferred. Without it a hand hesitating over a button for two thirds of a second has the button
    /// vanish from under it.
    /// </summary>
    private bool Parked => _part != ChromePart.None || _railNear >= 0;

    /// <summary>
    /// How long the recorded pointer has to stay untouched before the chrome goes. Being parked buys
    /// patience, not immunity — see <see cref="ParkedIdleMilliseconds"/>.
    /// </summary>
    private long IdleWindow => Parked ? ParkedIdleMilliseconds : IdleMilliseconds;

    private ChromeState Decide(long now)
    {
        // A flyout is open, a drag is in progress, or the file is still loading: the controls are the way
        // out of that state, so nothing about the pointer may take them away.
        if (HoldChrome || KeepChrome || now < _forceUntil) return new ChromeState(true, true, true);

        if (now - _lastActivity >= IdleWindow)
        {
            // Nothing but the rail's own grace window survives the pointer going still.
            return new ChromeState(false, false, now < _railUntil);
        }

        var (bar, title) = Edges();

        // A pointer already on a control means the user arrived, whatever the bands say — the rail in
        // particular stands clear of the bottom fifth.
        if (_part == ChromePart.Bar) bar = true;
        if (_part == ChromePart.Title) title = true;

        // 「显示进度条的时候不需要同步显示音量条」: the bar used to be one of the rail's reasons, and it was
        // the wrong kind of reason — the pointer being in the bottom fifth is a request for the transport
        // bar and says nothing at all about the volume.
        return new ChromeState(bar, title, _railNear >= 0 || _part == ChromePart.Volume || now < _railUntil);
    }

    /// <summary>
    /// How strongly to draw a rail that is up. Full for every reason that is not the pointer walking into
    /// the strip, because those are readouts; graded on the pointer's own two distances otherwise.
    /// </summary>
    private double Loudness(long now)
    {
        if (HoldChrome || KeepChrome || now < _forceUntil || now < _railUntil) return 1;

        // A hand on the slider is aiming, not approaching. So is one that has left the strip while the
        // rail is up for some other reason — there is no distance to grade it by.
        if (_part == ChromePart.Volume || _railNear < 0) return 1;

        // Hundredths: fine enough that a fade chasing it looks continuous, coarse enough that a mouse
        // rattling on a desk does not repaint.
        return Math.Round(RailFloor + (1 - RailFloor) * _railNear * Centred, 2);
    }

    /// <summary>
    /// How near the pointer is to the picture's vertical middle — 1 at the centre, 0 at the top and
    /// bottom edges. The 「中心」 half of 「越接近右边的中心显示越明显」: the corners of the right edge are
    /// where a pointer on its way to the transport bar or the window buttons passes through, so the rail
    /// stays at its floor there and only comes up properly for a pointer that is actually heading for it.
    /// </summary>
    private double Centred => _pointerY < 0 ? 0 : 1 - Math.Abs(_pointerY - 0.5) * 2;

    /// <summary>
    /// Which of the two edge overlays the pointer is asking for. The bands used to be half the height
    /// each, so any movement at all was already bringing chrome up over the picture.
    /// </summary>
    private (bool Bar, bool Title) Edges()
    {
        if (_pointerY < 0) return (false, false);

        var band = 1d / Bands;
        return (_pointerY >= 1 - band, _pointerY <= band);
    }
}
