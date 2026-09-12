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
    /// 没有剧照的条目上这一格有多高：一格底色加一层罩子，垫住海报、片名和那排键。
    /// <para>
    /// 和 <see cref="ArtHeight"/> 同值（<see cref="Height"/> 上面那段说明为什么留着两个名字）。
    /// </para>
    /// </summary>
    public const double PlainHeight = 412;

    /// <summary>
    /// 有剧照时这一格有多高。数是内容给的，不是窗口给的：海报 300 高、上下两道边 28 和 24、那排键在自己
    /// 一行上另占 60（44 的键加头上 16），加起来 412 —— 内容正好把这一格填满，一像素富余也不留。
    /// <para>
    /// 写死而不是跟着视口走，就是「一大片空白」那句话的修法：跟着视口走的那一版在 1440 高的窗口上要留出
    /// 一屏的画面，而画面上只有角上一枚记号。
    /// </para>
    /// <para>
    /// 「统一改为在剧名上方显示徽标」之后那一叠字里多了一枚徽标（最高 56，连间距 66），这一格装得下：海报那
    /// 300 仍是两者里高的那个（那一叠连徽标算下来两百三上下，片名折两行也才两百六，都比海报矮）。
    /// <em>集页不一样</em> —— 那一格的高本来就按那一叠实测给（<see cref="EpisodeHeight"/>），屏上真的多了
    /// 一样东西，它就真的长高，这是诚实的。
    /// </para>
    /// <para>
    /// 2026-09-12 从 460 压到 380 —— 「剧页面和电影页面上方空位太多了，把下面的组件往上移动一些」：460 的
    /// 年代海报头顶上留着 108 的空画面，红框圈的就是它。同一天那排键从片名那一栏里搬出来、自己占一行（他：
    /// 「继续播放 从头开始还有后面的那些图标单独一行」），这一格跟着长回 412 —— 长的这 60 就是那排键那一行，
    /// 海报那一栏的 300 一个像素没动。两档仍然同高，<see cref="Height"/> 的判据留着，是因为按「有没有图」
    /// 分档的版面值不止带高一处（背景层、尾部、纸面下限都跟它走）。
    /// </para>
    /// <para>
    /// 这一格里的内容靠下站（集页的宽版式除外，那一页靠上，见 <c>DetailViewModel.HeroContentAlignment</c>）：
    /// 写死的带高比内容高一点，那点富余留在这三页的头顶上正是原来那个样子，海报和那一栏字的下沿对齐。
    /// </para>
    /// </summary>
    public const double ArtHeight = 412;

    /// <summary>
    /// 宽版式那排键底下的断点进度那一行连行距占多高：条和「剩余 x 分钟」一行 16（12 号字的行高，条自己
    /// 定死 4 高，行听字的），头上再隔 12 的行距 —— 共 28。
    /// <para>
    /// 只给「有断点」的条目加（<c>DetailViewModel.HeroLayoutHeight</c>）：412 那一档是按没有这一行的内容
    /// 量的，进度行一露头带子就得长这么多，不然底对齐的那一叠从带子底下冒出去。没有断点时这一行整个
    /// 收着，一个像素都不占。集页不在此列 —— 那一格格的高按内容实测给（<see cref="EpisodeHeight"/>），
    /// 这一行的账已经走在实测里。
    /// </para>
    /// </summary>
    public const double ProgressRoom = 28;

    /// <summary>
    /// 这一格的高：有剧照、没有剧照两档同值（见 <see cref="ArtHeight"/> 那段「并轨」）；判据继续传，分档
    /// 落在带高之外的版面值上。
    /// </summary>
    /// <param name="artwork">
    /// 服务器上有没有这一张图 —— <see cref="ItemArtwork.Hero"/>，不是「位图解出来了没有」。
    /// <para>
    /// 这两句话差着一次网络往返，而版面按它分档（背景层、尾部、纸面下限都跟着翻），所以拿后一句当判据就是
    /// 「先按没有图布一遍，图到了再翻一遍」 —— 屏上看着就是「点击主页封面后窗口会闪一下，然后才会进入页面」。
    /// 列表接口回来的条目已经带着 ImageTags，所以前一句在导航那一刻就答得出，第一帧的版面就是最后的版面。
    /// </para>
    /// </param>
    public static double Height(bool artwork) => artwork ? ArtHeight : PlainHeight;

    /// <summary>
    /// 集页那一格最矮能到多少，同时也是还没量到那一叠字和键有多高时用的那个数（见 <see cref="EpisodeHeight"/>）。
    /// 一张 16:9 剧照加上下那两道留白就是这个量级，所以第一帧按它布出来的版面和量完之后只差几个像素，屏上看不
    /// 出挪动 —— 反过来拿 <see cref="ArtHeight"/> 兜底的那一版每进一次集页都要从 460 缩到实测值，那是一次看得
    /// 见的跳。比这个数再矮也不是「紧凑」而是「什么都放不下」。
    /// </summary>
    public const double EpisodeFloor = 200;

    /// <summary>
    /// 右上角那张艺术图最宽能画到多少 —— 「给集页面右上角添加艺术图」。
    /// <para>
    /// 320 → 480 是用户第二次要的「放大集页面右侧的艺术图」（第一次是 146 → 180，配的是「再把艺术图调大一些」）。
    /// 两个数同时是这一格的解码宽度（<c>DetailViewModel.CornerDecodeWidth</c>）：画出来的宽和取图时用的宽一旦
    /// 分岔，这张图要么糊要么白占内存。
    /// </para>
    /// <para>
    /// 它和 <see cref="CornerHeight"/> 一起定死这一格的形状，所以这一格的<em>高</em>是可算的、不跟着版面走 ——
    /// 这一栏的宽会挤到中间那一栏（片名折行的位置随之变），而一叠字的实测高又回头喂带高（<see
    /// cref="EpisodeHeight"/>）；让这一格跟着实测走，两个数就互相追着改，屏上是这张画缓缓地缩（同
    /// <c>DetailViewModel.HeroLayoutWidth</c> 上那段「不能读回缩放后的内容高度」）。
    /// </para>
    /// </summary>
    public const double CornerWidth = 480;

    /// <summary>
    /// 右上角那张艺术图最高能画到多少 —— 同 <see cref="CornerWidth"/>，480×270 正好一格 16:9。
    /// <para>
    /// 这一格<b>算进集页的带高</b>（<c>DetailViewModel.HeroRoom</c>）：艺术图顶对齐摆在这一格里，带子矮过
    /// 它就是一截画压在底下的音轨那一行上。从前 180 比那一叠字键的实测高（两百六七）矮，压不着；抬到 270
    /// 之后就压得着了。
    /// </para>
    /// </summary>
    public const double CornerHeight = 270;

    /// <summary>
    /// 没有画面可铺时那一格的高：由它里面那一叠（剧照、片名、副标题、读数、那排键）连上下留白实测给出。
    /// 两处走它 —— 集页的宽版式，和<em>没有背景图可铺</em>的紧凑版式。
    /// <para>
    /// 有了图中那种画面可铺的档案，这一支让位给 <see cref="CompactHeight"/>（画面条和这一叠取大）；没有的
    /// 那一档从前写死 <see cref="PlainHeight"/>，而屏上就是「窄窗口时候上方有大片空位」（2026-09-12）：底对齐
    /// 的那一叠头上空着一百多像素，什么都没有。现在两处同一个答案 —— 带子不多不少就是那一叠要的高。
    /// </para>
    /// <para>
    /// 规矩和别的页面是同一条 —— 高由内容定、不跟窗口走（<see cref="ArtHeight"/> 上那一段），只是集页量在
    /// 运行时。为什么不能跟着用电影页那一档：单集配的是一张 16:9 剧照，比 2:3 的海报矮一大截，那一叠字也比
    /// 电影页少两行，而这一叠是底对齐的 —— 给它整档带高就等于在它头上留出上百像素只有画面的地方，「集拉大
    /// 窗口后会导致左上角空空的，画面不协调，电影那边处理的就很好」说的正是那一块。电影页之所以「处理的很好」，
    /// 恰恰因为 412 就是按那一页的内容量的（装下海报 300、那排键那一行 60 和两道边）。
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
    /// 封面随窗口加宽的那一档斜率：参考宽以上每宽一像素，封面宽多少。旧规矩是等比
    /// （<paramref name="baseWidth"/> ÷ 参考宽 ≈ 0.16），2026-09-12 他看完实拍改口「拉窄窗口的时候封面不要
    /// 缩小，拉长窗口的时候封面要稍微放大」—— 缩小那一半整个去掉，放大这一半放缓到 0.1。真正的上限不在这
    /// 个数上：FitStill 里那道「不超过旁边文字栏」的高上限（2:3 的封面高得比宽快）才是它在实际页面上的顶。
    /// </summary>
    public const double StillGrowPerPixel = 0.1;

    /// <summary>
    /// 集页左上角那张封面跟多宽的窗口走 —— 2026-09-12 他两句话定的形状：「集页面左上角的封面要跟随窗口的宽度
    /// 放大和缩小」，同日看完实拍又改成「拉窄窗口的时候封面不要缩小，拉长窗口的时候封面要稍微放大，适配右边
    /// 组件的大小」。
    /// <para>
    /// 于是分两段：参考宽（1280，<c>DetailViewModel.HeroReferenceWidth</c>）以下封面不缩，<paramref
    /// name="baseWidth"/> 到底 —— 从前等比缩小的那一半（1024 上八成）整个去掉；参考宽以上每像素长
    /// <see cref="StillGrowPerPixel"/>，比旧的等比斜率慢一截 —— 「稍微放大」。高由 <see cref="StillBox"/> 按位图
    /// 自己的形状配，实际画多大还过 FitStill 那道「不超过旁边文字栏」的高上限。
    /// </para>
    /// <para>
    /// 紧凑版式不在此列：那一档按参考图是贴着页宽的大封面，照旧用 <paramref name="baseWidth"/>。页面还没量到
    /// 宽（0，第一次布局之前）也照旧用基宽，等量到了再来。
    /// </para>
    /// </summary>
    /// <param name="pageWidth">这一页看得见的那一段有多宽。</param>
    /// <param name="baseWidth">参考宽上这一格画多宽。</param>
    /// <param name="referenceWidth">版式排内容的参考宽。</param>
    public static double StillWidth(double pageWidth, double baseWidth, double referenceWidth)
    {
        if (baseWidth <= 0 || referenceWidth <= 0 || pageWidth <= 0) return baseWidth;
        if (pageWidth <= referenceWidth) return baseWidth;

        return baseWidth + (pageWidth - referenceWidth) * StillGrowPerPixel;
    }

    /// <summary>
    /// 右上角那张艺术图这一次最宽能画多少 —— 题栏保底。
    /// <para>
    /// 角图占的是第三栏（Auto），中间题栏拿剩下的；窗口窄下来的时候它必须让位，不然题栏会被挤得放不下一行
    /// 片名。保底的数是参考宽（1280）上题栏本来就有的那一份：1280 − 内边距 96（板沿 28 加板内 20，两道）−
    /// 封面 352（那一档的 <see cref="StillWidth"/>）− 两道栏距 44 − 角图 <see cref="CornerWidth"/> ＝ 308。
    /// 参考宽以上这道钳制不咬合（角图照旧 480 封顶、题栏只宽不窄），以下角图线性让位 —— 2026-09-12 他那句
    /// 「徽标跟艺术图不要动」说的是位置和画法不动，不是让它在窄窗口上把片名挤没。
    /// </para>
    /// </summary>
    /// <param name="pageWidth">这一页看得见的那一段有多宽。</param>
    /// <param name="stillWidth">封面这一次画多宽（<see cref="StillWidth"/>）。</param>
    public static double CornerBoxWidth(double pageWidth, double stillWidth)
    {
        const double insets = 96, spacings = 44, minTitle = 308;

        return Math.Clamp(pageWidth - insets - stillWidth - spacings - minTitle, 0, CornerWidth);
    }

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
    /// 头图左边那张封面（<see cref="StillBox"/> 画的那张）这一档版面要不要画 —— 2026-09-12 一天四句，
    /// 最后落在「紧凑版式（<paramref name="compact"/>）下剧页和电影页不画」：早上他问「剧页面和电影页面的
    /// 封面怎么没了？」，把「紧凑版式不画海报」的旧规矩整个作废；中午「剧页面和集页面窄窗口下不用显示封面」
    /// 点走剧、集两页；傍晚一句「集页面的封面留着」又把集页还了回来；再一句「电影页面窄窗口也要隐藏左上角的
    /// 封面」把电影页收了回去。最终收掉的是剧、电影两页那张 —— 剧页那张，画面条铺的本来就是同一部剧的图，
    /// 单列里它是纯占地方；季、集两页照旧画。
    /// <para>
    /// 页面按条目类型认（<paramref name="type"/>，Emby 服务器的 <c>Type</c> 原文）；类型还没到手
    /// （<c>null</c>，这一拍连页宽都还是 0，紧凑根本没成立）当要画。图解出来没有不在这句话的范围里（那是
    /// <c>DetailViewModel.StillVisibility</c> 的另一半），这里只回答「这一档版面要不要它」。取图那一头也不看它
    /// （<c>StillDecodeWidth</c> 照旧取）：窗口从窄拉宽跨过 <see cref="CompactFloor"/> 的那一下答案从不要翻成要，
    /// 图已经在手，封面就地回来，不用再等一趟往返。
    /// </para>
    /// </summary>
    public static bool ShowsStill(bool compact, string? type) =>
        !(compact && type is EmbyItemType.Series or EmbyItemType.Movie);

    /// <summary>
    /// 这一页这次走哪套版式 —— 页面窄过 <see cref="CompactFloor"/> 就换成单列的紧凑版式（参考图上那种：等比的
    /// 画面条占满页宽，片名压在画面下沿，文件选项、播放、进度和带字的那排操作排在画面底下的暗区里，一列到底，
    /// 不再缩小整个桌面版式）。
    /// <para>
    /// 「缩小到一定程度后 UI 参考手机版式切换」的那条线。桌面版式在 <see cref="CompactFloor"/> 以上照旧：
    /// 组件不缩小，靠行装不下就换行把窄页面排开（2026-09-12「收窄窗口后组件要自动换行」—— 从前 1024 到 1280
    /// 那一段整体等比缩小，缩到八成的组件又小又松，换行把它替了）。0 是「还没量」（第一次布局之前），那一档照走
    /// 桌面版式 —— 先按宽的布一遍、量出来再换，就是「点击主页封面后窗口会闪一下」那一类跳。
    /// </para>
    /// </summary>
    /// <param name="pageWidth">这一页看得见的那一段有多宽，由页面量出来交进来（<c>DetailPage.OnBodySizeChanged</c>）。</param>
    public static bool IsCompact(double pageWidth) => pageWidth > 0 && pageWidth < CompactFloor;

    /// <summary>
    /// <see cref="IsCompact"/> 那条线：页面窄过这个数就换紧凑版式。
    /// <para>
    /// 1024，不是更早那一版的 720 —— 「我是说窄窗口下组件不够紧凑」（2026-09-12，配图是 850 宽的窗口）：
    /// 720 到 1024 那一段从前归「桌面版式整体缩小」，缩到六七成的组件又小又松，正是他说的那副样子。单列版式
    /// 在这个宽度上摆得开（参考图本来就是手机版式，平板宽度只是它更宽的样子），所以线抬到 1024 —— 桌面缩小
    /// 只留给 1024 到 1280 那一段缩到八成以上还撑得开的宽度，再往上就是原版。
    /// </para>
    /// </summary>
    public const double CompactFloor = 1024;

    /// <summary>
    /// 紧凑版式里头图那一格有多高，分两档 —— 判据是「有没有画面可铺」（<paramref name="artwork"/>）。
    /// <list type="bullet">
    /// <item>有：画面条按「页宽 × 高宽比」给（宽 600 的页面上一张 16:9 就是 337 高），但至少要站得下片名那一叠
    /// （<paramref name="contentRoom"/>，实测进来的）：比画面条还高的那一叠在底对齐的摆法里会从带子里冒出去。
    /// 画面条比那一叠矮的时候带子就地长高，背景那张图铺满带子（<see cref="PictureHeight"/> 的带子那一档），
    /// 画面上下多裁一点 —— 挤的是画面，不是字。</item>
    /// <item>没有：带子就是那一叠要的高（<see cref="EpisodeHeight"/>），一像素不多。集页在紧凑版式下永远到这一
    /// 档（那一页不铺背景图，见 <c>DetailViewModel.SpreadsBackdrop</c>），从前它写死
    /// <see cref="PlainHeight"/>，而屏上就是「窄窗口时候上方有大片空位」（2026-09-12）：底对齐的那一叠头上空着
    /// 一百多像素。画面条和那一叠取大在有画面的时候是对的（高出来的是画面），没有画面的时候就是把空气算成了
    /// 内容。</item>
    /// </list>
    /// </summary>
    /// <param name="pageWidth">这一页有多宽。</param>
    /// <param name="heightRatio">背景那张位图的高 ÷ 宽（16:9 是 0.5625），解出来后由视图模型送来。</param>
    /// <param name="contentRoom">片名那一叠连海报、带子上下留白实测要占多高（<c>DetailViewModel.HeroRoom</c>）。</param>
    /// <param name="artwork">
    /// 这一页背后有没有那张图 —— 同 <see cref="BackdropShown"/> 那一头的判据（<c>DetailViewModel.HeroArt</c>），
    /// 不是「位图解出来了没有」。
    /// </param>
    public static double CompactHeight(double pageWidth, double heightRatio, double contentRoom, bool artwork)
    {
        if (!artwork) return EpisodeHeight(contentRoom);

        var strip = SaneRatio(heightRatio) ? Math.Max(pageWidth, 0) * heightRatio : 0;
        return Math.Max(Math.Max(strip, contentRoom), 0);
    }

    /// <summary>
    /// 背景那一张这一次画多高 —— 「窗口收窄时背景图要等比例缩放」。
    /// <para>
    /// 从前它是一支铺满整层的 <c>ImageBrush</c>（UniformToFill，钉在视口上）：窗口越窄，按高撑满的那张被裁得
    /// 越狠，图「人不动、衣裳在缩」—— 屏上是越窄越放大。现在它画在一个跟宽走的高度的盒子里：宽的窗口上这个
    /// 高被视口封顶，照旧铺满第一屏；窄到「页宽 × 图的高宽比」够不着视口之后，盒高就跟着页宽走 —— 整张图等比
    /// 缩小、一像素不裁，盒子底下多出来的那一段交给压暗的尾部，参考图上正是这个样子。
    /// </para>
    /// <para>
    /// 至少垫住带子（<paramref name="bandHeight"/>）：罩子画在带子里，画面条比带子矮的时候罩子的下半就站在
    /// 空底上 —— 至少垫住它，挤的才是画面而不是字。视口还没量到（0，第一次布局之前）不封顶，先按等比那条给。
    /// </para>
    /// </summary>
    /// <param name="pageWidth">这一页有多宽。</param>
    /// <param name="heightRatio">背景那张位图的高 ÷ 宽；不成话（没解出来、或是 0）就按 16:9（0.5625）兜底。</param>
    /// <param name="viewport">页面可视区的高；0 是「还没量」。</param>
    /// <param name="bandHeight">头图那一格这一次的高 —— 画面条的下限。</param>
    public static double PictureHeight(double pageWidth, double heightRatio, double viewport, double bandHeight)
    {
        var ratio = SaneRatio(heightRatio) ? heightRatio : 9d / 16;
        var height = Math.Max(Math.Max(bandHeight, Math.Max(pageWidth, 0) * ratio), 0);
        return viewport > 0 ? Math.Min(viewport, height) : height;
    }

    private static bool SaneRatio(double value) => double.IsFinite(value) && value > 0;

    /// <summary>
    /// 背景那一张这次被裁掉多少 —— 0..1，只在「铺满」那一档非零（<see cref="PictureHeight"/> 被视口封顶、
    /// 图按宽撑满、底部被裁掉的那一档），画面条那一档整张图都看得见，给 0。
    /// <para>
    /// 「当窗口拉宽导致背景图下方被裁切的时候，触发背景图模糊，裁切越多越模糊」（2026-09-12）的自变量：
    /// 拉宽让「页宽 × 高宽比」长过视口，图按宽撑满、顶部对齐，裁掉的全在下方；裁掉的占比就是 1 − 画面条高 ÷
    /// 视口。它喂 <see cref="BackdropBlur.CropRadius"/>（基础半径 + 占比 × 加量，量化成档）。
    /// </para>
    /// </summary>
    /// <param name="pageWidth">这一页有多宽。</param>
    /// <param name="heightRatio">背景那张位图的高 ÷ 宽，同 <see cref="PictureHeight"/>。</param>
    /// <param name="viewport">页面可视区的高；0 是「还没量」。</param>
    public static double PictureCrop(double pageWidth, double heightRatio, double viewport)
    {
        if (viewport <= 0) return 0;

        var ratio = SaneRatio(heightRatio) ? heightRatio : 9d / 16;
        var natural = Math.Max(pageWidth, 0) * ratio;

        return natural <= viewport ? 0 : Math.Clamp(1 - viewport / natural, 0, 1);
    }

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

    /// <inheritdoc cref="TailHeight(double, double, bool, double, double)"/>
    /// <remarks>
    /// 画面铺满第一屏的那一档（<paramref name="pictureBottom"/> = <paramref name="viewport"/>）：老规矩原样，
    /// 旧调用和老测试走的都是它。
    /// </remarks>
    public static double TailHeight(double viewport, double heroHeight, bool artwork, double paperLine) =>
        TailHeight(viewport, heroHeight, artwork, paperLine, viewport);

    /// <summary>
    /// 头图底下那段压暗的尾部至多撑多高 —— 「下方的媒体信息等，要往下滑才能看到」加上「拉大或拉小窗口会导致
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
    /// 「透着剧照」以画面的下沿为界（<see cref="PictureHeight"/>）：背景改成等比缩放之后，画面条底下已经没有
    /// 画面，尾部再往下撑就只是一段空黑 —— 中间一大块空位、纸面和货架被压到老下面去（2026-09-12 他指着的
    /// 那张截图）。所以这一版多了第三道上限：尾部至多撑到画面的下沿，底下的空当交给正文的内容自动补上。画面
    /// 铺满第一屏的那一档这道上限就是第一屏本身，版面一像素不动（无参那个重载就是它，老测试走的也是它）。
    /// </para>
    /// <para>
    /// 没有剧照的条目上给 0，那一档照旧走 <see cref="BodyHeight"/>：背后没有图可露，撑起来的只是一段空黑，而
    /// 媒体信息 却要多滚一屏才看得到。
    /// </para>
    /// <para>
    /// 撑<em>多少</em>是这一处来回最多的地方，最后钉在用户的两句话上（2026-09-05）：「窗口大于1600*900后开始
    /// 显示下方的黑边，小于1600*900时海报占满整个窗口」，加上分界跟显示器走——「显示器分别为4k时设定为1920×1080
    /// 2k时1600×900 1080p时1366×768」。尾部一直撑到把第一屏补满为止，但纸面的上沿最深走到
    /// <paramref name="paperLine"/> 那条线为止（阈值窗口高换算成视口，见 <see cref="PaperLineFor"/>）—— 线以内
    /// 四种页面都是「画面铺满第一屏」，过了线纸面带着下一节的内容一像素一像素地回来。带子矮的
    /// 页面（集页）和带子高的页面（电影页）由此在同一个窗口上给出同一个答案：纸面的位置只看这条线，不看带子
    /// 多高、内容多长。
    /// </para>
    /// <para>
    /// 另一句是「不许跳」—— 「拉大窗口之后下面突然冒出一大截」：所以这里只有 <c>Math.Min</c>，没有台阶。
    /// 纸的上沿就是「带子加封了顶的尾部」，窗口每高一像素它就多露一像素，从 0 开始长。上一版在这儿留了一道
    /// 120 的门槛（露不够那么多就当封顶不存在、整屏归剧照），本意是别露出「一条几乎什么都没有的暗带」，可屏上
    /// 的样子是拖窗口拖到某一下，底下那一叠<em>整块冒出来</em>。宁可在某几个尺寸上先露出纸的上边那一条，也不要
    /// 那一下跳 —— 拖窗口的人看得见的是变化，不是某一个尺寸下的定格。
    /// </para>
    /// </summary>
    /// <param name="viewport">同 <see cref="BodyHeight"/>。</param>
    /// <param name="heroHeight">同 <see cref="BodyHeight"/>。</param>
    /// <param name="artwork">同 <see cref="TailHeight(double, double, bool, double)"/>：服务器上有没有那张图。</param>
    /// <param name="paperLine">
    /// 纸面上沿的线，视口坐标 —— 阈值窗口的高（<see cref="PaperLineFor"/>）减掉标题栏加面包屑那截，由页面量好
    /// 传进来。0 是「显示器还没读到」：不撑，纸面回到由内容定的位置，等线到了再来。
    /// </param>
    /// <param name="pictureBottom">
    /// 背景那一张的下沿，视口坐标 —— <see cref="PictureHeight"/> 算出来的那个数。尾部是「压暗的画面」，画面条
    /// 以下没有画面可压，撑过去就是空位。
    /// </param>
    public static double TailHeight(
        double viewport, double heroHeight, bool artwork, double paperLine, double pictureBottom) =>
        artwork && paperLine > 0
            ? Math.Max(0, Math.Round(Math.Min(
                Math.Min(BodyHeight(viewport, heroHeight), Math.Max(0, paperLine - heroHeight)),
                Math.Max(0, pictureBottom - heroHeight))))
            : 0;

    /// <summary>
    /// 纸面上沿那条线对应的<em>窗口</em>高，跟着显示器走 —— 用户原话（2026-09-05）：「显示器分别为4k时设定为
    /// 1920×1080 2k时1600×900 1080p时1366×768」，接在他前一句「窗口大于1600*900后开始显示下方的黑边，
    /// 小于1600*900时海报占满整个窗口」后面。判的是<em>显示器</em>（这张屏有多大），给的是<em>窗口</em>
    /// （黑边从多高的窗口开始回来）：4K（屏高 ≥ 2000：2160、2880）→ 1080，2K（屏高 ≥ 1300：1440，带鱼屏
    /// 3440×1440 一并落这儿）→ 900，其余（1080p 一类：1080、1200、768）→ 768。
    /// <para>
    /// 宽度不参与：黑边是竖着的事，同一条线上 3440×1440 的带鱼屏和 2560×1440 一个答案。0（显示器读不到）就是 0，
    /// 调用方那一线不撑 —— 纸面回到由内容定的位置，等下一次量到了再来。
    /// </para>
    /// <para>
    /// 换算成视口（减掉标题栏加面包屑那截，自检量出来 68 上下）是页面的事：那截高度跟着主题字号和面包屑的显隐
    /// 走，Core 里没有一个能读到它的地方。这条线同时就是「下面越改空位越大」的封顶：剧情说明（集页是集带）底下
    /// 那段透着画面的暗区最长是「线 − 带子 − 内容高」，窗口再高也是这个数，多出来的全归纸 —— 8 月在 1974×1082
    /// 上圈过的三百像素空画面就是「撑满整个视口」的写法，这条线把它限死了。
    /// </para>
    /// </summary>
    /// <param name="monitorWidth">窗口所在显示器的像素宽（<c>HostWindow.MonitorSize</c>）。</param>
    /// <param name="monitorHeight">同上，像素高。</param>
    public static double PaperLineFor(int monitorWidth, int monitorHeight)
    {
        if (monitorHeight >= 2000) return 1080;
        if (monitorHeight >= 1300) return 900;
        return monitorHeight > 0 ? 768 : 0;
    }

    /// <summary>
    /// 正文那张纸自己至少要多高 —— 一整屏。
    /// <para>
    /// 有了 <see cref="TailHeight"/> 就必须有这一条：滚到底的那一屏只能有纸（还是「滑到下面不用显示背景了」），
    /// 而纸不再接在尾部的内容后面、跟它合起来正好一屏，它现在从第一屏的下沿起（封顶那一档从更早一点起）。
    /// 纸自己的内容凑不满一屏的条目（没有单集、相似也寥寥的电影）就靠这个下限补上 —— 少了它，滚到底的屏幕上沿
    /// 会漏出一截压暗的剧照。
    /// </para>
    /// <para>
    /// 和 <see cref="PaperLineFor"/> 无关：那条线只改「纸从哪儿开始」，不改「滚到底那一屏必须全是纸」，而后者要
    /// 的正好是一屏。
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
    /// 上限，不是定值：要垫的是海报、片名和那排键那一叠东西，而那一叠多高跟窗口没关系。电影、剧、季那档
    /// 带子 412（见 <see cref="ArtHeight"/>），罩子由 <see cref="ScrimSpan"/> 收成 392 正好坐满；上限 440
    /// 只在更高的带子上咬合（集页按那一叠实测给的那一档，见 <see cref="EpisodeHeight"/>，片名折行时会高过
    /// 440）—— 罩子比带子还高就会从带子的上沿溢出去，屏上是顶边突然暗一档，而标题条那层洗照着另一套
    /// 坐标算，两边就错开。
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
