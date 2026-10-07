using System.Text.Json;
using EmbyNian.Configuration;
using EmbyNian.MoviePilot;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

internal static class MoviePilotQuerySafetyTests
{
    public static void Register()
    {
        Test("MoviePilot 精确查询：指定季号零和二都保留，未指定不猜第一季", () =>
        {
            foreach (var season in new int?[] { null, 0, 2 })
            {
                using var transport = new StubTransport().Answer("login/access-token", Session)
                    .Answer("media/search", JsonSerializer.Serialize(new[] { new { title = "Target", type = "电视剧", media_source = "themoviedb", media_id = "42", season } }))
                    .Answer("search/media", "[]");
                var service = Service(transport);
                var media = service.SearchAsync("Target", CancellationToken.None).GetAwaiter().GetResult().Single();
                Assert.Equal(season, media.Season);
                service.SearchResourcesAsync(media, CancellationToken.None).GetAwaiter().GetResult();
                var url = transport.Only("search/media").Url;
                if (season is { } value) Assert.Contains("&season=" + value, url);
                else Assert.DoesNotContain("&season=", url);
                Assert.Contains("mtype=" + Uri.EscapeDataString("电视剧"), url);
            }
        });

        Test("MoviePilot 精确资源：不透明种子标题仍保留目标剧类型和媒体快照", () =>
        {
            const string context = """
                [{"media_info":{"title":"目标剧","type":"电视剧","media_source":"themoviedb","media_id":"42","imdb_id":"tt0000042"},
                  "media_info_is_target":true,"match_status":"exact",
                  "torrent_info":{"title":"Opaque.1080p","media_source":"imdb","media_id":"tt0000042","enclosure":"https://example.invalid/torrent"}}]
                """;
            var selected = Media("电视剧");
            var row = MoviePilotMediaParser.ParseResources(Json(context), selected.MediaSource, selected.MediaId, selected).Single();
            Assert.Null(row.DownloadProblem);
            Assert.Equal("电视剧", row.MediaType);
            var body = MoviePilotMediaParser.DownloadBody(row);
            Assert.Equal("电视剧", ((JsonElement)body["media_in"]!).GetProperty("type").GetString());
            Assert.Equal("42", ((JsonElement)body["media_in"]!).GetProperty("media_id").GetString());
            Assert.Equal("imdb", ((JsonElement)body["torrent_in"]!).GetProperty("media_source").GetString());
            Assert.False(body.ContainsKey("mtype"), "download/add 不支持靠 mtype 补回丢掉的命名空间");
        });

        Test("MoviePilot 精确资源：同数字不同类型、不同来源和缺快照不能下载", () =>
        {
            foreach (var info in new[]
            {
                """{"title":"Other","type":"电影","media_source":"themoviedb","media_id":"42"}""",
                """{"title":"Other","type":"电视剧","media_source":"douban","media_id":"42"}""",
                "null"
            })
            {
                var selected = Media("电视剧");
                var context = Json("[{\"media_info\":" + info + ",\"torrent_info\":{\"title\":\"Opaque\"}}]");
                var row = MoviePilotMediaParser.ParseResources(context, "themoviedb", "42", selected).Single();
                Assert.NotNull(row.DownloadProblem);
                Assert.Throws<MoviePilotOperationBlockedException>(() => MoviePilotMediaParser.DownloadBody(row));
            }
        });

        Test("MoviePilot 精确资源：候选结果不能冒充目标精确匹配", () =>
        {
            var data = Json("""
                [{"media_info":{"title":"Target","type":"电视剧","media_source":"themoviedb","media_id":"42"},
                  "media_info_is_target":false,"match_status":"candidate","torrent_info":{"title":"Candidate"}}]
                """);
            var row = MoviePilotMediaParser.ParseResources(data, "themoviedb", "42", Media("电视剧")).Single();
            Assert.NotNull(row.DownloadProblem);
            Assert.Throws<MoviePilotOperationBlockedException>(() => MoviePilotMediaParser.DownloadBody(row));
        });

        Test("MoviePilot 关键词资源：半对身份不能跨 media_info 和种子拼接", () =>
        {
            var data = Json("""
                [{"media_info":{"title":"Partial","type":"电视剧","media_source":"douban"},
                  "torrent_info":{"title":"Torrent","media_source":"themoviedb","media_id":"42"}}]
                """);
            var row = MoviePilotMediaParser.ParseResources(data, null, null).Single();
            Assert.Equal("themoviedb", row.MediaSource);
            Assert.Equal("42", row.MediaId);
            Assert.Equal(JsonValueKind.Undefined, row.MediaInfo.ValueKind);
        });

        Test("MoviePilot 优惠：未知和普通不进入优惠筛选，数值优先", () =>
        {
            foreach (var promotion in new[] { "未知", "普通", "NORMAL", "Unrecognized", "" })
            {
                var data = Json(JsonSerializer.Serialize(new[] { new { torrent_info = new { title = "T", volume_factor = promotion } } }));
                var row = MoviePilotMediaParser.ParseResources(data, null, null).Single();
                Assert.False(row.HasDiscount, promotion);
                Assert.False(row.IsFree, promotion);
                Assert.True((row with { DownloadFactor = 0 }).IsFree);
                Assert.True((row with { DownloadFactor = 0.5 }).HasDiscount);
                Assert.True((row with { UploadFactor = 2 }).HasDiscount);
            }
            var free = new MoviePilotResource { Title = "T", TorrentInfo = Json("{}"), Promotion = "免费" };
            Assert.True(free.IsFree && free.HasDiscount);
            Assert.False((free with { DownloadFactor = 1, UploadFactor = 1 }).HasDiscount);
        });

        Test("MoviePilot 来源能力：显式空列表不支持影视，插件标识不设白名单", () =>
        {
            using var transport = new StubTransport().Answer("login/access-token", Session)
                .Answer("media/source", """
                    [{"name":"仅详情","media_source":"detail.only","media_types":[]},
                     {"name":"影视插件","media_source":"acme.video","media_types":[" Movie ","TV"]}]
                    """);
            var sources = Service(transport).MediaSourcesAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(2, sources.Count);
            Assert.False(sources[0].IsVideo);
            Assert.True(sources[1].IsVideo);
        });

        Test("MoviePilot 功能关闭：保存过凭据也不登录读取来源目录", () =>
        {
            using var transport = new StubTransport().Answer("login/access-token", Session).Answer("media/source", "[]");
            var settings = Settings();
            settings.MoviePilot.Enabled = false;
            Assert.Throws<OperationCanceledException>(() => Service(transport, settings).MediaSourcesAsync(CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(0, transport.Total);
        });

        Test("MoviePilot 确认内容：同名跨类型来源和资源站点必须可区分", () =>
        {
            var movie = Media("电影");
            var series = Media("电视剧");
            Assert.False(movie.Confirmation == series.Confirmation);
            Assert.Contains("42", movie.Confirmation);
            Assert.False(movie.Confirmation == (movie with { MediaSource = "douban" }).Confirmation);
            var resource = new MoviePilotResource { Title = "Same", SiteName = "A", TorrentInfo = Json("{}") };
            Assert.False(resource.Confirmation == (resource with { SiteName = "B" }).Confirmation);
        });

        Test("MoviePilot 预览表单：复制集合仍匹配，改目标或历史不匹配", () =>
        {
            var file = Json("""{"storage":"local","type":"file","path":"/source.mkv"}""");
            var request = new MoviePilotTransferRequest { Files = [new("source", "/source.mkv")] };
            var preview = new MoviePilotTransferPreview(request, [file], new(true, "", [], true));
            Assert.True(preview.Matches(request));
            Assert.True(preview.Matches(request with { Files = request.Files.ToList() }));
            Assert.False(preview.Matches(request with { TargetPath = "/other" }));
        });
    }

    private const string Session = """{"access_token":"fixture-token","super_user":true,"user_name":"fixture-user"}""";
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static MoviePilotMedia Media(string type) => new() { Title = "Target", Type = type, MediaSource = "themoviedb", MediaId = "42" };
    private static AppSettings Settings()
    {
        var settings = new AppSettings();
        settings.MoviePilot.Enabled = true;
        settings.MoviePilot.Url = "192.0.2.10:3001";
        settings.MoviePilot.Username = "fixture-user";
        new MoviePilotCredentials(PassthroughSecretProtector.Instance).SetPassword(settings.MoviePilot, "fixture-password");
        return settings;
    }
    private static MoviePilotService Service(StubTransport transport, AppSettings? settings = null) =>
        new(new MoviePilotClient(transport), new MoviePilotCredentials(PassthroughSecretProtector.Instance), settings ?? Settings());
}
