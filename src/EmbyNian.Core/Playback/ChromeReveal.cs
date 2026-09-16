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
    /// </summary>
    public static bool Travelled(double dx, double dy) =>
        (dx > 0 || dy > 0) && (dx >= MovePixels || dy >= MovePixels);

    /// <summary>
    /// 藏匿期「一次性跳变」的挂起窗 —— 一次够到阈值、随后就冻住不动的位移，不是手。
    /// <para>
    /// <b>这条规矩解决的是第十一报：外屏进程把光标整块搬走。一台机器上装了 AyuGram（Telegram 的 Qt 分支）
    /// 在第二块屏上，每次收到静音的群聊消息（没有弹窗、没有焦点变化）都会把指针横向搬整整 60 像素，
    /// 一秒不到就把藏了不到两秒的光标叫回来。</b>判据不是推断出来的，是从四天日志里数出来的：同样的
    /// <c>dx=60, dy=0</c> 在 09-15 出现 64 次、09-14 出现 8 次、09-12 出现 6 次，其余签名全是个位数的一次性
    /// 事件（那些才是手）。藏点 <c>3100,932</c>、唤醒读数 <c>3160,932</c>——永远同一个 60。
    /// </para>
    /// <para>
    /// <b>为什么第九报的负计数锁挡不住它。</b>那道锁管的是<b>箭头画不画出来</b>（队列私有的显示计数），
    /// 而这条唤醒走的是另一个门：单传感器读的是 <c>GetCursorPos</c> 的<b>位置</b>，注入位移让位置真的
    /// 变了，<see cref="Travelled"/> 如实判「动了」——锁在它前面，够不着它。所以这不是把哪条杠杆再加一格
    /// 的问题，是传感器本身要能分清「手在走」和「别人把指针搬了一下」。
    /// </para>
    /// <para>
    /// <b>怎么分。</b>手在鼠标上是一个<b>过程</b>——指针连续地走，两拍之间总有新的位移；而注入是一次
    /// <b>事件</b>——搬完就没了，下一拍读到的位置与搬完那一刻<b>严格相等</b>。所以藏匿期的第一个够阈值
    /// 的位移先挂起一拍：这一拍不动静止时钟、不显示光标；下一拍若又变了（<see cref="Travelled"/> 又成立，
    /// 或只是位置与挂起值不同），那是手，照旧唤醒；若读数与挂起点一字不差，就是一次纯跳变，把它当成
    /// 新的藏点接着藏。代价是一个 100 毫秒的拍子——手真的在动的时候下一拍立刻确认，肉眼看不见。
    /// </para>
    /// <para>
    /// <b>为什么这个代价可以接受、而反过来不行。</b>把一次真手的一步误判成跳变的后果，是光标多藏
    /// 一百毫秒（而且若手真的只动一步就停，那本来就该继续藏——静止两秒才藏正是本来的规矩）；把一次
    /// 注入误判成手的后果，是光标在用户看片子时自己冒出来，也就是这一报本身。两个方向的代价不对称，
    /// 所以往「先怀疑」那边偏。
    /// </para>
    /// </summary>
    public const long WarpConfirmMilliseconds = 100;

    /// <summary>
    /// 藏匿期被挂起、等下一拍裁决的那次位移的落点。空（<see cref="_warpPending"/> 假）表示没有待裁决的
    /// 跳变。见 <see cref="WarpConfirmMilliseconds"/> 的类注释：这是我们自己记的第二个位置，不是第二个
    /// 传感器——它只是「同一个传感器上一拍说了什么」。
    /// </summary>
    private NativePointLike _warpAt;

    private bool _warpPending;

    private long _warpSeenAt;

    /// <summary>
    /// 上一拍刚裁决完一次纯跳变。手在鼠标上是一个<b>过程</b>：跳变之后再来的那一次位移不可能是又一次
    /// 同步注入（注入的形状就是「搬完就冻住」），所以它只能是手，直接放行，不再二次挂起。
    /// <para>
    /// 没有这个标记的时候，每次够阈值的位移都要重新挂起一拍，于是「手连着走两拍」被读成「跳变 + 又一次
    /// 跳变」，光标永远轮不到唤醒——自检里那条「真手连着走两拍要能叫醒光标」正是这么红的。挂起是<b>每段
    /// 安静之后的第一记</b>位移的入门手续，不是每一拍都要重来的仪式。
    /// </para>
    /// <para>
    /// <b>它只在 <see cref="WarpHandMilliseconds"/> 之内作数，这是第十五报补上的一条</b>。此前它是个没有
    /// 期限的闩：一次注入被裁决掉之后，闩就一直立着，而藏匿期里没有任何东西会去动它（<see cref="ClearWarp"/>
    /// 只在光标显示时和换片时跑），于是<b>同一次藏匿里第二条及以后的注入全部免检放行</b>——第一条消息的
    /// 60 像素被挡住，第二条就把光标叫了回来。用户的原话是「每次收到静音的群聊消息都会让鼠标显示」，那正是
    /// 「只挡得住第一条」的听感。免检的语义是「这只手还在刚才那一记上继续走」，而手继续走是紧接着的下一拍
    /// （十赫兹下约一百毫秒），不是若干秒之后。
    /// </para>
    /// </summary>
    private bool _warpJustCleared;

    /// <summary>上一拍那次免检是什么时候立起来的，给它一个期限用。见 <see cref="WarpJustClearedFor"/>。</summary>
    private long _warpClearedAt;

    /// <summary>
    /// 免检的期限：从判掉一次跳变算起，多长时间之内再来的位移才可能是「同一只手还在走」。
    /// <para>
    /// <b>两百毫秒，也就是两个拍子。</b>轮询是十赫兹，手在鼠标上连续走动时相邻两个够阈值的读数相隔约
    /// 一百毫秒；留两拍是给调度抖动和自检那条一百二十毫秒间隔的「连走两拍」留余量。再长就开始放进真正的
    /// 第二次注入了——AyuGram 两条消息之间是秒级，不是百毫秒级。判错的方向仍然偏向藏：一只真手若是慢到
    /// 超过两拍才走第二记，那一记照旧走「先挂起」的手续，下一拍就认成手（见 <see cref="WarpOrHand"/>），
    /// 代价只是多藏一百毫秒。
    /// </para>
    /// </summary>
    public const long WarpHandMilliseconds = 200;

    /// <summary>
    /// 藏匿期的一记够阈值位移出现时，向真实输入见证问「最近有没有真手的移动」的窗口
    /// （第十六报，2026-09-16）。见证（<c>RealInputWitness</c>，在 Shell）说「有」就是手，说「没有」
    /// 就是注入 —— 与形状无关。
    /// <para>
    /// 300 的账：轮询一拍 100ms，手停下到十赫兹的轮询看见最后那记位移，最多隔两拍（200ms 出头），
    /// 窗口必须盖住它，不然手停下后的收尾一记会被冤成注入、光标该醒不醒；往宽了也不能太宽，不然
    /// 注入落地的一瞬恰好还记着一记久远的真移动，注入会被冤放。300 在两边都站得住 —— 手移动的
    /// 时候 WM_INPUT 一秒上百条，窗口里全是见证；注入（SetCursorPos）根本不产生见证，注入
    /// （SendInput）产生的没有 hDevice。
    /// </para>
    /// </summary>
    public const long WarpWitnessMilliseconds = 300;

    /// <summary>
    /// 免检还作不作数：立起来过，而且还在 <see cref="WarpHandMilliseconds"/> 之内。过期就地作废——只判一次，
    /// 因为下一次够阈值的位移该重新过手续（那可能是下一条消息的注入）。
    /// </summary>
    private bool WarpJustClearedFor(long now)
    {
        if (!_warpJustCleared) return false;

        if (now - _warpClearedAt <= WarpHandMilliseconds)
        {
            _warpJustCleared = false;
            return true;
        }

        // 过期：闩自己收走，这一记（以及之后每一记）都按常态先挂起。不清的话，第一次判掉跳变之后的
        // 任意一记位移都会被它放行——那正是这一报要修的东西。
        _warpJustCleared = false;
        return false;
    }

    /// <summary>
    /// 藏匿期被认出来、并据以继续藏下去的一次性跳变，累计多少次。给日志和自检读：这个数不涨，
    /// 就说明外屏那条路要么没在动、要么这次修法根本没生效。
    /// </summary>
    public int WarpsIgnored { get; private set; }

    /// <summary>
    /// 一个只有两个整数的最小位置类型，免得 Core 依赖 Shell 的 <c>NativePoint</c>。Core 里不该知道 Win32。
    /// </summary>
    public readonly record struct NativePointLike(int X, int Y);

    /// <summary>
    /// 藏匿期收到一个够到 <see cref="MovePixels"/> 的位移时该怎么做——把「手」和「别人的一次注入」
    /// 分开的那一步。返回真表示「这是手，按移动处理」；返回假表示「先挂起」或「确认是注入，别理它」。
    /// <para>
    /// 调用方（<c>PlayerPage.PollPointer</c>）在光标<b>没有藏</b>时不该问这个：显示期任何位移都是手，
    /// 没有要保住的东西，多一拍怀疑只是让控件迟钝。所以这个方法只在藏匿期有意义，这一点写在名字上。
    /// </para>
    /// <para>
    /// 三个出口，一次只走一个：<b>第一次够阈值的位移</b>挂起（返回假、<see cref="_warpPending"/> 立起）；
    /// <b>下一拍离落点又走够了 <see cref="MovePixels"/></b>确认是手（返回真、挂起清掉）；<b>下一拍仍在落点
    /// 附近</b>（一两像素的桌面抖动也算「原地」）确认是注入（返回假、<see cref="WarpsIgnored"/> 加一、挂起清掉，
    /// 调用方应把参照点推进到挂起点）。
    /// </para>
    /// <para>
    /// <b>第十五报的两处修正都在这三个出口里。</b>其一，「走开」判的是够不够 <see cref="MovePixels"/> 而不是
    /// 「坐标一个字节不差」——桌面抖动会让严格不等号把注入误读成手。其二，判掉一跳之后立起的免检闩有期限
    /// （<see cref="WarpHandMilliseconds"/>），好让它挡得住第二条、第三条消息，而不是只挡得住第一条。
    /// </para>
    /// </summary>
    /// <param name="x">本拍读到的屏幕绝对坐标。</param>
    /// <param name="y">同上。</param>
    /// <param name="now">这一拍的时钟，与规则其它地方同一个。</param>
    public bool WarpOrHand(int x, int y, long now)
    {
        // 上一拍刚判掉一次纯跳变，而且还在免检期内：紧接着又来一次位移。同步注入的形状是「搬完就冻住」，
        // 它不会在下一拍再搬一次；所以这一记只能是手在走。直接认，不二次挂起——否则手走两拍会被读成两次
        // 跳变，永远叫不醒光标。
        //
        // 免检有期限（第十五报）：立起它的那条注入被挡下之后，闩不能一直立着，否则同一次藏匿里第二条
        // 消息的注入照样免检通过——那就是「每次收到消息光标都冒出来」。
        if (WarpJustClearedFor(now)) return true;

        // 已经挂着一次待裁决的位移：看这一拍与挂起点的关系。
        if (_warpPending)
        {
            // 位置又走开了 —— 手在走。挂起清掉，交回给调用方当移动处理。
            //
            // 「走开」够的是同一个 <see cref="MovePixels"/>，不是「一个像素都不一样」（第十五报）：挂起点是
            // 别人搬到的落点，而我们脚下这台机器的桌面抖动实测能到两像素，一次注入落地之后紧接着的一两像素
            // 抖动，用严格不等号读就是「手在走」，于是第二条消息还没到、光标已经被抖醒了。手的第一记是几十
            // 像素，五这个数在两边都站得住。
            if (ChromeReveal.Travelled(Math.Abs(x - _warpAt.X), Math.Abs(y - _warpAt.Y)))
            {
                _warpPending = false;
                return true;
            }

            // 还在落点附近（含一两像素的抖动）、而且已经过了一拍：一次纯跳变。继续藏，并记住「刚判过一跳」。
            // 注意判的是「离落点」而不是「离上一拍」：抖动不推进落点，所以它既不会攒成一记假的手，也不会
            // 把一次真手拆散丢掉。
            if (now - _warpSeenAt >= WarpConfirmMilliseconds)
            {
                _warpPending = false;
                _warpJustCleared = true;
                _warpClearedAt = now;
                WarpsIgnored++;
                return false;
            }

            // 同一拍内又被问了一次（或还没到确认时间）：保持挂起，别提前放行。
            return false;
        }

        // 第一次够阈值的位移：挂起，等下一拍。
        _warpAt = new NativePointLike(x, y);
        _warpPending = true;
        _warpSeenAt = now;
        return false;
    }

    /// <summary>
    /// 藏匿期被挂起、等下一拍确认的跳变落点，或 null。给调用方在「这一拍位置与上一拍相同」时要问的那
    /// 一句：如果相同的位置<b>正是</b>挂起点，那这拍不是「同位置」而是「跳变确认完」。
    /// </summary>
    public NativePointLike? WarpPendingAt => _warpPending ? _warpAt : null;

    /// <summary>
    /// 光标从「藏」变回「显」时把挂起清掉：藏匿期结束，没有待裁决的东西了。<see cref="Reset"/> 也调它。
    /// </summary>
    private void ClearWarp()
    {
        _warpPending = false;
        _warpSeenAt = 0;
        _warpJustCleared = false;
        _warpClearedAt = 0;
    }

    /// <summary>
    /// 藏匿期一记够阈值的位移，由<b>真实输入见证</b>裁决（第十六报，2026-09-16）。
    /// <para>
    /// <see cref="WarpOrHand"/> 是形状的裁决者：它拿到的唯一事实是坐标，而十六报的日志证明了形状
    /// 的极限 —— AyuGram 的注入是一段动画（四秒五步、每步十几到几十像素），每一步单独看都与「手
    /// 走了一拍」无法区分，第十五报的期限与「离落点走开」在它面前全都失守。这一条是形状之外的
    /// 裁决：Shell 的 <c>RealInputWitness</c> 从 WM_INPUT 里看见了什么，原样递进来 —— 有 hDevice
    /// 的移动是手，没有是注入。判据一句话，裁决就一句话。
    /// </para>
    /// <para>
    /// 两个出口。真（<paramref name="realInputSeen"/>）：交回给调用方当移动处理 —— 挂起与免检闩都
    /// 清掉，见证模式下它们不该再立起来。假：确认是注入，<see cref="WarpsIgnored"/> 记账、挂起清掉，
    /// 调用方把参照点推进到落点 —— 与 <see cref="WarpOrHand"/> 冻结确认那一支同样的交接，只是判定
    /// 换了证人。<b>注入不立免检闩</b>：动画注入一步一判，每一步都是独立的「没有见证」，上一步的
    /// 判决不能让下一步免检 —— 那正是十五报挡不住第二条消息的老路。
    /// </para>
    /// <para>
    /// 见证缺席（注册失败、解析不出来）时不走这一条：调用方（<c>PollPointer</c>）看到 <c>Ready</c>
    /// 是假就退回 <see cref="WarpOrHand"/>。这一条与那条永远不混用 —— 一次裁决两个证人会互相污染。
    /// </para>
    /// </summary>
    /// <param name="realInputSeen">见证说这记位移出现前后有没有真手的移动。</param>
    /// <param name="now">这一拍的时钟，与规则其它地方同一个。留着签名上的对称：裁决都收时钟。</param>
    /// <returns>真＝手，按移动处理；假＝注入，接着藏。</returns>
    public bool HideMoveVerdict(bool realInputSeen, long now)
    {
        if (!realInputSeen)
        {
            WarpsIgnored++;
            ClearWarp();
            return false;
        }

        ClearWarp();
        return true;
    }

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

        // 新片子开场，上一个文件藏匿期里挂着的跳变与它数到的次数都归零：那两个数是「这一段藏匿」的账，
        // 跨文件延续会把日志读成「这一报修了以后还在犯」。
        ClearWarp();
        WarpsIgnored = 0;
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

        // 光标一旦回到「显」，藏匿期那一套挂起就没有意义了。留一口气不清会让下一次藏匿的第一个位移
        // 直接被当成「下一拍」而误判 —— 藏匿期的账只在藏匿期里算。
        if (!hide) ClearWarp();

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
