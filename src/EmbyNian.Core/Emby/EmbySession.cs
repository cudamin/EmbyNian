using EmbyNian.Configuration;
using EmbyNian.Diagnostics;

namespace EmbyNian.Emby;

/// <summary>当前浏览身份，以及仅能提交到原身份的一次令牌恢复。</summary>
public sealed class EmbySession : IDisposable
{
    private const string Category = "session";

    private readonly EmbyHttp _http;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly CredentialVault _vault;
    private readonly SemaphoreSlim _reauthenticationGate = new(1, 1);
    private readonly object _lifecycleGate = new();

    private EmbyClient? _client;
    private EmbySessionScope.Identity? _identity;
    private List<EmbyItem>? _restoredViews;
    private CancellationTokenSource _scopeLifetime = new();
    private long _generation;
    private long _clientGeneration;
    private long _scopeGeneration;
    private bool _disposed;

    public EmbySession(AppSettings settings, SettingsStore store, CredentialVault vault, DeviceIdentity device)
        : this(settings, store, vault, device, null)
    {
    }

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

    public event EventHandler<string>? SignedOut;

    public EmbyServerGateway Gateway { get; }

    public DeviceIdentity Device { get; }

    public ServerProfile? Server { get; private set; }

    public AccountProfile? Account { get; private set; }

    public bool IsSignedIn => Connection is not null;

    public EmbyConnection? Connection
    {
        get { lock (_lifecycleGate) return _client?.Connection; }
    }

    public EmbyClient Client
    {
        get
        {
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _client ?? throw new InvalidOperationException("尚未登录 Emby");
            }
        }
    }

    public string ServerDisplayName =>
        Connection?.ServerName is { Length: > 0 } name ? name : Server?.Name ?? "";

    /// <summary>固定一场播放或下载的身份；浏览切服不撤销，退出登录和释放会话撤销。</summary>
    public EmbySessionScope Capture()
    {
        lock (_lifecycleGate)
        {
            _ = Client;
            return new EmbySessionScope(this, _identity!, _scopeGeneration, _scopeLifetime.Token);
        }
    }

    public async Task SignInAsync(ServerProfile server, AccountProfile account, string password, string username, bool rememberPassword, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = BeginIdentityChange();
        try
        {
            var apiBase = EmbyServerAddress.Normalize(server.Url);
            var connection = await Gateway.AuthenticateAsync(apiBase, username, password, cancellationToken).ConfigureAwait(false);

            lock (_lifecycleGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanCommit(generation) || !HasAddress(server, apiBase))
                    throw new OperationCanceledException("登录操作已被新的身份选择替代", cancellationToken);

                CommitSignIn(server, account, connection, password, rememberPassword);
            }
        }
        finally
        {
            FinishIdentityChange(generation);
        }
    }

    /// <summary>显式登录和恢复都先占有轮次，较早发出的成功或失败不能改变较晚的选择。</summary>
    private long BeginIdentityChange()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _restoredViews = null;
            return ++_generation;
        }
    }

    private void FinishIdentityChange(long generation)
    {
        lock (_lifecycleGate)
        {
            // 切换失败可继续使用旧浏览身份，但切换前已发出的恢复仍然作废。
            if (CanCommit(generation) && _client is not null) _clientGeneration = generation;
        }
    }

    private bool CanCommit(long generation) => !_disposed && generation == _generation;

    private static bool HasAddress(ServerProfile server, Uri apiBase) =>
        EmbyServerAddress.TryNormalize(server.Url, out var current, out _) && current == apiBase;

    // 调用方持有 lifecycleGate，凭据、当前 client 和最后身份必须一起提交。
    private void CommitSignIn(ServerProfile server, AccountProfile account, EmbyConnection connection, string password, bool rememberPassword)
    {
        account.Username = connection.UserName;
        account.UserId = connection.UserId;
        account.LastSignedIn = DateTimeOffset.Now;
        _vault.SetAccessToken(account, connection.AccessToken);
        _vault.SetPassword(account, password, rememberPassword);
        Adopt(server, account, connection);
        Persist();
    }

    /// <summary>保存令牌失效时用保存密码恢复；整条路径只属于进入时取得的身份轮次。</summary>
    public async Task<bool> TryRestoreAsync(ServerProfile server, AccountProfile account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = BeginIdentityChange();
        try
        {
            return await RestoreAsync(server, account, generation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            FinishIdentityChange(generation);
        }
    }

    private async Task<bool> RestoreAsync(ServerProfile server, AccountProfile account, long generation, CancellationToken cancellationToken)
    {
        var savedToken = account.ProtectedAccessToken;
        var token = _vault.GetAccessToken(account);
        var userId = account.UserId;
        var username = account.Username;
        if (token.Length == 0 || userId.Length == 0) return false;
        if (!EmbyServerAddress.TryNormalize(server.Url, out var apiBase, out _)) return false;

        var connection = new EmbyConnection(apiBase, token, userId, username, server.Name, Device);
        var candidate = new EmbyClient(_http, connection);
        try
        {
            var views = await candidate.GetViewsAsync(cancellationToken).ConfigureAwait(false);
            connection = await NameServerAsync(apiBase, connection, cancellationToken).ConfigureAwait(false);
            lock (_lifecycleGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanCommit(generation) || !HasAddress(server, apiBase)) return false;
                Adopt(server, account, connection);
                _restoredViews = views;
            }

            Log.Info(Category, "已使用保存的令牌恢复登录");
            return true;
        }
        catch (EmbyTokenExpiredException)
        {
            lock (_lifecycleGate)
            {
                if (!CanCommit(generation) || !HasAddress(server, apiBase)) return false;
                if (account.ProtectedAccessToken == savedToken) _vault.ClearAccessToken(account);
            }
            Log.Info(Category, "保存的令牌已失效");
        }
        catch (EmbyApiException error)
        {
            Log.Warn(Category, "无法使用保存的令牌恢复登录", error);
            return false;
        }

        string password;
        bool rememberPassword;
        lock (_lifecycleGate)
        {
            if (!CanCommit(generation) || !HasAddress(server, apiBase) || !account.HasSavedPassword) return false;
            password = _vault.GetPassword(account);
            rememberPassword = account.RememberPassword;
        }

        try
        {
            connection = await Gateway.AuthenticateAsync(apiBase, username, password, cancellationToken).ConfigureAwait(false);
            lock (_lifecycleGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanCommit(generation) || !HasAddress(server, apiBase)) return false;
                CommitSignIn(server, account, connection, password, rememberPassword);
                return true;
            }
        }
        catch (EmbyApiException error)
        {
            Log.Warn(Category, "使用保存的密码自动登录失败", error);
            return false;
        }
    }

    /// <summary>恢复令牌的探针已经取回媒体库，只交给外壳一次。</summary>
    public List<EmbyItem>? TakeRestoredViews()
    {
        lock (_lifecycleGate)
        {
            var views = _restoredViews;
            _restoredViews = null;
            return views;
        }
    }

    public Task<T> ExecuteAsync<T>(Func<EmbyClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        EmbyClient client;
        long generation;
        lock (_lifecycleGate)
        {
            client = Client;
            generation = _clientGeneration;
        }
        return ExecuteWithClientAsync(client, generation, operation, cancellationToken);
    }

    public Task ExecuteAsync(Func<EmbyClient, CancellationToken, Task> operation, CancellationToken cancellationToken) =>
        ExecuteAsync<bool>(async (client, token) =>
        {
            await operation(client, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    internal bool IsCurrent(EmbySessionScope scope)
    {
        lock (_lifecycleGate)
            return !_disposed && scope.Generation == _scopeGeneration
                && _client?.Connection.IsSameIdentityAs(scope.Connection) == true;
    }

    internal async Task<T> ExecuteAsync<T>(EmbySessionScope scope, Func<EmbyClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.Lifetime);
        EmbyClient client;
        long generation;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (scope.Generation != _scopeGeneration) throw new OperationCanceledException("已退出捕获的登录会话", lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (_client?.Connection.IsSameIdentityAs(scope.Connection) == true) scope.Follow(_identity!);
            client = scope.Client;
            generation = _clientGeneration;
        }

        var result = await ExecuteWithClientAsync(client, generation, operation, lifetime.Token).ConfigureAwait(false);
        lifetime.Token.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<T> ExecuteWithClientAsync<T>(EmbyClient client, long generation, Func<EmbyClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation(client, cancellationToken).ConfigureAwait(false);
        }
        catch (EmbyTokenExpiredException)
        {
            var fresh = await TryReauthenticateAsync(client, generation, cancellationToken).ConfigureAwait(false);
            if (fresh is null) throw;
            cancellationToken.ThrowIfCancellationRequested();
            // 重试持有已核对身份的 client，而不是在 await 之后重新读取全局 Client。
            return await operation(fresh, cancellationToken).ConfigureAwait(false);
        }
    }

    private EmbyClient? SameIdentityClient(EmbyClient stale) =>
        !_disposed && !ReferenceEquals(_client, stale)
            && _client?.Connection.IsSameIdentityAs(stale.Connection) == true ? _client : null;

    private async Task<EmbyClient?> TryReauthenticateAsync(EmbyClient stale, long generation, CancellationToken cancellationToken)
    {
        await _reauthenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ServerProfile? server;
            AccountProfile? account;
            string password;
            string savedPassword;
            bool rememberPassword;
            lock (_lifecycleGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_client, stale)) return SameIdentityClient(stale);
                if (!CanCommit(generation)) return null;
                server = Server;
                account = Account;
                savedPassword = account?.ProtectedPassword ?? "";
                password = account is null ? "" : _vault.GetPassword(account);
                rememberPassword = account?.RememberPassword ?? false;
            }

            if (server is null || account is null || savedPassword.Length == 0)
            {
                EndSession("登录状态已过期，请重新登录", stale, generation);
                return null;
            }

            EmbyConnection connection;
            try
            {
                Log.Info(Category, "令牌过期，正在使用保存的密码重新登录");
                connection = await Gateway.AuthenticateAsync(stale.Connection.ApiBase, stale.Connection.UserName, password, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lock (_lifecycleGate)
                {
                    if (!CanCommit(generation) || !ReferenceEquals(_client, stale)) return SameIdentityClient(stale);
                }
                if (error is not EmbyApiException) throw;
                Log.Warn(Category, "自动重新登录失败", error);
                EndSession("登录状态已过期，请重新登录", stale, generation);
                return null;
            }

            lock (_lifecycleGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanCommit(generation) || !ReferenceEquals(_client, stale)) return SameIdentityClient(stale);
                if (!HasAddress(server, stale.Connection.ApiBase) || account.ProtectedPassword != savedPassword) return null;
                if (connection.IsSameIdentityAs(stale.Connection))
                {
                    CommitSignIn(server, account, connection, password, rememberPassword);
                    return _client;
                }
            }

            EndSession("登录状态已过期，请重新登录", stale, generation);
            return null;
        }
        finally
        {
            // Dispose 不释放这个托管闸门，仍在飞的调用必须能归还它。
            _reauthenticationGate.Release();
        }
    }

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
        _restoredViews = null;
        if (connection.ServerName.Length > 0 && server.HasPlaceholderName) server.Name = connection.ServerName;
        Server = server;
        Account = account;
        _client = new EmbyClient(_http, connection);
        _clientGeneration = _generation;
        if (_identity?.Client.Connection.IsSameIdentityAs(connection) == true)
            _identity.Client = _client;
        else
            _identity = new EmbySessionScope.Identity(_client);
        _settings.Remember(server, account);
    }

    public void SignOut() => EndSession("已退出登录");

    private void EndSession(string reason, EmbyClient? expected = null, long generation = 0)
    {
        CancellationTokenSource lifetime;
        lock (_lifecycleGate)
        {
            if (_disposed || (expected is not null && (!CanCommit(generation) || !ReferenceEquals(_client, expected)))) return;
            lifetime = _scopeLifetime;
            _scopeLifetime = new CancellationTokenSource();
            EndSessionLocked(reason);
        }

        lifetime.Cancel();
        lifetime.Dispose();
    }

    private void EndSessionLocked(string reason)
    {
        var wasSignedIn = _client is not null;
        _client = null;
        _identity = null;
        _restoredViews = null;
        _generation++;
        _scopeGeneration++;
        if (!wasSignedIn) return;

        if (Account is { } account) _vault.ClearAccessToken(account);
        Persist();
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
            Log.Warn(Category, "保存登录设置失败", error);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource lifetime;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _scopeGeneration++;
            _client = null;
            _identity = null;
            _restoredViews = null;
            lifetime = _scopeLifetime;
        }

        lifetime.Cancel();
        lifetime.Dispose();
        _http.Dispose();
    }
}
