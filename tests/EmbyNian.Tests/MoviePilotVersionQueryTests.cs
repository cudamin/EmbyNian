using EmbyNian.Emby;
using EmbyNian.MoviePilot;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 「在 MoviePilot 搜索其他版本」发什么搜索词 —— 用户定的对应（2026-09-24）在这里变成断言：
/// 剧→<c>剧名</c>；季→<c>剧名 S0x</c>；集→<c>剧名 S0x E0x</c>；电影→<c>电影名</c>。
/// <para>
/// 值得钉，是因为错起来屏上看着正常：少个空格、季号取错成集号、季页拿了「第 2 季」当剧名 —— 都要有人一条条比
/// 才看得出来，而这几行正是那种「一个输入一个正确答案」的判断（同 <see cref="Emby.ItemMenu"/>）。
/// </para>
/// </summary>
internal static class MoviePilotVersionQueryTests
{
    public static void Register()
    {
        Test("MP 搜版本：电影发片名", () =>
            Assert.Equal("星际穿越", MoviePilotVersionQuery.Keyword(
                new EmbyItem { Id = "m1", Name = "星际穿越", Type = EmbyItemType.Movie })));

        Test("MP 搜版本：剧发整部剧的名字", () =>
            Assert.Equal("三体", MoviePilotVersionQuery.Keyword(
                new EmbyItem { Id = "s1", Name = "三体", Type = EmbyItemType.Series })));

        Test("MP 搜版本：季发「剧名 S0x」，季号取自己的 IndexNumber、剧名取 SeriesName", () =>
        {
            // 季自己的名字是「第 2 季」，不是剧名 —— 拿它当词头就搜不到了，必须用 SeriesName。
            var season = new EmbyItem
            {
                Id = "n2",
                Name = "第 2 季",
                Type = EmbyItemType.Season,
                SeriesName = "三体",
                IndexNumber = 2
            };
            Assert.Equal("三体 S02", MoviePilotVersionQuery.Keyword(season));
        });

        Test("MP 搜版本：集发「剧名 S0x E0x」，季号取 ParentIndexNumber、集号取 IndexNumber", () =>
        {
            var episode = new EmbyItem
            {
                Id = "e1",
                Name = "崛起",
                Type = EmbyItemType.Episode,
                SeriesName = "攻壳机动队",
                ParentIndexNumber = 1,
                IndexNumber = 2
            };
            Assert.Equal("攻壳机动队 S01 E02", MoviePilotVersionQuery.Keyword(episode));
        });

        Test("MP 搜版本：季号缺失时退回只有剧名，不发一个光「S」出去", () =>
        {
            var season = new EmbyItem { Id = "n0", Name = "特别篇", Type = EmbyItemType.Season, SeriesName = "某剧" };
            Assert.Equal("某剧", MoviePilotVersionQuery.Keyword(season));
        });

        Test("MP 搜版本：季/集没给 SeriesName 时退到条目自己的名字，总比空词强", () =>
        {
            var episode = new EmbyItem { Id = "e2", Name = "某集", Type = EmbyItemType.Episode, ParentIndexNumber = 1, IndexNumber = 3 };
            Assert.Equal("某集 S01 E03", MoviePilotVersionQuery.Keyword(episode));
        });

        Test("MP 搜版本：只有电影/剧/季/集这四种支持，合集/媒体库/演职人员不支持", () =>
        {
            foreach (var type in (string[])[EmbyItemType.Movie, EmbyItemType.Series, EmbyItemType.Season, EmbyItemType.Episode])
                Assert.True(MoviePilotVersionQuery.Supports(new EmbyItem { Id = "x", Type = type }), $"{type} 应支持");

            foreach (var type in (string[])[EmbyItemType.BoxSet, EmbyItemType.CollectionFolder, EmbyItemType.Person, EmbyItemType.Folder])
                Assert.False(MoviePilotVersionQuery.Supports(new EmbyItem { Id = "x", Type = type }), $"{type} 不该支持");
        });
    }
}
