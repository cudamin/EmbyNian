using EmbyNian.Configuration;

namespace EmbyNian.Emby;

/// <summary>密码只属于输入时的服务器和账号，不跟随地址框或用户列表迁移。</summary>
public static class EmbySignInIdentity
{
    public static bool SameAddress(string? left, string? right) =>
        EmbyServerAddress.TryNormalize(left ?? "", out var first, out _)
            && EmbyServerAddress.TryNormalize(right ?? "", out var second, out _)
            && first == second;

    public static bool SameUsername(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    public static AccountProfile? SavedAccount(IEnumerable<ServerProfile> servers, string address, string username) =>
        servers.FirstOrDefault(server => SameAddress(server.Url, address))?.Accounts
            .FirstOrDefault(account => SameUsername(account.Username, username));

    public static bool IsPasswordless(string address, Uri? usersAddress, string username, EmbyUser? selected) =>
        usersAddress is not null && SameAddress(address, usersAddress.AbsoluteUri)
            && selected is { HasPassword: false } && SameUsername(selected.Name, username);

    public static string PasswordFor(
        string address,
        string username,
        string password,
        string passwordAddress,
        string passwordUsername,
        bool passwordless) =>
        !passwordless && SameAddress(address, passwordAddress) && SameUsername(username, passwordUsername)
            ? password : "";
}
