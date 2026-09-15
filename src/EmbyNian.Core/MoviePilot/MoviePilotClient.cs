using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EmbyNian.Diagnostics;

namespace EmbyNian.MoviePilot;

/// <summary>
/// Talks to a MoviePilot service. Deliberately a sibling of <see cref="Emby.EmbyHttp"/> rather than a
/// generalisation of it, because almost nothing is shared except the shape.
/// <para>
/// MoviePilot is a separate product on a separate host with a separate auth scheme: Emby authenticates with
/// an <c>X-Emby-Token</c> header plus a device-authorisation header, MoviePilot has an OAuth2 form login
/// whose result is a short-lived JWT carried as <c>Authorization: Bearer</c>. One client that spoke both
/// would be a client whose every line has to ask which server it is talking to.
/// </para>
/// <para>
/// The one thing genuinely worth reusing is the JSON options, and those are already a static on
/// <see cref="Emby.EmbyHttp.Json"/> — a single documented place where casing and number handling are
/// decided. <c>JsonElement</c> is the return type throughout because MoviePilot's payloads are large and
/// this client only ever reads a handful of fields out of each; a fully typed model of the subscription
/// object alone would be sixty properties, most of which nothing here reads.
/// </para>
/// </summary>
public sealed class MoviePilotClient : IDisposable
{
    private const string Category = "moviepilot";
    private const string BasePath = "api/v1/";

    /// <summary>
    /// Where the login endpoint lives. Form-encoded, not JSON: this is a FastAPI OAuth2 password flow, and
    /// posting JSON to it answers 422 「请求参数不正确」 with the fields listed as missing.
    /// </summary>
    private const string TokenPath = "api/v1/login/access-token";

    private readonly HttpClient _http;
    private readonly HttpMessageHandler _handler;

    public MoviePilotClient(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };

        _handler = handler;
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Signs in and returns the JWT, or throws with the server's own words.
    /// <para>
    /// The password is only ever in memory here and in the caller's own field — it is not logged, and the
    /// request body is built by hand rather than through a logging helper for that reason.
    /// </para>
    /// </summary>
    public async Task<MoviePilotSession> SignInAsync(
        Uri apiBase,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new MoviePilotException("请填写 MoviePilot 用户名");
        if (string.IsNullOrEmpty(password)) throw new MoviePilotException("请填写 MoviePilot 密码");

        var url = MoviePilotAddress.Combine(apiBase, TokenPath);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username.Trim(),
                ["password"] = password
            })
        };

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // 401 here really is a wrong username or password, so it gets its own sentence rather than the
            // generic status line — it is the one failure a user can fix by typing.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new MoviePilotException("用户名或密码不正确", response.StatusCode, body);

            throw new MoviePilotException(
                Describe(response, body, "登录"), response.StatusCode, body);
        }

        var result = Parse<MoviePilotSignInResult>(body, "登录");
        if (string.IsNullOrWhiteSpace(result.AccessToken))
            throw new MoviePilotException("MoviePilot 没有返回访问令牌");

        Log.Info(Category, $"已登录 MoviePilot（用户 {result.UserName ?? username}，超级管理员={result.SuperUser}）");
        return new MoviePilotSession(apiBase, result.AccessToken!, result.UserName ?? username, result.SuperUser);
    }

    /// <summary>
    /// One read against the API. Returns the <c>data</c> member of MoviePilot's envelope, so callers never
    /// see the <c>{success, message, data}</c> wrapper.
    /// <para>
    /// Note that MoviePilot answers business failures with HTTP 200 and <c>success: false</c> in some
    /// places and with a real status code in others; both are handled, because guessing which one a given
    /// endpoint uses is exactly the kind of thing that breaks on an upgrade.
    /// </para>
    /// </summary>
    public async Task<JsonElement> GetAsync(
        Uri apiBase,
        string token,
        string path,
        CancellationToken cancellationToken)
    {
        var url = MoviePilotAddress.Combine(apiBase, BasePath + path.TrimStart('/'));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // 401 on a read is a token that has run out. Its own exception type, because the fix is different
        // from 「the address is wrong」: it means sign in again.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new MoviePilotTokenExpiredException(Describe(response, body, "请求"));

        if (!response.IsSuccessStatusCode)
            throw new MoviePilotException(Describe(response, body, "请求"), response.StatusCode, body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("success", out var success) &&
            success.ValueKind == JsonValueKind.False)
        {
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new MoviePilotException(string.IsNullOrWhiteSpace(message) ? "MoviePilot 报告了一个错误" : message!);
        }

        // A response without the envelope is passed through whole rather than treated as an error — the
        // shape varies, and a caller that wanted an array still gets one.
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
            ? data.Clone()
            : root.Clone();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MoviePilotUnreachableException($"连接 {request.RequestUri?.Host} 超时", error);
        }
        catch (HttpRequestException error)
        {
            throw new MoviePilotUnreachableException(
                $"无法连接到 {request.RequestUri?.Host}：{error.Message}", error);
        }
    }

    /// <summary>
    /// One sentence about a failed call, preferring what the server said over the status code — a
    /// MoviePilot error message is written for a person and a bare 404 is not.
    /// </summary>
    private static string Describe(HttpResponseMessage response, string body, string action)
    {
        var message = TryReadMessage(body);
        if (!string.IsNullOrWhiteSpace(message)) return message!;

        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => $"{action}失败：MoviePilot 上没有这个接口（{(int)response.StatusCode}），版本可能不匹配",
            HttpStatusCode.Forbidden => $"{action}失败：这个账号没有权限",
            _ => $"{action}失败：服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}"
        };
    }

    /// <summary>Pulls <c>message</c> out of a MoviePilot error envelope, if there is one and it says something.</summary>
    private static string? TryReadMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("message", out var message)) return null;

            var text = message.ValueKind == JsonValueKind.String ? message.GetString() : null;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (JsonException)
        {
            // Not JSON — a proxy's HTML error page, or a truncated body. Nothing useful to lift out of it.
            return null;
        }
    }

    private static T Parse<T>(string body, string action)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, Emby.EmbyHttp.Json)
                ?? throw new MoviePilotException($"{action}失败：MoviePilot 返回了空响应");
        }
        catch (JsonException error)
        {
            throw new MoviePilotException($"{action}失败：无法解析 MoviePilot 的响应（{error.Message}）", null, body, error);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }
}

/// <summary>
/// What a sign-in produced. The token is the only credential the client keeps; the username and the
/// super-admin flag are for the settings card to report, and the flag is worth surfacing because a
/// MoviePilot account that is not a super admin gets a 401 on most of the API and the error message for
/// that is otherwise baffling.
/// </summary>
public sealed record MoviePilotSession(Uri ApiBase, string AccessToken, string UserName, bool SuperUser);
