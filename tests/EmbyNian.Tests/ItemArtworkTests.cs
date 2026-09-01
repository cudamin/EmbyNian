using EmbyNian.Emby;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 「把媒体的徽标、缩略图、横幅图、艺术图、背景图融入对应媒体的 ui 界面」, asserted without a server or a
/// window. What <see cref="ItemArtwork"/> answers is 「which of an item's five artworks goes where」, and the
/// two halves worth pinning down are the preference orders — a page that quietly picks its fourth choice
/// looks exactly like a page whose server has no artwork — and the artwork borrowed from the show (徽标,
/// 背景图, 缩略图), where the tag that comes back belongs to a different item's id than the page is about.
/// </summary>
internal static class ItemArtworkTests
{
    public static void Register()
    {
        RegisterPlate();
        RegisterCorner();
        RegisterBanner();
        RegisterHero();
        RegisterHas();
        RegisterReport();
    }

    private static void RegisterPlate()
    {
        Test("图片：徽标优先当名牌，用自己的 id 取", () =>
        {
            var plate = ItemArtwork.Plate(Item("film1", (EmbyImageStore.Logo, "logotag")));

            Assert.NotNull(plate);
            Assert.Equal("film1", plate!.Value.ItemId);
            Assert.Equal(EmbyImageStore.Logo, plate.Value.ImageType);
            Assert.Equal("logotag", plate.Value.Tag);
        });

        Test("图片：没有徽标时用横幅图", () =>
        {
            var banner = ItemArtwork.Plate(Item("s1",
                (EmbyImageStore.Banner, "b"), (EmbyImageStore.Art, "a"), (EmbyImageStore.Primary, "p")));
            Assert.Equal(EmbyImageStore.Banner, banner!.Value.ImageType);
        });

        Test("图片：海报、缩略图和艺术图当不了名牌", () =>
        {
            // 海报 is the title page of the artwork set, not a picture of the title — a page that used it
            // here would show the poster twice, once beside the title and once instead of it. 艺术图 has no
            // title on it at all, and the hero background is where it goes instead.
            Assert.Null(ItemArtwork.Plate(Item("m1",
                (EmbyImageStore.Primary, "p"), (EmbyImageStore.Thumb, "t"), (EmbyImageStore.Art, "a"))));
        });

        Test("图片：单集借剧集的徽标，取的是剧集那一头的 id", () =>
        {
            var episode = Item("ep7", (EmbyImageStore.Primary, "still"));
            episode.ParentLogoItemId = "series1";
            episode.ParentLogoImageTag = "parentlogo";

            var plate = ItemArtwork.Plate(episode);

            // The whole reason this returns a ref rather than a kind: fetched under "ep7" the server has
            // nothing to send back, and the page would show no name plate at all without saying why.
            Assert.Equal("series1", plate!.Value.ItemId);
            Assert.Equal(EmbyImageStore.Logo, plate.Value.ImageType);
            Assert.Equal("parentlogo", plate.Value.Tag);
        });

        Test("图片：自己有徽标时不借剧集的", () =>
        {
            var episode = Item("ep7", (EmbyImageStore.Logo, "own"));
            episode.ParentLogoItemId = "series1";
            episode.ParentLogoImageTag = "parentlogo";

            Assert.Equal("ep7", ItemArtwork.Plate(episode)!.Value.ItemId);
            Assert.Equal("own", ItemArtwork.Plate(episode)!.Value.Tag);
        });

        Test("图片：只有一半的继承徽标不当名牌用", () =>
        {
            // Emby sends both or neither; a tag without an id has nothing to fetch it from, and an id
            // without a tag would fetch whatever version the server holds now.
            var half = Item("ep8");
            half.ParentLogoImageTag = "tag";
            Assert.Null(ItemArtwork.Plate(half));

            var other = Item("ep9");
            other.ParentLogoItemId = "series1";
            Assert.Null(ItemArtwork.Plate(other));
        });

        Test("图片：空标签当没有", () =>
        {
            var blank = Item("m2", (EmbyImageStore.Logo, ""), (EmbyImageStore.Banner, "b"));
            Assert.Equal(EmbyImageStore.Banner, ItemArtwork.Plate(blank)!.Value.ImageType);
        });

        Test("图片：没有条目就没有名牌", () => Assert.Null(ItemArtwork.Plate(null)));

        Test("图片：两条优先序不共用任何一种，各归各位", () =>
        {
            Assert.Equal(
                $"{EmbyImageStore.Logo},{EmbyImageStore.Banner}",
                string.Join(',', ItemArtwork.PlateOrder));

            // 背景图 first, 艺术图 second — the only other wide artwork an item is likely to have.
            Assert.Equal(
                $"{EmbyImageStore.Backdrop},{EmbyImageStore.Art},{EmbyImageStore.Thumb},{EmbyImageStore.Primary}",
                string.Join(',', ItemArtwork.HeroOrder));

            // The overlap is the thing worth asserting: a kind in both lists would be drawn twice on the
            // same band, once as the name plate and once behind it. 艺术图 是唯一有两处活的（头图第二档 +
            // 右下角那张），而那两处不靠「不同的列表」分开，靠 ItemArtwork.Corner 那一句问 —— 见上面那几条。
            Assert.False(ItemArtwork.PlateOrder.Intersect(ItemArtwork.HeroOrder).Any(),
                "同一种图不能同时当名牌和背景");

            // 主页轮播 is the same band again — the 徽标 sits on top of the picture — so the same must hold
            // there. 海报 absent is the whole difference from HeroOrder, and it is the point of the list.
            Assert.Equal(
                $"{EmbyImageStore.Backdrop},{EmbyImageStore.Art},{EmbyImageStore.Thumb}",
                string.Join(',', ItemArtwork.BannerOrder));

            Assert.False(ItemArtwork.PlateOrder.Intersect(ItemArtwork.BannerOrder).Any(),
                "同一种图不能同时当名牌和轮播底图");

            Assert.DoesNotContain(EmbyImageStore.Primary, string.Join(',', ItemArtwork.BannerOrder));
        });
    }

    private static void RegisterCorner()
    {
        Test("图片：角上那张是这个条目自己的艺术图", () =>
        {
            // 「把艺术图添加到窗口右下」。这个条目有背景图，所以铺满整页的是背景图，艺术图这会儿没别的活。
            var item = Item("film20", (EmbyImageStore.Primary, "p"), (EmbyImageStore.Art, "arttag"));
            item.BackdropImageTags.Add("bd");

            var corner = ItemArtwork.Corner(item);

            Assert.NotNull(corner);
            Assert.Equal("film20", corner!.Value.ItemId);
            Assert.Equal(EmbyImageStore.Art, corner.Value.ImageType);
            Assert.Equal("arttag", corner.Value.Tag);
        });

        Test("图片：整页已经站在这张艺术图上，角上就空着", () =>
        {
            // 同一张图在一页上出现两次 —— 铺满整页那么大一张，右下角再钉一张 260 宽的缩印本 —— 比只画一处更糟。
            var only = Item("film21", (EmbyImageStore.Primary, "p"), (EmbyImageStore.Art, "arttag"));

            Assert.Equal(EmbyImageStore.Art, ItemArtwork.Hero(only)[0].ImageType);
            Assert.Null(ItemArtwork.Corner(only));

            // 有了背景图就换回来：这会儿头图站的是另一张图，角上那张才是这一页的第二张画面。
            only.BackdropImageTags.Add("bd");
            Assert.NotNull(ItemArtwork.Corner(only));
        });

        Test("图片：角上那张只跟头图头一档比", () =>
        {
            // 头图是一串备选（前一张解不出来才往下退），而屏上铺着的只有头一档那张。艺术图排在后面几档时这一页
            // 根本没在用它，角上照画 —— 拿整串去比会把有背景图的条目全判成「已经画过了」。
            var episode = Episode("ep30", (EmbyImageStore.Art, "arttag"));
            episode.ParentBackdropItemId = "series11";
            episode.ParentBackdropImageTags.Add("parentbd");

            Assert.Equal("series11", ItemArtwork.Hero(episode)[0].ItemId);
            Assert.Equal("ep30", ItemArtwork.Corner(episode)!.Value.ItemId);
        });

        Test("图片：没有艺术图就空着，不借上一层的", () =>
        {
            Assert.Null(ItemArtwork.Corner(Item("film22", (EmbyImageStore.Primary, "p"))));
            Assert.Null(ItemArtwork.Corner(null));

            // 空标签当没有：拿它去取会取到服务器现在手上那一版。
            Assert.Null(ItemArtwork.Corner(Item("film23", (EmbyImageStore.Art, ""))));

            // 借来的那几种是给「铺满整页」和名牌用的。一集没有自己的艺术图，那个角就空着，而不是把剧集那一层
            // 的图缩一张钉上去 —— 服务器也确实不发：ParentLogo、ParentBackdrop、ParentThumb 都有，ParentArt 没有。
            var episode = Episode("ep31", (EmbyImageStore.Primary, "still"));
            episode.ParentBackdropItemId = "series12";
            episode.ParentBackdropImageTags.Add("parentbd");
            episode.SeriesId = "series12";
            episode.SeriesThumbImageTag = "seriesthumb";

            Assert.True(ItemArtwork.Hero(episode).Count > 0);
            Assert.Null(ItemArtwork.Corner(episode));
        });
    }

    private static void RegisterBanner()
    {
        Test("图片：轮播底图先要背景图", () =>
        {
            var item = Item("film2", (EmbyImageStore.Art, "a"), (EmbyImageStore.Thumb, "t"));
            item.BackdropImageTags.Add("bd");

            var banner = ItemArtwork.Banner(item);

            Assert.Equal("film2", banner!.Value.ItemId);
            Assert.Equal(EmbyImageStore.Backdrop, banner.Value.ImageType);
            Assert.Equal("bd", banner.Value.Tag);
        });

        Test("图片：没有背景图时轮播用艺术图，再退到缩略图", () =>
        {
            var art = Item("film3", (EmbyImageStore.Art, "a"), (EmbyImageStore.Thumb, "t"));
            Assert.Equal(EmbyImageStore.Art, ItemArtwork.Banner(art)!.Value.ImageType);

            var thumb = Item("film4", (EmbyImageStore.Thumb, "t"));
            Assert.Equal(EmbyImageStore.Thumb, ItemArtwork.Banner(thumb)!.Value.ImageType);
        });

        Test("图片：只有海报的条目上不了轮播", () =>
        {
            // 整宽那条带上一张 2:3 的海报裁出来是一条下巴，宁可这个条目不当幻灯片。
            Assert.Null(ItemArtwork.Banner(Item("film5", (EmbyImageStore.Primary, "p"))));
            Assert.Null(ItemArtwork.Banner(Item("film6")));
            Assert.Null(ItemArtwork.Banner(null));
        });

        Test("图片：单集借剧集的背景图，取的是剧集那一头的 id", () =>
        {
            var episode = Item("ep12", (EmbyImageStore.Primary, "still"));
            episode.ParentBackdropItemId = "series2";
            episode.ParentBackdropImageTags.Add("parentbd");

            var banner = ItemArtwork.Banner(episode);

            Assert.Equal("series2", banner!.Value.ItemId);
            Assert.Equal(EmbyImageStore.Backdrop, banner.Value.ImageType);
            Assert.Equal("parentbd", banner.Value.Tag);
        });

        Test("图片：自己有宽图时不借剧集的背景图", () =>
        {
            var episode = Item("ep13", (EmbyImageStore.Thumb, "own"));
            episode.ParentBackdropItemId = "series2";
            episode.ParentBackdropImageTags.Add("parentbd");

            Assert.Equal("ep13", ItemArtwork.Banner(episode)!.Value.ItemId);
            Assert.Equal("own", ItemArtwork.Banner(episode)!.Value.Tag);
        });

        Test("图片：只有一半的继承背景图不当底图用", () =>
        {
            var noTag = Item("ep14");
            noTag.ParentBackdropItemId = "series2";
            Assert.Null(ItemArtwork.Banner(noTag));

            var noId = Item("ep15");
            noId.ParentBackdropImageTags.Add("parentbd");
            Assert.Null(ItemArtwork.Banner(noId));

            // 空串跟没有一样 —— 拿它去取会取到服务器现在手上的那一版，而不是这次读到的那一版。
            var blank = Item("ep16");
            blank.ParentBackdropItemId = "series2";
            blank.ParentBackdropImageTags.Add("");
            Assert.Null(ItemArtwork.Banner(blank));
        });
    }

    /// <summary>
    /// 详情页背后那张铺满页面的画面取谁的哪一种。和 <see cref="ItemArtwork.Banner"/> 的区别是它借来的那两档只给
    /// 集和季 —— 「集页面要用这个剧的背景图或缩略图」，而电影和剧集自己那趟上溯走到最后是媒体库那一层。
    /// </summary>
    private static void RegisterHero()
    {
        Test("图片：头图先要背景图，问的是它自己那张列表", () =>
        {
            var item = Item("film7", (EmbyImageStore.Art, "a"), (EmbyImageStore.Primary, "p"));
            item.BackdropImageTags.Add("bd");

            var hero = ItemArtwork.Hero(item);

            Assert.Equal("film7", hero[0].ItemId);
            Assert.Equal(EmbyImageStore.Backdrop, hero[0].ImageType);
            Assert.Equal("bd", hero[0].Tag);
        });

        Test("图片：头图按优先序往下退，最后退到海报", () =>
        {
            var art = Item("film8", (EmbyImageStore.Art, "a"), (EmbyImageStore.Thumb, "t"));
            Assert.Equal(EmbyImageStore.Art, ItemArtwork.Hero(art)[0].ImageType);

            var thumb = Item("film9", (EmbyImageStore.Thumb, "t"), (EmbyImageStore.Primary, "p"));
            Assert.Equal(EmbyImageStore.Thumb, ItemArtwork.Hero(thumb)[0].ImageType);

            // 海报是这一条的最后一档 —— 详情页那一格是张圆角卡片，裁一张 2:3 还能看，这也是它和轮播的唯一差别。
            Assert.Equal(EmbyImageStore.Primary, ItemArtwork.Hero(Item("film10", (EmbyImageStore.Primary, "p")))[0].ImageType);

            // 徽标和横幅图不在这条序里：它们是名牌，站在这张图前面。
            Assert.Equal(0, ItemArtwork.Hero(Item("film11",
                (EmbyImageStore.Logo, "l"), (EmbyImageStore.Banner, "b"))).Count);
        });

        Test("图片：集页头图先要这个剧的背景图", () =>
        {
            // 「集页面要用这个剧的背景图或缩略图」。单集自己那张图是海报，而单集的海报就是从视频里截的一帧 ——
            // 放到整页那么大，头上那张图就跟「更多单集」里它自己那张卡片是同一张。
            var episode = Episode("ep19", (EmbyImageStore.Primary, "still"));
            episode.ParentBackdropItemId = "series3";
            episode.ParentBackdropImageTags.Add("parentbd");

            var hero = ItemArtwork.Hero(episode);

            Assert.Equal("series3", hero[0].ItemId);
            Assert.Equal(EmbyImageStore.Backdrop, hero[0].ImageType);
            Assert.Equal("parentbd", hero[0].Tag);
        });

        Test("图片：这个剧没有背景图就用这个剧的缩略图", () =>
        {
            // 「或缩略图」的那一半。缩略图是剧集那一层唯一另一张宽幅的画面。
            var episode = Episode("ep20", (EmbyImageStore.Primary, "still"));
            episode.SeriesId = "series4";
            episode.SeriesThumbImageTag = "seriesthumb";

            var hero = ItemArtwork.Hero(episode);

            Assert.Equal("series4", hero[0].ItemId);
            Assert.Equal(EmbyImageStore.Thumb, hero[0].ImageType);
            Assert.Equal("seriesthumb", hero[0].Tag);
        });

        Test("图片：这个剧的缩略图排在上溯来的那张前面", () =>
        {
            // 那趟上溯停在手上有图的那一层，所以单集问到的可能是季的那一张；用户要的是「这个剧的」。
            var episode = Episode("ep21");
            episode.SeriesId = "series5";
            episode.SeriesThumbImageTag = "seriesthumb";
            episode.ParentThumbItemId = "season5";
            episode.ParentThumbImageTag = "seasonthumb";

            Assert.Equal("series5", ItemArtwork.Hero(episode)[0].ItemId);

            // 服务器只发了上溯那一对时，那一对就是答案 —— 季的缩略图也还是这个剧的画面，比截图强。
            var walked = Episode("ep22");
            walked.ParentThumbItemId = "season5";
            walked.ParentThumbImageTag = "seasonthumb";

            Assert.Equal("season5", ItemArtwork.Hero(walked)[0].ItemId);
            Assert.Equal("seasonthumb", ItemArtwork.Hero(walked)[0].Tag);
        });

        Test("图片：借来的两档之后还留着这一集自己的图", () =>
        {
            // 一串而不是一个：取图那一遍挨着往下退，剧集那张解不出来还能退回这一集自己那张，而不是整页没图。
            var episode = Episode("ep23", (EmbyImageStore.Primary, "still"));
            episode.ParentBackdropItemId = "series6";
            episode.ParentBackdropImageTags.Add("parentbd");
            episode.SeriesId = "series6";
            episode.SeriesThumbImageTag = "seriesthumb";

            var hero = ItemArtwork.Hero(episode);

            Assert.Equal(3, hero.Count);
            Assert.Equal("series6", hero[0].ItemId);
            Assert.Equal(EmbyImageStore.Thumb, hero[1].ImageType);
            Assert.Equal("ep23", hero[2].ItemId);
            Assert.Equal(EmbyImageStore.Primary, hero[2].ImageType);
        });

        Test("图片：只有集和季借上一层的图", () =>
        {
            // 那趟上溯走到最后是媒体库那一层，而媒体库的背景图不是这部片子的画面。电影和剧集自己就有宽幅图，
            // 借来的那两档只会把一张别人的画面顶到前面去。
            var film = Item("film16", (EmbyImageStore.Primary, "p"));
            film.Type = EmbyItemType.Movie;
            film.ParentBackdropItemId = "library1";
            film.ParentBackdropImageTags.Add("librarybd");

            var hero = ItemArtwork.Hero(film);

            Assert.Equal(1, hero.Count);
            Assert.Equal("film16", hero[0].ItemId);

            // 季页和集页是同一副数据形状，也是同一条规则：季自己那张图也是一张海报。
            var season = Item("season7", (EmbyImageStore.Primary, "p"));
            season.Type = EmbyItemType.Season;
            season.ParentBackdropItemId = "series7";
            season.ParentBackdropImageTags.Add("parentbd");

            Assert.Equal("series7", ItemArtwork.Hero(season)[0].ItemId);
        });

        Test("图片：只有一半的继承图不当头图用", () =>
        {
            // 同名牌那一条：服务器要么两个都发要么都不发。只有标签没处去取，只有 id 取回来的是服务器现在手上
            // 那一版，而不是这次读到的那一版。
            var noTag = Episode("ep24");
            noTag.ParentThumbItemId = "series8";
            Assert.Equal(0, ItemArtwork.Hero(noTag).Count);

            var noId = Episode("ep25");
            noId.SeriesThumbImageTag = "seriesthumb";
            Assert.Equal(0, ItemArtwork.Hero(noId).Count);

            var blank = Episode("ep26");
            blank.ParentBackdropItemId = "series8";
            blank.ParentBackdropImageTags.Add("");
            blank.SeriesId = "series8";
            blank.SeriesThumbImageTag = "";
            Assert.Equal(0, ItemArtwork.Hero(blank).Count);
        });

        Test("图片：头图一张都没有就是没有", () =>
        {
            Assert.Equal(0, ItemArtwork.Hero(Item("film12")).Count);
            Assert.Equal(0, ItemArtwork.Hero(null).Count);

            // 空串跟没有一样：拿它去取会取到服务器现在手上的那一版。
            var blank = Item("film13", (EmbyImageStore.Thumb, ""));
            blank.BackdropImageTags.Add("");
            Assert.Equal(0, ItemArtwork.Hero(blank).Count);
        });

        Test("图片：头图那一串和「有没有图」是同一句话", () =>
        {
            // 版面高按「有没有图」分两档（DetailHero.Height），而 DetailViewModel 拿的是 Count > 0，取图那一遍
            // 走的是同一串 —— 同一句话，不是两句要对上的话。
            Assert.True(ItemArtwork.Hero(Item("film14", (EmbyImageStore.Thumb, "t"))).Count > 0);
            Assert.False(ItemArtwork.Hero(Item("film15")).Count > 0);

            // 借来的那两档正是「必须同一串」的原因：这一集自己一种图都没有，可是这一页有图可铺。按它自己那张
            // 标签表答就会答成没有 —— 那就是先按 380 布一遍、图到了再按 460 布第二遍。
            var episode = Episode("ep27");
            episode.ParentBackdropItemId = "series9";
            episode.ParentBackdropImageTags.Add("parentbd");

            Assert.True(ItemArtwork.Hero(episode).Count > 0);
            Assert.False(ItemArtwork.HeroOrder.Any(type => ItemArtwork.Has(episode, type)));
        });
    }

    private static void RegisterHas()
    {
        Test("图片：背景图问的是它自己那张列表", () =>
        {
            var item = Item("m3");
            Assert.False(ItemArtwork.Has(item, EmbyImageStore.Backdrop));

            // Emby sends backdrops as an array and everything else as a single tag, so asking ImageTags
            // for a Backdrop would report 「no」 on an item with four of them.
            item.BackdropImageTags.Add("bd1");
            Assert.True(ItemArtwork.Has(item, EmbyImageStore.Backdrop));
        });

        Test("图片：其余四种问的是标签表", () =>
        {
            var item = Item("m4", (EmbyImageStore.Primary, "p"), (EmbyImageStore.Art, ""));

            Assert.True(ItemArtwork.Has(item, EmbyImageStore.Primary));
            Assert.False(ItemArtwork.Has(item, EmbyImageStore.Art));
            Assert.False(ItemArtwork.Has(item, EmbyImageStore.Logo));
        });

        Test("图片：背景图那张列表里的空标签也当没有", () =>
        {
            var item = Item("m7");
            item.BackdropImageTags.Add("");
            Assert.False(ItemArtwork.Has(item, EmbyImageStore.Backdrop));

            item.BackdropImageTags.Add("bd2");
            Assert.True(ItemArtwork.Has(item, EmbyImageStore.Backdrop));
            Assert.Equal("bd2", ItemArtwork.Banner(item)!.Value.Tag);
        });
    }

    private static void RegisterReport()
    {
        Test("图片：单个条目按用户的说法报有哪几种", () =>
        {
            var item = Item("m5", (EmbyImageStore.Primary, "p"), (EmbyImageStore.Thumb, "t"));
            item.BackdropImageTags.Add("bd");

            Assert.Equal("海报、缩略图、背景图", ItemArtwork.Kinds(item));
        });

        Test("图片：一种都没有要明说，不留一行空的", () =>
        {
            // 「the plate is missing」 and 「the server has no logo」 look identical on screen, and only one
            // of the two is this build's fault.
            Assert.Equal("无", ItemArtwork.Kinds(Item("m6")));
        });

        Test("图片：借来的徽标单独报，不冒充自己的", () =>
        {
            var episode = Item("ep10", (EmbyImageStore.Primary, "still"));
            episode.ParentLogoItemId = "series1";
            episode.ParentLogoImageTag = "tag";

            Assert.Equal("海报、剧集徽标", ItemArtwork.Kinds(episode));

            var own = Item("ep11", (EmbyImageStore.Logo, "own"));
            own.ParentLogoItemId = "series1";
            own.ParentLogoImageTag = "tag";
            Assert.DoesNotContain("剧集徽标", ItemArtwork.Kinds(own));
        });

        Test("图片：批量统计报的是每种有几个", () =>
        {
            var census = ItemArtwork.Census([
                Item("a", (EmbyImageStore.Primary, "p"), (EmbyImageStore.Logo, "l")),
                Item("b", (EmbyImageStore.Primary, "p")),
                Item("c")
            ]);

            Assert.Contains("3 个条目", census);
            Assert.Contains("海报 2", census);
            Assert.Contains("徽标 1", census);
            Assert.Contains("横幅图 0", census);
            Assert.Contains("艺术图 0", census);
        });

        Test("图片：借来的背景图也单独报，主页轮播全指望它", () =>
        {
            // 「服务器到底有没有把剧集的背景图发下来」只有实测答得出，而这一行就是那份实测的读数：单集自己
            // 几乎没有背景图，所以这个数是零还是不零，决定了轮播是站在剧集的背景图上还是退到单集的剧照上。
            var episode = Item("ep17", (EmbyImageStore.Primary, "still"));
            episode.ParentBackdropItemId = "series2";
            episode.ParentBackdropImageTags.Add("parentbd");

            Assert.Equal("海报、剧集背景图", ItemArtwork.Kinds(episode));
            Assert.Contains("剧集背景图 1", ItemArtwork.Census([episode, Item("d")]));

            var own = Item("ep18");
            own.BackdropImageTags.Add("bd");
            own.ParentBackdropItemId = "series2";
            own.ParentBackdropImageTags.Add("parentbd");
            Assert.DoesNotContain("剧集背景图", ItemArtwork.Kinds(own));
        });

        Test("图片：借来的缩略图也单独报，集页头图要用它", () =>
        {
            // 「这台服务器发不发剧集的缩略图」只有真账号答得出，而集页头上那张图第二档就是它（见 ItemArtwork.Hero）：
            // 这一行是零还是不零，决定了一集没有剧集背景图的时候，头上是这个剧的画面还是这一集的截图。
            var episode = Episode("ep28", (EmbyImageStore.Primary, "still"));
            episode.SeriesId = "series10";
            episode.SeriesThumbImageTag = "seriesthumb";

            Assert.Equal("海报、剧集缩略图", ItemArtwork.Kinds(episode));
            Assert.Contains("剧集缩略图 1", ItemArtwork.Census([episode, Item("e")]));

            var own = Episode("ep29", (EmbyImageStore.Thumb, "own"));
            own.SeriesId = "series10";
            own.SeriesThumbImageTag = "seriesthumb";
            Assert.DoesNotContain("剧集缩略图", ItemArtwork.Kinds(own));
        });

        Test("图片：剧集那一层有什么图，跟哪一页会用它是两回事", () =>
        {
            // Inherited 说的是「服务器发下来了什么」，不看类型 —— 自检那行读数问的是前者，Hero 才管后者。
            var film = Item("film17", (EmbyImageStore.Primary, "p"));
            film.Type = EmbyItemType.Movie;
            film.ParentBackdropItemId = "library2";
            film.ParentBackdropImageTags.Add("librarybd");

            Assert.Equal(1, ItemArtwork.Inherited(film).Count);
            Assert.Equal(1, ItemArtwork.Hero(film).Count);
            Assert.Equal("film17", ItemArtwork.Hero(film)[0].ItemId);

            Assert.Equal(0, ItemArtwork.Inherited(Item("film18")).Count);
            Assert.Equal(0, ItemArtwork.Inherited(null).Count);
        });

        Test("图片：没有条目时统计说没有，不说 0 个条目", () =>
            Assert.Equal("没有条目可数", ItemArtwork.Census([])));

        Test("图片：每种图都有个中文名，认不出来的照原样报", () =>
        {
            Assert.Equal("徽标", ItemArtwork.Name(EmbyImageStore.Logo));
            Assert.Equal("背景图", ItemArtwork.Name(EmbyImageStore.Backdrop));

            // A report is not the place to throw: 「Disc」 read back as 「Disc」 is still a readable line.
            Assert.Equal("Disc", ItemArtwork.Name("Disc"));
        });
    }

    private static EmbyItem Item(string id, params (string Type, string Tag)[] tags)
    {
        var item = new EmbyItem { Id = id, Name = id };
        foreach (var (type, tag) in tags) item.ImageTags[type] = tag;
        return item;
    }

    /// <summary>
    /// 一集。类型是判据的一部分 —— 头图借剧集那一层这件事只对集和季生效（<see cref="ItemArtwork.Hero"/>），所以
    /// 不带类型的条目在那条规则下就是一部电影。
    /// </summary>
    private static EmbyItem Episode(string id, params (string Type, string Tag)[] tags)
    {
        var item = Item(id, tags);
        item.Type = EmbyItemType.Episode;
        return item;
    }
}
