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
    ///
    /// 一句补充（2026-09-26）：指针压在 Skip 上也不点亮 bar/title（<see cref="ChromeReveal.Decide"/> 的
    /// Skip 支，用户令「鼠标移到按钮上的时候不会唤出进度条」）—— 按钮自己跟着 offer 显隐，压着它的指针
    /// 只剩下「指针压在它上面时别把光标收走」（见 <see cref="PointerHolds"/>）与音量条的既有判据。
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
/// top one, and the volume rail while it is near its own rectangle down the right edge. The rest of the
/// height is therefore a dead zone where none of them is on screen, which is the point — that is where
/// the subtitles are.
/// </para>
/// <para>
/// <b>时间不再收走控件（用户令 2026-09-28：「当鼠标停留在对应控件的渐变触发位置时，不要自动隐藏这些控件」）。</b>
/// 从前每一样另有一条空闲窗口：指针静止 650ms（停在控件上则 2000ms）之后，哪怕指针仍落在它的唤出带里，
/// 它也会被收走。用户点名的正是那一刻 ——
/// 指针在「让控件淡入的那个位置」上停着，控件自己又淡没了。现在这三样的显隐<b>只由位置决定</b>（外加钉住、
/// 拖动、双击纯净闸与两条宽限期），控件跟着指针走：指针在带里，控件就在；指针回到画面中间的死区，控件立刻收。
/// 代价写在明处：指针撂在带里不动，控件就一直挂着（从前两秒后收走）—— 要收回来，把指针挪回画面中间或移出窗口。
/// </para>
/// <para>
/// 空闲钟因此只剩一个客户：<b>鼠标指针自己</b>（<see cref="CursorIdleMilliseconds"/>）。它在指针没压在
/// <b>真控件</b>上、又停在画面里的时候走 —— 于是「音量条 / 进度条上指针不藏」是这条结构推出来的结果
/// （<see cref="PointerHolds"/>，单测里单列一条钉着）。
/// </para>
/// <para>
/// <b>报上的「控件」与「光标」自此各认各的（用户令 2026-09-29：「只有鼠标停在控件进度条和音量条上方的
/// 按钮上的时候才不隐藏鼠标，触发渐变的时候不隐藏控件，但是要隐藏鼠标」）。</b>这句话把两半拆开之后就不再
/// 是同一条判据了：
/// <list type="bullet">
///   <item><b>控件</b>在它自己的唤出带里就不收 —— 带子（proximity，包括压强度的那个位置）里控件留着，
///     这正是上一轮那条令，原样不动。</item>
///   <item><b>光标</b>只在指针正压在控件<b>本体</b>上（按钮、滑杆）时才不走。只是把指针停在唤起控件的那个
///     位置（带子里），控件留着、光标照走 —— 从前那条 <c>!next.Any</c> 太宽了，把「带子里」也当成
///     「屏上有东西可瞄」。</item>
/// </list>
/// 于是 <c>ChromeState.Any</c> 不再是光标的闸，<see cref="PointerHolds"/> 才是：它只认
/// <see cref="_part"/> 那四个命中的之一。
/// </para>
/// <para>
/// <b>All three edges arrive by degrees, not as flips</b> (用户令 2026-09-27 四条「参独占模式……修改集成
/// 模式的鼠标位置判断」)。<see cref="State"/> 那三个布尔仍旧只答「该不该出现」，可每一样另配一条<b>强度</b>供页面
/// 淡入：<see cref="TitleStrength"/> 随指针靠近顶边升起、<see cref="BarStrength"/> 随靠近底边升起（进度条的
/// 高度也跟着它长）、<see cref="RailStrength"/> 随靠近右缘中心升起。这照的是独占模式 uosc 的 proximity 淡入
/// （距控件近则明显），<b>推翻了早先的 </b>「不要淡入淡出了，鼠标移动到对应位置直接显示」—— 那一版让标题条与
/// 控制条二值直显，只有音量条淡入；四条令点名要三条边都跟音量条一样。强度是纯算术（<see cref="TitleLoudness"/>
/// / <see cref="BarLoudness"/> / <see cref="Loudness"/>），页面把它写成 <c>Opacity</c> 或高度。
/// </para>
/// <para>
/// The volume rail is still special in one way: it no longer rides with the transport bar —
/// 「显示进度条的时候不需要同步显示音量条」 replaced the earlier 「显示进度条的时候音量条也要显示」, so it comes
/// up for its own approach strip along the right edge, for a hand already resting on it, and for a moment
/// after a wheel or key change — 「鼠标滚轮调整音量时要显示音量条」. Its strength grades on the pointer's
/// euclidean distance to the rail's own rectangle (uosc proximity, <see cref="RailProximity"/>), which the
/// other two — full-width strips that only care about one axis — do not need.
/// </para>
/// <para>
/// 指针之外的第二个输入是「正在拖动窗口」（<see cref="SetWindowDrag"/>）：这一趟窗口跟着手走，位置那套判据
/// 整段不作数，屏上只留标题条 —— 手就压在它上面 —— 进度条与音量条一个都不画。
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
    /// How long the pointer has to hold still before the mouse cursor itself goes.
    /// <para>
    /// <b>1000，mpv.net 的默认（2026-09-16 用户拍板「完全照搬 mpv.net」）。</b>mpv 的
    /// <c>cursor-autohide</c> 不另设时就是这个数，mpv.net 的 <c>_cursorAutohide = 1000</c> 原样照搬。
    /// </para>
    /// <para>
    /// <b>2026-09-28 起它是本类唯一的空闲窗口</b>：控件那三条边的显隐改成只认位置（用户令「当鼠标停留在
    /// 对应控件的渐变触发位置时，不要自动隐藏这些控件」，见类注），从前那两个 650/2000 的窗口随之删除。
    /// 于是「鼠标静止一秒就藏」只在<b>指针没压在真控件上</b>时成立 —— <c>Settle</c> 里那个
    /// <see cref="PointerHolds"/> 是它的闸：指针停在音量条、进度条、标题条或跳过按钮的<b>本体</b>上时
    /// 光标不走（用户令 2026-09-28 的另一半「当鼠标停留在音量条或进度条上时，不要自动隐藏鼠标指针」，
    /// 2026-09-29 收窄成「只有按钮上才不藏」，见 <see cref="PointerHolds"/>）。
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
    /// 左上角那几块亚克力玻璃「淡到底」那条外沿，占画面高度的比例。<b>2026-09-27 晚起它只管左上角那块
    /// 亚克力的深浅曲线（<see cref="TopGlassDepth"/>），不再是控件的唤出线</b> —— 三条边的唤出已改成按
    /// 绝对像素算的 uosc proximity（见 <see cref="TopNear"/> / <see cref="BottomNear"/> 与
    /// <see cref="TopReachPixels"/>）。
    /// <para>
    /// 留 0.20：亚克力只在标题条露着的时候才画得到（那几块玻璃是 <c>TitleStrip</c> 的孩子，条子收了它们就不画），
    /// 而标题条露不露由像素唤出（<see cref="TopReachPixels"/>≈160px）说了算；在窗口化那种高度上 0.20×高≈160px，
    /// 两者对得上。分辨率差很多时这条（比例）与像素唤出会有出入，但亚克力被「条子在不在」兜着，越界那截根本
    /// 不画，看不出来 —— 所以没必要为它也改成像素、去牵动 <see cref="TopGlassDepth"/> 的签名与那几处测试。
    /// </para>
    /// <para>
    /// <c>public</c>：<see cref="TopGlassDepth"/> 要在它和满深线之间插值，页面探针要问「带下沿在哪」。
    /// </para>
    /// </summary>
    public const double EdgeBandFraction = 0.20;

    /// <summary>
    /// uosc 的 proximity 唤出常数（<c>elements/Element.lua</c>：<c>proximity_in=40</c>、
    /// <c>proximity_out=120</c>，绝对像素）。指针离控件矩形近于 <see cref="ProximityInPixels"/> 就满显，
    /// 远过 <see cref="ProximityOutPixels"/> 就全隐，中间线性 —— 用户令 2026-09-27 晚「改成跟独占一样，
    /// 包括音量条」。绝对像素而非画面比例，是这条令的关键：独占与分辨率无关，比例会随窗口高矮忽早忽晚。
    /// </summary>
    public const double ProximityInPixels = 40;

    /// <summary>见 <see cref="ProximityInPixels"/>。</summary>
    public const double ProximityOutPixels = 120;

    /// <summary>
    /// 顶部那块「控件矩形」的高度（逻辑像素），照独占 uosc 的 <c>top_bar_size=40</c>。唤出的距离从这块矩形
    /// 的下沿算：标题满显于离顶 ≤ <see cref="ProximityInPixels"/>＋这个数（80px），全隐于 ≥
    /// <see cref="ProximityOutPixels"/>＋这个数（<see cref="TopReachPixels"/>＝160px）。用独占的 40 而不是
    /// 本项目标题条那 96px，是为了让「什么时候开始显示」与独占一模一样，而不是更早。
    /// </summary>
    public const double TopBarPixels = 40;

    /// <summary>
    /// 底部那块「控件矩形」的高度（逻辑像素）：独占 uosc 底部是控制条摞在时间轴上（<c>controls_size</c> ＋
    /// 边距 ＋ 时间轴），约 88；本项目底部整条也约 89。控制条／进度条满显于离底 ≤ 128px、全隐于 ≥
    /// <see cref="BottomReachPixels"/>＝208px —— 比顶部深一截，正是独占底部那一簇比顶栏高的缘故，也是用户
    /// 「进度条要鼠标下移到更低才显示」抱怨的正解（底边唤出要够高）。
    /// </summary>
    public const double BottomBarPixels = 88;

    /// <summary>顶边唤出到此像素之外就全隐（<see cref="TopBarPixels"/>＋<see cref="ProximityOutPixels"/>）。给测试与页面读。</summary>
    public const double TopReachPixels = TopBarPixels + ProximityOutPixels;

    /// <summary>底边唤出到此像素之外就全隐（<see cref="BottomBarPixels"/>＋<see cref="ProximityOutPixels"/>）。</summary>
    public const double BottomReachPixels = BottomBarPixels + ProximityOutPixels;

    /// <summary>
    /// 左上角那几块玻璃「深到底」那条线，比左簇最下面那块玻璃的下沿再往上这么多像素。
    /// <para>
    /// 用户令 2026-09-27 傍晚第四批「左上角的颜色深度在鼠标移动到剧名下方那条线之前一点的时候达到最大」：
    /// 那条线是 <see cref="TopGlassDepth"/> 的第二个参数，由页面按左簇里最下面那块玻璃（有剧名时就是剧名
    /// 那块 —— 那句话里的「剧名下方那条线」）的下沿减掉这个数算出来。
    /// </para>
    /// <para>
    /// **「一点」不能是 0**：正好停在沿上，鼠标擦着那条线往下一走玻璃就开始变淡，而他说的是「那条线之前
    /// **一点**」。四个像素与这一条上其他几处缝是同一个数（标题条里那两块玻璃之间、返回与片名之间）。
    /// </para>
    /// </summary>
    public const double TopGlassFullInset = 4;

    /// <summary>
    /// 指针离画面顶边有多近：<b>0 ＝ 已经到了顶部带的下沿或者根本不在带子里，1 ＝ 指针在
    /// <paramref name="fullAt"/> 那条线之上（玻璃最深）</b>。
    /// <para>
    /// 用户令 2026-09-27 傍晚「加深左上角亚克力背景的颜色，鼠标位置越靠上亚克力背景的颜色越深」，
    /// 第四批又补了一句「颜色深度在鼠标移动到剧名下方那条线**之前一点**的时候达到最大」—— 于是这条曲线
    /// 不是从顶边算起，而是<b>从那条线算起</b>：线之上全是 1，线以下线性退到顶部带的下沿为 0。
    /// <paramref name="fullAt"/> 是那条线按画面高度算的比例，页面量出来的（见 <see cref="TopGlassFullInset"/>）。
    /// </para>
    /// <para>
    /// 带子就是 <see cref="Edges"/> 那一条唤出带（<see cref="EdgeBandFraction"/>），这一条不是顺手：
    /// <b>「指针靠到多近才算靠上」与「标题条什么时候出来」必须是同一条线</b>，否则会出现玻璃已经最深、
    /// 条子却还没出来的场面 —— 那块底是条子的一部分，它不该比条子先到。
    /// </para>
    /// <para>
    /// 它住在 Core 而不是页面里，理由与 <see cref="RailRoom"/>、音量条那条强度曲线同款：这是一条拿两个数
    /// 就答得出来的规则，单元测试钉得住；页面负责把像素换算成比例、并且量出那条线在哪。指针不在画面里（-1）
    /// 也答 0，那几块这时候根本不该在屏上。
    /// </para>
    /// </summary>
    public static double TopGlassDepth(double pointerY, double fullAt)
    {
        if (pointerY < 0 || pointerY > EdgeBandFraction) return 0;

        var full = Math.Clamp(fullAt, 0, EdgeBandFraction);
        if (pointerY <= full) return 1;

        var span = EdgeBandFraction - full;
        if (span <= 0) return 1;

        return 1 - ((pointerY - full) / span);
    }

    /// <summary>
    /// How strong the volume rail is at its dimmest, as a fraction of full. <b>0 自 2026-09-27 晚起</b>
    /// （用户令「改成跟独占一样，包括音量条」）：独占 uosc 的音量条按 proximity 一路淡到 0，没有下限，所以
    /// 这里跟标题条、控制条一样从 0 起淡。它曾是 0.35（理由：「决定要显示却淡到五个百分点不是含蓄、是故障」）——
    /// 那条顾虑让位给「与独占一致」这条更晚的指令。分级用的那条曲线是 <see cref="RailProximity"/>（指针到音量条
    /// 矩形的 uosc 欧氏距离，页面量、Core 换算），2026-09-28「参考独占模式修复」把旧的「横向线性带 ＋ 竖向中心
    /// 偏置」换成了它。留成具名常量而不是删掉：一处想再给下限就改这一个数，测试也照它写。
    /// </summary>
    public const double RailFloor = 0;

    /// <summary>
    /// 画面小于这个宽度（逻辑像素）就不画音量条了 —— 用户令 2026-09-23：
    /// 「集成模式下窗口小于一定程度的时候自动隐藏音量条」。
    /// <para>
    /// 两个数照<b>音量条自己的尺寸</b>定，不是拍的（条子的尺寸由 <c>ProbeRailFade</c> 每次读出：40 宽、右边距 20、
    /// 约 320 高 ＝ 滑杆 280 ＋ 静音键 40，右贴、纵向居中 —— 与独占 uosc 的音量矩形同几何，这也是本次
    /// 「参考独占模式修复」的前提）：
    /// </para>
    /// <list type="bullet">
    ///   <item>高度 <see cref="RailMinPictureHeight"/>：条子要占满约 320 的高，而独占 uosc 还把音量条自身压在
    ///     「顶栏到控制条之间可用高度的八成」以内——画面再矮下去，那八成很快就托不住整条 320，条子只能被上下
    ///     切短。与其画一根裁过的柱子，不如整条不画，留足余量收在这个数。</item>
    ///   <item>宽度 720：条子连右边距占 60，其 uosc 唤出反达（<see cref="RailProximity"/>，离矩形 120px 起淡）
    ///     还要再往里约 120，合起来近 180 —— 画面窄过这个数，「音量」这件事就比画面本身还抢眼。</item>
    /// </list>
    /// <para>
    /// 两个方向各自成立，所以判据是合取：拖窗口下边缘拖出的「宽而矮」与拖右边缘拖出的「窄而高」都要收。
    /// 要调就调这两个常量 —— 改大改小只影响「小到什么程度才收」，与 <see cref="RailStrength"/> 那条
    /// 由指针距离决定的曲线无关。
    /// </para>
    /// <para>
    /// 它住在 Core 而不是页面里，理由与 <see cref="EdgeBandFraction"/> 同款：这是一条「什么时候不画」的
    /// 规则，拿两个数就答得出来，单元测试钉得住；页面只负责把窗口的尺寸递进来。
    /// </para>
    /// </summary>
    public const double RailMinPictureWidth = 720;

    /// <summary>画面矮于这个高度就不画音量条了。推导见 <see cref="RailMinPictureWidth"/>。</summary>
    public const double RailMinPictureHeight = 560;

    /// <summary>
    /// 这么大的画面容得下音量条吗。假的时候这条音量条<b>一个像素都不画</b>：既不为指针走近右缘而露面，
    /// 也不为滚轮／按键的读数而露面 —— 「自动隐藏」是按那句话的正面意思做的（窗口小 ⇒ 没有音量条），
    /// 代价写在明处：<b>小窗口里滚轮调音量再没有数字可看</b>，把窗口放大或进全屏就有。
    /// <para>
    /// 它只答「这个画面配不配有一条音量条」，与「此刻该不该显示」是两问；后一问仍旧归
    /// <see cref="Pointer"/> 那条路（位置、滚轮宽限、手压在滑杆上）。
    /// </para>
    /// </summary>
    public static bool RailRoom(double pictureWidth, double pictureHeight) =>
        pictureWidth >= RailMinPictureWidth && pictureHeight >= RailMinPictureHeight;

    /// <summary>最后一记活动的时刻（<see cref="CursorIdleMilliseconds"/> 那条钟的起点；2026-09-28 起只量光标）。</summary>
    private long _lastActivity;

    /// <summary>Until this tick count the whole chrome shows whatever the pointer is doing.</summary>
    private long _forceUntil;

    /// <summary>Until this tick count the rail shows even though the pointer is not on it.</summary>
    private long _railUntil;

    /// <summary>Where the pointer was last seen, as a fraction of the picture's height, or -1 for away.</summary>
    private double _pointerY = -1;

    /// <summary>
    /// 画面高度（逻辑像素），最近一次指针读数带进来的。唤出范围按<b>绝对像素</b>算（照独占 uosc 的 proximity），
    /// 要拿它把 <see cref="_pointerY"/> 那个比例换回「离边多少像素」。指针不在画面里（-1）时用不到 ——
    /// <see cref="TopNear"/> / <see cref="BottomNear"/> 先看 <c>_pointerY &lt; 0</c> 就答 0 了。
    /// </summary>
    private double _height;

    /// <summary>
    /// The volume rail's reveal strength from proximity: the uosc proximity the page measured from the
    /// pointer to the rail's own rectangle (<see cref="RailProximity"/>), so 0 at the reveal reach, 1 within
    /// <see cref="ProximityInPixels"/> of the rail, and -1 for a pointer that is no reason to show it at all
    /// — the same 「-1 means away」 as <see cref="_pointerY"/>, so that 「should the rail be up」 and 「how
    /// strongly」 can be one number instead of a flag plus a number that has to agree with it.
    /// <para>
    /// 页面在 <c>RailNear</c> 里把 proximity 恰为 0（够到唤出反达而已）也报成 -1，于是「离得刚好够远」不构成
    /// 显示理由，与独占 proximity 0 ＝ 不画一致；<see cref="Decide"/> 的 <c>_railNear &gt;= 0</c> 因此正好是
    /// 「在唤出范围内」。
    /// </para>
    /// </summary>
    private double _railNear = -1;

    private ChromePart _part;
    private bool _holdChrome;
    private bool _keepChrome;

    /// <summary>
    /// 窗口正被标题条拖着走（2026-09-22，用户令「在播放页面中，当用户长按标题并拖动播放窗口时，拖动过程中
    /// 不要显示进度条和音量条」）。
    /// <para>
    /// 它压过 <see cref="HoldChrome"/>，而不是与它并列：拖动那条路上控件本来就是被钉住的（手停在标题条上
    /// 不动，空闲钟不该说话），可钉住的原话是「三样一起给」，而这一趟要的恰好是「三样里只留一样」。
    /// <see cref="Decide"/> 里拖动那一支因此排在钉住前面。
    /// </para>
    /// <para>
    /// 留下的那一样是标题条，是必须的：手就压在它上面，拖动是它的手势，把它收掉等于把手上那颗东西抽走。
    /// 而进度条与音量条在这一趟里既没有读得进去的人，又是画在一块正跟着窗口晃的画面上 —— 拖动时窗口每一拍
    /// 都在动，两根条跟着重排、跟着晃，比它们本身的信息量显眼得多。
    /// </para>
    /// </summary>
    private bool _windowDragging;

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
    /// caused, and it grades it as the uosc volume does: the pointer's euclidean distance to the rail's own
    /// rectangle (<see cref="RailProximity"/>), measured by the page because only it knows where the rail
    /// sits. Both axes fall out of that one distance — the corners of the picture are far from a rail that
    /// stands 40 wide and centred down the right edge, so they stay dark without a separate centre bias.
    /// </para>
    /// </summary>
    public double RailStrength { get; private set; } = 1;

    /// <summary>
    /// How strongly the title strip should be drawn, 0 when it is not up at all and up to 1 hard against the
    /// top edge. The page fades to this rather than flipping to it — 「参独占模式鼠标位置越靠近窗口上方标题越
    /// 明显」 (用户令 2026-09-27，照 uosc 的 proximity 淡入)。
    /// <para>
    /// The exact sibling of <see cref="RailStrength"/> along the other edge: full for every reason that is a
    /// readout rather than an approach — a hand already on the strip, a pinned chrome, a keyboard command —
    /// and graded by how near the pointer is to the top otherwise (<see cref="TopNear"/>). This is the reveal
    /// half; the acrylic behind the left cluster deepens on its own curve (<see cref="TopGlassDepth"/>).
    /// </para>
    /// <para>
    /// 与音量条不同，标题条<b>没有下限</b>（<see cref="RailFloor"/> 那种）：uosc 的顶栏一路淡到 0，带内沿处
    /// 因此很淡。这是照搬独占的取舍，嫌太虚就在 <see cref="TitleLoudness"/> 里加一个下限 —— 一处常量。
    /// </para>
    /// </summary>
    public double TitleStrength { get; private set; } = 1;

    /// <summary>
    /// How strongly the transport bar (the button row, and with it the growing timeline) should be drawn,
    /// 0 when it is not up at all and up to 1 hard against the bottom edge — 「参独占模式鼠标位置越靠近窗口
    /// 下方按钮条越明显 / 进度条显示越多」. The sibling of <see cref="RailStrength"/> and
    /// <see cref="TitleStrength"/> along the bottom edge; graded by <see cref="BottomNear"/>, full for the
    /// same readout reasons. The page also grows the timeline's height with it (uosc's
    /// <c>Timeline:get_effective_size</c>, whose visibility follows the controls).
    /// </summary>
    public double BarStrength { get; private set; } = 1;

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
    /// go idle, and the one thing that clock still decides is <see cref="CursorHidden"/> — a pointer
    /// resting in the dead zone would keep its arrow for the whole film, which is
    /// 「鼠标指针还是不会自动隐藏」. （控件那三样 2026-09-28 起按位置显隐，不再看这个钟；这一位对它们只剩
    /// 「钉住 = 三样一起给」的作用。）Only a real change of the hold is news; the countdown that follows one is
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
    /// 拖动标题条移动窗口开始／结束（2026-09-22，用户令见 <see cref="_windowDragging"/>）。与
    /// <see cref="SetHold"/> / <see cref="SetKeep"/> 同款：锁存，重复告知不作数；放开时重盖空闲钟。
    /// <para>
    /// 放开时那一记重盖是必要的，理由和另外两个不同：拖动期间页面那一头不问指针轮询（窗口跟着指针走，
    /// 指针相对窗口没动过），所以整段拖动里一次活动都没落账。若照「上一记活动」去算，一场拖长的拖动松手
    /// 那一拍空闲钟已经走满，而这时指针正压在标题条上 —— 控件按位置留着，可<b>光标</b>会当场被这条走满的钟
    /// 收走（从前更糟：控件与光标一起收）。
    /// </para>
    /// </summary>
    public bool SetWindowDrag(bool dragging, long now)
    {
        if (_windowDragging == dragging) return Settle(now);

        _windowDragging = dragging;
        if (!dragging) _lastActivity = now;
        return Settle(now);
    }

    /// <summary>
    /// Whether the mouse cursor should be hidden: the pointer is over the picture, has stopped, and is
    /// not resting on a control.
    /// <para>
    /// It waits for the pointer to actually stop — for <see cref="CursorIdleMilliseconds"/>, a window of its
    /// own — rather than merely for the chrome to be down. The dead zone between the edge bands reveals
    /// nothing, so the chrome goes as soon as the pointer crosses into it, and a cursor that vanished there
    /// would disappear in the middle of a movement with nothing on screen to explain where it had gone.
    /// </para>
    /// <para>
    /// <b>「not resting on a control」is <see cref="PointerHolds"/>, and it is the narrower half of the two
    /// rules</b>（用户令 2026-09-29）：控件在它自己的唤出带里就留着，光标在那条带子里照走。从前这里问的
    /// 是「屏上有没有东西」（<c>!next.Any</c>），唤出带因此也保住了光标 —— 那句话把两半说成一条判据，
    /// 用户点名要拆开。
    /// </para>
    /// <para>
    /// <b>「Stopped」 is decided by one clock and one sensor.</b> <see cref="Moved"/> and
    /// <see cref="Pointer(double, double, ChromePart, double, long, bool)"/> with <c>moved: true</c> are the
    /// only two things that restamp the idle clock; a report that is merely a position — the poll's own
    /// unchanged reading, a resize — is not a movement and leaves it alone. That split is what lets a
    /// pointer genuinely at rest expire, which is the whole of 「鼠标静止不动一秒之后要自动隐藏」.
    /// </para>
    /// </summary>
    public bool CursorHidden { get; private set; }

    /// <summary>
    /// 空闲钟走到了哪儿（毫秒）—— <b>只给外壳的诊断行读</b>：光标该藏不藏的这些时候，这一格说清
    /// 是不是被谁不停重盖着（2026-09-16 晚加的，用户报「鼠标已经不会自动隐藏了」时手边唯一的读数）。
    /// 只读，不改任何状态。控件那三样 2026-09-28 起不看它（按位置显隐），读它只对光标有意义。
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
    /// <paramref name="part"/>, and <paramref name="railNear"/> is the volume rail's uosc proximity the page
    /// measured to the rail's rectangle — -1 for 「no reason to show it」, 0 at the reveal reach, 1 within
    /// <see cref="ProximityInPixels"/> of the rail.
    /// <para>
    /// The rail is asked about separately from the hit test rather than being one of its answers. A hit
    /// test has to pick a single winner, and the skip-intro button and the top strip both overlap the
    /// right edge — while the hit test was the only source of truth a pointer there resolved to Skip or
    /// Title and the rail silently refused to come up:
    /// 「音量条判定有问题，我鼠标移到窗口右边有时候不会显示」. Reveal and grab are different questions, so
    /// they get different tests.
    /// </para>
    /// <para>
    /// Proximity rather than a yes: the same reading answers 「should the rail be up」 and 「how strongly」, and
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
    /// how 「鼠标静止不动一秒之后要自动隐藏」 is measured from the last thing a hand did. A position reported
    /// again is the opposite: it is what the ten-hertz poll and every resize hand over, they say nothing about
    /// a hand, and restamping for them is what 「鼠标隐藏了一会然后又会自动冒出来」 was made of — a cursor
    /// whose second never gets to expire, from a player whose own log says the pointer never moved.
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
        _height = height;
        _part = part;
        _railNear = railNear < 0 ? -1 : Math.Clamp(railNear, 0, 1);

        // Anything the pointer is resting on counts as activity even without movement — 指针停在控件本体上
        // 的时候光标也不该走（<see cref="PointerHolds"/> 是那一问的判据；这里记的只是空闲钟）。而 a report
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
    /// 指针正压在控件上、于是光标不该被收走 —— <b>只认本体，不认唤出带</b>（用户令 2026-09-29
    /// 「只有鼠标停在控件进度条和音量条上方的按钮上的时候才不隐藏鼠标，触发渐变的时候不隐藏控件，但是要
    /// 隐藏鼠标」）。
    /// <para>
    /// 三条判据在这儿各答一件事，别混：<b>控件</b>显不显由位置（唤出带，<see cref="Decide"/> 的 Eges 与
    /// <see cref="_part"/>），<b>强度</b>由 proximity（<see cref="BarStrength"/> 那三样），<b>光标</b>藏不藏
    /// 由这一问。从前光标那一格写的是 <c>!next.Any</c> —— 只要三条带里任何一条亮着就不藏，于是「停在唤出
    /// 带里看控件淡入」这个位置上也把光标留住了，与「触发渐变时要隐藏鼠标」正相反。
    /// </para>
    /// <para>
    /// 「本体」就是 <see cref="PartAt"/> 那四处命中：进度条／控制条那一排按钮（<see cref="ChromePart.Bar"/>）、
    /// 标题条那几块玻璃（<see cref="ChromePart.Title"/>）、音量条的滑杆与静音键（<see cref="ChromePart.Volume"/>）、
    /// 跳过按钮（<see cref="ChromePart.Skip"/>）。它们都是<b>看得见摸得着的一块</b>，指针压在上面时藏掉光标
    /// 是说不通的；而带子是空无一物的位置，那里藏光标正是它该在的地方。
    /// </para>
    /// <para>
    /// 音量条那一半有个来历：uosc 的音量条是一个<b>矩形</b>（40 宽、贴右缘、纵向居中），指针只要落进它自己的
    /// 矩形就算本体 —— <see cref="ChromePart.Volume"/> 由页面按那个矩形的命中测试给出，不是按 proximity 算的。
    /// </para>
    /// </summary>
    private bool PointerHolds => HoldsCursor(_part);

    /// <summary>
    /// <paramref name="part"/> 是不是「光标该为它留一手」的那几块 —— 四个都是<b>看得见摸得着的一块</b>
    /// （进度条／控制条那一排按钮、标题条那几块玻璃、音量条的滑杆与静音键、跳过按钮）。唤出带不在其中：
    /// 带子是空无一物的位置，指针在那里时控件留着、光标照走（用户令 2026-09-29，详见
    /// <see cref="PointerHolds"/>）。
    /// <para>
    /// <c>public static</c> 是给外壳的诊断行用的（<c>PlayerPage.ExplainNoHide</c> 读的是页面自己那份
    /// <c>_pointerOn</c>，不是规则记的那一份）—— 两处必须同一个答案，所以判据只有一个。
    /// </para>
    /// </summary>
    public static bool HoldsCursor(ChromePart part) =>
        part == ChromePart.Bar || part == ChromePart.Title
        || part == ChromePart.Volume || part == ChromePart.Skip;

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
    /// 窗口此刻是不是正被标题条拖着。与 <see cref="Silenced"/> 同款：给页面与诊断读的只读读数 ——
    /// 底边那条细进度线要按它决定画不画（见 <c>PlayerPage.Render</c>），而它不在 <see cref="State"/> 里。
    /// </summary>
    public bool WindowDragging => _windowDragging;

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
        TitleStrength = 1;
        BarStrength = 1;
        CursorHidden = false;
    }

    /// <summary>Applies the rule to the recorded pointer state and reports whether anything moved.</summary>
    private bool Settle(long now)
    {
        var next = Decide(now);
        var strength = next.Rail ? Loudness(now) : 0;
        var titleStrength = next.Title ? TitleLoudness(now) : 0;
        var barStrength = next.Bar ? BarLoudness(now) : 0;

        // The cursor is a process-wide resource, so it only ever hides while the pointer is genuinely
        // over this picture and not resting on a control it might be about to use.
        //
        // **这里只看「压在真控件上了没有」，不看「屏上有没有东西」**（用户令 2026-09-29「只有鼠标停在控件，
        // 进度条和上方的按钮还有音量条上的时候才不隐藏鼠标，触发渐变的时候不隐藏控件，但是要隐藏鼠标」）。
        // 从前这一格写的是 !next.Any —— 只要三条唤出带里任何一条亮着就不藏光标，于是「停在唤出带里」这个
        // 位置上光标也被留住了，与「触发渐变时要隐藏鼠标」正相反。控件显隐那一半一个字没动（就是上面的
        // next：位置说了算），只把光标这一半收窄到 PointerHolds。
        //
        // 留在里面的三个理由各是「指针压在一块真东西上」：控件本体（PointerHolds）、弹出菜单开着
        // （HoldChrome）、文件还在加载（KeepChrome）。
        //
        // WindowFocused 是 mpv.net 的 ActiveForm == this（2026-09-16 照搬）：窗口不在前台就不藏；
        // 这一位从真翻假的那一拍，本来藏着的 hide 也跟着变假 —— OnLostFocus → ShowCursor 那条路
        // 就是从这里走通的，外壳喂完这一位推一拍即可。
        var hide = !PointerHolds
            && _pointerY >= 0
            && !HoldChrome
            && !KeepChrome
            && WindowFocused
            && now - _lastActivity >= CursorIdleMilliseconds;

        // The strength is quantised at the source rather than compared with a tolerance here, so that
        // 「did anything change」 stays an equality — a pointer sliding along an edge moves it in
        // hundredths, and every hundredth is a repaint the page has asked to hear about.
        if (next == State && strength == RailStrength && titleStrength == TitleStrength
            && barStrength == BarStrength && hide == CursorHidden) return false;

        State = next;
        RailStrength = strength;
        TitleStrength = titleStrength;
        BarStrength = barStrength;
        CursorHidden = hide;

        return true;
    }

    private ChromeState Decide(long now)
    {
        // 拖动标题移动窗口（2026-09-22，用户令「拖动过程中不要显示进度条和音量条」）：三样里只留标题条。
        // 这一支排在钉住前面，因为拖动同时也是「钉住」的一个理由（手停在标题条上不动），而钉住的原话是
        // 三样一起给 —— 谁想给两根条的例外加条件，先看清楚这个先后。
        if (_windowDragging) return new ChromeState(false, true, false);

        // A flyout is open or the file is still loading: the controls are the way out of that state, so
        // nothing about the pointer may take them away. （窗口拖动从前也在这一支里，2026-09-22 起它自己一支，
        // 见上面那两行 —— 它要的恰好是「三样里只留一样」。）
        if (HoldChrome || KeepChrome || now < _forceUntil) return new ChromeState(true, true, true);

        // 双击全屏／还原的纯净闸（2026-09-18）：在真手再动（Moved/WakeFully/FlashRail/Reset 解锁）之前，
        // 点画面那一下给的宽限与指针所在的位置都不构成显示理由 —— 画面保持只有片子。
        if (_silenced) return new ChromeState(false, false, false);

        var (bar, title) = Edges();

        // A pointer already on a control means the user arrived, whatever the bands say — the rail in
        // particular stands clear of the bottom fifth.
        //
        // 压在跳过按钮上不唤进度条（用户令 2026-09-26「鼠标移到按钮上的时候不会唤出进度条」）：按钮按
        // offer 的节拍自己显隐（SkipVisibility），指针到它上面不等于「要看控制条」—— 而它恰在底部边缘带里，
        // 底带判据会把进度条带出来。这一支盖过 Edges 的结果：bar/title 都不亮，音量条照旧走自己的判据。
        if (_part == ChromePart.Bar) bar = true;
        else if (_part == ChromePart.Title) title = true;
        else if (_part == ChromePart.Skip) (bar, title) = (false, false);

        // 「显示进度条的时候不需要同步显示音量条」: the bar used to be one of the rail's reasons, and it was
        // the wrong kind of reason — the pointer being in the bottom band is a request for the transport
        // bar and says nothing at all about the volume.
        //
        // **这里没有时间**（用户令 2026-09-28「当鼠标停留在对应控件的渐变触发位置时，不要自动隐藏这些控件」）：
        // 上面那几问全是「指针此刻在哪」，于是控件跟着指针走 —— 从前那一支「空闲窗口走了就把三样一起收掉」
        // 已经删除，指针停在唤出带（或压在控件上）里多久，那一样就留多久。要收回来只有两条路：指针回到
        // 画面中间的死区（Edges 与 _part 当场都答 false），或者移出画面（PointerLeft 清掉读数）。
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
        // rattling on a desk does not repaint. _railNear is already the uosc proximity the page measured
        // to the rail's own rectangle (both axes at once, so no separate centre bias) — see RailProximity.
        return Math.Round(RailFloor + (1 - RailFloor) * _railNear, 2);
    }

    /// <summary>
    /// How strongly to draw a title strip that is up — the top-edge sibling of <see cref="Loudness"/>. Full
    /// for every reason that is a readout or a pointing rather than an approach (a hand on the strip, a pinned
    /// chrome, a keyboard command); graded by <see cref="TopNear"/> (uosc proximity, absolute pixels) otherwise.
    /// <para>
    /// No floor: 「改成跟独占一样」 — all three edges now fade to nothing at their reveal reach, the volume too
    /// (<see cref="RailFloor"/> is 0). Full against the top, invisibly faint at the reach.
    /// </para>
    /// </summary>
    private double TitleLoudness(long now)
    {
        if (HoldChrome || KeepChrome || now < _forceUntil || _part == ChromePart.Title) return 1;
        return Math.Round(TopNear, 2);
    }

    /// <summary>
    /// How strongly to draw a transport bar that is up — the bottom-edge sibling of <see cref="Loudness"/>.
    /// The page uses it for both the button row's opacity and the timeline's height (uosc grows the timeline
    /// with the controls' visibility). Full for the same readout reasons; graded by <see cref="BottomNear"/>.
    /// </summary>
    private double BarLoudness(long now)
    {
        if (HoldChrome || KeepChrome || now < _forceUntil || _part == ChromePart.Bar) return 1;
        return Math.Round(BottomNear, 2);
    }

    /// <summary>
    /// 独占 uosc 的 proximity 曲线（<c>elements/Element.lua</c>）：指针离控件矩形 <paramref name="distFromEdgePx"/>
    /// 像素（矩形贴着屏幕那条边、高 <paramref name="barPx"/>），近于 <see cref="ProximityInPixels"/> 满显（1）、
    /// 远过 <see cref="ProximityOutPixels"/> 全隐（0），中间线性。指针不在画面里（距离 &lt; 0）答 0。
    /// </summary>
    private static double Reveal(double distFromEdgePx, double barPx)
    {
        if (distFromEdgePx < 0) return 0;

        var toRect = Math.Max(0, distFromEdgePx - barPx);
        var range = ProximityOutPixels - ProximityInPixels;
        return 1 - Math.Clamp(toRect - ProximityInPixels, 0, range) / range;
    }

    /// <summary>
    /// 独占 uosc 的音量条 proximity 曲线（<c>elements/Element.lua</c> ＋ <c>lib/utils.lua</c> 的
    /// <c>get_point_to_rectangle_proximity</c>）：指针离音量条矩形 <paramref name="distancePixels"/> 像素，
    /// 近于 <see cref="ProximityInPixels"/> 满显（1）、远过 <see cref="ProximityOutPixels"/> 全隐（0），中间线性。
    /// <para>
    /// <b>几何在页面、曲线在这里</b>（用户令 2026-09-28「参考独占模式修复」）：标题条／控制条是整幅宽的条，只有
    /// Y 有意义，Core 直接拿 <see cref="_pointerY"/> 算（<see cref="TopNear"/>／<see cref="BottomNear"/>）；音量条是
    /// 一块有限矩形，横竖两轴都要，只有页面看得到它排在哪，所以由页面量出「指针到那条 40 宽、贴右缘、纵向居中的
    /// 真矩形的欧氏距离」（<c>PlayerPage.RailNear</c>，同 uosc 的算法），这里把距离换成强度。曲线单测钉得住
    /// （<c>RegisterChromeReveal</c>），几何归自检的 <c>ProbeRailFade</c>——它手上有排过版的音量条元素。
    /// </para>
    /// <para>
    /// 这条取代了旧的「横向线性带 ＋ 竖向中心偏置 <c>Centred</c>」那套近似：<c>Centred</c> 是 Core 拿不到矩形时
    /// 对「竖向也该衰减」的粗略估计，改量真矩形的欧氏距离后四角变暗自然成立且更准，与独占逐像素一致。
    /// </para>
    /// </summary>
    public static double RailProximity(double distancePixels) => Reveal(distancePixels, 0);

    /// <summary>
    /// How near the pointer is to the top edge — the top-edge sibling of the volume's approach, as uosc
    /// proximity in absolute pixels (<see cref="Reveal"/> against <see cref="TopBarPixels"/>). 1 near the top,
    /// 0 beyond <see cref="TopReachPixels"/>. Absolute pixels, not a fraction of height, so 「what counts as
    /// near」 is the same on every window size — 「改成跟独占一样」.
    /// </summary>
    private double TopNear => Reveal(_pointerY < 0 ? -1 : _pointerY * _height, TopBarPixels);

    /// <summary>How near the pointer is to the bottom edge, as uosc proximity in absolute pixels
    /// (<see cref="Reveal"/> against <see cref="BottomBarPixels"/>). Symmetric to <see cref="TopNear"/>, but
    /// reaching further because the bottom cluster is taller — the whole of 「进度条要鼠标下移到更低才显示」.</summary>
    private double BottomNear => Reveal(_pointerY < 0 ? -1 : (1 - _pointerY) * _height, BottomBarPixels);

    /// <summary>
    /// Which of the two edge overlays the pointer is asking for. Now the uosc proximity being non-zero: the
    /// strip or bar is 「up」 exactly while the pointer is within its reveal reach (<see cref="TopReachPixels"/>
    /// / <see cref="BottomReachPixels"/>), and its strength then grades from there — the same one number
    /// answers 「是否出现」 and 「多明显」, so they cannot disagree.
    /// </summary>
    private (bool Bar, bool Title) Edges()
    {
        if (_pointerY < 0) return (false, false);

        return (BottomNear > 0, TopNear > 0);
    }
}
