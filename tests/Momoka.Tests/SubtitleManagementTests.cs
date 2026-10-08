using System.Net;
using System.Text;
using Momoka.Configuration;
using Momoka.Emby;
using Momoka.Infrastructure;
using static Momoka.Tests.TestHarness;

namespace Momoka.Tests;

internal static class SubtitleManagementTests
{
    public static void Register()
    {
        Test("字幕管理：切服后不把旧文件的搜索、下载和删除发给新身份", () => Run(async () =>
        {
            using var fixture = new Fixture();
            await fixture.SignIn("a");
            var operations = new ServerSubtitles(fixture.Session.Capture(), fixture.Item, fixture.Source);
            await fixture.SignIn("b");
            var count = fixture.Transport.Requests.Count;
            await Cancelled(() => operations.SearchAsync("chi"));
            await Cancelled(() => operations.DownloadAsync(new RemoteSubtitleInfo { Id = "candidate" }));
            await Cancelled(() => operations.DeleteAsync(fixture.Track));
            await Cancelled(() => operations.ReloadAsync());
            Assert.Equal(count, fixture.Transport.Requests.Count);
        }));

        Test("字幕管理：注销后旧对话框不能删除", () => Run(async () =>
        {
            using var fixture = new Fixture();
            await fixture.SignIn("a");
            var operations = new ServerSubtitles(fixture.Session.Capture(), fixture.Item, fixture.Source);
            fixture.Session.SignOut();
            var count = fixture.Transport.Requests.Count;
            await Cancelled(() => operations.DeleteAsync(fixture.Track));
            Assert.Equal(count, fixture.Transport.Requests.Count);
        }));

        Test("字幕管理：搜索途中切服，迟到结果不交回旧面板", () => Run(async () =>
        {
            using var fixture = new Fixture();
            await fixture.SignIn("a");
            var operations = new ServerSubtitles(fixture.Session.Capture(), fixture.Item, fixture.Source);
            var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Transport.SearchReply = reply.Task;
            var search = operations.SearchAsync("chi");
            await fixture.SignIn("b");
            reply.SetResult(Json("[]"));
            await Cancelled(() => search);
            Assert.True(fixture.Transport.Requests.Where(request => request.Path.Contains("RemoteSearch", StringComparison.Ordinal)).All(request => request.Host == "a.invalid"));
        }));

        Test("字幕管理：删除前重新核对轨号，重编号后不删除相邻文件", () => Run(async () =>
        {
            using var fixture = new Fixture();
            await fixture.SignIn("a");
            var operations = new ServerSubtitles(fixture.Session.Capture(), fixture.Item, fixture.Source);
            fixture.Transport.ItemJson = ItemJson("另一条字幕");
            await Rejected(() => operations.DeleteAsync(fixture.Track));
            Assert.False(fixture.Transport.Requests.Any(request => request.Method == "DELETE"));
        }));

        Test("字幕管理：仅允许本文件的外挂字幕删除，并固定操作条目", () => Run(async () =>
        {
            using var fixture = new Fixture();
            await fixture.SignIn("a");
            var operations = new ServerSubtitles(fixture.Session.Capture(), fixture.Item, fixture.Source);
            var count = fixture.Transport.Requests.Count;
            await Rejected(() => operations.DeleteAsync(new MediaStream { Index = 3, Type = "Subtitle", IsExternal = true, Title = "离线字幕" }));
            await Rejected(() => operations.DeleteAsync(new MediaStream { Index = 0, Type = "Subtitle" }));
            await Rejected(() => operations.DownloadAsync(new RemoteSubtitleInfo()));
            Assert.Equal(count, fixture.Transport.Requests.Count);
            await operations.DeleteAsync(fixture.Track);
            var deletion = fixture.Transport.Requests.Single(request => request.Method == "DELETE");
            Assert.Equal("/emby/Items/fixture/Subtitles/3", deletion.Path);
            Assert.Equal("a.invalid", deletion.Host);
        }));

        Test("字幕管理：重新读取后只接受新轨道快照", () => Run(async () =>
        {
            using var fixture = new Fixture();
            await fixture.SignIn("a");
            var operations = new ServerSubtitles(fixture.Session.Capture(), fixture.Item, fixture.Source);
            var current = await operations.ReloadAsync();
            Assert.Equal(1, current.SubtitleStreams.Count());
            await Rejected(() => operations.DeleteAsync(fixture.Track));
            await operations.DeleteAsync(current.SubtitleStreams.Single());
            Assert.Equal(1, fixture.Transport.Requests.Count(request => request.Method == "DELETE"));
        }));

        Test("字幕管理：同标签字幕重编号后拒绝删除，不把另一文件当原文件", () => Run(async () =>
        {
            using var fixture = new Fixture();
            await fixture.SignIn("a");
            fixture.Source.MediaStreams.Add(new MediaStream
            { Index = 4, Type = "Subtitle", IsExternal = true, Codec = "srt", Title = "离线字幕" });
            var operations = new ServerSubtitles(fixture.Session.Capture(), fixture.Item, fixture.Source);
            await Rejected(() => operations.DeleteAsync(fixture.Track));
            Assert.False(fixture.Transport.Requests.Any(request => request.Method == "DELETE"));
        }));

        Test("字幕管理：其他字幕变化也要求重读确认，不继续使用旧索引快照", () => Run(async () =>
        {
            using var fixture = new Fixture();
            await fixture.SignIn("a");
            fixture.Source.MediaStreams.Add(new MediaStream
            { Index = 4, Type = "Subtitle", IsExternal = true, Codec = "srt", Title = "另一字幕" });
            var operations = new ServerSubtitles(fixture.Session.Capture(), fixture.Item, fixture.Source);
            await Rejected(() => operations.DeleteAsync(fixture.Track));
            Assert.False(fixture.Transport.Requests.Any(request => request.Method == "DELETE"));
        }));

        Test("字幕管理：媒体源消失不退回另一版同号字幕", () => Run(async () =>
        {
            using var fixture = new Fixture();
            await fixture.SignIn("a");
            var operations = new ServerSubtitles(fixture.Session.Capture(), fixture.Item, fixture.Source);
            fixture.Transport.ItemJson = ItemJson("离线字幕").Replace("source", "other", StringComparison.Ordinal);
            await Rejected(() => operations.ReloadAsync());
            Assert.False(fixture.Transport.Requests.Any(request => request.Method == "DELETE"));
        }));
    }

    private static void Run(Func<Task> work) => work().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

    private static async Task Cancelled(Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("旧身份操作没有取消");
    }

    private static async Task Rejected(Func<Task> work)
    {
        try { await work(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("过期或不属于文件的操作没有被拒绝");
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private static string ItemJson(string title) => """
        { "Id": "fixture", "MediaSources": [{ "Id": "source", "Name": "synthetic.mkv", "MediaStreams": [
          { "Index": 3, "Type": "Subtitle", "IsExternal": true, "Codec": "srt", "Title": "$TITLE" }
        ] }] }
        """.Replace("$TITLE", title, StringComparison.Ordinal);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "momoka-subtitle-" + Guid.NewGuid().ToString("N"));
        private readonly CredentialVault _vault = new(PassthroughSecretProtector.Instance);
        internal readonly Transport Transport = new();
        internal readonly AppSettings Settings = new();
        internal readonly EmbySession Session;
        internal readonly MediaStream Track = new() { Index = 3, Type = "Subtitle", IsExternal = true, Codec = "srt", Title = "离线字幕" };
        internal readonly MediaSource Source;
        internal readonly EmbyItem Item;

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Source = new MediaSource { Id = "source", Name = "synthetic.mkv", MediaStreams = [Track] };
            Item = new EmbyItem { Id = "fixture", MediaSources = [Source] };
            Session = new EmbySession(Settings, new SettingsStore(new AppPaths(_root), PassthroughSecretProtector.Instance),
                _vault, DeviceIdentity.Create("subtitle-fixture", "1"), Transport);
        }

        internal async Task SignIn(string name)
        {
            var server = new ServerProfile { Url = $"https://{name}.invalid/emby", Name = name };
            var account = new AccountProfile { UserId = name, Username = name };
            _vault.SetAccessToken(account, "fixture-token");
            server.Accounts.Add(account);
            Settings.Servers.Add(server);
            Assert.True(await Session.TryRestoreAsync(server, account, CancellationToken.None));
        }

        public void Dispose()
        {
            Session.Dispose();
            Directory.Delete(_root, true);
        }
    }

    private sealed record Request(string Host, string Path, string Method);

    private sealed class Transport : HttpMessageHandler
    {
        internal readonly List<Request> Requests = [];
        internal string ItemJson = SubtitleManagementTests.ItemJson("离线字幕");
        internal Task<HttpResponseMessage>? SearchReply;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(new Request(uri.Host, uri.AbsolutePath, request.Method.Method));
            if (uri.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)) return Task.FromResult(Json("{\"Items\":[]}"));
            if (uri.AbsolutePath.EndsWith("/System/Info/Public", StringComparison.Ordinal)) return Task.FromResult(Json("{}"));
            if (uri.AbsolutePath.Contains("RemoteSearch", StringComparison.Ordinal)) return SearchReply ?? Task.FromResult(Json("[]"));
            if (request.Method == HttpMethod.Delete) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            if (uri.AbsolutePath.EndsWith("/Items/fixture", StringComparison.Ordinal)) return Task.FromResult(Json(ItemJson));
            throw new InvalidOperationException("Unexpected fixture request");
        }
    }
}
