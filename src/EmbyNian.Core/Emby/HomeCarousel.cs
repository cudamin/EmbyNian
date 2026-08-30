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
    /// The band's height ceiling before anyone has measured the window — the first layout pass, and the
    /// self-check's readings, which have no window at all. 560 is the band the app opens at (a 1280-wide
    /// client less the 48-wide rail, over <see cref="Aspect"/>), so the first frame is already the size the
    /// second one will be.
    /// </summary>
    public const double UnmeasuredHeight = 560;

    /// <summary>
    /// The most of the window's height the band is allowed to take — 「调整窗口大小时候，轮播画面不会被裁切」.
    /// <para>
    /// This used to be an absolute 560, and that absolute number is the whole of the bug. The band's shape is
    /// what decides how much of a 16:9 backdrop is thrown away (<see cref="Aspect"/>), and a fixed ceiling
    /// means the shape changes with the window's size: at 1280 wide the width rule and the ceiling agreed
    /// exactly, so every pixel of width past the default flattened the band and cut more off the top and
    /// bottom of the picture — 1864 wide gave a band of 3.3:1, which is 「被裁切」 by 46% instead of 19%.
    /// A share of the height cannot do that. Two windows of the same shape now get the same band shape and
    /// therefore the same picture, whatever their size, and the ceiling still keeps the promise it was
    /// written for: a quarter of the window is left for the first row of cards.
    /// </para>
    /// <para>
    /// Above <see cref="WindowAspect"/> ÷ <see cref="Aspect"/> = 0.727, and that is the whole reason for the
    /// third decimal: at the locked window shape the width rule has to be the one that binds, or the band
    /// would be a shade flatter than 2.2 and the crop would drift with the size again. The margin covers the
    /// rounding at both ends and the smallest window the shell allows; it is not large enough to give away
    /// height the ceiling was not asked for.
    /// </para>
    /// </summary>
    public const double HeightShare = 0.74;

    /// <summary>
    /// 锁定窗口比例大小: the shape the window's client area is held in — width ÷ height — so that the band's
    /// own shape, and with it how much of the picture is cropped, stops depending on the window's size.
    /// <para>
    /// 1.6 is 8:5, which is the shape the window already opens at (1280×800). Nothing about the number is
    /// derived: it is the size this app has always started at and the one the carousel was drawn against.
    /// What is derived is <see cref="HeightShare"/>, which has to stay above this ÷ <see cref="Aspect"/> for
    /// the lock to mean anything.
    /// </para>
    /// <para>
    /// The rail on the left makes the band narrower than the client area, by 48 collapsed and by the pane's
    /// full width when it is open. Both only ever make the band <em>shorter</em> than the ceiling — a
    /// narrower band at the same height is a smaller quotient — so the lock holds with the pane open or shut,
    /// and the picture is cropped by the same 19% either way.
    /// </para>
    /// </summary>
    public const double WindowAspect = 1.6;

    /// <summary>
    /// How long a slide stands before the carousel moves on, and how long it waits again after someone
    /// presses a chevron. Eight seconds is long enough to read the synopsis of the one slide you care about
    /// and short enough that the band does not look frozen.
    /// </summary>
    public static readonly TimeSpan Dwell = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 2.2:1, rather than the artwork's own 16:9. The band fills the top of the window edge to edge — the
    /// reference's own full-bleed banner — but not <c>max-height:100vh</c>: a 16:9 band 1800 wide is 1000
    /// tall, which is every shelf pushed off the screen on a page whose whole point is the shelves.
    /// Cropping the picture is the compromise: it still fills the width, and the first row of cards stays
    /// in sight. <see cref="HeightShare"/> is the other half of the same promise.
    /// <para>
    /// The number is also the exact size of the crop, which is why it is public: a 2.2 band showing a 16:9
    /// picture <c>UniformToFill</c> keeps (9/16) ÷ (1/2.2) = 80.8% of its height, so 19.2% of every backdrop
    /// is off screen. That figure is allowed to be what it is; what it is not allowed to do is change while
    /// the user drags a window edge.
    /// </para>
    /// </summary>
    public const double Aspect = 2.2;

    /// <summary>
    /// The tallest band this window may have: <see cref="HeightShare"/> of the window's own height, or
    /// <see cref="UnmeasuredHeight"/> while nobody has measured the window yet. Never below
    /// <see cref="MinHeight"/> — a ceiling under the floor is not a window shape, it is a window nobody can
    /// see the page in, and <see cref="Height"/> clamps between the two.
    /// </summary>
    /// <param name="viewport">The window's client height, or 0 for 「not measured yet」.</param>
    public static double Cap(double viewport) => viewport <= 0
        ? UnmeasuredHeight
        : Math.Max(MinHeight, Math.Round(viewport * HeightShare));

    /// <summary>
    /// The band's height for the width it has to fill, inside the room the window has. The floor is for the
    /// moment before the first measurement, when the width is still zero: a band of no height decodes no
    /// picture and never asks again, because nothing about it changes afterwards.
    /// </summary>
    /// <param name="width">How wide the band is — the page's width, rail excluded.</param>
    /// <param name="viewport">The window's client height; see <see cref="Cap"/>.</param>
    public static double Height(double width, double viewport) => width <= 0
        ? MinHeight
        : Math.Clamp(Math.Round(width / Aspect), MinHeight, Cap(viewport));

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
