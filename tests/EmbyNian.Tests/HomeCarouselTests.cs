using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 「参考主页轮播大图版-misty-4.9.css 给主页轮播功能」, asserted without a window. What
/// <see cref="HomeCarousel"/> answers is 「which items get to be the big picture, and what does one of them
/// say」 — the halves that a control cannot be asked about once it is drawn: a set of slides built from
/// 设置里那个来源发回来的候选（2026-09-13「轮播图改用前十个最近添加」，同日下午来源、媒体、张数、秒数四样
/// 进了设置），which card on the right the one on
/// screen corresponds to, and a band height that has to leave the shelves below it reachable.
/// </summary>
internal static class HomeCarouselTests
{
    public static void Register()
    {
        RegisterSlides();
        RegisterStep();
        RegisterHeight();
        RegisterText();
        RegisterBackdrop();
        RegisterPreferences();
    }

    /// <summary>
    /// 设置里那四行（2026-09-13「新增在设置中设置轮播图要使用什么媒体，和要使用最近添加还是随机的还有数量
    /// 的选项。还有封面的轮换的秒数」）在 Core 这一头的落点：张数和秒数夹进同一档（设置页的行范围和
    /// SettingsMigration.Normalize 读的都是这对常量），媒体到请求参数的两张表，还有枚举从 0 数起 ——
    /// 旧设置文件缺键读出来的整数就是装机默认，这条断言就是「升上来的老用户一个像素都不变」的凭据。
    /// </summary>
    private static void RegisterPreferences()
    {
        Test("轮播：设置里的张数和秒数夹进同一档", () =>
        {
            Assert.Equal(HomeCarousel.MinSlots, HomeCarousel.ClampSlots(0));
            Assert.Equal(HomeCarousel.MinSlots, HomeCarousel.ClampSlots(1));
            Assert.Equal(7, HomeCarousel.ClampSlots(7));
            Assert.Equal(HomeCarousel.MaxSlots, HomeCarousel.ClampSlots(99));
            Assert.Equal(HomeCarousel.Slots, HomeCarousel.ClampSlots(HomeCarousel.Slots));

            Assert.Equal(TimeSpan.FromSeconds(HomeCarousel.MinDwellSeconds), HomeCarousel.DwellFor(0));
            Assert.Equal(TimeSpan.FromSeconds(12), HomeCarousel.DwellFor(12));
            Assert.Equal(TimeSpan.FromSeconds(HomeCarousel.MaxDwellSeconds), HomeCarousel.DwellFor(3600));
            Assert.Equal(HomeCarousel.Dwell, HomeCarousel.DwellFor(HomeCarousel.DefaultDwellSeconds));
        });

        Test("轮播：媒体到请求参数的两张表 —— 最近添加的剧集要带单集，随机不带", () =>
        {
            // 最近添加走 /Items/Latest，GroupItems 并剧之后那条可能以任意一头出现，所以 Series 和 Episode
            // 都要；随机走 /Items SortBy=Random，没有 GroupItems，单集上来就是一部剧一张 —— 只按 Series 要。
            Assert.Equal(0, HomeCarousel.LatestTypes(CarouselMediaType.All).Count);
            Assert.Equal("Movie", HomeCarousel.LatestTypes(CarouselMediaType.Movies)[0]);
            Assert.Equal("Series", HomeCarousel.LatestTypes(CarouselMediaType.Series)[0]);
            Assert.Equal("Episode", HomeCarousel.LatestTypes(CarouselMediaType.Series)[1]);

            Assert.Equal("Movie", HomeCarousel.RandomTypes(CarouselMediaType.All)[0]);
            Assert.Equal("Series", HomeCarousel.RandomTypes(CarouselMediaType.All)[1]);
            Assert.Equal("Movie", HomeCarousel.RandomTypes(CarouselMediaType.Movies)[0]);
            Assert.Equal("Series", HomeCarousel.RandomTypes(CarouselMediaType.Series)[0]);
        });

        Test("轮播：来源和媒体的枚举从 0 数起，缺键读出来就是装机默认", () =>
        {
            Assert.Equal(0, (int)CarouselSource.Recent);
            Assert.Equal(1, (int)CarouselSource.Random);
            Assert.Equal(0, (int)CarouselMediaType.All);
            Assert.Equal(1, (int)CarouselMediaType.Movies);
            Assert.Equal(2, (int)CarouselMediaType.Series);
        });
    }

    private static void RegisterBackdrop()
    {
        Test("轮播：左侧延长原图宽度的 10%，原图和高度保持不变", () =>
        {
            const int width = 100;
            const int height = 12;
            var original = Enumerable.Range(0, width * height * 4).Select(index => (byte)(index % 251)).ToArray();
            var (pixels, extendedWidth) = CarouselBackdrop.ExtendLeft(original, width, height);

            Assert.Equal(110, extendedWidth);
            Assert.Equal(extendedWidth * height * 4, pixels.Length);
            for (var y = 0; y < height; y++)
                Assert.True(original.AsSpan(y * width * 4, width * 4)
                    .SequenceEqual(pixels.AsSpan((y * extendedWidth + 10) * 4, width * 4)), "原图像素不应被改写或拉伸");
            Assert.Equal(128, CarouselBackdrop.ExtensionWidth(1280));
        });

        Test("轮播：延长区取左侧颜色，接缝逐行连续", () =>
        {
            const int width = 100;
            const int height = 16;
            var original = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var at = (y * width + x) * 4;
                    original[at] = x < 2 ? (byte)(40 + y * 8) : (byte)255;
                    original[at + 1] = x < 2 ? (byte)60 : (byte)255;
                    original[at + 2] = x < 2 ? (byte)190 : (byte)255;
                    original[at + 3] = 255;
                }

            var (pixels, extendedWidth) = CarouselBackdrop.ExtendLeft(original, width, height);
            for (var y = 0; y < height; y++)
            {
                var row = y * extendedWidth * 4;
                Assert.Equal((byte)60, pixels[row + 1]);
                Assert.Equal((byte)190, pixels[row + 2]);
                Assert.True(pixels.AsSpan(row + 9 * 4, 4).SequenceEqual(pixels.AsSpan(row + 10 * 4, 4)), "接缝不能出现色阶");
            }
        });

        Test("轮播：单像素背景也能延长，没有空行或黑色填充", () =>
        {
            byte[] original = [12, 60, 220, 255];
            var (pixels, width) = CarouselBackdrop.ExtendLeft(original, 1, 1);
            Assert.Equal(2, width);
            Assert.True(pixels.AsSpan(0, 4).SequenceEqual(original));
            Assert.True(pixels.AsSpan(4, 4).SequenceEqual(original));
        });
    }

    private static void RegisterSlides()
    {
        Test("轮播：按最近添加的次序，头十个上", () =>
        {
            // 「轮播图改用前十个最近添加」：来的次序就是上的次序，第十张之后的轮不上 —— 继续观看从
            // 2026-09-13 起不再参加，这个函数也就只剩最近添加一个参数。
            var latest = Enumerable.Range(0, 14).Select(index => Wide($"l{index}")).ToList();
            var slides = HomeCarousel.Slides(latest);

            Assert.Equal(HomeCarousel.Slots, slides.Count);
            Assert.Equal("l0", slides[0].Id);
            Assert.Equal("l9", slides[^1].Id);
            Assert.False(slides.Any(item => item.Id == "l10"));
        });

        Test("轮播：一个剧集只占一张幻灯片", () =>
        {
            // 服务器那头的 GroupItems 已经把同一部剧的最新单集并成了一条，这里这条是保险：一个剧集四张
            // 幻灯片站在同一张背景图上、挂着同一个名字，读起来就是一条停住了的轮播。「前十个」因此是
            // 筛完之后的前十个。
            var slides = HomeCarousel.Slides(
                [Episode("e1", "s1"), Episode("e2", "s1"), Episode("e3", "s2"), Wide("m1")]);

            Assert.Equal(3, slides.Count);
            Assert.Equal("e1", slides[0].Id);
            Assert.Equal("e3", slides[1].Id);
            Assert.Equal("m1", slides[2].Id);
        });

        Test("轮播：没有宽图的条目上不了轮播", () =>
        {
            var poster = new EmbyItem { Id = "p1", Name = "只有海报", Type = EmbyItemType.Movie };
            poster.ImageTags["Primary"] = "p";

            var slides = HomeCarousel.Slides([poster, Wide("l2")]);

            Assert.Equal(1, slides.Count);
            Assert.Equal("l2", slides[0].Id);
        });

        Test("轮播：借来的剧集背景图算宽图", () =>
        {
            var episode = Episode("e5", "s3", backdrop: false);
            episode.ParentBackdropItemId = "s3";
            episode.ParentBackdropImageTags.Add("parentbd");

            Assert.Equal(1, HomeCarousel.Slides([episode]).Count);
            Assert.Equal(0, HomeCarousel.Slides([Episode("e6", "s4", backdrop: false)]).Count);
        });

        Test("轮播：最多就那几张，多的不要", () =>
        {
            var many = Enumerable.Range(0, 30).Select(index => Wide($"m{index}")).ToList();

            Assert.Equal(HomeCarousel.Slots, HomeCarousel.Slides(many).Count);
            Assert.Equal(3, HomeCarousel.Slides(many, slots: 3).Count);

            // 0 张的余量就是不要轮播，而不是「有几张算几张」。
            Assert.Equal(0, HomeCarousel.Slides(many, slots: 0).Count);
        });

        Test("轮播：最近添加空了就没有轮播", () =>
            Assert.Equal(0, HomeCarousel.Slides([]).Count));
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
        Test("轮播：封面扩展到原快捷入口区域，仍按整幅宽度换算高度", () =>
        {
            Assert.Equal(455d, HomeCarousel.Height(2000, 1064));
            Assert.Equal(470d, HomeCarousel.Height(2000, 1100));
            Assert.Equal(821d, HomeCarousel.Height(2000, 1920));
            Assert.Equal(0.76d, HomeCarousel.BandHeightShare);
            Assert.Equal(HomeCarousel.UnmeasuredHeight, HomeCarousel.Height(800, 0));
            Assert.Equal(HomeCarousel.UnmeasuredHeight, HomeCarousel.Height(800, -100));
            Assert.Equal(608d, HomeCarousel.UnmeasuredHeight);
            Assert.Equal(HomeCarousel.MinHeight, HomeCarousel.Height(2000, 300));
            Assert.Equal(HomeCarousel.MinHeight, HomeCarousel.Height(2000, 640));
        });

        Test("轮播：一屏是上限，超出去带子就不再长高", () =>
        {
            Assert.Equal(800d, HomeCarousel.Height(800, 3111));
            Assert.Equal(470d, HomeCarousel.Height(700, 1100));
            Assert.Equal(371d, HomeCarousel.Height(371, 1100));
            Assert.Equal(360d, HomeCarousel.Height(360, 1100));
            Assert.Equal(470d, HomeCarousel.Height(0, 1100));
            Assert.Equal(HomeCarousel.MinHeight, HomeCarousel.Height(100, 1100));
        });

        Test("轮播：改变宽度时封面保持同一形状，图片不变形", () =>
        {
            var shape = HomeCarousel.WindowAspect / HomeCarousel.BandHeightShare;
            for (var width = 760d; width <= 3600; width += 20)
            {
                var band = HomeCarousel.Height(2400, width);
                Assert.Equal(Math.Round(width * HomeCarousel.BandHeightShare / HomeCarousel.WindowAspect), band);
                Assert.True(Math.Abs(width / band - shape) < 0.01,
                    $"带宽 {width} 时带子的形状是 {width / band:0.000}:1，不是 {shape:0.000}:1");
            }
        });

        Test("轮播：开窗那一档的页面正好是 16:9", () =>
        {
            Assert.Equal(16d / 9, HomeCarousel.WindowAspect);
            Assert.Equal("16:9", HomeCarousel.WindowAspectLabel);
            Assert.Equal(1422d, Math.Round(HomeCarousel.WindowAspect * 800));
            Assert.Equal(608d, HomeCarousel.Height(800, 1422));
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
