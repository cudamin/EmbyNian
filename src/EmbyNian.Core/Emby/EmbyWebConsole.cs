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

    /// <summary>
    /// Emby 网页端存主题的两个键：一个管普通页面，一个管设置和控制台那类页面（网页端自己的设置里就是
    /// 「主题」和「设置页面的主题」两行）。<c>usersettingsbuilder.js</c> 里 <c>theme()</c> 和
    /// <c>settingsTheme()</c> 存的就是这两个名字，两个都是 <c>enableOnServer=false</c> —— 只落在浏览器
    /// 自己的 <c>localStorage</c> 里，不会写到服务器的用户偏好上，所以动它们影响不到用户其他设备上的
    /// Emby。前缀是用户 id，见 <c>appsettings.js</c> 的 <c>getKey</c>（<c>userId + "-" + name</c>）。
    /// </summary>
    private static readonly string[] ThemeKeys = ["appTheme", "settingsTheme"];

    /// <summary>
    /// 写进上面那两个键的值：跟着 <c>prefers-color-scheme</c> 走。
    /// <para>
    /// 这个值是这件事唯一走得通的写法，值得记下来为什么。直接写 <c>"light"</c> 是不行的：
    /// <c>skinmanager.js</c> 的 <c>setTheme</c> 里，凡是和默认那套（浏览器上是 <c>dark</c>）不同的主题都要过
    /// 一道注册检查，没有 Emby Premiere 就被拨回 <c>dark</c> —— 这台机器上那个缓存键
    /// (<c>appthemesregistered</c>) 正是 <c>false</c>。而 <c>"auto"</c> 走的是另一条分支：它把主题交给
    /// <c>apphost.js</c> 的 <c>getPreferredTheme()</c>（也就是 <c>matchMedia("(prefers-color-scheme: dark)")</c>），
    /// 并且当场把 <c>requiresRegistration</c> 置为 false，于是浅色也放行。这不是绕过收费项，而是 Emby 自己
    /// 给「跟随系统深浅」留的免费口子。
    /// </para>
    /// <para>
    /// 深浅本身不由这段脚本决定，而是由内嵌浏览器的 <c>PreferredColorScheme</c> 决定（见
    /// <c>DashboardPage</c>）—— 所以这段脚本和当前是哪一套主题无关，只说「跟着浏览器的深浅走」。
    /// </para>
    /// </summary>
    public const string FollowColorScheme = "auto";

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
        var host = HostLiteral(apiBase);
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

    /// <summary>
    /// 另一段文档开始脚本：让内嵌控制台的深浅跟着本应用当前的主题走。
    /// <para>
    /// 做法是把网页端那两个主题设置（见 <see cref="ThemeKeys"/>）都写成
    /// <see cref="FollowColorScheme"/>，也就是「跟着浏览器的 <c>prefers-color-scheme</c>」，而那一位由
    /// <c>DashboardPage</c> 按 <c>ThemeHost.Current.IsDark</c> 设在内嵌浏览器上。为什么是这个值而不是直接写
    /// <c>light</c>／<c>dark</c>，见 <see cref="FollowColorScheme"/> 上那段 —— 那是这件事的全部机关。
    /// </para>
    /// <para>
    /// 每次载入都重写，不是「没有值时才写」：跟随主题是这一页的规矩，用户在控制台自己那个下拉框里改的
    /// 主题只在本次载入里有效，下次进这一页又跟回来。写的这两个键都只落在浏览器本地，不碰服务器上的用户
    /// 偏好，所以他在手机、网页上看到的 Emby 不受影响。强调色没有一起跟，正因为它是存服务器的
    /// （<c>accentColor</c>，<c>enableOnServer=true</c>）—— 那会改到他其他设备。
    /// </para>
    /// <para>
    /// 和 <see cref="SignInScript"/> 一样只对我们自己那台服务器的页面动手，理由同样是这段脚本会跑在这个
    /// 控件载入的每一个文档里。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">没有用户 id：这两个键都带用户前缀，写不出来。</exception>
    public static string ThemeScript(Uri apiBase, string userId)
    {
        ArgumentNullException.ThrowIfNull(apiBase);
        ArgumentException.ThrowIfNullOrEmpty(userId);

        var host = HostLiteral(apiBase);
        var keys = JsonSerializer.Serialize(ThemeKeys.Select(name => $"{userId}-{name}"));
        var value = JsonSerializer.Serialize(FollowColorScheme);

        return $$"""
            (function () {
              try {
                if ((location.host || "").toLowerCase() !== {{host}}) return;

                var keys = {{keys}};
                for (var i = 0; i < keys.length; i++) localStorage.setItem(keys[i], {{value}});
              } catch (e) {
                // 写不进去就让网页端用它自己存着的那套主题，不值得为此把控制台变成白屏。
              }
            })();
            """;
    }

    /// <summary>
    /// <c>location.host</c> 那个串，做成 JSON 字面量。<see cref="Uri.Authority"/> 而不是 <c>Host</c>：前者是
    /// host:port，且端口是该 scheme 自己的默认端口时不带端口 —— 正好是 <c>location.host</c> 的形状。
    /// </summary>
    private static string HostLiteral(Uri apiBase) =>
        JsonSerializer.Serialize(apiBase.Authority.ToLowerInvariant());
}
