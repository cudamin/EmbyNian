using System.Collections.Concurrent;
using System.Net;
using System.Text;
using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

internal static class ImageIdentityTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    public static void Register()
    {
        Case("图片身份：不同服务器的相同条目标签不共用磁盘缓存", async () =>
        {
            using var fixture = new Fixture();
            await fixture.Select("https://a.invalid/media", "u");
            Assert.Equal("a.invalid/u", Text(await fixture.Get()));
            await fixture.Select("https://b.invalid/media", "u");
            Assert.Equal("b.invalid/u", Text(await fixture.Get()));
            Assert.Equal(2, fixture.Transport.Images.Count);
        });

        Case("图片身份：同服务器换账号不读上一账号的缓存", async () =>
        {
            using var fixture = new Fixture();
            await fixture.Select("https://a.invalid/media", "a");
            await fixture.Get();
            await fixture.Select("https://a.invalid/media", "b");
            Assert.Equal("a.invalid/b", Text(await fixture.Get()));
            Assert.Equal(2, fixture.Transport.Images.Count);
        });

        Case("图片身份：路径大小写和图片标签标点不会撞缓存键", async () =>
        {
            using var fixture = new Fixture();
            await fixture.Select("https://a.invalid/media", "u");
            await fixture.Get("a-b");
            await fixture.Get("ab");
            await fixture.Select("https://a.invalid/Media", "u");
            await fixture.Get("a-b");
            Assert.Equal(3, fixture.Transport.Images.Count);
        });

        Case("图片身份：同身份恢复令牌仍命中原缓存", async () =>
        {
            using var fixture = new Fixture();
            await fixture.Select("https://a.invalid/media", "u");
            await fixture.Get();
            await fixture.Select("https://A.invalid:443/media/", "u");
            Assert.Equal("a.invalid/u", Text(await fixture.Get()));
            Assert.Equal(1, fixture.Transport.Images.Count);
        });

        Case("图片身份：旧下载迟到不交给新身份或占用新身份的请求", async () =>
        {
            using var fixture = new Fixture();
            var entered = Signal();
            var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Transport.ImageReply = request =>
            {
                if (request.RequestUri!.Host != "a.invalid") return Task.FromResult(fixture.Transport.Image(request));
                entered.TrySetResult();
                return reply.Task;
            };
            await fixture.Select("https://a.invalid/media", "u");
            var old = fixture.Get();
            await entered.Task;
            await fixture.Select("https://b.invalid/media", "u");
            var current = fixture.Get();
            reply.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("old-a") });
            var cancelled = false;
            try { await old; } catch (OperationCanceledException) { cancelled = true; }
            Assert.True(cancelled, "旧身份的图片应放弃，而不是以成功结果回到新页面");
            Assert.Equal("b.invalid/u", Text(await current));
        });

        Case("图片身份：重试期间切服不拿新身份重发旧图片", async () =>
        {
            using var fixture = new Fixture();
            var entered = Signal();
            fixture.Transport.ImageReply = request =>
            {
                entered.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            };
            await fixture.Select("https://a.invalid/media", "u");
            var old = fixture.Get();
            await entered.Task;
            await fixture.Select("https://b.invalid/media", "u");
            var cancelled = false;
            try { await old; } catch (OperationCanceledException) { cancelled = true; }
            Assert.True(cancelled);
            Assert.Equal(1, fixture.Transport.Images.Count, "切服后不再重试旧图片");
        });

        Case("图片身份：章节图和海报遵循同一身份边界", async () =>
        {
            using var fixture = new Fixture();
            await fixture.Select("https://a.invalid/media", "u");
            await fixture.Store.GetChapterAsync("same", 0, null, 320, None);
            await fixture.Select("https://b.invalid/media", "u");
            Assert.Equal("b.invalid/u", Text(await fixture.Store.GetChapterAsync("same", 0, null, 320, None)));
            Assert.Equal(2, fixture.Transport.Images.Count);
        });
    }

    private static string Text(byte[]? bytes) => bytes is null ? "" : Encoding.UTF8.GetString(bytes);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Case(string name, Func<Task> run) =>
        Test(name, () => run().WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult());

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"embynian-images-{Guid.NewGuid():N}");
        private readonly AppSettings _settings = new();
        public Transport Transport { get; } = new();
        public EmbySession Session { get; }
        public EmbyImageStore Store { get; }

        public Fixture()
        {
            Session = new EmbySession(_settings, new SettingsStore(new AppPaths(_root), PassthroughSecretProtector.Instance),
                new CredentialVault(PassthroughSecretProtector.Instance), DeviceIdentity.Create("image-fixture", "test"), Transport);
            Store = new EmbyImageStore(Session, Path.Combine(_root, "images"));
        }

        public async Task Select(string address, string user)
        {
            var account = new AccountProfile
            {
                Username = user,
                UserId = user,
                ProtectedAccessToken = PassthroughSecretProtector.Instance.Protect("synthetic-" + user)
            };
            var server = new ServerProfile { Name = "fixture", Url = address };
            server.Accounts.Add(account);
            _settings.Servers.Add(server);
            Assert.True(await Session.TryRestoreAsync(server, account, None));
        }

        public Task<byte[]?> Get(string tag = "same") => Store.GetAsync("same", "Primary", tag, 320, None);

        public void Dispose()
        {
            Session.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class Transport : HttpMessageHandler
    {
        public ConcurrentQueue<string> Images { get; } = new();
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? ImageReply { get; set; }

        public HttpResponseMessage Image(HttpRequestMessage request)
        {
            var user = request.Headers.GetValues("X-Emby-Token").Single().Replace("synthetic-", "", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{request.RequestUri!.Host}/{user}") };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/Images/", StringComparison.Ordinal))
            {
                Images.Enqueue(request.RequestUri.AbsoluteUri);
                return ImageReply?.Invoke(request) ?? Task.FromResult(Image(request));
            }
            var body = request.RequestUri.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)
                ? "{\"Items\":[],\"TotalRecordCount\":0}" : "{\"ServerName\":\"fixture\",\"Version\":\"4.9\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
