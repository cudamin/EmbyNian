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
    /// 这个下限现在是**字块唯一的保险**。字块从顶上往下排（2026-09-05 那一天先挪到右下角、又挪回左下角，2026-09-10
    /// 再按「把红框里的东西移动到左上角」挪到顶上 —— PageSlate 底下，顶距是 <c>HomeBanner</c> 里写死的那一个数 ——
    /// 所以这里从前那条 <c>InfoDrop</c>（「字从正中往下沉多少」，按带高算的一条纯函数）连它的两条单测一起删掉了，
    /// 别去别处找它）。带子 2026-09-09 弄扁之后这个下限更容易碰到：带宽不到 711 就到了（从前要 427），而字块整个
    /// 放得下得 900 宽往上 —— 装不下那一档伸出下沿的是简介和按键那一头，
    /// <c>HomeBanner.State</c> 把「字块底下还剩多少」报进自检，好让那件事看得见。
    /// </para>
    /// </summary>
    public const double MinHeight = 240;

    /// <summary>
    /// 量不到自己宽度的那一下这条带有多高（第一帧，还有自检里那份没上树的控件）。480 就是开窗那一档的带高
    /// （浏览区 1422 全宽，剧照缩到六成按 16:9 换成高），所以第一帧已经是第二帧的样子。从前带宽整个按 16:9 算的
    /// 时候这个数是 800 —— 一整屏。
    /// </summary>
    public const double UnmeasuredHeight = 480;

    /// <summary>
    /// 剧照占带子的几成宽：六成，靠右站（「把主页的轮播图移动到右边」＋「把轮播图弄扁一些」，2026-09-09）。带高
    /// 就是「这个宽的一张 16:9 剧照」的高（<see cref="Height"/>），所以图正好铺满带的上下两条边；左边那四成是
    /// 带子自己的底色，字块站在上面，图的左沿再压一道同色的渐融（<c>HomeBanner</c> 标记里 <c>Fade</c> 那一段）。
    /// <para>
    /// 六成不是随手挑的：字块最宽 620 加左边距 60，在开窗那一档 1422 宽的带上停在 680，而渐融（图宽的三成半）到
    /// 867 才散尽 —— 字块的尾巴一直走在渐融里；最小窗口 900 宽那一档算下来还剩三个像素。再窄成五成，字块就得
    /// 站到散尽了的亮图上；再宽成七成，左边那片摆不下徽标加两行简介。
    /// </para>
    /// </summary>
    public const double PictureShare = 0.6;

    /// <summary>
    /// 这一条的形状，宽 ÷ 高 —— 16:9，也就是服务器发来的宽剧照自己的形状。
    /// <para>
    /// **剧照本身在任何窗口形状下都不裁**（<see cref="Height"/> 那一块里按自己的比例整张画出来）。从前带高也按
    /// 这个形状从带宽算，图因此铺满整条带、16:9 的窗口上正好一屏高；2026-09-09「把轮播图弄扁一些」之后带高改按
    /// <see cref="PictureShare"/> 算，这个形状剩下的两件差事：开窗那一档的默认宽度（<c>HostWindow</c>），和把
    /// 图宽从带高换出来（<c>HomeBanner.Resize</c>：图宽＝带高×16÷9）。
    /// </para>
    /// <para>
    /// **带宽从前是整个页宽，一个数都不用扣掉，横竖都不用。** 大图 2026-09-08 从「左边一栏、右边一列媒体库」改回
    /// 铺满整宽（「移除轮播图右边的媒体库」），右栏那一段宽不再减；侧边栏 2026-09-06 就删了，页面本来也是整个
    /// 客户区。竖向也不扣：外壳画在页面<em>上面</em>而不是上方。**2026-09-10 下半天「弄个框把轮播图框起来」之后
    /// 这条改了口**：带宽是页宽减四边各 24 的卡片留白（那一版带子不再铺满上半页、不再贴到窗口的边），带高跟着
    /// 矮一截；本来的竖向也不扣，现在扣的是字块那头以外的三边。这个形状剩下的差事：把图宽从带高换出来
    /// （<c>HomeBanner.Resize</c>：图宽＝带高×16÷9）。
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
    /// 这条带有多高：**剧照缩到带宽的六成（<see cref="PictureShare"/>）之后，那张 16:9 剧照的高** —— 「把轮播图
    /// 弄扁一些」，2026-09-09。图靠带子的右沿按自己的比例画出来、再放大到「带高÷0.9」竖向居中 —— 上下各裁
    /// 5%（2026-09-10「轮播图上下各裁切百分之五」）是 HomeBanner 在视图层剪的，不影响这里算出来的带高；左边
    /// 那四成是带子自己的底色，字块站在上面。
    /// <para>
    /// 从前带宽整个按 16:9 算，16:9 的窗口上带高正好一屏、底下第一排货架滚一下才露出来；弄扁之后（1422 宽的开窗
    /// 那一档是 480）第一排货架就露在第一屏里。
    /// </para>
    /// <para>
    /// 一屏是上限而不是目标：超宽屏上「带宽×六成 ÷ 16 × 9」会比一屏还高，那时带高被一屏封住，图照旧吃满带高、
    /// 贴右沿，底色留在左边（只是那一截更宽）。下限 <see cref="MinHeight"/> 兜的是矮到不像话的窗口；带宽不到
    /// 427 的时候图连六成宽都摆不下、改吃满带宽，那一档底下留一条底色（<c>HomeBanner.PictureRead</c> 两档都认）。
    /// </para>
    /// <para>
    /// 量不到带宽的那一下（第一帧、还有自检里那份没上树的控件）用 <see cref="UnmeasuredHeight"/>，那就是开窗那一
    /// 档的带高，所以第一帧已经是第二帧的样子。
    /// </para>
    /// </summary>
    /// <param name="viewport">The window's client height, or 0 for 「not measured yet」.</param>
    /// <param name="width">这条带自己有多宽（现在就是整个页宽），或者 0 表示还没量到。</param>
    public static double Height(double viewport, double width)
    {
        if (width <= 0) return UnmeasuredHeight;

        var picture = Math.Round(width * PictureShare / WindowAspect);
        var ceiling = viewport > 0 ? Math.Max(MinHeight, Math.Round(viewport)) : double.MaxValue;

        return Math.Clamp(picture, MinHeight, ceiling);
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
