namespace EmbyNian.Emby;

public static class EmbyServerAdministration
{
    /// <summary>
    /// Emby 4.10 的改名接口更新整份配置。保留服务器返回的未知字段，只改 ServerName；
    /// 读取与提交之间重新核对身份，离页取消或切服后绝不提交旧配置。
    /// </summary>
    public static async Task RenameAsync(EmbySessionScope scope, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        scope.ThrowIfNotCurrent();
        var configuration = await scope.ExecuteAsync((client, token) => client.GetServerConfigurationAsync(token), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        scope.ThrowIfNotCurrent();
        configuration["ServerName"] = name.Trim();
        await scope.ExecuteAsync((client, token) => client.SaveServerConfigurationAsync(configuration, token), cancellationToken)
            .ConfigureAwait(false);
    }
}
