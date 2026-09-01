using EmbyNian.Emby;

namespace EmbyNian.Diagnostics;

/// <summary>
/// 把一个异常变成一句能摆在用户面前的话。
/// <para>
/// 在 Core 而不是在外壳里，是为了能被钉住 —— 这一句是用户在出错时唯一看得见的东西，而它出错的样子恰恰是「屏上有
/// 一句话，只是那句话不对」：一次请求超时说成「操作已取消」，用户会以为是自己点了别处；一个过期的令牌说成
/// 「无法连接到服务器」，用户会去查网线，而该做的是重新登录一次。截图看不出对错，日志里那一行是英文原文。
/// </para>
/// <para>
/// 从前它在外壳里，理由写着「措辞是界面的决定，WinForms 那个外壳说的话不一样，所以两处各留一份」。那个理由已经
/// 不成立：WinForms 外壳早就没了（2026-09-02 连 `legacy/v1/` 一起从工作树里删掉），现在只有一个外壳，也就只该有
/// 一份措辞。
/// </para>
/// </summary>
public static class Failure
{
    /// <summary>
    /// 一句话。次序是要紧的：<see cref="EmbyUnreachableException"/> 和
    /// <see cref="EmbyTokenExpiredException"/> 都是 <see cref="EmbyApiException"/> 的子类，排在它后面就永远轮不到，
    /// 用户看到的会是那两种情况下毫无意义的服务器原文。
    /// </summary>
    public static string Describe(Exception error) => error switch
    {
        EmbyUnreachableException => "无法连接到服务器，请检查地址和网络",
        EmbyTokenExpiredException => "登录状态已过期，请重新登录",

        // 这两种用服务器（或者登录握手）自己那句话：「用户名或密码不正确」比任何改写都准确，而 API 那一层的消息
        // 里带着状态码和服务器返回的正文，出错时唯一有用的线索就在里面。
        EmbyAuthenticationException authentication => authentication.Message,
        EmbyApiException api => api.Message,

        UnauthorizedAccessException => "没有访问权限",
        IOException io => io.Message,
        OperationCanceledException => "操作已取消",
        _ => error.Message
    };
}
