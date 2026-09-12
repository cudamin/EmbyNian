using System.Net;
using System.Text;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 恢复登录那一趟：它既是令牌有效性的探针，又顺手取回了外壳紧接着要的媒体库列表。
/// <para>
/// 这一层从前一条自动化验证都没有 —— 测试工程连不上真服务器，而这几档在真服务器上也碰不到：一个刚过期的
/// 令牌、一台答了但一个库都没有的服务器、一份存了令牌却没有 UserId 的档案。四道闸门看不见它们，屏上也看不
/// 见（恢复失败的下场只是「回到登录页」，看着像是本来就没登录过）。
/// </para>
/// <para>
/// 靠的是把传输层交进来（<c>EmbySession</c> 那个 internal 构造函数）：一个假的
/// <see cref="HttpMessageHandler"/> 按 URL 回话，于是整条恢复路径可以在没有服务器的地方走完。
/// </para>
/// </summary>
internal static class SessionTests
{
    private const string Views = """
    { "Items": [
        { "Id": "4", "Name": "电视节目", "Type": "CollectionFolder", "CollectionType": "tvshows" },
        { "Id": "6", "Name": "电影", "Type": "CollectionFolder", "CollectionType": "movies" }
      ], "TotalRecordCount": 2 }
    """;

    private const string PublicInfo = """{ "ServerName": "果服", "Version": "4.9.5.0", "Id": "s1" }""";

    private const string SignedIn = """
    { "AccessToken": "token-fresh", "ServerId": "s1", "User": { "Id": "u1", "Name": "donxuelian" } }
    """;

    /// <summary>「最近添加」回的是一个裸数组，不是 <c>{ "Items": … }</c> —— 这一层的形状各接口不一样。</summary>
    private const string OneItem = """[ { "Id": "m1", "Name": "攻壳机动队" } ]""";

    public static void Register()
    {
        RegisterRestore();
        RegisterReauthentication();
        RegisterSessionReplacement();
        RegisterTransportFailures();
    }

    private static void RegisterRestore()
    {
        Test("恢复登录：令牌探针取回的媒体库交给外壳，不用再问一遍", () =>
        {
            var probe = new StubTransport()
                .Answer("Views", Views)
                .Answer("System/Info/Public", PublicInfo);

            var (session, server, account) = Signed(probe);

            Assert.True(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)), "令牌应该好使");
            Assert.Equal(1, probe.Count("Views"), "恢复那一趟只问了一次媒体库");

            var handed = session.TakeRestoredViews();
            Assert.True(handed is { Count: 2 }, "那一份要交到外壳手上");
            Assert.Equal("电视节目", handed![0].Name);
            Assert.Equal(1, probe.Count("Views"), "领这一份不该再打一次接口 —— 省掉的就是这一趟");
        });

        Test("恢复登录：那一份只给一次，刷新照旧真去问服务器", () =>
        {
            // 给两次就意味着服务器上新加的媒体库永远不出现在导航栏里，而用户手动刷新也治不好。
            var probe = new StubTransport()
                .Answer("Views", Views)
                .Answer("System/Info/Public", PublicInfo);

            var (session, server, account) = Signed(probe);
            Assert.True(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)));

            Assert.True(session.TakeRestoredViews() is not null, "第一次领得到");
            Assert.True(session.TakeRestoredViews() is null, "第二次就该是空的，调用方自己去问");
        });

        Test("恢复登录：令牌过期了不交东西，而且把它清掉", () =>
        {
            var probe = new StubTransport()
                .Fail("Views", HttpStatusCode.Unauthorized)
                .Answer("System/Info/Public", PublicInfo);

            var (session, server, account) = Signed(probe);

            Assert.False(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)), "过期的令牌不算恢复成功");
            Assert.True(session.TakeRestoredViews() is null, "失败那一路一份都不许留下");
            Assert.False(account.HasSavedToken, "过期的令牌要从档案里清掉，否则每次启动都白试一遍");
        });

        Test("恢复登录：服务器答了但一个库都没有，也算恢复成功", () =>
        {
            // 空列表和「问不出来」是两件事。这台账号真的一个库都没有的时候，恢复是成功的，交出去的就是一份
            // 空列表 —— 而不是 null，null 会让外壳再问一遍同一个已经答过的问题。
            var probe = new StubTransport()
                .Answer("Views", """{ "Items": [], "TotalRecordCount": 0 }""")
                .Answer("System/Info/Public", PublicInfo);

            var (session, server, account) = Signed(probe);

            Assert.True(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)));

            var handed = session.TakeRestoredViews();
            Assert.True(handed is { Count: 0 }, "空列表要交出去，而不是当作没有");
        });

        Test("恢复登录：没令牌、或者没 UserId，一个请求都不发", () =>
        {
            var probe = new StubTransport().Answer("Views", Views).Answer("System/Info/Public", PublicInfo);
            var (session, server, account) = Signed(probe);

            account.ProtectedAccessToken = "";
            Assert.False(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)), "没令牌就没什么可恢复的");

            account.ProtectedAccessToken = PassthroughSecretProtector.Instance.Protect("token-1");
            account.UserId = "";
            Assert.False(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)), "没 UserId 连 URL 都拼不出来");

            Assert.Equal(0, probe.Total, "两档都该在发请求之前就返回 —— 启动时白等一趟往返是要命的");
        });

        Test("恢复登录：服务器名字读不到就用档案里那个，不至于恢复失败", () =>
        {
            // /System/Info/Public 是「顺手问一下它自己叫什么」，不是恢复的前提条件。
            var probe = new StubTransport()
                .Answer("Views", Views)
                .Fail("System/Info/Public", HttpStatusCode.InternalServerError);

            var (session, server, account) = Signed(probe);

            Assert.True(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)), "名字读不到不该拖垮恢复");
            Assert.Equal("我的服务器", session.ServerDisplayName, "退回档案里存着的那个名字");
            Assert.True(session.TakeRestoredViews() is { Count: 2 }, "媒体库那一份照旧交出去");
        });

        Test("恢复登录：换了账号就作废上一个账号的媒体库", () =>
        {
            var probe = new StubTransport()
                .Answer("Views", Views)
                .Answer("System/Info/Public", PublicInfo);

            var (session, server, first) = Signed(probe);
            Assert.True(Wait(session.TryRestoreAsync(server, first, CancellationToken.None)));

            // 第一个账号那一份还没被领走，这时候换到第二个账号 —— 领出来的必须是第二个账号自己那一份。
            var second = new AccountProfile
            {
                Username = "tafei",
                UserId = "u2",
                ProtectedAccessToken = PassthroughSecretProtector.Instance.Protect("token-2")
            };
            server.Accounts.Add(second);

            Assert.True(Wait(session.TryRestoreAsync(server, second, CancellationToken.None)));
            Assert.Equal("u2", session.Account?.UserId, "会话已经换到第二个账号");
            Assert.True(session.TakeRestoredViews() is { Count: 2 }, "领到的是这一次取回来的那一份");
        });
    }

    /// <summary>
    /// 令牌在会话中途过期这一档。这是整个客户端最要紧的一条失败路径，也是最碰不到的一条 —— 真服务器上的令牌不会
    /// 按需过期，而屏上两种下场看着差不多：一种是「悄悄重登一次，用户什么都没察觉」，另一种是「回到登录页」。
    /// 走错了的代价是：本来只要静静重登一次的，却把人踢回登录页；或者反过来，令牌真的废了却在原地一遍遍重试。
    /// </summary>
    private static void RegisterReauthentication()
    {
        Test("令牌中途过期：存过密码就悄悄重登一次，那一趟自己重试", () =>
        {
            var transport = new StubTransport()
                .Answer("Views", Views)
                .Answer("System/Info/Public", PublicInfo)
                .Answer("Users/AuthenticateByName", SignedIn)
                // 第一次问条目撞上 401，重登之后第二次成功。
                .Sequence("Items", (HttpStatusCode.Unauthorized, ""), (HttpStatusCode.OK, OneItem));

            var (session, server, account) = Signed(transport);
            account.ProtectedPassword = PassthroughSecretProtector.Instance.Protect("pw");
            account.RememberPassword = true;

            Assert.True(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)));

            var items = Wait(session.ExecuteAsync(
                (client, token) => client.GetLatestAsync(null, 10, token), CancellationToken.None));

            Assert.Equal(1, items.Count, "重试那一趟的答案要交回给调用方");
            Assert.Equal(1, transport.Count("Users/AuthenticateByName"), "只重登一次");
            Assert.Equal(2, transport.Count("Items"), "撞过一次 401，重登之后再问一次");
            Assert.True(session.IsSignedIn, "用户根本不该被踢出去");
        });

        Test("令牌中途过期：没存密码就结束会话，而且说得出为什么", () =>
        {
            var transport = new StubTransport()
                .Answer("Views", Views)
                .Answer("System/Info/Public", PublicInfo)
                .Fail("Items", HttpStatusCode.Unauthorized);

            var (session, server, account) = Signed(transport);
            Assert.False(account.HasSavedPassword, "这一档的前提就是没有密码可用");
            Assert.True(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)));

            var reason = "";
            session.SignedOut += (_, why) => reason = why;

            Assert.Throws<EmbyTokenExpiredException>(() => Wait(session.ExecuteAsync(
                (client, token) => client.GetLatestAsync(null, 10, token), CancellationToken.None)));

            Assert.False(session.IsSignedIn, "会话必须真的结束，否则页面会一遍遍撞同一个 401");
            Assert.Equal("登录状态已过期，请重新登录", reason, "外壳靠这一句回登录页");
            Assert.Equal(0, transport.Count("Users/AuthenticateByName"), "没密码就不该去试登录");
        });

        Test("退出登录赶上悄悄重登：那一次登录作废，会话不许被复活", () =>
        {
            // 时间窗是「重登那一趟还没回来」，而那一刻界面线程是空着的 —— 用户按得到「退出登录」。少了这一条
            // 判断，迟到的登录成功会 Adopt 一个新 client：外壳已经回到登录页，而这个对象手上还捏着一个能用的
            // 令牌，之后每个请求都照旧通。
            var transport = new StubTransport()
                .Answer("Views", Views)
                .Answer("System/Info/Public", PublicInfo)
                .Answer("Users/AuthenticateByName", SignedIn)
                .Sequence("Items", (HttpStatusCode.Unauthorized, ""), (HttpStatusCode.OK, OneItem));

            var (session, server, account) = Signed(transport);
            account.ProtectedPassword = PassthroughSecretProtector.Instance.Protect("pw");
            account.RememberPassword = true;

            Assert.True(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)));

            var announced = 0;
            session.SignedOut += (_, _) => announced++;
            transport.When("Users/AuthenticateByName", session.SignOut);

            Assert.Throws<EmbyTokenExpiredException>(() => Wait(session.ExecuteAsync(
                (client, token) => client.GetLatestAsync(null, 10, token), CancellationToken.None)));

            Assert.False(session.IsSignedIn, "退出登录之后，迟到的登录成功不许把会话接回来");
            Assert.Equal(1, announced, "「已退出登录」只该喊一次 —— 再喊一次外壳会把登录页重建一遍");
            Assert.Equal(1, transport.Count("Items"), "这次登录作废，被 401 打断那一趟就不该重试");
        });

        Test("退出登录赶上重登、用户又已重新登录：迟到的重登作废，也不许把新会话踢下去", () =>
        {
            // 同一个时间窗再深一层：重登还在路上，用户退出登录，**紧接着又登录成功**，全赶在重登那一趟的
            // 响应回来之前。这时迟到的重登两头都不能沾 —— 装上它自己的 client 是把用户新建的会话顶掉，
            // 按老办法置空 client 顶掉的是用户那一个；照「续不上」扔异常则把人家刚建好的会话踢回登录页。
            // 它只能什么都不装，让调用方拿用户现在的 client 把原来那个请求重试一遍。
            var transport = new StubTransport()
                .Answer("Views", Views)
                .Answer("System/Info/Public", PublicInfo)
                .Answer("Users/AuthenticateByName", SignedIn)
                .Sequence("Items", (HttpStatusCode.Unauthorized, ""), (HttpStatusCode.OK, OneItem));

            var (session, server, account) = Signed(transport);
            account.ProtectedPassword = PassthroughSecretProtector.Instance.Protect("pw");
            account.RememberPassword = true;

            Assert.True(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)));

            var announced = 0;
            session.SignedOut += (_, _) => announced++;

            // When 钩子在重登的响应送达之前插手，里面同步跑完「退出登录 + 重新登录」；里面那一趟登录会
            // 再进同一个钩子，靠这面旗跳过，不然就是自己套自己。
            var once = 0;
            transport.When("Users/AuthenticateByName", () =>
            {
                if (Interlocked.Exchange(ref once, 1) == 1) return;

                session.SignOut();
                session.SignInAsync(server, account, "pw", account.Username, true, CancellationToken.None)
                    .GetAwaiter().GetResult();
            });

            var items = Wait(session.ExecuteAsync(
                (client, token) => client.GetLatestAsync(null, 10, token), CancellationToken.None));

            Assert.True(items is { Count: 1 }, "重试要落到用户新登录的那个 client 上，一次就成");
            Assert.True(session.IsSignedIn, "用户刚建立的会话不许被迟到的重登踢掉");
            Assert.True(account.HasSavedToken, "退出之后重新登录写下的令牌要还在 —— 作废的是重登那一趟，不是他");
            Assert.Equal(1, announced, "退出登录那一声照旧只喊一次");
            Assert.Equal(2, transport.Count("Items"), "401 那一趟，加上换到新 client 之后的重试");
        });

        Test("令牌中途过期：重登也被拒，就不再原地打转", () =>
        {
            var transport = new StubTransport()
                .Answer("Views", Views)
                .Answer("System/Info/Public", PublicInfo)
                .Fail("Items", HttpStatusCode.Unauthorized)
                // 密码也改过了：重登同样 401。
                .Fail("Users/AuthenticateByName", HttpStatusCode.Unauthorized);

            var (session, server, account) = Signed(transport);
            account.ProtectedPassword = PassthroughSecretProtector.Instance.Protect("旧密码");
            account.RememberPassword = true;

            Assert.True(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)));

            Assert.Throws<EmbyTokenExpiredException>(() => Wait(session.ExecuteAsync(
                (client, token) => client.GetLatestAsync(null, 10, token), CancellationToken.None)));

            Assert.False(session.IsSignedIn);
            Assert.Equal(1, transport.Count("Users/AuthenticateByName"), "试一次就够，不许重试到超时");
            Assert.Equal(1, transport.Count("Items"), "重登没成，那一趟就不该再问一遍");
        });
    }

    private static void RegisterSessionReplacement()
    {
        foreach (var timesOut in new[] { false, true })
        {
            Test($"旧重登{(timesOut ? "超时" : "被拒")}：用户已手动重登同一账号时保留新会话", () =>
            {
                var transport = new StubTransport()
                    .Answer("Views", Views)
                    .Answer("System/Info/Public", PublicInfo)
                    // When 内的手动登录先取走第一个答案，旧重登再拿第二个。
                    .Sequence("Users/AuthenticateByName", (HttpStatusCode.OK, SignedIn), (HttpStatusCode.Unauthorized, ""))
                    .Sequence("Items", (HttpStatusCode.Unauthorized, ""), (HttpStatusCode.OK, OneItem));
                var (session, server, account) = Signed(transport);
                using var lifetime = session;
                account.ProtectedPassword = PassthroughSecretProtector.Instance.Protect("pw");
                account.RememberPassword = true;
                Assert.True(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)));

                var announced = 0;
                session.SignedOut += (_, _) => announced++;
                var once = false;
                transport.When("Users/AuthenticateByName", () =>
                {
                    if (once) return;
                    once = true;
                    session.SignOut();
                    session.SignInAsync(server, account, "pw", account.Username, true, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    if (timesOut) throw new TaskCanceledException("旧重登超时");
                });

                Exception? failure = null;
                List<EmbyItem>? items = null;
                try
                {
                    items = Wait(session.ExecuteAsync(
                        (client, token) => client.GetLatestAsync(null, 10, token), CancellationToken.None));
                }
                catch (Exception error)
                {
                    failure = error;
                }

                Assert.True(session.IsSignedIn, "旧重登的失败不能注销用户刚建立的新会话");
                Assert.True(account.HasSavedToken, "新登录保存的令牌不能被旧失败清除");
                Assert.Equal(1, announced, "只通知用户主动退出的那一次");
                Assert.Null(failure, "相同服务器与账号的新连接可以接续原请求");
                Assert.True(items is { Count: 1 });
            });
        }

        foreach (var changeServer in new[] { false, true })
        foreach (var duringReauthentication in new[] { false, true })
        {
            var identity = changeServer ? "服务器" : "账号";
            var moment = duringReauthentication ? "自动重登中" : "旧请求返回 401 前";
            Test($"切换{identity}发生在{moment}：旧写操作不重试到新身份", () =>
            {
                var userId = changeServer ? "u1" : "u2";
                var replacementReply = "{\"AccessToken\":\"replacement-token\",\"User\":{\"Id\":\"" + userId + "\",\"Name\":\"replacement\"}}";
                var transport = new StubTransport()
                    .Answer("Views", Views)
                    .Answer("System/Info/Public", PublicInfo)
                    .Sequence("Users/AuthenticateByName", (HttpStatusCode.OK, replacementReply), (HttpStatusCode.OK, SignedIn))
                    .Sequence("PlayedItems", (HttpStatusCode.Unauthorized, ""), (HttpStatusCode.OK, "{}"));
                var (session, server, account) = Signed(transport);
                using var lifetime = session;
                account.ProtectedPassword = PassthroughSecretProtector.Instance.Protect("pw");
                account.RememberPassword = true;
                Assert.True(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)));

                var replacementServer = changeServer
                    ? new ServerProfile { Name = "另一台假服务器", Url = "https://replacement.example.test" }
                    : server;
                var replacementAccount = new AccountProfile { Username = "replacement", UserId = userId };
                replacementServer.Accounts.Add(replacementAccount);
                var announced = 0;
                session.SignedOut += (_, _) => announced++;
                var once = false;
                transport.When(duringReauthentication ? "Users/AuthenticateByName" : "PlayedItems", () =>
                {
                    if (once) return;
                    once = true;
                    session.SignOut();
                    session.SignInAsync(replacementServer, replacementAccount, "pw", replacementAccount.Username, true, CancellationToken.None)
                        .GetAwaiter().GetResult();
                });

                Assert.Throws<EmbyTokenExpiredException>(() => Wait(session.ExecuteAsync(
                    (client, token) => client.MarkPlayedAsync("m1", token), CancellationToken.None)));

                Assert.True(session.IsSignedIn, "旧请求作废不等于退出新会话");
                Assert.True(ReferenceEquals(replacementAccount, session.Account));
                Assert.True(replacementAccount.HasSavedToken);
                Assert.Equal(1, announced);
                Assert.Equal(1, transport.SentTo("PlayedItems").Count, "旧账号的标记观看不能发送给新账号或新服务器");
            });
        }
    }

    /// <summary>
    /// 传输层那两种「答不上来」。它们的区别只在一句话上，而那句话是用户唯一看得见的东西：请求超时说成
    /// 「操作已取消」，用户会以为是自己点了别处；而他真的取消了的时候，屏上不该冒出一句「无法连接到服务器」。
    /// </summary>
    private static void RegisterTransportFailures()
    {
        Test("请求超时：读成「连不上服务器」，不是「取消了」", () =>
        {
            // .NET 里超时就是一个 TaskCanceledException，照类型直译就成了「操作已取消」—— 而用户没取消任何东西。
            var transport = new StubTransport()
                .Answer("Views", Views)
                .Answer("System/Info/Public", PublicInfo)
                .Throw("Items", new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

            var (session, server, account) = Signed(transport);
            Assert.True(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)));

            var error = Assert.Catch<EmbyUnreachableException>(() => Wait(session.ExecuteAsync(
                (client, token) => client.GetLatestAsync(null, 10, token), CancellationToken.None)));

            Assert.Contains("超时", error.Message);
            Assert.Equal("无法连接到服务器，请检查地址和网络", Failure.Describe(error));
        });

        Test("调用方自己取消：还是「取消」，不许翻成连不上", () =>
        {
            // 翻错的代价是相反方向的：翻页翻得快、上一趟被取消，屏上却冒出一句「无法连接到服务器」。
            var transport = new StubTransport()
                .Answer("Views", Views)
                .Answer("System/Info/Public", PublicInfo)
                .Throw("Items", new TaskCanceledException());

            var (session, server, account) = Signed(transport);
            Assert.True(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            Assert.Throws<TaskCanceledException>(() => Wait(session.ExecuteAsync(
                (client, token) => client.GetLatestAsync(null, 10, token), cancelled.Token)));
        });

        Test("连不上：DNS 或者拒接说的也是「连不上」", () =>
        {
            var transport = new StubTransport()
                .Answer("Views", Views)
                .Answer("System/Info/Public", PublicInfo)
                .Throw("Items", new HttpRequestException("No such host is known."));

            var (session, server, account) = Signed(transport);
            Assert.True(Wait(session.TryRestoreAsync(server, account, CancellationToken.None)));

            var error = Assert.Catch<EmbyUnreachableException>(() => Wait(session.ExecuteAsync(
                (client, token) => client.GetLatestAsync(null, 10, token), CancellationToken.None)));

            Assert.Equal("无法连接到服务器，请检查地址和网络", Failure.Describe(error));
        });
    }

    /// <summary>
    /// 一台服务器加一个存了令牌的账号，以及挂在 <paramref name="transport"/> 上的会话。什么都不落盘：
    /// <c>AppPaths</c> 只拼字符串，那个临时目录名从来不会被建出来。
    /// </summary>
    private static (EmbySession Session, ServerProfile Server, AccountProfile Account) Signed(StubTransport transport)
    {
        var settings = new AppSettings();
        var server = new ServerProfile { Name = "我的服务器", Url = "http://192.168.31.230:8896" };
        var account = new AccountProfile
        {
            Username = "donxuelian",
            UserId = "u1",
            ProtectedAccessToken = PassthroughSecretProtector.Instance.Protect("token-1")
        };

        server.Accounts.Add(account);
        settings.Servers.Clear();
        settings.Servers.Add(server);

        var session = new EmbySession(
            settings,
            new SettingsStore(
                new AppPaths(Path.Combine(Path.GetTempPath(), $"embynian-session-{Guid.NewGuid():N}")),
                PassthroughSecretProtector.Instance),
            new CredentialVault(PassthroughSecretProtector.Instance),
            DeviceIdentity.Create("device-1", "3.0.0"),
            transport);

        return (session, server, account);
    }

    /// <summary>
    /// 等一趟请求，最多五秒。**失败时抛的是原始异常而不是 <see cref="AggregateException"/>** ——
    /// <c>Task.Wait</c> 会包一层，而这一批测试问的正是「用户会看到哪一种异常」，包起来就问不出来了。
    /// </summary>
    private static T Wait<T>(Task<T> task)
    {
        try
        {
            Assert.True(((Task)task).Wait(TimeSpan.FromSeconds(5)), "请求卡住了");
        }
        catch (AggregateException)
        {
            // 下面那句会用原始异常重抛。
        }

        return task.GetAwaiter().GetResult();
    }
}
