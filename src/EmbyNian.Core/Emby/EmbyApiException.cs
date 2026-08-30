using System.Net;

namespace EmbyNian.Emby;

public class EmbyApiException : Exception
{
    public EmbyApiException(string message, HttpStatusCode? statusCode = null, string? responseBody = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ResponseBody { get; }
}

/// <summary>Username or password rejected while signing in.</summary>
public sealed class EmbyAuthenticationException(string message, HttpStatusCode? statusCode = null, string? responseBody = null)
    : EmbyApiException(message, statusCode, responseBody);

/// <summary>
/// The saved access token is no longer valid. Callers re-authenticate once with the
/// stored password before surfacing this to the user.
/// </summary>
public sealed class EmbyTokenExpiredException(string message = "登录状态已过期，需要重新登录")
    : EmbyApiException(message, HttpStatusCode.Unauthorized);

/// <summary>The server could not be reached at all (DNS, refused, timeout).</summary>
public sealed class EmbyUnreachableException(string message, Exception? inner = null)
    : EmbyApiException(message, null, null, inner);
