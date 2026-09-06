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
        RegisterMatch();
        RegisterStep();
        RegisterHeight();
        RegisterRail();
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

    /// <summary>
    /// 「轮播图滚动到对应媒体时右边要自动框出对应媒体」：台上那张对应右栏第几张。
    /// </summary>
    private static void RegisterMatch()
    {
        Test("轮播：右栏框出的就是台上那一个条目", () =>
        {
            var cards = new List<EmbyItem> { Wide("a"), Episode("e1", "s1"), Wide("b") };

            Assert.Equal(1, HomeCarousel.MatchIndex(Episode("e1", "s1"), cards));
            Assert.Equal(2, HomeCarousel.MatchIndex(Wide("b"), cards));
        });

        Test("轮播：条目对不上就退一步认同一个剧集", () =>
        {
            // 幻灯片来自最近添加（继续观看不够那一档）时，同一部剧两边各是一集 —— 那时框右栏里那一集。
            var cards = new List<EmbyItem> { Wide("a"), Episode("e1", "s1") };

            Assert.Equal(1, HomeCarousel.MatchIndex(Episode("e9", "s1"), cards));

            // 剧也对不上就一张都不框，而不是退回第一张。
            Assert.Equal(-1, HomeCarousel.MatchIndex(Episode("e9", "s9"), cards));
            Assert.Equal(-1, HomeCarousel.MatchIndex(Wide("z"), cards));
            Assert.Equal(-1, HomeCarousel.MatchIndex(Wide("a"), []));
        });

        Test("轮播：同一个条目永远赢过同一个剧集", () =>
        {
            // 松的那一档排在前面也不许抢答：走完整个列表才交答案。
            var cards = new List<EmbyItem> { Episode("e1", "s1"), Episode("e2", "s1") };

            Assert.Equal(1, HomeCarousel.MatchIndex(Episode("e2", "s1"), cards));
        });

        Test("轮播：没有 id 的条目不框任何一张", () =>
            Assert.Equal(-1, HomeCarousel.MatchIndex(
                new EmbyItem { Name = "无名" },
                [new EmbyItem { Name = "也无名" }])));
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
        Test("轮播：带高就是那张 16:9 剧照在这个带宽下的高", () =>
        {
            // 「封面固定到最上方，上下不要有黑边」：带高照带宽按 16:9 算，图因此正好铺满这一块。
            Assert.Equal(619d, HomeCarousel.Height(2000, 1100));
            Assert.Equal(360d, HomeCarousel.Height(2000, 640));
            Assert.Equal(1080d, HomeCarousel.Height(2000, 1920));

            // 量不到带宽的那一下（第一帧、自检里那份没上树的控件）用开窗那一档，所以第一帧就是第二帧的样子。
            Assert.Equal(HomeCarousel.UnmeasuredHeight, HomeCarousel.Height(800, 0));
            Assert.Equal(HomeCarousel.UnmeasuredHeight, HomeCarousel.Height(800, -100));
            Assert.Equal(640d, HomeCarousel.UnmeasuredHeight);

            // 下限兜的是窄到不像话的窗口：一条比这还矮的带子，字块和播放键就没地方站了。
            Assert.Equal(HomeCarousel.MinHeight, HomeCarousel.Height(2000, 300));
        });

        Test("轮播：一屏是上限，超出去就改成留左右底色", () =>
        {
            // 超宽屏上「带宽 ÷ 16 × 9」会比一屏还高，那时带高被一屏封住 —— 图跟着改成吃满带高、底色留在左右
            // （HomeBanner.PictureRead 两档都认）。不封的话第一屏里连播放键都看不见。
            Assert.Equal(800d, HomeCarousel.Height(800, 3111));
            Assert.Equal(619d, HomeCarousel.Height(700, 1100));
            Assert.Equal(619d, HomeCarousel.Height(619, 1100));
            Assert.Equal(600d, HomeCarousel.Height(600, 1100));

            // 窗口高说不出来的时候不封顶（自检里那份控件就是这样），照带宽算。
            Assert.Equal(619d, HomeCarousel.Height(0, 1100));

            // 封顶也不许低过下限。
            Assert.Equal(HomeCarousel.MinHeight, HomeCarousel.Height(100, 1100));
        });

        Test("轮播：每一种带宽上「上下不留底色」都成立", () =>
        {
            // 这一条是「上下不要有黑边」的全称说法：带高不超过「带宽 ÷ 16 × 9」，所以图要么正好铺满，要么是被
            // 一屏封住那一档 —— 那一档吃紧的是高、底色留在左右。反过来（带子比 16:9 高）就是上下留底色，一次
            // 都不许出现。
            for (var width = 320d; width <= 3600; width += 20)
            {
                var band = HomeCarousel.Height(2400, width);

                Assert.True(
                    band <= (width / HomeCarousel.WindowAspect) + 0.5 || band <= HomeCarousel.MinHeight,
                    $"带宽 {width} 时带高 {band}，比 16:9 还高，图的上下会留底色");
            }
        });

        Test("轮播：开窗那一档的页面正好是 16:9", () =>
        {
            // 「锁定比例大小改为 16:9，计算比例时要排除侧边栏」。**这个形状不再等于「第一屏被一张剧照铺满」** ——
            // 右边那一栏（RailWidth）占掉一段宽之后，大图那一块只占第一屏的上面一截。它现在管三件事：带高按它从
            // 带宽算、开窗那一档的默认宽度、两道渐变按它算留白。
            Assert.Equal(16d / 9, HomeCarousel.WindowAspect);
            Assert.Equal("16:9", HomeCarousel.WindowAspectLabel);

            // 从前这里还钉着一个 SideRail = 49（收起来的侧边栏 48 加它右边那道 1 像素的竖线），因为这个形状说的
            // 是「客户区减掉那一条」。侧边栏 2026-09-06 删掉之后页面就是整个客户区，那个常数也跟着删了 ——
            // **而页宽一个像素都没变**：从前是 1471 的窗口配 1422 的页面，现在是 1422 配 1422。

            // 开窗那一档写出来：800 高的客户区，页面 1422 宽；减掉右栏 280 之后大图那一块是 1142 宽、642 高。
            Assert.Equal(1422d, Math.Round(HomeCarousel.WindowAspect * 800));
            Assert.Equal(280d, HomeCarousel.RailWidth(CardSize.WideWidth));
            Assert.Equal(642d, HomeCarousel.Height(800, 1422 - 280));
        });
    }

    private static void RegisterRail()
    {
        Test("轮播：右边那一栏就是一张卡加两边的留白", () =>
        {
            // 第一屏是并排两栏：左边大图、右边竖着排的继续观看。这一栏宽多少完全由卡宽定 —— 卡最宽 240
            // （RailCardCap，「把继续观看缩小一些」），所以这一栏最宽 280。
            Assert.Equal(20d, HomeCarousel.RailInset);
            Assert.Equal(280d, HomeCarousel.RailWidth(300));

            // 卡宽是 CardSize 固定的默认档：16:9 卡 300 进来，栏宽照旧是 280。
            Assert.Equal(280d, HomeCarousel.RailWidth(CardSize.WideWidth));
        });

        Test("轮播：右边那一栏的卡最宽 240", () =>
        {
            // 「把继续观看缩小一些」：上限从装机那一档（300）收到 240，一张 240×135 正好是 16:9。这一栏越窄，
            // 左边大图就越宽、跟着也越高（Height 按带宽算），所以这个数是两栏一起的那个旋钮。
            Assert.Equal(240, HomeCarousel.RailCardCap);
            Assert.Equal(240, HomeCarousel.RailCard(CardSize.WideWidth));

            // 上限拿更大的输入也成立（「海报宽度」那行设置删掉之后，调用点固定是 300，这一条钉的是契约本身）。
            Assert.Equal(240, HomeCarousel.RailCard(600));
            Assert.Equal(280d, HomeCarousel.RailWidth(600));
        });

        Test("轮播：没有卡可放就没有这一栏", () =>
        {
            // 继续观看空着、或者在设置里被勾掉了：交回 0，大图占满整个第一屏。
            Assert.Equal(0d, HomeCarousel.RailWidth(0));
            Assert.Equal(0d, HomeCarousel.RailWidth(-10));
            Assert.Equal(0, HomeCarousel.RailCard(0));
            Assert.Equal(0, HomeCarousel.RailCard(-10));
        });

        Test("轮播：最窄的窗口上大图仍然站得下字块", () =>
        {
            // 这一栏不按窗口宽让位，所以「最窄的窗口上还剩多少」是它唯一的下限论证：窗口最小 900 宽
            // （HostWindow.MinimumWidth），减掉这一栏之后，剩给大图的宽必须放得下字块最窄那一档
            // （HomeBanner 里 Info 的 MaxWidth 下限 280 加右边距 60 —— 字块 2026-09-05 挪到了右下角，那个边距
            // 跟着换了边，宽度这笔账一个数没变）。这一条一红，就该给这一栏加一条让位的规矩。
            // 2026-09-06 侧边栏删掉之后这一行不再减那 49，于是余量从 560 涨到 620 —— 这条只会更宽裕。
            const double narrowest = 900;
            const double text = 280 + 60;

            var band = narrowest - HomeCarousel.RailWidth(CardSize.WideWidth);

            Assert.True(band >= text, $"最窄的窗口上大图只剩 {band}，字块要 {text}");
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
