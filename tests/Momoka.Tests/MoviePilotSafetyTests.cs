using System.Net;
using System.Text.Json;
using Momoka.Configuration;
using Momoka.Emby;
using Momoka.MoviePilot;
using static Momoka.Tests.TestHarness;

namespace Momoka.Tests;

internal static class MoviePilotSafetyTests
{
    public static void Register()
    {
        HistoryTests();
        PreviewTests();
        ReceiptTests();
        CollectionTests();
        OperationTests();
    }

    private static void OperationTests()
    {
        Test("MoviePilot 操作归属：旧搜索结果不能在新连接订阅", () =>
        {
            using var transport = OperationTransport();
            var settings = Settings();
            var service = Service(transport, settings);
            var media = service.SearchAsync("Target", CancellationToken.None).GetAwaiter().GetResult().Single();
            settings.MoviePilot.Url = "192.0.2.30:3001";
            Assert.Throws<MoviePilotException>(() => service.SubscribeAsync(media, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(0, transport.Count("subscribe/"));
        });

        Test("MoviePilot 操作归属：旧种子不能携带私有信息提交给另一账号", () =>
        {
            using var transport = OperationTransport();
            var settings = Settings();
            var service = Service(transport, settings);
            var resource = service.SearchByKeywordAsync("Target", CancellationToken.None).GetAwaiter().GetResult().Single();
            settings.MoviePilot.Username = "other-user";
            Assert.Throws<MoviePilotException>(() => service.DownloadAsync(resource, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(0, transport.Count("download/add"));
        });

        Test("MoviePilot 操作防重：重新搜索不能绕过已提交的资源身份", () =>
        {
            using var transport = OperationTransport();
            var service = Service(transport);
            var first = service.SearchByKeywordAsync("Target", CancellationToken.None).GetAwaiter().GetResult().Single();
            service.DownloadAsync(first, CancellationToken.None).GetAwaiter().GetResult();
            var second = service.SearchByKeywordAsync("Target", CancellationToken.None).GetAwaiter().GetResult().Single();
            Assert.Throws<MoviePilotException>(() => service.DownloadAsync(second, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(1, transport.Count("download/add"));
        });

        Test("MoviePilot 操作防重：响应丢失后重新搜索仍不能重发下载", () =>
        {
            using var transport = OperationTransport().Throw("download/add", new HttpRequestException("fixture lost response"));
            var service = Service(transport);
            var first = service.SearchByKeywordAsync("Target", CancellationToken.None).GetAwaiter().GetResult().Single();
            Assert.Throws<MoviePilotException>(() => service.DownloadAsync(first, CancellationToken.None).GetAwaiter().GetResult());
            var second = service.SearchByKeywordAsync("Target", CancellationToken.None).GetAwaiter().GetResult().Single();
            Assert.Throws<MoviePilotException>(() => service.DownloadAsync(second, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(1, transport.Count("download/add"));
        });

        Test("MoviePilot 操作防重：订阅请求在途时重搜同身份只发一次", () =>
        {
            using var transport = OperationTransport();
            var service = Service(transport);
            var media = service.SearchAsync("Target", CancellationToken.None).GetAwaiter().GetResult().Single();
            var entered = false;
            transport.When("subscribe/", () =>
            {
                if (entered) return;
                entered = true;
                var again = service.SearchAsync("Target", CancellationToken.None).GetAwaiter().GetResult().Single();
                Assert.Throws<MoviePilotException>(() => service.SubscribeAsync(again, CancellationToken.None).GetAwaiter().GetResult());
            });
            service.SubscribeAsync(media, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(1, transport.Count("subscribe/"));
        });

        Test("MoviePilot 操作防重：整理换新预览也不能重放未决批次", () =>
        {
            using var transport = TransferTransport();
            var service = Service(transport);
            var first = Preview(service);
            transport.Answer("transfer/manual", "{\"success\":true,\"data\":null}");
            service.TransferSubmitAsync(first, true, CancellationToken.None).GetAwaiter().GetResult();
            transport.Answer("transfer/manual", "{\"success\":true,\"data\":" + PreviewData + "}");
            var second = Preview(service);
            Assert.Throws<MoviePilotException>(() => service.TransferSubmitAsync(second, true, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(1, transport.SentTo("transfer/manual").Count(sent => sent.Body.Contains("\"preview\":false", StringComparison.Ordinal)));
        });
    }

    private static StubTransport OperationTransport() => new StubTransport()
        .Answer("login/access-token", Session)
        .Answer("media/search", "[{\"title\":\"Target\",\"type\":\"电视剧\",\"media_source\":\"themoviedb\",\"media_id\":\"42\"}]")
        .Answer("search/title", "[{\"torrent_info\":{\"title\":\"Target\",\"site\":1,\"page_url\":\"https://example.invalid/details/42\",\"enclosure\":\"https://example.invalid/download/42\"}}]")
        .Answer("subscribe/", "{\"success\":true,\"data\":{\"id\":42}}")
        .Answer("download/add", "{\"success\":true,\"data\":{\"download_id\":\"fixture\"}}");

    private static void HistoryTests()
    {
        Test("MoviePilot 历史完整性：缺失或非法总数不能当作已经收齐", () =>
        {
            foreach (var total in new[] { "", ",\"total\":-1", ",\"total\":\"unknown\"", ",\"total\":0" })
            {
                using var transport = new StubTransport().Answer("login/access-token", Session)
                    .Answer("history/transfer", "{\"success\":true,\"data\":{\"list\":[" + HistoryJson() + "]" + total + "}}");
                var service = Service(transport);
                Assert.Throws<MoviePilotException>(() => service.FindTransferHistoriesAsync("Original", CancellationToken.None).GetAwaiter().GetResult());
            }
        });

        Test("MoviePilot 历史完整性：不可识别的记录不能静默丢弃", () =>
        {
            using var transport = new StubTransport().Answer("login/access-token", Session)
                .Answer("history/transfer", "{\"success\":true,\"data\":{\"total\":1,\"list\":[" + HistoryJson() + ",{\"id\":\"bad\"}]}}");
            Assert.Throws<MoviePilotException>(() => Service(transport).FindTransferHistoriesAsync("Original", CancellationToken.None).GetAwaiter().GetResult());
        });

        Test("MoviePilot 历史完整性：分页途中总数改变必须重新查询", () =>
        {
            using var transport = new StubTransport().Answer("login/access-token", Session)
                .Sequence("history/transfer", (HttpStatusCode.OK, Page(3, HistoryJson(1))),
                    (HttpStatusCode.OK, Page(2, HistoryJson(2))));
            Assert.Throws<MoviePilotException>(() => Service(transport).FindTransferHistoriesAsync("Original", CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(2, transport.Count("history/transfer"));
        });

        Test("MoviePilot 历史重核：同路径的 FileItem 变成目录不能继续提交", () =>
        {
            using var transport = TransferTransport()
                .Sequence("history/transfer", (HttpStatusCode.OK, Page(1, HistoryJson())),
                    (HttpStatusCode.OK, Page(1, HistoryJson(fileType: "dir"))));
            var service = Service(transport);
            var history = service.FindTransferHistoriesAsync("Original", CancellationToken.None).GetAwaiter().GetResult().Single();
            var preview = service.TransferPreviewAsync(Request(history), [history.SourceFile], CancellationToken.None).GetAwaiter().GetResult();
            Assert.Throws<MoviePilotTransferBlockedException>(() => service.TransferSubmitAsync(preview, false, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(0, transport.Count("transfer/manual?background=true"));
            Assert.Equal(1, transport.SentTo("transfer/manual").Count, "只允许预览，不得将已变成目录的 logid 发出");
        });

        Test("MoviePilot 历史重核：复用的识别身份改变必须重新预览", () =>
        {
            using var transport = TransferTransport()
                .Sequence("history/transfer", (HttpStatusCode.OK, Page(1, HistoryJson())),
                    (HttpStatusCode.OK, Page(1, HistoryJson(mediaId: "999"))));
            var service = Service(transport);
            var history = service.FindTransferHistoriesAsync("Original", CancellationToken.None).GetAwaiter().GetResult().Single();
            var preview = service.TransferPreviewAsync(Request(history) with { FromHistory = true }, [history.SourceFile], CancellationToken.None).GetAwaiter().GetResult();
            Assert.Throws<MoviePilotTransferBlockedException>(() => service.TransferSubmitAsync(preview, false, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(1, transport.SentTo("transfer/manual").Count);
        });

        Test("MoviePilot 软链接依赖：不能只按旧片名寻找外部引用", () =>
        {
            var original = HistoryJson();
            var dependent = HistoryJson(2, title: "Unrelated", source: "/library/original.mkv", target: "/elsewhere/renamed.mkv");
            using var transport = TransferTransport()
                .Answer("title=" + Uri.EscapeDataString("/library/original.mkv"), Page(2, original, dependent))
                .Answer("history/transfer", Page(1, original));
            var service = Service(transport);
            var history = service.FindTransferHistoriesAsync("Original", CancellationToken.None).GetAwaiter().GetResult().Single();
            var preview = service.TransferPreviewAsync(Request(history), [history.SourceFile], CancellationToken.None).GetAwaiter().GetResult();
            Assert.Throws<MoviePilotTransferBlockedException>(() => service.TransferSubmitAsync(preview, false, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(1, transport.SentTo("transfer/manual").Count);
            Assert.Equal(1, transport.Count("title=" + Uri.EscapeDataString("/library/original.mkv")));
        });

        Test("MoviePilot 软链接依赖：同一批次选中的链也不能靠顺序猜安全", () =>
        {
            var original = MoviePilotTransferHistory.Parse(Json(HistoryJson()))!;
            var dependent = MoviePilotTransferHistory.Parse(Json(HistoryJson(2, source: original.TargetPath, target: "/other/final.mkv")))!;
            Assert.Equal(2L, MoviePilotTransferHistoryMatch.Dependents([original, dependent], [original, dependent]).Single().Id);
        });
    }

    private static void PreviewTests()
    {
        Test("MoviePilot 预览快照：调用方改动原文件集合不能改变已预览范围", () =>
        {
            using var transport = TransferTransport();
            var files = new List<MoviePilotTransferFile> { new("source", "/downloads/source.mkv") };
            var resolved = new List<JsonElement> { File("/downloads/source.mkv") };
            var preview = Service(transport).TransferPreviewAsync(new MoviePilotTransferRequest { Files = files }, resolved, CancellationToken.None).GetAwaiter().GetResult();
            files[0] = new("other", "/downloads/other.mkv");
            resolved[0] = File("/downloads/other.mkv");
            Assert.Equal("/downloads/source.mkv", preview.Request.Files[0].Path);
            Assert.Equal("/downloads/source.mkv", preview.Files[0].GetProperty("path").GetString());
        });

        Test("MoviePilot 预览快照：修改展示回执不能借用旧预览提交", () =>
        {
            using var transport = TransferTransport();
            var service = Service(transport);
            var preview = Preview(service);
            preview.Result.Items[0].Target = "/different/target.mkv";
            Assert.Throws<MoviePilotTransferBlockedException>(() => service.TransferSubmitAsync(preview, false, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(1, transport.SentTo("transfer/manual").Count);
        });

        Test("MoviePilot 预览防重：明确收到回执也不能重复使用同一预览", () =>
        {
            using var transport = TransferTransport();
            var service = Service(transport);
            var preview = Preview(service);
            service.TransferSubmitAsync(preview, false, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Throws<MoviePilotTransferBlockedException>(() => service.TransferSubmitAsync(preview, false, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(2, transport.SentTo("transfer/manual").Count, "一次预览和一次实际提交");
        });

        Test("MoviePilot 预览防重：传输结果不明后不能重发同一预览", () =>
        {
            using var transport = TransferTransport();
            var service = Service(transport);
            var preview = Preview(service);
            transport.Throw("transfer/manual", new HttpRequestException("fixture interrupted response"));
            Assert.Throws<MoviePilotException>(() => service.TransferSubmitAsync(preview, false, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Throws<MoviePilotTransferBlockedException>(() => service.TransferSubmitAsync(preview, false, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(2, transport.SentTo("transfer/manual").Count, "不明回执只能去服务端核对，不能再发送");
        });

        Test("MoviePilot 预览防重：提交中的重入不能再发第二个请求", () =>
        {
            using var transport = TransferTransport();
            var service = Service(transport);
            var preview = Preview(service);
            var entered = false;
            transport.When("transfer/manual", () =>
            {
                if (entered) return;
                entered = true;
                Assert.Throws<MoviePilotTransferBlockedException>(() => service.TransferSubmitAsync(preview, false, CancellationToken.None).GetAwaiter().GetResult());
            });
            service.TransferSubmitAsync(preview, false, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(2, transport.SentTo("transfer/manual").Count);
        });

        Test("MoviePilot 预览绑定：关闭功能后旧预览不得提交", () =>
        {
            using var transport = TransferTransport();
            var settings = Settings();
            var service = Service(transport, settings);
            var preview = Preview(service);
            settings.MoviePilot.Enabled = false;
            Assert.Throws<OperationCanceledException>(() => service.TransferSubmitAsync(preview, false, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(1, transport.SentTo("transfer/manual").Count);
        });

        Test("MoviePilot 整理确认：logid 即使未开重新整理也提示旧目标清理", () =>
        {
            var history = MoviePilotTransferHistory.Parse(Json(HistoryJson()))!;
            var result = MoviePilotTransfer.ParseResult(Json(PreviewData), true, "", true);
            var text = MoviePilotTransfer.Confirmation(Request(history), result, false);
            Assert.Contains("旧目标", text);
            Assert.Contains("清理", text);
            Assert.Contains("/library/new.mkv", text, "确认中应列出预览的实际目标");
        });
    }

    private static void ReceiptTests()
    {
        Test("MoviePilot 提交回执：空成功信封只报告已接收待核对", () =>
        {
            using var transport = TransferTransport();
            var service = Service(transport);
            var preview = Preview(service);
            transport.Answer("transfer/manual", "{\"success\":true,\"data\":null}");
            var result = service.TransferSubmitAsync(preview, false, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(1, result.Items.Count);
            Assert.Equal("accepted", result.Items[0].State);
            Assert.Equal("/downloads/source.mkv", result.Items[0].Source);
            Assert.Contains("待确认", result.Items[0].Message);
            Assert.Contains("已完成 0", result.Summary);
        });

        Test("MoviePilot 提交回执：未返回的文件保留为待核对行", () =>
        {
            using var transport = TransferTransport().Answer("transfer/manual", "{\"success\":true,\"data\":{\"items\":[" +
                "{\"source\":\"/downloads/source.mkv\",\"target\":\"/library/new.mkv\",\"success\":true}," +
                "{\"source\":\"/downloads/second.mkv\",\"target\":\"/library/second.mkv\",\"success\":true}]}}");
            var service = Service(transport);
            var preview = service.TransferPreviewAsync(new MoviePilotTransferRequest
            {
                Files = [new("source", "/downloads/source.mkv"), new("second", "/downloads/second.mkv")]
            }, [File("/downloads/source.mkv"), File("/downloads/second.mkv")], CancellationToken.None).GetAwaiter().GetResult();
            transport.Answer("transfer/manual", "{\"success\":false,\"data\":{\"items\":[{\"source\":\"/downloads/source.mkv\",\"state\":\"completed\",\"success\":true}]}}");
            var result = service.TransferSubmitAsync(preview, false, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(2, result.Items.Count);
            Assert.Equal("completed", result.Items[0].State);
            Assert.Equal("manual_review", result.Items[1].State);
            Assert.Equal("/downloads/second.mkv", result.Items[1].Source);
        });

        Test("MoviePilot 写入回执：缺少布尔 success 不能冒充操作成功", () =>
        {
            foreach (var body in new[] { "{}", "null", "[]", "{\"success\":\"false\"}", "{\"success\":null}" })
            {
                using var transport = new StubTransport().Answer("download/add", body);
                using var client = new MoviePilotClient(transport);
                Assert.Throws<MoviePilotException>(() => client.PostAsync(new Uri("http://192.0.2.10:3001/"), "fixture-token", "download/add", new { }, CancellationToken.None).GetAwaiter().GetResult());
                Assert.Equal(1, transport.Count("download/add"));
            }
        });
    }

    private static void CollectionTests()
    {
        Test("MoviePilot 文件收集：分页未收齐不能返回一部分季文件", () =>
        {
            using var transport = new StubTransport()
                .Answer("Items/season", "{\"Id\":\"season\",\"Type\":\"Season\",\"Name\":\"Season\",\"IndexNumber\":1}")
                .Answer("Items", "{\"Items\":[{\"Id\":\"e1\",\"Type\":\"Episode\",\"Path\":\"/library/e1.mkv\"}],\"TotalRecordCount\":2}");
            Assert.Throws<MoviePilotException>(() => Collect(transport, "season"));
        });

        Test("MoviePilot 文件收集：分两页时保留全部文件及起点", () =>
        {
            using var transport = new StubTransport()
                .Answer("Items/season", "{\"Id\":\"season\",\"Type\":\"Season\",\"Name\":\"Season\",\"IndexNumber\":1}")
                .Sequence("Items", (HttpStatusCode.OK, "{\"Items\":[{\"Id\":\"e1\",\"Type\":\"Episode\",\"Path\":\"/library/e1.mkv\"}],\"TotalRecordCount\":2}"),
                    (HttpStatusCode.OK, "{\"Items\":[{\"Id\":\"e2\",\"Type\":\"Episode\",\"Path\":\"/library/e2.mkv\"}],\"TotalRecordCount\":2}"));
            var context = Collect(transport, "season");
            Assert.Equal(2, context.Files.Count);
            Assert.Contains("StartIndex=1", transport.SentTo("/Items?")[1].Url);
        });

        Test("MoviePilot 文件收集：其中一集没有文件不能默默缩小范围", () =>
        {
            using var transport = new StubTransport()
                .Answer("Items/season", "{\"Id\":\"season\",\"Type\":\"Season\",\"Name\":\"Season\",\"IndexNumber\":1}")
                .Answer("Items", "{\"Items\":[{\"Id\":\"e1\",\"Type\":\"Episode\",\"Path\":\"/library/e1.mkv\"},{\"Id\":\"e2\",\"Type\":\"Episode\",\"Name\":\"Missing\"}],\"TotalRecordCount\":2}");
            Assert.Throws<MoviePilotException>(() => Collect(transport, "season"));
        });
    }

    private const string Session = "{\"access_token\":\"fixture-token\",\"super_user\":true,\"user_name\":\"fixture-user\"}";
    private const string PreviewData = "{\"items\":[{\"source\":\"/downloads/source.mkv\",\"target\":\"/library/new.mkv\",\"success\":true}]}";

    private static StubTransport TransferTransport() => new StubTransport()
        .Answer("login/access-token", Session)
        .Answer("transfer/manual", "{\"success\":true,\"data\":" + PreviewData + "}");

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static JsonElement File(string path) => JsonSerializer.SerializeToElement(new { storage = "local", type = "file", path });
    private static string Page(int total, params string[] histories) => "{\"success\":true,\"data\":{\"total\":" + total + ",\"list\":[" + string.Join(',', histories) + "]}}";
    private static string HistoryJson(long id = 1, string title = "Original", string source = "/downloads/source.mkv", string target = "/library/original.mkv", string fileType = "file", string mediaId = "1") =>
        JsonSerializer.Serialize(new
        {
            id,
            title,
            src = source,
            dest = target,
            status = true,
            mode = "softlink",
            type = "电视剧",
            media_source = "themoviedb",
            media_id = mediaId,
            seasons = "S01",
            episodes = "E01",
            src_fileitem = new { storage = "local", type = fileType, path = source },
            dest_fileitem = new { storage = "local", type = "file", path = target }
        });

    private static MoviePilotTransferRequest Request(MoviePilotTransferHistory history) => new()
    {
        Files = [new("source", history.TargetPath)],
        Histories = [history],
        TargetStorage = "local",
        TargetPath = "/library",
        TransferType = "softlink"
    };

    private static MoviePilotTransferPreview Preview(MoviePilotService service) => service.TransferPreviewAsync(
        new MoviePilotTransferRequest { Files = [new("source", "/downloads/source.mkv")] },
        [File("/downloads/source.mkv")], CancellationToken.None).GetAwaiter().GetResult();

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

    private static MoviePilotTransferContext Collect(StubTransport transport, string id)
    {
        var client = new EmbyClient(new EmbyHttp(transport), new EmbyConnection(new Uri("http://192.0.2.20:8096/"), "fixture-token", "fixture-user", "Fixture", "Fixture", DeviceIdentity.Create("fixture-device", "1.0")));
        return MoviePilotTransferCollect.CollectAsync(client, new EmbyItem { Id = id, Type = EmbyItemType.Season }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
