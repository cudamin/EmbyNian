using System.Text.Json.Nodes;

namespace Momoka.Emby;

/// <summary>保留服务器的完整用户文档，更新某个页签时不能重建、丢弃其他权限或插件字段。</summary>
public sealed class EmbyManagedUser(JsonObject document)
{
    public JsonObject Document { get; } = document;
    public string Id => Text(Document, "Id");
    public string Name => Text(Document, "Name");
    public string ConnectUserName => Text(Document, "ConnectUserName");
    public string ImageTag => Text(Document, "PrimaryImageTag");
    public JsonObject Policy => Document["Policy"] as JsonObject ?? throw new InvalidDataException("服务器没有返回用户权限。");
    public JsonObject Configuration => Document["Configuration"] as JsonObject ?? new();
    public bool IsAdministrator => Flag(Policy, "IsAdministrator");
    public bool IsDisabled => Flag(Policy, "IsDisabled");
    public bool HasPassword => Flag(Document, "HasConfiguredPassword", Flag(Document, "HasPassword"));

    public static string Text(JsonObject value, string key) => value[key]?.ToString() ?? "";
    public static bool Flag(JsonObject value, string key, bool fallback = false) =>
        bool.TryParse(value[key]?.ToString(), out var result) ? result : fallback;
    public static string[] Strings(JsonObject value, string key) => value[key] is JsonArray array
        ? array.Select(item => item?.ToString() ?? "").Where(item => item.Length > 0).ToArray() : [];
}

public sealed record UserPermission(string Key, string Label, string Group, string Note = "");

/// <summary>Emby 4.10 users/profiletab 的权限。动态功能另从 Features?FeatureType=User 读取。</summary>
public static class EmbyUserPermissions
{
    public static IReadOnlyList<UserPermission> Profile { get; } =
    [
        new("EnableRemoteAccess", "允许远程连接此服务器", "账号"),
        new("IsAdministrator", "允许此用户管理服务器", "账号"),
        new("EnableMediaPlayback", "允许媒体播放", "播放"),
        new("EnableAudioPlaybackTranscoding", "允许音频转码", "播放"),
        new("EnableVideoPlaybackTranscoding", "允许视频转码", "播放"),
        new("EnablePlaybackRemuxing", "允许更改容器格式而不重新编码视频", "播放"),
        new("EnableLiveTvAccess", "直播电视", "功能访问"),
        new("EnableLiveTvManagement", "管理直播电视录制", "功能访问"),
        new("EnableRemoteControlOfOtherUsers", "允许远程控制其他用户", "远程控制"),
        new("EnableSharedDeviceControl", "允许远程控制共享设备", "远程控制", "共享设备会接受其他用户的远程控制。"),
        new("EnableContentDownloading", "允许媒体下载", "下载"),
        new("EnableSyncTranscoding", "允许需要转码的媒体下载", "下载"),
        new("EnableSubtitleDownloading", "允许下载字幕", "字幕"),
        new("EnableSubtitleManagement", "允许删除或上传字幕", "字幕"),
        new("AllowCameraUpload", "允许相机上传", "其他权限"),
        new("EnableMediaConversion", "允许媒体转换", "其他权限"),
        new("AllowSharingPersonalItems", "允许分享个人内容", "其他权限"),
        new("EnablePublicSharing", "允许分享媒体链接", "其他权限"),
        new("EnableUserPreferenceAccess", "允许更改用户图片和密码", "其他权限"),
        new("IsDisabled", "禁用此用户", "登录", "禁用后此用户将无法连接服务器。"),
        new("IsHidden", "在本地网络的登录页面上隐藏此用户", "登录"),
        new("IsHiddenRemotely", "在远程登录页面上隐藏此用户", "登录"),
        new("IsHiddenFromUnusedDevices", "在从未登录过的设备上隐藏此用户", "登录")
    ];

    public static JsonArray Array(IEnumerable<string> values) => new(values.Distinct(StringComparer.Ordinal)
        .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    /// <summary>只合并本页实际改动的键；保留另一个客户端在编辑期间更新的其他字段。</summary>
    public static JsonObject Merge(JsonObject latest, JsonObject original, JsonObject edited)
    {
        var result = (JsonObject)latest.DeepClone();
        foreach (var pair in edited)
            if (!JsonNode.DeepEquals(original[pair.Key], pair.Value)) result[pair.Key] = pair.Value?.DeepClone();
        return result;
    }

    public static void ValidatePassword(bool administrator, string password, string confirmation)
    {
        if (password != confirmation) throw new ArgumentException("两次输入的密码不一致。");
        if (administrator && string.IsNullOrEmpty(password)) throw new ArgumentException("管理员必须设置密码。");
    }

    public static void ValidatePin(string pin)
    {
        if (pin.Length != 0 && (pin.Length != 4 || pin.Any(c => c is < '0' or > '9')))
            throw new ArgumentException("个人 PIN 必须是 4 位数字；留空可移除 PIN。");
    }

    public static JsonObject Schedule(string day, double start, double end)
    {
        if (!Enum.GetNames<DayOfWeek>().Contains(day, StringComparer.Ordinal) || !double.IsFinite(start) || !double.IsFinite(end)
            || start < 0 || end > 24 || start >= end)
            throw new ArgumentException("访问时段的结束时间必须晚于开始时间，范围为 00:00 至 24:00。");
        return new() { ["DayOfWeek"] = day, ["StartHour"] = start, ["EndHour"] = end };
    }
}

public static class EmbyUserAdministration
{
    /// <summary>读取最新文档再合并，每次写入前核对固定身份。分步失败时不伪装成事务成功。</summary>
    public static async Task SaveAsync(EmbySessionScope scope, EmbyManagedUser original, JsonObject editedPolicy,
        string? name, CancellationToken token)
    {
        scope.ThrowIfNotCurrent();
        var latest = await scope.ExecuteAsync((client, ct) => client.GetManagedUserAsync(original.Id, ct), token).ConfigureAwait(false);
        if (name is not null && name != original.Name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            latest.Document["Name"] = name.Trim();
            token.ThrowIfCancellationRequested();
            scope.ThrowIfNotCurrent();
            await scope.ExecuteAsync((client, ct) => client.UpdateManagedUserAsync(original.Id, latest.Document, ct), token).ConfigureAwait(false);
        }
        var policy = EmbyUserPermissions.Merge(latest.Policy, original.Policy, editedPolicy);
        if (JsonNode.DeepEquals(policy, latest.Policy)) return;
        token.ThrowIfCancellationRequested();
        scope.ThrowIfNotCurrent();
        await scope.ExecuteAsync((client, ct) => client.UpdateManagedUserPolicyAsync(original.Id, policy, ct), token).ConfigureAwait(false);
    }
}
