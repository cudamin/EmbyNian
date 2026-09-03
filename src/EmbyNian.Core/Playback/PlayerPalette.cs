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
/// <b>这一整套故意不跟主题走。</b>浮层压的是画面，不是应用的那张面 —— 六套主题里那套浅色（晴昼）一换上来，
/// 跟着主题走的墨色就变成深字压在近黑的罩子上，一个字都读不出来。所以 <c>PlayerPage.xaml</c> 上写着
/// <c>RequestedTheme="Dark"</c>，颜色也在这里写死，和 <c>Palette.xaml</c> 末尾那两支
/// <c>EgOnScrim*</c> 是同一个道理。
/// </para>
/// </summary>
public static class PlayerPalette
{
    // ---- 底色 --------------------------------------------------------------------
    //
    // 九个底色，下面那张表全部由它们配一个 alpha 得来。分开写是因为「这块面板有多透」和「这块面板是什么
    // 颜色」是两个会各自被改的决定。

    /// <summary>
    /// 压在画面上那个近黑。上下两道罩子和换集时的遮挡层都是它。
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

    private static ThemeColor White { get; } = ThemeColor.Rgb(0xFF, 0xFF, 0xFF);

    private static ThemeColor Black { get; } = ThemeColor.Rgb(0, 0, 0);

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

        // 三块牌子。透明度递增：统计面板最透（它挡着画面的左上角，而且一直开着），音量条居中，
        // 章节预览最实（里面有一张缩略图，底透了缩略图就发灰）。
        ("PlayerStatsBrush", Panel.WithAlpha(0xD9)),
        ("PlayerRailBrush", Panel.WithAlpha(0xE6)),
        ("PlayerPeekBrush", Panel.WithAlpha(0xF2)),

        // 「跳过片头」那颗按钮，和换集时把上一集最后一帧盖住的那层（这一层必须是全不透明的）。
        ("PlayerSkipBrush", Raised.WithAlpha(0xE6)),
        ("PlayerCoverBrush", Film.WithAlpha(0xFF)),

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

        // 暂停/播放 那一秒的角标：白图标加一圈同形的黑描边 —— 「不要黑色的圆形边框，只要白色的三角形」，
        // 而白三角压在白墙上本来是看不见的。
        ("PlayerPulseBrush", White),
        ("PlayerPulseRimBrush", Black.WithAlpha(0x59))
    ];

    /// <summary>
    /// 控制条背后那道渐深的罩子，从上沿到下沿。<c>Along</c> 就是渐变停点的 <c>Offset</c>，<c>Alpha</c> 配上
    /// <see cref="Film"/> 就是那一档的颜色 —— 整道罩子只有 alpha 在变。
    /// <para>
    /// 上沿是全透明：罩子的意思是「往下越来越压得住字」，一道从半黑开始的罩子在画面中间会留出一条硬边。
    /// </para>
    /// </summary>
    public static IReadOnlyList<(double Along, byte Alpha)> BottomScrimStops { get; } =
        [(0, 0x00), (0.35, 0x8C), (1, 0xF0)];

    /// <summary>
    /// 标题条背后那道，方向相反 —— 上沿最浓，到下沿散尽。两个停点就够：这一条底下只压着两行字和几颗按键，
    /// 不像控制条那边还有一条进度轨要托住。
    /// </summary>
    public static IReadOnlyList<(double Along, byte Alpha)> TopScrimStops { get; } =
        [(0, 0xE6), (1, 0x00)];
}
