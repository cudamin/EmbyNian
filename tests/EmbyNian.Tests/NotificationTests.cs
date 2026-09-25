using System.Text.Json;
using EmbyNian.Configuration;
using EmbyNian.Emby;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 「通知改走 Emby」（用户令 2026-09-25，原「客户端直发 MoviePilot webhook」那套已退役）：本客户端对 Emby
/// 通知系统那几条接口的请求形状（官方网页端 <c>/settings/notifications.html</c> 用的就是它们）、条目与
/// 事件表的解析、条目深拷贝，以及旧设置文件里残留的通知节的去向。全部离线，假传输层按 URL 回话。
/// </summary>
internal static class NotificationTests
{
    private static readonly Uri ApiBase = new("http://192.0.2.10:8896/emby/");

    private static EmbyClient Client(StubTransport stub) =>
        new(new EmbyHttp(stub),
            new EmbyConnection(ApiBase, "token-1", "u1", "docuser", "果服",
                DeviceIdentity.Create("device-1", "3.0.0")));

    public static void Register()
    {
        RegisterReads();
        RegisterWrites();
        RegisterEntry();
        RegisterMigration();
    }

    /// <summary>本机服务器上真实回样的一段 Configured 条目（Webhooks 转发到 MoviePilot），字段名与嵌套按它钉。</summary>
    private const string ConfiguredSample = """
        [{
            "NotifierKey": "webhooknotifications",
            "SetupModuleUrl": "configurationpage?name=webhookeditorjs",
            "ServiceName": "Webhooks",
            "PluginId": "85a7b1d4-fbda-4e85-a0a2-ac303c9946a4",
            "FriendlyName": "MP",
            "Id": "6a20224d1eb04bd689ba726a35e8b346",
            "Enabled": true,
            "UserIds": [],
            "DeviceIds": [],
            "LibraryIds": [],
            "EventIds": ["library.new", "playback.start", "playback.stop"],
            "UserId": "1",
            "IsSelfNotification": false,
            "GroupItems": false,
            "Options": { "EnableMultipartFormData": "true", "Url": "http://172.17.0.1:3001/api/v1/webhook?token=x" }
        }]
        """;

    private const string TypesSample = """
        [
            {
                "Name": "服务器", "Id": "system",
                "Events": [ { "Name": "服务器启动", "Id": "system.serverstartup", "CategoryName": "服务器", "CategoryId": "system" } ]
            },
            {
                "Name": "媒体库", "Id": "library",
                "Events": [ { "Name": "新媒体入库", "Id": "library.new", "CategoryName": "媒体库", "CategoryId": "library" } ]
            }
        ]
        """;

    private static void RegisterReads()
    {
        Test("通知读取：列表走 Configured，UserId 在查询串上", () =>
        {
            var stub = new StubTransport().Answer("Notifications/Services/Configured", ConfiguredSample);
            var entries = Client(stub).GetNotificationsAsync(CancellationToken.None).GetAwaiter().GetResult();

            var sent = stub.Only("Notifications/Services/Configured");
            Assert.Equal("GET", sent.Method);
            Assert.Contains("Notifications/Services/Configured?UserId=u1", sent.Url, "按当前用户问 —— 官方网页端同款参数，缺了它服务器直接 500");
            Assert.Equal(1, entries.Count);
            Assert.Equal("MP", entries[0].FriendlyName);
            Assert.Equal("webhooknotifications", entries[0].NotifierKey);
            Assert.Equal("http://172.17.0.1:3001/api/v1/webhook?token=x", entries[0].Options["Url"]);
            Assert.True(entries[0].Options["EnableMultipartFormData"] == "true", "服务器存的是字符串，不是布尔");
            Assert.Equal(3, entries[0].EventIds.Count);
        });

        Test("通知读取：事件表按类分节带本地化名，Defaults 要带上 NotifierKey", () =>
        {
            var stub = new StubTransport()
                .Answer("Notifications/Types", TypesSample)
                .Answer("Notifications/Services/Defaults", """{ "NotifierKey": "webhooknotifications", "Enabled": true, "EventIds": [] }""");
            var client = Client(stub);

            var types = client.GetNotificationTypesAsync(CancellationToken.None).GetAwaiter().GetResult();
            var sent = stub.Only("Notifications/Types");
            Assert.Contains("UserId=u1", sent.Url);
            Assert.Equal(2, types.Count);
            Assert.Equal("system", types[0].Id);
            Assert.Equal("服务器启动", types[0].Events[0].Name, "名字服务器已本地化，客户端照用，不自带词表");
            Assert.Equal("system", types[0].Events[0].CategoryId);

            var defaults = client.GetNotificationDefaultsAsync("webhooknotifications", CancellationToken.None).GetAwaiter().GetResult();
            var sentDefaults = stub.Only("Notifications/Services/Defaults");
            Assert.Contains("UserId=u1", sentDefaults.Url);
            Assert.Contains("NotifierKey=webhooknotifications", sentDefaults.Url, "新建哪条渠道的底，得说清是哪个服务");
            Assert.True(defaults.Enabled);
        });

        Test("通知读取：渠道名单、用户、媒体库、设备四份名单各走各的接口", () =>
        {
            var stub = new StubTransport()
                .Answer("Notifications/Services?", """[ { "Name": "Webhooks", "Id": "webhooknotifications", "Icon": "webhook" } ]""")
                .Answer("Users?", """[ { "Id": "u1", "Name": "donxuelian" } ]""")
                .Answer("Library/VirtualFolders", """[ { "Name": "电影", "ItemId": "6", "Guid": "5019d9fabf0246b4a2e934766c9c0d47" } ]""")
                .Answer("Devices", """{ "Items": [ { "Id": "23", "Name": "Probe", "AppName": "Emby MPV Client" } ], "TotalRecordCount": 1 }""");
            var client = Client(stub);

            var services = client.GetNotificationServicesAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(1, services.Count);
            Assert.Equal("webhooknotifications", services[0].Id);

            var users = client.GetNotificationUsersAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal("donxuelian", users[0].Name);

            var libraries = client.GetNotificationLibrariesAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal("5019d9fabf0246b4a2e934766c9c0d47", libraries[0].PickId, "官方编辑器认 Guid —— LibraryIds 存的是它");

            var devices = client.GetNotificationDevicesAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal("23", devices[0].Id, "Devices 是信封形状，取的是里面的 Items");

            Assert.Equal("GET", stub.Only("Users?").Method);
            Assert.Equal("GET", stub.Only("Library/VirtualFolders").Method);
            Assert.Equal("GET", stub.Only("Devices").Method);
        });

        Test("通知读取：媒体库没有 Guid 时回落到 ItemId（老库两条腿都要有）", () =>
        {
            var legacy = new NotificationLibrary { Name = "旧库", ItemId = "42" };
            Assert.Equal("42", legacy.PickId);
            var modern = new NotificationLibrary { Name = "新库", ItemId = "6", Guid = "abc" };
            Assert.Equal("abc", modern.PickId);
        });
    }

    private static void RegisterWrites()
    {
        Test("通知写入：保存是整份 POST 到 Configured，正文键是 Emby 的 PascalCase", () =>
        {
            var stub = new StubTransport().Answer("Notifications/Services/Configured", "");
            var client = Client(stub);

            var entry = new UserNotificationInfo
            {
                NotifierKey = "webhooknotifications",
                ServiceName = "Webhooks",
                FriendlyName = "MP",
                Enabled = true,
                EventIds = ["playback.stop"],
                Options = new Dictionary<string, string> { ["Url"] = "http://h:3001/api/v1/webhook?token=t" }
            };
            client.SaveNotificationAsync(entry, CancellationToken.None).GetAwaiter().GetResult();

            var sent = stub.Only("Notifications/Services/Configured");
            Assert.Equal("POST", sent.Method);
            Assert.Contains("\"NotifierKey\":\"webhooknotifications\"", sent.Body, "MoviePilot 这头不读它，但 Emby 读 —— 键的大小写跟服务器对齐");
            Assert.Contains("\"EventIds\":[\"playback.stop\"]", sent.Body);
            Assert.Contains("\"Url\":\"http://h:3001/api/v1/webhook?token=t\"", sent.Body);
            Assert.DoesNotContain("token-1", sent.Body, "访问令牌只在头上，绝不进正文");
        });

        Test("通知写入：删除是 DELETE，条目 id 和用户都在查询串上，恰好一趟", () =>
        {
            var stub = new StubTransport().Answer("Notifications/Services/Configured", "");
            Client(stub).DeleteNotificationAsync("6a20224d1eb04bd689ba726a35e8b346", CancellationToken.None)
                .GetAwaiter().GetResult();

            var sent = stub.Only("Notifications/Services/Configured");
            Assert.Equal("DELETE", sent.Method);
            Assert.Contains("Id=6a20224d1eb04bd689ba726a35e8b346", sent.Url);
            Assert.Contains("UserId=u1", sent.Url, "官方语义：删的是「这条 + 这个用户」");
            Assert.Equal("", sent.Body, "删除不带正文");
        });

        Test("通知测试：POST 到 Services/Test，发的是条目本身", () =>
        {
            var stub = new StubTransport().Answer("Notifications/Services/Test", "");
            var client = Client(stub);

            var entry = new UserNotificationInfo { NotifierKey = "webhooknotifications", Enabled = true };
            client.TestNotificationAsync(entry, CancellationToken.None).GetAwaiter().GetResult();

            var sent = stub.Only("Notifications/Services/Test");
            Assert.Equal("POST", sent.Method);
            Assert.Contains("\"NotifierKey\":\"webhooknotifications\"", sent.Body);
        });
    }

    private static void RegisterEntry()
    {
        Test("通知条目：Clone 是深拷贝 —— 改副本的列表和字典不碰原件", () =>
        {
            var entry = new UserNotificationInfo
            {
                FriendlyName = "MP",
                EventIds = ["playback.stop"],
                UserIds = ["u1"],
                Options = new Dictionary<string, string> { ["Url"] = "http://h/webhook" }
            };

            var copy = entry.Clone();
            copy.EventIds.Add("library.new");
            copy.UserIds.Add("u2");
            copy.Options["Url"] = "http://changed/";
            copy.FriendlyName = "改过";

            Assert.Equal("MP", entry.FriendlyName);
            Assert.Equal("playback.stop", string.Join(',', entry.EventIds), "事件表不能被副本带走");
            Assert.Equal("u1", string.Join(',', entry.UserIds));
            Assert.Equal("http://h/webhook", entry.Options["Url"], "Options 是字典，浅拷贝就是同一个字典 —— 必须重造");
            Assert.Equal("改过", copy.FriendlyName);
        });
    }

    private static void RegisterMigration()
    {
        Test("通知退役：旧设置文件里的 Notifications 节能读进来，再落盘就没了", () =>
        {
            const string json = """{ "SchemaVersion": 10, "DeviceId": "d-1", "Notifications": { "Enabled": true, "WebhookUrl": "http://h/api/v1/webhook?token=x", "TriggerPercent": 90 } }""";
            var settings = SettingsMigration.FromJson(json, PassthroughSecretProtector.Instance);

            var written = JsonSerializer.Serialize(settings, SettingsSerializer.WriteOptions);
            Assert.DoesNotContain("Notifications", written, "设置文档里不再有这一节 —— 通知数据全在服务器上");
            Assert.DoesNotContain("WebhookUrl", written);
        });
    }
}
