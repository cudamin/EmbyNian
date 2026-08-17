using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbyMpvClient;

internal sealed class AppSettings
{
    public List<ServerProfile> Servers { get; set; } = [];
    public string? LastServerId { get; set; }
    public string? LastAccountId { get; set; }
    public string MpvPath { get; set; } = @"C:\mpv_config-2026.08.12\mpv.exe";
    public string MpvConfigPath { get; set; } = @"C:\mpv_config-2026.08.12\portable_config\mpv.conf";
    public string InputConfigPath { get; set; } = @"C:\mpv_config-2026.08.12\portable_config\input.conf";

    // Legacy properties are retained for migration from versions before profile support.
    public string ServerUrl { get; set; } = "http://localhost:8096";
    public string Username { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string UserId { get; set; } = "";

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmbyMpvClient", "settings.json");

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch { settings = new(); }

        settings.MigrateLegacyProfile();
        settings.Activate(settings.LastServerId, settings.LastAccountId);
        return settings;
    }

    public void Activate(string? serverId, string? accountId)
    {
        var server = Servers.FirstOrDefault(item => item.Id == serverId) ?? Servers.FirstOrDefault();
        var account = server?.Accounts.FirstOrDefault(item => item.Id == accountId) ?? server?.Accounts.FirstOrDefault();
        if (server is null) return;
        LastServerId = server.Id;
        ServerUrl = server.Url;
        if (account is null) { Username = AccessToken = UserId = ""; return; }
        LastAccountId = account.Id;
        Username = account.Username;
        AccessToken = account.AccessToken;
        UserId = account.UserId;
    }

    public void UpdateActiveAccount(string password)
    {
        var server = Servers.First(item => item.Id == LastServerId);
        var account = server.Accounts.First(item => item.Id == LastAccountId);
        account.Username = Username;
        account.AccessToken = AccessToken;
        account.UserId = UserId;
        account.ProtectedPassword = PasswordProtector.Protect(password);
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void MigrateLegacyProfile()
    {
        if (Servers.Count > 0) return;
        var server = new ServerProfile { Name = "我的 Emby", Url = ServerUrl };
        if (!string.IsNullOrWhiteSpace(Username))
            server.Accounts.Add(new AccountProfile { Username = Username, AccessToken = AccessToken, UserId = UserId });
        Servers.Add(server);
        LastServerId = server.Id;
        LastAccountId = server.Accounts.FirstOrDefault()?.Id;
    }
}

internal sealed class ServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Emby 服务器";
    public string Url { get; set; } = "http://localhost:8096";
    public List<AccountProfile> Accounts { get; set; } = [];
    public override string ToString() => Name;
}

internal sealed class AccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string UserId { get; set; } = "";
    public string GetPassword() => PasswordProtector.Unprotect(ProtectedPassword);
    public override string ToString() => Username;
}

internal static class PasswordProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EmbyMpvClient.Profile.v1");

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}
