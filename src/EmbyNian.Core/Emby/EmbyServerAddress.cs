namespace EmbyNian.Emby;

/// <summary>
/// Turns whatever the user typed into a usable API base address. Accepts
/// "192.168.1.5:8896", "http://host/emby", "https://host/media/" and so on.
/// </summary>
public static class EmbyServerAddress
{
    /// <summary>The API base, always ending in "/emby/" so relative request paths resolve correctly.</summary>
    public static Uri Normalize(string input)
    {
        if (!TryNormalize(input, out var address, out var error)) throw new ArgumentException(error, nameof(input));
        return address;
    }

    public static bool TryNormalize(string input, out Uri address, out string error)
    {
        address = null!;
        error = "";

        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            error = "请填写服务器地址";
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            error = "服务器地址格式不正确";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "服务器地址只支持 http 或 https";
            return false;
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            error = "服务器地址缺少主机名";
            return false;
        }

        var path = parsed.AbsolutePath.TrimEnd('/');
        // A user who pastes the address out of a browser usually includes /emby already.
        if (path.EndsWith("/emby", StringComparison.OrdinalIgnoreCase)) path = path[..^5];
        else if (path.Equals("/emby", StringComparison.OrdinalIgnoreCase)) path = "";

        var builder = new UriBuilder(parsed.Scheme, parsed.Host, parsed.Port)
        {
            Path = path + "/emby/",
            Query = "",
            Fragment = ""
        };

        address = builder.Uri;
        return true;
    }

    /// <summary>The address without the "/emby/" suffix, for display back to the user.</summary>
    public static string ToDisplayString(Uri apiBase)
    {
        var text = apiBase.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return text.EndsWith("/emby", StringComparison.OrdinalIgnoreCase) ? text[..^5] : text;
    }
}
