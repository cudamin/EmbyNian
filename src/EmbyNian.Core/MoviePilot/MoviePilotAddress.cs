namespace EmbyNian.MoviePilot;

/// <summary>
/// Where a MoviePilot service lives, as the settings page can express it.
/// <para>
/// Modelled on <see cref="Emby.EmbyServerAddress"/> and for the same reason: the address a user types and the
/// address an API call wants are two different things, and the conversion has exactly one right answer per
/// input, so it belongs in Core where a test can hold it down rather than in a settings row.
/// </para>
/// <para>
/// MoviePilot serves two ports — the web UI and the API are separate listeners (3000 and 3001 by default).
/// Only the API one is any use here, and users reasonably paste the address they have in the browser. So a
/// bare host, a path, or the UI port all have to be tidied into something callable, and
/// <see cref="TryNormalize"/> is that tidying.
/// </para>
/// </summary>
public static class MoviePilotAddress
{
    /// <summary>The port MoviePilot's API listens on when nothing says otherwise.</summary>
    public const int DefaultApiPort = 3001;

    /// <summary>
    /// The scheme assumed when the user types a bare <c>host:port</c>. Plain HTTP is the right guess rather
    /// than HTTPS: this is a service on a home LAN, and assuming TLS would fail every call against a server
    /// that never offered it.
    /// </summary>
    private const string DefaultScheme = "http";

    /// <summary>
    /// Turns what a user typed into the base URI every request is built on, or explains why it cannot be.
    /// <para>
    /// A blank string is accepted and yields <c>null</c> rather than an error, because 「not configured yet」
    /// is the normal state of this setting and the row that shows it must be perfectly happy with it.
    /// </para>
    /// </summary>
    public static bool TryNormalize(string? text, out Uri? address, out string error)
    {
        address = null;
        error = "";

        if (string.IsNullOrWhiteSpace(text))
        {
            // Not a failure: it means the user has not filled this in, and there is nothing to call.
            return true;
        }

        var trimmed = text.Trim();

        // A scheme is required for Uri to treat this as absolute; anything without one gets http://, which is
        // what lets "192.0.2.10:3001" parse as host plus port instead of as a relative path.
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = DefaultScheme + "://" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            error = "地址看不出来是一个网址，像这样写：192.168.1.5:3001";
            return false;
        }

        if (parsed.Scheme is not ("http" or "https"))
        {
            error = $"只认 http 和 https，不认 {parsed.Scheme}";
            return false;
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            error = "地址里没有主机名";
            return false;
        }

        address = new UriBuilder(parsed.Scheme, parsed.Host, Port(parsed)).Uri;
        return true;
    }

    /// <summary>
    /// The port to actually call. An explicitly typed port is kept as-is — including 3000, which is the web
    /// UI's port: someone running the API there on purpose is a configuration this has no business
    /// overriding, and the connection test is what tells him he guessed wrong.
    /// <para>
    /// Only a port nobody typed moves, which is what <see cref="Uri.IsDefaultPort"/> reports.
    /// </para>
    /// </summary>
    private static int Port(Uri parsed) => parsed.IsDefaultPort ? DefaultApiPort : parsed.Port;

    /// <summary>What to show back in the settings box after a successful normalisation.</summary>
    public static string ToDisplayString(Uri address) =>
        address.IsDefaultPort || address.Port == DefaultApiPort
            ? address.Host
            : $"{address.Host}:{address.Port}";

    /// <summary>Builds the absolute URI for one API path, e.g. <c>api/v1/dashboard/system</c>.</summary>
    public static Uri Combine(Uri apiBase, string path)
    {
        var trimmed = path.TrimStart('/');
        return new Uri(apiBase, trimmed);
    }
}
