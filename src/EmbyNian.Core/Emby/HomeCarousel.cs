namespace EmbyNian.Emby;

/// <summary>
/// 主页轮播 —— 「参考主页轮播大图版-misty-4.9.css 给主页轮播功能」: which of the home page's items become the
/// big picture at the top of it, how far one press moves, how tall that band is, and what one slide says.
/// <para>
/// Pure and in Core for the same reason as <see cref="ItemArtwork"/>: every rule here is a decision about
/// content rather than about pixels — one slide per show, no slide without wide artwork, wrap around at
/// either end — and each of them would otherwise live inside an event handler where nothing can ask it a
/// question. What is left on top of this (<c>HomeBanner</c>) is fades, a timer and two chevrons.
/// </para>
/// </summary>
public static class HomeCarousel
{
    /// <summary>
    /// How many slides at most. Eight, because the dots along the bottom edge are how a reader tells where
    /// in the set they are and a row of twenty dots says nothing — and because a slide near the front holds
    /// a decoded backdrop, so the slot count is a memory budget as much as a design one.
    /// </summary>
    public const int Slots = 8;

    /// <summary>
    /// The shortest band there is, whatever the window does. Below this the picture is a letterbox slit and
    /// the title standing in front of it has nowhere to sit; a window that narrow gets a band that is too
    /// tall for it rather than a strip of nothing.
    /// <para>
    /// 这个下限现在是**字块唯一的保险**。字块 2026-09-05 挪到了带子的右下角（「把红框框出来的移到右下角」），贴着
    /// 下沿站，边距是 <c>HomeBanner</c> 里两个写死的数 —— 所以这里从前那条 <c>InfoDrop</c>（「字从正中往下沉多少」，
    /// 按带高算的一条纯函数）连它的两条单测一起删掉了，别去别处找它。带子矮到装不下那一块字时，被剪掉的是片名那
    /// 一头，而 <c>HomeBanner.State</c> 把「字块头顶还剩多少」报进自检，好让那件事看得见。
    /// </para>
    /// </summary>
    public const double MinHeight = 240;

    /// <summary>
    /// 量不到自己宽度的那一下这条带有多高（第一帧，还有自检里那份没上树的控件）。640 就是开窗那一档的带高
    /// （浏览区 1422 减掉右栏 280，再按 <see cref="WindowAspect"/> 换成高），所以第一帧已经是第二帧的样子。
    /// </summary>
    public const double UnmeasuredHeight = 640;

    /// <summary>
    /// 这一页的形状，宽 ÷ 高 —— 16:9，也就是服务器发来的宽剧照的形状。
    /// <para>
    /// **剧照本身在任何窗口形状下都不裁**（<see cref="Height"/> 那一块里按自己的比例整张画出来），而 2026-09-05
    /// 之后带高就是照这个形状从带宽算出来的，所以**图正好铺满带子、上下一条底色都不留** —— 「封面固定到最上方，
    /// 上下不要有黑边」。只有一种例外：算出来比一屏还高的时候（超宽屏），带高被一屏封住，那时底色留在左右。
    /// </para>
    /// <para>
    /// 从前有一个开关（「锁定窗口比例大小」）把浏览中的窗口一直按住在这个形状上，2026-09-05 按用户的话删掉了。
    /// 所以现在这个数有两处用：带高按它从带宽算（<see cref="Height"/>）、开窗那一档的默认宽度就是这个形状
    /// （<c>HostWindow</c>）。第三处从前是主页那个徽标按它算剧照的四周留白（<c>PlaceLogo</c>），2026-09-05 徽标
    /// 挪进字块之后那个方法整个删了。
    /// </para>
    /// <para>
    /// 「继续观看完整落在第一屏里」和这个形状没有关系，别把两者绑在一起 —— 那一排现在是第一屏右边那一栏
    /// （<see cref="RailWidth"/>），本来就整个在第一屏里，窗口是什么形状都成立。中间有一版把那条锁改成「视口减掉
    /// 第一排货架」来兑现那句话，代价是带子不再是整屏、也就不再有「不裁切」这个承诺；那一版已经删了。
    /// </para>
    /// <para>
    /// **一个数都不用扣掉，横竖都不用。** 从前横向要扣掉侧边栏那一条（「计算比例时要排除侧边栏」，一个叫
    /// <c>SideRail</c> 的常数，49 = 收起来的窄条 48 加它右边那道竖线）—— 侧边栏 2026-09-06 删掉之后页面就是整个
    /// 客户区，那个常数连它在 <c>HostWindow</c> 的两处用法一起没了。竖向本来就不扣：外壳那两行是画在页面
    /// <em>上面</em>而不是上方的，主页上图从窗口的顶边就开始 —— 整个客户区的高都是图的。
    /// </para>
    /// </summary>
    public const double WindowAspect = 16.0 / 9.0;

    /// <summary>The ratio in the language a report says it in, rather than as 1.78:1.</summary>
    public const string WindowAspectLabel = "16:9";

    /// <summary>
    /// How long a slide stands before the carousel moves on, and how long it waits again after someone
    /// presses a chevron. Eight seconds is long enough to read the synopsis of the one slide you care about
    /// and short enough that the band does not look frozen.
    /// </summary>
    public static readonly TimeSpan Dwell = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 这条带有多高：**就是那张 16:9 剧照在这个带宽下的高度** —— 「封面固定到最上方，上下不要有黑边」。带子因此
    /// 正好被一张不裁切的剧照铺满，图贴着窗口的顶边，上下一条底色都不留；第一屏剩下的那一截露出下面第一排横着的
    /// 货架，那也是「底下还有东西」唯一的招牌。
    /// <para>
    /// 2026-09-05 之前这一块占满整整一屏（「轮播页面占满窗口」），而右边那一栏继续观看占掉一段宽之后带子就比 16:9
    /// 高了，于是图的上下各留一条底色（他那台窗口上约 145 像素）—— 这一版换掉的正是那两条。
    /// </para>
    /// <para>
    /// 一屏是上限而不是目标：超宽屏上「带宽 ÷ 16 × 9」会比一屏还高，那时带高被一屏封住，图改成吃满带高、底色留在
    /// 左右（<c>HomeBanner.PictureRead</c> 两档都认）。下限 <see cref="MinHeight"/> 兜的是矮到不像话的窗口。
    /// </para>
    /// <para>
    /// 量不到带宽的那一下（第一帧、还有自检里那份没上树的控件）用 <see cref="UnmeasuredHeight"/>，那就是开窗那一
    /// 档的带高，所以第一帧已经是第二帧的样子。
    /// </para>
    /// </summary>
    /// <param name="viewport">The window's client height, or 0 for 「not measured yet」.</param>
    /// <param name="width">这条带自己有多宽（页宽减掉右栏），或者 0 表示还没量到。</param>
    public static double Height(double viewport, double width)
    {
        if (width <= 0) return UnmeasuredHeight;

        var picture = Math.Round(width / WindowAspect);
        var ceiling = viewport > 0 ? Math.Max(MinHeight, Math.Round(viewport)) : double.MaxValue;

        return Math.Clamp(picture, MinHeight, ceiling);
    }

    /// <summary>
    /// 第一屏右边那一栏「继续观看」有多宽：一张卡（<see cref="RailCard"/>）加它两边的留白。第一屏是并排两栏 ——
    /// 左边 <see cref="Height"/> 那条大图，右边这一栏竖着排的继续观看，所以这个数同时也是「大图少掉多少宽」。
    /// <para>
    /// 不按窗口宽收窄，也不会在窄窗口上让位：这一栏最宽 280（<see cref="RailCard"/> 的上限加两个
    /// <see cref="RailInset"/>），而窗口最小 900 宽（<c>HostWindow.MinimumWidth</c>），减掉这一栏之后大图还剩
    /// 六百多，字块最窄那一档（280 加右边距 60）站得下 —— 这一条由单测按四种海报宽各算一遍。所以
    /// 这一栏缺席只有三种理由：继续观看那一排空着、它在设置里被勾掉了，或者轮播整个关掉了
    /// （<c>UiSettings.ShowHomeBanner</c>，那一档连大图带右栏一起没有，继续观看回到下面横着排的那一叠里）。少了
    /// 「窄窗口上也在」这句话，继续观看就得另有一套横排的版面，那是第二种第一屏，而两种第一屏就是两份要各自维护
    /// 的版面。
    /// </para>
    /// <para>
    /// 这一栏越窄，大图就越宽、跟着也越高（<see cref="Height"/> 按带宽算）—— 两个数是连着的，别只改一个。
    /// </para>
    /// </summary>
    /// <param name="card">
    /// 16:9 卡宽，装机那一档是 <c>CardSize.WideWidth</c>。0 或负数是「没有卡可放」，交回 0 表示没有这一栏。
    /// </param>
    public static double RailWidth(int card) => card > 0 ? RailCard(card) + (RailInset * 2) : 0;

    /// <summary>
    /// 这一栏里一张卡多宽：进来的 16:9 卡宽封在**最宽 240** —— 「把继续观看缩小一些」，从装机那一档
    /// （<see cref="Infrastructure.CardSize.WideWidth"/>，300）收下来的。
    /// <para>
    /// 卡宽同时就是解码宽（见 <c>CardSize</c> 的类注释），所以这一栏里的卡和别处是同一个数。封顶这一头从前是
    /// 活的：海报宽度那行设置还在时，滑杆拖到 340 那一头 16:9 卡是 600 宽，不封顶这一栏就要 640，在一台
    /// 1600 宽的页面上占掉四成。那一行 2026-09-05 删掉了，输入固定是 300，封顶就成了这道契约上的一道保险。
    /// </para>
    /// <para>
    /// 封的是一个常数而不是「页宽的几成」，是因为卡是按这个宽度建出来的（<c>CardItem</c> 一次定死宽和解码宽）：
    /// 跟着页宽走就意味着每次拖窗口都要把这一列重建一遍。
    /// </para>
    /// </summary>
    public static int RailCard(int card) => card > 0 ? Math.Min(card, RailCardCap) : 0;

    /// <summary>
    /// <see cref="RailCard"/> 的上限，也就是这一栏里最宽的一张卡。240×135 正好是 16:9。
    /// </summary>
    public const int RailCardCap = 240;

    /// <summary>
    /// 这一栏里的卡左右各留多少。两边一样，所以卡在这一栏里居中；<see cref="RailWidth"/> 就是卡宽加两个这个数。
    /// </summary>
    public const double RailInset = 20;

    /// <summary>
    /// Which items get to be slides —— **先用继续观看，不够再用最近添加**：「首页的轮播图有继续观看就用继续观看，
    /// 没有或者继续观看不够就用最近添加」（用户的话，2026-09-05）。最近添加是来补位的：继续观看空着、或者它里面
    /// 上得了台的条目凑不满 <see cref="Slots"/> 张，顶上那一块也不该是空的或者只有两张。
    /// <para>
    /// 两排都是具名参数，而不是「按版面次序交一串进来」。这一条推翻了同一天早些时候那一版（「轮播图优先使用排第
    /// 一个的…没有继续观看就往下顺延」，那时次序由 设置 → 主页 那张表说）：**他这一句把两个来源直接点了名**，所以
    /// 接下来看和每个媒体库自己那一排从此都不参加 —— 前者本来就是「继续观看的下一集」、和第一排讲的是同一件事，
    /// 后者是最近添加按库切开的一份。轮播现在有自己的开关（<c>UiSettings.ShowHomeBanner</c>），所以它取哪两排也
    /// 不再跟着那张表上的勾走。
    /// </para>
    /// <para>
    /// 两条规则做筛选。一个剧集只占一张：继续观看在真账号上常常是同一部剧的四集，四张幻灯片站在同一张剧集背景图上、
    /// 挂着同一个名字，读起来就是一条停住了的轮播。以及没有宽图的一概不上
    /// （<see cref="ItemArtwork.BannerOrder"/>）—— 图就是这条带的全部，一张没有图的幻灯片是一个深色矩形上的标题。
    /// 「不够」因此是按**筛完之后**算的：继续观看里有六集但全是同一部剧，那就只有一张，剩下七张由最近添加补。
    /// </para>
    /// <para>
    /// The lists are the shelves' own, already stripped of music by the page: this band costs no request of
    /// its own, which is what lets it be built from whatever those rows came back with.
    /// </para>
    /// </summary>
    /// <param name="resume">继续观看那一排，先上。</param>
    /// <param name="latest">最近添加那一排，补位。</param>
    /// <param name="slots">最多几张，见 <see cref="Slots"/>。</param>
    public static IReadOnlyList<EmbyItem> Slides(
        IReadOnlyList<EmbyItem> resume,
        IReadOnlyList<EmbyItem> latest,
        int slots = Slots)
    {
        var slides = new List<EmbyItem>(Math.Max(0, slots));
        var shows = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<EmbyItem>[] rows = [resume, latest];

        foreach (var row in rows)
            foreach (var item in row)
            {
                if (slides.Count >= slots) return slides;
                if (ItemArtwork.Banner(item) is null || !shows.Add(Show(item))) continue;

                slides.Add(item);
            }

        return slides;
    }

    /// <summary>
    /// 台上这张幻灯片对应右栏里的第几张卡 —— 「轮播图滚动到对应媒体时右边要自动框出对应媒体」（用户的话，
    /// 2026-09-05）。对不上就是 -1，那时右栏一张也不框。
    /// <para>
    /// 先认同一个条目（id 一样），认不到再认同一个剧集（<see cref="Show"/>，也就是剧 id，没有剧的就是它自己）。
    /// 两档都要：幻灯片取自继续观看时它<em>就是</em>右栏里的某一张，id 一比就中；而它取自最近添加时（继续观看不够
    /// 那一档）同一部剧可能两边各有一集 —— 那时框出右栏里那一集才是「对应媒体」，框空反而像是没做。
    /// </para>
    /// <para>
    /// 松的那一档要走完整个列表才交答案：id 相同的那一张可能排在剧相同的那一张后面，而严格的那一档永远该赢。
    /// </para>
    /// </summary>
    /// <param name="slide">台上那张幻灯片背后的条目。</param>
    /// <param name="cards">右栏里那一列，按屏上的次序。</param>
    public static int MatchIndex(EmbyItem slide, IReadOnlyList<EmbyItem> cards)
    {
        if (slide.Id is not { Length: > 0 }) return -1;

        var show = Show(slide);
        var loose = -1;

        for (var index = 0; index < cards.Count; index++)
        {
            if (string.Equals(cards[index].Id, slide.Id, StringComparison.Ordinal)) return index;

            if (loose < 0 && string.Equals(Show(cards[index]), show, StringComparison.Ordinal)) loose = index;
        }

        return loose;
    }

    /// <summary>
    /// What counts as the same show: the series id when the item has one, else the item's own. An episode and
    /// its own series arriving in two different rows would otherwise be two slides of the same thing.
    /// </summary>
    private static string Show(EmbyItem item) =>
        item.SeriesId is { Length: > 0 } series ? series : item.Id;

    /// <summary>
    /// Where <paramref name="delta"/> presses from <paramref name="index"/> land, wrapping at both ends: the
    /// last slide's 下一张 is the first one again. A band that stopped at the end would have to dim a chevron
    /// to say so, and this one moves on a timer besides — a reader who looks up mid-cycle should not have to
    /// work out that it is over.
    /// </summary>
    public static int Step(int index, int delta, int count)
    {
        if (count <= 0) return 0;

        var next = (index + delta) % count;
        return next < 0 ? next + count : next;
    }

    /// <summary>
    /// The dim line under a slide's title: which episode it is, then the facts (<see cref="ItemDetail.Facts"/>
    /// — year, runtime, rating). An episode is billed under its show's name on the title line
    /// (<see cref="ItemDetail.Title"/>), so without this line the slide would not say which episode it is.
    /// </summary>
    public static string Caption(EmbyItem item)
    {
        var parts = new List<string>(2);

        if (item.Type == EmbyItemType.Episode) parts.Add(ItemDetail.EpisodeLabel(item));
        if (ItemDetail.Facts(item) is { Length: > 0 } facts) parts.Add(facts);

        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// The synopsis, or empty when the server sent none. How many lines of it fit is the band's business, and
    /// a clamp here would cut the same sentence at a different word on every window width.
    /// <para>
    /// 走 <see cref="ItemDetail.ProseLine"/> 而不是光 <c>Trim()</c>：刮削来的简介里带着排版用的空白（中文那些
    /// 爱用两个全角空格当段首缩进），而这条带只给两行 —— 一个段落换行白占一行，句中那两格空白在屏上就是「句号
    /// 后面空出六个字」。
    /// </para>
    /// </summary>
    public static string Synopsis(EmbyItem item) => ItemDetail.ProseLine(item.Overview);

    /// <summary>
    /// What one dot along the bottom edge is called, for a reader who is not looking at the picture:
    /// 「第 3 张，共 8 张」. The dots are the only thing on this band that says how many slides there are.
    /// </summary>
    public static string Position(int index, int count) => $"第 {index + 1} 张，共 {count} 张";
}
