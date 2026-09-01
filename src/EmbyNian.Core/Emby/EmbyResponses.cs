using System.Text.Json.Serialization;

namespace EmbyNian.Emby;

public sealed class ItemsResult
{
    public List<EmbyItem> Items { get; set; } = [];

    public int TotalRecordCount { get; set; }
}

public sealed class EmbyUser
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string? ServerId { get; set; }

    public bool HasPassword { get; set; }

    public string? PrimaryImageTag { get; set; }
}

public sealed class AuthenticationResult
{
    public string AccessToken { get; set; } = "";

    public string? ServerId { get; set; }

    public EmbyUser? User { get; set; }
}

public sealed class PublicSystemInfo
{
    public string? ServerName { get; set; }

    public string? Version { get; set; }

    public string? Id { get; set; }

    public string? OperatingSystem { get; set; }
}

/// <summary>
/// What the 服务器 page reads out of <c>/System/Info</c>. That endpoint is administrators-only; a
/// normal account gets the public subset instead, which uses the same field names, so one class
/// covers both and <see cref="IsRestricted"/> records which one answered.
/// </summary>
public sealed class EmbySystemInfo
{
    public string? ServerName { get; set; }

    public string? Version { get; set; }

    public string? Id { get; set; }

    public string? OperatingSystem { get; set; }

    public string? OperatingSystemDisplayName { get; set; }

    /// <summary>The port the server answers plain HTTP on; admin-only.</summary>
    public int? HttpServerPortNumber { get; set; }

    public int? HttpsPortNumber { get; set; }

    public bool EnableHttps { get; set; }

    /// <summary>"http://172.17.0.4:8096" — the address to hand to another device on the same network.</summary>
    public string? LocalAddress { get; set; }

    /// <summary>"http://67.159.48.146:8096" — only set when the server knows its public address.</summary>
    public string? WanAddress { get; set; }

    public bool HasUpdateAvailable { get; set; }

    public bool HasPendingRestart { get; set; }

    public string? SystemUpdateLevel { get; set; }

    /// <summary>Set by the client when only the public subset came back, so the page can explain the gaps.</summary>
    [JsonIgnore]
    public bool IsRestricted { get; set; }
}

/// <summary>Everything the app needs to talk to one server as one user.</summary>
public sealed record EmbyConnection(
    Uri ApiBase,
    string AccessToken,
    string UserId,
    string UserName,
    string ServerName,
    DeviceIdentity Device);
