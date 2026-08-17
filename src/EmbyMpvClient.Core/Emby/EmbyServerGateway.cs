using System.Text.Json.Serialization;
using EmbyMpvClient.Diagnostics;

namespace EmbyMpvClient.Emby;

/// <summary>
/// Operations that work before (or without) a sign-in: probing a server and
/// authenticating. Split from <see cref="EmbyClient"/> so an authenticated client is
/// only ever constructed from a real session.
/// </summary>
public sealed class EmbyServerGateway(EmbyHttp http, DeviceIdentity device)
{
    private const string Category = "emby";

    private EmbyHttp.RequestContext Anonymous => new(device, null);

    /// <summary>Used by the "测试连接" button; also confirms the address really is an Emby server.</summary>
    public Task<PublicSystemInfo> GetPublicSystemInfoAsync(Uri apiBase, CancellationToken cancellationToken) =>
        http.GetJsonAsync<PublicSystemInfo>(EmbyUrl.Combine(apiBase, "System/Info/Public"), Anonymous, cancellationToken);

    /// <summary>
    /// The users the server is willing to advertise, so the login page can offer a
    /// picker instead of making the user type a username.
    /// </summary>
    public Task<List<EmbyUser>> GetPublicUsersAsync(Uri apiBase, CancellationToken cancellationToken) =>
        http.GetJsonAsync<List<EmbyUser>>(EmbyUrl.Combine(apiBase, "Users/Public"), Anonymous, cancellationToken);

    public async Task<EmbyConnection> AuthenticateAsync(
        Uri apiBase,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new EmbyAuthenticationException("请填写用户名");

        var url = EmbyUrl.Combine(apiBase, "Users/AuthenticateByName");
        var body = new AuthenticateByNameRequest { Username = username.Trim(), Pw = password };
        var context = new EmbyHttp.RequestContext(device, null, IsAuthenticationAttempt: true);

        var result = await http.PostJsonAsync<AuthenticationResult>(url, body, context, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result.AccessToken) || string.IsNullOrWhiteSpace(result.User?.Id))
            throw new EmbyApiException("服务器返回的登录结果缺少访问令牌或用户 ID");

        string serverName;
        try
        {
            var info = await GetPublicSystemInfoAsync(apiBase, cancellationToken).ConfigureAwait(false);
            serverName = info.ServerName ?? apiBase.Host;
        }
        catch (EmbyApiException error)
        {
            // Cosmetic only: a nameless server should not fail an otherwise good login.
            Log.Warn(Category, "登录成功但无法读取服务器名称", error);
            serverName = apiBase.Host;
        }

        Log.Info(Category, $"已登录 {serverName}（用户 {result.User!.Name}）");
        return new EmbyConnection(apiBase, result.AccessToken, result.User.Id, result.User.Name, serverName, device);
    }

    private sealed class AuthenticateByNameRequest
    {
        /// <summary>
        /// Emby 4.9.5+ binds the body case-sensitively and reads <c>UserName</c>; older servers
        /// deserialize case-insensitively, so this one spelling works on every version.
        /// </summary>
        [JsonPropertyName("UserName")]
        public string Username { get; set; } = "";

        /// <summary>Emby's field name for the plain password.</summary>
        public string Pw { get; set; } = "";
    }
}
