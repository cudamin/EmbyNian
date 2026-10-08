using System.Net;
using System.Text.Json.Nodes;
using Momoka.Emby;
using static Momoka.Tests.TestHarness;

namespace Momoka.Tests;

internal static class UserManagementTests
{
    public static void Register()
    {
        Test("用户管理：分页读取完整且保留反代路径", () =>
        {
            var transport = new StubTransport().Sequence("Users/Query",
                (HttpStatusCode.OK, """{"Items":[{"Id":"a","Name":"甲","Policy":{}}],"TotalRecordCount":2}"""),
                (HttpStatusCode.OK, """{"Items":[{"Id":"b","Name":"乙","Policy":{}}],"TotalRecordCount":2}"""));
            using var http = new EmbyHttp(transport);
            var result = Client(http).GetManagedUsersAsync(default).GetAwaiter().GetResult();
            Assert.Equal(2, result.Count);
            Assert.Equal("b", result[1].Id);
            var requests = transport.SentTo("Users/Query");
            Assert.True(requests[1].Url.StartsWith("https://fixture.invalid/media/emby/Users/Query?StartIndex=1", StringComparison.Ordinal));
            Assert.True(requests.All(request => request.Method == "GET" && !request.Url.Contains("fixture-token", StringComparison.Ordinal)));
        });
        Test("用户管理：不完整或重复分页不能伪装成完整用户列表", () =>
        {
            foreach (var next in new[] { "[]", """[{"Id":"a","Policy":{}}]""" })
            {
                using var http = new EmbyHttp(new StubTransport().Sequence("Users/Query",
                    (HttpStatusCode.OK, """{"Items":[{"Id":"a","Policy":{}}],"TotalRecordCount":2}"""),
                    (HttpStatusCode.OK, "{\"Items\":" + next + ",\"TotalRecordCount\":2}")));
                Assert.Throws<InvalidDataException>(() => Client(http).GetManagedUsersAsync(default).GetAwaiter().GetResult());
            }
        });
        Test("用户管理：权限合并保留未知字段和并发修改", () =>
        {
            var original = Parse("""{"IsHidden":false,"EnableRemoteAccess":true,"Plugin":{"Value":1},"MaxParentalRating":5}""");
            var latest = Parse("""{"IsHidden":false,"EnableRemoteAccess":false,"Plugin":{"Value":9},"Future":42,"MaxParentalRating":5}""");
            var edited = (JsonObject)original.DeepClone();
            edited["IsHidden"] = true;
            edited["MaxParentalRating"] = null;
            var result = EmbyUserPermissions.Merge(latest, original, edited);
            Assert.True(result["IsHidden"]!.GetValue<bool>());
            Assert.True(!result["EnableRemoteAccess"]!.GetValue<bool>());
            Assert.Equal(9, result["Plugin"]!["Value"]!.GetValue<int>());
            Assert.Equal(42, result["Future"]!.GetValue<int>());
            Assert.True(result.ContainsKey("MaxParentalRating") && result["MaxParentalRating"] is null);
            Assert.True(!original["IsHidden"]!.GetValue<bool>() && !latest["IsHidden"]!.GetValue<bool>());
        });
        Test("用户管理：新建用户按服务器协议传复制选项", () =>
        {
            var transport = new StubTransport().Answer("Users/New", """{"Id":"new","Name":"新用户","Policy":{}}""");
            using var http = new EmbyHttp(transport);
            var result = Client(http).CreateManagedUserAsync(" 新用户 ", "source", ["userpolicy", "userdata"], default).GetAwaiter().GetResult();
            Assert.Equal("new", result.Id);
            var sent = transport.Only("Users/New");
            var body = Parse(sent.Body);
            Assert.Equal("POST", sent.Method);
            Assert.Equal("新用户", body["Name"]!.ToString());
            Assert.Equal("source", body["CopyFromUserId"]!.ToString());
            Assert.Equal(2, body["UserCopyOptions"]!.AsArray().Count);
        });
        Test("用户管理：复制数据以来源用户为端点并指定目标用户", () =>
        {
            var transport = new StubTransport().Answer("Users/source/CopyData", "");
            using var http = new EmbyHttp(transport);
            Client(http).CopyManagedUserDataAsync("target", "source", ["userconfiguration"], default).GetAwaiter().GetResult();
            var sent = transport.Only("CopyData");
            var body = Parse(sent.Body);
            Assert.Equal("POST", sent.Method);
            Assert.Equal("target", body["ToUserIds"]![0]!.ToString());
            Assert.Equal("userconfiguration", body["CopyOptions"]![0]!.ToString());
            Assert.True(!body.ContainsKey("CopyFromUserId"));
        });
        Test("用户管理：密码和 PIN 只在请求体中，空 PIN 显式清除", () =>
        {
            var transport = new StubTransport().Answer("/Password", "").Answer("Configuration/Partial", "");
            using var http = new EmbyHttp(transport);
            var client = Client(http);
            client.UpdateManagedUserPasswordAsync("a", "sensitive-test-only", default).GetAwaiter().GetResult();
            client.UpdateManagedUserPinAsync("a", "", default).GetAwaiter().GetResult();
            Assert.Equal("sensitive-test-only", Parse(transport.Only("/Password").Body)["NewPw"]!.ToString());
            Assert.True(!transport.Only("/Password").Url.Contains("sensitive-test-only", StringComparison.Ordinal));
            var pin = Parse(transport.Only("Configuration/Partial").Body);
            Assert.True(pin.ContainsKey("ProfilePin") && pin["ProfilePin"] is null);
        });
        Test("用户管理：密码 PIN 与访问时段输入约束", () =>
        {
            Assert.Throws<ArgumentException>(() => EmbyUserPermissions.ValidatePassword(true, "", ""));
            Assert.Throws<ArgumentException>(() => EmbyUserPermissions.ValidatePassword(false, "abc", "xyz"));
            EmbyUserPermissions.ValidatePassword(false, "", "");
            foreach (var pin in new[] { "123", "12345", "a123", "１２３４" })
                Assert.Throws<ArgumentException>(() => EmbyUserPermissions.ValidatePin(pin));
            EmbyUserPermissions.ValidatePin("0123");
            EmbyUserPermissions.ValidatePin("");
            Assert.Throws<ArgumentException>(() => EmbyUserPermissions.Schedule("Monday", 22, 8));
            Assert.Throws<ArgumentException>(() => EmbyUserPermissions.Schedule("Monday", double.NaN, 8));
            Assert.Throws<ArgumentException>(() => EmbyUserPermissions.Schedule("NotADay", 0, 24));
            Assert.Equal(24d, EmbyUserPermissions.Schedule("Monday", 0, 24)["EndHour"]!.GetValue<double>());
        });
        Test("用户管理：头像沿用用户接口的 Base64 正文", () =>
        {
            var transport = new StubTransport().Answer("Users/a/Images/Primary", "");
            using var http = new EmbyHttp(transport);
            Client(http).UploadManagedUserImageAsync("a", [1, 2, 3, 4], "image/png", default).GetAwaiter().GetResult();
            Assert.Equal("AQIDBA==", transport.Only("Images/Primary").Body);
            Assert.Throws<ArgumentException>(() => Client(http).UploadManagedUserImageAsync("a", [1], "image/webp", default).GetAwaiter().GetResult());
        });
        Test("用户管理：删除与 Connect 解绑使用官方 POST 端点", () =>
        {
            var transport = new StubTransport().Answer("Users/a/Delete", "").Answer("Connect/Link/Delete", "").Answer("Users/a/Images/Primary/Delete", "");
            using var http = new EmbyHttp(transport);
            var client = Client(http);
            client.DeleteManagedUserAsync("a", default).GetAwaiter().GetResult();
            client.UnlinkManagedUserAsync("a", default).GetAwaiter().GetResult();
            client.DeleteManagedUserImageAsync("a", default).GetAwaiter().GetResult();
            foreach (var sent in transport.SentTo("/Delete"))
            {
                Assert.Equal("POST", sent.Method);
                Assert.Equal("", sent.Body);
            }
        });
        Test("用户管理：拒绝越界用户 ID 且保留错误和取消语义", () =>
        {
            var transport = new StubTransport().Fail("Users/Query", HttpStatusCode.Forbidden);
            using var http = new EmbyHttp(transport);
            var client = Client(http);
            Assert.Throws<ArgumentException>(() => client.DeleteManagedUserAsync("../System", default).GetAwaiter().GetResult());
            Assert.Throws<EmbyApiException>(() => client.GetManagedUsersAsync(default).GetAwaiter().GetResult());
            using var source = new CancellationTokenSource();
            source.Cancel();
            Assert.Throws<OperationCanceledException>(() => client.GetManagedUsersAsync(source.Token).GetAwaiter().GetResult());
        });
        Test("用户管理：其他偏好链接保留反代并转义用户标识", () =>
        {
            var url = EmbyWebConsole.UserPreferencesUrl(new Uri("https://fixture.invalid/media/emby/"), "a&b");
            Assert.Equal("https://fixture.invalid/media/web/index.html#!/settings?userId=a%26b", url);
        });
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
    private static EmbyClient Client(EmbyHttp http) => new(http,
        new EmbyConnection(new Uri("https://fixture.invalid/media/emby/"), "fixture-token", "admin", "测试管理员",
            "离线服务器", DeviceIdentity.Create("fixture", "test")));
}
