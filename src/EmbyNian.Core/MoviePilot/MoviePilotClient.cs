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

        // 图片代理（system/cache/image）只认网页端登录流程种下的资源 Cookie，不认 Bearer 头。好在带 Bearer 的
        // 每一趟 API 调用，服务器都会顺手在响应里 Set-Cookie 一枚资源令牌（v2/v3 的 verify_token 都这么做）——
        // 所以这里挂一个 CookieContainer，让任何一趟轮询顺手把种子带上，之后的取图请求就都带着它了。测试注入的
        // 假传输层不是 SocketsHttpHandler，跳过（假传输层不认 Cookie，测试也不测它）。
        if (handler is SocketsHttpHandler sockets) sockets.CookieContainer = new CookieContainer();

        _handler = handler;

        // 120 秒而不是常见的 30：资源搜索（search/media）要 MoviePilot 现去各个站点捞种子，几十秒是常事。连不上
        // 由上面的 ConnectTimeout=8s 快速兜底，所以这个较长的响应上限只会落在「连上了、正在慢慢搜」这一种情形，
        // 登录和识别搜索本来就秒回、够不着它。
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(120) };
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
    /// 取一份任意网址的原始字节。给下载卡片的封面用：MoviePilot 的图片代理（同一个主机，资源 Cookie 已在
    /// <see cref="_http"/> 的罐子里）和 TMDB 的直连网址都从这一条走。不拆信封 —— 图片不是 API，没有信封。
    /// <para>
    /// 调用方自带取消；超时由调用方用链接的 <see cref="CancellationTokenSource"/> 自己掐 —— 这一份 http 的
    /// 120 秒是给资源搜索备的，图片等不起那么久。
    /// </para>
    /// </summary>
    public async Task<byte[]> GetBytesAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new MoviePilotException(
                $"取图失败：{(int)response.StatusCode} {response.ReasonPhrase}", response.StatusCode);

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
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
        return await ReadDataAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One write against the API. Posts <paramref name="body"/> as JSON and returns the <c>data</c> member
    /// of the envelope, exactly like <see cref="GetAsync"/>. Both share <see cref="ReadDataAsync"/>, so a
    /// 401, a business failure carried on a 200, and the envelope-unwrap can never come apart between them.
    /// <para>
    /// 新增订阅走这一条。正文由 <see cref="MoviePilotMediaParser.SubscribeBody"/> 拼好，序列化用和读取同一套
    /// JSON 选项（<see cref="Emby.EmbyHttp.Json"/>），大小写和数字处理只有一个说法。
    /// </para>
    /// </summary>
    public async Task<JsonElement> PostAsync(
        Uri apiBase,
        string token,
        string path,
        object body,
        CancellationToken cancellationToken) =>
        DataOrThrow(await PostReplyAsync(apiBase, token, path, body, cancellationToken).ConfigureAwait(false));

    internal async Task<MoviePilotReply> PostReplyAsync(
        Uri apiBase,
        string token,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        var url = MoviePilotAddress.Combine(apiBase, BasePath + path.TrimStart('/'));

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, Emby.EmbyHttp.Json), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadReplyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns one response into its <c>data</c>, or throws with the server's own words. Shared by
    /// <see cref="GetAsync"/> and <see cref="PostAsync"/> so the three failure modes below are handled once.
    /// <para>
    /// 401 单独一档（令牌到期，得重登，见 <see cref="MoviePilotTokenExpiredException"/>）；有些接口用 HTTP 200
    /// 揣一个 <c>success:false</c> 的业务错误，也当失败；没有信封的回话（比如 <c>media/search</c> 的裸数组）整份
    /// 交出去，想要数组的调用方照样拿得到数组。
    /// </para>
    /// </summary>
    private static async Task<JsonElement> ReadDataAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        DataOrThrow(await ReadReplyAsync(response, cancellationToken).ConfigureAwait(false));

    private static JsonElement DataOrThrow(MoviePilotReply reply)
    {
        if (!reply.Success)
            throw new MoviePilotException(string.IsNullOrWhiteSpace(reply.Message) ? "MoviePilot 报告了一个错误" : reply.Message);
        return reply.Data;
    }

    // 整理允许部分成功，保留失败信封里的逐文件回执，不能因此重试整批。
    private static async Task<MoviePilotReply> ReadReplyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new MoviePilotTokenExpiredException(Describe(response, body, "请求"));

        if (!response.IsSuccessStatusCode)
            throw new MoviePilotException(Describe(response, body, "请求"), response.StatusCode, body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var success = root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var flag) ||
            flag.ValueKind != JsonValueKind.False;
        var message = MoviePilotTransfer.Text(root, "message");
        var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var inner)
            ? inner.Clone() : root.Clone();
        return new MoviePilotReply(success, message, data);
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

internal sealed record MoviePilotReply(bool Success, string Message, JsonElement Data);
