using EmbyNian.Emby;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 「参考主页轮播大图版-misty-4.9.css 给主页轮播功能」, asserted without a window. What
/// <see cref="HomeCarousel"/> answers is 「which items get to be the big picture, and what does one of them
/// say」 — the two halves that a control cannot be asked about once it is drawn: a set of slides built out of
/// three rows that overlap, and a band height that has to leave the shelves below it reachable.
/// </summary>
internal static class HomeCarouselTests
{
    public static void Register()
    {
        RegisterSlides();
        RegisterStep();
        RegisterHeight();
        RegisterFoldHeight();
        RegisterText();
    }

    private static void RegisterSlides()
    {
        Test("轮播：按最近添加、继续观看、接下来看的顺序取", () =>
        {
            // 「海报要用最近添加」：最近添加打头，另外两行补位。
            var slides = HomeCarousel.Slides([Wide("r1")], [Wide("n1")], [Wide("l1")]);

            Assert.Equal(3, slides.Count);
            Assert.Equal("l1", slides[0].Id);
            Assert.Equal("r1", slides[1].Id);
            Assert.Equal("n1", slides[2].Id);
        });

        Test("轮播：一个剧集只占一张幻灯片", () =>
        {
            // 继续观看 on a real account is four episodes of the same show, and four slides standing on the
            // same series backdrop under the same name read as a carousel that has stopped moving. 最近添加
            // 先走，所以 s1 这个剧占的是它自己那一张，继续观看里那三集只剩 s2 那一张进得来。
            var slides = HomeCarousel.Slides(
                [Episode("e1", "s1"), Episode("e2", "s1"), Episode("e3", "s2")],
                [Episode("e4", "s1")],
                [Wide("s1")]);

            Assert.Equal(2, slides.Count);
            Assert.Equal("s1", slides[0].Id);
            Assert.Equal("e3", slides[1].Id);
        });

        Test("轮播：没有宽图的条目上不了轮播", () =>
        {
            var poster = new EmbyItem { Id = "p1", Name = "只有海报", Type = EmbyItemType.Movie };
            poster.ImageTags["Primary"] = "p";

            var slides = HomeCarousel.Slides([poster], [], [Wide("l2")]);

            Assert.Equal(1, slides.Count);
            Assert.Equal("l2", slides[0].Id);
        });

        Test("轮播：借来的剧集背景图算宽图", () =>
        {
            var episode = Episode("e5", "s3", backdrop: false);
            episode.ParentBackdropItemId = "s3";
            episode.ParentBackdropImageTags.Add("parentbd");

            Assert.Equal(1, HomeCarousel.Slides([episode], [], []).Count);
            Assert.Equal(0, HomeCarousel.Slides([Episode("e6", "s4", backdrop: false)], [], []).Count);
        });

        Test("轮播：最多就那几张，多的不要", () =>
        {
            var many = Enumerable.Range(0, 30).Select(index => Wide($"m{index}")).ToList();

            Assert.Equal(HomeCarousel.Slots, HomeCarousel.Slides(many, many, many).Count);
            Assert.Equal(3, HomeCarousel.Slides(many, [], [], slots: 3).Count);

            // 0 张的余量就是不要轮播，而不是「有几张算几张」。
            Assert.Equal(0, HomeCarousel.Slides(many, many, many, slots: 0).Count);
        });

        Test("轮播：三行都空就没有轮播", () => Assert.Equal(0, HomeCarousel.Slides([], [], []).Count));
    }

    private static void RegisterStep()
    {
        Test("轮播：翻到头再翻回第一张", () =>
        {
            Assert.Equal(1, HomeCarousel.Step(0, 1, 3));
            Assert.Equal(0, HomeCarousel.Step(2, 1, 3));
            Assert.Equal(2, HomeCarousel.Step(0, -1, 3));
            Assert.Equal(1, HomeCarousel.Step(2, -1, 3));
        });

        Test("轮播：一张和一张都没有时按哪边都不动", () =>
        {
            Assert.Equal(0, HomeCarousel.Step(0, 1, 1));
            Assert.Equal(0, HomeCarousel.Step(0, -1, 1));

            // 数目为零时返回 0 而不是负数：这个数会被当成下标用。
            Assert.Equal(0, HomeCarousel.Step(0, 1, 0));
            Assert.Equal(0, HomeCarousel.Step(3, -1, 0));
        });
    }

    private static void RegisterHeight()
    {
        Test("轮播：带高按页宽算，两头夹住", () =>
        {
            // 2.2:1，所以 1100 宽正好是 500 高；上限管的是宽屏，下限管的是窄窗口。
            Assert.Equal(500d, HomeCarousel.Height(1100, 800));
            Assert.Equal(HomeCarousel.UnmeasuredHeight, HomeCarousel.Height(3000, 0));
            Assert.Equal(HomeCarousel.MinHeight, HomeCarousel.Height(400, 800));
        });

        Test("轮播：还没量到宽度时也有高度", () =>
        {
            // 高度是 0 的带一张图都不会解码，而它之后不会再变，所以那一次就是永远。
            Assert.Equal(HomeCarousel.MinHeight, HomeCarousel.Height(0, 800));
            Assert.Equal(HomeCarousel.MinHeight, HomeCarousel.Height(-100, 800));
        });

        Test("轮播：上限跟着窗口高走，不是一个写死的数", () =>
        {
            // 普通高度路径的上限从 560 这个绝对值换成窗口高的一份额。同样形状的窗口于是给出同样形状的带子。
            Assert.Equal(592d, HomeCarousel.Cap(800));
            Assert.Equal(1036d, HomeCarousel.Cap(1400));

            // 量不到窗口高的那一下用默认窗口那一档的值，第一帧因此就是第二帧的样子 —— 开窗的客户区是 800 高，
            // 量到之后给的就是这个数。
            Assert.Equal(HomeCarousel.Cap(800), HomeCarousel.UnmeasuredHeight);
            Assert.Equal(HomeCarousel.UnmeasuredHeight, HomeCarousel.Cap(0));
            Assert.Equal(HomeCarousel.UnmeasuredHeight, HomeCarousel.Cap(-1));

            // 上限压不到下限底下去：那不是一个窗口形状，那是一页什么都看不见的窗口。
            Assert.Equal(HomeCarousel.MinHeight, HomeCarousel.Cap(100));
        });

        Test("轮播：留得下第一排卡片", () =>
        {
            // 带宽现在等于整个工作区的宽（图铺到窗口的顶边和左右边沿），高却不是 100vh —— 上限就是这条断言：
            // 1080p 全屏下这一块加上第一块牌子还得给第一排卡片留出地方。
            var band = HomeCarousel.Height(1864, 1040);
            Assert.True(band < 1040 * 0.75, $"1080p 全屏下带高 {band}");

            // 而且这句话在每一种窗口高上都成立，不只是在 1080p 上：上限是按份额算的。
            for (var window = 560d; window <= 2400; window += 20)
                Assert.True(
                    HomeCarousel.Height(window * 4, window) <= window * 0.75,
                    $"窗口高 {window} 时带子吃掉了 {HomeCarousel.Height(window * 4, window)}");
        });

        Test("轮播：锁定的是侧边栏右边那一片，16:9", () =>
        {
            // 「锁定比例大小改为 16:9，计算比例时要排除侧边栏」：比例说的是页面那一片，不是整个客户区。
            Assert.Equal(16d / 9, HomeCarousel.WindowAspect);
            Assert.Equal("16:9", HomeCarousel.WindowAspectLabel);

            // 扣掉的那一条是收起来的窄条（48）加它右边那道 1 像素的竖线。收起那一档才是这个数：跟着侧边栏张
            // 开会让窗口在每次收放时改大小，而严格首屏两档都得成立。现场那两个数由外壳自检对一遍。
            Assert.Equal(49, HomeCarousel.SideRail);

            // 开窗那一档：800 高的客户区，页面 1422 宽，正好 16:9；窗口因此比 16:9 宽出那一条。
            var page = Math.Round(HomeCarousel.WindowAspect * 800);
            Assert.Equal(1422d, page);
            Assert.True(Math.Abs(page / 800 - HomeCarousel.WindowAspect) < 0.001, $"页面 {page}×800 不是 16:9");
        });

        Test("轮播：锁定的 16:9 窗口里，普通高度路径由份额咬住", () =>
        {
            // 这是还没量到首排时的普通 Height 路径，不是锁定主页最终使用的 FoldHeight。侧边栏已经不算在比例
            // 里了，所以收起侧边栏时页宽就是窗口高乘这个比例 —— 而 16:9 ÷ 2.2 = 0.808 比份额 0.74 大，于是
            // 这一档的带高是上限本身，形状 2.4:1，不再是首选的 2.2:1。留给第一排卡片那四分之一是对屏幕的
            // 承诺，2.2:1 只是偏好，所以让份额赢。
            for (var window = 560d; window <= 2400; window += 10)
            {
                var page = window * HomeCarousel.WindowAspect;
                var band = HomeCarousel.Height(page, window);

                Assert.Equal(HomeCarousel.Cap(window), band);

                // 而且压平多少只跟形状有关、跟窗口多大无关：同形窗口给同形带子。
                Assert.True(
                    Math.Abs(page / band - HomeCarousel.WindowAspect / HomeCarousel.HeightShare) < 0.02,
                    $"窗口高 {window} 时带子是 {page / band:0.000}:1");
            }

            Assert.True(
                HomeCarousel.WindowAspect / HomeCarousel.Aspect > HomeCarousel.HeightShare,
                $"{HomeCarousel.WindowAspect / HomeCarousel.Aspect} 没有被份额 {HomeCarousel.HeightShare} 咬住");
        });

        Test("轮播：普通高度上限咬住时，同形窗口仍给出同形横幅", () =>
        {
            // 一个又宽又矮的窗口会把普通高度路径压平。这里守的是另一半：压平多少
            // 只跟窗口的形状有关，跟它多大无关。两个 2.4:1 的窗口，一个 1200 宽一个 2400 宽，带子形状一样。
            // 侧边栏那一条已经不在比例里，所以这两个宽度就是页宽本身，屏幕上不再差那一点点。
            var small = HomeCarousel.Height(1200, 500);
            var large = HomeCarousel.Height(2400, 1000);

            Assert.Equal(HomeCarousel.Cap(500), small);
            Assert.Equal(HomeCarousel.Cap(1000), large);
            Assert.True(Math.Abs((1200 / small) - (2400 / large)) < 0.01,
                $"同形状不同大小的窗口给出了不同形状的带子：{1200 / small} 对 {2400 / large}");
        });

        Test("轮播：字块往下沉，带子越高沉得越多", () =>
        {
            // 「红框中的字体往下移动一些」：正中读着像图注，字该在下半张。13% 是按带高算的，所以 400 的带沉 52。
            Assert.Equal(52d, HomeCarousel.InfoDrop(400));
            Assert.True(HomeCarousel.InfoDrop(460) > HomeCarousel.InfoDrop(400), "带子越高该沉得越多");

            // 上限管的是宽屏：再高也不会把字压到底边那排小横条上。
            Assert.Equal(HomeCarousel.InfoDrop(HomeCarousel.UnmeasuredHeight), HomeCarousel.InfoDrop(2000));
            Assert.True(HomeCarousel.InfoDrop(HomeCarousel.UnmeasuredHeight) <= 64, "沉过头了");
        });

        Test("轮播：窄窗口一点都不沉", () =>
        {
            // 下限那一档字块正正居中，一点都不沉 —— 那点余量正是让播放键留在带子里的。
            Assert.Equal(0d, HomeCarousel.InfoDrop(HomeCarousel.MinHeight));
            Assert.Equal(0d, HomeCarousel.InfoDrop(0));
            Assert.Equal(0d, HomeCarousel.InfoDrop(-100));

            // 这条规则不知道字块多高，所以它守的是一句和字块高度无关的话：沉下去的量不超过「比下限高出来的那
            // 部分」的一半，于是字块底下离带底的余量，永远不比它在最窄那一档时更小。
            for (var h = HomeCarousel.MinHeight; h <= 1200; h += 4)
            {
                var drop = HomeCarousel.InfoDrop(h);

                Assert.True(drop >= 0, $"带高 {h} 时沉了 {drop}");
                Assert.True(drop <= (h - HomeCarousel.MinHeight) / 2 + 0.001, $"带高 {h} 时沉了 {drop}，吃掉了下边的余量");
            }
        });
    }

    private static void RegisterFoldHeight()
    {
        Test("轮播：锁定窗口把完整继续观看留在首屏", () =>
        {
            // 继续观看连同它上面的空隙实测占 293。开窗那一档浏览区是 1422×800，无论侧边栏给轮播留下 1422
            // 还是（张开时）1222 的实际宽度，带高都只由视口减掉这一块，第一排下沿因此落在同一个位置。
            Assert.Equal(507d, HomeCarousel.FoldHeight(1422, 800, 293, enabled: true));
            Assert.Equal(507d, HomeCarousel.FoldHeight(1222, 800, 293, enabled: true));

            // 窄屏上把窗口挤矮的那一档同样成立，不是只替默认窗口凑出来的数。
            Assert.Equal(278d, HomeCarousel.FoldHeight(1015, 571, 293, enabled: true));
            Assert.Equal(278d, HomeCarousel.FoldHeight(815, 571, 293, enabled: true));
        });

        Test("轮播：锁定未启用或没有货架读数时保留原高度规则", () =>
        {
            Assert.Equal(HomeCarousel.Height(1422, 800), HomeCarousel.FoldHeight(1422, 800, 0, enabled: true));
            Assert.Equal(HomeCarousel.Height(1422, 800), HomeCarousel.FoldHeight(1422, 800, 293, enabled: false));
            Assert.Equal(HomeCarousel.Height(1422, 0), HomeCarousel.FoldHeight(1422, 0, 293, enabled: true));

            // 严格首屏不能再套普通路径的 240 下限，否则大卡片在最小窗口里必然被截断。
            Assert.Equal(62d, HomeCarousel.FoldHeight(600, 562.5, 500, enabled: true));

            // 第一排恰好吃完整个视口时，0 是合法的最终横幅高度；页面不能把它误认成「还没量到」后
            // 又恢复普通高度，否则两种高度会在 LayoutUpdated 之间来回振荡。
            Assert.Equal(0d, HomeCarousel.FoldHeight(600, 560, 560, enabled: true));
        });
    }

    private static void RegisterText()
    {
        Test("轮播：单集报到集，电影报年份", () =>
        {
            var episode = Episode("e7", "s5");
            episode.ParentIndexNumber = 1;
            episode.IndexNumber = 2;
            episode.Name = "旅途的终点";
            episode.ProductionYear = 2016;

            Assert.Equal("S1:E2 - 旅途的终点  ·  2016", HomeCarousel.Caption(episode));

            var film = Wide("f1");
            film.ProductionYear = 1999;
            Assert.Equal("1999", HomeCarousel.Caption(film));
        });

        Test("轮播：什么都不知道的条目就不写那行", () =>
            Assert.Equal("", HomeCarousel.Caption(Wide("f2"))));

        Test("轮播：简介只去空白，截几行是带的事", () =>
        {
            var item = Wide("f3");
            item.Overview = "  一句话。  ";
            Assert.Equal("一句话。", HomeCarousel.Synopsis(item));
            Assert.Equal("", HomeCarousel.Synopsis(Wide("f4")));
        });

        Test("轮播：位置从 1 数起", () =>
        {
            Assert.Equal("第 1 张，共 8 张", HomeCarousel.Position(0, 8));
            Assert.Equal("第 3 张，共 8 张", HomeCarousel.Position(2, 8));
        });
    }

    /// <summary>A film with a backdrop of its own — the plainest thing that qualifies as a slide.</summary>
    private static EmbyItem Wide(string id)
    {
        var item = new EmbyItem { Id = id, Name = id, Type = EmbyItemType.Movie };
        item.BackdropImageTags.Add("bd");
        return item;
    }

    private static EmbyItem Episode(string id, string seriesId, bool backdrop = true)
    {
        var item = new EmbyItem
        {
            Id = id,
            Name = id,
            Type = EmbyItemType.Episode,
            SeriesId = seriesId,
            SeriesName = seriesId
        };

        if (backdrop) item.BackdropImageTags.Add("bd");
        return item;
    }
}
