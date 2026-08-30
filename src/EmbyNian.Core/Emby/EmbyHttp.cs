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

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public EmbyHttp(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 12,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };

        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        _ownsClient = true;
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

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri url,
        object? body,
        RequestContext context,
        CancellationToken cancellationToken)
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
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
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
            var text = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (text.Length == 0) return null;
            return text.Length <= MaxLoggedBodyLength ? text : text[..MaxLoggedBodyLength] + "…";
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
        if (_ownsClient) _http.Dispose();
    }

    public readonly record struct RequestContext(DeviceIdentity Device, string? AccessToken, bool IsAuthenticationAttempt = false);
}
