using System.Net;

namespace EmbyNian.Emby;

/// <summary>认证只属于最初来源；一旦离开，整条跳转链都不能重新获得认证。</summary>
internal static class EmbyHttpRedirect
{
    internal const int Limit = 5;

    internal static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    internal static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme && left.Port == right.Port
            && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase);

    internal static void Validate(Uri url)
    {
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || url.UserInfo.Length != 0)
            throw new EmbyApiException("请求地址只支持不含凭据的 HTTP 或 HTTPS 地址");
    }

    internal static Uri Next(Uri current, Uri? location, HttpMethod method, HttpStatusCode status, bool authenticationAttempt)
    {
        if (location is null || !Uri.TryCreate(current, location, out var next))
            throw new EmbyApiException("服务器返回了无效的重定向地址", status);

        Validate(next);
        if (current.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
            throw new EmbyApiException("不允许从 HTTPS 重定向到 HTTP", status);

        var resource = method == HttpMethod.Get || method == HttpMethod.Head;
        if (!SameOrigin(current, next) && (authenticationAttempt || !resource))
            throw new EmbyApiException("不允许向其他来源重发登录或写入请求", status);

        // 只有 307/308 明确保留方法和正文；不把写入悄悄改成 GET，也不猜测正文能否重放。
        if (!resource && status is not (HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            throw new EmbyApiException("写入请求仅支持同源 307 或 308 重定向", status);

        return next;
    }
}
