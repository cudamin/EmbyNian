using System.Net;
using System.Text;
using EmbyNian.Emby;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

internal static class HttpRedirectTests
{
    private static readonly Uri Origin = new("https://emby.invalid/emby/resource");
    private static readonly EmbyHttp.RequestContext Context = new(DeviceIdentity.Create("redirect-fixture", "1"), "placeholder-token");

    public static void Register()
    {
        foreach (var status in new[] { 301, 302, 303, 307, 308 })
        {
            Case($"HTTP 跳转：同源 {status} 保留认证并释放每一跳", async () =>
            {
                using var handler = new RedirectTransport();
                handler.Reply = (request, index, _) => Task.FromResult(index == 0
                    ? Redirect(status, "/emby/next") : Ok("answer"));
                using var http = new EmbyHttp(handler);
                Assert.Equal("answer", Encoding.UTF8.GetString(await http.GetBytesAsync(Origin, Context, CancellationToken.None)));
                Assert.Equal(2, handler.Sent.Count);
                Assert.True(handler.Sent.All(sent => sent.Token == "placeholder-token" && sent.EmbyAuthorization.Length > 0));
                Assert.Equal("https://emby.invalid/emby/next", handler.Sent[1].Url.AbsoluteUri);
                handler.AssertDisposed();
            });

            Case($"HTTP 跳转：跨源 {status} GET 不带认证，回跳原站也不恢复认证", async () =>
            {
                using var handler = new RedirectTransport();
                handler.Reply = (_, index, _) => Task.FromResult(index switch
                {
                    0 => Redirect(status, "https://cdn.invalid/file"),
                    1 => Redirect(status, "https://emby.invalid/back"),
                    _ => Ok("answer")
                });
                using var http = new EmbyHttp(handler);
                await http.GetBytesAsync(Origin, Context, CancellationToken.None);
                Assert.Equal(3, handler.Sent.Count);
                Assert.Equal("placeholder-token", handler.Sent[0].Token);
                Assert.True(handler.Sent.Skip(1).All(sent => sent.Token.Length == 0 && sent.EmbyAuthorization.Length == 0
                    && sent.Authorization.Length == 0 && sent.Cookie.Length == 0 && sent.Body.Length == 0));
                handler.AssertDisposed();
            });

            foreach (var authentication in new[] { false, true })
                Case($"HTTP 跳转：跨源 {status} {(authentication ? "登录" : "写操作")}不重放正文", async () =>
                {
                    using var handler = new RedirectTransport();
                    handler.Reply = (_, _, _) => Task.FromResult(Redirect(status, "https://other.invalid/login"));
                    using var http = new EmbyHttp(handler);
                    var context = Context with { IsAuthenticationAttempt = authentication };
                    await Fails<EmbyApiException>(http.PostAsync(Origin, new { Pw = "synthetic-password" }, context, CancellationToken.None));
                    Assert.Equal(1, handler.Sent.Count);
                    handler.AssertDisposed();
                });
        }

        foreach (var location in new[] { "https://emby.invalid:444/file", "https://other.invalid/file", "http://other.invalid/file" })
            Case($"HTTP 跳转：来源核对 {location} 不泄漏头", async () =>
            {
                using var handler = new RedirectTransport();
                handler.Reply = (_, index, _) => Task.FromResult(index == 0 ? Redirect(302, location) : Ok("answer"));
                using var http = new EmbyHttp(handler);
                if (location.StartsWith("http:", StringComparison.Ordinal))
                {
                    await Fails<EmbyApiException>(http.GetBytesAsync(Origin, Context, CancellationToken.None));
                    Assert.Equal(1, handler.Sent.Count);
                }
                else
                {
                    await http.GetBytesAsync(Origin, Context, CancellationToken.None);
                    Assert.Equal("", handler.Sent[1].Token);
                    Assert.Equal("", handler.Sent[1].EmbyAuthorization);
                }
                handler.AssertDisposed();
            });

        foreach (var status in new[] { 307, 308 })
            Case($"HTTP 跳转：同源 {status} 正文按原方法和长度重发，释放请求正文", async () =>
            {
                using var handler = new RedirectTransport();
                handler.Reply = (_, index, _) => Task.FromResult(index == 0 ? Redirect(status, "./login") : Ok("{}"));
                using var http = new EmbyHttp(handler);
                await http.PostAsync(Origin, new { Pw = "synthetic-password" }, Context with { IsAuthenticationAttempt = true }, CancellationToken.None);
                Assert.Equal(2, handler.Sent.Count);
                Assert.True(handler.Sent.All(s => s.Method == "POST" && s.ContentLength == Encoding.UTF8.GetByteCount(s.Body)));
                Assert.Equal(handler.Sent[0].Body, handler.Sent[1].Body);
                Assert.Equal(handler.Sent[0].Token, handler.Sent[1].Token);
                handler.AssertDisposed();
            });

        foreach (var status in new[] { 301, 302, 303 })
            Case($"HTTP 跳转：{status} 不能把同源 POST 偷换成 GET", async () =>
            {
                using var handler = new RedirectTransport();
                handler.Reply = (_, _, _) => Task.FromResult(Redirect(status, "/emby/new"));
                using var http = new EmbyHttp(handler);
                await Fails<EmbyApiException>(http.PostAsync(Origin, new { value = "write" }, Context, CancellationToken.None));
                Assert.Equal(1, handler.Sent.Count);
                handler.AssertDisposed();
            });

        Case("HTTP 跳转：主机大小写与默认端口等价，不误删同源认证", async () =>
        {
            using var handler = new RedirectTransport();
            handler.Reply = (_, index, _) => Task.FromResult(index == 0
                ? Redirect(302, "https://EMBY.invalid:443/next") : Ok("answer"));
            using var http = new EmbyHttp(handler);
            await http.GetBytesAsync(Origin, Context, CancellationToken.None);
            Assert.Equal("placeholder-token", handler.Sent[1].Token);
            Assert.True(EmbyHttpRedirect.SameOrigin(new Uri("http://EMBY.invalid:80/"), new Uri("http://emby.invalid/next")));
            handler.AssertDisposed();
        });

        foreach (var authentication in new[] { false, true })
            Case($"HTTP 跳转：HTTP 升级 HTTPS 也不能跨源重放{(authentication ? "登录" : "写入")}正文", async () =>
            {
                using var handler = new RedirectTransport();
                handler.Reply = (_, _, _) => Task.FromResult(Redirect(307, "https://emby.invalid/emby/login"));
                using var http = new EmbyHttp(handler);
                await Fails<EmbyApiException>(http.PostAsync(new Uri("http://emby.invalid/emby/login"),
                    new { Pw = "synthetic" }, Context with { IsAuthenticationAttempt = authentication }, CancellationToken.None));
                Assert.Equal(1, handler.Sent.Count);
                handler.AssertDisposed();
            });

        Case("HTTP 跳转：上传字节同源 308 保持正文，两个请求各自释放内容", async () =>
        {
            using var handler = new RedirectTransport();
            handler.Reply = (_, index, _) => Task.FromResult(index == 0 ? Redirect(308, "/emby/upload") : Ok("{}"));
            using var http = new EmbyHttp(handler);
            var bytes = Encoding.UTF8.GetBytes("synthetic-image");
            await http.PostBytesAsync(Origin, bytes, "image/png", Context, CancellationToken.None);
            Assert.Equal("synthetic-image", handler.Sent[0].Body);
            Assert.Equal(handler.Sent[0].Body, handler.Sent[1].Body);
            Assert.Equal((long)bytes.Length, handler.Sent[1].ContentLength);
            Assert.Equal("synthetic-image", Encoding.UTF8.GetString(bytes));
            handler.AssertDisposed();
        });

        Test("HTTP 跳转：HEAD 是可跨源资源，认证 HEAD 与 DELETE 仍拒绝", () =>
        {
            var next = new Uri("https://cdn.invalid/file");
            Assert.Equal(next, EmbyHttpRedirect.Next(Origin, next, HttpMethod.Head, HttpStatusCode.Found, false));
            Assert.Throws<EmbyApiException>(() => EmbyHttpRedirect.Next(Origin, next, HttpMethod.Head, HttpStatusCode.Found, true));
            Assert.Throws<EmbyApiException>(() => EmbyHttpRedirect.Next(Origin, next, HttpMethod.Delete, HttpStatusCode.TemporaryRedirect, false));
        });

        foreach (var location in new string?[] { null, "file:///C:/private.txt", "ftp://other.invalid/file", "https://user:password@other.invalid/file" })
            Case($"HTTP 跳转：拒绝无效目标 {location ?? "missing-location"}", async () =>
            {
                using var handler = new RedirectTransport();
                handler.Reply = (_, _, _) => Task.FromResult(Redirect(302, location));
                using var http = new EmbyHttp(handler);
                await Fails<EmbyApiException>(http.GetBytesAsync(Origin, Context, CancellationToken.None));
                Assert.Equal(1, handler.Sent.Count);
                handler.AssertDisposed();
            });

        Case("HTTP 跳转：循环最多五次，所有响应都释放", async () =>
        {
            using var handler = new RedirectTransport();
            handler.Reply = (_, _, _) => Task.FromResult(Redirect(302, "/emby/resource"));
            using var http = new EmbyHttp(handler);
            await Fails<EmbyApiException>(http.GetBytesAsync(Origin, Context, CancellationToken.None));
            Assert.Equal(EmbyHttpRedirect.Limit + 1, handler.Sent.Count);
            handler.AssertDisposed();
        });

        Case("HTTP 跳转：跨源资源 401 不误报为 Emby 令牌过期", async () =>
        {
            using var handler = new RedirectTransport();
            handler.Reply = (_, index, _) => Task.FromResult(index == 0
                ? Redirect(302, "https://cdn.invalid/file") : Reply(HttpStatusCode.Unauthorized, "denied"));
            using var http = new EmbyHttp(handler);
            try
            {
                await http.GetBytesAsync(Origin, Context, CancellationToken.None);
                throw new AssertionException("应拒绝资源 401");
            }
            catch (EmbyApiException error)
            {
                Assert.False(error is EmbyTokenExpiredException);
            }
            handler.AssertDisposed();
        });

        Case("HTTP 跳转：正文截止时间覆盖最终响应而不只覆盖响应头", async () =>
        {
            using var handler = new RedirectTransport();
            var stream = new BlockedStream();
            handler.Reply = (_, index, _) => Task.FromResult(index == 0
                ? Redirect(302, "/emby/slow") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
            using var http = new EmbyHttp(handler, TimeSpan.FromMilliseconds(500));
            await Fails<EmbyUnreachableException>(http.GetBytesAsync(Origin, Context, CancellationToken.None));
            Assert.True(stream.ReadStarted.Task.IsCompleted);
            Assert.True(stream.Disposed);
            handler.AssertDisposed();
        });

        foreach (var truncated in new[] { false, true })
            Case($"HTTP 下载跳转：{(truncated ? "长度不匹配" : "取消正文")}不覆盖旧文件、清理 part 与响应", async () =>
            {
                using var handler = new RedirectTransport();
                var root = Path.Combine(AppContext.BaseDirectory, "redirect-fixtures", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    var path = Path.Combine(root, "movie.mkv");
                    await File.WriteAllTextAsync(path, "original");
                    var blocked = new BlockedStream();
                    handler.Reply = (_, index, _) =>
                    {
                        if (index == 0) return Task.FromResult(Redirect(307, "https://cdn.invalid/file"));
                        HttpContent content = truncated ? new StreamContent(new MemoryStream([1, 2, 3])) : new StreamContent(blocked);
                        if (truncated) content.Headers.ContentLength = 10;
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                    };
                    using var http = new EmbyHttp(handler);
                    using var cancellation = new CancellationTokenSource();
                    var download = http.DownloadToFileAsync(Origin, path, Context, null, cancellation.Token);
                    if (truncated) await Fails<IOException>(download);
                    else
                    {
                        await blocked.ReadStarted.Task;
                        cancellation.Cancel();
                        await Fails<OperationCanceledException>(download);
                        Assert.True(blocked.Disposed);
                    }
                    Assert.Equal("original", await File.ReadAllTextAsync(path));
                    Assert.False(File.Exists(path + ".part"));
                    Assert.Equal("", handler.Sent[1].Token);
                    handler.AssertDisposed();
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });

        Test("HTTP 跳转：实际 SocketsHttpHandler 不允许自动重定向或共享认证 Cookie", () =>
        {
            var handler = new SocketsHttpHandler();
            using var http = new EmbyHttp(handler);
            Assert.False(handler.AllowAutoRedirect);
            Assert.False(handler.UseCookies);
        });
    }

    private static void Case(string name, Func<Task> run) =>
        Test(name, () => run().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());

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

    private static HttpResponseMessage Redirect(int status, string? location)
    {
        var response = Reply((HttpStatusCode)status, "redirect");
        if (location is not null) response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpResponseMessage Ok(string body) => Reply(HttpStatusCode.OK, body);

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Sent(Uri Url, string Method, string Body, string Token, string EmbyAuthorization, string Authorization, string Cookie, long? ContentLength);

    private sealed class RedirectTransport : HttpMessageHandler
    {
        public List<Sent> Sent { get; } = [];
        private readonly List<HttpRequestMessage> _requests = [];
        private readonly List<HttpResponseMessage> _responses = [];
        public Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } =
            (_, _, _) => Task.FromResult(Ok("{}"));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string Header(string name) => request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";
            var index = Sent.Count;
            Sent.Add(new Sent(request.RequestUri!, request.Method.Method, body, Header("X-Emby-Token"),
                Header("X-Emby-Authorization"), Header("Authorization"), Header("Cookie"), request.Content?.Headers.ContentLength));
            _requests.Add(request);
            var response = await Reply(request, index, cancellationToken);
            _responses.Add(response);
            return response;
        }

        public void AssertDisposed()
        {
            foreach (var response in _responses)
                Assert.Throws<ObjectDisposedException>(() => response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            foreach (var request in _requests)
            {
                Assert.Throws<ObjectDisposedException>(() => request.Method = HttpMethod.Head);
                if (request.Content is { } content)
                    Assert.Throws<ObjectDisposedException>(() => content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            }
        }
    }

    private sealed class BlockedStream : Stream
    {
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
