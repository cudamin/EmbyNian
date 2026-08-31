using EmbyNian.Emby;
using EmbyNian.Theming;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// Every string and list the 详情页 draws, asserted here rather than on screen.
/// <para>
/// <see cref="ItemDetail"/>'s own class comment says it was split out of the page so that the test
/// project — which references the core assembly and nothing UI — could reach it, and names the WinForms
/// version's untested display helpers as the thing being avoided. It was then left with no tests of its
/// own for the whole of the WinUI rewrite. These are those tests, written while the page moved to MVVM:
/// the view model calls exactly these functions, so a page that renders the wrong subtitle now fails
/// here instead of after a build, a launch and a click.
/// </para>
/// </summary>
internal static class ItemDetailTests
{
    private const long Minute = 60 * 10_000_000L;

    public static void Register()
    {
        RegisterHeadings();
        RegisterFacts();
        RegisterSources();
        RegisterCast();
        RegisterEpisodes();
        RegisterMediaInfo();
        RegisterWebUrl();
        RegisterHero();
    }

    private static void RegisterHeadings()
    {
        Test("详情：单集挂在剧名下，集号与集名在第二行", () =>
        {
            var episode = Episode("旅途的终点", season: 1, number: 2, series: "葬送的芙莉莲");

            // The show is where the reader thinks they are, so it gets the headline.
            Assert.Equal("葬送的芙莉莲", ItemDetail.Title(episode));

            // Deliberately not EpisodeCode's zero-padded S01E02: this one is read as prose.
            Assert.Equal("S1:E2 - 旅途的终点", ItemDetail.Subline(episode));
            Assert.Equal("S01E02", episode.EpisodeCode);
        });

        Test("详情：缺少集号的单集退回集名，不留一个空的连字符", () =>
        {
            var episode = Episode("特别篇", season: null, number: null, series: "某剧");
            Assert.Equal("特别篇", ItemDetail.Subline(episode));
        });

        Test("详情：单集的标题点得动，去它自己的季", () =>
        {
            var episode = new EmbyItem
            {
                Id = "ep7",
                Name = "逮捕才干的律师",
                Type = EmbyItemType.Episode,
                SeriesId = "series1",
                SeriesName = "99.9刑事专业律师",
                SeasonId = "season2",
                SeasonName = "第二季"
            };

            // The season, not the show: the headline reads 「99.9刑事专业律师」 but the list the reader wants
            // next is the one this episode sits in. Only Id and Type have to be right — the page it opens
            // re-fetches by id — and the name rides along so the tooltip can say where the click goes.
            var season = ItemDetail.TitleTarget(episode);
            Assert.Equal("season2", season?.Id);
            Assert.Equal(EmbyItemType.Season, season?.Type);
            Assert.Equal("第二季", season?.Name);

            // A show with flat episodes and no seasons still has somewhere to go.
            var loose = ItemDetail.TitleTarget(new EmbyItem
            {
                Id = "ep7",
                Name = "某集",
                Type = EmbyItemType.Episode,
                SeriesId = "series1",
                SeriesName = "某剧"
            });
            Assert.Equal("series1", loose?.Id);
            Assert.Equal(EmbyItemType.Series, loose?.Type);
            Assert.Equal("某剧", loose?.Name);

            // Nowhere to go disables the button rather than navigating to a page that cannot load: a movie's
            // headline is the movie, and an orphaned episode has no parent to name.
            Assert.Null(ItemDetail.TitleTarget(new EmbyItem { Id = "m1", Type = EmbyItemType.Movie }));
            Assert.Null(ItemDetail.TitleTarget(new EmbyItem { Id = "ep7", Type = EmbyItemType.Episode }));
        });

        Test("详情：电影第二行是类型，季退回剧名", () =>
        {
            var movie = new EmbyItem { Name = "你的名字", Type = EmbyItemType.Movie, Genres = ["动画", "爱情", "奇幻"] };
            Assert.Equal("动画  ·  爱情  ·  奇幻", ItemDetail.Subline(movie));

            var season = new EmbyItem { Name = "第一季", Type = EmbyItemType.Season, SeriesName = "葬送的芙莉莲" };
            Assert.Equal("葬送的芙莉莲", ItemDetail.Subline(season));

            // Nothing to say is an empty string, which is what the page hides the row on.
            Assert.Equal("", ItemDetail.Subline(new EmbyItem { Name = "无信息", Type = EmbyItemType.Movie }));
        });

        Test("详情：评分用不变文化格式化，0 与缺失都当作没有", () =>
        {
            Assert.Equal("8.4", ItemDetail.Score(new EmbyItem { CommunityRating = 8.4f }));

            // 8 rather than 8.0, and a dot even where the machine's separator is a comma.
            Assert.Equal("8", ItemDetail.Score(new EmbyItem { CommunityRating = 8f }));
            Assert.Equal("8.4", ItemDetail.Score(new EmbyItem { CommunityRating = 8.44f }));
            Assert.Equal("", ItemDetail.Score(new EmbyItem { CommunityRating = 0 }));
            Assert.Equal("", ItemDetail.Score(new EmbyItem()));
        });
    }

    private static void RegisterFacts()
    {
        Test("详情：首播日期胜过年份，两者都没有时也有其他片段", () =>
        {
            var precise = new EmbyItem
            {
                Type = EmbyItemType.Movie,
                ProductionYear = 2016,
                PremiereDate = new DateTimeOffset(2016, 8, 26, 0, 0, 0, TimeSpan.FromHours(9)),
                RunTimeTicks = 106 * Minute,
                OfficialRating = "PG-13"
            };

            var parts = ItemDetail.FactParts(precise).ToList();
            Assert.Equal(3, parts.Count);
            Assert.Contains("2016", parts[0], "首播日期在前，且带月日");
            Assert.Equal("1 小时 46 分", parts[1]);
            Assert.Equal("PG-13", parts[2]);

            var yearOnly = new EmbyItem { Type = EmbyItemType.Movie, ProductionYear = 2016 };
            Assert.Equal("2016", ItemDetail.Facts(yearOnly));
            Assert.Equal("", ItemDetail.Facts(new EmbyItem { Type = EmbyItemType.Movie }));
        });

        Test("详情：季数只出现在剧集页", () =>
        {
            var series = new EmbyItem { Type = EmbyItemType.Series, ProductionYear = 2023, ChildCount = 2 };
            Assert.Equal("2023  ·  共 2 季", ItemDetail.Facts(series));

            // A movie with a ChildCount (extras, versions) must not be described as having seasons.
            var movie = new EmbyItem { Type = EmbyItemType.Movie, ProductionYear = 2023, ChildCount = 2 };
            Assert.Equal("2023", ItemDetail.Facts(movie));
        });

        Test("详情：这一行读到的每个字段，详情查询都点了名", () =>
        {
            // Emby gates all of them. A request whose Fields does not name a field gets the item back
            // without it, so 「服务器没有这条数据」 and 「我们没有要」 look exactly the same on screen —
            // verified against the live server, which returned an empty studio list and no EndDate for a
            // show that has both. Dropping a name here would quietly shorten the facts line, and nothing
            // else in this project would notice.
            var fields = EmbyFields.Detail.Split(',', StringSplitOptions.TrimEntries);

            string[] read =
                ["ProductionYear", "PremiereDate", "EndDate", "Studios", "OfficialRating", "CommunityRating", "ChildCount"];

            foreach (var field in read)
                Assert.True(fields.Contains(field), $"详情查询漏了 {field}，对应的那一段会无声地消失");
        });

        Test("详情：剧集写年份区间，电影写首播日", () =>
        {
            // 99.9 as the live server sends it: began 2016, ended 2018-03-11.
            var ended = new EmbyItem
            {
                Type = EmbyItemType.Series,
                ProductionYear = 2016,
                PremiereDate = new DateTimeOffset(2016, 4, 17, 0, 0, 0, TimeSpan.FromHours(9)),
                EndDate = new DateTimeOffset(2018, 3, 11, 0, 0, 0, TimeSpan.FromHours(9))
            };
            Assert.Equal("2016 – 2018", ItemDetail.Years(ended));

            // Still on the air: no second year to print, and 「2016 –」 would only look truncated.
            var running = new EmbyItem { Type = EmbyItemType.Series, ProductionYear = 2016 };
            Assert.Equal("2016", ItemDetail.Years(running));

            // One year start to finish. The range would read 「2018 – 2018」.
            var oneYear = new EmbyItem
            {
                Type = EmbyItemType.Series,
                ProductionYear = 2018,
                EndDate = new DateTimeOffset(2018, 12, 20, 0, 0, 0, TimeSpan.FromHours(9))
            };
            Assert.Equal("2018", ItemDetail.Years(oneYear));

            // A show whose year is only in the premiere date still gets a segment.
            var premiereOnly = new EmbyItem
            {
                Type = EmbyItemType.Series,
                PremiereDate = new DateTimeOffset(2016, 4, 17, 0, 0, 0, TimeSpan.FromHours(9))
            };
            Assert.Equal("2016", ItemDetail.Years(premiereOnly));
            Assert.Equal("", ItemDetail.Years(new EmbyItem { Type = EmbyItemType.Series }));

            var movie = new EmbyItem
            {
                Type = EmbyItemType.Movie,
                ProductionYear = 2016,
                PremiereDate = new DateTimeOffset(2016, 8, 26, 0, 0, 0, TimeSpan.FromHours(9)),
                EndDate = new DateTimeOffset(2018, 3, 11, 0, 0, 0, TimeSpan.FromHours(9))
            };
            Assert.Contains("2016/8/2", ItemDetail.Years(movie), "电影仍然写到日，且不受 EndDate 影响");
        });

        Test("详情：制作方只取第一个，且不写在单集页上", () =>
        {
            var series = new EmbyItem
            {
                Type = EmbyItemType.Series,
                ProductionYear = 2016,
                EndDate = new DateTimeOffset(2018, 3, 11, 0, 0, 0, TimeSpan.FromHours(9)),
                OfficialRating = "KR-15",
                ChildCount = 2,
                Studios = [new EmbyStudio { Name = "TBS" }, new EmbyStudio { Name = "Netflix" }]
            };
            Assert.Equal("TBS", ItemDetail.Studio(series));
            Assert.Equal("2016 – 2018  ·  TBS  ·  KR-15  ·  共 2 季", ItemDetail.Facts(series));

            // An empty first entry is a metadata gap, not the studio's name.
            Assert.Equal("TBS", ItemDetail.Studio(new EmbyItem
            {
                Type = EmbyItemType.Movie,
                Studios = [new EmbyStudio { Name = "  " }, new EmbyStudio { Name = "TBS" }]
            }));

            var episode = Episode("第七集", 2, 7, "99.9");
            episode.Studios.Add(new EmbyStudio { Name = "TBS" });
            Assert.Equal("", ItemDetail.Studio(episode));
            Assert.Equal("", ItemDetail.Facts(new EmbyItem { Type = EmbyItemType.Movie }));
        });

        Test("详情：下一集一行说出是哪一集，并只在有断点时说剩余", () =>
        {
            var series = new EmbyItem { Id = "series1", Name = "99.9", Type = EmbyItemType.Series };
            var next = Episode("逮捕才干的律师", 2, 7, "99.9");
            next.RunTimeTicks = 46 * Minute;
            next.UserData = new EmbyUserData { PlaybackPositionTicks = 67 * 10_000_000L };

            Assert.Equal("S2:E7 - 逮捕才干的律师", ItemDetail.NextUpTitle(series, next));
            Assert.Equal("剩余 44 分钟", ItemDetail.NextUpRemaining(next));
            Assert.True(ItemDetail.NextUpProgress(next) is > 2 and < 3, "进度是百分数，不是 0..1");

            // Nobody has started it: 「剩余 46 分钟」 would only repeat the runtime as if it were progress.
            var fresh = Episode("第一集", 1, 1, "99.9");
            fresh.RunTimeTicks = 46 * Minute;
            Assert.Equal("S1:E1 - 第一集", ItemDetail.NextUpTitle(series, fresh));
            Assert.Equal("", ItemDetail.NextUpRemaining(fresh));
            Assert.Equal(0d, ItemDetail.NextUpProgress(fresh));

            // An episode page already carries the same line as its headline's second row, and a film's
            // next thing to watch is itself.
            Assert.Equal("", ItemDetail.NextUpTitle(next, next));
            Assert.Equal("", ItemDetail.NextUpTitle(series, null));
            Assert.Equal("", ItemDetail.NextUpTitle(series, new EmbyItem { Name = "电影", Type = EmbyItemType.Movie }));
            Assert.Equal("", ItemDetail.NextUpRemaining(null));
        });

        Test("详情：播放按钮说出续播位置，否则说出集号", () =>
        {
            Assert.Equal("播放", ItemDetail.PlayText(null));

            var fresh = Episode("第一集", 1, 1, "某剧");
            Assert.Equal("播放 S01E01", ItemDetail.PlayText(fresh));

            var movie = new EmbyItem { Name = "电影", Type = EmbyItemType.Movie };
            Assert.Equal("播放", ItemDetail.PlayText(movie));

            var resumed = new EmbyItem
            {
                Name = "电影",
                Type = EmbyItemType.Movie,
                RunTimeTicks = 100 * Minute,
                UserData = new EmbyUserData { PlaybackPositionTicks = 20 * Minute + 34 * 10_000_000L }
            };
            Assert.True(resumed.HasResumePosition);
            Assert.Equal("继续播放 20:34", ItemDetail.PlayText(resumed));
        });

        Test("详情：只看了几秒或快看完的不算续播", () =>
        {
            // The resume clock is what makes 从头开始 a second button rather than a toggle, so a position
            // the server kept but nobody would want back must not produce one.
            var barelyStarted = new EmbyItem
            {
                Type = EmbyItemType.Movie,
                RunTimeTicks = 100 * Minute,
                UserData = new EmbyUserData { PlaybackPositionTicks = 2_000_000L }
            };
            Assert.Equal("播放", ItemDetail.PlayText(barelyStarted));

            var nearlyDone = new EmbyItem
            {
                Type = EmbyItemType.Movie,
                RunTimeTicks = 100 * Minute,
                UserData = new EmbyUserData { PlaybackPositionTicks = 9_990 * Minute / 100 }
            };
            Assert.Equal("播放", ItemDetail.PlayText(nearlyDone));
        });

        Test("详情：视频行在没有媒体源时是空的，剧集页因此不显示它", () =>
        {
            Assert.Equal("", ItemDetail.VideoLine(null));

            var source = new MediaSource
            {
                Container = "mkv",
                Size = 9_010_000_000L,
                MediaStreams = [new MediaStream { Index = 0, Type = "Video", Height = 1080, Codec = "hevc" }]
            };
            Assert.Equal("视频：  1080p  ·  HEVC  ·  MKV  ·  8.4 GB", ItemDetail.VideoLine(source));

            // A source the server sent no streams for has nothing to say, not a bare 「视频：」 label.
            Assert.Equal("", ItemDetail.VideoLine(new MediaSource()));
        });

        Test("详情：自动一行说出会选中哪条轨，不知道时说明由谁决定", () =>
        {
            Assert.Equal("自动 · 日语 · AAC", ItemDetail.AutoLabel("日语 · AAC"));
            Assert.Equal("自动（由 mpv 决定）", ItemDetail.AutoLabel(null));
            Assert.Equal("自动（由 mpv 决定）", ItemDetail.AutoLabel("   "));
        });

        Test("详情：服务器发来的原样报文能读进来，工作室的数字 Id 不会拖垮整页", () =>
        {
            // Trimmed from a real 「99.9 刑事专业律师」 response. Emby types an item's own Id as a string but a
            // studio's (and a genre's) as a JSON number; a DTO that declared those as string threw mid-parse
            // and blanked the whole detail page, with nothing but a warning in the log to say why. The fix is
            // to not map them at all, and this payload is here to keep it that way.
            const string json = """
                {"Name":"99.9 刑事专业律师","Id":"5687","PremiereDate":"2016-04-17T00:00:00.0000000Z",
                 "OfficialRating":"KR-15","Genres":["犯罪"],"CommunityRating":7.3,"RunTimeTicks":27600000000,
                 "ProductionYear":2016,"ParentId":"5","Type":"Series","Studios":[{"Name":"TBS","Id":4738}],
                 "GenreItems":[{"Name":"犯罪","Id":1077}],"TagItems":[],"ChildCount":2,"Status":"Ended",
                 "EndDate":"2018-03-11T00:00:00.0000000Z"}
                """;

            var item = System.Text.Json.JsonSerializer.Deserialize<EmbyItem>(json, EmbyHttp.Json);
            Assert.NotNull(item);
            Assert.Equal("TBS", ItemDetail.Studio(item!));
            Assert.Equal("2016 – 2018  ·  TBS  ·  46 分钟  ·  KR-15  ·  共 2 季", ItemDetail.Facts(item!));
        });
    }

    private static void RegisterSources()
    {
        Test("媒体源：名字优先，其次文件名，都没有才说默认", () =>
        {
            var named = new MediaSource { Name = "导演剪辑版", Container = "mkv" };
            Assert.Equal("导演剪辑版  ·  MKV", ItemDetail.SourceLabel(named));

            var byPath = new MediaSource { Path = @"\\nas\films\Arrival.2016.2160p.mkv" };
            Assert.Equal("Arrival.2016.2160p.mkv", ItemDetail.SourceLabel(byPath));

            var qualityOnly = new MediaSource { Container = "mp4" };
            Assert.Equal("MP4", ItemDetail.SourceLabel(qualityOnly));

            Assert.Equal("默认媒体源", ItemDetail.SourceLabel(new MediaSource()));
        });

        Test("媒体源：行的顺序就是服务器给的顺序", () =>
        {
            var item = new EmbyItem
            {
                MediaSources =
                [
                    new MediaSource { Id = "a", Name = "4K HDR" },
                    new MediaSource { Id = "b", Name = "1080p" }
                ]
            };

            var rows = ItemDetail.SourceRows(item);
            Assert.Equal(2, rows.Count);
            Assert.Equal("4K HDR", rows[0].Text);
            Assert.Equal("1080p", rows[1].Text);
            Assert.True(ReferenceEquals(rows[0].Source, item.MediaSources[0]), "行必须持有原对象，播放要用它");

            Assert.Equal(0, ItemDetail.SourceRows(new EmbyItem()).Count);
        });

        Test("媒体源：默认源按引用命中，与 id 无关", () =>
        {
            var item = new EmbyItem
            {
                MediaSources =
                [
                    new MediaSource { Id = "", Name = "第一版" },
                    new MediaSource { Id = "", Name = "第二版" }
                ]
            };

            var rows = ItemDetail.SourceRows(item);

            // Emby leaves the id empty for some direct-play files, so three empty ids all 「match」 each
            // other. The default source is one of these very objects, which settles it without ids.
            Assert.Equal("第二版", ItemDetail.PickSource(rows, item.MediaSources[1])?.Text);
            Assert.Equal("第一版", ItemDetail.PickSource(rows, item.DefaultMediaSource)?.Text);
        });

        Test("媒体源：另取一次得到的源按 id 命中", () =>
        {
            var rows = ItemDetail.SourceRows(new EmbyItem
            {
                MediaSources = [new MediaSource { Id = "src1" }, new MediaSource { Id = "src2" }]
            });

            // Same file, different object: this is the item re-fetched after 标记已看 reloaded the page.
            var refetched = new MediaSource { Id = "src2" };
            Assert.True(ReferenceEquals(rows[1].Source, ItemDetail.PickSource(rows, refetched)?.Source));
        });

        Test("媒体源：无从选择时返回 null 而不是第一行", () =>
        {
            var rows = ItemDetail.SourceRows(new EmbyItem { MediaSources = [new MediaSource { Id = "" }] });

            Assert.Null(ItemDetail.PickSource(rows, null));
            Assert.Null(ItemDetail.PickSource([], new MediaSource { Id = "src1" }));

            // An unrelated source with an empty id must not fall through onto the empty-id row.
            Assert.Null(ItemDetail.PickSource(rows, new MediaSource { Id = "" }));
        });
    }

    private static void RegisterCast()
    {
        Test("演职人员：一人只出一张卡，取第一个署名", () =>
        {
            var item = new EmbyItem
            {
                People =
                [
                    new EmbyPerson { Id = "p1", Name = "种崎敦美", Role = "芙莉莲", Type = "Actor" },
                    new EmbyPerson { Id = "p1", Name = "种崎敦美", Role = "旁白", Type = "Actor" },
                    new EmbyPerson { Id = "p2", Name = "斋藤圭一郎", Type = "Director" },
                    new EmbyPerson { Id = "", Name = "无 id 的人" },
                    new EmbyPerson { Id = "p3", Name = "" }
                ]
            };

            var cast = ItemDetail.Cast(item);
            Assert.Equal(2, cast.Count);
            Assert.Equal("芙莉莲", cast[0].Credit);
            Assert.Equal("导演", cast[1].Credit, "没有角色名时用职务的中文");
        });

        Test("演职人员：卡片是人物类型，所以不会长出播放角标", () =>
        {
            var item = new EmbyItem
            {
                People = [new EmbyPerson { Id = "p1", Name = "某人", Type = "Actor", PrimaryImageTag = "tag1" }]
            };

            var card = ItemDetail.Cast(item)[0].Card;
            Assert.Equal(EmbyItemType.Person, card.Type);
            Assert.False(card.IsPlayable);
            Assert.Equal("tag1", card.PrimaryImageTag, "头像标签要落到图片存储会去找的地方");
        });

        Test("导演行：只收导演、去重、顿号连接", () =>
        {
            var item = new EmbyItem
            {
                People =
                [
                    new EmbyPerson { Id = "p1", Name = "种崎敦美", Role = "芙莉莲", Type = "Actor" },
                    new EmbyPerson { Id = "p2", Name = "斋藤圭一郎", Type = "Director" },
                    new EmbyPerson { Id = "p2", Name = "斋藤圭一郎", Type = "Director" },
                    new EmbyPerson { Id = "p4", Name = "某人", Type = "Writer" }
                ]
            };

            Assert.Equal("导演：斋藤圭一郎", ItemDetail.Directors(item));
            Assert.Equal("", ItemDetail.Directors(new EmbyItem()), "没有导演时整行收起而不是留一个冒号");
        });

        Test("演职人员：超过上限就停下", () =>
        {
            var item = new EmbyItem();
            for (var index = 0; index < ItemDetail.CastLimit + 5; index++)
                item.People.Add(new EmbyPerson { Id = $"p{index}", Name = $"人 {index}", Type = "Actor" });

            Assert.Equal(ItemDetail.CastLimit, ItemDetail.Cast(item).Count);
            Assert.Equal(3, ItemDetail.Cast(item, 3).Count);
        });
    }

    private static void RegisterEpisodes()
    {
        Test("单集：默认打开还没看完的那一季与那一集", () =>
        {
            var seasons = new List<EmbyItem>
            {
                Season("第一季", unplayed: 0),
                Season("第二季", unplayed: 3),
                Season("第三季", unplayed: 8)
            };

            Assert.Equal("第二季", ItemDetail.PickSeason(seasons)?.Name);

            // A show watched to the end opens on its first season rather than on nothing.
            Assert.Equal("第一季", ItemDetail.PickSeason([Season("第一季", 0), Season("第二季", 0)])?.Name);
            Assert.Null(ItemDetail.PickSeason([]));
        });

        Test("单集：有特辑时默认落在正片那一季", () =>
        {
            // 特辑 sorts to the front and is almost never watched, so 「第一个还有没看的季」 taken over the
            // whole list would open every show on its extras.
            var seasons = new List<EmbyItem>
            {
                Season("特辑", unplayed: 4, index: 0),
                Season("第一季", unplayed: 0, index: 1),
                Season("第二季", unplayed: 3, index: 2)
            };

            Assert.Equal("第二季", ItemDetail.PickSeason(seasons)?.Name);

            // Watched through: the first numbered season, still not the specials.
            Assert.Equal("第一季", ItemDetail.PickSeason(
                [Season("特辑", 4, 0), Season("第一季", 0, 1)])?.Name);

            // A show that is nothing but specials has to open on them.
            Assert.Equal("特辑", ItemDetail.PickSeason([Season("特辑", 4, 0)])?.Name);
        });

        Test("单集：播放从第一集未看的开始", () =>
        {
            var episodes = new List<EmbyItem> { Watched("第一集"), Watched("第二集"), Unwatched("第三集") };
            Assert.Equal("第三集", ItemDetail.PickEpisode(episodes)?.Name);

            Assert.Equal("第一集", ItemDetail.PickEpisode([Watched("第一集"), Watched("第二集")])?.Name);
            Assert.Null(ItemDetail.PickEpisode([]));
        });

        Test("单集：标题行数出总数与已看数", () =>
        {
            var episodes = new List<EmbyItem> { Watched("一"), Watched("二"), Unwatched("三") };

            Assert.Equal("更多来自：第一季  ·  3 集，已看 2 集", ItemDetail.EpisodeHeading("第一季", episodes));
            Assert.Equal("更多单集  ·  3 集，已看 2 集", ItemDetail.EpisodeHeading(null, episodes));
            Assert.Equal("更多单集", ItemDetail.EpisodeHeading("第一季", []));
        });

        Test("单集：行标题带编号，行信息只写服务器给的部分", () =>
        {
            var full = new EmbyItem
            {
                IndexNumber = 3,
                Name = "汽笛声",
                RunTimeTicks = 24 * Minute,
                PremiereDate = new DateTimeOffset(2016, 4, 17, 0, 0, 0, TimeSpan.Zero)
            };
            Assert.Equal("3. 汽笛声", ItemDetail.EpisodeRowTitle(full));
            Assert.Equal("2016/4/17  ·  24 分钟", ItemDetail.EpisodeRowInfo(full));

            // No premiere date reads 「46 分钟」 rather than 「 · 46 分钟」; no number reads the name alone.
            Assert.Equal("24 分钟", ItemDetail.EpisodeRowInfo(new EmbyItem { RunTimeTicks = 24 * Minute }));
            Assert.Equal("汽笛声", ItemDetail.EpisodeRowTitle(new EmbyItem { Name = "汽笛声" }));
            Assert.Equal("", ItemDetail.EpisodeRowInfo(new EmbyItem()));
        });

        Test("单集：三种入口都问对了剧与季", () =>
        {
            // A show asks for the season that was picked in the drop-down.
            var series = new EmbyItem { Id = "series1", Name = "某剧", Type = EmbyItemType.Series };
            var picked = new EmbyItem { Id = "season2", Name = "第二季", Type = EmbyItemType.Season };
            AssertScope(ItemDetail.EpisodeScope(series, picked), "series1", "season2", "第二季");

            // A season page asks for itself.
            var season = new EmbyItem
            {
                Id = "season2", Name = "第二季", Type = EmbyItemType.Season, SeriesId = "series1"
            };
            AssertScope(ItemDetail.EpisodeScope(season, null), "series1", "season2", "第二季");

            // An episode page asks for its own siblings, which is what someone who just finished one wants.
            var episode = new EmbyItem
            {
                Id = "ep5",
                Type = EmbyItemType.Episode,
                SeriesId = "series1",
                SeasonId = "season2",
                SeasonName = "第二季"
            };
            AssertScope(ItemDetail.EpisodeScope(episode, null), "series1", "season2", "第二季");
        });

        Test("单集：换季仍然按选择的季请求", () =>
        {
            var series = new EmbyItem { Id = "series1", Type = EmbyItemType.Series };
            var season1 = new EmbyItem { Id = "season1", Name = "第一季", Type = EmbyItemType.Season };
            var season2 = new EmbyItem { Id = "season2", Name = "第二季", Type = EmbyItemType.Season };

            AssertScope(ItemDetail.EpisodeScope(series, season1), "series1", "season1", "第一季");
            AssertScope(ItemDetail.EpisodeScope(series, season2), "series1", "season2", "第二季");
        });

        Test("单集：剧集页缺少季信息时仍然问得出剧 id", () =>
        {
            var loose = new EmbyItem { Id = "series1", Type = EmbyItemType.Series };
            AssertScope(ItemDetail.EpisodeScope(loose, null), "series1", null, null);
        });

        Test("单集：列表里标出正在看的那一集", () =>
        {
            var episodes = new List<EmbyItem>
            {
                new() { Id = "ep1", Type = EmbyItemType.Episode },
                new() { Id = "ep2", Type = EmbyItemType.Episode },
                new() { Id = "ep3", Type = EmbyItemType.Episode }
            };

            var page = new EmbyItem { Id = "ep3", Type = EmbyItemType.Episode };

            Assert.False(ItemDetail.IsCurrentEpisode(episodes[0], page));
            Assert.False(ItemDetail.IsCurrentEpisode(episodes[1], page));
            Assert.True(ItemDetail.IsCurrentEpisode(episodes[2], page), "剧集页列的是自己的同季兄弟，其中一行就是自己");

            // A season switched in the drop-down lists episodes the open one is not among; a season page's
            // own id may even equal an episode's; and a series or movie page has no 「this one」 at all.
            // 全都不标 —— 标不出来的时候第一行也不是答案。
            var elsewhere = new EmbyItem { Id = "ep9", Type = EmbyItemType.Episode };
            Assert.False(episodes.Exists(episode => ItemDetail.IsCurrentEpisode(episode, elsewhere)));

            var asSeason = new EmbyItem { Id = "ep3", Type = EmbyItemType.Season };
            Assert.False(episodes.Exists(episode => ItemDetail.IsCurrentEpisode(episode, asSeason)));
            Assert.False(episodes.Exists(episode => ItemDetail.IsCurrentEpisode(episode, null)));
        });

        Test("单集：只有季页画成竖置列表", () =>
        {
            // 「只有季页的集使用竖置列表列表就好，另外两个页面使用竖置翻页」。四种页面共用一份详情页，
            // 所以这条规则必须是「按页面自己的类型算」的纯函数 —— 上一次它是页面上的一句话，于是给季页
            // 改的列表连剧页和集页一起改掉了。
            Assert.True(ItemDetail.EpisodesAsList(EmbyItemType.Season));

            Assert.False(ItemDetail.EpisodesAsList(EmbyItemType.Series));
            Assert.False(ItemDetail.EpisodesAsList(EmbyItemType.Episode));
            Assert.False(ItemDetail.EpisodesAsList(EmbyItemType.Movie));

            // 类型还没读到的那一瞬间（页面刚构造、请求还没回来）按横带算，和电影页同一档。
            Assert.False(ItemDetail.EpisodesAsList(null));
            Assert.False(ItemDetail.EpisodesAsList(""));
        });

        Test("单集：横带卡片第二行写第几集与时长", () =>
        {
            var full = new EmbyItem { IndexNumber = 7, Name = "汽笛声", RunTimeTicks = 46 * Minute };
            Assert.Equal("第 7 集  ·  46 分钟", ItemDetail.EpisodeCardSubtitle(full));

            // 只写服务器给的那部分，缺了不留一个孤零零的分隔点。
            Assert.Equal("第 7 集", ItemDetail.EpisodeCardSubtitle(new EmbyItem { IndexNumber = 7 }));
            Assert.Equal("46 分钟", ItemDetail.EpisodeCardSubtitle(new EmbyItem { RunTimeTicks = 46 * Minute }));

            // 两样都没有就空着。卡片默认的第二行是「S02E07 · 剧名」，而剧名在这两种页面上都是本页自己
            // 的名字，一张卡重复一遍毫无用处。
            Assert.Equal("", ItemDetail.EpisodeCardSubtitle(new EmbyItem { Name = "汽笛声" }));

            // 第 0 集（特辑）是个正当的编号，不能被「没有编号」吃掉。
            Assert.Equal("第 0 集", ItemDetail.EpisodeCardSubtitle(new EmbyItem { IndexNumber = 0 }));
        });

        Test("单集：横带开在正在看的那一集上", () =>
        {
            var episodes = new List<EmbyItem>
            {
                new() { Id = "ep1", Type = EmbyItemType.Episode },
                new() { Id = "ep2", Type = EmbyItemType.Episode },
                new() { Id = "ep3", Type = EmbyItemType.Episode }
            };

            Assert.Equal(2, ItemDetail.EpisodeFocus(episodes, new EmbyItem
            {
                Id = "ep3", Type = EmbyItemType.Episode
            }));

            // 第一张就是自己时答 0，和「别动」是同一个答案 —— 本来就已经在带子的开头。
            Assert.Equal(0, ItemDetail.EpisodeFocus(episodes, new EmbyItem
            {
                Id = "ep1", Type = EmbyItemType.Episode
            }));

            // 剧页、电影页、换了一季之后的列表里都没有「这一集」，0 读作「别动」而不是「第一集」。
            Assert.Equal(0, ItemDetail.EpisodeFocus(episodes, new EmbyItem
            {
                Id = "ep9", Type = EmbyItemType.Episode
            }));
            Assert.Equal(0, ItemDetail.EpisodeFocus(episodes, new EmbyItem
            {
                Id = "ep3", Type = EmbyItemType.Season
            }));
            Assert.Equal(0, ItemDetail.EpisodeFocus(episodes, null));
            Assert.Equal(0, ItemDetail.EpisodeFocus([], new EmbyItem { Id = "ep3", Type = EmbyItemType.Episode }));
        });
    }

    /// <summary>
    /// 媒体信息, the table under 更多单集. Its heading used to name the episode a show's page was
    /// describing (「媒体信息 · S02E07」); the panel is now only ever shown on the page of the file itself,
    /// so the heading is a constant in markup and the only thing left to assert is the table.
    /// </summary>
    private static void RegisterMediaInfo()
    {
        Test("媒体信息：一张表，标签自带空格和全角冒号", () =>
        {
            var rows = ItemDetail.MediaInfo(new EmbyItem { Tags = ["Atmos", "杜比视界"] }, Silo());

            Assert.Equal("文 件：格 式：大 小：时 长：分辨率：码 率：HDR：色 深：帧 率：音 频：字 幕：路 径：标 签：",
                string.Concat(rows.Select(row => row.Label)));

            // 文 件 is the release name alone: the container is on the 格 式 line and the directory on 路 径.
            Assert.Equal("Silo.S03E09.2026.2160p.ATVP.WEB-DL.DV.H.265.DDP.5.1.Atmos-FROGWeb", rows[0].Value);
            Assert.Equal("HEVC · Main 10 · MKV", rows[1].Value);
            Assert.Equal("10.8 GB", rows[2].Value);
            Assert.Equal("1 小时 1 分", rows[3].Value);
            Assert.Equal("3840 x 1606", rows[4].Value);

            // The video stream's own rate, not the container's 25 Mb/s — the panel describes one file, and
            // the stream is the number MediaInfo quotes.
            Assert.Equal("24.2 Mb/s", rows[5].Value);
            Assert.Equal("Dolby Vision", rows[6].Value, "HDR 一行照抄服务器的拼法，不自己起名字");
            Assert.Equal("10 bits", rows[7].Value);
            Assert.Equal("23.976 FPS", rows[8].Value);
            Assert.Equal("01: English DDP 5.1 Atmos @768 kb/s", rows[9].Value);
            Assert.Equal("01: 简体中文", rows[10].Value);
            Assert.Equal(@"\\nas\media\TV\Silo\Season 03", rows[11].Value);
            Assert.Equal("Atmos、杜比视界", rows[12].Value);
        });

        Test("媒体信息：字幕两个一行，落单的那个占满整行", () =>
        {
            var source = new MediaSource();
            for (var i = 1; i <= 5; i++)
            {
                source.MediaStreams.Add(new MediaStream { Type = "Subtitle", DisplayTitle = $"字幕 {i}" });
            }

            var rows = ItemDetail.MediaInfo(new EmbyItem(), source);
            Assert.Equal(3, rows.Count, "五条字幕占三行，别的什么都没有");

            Assert.Equal("字 幕：", rows[0].Label);
            Assert.Equal("", rows[1].Label, "续行不再重复标签，整块读下来是一段");
            Assert.Equal("01: 字幕 1", rows[0].Value);
            Assert.Equal("02: 字幕 2", rows[0].Second);
            Assert.Equal(1, rows[0].Span);

            // 05 has no partner, so it takes both value columns rather than leaving half a line empty.
            Assert.Equal("05: 字幕 5", rows[2].Value);
            Assert.Equal("", rows[2].Second);
            Assert.Equal(2, rows[2].Span);
        });

        Test("媒体信息：服务器没说的不占行，一个源都没有时说清楚", () =>
        {
            var thin = ItemDetail.MediaInfo(new EmbyItem(), new MediaSource { Container = "mp4" });
            Assert.Equal(1, thin.Count);
            Assert.Equal("格 式：", thin[0].Label);
            Assert.Equal("MP4", thin[0].Value);

            var none = ItemDetail.MediaInfo(new EmbyItem(), null);
            Assert.Equal(1, none.Count);
            Assert.Contains("没有返回可播放的媒体源", none[0].Value);
        });
    }

    private static void RegisterWebUrl()
    {
        Test("网页端：/emby 要脱掉，且不分大小写", () =>
        {
            var item = new EmbyItem { Id = "42", ServerId = "srv" };

            Assert.Equal("http://192.168.31.230:8896/web/index.html#!/item?id=42&serverId=srv",
                ItemDetail.WebUrl(new Uri("http://192.168.31.230:8896/emby/"), item));

            // The address came from something a person typed, so the case is not ours to assume.
            Assert.Equal("http://h:8096/web/index.html#!/item?id=42&serverId=srv",
                ItemDetail.WebUrl(new Uri("http://h:8096/EMBY/"), item));

            // A reverse proxy's sub-path is part of the web root and must survive.
            Assert.Equal("https://nas.example.com/media/web/index.html#!/item?id=42&serverId=srv",
                ItemDetail.WebUrl(new Uri("https://nas.example.com/media/emby/"), item));
        });

        Test("网页端：没有 serverId 就不带，id 会转义", () =>
        {
            Assert.Equal("http://h:8096/web/index.html#!/item?id=42",
                ItemDetail.WebUrl(new Uri("http://h:8096/emby/"), new EmbyItem { Id = "42" }));

            Assert.Contains("id=a%20b",
                ItemDetail.WebUrl(new Uri("http://h:8096/emby/"), new EmbyItem { Id = "a b" }));
        });
    }

    /// <summary>
    /// 头图那一格有多高，以及它底下那张纸至少要多高 —— 「图一页面怎么改的一大片空白，改回去」和「滑到下面
    /// 不用显示背景了，五颜六色的太丑了」。前一句要的是「高跟窗口无关」，后一句要的是「纸补满看得见的那一段」。
    /// </summary>
    private static void RegisterHero()
    {
        Test("头图：高由内容定，跟窗口无关", () =>
        {
            // 「一大片空白」的修法本身：窗口再高，这一格还是那一格带子。
            Assert.Equal(DetailHero.ArtHeight, DetailHero.Height(true));
            Assert.Equal(DetailHero.PlainHeight, DetailHero.Height(false));

            // 没有画面可看的那一档更矮 —— 多留的每一像素都是空白。
            Assert.True(DetailHero.PlainHeight < DetailHero.ArtHeight);
        });

        Test("正文：补满头图下面看得见的那一段", () =>
        {
            // 887 高的视口减去 460 的带子，剩下的 427 是整个 BodyRegion 的下限。HeroTail 先占实际内容高，
            // 星号行再把余下高度交给 BodySheet；不给这个下限，短页面的下半屏就漏出背景图。
            Assert.Equal(427d, DetailHero.BodyHeight(887, true));
            Assert.Equal(507d, DetailHero.BodyHeight(887, false));

            // 视口是小数的那一下（缩放比不是整数时常有），四舍五入到整像素，不留半像素的缝。
            Assert.Equal(427d, DetailHero.BodyHeight(886.6, true));
        });

        Test("正文：比头图还矮的窗口上不要负数", () =>
        {
            // 窗口矮到带子都放不下时，纸没有下限可言 —— 该滚动，而不是撑出一格负高来。
            Assert.Equal(0d, DetailHero.BodyHeight(300, true));

            // 还没量到视口的那一下不是「窗口很矮」而是「还没量」，同样给 0。
            Assert.Equal(0d, DetailHero.BodyHeight(0, true));
            Assert.Equal(0d, DetailHero.BodyHeight(-40, true));
        });

        // 「往下拉之后标题颜色要渐变，变的和下方背景一样」—— 洗多浓这件事的那条曲线。它读的是头图底下那道渐深的
        // 罩子在视口上沿那一行的浓度，所以下面这几个数就是屏上那道罩子的五个停点，只是换到了页面坐标上。
        Test("标题条：洗的浓度读的是那道罩子自己的曲线", () =>
        {
            // 罩子上沿以上没有罩子：还没往下拉的那一下一点都不洗，也就是已经定下来的那一版原样不动。
            Assert.Equal(0d, DetailHero.TopWash(0, true));
            Assert.Equal(0d, DetailHero.TopWash(20, true));
            Assert.Equal(0d, DetailHero.TopWash(-80, true));

            // 没有剧照就没有那道罩子，也就没有那道横缝要补。
            Assert.Equal(0d, DetailHero.TopWash(300, false));

            // 那五个停点：#00@0、#40@0.14、#7A@0.34、#AA@0.62、#B8@1。罩子高 440，坐在 460 那格带子的下沿上，
            // 所以页面坐标要让出 20。这里照旧写死这几个数：DetailHero.ScrimStops 现在是屏上那支画刷的唯一来源，
            // 拿它自己去算就成了同一句话说两遍 —— 写死才拦得住「把表改了、曲线跟着变了，可谁都没发觉」。
            Near(0x40 / 255d, DetailHero.TopWash(20 + (0.14 * 440), true));
            Near(0x7A / 255d, DetailHero.TopWash(20 + (0.34 * 440), true));
            Near(0xAA / 255d, DetailHero.TopWash(20 + (0.62 * 440), true));

            // 拉过一整格带子之后浓度停在最浓那一档，不是满黑 ——「太黑了都看不清背景」：那张固定的剧照在这之后
            // 仍然透着（见 DetailHero.ScrimCeiling），接下来经过的是同一个 alpha 的 HeroTail，纸面位置由
            // PaperCover 另算。
            Assert.Equal(DetailHero.ScrimCeiling / 255d, DetailHero.TopWash(DetailHero.ArtHeight, true));
            Assert.Equal(DetailHero.ScrimCeiling / 255d, DetailHero.TopWash(4000, true));

            // 满黑那一版留下的坑：这一档要是回到 1，标题条在尾部经过时就比尾部本身深一截，横缝原样长回来。
            Assert.True(DetailHero.ScrimCeiling < 0xFF);
        });

        Test("标题条：这条曲线一路只深不浅", () =>
        {
            // 中间任何一段掉头，屏上就是往下拉的时候标题条忽然变浅一下 —— 比原来那道缝更难看。
            var last = -1d;

            for (var y = 0d; y <= DetailHero.ArtHeight + 40; y += 4)
            {
                var wash = DetailHero.TopWash(y, true);

                Assert.True(wash >= last, $"{y:0} 处洗回去了：{last:0.000}→{wash:0.000}");
                Assert.True(wash is >= 0 and <= 1, $"{y:0} 处洗出了 0..1 之外的数 {wash:0.000}");

                last = wash;
            }

            Assert.Equal(DetailHero.ScrimCeiling / 255d, last);
        });

        // 屏上那支画刷现在是拿 ScrimStops 生成的（DetailPage.PaintScrim），XAML 里那份手写的副本已经撤掉。
        // 所以这张表从「一份注释」变成了「画面本身」：它错一格，屏上就跟着错一格，而这一层没有眼睛看着。
        Test("罩子：那张表本身站得住", () =>
        {
            var stops = DetailHero.ScrimStops;

            // 一支渐变至少要有头有尾。生成那一支画刷时是照着表一格一格加停点的，表空了屏上就是一格不透明的墨。
            Assert.True(stops.Count >= 2, $"表上只有 {stops.Count} 个停点");

            // 头尾必须正好落在 0 和 1：渐变停点的 Offset 就是这个数，头不在 0 上、罩子上沿就凭空出现一道边；
            // 尾不在 1 上、末段就留一截没有停点的地方，由画刷自己按最后一格铺平 —— 那一截和 HeroTail 的接缝
            // 位置就跟着表走，而 BodySeal 只看颜色对不对，看不出它提前平了多少。
            Assert.Equal(0d, stops[0].Along);
            Assert.Equal(1d, stops[^1].Along);

            // 上沿透明、末档就是 ScrimCeiling：前者是「罩子上面还能看见剧照」，后者是尾部那一块要接住的颜色
            // （HeroTail 的底色取的是末档，见 PaintScrim）。
            Assert.Equal(0x00, stops[0].Alpha);
            Assert.Equal(DetailHero.ScrimCeiling, stops[^1].Alpha);

            // 一路往下只深不浅，位置也只往下走。TopWash 那两条测的是「读出来的曲线」，这一条测的是「表本身」：
            // 表上掉一次头，屏上的罩子中间就浅一道，而 TopWash 照旧读得出同一道浅 —— 两边一起错就没人报警。
            for (var i = 1; i < stops.Count; i++)
            {
                Assert.True(stops[i].Along > stops[i - 1].Along,
                    $"第 {i} 个停点没往下走：{stops[i - 1].Along:0.00}→{stops[i].Along:0.00}");
                Assert.True(stops[i].Alpha >= stops[i - 1].Alpha,
                    $"第 {i} 个停点浅回去了：0x{stops[i - 1].Alpha:X2}→0x{stops[i].Alpha:X2}");
            }

            // 罩子下沿正好落在带子下沿上：它是 VerticalAlignment=Bottom 加一个写死的高，所以上面让出的那段
            // （自检拿 带子高 - 罩子高 读回来当 inset）必须就是 ScrimInset。这两个数任一动了而另一个没动，
            // TopWash 算的那条曲线就和屏上的罩子错开一段。
            Assert.Equal(DetailHero.ArtHeight, DetailHero.ScrimInset + DetailHero.ScrimHeight);

            // 那支墨的 RGB。生成停点时只取 R/G/B、alpha 由表给，所以这三个数就是罩子的颜色本身；
            // 尾部那一块也是拿它上色的，差一位就是尾部和罩子之间横着一道色差。
            Assert.Equal(0x0C, DetailHero.ScrimInk.R);
            Assert.Equal(0x0E, DetailHero.ScrimInk.G);
            Assert.Equal(0x11, DetailHero.ScrimInk.B);
        });

        // 「太黑了都看不清背景」的另一半：透光到什么程度算过头，由字说话，不由眼睛说话。这一条把
        // DetailHero.ScrimCeiling 那段注释里的理由钉住 —— 再往下调这个数，这里先红。
        Test("头图罩子：最浓那一档底下，最淡的那行小字仍读得出", () =>
        {
            // 罩子和尾部那一家黑，以及压在图上那两支墨（Palette 里的 EgOnScrim*，那两支不跟主题走）。
            var veil = ThemeColor.Parse("#0C0E11");
            var dim = ThemeColor.Parse("#C9D0D9");
            var ink = ThemeColor.Parse("#F3F5F8");

            // 最坏的剧照：最亮处按纯白算（雪景、白墙、天空）。Mix 就是 sRGB 上的这一次合成，屏上那一层
            // 半透明黑压下来得到的正是这个色。
            var worst = ThemeColor.Rgb(0xFF, 0xFF, 0xFF).Mix(veil, DetailHero.ScrimCeiling / 255d);

            // 简介那一段是 14 的字、走淡墨，所以它卡的是正文那条 4.5:1，不是大字号那条 3:1。
            var faint = ThemeColor.Contrast(dim, worst);
            var strong = ThemeColor.Contrast(ink, worst);

            Assert.True(faint >= 4.5, $"最淡的小字在最亮的剧照上只有 {faint:0.00}:1，罩子透过头了");
            Assert.True(strong >= 4.5, $"片名那支墨在最亮的剧照上只有 {strong:0.00}:1，罩子透过头了");
        });

        Test("标题条：正文纸面按实际位置覆盖", () =>
        {
            const double paperTop = 660;
            const double titleHeight = 72;

            Assert.Equal(0d, DetailHero.PaperCover(620, paperTop, titleHeight));
            Assert.Equal(0d, DetailHero.PaperCover(paperTop, paperTop, titleHeight));
            Assert.Equal(0.5, DetailHero.PaperCover(paperTop + 36, paperTop, titleHeight));
            Assert.Equal(1d, DetailHero.PaperCover(paperTop + titleHeight, paperTop, titleHeight));
            Assert.Equal(1d, DetailHero.PaperCover(4000, paperTop, titleHeight));

            // 第一次布局还没量到标题栏高度时，只要纸面越过边界就直接按盖满处理，不能除以 0。
            Assert.Equal(1d, DetailHero.PaperCover(paperTop + 1, paperTop, 0));
        });

        Test("标题条：翻墨的那条线带回差", () =>
        {
            // 剧照和黑色 HeroTail 都用固定浅墨；正文纸面覆盖到四分之三后才换回主题墨。一条线上翻的话，
            // 指针在纸面边界附近轻轻一滚就会来回跳。
            Assert.False(DetailHero.WashedOver(0.70, false));
            Assert.True(DetailHero.WashedOver(0.75, false));

            // 已经翻过去了，得跌到 0.6 以下才翻回来。
            Assert.True(DetailHero.WashedOver(0.65, true));
            Assert.False(DetailHero.WashedOver(0.55, true));
        });

        // 差半级看不出来，差一级就是一道缝，所以按 255 级比。
        static void Near(double expected, double actual) =>
            Assert.True(Math.Abs(expected - actual) * 255 < 1,
                $"洗的浓度差了 {(expected - actual) * 255:0.0} 级：该 {expected:0.000}，是 {actual:0.000}");
    }

    /// <summary>
    /// The file from the reference the table was drawn to match: one 4K Dolby Vision video stream, one
    /// Atmos track, one subtitle, and a release name with a dot in every gap so 文 件 has to strip exactly
    /// one extension.
    /// </summary>
    private static MediaSource Silo() => new()
    {
        Path = @"\\nas\media\TV\Silo\Season 03\Silo.S03E09.2026.2160p.ATVP.WEB-DL.DV.H.265.DDP.5.1.Atmos-FROGWeb.mkv",
        Container = "mkv",
        Size = 11_596_411_699L,
        RunTimeTicks = 61 * Minute,
        Bitrate = 25_000_000,
        MediaStreams =
        [
            new MediaStream
            {
                Type = "Video",
                Codec = "hevc",
                Profile = "Main 10",
                Width = 3840,
                Height = 1606,
                BitRate = 24_200_000,
                BitDepth = 10,
                AverageFrameRate = 23.976023,
                VideoRange = "Dolby Vision"
            },
            new MediaStream { Type = "Audio", DisplayTitle = "English DDP 5.1 Atmos", BitRate = 768_000 },
            new MediaStream { Type = "Subtitle", DisplayTitle = "简体中文" }
        ]
    };

    private static EmbyItem Episode(string name, int? season, int? number, string series) => new()
    {
        Name = name,
        Type = EmbyItemType.Episode,
        SeriesName = series,
        ParentIndexNumber = season,
        IndexNumber = number
    };

    private static EmbyItem Season(string name, int unplayed, int? index = null) => new()
    {
        Name = name,
        Type = EmbyItemType.Season,
        IndexNumber = index,
        UserData = new EmbyUserData { UnplayedItemCount = unplayed }
    };

    private static EmbyItem Watched(string name) => new()
    {
        Name = name,
        Type = EmbyItemType.Episode,
        UserData = new EmbyUserData { Played = true }
    };

    private static EmbyItem Unwatched(string name) => new() { Name = name, Type = EmbyItemType.Episode };

    /// <summary>
    /// Asserts one <see cref="ItemDetail.EpisodeScope"/> answer a field at a time, rather than comparing
    /// the tuple whole: two of the three are nullable, and a tuple literal holding <c>null</c> has no
    /// natural type for <see cref="Assert.Equal{T}"/> to infer from.
    /// </summary>
    private static void AssertScope(
        (string SeriesId, string? SeasonId, string? SeasonName) scope,
        string seriesId,
        string? seasonId,
        string? seasonName)
    {
        Assert.Equal(seriesId, scope.SeriesId);
        Assert.Equal(seasonId, scope.SeasonId);
        Assert.Equal(seasonName, scope.SeasonName);
    }
}
