using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Momoka.Configuration;

namespace Momoka.Emby;

/// <summary>浏览偏好属于服务器路径和账号，不属于碰巧相同的条目编号。</summary>
public static class BrowsingPreferences
{
    public static string IdentityKey(EmbyConnection connection) => "identity:" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
        {
            connection.ApiBase.AbsoluteUri, connection.UserId
        }))));

    public static string LibraryKey(EmbyConnection connection, string libraryId) => IdentityKey(connection) + ":" + libraryId;

    public static void SelectIdentity(UiSettings ui, EmbyConnection connection)
    {
        var key = IdentityKey(connection);
        if (ui.HomeRowsIdentity == key) return;
        ui.HomeRowsByIdentity ??= new(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(ui.HomeRowsIdentity))
        {
            // 旧格式没有身份可追溯，只能在首次成功登录时认领，不能继续让所有账号共用。
            Migrate(ui.Sort, key);
            Migrate(ui.Filters, key);
            Migrate(ui.Views, key);
        }
        else
        {
            ui.HomeRowsByIdentity[ui.HomeRowsIdentity] = Copy(ui.HomeRows);
            ui.HomeRows = ui.HomeRowsByIdentity.TryGetValue(key, out var saved) ? Copy(saved) : [];
        }
        ui.HomeRowsIdentity = key;
    }

    private static List<HomeRowSetting> Copy(List<HomeRowSetting>? source) => source is null ? []
        : [.. source.Where(row => row is not null).Select(row => new HomeRowSetting
        { Key = row.Key, Title = row.Title, Visible = row.Visible })];

    private static void Migrate<T>(Dictionary<string, T> entries, string identity)
    {
        foreach (var (key, value) in entries.ToArray())
        {
            if (key.StartsWith("identity:", StringComparison.Ordinal)) continue;
            entries.TryAdd(identity + ":" + key, value);
            entries.Remove(key);
        }
    }
}
