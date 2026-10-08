using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbyNian.Emby;

public sealed partial class EmbyClient
{
    public Task<JsonObject> GetServerConfigurationAsync(CancellationToken cancellationToken) =>
        http.GetJsonAsync<JsonObject>(EmbyUrl.Combine(ApiBase, "System/Configuration"), Context, cancellationToken);

    public Task SaveServerConfigurationAsync(JsonObject configuration, CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "System/Configuration"), configuration, Context, cancellationToken);

    public Task RestartServerAsync(CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "System/Restart"), null, Context, cancellationToken);

    public Task ShutdownServerAsync(CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "System/Shutdown"), null, Context, cancellationToken);

    /// <summary>
    /// 只读控制台。先确认系统信息，再并行读取会话和最近活动；没有管理员权限时仅展示公开信息。
    /// 401 留给 EmbySession 恢复登录，其他分区错误不抹掉已经成功的服务器信息。
    /// </summary>
    public async Task<EmbyDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken)
    {
        var system = await GetSystemInfoAsync(cancellationToken).ConfigureAwait(false);
        if (system.IsRestricted)
        {
            const string message = "需要服务器管理员权限";
            return new(system, new(null, message), new(null, message));
        }

        var sessions = ReadDashboardSectionAsync<List<EmbyDashboardSession>>(
            EmbyUrl.Combine(ApiBase, "Sessions"), cancellationToken);
        var activity = ReadDashboardSectionAsync<EmbyActivityResult>(
            EmbyUrl.Combine(ApiBase, "System/ActivityLog/Entries", ("StartIndex", "0"), ("Limit", "20")), cancellationToken);
        await Task.WhenAll(sessions, activity).ConfigureAwait(false);
        return new(system, await sessions.ConfigureAwait(false), await activity.ConfigureAwait(false));
    }

    private async Task<DashboardSection<T>> ReadDashboardSectionAsync<T>(Uri url, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var value = await http.GetJsonAsync<T>(url, Context, cancellationToken).ConfigureAwait(false);
            return new(value, null);
        }
        catch (EmbyApiException error) when (error.StatusCode == HttpStatusCode.Forbidden)
        {
            return new(null, "需要服务器管理员权限");
        }
        catch (EmbyApiException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return new(null, "当前服务器未提供此项信息");
        }
        catch (EmbyApiException error) when (error.StatusCode != HttpStatusCode.Unauthorized)
        {
            return new(null, "读取失败，请稍后刷新");
        }
        catch (HttpRequestException)
        {
            return new(null, "连接失败，请检查网络后刷新");
        }
        catch (JsonException)
        {
            return new(null, "服务器返回的数据无法识别");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(null, "读取超时，请稍后刷新");
        }
    }
}
