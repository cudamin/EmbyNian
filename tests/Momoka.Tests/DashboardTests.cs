using System.Net;
using Momoka.Emby;
using static Momoka.Tests.TestHarness;

namespace Momoka.Tests;

internal static class DashboardTests
{
    public static void Register()
    {
        Test("原生控制台：只读当前服务器并保留反代路径", () =>
        {
            var transport = Answers().Answer("/Sessions", """
                [{"Id":"session","DeviceName":"客厅电视","UserName":"测试用户",
                  "NowPlayingItem":{"Name":"离线电影","RunTimeTicks":600000000},
                  "PlayState":{"PositionTicks":300000000,"IsPaused":true,"PlayMethod":"DirectPlay"}}]
                """).Answer("ActivityLog/Entries", """
                {"Items":[{"Name":"媒体库扫描完成","Date":"2026-10-08T01:00:00Z","Severity":"Info"}],"TotalRecordCount":25}
                """);
            using var http = new EmbyHttp(transport);
            var data = Read(http);
            Assert.Equal("离线服务器", data.System.ServerName);
            Assert.Equal("客厅电视", data.Sessions.Value![0].DeviceName);
            Assert.True(data.Sessions.Value[0].PlayState!.IsPaused);
            Assert.Equal(25, data.Activity.Value!.TotalRecordCount);
            Assert.Equal("GET", transport.Only("/Sessions").Method);
            var activity = transport.Only("ActivityLog/Entries");
            Assert.Equal("GET", activity.Method);
            Assert.Contains("/media/emby/System/ActivityLog/Entries", activity.Url);
            Assert.Contains("Limit=20", activity.Url);
            Assert.Contains("StartIndex=0", activity.Url);
            Assert.False(activity.Url.Contains("fixture-token", StringComparison.Ordinal));
        });

        Test("原生控制台：普通账号只展示公开信息", () =>
        {
            var transport = new StubTransport()
                .Answer("/System/Info/Public", """{"ServerName":"公开服务器"}""")
                .Fail("/System/Info", HttpStatusCode.Forbidden);
            using var http = new EmbyHttp(transport);
            var data = Read(http);
            Assert.True(data.System.IsRestricted);
            Assert.True(data.Sessions.Value is null && data.Activity.Value is null);
            Assert.Contains("管理员", data.Sessions.Error!);
            Assert.Equal(0, transport.SentTo("/Sessions").Count);
            Assert.Equal(0, transport.SentTo("ActivityLog").Count);
        });

        Test("原生控制台：单项失败不抹掉成功分区", () =>
        {
            using var http = new EmbyHttp(Answers().Fail("ActivityLog/Entries", HttpStatusCode.Forbidden));
            var data = Read(http);
            Assert.True(data.Sessions.Value is { Count: 0 });
            Assert.True(data.Sessions.Error is null);
            Assert.True(data.Activity.Value is null);
            Assert.Contains("管理员", data.Activity.Error!);
        });

        Test("原生控制台：缺失接口和损坏数据不能显示成空列表", () =>
        {
            using var http = new EmbyHttp(Answers().Fail("/Sessions", HttpStatusCode.NotFound)
                .Answer("ActivityLog/Entries", "invalid-json"));
            var data = Read(http);
            Assert.True(data.Sessions.Value is null && data.Activity.Value is null);
            Assert.True(!string.IsNullOrWhiteSpace(data.Sessions.Error));
            Assert.True(!string.IsNullOrWhiteSpace(data.Activity.Error));
        });

        Test("原生控制台：令牌过期交回会话恢复", () =>
        {
            using var http = new EmbyHttp(Answers().Fail("ActivityLog/Entries", HttpStatusCode.Unauthorized));
            Assert.Throws<EmbyTokenExpiredException>(() => Read(http));
        });

        Test("原生控制台：取消读取不降级成分区错误", () =>
        {
            using var token = new CancellationTokenSource();
            token.Cancel();
            using var http = new EmbyHttp(Answers());
            Assert.Throws<OperationCanceledException>(() => Read(http, token.Token));
        });

        Test("服务器电源：POST 当前服务器端点且不携带配置或凭据参数", () =>
        {
            var transport = new StubTransport().Answer("System/Restart", "").Answer("System/Shutdown", "");
            using var http = new EmbyHttp(transport);
            var client = Client(http);
            client.RestartServerAsync(CancellationToken.None).GetAwaiter().GetResult();
            client.ShutdownServerAsync(CancellationToken.None).GetAwaiter().GetResult();
            foreach (var route in new[] { "System/Restart", "System/Shutdown" })
            {
                var sent = transport.Only(route);
                Assert.Equal("POST", sent.Method);
                Assert.Equal("https://fixture.invalid/media/emby/" + route, sent.Url);
                Assert.Equal("", sent.Body);
            }
        });

        Test("服务器改名：空白名称在读取配置之前被拒绝", () =>
            Assert.Throws<ArgumentException>(() =>
                EmbyServerAdministration.RenameAsync(null!, " \t ", CancellationToken.None).GetAwaiter().GetResult()));
    }

    private static StubTransport Answers() => new StubTransport()
        .Answer("/System/Info", """{"ServerName":"离线服务器","Version":"4.9.1.0"}""")
        .Answer("/Sessions", "[]")
        .Answer("ActivityLog/Entries", """{"Items":[],"TotalRecordCount":0}""");

    private static EmbyDashboardSnapshot Read(EmbyHttp http, CancellationToken token = default) =>
        Client(http).GetDashboardAsync(token).GetAwaiter().GetResult();

    private static EmbyClient Client(EmbyHttp http) => new(http,
        new EmbyConnection(new Uri("https://fixture.invalid/media/emby/"), "fixture-token", "user", "测试用户",
            "离线服务器", DeviceIdentity.Create("fixture", "test")));
}
