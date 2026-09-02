using EmbyNian.Theming;

namespace EmbyNian.Emby;

/// <summary>
/// 详情页头上那一格有多高，以及它底下的正文至少要多高 —— 「让背景图填满页面，别只显示一个框」加上
/// 「图一页面怎么改的一大片空白，改回去」「滑到下面不用显示背景了」。
/// <para>
/// 三版的账：最早它是一格写死 376 高、四周留 28、围一圈发丝线的胶片 —— 一张剧照裱在页面中间的框里，正是
/// 「别只显示一个框」说的那副样子。第二版把它铺满一屏，框没了，可是海报、片名和那排键全被压到窗口下沿，
/// 上面剩大半屏没有一个字的画面 —— 那就是「一大片空白」。这一版回到一格带子：高由内容定（<see
/// cref="ArtHeight"/>），跟窗口无关，所以窗口再大也不会撑出空白；铺满页面那件事交给背景那一层（它在滚动
/// 视图外面，独立于这一格铺满整窗），而正文是一张不透明的纸，往下滚就把那张图盖住。
/// </para>
/// <para>
/// 和 <see cref="HomeCarousel.Height"/> 一样放在这里、一样是纯函数：这两块是整个界面上最大的两张图，而
/// 「多高」这种事一旦写在 SizeChanged 里，就没人能再问它一句了。
/// </para>
/// </summary>
public static class DetailHero
{
    /// <summary>
    /// 没有剧照的条目上这一格有多高。这时候它是一格底色加一层罩子，只要垫住海报、片名和那排键就够，比有图
    /// 的那一档再矮一点：没有画面可看，多留的每一像素都是空白。
    /// </summary>
    public const double PlainHeight = 380;

    /// <summary>
    /// 有剧照时这一格有多高。数是内容给的，不是窗口给的：海报 300 高、上下两道边 28 和 64，加上字块和那排
    /// 键，440 上下才站得开，短一格就会把播放键挤出带子；460 在那之上留一线干净的画面。
    /// <para>
    /// 写死而不是跟着视口走，就是「一大片空白」那句话的修法：跟着视口走的那一版在 1440 高的窗口上要留出
    /// 一屏的画面，而画面上只有角上一枚记号。
    /// </para>
    /// <para>
    /// 「统一改为在剧名上方显示徽标」之后那一叠字里多了一枚徽标（最高 56，连间距 66），这个数没跟着改，因为
    /// 不用改：这一格里让给内容的是 460 − 28 − 64 = 368，而海报那 300 仍然是两者里高的那个（那一叠连徽标
    /// 算下来两百三上下，片名折两行也才两百六）。<em>集页不一样</em> —— 那一格的高本来就按那一叠实测给
    /// （<see cref="EpisodeHeight"/>），所以它会跟着徽标长高六十几像素，这是诚实的：屏上真的多了一样东西。
    /// </para>
    /// </summary>
    public const double ArtHeight = 460;

    /// <summary>
    /// 这一格的高：有剧照 <see cref="ArtHeight"/>，没有就退回 <see cref="PlainHeight"/>。
    /// </summary>
    /// <param name="artwork">
    /// 服务器上有没有这一张图 —— <see cref="ItemArtwork.Hero"/>，不是「位图解出来了没有」。
    /// <para>
    /// 这两句话差着一次网络往返，而这一格的高按它分两档，所以拿后一句当判据就是「先按 380 布一遍，图到了再
    /// 按 460 布第二遍」 —— 屏上看着就是「点击主页封面后窗口会闪一下，然后才会进入页面」。列表接口回来的条目
    /// 已经带着 ImageTags，所以前一句在导航那一刻就答得出，第一帧的版面就是最后的版面。
    /// </para>
    /// </param>
    public static double Height(bool artwork) => artwork ? ArtHeight : PlainHeight;

    /// <summary>
    /// 集页那一格最矮能到多少，同时也是还没量到那一叠字和键有多高时用的那个数（见 <see cref="EpisodeHeight"/>）。
    /// 一张 16:9 剧照加上下那两道留白就是这个量级，所以第一帧按它布出来的版面和量完之后只差几个像素，屏上看不
    /// 出挪动 —— 反过来拿 <see cref="ArtHeight"/> 兜底的那一版每进一次集页都要从 460 缩到实测值，那是一次看得
    /// 见的跳。比这个数再矮也不是「紧凑」而是「什么都放不下」。
    /// <para>
    /// 它同时关着「右上角那张艺术图能画多高」—— 「右上角显示艺术图」。那张图顶对齐摆在这一格里，上限 320×180
    /// （<c>DetailPage.xaml</c> 里的 <c>CornerArt</c>），而最矮的一档带子减掉上下那两道留白（28 和 16）只剩 156，
    /// 比 180 矮 —— 所以在那一档上它按 <c>Uniform</c> 自己缩到 156 高（277 宽），<em>不会顶出带子</em>：带子的高
    /// 只按海报和那一叠字键算（<c>DetailViewModel.HeroRoom</c>），从来不看这一张，而实测下来集页那一格是 240 上下，
    /// 缩的只是碰到下限那种退化情形。上限从 146 抬到 180 是用户要的（「再把艺术图调大一些」）。
    /// </para>
    /// </summary>
    public const double EpisodeFloor = 200;

    /// <summary>
    /// 集页那一格的高：由它里面那一叠（剧照、片名、副标题、读数、那排键）连上下留白实测给出。
    /// <para>
    /// 规矩和别的页面是同一条 —— 高由内容定、不跟窗口走（<see cref="ArtHeight"/> 上那一段），只是这一页量在
    /// 运行时。为什么不能跟着用 460：单集配的是一张 16:9 剧照，比 2:3 的海报矮一大截，那一叠字也比电影页少
    /// 两行，而这一叠是底对齐的 —— 给它 460 就等于在它头上留出两百来像素只有画面的地方，「集拉大窗口后会导致
    /// 左上角空空的，画面不协调，电影那边处理的就很好」说的正是那一块。电影页之所以「处理的很好」，恰恰因为
    /// 460 就是那一页量出来的内容高。
    /// </para>
    /// <para>
    /// 上一版是「从视口里减掉底下那一段」：窗口越高带子越高，一直到 460 封顶，于是那块空白跟着窗口一起长 ——
    /// 拉大窗口才看得明显。那一版夹着的下限就是这里的实测值本身，所以矮窗口上的版面一个像素没变（音轨那一行
    /// 和同季那一带集照旧尽量落在第一屏里，只是靠「带子不多占」而不是靠「带子算得准」）；变的只是高窗口上
    /// 不再拿画面去凑高度。
    /// </para>
    /// </summary>
    /// <param name="heroRoom">
    /// 那一叠连上下留白实测要占多高，由页面量出来交进来（<c>DetailPage.OnHeroStackSizeChanged</c>）。不封顶：
    /// 片名折两行、窗口窄到读数换行的时候它会超过 <see cref="ArtHeight"/>，那时候带子就该跟着长 —— 封了顶的
    /// 那一版会让那一叠从带子里溢出去，压在底下的音轨那一行上。0 是「还没量」，按 <see cref="EpisodeFloor"/> 给。
    /// </param>
    public static double EpisodeHeight(double heroRoom) => Math.Max(EpisodeFloor, Math.Round(heroRoom));

    /// <summary>
    /// 片名左边那张海报（集页上是剧照）真正画多大 —— 「海报下方会被裁切，要能看到完整的海报」。
    /// <para>
    /// 把这张图整个装进 <paramref name="maxWidth"/>×<paramref name="maxHeight"/> 那个盒子里，比例一分不动，所以
    /// 一张图要么贴着盒子的宽、要么贴着它的高，另一边比盒子小 —— 那一边少掉的宽度就是从前被裁掉的部分。盒子只会
    /// 收窄不会变宽（结果两边都不超过传进来的上限），于是版面一格都不用改：那一栏的宽由这张图给，带子的高按海报
    /// 那一档算，两个数原来是什么量级现在还是。
    /// </para>
    /// <para>
    /// 从前这一格是写死的 210×300（0.7:1），图按 <c>UniformToFill</c> 铺满它 —— 而服务器上的海报是 2:3
    /// （0.667:1），于是上下各裁掉七八个像素。海报底下那一条常常正是片名和演员表，所以「裁掉一点」屏上就是
    /// 「这张海报缺了一块」。0.7 这个数本身也不是海报的形状，只是一格看着差不多的方框。
    /// </para>
    /// <para>
    /// 传进来的是这张位图自己的像素尺寸，不是服务器那个 <c>PrimaryImageAspectRatio</c>：屏上画的是解出来的这一张，
    /// 而它可能压根不是海报（没有海报的条目退到缩略图，那是一张 16:9 的图）。两个数不一样的时候，对得上屏幕的
    /// 是位图那一份。
    /// </para>
    /// </summary>
    /// <param name="pixelWidth">解出来那张位图的像素宽。</param>
    /// <param name="pixelHeight">同上，像素高。</param>
    /// <param name="maxWidth">这一格最宽能到多少 —— 海报那一档 210，集页那张 16:9 剧照 300。</param>
    /// <param name="maxHeight">这一格最高能到多少 —— 海报那一档 300，集页 169。</param>
    /// <returns>
    /// 这一格该画多大，取整到像素。位图的尺寸还不成话（没解出来、或者报的是 0）就是 null —— 那一格照旧用默认
    /// 那一档，而不是缩成一条线。
    /// </returns>
    public static (double Width, double Height)? StillBox(
        double pixelWidth, double pixelHeight, double maxWidth, double maxHeight)
    {
        if (!Sane(pixelWidth) || !Sane(pixelHeight) || !Sane(maxWidth) || !Sane(maxHeight)) return null;

        var scale = Math.Min(maxWidth / pixelWidth, maxHeight / pixelHeight);

        return (Math.Min(Math.Round(pixelWidth * scale), maxWidth),
            Math.Min(Math.Round(pixelHeight * scale), maxHeight));

        static bool Sane(double value) => double.IsFinite(value) && value > 0;
    }

    /// <summary>
    /// 右上角那张艺术图这一次有没有地方站 —— 「窗口缩小到一定程度自动隐藏」。
    /// <para>
    /// 那一张占带子里的一整栏（<c>DetailPage.CornerArt</c>），栏宽由图自己给，所以它宽多少片名那一栏就窄多少。
    /// 图放大之后这件事咬人了：窄窗口上片名先折成两三行，再窄一点就是「攻壳」两个字一行 —— 而它只是一张装饰，
    /// 片名不是。所以窄到一定程度就整个不画，那一栏退回 0 宽，片名把地方全占回来。
    /// </para>
    /// <para>
    /// 判的是<em>这一页有多宽</em>而不是窗口有多宽：侧边栏展开的时候页面会窄掉两百来像素，而挤着片名的正是页面
    /// 这一头。<see cref="CornerFloor"/> 那个数怎么来的写在它自己身上。
    /// </para>
    /// </summary>
    /// <param name="pageWidth">这一页看得见的那一段有多宽，由页面量出来交进来（<c>DetailPage.OnBodySizeChanged</c>）。</param>
    public static bool CornerFits(double pageWidth) => pageWidth >= CornerFloor;

    /// <summary>
    /// <see cref="CornerFits"/> 那条线：页面窄过这个数就不画右上角那张画了。
    /// <para>
    /// 这个数是从「片名还剩多宽」倒推的：带子左右各让 60，海报那一栏最宽 210，两道栏距 22，右边那张画最宽 320
    /// （<c>DetailPage.CornerArt</c> 上那个上限），于是片名那一栏 = 页宽 − 434。要它至少剩 420（大号片名折两行
    /// 还读得开）就得页宽 ≥ 854 + 320 ≈ 1120。窗口最窄能拖到 900（<c>HostWindow.MinimumWidth</c>），侧边栏一展开
    /// 页面还要再窄两百来 —— 所以这条线是真能碰到的，不是写着好看。
    /// </para>
    /// </summary>
    public const double CornerFloor = 1120;

    /// <summary>
    /// 头图下面那整段至少要多高 —— 「滑到下面不用显示背景了，五颜六色的太丑了」。
    /// <para>
    /// 背景那一层铺满整个窗口并且固定不动，所以底下这一整段必须至少补满视口减掉头图后的空间。没有剧照的那一档
    /// 靠的就是这个下限：页面把它给 BodyRegion，上面先由 HeroTail 占掉音轨和剧情说明的实高，星号行再把剩余高度
    /// 交给不透明 BodySheet，于是短页面的纸面下沿仍然落到窗口底。
    /// </para>
    /// <para>
    /// 有剧照的那一档不再把富余高度交给纸，而是交给尾部（<see cref="TailHeight"/> 就是拿这一支算的），理由在
    /// 那一段上：纸的上沿一进第一屏就是一块盖住剧照的板子。这一支于是有两个用处 —— 那一档的下限，和「富余多少」
    /// 这个数本身。
    /// </para>
    /// <para>
    /// 视口是 0 的那一下是第一次布局之前，那时候量出来的不是「窗口很矮」而是「还没量」，给 0：撑不出高来的
    /// 一版会在第一帧留一格空白，而这个值随后一定会再来一次（<c>DetailPage.OnBodySizeChanged</c>）。
    /// </para>
    /// </summary>
    /// <param name="viewport">页面可视区的高，也就是滚动视图自己的高。</param>
    /// <param name="heroHeight">头图那一格这一次真正的高（<see cref="Height"/> 或 <see cref="EpisodeHeight"/>）。</param>
    public static double BodyHeight(double viewport, double heroHeight) =>
        viewport <= 0 ? 0 : Math.Max(0, Math.Round(viewport) - heroHeight);

    /// <summary>
    /// 头图底下那段压暗的尾部至少要多高 —— 「下方的媒体信息等，要往下滑才能看到」加上「拉大或拉小窗口会导致
    /// 背景图被遮挡」。
    /// <para>
    /// 两句话是同一处：正文那张纸不透明，它的上沿一落进第一屏就是一块盖在剧照上的板子，而它从前落在哪儿只由
    /// 内容定 —— 带子那 460 加上音轨和剧情说明实测的高，跟窗口一点关系没有。于是同一个条目在不同窗口上盖掉的
    /// 量完全不同：1280 宽的窗口上那道边正好落在视口下沿外面（屏上看不见），拉到 1760 宽就有三百像素的纸浮在
    /// 剧照上，而拖的过程里它一直在动。<em>纸的上沿该由视口定，不该由内容定。</em>
    /// </para>
    /// <para>
    /// 富余的高度给尾部而不给纸（<see cref="BodyHeight"/> 那一版给的是纸）：尾部里那一叠是顶对齐的，撑高它只是
    /// 在剧情说明底下多出一段同色的画面 —— 音轨和剧情说明一个像素都不动，那道明暗分界照旧贴在剧情说明下沿；撑高
    /// 纸是把那道不透明的边往上提，也就是这一条要修的东西。撑出来的那一段和罩子末端同色（<see cref="ScrimCeiling"/>），
    /// 所以第一屏下半截仍然透着剧照，不是一块黑。
    /// </para>
    /// <para>
    /// 没有剧照的条目上给 0，那一档照旧走 <see cref="BodyHeight"/>：背后没有图可露，撑起来的只是一段空黑，而
    /// 媒体信息 却要多滚一屏才看得到。
    /// </para>
    /// <para>
    /// 撑<em>多少</em>是这一处来回最多的地方，最后钉在两句话上。一句是「剧情说明底下那段空画面不许跟着窗口长」
    /// —— 「下面越改空位越大」：撑到 <see cref="TailCap"/> 就不再撑，富余的高度交给正文那张纸往上走，窗口越大，
    /// 第一屏底下多出来的是 媒体信息 那一叠<em>内容</em>，而不是一块越来越高的空画面。
    /// </para>
    /// <para>
    /// 另一句是「不许跳」—— 「拉大窗口之后下面突然冒出一大截」：所以这里只有一个 <c>Math.Min</c>，没有台阶。
    /// 纸的上沿就是「带子加封了顶的尾部」，窗口每高一像素它就多露一像素，从 0 开始长。上一版在这儿留了一道
    /// 120 的门槛（露不够那么多就当封顶不存在、整屏归剧照），本意是别露出「一条几乎什么都没有的暗带」，可屏上
    /// 的样子是拖窗口拖到某一下，底下那一叠<em>整块冒出来</em>。宁可在某几个尺寸上先露出纸的上边那一条，也不要
    /// 那一下跳 —— 拖窗口的人看得见的是变化，不是某一个尺寸下的定格。
    /// </para>
    /// </summary>
    /// <param name="viewport">同 <see cref="BodyHeight"/>。</param>
    /// <param name="heroHeight">同 <see cref="BodyHeight"/>。</param>
    /// <param name="artwork">同 <see cref="Height"/>：服务器上有没有那张图。</param>
    public static double TailHeight(double viewport, double heroHeight, bool artwork) =>
        artwork ? Math.Min(BodyHeight(viewport, heroHeight), TailCap) : 0;

    /// <summary>
    /// 尾部硬撑到这么高就不再撑 —— 「下面越改空位越大」。
    /// <para>
    /// 撑起来的是剧情说明底下那一段压暗的空画面，这个数就是它最长能有多长。<em>封的是尾部自己</em>，不是第一屏：
    /// 封第一屏的那一版（860 → 900 → 1000，三个数都是看过截图之后往上抬的）里窗口每高一像素这段空画面就跟着长
    /// 一像素，1974×1082 那个窗口上它有三百多像素 —— 用户圈出来的正是这一块。「阈值调高一点点」和「下面空荡荡
    /// 的」说的从头到尾都是它，而我把它读成了露出来的那一条纸边，于是三回都往反方向调。
    /// </para>
    /// <para>
    /// 280 是照着屏上量的：那一段撑到两百八十上下，剧情说明和第一屏下沿之间还剩一截看得出「图还往下走」的画面，
    /// 再长就是闷着的一块。窗口再高，多出来的高度全归正文那张纸，于是第一屏底下露出来的是 媒体信息 那一叠内容 ——
    /// 高窗口该多看见东西，不是多看见空地。
    /// </para>
    /// <para>
    /// 封在尾部身上就得认一件事：带子的高按页面分档（集页那一档只有两百来，见 <see cref="EpisodeHeight"/>），
    /// 所以同一个窗口上集页会比电影页早一点露出纸来。上一版为了「两种页面同一个答案」才把顶封在第一屏上，可是
    /// 那个答案是拿空画面换的 —— 集页的带子矮，要补满第一屏就得撑出更长的一段空画面，正是这一条要修的东西。
    /// </para>
    /// <para>
    /// <em>下限不封顶</em>：这个数只管「硬撑到多少」，尾部里的内容本来就比它高的时候（长简介、集页那一带集）
    /// 照旧按内容走，一个像素都不裁。
    /// </para>
    /// </summary>
    public const double TailCap = 280;

    /// <summary>
    /// 正文那张纸自己至少要多高 —— 一整屏。
    /// <para>
    /// 有了 <see cref="TailHeight"/> 就必须有这一条：滚到底的那一屏只能有纸（还是「滑到下面不用显示背景了」），
    /// 而纸不再接在尾部的内容后面、跟它合起来正好一屏，它现在从第一屏的下沿起（封顶那一档从更早一点起）。
    /// 纸自己的内容凑不满一屏的条目（没有单集、相似也寥寥的电影）就靠这个下限补上 —— 少了它，滚到底的屏幕上沿
    /// 会漏出一截压暗的剧照。
    /// </para>
    /// <para>
    /// 和 <see cref="TailCap"/> 无关：封顶只改「纸从哪儿开始」，不改「滚到底那一屏必须全是纸」，而后者要的正好
    /// 是一屏。
    /// </para>
    /// </summary>
    /// <param name="viewport">同 <see cref="BodyHeight"/>。</param>
    /// <param name="artwork">同 <see cref="TailHeight"/>：没有剧照的那一档不撑尾部，也就不用撑纸。</param>
    public static double PaperHeight(double viewport, bool artwork) =>
        !artwork || viewport <= 0 ? 0 : Math.Round(viewport);

    /// <summary>
    /// 头图底下那道渐深的罩子：它坐在带子的下沿，上面留 <see cref="ScrimInset"/>，自己高
    /// <see cref="ScrimSpan"/>（最高 <see cref="ScrimHeight"/>），五个停点各说「走了这道罩子的百分之几」和
    /// 「那儿有多黑」。
    /// <para>
    /// 屏上那支画刷就是拿这张表生成的（<c>DetailPage.PaintScrim</c>）—— 从前 DetailPage.xaml 里另写了一份
    /// 同样的五个停点，一份看得见、一份算得出，改一份忘另一份就是标题条上接出一道横缝。现在只有这一份。
    /// 自检照旧拿元素上真正的那几个停点跟这张表对一遍（<c>DetailPage.WashRead</c>）：生成这件事本身也会坏 ——
    /// 画刷没赋上、方向画反、被样式盖掉，都得有人看着。
    /// </para>
    /// </summary>
    public const double ScrimInset = 20;

    /// <inheritdoc cref="ScrimInset"/>
    /// <remarks>
    /// 上限，不是定值：要垫的是海报、片名和那排键那一叠东西，而那一叠多高跟窗口没关系，所以带子高过 460 时
    /// 这道罩子就停在 440，上面剩的全是干净的画面。带子比它矮的时候（集页按那一叠实测给的那一档，见
    /// <see cref="EpisodeHeight"/>）由 <see cref="ScrimSpan"/> 把它收到「带高减去上面那道 <see
    /// cref="ScrimInset"/>」—— 罩子比带子还高就会从带子的上沿溢出去，屏上是顶边突然暗一档，而标题条那层洗
    /// 照着另一套坐标算，两边就错开。
    /// </remarks>
    public const double ScrimHeight = 440;

    /// <summary>
    /// 这一格里那道罩子真正有多高：坐在带子的下沿，上面留 <see cref="ScrimInset"/>，最高
    /// <see cref="ScrimHeight"/>。带子和罩子的高一旦各说各话，标题条上那道横缝就回来了（见
    /// <see cref="TopWash"/>），所以屏上那一块和算浓度的这一支读的是同一个函数。
    /// </summary>
    /// <param name="bandHeight">头图那一格这一次的高；0 或负数当「还没量」，按 <see cref="ScrimHeight"/> 给。</param>
    public static double ScrimSpan(double bandHeight) => bandHeight <= 0
        ? ScrimHeight
        : Math.Min(ScrimHeight, Math.Max(0, bandHeight - ScrimInset));

    /// <summary>
    /// 罩子最浓那一档有多不透明 —— 「太黑了都看不清背景」。
    /// <para>
    /// 这一档原来是满黑（0xFF）：罩子走到带子下沿时把画面压死，而底下那段延到剧情说明的尾部又必须和它同色，
    /// 于是从片名往下整整一屏都是纯黑，那张铺满页面的剧照只在最上面露出一条。压到 0xB8（七成二）之后，画面
    /// 在片名、音轨和剧情说明背后一直看得见，而遮住它的仍然只有正文那张纸。
    /// </para>
    /// <para>
    /// 这个数是这一段里最小最淡的那行字定的，不是挑出来的：简介是 14 的字、用压在图上那支淡墨（#C9D0D9）。
    /// 把剧照最亮处按纯白算，七成二的罩子底下它仍有 6:1 上下的对比；再压到六成就掉到 4.5:1 那条线上，一张
    /// 雪景剧照上那一段就读不动了。所以「再透一点」不是把这个数往下调，是把那段字换成更亮的墨。
    /// </para>
    /// <para>
    /// 尾部那一块（<c>DetailPage.HeroTail</c>）必须写成同一个 alpha，否则罩子末端和它之间横着一道明暗接缝；
    /// 自检对着这一条读（<c>DetailPage.BodySeal</c> 里那句「尾部接住头图末色」）。
    /// </para>
    /// </summary>
    public const byte ScrimCeiling = 0xB8;

    /// <summary>
    /// 五个停点。曲线比等比缩放更陡、末段几乎放平：片名、读数和那排键都落在带子下面那三分之一里，所以浓度
    /// 要在它们头上就爬够（0.62 处已经到 0xAA），而最后那 38% 只走 0xAA→0xB8 —— 末端的斜率接近零，接上那块
    /// 同色的尾部时连一道折角都看不出来。
    /// <para>
    /// 公开是为了让屏上那支画刷直接由它生成，见 <see cref="ScrimInset"/> 上那一段。<c>Along</c> 是「走了这道
    /// 罩子的百分之几」，正好就是渐变停点的 <c>Offset</c>；<c>Alpha</c> 配上 <see cref="ScrimInk"/> 就是那一
    /// 档的颜色。
    /// </para>
    /// </summary>
    public static IReadOnlyList<(double Along, byte Alpha)> ScrimStops { get; } =
        [(0, 0x00), (0.14, 0x40), (0.34, 0x7A), (0.62, 0xAA), (1, ScrimCeiling)];

    /// <summary>
    /// 罩子那支墨的底色。停点之间变的只有 alpha，RGB 全程是这一个。
    /// <para>
    /// 和主题里那支 <c>UiTheme.Scrim</c> 同色，但不是同一件事，所以没有共用一个来源：那一支跟着主题深浅换
    /// alpha，这一支不换 —— 它压的是一张剧照，剧照不会因为用户挑了浅色主题就变亮，白墨的字也照旧压在它上面。
    /// </para>
    /// </summary>
    public static ThemeColor ScrimInk { get; } = ThemeColor.Parse("#0C0E11");

    /// <summary>
    /// 标题栏那一条上要压多浓 —— 0 是不压，<see cref="ScrimCeiling"/>／255 是头图罩子已经走到最浓的末端。
    /// <para>
    /// 为什么要洗：那张剧照和它顶上那层罩子是钉在窗口上的（<c>DetailPage.LiftBackdrop</c>），而这道渐深的罩子
    /// 跟着内容滚。于是滚到一半时，罩子在视口上沿那一行的浓度就成了一道横线：线上面只有剧照，线下面是剧照加
    /// 这么浓的罩子 —— 「一往下拉颜色就不一样了」。这个函数给的就是那一行的浓度，页面把同样浓的 HeroTail
    /// 黑色压到线上面，线两边就一样深。
    /// </para>
    /// <para>
    /// 曲线是罩子自己的曲线，不是另编的一条：<paramref name="offset"/> 就是视口上沿在内容里的位置，所以
    /// <c>TopWash(offset)</c> 读的正是罩子在那一行的 alpha。滚过一格带子之后浓度停在 <see cref="ScrimCeiling"/>，
    /// 接下来经过的是同一档浓度的 HeroTail —— 两边都还透着那张固定的剧照，所以那一行照旧看不出来；正文纸面何时
    /// 到达由 <see cref="PaperCover"/> 另算，不能再把这两个阶段混成一个数。
    /// </para>
    /// </summary>
    /// <param name="offset">页面滚到哪儿了（滚动视图的竖向偏移，也就是视口上沿在内容里的位置）。</param>
    /// <param name="artwork">同 <see cref="Height"/>：服务器上有没有那张图。没有图就没有那道罩子，也就不用洗。</param>
    public static double TopWash(double offset, bool artwork) => TopWash(offset, artwork, ArtHeight);

    /// <inheritdoc cref="TopWash(double, bool)"/>
    /// <param name="offset">同上。</param>
    /// <param name="artwork">同上。</param>
    /// <param name="bandHeight">
    /// 头图那一格这一次的高。集页那一格按里面那一叠实测给（<see cref="EpisodeHeight"/>），罩子跟着收
    /// （<see cref="ScrimSpan"/>），这条曲线也就得按收完的那一块算 —— 不传的那个重载按
    /// <see cref="ArtHeight"/> 算，也就是别的页面上那一格。
    /// </param>
    public static double TopWash(double offset, bool artwork, double bandHeight)
    {
        if (!artwork) return 0;

        var band = bandHeight > 0 ? bandHeight : ArtHeight;
        var span = ScrimSpan(band);
        var top = band - span;
        var y = Math.Clamp(offset, 0, band);
        var fromY = 0d;
        var fromValue = 0d;

        foreach (var (along, alpha) in ScrimStops)
        {
            var at = top + (along * span);
            var value = alpha / 255d;

            // 第一个停点在罩子的上沿上而不是 0 上：那条线以上没有罩子，那一段是平的 0。
            if (y <= at) return at <= fromY ? value : fromValue + ((value - fromValue) * (y - fromY) / (at - fromY));

            (fromY, fromValue) = (at, value);
        }

        return ScrimCeiling / 255d;
    }

    /// <summary>
    /// 真正正文纸面覆盖标题栏的比例。纸面上沿刚到标题栏底边时是 0，继续上滚一个标题栏高度后是 1；中间值
    /// 直接给页面那支黑色/纸色硬边渐变使用。
    /// </summary>
    /// <param name="offset">页面当前滚动位置。</param>
    /// <param name="paperTop">正文纸面在滚动内容里的真实纵坐标。</param>
    /// <param name="titleHeight">标题栏与面包屑合起来、由 TitleWash 覆盖的实际高度。</param>
    public static double PaperCover(double offset, double paperTop, double titleHeight)
    {
        if (offset <= paperTop) return 0;
        if (titleHeight <= 0) return 1;
        return Math.Clamp((offset - paperTop) / titleHeight, 0, 1);
    }

    /// <summary>
    /// 正文纸面覆盖到这么多时，标题栏那行字该不该换回主题自己的墨。
    /// <para>
    /// 压在剧照和黑色 HeroTail 上都用固定浅墨（<c>EgOnScrimBrush</c>）；浅色主题那张纸盖上来之后，白字会
    /// 消失，所以这件事只跟纸面覆盖比例翻，不能再跟头图罩子的浓度翻。
    /// </para>
    /// <para>
    /// 两个门槛而不是一个：一条线上翻墨，指针在那条线附近轻轻一滚就会来回跳。往上要洗到 0.75（标题条这时候
    /// 基本上就是纸的颜色了），退回去要跌到 0.6 以下。
    /// </para>
    /// </summary>
    /// <param name="wash">正文纸面现在覆盖多少，<see cref="PaperCover"/> 给的那个数。</param>
    /// <param name="washed">上一次算出来的结果 —— 回差的那一半，没有它就只是一条线。</param>
    public static bool WashedOver(double wash, bool washed) => wash >= (washed ? 0.60 : 0.75);
}
