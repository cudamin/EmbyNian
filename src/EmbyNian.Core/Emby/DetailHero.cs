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
    /// 一屏的画面，而画面上只有右上角一枚徽标。
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
    /// 头图下面那整段至少要多高 —— 「滑到下面不用显示背景了，五颜六色的太丑了」。
    /// <para>
    /// 背景那一层铺满整个窗口并且固定不动，所以底下这一整段必须至少补满视口减掉头图后的空间。页面把这个
    /// 下限给 BodyRegion：上面先由黑色 HeroTail 占掉音轨和剧情说明的实高，星号行再把剩余高度交给不透明
    /// BodySheet。这样分界线可以下移，短页面的纸面下沿仍然落到窗口底。
    /// </para>
    /// <para>
    /// 视口是 0 的那一下是第一次布局之前，那时候量出来的不是「窗口很矮」而是「还没量」，给 0：撑不出高来的
    /// 一版会在第一帧留一格空白，而这个值随后一定会再来一次（<c>DetailPage.OnBodySizeChanged</c>）。
    /// </para>
    /// </summary>
    /// <param name="viewport">页面可视区的高，也就是滚动视图自己的高。</param>
    /// <param name="artwork">同 <see cref="Height"/>：服务器上有没有那张图。</param>
    public static double BodyHeight(double viewport, bool artwork) =>
        viewport <= 0 ? 0 : Math.Max(0, Math.Round(viewport) - Height(artwork));

    /// <summary>
    /// 头图底下那道渐深的罩子：它坐在带子的下沿，上面留 <see cref="ScrimInset"/>，自己高
    /// <see cref="ScrimHeight"/>，五个停点各说「走了这道罩子的百分之几」和「那儿有多黑」。
    /// <para>
    /// 和 DetailPage.xaml 里那一段必须是同一组数 —— 那边是看得见的那一份，这边是算得出的那一份。两份对不上
    /// 就是标题条上接出一道横缝，所以自检拿元素上真正的那几个停点跟这张表对一遍（<c>DetailPage.WashRead</c>）。
    /// </para>
    /// </summary>
    private const double ScrimInset = 20;

    private const double ScrimHeight = 440;

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
    /// </summary>
    private static readonly (double Along, byte Alpha)[] ScrimStops =
        [(0, 0x00), (0.14, 0x40), (0.34, 0x7A), (0.62, 0xAA), (1, ScrimCeiling)];

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
    public static double TopWash(double offset, bool artwork)
    {
        if (!artwork) return 0;

        var y = Math.Clamp(offset, 0, Height(true));
        var fromY = 0d;
        var fromValue = 0d;

        foreach (var (along, alpha) in ScrimStops)
        {
            var at = ScrimInset + (along * ScrimHeight);
            var value = alpha / 255d;

            // 第一个停点在 ScrimInset 上而不是 0 上：罩子的上沿以上没有罩子，那一段是平的 0。
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
    /// 压在剧照和黑色 HeroTail 上都用固定浅墨（<c>EgOnScrimBrush</c>）；三套浅色主题的纸盖上来之后，白字会
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
