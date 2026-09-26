using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.MoviePilot;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

internal static class IdentityRegressionTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    public static void Register()
    {
        RegisterPasswords();
        RegisterSessionChanges();
        RegisterScopes();
        RegisterMoviePilot();
        RegisterDownloads();
        HttpRedirectTests.Register();
    }

    private static void Case(string name, Func<Task> run) =>
        Test(name, () => run().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Fails<T>(Task task) where T : Exception
    {
        try
        {
            await task;
        }
        catch (T)
        {
            return;
        }
        throw new AssertionException($"期望 {typeof(T).Name}");
    }

    private static void RegisterPasswords()
    {
        Case("身份回归：同名账号的保存密码只按当前规范化服务器查找", async () =>
        {
            using var fixture = new SessionFixture();
            var a = fixture.A;
            var b = fixture.B;
            a.Account.Username = b.Account.Username = "shared";
            fixture.Settings.Remember(a.Server, a.Account);
            var address = b.Server.Url + "/emby/";
            var saved = EmbySignInIdentity.SavedAccount(fixture.Settings.Servers, address, " SHARED ");
            Assert.True(ReferenceEquals(b.Account, saved));
            var password = fixture.Vault.GetPassword(saved!);
            await fixture.Session.Gateway.AuthenticateAsync(EmbyServerAddress.Normalize(address), "shared", password, None);
            var sent = fixture.Transport.Sent.Single(s => s.IsAuthentication);
            Assert.Equal("b.invalid", sent.Url.Host);
            using var body = JsonDocument.Parse(sent.Body);
            Assert.Equal("password-b", body.RootElement.GetProperty("Pw").GetString());
            Assert.DoesNotContain("password-a", sent.Body);

            b.Server.Accounts.Clear();
            Assert.Null(EmbySignInIdentity.SavedAccount(fixture.Settings.Servers, address, "shared"));
        });

        Case("身份回归：无密码账号的认证正文明确为空，忽略残留密码", async () =>
        {
            using var fixture = new SessionFixture();
            var address = fixture.B.Server.Url;
            var apiBase = EmbyServerAddress.Normalize(address);
            var user = new EmbyUser { Name = "b", HasPassword = false };
            var passwordless = EmbySignInIdentity.IsPasswordless(address, apiBase, user.Name, user);
            Assert.True(passwordless);
            var password = EmbySignInIdentity.PasswordFor(address, "b", "leftover", address, "b", passwordless);
            await fixture.Session.Gateway.AuthenticateAsync(apiBase, user.Name, password, None);
            using var body = JsonDocument.Parse(fixture.Transport.Sent.Single(s => s.IsAuthentication).Body);
            Assert.Equal("", body.RootElement.GetProperty("Pw").GetString());
            Assert.False(EmbySignInIdentity.IsPasswordless(fixture.A.Server.Url, apiBase, "b", user));
            Assert.False(EmbySignInIdentity.IsPasswordless(address, apiBase, "other", user));
        });

        Test("身份回归：地址或账号变更丢弃旧来源密码，等价地址不丢", () =>
        {
            const string address = "https://a.invalid/media";
            Assert.Equal("", EmbySignInIdentity.PasswordFor("https://b.invalid/media", "a", "old", address, "a", false));
            Assert.Equal("", EmbySignInIdentity.PasswordFor(address, "b", "old", address, "a", false));
            Assert.Equal("", EmbySignInIdentity.PasswordFor("", "a", "old", address, "a", false));
            Assert.Equal("old", EmbySignInIdentity.PasswordFor("https://A.invalid:443/media/emby/", " A ", "old", address, "a", false));
            Assert.False(EmbySignInIdentity.SameAddress(address, "https://a.invalid/Media"));
        });
    }

    private static void RegisterSessionChanges()
    {
        foreach (var sameServer in new[] { false, true })
            foreach (var outcome in new[] { "success", "rejected", "timeout" })
                Case($"身份回归：A 重登 {outcome} 迟到，直接恢复{(sameServer ? "同服 B 账号" : "B 服务器")}不受影响", async () =>
                {
                    using var fixture = new SessionFixture(sameServer);
                    var entered = Signal<bool>();
                    var reply = Signal<HttpResponseMessage>();
                    fixture.Transport.Reply = (sent, _) =>
                    {
                        if (sent.IsAuthentication && sent.Username == "a")
                        {
                            entered.TrySetResult(true);
                            return reply.Task;
                        }
                        return Task.FromResult(sent.IsWrite ? Json("", HttpStatusCode.Unauthorized) : Standard(sent));
                    };
                    await fixture.Restore(fixture.A);
                    var oldToken = fixture.A.Account.ProtectedAccessToken;
                    var old = fixture.Session.ExecuteAsync((client, token) => client.MarkPlayedAsync("old-item", token), None);
                    await entered.Task;
                    await fixture.Restore(fixture.B);
                    var bToken = fixture.B.Account.ProtectedAccessToken;
                    if (outcome == "timeout") reply.SetException(new TaskCanceledException("synthetic timeout"));
                    else reply.SetResult(outcome == "success" ? SignedIn("a", "fresh-a") : Json("", HttpStatusCode.Unauthorized));
                    await Fails<EmbyTokenExpiredException>(old);

                    Assert.True(ReferenceEquals(fixture.B.Account, fixture.Session.Account));
                    Assert.Equal(bToken, fixture.B.Account.ProtectedAccessToken);
                    Assert.Equal(oldToken, fixture.A.Account.ProtectedAccessToken, "旧结果不能写入 A 的凭据");
                    Assert.Equal(fixture.B.Server.Id, fixture.Settings.LastServerId);
                    Assert.Equal(fixture.B.Account.Id, fixture.Settings.LastAccountId);
                    Assert.Equal(0, fixture.SignedOut);
                    Assert.Equal(1, fixture.Transport.Sent.Count(s => s.IsWrite));
                });

        foreach (var success in new[] { true, false })
            Case($"身份回归：B 显式登录后 A 迟到{(success ? "成功" : "失败")}不能覆盖或注销", async () =>
            {
                using var fixture = new SessionFixture();
                var entered = Signal<bool>();
                var reply = Signal<HttpResponseMessage>();
                fixture.Transport.Reply = (sent, _) =>
                {
                    if (sent.IsAuthentication && sent.Username == "a")
                    {
                        entered.TrySetResult(true);
                        return reply.Task;
                    }
                    return Task.FromResult(sent.IsWrite ? Json("", HttpStatusCode.Unauthorized) : Standard(sent));
                };
                await fixture.Restore(fixture.A);
                var old = fixture.Session.ExecuteAsync((client, token) => client.MarkPlayedAsync("old", token), None);
                await entered.Task;
                await fixture.Session.SignInAsync(fixture.B.Server, fixture.B.Account, "password-b", "b", true, None);
                reply.SetResult(success ? SignedIn("a", "late-a") : Json("", HttpStatusCode.Unauthorized));
                await Fails<EmbyTokenExpiredException>(old);
                Assert.True(ReferenceEquals(fixture.B.Account, fixture.Session.Account));
                Assert.Equal(0, fixture.SignedOut);
                Assert.Equal(1, fixture.Transport.Sent.Count(s => s.IsWrite));
            });

        Case("身份回归：B 恢复尚未完成时，A 的旧恢复失败不能撤销 B 的选择", async () =>
        {
            using var fixture = new SessionFixture();
            var reauthEntered = Signal<bool>();
            var reauthReply = Signal<HttpResponseMessage>();
            var bEntered = Signal<bool>();
            var bReply = Signal<HttpResponseMessage>();
            fixture.Transport.Reply = (sent, _) =>
            {
                if (sent.IsAuthentication)
                {
                    reauthEntered.TrySetResult(true);
                    return reauthReply.Task;
                }
                if (sent.IsViews && sent.Token == "token-b")
                {
                    bEntered.TrySetResult(true);
                    return bReply.Task;
                }
                return Task.FromResult(sent.IsWrite ? Json("", HttpStatusCode.Unauthorized) : Standard(sent));
            };
            await fixture.Restore(fixture.A);
            var old = fixture.Session.ExecuteAsync((client, token) => client.MarkPlayedAsync("old", token), None);
            await reauthEntered.Task;
            var b = fixture.Session.TryRestoreAsync(fixture.B.Server, fixture.B.Account, None);
            await bEntered.Task;
            reauthReply.SetResult(Json("", HttpStatusCode.Unauthorized));
            await Fails<EmbyTokenExpiredException>(old);
            Assert.Equal(0, fixture.SignedOut);
            bReply.SetResult(Views("b"));
            Assert.True(await b);
            Assert.True(ReferenceEquals(fixture.B.Account, fixture.Session.Account));
            Assert.Equal(1, fixture.Transport.Sent.Count(s => s.IsWrite));
        });

        foreach (var unauthorized in new[] { false, true })
            Case($"身份回归：两次令牌恢复逆序返回，旧 A {(unauthorized ? "401" : "成功")}不落地", async () =>
            {
                using var fixture = new SessionFixture();
                var entered = Signal<bool>();
                var reply = Signal<HttpResponseMessage>();
                fixture.Transport.Reply = (sent, _) =>
                {
                    if (sent.IsViews && sent.Token == "token-a")
                    {
                        entered.TrySetResult(true);
                        return reply.Task;
                    }
                    return Task.FromResult(Standard(sent));
                };
                var oldToken = fixture.A.Account.ProtectedAccessToken;
                var a = fixture.Session.TryRestoreAsync(fixture.A.Server, fixture.A.Account, None);
                await entered.Task;
                await fixture.Restore(fixture.B);
                reply.SetResult(unauthorized ? Json("", HttpStatusCode.Unauthorized) : Views("a"));
                Assert.False(await a);
                Assert.Equal(oldToken, fixture.A.Account.ProtectedAccessToken);
                Assert.Equal("b-view", fixture.Session.TakeRestoredViews()![0].Id);
                Assert.True(ReferenceEquals(fixture.B.Account, fixture.Session.Account));
                Assert.Equal(0, fixture.Transport.Sent.Count(s => s.IsAuthentication));
            });

        Case("身份回归：显式登录逆序完成，只允许最后选择保存凭据", async () =>
        {
            using var fixture = new SessionFixture();
            var entered = Signal<bool>();
            var reply = Signal<HttpResponseMessage>();
            fixture.Transport.Reply = (sent, _) =>
            {
                if (sent.IsAuthentication && sent.Username == "a")
                {
                    entered.TrySetResult(true);
                    return reply.Task;
                }
                return Task.FromResult(Standard(sent));
            };
            var oldToken = fixture.A.Account.ProtectedAccessToken;
            var a = fixture.Session.SignInAsync(fixture.A.Server, fixture.A.Account, "new-a", "a", true, None);
            await entered.Task;
            await fixture.Session.SignInAsync(fixture.B.Server, fixture.B.Account, "new-b", "b", true, None);
            reply.SetResult(SignedIn("a", "late-a"));
            await Fails<OperationCanceledException>(a);
            Assert.True(ReferenceEquals(fixture.B.Account, fixture.Session.Account));
            Assert.Equal(oldToken, fixture.A.Account.ProtectedAccessToken);
            Assert.Equal("password-a", fixture.Vault.GetPassword(fixture.A.Account));
        });

        Case("身份回归：尚无 client 时退出登录也撤销正在登录的结果", async () =>
        {
            using var fixture = new SessionFixture();
            var entered = Signal<bool>();
            var reply = Signal<HttpResponseMessage>();
            fixture.Transport.Reply = (sent, _) =>
            {
                if (!sent.IsAuthentication) return Task.FromResult(Standard(sent));
                entered.TrySetResult(true);
                return reply.Task;
            };
            var signIn = fixture.Session.SignInAsync(fixture.A.Server, fixture.A.Account, "new", "a", true, None);
            await entered.Task;
            fixture.Session.SignOut();
            reply.SetResult(SignedIn("a", "late"));
            await Fails<OperationCanceledException>(signIn);
            Assert.False(fixture.Session.IsSignedIn);
            Assert.Equal("token-a", fixture.Vault.GetAccessToken(fixture.A.Account));
        });

        Case("身份回归：并发同身份 401 合并一次登录，重试都用已核对的 client", async () =>
        {
            using var fixture = new SessionFixture();
            var entered = Signal<bool>();
            var reply = Signal<HttpResponseMessage>();
            fixture.Transport.Reply = (sent, _) =>
            {
                if (sent.IsAuthentication)
                {
                    entered.TrySetResult(true);
                    return reply.Task;
                }
                return Task.FromResult(sent.IsWrite && sent.Token == "token-a"
                    ? Json("", HttpStatusCode.Unauthorized) : Standard(sent));
            };
            await fixture.Restore(fixture.A);
            var first = fixture.Session.ExecuteAsync((client, token) => client.MarkPlayedAsync("one", token), None);
            await entered.Task;
            var second = fixture.Session.ExecuteAsync((client, token) => client.MarkPlayedAsync("two", token), None);
            reply.SetResult(SignedIn("a", "fresh-a"));
            await Task.WhenAll(first, second);
            Assert.Equal(1, fixture.Transport.Sent.Count(s => s.IsAuthentication));
            Assert.Equal(2, fixture.Transport.Sent.Count(s => s.IsWrite && s.Token == "fresh-a"));
            Assert.Equal(0, fixture.SignedOut);
        });
    }

    private static void RegisterScopes()
    {
        foreach (var sameServer in new[] { false, true })
            Case($"身份 Scope：切换{(sameServer ? "账号" : "服务器")}后仍可给 A 正确收尾", async () =>
            {
                using var fixture = new SessionFixture(sameServer);
                await fixture.Restore(fixture.A);
                var scope = fixture.Session.Capture();
                Assert.True(scope.IsCurrent);
                await fixture.Restore(fixture.B);
                Assert.False(scope.IsCurrent);
                await scope.ExecuteAsync(async (client, token) => { await client.MarkPlayedAsync("old", token); }, None);
                var connection = await scope.ExecuteAsync((client, _) => Task.FromResult(client.Connection), None);
                Assert.True(connection.IsSameIdentityAs(scope.Connection));
                var sent = fixture.Transport.Sent.Single(s => s.IsWrite);
                Assert.Equal("token-a", sent.Token);
                Assert.Contains("/Users/a/", sent.Url.AbsolutePath);
                Assert.True(ReferenceEquals(fixture.B.Account, fixture.Session.Account));
            });

        Case("身份 Scope：同身份刷新共享给已捕获 scope，快照不变且切 B 后仍用 A 新令牌", async () =>
        {
            using var fixture = new SessionFixture();
            fixture.Transport.Reply = (sent, _) => Task.FromResult(sent.IsWrite && sent.Token == "token-a"
                ? Json("", HttpStatusCode.Unauthorized) : Standard(sent));
            await fixture.Restore(fixture.A);
            var refreshing = fixture.Session.Capture();
            var waiting = fixture.Session.Capture();
            await refreshing.ExecuteAsync((client, token) => client.MarkPlayedAsync("first", token), None);
            Assert.Equal("token-a", waiting.Connection.AccessToken);
            Assert.True(waiting.IsCurrent);
            await fixture.Restore(fixture.B);
            await waiting.ExecuteAsync((client, token) => client.MarkPlayedAsync("last", token), None);
            Assert.Equal("fresh-a", fixture.Transport.Sent.Last(s => s.IsWrite).Token);
            Assert.Equal(1, fixture.Transport.Sent.Count(s => s.IsAuthentication));
        });

        Case("身份 Scope：离开 A 后 A 的 401 失败，但不能重登或注销 B", async () =>
        {
            using var fixture = new SessionFixture();
            await fixture.Restore(fixture.A);
            var scope = fixture.Session.Capture();
            await fixture.Restore(fixture.B);
            fixture.Transport.Reply = (sent, _) => Task.FromResult(sent.IsWrite
                ? Json("", HttpStatusCode.Unauthorized) : Standard(sent));
            await Fails<EmbyTokenExpiredException>(scope.ExecuteAsync((client, token) => client.MarkPlayedAsync("old", token), None));
            Assert.Equal(0, fixture.Transport.Sent.Count(s => s.IsAuthentication));
            Assert.True(ReferenceEquals(fixture.B.Account, fixture.Session.Account));
            Assert.Equal(0, fixture.SignedOut);
        });

        foreach (var dispose in new[] { false, true })
            Case($"身份 Scope：{(dispose ? "Dispose" : "SignOut")}撤销既有 scope 及在飞操作", async () =>
            {
                using var fixture = new SessionFixture();
                await fixture.Restore(fixture.A);
                var scope = fixture.Session.Capture();
                var entered = Signal<bool>();
                var active = scope.ExecuteAsync(async (_, token) =>
                {
                    entered.SetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }, None);
                await entered.Task;
                if (dispose) fixture.Session.Dispose();
                else fixture.Session.SignOut();
                await Fails<OperationCanceledException>(active);
                Assert.False(scope.IsCurrent);
                if (dispose)
                    await Fails<ObjectDisposedException>(scope.ExecuteAsync((_, _) => Task.CompletedTask, None));
                else
                {
                    await fixture.Restore(fixture.B);
                    await Fails<OperationCanceledException>(scope.ExecuteAsync((_, _) => Task.CompletedTask, None));
                }
            });
    }

    private static void RegisterMoviePilot()
    {
        Case("MoviePilot 身份：同地址切账号与改密文凭据都使 JWT 失效，等价地址继续复用", async () =>
        {
            using var fixture = new MoviePilotFixture();
            await fixture.Service.SearchAsync("first", None);
            fixture.Settings.MoviePilot.Url = "http://MP.invalid:80/";
            await fixture.Service.SearchAsync("same", None);
            Assert.Equal(1, fixture.LoginCount);
            fixture.Settings.MoviePilot.Username = "b";
            fixture.Credentials.SetPassword(fixture.Settings.MoviePilot, "password-b");
            await fixture.Service.SearchAsync("second", None);
            Assert.Equal("Bearer jwt-b-2", fixture.Transport.Sent.Last().Authorization);
            fixture.Credentials.SetPassword(fixture.Settings.MoviePilot, "changed");
            await fixture.Service.SearchAsync("changed", None);
            Assert.Equal("Bearer jwt-b-3", fixture.Transport.Sent.Last().Authorization);
            Assert.Equal(3, fixture.LoginCount);
        });

        Case("MoviePilot 身份：清空保存密码后不能继续使用缓存 JWT", async () =>
        {
            using var fixture = new MoviePilotFixture();
            await fixture.Service.SearchAsync("first", None);
            fixture.Credentials.ClearPassword(fixture.Settings.MoviePilot);
            var count = fixture.Transport.Sent.Count;
            await Fails<MoviePilotException>(fixture.Service.SearchAsync("second", None));
            Assert.Equal(count, fixture.Transport.Sent.Count);
        });

        foreach (var change in new[] { "username", "password", "address" })
            Case($"MoviePilot 身份：登录期间修改 {change}，旧结果不缓存、不执行原操作", async () =>
            {
                using var fixture = new MoviePilotFixture();
                var entered = Signal<bool>();
                var reply = Signal<HttpResponseMessage>();
                var first = true;
                fixture.Transport.Reply = (sent, _) =>
                {
                    if (sent.IsMoviePilotLogin && first)
                    {
                        first = false;
                        entered.SetResult(true);
                        return reply.Task;
                    }
                    return Task.FromResult(fixture.Answer(sent));
                };
                var old = fixture.Service.SearchAsync("old", None);
                await entered.Task;
                if (change == "username") fixture.Settings.MoviePilot.Username = "b";
                else if (change == "password") fixture.Credentials.SetPassword(fixture.Settings.MoviePilot, "new-password");
                else fixture.Settings.MoviePilot.Url = "http://new-mp.invalid";
                var fresh = fixture.Service.SearchAsync("new", None);
                reply.SetResult(Json("{\"access_token\":\"old-jwt\",\"user_name\":\"a\"}"));
                await Fails<OperationCanceledException>(old);
                await fresh;
                Assert.Equal(1, fixture.Transport.Sent.Count(s => s.Url.AbsolutePath.Contains("media/search", StringComparison.Ordinal)));
                Assert.False(fixture.Transport.Sent.Any(s => s.Authorization == "Bearer old-jwt"));
            });

        Case("MoviePilot 身份：A 迟到 401 不清除 B 缓存，也不把 A 订阅重放到 B", async () =>
        {
            using var fixture = new MoviePilotFixture();
            var entered = Signal<bool>();
            var reply = Signal<HttpResponseMessage>();
            fixture.Transport.Reply = (sent, _) =>
            {
                if (sent.Url.AbsolutePath.Contains("subscribe", StringComparison.Ordinal))
                {
                    entered.SetResult(true);
                    return reply.Task;
                }
                return Task.FromResult(fixture.Answer(sent));
            };
            var media = new MoviePilotMedia { Title = "synthetic", MediaId = "1", MediaSource = "themoviedb" };
            var old = fixture.Service.SubscribeAsync(media, None);
            await entered.Task;
            fixture.Settings.MoviePilot.Username = "b";
            fixture.Credentials.SetPassword(fixture.Settings.MoviePilot, "password-b");
            await fixture.Service.SearchAsync("b", None);
            reply.SetResult(Json("", HttpStatusCode.Unauthorized));
            await Fails<OperationCanceledException>(old);
            await fixture.Service.SearchAsync("b-again", None);
            Assert.Equal(2, fixture.LoginCount);
            Assert.Equal(1, fixture.Transport.Sent.Count(s => s.Url.AbsolutePath.Contains("subscribe", StringComparison.Ordinal)));
            Assert.Equal("Bearer jwt-b-2", fixture.Transport.Sent.Last().Authorization);
        });

        Case("MoviePilot 身份：同身份迟到 401 不清除已经刷新过的 JWT", async () =>
        {
            using var fixture = new MoviePilotFixture();
            await fixture.Service.SearchAsync("warm", None);
            var firstEntered = Signal<bool>();
            var secondEntered = Signal<bool>();
            var firstReply = Signal<HttpResponseMessage>();
            var secondReply = Signal<HttpResponseMessage>();
            fixture.Transport.Reply = (sent, _) =>
            {
                if (sent.Authorization == "Bearer jwt-a-1")
                {
                    if (sent.Url.Query.Contains("first", StringComparison.Ordinal))
                    {
                        firstEntered.SetResult(true);
                        return firstReply.Task;
                    }
                    secondEntered.SetResult(true);
                    return secondReply.Task;
                }
                return Task.FromResult(fixture.Answer(sent));
            };
            var first = fixture.Service.SearchAsync("first", None);
            var second = fixture.Service.SearchAsync("second", None);
            await Task.WhenAll(firstEntered.Task, secondEntered.Task);
            firstReply.SetResult(Json("", HttpStatusCode.Unauthorized));
            await first;
            secondReply.SetResult(Json("", HttpStatusCode.Unauthorized));
            await second;
            Assert.Equal(2, fixture.LoginCount);
            Assert.Equal("Bearer jwt-a-2", fixture.Transport.Sent.Last().Authorization);
        });
    }

    private static void RegisterDownloads()
    {
        Case("下载身份：队首挂起时切 B，旧队列不请求 B、也不覆盖已有目标", async () =>
        {
            using var fixture = new SessionFixture();
            using var gate = new SemaphoreSlim(1, 1);
            var entered = Signal<bool>();
            var reply = Signal<HttpResponseMessage>();
            fixture.Transport.Reply = (sent, _) =>
            {
                if (sent.Url.AbsolutePath.Contains("/Download", StringComparison.Ordinal))
                {
                    entered.TrySetResult(true);
                    return reply.Task;
                }
                return Task.FromResult(Standard(sent));
            };
            await fixture.Restore(fixture.A);
            var existing = Path.Combine(fixture.Root, "second.mkv");
            await File.WriteAllTextAsync(existing, "existing-file");
            var first = QueuedDownload(fixture.Session.Capture(), gate, "first", Path.Combine(fixture.Root, "first.mkv"));
            await entered.Task;
            var second = QueuedDownload(fixture.Session.Capture(), gate, "second", existing);
            await fixture.Restore(fixture.B);
            reply.SetResult(Json("synthetic-file"));
            await first;
            await Fails<OperationCanceledException>(second);
            Assert.Equal("existing-file", await File.ReadAllTextAsync(existing));
            Assert.Equal(1, fixture.Transport.Sent.Count(s => s.Url.AbsolutePath.Contains("/Download", StringComparison.Ordinal)));
            Assert.Equal("a.invalid", fixture.Transport.Sent.Last(s => s.Url.AbsolutePath.Contains("/Download", StringComparison.Ordinal)).Url.Host);
        });

        Case("下载身份：普通导航无身份变化，同一 scope 可顺序下载多个文件", async () =>
        {
            using var fixture = new SessionFixture();
            using var gate = new SemaphoreSlim(1, 1);
            await fixture.Restore(fixture.A);
            var scope = fixture.Session.Capture();
            using (var navigation = new LoadGeneration())
            {
                navigation.Begin();
                navigation.Cancel();
            }
            await QueuedDownload(scope, gate, "one", Path.Combine(fixture.Root, "one.mkv"));
            await QueuedDownload(scope, gate, "two", Path.Combine(fixture.Root, "two.mkv"));
            Assert.True(scope.IsCurrent);
            Assert.Equal(2, fixture.Transport.Sent.Count(s => s.Url.AbsolutePath.Contains("/Download", StringComparison.Ordinal)));
        });
    }

    private static async Task QueuedDownload(EmbySessionScope scope, SemaphoreSlim gate, string itemId, string path)
    {
        await gate.WaitAsync();
        try
        {
            scope.ThrowIfNotCurrent();
            await scope.ExecuteAsync((client, token) => client.DownloadToFileAsync(itemId, path, null, token), None);
        }
        finally
        {
            gate.Release();
        }
    }

    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage SignedIn(string user, string token) =>
        Json(JsonSerializer.Serialize(new { AccessToken = token, User = new { Id = user, Name = user } }));

    private static HttpResponseMessage Views(string user) =>
        Json(JsonSerializer.Serialize(new { Items = new[] { new { Id = user + "-view", Name = user } } }));

    private static HttpResponseMessage Standard(Sent sent)
    {
        if (sent.IsAuthentication) return SignedIn(sent.Username, "fresh-" + sent.Username);
        if (sent.IsViews) return Views(sent.Token.EndsWith('b') ? "b" : "a");
        if (sent.Url.AbsolutePath.EndsWith("System/Info/Public", StringComparison.Ordinal)) return Json("{\"ServerName\":\"synthetic\"}");
        return Json("{}");
    }

    private sealed record Sent(Uri Url, string Method, string Body, string Token, string Authorization)
    {
        public bool IsViews => Url.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal);
        public bool IsWrite => Url.AbsolutePath.Contains("/PlayedItems/", StringComparison.Ordinal);
        public bool IsAuthentication => Url.AbsolutePath.EndsWith("/Users/AuthenticateByName", StringComparison.Ordinal);
        public bool IsMoviePilotLogin => Url.AbsolutePath.EndsWith("login/access-token", StringComparison.Ordinal);
        public string Username
        {
            get
            {
                if (IsMoviePilotLogin)
                    return Uri.UnescapeDataString(Body.Split('&').Single(pair => pair.StartsWith("username=", StringComparison.Ordinal))[9..]);
                using var document = JsonDocument.Parse(Body);
                return document.RootElement.GetProperty("UserName").GetString()!;
            }
        }
    }

    private sealed class Transport : HttpMessageHandler
    {
        public ConcurrentQueue<Sent> Sent { get; } = new();
        public Func<Sent, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } =
            (sent, _) => Task.FromResult(Standard(sent));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var sent = new Sent(request.RequestUri!, request.Method.Method, body,
                request.Headers.TryGetValues("X-Emby-Token", out var token) ? token.Single() : "",
                request.Headers.TryGetValues("Authorization", out var authorization) ? authorization.Single() : "");
            Sent.Enqueue(sent);
            return await Reply(sent, cancellationToken);
        }
    }

    private sealed record Profile(ServerProfile Server, AccountProfile Account);

    private sealed class SessionFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "identity-fixtures", Guid.NewGuid().ToString("N"));
        public Transport Transport { get; } = new();
        public AppSettings Settings { get; } = new();
        public CredentialVault Vault { get; } = new(PassthroughSecretProtector.Instance);
        public EmbySession Session { get; }
        public Profile A { get; }
        public Profile B { get; }
        public int SignedOut { get; private set; }

        public SessionFixture(bool sameServer = false)
        {
            Directory.CreateDirectory(Root);
            A = Add("a", "https://a.invalid");
            B = Add("b", sameServer ? A.Server.Url : "https://b.invalid", sameServer ? A.Server : null);
            Session = new EmbySession(Settings, new SettingsStore(new AppPaths(Root), PassthroughSecretProtector.Instance),
                Vault, DeviceIdentity.Create("identity-fixture", "1"), Transport);
            Session.SignedOut += (_, _) => SignedOut++;
        }

        private Profile Add(string user, string address, ServerProfile? server = null)
        {
            if (server is null)
            {
                server = new ServerProfile { Name = user, Url = address };
                Settings.Servers.Add(server);
            }
            var account = new AccountProfile { Username = user, UserId = user };
            Vault.SetAccessToken(account, "token-" + user);
            Vault.SetPassword(account, "password-" + user, true);
            server.Accounts.Add(account);
            return new Profile(server, account);
        }

        public async Task Restore(Profile profile) =>
            Assert.True(await Session.TryRestoreAsync(profile.Server, profile.Account, None));

        public void Dispose()
        {
            Session.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class MoviePilotFixture : IDisposable
    {
        public Transport Transport { get; } = new();
        public AppSettings Settings { get; } = new();
        public MoviePilotCredentials Credentials { get; } = new(PassthroughSecretProtector.Instance);
        public MoviePilotService Service { get; }
        private readonly MoviePilotClient _client;
        public int LoginCount { get; private set; }

        public MoviePilotFixture()
        {
            Settings.MoviePilot.Enabled = true;
            Settings.MoviePilot.Url = "http://mp.invalid";
            Settings.MoviePilot.Username = "a";
            Credentials.SetPassword(Settings.MoviePilot, "password-a");
            Transport.Reply = (sent, _) => Task.FromResult(Answer(sent));
            _client = new MoviePilotClient(Transport);
            Service = new MoviePilotService(_client, Credentials, Settings);
        }

        public HttpResponseMessage Answer(Sent sent)
        {
            if (!sent.IsMoviePilotLogin) return Json("[]");
            LoginCount++;
            return Json(JsonSerializer.Serialize(new { access_token = $"jwt-{sent.Username}-{LoginCount}", user_name = sent.Username }));
        }

        public void Dispose() => _client.Dispose();
    }
}
