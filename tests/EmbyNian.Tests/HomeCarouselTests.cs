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
            // 这条就是「调整窗口大小时候，轮播画面不会被裁切」的修法：上限从 560 这个绝对值换成窗口高的
            // 一份额。同样形状的窗口于是给出同样形状的带子 —— 而带子的形状就是裁掉多少。
            Assert.Equal(592d, HomeCarousel.Cap(800));
            Assert.Equal(1036d, HomeCarousel.Cap(1400));

            // 量不到窗口高的那一下用默认窗口那一档的值，第一帧因此就是第二帧的样子。
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

        Test("轮播：锁住窗口比例之后，画面裁掉的那一份不再跟着窗口大小变", () =>
        {
            // 用户报的就是这件事。锁定之后客户区是 8:5，带宽是客户区的宽减掉左边那条侧边栏（收起 48／
            // 张开三百多），而这一条断言的是：这么算出来的带子永远正好是 2.2:1 —— 上限一次都不咬。
            // 带子是 2.2:1，16:9 的剧照就永远只裁掉 19.2%，跟窗口是 900 宽还是 3800 宽无关。
            foreach (var rail in new[] { 0d, 49, 320 })
                for (var window = 560d; window <= 2400; window += 10)
                {
                    var width = (window * HomeCarousel.WindowAspect) - rail;
                    if (width <= 0) continue;

                    var wanted = Math.Round(width / HomeCarousel.Aspect);
                    if (wanted < HomeCarousel.MinHeight) continue;

                    Assert.Equal(wanted, HomeCarousel.Height(width, window));
                }

            // 上面那一圈成立的条件写在这里，免得改了份额或者比例之后只剩一圈过不了的断言：份额必须压得住
            // 「锁定比例 ÷ 带子比例」，而侧边栏只会让带子更矮，所以 rail = 0 是最紧的那一档。
            Assert.True(
                HomeCarousel.HeightShare > HomeCarousel.WindowAspect / HomeCarousel.Aspect,
                $"份额 {HomeCarousel.HeightShare} 压不住 {HomeCarousel.WindowAspect / HomeCarousel.Aspect}");
        });

        Test("轮播：不锁比例时上限咬住，可是咬住的那一档也只跟形状有关", () =>
        {
            // 不锁的时候一个又宽又矮的窗口还是会把带子压平 —— 那是没法两全的事。这里守的是另一半：压平多少
            // 只跟窗口的形状有关，跟它多大无关。两个 2.4:1 的窗口，一个 1200 宽一个 2400 宽，带子形状一样。
            // 侧边栏那 48 像素不跟着窗口缩放，所以真到屏幕上还差一点点（1200 那档 3.11、2400 那档 3.18，也就是
            // 裁掉 42.9% 对 44.0%）；这里按带宽等于客户区宽来算，量的是这条规则本身。
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
