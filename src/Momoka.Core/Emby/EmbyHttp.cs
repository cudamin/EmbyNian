using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Momoka.Diagnostics;

namespace Momoka.Emby;

/// <summary>
/// The single place that performs HTTP against Emby.
/// <para>
/// Every request carries its own headers on the <see cref="HttpRequestMessage"/>.
/// v1 mutated <c>HttpClient.DefaultRequestHeaders</c> before each call, which raced
/// against itself as soon as posters were fetched concurrently.
/// </para>
/// </summary>
public sealed class EmbyHttp : IDisposable
{
    private const string Category = "http";
    private const int MaxErrorBodyBytes = 8000;

    private readonly HttpClient _http;

    /// <summary>
    /// 同一个连接池上的第二个客户端，只给「下载到设备」用，不设整体截止时间。
    /// 普通请求由 <see cref="SendAndReadAsync{T}"/> 限制请求头和正文的总耗时；影片下载可以持续更久，
    /// 只受调用方取消和 handler 的连接超时约束。两个客户端共用的 handler 由 <see cref="Dispose"/> 统一释放。
    /// </summary>
    private readonly HttpClient _long;

    private readonly HttpMessageHandler _handler;

    public EmbyHttp(HttpMessageHandler? handler = null)
        : this(handler, TimeSpan.FromSeconds(30))
    {
    }

    /// <summary>测试可缩短等待上限，生产请求仍使用公开构造函数的 30 秒。</summary>
    internal EmbyHttp(HttpMessageHandler? handler, TimeSpan requestTimeout)
    {
        handler ??= new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 12,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AllowAutoRedirect = false,
            UseCookies = false
        };

        // 注入真实 handler 时也不能绕过逐跳认证边界。
        if (handler is SocketsHttpHandler sockets)
        {
            sockets.AllowAutoRedirect = false;
            sockets.UseCookies = false;
        }
        else if (handler is HttpClientHandler clientHandler)
        {
            clientHandler.AllowAutoRedirect = false;
            clientHandler.UseCookies = false;
        }

        _handler = handler;
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = requestTimeout };
        _long = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<T> GetJsonAsync<T>(Uri url, RequestContext context, CancellationToken cancellationToken)
        where T : notnull =>
        SendAndReadAsync(HttpMethod.Get, url, null, context,
            (response, token) => ReadJsonAsync<T>(response, url, token), cancellationToken);

    public Task<T> PostJsonAsync<T>(Uri url, object? body, RequestContext context, CancellationToken cancellationToken)
        where T : notnull =>
        SendAndReadAsync(HttpMethod.Post, url, body, context,
            (response, token) => ReadJsonAsync<T>(response, url, token), cancellationToken);

    public Task PostAsync(Uri url, object? body, RequestContext context, CancellationToken cancellationToken) =>
        SendAndReadAsync(HttpMethod.Post, url, body, context,
            static (_, _) => Task.FromResult(true), cancellationToken);

    /// <summary>
    /// POSTs raw bytes with <paramref name="contentType"/> as the whole body — 「上传这张图」用。
    /// <para>
    /// 和上面那几个不一样：这条路的正文<b>不是 JSON</b>，是一张图片的原样字节，所以不能走
    /// <see cref="SendAsync"/> 里那段「把对象序列化成 JSON 再发」的默认处理。直接给
    /// <see cref="ByteArrayContent"/> 而不是流：一张封面撑死几兆，装进内存换一个准确的 Content-Length
    /// 是对的（Emby 对 chunked 的分块传输本来就吃不消，见 <see cref="SendAsync"/> 里那一段）。
    /// </para>
    /// </summary>
    public Task PostBytesAsync(
        Uri url,
        byte[] bytes,
        string contentType,
        RequestContext context,
        CancellationToken cancellationToken) =>
        SendAndReadAsync(HttpMethod.Post, url, new RawBody(bytes, contentType), context,
            static (_, _) => Task.FromResult(true), cancellationToken);

    /// <summary>
    /// 原样的正文字节，连它的内容类型。<see cref="SendAsync"/> 认这一个类型 —— 别的对象都被当成「要序列化的
    /// JSON 载荷」，只有它是「这就是正文」。
    /// </summary>
    private sealed record RawBody(byte[] Bytes, string ContentType);

    /// <summary>
    /// Posts, and takes the response body if there is one. Emby answers the user-state endpoints with the
    /// item's fresh <c>UserItemDataDto</c>, which is worth having — it is where the server's own idea of
    /// the resume position and a series' remaining-episode count comes from, and the alternative is
    /// guessing at both or asking for the whole item again. But no version promises it, so a missing or
    /// unreadable body is null rather than a failure: the call itself succeeded.
    /// </summary>
    public Task<T?> PostForJsonAsync<T>(Uri url, object? body, RequestContext context, CancellationToken cancellationToken)
        where T : class => SendForJsonAsync<T>(HttpMethod.Post, url, body, context, cancellationToken);

    /// <inheritdoc cref="PostForJsonAsync{T}"/>
    public Task<T?> DeleteForJsonAsync<T>(Uri url, RequestContext context, CancellationToken cancellationToken)
        where T : class => SendForJsonAsync<T>(HttpMethod.Delete, url, null, context, cancellationToken);

    public Task DeleteAsync(Uri url, RequestContext context, CancellationToken cancellationToken) =>
        SendAndReadAsync(HttpMethod.Delete, url, null, context,
            static (_, _) => Task.FromResult(true), cancellationToken);

    private Task<T?> SendForJsonAsync<T>(
        HttpMethod method,
        Uri url,
        object? body,
        RequestContext context,
        CancellationToken cancellationToken)
        where T : class =>
        SendAndReadAsync(method, url, body, context,
            (response, token) => ReadOptionalJsonAsync<T>(response, method, url, token), cancellationToken);

    private static async Task<T?> ReadOptionalJsonAsync<T>(
        HttpResponseMessage response,
        HttpMethod method,
        Uri url,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            Log.Debug(Category, $"{method} {Redact(url)} 没有可解析的响应体（{error.GetType().Name}）");
            return null;
        }
    }

    public Task<byte[]> GetBytesAsync(Uri url, RequestContext context, CancellationToken cancellationToken) =>
        SendAndReadAsync(HttpMethod.Get, url, null, context,
            static (response, token) => response.Content.ReadAsByteArrayAsync(token), cancellationToken);

    /// <summary>
    /// 普通请求的截止时间覆盖整个读取过程。ResponseHeadersRead 让 HttpClient.Timeout 在响应头到达时结束，
    /// 若正文只拿调用方令牌，传 None 的共享图片下载就可能永远占着连接和下载任务。
    /// </summary>
    private async Task<T> SendAndReadAsync<T>(
        HttpMethod method,
        Uri url,
        object? body,
        RequestContext context,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_http.Timeout);

        try
        {
            using var response = await SendAsync(method, url, body, context, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var result = await read(response, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmbyUnreachableException($"连接 {url.Host} 超时");
        }
        catch (HttpRequestException error)
        {
            throw new EmbyUnreachableException($"读取 {url.Host} 的响应失败（{error.HttpRequestError}）");
        }
        catch (IOException)
        {
            throw new EmbyUnreachableException($"读取 {url.Host} 的响应失败");
        }
    }

    /// <summary>
    /// 「下载到设备」：把一个地址上的东西边下边写到磁盘上，写完返回一共多少字节。
    /// <para>
    /// 先落成 <c>.part</c> 再改名，所以一次中断（关掉程序、断网）留下的是一个一眼看得出没下完的文件，而不是
    /// 一个大小不对却叫着正确名字的影片。整个内容不进内存：一个「读成 byte[] 再写」的写法在这里就是把十几个
    /// G 装进内存。
    /// </para>
    /// </summary>
    /// <param name="progress">
    /// 已经下了多少、一共多少（服务器没说长度时后者为空）。隔一段才报一次，见 <see cref="ReportEvery"/>。
    /// 拿 <see cref="Progress{T}"/> 造出来的话，回调会自己回到造它的那个线程上，界面不用再marshal一次。
    /// </param>
    public async Task<long> DownloadToFileAsync(
        Uri url,
        string path,
        RequestContext context,
        IProgress<(long Done, long? Total)>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, url, null, context, cancellationToken, _long)
            .ConfigureAwait(false);

        var total = response.Content.Headers.ContentLength;
        var temporary = path + ".part";
        var done = 0L;

        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);

        try
        {
            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var target = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);

            await using (source.ConfigureAwait(false))
            await using (target.ConfigureAwait(false))
            {
                var buffer = new byte[1 << 20];
                var reported = 0L;

                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;

                    if (done - reported < ReportEvery) continue;
                    reported = done;
                    progress?.Report((done, total));
                }
            }

            // 服务器说了有多少就必须收到这么多。少了还改名的话，磁盘上留下的是一个名字正确、大小不对的影片
            // 文件 —— 播到一半没了，而谁也看不出这是下载断在半路。传输层多数情况下自己会为提前断开的连接抛
            // 一个 IOException，这一句是把「多数情况」写成规矩：不完整就当失败，下面那个 catch 顺手把 .part
            // 删掉。长度不知道（服务器没给 Content-Length）时无从比对，那时候 EOF 就是全部答案。
            if (total is { } expected && done != expected)
                throw new IOException($"下载不完整：收到 {done} 字节，服务器说有 {expected} 字节");

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            progress?.Report((done, total ?? done));
            return done;
        }
        catch
        {
            // 下坏的那半个文件不留在磁盘上。删不掉就算了 —— 这一路已经在往上抛一个更值得说的错误。
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    /// <summary>下载进度隔多少字节报一次。每一兆报一次的话，一个大文件就是上万条提示。</summary>
    private const long ReportEvery = 4L << 20;

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri url,
        object? body,
        RequestContext context,
        CancellationToken cancellationToken,
        HttpClient? via = null)
    {
        EmbyHttpRedirect.Validate(url);
        var payload = body switch
        {
            null => null,
            RawBody raw => raw,
            // Emby 需要准确的 Content-Length，跳转也只能重放同一份已序列化正文。
            _ => new RawBody(JsonSerializer.SerializeToUtf8Bytes(body, Json), "application/json")
        };
        var authenticated = true;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (authenticated)
            {
                request.Headers.TryAddWithoutValidation("X-Emby-Authorization", context.Device.ToAuthorizationHeader());
                if (!string.IsNullOrEmpty(context.AccessToken))
                    request.Headers.TryAddWithoutValidation("X-Emby-Token", context.AccessToken);
            }
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (payload is not null)
            {
                request.Content = new ByteArrayContent(payload.Bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(payload.ContentType);
            }

            HttpResponseMessage response;
            try
            {
                response = await (via ?? _http)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new EmbyUnreachableException($"连接 {url.Host} 超时");
            }
            catch (HttpRequestException error)
            {
                throw new EmbyUnreachableException($"无法连接到 {url.Host}（{error.HttpRequestError}）");
            }

            if (response.IsSuccessStatusCode) return response;

            using (response)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (EmbyHttpRedirect.IsRedirect(response.StatusCode))
                {
                    if (redirects >= EmbyHttpRedirect.Limit)
                        throw new EmbyApiException("服务器重定向次数过多", response.StatusCode);
                    var next = EmbyHttpRedirect.Next(url, response.Headers.Location, method, response.StatusCode, context.IsAuthenticationAttempt);
                    authenticated &= EmbyHttpRedirect.SameOrigin(url, next);
                    url = next;
                    continue;
                }

                await ReadBodySafelyAsync(response, cancellationToken).ConfigureAwait(false);
                Log.Warn(Category, $"{method} {Redact(url)} -> {(int)response.StatusCode}");
                // 资源来源的 401 不是 Emby 令牌失效，不能触发原服务器重新登录。
                if (!authenticated && response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new EmbyApiException("重定向资源拒绝访问", response.StatusCode);
                throw Translate(url, response.StatusCode, context.IsAuthenticationAttempt);
            }
        }
    }

    private static EmbyApiException Translate(Uri url, HttpStatusCode status, bool isAuthenticationAttempt)
    {
        if (status == HttpStatusCode.Unauthorized)
        {
            return isAuthenticationAttempt
                ? new EmbyAuthenticationException("用户名或密码不正确", status)
                : new EmbyTokenExpiredException();
        }

        if (status == HttpStatusCode.Forbidden)
            return new EmbyApiException("当前账户没有访问该内容的权限", status);

        if (status == HttpStatusCode.NotFound)
            return new EmbyApiException($"服务器上找不到该资源（{Redact(url)}）", status);

        // 错误正文和 ReasonPhrase 都可能回显请求凭据；异常会被界面和日志直接使用。
        return new EmbyApiException($"服务器返回 HTTP {(int)status}", status);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, Uri url, CancellationToken cancellationToken)
        where T : notnull
    {
        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
            return value ?? throw new EmbyApiException($"服务器对 {Redact(url)} 返回了空响应");
        }
        catch (JsonException error)
        {
            throw new EmbyApiException($"无法解析服务器对 {Redact(url)} 的响应（行 {error.LineNumber}，字节 {error.BytePositionInLine}）",
                response.StatusCode);
        }
    }

    private static async Task ReadBodySafelyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            // 小错误响应读完以复用连接；正文受字节数和请求截止时间约束，不解码、不交给诊断。
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[MaxErrorBodyBytes];
            await using (stream.ConfigureAwait(false))
            {
                await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    /// <summary>Strips the query so an api_key never reaches the log file.</summary>
    internal static string Redact(Uri url) => url.GetLeftPart(UriPartial.Path);

    public void Dispose()
    {
        // 两个客户端都不持有 handler（disposeHandler: false），所以它在这里放，而且只放一次。
        _http.Dispose();
        _long.Dispose();
        _handler.Dispose();
    }

    public readonly record struct RequestContext(DeviceIdentity Device, string? AccessToken, bool IsAuthenticationAttempt = false);
}
