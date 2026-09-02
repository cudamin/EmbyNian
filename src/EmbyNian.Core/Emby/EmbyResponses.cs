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

/// <summary>新建合集之后服务器答的那一句：这个合集的 id 和名字。</summary>
public sealed class CollectionCreationResult
{
    public string Id { get; set; } = "";

    public string? Name { get; set; }
}

/// <summary>
/// 「修改媒体封面图」问服务器要来的那一批候选图：各家刮削源上这个条目有哪些封面。
/// <see cref="Providers"/> 是这个条目能问的几家，一家都没有的时候那一批也是空的（服务器没装刮削插件、
/// 或者这个条目没有对上任何一家的记录）。
/// </summary>
public sealed class RemoteImageResult
{
    public List<RemoteImageInfo> Images { get; set; } = [];

    public int TotalRecordCount { get; set; }

    public List<string> Providers { get; set; } = [];
}

/// <summary>候选图中的一张。</summary>
public sealed class RemoteImageInfo
{
    public string? ProviderName { get; set; }

    /// <summary>原图地址。下载那一下交回给服务器，由服务器去取（客户端不一定连得上刮削源）。</summary>
    public string Url { get; set; } = "";

    /// <summary>缩略图地址，多数刮削源会给；没给就退回 <see cref="Url"/>。</summary>
    public string? ThumbnailUrl { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public double? CommunityRating { get; set; }

    public int? VoteCount { get; set; }

    public string? Language { get; set; }

    public string? DisplayLanguage { get; set; }

    /// <summary>"Primary"、"Backdrop"…… 问的时候点了名，所以回来的都是那一种。</summary>
    public string? Type { get; set; }

    /// <summary>一行给人看的说明：哪家、多大、什么语言。</summary>
    public string Describe()
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(ProviderName)) parts.Add(ProviderName!);
        if (Width is > 0 && Height is > 0) parts.Add($"{Width}×{Height}");
        if (DisplayLanguage is { Length: > 0 } language) parts.Add(language);
        else if (Language is { Length: > 0 } code) parts.Add(code);
        if (CommunityRating is { } rating and > 0) parts.Add($"{rating:0.#} 分");
        return string.Join("  ·  ", parts);
    }
}

/// <summary>字幕搜索结果中的一条。</summary>
public sealed class RemoteSubtitleInfo
{
    /// <summary>下载时交回给服务器的那个记号，不是给人看的。</summary>
    public string Id { get; set; } = "";

    public string? ProviderName { get; set; }

    public string? Name { get; set; }

    public string? Format { get; set; }

    public string? Author { get; set; }

    public string? Comment { get; set; }

    public string? Language { get; set; }

    public float? CommunityRating { get; set; }

    public int? DownloadCount { get; set; }

    /// <summary>按文件哈希对上的 —— 这一条基本可以肯定是这个文件的字幕，而不是同名影片的另一个版本。</summary>
    public bool? IsHashMatch { get; set; }

    public bool? IsForced { get; set; }

    public bool? IsHearingImpaired { get; set; }

    /// <summary>列表里那一行：名字，加上能说清「这一条靠不靠得住」的几样。</summary>
    public string Describe()
    {
        var parts = new List<string>(6);
        if (!string.IsNullOrWhiteSpace(ProviderName)) parts.Add(ProviderName!);
        if (!string.IsNullOrWhiteSpace(Language)) parts.Add(Language!);
        if (!string.IsNullOrWhiteSpace(Format)) parts.Add(Format!.ToUpperInvariant());
        if (IsHashMatch == true) parts.Add("文件精确匹配");
        if (DownloadCount is { } count and > 0) parts.Add($"{count} 次下载");
        if (CommunityRating is { } rating and > 0) parts.Add($"{rating:0.#} 分");
        if (IsForced == true) parts.Add("强制");
        if (IsHearingImpaired == true) parts.Add("听障");
        return string.Join("  ·  ", parts);
    }
}

/// <summary>下载一条字幕之后服务器答的：它在这个文件里成了第几条轨道。</summary>
public sealed class SubtitleDownloadResult
{
    public int? NewIndex { get; set; }
}
