using System.Text.Json;

namespace EmbyMpvClient.Configuration;

/// <summary>
/// Reads any settings.json this app has ever written and returns the current shape.
/// Kept as a pure function over a JSON string so the migration paths are unit-testable
/// without touching the disk.
/// </summary>
public static class SettingsMigration
{
    public static AppSettings FromJson(string json, ISecretProtector protector)
    {
        if (string.IsNullOrWhiteSpace(json)) return NewDefaults();

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return NewDefaults();

        var version = ReadInt(root, "SchemaVersion") ?? 1;
        var settings = version >= 2 ? ReadCurrent(json) : ReadLegacy(root, protector);

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        return Normalize(settings);
    }

    public static AppSettings NewDefaults()
    {
        var settings = new AppSettings();
        settings.Servers.Add(new ServerProfile { Name = "我的 Emby" });
        settings.LastServerId = settings.Servers[0].Id;
        return Normalize(settings);
    }

    /// <summary>Clamps anything a hand-edited file could put out of range.</summary>
    public static AppSettings Normalize(AppSettings settings)
    {
        settings.EnsureDeviceId();
        settings.Ui.PageSize = Math.Clamp(settings.Ui.PageSize, 20, 500);
        settings.Ui.PosterWidth = Math.Clamp(settings.Ui.PosterWidth, 120, 340);
        settings.Ui.WindowWidth = Math.Clamp(settings.Ui.WindowWidth, 1040, 8000);
        settings.Ui.WindowHeight = Math.Clamp(settings.Ui.WindowHeight, 680, 8000);
        settings.Playback.MarkWatchedPercent = Math.Clamp(settings.Playback.MarkWatchedPercent, 50, 100);
        settings.Playback.ProgressReportIntervalSeconds = Math.Clamp(settings.Playback.ProgressReportIntervalSeconds, 1, 60);
        settings.Mpv.ConfigBackupsToKeep = Math.Clamp(settings.Mpv.ConfigBackupsToKeep, 1, 200);

        foreach (var server in settings.Servers)
        {
            server.Name = string.IsNullOrWhiteSpace(server.Name) ? "Emby 服务器" : server.Name.Trim();
            server.Url = server.Url.Trim();
        }

        // Keep the remembered pair consistent: the remembered account has to live on
        // the remembered server, or the login page opens on a mismatched selection.
        if (settings.LastAccountId is not null && settings.FindServer(settings.LastServerId)?.FindAccount(settings.LastAccountId) is null)
        {
            var owner = settings.Servers.FirstOrDefault(server => server.FindAccount(settings.LastAccountId) is not null);
            if (owner is not null) settings.LastServerId = owner.Id;
            else settings.LastAccountId = null;
        }

        if (settings.FindServer(settings.LastServerId) is null)
        {
            settings.LastServerId = settings.Servers.FirstOrDefault()?.Id;
            settings.LastAccountId = settings.ResolveLastAccount()?.Id;
        }

        return settings;
    }

    private static AppSettings ReadCurrent(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, SettingsSerializer.ReadOptions) ?? NewDefaults();

    /// <summary>
    /// v1 kept the signed-in server/user at the document root and, in later builds, also
    /// a <c>Servers</c> array. Prefer the array when present, otherwise synthesise one
    /// profile from the flat fields so the user does not have to re-enter anything.
    /// </summary>
    private static AppSettings ReadLegacy(JsonElement root, ISecretProtector protector)
    {
        var settings = new AppSettings
        {
            LastServerId = ReadString(root, "LastServerId"),
            LastAccountId = ReadString(root, "LastAccountId")
        };

        settings.Mpv.ExecutablePath = ReadString(root, "MpvPath") ?? settings.Mpv.ExecutablePath;
        settings.Mpv.ConfigPath = ReadString(root, "MpvConfigPath") ?? settings.Mpv.ConfigPath;
        settings.Mpv.InputConfigPath = ReadString(root, "InputConfigPath") ?? settings.Mpv.InputConfigPath;

        if (root.TryGetProperty("Servers", out var servers) && servers.ValueKind == JsonValueKind.Array && servers.GetArrayLength() > 0)
        {
            foreach (var element in servers.EnumerateArray())
            {
                settings.Servers.Add(ReadLegacyServer(element, protector));
            }
        }
        else
        {
            var server = new ServerProfile
            {
                Name = "我的 Emby",
                Url = ReadString(root, "ServerUrl") ?? "http://localhost:8096"
            };

            var username = ReadString(root, "Username");
            if (!string.IsNullOrWhiteSpace(username))
            {
                server.Accounts.Add(new AccountProfile
                {
                    Username = username.Trim(),
                    UserId = ReadString(root, "UserId") ?? "",
                    ProtectedAccessToken = ProtectPlainText(protector, ReadString(root, "AccessToken"))
                });
            }

            settings.Servers.Add(server);
        }

        settings.LastServerId ??= settings.Servers.FirstOrDefault()?.Id;
        settings.LastAccountId ??= settings.Servers.FirstOrDefault()?.Accounts.FirstOrDefault()?.Id;
        return settings;
    }

    private static ServerProfile ReadLegacyServer(JsonElement element, ISecretProtector protector)
    {
        var server = new ServerProfile
        {
            Name = ReadString(element, "Name") ?? "Emby 服务器",
            Url = ReadString(element, "Url") ?? "http://localhost:8096"
        };

        if (ReadString(element, "Id") is { Length: > 0 } id) server.Id = id;

        if (!element.TryGetProperty("Accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
            return server;

        foreach (var item in accounts.EnumerateArray())
        {
            var account = new AccountProfile
            {
                Username = ReadString(item, "Username") ?? "",
                UserId = ReadString(item, "UserId") ?? "",
                // v1 already wrapped the password with the same DPAPI entropy, so it carries over as-is.
                ProtectedPassword = ReadString(item, "ProtectedPassword") ?? "",
                ProtectedAccessToken = ProtectPlainText(protector, ReadString(item, "AccessToken"))
            };

            if (ReadString(item, "Id") is { Length: > 0 } accountId) account.Id = accountId;
            account.RememberPassword = account.ProtectedPassword.Length > 0;
            server.Accounts.Add(account);
        }

        return server;
    }

    private static string ProtectPlainText(ISecretProtector protector, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try
        {
            return protector.Protect(value);
        }
        catch
        {
            return "";
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}
