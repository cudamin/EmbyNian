using EmbyNian.Emby;

namespace EmbyNian.Shell.Diagnostics;

/// <summary>
/// Turns an exception into one line worth putting in front of a user.
/// <para>
/// The same switch the WinForms shell keeps in <c>AppView.Describe</c>. It is worth having in both
/// places rather than in Core: the wording is a UI decision, and this shell says things the other one
/// has no way to ("请检查地址和网络" reads differently under an InfoBar than in a toast).
/// </para>
/// </summary>
internal static class Failure
{
    public static string Describe(Exception error) => error switch
    {
        EmbyUnreachableException => "无法连接到服务器，请检查地址和网络",
        EmbyTokenExpiredException => "登录状态已过期，请重新登录",
        EmbyAuthenticationException authentication => authentication.Message,
        EmbyApiException api => api.Message,
        UnauthorizedAccessException => "没有访问权限",
        IOException io => io.Message,
        OperationCanceledException => "操作已取消",
        _ => error.Message
    };
}
