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
        RegisterShelfLift();
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
        Test("轮播：带高就是一屏", () =>
        {
            // 「轮播页面占满窗口」：第一屏就是这一块，继续观看那一排坐在一层玻璃上压在它下半截。
            Assert.Equal(800d, HomeCarousel.Height(800));
            Assert.Equal(571d, HomeCarousel.Height(571));
            Assert.Equal(1040d, HomeCarousel.Height(1040));

            // 量不到窗口高的那一下（第一帧、自检里那份没有 XamlRoot 的控件）用开窗那一档，所以第一帧就是
            // 第二帧的样子。
            Assert.Equal(HomeCarousel.UnmeasuredHeight, HomeCarousel.Height(0));
            Assert.Equal(HomeCarousel.UnmeasuredHeight, HomeCarousel.Height(-100));
            Assert.Equal(800d, HomeCarousel.UnmeasuredHeight);

            // 下限兜的是矮到不像话的窗口：一条比这还矮的带子，字块和播放键就没地方站了。
            Assert.Equal(HomeCarousel.MinHeight, HomeCarousel.Height(100));
        });

        Test("轮播：锁定的那一片正好被一张不裁切的 16:9 铺满", () =>
        {
            // 「锁定比例大小改为 16:9，计算比例时要排除侧边栏」＋「轮播的海报能保持16:9」＋「轮播页面占满窗口」
            // 三句话合起来就是这一条：页面是 16:9，第一屏就是页面，剧照整张画出来，于是三者严丝合缝。
            Assert.Equal(16d / 9, HomeCarousel.WindowAspect);
            Assert.Equal("16:9", HomeCarousel.WindowAspectLabel);

            // 扣掉的那一条是那条窄条（48）加它右边那道 1 像素的竖线。现场那两个数由外壳自检对一遍。
            Assert.Equal(49, HomeCarousel.SideRail);

            // 每一种窗口高上都成立：页宽照比例算出来，带高就是那一屏，两个数因此是同一张 16:9。
            for (var window = 300d; window <= 2400; window += 10)
            {
                var page = Math.Round(window * HomeCarousel.WindowAspect);
                var band = HomeCarousel.Height(window);

                Assert.True(Math.Abs(band - window) <= 1, $"窗口高 {window} 时带高 {band}，没占满这一屏");
                Assert.True(
                    Math.Abs(page / band - HomeCarousel.WindowAspect) < 0.01,
                    $"窗口高 {window} 时那一块是 {page / band:0.000}:1，不是 16:9");
            }

            // 开窗那一档写出来：800 高的客户区，页面 1422 宽。
            Assert.Equal(1422d, Math.Round(HomeCarousel.WindowAspect * 800));
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

    private static void RegisterShelfLift()
    {
        Test("轮播：玻璃提起来的量就是「第一排整块 + 一口气」", () =>
        {
            // 「把继续观看那个地方的背景改成亚克力半透明材质，轮播页面占满窗口」：大图占满一屏，继续观看那一块
            // 往上提，提的量正好让第一排完整落在窗口里、下一排从窗口外开始。玻璃顶上那段留白算在实测值里。
            Assert.Equal(309d, HomeCarousel.ShelfLift(800, 293, 16));
            Assert.Equal(293d, HomeCarousel.ShelfLift(800, 293, 0));

            // 窄屏上那一档同样成立，不是只替默认窗口凑出来的数。
            Assert.Equal(309d, HomeCarousel.ShelfLift(571, 293, 16));
        });

        Test("轮播：还没量到第一排、或者根本没有大图时不提", () =>
        {
            // 提零就是「大图占满一屏、货架在屏外」，滚一下就到 —— 那是没有读数时唯一说得出口的样子。
            Assert.Equal(0d, HomeCarousel.ShelfLift(800, 0, 16));
            Assert.Equal(0d, HomeCarousel.ShelfLift(800, -10, 16));
            Assert.Equal(0d, HomeCarousel.ShelfLift(0, 293, 16));
        });

        Test("轮播：玻璃提不过大图自己的高", () =>
        {
            // 提过头那一叠就顶到窗口顶边上去了，而顶上那一条是页眉和标题栏的地方。
            Assert.Equal(400d, HomeCarousel.ShelfLift(400, 500, 16));
            Assert.Equal(571d, HomeCarousel.ShelfLift(571, 600, 0));
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
