using System.Net;
using System.Text.Json.Nodes;
using EmbyNian.Emby;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

internal static class LibraryManagementTests
{
    public static void Register()
    {
        Test("媒体库管理：分页、ItemId 和旧目录回退", () =>
        {
            var transport = new StubTransport().Sequence("VirtualFolders/Query",
                (HttpStatusCode.OK, """{"Items":[{"ItemId":"movie","Id":"guid","Name":"电影","Locations":["/media/movies"]}],"TotalRecordCount":2}"""),
                (HttpStatusCode.OK, """{"Items":[{"ItemId":"tv","Name":"剧集","LibraryOptions":{"PathInfos":[{"Path":"/media/tv","NetworkPath":"smb://server/tv"}]}}],"TotalRecordCount":2}"""));
            using var http = new EmbyHttp(transport);
            var folders = Client(http).GetVirtualFoldersAsync(default).GetAwaiter().GetResult();
            Assert.Equal(2, folders.Count);
            Assert.Equal("movie", folders[0].Id);
            Assert.Equal("/media/movies", folders[0].Paths[0]!["Path"]!.ToString());
            Assert.Equal("smb://server/tv", folders[1].Paths[0]!["NetworkPath"]!.ToString());
            Assert.True(transport.SentTo("Query")[1].Url.Contains("StartIndex=1", StringComparison.Ordinal));
        });
        Test("媒体库管理：损坏分页必须报错", () =>
        {
            foreach (var next in new[] { "[]", """[{"ItemId":"a"}]""" })
            {
                using var http = new EmbyHttp(new StubTransport().Sequence("Query",
                    (HttpStatusCode.OK, """{"Items":[{"ItemId":"a"}],"TotalRecordCount":2}"""),
                    (HttpStatusCode.OK, "{\"Items\":" + next + ",\"TotalRecordCount\":2}")));
                Assert.Throws<InvalidDataException>(() => Client(http).GetVirtualFoldersAsync(default).GetAwaiter().GetResult());
            }
        });
        Test("媒体库管理：深层合并保留并发修改和未知提供者字段", () =>
        {
            var baseline = Parse("""{"EnableRealtimeMonitor":true,"PathInfos":[{"Path":"/old"}],"TypeOptions":[{"Type":"Movie","MetadataFetchers":["A"],"ImageOptions":[{"Type":"Backdrop","Limit":1,"MinWidth":0}]}]}""");
            var draft = (JsonObject)baseline.DeepClone();
            draft["EnableRealtimeMonitor"] = false;
            draft["TypeOptions"]![0]!["ImageOptions"]![0]!["Limit"] = 3;
            var latest = Parse("""{"EnableRealtimeMonitor":true,"PathInfos":[{"Path":"/new"}],"Plugin":{"Keep":9},"TypeOptions":[{"Type":"Movie","MetadataFetchers":["B"],"ImageOptions":[{"Type":"Backdrop","Limit":1,"MinWidth":1280,"Future":true}]},{"Type":"Series","Future":7}]}""");
            var merged = EmbyLibraryOptions.Merge(latest, baseline, draft);
            Assert.True(!EmbyLibraryOptions.Flag(merged, "EnableRealtimeMonitor"));
            Assert.Equal("/new", merged["PathInfos"]![0]!["Path"]!.ToString());
            Assert.Equal("B", merged["TypeOptions"]![0]!["MetadataFetchers"]![0]!.ToString());
            Assert.Equal("1280", merged["TypeOptions"]![0]!["ImageOptions"]![0]!["MinWidth"]!.ToString());
            Assert.Equal("3", merged["TypeOptions"]![0]!["ImageOptions"]![0]!["Limit"]!.ToString());
            Assert.Equal("7", merged["TypeOptions"]![1]!["Future"]!.ToString());
            Assert.Equal("9", merged["Plugin"]!["Keep"]!.ToString());
            Assert.Equal("1", baseline["TypeOptions"]![0]!["ImageOptions"]![0]!["Limit"]!.ToString());
        });
        Test("媒体库管理：首次编辑默认图片类型保留类型标识", () =>
        {
            var baseline = Parse("""{"TypeOptions":[{"Type":"Movie","ImageOptions":[{"Type":"Logo","Limit":0,"MinWidth":0}]}]}""");
            var draft = (JsonObject)baseline.DeepClone();
            draft["TypeOptions"]![0]!["ImageOptions"]![0]!["Limit"] = 1;
            var merged = EmbyLibraryOptions.Merge(new(), baseline, draft);
            Assert.Equal("Movie", merged["TypeOptions"]![0]!["Type"]!.ToString());
            Assert.Equal("Logo", merged["TypeOptions"]![0]!["ImageOptions"]![0]!["Type"]!.ToString());
            Assert.Equal("1", merged["TypeOptions"]![0]!["ImageOptions"]![0]!["Limit"]!.ToString());
        });
        Test("媒体库管理：未修改草稿不产生配置覆写", () =>
        {
            var baseline = Parse("""{"PreferredMetadataLanguage":"","TypeOptions":[{"Type":"Movie","ImageOptions":[{"Type":"Primary","Limit":1}]}]}""");
            var latest = Parse("""{"PreferredMetadataLanguage":"zh","ServerNewOption":42}""");
            Assert.True(JsonNode.DeepEquals(latest, EmbyLibraryOptions.Merge(latest, baseline, (JsonObject)baseline.DeepClone())));
        });
        Test("媒体库管理：新建采用服务器默认值并正确映射混合类型", () =>
        {
            var available = Parse("""{"DefaultLibraryOptions":{"EnableRealtimeMonitor":true,"Plugin":42,"MinResumePct":3}}""");
            var options = EmbyLibraryOptions.NewOptions(available, "");
            options["PathInfos"] = JsonNode.Parse("""[{"Path":"/media/new","Username":"fixture-user","Password":"fixture-password"}]""");
            var transport = new StubTransport().Answer("Library/VirtualFolders", "");
            using var http = new EmbyHttp(transport);
            Client(http).CreateVirtualFolderAsync(" 新媒体库 ", "", options, default).GetAwaiter().GetResult();
            var request = transport.Only("VirtualFolders");
            Assert.True(request.Url.StartsWith("https://fixture.invalid/media/emby/Library/VirtualFolders?", StringComparison.Ordinal));
            Assert.True(request.Url.Contains("refreshLibrary=true", StringComparison.Ordinal) && !request.Url.Contains("collectionType", StringComparison.Ordinal));
            Assert.True(!request.Url.Contains("fixture-password", StringComparison.Ordinal));
            Assert.Equal("42", Parse(request.Body)["LibraryOptions"]!["Plugin"]!.ToString());
            Assert.True(EmbyLibraryOptions.Flag(options, "EnableRealtimeMonitor"));
            Assert.True(!available["DefaultLibraryOptions"]!.AsObject().ContainsKey("PathInfos"));
        });
        Test("媒体库管理：路径写入使用专用端点且凭据留在正文", () =>
        {
            var transport = new StubTransport().Answer("VirtualFolders/Paths", "").Answer("Environment/ValidatePath", "").Answer("Environment/DirectoryContents", "[]");
            using var http = new EmbyHttp(transport);
            var client = Client(http);
            var path = Parse("""{"Path":"/media/a&b","NetworkPath":"\\\\nas\\video","Username":"test-account","Password":"test-secret"}""");
            client.ValidateServerDirectoryAsync("/media/a&b", "test-account", "test-secret", false, default).GetAwaiter().GetResult();
            client.GetServerDirectoriesAsync("/media/a&b", "test-account", "test-secret", default).GetAwaiter().GetResult();
            client.AddLibraryPathAsync("a", path, default).GetAwaiter().GetResult();
            client.UpdateLibraryPathAsync("a", path, default).GetAwaiter().GetResult();
            client.RemoveLibraryPathAsync("a", "/media/a&b", default).GetAwaiter().GetResult();
            Assert.True(transport.SentTo("/emby/").All(request => !request.Url.Contains("test-secret", StringComparison.Ordinal) && !request.Url.Contains("test-account", StringComparison.Ordinal)));
            Assert.True(transport.SentTo("Paths").All(request => request.Method == "POST"));
            Assert.Equal("test-secret", Parse(transport.SentTo("Paths")[0].Body)["PathInfo"]!["Password"]!.ToString());
            Assert.Equal("/media/a&b", Parse(transport.Only("Paths/Delete").Body)["Path"]!.ToString());
        });
        Test("媒体库管理：改名和移除使用媒体库端点", () =>
        {
            var transport = new StubTransport().Answer("VirtualFolders/Name", "").Answer("VirtualFolders/Delete", "");
            using var http = new EmbyHttp(transport);
            var client = Client(http);
            client.RenameVirtualFolderAsync("a", "新名称", default).GetAwaiter().GetResult();
            client.RemoveVirtualFolderAsync("a", default).GetAwaiter().GetResult();
            Assert.Equal("新名称", Parse(transport.Only("/Name").Body)["NewName"]!.ToString());
            Assert.Equal("a", Parse(transport.Only("/Delete").Body)["Id"]!.ToString());
            Assert.True(transport.SentTo("/emby/").All(request => !request.Url.Contains("Items/", StringComparison.Ordinal)));
        });
        Test("媒体库管理：扫描模式、替换图片和取消任务语义", () =>
        {
            var transport = new StubTransport().Answer("Items/a/Refresh", "").Answer("ScheduledTasks/Running", "");
            using var http = new EmbyHttp(transport);
            var client = Client(http);
            client.ScanVirtualFolderAsync("a", "scan", true, default).GetAwaiter().GetResult();
            client.ScanVirtualFolderAsync("a", "missing", false, default).GetAwaiter().GetResult();
            client.ScanVirtualFolderAsync("a", "all", true, default, true).GetAwaiter().GetResult();
            var scans = transport.SentTo("Items/a/Refresh");
            Assert.True(scans[0].Url.Contains("MetadataRefreshMode=Default", StringComparison.Ordinal) && scans[0].Url.Contains("ReplaceAllImages=false", StringComparison.Ordinal));
            Assert.True(scans[1].Url.Contains("MetadataRefreshMode=FullRefresh", StringComparison.Ordinal) && scans[1].Url.Contains("ReplaceAllMetadata=false", StringComparison.Ordinal));
            Assert.True(scans[2].Url.Contains("ReplaceAllMetadata=true", StringComparison.Ordinal) && scans[2].Url.Contains("ReplaceAllImages=true", StringComparison.Ordinal));
            Assert.True(scans[2].Url.Contains("ReplaceThumbnailImages=true", StringComparison.Ordinal));
            client.SetLibraryTaskRunningAsync("task", true, default).GetAwaiter().GetResult();
            client.SetLibraryTaskRunningAsync("task", false, default).GetAwaiter().GetResult();
            Assert.Equal("POST", transport.Only("Running/task/Delete").Method);
        });
        Test("媒体库管理：组合选项、单位和内容类型规则", () =>
        {
            LibrarySetting Field(string key) => EmbyLibrarySettings.All.Single(setting => setting.Key == key);
            var options = new JsonObject();
            Field("MultiVersion").Write(options, "both");
            Assert.True(EmbyLibraryOptions.Flag(options, "EnableMultiVersionByFiles") && EmbyLibraryOptions.Flag(options, "EnableMultiVersionByMetadata"));
            Field("Thumbnails").Write(options, "scanandtask");
            Assert.True(EmbyLibraryOptions.Flag(options, "EnableChapterImageExtraction") && EmbyLibraryOptions.Flag(options, "ExtractChapterImagesDuringLibraryScan"));
            Field("Thumbnails").Write(options, "");
            Assert.True(!EmbyLibraryOptions.Flag(options, "EnableChapterImageExtraction") && !EmbyLibraryOptions.Flag(options, "ExtractChapterImagesDuringLibraryScan"));
            Field("SampleIgnoreSize").Write(options, "300");
            Assert.Equal(314572800d, EmbyLibraryOptions.Number(options, "SampleIgnoreSize"));
            Assert.Equal("300", Field("SampleIgnoreSize").Read(options));
            Assert.True(Field("IntroDetection").AppliesTo("tvshows") && Field("IntroDetection").AppliesTo("") && !Field("IntroDetection").AppliesTo("movies"));
            Assert.True(!Field("EnableRealtimeMonitor").AppliesTo("boxsets"));
            Assert.True(EmbyLibrarySettings.All.Select(field => field.Key).Distinct().Count() == EmbyLibrarySettings.All.Count);
        });
        Test("媒体库管理：输入校验阻止空目录、重复目录和非有限数值", () =>
        {
            Assert.Throws<ArgumentException>(() => EmbyLibraryOptions.ValidateNew("新库", new()));
            Assert.Throws<ArgumentException>(() => EmbyLibraryOptions.ValidateNew("新库", Parse("""{"PathInfos":[{"Path":"/A"},{"Path":"/a"}]}""")));
            Assert.Throws<ArgumentException>(() => EmbyLibraryOptions.Validate(Parse("""{"MinResumePct":90,"MaxResumePct":2}""")));
            Assert.Throws<ArgumentException>(() => EmbyLibrarySettings.All.Single(field => field.Key == "MinResumePct").Write(new(), "NaN"));
        });
        Test("媒体库管理：拒绝越界标识并保留权限与取消错误", () =>
        {
            var transport = new StubTransport().Fail("VirtualFolders/Query", HttpStatusCode.Forbidden);
            using var http = new EmbyHttp(transport);
            Assert.Throws<ArgumentException>(() => Client(http).RemoveVirtualFolderAsync("../Items/a", default).GetAwaiter().GetResult());
            Assert.Throws<EmbyApiException>(() => Client(http).GetVirtualFoldersAsync(default).GetAwaiter().GetResult());
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => Client(http).GetVirtualFoldersAsync(cancel.Token).GetAwaiter().GetResult());
        });
        Test("媒体库管理：插件配置入口保持服务器来源与反代前缀", () =>
        {
            var root = new Uri("https://fixture.invalid/media/emby/");
            Assert.Equal("https://fixture.invalid/media/web/index.html#!/configurationpage?name=plugin", EmbyWebConsole.PageUrl(root, "#!/configurationpage?name=plugin"));
            Assert.Throws<ArgumentException>(() => EmbyWebConsole.PageUrl(root, "https://other.invalid/web/index.html"));
            Assert.Throws<ArgumentException>(() => EmbyWebConsole.PageUrl(root, "javascript:alert(1)"));
            Assert.Throws<ArgumentException>(() => EmbyWebConsole.PageUrl(root, "//other.invalid"));
            Assert.Throws<ArgumentException>(() => EmbyWebConsole.PageUrl(root, "../index.html"));
        });
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
    private static EmbyClient Client(EmbyHttp http) => new(http, new EmbyConnection(new Uri("https://fixture.invalid/media/emby/"), "fixture-token", "admin", "管理员", "离线服务器", DeviceIdentity.Create("fixture", "test")));
}
