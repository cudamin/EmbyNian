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
    /// </summary>
    public const double MinHeight = 240;

    /// <summary>
    /// 量不到窗口高的那一下这条带有多高（第一帧，还有自检里那份没有 <c>XamlRoot</c> 的控件）。800 就是开窗那一
    /// 档的客户区高，而这条带占满一屏（<see cref="Height"/>），所以第一帧已经是第二帧的样子。
    /// </summary>
    public const double UnmeasuredHeight = 800;

    /// <summary>
    /// 锁定窗口比例大小: the shape the browsing area is held in, as width ÷ height — 16:9.
    /// <para>
    /// **这条锁的用处是「调整窗口大小时轮播画面不被裁切」**，也就是它当初被要来做的那件事。剧照本身在任何窗口形状
    /// 下都不裁（<see cref="Height"/> 那一块里按自己的比例整张画出来），锁买到的是**上下左右都不留底色**：第一屏
    /// 就是一屏，页面锁在 16:9，而服务器发来的宽图全是 16:9，所以那一屏正好被一张完整的剧照铺满。锁着别的形状或者
    /// 最大化时图还是完整的，只是四周多两条底色。
    /// </para>
    /// <para>
    /// 「继续观看完整落在第一屏里」不是这条锁的事，别把两者绑在一起 —— 那一排是压在图上的（<see cref="ShelfLift"/>
    /// 把它往上提），锁开不开都成立。中间有一版把锁改成「视口减掉第一排货架」来兑现那句话，代价是带子不再是整屏、
    /// 也就不再有「不裁切」这个承诺；那一版已经删了。
    /// </para>
    /// <para>
    /// 「计算比例时要排除侧边栏」: the area this ratio describes is the client area less
    /// <see cref="SideRail"/>, which is the page rather than the window. The rail is chrome, and counting it
    /// meant the page was never the shape the lock named — the old 1.6:1 window drew its pages at 1.54:1.
    /// </para>
    /// <para>
    /// Nothing is excluded vertically, which is a decision rather than an omission: the title bar's 32 is
    /// drawn over the page rather than above it, and on the home page the picture starts at the window's very
    /// top edge — so the whole client height is the picture's.
    /// </para>
    /// </summary>
    public const double WindowAspect = 16.0 / 9.0;

    /// <summary>The ratio in the language the setting says it in, rather than as 1.78:1.</summary>
    public const string WindowAspectLabel = "16:9";

    /// <summary>
    /// The strip of client width <see cref="WindowAspect"/> does not count: the collapsed navigation rail
    /// (<c>NavigationView.CompactPaneLength</c>, 48) plus the hairline down its right edge. Device-independent
    /// pixels — a window rectangle is physical, so whoever applies it scales by their own dpi first.
    /// <para>
    /// The collapsed width on purpose, rather than whatever the pane happens to be showing. Following the live
    /// pane would resize the window on every toggle of the rail, and the strict first screen has to hold in
    /// both pane states （「不管收起还是展开侧边栏，都要看到完整的继续观看」）: an opened pane borrows its 200
    /// from the page instead. 「侧边栏默认为折叠状态」, so this is also the width the window opens with.
    /// </para>
    /// <para>
    /// Both numbers belong to the shell, so the shell's self-check pins them against the real
    /// <c>NavigationView</c> instead of trusting this constant to have kept up with the markup.
    /// </para>
    /// </summary>
    public const int SideRail = 49;

    /// <summary>
    /// How long a slide stands before the carousel moves on, and how long it waits again after someone
    /// presses a chevron. Eight seconds is long enough to read the synopsis of the one slide you care about
    /// and short enough that the band does not look frozen.
    /// </summary>
    public static readonly TimeSpan Dwell = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 这条带有多高：一屏 —— 「轮播页面占满窗口」。第一屏就是这一块，继续观看那一排坐在一层亚克力玻璃上压在它
    /// 的下半截（那一层由 <c>HomePage</c> 往上提，提多少量出来算），所以「占满窗口」和「留着继续观看」这两句话
    /// 同时成立。
    /// <para>
    /// 剧照在这一块里按自己的比例整张画出来、站在正中（见 <c>HomeBanner.xaml</c>）：窗口锁在
    /// <see cref="WindowAspect"/> 时那正好铺满这一块，锁着别的形状时左右或上下留一条底色 —— 一个像素都不裁。
    /// </para>
    /// <para>
    /// 量不到窗口高的那一下（第一帧、还有自检里那份没有 <c>XamlRoot</c> 的控件）用
    /// <see cref="UnmeasuredHeight"/>，那就是开窗那一档的客户区高，所以第一帧已经是第二帧的样子。下限
    /// <see cref="MinHeight"/> 兜的是矮到不像话的窗口。
    /// </para>
    /// </summary>
    /// <param name="viewport">The window's client height, or 0 for 「not measured yet」.</param>
    public static double Height(double viewport) => viewport > 0
        ? Math.Max(MinHeight, Math.Round(viewport))
        : UnmeasuredHeight;

    /// <summary>
    /// 压在大图上那一叠货架往上提多少，才刚好压住它的下半截而不遮住别的东西 —— 「把继续观看那个地方的背景改成
    /// 亚克力半透明材质，轮播页面占满窗口」。<paramref name="within"/> 是那一叠从自己的顶边到第一排下沿实测的
    /// 高度（那一叠顶上那段留白算在里面），<paramref name="breath"/> 是第一排下沿到窗口下沿留的一口气。
    /// <para>
    /// 提的量不超过这一块自己的高度：提过头那一叠就顶到窗口顶边上去，而顶上那一条是页眉和标题栏的地方。也不小于
    /// 零 —— 一叠还没量出来的时候提零，那时屏上就是「大图占满一屏、货架在屏外」，滚一下就到。
    /// </para>
    /// </summary>
    public static double ShelfLift(double band, double within, double breath) =>
        band <= 0 || within <= 0 ? 0 : Math.Clamp(Math.Round(within + breath), 0, Math.Round(band));

    /// <summary>
    /// How far below the band's middle the text block sits — 「红框中的字体往下移动一些」. Dead centre reads as
    /// a caption pinned to the picture's midline; a hero's words belong in its lower half, which is also the
    /// part of a backdrop least likely to hold a face.
    /// <para>
    /// The cap is what keeps this safe on a squeezed band. A short window gets no drop at all: at
    /// <see cref="MinHeight"/> the block is centred, and everything a taller band adds is room the drop is
    /// allowed to spend — at most half of it, so the block never comes closer to the bottom edge than it
    /// already is in the narrowest window, whatever the block's own height turns out to be. That is the
    /// invariant the row of dots along the bottom edge depends on, and it holds without this rule knowing
    /// how tall the block is.
    /// </para>
    /// <para>
    /// What the drop does not solve is the chevrons — 「翻页的按钮会挡住字体」. They sit against the band's left
    /// and right edges at its vertical middle, so dropping the block only moves which line they cross. Keeping
    /// them off the words is a matter of columns, not of height, and it lives in the view: the copy starts to
    /// the right of the arrows' own strip, which <c>HomeBanner.Probe</c> asserts from the two elements' set
    /// widths and margins.
    /// </para>
    /// </summary>
    public static double InfoDrop(double height) =>
        Math.Clamp(Math.Round(height * DropShare), 0, Math.Min(MaxDrop, Math.Max(0, (height - MinHeight) / 2)));

    /// <summary>
    /// The share of the band's height the text block drops by, and the most it ever drops. 13% keeps the move
    /// proportional to the picture — a 460-tall band drops 60 — and the ceiling stops the widest windows from
    /// pushing the words down onto the dots.
    /// </summary>
    private const double DropShare = 0.13;

    /// <inheritdoc cref="DropShare"/>
    private const double MaxDrop = 64;

    /// <summary>
    /// Which items get to be slides, out of the rows the page has already read: 最近添加 first, then 继续观看,
    /// then 接下来看 —— 「海报要用最近添加」. The other two rows are there to fill: an account whose newest
    /// items happen to have no wide artwork would otherwise leave the top of the page empty, and a half-watched
    /// episode is never a worse slide than nothing at all.
    /// <para>
    /// Two rules do the filtering. One slide per show, because 继续观看 on a real account is often four
    /// episodes of one series, and four slides standing on the same series backdrop under the same name read
    /// as a carousel that has stopped moving. And nothing without wide artwork
    /// (<see cref="ItemArtwork.BannerOrder"/>), because the picture is the whole of this band — a slide with
    /// nothing behind it is a title on a dark rectangle.
    /// </para>
    /// <para>
    /// The lists are the shelves' own, already stripped of music by the page: this band costs no request of
    /// its own, which is what lets it be built from whatever those three came back with.
    /// </para>
    /// </summary>
    public static IReadOnlyList<EmbyItem> Slides(
        IReadOnlyList<EmbyItem> resumed,
        IReadOnlyList<EmbyItem> nextUp,
        IReadOnlyList<EmbyItem> latest,
        int slots = Slots)
    {
        var slides = new List<EmbyItem>(Math.Max(0, slots));
        var shows = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in new[] { latest, resumed, nextUp })
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
    /// The synopsis, or empty when the server sent none. Trimmed and no more: how many lines of it fit is the
    /// band's business, and a clamp here would cut the same sentence at a different word on every window width.
    /// </summary>
    public static string Synopsis(EmbyItem item) => item.Overview?.Trim() ?? "";

    /// <summary>
    /// What one dot along the bottom edge is called, for a reader who is not looking at the picture:
    /// 「第 3 张，共 8 张」. The dots are the only thing on this band that says how many slides there are.
    /// </summary>
    public static string Position(int index, int count) => $"第 {index + 1} 张，共 {count} 张";
}
