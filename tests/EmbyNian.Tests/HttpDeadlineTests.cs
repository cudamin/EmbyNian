using System.Net;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>响应头到了、正文却不再前进：假流把这段时间窗固定住，全程不打开网络连接。</summary>
internal static class HttpDeadlineTests
{
    private static readonly Uri Url = new("https://deadline.example.test/emby/Items");
    private static readonly EmbyHttp.RequestContext Context = new(DeviceIdentity.Create("deadline-test", "1"), null);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(2);

    public static void Register()
    {
        (string Name, Func<EmbyHttp, CancellationToken, Task> Read)[] readers =
        [
            ("GET JSON", (http, token) => http.GetJsonAsync<EmbyItem>(Url, Context, token)),
            ("POST JSON", (http, token) => http.PostJsonAsync<EmbyItem>(Url, new { Name = "test" }, Context, token)),
            ("POST 可选 JSON", (http, token) => http.PostForJsonAsync<EmbyItem>(Url, null, Context, token)),
            ("DELETE 可选 JSON", (http, token) => http.DeleteForJsonAsync<EmbyItem>(Url, Context, token)),
            ("图片字节", (http, token) => http.GetBytesAsync(Url, Context, token))
        ];

        foreach (var (name, read) in readers)
        {
            Test($"HTTP 正文超时：{name} 的响应头到达后仍受截止时间约束", () =>
                AssertBodyTimeout(read, HttpStatusCode.OK));
        }

        Test("HTTP 正文超时：错误响应体停住也不能拖住整个请求", () =>
            AssertBodyTimeout((http, token) => http.GetJsonAsync<EmbyItem>(Url, Context, token), HttpStatusCode.BadGateway));

        Test("HTTP 正文取消：调用方取消仍是取消，不能报连接超时", () =>
        {
            using var body = new PausedReadStream([]);
            using var http = new EmbyHttp(new PausedBodyTransport(body), TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var request = http.GetBytesAsync(Url, Context, cancellation.Token);
            body.Started.WaitAsync(TestTimeout).GetAwaiter().GetResult();

            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => request.WaitAsync(TestTimeout).GetAwaiter().GetResult());
        });

        Test("HTTP 长下载：正文超过普通请求上限仍能完成", () =>
        {
            WithDownloadPath(path =>
            {
                byte[] expected = [1, 2, 3];
                using var body = new PausedReadStream(expected);
                using var http = new EmbyHttp(new PausedBodyTransport(body), RequestTimeout);
                using var cancellation = new CancellationTokenSource();
                var download = http.DownloadToFileAsync(Url, path, Context, null, cancellation.Token);

                try
                {
                    body.Started.WaitAsync(TestTimeout).GetAwaiter().GetResult();
                    Task.Delay(RequestTimeout * 3).GetAwaiter().GetResult();
                    Assert.False(download.IsCompleted, "长下载不能复用普通请求的整体截止时间");

                    body.Resume();

                    Assert.Equal((long)expected.Length, download.WaitAsync(TestTimeout).GetAwaiter().GetResult());
                    Assert.True(File.ReadAllBytes(path).SequenceEqual(expected), "完整内容应当写入目标文件");
                    Assert.False(File.Exists(path + ".part"));
                }
                finally
                {
                    cancellation.Cancel();
                    ObserveCompletion(download);
                }
            });
        });

        Test("HTTP 长下载：正文等待时仍可由调用方取消并清理临时文件", () =>
        {
            WithDownloadPath(path =>
            {
                using var body = new PausedReadStream([1, 2, 3]);
                using var http = new EmbyHttp(new PausedBodyTransport(body), RequestTimeout);
                using var cancellation = new CancellationTokenSource();
                var download = http.DownloadToFileAsync(Url, path, Context, null, cancellation.Token);
                body.Started.WaitAsync(TestTimeout).GetAwaiter().GetResult();

                cancellation.Cancel();

                Assert.Throws<OperationCanceledException>(() => download.WaitAsync(TestTimeout).GetAwaiter().GetResult());
                Assert.False(File.Exists(path));
                Assert.False(File.Exists(path + ".part"));
            });
        });
    }

    private static void AssertBodyTimeout(Func<EmbyHttp, CancellationToken, Task> read, HttpStatusCode status)
    {
        using var body = new PausedReadStream([]);
        using var http = new EmbyHttp(new PausedBodyTransport(body, status), RequestTimeout);
        using var cleanup = new CancellationTokenSource();
        var request = read(http, cleanup.Token);

        try
        {
            body.Started.WaitAsync(TestTimeout).GetAwaiter().GetResult();
            var error = Assert.Catch<EmbyUnreachableException>(() => request.WaitAsync(TestTimeout).GetAwaiter().GetResult());
            Assert.Contains("超时", error.Message);
            Assert.False(cleanup.IsCancellationRequested, "截止时间必须来自 HTTP 层，不能依赖调用方取消");
        }
        finally
        {
            cleanup.Cancel();
            ObserveCompletion(request);
        }
    }

    private static void ObserveCompletion(Task task)
    {
        try { task.WaitAsync(TestTimeout).GetAwaiter().GetResult(); }
        catch (Exception) { }
    }

    private static void WithDownloadPath(Action<string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"embynian-http-deadline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "download.bin");
        try { check(path); }
        finally
        {
            AtomicFile.TryDelete(path);
            AtomicFile.TryDelete(path + ".part");
            Directory.Delete(directory);
        }
    }

    private sealed class PausedBodyTransport(PausedReadStream body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StreamContent(body) });
    }

    private sealed class PausedReadStream(byte[] contents) : Stream
    {
        private readonly MemoryStream _source = new(contents);
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Resume() => _resume.TrySetResult(true);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult(true);
            await _resume.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _resume.TrySetCanceled();
                _source.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
