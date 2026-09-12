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
    /// 最矮的封面仍留出标题和操作键的空间。窄窗口由 HomeBanner 收起简介，
    /// 保持封面中的必要操作可见。
    /// </summary>
    public const double MinHeight = 300;

    /// <summary>
    /// 第一帧尚未量到宽度时使用默认 1422 宽对应的高度，避免布局完成前后跳动。
    /// </summary>
    public const double UnmeasuredHeight = 608;

    /// <summary>
    /// 封面带高为整幅宽度按 16:9 换算高度的 76%，约 2.34:1。
    /// 2026-09-11 用户移除“我的片库 / 快速进入”整排，并要求封面覆盖该区域。
    /// 1064 宽时从 359 增至 455，图像按 UniformToFill 铺满，标题栏直接浮在图上。
    /// </summary>
    public const double BandHeightShare = 0.76;

    /// <summary>
    /// 16:9 —— 服务器发来的宽剧照自己的形状。
    /// <para>
    /// 现在它有两条差事：开窗那一档的默认宽度（<c>HostWindow</c>），和把带高从带宽换出来（<see cref="Height"/>：
    /// 带宽 × <see cref="BandHeightShare"/> ÷ 这个数）。**剧照自己画多大已经不用它了** —— 铺满那一版由
    /// <c>Stretch="UniformToFill"</c> 按图自己的比例放大到盖住整条带、多出来的一截对半裁掉
    /// （「去掉首页封面轮播图的黑边」，2026-09-11；在那之前是「缩到六成、整张画出来」，16:9 的图在 2.96:1 的
    /// 带子里只裁上下各 5%）。现在的带子约 2.34:1，16:9 图片保留中间约 76% 的高度。
    /// </para>
    /// <para>
    /// **带宽是整个页宽，一个数都不用扣掉，横竖都不用。** 大图 2026-09-08 从「左边一栏、右边一列媒体库」改回
    /// 铺满整宽（「移除轮播图右边的媒体库」），右栏那一段宽不再减；侧边栏 2026-09-06 就删了，页面本来也是整个
    /// 客户区。竖向也不扣：外壳画在页面<em>上面</em>而不是上方。2026-09-10 下半天到 09-11 之间「弄个框把轮播图
    /// 框起来」那一版曾经把带宽收成「页宽减四边各 24」，2026-09-11 用户要「占满窗口的上半部分（包括窗口标题）」
    /// 之后卡片版式整个作废，带宽回到整幅页宽。
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
    /// 带高由 <see cref="BandHeightShare"/> 随整幅宽度换算，1422 宽的默认窗口对应 608。
    /// 标题栏与被移除的快捷入口区域都属于同一张封面，不另外堆叠有底色的导航行。
    /// <para>
    /// 剧照铺满这条带（<c>HomeBanner</c> 的 <c>Stretch="UniformToFill"</c>），带子比 16:9 的图宽，所以图按宽
    /// 放大、上下各裁掉约 12%。**裁多少是带子自己的形状说了算，不是另一个
    /// 常数**：带子多高，图就裁多少 —— 带宽一拉，带高跟着变，裁的量跟着变，而图一个像素都不变形。
    /// </para>
    /// <para>
    /// 一屏是上限而不是目标：超宽屏上按比例换算的高度可能超过一屏，那时带高被一屏封住。下限
    /// <see cref="MinHeight"/> 兜的是矮到不像话的窗口（那一档字块会伸出下沿，<c>HomeBanner.State</c> 报得出来）。
    /// </para>
    /// <para>
    /// 量不到带宽的那一下（第一帧、还有自检里那份没上树的控件）用 <see cref="UnmeasuredHeight"/>，那就是开窗那一
    /// 档的带高，所以第一帧已经是第二帧的样子。
    /// </para>
    /// </summary>
    /// <param name="viewport">The window's client height, or 0 for 「not measured yet」.</param>
    /// <param name="width">这条带自己有多宽（就是整个页宽），或者 0 表示还没量到。</param>
    public static double Height(double viewport, double width)
    {
        if (width <= 0) return UnmeasuredHeight;

        var band = Math.Round(width * BandHeightShare / WindowAspect);
        var ceiling = viewport > 0 ? Math.Max(MinHeight, Math.Round(viewport)) : double.MaxValue;

        return Math.Clamp(band, MinHeight, ceiling);
    }

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
