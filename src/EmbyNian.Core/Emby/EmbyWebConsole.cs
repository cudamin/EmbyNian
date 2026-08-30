using System.Text.Json;

namespace EmbyNian.Emby;

/// <summary>
/// 需求 8：把 Emby 自己的网页控制台嵌进设置页要用到的两样东西 —— 那个页面的地址，以及让它免登录的一小段脚本。
/// <para>
/// Here rather than in the page, because both are decisions about Emby and neither needs a window: the URL
/// is arithmetic on the API base, and the script is a string. That also makes them the one part of this
/// requirement a test project which references nothing but Core can check. (The dashboard <i>API</i> models
/// — <see cref="EmbySystemInfo"/> and friends — are a different thing and live in EmbyDashboard.cs.)
/// </para>
/// </summary>
public static class EmbyWebConsole
{
    /// <summary>
    /// Where Emby's web client keeps the servers it has signed into. Read off the running server's own
    /// <c>modules/emby-apiclient/credentials.js</c> (<c>var StorageKey="servercredentials3"</c>), whose
    /// storage is plain <c>localStorage</c> with no prefix — <c>appstorage-localstorage.js</c> is a
    /// four-line wrapper over it.
    /// </summary>
    public const string StorageKey = "servercredentials3";

    /// <summary>
    /// <c>ConnectionMode_Manual</c> in <c>connectionmanager.js</c>. The mode for an address the user typed,
    /// which is what ours is: it came out of this app's own server profile.
    /// </summary>
    private const int ManualConnectionMode = 2;

    /// <summary>The page 需求 8 names, hash route and all.</summary>
    public static string Url(Uri apiBase) => $"{Root(apiBase)}/web/index.html#!/dashboard";

    /// <summary>
    /// Where the web client is served from: the API base without <c>/emby/</c>. The same string the client
    /// computes for itself — <c>app.js</c> takes <c>location.href</c> up to the last <c>/web</c> — so a
    /// server behind a reverse proxy on a sub-path keeps that path in both.
    /// </summary>
    public static string Root(Uri apiBase) => EmbyServerAddress.ToDisplayString(apiBase);

    /// <summary>
    /// A document-start script that leaves this session's credentials where Emby's web client looks for
    /// them, so the embedded console comes up signed in instead of on a login form.
    /// <para>
    /// This is what a returning browser does: the client's own sign-in writes the same entry, and boot
    /// restores it — <c>connect()</c> → <c>getAvailableServers()</c> → <c>connectToServer()</c> →
    /// <c>afterConnectValidated</c>, which reads <c>server.UserId</c>, finds that user in
    /// <c>server.Users</c>, validates the token against <c>System/Info</c> and resolves 「SignedIn」. Hence
    /// the shape: the last-used user id on the server entry, and the token in the per-user list.
    /// </para>
    /// <para>
    /// Chosen over the client's other way in, <c>?accessToken=…&amp;userId=…&amp;e=1</c>, for two reasons.
    /// That path puts a live credential in a URL — which lands in history, in the server's request log and
    /// in anything that logs a navigation — and having taken it the client immediately does
    /// <c>window.location = "index.html"</c>, dropping the <c>#!/dashboard</c> route it was asked for.
    /// </para>
    /// <para>
    /// Merges rather than overwrites, and only ever for our own host. Merging keeps the server id the client
    /// fills in after its first connection, which is what its api-client map is keyed by; the host guard is
    /// because a document-start script runs in every document the control loads, and an access token must
    /// not be written into some other site's storage because a link went there.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">No user id or no token: there is nothing to seed.</exception>
    public static string SignInScript(Uri apiBase, string userId, string accessToken, string serverName)
    {
        ArgumentNullException.ThrowIfNull(apiBase);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        // Serialised rather than interpolated. Everything below reaches the script as a JSON literal, and
        // the default encoder escapes <, >, &, quotes and every non-ASCII character — so a server named
        // 「果服」 and a token with a quote in it are both just text by the time the browser parses this.
        var seed = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Name"] = string.IsNullOrWhiteSpace(serverName) ? "Emby" : serverName.Trim(),
            ["ManualAddress"] = Root(apiBase),
            ["UserId"] = userId,
            ["AccessToken"] = accessToken
        });

        // host:port, which is what location.host is: no scheme, and no port when it is the scheme's own.
        var host = JsonSerializer.Serialize(apiBase.Authority.ToLowerInvariant());
        var key = JsonSerializer.Serialize(StorageKey);

        return $$"""
            (function () {
              try {
                if ((location.host || "").toLowerCase() !== {{host}}) return;

                var seed = {{seed}};
                var key = {{key}};

                var data = null;
                try { data = JSON.parse(localStorage.getItem(key) || "null"); } catch (e) { data = null; }
                if (!data || !Array.isArray(data.Servers)) data = { Servers: [] };

                var wanted = seed.ManualAddress.toLowerCase();
                var server = null;
                for (var i = 0; i < data.Servers.length; i++) {
                  var candidate = data.Servers[i];
                  if (candidate && (candidate.ManualAddress || "").toLowerCase() === wanted) { server = candidate; break; }
                }
                if (!server) { server = {}; data.Servers.unshift(server); }

                server.Name = server.Name || seed.Name;
                server.ManualAddress = seed.ManualAddress;
                server.ManualAddressOnly = true;
                server.IsLocalServer = true;
                server.LastConnectionMode = {{ManualConnectionMode}};
                server.DateLastAccessed = Date.now();
                server.UserId = seed.UserId;
                server.AccessToken = seed.AccessToken;

                var users = Array.isArray(server.Users) ? server.Users : [];
                var user = null;
                for (var j = 0; j < users.length; j++) {
                  if (users[j] && users[j].UserId === seed.UserId) { user = users[j]; break; }
                }
                if (!user) { user = { UserId: seed.UserId }; users.push(user); }
                user.AccessToken = seed.AccessToken;
                server.Users = users;

                localStorage.setItem(key, JSON.stringify(data));
              } catch (e) {
                // 免登录失败不该把控制台变成白屏：让它照常显示登录框就好。
              }
            })();
            """;
    }
}
