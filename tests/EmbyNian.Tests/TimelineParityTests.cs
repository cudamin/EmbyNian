using System.Text.Json;
using EmbyNian.Playback;
using EmbyNian.Theming;

namespace EmbyNian.Tests;

internal static class TimelineParityTests
{
    internal static void Register()
    {
        TestHarness.Test("时间轴彩段：OP/ED 与中日文章名按紧邻章节定界", () =>
        {
            foreach (var name in new[] { "OP", "OP 01", "episode opening", "片头", "片头开始", "オープニング", "Intro Start", "INTRO" })
            {
                var map = TimelineChapterMap.Build([new(90, "正片"), new(0, name), new(600, "ED")]);
                Assert.Equal(2, map.Sections.Count);
                Assert.Equal(new TimelineSection(0, 90, TimelineSectionKind.Opening), map.Sections[0]);
                Assert.Equal(new TimelineSection(600, double.PositiveInfinity, TimelineSectionKind.Ending), map.Sections[1]);
            }
            foreach (var name in new[] { "ED", "ending", "End", "エンディング", "片尾", "片尾开始", "credits" })
                Assert.Equal(TimelineSectionKind.Ending, TimelineChapterMap.Build([new(50, name)]).Sections.Single().Kind);
        });
        TestHarness.Test("时间轴彩段：不偷用自动跳过的位置推断与时长门槛", () =>
        {
            Assert.Equal(0, TimelineChapterMap.Build([new(0, "OP")]).Sections.Count);
            Assert.Equal(0, TimelineChapterMap.Build([new(0, "开始"), new(90, "正片"), new(180, "预告")]).Sections.Count);
            Assert.Equal(0, TimelineChapterMap.Build([new(0, "opening theme"), new(10, "正文")]).Sections.Count);
            Assert.Equal(1, TimelineChapterMap.Build([new(0, "OP"), new(2, "正文")]).Sections.Count);
        });
        TestHarness.Test("时间轴章节：排序、缺名、源索引与无效时间", () =>
        {
            var map = TimelineChapterMap.Build([new(40, "Chapter 4"), new(0, "(unnamed)"), new(double.NaN, "坏"), new(-1, "坏")]);
            Assert.Equal(2, map.Chapters.Count);
            Assert.Equal("章节 1", map.Chapters[0].Title);
            Assert.Equal(1, map.Chapters[0].SourceIndex);
            Assert.Equal("章节 4", map.Chapters[1].Title);
            Assert.Equal("章节 4", map.CaptionAt(40));
        });
        TestHarness.Test("时间轴广告：配对、结束章仅隐藏标题、重叠中点切分", () =>
        {
            var map = TimelineChapterMap.Build([new(0, "Segment Start (1)"), new(10, "Segment Start (2)"),
                new(20, "Segment End (1)"), new(30, "Segment End (2)")]);
            Assert.Equal(2, map.Sections.Count);
            Assert.Equal(14.99, map.Sections[0].End);
            Assert.Equal(15d, map.Sections[1].Start);
            Assert.True(map.Chapters.Any(chapter => chapter.Start == 14.99 && chapter.EndOnly));
            Assert.Equal("", map.CaptionAt(30));
            Assert.Equal(1, TimelineChapterMap.Build([new(10, "[SponsorBlock]: ad"), new(20, "film")]).Sections.Count);
        });
        TestHarness.Test("时间轴配色：RGBA 与独占配置同一颜色和透明度", () =>
        {
            Assert.Equal(ThemeColor.Parse("#6430ABF9"), TimelineChapterMap.OpeningColor);
            Assert.Equal(TimelineChapterMap.OpeningColor, TimelineChapterMap.EndingColor);
            Assert.Equal(ThemeColor.Parse("#80C54E4E"), TimelineChapterMap.AdvertisementColor);
            var root = Root();
            var lua = File.ReadAllText(Path.Combine(root, "assets/mpv-ui/scripts/uosc/main.lua"));
            Assert.True(lua.Contains("openings:30abf964,endings:30abf964,ads:c54e4e80", StringComparison.Ordinal));
            foreach (var pattern in new[] { "^intro%s*start", "^intro$", "^片头$", "^片尾$", "^credits$" })
                Assert.True(lua.Contains(pattern, StringComparison.Ordinal), $"两模式缺少共同章节别名 {pattern}");
        });
        TestHarness.Test("时间轴缓存：独立缓存岛与回看缓存不能画成前缀", () =>
        {
            var cache = new TimelineCache([new(20, 40), new(60, 80)]);
            var gaps = cache.Uncached(100, "auto", true);
            Assert.Equal(3, gaps.Count);
            Assert.Equal(new TimelineRange(0, 20), gaps[0]);
            Assert.Equal(new TimelineRange(40, 60), gaps[1]);
            Assert.Equal(new TimelineRange(80, 100), gaps[2]);
        });
        TestHarness.Test("时间轴缓存：关闭、本地自动、网络自动与空缓存", () =>
        {
            Assert.Equal(0, TimelineCache.Empty.Uncached(100, "no", true).Count);
            Assert.Equal(0, TimelineCache.Empty.Uncached(100, "auto", false).Count);
            Assert.Equal(new TimelineRange(0, 100), TimelineCache.Empty.Uncached(100, "auto", true).Single());
            Assert.Equal(new TimelineRange(0, 100), TimelineCache.Empty.Uncached(100, "yes", false).Single());
            Assert.Equal(0, TimelineCache.Empty.Uncached(double.NaN, "yes", true).Count);
        });
        TestHarness.Test("时间轴缓存：首尾缓存标志、乱序重叠、半秒过滤", () =>
        {
            var cache = new TimelineCache([new(60, 90), new(10, 40), new(20, 50)], true, true);
            Assert.Equal(new TimelineRange(50, 60), cache.Uncached(100, "auto", true).Single());
            Assert.Equal(0, new TimelineCache([new(0, 49.75), new(50.25, 100)]).Uncached(100, "yes", false).Count);
            Assert.Equal(new TimelineRange(10, 30), new TimelineCache([new(0, 10), new(20, 20.25), new(30, 100)])
                .Uncached(100, "yes", false).Single());
            Assert.Equal(new TimelineRange(0, 100), new TimelineCache([new(double.NaN, 40), new(70, 50)])
                .Uncached(100, "yes", false).Single());
        });
        TestHarness.Test("时间轴缓存：解析完整状态，EOF 与 EOF-cached 不混淆", () =>
        {
            using var data = JsonDocument.Parse("""{"seekable-ranges":[{"start":10,"end":20}],"bof-cached":true,"eof-cached":true,"cache-duration":24,"eof":false}""");
            var cache = TimelineCache.Parse(data.RootElement);
            Assert.Equal(12d, cache.BufferedSeconds(2));
            Assert.Equal(0, cache.Uncached(100, "auto", true).Count);
            Assert.Null((cache with { EndOfFile = true }).BufferedSeconds(1));
            using var empty = JsonDocument.Parse("null");
            Assert.Equal(TimelineCache.Empty, TimelineCache.Parse(empty.RootElement));
        });
        TestHarness.Test("时间轴状态：缓存、循环点与章节改变单独唤醒快照", () =>
        {
            var before = new PlayerStatus { Duration = 100, Position = 10, Loaded = true };
            Assert.True((before with { Cache = new TimelineCache([new(10, 20)]) }).DiffersFrom(before));
            Assert.True((before with { LoopA = 0 }).DiffersFrom(before));
            Assert.True((before with { LoopB = 50 }).DiffersFrom(before));
            Assert.True((before with { Chapters = [new(0, "OP")] }).DiffersFrom(before));
            Assert.True((before with { NetworkSource = true }).DiffersFrom(before));
            Assert.False((before with { Cache = new TimelineCache([]) }).DiffersFrom(before));
            var gate = new StatusCoalescer();
            Assert.True(gate.ShouldPublish(before));
            Assert.True(gate.ShouldPublish(before with { Cache = new TimelineCache([], Ahead: 5) }));
        });
        TestHarness.Test("时间轴吸附：二维最近菱形、边界与等距稳定", () =>
        {
            var marks = TimelineChapterMap.Build([new(10, "一"), new(20, "二")]).Chapters;
            var x = TimelineScale.XAt(10, 1001, 100);
            Assert.Equal(0, TimelineScale.ChapterAt(x + 4, 3, 1001, 31, 31, 100, marks));
            Assert.Equal(-1, TimelineScale.ChapterAt(x + 4, 6, 1001, 31, 31, 100, marks));
            Assert.Equal(-1, TimelineScale.ChapterAt(x, 12, 1001, 31, 31, 100, marks));
            Assert.Equal(10d, TimelineScale.TimeAt(x, 1001, 100));
            Assert.Equal(0d, TimelineScale.TimeAt(-10, 1001, 100));
            Assert.Equal(100d, TimelineScale.TimeAt(2000, 1001, 100));
        });
        TestHarness.Test("时间轴时间格式：按整片时长固定时分秒宽度", () =>
        {
            Assert.Equal("5", TimelineScale.Clock(5.9, 45));
            Assert.Equal("00:05", TimelineScale.Clock(5.9, 600));
            Assert.Equal("00:00:05", TimelineScale.Clock(5.9, 5400));
            Assert.Equal("−01:00", TimelineScale.Clock(-60, 600));
        });
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库");
    }
}
