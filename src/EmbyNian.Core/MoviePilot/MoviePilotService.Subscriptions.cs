using System.Globalization;

namespace EmbyNian.MoviePilot;

public sealed partial class MoviePilotService
{
    public async Task<IReadOnlyList<MoviePilotSubscription>> SubscriptionsAsync(CancellationToken cancellationToken)
    {
        var identity = CaptureIdentity();
        // v3 的 page/count 均省略时返回全量，避免主页只显示默认分页的第一批。
        var data = await CallAsync((address, token) => client.GetAsync(address, token, "subscribe/", cancellationToken),
            cancellationToken, identity).ConfigureAwait(false);
        var stamp = TransferConnectionStamp(identity);
        return MoviePilotSubscription.ParseList(data).Select(item => item with
        {
            ConnectionStamp = stamp,
            Media = item.Media with { ConnectionStamp = stamp }
        }).ToArray();
    }

    public bool IsCurrentSubscription(MoviePilotSubscription subscription) => IsCurrentConnection(subscription.ConnectionStamp);

    public bool SubscriptionNeedsReview(MoviePilotSubscription subscription) =>
        OperationState(SubscriptionOperationKey(subscription)) == MoviePilotOperationState.Uncertain;

    /// <summary>调用方必须先由用户核对服务器状态并确认；只解除客户端限制，不发送远程请求。</summary>
    public void ConfirmSubscriptionReviewed(MoviePilotSubscription subscription)
    {
        RequireResultConnection(subscription.ConnectionStamp);
        var key = SubscriptionOperationKey(subscription);
        lock (_operationGate)
            if (_operations.GetValueOrDefault(key) == MoviePilotOperationState.Uncertain) _operations.Remove(key);
    }

    private static string SubscriptionOperationKey(MoviePilotSubscription subscription) => OperationKey(
        subscription.ConnectionStamp, "subscription-management", subscription.Id.ToString(CultureInfo.InvariantCulture));

    public async Task<MoviePilotSubscription> SubscriptionAsync(MoviePilotSubscription subscription, CancellationToken cancellationToken)
    {
        var identity = RequireResultConnection(subscription.ConnectionStamp);
        var data = await CallAsync((address, token) => client.GetAsync(address, token,
            "subscribe/" + subscription.Id.ToString(CultureInfo.InvariantCulture), cancellationToken), cancellationToken, identity).ConfigureAwait(false);
        var current = MoviePilotSubscription.Parse(data);
        if (current is null || current.Id != subscription.Id) throw new MoviePilotException("订阅已不存在，请返回主页刷新");
        return current with
        {
            ConnectionStamp = subscription.ConnectionStamp,
            Media = current.Media with { ConnectionStamp = subscription.ConnectionStamp }
        };
    }

    public async Task<MoviePilotSubscriptionFiles> SubscriptionFilesAsync(MoviePilotSubscription subscription, CancellationToken cancellationToken)
    {
        var identity = RequireResultConnection(subscription.ConnectionStamp);
        var data = await CallAsync((address, token) => client.GetAsync(address, token,
            "subscribe/files/" + subscription.Id.ToString(CultureInfo.InvariantCulture), cancellationToken), cancellationToken, identity).ConfigureAwait(false);
        return MoviePilotSubscriptionFiles.Parse(data, subscription);
    }

    public async Task UpdateSubscriptionAsync(MoviePilotSubscription subscription, MoviePilotSubscriptionEdit edit, CancellationToken cancellationToken)
    {
        var body = edit.Changes(subscription);
        if (body.Count == 1) return;
        await MutateSubscriptionAsync(subscription, HttpMethod.Put, "subscribe/", body, cancellationToken).ConfigureAwait(false);
    }

    public Task ManageSubscriptionAsync(MoviePilotSubscription subscription, MoviePilotSubscriptionAction action, CancellationToken cancellationToken)
    {
        var id = subscription.Id.ToString(CultureInfo.InvariantCulture);
        var (method, path) = action switch
        {
            MoviePilotSubscriptionAction.Pause => (HttpMethod.Put, $"subscribe/status/{id}?state=S"),
            MoviePilotSubscriptionAction.Resume => (HttpMethod.Put, $"subscribe/status/{id}?state=R"),
            MoviePilotSubscriptionAction.Search => (HttpMethod.Post, $"subscribe/search/{id}"),
            MoviePilotSubscriptionAction.Reset => (HttpMethod.Post, $"subscribe/reset/{id}"),
            MoviePilotSubscriptionAction.Delete => (HttpMethod.Delete, $"subscribe/{id}"),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return MutateSubscriptionAsync(subscription, method, path, null, cancellationToken);
    }

    private async Task MutateSubscriptionAsync(MoviePilotSubscription subscription, HttpMethod method, string path,
        object? body, CancellationToken cancellationToken)
    {
        var identity = RequireResultConnection(subscription.ConnectionStamp);
        var key = SubscriptionOperationKey(subscription);
        await WriteAsync(identity, [key], async (address, token) =>
        {
            try { return await client.MutateAsync(address, token, method, path, body, cancellationToken).ConfigureAwait(false); }
            // 官方公开文档的旧版搜索/重置仍用 GET。只有明确 405 拒绝 POST 才退回；超时等不重放。
            catch (MoviePilotException error) when (error.Status == System.Net.HttpStatusCode.MethodNotAllowed && method == HttpMethod.Post)
            { return await client.MutateAsync(address, token, HttpMethod.Get, path, body, cancellationToken).ConfigureAwait(false); }
        }, cancellationToken).ConfigureAwait(false);
        // 明确成功后可继续暂停/恢复/编辑；结果不明仍由 WriteAsync 锁住，禁止自动重放。
        lock (_operationGate) _operations.Remove(key);
    }
}
