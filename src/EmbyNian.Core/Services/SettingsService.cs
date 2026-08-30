using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;

namespace EmbyNian.Services;

/// <summary>
/// The settings the pages read, the two profile lookups that create a profile when the user has named one
/// that does not exist yet, and the write-back.
/// <para>
/// An interface because this is what a page actually depends on. Handing every page the whole composition
/// root gave a page that only wants 每页条数 the ability to reach the session, the image cache and the
/// player as well — which is not a dependency anyone declared, and not one anything can substitute.
/// </para>
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// The live settings object, edited in place by the settings page and written out by <see cref="Save"/>.
    /// One instance for the process: two would mean an edit on one page invisible to the next.
    /// </summary>
    AppSettings Settings { get; }

    /// <summary>How many items one browse request asks for.</summary>
    int PageSize { get; }

    ServerProfile ResolveServer(string url);

    AccountProfile ResolveAccount(ServerProfile server, string username);

    void Save();
}

/// <inheritdoc cref="ISettingsService"/>
public sealed class SettingsService(SettingsStore store, AppSettings settings) : ISettingsService
{
    // Unchanged from the composition root this was carved out of, so a line that used to be filed under
    // app still is: the 诊断 page lists by category, and renaming one loses the history behind it.
    private const string Category = "app";

    /// <inheritdoc />
    public AppSettings Settings { get; } = settings;

    /// <summary>
    /// How many items one browse request asks for. The range is the one
    /// <see cref="SettingsMigration.Normalize"/> clamps the setting to; a narrower one here would quietly
    /// ignore the top of what the settings page lets a user choose.
    /// </summary>
    public int PageSize => Math.Clamp(Settings.Ui.PageSize, 20, 500);

    /// <summary>
    /// The saved profile for an address the user typed, creating one if this server is new. Matching is
    /// on the normalised API base rather than the raw text, so "192.168.1.5:8096" and
    /// "http://192.168.1.5:8096/emby/" are recognised as the same server instead of piling up two
    /// profiles with two copies of the password.
    /// </summary>
    public ServerProfile ResolveServer(string url)
    {
        var normalized = EmbyServerAddress.Normalize(url);

        foreach (var candidate in Settings.Servers)
        {
            if (EmbyServerAddress.TryNormalize(candidate.Url, out var existing, out _) &&
                string.Equals(existing.AbsoluteUri, normalized.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                // Keep the canonical form: a profile saved from a hand-typed host gets tidied up the
                // first time it is signed into again.
                candidate.Url = EmbyServerAddress.ToDisplayString(normalized);
                return candidate;
            }
        }

        var server = new ServerProfile { Url = EmbyServerAddress.ToDisplayString(normalized) };
        Settings.Servers.Add(server);
        Log.Info(Category, $"已新增服务器配置：{server.Url}");
        return server;
    }

    /// <summary>The saved account for a username on one server, creating one if it is new.</summary>
    public AccountProfile ResolveAccount(ServerProfile server, string username)
    {
        var name = username.Trim();

        var account = server.Accounts.FirstOrDefault(
            candidate => string.Equals(candidate.Username, name, StringComparison.OrdinalIgnoreCase));

        if (account is not null) return account;

        account = new AccountProfile { Username = name };
        server.Accounts.Add(account);
        return account;
    }

    /// <summary>
    /// Writes the settings out, and never throws: every caller is a UI action that has already happened —
    /// a toggle flipped, a server renamed — and there is nothing for it to undo.
    /// </summary>
    public void Save()
    {
        try
        {
            store.Save(Settings);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "保存设置失败", error);
        }
    }
}
