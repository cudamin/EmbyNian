using System.Net;
using System.Text.Json;
using Momoka.Configuration;
using Momoka.MoviePilot;
using static Momoka.Tests.TestHarness;

namespace Momoka.Tests;

internal static class MoviePilotAuditTests
{
    public static void Register()
    {
        MoviePilotSafetyTests.Register();
        MoviePilotQuerySafetyTests.Register();

        Test("MoviePilot 资源：标签优惠时间与浮点体积完整解析", () =>
        {
            var rows = MoviePilotMediaParser.ParseResources(Json("""
                [{"meta_info":{"resource_pix":"1080p"},"media_info":{"title":"某剧","source":"themoviedb","tmdb_id":123},
                  "torrent_info":{"title":"A","description":"简繁字幕","site_name":"站A","size":719463279.0,
                    "pubdate":"2026-09-27T04:30:00Z","downloadvolumefactor":"0","uploadvolumefactor":2,
                    "labels":["中字","中字",{"name":"官方"}],"hit_and_run":true,"page_url":"https://example.org/details?id=9"}}]
                """), null, null);
            var row = rows[0];
            Assert.Equal(719463279L, row.Size);
            Assert.Equal("中字 · 官方", row.LabelsText);
            Assert.Equal("免费下载 · 上传 2 倍", row.PromotionText);
            Assert.True(row.IsFree && row.HasDiscount && row.HitAndRun);
            Assert.Equal(2026, row.PublishedAt!.Value.Year);
            Assert.Equal("123", row.MediaId);
            Assert.Equal("themoviedb", row.MediaSource);
            Assert.Equal("https://example.org/details?id=9", row.DetailsUri!.AbsoluteUri);
            Assert.Equal("简繁字幕", row.Description);
        });

        Test("MoviePilot 资源：详情仅认网页，未知优惠不冒充免费", () =>
        {
            var row = Resource("unknown");
            foreach (var invalid in new[] { "file:///c:/secret", "javascript:alert(1)", "magnet:?xt=x", "C:/x.exe", "https://user:secret@example.org/" })
                Assert.Null((row with { PageUrl = invalid }).DetailsUri);
            Assert.False(row.IsFree || row.HasDiscount);
            Assert.Equal("优惠未提供", row.PromotionText);
            Assert.Equal("发布时间未提供", row.PublishedLine);
            Assert.False((row with { Promotion = "FREE", DownloadFactor = 1 }).IsFree, "数值因子优先，过期的 FREE 字样不能覆盖它");
        });

        Test("MoviePilot 资源过滤：多个条件求交集，缺时间不混进最近发布", () =>
        {
            var now = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
            var rows = new[]
            {
                Resource("Show 1080p") with { SiteName = "A", Description = "中字", Labels = ["官组"], Resolution = "1080p", DownloadFactor = 0, Seeders = 8, PublishedAt = now.AddDays(-2) },
                Resource("Show 1080p old") with { SiteName = "A", Description = "中字", Labels = ["官组"], Resolution = "1080p", DownloadFactor = 0, Seeders = 9 },
                Resource("Show 1080p HR") with { SiteName = "A", Description = "中字", Labels = ["官组"], Resolution = "1080p", DownloadFactor = 0, Seeders = 4, PublishedAt = now, HitAndRun = true }
            };
            var filter = new MoviePilotResourceFilter
            {
                Text = "show 中字",
                Site = "A",
                Label = "官组",
                Resolution = "1080p",
                Promotion = MoviePilotPromotionFilter.Free,
                SeededOnly = true,
                ExcludeHitAndRun = true,
                PublishedWithinDays = 7
            };
            Assert.Equal(1, filter.Apply(rows, now).Count());
            Assert.Equal("Show 1080p", filter.Apply(rows, now).Single().Title);
            Assert.Equal(3, new MoviePilotResourceFilter().Apply(rows, now).Count(), "清除筛选恢复全部原始结果");
            Assert.Equal("Show 1080p old", (new MoviePilotResourceFilter { Sort = MoviePilotResourceSort.Seeders }).Apply(rows, now).First().Title);
        });

        Test("MoviePilot 资源排序：未知体积与时间排后，最新按时间而非字符串", () =>
        {
            var rows = new[] { Resource("unknown"), Resource("small") with { Size = 2 }, Resource("large") with { Size = 10 } };
            var sorted = (new MoviePilotResourceFilter { Sort = MoviePilotResourceSort.Smallest }).Apply(rows, DateTimeOffset.Now).ToList();
            Assert.Equal("small", sorted[0].Title);
            Assert.Equal("unknown", sorted[2].Title);
        });

        Test("MoviePilot 原记录：目标路径优先，不选后续用软链接作源的记录", () =>
        {
            var original = History(20, "/downloads/source.mp4", "/library/S00E07.mp4");
            var later = History(26, "/library/S00E07.mp4", "/elsewhere/S04E24.mp4");
            var candidates = MoviePilotTransferHistoryMatch.Candidates([later, original], "/library/S00E07.mp4");
            Assert.Equal(1, candidates.Count);
            Assert.Equal(20L, candidates[0].Id);
            Assert.Equal("/downloads/source.mp4", candidates[0].TransferPath);
            Assert.Equal("/library/S00E07.mp4", (original with { Mode = "move" }).TransferPath);
        });

        Test("MoviePilot 原记录：缺失和多条匹配都不猜文件", () =>
        {
            var context = new MoviePilotTransferContext("某剧", "电视剧", "1", 0, "7", [new("e", "/library/S00E07.mp4")]);
            Assert.Catch<MoviePilotException>(() => MoviePilotTransferHistoryMatch.RequireMatches(context, []));
            Assert.Catch<MoviePilotException>(() => MoviePilotTransferHistoryMatch.RequireMatches(context,
                [History(1, "/a", "/library/S00E07.mp4"), History(2, "/b", "/library/S00E07.mp4")]));
        });

        Test("MoviePilot 原记录重整：发送 logid 与新季集，不回传 Emby 软链接", () =>
        {
            var history = History(20, "/downloads/source.mp4", "/library/S00E07.mp4");
            var request = Request(history);
            var body = request.Body([history.SourceFile], preview: true);
            Assert.Equal(20L, body["logid"]);
            Assert.False(body.ContainsKey("fileitem") || body.ContainsKey("fileitems"));
            Assert.Equal(4, body["season"]);
            Assert.Equal("24", body["episode_detail"]);
            Assert.Equal(false, body["from_history"]);
            Assert.Equal(true, body["library_type_folder"]);
            Assert.Equal(false, body["skip_success"]);
            Assert.NotNull((request with { FromHistory = true }).Problem);
        });

        Test("MoviePilot 原记录重整：文件必须精确匹配且不能是目录", () =>
        {
            var history = History(20, "/downloads/source.mp4", "/library/S00E07.mp4");
            Assert.Catch<MoviePilotException>(() => Request(history).Body([File("/library/S00E07.mp4")], true));
            Assert.Catch<MoviePilotException>(() => MoviePilotTransfer.RequireFile(Json("""{"storage":"local","type":"dir","path":"/downloads"}"""), "/downloads"));
        });

        Test("MoviePilot 原记录服务：分页取全，目标按 logid 匹配并保留分类刮削", () =>
        {
            var transport = new StubTransport().Answer("login/access-token", Session)
                .Sequence("history/transfer", (HttpStatusCode.OK, """{"success":true,"data":{"total":2,"list":[{"id":1,"title":"T","src":"/a","dest":"/b"}]}}"""),
                    (HttpStatusCode.OK, """{"success":true,"data":{"total":2,"list":[{"id":2,"title":"T","src":"/c","dest":"/d"}]}}"""))
                .Answer("transfer/manual/target-path", """{"success":true,"data":{"target_storage":"local","target_path":"/library","transfer_type":"softlink","scrape":true,"library_type_folder":true,"library_category_folder":true}}""");
            var service = Service(transport);
            var histories = service.FindTransferHistoriesAsync("T", CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(2, histories.Count);
            Assert.Contains("page=2", transport.SentTo("history/transfer")[1].Url);
            var options = service.TransferHistoryTargetAsync([histories[0]], CancellationToken.None).GetAwaiter().GetResult();
            var directory = options.Directories.Single();
            Assert.True(directory.Scrape && directory.TypeFolder && directory.CategoryFolder);
            Assert.Contains("\"logid\":1", transport.Only("transfer/manual/target-path").Body);
        });

        Test("MoviePilot 原记录服务：分页重复或不完整时明确停止", () =>
        {
            var transport = new StubTransport().Answer("login/access-token", Session)
                .Answer("history/transfer", """{"success":true,"data":{"total":2,"list":[{"id":1,"title":"T"}]}}""");
            Assert.Catch<MoviePilotException>(() => Service(transport).FindTransferHistoriesAsync("T", CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(2, transport.Count("history/transfer"));
        });

        Test("MoviePilot 原记录：外部软链接依赖必须阻止清理原目标", () =>
        {
            var original = History(20, "/downloads/source.mp4", "/library/S00E07.mp4");
            var later = History(26, original.TargetPath, "/elsewhere/S04E24.mp4");
            Assert.Equal(26L, MoviePilotTransferHistoryMatch.Dependents([original], [original, later]).Single().Id);
            Assert.Equal(0, MoviePilotTransferHistoryMatch.Dependents([original], [original, later with { Mode = "copy" }]).Count);
        });

        Test("MoviePilot 预览：绿色但缺文件、多文件或换源都不能提交", () =>
        {
            var history = History(20, "/downloads/source.mp4", "/library/S00E07.mp4");
            var request = Request(history);
            var line = new MoviePilotTransferLine { Source = history.SourcePath, Target = "/library/S04E24.mp4", Success = true, State = "preview" };
            var result = new MoviePilotTransferResult(true, "", [line], true);
            Assert.True(new MoviePilotTransferPreview(request, [history.SourceFile], result).CanSubmit);
            Assert.False(new MoviePilotTransferPreview(request, [history.SourceFile, File("/second.mp4")], result).CanSubmit);
            Assert.False(new MoviePilotTransferPreview(request, [File("/wrong.mp4")], result).CanSubmit);
        });

        Test("MoviePilot 精确搜剧：媒体类型跟随剧编号发送", () =>
        {
            var transport = new StubTransport().Answer("login/access-token", Session).Answer("search/media", "[]");
            Service(transport).SearchResourcesAsync(new MoviePilotMedia { Title = "T", Type = "电视剧", MediaId = "82684", MediaSource = "themoviedb" },
                CancellationToken.None).GetAwaiter().GetResult();
            Assert.Contains("mtype=" + Uri.EscapeDataString("电视剧"), transport.Only("search/media").Url);
        });

        Test("MoviePilot 原记录：读取后换服务器不能拿相同 logid 去新服务器预览", () =>
        {
            var transport = new StubTransport().Answer("login/access-token", Session)
                .Answer("history/transfer", """{"success":true,"data":{"total":1,"list":[{"id":20,"title":"T","src":"/source.mp4","dest":"/target.mp4","src_fileitem":{"storage":"local","type":"file","path":"/source.mp4"}}]}}""");
            var settings = Settings();
            var service = Service(transport, settings);
            var history = service.FindTransferHistoriesAsync("T", CancellationToken.None).GetAwaiter().GetResult().Single();
            settings.MoviePilot.Url = "192.0.2.20:3001";
            Assert.Catch<MoviePilotTransferBlockedException>(() => service.TransferHistoryTargetAsync([history], CancellationToken.None).GetAwaiter().GetResult());
            Assert.Catch<MoviePilotTransferBlockedException>(() => service.TransferPreviewAsync(Request(history), [history.SourceFile], CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(0, transport.Count("transfer/manual"));
        });

        Test("MoviePilot 搜索：来源筛选按重复 media_source 传给 media/search", () =>
        {
            var transport = new StubTransport().Answer("login/access-token", Session)
                .Answer("media/search", """[{"title":"三体","media_source":"douban","media_id":"123"}]""");
            var found = Service(transport).SearchAsync("三体", CancellationToken.None, ["douban", "themoviedb", "douban"])
                .GetAwaiter().GetResult();

            Assert.Equal(1, found.Count);
            Assert.Equal("douban", found[0].MediaSource);
            var sent = transport.Only("media/search");
            Assert.Contains("media_source=douban&media_source=themoviedb", sent.Url, "重复参数是官方数组的写法，去重且保序");
        });

        Test("MoviePilot 搜索：不选来源就不带 media_source", () =>
        {
            var transport = new StubTransport().Answer("login/access-token", Session).Answer("media/search", "[]");
            Service(transport).SearchAsync("三体", CancellationToken.None).GetAwaiter().GetResult();

            Assert.DoesNotContain("media_source", transport.Only("media/search").Url, "全来源由服务器自己排序分页");
        });

        Test("MoviePilot 搜索：IMDb 是独立来源，编号是 tt 加数字", () =>
        {
            const string Results = """
                [
                  {"title":"肖申克的救赎","year":1994,"type":"电影","media_source":"imdb","media_id":"tt0111161"},
                  {"title":"某剧","type":"电视剧","media_source":"imdb","imdb_id":"tt0944947"}
                ]
                """;
            var list = MoviePilotMediaParser.Parse(Json(Results));

            Assert.Equal(2, list.Count);
            Assert.Equal("tt0111161", list[0].MediaId);
            Assert.Equal("IMDb", list[0].SourceLabel);
            Assert.True(list[0].CanSubscribe, "imdb + tt 编号就是完整身份对");
            Assert.Equal("tt0944947", list[1].MediaId, "没有 media_id 时退到来源自己的辅助编号");
        });

        Test("MoviePilot 来源：已知来源显示名字，未知原样，空给空", () =>
        {
            Assert.Equal("豆瓣", MoviePilotSourceNames.Label("douban"));
            Assert.Equal("豆瓣音乐", MoviePilotSourceNames.Label("doubanmusic"));
            Assert.Equal("acfun", MoviePilotSourceNames.Label("acfun"), "插件来源不折叠成「其他」");
            Assert.Equal("", MoviePilotSourceNames.Label(null));
        });

        Test("MoviePilot 来源：影视筛选认电影电视剧，音乐来源不混进来", () =>
        {
            Assert.True(new MoviePilotMediaSource("TMDB", "themoviedb", ["电影", "电视剧"]).IsVideo);
            Assert.False(new MoviePilotMediaSource("插件", "someplugin", []).IsVideo, "显式空能力列表不是影视支持");
            Assert.False(new MoviePilotMediaSource("豆瓣音乐", "doubanmusic", ["专辑", "单曲"]).IsVideo);
        });

        Test("MoviePilot 来源目录：media/source 拆成来源，无名退回标识", () =>
        {
            var transport = new StubTransport().Answer("login/access-token", Session)
                .Answer("media/source", """
                    [{"name":"豆瓣","media_source":"douban","media_types":["电影","电视剧"]},
                     {"media_source":"acfun"},
                     {"name":"豆瓣音乐","media_source":"doubanmusic","media_types":["专辑"]}]
                    """);
            var sources = Service(transport).MediaSourcesAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(3, sources.Count);
            Assert.Equal("豆瓣", sources[0].Name);
            Assert.Equal("acfun", sources[1].Name, "没给名字就用标识，不留空名字在屏上");
            Assert.Equal("专辑", string.Join("|", sources[2].Types));
        });

        Test("MoviePilot 提交：换连接不能把旧预览发到新服务器", () =>
        {
            var transport = new StubTransport().Answer("login/access-token", Session)
                .Answer("transfer/manual", """{"success":true,"data":{"items":[{"source":"/source.mp4","target":"/dest.mp4","success":true}]}}""");
            var settings = Settings();
            var service = Service(transport, settings);
            var request = new MoviePilotTransferRequest { Files = [new("source", "/source.mp4")] };
            var preview = service.TransferPreviewAsync(request, [File("/source.mp4")], CancellationToken.None).GetAwaiter().GetResult();
            settings.MoviePilot.Url = "192.0.2.20:3001";
            Assert.Catch<MoviePilotException>(() => service.TransferSubmitAsync(preview, false, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(1, transport.Count("transfer/manual"), "只有预览，没有第二次写请求");
        });
    }

    private const string Session = """{"access_token":"jwt","user_name":"tester","super_user":true}""";
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static JsonElement File(string path) => Json(JsonSerializer.Serialize(new { storage = "local", type = "file", path }));
    private static MoviePilotResource Resource(string title) => new() { Title = title, TorrentInfo = Json("{}") };
    private static MoviePilotTransferHistory History(long id, string source, string target) => new()
    {
        Id = id,
        Title = "某剧",
        Mode = "softlink",
        Success = true,
        SourcePath = source,
        TargetPath = target,
        SourceFile = File(source),
        TargetFile = File(target),
        MediaId = "1",
        MediaSource = "themoviedb",
        Seasons = "S00",
        Episodes = "E07"
    };
    private static MoviePilotTransferRequest Request(MoviePilotTransferHistory history) => new()
    {
        Files = [new("special", history.TargetPath)],
        Histories = [history],
        Type = "电视剧",
        MediaSource = "themoviedb",
        MediaId = "1",
        Season = "4",
        Episode = "24",
        TargetStorage = "local",
        TargetPath = "/library",
        TransferType = "softlink",
        TypeFolder = true,
        Reorganize = true
    };
    private static AppSettings Settings()
    {
        var settings = new AppSettings();
        settings.MoviePilot.Enabled = true;
        settings.MoviePilot.Url = "192.0.2.10:3001";
        settings.MoviePilot.Username = "tester";
        new MoviePilotCredentials(PassthroughSecretProtector.Instance).SetPassword(settings.MoviePilot, "password");
        return settings;
    }
    private static MoviePilotService Service(StubTransport transport, AppSettings? settings = null) =>
        new(new MoviePilotClient(transport), new MoviePilotCredentials(PassthroughSecretProtector.Instance), settings ?? Settings());
}
