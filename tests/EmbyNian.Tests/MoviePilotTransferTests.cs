using System.Net;
using System.Text.Json;
using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.MoviePilot;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// MoviePilot「手动整理」的那几条规则。协议按上游 v3.0.1 核对（2026-09-24，源码快照在
/// <c>work/moviepilot-protocol-20260924-M8nFhS</c>）。
/// <para>
/// 钉住的都是「写错了屏上看不出来」的事：路径必须绝对且不是磁盘根、storage/list 换不出的文件不许换成父目录、
/// 预览全绿才放提交、部分成功不许整批重试、<c>success:false</c> 的信封里还得留下逐文件回执。
/// </para>
/// </summary>
internal static class MoviePilotTransferTests
{
    public static void Register()
    {
        PathTests();
        RequestTests();
        ParseTests();
        ServiceTests();
        MenuTests();
    }

    // ── 路径 ─────────────────────────────────────────────────────────────────────────────────────────

    private static void PathTests()
    {
        Test("手动整理路径：本地绝对路径三种写法都认", () =>
        {
            Assert.True(MoviePilotTransfer.IsAbsolutePath(@"/media/downloads/电影.mkv"));
            Assert.True(MoviePilotTransfer.IsAbsolutePath(@"D:\Downloads\电影.mkv"));
            Assert.True(MoviePilotTransfer.IsAbsolutePath(@"\\NAS\media\电影.mkv"), "UNC 也是绝对路径");
        });

        Test("手动整理路径：相对路径、网址和磁盘根都挡下", () =>
        {
            foreach (var hostile in (string[])["Downloads/电影.mkv", "./电影.mkv", "../电影.mkv",
                "http://x/电影.mkv", "ftp://x/y", "/", "D:\\", "D:/", "//NAS/media", ""])
                Assert.False(MoviePilotTransfer.IsAbsolutePath(hostile), $"「{hostile}」不该被当成可整理的源路径");
        });

        Test("手动整理路径：父目录取对，盘根和 POSIX 根各归各", () =>
        {
            Assert.Equal("/media/downloads", MoviePilotTransfer.ParentPath("/media/downloads/电影.mkv"));
            Assert.Equal("D:/Downloads", MoviePilotTransfer.ParentPath(@"D:\Downloads\电影.mkv"));
            Assert.Equal("D:/", MoviePilotTransfer.ParentPath("D:/a.mkv"), "盘根只剩斜杠那一份");
            Assert.Equal("/", MoviePilotTransfer.ParentPath("/a.mkv"));
        });

        Test("手动整理路径：Windows 不分大小写，远端路径分", () =>
        {
            Assert.True(MoviePilotTransfer.SamePath(@"D:\Media\a.mkv", "D:/media/a.mkv"));
            Assert.False(MoviePilotTransfer.SamePath("/Media/a.mkv", "/media/a.mkv"), "POSIX 路径大小写是两份文件");
        });

        Test("手动整理路径：路径名取文件名", () =>
        {
            Assert.Equal("电影.mkv", MoviePilotTransfer.PathName(@"/media/x/电影.mkv"));
            Assert.Equal("电影.mkv", MoviePilotTransfer.PathName(@"D:\media\x\电影.mkv"));
        });
    }

    // ── 请求校验与正文 ───────────────────────────────────────────────────────────────────────────────

    private static void RequestTests()
    {
        Test("手动整理请求：没有文件、目标存储缺路径、坏编号各有各的话", () =>
        {
            Assert.Contains("没有可整理的文件", Problem(new MoviePilotTransferRequest()));
            Assert.Contains("目标存储", Problem(Request(targetStorage: "", targetPath: "/media/library")),
                "给了目标路径没给存储");
            Assert.Contains("正整数", Problem(Request(mediaId: "abc")), "TMDB 编号必须是数");
            Assert.Contains("最小文件大小", Problem(Request(minimumSize: "-3")));
            Assert.Contains("季号", Problem(Request(type: "电视剧", season: "x")));
            Assert.Contains("整理方式", Problem(Request(transferType: "teleport")));
        });

        Test("手动整理请求：合法的最简请求没有话", () =>
        {
            Assert.Null(Problem(Request()));
            Assert.Null(Problem(Request(type: "电视剧", season: "1", mediaId: "157336")));
            Assert.Null(Problem(Request(mediaId: "")), "编号留空是「自动识别」，合法");
        });

        Test("手动整理正文：单文件走 fileitem，缺的键一个都不放", () =>
        {
            var body = Request(targetStorage: "local", targetPath: "/media/library").Body([File("/x/a.mkv")], preview: true);

            Assert.True(body.ContainsKey("fileitem"), "单文件用 fileitem");
            Assert.False(body.ContainsKey("fileitems"));
            Assert.Equal("电影", body["type_name"]);
            Assert.True((bool)body["preview"]!);
            Assert.True((bool)body["skip_success"]!);
            Assert.Equal("local", body["target_storage"]);
            Assert.Equal("/media/library", body["target_path"]);
            Assert.Equal("copy", body["transfer_type"]);
            Assert.False(body.ContainsKey("season"), "电影不发季号");
            Assert.False(body.ContainsKey("episode_part"), "空 Part 不占位");
            Assert.False(body.ContainsKey("media_id"), "自动识别不发身份对");

            // 存储没填就不发那几个键 —— 半个目标比没有目标更容易让服务器猜。
            var bare = Request(targetStorage: "", targetPath: "", transferType: "").Body([File("/x/a.mkv")], preview: true);
            Assert.False(bare.ContainsKey("target_storage"));
            Assert.False(bare.ContainsKey("target_path"));
            Assert.False(bare.ContainsKey("transfer_type"));
        });

        Test("手动整理正文：一批文件走 fileitems，季号是整数，重新整理会关掉跳过历史", () =>
        {
            var request = Request(type: "电视剧", season: "2", episode: "5", part: "part1", reorganize: true) with
            {
                Files = [new MoviePilotTransferFile("e1.mkv", "/x/e1.mkv"), new MoviePilotTransferFile("e2.mkv", "/x/e2.mkv")]
            };
            var body = request.Body([File("/x/e1.mkv"), File("/x/e2.mkv")], preview: false);

            Assert.True(body.ContainsKey("fileitems"), "多文件用 fileitems");
            Assert.Equal(2, System.Text.Json.JsonSerializer.SerializeToElement(body["fileitems"])!.GetArrayLength());
            Assert.Equal(2, (int)body["season"]!, "季号按数字发，字符串会被 422");
            Assert.Equal("5", body["episode_detail"]);
            Assert.Equal("part1", body["episode_part"]);
            Assert.False((bool)body["skip_success"]!, "重新整理和跳过历史是反义的，只能开一头");
            Assert.False((bool)body["preview"]!);
        });

        Test("手动整理正文：文件和 MoviePilot 认的对不上就停", () =>
        {
            var request = Request();
            var error = Assert.Catch<MoviePilotException>(() =>
                request.Body([File("/other/path.mkv")], preview: true));
            Assert.Contains("对不上", error.Message);
            Assert.Contains("不会改为整理", error.Message, "兜底成父目录是这一条最不能发生的事");
        });
    }

    // ── 回执解析 ─────────────────────────────────────────────────────────────────────────────────────

    private static void ParseTests()
    {
        Test("手动整理回执：预览拆成行，一行的大字小字都对", () =>
        {
            var result = MoviePilotTransfer.ParseResult(Json("""
                {"summary":{"total":2,"success":1,"failed":1},
                 "items":[
                   {"source":"/x/a.mkv","target":"/media/电影/星际穿越/星际穿越.mkv","success":true,
                    "type":"电影","title":"星际穿越","season":null,"episode":null},
                   {"source":"/x/b.mkv","target":null,"success":false,"message":"无法识别"}]}
                """), success: true, "信封的话", preview: true);

            Assert.Equal(2, result.Items.Count);
            Assert.True(result.IsPreview);
            Assert.Equal("/media/电影/星际穿越/星际穿越.mkv", result.Items[0].Headline);
            Assert.Equal("可整理", result.Items[0].StatusLine);
            Assert.Equal("/x/b.mkv", result.Items[1].Headline, "失败的那行大字退到源路径");
            Assert.Contains("无法识别", result.Items[1].StatusLine);
            Assert.Contains("无法整理", result.Items[1].StatusLine);

            Assert.False(result.CanSubmit, "预览里有失败就不放提交");
        });

        Test("手动整理回执：提交的 state 原样认，accepted 不冒充完成", () =>
        {
            var result = MoviePilotTransfer.ParseResult(Json("""
                {"items":[{"state":"accepted","success":true,"target":"/media/x.mkv"},
                          {"state":"completed","success":true,"target":"/media/y.mkv"},
                          {"state":"failed","success":false,"message":"目标已存在"}]}
                """), success: true, "", preview: false);

            Assert.Equal("已接收，等待整理", result.Items[0].Status);
            Assert.Equal("已完成", result.Items[1].Status);
            Assert.Equal("失败", result.Items[2].Status);
            Assert.Contains("已接收 1", result.Summary);
            Assert.Contains("已完成 1", result.Summary);
            Assert.Contains("失败或待处理 1", result.Summary);
        });

        Test("手动整理回执：部分成功的批次不当作可以整批重试", () =>
        {
            // success:false 的信封也要留下 items —— 服务器已经收走了一半，把整批再发一遍等于搬两次。
            var result = MoviePilotTransfer.ParseResult(Json("""
                {"items":[{"state":"completed","success":true},{"state":"failed","success":false}]}
                """), success: false, "1 个失败", preview: false);

            Assert.False(result.Success);
            Assert.Equal(2, result.Items.Count);
            Assert.Equal("1 个失败", result.Message);
        });

        Test("手动整理回执：预览全绿才放行提交", () =>
        {
            Assert.True(Parse(Json("""
                {"items":[{"source":"/x/a.mkv","target":"/media/a.mkv","success":true}]}
                """), preview: true).CanSubmit);

            Assert.False(Parse(Json("""{"items":[{"source":"/x/a.mkv","target":"","success":true}]}"""),
                preview: true).CanSubmit, "没有目标路径的预览不能提交");
            Assert.False(Parse(Json("""{"items":[{"state":"completed","success":true}]}"""),
                preview: false).CanSubmit, "提交回执不是预览，不能再当通行证");
        });

        Test("手动整理确认话术：移动、覆盖、清历史各占一句", () =>
        {
            var preview = Parse(Json("""
                {"items":[{"source":"/x/a.mkv","target":"/media/a.mkv","success":true}]}
                """), preview: true);

            Assert.Contains("移动（源文件会被移走）", MoviePilotTransfer.Confirmation(Request(transferType: "move"), preview, background: false));
            Assert.Contains("覆盖", MoviePilotTransfer.Confirmation(Request(), preview, background: true));
            Assert.Contains("队列", MoviePilotTransfer.Confirmation(Request(), preview, background: true));
            Assert.Contains("清理命中", MoviePilotTransfer.Confirmation(Request(reorganize: true), preview, background: false));
            Assert.Contains("复用历史识别", MoviePilotTransfer.Confirmation(Request(fromHistory: true), preview, background: false));
        });
    }

    // ── 服务那一层（假传输层走真路径） ──────────────────────────────────────────────────────────────

    private static void ServiceTests()
    {
        Test("手动整理服务：storage/list 换出真文件才往下走", () =>
        {
            var transport = new StubTransport()
                .Answer("login/access-token", Session)
                .Answer("storage/list",
                    """{"success":true,"data":[{"storage":"local","type":"file","path":"/media/downloads/电影.mkv","name":"电影.mkv","size":123}]}""");

            var file = ServiceOn(transport).TransferFileAsync("/media/downloads/电影.mkv", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.Equal("file", file.GetProperty("type").GetString());
            Assert.Equal(1, transport.Count("login/access-token"));
            var sent = transport.Only("storage/list");
            Assert.Equal("POST", sent.Method);
            Assert.Contains("\"storage\":\"local\"", sent.Body, "换文件这一趟自己声明本地存储");
            Assert.Contains("media/downloads", sent.Body, "中文会被 JSON 选项转义，ASCII 路径段照在");
        });

        Test("手动整理服务：MoviePilot 看不见的文件就地停，不整理父目录", () =>
        {
            // 列表回了别的文件（服务器上路径对不上），按父目录兜底的写法会把整个目录搬走 —— 必须停。
            var transport = new StubTransport()
                .Answer("login/access-token", Session)
                .Answer("storage/list",
                    """{"success":true,"data":[{"storage":"local","type":"file","path":"/media/downloads/别的.mkv"}]}""");

            var error = Assert.Catch<MoviePilotException>(() =>
                ServiceOn(transport).TransferFileAsync("/media/downloads/电影.mkv", CancellationToken.None)
                    .GetAwaiter().GetResult());
            Assert.Contains("看不到这个文件", error.Message);
        });

        Test("手动整理服务：一批文件里有一个换不出来就整批停", () =>
        {
            var transport = new StubTransport()
                .Answer("login/access-token", Session)
                .Answer("storage/list",
                    """{"success":true,"data":[{"storage":"local","type":"file","path":"/media/x/e1.mkv"}]}""");

            var error = Assert.Catch<MoviePilotException>(() =>
                ServiceOn(transport).TransferFilesAsync(
                    [new MoviePilotTransferFile("e1.mkv", "/media/x/e1.mkv"),
                     new MoviePilotTransferFile("e2.mkv", "/media/x/e2.mkv")],
                    CancellationToken.None).GetAwaiter().GetResult());
            Assert.Contains("e2.mkv", error.Message);
            Assert.Equal(2, transport.Count("storage/list"), "第二个才失败，前一个的请求照发");
        });

        Test("手动整理服务：目的路径匹配读的是 target-path 那一趟", () =>
        {
            var transport = new StubTransport()
                .Answer("login/access-token", Session)
                .Answer("transfer/manual/target-path",
                    """{"success":true,"data":{"target_storage":"local","target_path":"/media/library","transfer_type":"copy","scrape":false}}""");

            var matched = ServiceOn(transport).TransferTargetAsync(File("/x/a.mkv"), CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.Equal(1, matched.Directories.Count);
            var directory = matched.Directories[0];
            Assert.Equal("/media/library", directory.Path);
            Assert.Equal("copy", directory.TransferType);
        });

        Test("手动整理服务：预览发 preview:true，提交发 preview:false、队列走 background 查询串", () =>
        {
            var transport = new StubTransport()
                .Answer("login/access-token", Session)
                .Answer("transfer/manual?background=false",
                    """{"success":true,"data":{"items":[{"source":"/x/a.mkv","target":"/media/a.mkv","success":true}]}}""")
                .Answer("transfer/manual?background=true",
                    """{"success":true,"data":{"items":[{"state":"accepted","success":true,"target":"/media/a.mkv"}]}}""");

            var service = ServiceOn(transport);
            var request = Request(targetStorage: "local", targetPath: "/media/library");
            var files = new List<JsonElement> { File("/x/a.mkv") };

            var preview = service.TransferPreviewAsync(request, files, CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(preview.Result.IsPreview);
            Assert.Contains("\"preview\":true", transport.SentTo("transfer/manual")[0].Body);

            var queued = service.TransferSubmitAsync(request, files, background: true, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.Equal("accepted", queued.Items[0].State);
            var sent = transport.Only("transfer/manual?background=true");
            Assert.Contains("background=true", sent.Url);
            Assert.Contains("\"preview\":false", sent.Body);
        });

        Test("手动整理服务：success:false 的提交信封不抛，逐文件回执交到界面上", () =>
        {
            var transport = new StubTransport()
                .Answer("login/access-token", Session)
                .Answer("transfer/manual?background=false",
                    """{"success":false,"message":"部分文件整理失败","data":{"items":[{"state":"failed","success":false,"message":"目标已存在"}]}}""");

            var result = ServiceOn(transport).TransferSubmitAsync(
                Request(), [File("/x/a.mkv")], background: false, CancellationToken.None).GetAwaiter().GetResult();

            Assert.False(result.Success);
            Assert.Equal("部分文件整理失败", result.Message);
            Assert.Equal("失败", result.Items[0].Status);
        });

        Test("手动整理服务：选项下拉从三个接口各取各的，空表退到默认", () =>
        {
            var transport = new StubTransport()
                .Answer("login/access-token", Session)
                .Answer("storage/options", """{"success":true,"data":[{"name":"本地","type":"local"},{"name":"Rclone","type":"rclone"}]}""")
                .Answer("storage/directories", """{"success":true,"data":[{"library_storage":"local","library_path":"/media/library","transfer_type":"copy","overwrite_mode":"latest"}]}""")
                .Answer("media/source", """{"success":true,"data":[{"name":"TheMovieDb","media_source":"themoviedb","media_types":["电影","电视剧"]}]}""");

            var options = ServiceOn(transport).TransferOptionsAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(2, options.Storages.Count);
            Assert.Equal(1, options.Directories.Count);
            Assert.Equal("/media/library", options.Directories[0].Path);
            Assert.Equal(1, options.MediaSources.Count);
            Assert.Equal("themoviedb", options.MediaSources[0].Id);
            Assert.Contains("电影", string.Join("|", options.MediaSources[0].Types));
        });

        Test("手动整理服务：空机房也立得起表单（存储和数据源退默认）", () =>
        {
            var transport = new StubTransport()
                .Answer("login/access-token", Session)
                .Answer("storage/options", """{"success":true,"data":[]}""")
                .Answer("storage/directories", """{"success":true,"data":[]}""")
                .Answer("media/source", """{"success":true,"data":[]}""");

            var options = ServiceOn(transport).TransferOptionsAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(1, options.Storages.Count);
            Assert.Equal("local", options.Storages[0].Type);
            Assert.Equal(1, options.MediaSources.Count);
            Assert.Equal("themoviedb", options.MediaSources[0].Id);
        });

        Test("手动整理收集：电影用自己的 TMDB 号和自己的文件", () =>
        {
            // Emby 的单条目接口回裸条目，没有 MoviePilot 那层 {success,data} 信封。
            var transport = new StubTransport()
                .Answer("Items/m1", """{"Id":"m1","Name":"星际穿越","Type":"Movie","Path":"/media/downloads/星际穿越.mkv","MediaSources":[{"Path":"/media/downloads/星际穿越.mkv","Name":"星际穿越.mkv"}],"ProviderIds":{"Tmdb":"157336"}}""");

            var context = Collect(transport, new EmbyItem { Id = "m1", Type = EmbyItemType.Movie, Name = "星际穿越" });

            Assert.Equal("电影", context.Type);
            Assert.Equal("星际穿越", context.Title);
            Assert.Equal("157336", context.MediaId);
            Assert.Equal("/media/downloads/星际穿越.mkv", context.Files is [var only] ? only.Path : "");
        });

        Test("手动整理收集：单集回溯到剧集的 TMDB 号，集号季号另算", () =>
        {
            var transport = new StubTransport()
                .Answer("Items/e1", """{"Id":"e1","Name":"第二集","Type":"Episode","SeriesId":"s1","SeriesName":"某剧","ParentIndexNumber":1,"IndexNumber":2,"Path":"/media/tv/某剧/S01/某剧 S01E02.mkv","MediaSources":[{"Path":"/media/tv/某剧/S01/某剧 S01E02.mkv"}],"ProviderIds":{"Tmdb":"99901"}}""")
                .Answer("Items/s1", """{"Id":"s1","Name":"某剧","Type":"Series","ProviderIds":{"Tmdb":"100"}}""");

            var context = Collect(transport, new EmbyItem { Id = "e1", Type = EmbyItemType.Episode, Name = "第二集", SeriesId = "s1" });

            Assert.Equal("电视剧", context.Type);
            Assert.Equal("某剧", context.Title);
            Assert.Equal("100", context.MediaId, "单集的 TMDB 号不能冒充剧的");
            Assert.Equal(1, context.Season);
            Assert.Equal("2", context.Episode);
            Assert.Equal(1, context.Files.Count);
        });

        Test("手动整理收集：一季收齐底下每一集，上限截断不静默丢", () =>
        {
            var episodes = string.Join(",", Enumerable.Range(1, 40).Select(index =>
                $$"""{"Id":"e{{index}}","Type":"Episode","IndexNumber":{{index}},"Path":"/media/x/e{{index}}.mkv","MediaSources":[{"Path":"/media/x/e{{index}}.mkv"}]}"""));

            var transport = new StubTransport()
                .Answer("Items/n1", """{"Id":"n1","Name":"第 1 季","Type":"Season","SeriesId":"s1","IndexNumber":1}""")
                .Answer("Items", $$"""{"Items":[{{episodes}}],"TotalRecordCount":40}""");

            var context = Collect(transport, new EmbyItem { Id = "n1", Type = EmbyItemType.Season, Name = "第 1 季", SeriesId = "s1" });

            Assert.Equal(MoviePilotTransferCollect.MaxFiles, context.Files.Count);
            Assert.Equal("/media/x/e1.mkv", context.Files[0].Path);
        });
    }

    // ── 菜单 ─────────────────────────────────────────────────────────────────────────────────────────

    private static void MenuTests()
    {
        Test("更多菜单：开了 MoviePilot，「搜索其他版本」旁边有「手动整理…」", () =>
        {
            foreach (var item in (EmbyItem[])[
                new() { Id = "m1", Type = EmbyItemType.Movie },
                new() { Id = "s1", Type = EmbyItemType.Series },
                new() { Id = "n1", Type = EmbyItemType.Season },
                Resuming()])
            {
                var labels = LabelsOf(item);
                Assert.Contains("手动整理…", labels);
                Assert.True(
                    labels.IndexOf("搜索其他版本", StringComparison.Ordinal) < labels.IndexOf("手动整理…", StringComparison.Ordinal),
                    "搜索在前，整理在后 —— 先找别的版本、再动文件");
            }
        });

        Test("更多菜单：没开 MoviePilot 或条目对不上一部片，两条都没有", () =>
        {
            foreach (var item in (EmbyItem[])[
                new() { Id = "m1", Type = EmbyItemType.Movie },
                new() { Id = "b1", Type = EmbyItemType.BoxSet }])
                Assert.DoesNotContain("手动整理…", string.Join("|", ItemMenu.For(item).Select(row => row.Label)));

            Assert.DoesNotContain("手动整理…",
                string.Join("|", ItemMenu.For(new EmbyItem { Id = "m1", Type = EmbyItemType.Movie }, moviePilotEnabled: false)
                    .Select(row => row.Label)));
        });
    }

    // ── 夹具 ─────────────────────────────────────────────────────────────────────────────────────────

    private const string Session = """{"access_token":"jwt","super_user":true,"user_name":"docuser","user_id":1}""";

    private static string Item(string json) =>
        $$"""{"success":true,"message":"","data":{{json}}}""";

    private static MoviePilotTransferRequest Request(
        string targetStorage = "local",
        string targetPath = "/media/library",
        string transferType = "copy",
        string type = "电影",
        string mediaId = "",
        string season = "",
        string episode = "",
        string part = "",
        string minimumSize = "0",
        bool reorganize = false,
        bool fromHistory = false) => new()
        {
            Files = [new MoviePilotTransferFile("a.mkv", "/x/a.mkv")],
            TargetStorage = targetStorage,
            TargetPath = targetPath,
            TransferType = transferType,
            Type = type,
            MediaId = mediaId,
            Season = season,
            Episode = episode,
            Part = part,
            MinimumSize = minimumSize,
            Reorganize = reorganize,
            FromHistory = fromHistory
        };

    private static string? Problem(MoviePilotTransferRequest request) => request.Problem;

    private static JsonElement File(string path)
    {
        using var document = JsonDocument.Parse($$"""{"storage":"local","type":"file","path":"{{path}}"}""");
        return document.RootElement.Clone();
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static MoviePilotTransferResult Parse(JsonElement data, bool preview) =>
        MoviePilotTransfer.ParseResult(data, success: true, "", preview);

    private static MoviePilotService ServiceOn(StubTransport transport)
    {
        var settings = new AppSettings();
        settings.MoviePilot.Enabled = true;
        settings.MoviePilot.Url = "192.0.2.10:3001";
        settings.MoviePilot.Username = "docuser";

        var credentials = new MoviePilotCredentials(PassthroughSecretProtector.Instance);
        credentials.SetPassword(settings.MoviePilot, "docpass.1");

        return new MoviePilotService(new MoviePilotClient(transport), credentials, settings);
    }

    private static MoviePilotTransferContext Collect(StubTransport transport, EmbyItem item)
    {
        var client = new EmbyClient(
            new EmbyHttp(transport),
            new EmbyConnection(new Uri("http://192.0.2.10:8096/"), "token-1", "u1", "docuser", "果服",
                DeviceIdentity.Create("device-1", "3.0.0")));

        return MoviePilotTransferCollect.CollectAsync(client, item, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>继续观看那一排上的一张卡：一集看了三成（同 <see cref="ItemMenuTests"/> 的那一份）。</summary>
    private static EmbyItem Resuming() => new()
    {
        Id = "e1",
        Name = "第二集",
        Type = EmbyItemType.Episode,
        SeriesId = "s1",
        SeriesName = "某剧",
        UserData = new EmbyUserData { PlaybackPositionTicks = 1800_000_000, PlayedPercentage = 30 }
    };

    private static string LabelsOf(EmbyItem item) =>
        string.Join("|", ItemMenu.For(item, moviePilotEnabled: true).Select(row => row.Label));
}
