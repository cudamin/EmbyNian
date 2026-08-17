namespace EmbyMpvClient.Emby;

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

/// <summary>Everything the app needs to talk to one server as one user.</summary>
public sealed record EmbyConnection(
    Uri ApiBase,
    string AccessToken,
    string UserId,
    string UserName,
    string ServerName,
    DeviceIdentity Device);
