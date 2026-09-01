using EmbyNian.Configuration;
using EmbyNian.Diagnostics;

namespace EmbyNian.Emby;

/// <summary>
/// Owns the signed-in state: which server/account is active, the live
/// <see cref="EmbyClient"/>, and the one-shot re-authentication that happens when a
/// stored token has expired. Views call <see cref="ExecuteAsync{T}"/> so that recovery
/// is automatic instead of being re-implemented per screen.
/// </summary>
public sealed class EmbySession : IDisposable
{
    private const string Category = "session";

    private readonly EmbyHttp _http;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly CredentialVault _vault;
    private readonly SemaphoreSlim _reauthenticationGate = new(1, 1);

    private EmbyClient? _client;

    /// <summary>
    /// The library list <see cref="TryRestoreAsync"/> already has in hand, waiting to be collected by the
    /// shell that is about to ask for the same thing. See <see cref="TakeRestoredViews"/>.
    /// </summary>
    private List<EmbyItem>? _restoredViews;

    public EmbySession(AppSettings settings, SettingsStore store, CredentialVault vault, DeviceIdentity device)
        : this(settings, store, vault, device, null)
    {
    }

    /// <summary>
    /// The same session with its transport handed in. For tests only, and it is what lets the restore path
    /// be exercised at all: 「一个刚过期的令牌」「服务器答了但没有媒体库」这几档在真服务器上是碰不到的，而它们
    /// 正是这一层出过事的地方。
    /// </summary>
    internal EmbySession(
        AppSettings settings,
        SettingsStore store,
        CredentialVault vault,
        DeviceIdentity device,
        HttpMessageHandler? handler)
    {
        _settings = settings;
        _store = store;
        _vault = vault;
        _http = new EmbyHttp(handler);
        Gateway = new EmbyServerGateway(_http, device);
        Device = device;
    }

    /// <summary>Raised when the session ends and the shell must return to the login page.</summary>
    public event EventHandler<string>? SignedOut;

    public EmbyServerGateway Gateway { get; }

    public DeviceIdentity Device { get; }

    public ServerProfile? Server { get; private set; }

    public AccountProfile? Account { get; private set; }

    public bool IsSignedIn => _client is not null;

    public EmbyConnection? Connection => _client?.Connection;

    public EmbyClient Client => _client ?? throw new InvalidOperationException("尚未登录 Emby");

    /// <summary>
    /// The name to show for the connected server: what the server calls itself, which is what the
    /// login handshake reads out of <c>/System/Info/Public</c>. Falls back to the label saved in
    /// settings, and is empty when nobody is signed in.
    /// </summary>
    public string ServerDisplayName =>
        Connection?.ServerName is { Length: > 0 } name ? name : Server?.Name ?? "";

    public async Task SignInAsync(ServerProfile server, AccountProfile account, string password, string username, bool rememberPassword, CancellationToken cancellationToken)
    {
        var apiBase = EmbyServerAddress.Normalize(server.Url);
        var connection = await Gateway.AuthenticateAsync(apiBase, username, password, cancellationToken).ConfigureAwait(false);

        account.Username = connection.UserName;
        account.UserId = connection.UserId;
        account.LastSignedIn = DateTimeOffset.Now;
        _vault.SetAccessToken(account, connection.AccessToken);
        _vault.SetPassword(account, password, rememberPassword);

        Adopt(server, account, connection);
        Persist();
    }

    /// <summary>
    /// Tries the token saved from a previous run so a restart does not always land on
    /// the password prompt. Falls back to the saved password, then gives up quietly.
    /// </summary>
    public async Task<bool> TryRestoreAsync(ServerProfile server, AccountProfile account, CancellationToken cancellationToken)
    {
        var token = _vault.GetAccessToken(account);
        if (token.Length == 0 || account.UserId.Length == 0) return false;

        if (!EmbyServerAddress.TryNormalize(server.Url, out var apiBase, out _)) return false;

        var connection = new EmbyConnection(apiBase, token, account.UserId, account.Username, server.Name, Device);
        var candidate = new EmbyClient(_http, connection);

        try
        {
            // 这一趟同时是两件事：它既证明这个令牌还好使，又正好取回了外壳紧接着就要的那份媒体库列表 ——
            // 所以答案存下来给它，见 TakeRestoredViews。从前是扔掉的，于是每次启动这个接口都被问两遍
            // （日志里两行「读取到 N 个媒体库」相隔 20 毫秒）。
            var views = await candidate.GetViewsAsync(cancellationToken).ConfigureAwait(false);
            Adopt(server, account, await NameServerAsync(apiBase, connection, cancellationToken).ConfigureAwait(false));
            _restoredViews = views;
            Log.Info(Category, "已使用保存的令牌恢复登录");
            return true;
        }
        catch (EmbyTokenExpiredException)
        {
            Log.Info(Category, "保存的令牌已失效");
            _vault.ClearAccessToken(account);
        }
        catch (EmbyApiException error)
        {
            Log.Warn(Category, "无法使用保存的令牌恢复登录", error);
            return false;
        }

        if (!account.HasSavedPassword) return false;

        try
        {
            await SignInAsync(server, account, _vault.GetPassword(account), account.Username, account.RememberPassword, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (EmbyApiException error)
        {
            Log.Warn(Category, "使用保存的密码自动登录失败", error);
            return false;
        }
    }

    /// <summary>
    /// 恢复登录时那一趟已经取回来的媒体库列表，交出去一次就没了；从来没有过、或者已经被领走了就是 null，
    /// 那时候调用方照旧自己去问服务器。
    /// <para>
    /// <see cref="TryRestoreAsync"/> 拿「取一次媒体库列表」当令牌有效性的探针，而外壳启动时紧接着要的正是同
    /// 一份列表 —— 从前那个答案被扔掉，于是每次启动这个接口都被问两遍。局域网上是二十来毫秒，走反代或者外网
    /// 就是几百毫秒，而它压在「看到第一屏」的路上。
    /// </para>
    /// <para>
    /// **只给一次**是要紧的：刷新（换账号回来、手动重新读取）必须真去问服务器，否则新加的媒体库永远不出现。
    /// 换连接也作废（见 <c>Adopt</c>）—— 留着就是拿上一个账号的库给这一个账号建导航栏。
    /// </para>
    /// </summary>
    public List<EmbyItem>? TakeRestoredViews()
    {
        var views = _restoredViews;
        _restoredViews = null;
        return views;
    }

    public async Task<T> ExecuteAsync<T>(Func<EmbyClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var client = Client;
        try
        {
            return await operation(client, cancellationToken).ConfigureAwait(false);
        }
        catch (EmbyTokenExpiredException)
        {
            if (!await TryReauthenticateAsync(client, cancellationToken).ConfigureAwait(false))
            {
                EndSession("登录状态已过期，请重新登录");
                throw;
            }

            return await operation(Client, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ExecuteAsync(Func<EmbyClient, CancellationToken, Task> operation, CancellationToken cancellationToken) =>
        ExecuteAsync<bool>(async (client, token) =>
        {
            await operation(client, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public void SignOut() => EndSession("已退出登录");

    private async Task<bool> TryReauthenticateAsync(EmbyClient stale, CancellationToken cancellationToken)
    {
        await _reauthenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may already have refreshed while we waited on the gate.
            if (!ReferenceEquals(_client, stale)) return _client is not null;

            var server = Server;
            var account = Account;
            if (server is null || account is null || !account.HasSavedPassword) return false;

            Log.Info(Category, "令牌过期，正在使用保存的密码重新登录");
            await SignInAsync(server, account, _vault.GetPassword(account), account.Username, account.RememberPassword, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (EmbyApiException error)
        {
            Log.Warn(Category, "自动重新登录失败", error);
            return false;
        }
        finally
        {
            _reauthenticationGate.Release();
        }
    }

    /// <summary>
    /// Puts the server's own name on a restored connection. The token path builds its connection out
    /// of saved settings, where the name is only the local label, so a restart used to show the
    /// stand-in ("我的 Emby") where a fresh sign-in shows the real name. Cosmetic, so any failure
    /// keeps the label rather than breaking a restore that already works.
    /// </summary>
    private async Task<EmbyConnection> NameServerAsync(Uri apiBase, EmbyConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var info = await Gateway.GetPublicSystemInfoAsync(apiBase, cancellationToken).ConfigureAwait(false);
            return info.ServerName is { Length: > 0 } name ? connection with { ServerName = name } : connection;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.Warn(Category, "无法读取服务器名称，先用本地记住的名字", error);
            return connection;
        }
    }

    private void Adopt(ServerProfile server, AccountProfile account, EmbyConnection connection)
    {
        // 换了连接就作废：那一份是上一个账号（或者上一台服务器）的媒体库，交出去就是拿别人的库建导航栏。
        // 恢复登录那一路在这一句之后才存，见 TryRestoreAsync。
        _restoredViews = null;

        // A profile still carrying a stand-in name picks up what the server calls itself, so the
        // login page's server list and the rail agree. A name the user typed is left alone.
        if (connection.ServerName.Length > 0 && server.HasPlaceholderName) server.Name = connection.ServerName;

        Server = server;
        Account = account;
        _client = new EmbyClient(_http, connection);
        _settings.Remember(server, account);
    }

    private void EndSession(string reason)
    {
        if (_client is null) return;
        _client = null;
        Log.Info(Category, reason);
        SignedOut?.Invoke(this, reason);
    }

    private void Persist()
    {
        try
        {
            _store.Save(_settings);
        }
        catch (Exception error)
        {
            // A failed save must not invalidate a successful login.
            Log.Warn(Category, "登录成功但保存设置失败", error);
        }
    }

    public void Dispose()
    {
        _reauthenticationGate.Dispose();
        _http.Dispose();
    }
}
