using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 「参考主页轮播大图版-misty-4.9.css 给主页轮播功能」, asserted without a window. What
/// <see cref="HomeCarousel"/> answers is 「which items get to be the big picture, and what does one of them
/// say」 — the halves that a control cannot be asked about once it is drawn: a set of slides built from
/// 继续观看 with 最近添加 filling in behind it, which card on the right the one on screen corresponds to,
/// and a band height that has to leave the shelves below it reachable.
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
        Test("轮播：有继续观看就用继续观看", () =>
        {
            // 「首页的轮播图有继续观看就用继续观看，没有或者继续观看不够就用最近添加」：继续观看装得满的时候
            // 最近添加一张都上不来。
            var resume = Enumerable.Range(0, HomeCarousel.Slots).Select(index => Wide($"r{index}")).ToList();
            var slides = HomeCarousel.Slides(resume, [Wide("l1")]);

            Assert.Equal(HomeCarousel.Slots, slides.Count);
            Assert.Equal("r0", slides[0].Id);
            Assert.False(slides.Any(item => item.Id == "l1"));
        });

        Test("轮播：继续观看不够就用最近添加补", () =>
        {
            // 补位而不是取代：继续观看那两张照旧在最前面，后面接最近添加，凑到 Slots 张为止。
            var slides = HomeCarousel.Slides(
                [Wide("r1"), Wide("r2")],
                Enumerable.Range(0, 30).Select(index => Wide($"l{index}")).ToList());

            Assert.Equal(HomeCarousel.Slots, slides.Count);
            Assert.Equal("r1", slides[0].Id);
            Assert.Equal("r2", slides[1].Id);
            Assert.Equal("l0", slides[2].Id);
        });

        Test("轮播：没有继续观看就整条用最近添加", () =>
        {
            var slides = HomeCarousel.Slides([], [Wide("l1"), Wide("l2")]);

            Assert.Equal(2, slides.Count);
            Assert.Equal("l1", slides[0].Id);
        });

        Test("轮播：一个剧集只占一张幻灯片", () =>
        {
            // 继续观看 on a real account is four episodes of the same show, and four slides standing on the
            // same series backdrop under the same name read as a carousel that has stopped moving. 继续观看
            // 先走，所以 s1 这个剧占的是它里面的 e1，最近添加里同一个剧的都进不来 —— 「不够」也因此是按筛完之后
            // 算的。
            var slides = HomeCarousel.Slides(
                [Episode("e1", "s1"), Episode("e2", "s1"), Episode("e3", "s2")],
                [Episode("e4", "s1"), Wide("s1"), Wide("m1")]);

            Assert.Equal(3, slides.Count);
            Assert.Equal("e1", slides[0].Id);
            Assert.Equal("e3", slides[1].Id);
            Assert.Equal("m1", slides[2].Id);
        });

        Test("轮播：没有宽图的条目上不了轮播", () =>
        {
            var poster = new EmbyItem { Id = "p1", Name = "只有海报", Type = EmbyItemType.Movie };
            poster.ImageTags["Primary"] = "p";

            var slides = HomeCarousel.Slides([poster], [Wide("l2")]);

            Assert.Equal(1, slides.Count);
            Assert.Equal("l2", slides[0].Id);
        });

        Test("轮播：借来的剧集背景图算宽图", () =>
        {
            var episode = Episode("e5", "s3", backdrop: false);
            episode.ParentBackdropItemId = "s3";
            episode.ParentBackdropImageTags.Add("parentbd");

            Assert.Equal(1, HomeCarousel.Slides([episode], []).Count);
            Assert.Equal(0, HomeCarousel.Slides([Episode("e6", "s4", backdrop: false)], []).Count);
        });

        Test("轮播：最多就那几张，多的不要", () =>
        {
            var many = Enumerable.Range(0, 30).Select(index => Wide($"m{index}")).ToList();

            Assert.Equal(HomeCarousel.Slots, HomeCarousel.Slides(many, many).Count);
            Assert.Equal(3, HomeCarousel.Slides(many, [], slots: 3).Count);

            // 0 张的余量就是不要轮播，而不是「有几张算几张」。
            Assert.Equal(0, HomeCarousel.Slides(many, many, slots: 0).Count);
        });

        Test("轮播：两排都空就没有轮播", () =>
            Assert.Equal(0, HomeCarousel.Slides([], []).Count));
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
        Test("轮播：带高＝剧照缩到带宽六成后的 16:9 高（弄扁）", () =>
        {
            // 「把轮播图弄扁一些」＋「把主页的轮播图移动到右边」（2026-09-09）：剧照缩到带宽的六成靠右站，带高
            // 就是那六成按 16:9 算出来的高。从前带宽整个按 16:9 算（16:9 的窗口上正好一屏高），那一版第一排
            // 货架要滚一下才露出来。
            Assert.Equal(371d, HomeCarousel.Height(2000, 1100));
            Assert.Equal(648d, HomeCarousel.Height(2000, 1920));
            Assert.Equal(0.6d, HomeCarousel.PictureShare);

            // 量不到带宽的那一下（第一帧、自检里那份没上树的控件）用开窗那一档：1422 的页宽×六成按 16:9 算
            // 正好 480，所以第一帧就是第二帧的样子。这个数从前是 800 —— 一整屏。
            Assert.Equal(HomeCarousel.UnmeasuredHeight, HomeCarousel.Height(800, 0));
            Assert.Equal(HomeCarousel.UnmeasuredHeight, HomeCarousel.Height(800, -100));
            Assert.Equal(480d, HomeCarousel.UnmeasuredHeight);

            // 下限兜的是矮到不像话的窗口：一条比这还矮的带子，字块和播放键就没地方站了。六成那一档在 711 宽
            // 以下就碰到下限了（从前带宽整个按 16:9 算，要 427 以下才碰）。
            Assert.Equal(HomeCarousel.MinHeight, HomeCarousel.Height(2000, 300));
            Assert.Equal(HomeCarousel.MinHeight, HomeCarousel.Height(2000, 640));
        });

        Test("轮播：一屏是上限，超出去带子就不再长高", () =>
        {
            // 超宽屏上「带宽×六成 ÷ 16 × 9」会比一屏还高，那时带高被一屏封住 —— 图照旧吃满带高、贴右沿，
            // 底色留在左边（那一截更宽就是了）。不封的话第一屏里连播放键都看不见。
            Assert.Equal(800d, HomeCarousel.Height(800, 3111));
            Assert.Equal(371d, HomeCarousel.Height(700, 1100));
            Assert.Equal(371d, HomeCarousel.Height(371, 1100));
            Assert.Equal(360d, HomeCarousel.Height(360, 1100));

            // 窗口高说不出来的时候不封顶（自检里那份控件就是这样），照带宽算。
            Assert.Equal(371d, HomeCarousel.Height(0, 1100));

            // 封顶也不许低过下限。
            Assert.Equal(HomeCarousel.MinHeight, HomeCarousel.Height(100, 1100));
        });

        Test("轮播：每一种带宽上「上下不留底色」都成立", () =>
        {
            // 这一条是「上下不要有黑边」的全称说法：带高不超过「带宽×六成 ÷ 16 × 9」，图吃满带高、贴着带的
            // 上下两条边，底色只留在左边（字块站的那一截）。带宽不到 427 的那一档图连六成宽都摆不下、改吃满
            // 带宽，那一条下限的逃逸句管的就是它。
            for (var width = 320d; width <= 3600; width += 20)
            {
                var band = HomeCarousel.Height(2400, width);

                Assert.True(
                    band <= (width * HomeCarousel.PictureShare / HomeCarousel.WindowAspect) + 0.5
                        || band <= HomeCarousel.MinHeight,
                    $"带宽 {width} 时带高 {band}，比剧照缩到六成还高，图的上下会留底色");
            }
        });

        Test("轮播：开窗那一档的页面正好是 16:9", () =>
        {
            // 「锁定比例大小改为 16:9」。这个形状说的是**整个客户区**：侧边栏 2026-09-06 删掉之后页面就是整个客户
            // 区，大图铺满整宽（右边那一列媒体库 2026-09-08 删掉了），所以「带宽」就是页宽，一个数都不扣。它管
            // 的是开窗那一档的默认宽度；带高从前也按它从带宽算（图铺满整条带），弄扁之后改按 PictureShare 算。
            Assert.Equal(16d / 9, HomeCarousel.WindowAspect);
            Assert.Equal("16:9", HomeCarousel.WindowAspectLabel);

            // 开窗那一档写出来：800 高的客户区，页面 1422 宽；剧照缩到六成（853 宽）按 16:9 是 480 高 —— 带子
            // 占头上一截，底下 320 是第一排货架的。
            Assert.Equal(1422d, Math.Round(HomeCarousel.WindowAspect * 800));
            Assert.Equal(480d, HomeCarousel.Height(800, 1422));
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
