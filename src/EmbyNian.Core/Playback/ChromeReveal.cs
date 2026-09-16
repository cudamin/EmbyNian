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
    /// 藏匿期把光标叫回来，一只手要走够的<b>路程</b>：一段手势的总长，不是一记位移
    /// （第二十一报，2026-09-16）。这是藏匿期唯一的关，<see cref="HideMoveVerdict"/> 就是它的裁决。
    /// <para>
    /// <b>这条规矩解决的是从第十一报数到第二十报、同一个毛病报了二十一次的那件事。</b>第二块屏上的
    /// AyuGram（Telegram 的 Qt 分支）每收到一条静音的群聊消息（没有弹窗、没有焦点变化，而且用户把
    /// 鼠标<b>拔了</b>）就把指针挪一小段，藏了不到两秒的光标随即冒出来。十一到十五报按<b>形状</b>去
    /// 分辨（「注入搬完就冻住，手是个过程」），十六到十九报按<b>出处</b>去分辨（WM_INPUT 的
    /// <c>hDevice</c>）。两条路都到了尽头：十九报的日志里那段位移带着一个<b>真设备句柄</b>
    /// （<c>VID_1532&amp;PID_007C</c>），在输入层面与真手一字不差；而它的形状是一段平滑动画，
    /// 每一步都像手走了一拍。
    /// </para>
    /// <para>
    /// <b>它们剩下的唯一破绽是「走得不够远」。</b>日志里所有能归到这个幽灵头上的唤醒，位移全在
    /// 六十几像素以内就停住了：<c>60,0</c>、<c>40,10</c>、<c>42,15</c>、<c>37,33</c>、<c>30,7</c>、
    /// <c>28,20</c>、<c>10,2</c>；同一份日志里唯一一次确凿是手的唤醒是 <c>291,82</c>。一只想把光标
    /// 叫回来的手不会走五十像素就僵在那儿——它要去够暂停键、够进度条，一拍（100 毫秒）就是上百像素。
    /// 一百像素这个数正落在两者中间：比见过的每一段幽灵都宽出一半，而任何一次正常的手腕动作一两拍
    /// 之内就走完。
    /// </para>
    /// <para>
    /// <b>为什么这一条能成、而二十报的「回笼」不能。</b>二十报是<b>先显示、再改判</b>：显示之后
    /// 一秒二没有后续输入就把光标收回去。它判得对，可用户看见的正是那一秒二——「让鼠标显示」这句
    /// 抱怨，回笼修不掉。这一条是<b>先攒路程、够了才显示</b>：幽灵走的那五六十像素被攒进账里，
    /// 从头到尾没跨过一百，<b>光标一次都没出现过</b>。代价落在手上最多是一拍（100 毫秒），肉眼
    /// 看不见。回笼留着当后手，管那些真攒够了一百像素才停的东西。
    /// </para>
    /// <para>
    /// <b>「其他播放器和网页播放就不会出现这个bug」不是因为它们的传感器更严——第二十一报核过上游
    /// 源码，恰好相反。</b>mpv 的坎是 1 像素、MPC-HC 从藏匿点起算 2 像素、VLC 3 像素、Chromium 干脆
    /// 任何一条 <c>pointermove</c> 都算，而且一家都没有真实输入见证；同一段幽灵流多半也会把它们的
    /// 光标叫出来。差别在<b>后果的大小</b>：它们冒出来的是一支光秃秃的箭头、两秒后自己藏回去，
    /// 本项目冒出来的是整套控件，所以只有这边被当成 bug 报上来。连「轮询还是事件」这条也不是分界线
    /// ——mpv.net（真正有人在用的那个 C# 壳）同样轮询全局 <c>GetCursorPos</c>、同样用 5×dpi/96 的
    /// 切比雪夫阈值、参照点同样在不够阈值时冻着，与本项目改之前一模一样。
    /// </para>
    /// <para>
    /// 所以一百像素这道坎是<b>本项目自己量出来的</b>，不是照别人家抄的：上面那串幽灵签名最远走到 65，
    /// 唯一确凿的真手是 291,82。参照点那一侧的毛病（不够阈值时冻着，慢漂会一直攒）在第二十一报单独
    /// 改掉了——<c>PollPointer</c> 现在每拍都推进参照点，一拍读到的就是这一拍的速度；<b>攒</b>的活
    /// 全部收到这里来，而且只攒<b>连着在走</b>的那一段。
    /// </para>
    /// <para>
    /// <b>同一天的第二轮对抗复核把「怎么攒」也钉死了三条，写在 <see cref="HideMoveVerdict"/> 里。</b>
    /// 复核用的就是本项目自己的样本：十六报那段动画五步的切比雪夫步长是 14+42+10+20 = 86，<b>步与步
    /// 只隔一两百毫秒</b>，而原来那个 250 毫秒的断点比步距还宽，于是整段动画被当成<b>一段连续手势</b>
    /// 串起来，八十六对一百只剩一成四的裕度；两条消息前后脚到（用户的原话是「每次收到静音的群消息」
    /// ——群聊是连着来的）还能相加成一百七十二。三条修补是：账记<b>净位移</b>不是步长之和（+40 接
    /// −40 是一动没动，不是走了八十）；一记不够格的拍子（含整拍没动的那一拍）<b>当场把账归零</b>
    /// ——幽灵与真手之间唯一不随消息长短变化的结构差别就在这里，真手每一拍都在走，幽灵在两步之间
    /// 整拍冻住；再加一道 <see cref="WakeStepsNeeded"/> 的连续拍数下限，任何单拍跳变都出局。
    /// </para>
    /// </summary>
    public const double WakeTravelPixels = 100;

    /// <summary>
    /// 判成手，除了净位移够 <see cref="WakeTravelPixels"/>，还要<b>连着这么多拍每拍都够</b>
    /// <see cref="MovePixels"/>（2026-09-16 第二轮对抗复核加的下限）。
    /// <para>
    /// 只有总路程一道关时，一记 <b>一格跳到底</b> 的注入可以一击穿关：日志里 AyuGram 的签名就是
    /// 「一拍之内横跳整整 60 像素、纵向恒 0」，幅度翻一倍就是一百二十，单拍过线。真手不可能只走一拍
    /// 就停——手落在鼠标上是个<b>过程</b>，十赫兹的轮询下一划就是连着好几拍。三拍（三百毫秒）是
    /// 手和「一格跳变」之间最短的那道分界，代价是手要多走半拍，肉眼看不见。
    /// </para>
    /// </summary>
    public const int WakeStepsNeeded = 3;

    /// <summary>
    /// 一段手势的断点：两记够阈值的位移之间隔过这么久，就算上一段已经结束，路程账从零重记。
    /// <para>
    /// 二百五十毫秒＝两拍半。轮询十赫兹，手连续走动时相邻两记相隔约一百毫秒，留两拍半是给调度抖动
    /// 的余量；再长就开始把「隔了半秒的第二条消息」跟前一条攒到一起，那正是要挡的东西。
    /// <para>
    /// <b>它不是唯一的断点，而且现在已经不是主力。</b>第一轮写的是「断点靠时间而不是靠调用方来报
    /// 『这一拍没动』」，那句话在第二轮复核里被推翻了：调用方（<c>PollPointer</c>）现在每一拍都来，
    /// 整拍没动的那一拍也来，<see cref="HideMoveVerdict"/> 看见它就把账归零——这才是幽灵真正的破绽
    /// （它两步之间整拍冻住），因为幽灵的步距（一百到两百毫秒）本来就落在二百五十以内，拿时间当断点
    /// 等于明文宣布「每隔两百五十毫秒动一下的东西算一只手」。这一条留着管另一种情形：轮询自己漏了拍
    /// （拖窗时整个跳过一次 <c>PollPointer</c>、或者 GetCursorPos 那一拍读失败），账不该跨过那段空白
    /// 继续攒。
    /// </para>
    /// </summary>
    public const long WakeGapMilliseconds = 250;

    /// <summary>
    /// 藏匿期的一记够阈值位移出现时，向真实输入见证问「最近有没有真手的移动」的窗口
    /// （第十六报，2026-09-16）。见证（<c>RealInputWitness</c>，在 Shell）说「没有」就是注入。
    /// <para>
    /// 300 的账：轮询一拍 100ms，手停下到十赫兹的轮询看见最后那记位移，最多隔两拍（200ms 出头），
    /// 窗口必须盖住它，不然手停下后的收尾一记会被冤成注入、光标该醒不醒；往宽了也不能太宽，不然
    /// 注入落地的一瞬恰好还记着一记久远的真移动，注入会被冤放。300 在两边都站得住 —— 手移动的
    /// 时候 WM_INPUT 一秒上百条，窗口里全是见证；注入（SetCursorPos）根本不产生见证，注入
    /// （SendInput）产生的没有 hDevice。
    /// </para>
    /// <para>
    /// <b>第二十一报把它从「充分」降成了「必要」。</b>十六报里见证说「有」就直接唤醒，于是十九报
    /// 那段带着真设备句柄的幽灵流一句话就把光标叫了出来。现在见证只剩一票<b>否决权</b>：说「没有」
    /// 当场判注入；说「有」也只是获准去攒路程，够不够一百像素另说。见证缺席（注册失败）时没有这张
    /// 否决票，路程那一关照常。
    /// </para>
    /// </summary>
    public const long WitnessWindowMilliseconds = 300;

    /// <summary>
    /// 二十报（2026-09-16）的回笼窗口：判成手的唤醒之后，多久没有后续输入就把那次唤醒改判成幽灵流。
    /// <para>
    /// 13:29 那场的结论是「带设备句柄的输入流在输入层面与真手不可区分」——见证答真、裁决判手，
    /// 显示是按设计发生的， 显示之前没有任何判据能拦它。分水岭在显示<b>之后</b>：手必有下一步
    /// （继续动、点击、按键、滚轮），幽灵流停在落点上再也不动。1200 的账：比轮询两拍的收尾间隙
    /// （200ms 出头）宽得多，不会冤枉还在移动的手；比本来的空闲隐藏（2000ms）短 800ms，幽灵流
    /// 停住后光标在屏上多待的时间从「两秒」压到「一秒二」。真手真停住的情形只是把既有行为提前
    /// 了半拍 —— 而且指针停在控件上时 <see cref="ExpireIdle"/> 藏不下去，控件上的手不受影响。
    /// </para>
    /// </summary>
    public const long GhostQuiesceMilliseconds = 1200;

    /// <summary>
    /// 藏匿期被挡下、没有让光标显示的位移，累计多少记。给日志和自检读：这个数不涨，就说明外屏那条路
    /// 要么没在动、要么这次修法根本没生效。
    /// <para>
    /// 名字在第二十一报从 <c>WarpsIgnored</c> 改过来：「跳变」这个词属于十一到十五报那套形状判据，
    /// 而形状判据已经整个拆掉了。现在它数的是「过了关但路程不够」和「见证否决」两种挡法的总和。
    /// </para>
    /// </summary>
    public int MovesHeld { get; private set; }

    /// <summary>当前这段手势的净位移（矢量和的切比雪夫长度）—— 攒到 <see cref="WakeTravelPixels"/>
    /// 就够路程那一关。给日志读，让「挡下了但差多少」在下一份报告里是个数而不是一句猜。</summary>
    public double WakeTravel => _wakeTravel;

    /// <summary>当前这段手势连着走了几拍（每拍都够 <see cref="MovePixels"/>）。给日志读，
    /// 让「离唤醒还差半拍」也是个数。</summary>
    public int WakeSteps => _wakeSteps;

    private double _wakeTravel;

    private int _wakeSteps;

    /// <summary>这一段手势的矢量起点：从 <see cref="ClearHideGesture"/> 起累加的位移（有符号）。</summary>
    private double _wakeX, _wakeY;

    private long _wakeTravelAt;

    /// <summary>
    /// 手势账清零：账本（净位移、连着几拍、矢量起点、时刻）一起归零。四种情形调它 —— 见证投了否决票、
    /// 这一拍不够格（含整拍没动）、隔了 <see cref="WakeGapMilliseconds"/> 没有新位移、以及光标从
    /// 「藏」变回「显」（<see cref="Settle"/> 里那句）。
    /// <para>
    /// 攒到一半的账留着跨越一次显示是没有意义的：显示之后的下一段藏匿是新的一段。同理，一段手势中途
    /// 断过就不能再接起来——「手划半程、一串注入、手再划半程」要是能接上，一百像素那道坎就等于没有。
    /// </para>
    /// </summary>
    private void ClearHideGesture()
    {
        _wakeTravel = 0;
        _wakeSteps = 0;
        _wakeX = 0;
        _wakeY = 0;
        _wakeTravelAt = 0;
    }

    /// <summary>
    /// 藏匿期的一记位移该不该把光标叫回来 —— <b>藏匿期唯一的关</b>（第二十一报，2026-09-16；同日第二轮
    /// 对抗复核改过形状）。
    /// <para>
    /// 三问，按顺序：<b>见证的否决权</b>（<paramref name="realInputSeen"/>，见
    /// <see cref="WitnessWindowMilliseconds"/>）—— 说「没有真手的移动」当场判注入，不进账；
    /// <b>这一拍够不够格</b>（<see cref="MovePixels"/>）—— 不够格就说明手势断了，账归零重记；
    /// <b>路程</b>（<see cref="WakeTravelPixels"/> 与 <see cref="WakeStepsNeeded"/>）—— 净位移
    /// 够一百、而且连着三拍都在走，才算手。
    /// </para>
    /// <para>
    /// <b>账记净位移，不记步长之和。</b>参数是这一拍的位移，累加进这一段手势的矢量起点
    /// （<c>_wakeX/_wakeY</c>），账上的数是矢量和的切比雪夫长度。差别在带缓动或过冲的注入上：
    /// 「+40 接 −40」在步长之和的写法里是走了八十，在净位移里是<b>一动没动</b>；十六报那段五步动画
    /// 也因此在八十六处封顶，不论它下一步往哪儿走。
    /// </para>
    /// <para>
    /// <b>不够格的一拍 —— 包括整拍没动的那一拍 —— 当场把账归零。</b>这是这一版真正的分水岭，也是
    /// 幽灵与真手之间唯一不随消息长短变化的结构差别：真手连续走动时每一个十赫兹的轮询拍都有位移，
    /// 幽灵在步与步之间整拍整拍地冻住（十六报记着「步与步隔着一两百毫秒」，而轮询一拍一百毫秒）。
    /// 调用方（<c>PollPointer</c>）因此在藏匿期<b>每一拍都来</b>，没动的拍子也来，不许漏。
    /// </para>
    /// <para>
    /// <b>位移双向上限都过。</b>取绝对值再判阈值：<see cref="Travelled"/> 的形状是「至少一个轴为正
    /// 且够阈值」，直接喂负数是过不去的。
    /// </para>
    /// <para>
    /// 位移按 <c>max(|Σx|, |Σy|)</c> 量而不按欧氏距离：轮询读的是整数像素，切比雪夫距离与
    /// <see cref="Travelled"/> 判阈值用的是同一把尺，两处一致比多算一次平方根值钱；斜着走的手最多被
    /// 低估到 0.71 倍，一百像素的坎照样几拍就过。
    /// </para>
    /// <para>
    /// <b>判成手时故意不清账</b>：清账的活交给 <see cref="Settle"/> 里那句「没在藏就清」。这样日志行
    /// 打印 <see cref="WakeTravel"/> 时读到的是「走够了多少才醒的」，而不是一个已经被抹掉的零 ——
    /// 下一份报告里这个数就是判据本身的自证。
    /// </para>
    /// <para>
    /// 调用方（<c>PlayerPage.PollPointer</c>）在光标<b>没有藏</b>时不该问这个：显示期任何位移都是手，
    /// 没有要保住的东西，多一道关只是让控件迟钝。所以这个方法只在藏匿期有意义，这一点写在名字上。
    /// </para>
    /// <para>
    /// <b>代价说在明处：每拍不足五像素的手走不到终点。</b>手慢到每秒不到五十物理像素（这台机器
    /// 150% 缩放，约合每秒三十几个逻辑像素）时，每一拍都过不了 <see cref="MovePixels"/>，手势永远
    /// 起不来，光标会一直藏着。二十报之前不是这样 —— 那时参照点冻着，桌面抖一抖也攒得过五像素，
    /// 那正是这个毛病二十一次报上来的原因。所以这个代价是判据的一部分而不是意外：正常挪一下鼠标
    /// 是每秒几百像素，而点击、按键、滚轮三条路照旧无条件立刻显示，手不会被困住。
    /// </para>
    /// </summary>
    /// <param name="realInputSeen">见证说这记位移出现前后有没有真手的移动。见证缺席时调用方传真
    /// （没有否决票），路程那一关照常。</param>
    /// <param name="dx">这一拍的横向位移，像素，<b>带符号</b>（往左为负）。</param>
    /// <param name="dy">同上，纵向。</param>
    /// <param name="now">这一拍的时钟，与规则其它地方同一个。</param>
    /// <returns>真＝手，按移动处理；假＝接着藏。</returns>
    public bool HideMoveVerdict(bool realInputSeen, double dx, double dy, long now)
    {
        // 见证的否决票。没有真手的移动就是注入 —— 连攒都不许攒，不然一串纯注入会把账推到一百像素。
        if (!realInputSeen)
        {
            MovesHeld++;
            ClearHideGesture();
            return false;
        }

        // 这一拍整拍没动：这一段手势到此为止。幽灵就死在这一句上 —— 它两步之间会整拍冻住，而真手
        // 连着走的时候每一拍都有位移。不计一笔：没动的拍子是「没有位移」，不是「被挡下一记位移」。
        if (dx == 0 && dy == 0)
        {
            ClearHideGesture();
            return false;
        }

        // 上一记隔得太久（轮询自己漏了拍）：那一段手势已经结束，这一记是新一段的开头。
        if (_wakeTravelAt != 0 && now - _wakeTravelAt > WakeGapMilliseconds) ClearHideGesture();
        _wakeTravelAt = now;

        // 这一拍不够格：桌面抖动的量级，不是手。手势从这一拍断掉 —— 留着前半程等后半程来接，
        // 一百像素这道坎就等于没有（「手划半程、一串注入、手再划半程」）。
        if (!Travelled(Math.Abs(dx), Math.Abs(dy)))
        {
            MovesHeld++;
            ClearHideGesture();
            return false;
        }

        // 够格的一拍：进这一段手势的账。净位移是矢量起点到现在的切比雪夫长度，不是步子加起来。
        _wakeX += dx;
        _wakeY += dy;
        _wakeSteps++;
        _wakeTravel = Math.Max(Math.Abs(_wakeX), Math.Abs(_wakeY));

        if (_wakeSteps < WakeStepsNeeded || _wakeTravel < WakeTravelPixels)
        {
            MovesHeld++;
            return false;
        }

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
    /// 指针的最后读数是否停在控件上（或音量条的接近带里）。二十报的回笼闸要用它：停靠的指针
    /// 买的是 <see cref="ParkedIdleMilliseconds"/> 的耐心而不是豁免，回笼若在这种指针上拨时钟，
    /// 等于替真手把那份耐心一次性花光 —— 控件会在真手犹豫的半途塌掉。所以停靠的落点不回笼，
    /// 交给既有的停靠规则自己到期。
    /// </summary>
    public bool PointerParked => Parked;

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
    /// 二十报的回笼动作：把空闲时钟直接拨到「早已闲置满 <see cref="CursorIdleMilliseconds"/>」的位置，
    /// 让 <see cref="Settle"/> 用它自己的全部条件去裁决藏不藏。
    /// <para>
    /// 之所以是「拨时钟」而不是「命令藏」：判成手的唤醒之后指针可能落在任何地方——停在画面正中
    /// （幽灵流的落点），也停在控件上、chrome 还在屏上、或某个 hold 正立着。这三种里只有第一种
    /// 该藏，而这个类自己就是那套条件的唯一权威；外壳不该有第二份抄写的藏匿判据（第十三报之前
    /// 每一份抄写都各漏各的）。返回值仍然是「有没有翻动」，外壳拿它决定要不要记账。
    /// </para>
    /// </summary>
    public bool ExpireIdle(long now)
    {
        _lastActivity = now - CursorIdleMilliseconds;
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

        // 新片子开场，上一个文件藏匿期攒的手势路程与挡下的次数都归零：那两个数是「这一段藏匿」的账，
        // 跨文件延续会把日志读成「这一报修了以后还在犯」。
        ClearHideGesture();
        MovesHeld = 0;
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
        //
        // 清账这一句放在早退<b>之前</b>（2026-09-16 第二轮复核）：藏匿期那条路清账只是顺手，真正需要
        // 它的是「没有翻动」的那些拍 —— 光标以别的方式回到「显」（关机、离页、自检里那十几处直接调用）
        // 时 hide 是假而状态没变，早退一走，攒到一半的账就活到了一段新的藏匿里，等于给它的第一记位移
        // 白送几十像素。放在这里，这条不变式才是真的，而不是靠「每次藏之前都恰好有一秒二的静止」凑的。
        if (!hide) ClearHideGesture();

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
