namespace EmbyNian.Emby;

/// <summary>
/// One artwork to fetch: whose it is, which kind, and the tag that version of it is cached under. A record
/// rather than three loose arguments because the id is not always this item's own — see
/// <see cref="ItemArtwork.Plate"/>.
/// </summary>
public readonly record struct ArtworkRef(string ItemId, string ImageType, string Tag);

/// <summary>
/// Which of an item's five artworks the UI reaches for, and for what. 「把媒体的徽标、缩略图、横幅图、艺术图、
/// 背景图融入对应媒体的 ui 界面」 —— the five are the server's, and this is the one place that says what each
/// one is for:
/// <list type="bullet">
/// <item>徽标 <c>Logo</c> and 横幅图 <c>Banner</c> are name plates — a picture of the title. They replace the
/// text title in the detail page's hero (<see cref="Plate"/>).</item>
/// <item>艺术图 <c>Art</c> is wide artwork with no title on it, and it has two places: 「把艺术图的位置改到
/// 左上角」 puts it in the band's top-left corner (<see cref="Corner"/>, picked by <see cref="Mark"/>), and
/// when the item has no 背景图 it is instead what the whole page stands on (<see cref="HeroOrder"/>).</item>
/// <item>背景图 <c>Backdrop</c> is what the hero wants first; 缩略图 <c>Thumb</c> is what a 16:9 card wants
/// first (<c>CardItem.PreferWide</c>) and the hero's third choice.</item>
/// <item>The home page's carousel is the same three wide kinds and nothing else
/// (<see cref="BannerOrder"/>): it is the full width of the window, which is no place for a poster.</item>
/// </list>
/// <para>
/// One job each, with one exception and a rule that keeps it honest. 艺术图 is in two lists, so
/// <see cref="Corner"/> asks the other one first: the corner is empty exactly when the band behind it is
/// already standing on that same picture. Anything looser puts one picture on the page twice — a 260 wide
/// copy of the wallpaper, pinned to the corner of the wallpaper — which is worse than either place alone.
/// </para>
/// <para>
/// 三处向上借一层：名牌、轮播底图和集页/季页的头图都会用剧集那一层的图（<see cref="Plate"/>、
/// <see cref="Banner"/>、<see cref="Hero"/>）。单集自己几乎只有一张剧照，而剧照是从视频里截的一帧，放到整页
/// 那么大就是「集页面用了这一集的截图」 —— 「集页面要用这个剧的背景图或缩略图」。借来的标签属于另一个条目的
/// id，所以这三处回的都是 <see cref="ArtworkRef"/> 而不是「哪一种」。
/// </para>
/// <para>
/// Pure and in Core on purpose: 「which artwork belongs where」 is the kind of rule that otherwise ends up
/// spelled out in three view models and disagreeing with itself, and here it can be asserted without a
/// server, a window or a decoded bitmap.
/// </para>
/// </summary>
public static class ItemArtwork
{
    /// <summary>
    /// The name plate, in preference order: the item's own 徽标, else its 横幅图.
    /// <para>
    /// Those two and no more, because those two are the ones that are a picture <em>of the title</em> — a
    /// 徽标 is the show's wordmark, and a 横幅图 carries the title across its own artwork. 海报 is the title
    /// page rather than the title, and would land beside the poster already in the band; 艺术图 is 16:9
    /// artwork with no title on it, so at plate size it reads as a small picture where a name should be,
    /// and <see cref="HeroOrder"/> has a better use for it.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> PlateOrder = [EmbyImageStore.Logo, EmbyImageStore.Banner];

    /// <summary>
    /// 一个条目<em>自己</em>的哪几种图能当头图，按优先序。艺术图排第二，因为除了背景图它是这个条目唯一可能有的
    /// 宽幅图；缩略图和海报是原来那两档备选。
    /// <para>
    /// 「自己的」是这条序的全部范围 —— 集页和季页在这几档之前还要先问剧集那一层，那一段在 <see cref="Hero"/>。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> HeroOrder =
        [EmbyImageStore.Backdrop, EmbyImageStore.Art, EmbyImageStore.Thumb, EmbyImageStore.Primary];

    /// <summary>
    /// What the home page's carousel stands on, in preference order: 背景图, then 艺术图, then 缩略图.
    /// <para>
    /// The three wide kinds and no 海报 — not even as a last resort, which is the one place this differs from
    /// <see cref="HeroOrder"/>. The hero's band is a rounded card a third of a page tall, where a 2:3 poster
    /// cropped to fill still shows the middle of the poster; the carousel is the full width of the window,
    /// where the same crop is a band across somebody's chin. An item with none of the three does not become
    /// a slide at all (<see cref="HomeCarousel.Slides"/>), which is a better outcome than a stretched poster.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> BannerOrder =
        [EmbyImageStore.Backdrop, EmbyImageStore.Art, EmbyImageStore.Thumb];

    /// <summary>
    /// The name plate for a page, or null when the item has none to show and the text title stands.
    /// <para>
    /// The fallback to the show's logo is why this returns an <see cref="ArtworkRef"/> rather than a kind:
    /// an episode or a season rarely has a logo of its own, and Emby answers that by sending the parent's
    /// (<see cref="EmbyItem.ParentLogoItemId"/>) — a tag that belongs to a different item's id. A page that
    /// fetched it under its own id would get nothing back and quietly show no plate at all.
    /// </para>
    /// </summary>
    public static ArtworkRef? Plate(EmbyItem? item)
    {
        if (item is null) return null;

        foreach (var type in PlateOrder)
            if (Tag(item, type) is { Length: > 0 } tag)
                return new ArtworkRef(item.Id, type, tag);

        return item is { ParentLogoItemId: { Length: > 0 } parent, ParentLogoImageTag: { Length: > 0 } inherited }
            ? new ArtworkRef(parent, EmbyImageStore.Logo, inherited)
            : null;
    }

    /// <summary>
    /// 头图左上角那张画，没有就是 null —— 「有艺术图优先显示艺术图」。取的是这个条目自己的艺术图，而背后那一整页
    /// 已经站在同一张图上时它是空的（那时候那个角画名牌，见 <see cref="Mark"/>）。
    /// <para>
    /// 艺术图是横的、上面没有字，所以它当得起「角上摆一张画」这件事：徽标那一头是名牌（<see cref="Plate"/>），
    /// 海报是竖的，缩略图和背景图各有各的活。而它同时是 <see cref="HeroOrder"/> 的第二档 —— 一个条目没有背景图
    /// 的时候，铺满整页的就是这张艺术图。两处都画就是同一张图在一页上出现两次：整页那么大一张，角上再钉一张
    /// 缩印本。所以这里先问 <see cref="Hero"/> 头一档是谁，答案正好是这张就让这个角退回名牌。
    /// </para>
    /// <para>
    /// 「自己的」没有上溯：借来的那几种（<see cref="Inherited"/>）是给「铺满整页」和「名牌」用的，而角上这张是
    /// 装饰 —— 一集没有自己的艺术图，就退回名牌（<see cref="Mark"/>），而不是把剧集那一层的图缩一张钉上去。
    /// 服务器也不往下发艺术图：ParentLogo、ParentBackdrop、ParentThumb 都有，ParentArt 没有。
    /// </para>
    /// </summary>
    public static ArtworkRef? Corner(EmbyItem? item)
    {
        if (item is null) return null;
        if (Tag(item, EmbyImageStore.Art) is not { Length: > 0 } tag) return null;

        var corner = new ArtworkRef(item.Id, EmbyImageStore.Art, tag);

        return Hero(item) is [var behind, ..] && behind == corner ? null : corner;
    }

    /// <summary>
    /// 头图左上角那一张 —— 「把艺术图的位置改到左上角，有艺术图优先显示艺术图，没艺术图就显示徽标」。
    /// 两句话就是这一支：先问艺术图（<see cref="Corner"/>），问不着才退到名牌（<see cref="Plate"/>）。
    /// <para>
    /// 一个角上一张图，而不是从前那样一枚名牌钉在右上角、一张画钉在右下角。所以这两种图现在是同一个位置的两档，
    /// 而不是各占一角 —— 也因此这里不需要「两张都有的时候谁让谁」那种规矩：艺术图有就是它。
    /// </para>
    /// <para>
    /// 退档的那一手连着 <see cref="Corner"/> 自己那条「整页已经站在这张艺术图上就空着」：那种条目上这个角画的是
    /// 名牌，而不是空着 —— 空着是从前右下角的答案（那会儿右上角还另有一枚名牌），现在这个角是页面上唯一的记号位。
    /// </para>
    /// </summary>
    public static ArtworkRef? Mark(EmbyItem? item) => Corner(item) ?? Plate(item);

    /// <summary>
    /// The picture for one carousel slide, or null when the item has no wide artwork and so cannot be one.
    /// <para>
    /// Same shape and same reason as <see cref="Plate"/>: the item's own artwork first, then the show's, and
    /// an <see cref="ArtworkRef"/> rather than a kind because the show's tag belongs to the show's id. This is
    /// the fallback that matters most on the home page — 继续观看 and 接下来看 are almost entirely episodes,
    /// and an episode with no backdrop of its own is the normal case rather than the odd one.
    /// </para>
    /// </summary>
    public static ArtworkRef? Banner(EmbyItem? item)
    {
        if (item is null) return null;

        foreach (var type in BannerOrder)
            if (TagOf(item, type) is { Length: > 0 } tag)
                return new ArtworkRef(item.Id, type, tag);

        return ParentBackdrop(item);
    }

    /// <summary>
    /// 剧集那一层的宽幅图，按优先序排的一串：这个剧的背景图，然后这个剧的缩略图。服务器一张都没往下发就是空的。
    /// <para>
    /// 说的是「服务器发下来了什么」，不管这个条目是什么类型 —— 哪一页该用它是 <see cref="Hero"/> 的事（只有集页
    /// 和季页用）。分开是为了自检那一行读数：「这台服务器到底发不发剧集的缩略图」和「这一页用了谁的图」是两个
    /// 问题，前一个只有真账号答得出。
    /// </para>
    /// </summary>
    public static IReadOnlyList<ArtworkRef> Inherited(EmbyItem? item)
    {
        if (item is null) return [];

        var borrowed = new List<ArtworkRef>(2);

        if (ParentBackdrop(item) is { } backdrop) borrowed.Add(backdrop);
        if (ParentThumb(item) is { } thumb) borrowed.Add(thumb);

        return borrowed;
    }

    /// <summary>
    /// 详情页背后那张铺满页面的画面取谁的哪一种，按优先序排的一串；一张都没有就是空的。
    /// <para>
    /// 集页和季页先要剧集那一层的宽幅图 —— 「集页面要用这个剧的背景图或缩略图」。单集自己那张图是 海报，而单集的
    /// 海报就是从视频里截的一帧：放到整页那么大，头上那张图就跟「更多单集」里它自己那张卡片是同一张，而剧集的
    /// 背景图本来就是画给「铺在背后」用的。这两档只给集和季，不给电影和剧集本身：那趟上溯走到最后是媒体库那一层，
    /// 而媒体库的背景图不是这部片子的画面。
    /// </para>
    /// <para>
    /// 回的是一串取图用的 <see cref="ArtworkRef"/> 而不是「哪一种」：借来的标签属于剧集那一头的 id，按这个条目
    /// 自己的 id 去取会取回一个空答案（同 <see cref="Plate"/>）。一串而不是一个，是因为取图那一遍会挨着往下退 ——
    /// 某一张解不出来还能换下一张（<c>DetailViewModel.DecodeFirstAsync</c>）。这串和取图那一遍必须是同一串：
    /// 版面高按「有没有图」分两档，答案错了就是先按 380 布一遍、图到了再按 460 布第二遍。
    /// </para>
    /// <para>
    /// 存在的理由是「不用等图解出来就知道有没有」。详情页头上那一格的高按有没有这张图分两档
    /// （<see cref="DetailHero.Height"/>）；拿解好的位图当判据的话，图一到手整页就得重排一次 —— 屏上看着
    /// 就是「点击主页封面后窗口会闪一下，然后才会进入页面」。列表接口回来的条目已经带着 ImageTags、
    /// BackdropImageTags 和这几个继承来的标签，所以这句话在导航那一刻就答得出，第一帧的版面就是最后的版面。
    /// </para>
    /// </summary>
    public static IReadOnlyList<ArtworkRef> Hero(EmbyItem? item)
    {
        if (item is null) return [];

        var picks = new List<ArtworkRef>(HeroOrder.Count + 2);

        if (item.Type is EmbyItemType.Episode or EmbyItemType.Season) picks.AddRange(Inherited(item));

        foreach (var type in HeroOrder)
            if (TagOf(item, type) is { Length: > 0 } tag)
                picks.Add(new ArtworkRef(item.Id, type, tag));

        return picks;
    }

    /// <summary>
    /// Whether these two versions of one item would draw the same pictures — the hero behind the page, the
    /// mark in the corner, the plate, and the poster or still in the band.
    /// <para>
    /// 存在的理由是详情页现在**先用点进来那张卡片画一屏**，完整条目回来再补上评分、工作室、演职人员和播放目标。
    /// 图是最慢的那一样，所以这一句决定的是：那一批图能不能就这么留着。答案是「能」的时候完整条目那一趟一张图都
    /// 不用重取 —— 而重取的代价不是一次下载（缓存都在），是屏上的图先被清空再回来，也就是一次闪。
    /// </para>
    /// <para>
    /// 比的是**算出来的那几张图**而不是逐个字段：哪一张归谁、按哪个标签取，全在这个类里定，两边的答案一样就意味
    /// 着要取的是同一批文件。id 不同一律是 false，所以从一个条目翻到另一个条目时旧图不会留下来。
    /// </para>
    /// </summary>
    public static bool SamePictures(EmbyItem? left, EmbyItem? right)
    {
        if (left is null || right is null) return false;
        if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal)) return false;
        if (left.Type != right.Type) return false;

        if (Mark(left) != Mark(right)) return false;
        if (Plate(left) != Plate(right)) return false;
        if (!Hero(left).SequenceEqual(Hero(right))) return false;

        // 带子里那一张（海报，集页上是剧照）不走 ArtworkRef —— 它是拿条目自己按类型挨着退的，所以逐个类型比标签。
        foreach (var imageType in StillOrder)
            if (EmbyImageStore.TagFor(left, imageType) != EmbyImageStore.TagFor(right, imageType))
                return false;

        return true;
    }

    /// <summary>
    /// 带子里那一张挨着往下退的次序，也是 <see cref="SamePictures"/> 要比的那几种。集页上多一档背景图，但多比一
    /// 种不会把「一样」判成「不一样」，所以这里一张单子管两种页面。
    /// </summary>
    private static readonly IReadOnlyList<string> StillOrder =
        [EmbyImageStore.Primary, EmbyImageStore.Thumb, EmbyImageStore.Backdrop];

    /// <summary>
    /// Whether the server holds this kind for this item. 背景图 is asked of its own list, because Emby sends
    /// backdrops as an array (an item can have several) and everything else as one tag.
    /// </summary>
    public static bool Has(EmbyItem item, string imageType) => TagOf(item, imageType) is { Length: > 0 };

    /// <summary>
    /// Which kinds this item has, in report order, as 「海报、缩略图、背景图」. 「无」 when it has none —
    /// worth saying out loud, because 「the plate is missing」 and 「the server has no logo」 look identical
    /// on screen and only one of them is this build's fault.
    /// </summary>
    public static string Kinds(EmbyItem item)
    {
        var kinds = new List<string>(Names.Count);

        foreach (var (imageType, name) in Names)
            if (Has(item, imageType)) kinds.Add(name);

        if (item is { ParentLogoImageTag.Length: > 0 } && !Has(item, EmbyImageStore.Logo))
            kinds.Add("剧集徽标");

        if (First(item.ParentBackdropImageTags) is not null && !Has(item, EmbyImageStore.Backdrop))
            kinds.Add("剧集背景图");

        if (ParentThumb(item) is not null && !Has(item, EmbyImageStore.Thumb))
            kinds.Add("剧集缩略图");

        return kinds.Count == 0 ? "无" : string.Join("、", kinds);
    }

    /// <summary>
    /// How many of a batch have each kind: 「20 个条目 —— 海报 20、缩略图 18、徽标 3、横幅图 0、艺术图 0、
    /// 背景图 19」. What says whether an artwork this build now draws will ever be seen on this server.
    /// </summary>
    public static string Census(IReadOnlyList<EmbyItem> items)
    {
        if (items.Count == 0) return "没有条目可数";

        var parts = new List<string>(Names.Count + 1);

        foreach (var (imageType, name) in Names)
            parts.Add($"{name} {items.Count(item => Has(item, imageType))}");

        parts.Add($"剧集徽标 {items.Count(item => item is { ParentLogoImageTag.Length: > 0 })}");
        parts.Add($"剧集背景图 {items.Count(item => First(item.ParentBackdropImageTags) is not null)}");
        parts.Add($"剧集缩略图 {items.Count(item => ParentThumb(item) is not null)}");

        return $"{items.Count} 个条目 —— {string.Join("、", parts)}";
    }

    /// <summary>
    /// What to call one kind in a report: 「徽标」 for <c>Logo</c>. The image type itself for anything that
    /// is not one of the five — nothing asks that, but a line of a report is the wrong place to throw.
    /// </summary>
    public static string Name(string imageType)
    {
        foreach (var (type, name) in Names)
            if (type == imageType) return name;

        return imageType;
    }

    /// <summary>
    /// The five kinds and what to call them in a report, in the order the user listed them plus 海报 first
    /// (it is the one that was never in question). The names are the user's own words.
    /// </summary>
    private static readonly IReadOnlyList<(string ImageType, string Name)> Names =
    [
        (EmbyImageStore.Primary, "海报"),
        (EmbyImageStore.Logo, "徽标"),
        (EmbyImageStore.Thumb, "缩略图"),
        (EmbyImageStore.Banner, "横幅图"),
        (EmbyImageStore.Art, "艺术图"),
        (EmbyImageStore.Backdrop, "背景图")
    ];

    private static string? Tag(EmbyItem item, string imageType) =>
        item.ImageTags.TryGetValue(imageType, out var tag) ? tag : null;

    /// <summary>
    /// 剧集那一层的背景图，服务器没往下发就是 null。id 和标签必须齐 —— 服务器要么两个都发要么都不发，而只有标签
    /// 没有 id 就没处去取，只有 id 没有标签取回来的是服务器现在手上那一版而不是这次读到的那一版。
    /// </summary>
    private static ArtworkRef? ParentBackdrop(EmbyItem item) =>
        item is { ParentBackdropItemId: { Length: > 0 } parent } && First(item.ParentBackdropImageTags) is { } tag
            ? new ArtworkRef(parent, EmbyImageStore.Backdrop, tag)
            : null;

    /// <summary>
    /// 剧集那一层的缩略图，服务器没往下发就是 null。先问这个剧自己那一对（<see cref="EmbyItem.SeriesId"/> 加
    /// <see cref="EmbyItem.SeriesThumbImageTag"/>），再退到那趟上溯（<see cref="EmbyItem.ParentThumbItemId"/>）——
    /// 上溯停在手上有图的那一层，所以单集问到的可能是季的那一张，而用户要的是「这个剧的」。
    /// </summary>
    private static ArtworkRef? ParentThumb(EmbyItem item) =>
        item is { SeriesId: { Length: > 0 } series, SeriesThumbImageTag: { Length: > 0 } own }
            ? new ArtworkRef(series, EmbyImageStore.Thumb, own)
            : item is { ParentThumbItemId: { Length: > 0 } parent, ParentThumbImageTag: { Length: > 0 } tag }
                ? new ArtworkRef(parent, EmbyImageStore.Thumb, tag)
                : null;

    /// <summary>
    /// The tag one kind of this item's artwork is cached under, or null when the server holds none. 背景图 is
    /// the one kind that is not in <see cref="EmbyItem.ImageTags"/>: Emby sends backdrops as an array, because
    /// an item can have several, and asking the tag table for one would answer 「none」 for an item with four.
    /// </summary>
    private static string? TagOf(EmbyItem item, string imageType) => imageType == EmbyImageStore.Backdrop
        ? First(item.BackdropImageTags)
        : Tag(item, imageType);

    /// <summary>
    /// The first tag in one of the server's arrays that is worth fetching with, or null when there is none.
    /// An empty string counts as none, for the reason <see cref="Plate"/> gives: it would fetch whatever
    /// version the server holds now rather than the one this reading is about.
    /// </summary>
    private static string? First(List<string> tags)
    {
        foreach (var tag in tags)
            if (tag is { Length: > 0 }) return tag;

        return null;
    }
}
