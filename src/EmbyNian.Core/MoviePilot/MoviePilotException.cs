using System.Net;

namespace EmbyNian.MoviePilot;

/// <summary>
/// Anything MoviePilot said no to. Shaped after <see cref="Emby.EmbyApiException"/> so the two sibling
/// clients fail in the same vocabulary: a message already written for the user to read, plus the status and
/// body for the log.
/// </summary>
public class MoviePilotException : Exception
{
    public MoviePilotException(string message, HttpStatusCode? status = null, string? body = null, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        Body = body;
    }

    public HttpStatusCode? Status { get; }

    /// <summary>The response body as it arrived, for the log only — never shown in the UI as-is.</summary>
    public string? Body { get; }
}

/// <summary>Nothing answered at that address, or it answered too slowly. The one failure worth retrying.</summary>
public sealed class MoviePilotUnreachableException(string message, Exception? inner = null)
    : MoviePilotException(message, inner: inner);

/// <summary>
/// A 401 on something other than the sign-in call, i.e. the token has run out.
/// <para>
/// Its own type because the fix differs from every other failure: the address is fine and the credentials
/// were fine — the JWT simply has an expiry, so the client signs in again and replays.
/// </para>
/// </summary>
public sealed class MoviePilotTokenExpiredException(string message)
    : MoviePilotException(message, HttpStatusCode.Unauthorized);
