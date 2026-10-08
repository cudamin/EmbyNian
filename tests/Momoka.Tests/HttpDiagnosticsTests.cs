using System.Net;
using System.Text;
using Momoka.Diagnostics;
using Momoka.Emby;
using static Momoka.Tests.TestHarness;

namespace Momoka.Tests;

internal static class HttpDiagnosticsTests
{
    private const string Secret = "placeholder-sensitive-value";
    private static readonly Uri Url = new("https://diagnostics.invalid/emby/Items");
    private static readonly EmbyHttp.RequestContext Context = new(DeviceIdentity.Create("diagnostics-fixture", "1"), Secret);

    public static void Register()
    {
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.InternalServerError })
            Test($"HTTP 诊断：{(int)status} 错误正文不能把凭据带进异常或日志", () =>
                CheckNoSecret(new ReplyTransport(_ => new HttpResponseMessage(status)
                {
                    Content = new StringContent("request contained " + Secret)
                }), http => http.GetBytesAsync(Url, Context, CancellationToken.None), status));

        Test("HTTP 诊断：登录错误不能回显请求密码", () =>
            CheckNoSecret(new ReplyTransport(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("rejected Pw=" + Secret)
            }), http => http.PostJsonAsync<EmbyItem>(Url, new { UserName = "fixture", Pw = Secret },
                Context with { AccessToken = null, IsAuthenticationAttempt = true }, CancellationToken.None), HttpStatusCode.BadRequest));

        Test("HTTP 诊断：服务器自定义 ReasonPhrase 不进入日志", () =>
            CheckNoSecret(new ReplyTransport(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                ReasonPhrase = Secret,
                Content = new StringContent("failure")
            }), http => http.GetBytesAsync(Url, Context, CancellationToken.None), HttpStatusCode.BadGateway));

        Test("HTTP 诊断：传输异常中的完整地址和查询凭据不进入异常链", () =>
            CheckNoSecret(new ReplyTransport(_ => throw new HttpRequestException($"Failed at {Url}?api_key={Secret}")),
                http => http.GetBytesAsync(Url, Context, CancellationToken.None), null));

        Test("HTTP 诊断：成功响应读取正文时的传输错误同样不能回显凭据", () =>
            CheckNoSecret(new ReplyTransport(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new FailedContent()
            }), http => http.GetBytesAsync(Url, Context, CancellationToken.None), null));

        Test("HTTP 诊断：JSON 正文读取失败不能绕过传输脱敏", () =>
            CheckNoSecret(new ReplyTransport(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new FailedContent()
            }), http => http.GetJsonAsync<EmbyItem>(Url, Context, CancellationToken.None), null));

        Test("HTTP 诊断：JSON 错误路径里的服务器文本不进入异常链", () =>
            CheckNoSecret(new ReplyTransport(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"" + Secret + "\":[]}", Encoding.UTF8, "application/json")
            }), http => http.GetJsonAsync<Dictionary<string, int>>(Url, Context, CancellationToken.None), HttpStatusCode.OK));

        Test("HTTP 诊断：可选 JSON 解析失败也不记录服务器的字段名", () =>
        {
            var sink = new RingBufferLogSink();
            Log.UseSink(sink);
            try
            {
                using var http = new EmbyHttp(new ReplyTransport(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"" + Secret + "\":[]}", Encoding.UTF8, "application/json")
                }));
                Assert.Null(http.PostForJsonAsync<Dictionary<string, int>>(Url, null, Context, CancellationToken.None)
                    .GetAwaiter().GetResult());
                Assert.False(sink.Snapshot().Any(entry => entry.ToString().Contains(Secret, StringComparison.Ordinal)));
            }
            finally { Log.UseSink(NullLogSink.Instance); }
        });
    }

    private static void CheckNoSecret(HttpMessageHandler handler, Func<EmbyHttp, Task> request, HttpStatusCode? expectedStatus)
    {
        var sink = new RingBufferLogSink();
        Log.UseSink(sink);
        try
        {
            using var http = new EmbyHttp(handler);
            var error = Assert.Catch<EmbyApiException>(() => request(http).GetAwaiter().GetResult());
            Log.Warn("fixture", "request failed", error);
            Assert.Equal(expectedStatus, error.StatusCode);
            Assert.False((error.ToString() + error.ResponseBody).Contains(Secret, StringComparison.Ordinal));
            Assert.False(sink.Snapshot().Any(entry => entry.ToString().Contains(Secret, StringComparison.Ordinal)));
        }
        finally { Log.UseSink(NullLogSink.Instance); }
    }

    private sealed class FailedContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new HttpRequestException("Failed response body at ?api_key=" + Secret));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ReplyTransport(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }
}
