namespace EmbyNian.Emby;

/// <summary>跨页面存活的固定身份。切服可用旧令牌收尾，退出登录后不可继续。</summary>
public sealed class EmbySessionScope
{
    private readonly EmbySession _session;
    private Identity _identity;

    internal EmbySessionScope(EmbySession session, Identity identity, long generation, CancellationToken lifetime)
    {
        _session = session;
        _identity = identity;
        Connection = identity.Client.Connection;
        Generation = generation;
        Lifetime = lifetime;
    }

    /// <summary>捕获时的连接快照；令牌恢复不会改写这个值。</summary>
    public EmbyConnection Connection { get; }

    public bool IsCurrent => _session.IsCurrent(this);

    /// <summary>下载队列可放弃未开始的文件；播放收尾不使用这个限制。</summary>
    public void ThrowIfNotCurrent()
    {
        if (!IsCurrent) throw new OperationCanceledException("任务所属的登录身份已改变");
    }

    internal EmbyClient Client => _identity.Client;

    internal void Follow(Identity identity) => _identity = identity;

    internal sealed class Identity(EmbyClient client)
    {
        public EmbyClient Client { get; set; } = client;
    }

    internal long Generation { get; }

    internal CancellationToken Lifetime { get; }

    public Task<T> ExecuteAsync<T>(Func<EmbyClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        _session.ExecuteAsync(this, operation, cancellationToken);

    public Task ExecuteAsync(Func<EmbyClient, CancellationToken, Task> operation, CancellationToken cancellationToken) =>
        ExecuteAsync<bool>(async (client, token) =>
        {
            await operation(client, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
}
