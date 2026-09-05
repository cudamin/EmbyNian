using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbyNian.Diagnostics;

namespace EmbyNian.Emby;

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
    private const int MaxLoggedBodyLength = 2000;

    /// <summary>
    /// 错误响应体最多读这么多字节。比 <see cref="MaxLoggedBodyLength"/> 宽出四倍，因为那一条数的是字符而
    /// 这一条数的是字节 —— 一个汉字在 UTF-8 里是三个。见 <see cref="ReadBodySafelyAsync"/>。
    /// </summary>
    private const int MaxLoggedBodyBytes = MaxLoggedBodyLength * 4;

    private readonly HttpClient _http;

    /// <summary>
    /// 同一个连接池上的第二个客户端，只给「下载到设备」用，区别只有一样：<b>没有整体超时</b>。
    /// <para>
    /// <see cref="HttpClient.Timeout"/> 管的是整趟请求，连读响应体那一段一起算 —— 就算按
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> 只等到响应头，那个计时器也不会停。于是一个
    /// 十几个 G 的影片文件必然在 30 秒上被掐断，症状是「下载总是失败」。下载那一路的边界改成调用方自己的
    /// 取消令牌加上连接超时（那一条在 <see cref="SocketsHttpHandler.ConnectTimeout"/> 上，仍然管用）。
    /// </para>
    /// <para>
    /// 共用上面那个 handler，所以这不是第二个连接池 —— 两个客户端都不负责释放它，由本类
    /// <see cref="Dispose"/> 统一放掉。
    /// </para>
    /// </summary>
    private readonly HttpClient _long;

    private readonly HttpMessageHandler _handler;

    public EmbyHttp(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 12,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };

        _handler = handler;
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        _long = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<T> GetJsonAsync<T>(Uri url, RequestContext context, CancellationToken cancellationToken)
        where T : notnull
    {
        using var response = await SendAsync(HttpMethod.Get, url, null, context, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> PostJsonAsync<T>(Uri url, object? body, RequestContext context, CancellationToken cancellationToken)
        where T : notnull
    {
        using var response = await SendAsync(HttpMethod.Post, url, body, context, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, url, cancellationToken).ConfigureAwait(false);
    }

    public async Task PostAsync(Uri url, object? body, RequestContext context, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, url, body, context, cancellationToken).ConfigureAwait(false);
    }

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

    public async Task DeleteAsync(Uri url, RequestContext context, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, url, null, context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> SendForJsonAsync<T>(
        HttpMethod method,
        Uri url,
        object? body,
        RequestContext context,
        CancellationToken cancellationToken)
        where T : class
    {
        using var response = await SendAsync(method, url, body, context, cancellationToken).ConfigureAwait(false);

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            Log.Debug(Category, $"{method} {Redact(url)} 没有可解析的响应体：{error.Message}");
            return null;
        }
    }

    public async Task<byte[]> GetBytesAsync(Uri url, RequestContext context, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, url, null, context, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
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
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", context.Device.ToAuthorizationHeader());
        if (!string.IsNullOrEmpty(context.AccessToken))
            request.Headers.TryAddWithoutValidation("X-Emby-Token", context.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body is not null)
        {
            // JsonContent sends the body with Transfer-Encoding: chunked, which Emby 4.9.5
            // cannot read — the JSON never binds and every request fails with
            // "Value cannot be null. (Parameter 'name')". Pre-serialize to bytes so the
            // request goes out with a Content-Length header instead.
            var json = JsonSerializer.Serialize(body, Json);
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await (via ?? _http)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmbyUnreachableException($"连接 {url.Host} 超时", error);
        }
        catch (HttpRequestException error)
        {
            throw new EmbyUnreachableException($"无法连接到 {url.Host}：{error.Message}", error);
        }

        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            var responseBody = await ReadBodySafelyAsync(response, cancellationToken).ConfigureAwait(false);
            Log.Warn(Category, $"{method} {Redact(url)} -> {(int)response.StatusCode} {response.ReasonPhrase}");
            throw Translate(method, url, response.StatusCode, response.ReasonPhrase, responseBody, context.IsAuthenticationAttempt);
        }
    }

    private static EmbyApiException Translate(
        HttpMethod method,
        Uri url,
        HttpStatusCode status,
        string? reason,
        string? body,
        bool isAuthenticationAttempt)
    {
        if (status == HttpStatusCode.Unauthorized)
        {
            return isAuthenticationAttempt
                ? new EmbyAuthenticationException("用户名或密码不正确", status, body)
                : new EmbyTokenExpiredException();
        }

        if (status == HttpStatusCode.Forbidden)
            return new EmbyApiException("当前账户没有访问该内容的权限", status, body);

        if (status == HttpStatusCode.NotFound)
            return new EmbyApiException($"服务器上找不到该资源（{Redact(url)}）", status, body);

        var message = $"服务器返回 {(int)status} {reason}";
        if (!string.IsNullOrWhiteSpace(body)) message += Environment.NewLine + body;
        return new EmbyApiException(message, status, body);
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
            throw new EmbyApiException($"无法解析服务器对 {Redact(url)} 的响应：{error.Message}", response.StatusCode, null, error);
        }
    }

    private static async Task<string?> ReadBodySafelyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            // 只读开头这几千字节，而不是 ReadAsStringAsync 之后再截断：这一句是给日志和错误提示用的一句话，
            // 而响应体的大小是对面说了算的。反代或者门户网关在一个 502 上塞回来一整页 HTML 是常事，坏掉的
            // 服务器能塞回来更多 —— 「先整份读进内存，再切掉不要的」就是先中招再截断。
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[MaxLoggedBodyBytes];

            await using (stream.ConfigureAwait(false))
            {
                var read = await stream
                    .ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
                    .ConfigureAwait(false);

                var text = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                if (text.Length == 0) return null;
                return text.Length <= MaxLoggedBodyLength ? text : text[..MaxLoggedBodyLength] + "…";
            }
        }
        catch
        {
            return null;
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
