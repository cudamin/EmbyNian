using System.Net;
using System.Text.Json;
using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.MoviePilot;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

internal static class MoviePilotSubscriptionTests
{
    private const string Session = """{"access_token":"fixture-token","super_user":true,"user_name":"fixture"}""";
    private const string Series = """{"id":12,"name":"示例剧","year":"2026","type":"电视剧","media_source":"douban","media_id":"42","season":2,"total_episode":4,"start_episode":1,"lack_episode":1,"state":"R","include":"old","sites":[8],"unknown":"keep"}""";
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static MoviePilotSubscription Subscription => MoviePilotSubscription.Parse(Json(Series))!;
    private static AppSettings Settings()
    {
        var settings = new AppSettings();
        settings.MoviePilot.Enabled = true; settings.MoviePilot.Url = "https://fixture.invalid:3001";
        settings.MoviePilot.Username = "fixture";
        new MoviePilotCredentials(PassthroughSecretProtector.Instance).SetPassword(settings.MoviePilot, "fixture-password");
        return settings;
    }
    private static MoviePilotService Service(StubTransport transport, AppSettings? settings = null) =>
        new(new MoviePilotClient(transport), new(PassthroughSecretProtector.Instance), settings ?? Settings());
    private static StubTransport Transport() => new StubTransport().Answer("login/access-token", Session).Answer("subscribe/", "[" + Series + "]");
    private static MoviePilotSubscription Read(MoviePilotService service) => service.SubscriptionsAsync(CancellationToken.None).GetAwaiter().GetResult().Single();

    internal static void Register()
    {
        PlaybackTests();
        Test("MoviePilot 订阅列表：分区保留来源、季号与数字字符串，忽略音乐且不以媒体编号合并", () =>
        {
            var items = MoviePilotSubscription.ParseList(Json("[" + Series + "," + Series.Replace("\"id\":12", "\"id\":13").Replace("电视剧", "电影") + "," + Series.Replace("电视剧", "音乐") + "]"));
            Assert.Equal(2, items.Count); Assert.True(items[0].IsSeries); Assert.False(items[1].IsSeries);
            Assert.Equal("douban", items[0].Media.MediaSource); Assert.Equal("42", items[0].Media.MediaId);
            Assert.Equal(2, items[0].Media.Season); Assert.Equal(2026, items[0].Media.Year);
            Assert.Contains("订阅进度", items[0].ProgressLabel); Assert.DoesNotContain("已入库", items[0].ProgressLabel);
            Assert.Equal("", items[1].ProgressLabel);
            Assert.Throws<MoviePilotException>(() => MoviePilotSubscription.ParseList(Json("{}")));
        });
        Test("MoviePilot 文件统计：下载不冒充入库，服务器条目和实际文件都是入库证据", () =>
        {
            var data = Json("{\"subscribe\":" + Series + """
                , "episodes":{
                "1":{"title":"首集","backdrop":"https://art.invalid/episode-1.jpg","library":[{"file_path":"/library/01.mkv"}],"download":[]},
                "2":{"library":[],"download":[{"file_path":"/downloads/02.mkv"}]},
                "3":{"library":[{"server":"测试库","itemid":"item-3"}]}}}
                """);
            var files = MoviePilotSubscriptionFiles.Parse(data, Subscription);
            Assert.Equal(4, files.Episodes.Count); Assert.Equal(2, files.LibraryCount); Assert.Equal(2, files.MissingCount);
            Assert.Equal("已下载 · 待入库", files.Episodes[1].Status); Assert.Equal("缺集", files.Episodes[3].Status);
            Assert.Equal("https://art.invalid/episode-1.jpg", files.Episodes[0].ArtworkUrl);
            Assert.Contains("item-3", files.Episodes[2].Files);
        });
        Test("MoviePilot 文件统计：按订阅目标过滤季范围，拒绝错订阅与无统计", () =>
        {
            var sub = MoviePilotSubscription.Parse(Json(Series.Replace("\"start_episode\":1", "\"start_episode\":3")))!;
            var files = MoviePilotSubscriptionFiles.Parse(Json("{\"subscribe\":" + sub.Data.GetRawText() + ",\"episodes\":{\"1\":{},\"3\":{},\"6\":{}}}"), sub);
            Assert.Equal(2, files.Episodes.Count); Assert.Equal(3, files.Episodes[0].Number);
            Assert.Throws<MoviePilotException>(() => MoviePilotSubscriptionFiles.Parse(Json("{\"subscribe\":" + Series.Replace("\"id\":12", "\"id\":19") + ",\"episodes\":{}}"), sub));
            Assert.Throws<MoviePilotException>(() => MoviePilotSubscriptionFiles.Parse(Json("{}"), sub));
        });
        Test("MoviePilot 电影统计：电影零号条目与空数据不能混成已入库", () =>
        {
            var movie = MoviePilotSubscription.Parse(Json(Series.Replace("电视剧", "电影")))!;
            var files = MoviePilotSubscriptionFiles.Parse(Json("{\"subscribe\":" + movie.Data.GetRawText() + ",\"episodes\":{\"0\":{\"library\":[],\"download\":[]}}}"), movie);
            Assert.Equal(1, files.MissingCount); Assert.Equal(0, files.LibraryCount);
            Assert.Contains("无法判断", new MoviePilotSubscriptionFiles([]).Summary);
        });
        Test("MoviePilot 编辑：只发送改动字段和 id，清空为 null，不覆盖站点及运行事实", () =>
        {
            var edit = MoviePilotSubscriptionEdit.From(Subscription);
            Assert.Equal(1, edit.Changes(Subscription).Count);
            var body = (edit with { Include = "", TotalEpisodes = 6 }).Changes(Subscription);
            Assert.Equal(3, body.Count); Assert.Null(body["include"]); Assert.Equal(6, (int)body["total_episode"]!);
            Assert.False(body.ContainsKey("sites")); Assert.False(body.ContainsKey("lack_episode"));
            Assert.Throws<MoviePilotException>(() => (edit with { StartEpisode = 10 }).Changes(Subscription));
        });
        Test("MoviePilot 订阅管理：列表全量读取且五种动作使用准确方法和订阅主键", () =>
        {
            foreach (var (action, method, suffix) in new[] {
                (MoviePilotSubscriptionAction.Pause,"PUT","subscribe/status/12?state=S"),
                (MoviePilotSubscriptionAction.Resume,"PUT","subscribe/status/12?state=R"),
                (MoviePilotSubscriptionAction.Search,"POST","subscribe/search/12"),
                (MoviePilotSubscriptionAction.Reset,"POST","subscribe/reset/12"),
                (MoviePilotSubscriptionAction.Delete,"DELETE","subscribe/12") })
            {
                using var transport = new StubTransport().Answer("login/access-token", Session)
                    .Answer(suffix, "{\"success\":true,\"data\":null}").Answer("subscribe/", "[" + Series + "]");
                var service = Service(transport); var sub = Read(service);
                Assert.Equal("https://fixture.invalid:3001/api/v1/subscribe/", transport.SentTo("subscribe/")[0].Url);
                service.ManageSubscriptionAsync(sub, action, CancellationToken.None).GetAwaiter().GetResult();
                var sent = transport.Only(suffix); Assert.Equal(method, sent.Method); Assert.Equal("", sent.Body);
            }
        });
        Test("MoviePilot 订阅写入：成功后可继续操作，编辑正文只包含改动", () =>
        {
            using var transport = new StubTransport().Answer("login/access-token", Session)
                .Answer("subscribe/status", "{\"success\":true}")
                .Sequence("subscribe/", (HttpStatusCode.OK, "[" + Series + "]"), (HttpStatusCode.OK, "{\"success\":true}"));
            var service = Service(transport); var sub = Read(service);
            service.ManageSubscriptionAsync(sub, MoviePilotSubscriptionAction.Pause, CancellationToken.None).GetAwaiter().GetResult();
            service.ManageSubscriptionAsync(sub, MoviePilotSubscriptionAction.Resume, CancellationToken.None).GetAwaiter().GetResult();
            service.UpdateSubscriptionAsync(sub, MoviePilotSubscriptionEdit.From(sub) with { Keyword = "new" }, CancellationToken.None).GetAwaiter().GetResult();
            var write = transport.SentTo("subscribe/").Last(); Assert.Equal("PUT", write.Method);
            var body = Json(write.Body); Assert.Equal(2, body.EnumerateObject().Count()); Assert.Equal("new", body.GetProperty("keyword").GetString());
        });
        Test("MoviePilot 订阅搜索：仅明确 405 才兼容旧文档 GET", () =>
        {
            using var transport = new StubTransport().Answer("login/access-token", Session)
                .Sequence("subscribe/search/12", (HttpStatusCode.MethodNotAllowed, ""), (HttpStatusCode.OK, "{\"success\":true}"))
                .Answer("subscribe/", "[" + Series + "]");
            var service = Service(transport); var sub = Read(service);
            service.ManageSubscriptionAsync(sub, MoviePilotSubscriptionAction.Search, CancellationToken.None).GetAwaiter().GetResult();
            var requests = transport.SentTo("subscribe/search/12"); Assert.Equal(2, requests.Count);
            Assert.Equal("POST", requests[0].Method); Assert.Equal("GET", requests[1].Method);
        });
        Test("MoviePilot 订阅超时：不重放不退回 GET，其他写操作也等待核对", () =>
        {
            using var transport = new StubTransport().Answer("login/access-token", Session)
                .Throw("subscribe/search/12", new HttpRequestException("fixture timeout")).Answer("subscribe/", "[" + Series + "]");
            var service = Service(transport); var sub = Read(service);
            Assert.Throws<MoviePilotOperationUncertainException>(() => service.ManageSubscriptionAsync(sub, MoviePilotSubscriptionAction.Search, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Throws<MoviePilotOperationBlockedException>(() => service.ManageSubscriptionAsync(sub, MoviePilotSubscriptionAction.Pause, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(1, transport.Count("subscribe/search/12"));
            Assert.True(service.SubscriptionNeedsReview(sub));
            var before = transport.Total;
            service.ConfirmSubscriptionReviewed(sub);
            Assert.False(service.SubscriptionNeedsReview(sub));
            Assert.Equal(before, transport.Total);
        });
        Test("MoviePilot 订阅身份：切换连接和取消后拒绝旧卡片读取与写入", () =>
        {
            using var transport = Transport(); var settings = Settings(); var service = Service(transport, settings); var sub = Read(service);
            settings.MoviePilot.Username = "other";
            Assert.False(service.IsCurrentSubscription(sub));
            Assert.Throws<MoviePilotException>(() => service.SubscriptionFilesAsync(sub, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Throws<MoviePilotException>(() => service.ManageSubscriptionAsync(sub, MoviePilotSubscriptionAction.Delete, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(2, transport.Total);
            settings.MoviePilot.Enabled = false;
            Assert.Throws<OperationCanceledException>(() => service.SubscriptionsAsync(CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(2, transport.Total);
        });
        Test("MoviePilot 订阅详情：路径和文件统计归属固定，读取不会写入", () =>
        {
            using var transport = new StubTransport().Answer("login/access-token", Session)
                .Answer("subscribe/files/12", "{\"subscribe\":" + Series + ",\"episodes\":{}}")
                .Answer("subscribe/12", Series).Answer("subscribe/", "[" + Series + "]");
            var service = Service(transport); var sub = Read(service);
            Assert.Equal(12, service.SubscriptionAsync(sub, CancellationToken.None).GetAwaiter().GetResult().Id);
            Assert.Equal(4, service.SubscriptionFilesAsync(sub, CancellationToken.None).GetAwaiter().GetResult().MissingCount);
            Assert.Equal("GET", transport.Only("subscribe/12").Method); Assert.Equal("GET", transport.Only("subscribe/files/12").Method);
        });
    }

    private static void PlaybackTests()
    {
        Test("MoviePilot 入库播放：来源、媒体类型与季集共同匹配，不能按同名或相同数字混播", () =>
        {
            var sub = Subscription;
            var series = new EmbyItem { Id = "show", Name = sub.Media.Title, Type = EmbyItemType.Series, ProviderIds = new() { ["Douban"] = "42" } };
            Assert.True(MoviePilotLibraryPlayback.MatchesMedia(sub, series));
            series.ProviderIds = new() { ["Tmdb"] = "42" };
            Assert.False(MoviePilotLibraryPlayback.MatchesMedia(sub, series));
            series.Type = EmbyItemType.Movie; series.ProviderIds = new() { ["Douban"] = "42" };
            Assert.False(MoviePilotLibraryPlayback.MatchesMedia(sub, series));
            var episode = new MoviePilotSubscriptionEpisode(3, "", "", ["/library/e3.mkv"], []);
            var item = new EmbyItem { Id = "ep", Type = EmbyItemType.Episode, ParentIndexNumber = 2, IndexNumber = 2, IndexNumberEnd = 3, Path = "/library/e3.mkv" };
            Assert.True(MoviePilotLibraryPlayback.MatchesEpisode(sub, episode, item));
            item.ParentIndexNumber = 1;
            Assert.False(MoviePilotLibraryPlayback.MatchesEpisode(sub, episode, item));
            item.ParentIndexNumber = 2;
            Assert.False(MoviePilotLibraryPlayback.MatchesEpisode(sub, episode with { LibraryFiles = [] }, item));
            item.Path = null;
            Assert.False(MoviePilotLibraryPlayback.MatchesEpisode(sub, episode, item));
        });
        Test("MoviePilot 入库播放：路径比对保留 Unix 大小写，Windows 可规范斜线，拒绝缺失路径", () =>
        {
            var episode = new MoviePilotSubscriptionEpisode(1, "", "", ["/library/E01.mkv"], [])
            { LibraryEntries = [new("/library/E01.mkv", "emby", "12")] };
            var item = new EmbyItem { Path = "/library/e01.mkv" };
            Assert.False(MoviePilotLibraryPlayback.MatchesPath(episode, item));
            item.Path = "/library/E01.mkv";
            Assert.True(MoviePilotLibraryPlayback.MatchesPath(episode, item));
            episode = episode with { LibraryEntries = [new("D:\\library\\E01.mkv", "emby", "12")] };
            item.Path = "d:/library/e01.mkv";
            Assert.True(MoviePilotLibraryPlayback.MatchesPath(episode, item));
            Assert.False(MoviePilotLibraryPlayback.MatchesPath(episode with { LibraryEntries = [] }, item));
        });
        Test("MoviePilot 入库播放：当前 Emby 精确查找剧集，不发播放请求，缺集无目标", () =>
        {
            using var transport = new StubTransport()
                .Answer("Shows/show/Episodes", """{"Items":[{"Id":"ep1","Type":"Episode","ParentIndexNumber":2,"IndexNumber":1,"SeriesId":"show","Path":"/library/01.mkv"},{"Id":"other-season","Type":"Episode","ParentIndexNumber":1,"IndexNumber":1,"Path":"/other/01.mkv"}]}""")
                .Answer("Users/user/Items", """{"Items":[{"Id":"show","Type":"Series","ProviderIds":{"Douban":"42"}}]}""");
            using var http = new EmbyHttp(transport);
            var client = new EmbyClient(http, new EmbyConnection(new Uri("https://emby.invalid/emby/"), "fixture-token", "user", "测试", "测试", DeviceIdentity.Create("fixture", "test")));
            var files = new MoviePilotSubscriptionFiles([new(1, "", "", ["/library/01.mkv"], []), new(2, "", "", [], [])]);
            var targets = MoviePilotLibraryPlayback.FindAsync(client, Subscription, files, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(1, targets.Count); Assert.Equal("ep1", targets[1].Item.Id); Assert.Equal("show", targets[1].Parent!.Id);
            Assert.Contains("AnyProviderIdEquals=Douban.42", transport.Only("Users/user/Items").Url);
            Assert.Contains("IncludeItemTypes=Series", transport.Only("Users/user/Items").Url);
            Assert.Equal(2, transport.Total);
        });
        Test("MoviePilot 入库播放：电影可以匹配，外服同 itemid 不同路径不能成为播放目标", () =>
        {
            var movie = MoviePilotSubscription.Parse(Json(Series.Replace("电视剧", "电影")))!;
            using var transport = new StubTransport()
                .Answer("Users/user/Items/12", """{"Id":"12","Type":"Movie","Path":"/other/movie.mkv"}""")
                .Answer("Users/user/Items", "{\"Items\":[]}");
            using var http = new EmbyHttp(transport);
            var client = new EmbyClient(http, new EmbyConnection(new Uri("https://emby.invalid/emby/"), "fixture-token", "user", "测试", "测试", DeviceIdentity.Create("fixture", "test")));
            var episode = new MoviePilotSubscriptionEpisode(0, "", "", ["/library/movie.mkv"], [])
            { LibraryEntries = [new("/library/movie.mkv", "emby", "12")] };
            Assert.True(MoviePilotLibraryPlayback.MatchesEpisode(movie, episode, new() { Type = EmbyItemType.Movie, Path = "/movie.mkv" }));
            var targets = MoviePilotLibraryPlayback.FindAsync(client, movie, new([episode]), CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(0, targets.Count); Assert.Equal(2, transport.Total);
            transport.Answer("Users/user/Items", """{"Items":[{"Id":"movie","Type":"Movie","ProviderIds":{"Douban":"42"},"Path":"/library/movie.mkv"}]}""");
            targets = MoviePilotLibraryPlayback.FindAsync(client, movie, new([episode]), CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal("movie", targets[0].Item.Id);
        });
    }
}
