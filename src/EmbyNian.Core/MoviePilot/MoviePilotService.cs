using EmbyNian.Configuration;
using EmbyNian.Diagnostics;

namespace EmbyNian.MoviePilot;

/// <summary>
/// 搜索页那一半 MoviePilot 功能的入口：按需登录、缓存 JWT，对外只给「搜片」和「订阅」两件事。
/// <para>
/// 存在的理由是 <see cref="MoviePilotClient"/> 自己不记会话 —— 它每次都要一个 token，而登录换来的 JWT 是短命的。
/// 设置卡的「测试连接」每次现登录一次就完事，但搜索页会一次次地问，所以把「有没有登录、token 过没过期、过了怎么
/// 重登重放」收在这里一处，视图模型只管调 <see cref="SearchAsync"/> / <see cref="SubscribeAsync"/>。
/// </para>
/// <para>
/// 单例（见 <c>ShellServices</c>）：一次登录的 token 要跨多次搜索复用。它只依赖 Core 类型（客户端、凭据、设置），
/// 没有 UI，所以住在 Core、注册在 Shell，和 <c>PlaybackService</c> 一个路子。名字避开已有的
/// <see cref="MoviePilotSession"/>（那是「一次登录的结果」这个记录）。
/// </para>
/// </summary>
public sealed partial class MoviePilotService(
    MoviePilotClient client,
    MoviePilotCredentials credentials,
    AppSettings settings)
{
    private const string Category = "moviepilot";

    /// <summary>一次只让一趟登录在飞，省得同时来两次搜索各登一次。</summary>
    private readonly SemaphoreSlim _signIn = new(1, 1);

    private readonly object _sessionGate = new();
    private CachedSession? _session;

    // 只比较密文快照；明文仅在实际登录时解开，不作为缓存键或诊断文字。
    private sealed record SessionIdentity(Uri ApiBase, string Username, string ProtectedPassword);

    private sealed record CachedSession(SessionIdentity Identity, MoviePilotSession Session);

    /// <summary>用户在设置里开没开这项。搜索页拿它决定那个分段切换在不在。</summary>
    public bool Enabled => settings.MoviePilot.Enabled;

    /// <summary>
    /// 搜一个关键字。空词直接回空，不发请求。<c>media/search</c> 回的是裸数组，交给
    /// <see cref="MoviePilotMediaParser"/> 拆。取前 30 条（一屏够看），这一版不翻页。
    /// </summary>
    public async Task<IReadOnlyList<MoviePilotMedia>> SearchAsync(string term, CancellationToken cancellationToken)
    {
        var keyword = term.Trim();
        if (keyword.Length == 0) return [];

        var path = $"media/search?title={Uri.EscapeDataString(keyword)}&type=media&page=1&count=30";
        var data = await CallAsync((apiBase, token) =>
            client.GetAsync(apiBase, token, path, cancellationToken), cancellationToken).ConfigureAwait(false);

        var results = MoviePilotMediaParser.Parse(data);
        Log.Info(Category, $"MoviePilot 搜「{keyword}」：{results.Count} 条");
        return results;
    }

    /// <summary>
    /// 在 MoviePilot 上给这条结果建订阅。对外副作用（可能触发下载），调用方须先向用户二次确认。失败时抛出的异常
    /// 里那句话已经是给人看的（客户端会优先带上服务器自己的话）。
    /// </summary>
    public async Task SubscribeAsync(MoviePilotMedia media, CancellationToken cancellationToken)
    {
        if (!media.CanSubscribe)
            throw new MoviePilotException("这条结果缺少可订阅的身份信息，换一条试试");

        var body = MoviePilotMediaParser.SubscribeBody(media);
        await CallAsync((apiBase, token) =>
            client.PostAsync(apiBase, token, "subscribe/", body, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        Log.Info(Category, $"已在 MoviePilot 订阅《{media.Title}》");
    }

    /// <summary>
    /// 搜一部片的可下载资源（种子）。走 <c>search/media/{id}</c> 精确搜 —— MoviePilot 现去各站点捞，可能要等
    /// 几十秒（客户端为此把响应上限放到 120 秒）。身份对齐了才搜，缺了直接回空。
    /// </summary>
    public async Task<IReadOnlyList<MoviePilotResource>> SearchResourcesAsync(
        MoviePilotMedia media,
        CancellationToken cancellationToken)
    {
        if (!media.CanSubscribe) return [];

        var path = $"search/media/{Uri.EscapeDataString(media.MediaId!)}?media_source={Uri.EscapeDataString(media.MediaSource!)}";
        var data = await CallAsync((apiBase, token) =>
            client.GetAsync(apiBase, token, path, cancellationToken), cancellationToken).ConfigureAwait(false);

        var resources = MoviePilotMediaParser.ParseResources(data, media.MediaSource, media.MediaId);
        Log.Info(Category, $"MoviePilot 为《{media.Title}》搜到 {resources.Count} 个资源");
        return resources;
    }

    /// <summary>
    /// 按关键字搜可下载的资源（种子），走 <c>search/title</c> —— MoviePilot 的「模糊搜索资源」：它现去各站点捞、
    /// 直接回一片种子（和 <see cref="SearchResourcesAsync"/> 同一套 <c>Context</c> 形状），所以同样可能等几十秒。
    /// 空词直接回空、不发请求。关键字里带 <c>S01</c>／<c>E01</c> 由调用方（<see cref="MoviePilotVersionQuery"/>）拼好，
    /// 好让站点搜索把范围收到那一季／那一集。
    /// </summary>
    public async Task<IReadOnlyList<MoviePilotResource>> SearchByKeywordAsync(
        string keyword,
        CancellationToken cancellationToken)
    {
        var text = keyword.Trim();
        if (text.Length == 0) return [];

        var path = $"search/title?keyword={Uri.EscapeDataString(text)}&page=0";
        var data = await CallAsync((apiBase, token) =>
            client.GetAsync(apiBase, token, path, cancellationToken), cancellationToken).ConfigureAwait(false);

        // 关键字搜出来的种子，身份对（media_source/media_id）由种子自己带 —— 这里传 null，让解析器从 torrent_info
        // 里认（见 ParseResources），下载时照样能带上让服务器认片。
        var resources = MoviePilotMediaParser.ParseResources(data, null, null);
        Log.Info(Category, $"MoviePilot 关键词搜「{text}」：{resources.Count} 个资源");
        return resources;
    }

    /// <summary>
    /// 把一个种子加进 MoviePilot 的下载器（<c>download/add</c>）。对外副作用（真的开始下载），调用方须先二次确认。
    /// 整份 <c>torrent_info</c> 原样回传，下载器与保存路径用 MoviePilot 的默认。
    /// </summary>
    public async Task DownloadAsync(MoviePilotResource resource, CancellationToken cancellationToken)
    {
        var body = MoviePilotMediaParser.DownloadBody(resource);
        await CallAsync((apiBase, token) =>
            client.PostAsync(apiBase, token, "download/add", body, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        Log.Info(Category, $"已把资源「{resource.Title}」加入 MoviePilot 下载");
    }

    /// <summary>
    /// 下载管理的那一眼（<c>GET download/</c>）：正在下载的每一行，识别入库留下的媒体名和海报都带着。
    /// 首页的「正在下载」一排每几秒问一次的就是这一条 —— 所以计数只在 Debug 级记（Info 级五秒一条，日志就成了流水账）。
    /// </summary>
    public async Task<IReadOnlyList<MoviePilotDownloadTask>> DownloadingAsync(CancellationToken cancellationToken)
    {
        var data = await CallAsync((apiBase, token) =>
            client.GetAsync(apiBase, token, "download/", cancellationToken), cancellationToken).ConfigureAwait(false);

        var tasks = MoviePilotDownload.Parse(data);
        Log.Debug(Category, $"MoviePilot 正在下载：{tasks.Count} 个任务");
        return tasks;
    }

    /// <summary>
    /// 经 MoviePilot 取一张图（<c>system/cache/image</c>）。这个接口不认 Bearer 头，认的是网页端那枚资源 Cookie
    /// —— 而那枚 Cookie 由之前任何一趟带 Bearer 的 API 调用顺手种进客户端的 Cookie 罐子（见
    /// <see cref="MoviePilotClient"/> 构造器那段），轮询先于取图，次序天然是对的。服务器自己够得着图源
    /// （TMDB）还开着磁盘缓存，客户端直连不上图源时这条路是唯一能出图的。取不到抛异常，由调用方决定兜底。
    /// </summary>
    public async Task<byte[]> FetchImageAsync(string url, CancellationToken cancellationToken)
    {
        var apiBase = ResolveAddress();
        var proxy = MoviePilotAddress.Combine(
            apiBase, $"api/v1/system/cache/image?url={Uri.EscapeDataString(url)}");
        return await client.GetBytesAsync(proxy, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>直连取图：客户端自己够得着图源时最快的一条，也不惊动服务器。</summary>
    public Task<byte[]> FetchImageDirectAsync(string url, CancellationToken cancellationToken) =>
        client.GetBytesAsync(new Uri(url), cancellationToken);

    /// <summary>
    /// 一趟需要登录态的调用：先拿到（必要时现登录）会话，遇到令牌过期就作废、重登一次再重放。只重放一次 —— 重登
    /// 之后还 401 那是账号或权限的事，不是过期，再试也没用。泛型：整理那一半要整份回执（含失败的），不只 data。
    /// </summary>
    private async Task<T> CallAsync<T>(
        Func<Uri, string, Task<T>> call,
        CancellationToken cancellationToken)
    {
        var identity = CaptureIdentity();
        var session = await EnsureSessionAsync(identity, cancellationToken).ConfigureAwait(false);
        CheckIdentity(identity, cancellationToken);
        try
        {
            var result = await call(identity.ApiBase, session.Session.AccessToken).ConfigureAwait(false);
            CheckIdentity(identity, cancellationToken);
            return result;
        }
        catch (MoviePilotTokenExpiredException)
        {
            lock (_sessionGate)
            {
                CheckIdentity(identity, cancellationToken);
                if (ReferenceEquals(_session, session)) _session = null;
            }
            var fresh = await EnsureSessionAsync(identity, cancellationToken).ConfigureAwait(false);
            CheckIdentity(identity, cancellationToken);
            var result = await call(identity.ApiBase, fresh.Session.AccessToken).ConfigureAwait(false);
            CheckIdentity(identity, cancellationToken);
            return result;
        }
    }

    /// <summary>设置里的地址归一化成 API base，或用一句人话说明为什么不行。</summary>
    private Uri ResolveAddress()
    {
        if (!MoviePilotAddress.TryNormalize(settings.MoviePilot.Url, out var address, out var error))
            throw new MoviePilotException(error);

        return address ?? throw new MoviePilotException("先在设置里填上 MoviePilot 的服务地址");
    }

    private SessionIdentity CaptureIdentity()
    {
        var moviePilot = settings.MoviePilot;
        if (!MoviePilotAddress.TryNormalize(moviePilot.Url, out var apiBase, out var error))
            throw new MoviePilotException(error);
        if (apiBase is null) throw new MoviePilotException("先在设置里填上 MoviePilot 的服务地址");
        return new SessionIdentity(apiBase, moviePilot.Username?.Trim() ?? "", moviePilot.ProtectedPassword);
    }

    private void CheckIdentity(SessionIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = settings.MoviePilot;
        if (!MoviePilotAddress.TryNormalize(current.Url, out var apiBase, out _) || identity.ApiBase != apiBase
            || !string.Equals(identity.Username, current.Username?.Trim(), StringComparison.Ordinal)
            || identity.ProtectedPassword != current.ProtectedPassword)
            throw new OperationCanceledException("MoviePilot 登录配置已改变，请重新操作", cancellationToken);
    }

    private async Task<CachedSession> EnsureSessionAsync(SessionIdentity identity, CancellationToken cancellationToken)
    {
        lock (_sessionGate)
        {
            CheckIdentity(identity, cancellationToken);
            if (_session is { } cached && cached.Identity == identity) return cached;
            _session = null;
        }

        await _signIn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sessionGate)
            {
                CheckIdentity(identity, cancellationToken);
                if (_session is { } cached && cached.Identity == identity) return cached;
            }

            var password = credentials.GetPassword(identity.ProtectedPassword);
            if (identity.Username.Length == 0 || password.Length == 0)
                throw new MoviePilotException("MoviePilot 还没填用户名或密码，先去设置里连一下");

            MoviePilotSession session;
            try
            {
                session = await client.SignInAsync(identity.ApiBase, identity.Username, password, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                CheckIdentity(identity, cancellationToken);
                throw;
            }
            lock (_sessionGate)
            {
                CheckIdentity(identity, cancellationToken);
                _session = new CachedSession(identity, session);
                return _session;
            }
        }
        finally
        {
            _signIn.Release();
        }
    }
}
