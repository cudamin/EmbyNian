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
/// screen while the pointer is in the bottom edge band of the picture, the top strip while it is in the
/// top one, and a moment of stillness takes both away. The rest of the height is therefore a dead zone
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
    /// How long the pointer has to hold still before the mouse cursor itself goes.
    /// <para>
    /// <b>1000，mpv.net 的默认（2026-09-16 用户拍板「完全照搬 mpv.net」）。</b>mpv 的
    /// <c>cursor-autohide</c> 不另设时就是这个数，mpv.net 的 <c>_cursorAutohide = 1000</c> 原样照搬。
    /// 之前是 2000（「全屏播放且鼠标在画面上时，鼠标静止不动两秒之后要自动隐藏」），那句话被今天
    /// 这条更晚的指令接替；停靠在控件上的指针不受影响——它等的是 chrome 收起，而 chrome 的停靠
    /// 耐心（<see cref="ParkedIdleMilliseconds"/>，2000）没动，光标随 chrome 在两秒那拍一起走。
    /// </para>
    /// <para>
    /// 与 chrome 的窗口分开问、分开等的结构照旧：chrome 是请求（650ms 的死区静止就是「不要了」），
    /// 光标是指针本身（拿走它要等一个「鼠标已经放下」的静止），两个数只是都变小了。
    /// </para>
    /// </summary>
    public const long CursorIdleMilliseconds = 1000;

    /// <summary>
    /// How long a readout with no pointer behind it stays up — the volume rail after a wheel or key
    /// change, the whole chrome after a keyboard command. Longer than the pointer's idle window because
    /// there is nothing to move: the number has to be readable before it goes.
    /// </summary>
    public const long GraceMilliseconds = 1200;

    /// <summary>
    /// How far the pointer has to have moved, between two readings of the OS, before that counts as somebody
    /// moving the mouse.
    /// <para>
    /// <b>It is compared against the same reading every time, and that reading is the one that matters.</b>
    /// The single sensor this rule is driven by is a ten-hertz <c>GetCursorPos</c> poll — see
    /// <c>PlayerPage.PollPointer</c> for why the XAML event channel was retired as a source of truth. A polled
    /// position is absolute: two calls an hour apart differ by exactly however far the pointer physically
    /// travelled, with no accumulation, no anchor to walk along and no re-derived coordinate base, so the
    /// threshold is a step size and nothing else needs to be remembered.
    /// </para>
    /// <para>
    /// <b>Five, and it comes from the mature players.</b> mpv ignores an event whose coordinates are
    /// <em>exactly</em> equal (<c>input/input.c:914</c>) and its <c>w32_common.c</c> carries a whole comment
    /// about Windows sending spurious mouse events; MPC-HC documents a ±1 pixel tolerance
    /// (<c>PointEqualsImprecise</c>); mpv.net scales by DPI at <c>5 * dpi/96</c>, which is exactly five at
    /// 96 DPI. Five is that number. This machine's own log is what fixes the lower bound: a mouse lying
    /// untouched on a desk reported steps of <c>2,0</c>, <c>0,2</c> and <c>1,2</c> logical pixels during hides
    /// nobody was touching, so anything at or under two would read the desk as a hand.
    /// </para>
    /// <para>
    /// <b>The desk's whole vocabulary, not one step.</b> A poll reports the sensor's true position, so it sees
    /// every rattle the event stream would never bother to announce — up to two pixels on this machine. Five
    /// clears that by a wide margin, and sits far below a hand, whose first movement is tens of pixels. The
    /// safe direction is the one that matters: a desk pixel let through costs the bug being reported again; a
    /// hand pixel swallowed costs a wiggle of the wrist to fix.
    /// </para>
    /// </summary>
    public const double MovePixels = 5;

    /// <summary>
    /// Whether a reported position change of <paramref name="dx"/>,<paramref name="dy"/> is somebody moving the
    /// mouse rather than noise — see <see cref="MovePixels"/> for what is at stake and why the threshold is
    /// where it is. It is extracted rather than inlined because a test can then pin the one number the whole
    /// hide rests on, instead of two comments having to agree with each other.
    /// <para>
    /// <b>2026-09-17 起它只剩 XAML 事件路（<c>PlayerPage.Input.Moved</c>）这一个客户</b>：参数是这次
    /// 事件位置离上次<b>接受</b>的位置的绝对差（锚点只在接受时推进），事件信道拿它过滤 WinUI 为没动过
    /// 的指针抬的空事件。轮询那一侧（<c>PlayerPage.PollPointer</c>）的两个状态都改问
    /// <see cref="HandStep"/> 了——「显示态量每拍速度」的旧分工随参照点统一一并退役。
    /// </para>
    /// </summary>
    public static bool Travelled(double dx, double dy) =>
        (dx > 0 || dy > 0) && (dx >= MovePixels || dy >= MovePixels);

    /// <summary>
    /// 一记位移算不算「有人动了鼠标」—— <b>mpv.net 的原样判据</b>（2026-09-16 用户拍板
    /// 「完全照搬 mpv.net」）：位置离<b>上次记录点</b>的切比雪夫距离超过 <see cref="MovePixels"/> 就是。
    /// 对应 mpv.net <c>MainForm.IsCursorPosDifferent</c> 的那一问（阈值 5×dpi/96 的切比雪夫；
    /// 本项目轮询读的是物理像素，常数 5 就按它 96 DPI 下的值用）。
    /// <para>
    /// <b>2026-09-17 起它是轮询两个状态的唯一一问</b>（参考 dyphire/mpv-config 的播放光标语义统一：
    /// mpv 的 <c>cursor-autohide</c> 里「坐标变了的输入就是活动」，移动着的手不丢光标）。参照点是
    /// 冻着的，只在过线那一刻推进，于是一段每拍一两像素的慢移会对着同一个点累计，六拍之内必然
    /// 过线——显示态的慢手从此叫得回空闲钟，不再半路丢光标；藏匿期的「慢手也能叫回来」照旧。
    /// 桌面抖动两个状态都过不了线：它绕着停点打转，离开不上次过线点五像素。
    /// </para>
    /// <para>
    /// <b>接受的代价写明白：</b>参数带符号进来，往回走一记 60 像素也过线——十九报日志里那些带真设备
    /// 句柄的幽灵位移（最远 65 像素）从这里开始全部叫得醒光标。mpv、MPC-HC、VLC、Chromium、mpv.net
    /// 在同一段幽灵流面前全是这个行为，它们冒出来的只是一支一两秒后自己藏回去的箭头；本项目冒出来
    /// 的是整套控件，二十一次报上来的那个毛病因此以更轻的形式回归——这是用户在这两条路的岔口上
    /// 自己选的一边。真实输入见证（<c>RealInputWitness</c>）不再有裁决权，只留取证记账。
    /// </para>
    /// </summary>
    public static bool HandStep(double dx, double dy) =>
        Math.Abs(dx) > MovePixels || Math.Abs(dy) > MovePixels;

    /// <summary>
    /// 窗口此刻是不是前台 —— mpv.net 的 <c>ActiveForm == this</c> 那一问（2026-09-16 照搬）。
    /// <para>
    /// 假的时候 <see cref="Settle"/> 永远不藏：mpv.net 的 <c>CursorTimer_Tick</c> 把
    /// <c>ActiveForm == this</c> 列在藏匿条件里，窗口在别人手里时轮询照跑、藏匿不发生。外壳从
    /// <c>Window.Activated</c> 事件喂进来（<c>WindowActivationState.Deactivated</c> 为假，其余为真），
    /// 默认真——单测和「从未失焦」的窗口都按前台算。
    /// </para>
    /// <para>
    /// 失焦<b>还兼着显示</b>：mpv.net 的 <c>OnLostFocus → ShowCursor</c>。从真翻假的那一拍，
    /// <see cref="Settle"/> 的 hide 从真变假，下一次 Render 就把藏着的光标掀开——外壳的激活处理
    /// 里喂完这个位就推一拍，正是那条路的全部接线。
    /// </para>
    /// </summary>
    public bool WindowFocused { get; set; } = true;

    /// <summary>
    /// 上下两条边缘带各占画面高度的比例 —— 也就是「显示上方控件与下方进度条的触发阈值」。
    /// <para>
    /// <b>0.12，2026-09-15 由五分之一（0.20）改小</b>（用户的话：「将播放页面显示上方控件与下方进度条的
    /// 触发阈值调整为12%」）：带子窄了，唤出控件的手势要更明确地走到边上，压在画面中间的余地也更大 ——
    /// 死区从五分之三涨到 76%，字幕那一带更清净。命中到控件（<see cref="ChromePart.Bar"/>、
    /// <see cref="ChromePart.Title"/>）照旧无条件成立，跟带子多宽无关。
    /// </para>
    /// </summary>
    private const double EdgeBandFraction = 0.12;

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
    /// The rule's own record still says the pointer is outside the picture — <see cref="PointerLeft"/> ran
    /// and nothing has said "back" since. Read by the shell's tick to close the one loop both the events and
    /// the poll leave open: a hand that returns and stops within five pixels of where it left raises no
    /// pointer event (nothing moved), and the poll returns early both on a zero step and on one under the
    /// threshold, so nobody tells the rule the pointer came home — <see cref="_pointerY"/> stays at -1, and
    /// the settle rule, which hides the cursor only while the pointer is <em>in</em> the picture, never
    /// hides again. 「有时候需要点一下暂停再播放，鼠标才会开始自动隐藏」 was exactly this: the click brought
    /// the pointer's record home by hand, everything else had to wait for it.
    /// </summary>
    public bool PointerGone => _pointerY < 0;

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
    /// go idle. Not the chrome: a pointer resting in the bottom band keeps the transport bar over the
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
    /// not the chrome's — rather than merely for the chrome to be down. The dead zone between the two edge
    /// bands reveals nothing, so the chrome goes as soon as the pointer crosses into it, and a cursor
    /// that vanished there would disappear in the middle of a movement with nothing on screen to explain
    /// where it had gone.
    /// </para>
    /// <para>
    /// <b>「Stopped」 is decided by one clock and one sensor.</b> <see cref="Moved"/> and
    /// <see cref="Pointer(double, double, ChromePart, double, long, bool)"/> with <c>moved: true</c> are the
    /// only two things that restamp the idle clock; a report that is merely a position — the poll's own
    /// unchanged reading, a resize — is not a movement and leaves it alone. That split is what lets a
    /// pointer genuinely at rest expire, which is the whole of 「鼠标静止不动两秒之后要自动隐藏」.
    /// </para>
    /// </summary>
    public bool CursorHidden { get; private set; }

    /// <summary>
    /// 空闲钟走到了哪儿（毫秒）—— <b>只给外壳的诊断行读</b>：光标/控件该收不收的时候，这一格说清
    /// 是不是被谁不停重盖着（2026-09-16 晚加的，用户报「鼠标已经不会自动隐藏了」时手边唯一的读数）。
    /// 只读，不改任何状态。
    /// </summary>
    public long IdleAgo(long now) => now - _lastActivity;

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
        => Pointer(y, height, part, railNear, now, moved: true);

    /// <summary>
    /// The same reading with the caller's answer to the one question this class cannot work out for itself:
    /// is this a pointer that just moved, or the same pointer reported again?
    /// <para>
    /// Both arrive here as a position, and they want opposite things from the idle clock. A movement is
    /// activity by definition, so it restamps the clock and the countdown starts again from here — which is
    /// how 「鼠标静止不动两秒之后要自动隐藏」 is measured from the last thing a hand did. A position reported
    /// again is the opposite: it is what the ten-hertz poll and every resize hand over, they say nothing about
    /// a hand, and restamping for them is what 「鼠标隐藏了一会然后又会自动冒出来」 was made of — a cursor
    /// whose two seconds never get to expire, from a player whose own log says the pointer never moved.
    /// </para>
    /// <para>
    /// <paramref name="moved"/> false is therefore the honest answer for a report that is only a position.
    /// While the cursor is showing the distinction does no work — a still pointer produces no events, and the
    /// difference between the two readings is invisible until there is a hide to keep. It is the hidden half
    /// that needs it.
    /// </para>
    /// </summary>
    public bool Pointer(double y, double height, ChromePart part, double railNear, long now, bool moved)
    {
        _pointerY = height > 0 ? Math.Clamp(y / height, 0, 1) : 0;
        _part = part;
        _railNear = railNear < 0 ? -1 : Math.Clamp(railNear, 0, 1);

        // Anything the pointer is resting on counts as activity even without movement, which is what
        // keeps a short idle window from pulling a control out from under a hand aiming at it — and a report
        // the caller called a movement is activity whether or not it landed anywhere useful.
        if (moved || !CursorHidden) _lastActivity = now;

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
    /// poll that decides stillness records the moment it saw movement, then hands the new position on to
    /// be translated into the picture's own coordinates, and that translation has ways of failing that
    /// movement does not: a window with no size yet, a position that will not convert. When it failed, the
    /// poll's clock had moved on and this one had not, so the rule measured its two seconds from a stamp it
    /// never received and hid the cursor out from under a hand that was moving the mouse — 「静止 156ms」 in
    /// the log, from a rule whose window is two thousand.
    /// </para>
    /// </summary>
    /// <returns>True when <see cref="State"/> or <see cref="CursorHidden"/> changed.</returns>
    /// <remarks>
    /// 真手这一记同时是双击纯净闸的解锁键（2026-09-18）：轮询的 HandStep 过线是唯一一条
    /// 「与位置无关的手」证据，所以只有它有权解开 <see cref="Silence"/>；位置重报
    /// （<see cref="Pointer"/> 的 moved 与否、resize 的 <c>OnRootResized</c>）都无权。
    /// </remarks>
    public bool Moved(long now)
    {
        _silenced = false;
        _lastActivity = now;
        return Settle(now);
    }

    /// <summary>
    /// The pointer left the picture — onto the caption, another window, or another monitor. It is asking
    /// for nothing, so the idle countdown runs from here rather than the chrome staying up forever.
    /// <para>
    /// <b>藏匿期这一记不该自己就是一次显示</b>（2026-09-16 第二轮对抗复核点出来的漏路，判 fatal）。
    /// 清掉 <c>_pointerY</c> 会让 <see cref="Settle"/> 的藏匿条件 <c>_pointerY &gt;= 0</c> 当场失效，
    /// 于是「指针读数落到画面外」以<b>零路程</b>掀掉藏匿 —— 累加器根本不在前面站着，它两行之后就被
    /// 推翻。而这条路正是报上来的那个几何：AyuGram 在第二块屏上，幽灵把指针往那边搬就是往画面边缘外
    /// 搬；窗口化播放时客户区很小，一记六十像素的横跳随手就出界。
    /// </para>
    /// <para>
    /// <paramref name="keepHidden"/> 为真时保留 <c>_pointerY</c>，只清控件与音量条那一半，藏匿继续。
    /// 这不违反「光标是进程级资源」那条安全规矩：藏匿只对本进程线程队列里那些窗口生效，指针一落到别的
    /// 进程的窗口上，那个进程自己会 <c>SetCursor</c>，用户看得见箭头。指针回到「显」的任何一路，照旧
    /// 走完整的 <see cref="PointerLeft"/> 语义（默认参数就是它）。
    /// </para>
    /// </summary>
    /// <param name="keepHidden">指针已经在藏匿中，且这是「读数出了画面」而不是「手把指针挪出去了」：
    /// 只报位置，不许醒来。</param>
    public bool PointerLeft(long now, bool keepHidden = false)
    {
        if (!keepHidden) _pointerY = -1;
        _part = ChromePart.None;
        _railNear = -1;
        return Settle(now);
    }

    /// <summary>
    /// 记录里的指针是否正压在<b>真控件</b>上（不是「落在右边缘那条看不见的带子里」）。给二十报的幽灵
    /// 回笼读：那一问只该认「指针停在真控件上」，认几何带子是错的 —— 第二块屏就在右边，幽灵把指针往
    /// 那边搬，落点恰好落进最右那条 160 逻辑像素的带子，回笼于是整段藏匿里一次都不会发生。
    /// </summary>
    public bool PointerOnControl => _part != ChromePart.None;

    /// <summary>
    /// 双击全屏／还原的「画面纯净」闸（2026-09-18，用户令「双击时不得呼出或显示任何 UI 控件」）。
    /// <para>
    /// <see cref="Silence"/> 把点画面那一路给的宽限（<see cref="WakeFully"/>）连同指针所在位置的显示理由
    /// 一并收掉 —— 控件全部离屏，指针哪怕停在边缘带里也不构成显示，直到「真手再动」才解锁。解锁只认四条路：
    /// 轮询 HandStep 过线走的 <see cref="Moved"/>（唯一的位置无关真相源）、刻意的 <see cref="WakeFully"/> /
    /// <see cref="FlashRail"/>、以及新一播放的 <see cref="Reset"/>。<b>窗口 resize 的 moved 重报不解闸</b> ——
    /// 进退全屏自己那一下就是一次 resize（<c>OnRootResized</c> → <c>Pointer(moved: true)</c>），解了闸控件
    /// 会在切换的同一拍被位置规则摆回来，正是这个闸要挡的事。
    /// </para>
    /// </summary>
    private bool _silenced;

    /// <summary>纯净闸此刻是否压着规则。诊断与页面探测用；规则内部只看 <see cref="_silenced"/>。</summary>
    public bool Silenced => _silenced;

    /// <summary>
    /// 收掉一切：宽限、音量条读数、位置的显示理由，控件全部离屏。双击全屏／还原在切换当拍调用它，
    /// <c>true</c> 表示屏上确实有东西被收走了（调用方要画一遍）。
    /// </summary>
    public bool Silence(long now)
    {
        _silenced = true;
        _forceUntil = 0;
        _railUntil = 0;
        _lastActivity = now;
        return Settle(now);
    }

    /// <summary>
    /// Activity with nothing to point at: shows all of the chrome for the length of the grace window
    /// wherever the pointer happens to be. Used for keyboard-driven playback commands, which otherwise
    /// get no feedback at all if the pointer is resting in the middle of the picture. A deliberate show,
    /// so it also unlocks the double-click silence latch.
    /// </summary>
    public bool WakeFully(long now)
    {
        _silenced = false;
        _lastActivity = now;
        _forceUntil = now + GraceMilliseconds;
        return Settle(now);
    }

    /// <summary>
    /// Shows the volume rail for the length of the grace window without touching the other two. This is
    /// how the wheel and the volume keys get a readout: neither has a pointer on the rail, and the wheel
    /// works anywhere over the picture. A deliberate show, so it also unlocks the double-click silence
    /// latch.
    /// </summary>
    public bool FlashRail(long now)
    {
        _silenced = false;
        _lastActivity = now;
        _railUntil = now + GraceMilliseconds;
        return Settle(now);
    }

    /// <summary>One expiry check. Cheap enough to run on a timer and idempotent between changes.</summary>
    public bool Tick(long now) => Settle(now);

    /// <summary>
    /// Puts the chrome back the way it looks at the start of a playback. Called on each new file, because
    /// the pointer may be anywhere and the state left behind belongs to the file that just ended. A new
    /// playback is its own world, so the double-click silence latch does not survive it.
    /// </summary>
    public void Reset(long now)
    {
        _silenced = false;
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
        //
        // WindowFocused 是 mpv.net 的 ActiveForm == this（2026-09-16 照搬）：窗口不在前台就不藏；
        // 这一位从真翻假的那一拍，本来藏着的 hide 也跟着变假 —— OnLostFocus → ShowCursor 那条路
        // 就是从这里走通的，外壳喂完这一位推一拍即可。
        var hide = !next.Any
            && _pointerY >= 0
            && !HoldChrome
            && !KeepChrome
            && WindowFocused
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

        // 双击全屏／还原的纯净闸（2026-09-18）：在真手再动（Moved/WakeFully/FlashRail/Reset 解锁）之前，
        // 点画面那一下给的宽限与指针所在的位置都不构成显示理由 —— 画面保持只有片子。
        if (_silenced) return new ChromeState(false, false, false);

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
        // the wrong kind of reason — the pointer being in the bottom band is a request for the transport
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

        return (_pointerY >= 1 - EdgeBandFraction, _pointerY <= EdgeBandFraction);
    }
}
