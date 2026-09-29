using EmbyNian.Theming;

namespace EmbyNian.Playback;

/// <summary>
/// 播放器浮层的全部颜色，一张表，只有这一份。
/// <para>
/// 从前这些色值散在 <c>PlayerPage.xaml</c> 里三十五处写死的 hex 上，九个底色重复写了两到七遍：一行 OSD
/// 文字的颜色出现在五个地方，压在画面上那个近黑出现在六个地方。改一处忘一处，屏上就是同一档字里有一行
/// 偏了半档 —— 这类偏差没人报得出来，因为它看着就像本来的设计。
/// </para>
/// <para>
/// 放 Core 而不是放 XAML，除了「只有一份」还有一件事：外壳那层没法跑单元测试（测试项目只引 Core），
/// 颜色一进这里，梯级、透明度次序、对比度就都能拿测试压住了。屏上那些画刷由
/// <c>PlayerPage.PaintPalette</c> 照这张表生成，XAML 里只留空的画刷壳子。
/// </para>
/// <para>
/// <b>这一整套故意不跟主题走。</b>浮层压的是画面，不是应用的那张面 —— 一套浅色主题（从前那套「晴昼」）一换
/// 上来，跟着主题走的墨色就变成深字压在近黑的罩子上，一个字都读不出来。所以 <c>PlayerPage.xaml</c> 上写着
/// <c>RequestedTheme="Dark"</c>，颜色也在这里写死，和 <c>Palette.xaml</c> 末尾那两支
/// <c>EgOnScrim*</c> 是同一个道理。**「晴昼」2026-09-05 按用户的话删了，这条理由没跟着走** —— 删掉的是那套
/// 数据，不是「浮层不该跟主题走」这句话（见 <c>UiThemes</c> 的类注释）。
/// </para>
/// </summary>
public static class PlayerPalette
{
    // ---- 底色 --------------------------------------------------------------------
    //
    // 九个底色，下面那张表全部由它们配一个 alpha 得来。分开写是因为「这块面板有多透」和「这块面板是什么
    // 颜色」是两个会各自被改的决定。

    /// <summary>
    /// 压在画面上那个近黑。控制条背后那道罩子、标题条那层玻璃的底色，和换集时的遮挡层都是它。
    /// <para>
    /// 和 <c>DetailHero.ScrimInk</c>、<c>UiTheme.Scrim</c> 恰好同色，但故意不共用一个来源 —— 三者压的东西
    /// 不同（一帧视频、一张剧照、一整页界面），会各自被调。共用之后，把对话框的罩子调淡一点就会连带把
    /// 视频顶上那道也调淡。
    /// </para>
    /// </summary>
    public static ThemeColor Film { get; } = ThemeColor.Parse("#0C0E11");

    /// <summary>统计面板、音量条、章节预览这三块牌子的底。三块的透明度各不相同，颜色是同一个。</summary>
    public static ThemeColor Panel { get; } = ThemeColor.Parse("#1E2126");

    /// <summary>「跳过片头」那颗按钮的底，比牌子亮一档 —— 它是这一层里唯一一个等着被按的东西。</summary>
    public static ThemeColor Raised { get; } = ThemeColor.Parse("#2E333B");

    // ---- 墨色的四档 ---------------------------------------------------------------
    //
    // 从最亮到最淡，四档，压在画面上读。四档不是四个随手挑的灰：它们的亮度严格递减，测试盯着这一点，
    // 因为「次要读数比正文亮」这种事在深色底上很难用眼睛看出来。

    /// <summary>OSD 的正文与图标 —— 按钮文字、三颗窗口按键、当前时间。</summary>
    public static ThemeColor Ink { get; } = ThemeColor.Parse("#F2F4F7");

    /// <summary>细进度线的已播部分、章节预览里的章节名。比正文退半档。</summary>
    public static ThemeColor InkSoft { get; } = ThemeColor.Parse("#E0E4EA");

    /// <summary>次要读数 —— 副标题、总时长、源信息、预览里的时间、遮挡层上那句话。</summary>
    public static ThemeColor InkDim { get; } = ThemeColor.Parse("#A5ADBA");

    /// <summary>最淡的一档 —— 「跳过」按钮上那行小字和它底下十五秒的进度。</summary>
    public static ThemeColor InkFaint { get; } = ThemeColor.Parse("#8B93A0");

    /// <summary>
    /// 左上角第二行那句文件信息（分辨率 · 视频编码 · 音频格式 · 组名）专用的一档中性浅灰。
    /// <para>
    /// 用户令 2026-09-28 晚「元数据的字体加点灰色」先在独占模式那一头落地（uosc 顶栏那一行的
    /// <c>color = 'c8c8c8'</c>），随后「把独占模式的标题复刻到集成模式」把**同一个值**搬到这里 ——
    /// 复刻看的就是屏上两行一个颜色。
    /// </para>
    /// <para>
    /// 它<b>不是</b> <see cref="InkDim"/> 那一档：那一档是「次要读数」（总时长、源信息、预览里的时间），
    /// 偏冷、更深（<c>#A5ADBA</c>）。这一档是中性灰，只服务左上角那一行，所以另立一支而不是改
    /// <see cref="InkDim"/> 的值 —— 改那一支会把总时长那几处一起拖下水。
    /// </para>
    /// </summary>
    public static ThemeColor InkMeta { get; } = ThemeColor.Parse("#C8C8C8");

    private static ThemeColor White { get; } = ThemeColor.Rgb(0xFF, 0xFF, 0xFF);

    /// <summary>
    /// 播放页上下两条浮层底（标题条、控制条）的透明度（<c>0x40</c>：遮四分之一，透四分之三）。
    /// **这一层是半透明的纯色，不是亚克力**，理由在下面。
    /// <para>
    /// 2026-09-27 用户令「去掉黑色渐变，给红框的位置加一层亚克力背景」，先照做了一版真 <c>AcrylicBrush</c>
    /// （底色近黑、两个浓度，在**XAML 内容上**实测遮四成七八、横向跟着底下的图走，照片为证）。然后他播了一集：
    /// 标题条是一块**近乎不透的黑**。把他那张截图量出来 —— 标题条整条 <c>#101115</c>，它底下的画面是
    /// <c>#D1A979</c>，透光约百分之二。同一支画刷在首页、详情页那些照片上是透的，差别只有一个：**集成模式下
    /// 这一条底下是视频**。
    /// </para>
    /// <para>
    /// 结论：视频那一层（mpv 的 composition 交换链，经 <c>CompositionSurfaceBrush</c> 挂在 <c>VideoHost</c> 上）
    /// 不参与应用内亚克力的 backdrop 采样。玻璃采不到它，就只剩自己的底色 —— 「越透越黑」，「透」这个字在这一条
    /// 上根本无从谈起。**也别被那个便宜诊断骗第二次**：把一块纯色 <c>SpriteVisual</c>（<c>ColorBrush</c>）挂到
    /// <c>VideoHost</c> 上时，标题条**采得到**它（2026-09-27 拿洋红兜底色验过）；<c>ColorBrush</c> 走普通合成
    /// 路径，交换链不走。两者在那种诊断照片上分不出来，只有用户真的播一集才分得出来。
    /// </para>
    /// <para>
    /// 所以这里换成普通的半透明纯色：它与下面那一层是普通的 alpha 混合，视频也好、XAML 内容也好一视同仁 ——
    /// 这是「透」在本架构下唯一做得成的路子，代价是**没有模糊**（要模糊就得采到画面，而上面那条路已经断了）。
    /// </para>
    /// <para>
    /// 上述限制只针对直接采样交换链。时间轴另由视频区域取样提供应用内像素，再叠加 AcrylicBrush；标题栏仍使用这里的透明纯色。
    /// </para>
    /// <para>
    /// 为什么是四分之一：用户看过之后要的是「透明度调到最透」。同一天他还要了「进度条的背景也改成跟标题一样的
    /// 亚克力」—— 于是上下两条浮层一度共用这一支画刷（键名从 <c>PlayerTopGlassBrush</c> 改成了中性的
    /// <c>PlayerGlassBrush</c>，值也只留这一个）：原来控制条背后那道「上沿全透明、往下渐深到 0xF0」的罩子随那
    /// 一条令退役了，它和等厚的玻璃是两回事（渐变在亮场上是一块黑、在暗场上什么都不是）。
    /// **2026-09-27 晚控制条那一半也退役了**（用户令「移除集成模式进度条上方的一大块背景」）：
    /// 控制条不再铺这支，只剩标题条在用，这一档浓度如今只有标题条一处可见。
    /// </para>
    /// <para>
    /// 代价说在明处：遮四分之一之后，一行白字压在最亮的一格画面上只剩 2.5:1 上下（原来的底线是 4.5:1），亮画面
    /// 上的标题、进度轨与时间读数都会发飘。这是「要最透」必然付的钱；觉得读不清就往上加这个数 —— 改一个字节。
    /// </para>
    /// <para>
    /// 写在画刷表**前面**不是排版喜好：C# 的静态成员按声明顺序初始化，表在那一条上读这个值，声明放在后面就会
    /// 读到 0，屏上是一整条全透明。
    /// </para>
    /// </summary>
    public static byte GlassAlpha { get; } = 0x40;

    /// <summary>
    /// 左上角那几块玻璃（返回键 ＋ 片名那两块）<b>最深</b>的那一档：遮七成，指针贴到画面顶边时到的值。
    /// <para>
    /// 用户令 2026-09-27 傍晚「加深左上角亚克力背景的颜色，鼠标位置越靠上亚克力背景的颜色越深」：
    /// 那三块的底不再是「返回键一档、片名一档」的两个常数，而是<b>一条跟着指针高度走的直线</b> ——
    /// 指针走到满深线（第四批用户令「颜色深度在鼠标移动到剧名下方那条线之前一点的时候达到最大」，
    /// 那条线由页面量出来）之上就是这一档，退到顶部那条唤出带的下沿就回到上下两条浮层那一档
    /// （<see cref="GlassAlpha"/>）。算术是 <see cref="TopGlassAlphaAt"/>，指针高度换算成 0..1 那一半
    /// 归 <c>ChromeReveal.TopGlassDepth</c>（带子的宽度是它那边的 <c>EdgeBandFraction</c>：
    /// 「指针靠到多近才算靠上」与「标题条什么时候出来」用的是同一条线，否则会出现玻璃已经最深、条子却
    /// 还没出来的场面）。
    /// </para>
    /// <para>
    /// 取值比原来那一档（0x8C，遮五成半）再深一档。0xCC 往上就是一块实心板，而用户在「加深」这个词上
    /// 要的不是那个；觉得还不够深就加这个数，一个字节，只有这一处跟着动。
    /// </para>
    /// </summary>
    public static byte TopGlassAlpha { get; } = 0xB3;

    /// <summary>
    /// 左上角那几块共用的画刷键。<c>PlayerPage.PaintPalette</c> 按它把基准档填进去、
    /// <c>PlayerPage.ApplyTopGlass</c> 按它逐拍把指针那一档写上去，
    /// <c>PlayerPage.ProbePalette</c> 也按它把这一支从「与表逐字节相等」那条判据里挑出来 ——
    /// 它是全表唯一一支<b>值不是常数</b>的画刷。
    /// </summary>
    public const string TopGlassKey = "PlayerGlassTopBrush";

    /// <summary>
    /// 左上角那几块玻璃在 <paramref name="depth"/> 这一档的浓度。<paramref name="depth"/> 是「指针离
    /// 顶边多近」：0 ＝ 与上下两条浮层同一档（<see cref="GlassAlpha"/>），1 ＝ 贴着顶边
    /// （<see cref="TopGlassAlpha"/>）。
    /// <para>
    /// 越界的值先夹回来，NaN 当 0：这里不比别处，一个跑到 [0,1] 外面的数在 <see cref="byte"/> 上会绕回来，
    /// 屏上不是「更淡一点」而是另一档；而这一支恰恰是压在画面上的那几块底，绕成哪一档都没人看得出来。
    /// 指针读数还没拿到时页面本来就该给 0（<c>PlayerPage.TopGlassDepth</c> 挡了一道），这里再挡一道：
    /// 这条函数是公开的纯函数，别的调用者不该靠「他记得先挡」才对。
    /// </para>
    /// <para>
    /// 住在 Core、做成命名纯函数的理由与表里其他值一样：这条直线是「越靠上越深」那句话的<b>全部算术</b>，
    /// 单元测试钉得住；页面只负责把指针的高度换算成 depth 再把它写上去。
    /// </para>
    /// </summary>
    public static byte TopGlassAlphaAt(double depth)
    {
        var share = double.IsNaN(depth) ? 0 : Math.Clamp(depth, 0, 1);

        return (byte)Math.Round(GlassAlpha + ((TopGlassAlpha - GlassAlpha) * share));
    }

    /// <summary>
    /// 标题条右上那五颗（统计、置顶、最小化、最大化、关闭）鼠标压上去时那两档的底。
    /// <para>
    /// 用户令 2026-09-27 傍晚第四批：「鼠标移动到右上角的关闭的时候背景要和首页一样变成红色，然后右上角另外
    /// 几个按钮鼠标移动到按钮上的时候背景颜色太浅了容易和画面合在一起」；第五批把后半句改实了 ——
    /// 「这四个按钮鼠标移到上面的时候要用**白色亚克力**背景」（圈的是统计、置顶、最小化、最大化四颗，
    /// 关闭那颗仍旧是红的）。
    /// </para>
    /// <list type="bullet">
    ///   <item><b>四颗</b>：白 <see cref="StripHoverAlpha"/>（八成）／按下 <see cref="StripPressedAlpha"/>
    ///     （九成，按下去实一档）。**八成的来历**：2026-09-28 晚用户令「集成模式右上角的这个背景太透明了」——
    ///     第五批那版五成压在亮画面上（蓝天白云、亮场景）仍会糊进去。量过用户那张截图：悬停块内
    ///     (173,207,222) 对紧邻天空 (93,172,203)，底下换成云（R 230 上下）就几乎分不出来。**趋势是单向的**
    ///     （第五批「太浅了容易和画面合在一起」→ 这次「太透明」），再有这类反馈只往实里走，别往回调。
    ///     说清楚一件事：**这不是真亚克力**，是一层半透明的白 —— 真亚克力采不到视频
    ///     那一层（整笔账见 <see cref="GlassAlpha"/> 的注释），压在画面上只会是一块不透的板；半透明的白与底下
    ///     做普通 alpha 混合，视频也好 XAML 也好一视同仁，这是「白玻璃」在本架构下做得成的样子（代价是没有
    ///     模糊）。上一版那支近黑（film <c>0xE6</c>）随第五批那条令退役 —— 用户看过之后要的是白的那一边。</item>
    ///   <item><b>关闭</b>照首页那颗 —— 外壳开了 <c>ExtendsContentIntoTitleBar</c>，那三颗窗口按钮是**系统
    ///     画的**，Win11 上关闭键悬停就是 <see cref="CloseRed"/> 那个红，按下再压暗一成
    ///     （<see cref="CloseRedPressed"/>）。</item>
    /// </list>
    /// <para>
    /// **白底上的图标要转深色**（同一条令里问实的那一半）：那四颗的图标本身是白的，压在八成的白上会糊成
    /// 一片。于是 <see cref="InkOnWhite"/> 是**指针压着的那一颗**在悬停/按下两态的前景色，白底黑字，与浅色
    /// 主题里那颗 caption 按钮同一个道理；代价是指针一进一出图标会跳一次色。**只换压着的那一颗**
    /// （2026-09-27 傍晚第五批的第二趟：第一版写成四颗一起换，用户当场问「怎么是四个按钮一起变色」）。
    /// 关闭那颗**不改** —— 红底上的白叉本来就是对的。
    /// </para>
    /// </summary>
    public static byte StripHoverAlpha { get; } = 0xCC;

    /// <summary>
    /// 那四颗按下时那一档：白九成。它们**没有常态底**，所以「按下比悬停暗一档」在这一族上只会看着像没反应 ——
    /// 按下去实一档才是这一段里看得见的手感。悬停提到八成（2026-09-28 晚）之后按下同一批跟着提，
    /// 两档仍差一成。
    /// </summary>
    public static byte StripPressedAlpha { get; } = 0xE6;

    /// <summary>
    /// 压在那层白上的墨（四颗按钮悬停/按下时图标转成的颜色）。近黑但不纯黑：纯黑压在白上边太硬，
    /// 与这一页别的近黑也不是一个调子。
    /// </summary>
    public static ThemeColor InkOnWhite { get; } = ThemeColor.Parse("#101419");

    /// <summary>
    /// 关闭那颗悬停时的红：Win11 系统 caption 关闭键那一支（<c>#C42B1C</c>），也就是首页那颗鼠标压上去的
    /// 颜色（用户令「和首页一样变成红色」）。取值照系统的值，**没有在这台机器上悬停取样过**：把指针停到首页
    /// 那颗关闭键上取色，验证技能里是禁止的（用户用的是同一只鼠标）。
    /// </summary>
    public static ThemeColor CloseRed { get; } = ThemeColor.Parse("#C42B1C");

    /// <summary>关闭那颗按下时那一档：上面那支红压暗一成（<c>#C42B1C</c> × 0.9）。</summary>
    public static ThemeColor CloseRedPressed { get; } = ThemeColor.Parse("#B02719");

    /// <summary>
    /// 画刷键 → 颜色。<c>PlayerPage.PaintPalette</c> 逐条走这张表，把 XAML 里同名那支空画刷填上。
    /// <para>
    /// 键名带 <c>Player</c> 前缀，是为了和应用那套 <c>Eg*</c> 分清：那一套跟主题走，这一套不跟。
    /// </para>
    /// <para>
    /// 值相同的两条不合并（<c>PlayerEdgeBrush</c> 和 <c>PlayerTrackFillBrush</c> 现在都是 0x59 的白）。
    /// 它们是两件事 —— 一条描边和一段已缓冲的进度 —— 合并之后调描边就会连带调进度条，而这正是这张表要
    /// 消掉的那种牵连。
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Key, ThemeColor Color)> Brushes { get; } =
    [
        // 墨色四档
        ("PlayerInkBrush", Ink),
        ("PlayerInkSoftBrush", InkSoft),
        ("PlayerInkDimBrush", InkDim),
        ("PlayerInkFaintBrush", InkFaint),

        // 左上角第二行那句文件信息专用的一档（用户令 2026-09-28「把独占模式的标题复刻到集成模式」，
        // 与独占 uosc 顶栏那一行的 c8c8c8 同一个值）。它不在上面那条「四档亮度严格递减」的梯级里 ——
        // 那一梯级管的是「压在画面上的正文与三档次要读数」，这一支只服务那一行。
        ("PlayerInkMetaBrush", InkMeta),

        // 三块牌子。透明度递增：统计面板最透（它挡着画面的左上角，而且一直开着），音量条居中，
        // 章节预览最实（里面有一张缩略图，底透了缩略图就发灰）。
        ("PlayerRailBrush", Panel.WithAlpha(0xE6)),
        ("PlayerPeekBrush", Panel.WithAlpha(0xF2)),

        // 「跳过片头」那颗按钮，和换集时把上一集最后一帧盖住的那层（这一层必须是全不透明的）。
        // 悬停/按下两支跟着 2026-09-26 的键位改动一起来（用户令「鼠标移到按钮上的时候背景会变成透明的」）：
        // 默认 Button 模板的 PointerOver/Pressed 态各拿一套系统 ThemeResource 盖底，深色自定义底上就是「变透明」。
        // 修法是把态键按回按钮自己的资源字典（ProbeSeekTrack 修滑杆白条的同一手法），这两支就是按回去的底 ——
        // 同一个 Raised：悬停实一档（0xE6→0xF2，等被按的东西亮半分），按下暗一档（0xE6→0xD9）。
        ("PlayerSkipBrush", Raised.WithAlpha(0xE6)),
        ("PlayerSkipHoverBrush", Raised.WithAlpha(0xF2)),
        ("PlayerSkipPressedBrush", Raised.WithAlpha(0xD9)),
        ("PlayerCoverBrush", Film.WithAlpha(0xFF)),

        // 播放页自己那块舞台底（2026-09-18，进出页面的转场要用它）：和换集那层遮挡同色、同样必须全不透明，
        // 但**故意是独立一支** —— 一个盖的是上一集的最后一帧，一个是这一页在屏上的底，会各自被调；
        // 共用一个的代价就是上面那段注释说的那种牵连。
        ("PlayerStageBrush", Film.WithAlpha(0xFF)),

        // 标题条那一支（2026-09-27）。原本标题条与控制条共用，2026-09-27 晚用户令「移除集成模式进度条
        // 上方的一大块背景」，控制条（PlayerPage.xaml 的 Bar）不再铺它，于是只剩标题条在用。
        // **它是半透明的近黑，不是亚克力** —— 为什么，见上面 GlassAlpha 的注释，那是这一条全部的理由。
        ("PlayerGlassBrush", Film.WithAlpha(GlassAlpha)),

        // 左上角那三块（返回键 ＋ 片名两块）共用的那一支：**跟着指针高度走**（用户令 2026-09-27 傍晚
        // 「加深左上角亚克力背景的颜色，鼠标位置越靠上亚克力背景的颜色越深」）。表里给的是它最浅那一档
        // —— 与上下两条浮层同一支色号、同一档浓度 —— 指针往顶边去的时候由页面逐拍写深，
        // 曲线见 TopGlassAlphaAt 的注释。它是全表唯一一支值不是常数的画刷。
        (TopGlassKey, Film.WithAlpha(GlassAlpha)),

        // 标题条右上那五颗的悬停/按下（用户令 2026-09-27 傍晚第四批、第五批）：四颗一层半透明的白，
        // 关闭那颗一个红；白底上那四颗的图标转深色（第五批问实的那一半）。理由见 StripHoverAlpha 那段注释。
        ("PlayerStripHoverBrush", White.WithAlpha(StripHoverAlpha)),
        ("PlayerStripPressedBrush", White.WithAlpha(StripPressedAlpha)),
        ("PlayerStripHoverInkBrush", InkOnWhite),
        ("PlayerCloseHoverBrush", CloseRed),
        ("PlayerClosePressedBrush", CloseRedPressed),

        // 描边三档，全是白，越该被注意的越亮：统计面板只是块读数，章节预览是浮出来的，
        // 「跳过」是唯一一颗等着被按的按钮。（中间那一档从前音量条也在用，用户要求「音量条不需要边框」之后
        // 只剩章节预览这一个用户；这支画刷留着，不是没人要了。）
        ("PlayerEdgeFaintBrush", White.WithAlpha(0x4D)),
        ("PlayerEdgeBrush", White.WithAlpha(0x59)),
        ("PlayerEdgeStrongBrush", White.WithAlpha(0x66)),

        // 进度条那一带。刻度要比缓冲亮，否则章节分界在已缓冲的那一段里就看不见了。最后那一支是「细线的空的
        // 那一半」：控制条收起时那条细进度线，和「跳过」按钮底下那条十五秒的倒计时，同一件事同一支。
        ("PlayerTickBrush", White.WithAlpha(0xB3)),
        ("PlayerTrackBrush", White.WithAlpha(0x33)),
        ("PlayerTrackFillBrush", White.WithAlpha(0x59)),
        ("PlayerLineBrush", White.WithAlpha(0x26)),

        // 暂停/播放 那一秒的角标：一层纯白，别的什么都没有 —— 「不要黑色的圆形边框，只要白色的三角形」去掉了
        // 底板，「点击画面暂停和开始的图标要纯白色，去掉灰色」（2026-09-05）去掉了它背后那圈半透明黑描边。
        // 代价写在 PulseArt 的类注释里：一帧几乎全白的画面上这颗徽标看不见。
        ("PlayerPulseBrush", White)
    ];
}
