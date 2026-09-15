using System.Net;
using System.Text.Json;
using EmbyNian.Configuration;
using EmbyNian.MoviePilot;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// MoviePilot 接入的那几条规则（用户的话，2026-09-14：「放设置页」）。
/// <para>
/// 钉住的是三件各自有唯一正确答案的事：地址怎么归一化、信封里的 <c>data</c> 怎么拆出来、登录失败时那句话是
/// 不是给用户看的那一句。这三件都不需要一台真的 MoviePilot，而它们在屏上看起来都只是「点了一下没反应」或者
/// 「连接失败」—— 真正坏掉的是里子。
/// </para>
/// <para>
/// 真机实测过的形状写在这里当夹具（2026-09-14，v3.0.1）：登录是表单、成功回 JWT、失败回
/// <c>{"success":false,"message":"..."}</c>。这几份 JSON 是从那台服务器上抄下来的，不是编的。
/// </para>
/// </summary>
internal static class MoviePilotTests
{
    public static void Register()
    {
        AddressTests();
        ClientTests();
        ProbeTests();
    }

    // ── 地址归一化 ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 用户会怎么填地址，以及每一种该怎么理解。这一条是这一整张卡的地基：地址错了的话底下所有调用都错，
    /// 而屏上只会说「连不上」。
    /// </summary>
    private static void AddressTests()
    {
        Test("MoviePilot 地址：光敲主机名和端口就是合法地址", () =>
        {
            Assert.True(MoviePilotAddress.TryNormalize("192.168.31.230:3001", out var address, out var error), error);
            Assert.NotNull(address);
            Assert.Equal("http", address!.Scheme, "没写协议时应当补 http");
            Assert.Equal("192.168.31.230", address.Host);
            Assert.Equal(3001, address.Port);
        });

        Test("MoviePilot 地址：没写端口时补 API 那个 3001，而不是网址默认的 80", () =>
        {
            // 这条是这张卡最容易搞错的地方：MoviePilot 的网页界面和 API 是两个端口，
            // http://host/ 解析出来会是 80，而 80 上什么都没有。
            Assert.True(MoviePilotAddress.TryNormalize("192.168.31.230", out var address, out _));
            Assert.Equal(3001, address!.Port);
        });

        Test("MoviePilot 地址：自己写的端口一律照用，包括写成网页那个 3000", () =>
        {
            // 明写着 3000 的人是在说「我的 API 就在 3000 上」。替他改掉等于让他对着一个能连通的地址看
            // 「连不上」，而真正的答案（这个端口上没有 API）是他自己一看就明白的。
            Assert.True(MoviePilotAddress.TryNormalize("192.168.31.230:3000", out var address, out _));
            Assert.Equal(3000, address!.Port);
        });

        Test("MoviePilot 地址：带路径的网址只取到主机和端口", () =>
        {
            // 从浏览器地址栏复制下来的一整串是常事，尾巴上的页面路径不该进到 API 地址里。
            Assert.True(MoviePilotAddress.TryNormalize("http://192.168.31.230:3001/#/dashboard", out var address, out _));
            Assert.Equal("http://192.168.31.230:3001/", address!.AbsoluteUri);
        });

        Test("MoviePilot 地址：空着不算错，那只是还没填", () =>
        {
            // 装机就是空的，而这张卡在没填地址时也必须好好地立在屏上。
            Assert.True(MoviePilotAddress.TryNormalize("", out var address, out _));
            Assert.Null(address, "空地址应当是 null，不是错");
            Assert.True(MoviePilotAddress.TryNormalize(null, out _, out _), "null 同上");
        });

        Test("MoviePilot 地址：认不出来的东西要说得出为什么", () =>
        {
            // 给一句人能照着改的话，而不是让按钮按下去什么都不发生。
            Assert.False(MoviePilotAddress.TryNormalize("ftp://192.168.1.5", out _, out var protocol), "ftp 应当被拒");
            Assert.Contains("http", protocol);

            Assert.False(MoviePilotAddress.TryNormalize("http://", out _, out var empty), "没有主机名应当被拒");
            Assert.True(empty.Length > 0, "拒了就得说一句为什么");
        });

        Test("MoviePilot 地址：显示串和解析串是同一件事来回走", () =>
        {
            // 存的是用户看得懂的那一串，用的必须是归一化过的那一个 —— 两个方向都要能对上。
            // API 端口那一档不带 :3001 的尾巴：那是默认值，写出来只会让下一次归一化多绕一圈。
            Assert.True(MoviePilotAddress.TryNormalize("192.168.31.230:3001", out var address, out _));
            Assert.Equal("192.168.31.230", MoviePilotAddress.ToDisplayString(address!));
            Assert.Equal(3001, address!.Port, "收起来了，但用的时候仍是 3001");

            // 自己写的非默认端口要留在显示串里 —— 丢掉它等于把用户写的东西改掉了。
            Assert.True(MoviePilotAddress.TryNormalize("192.168.31.230:3000", out var custom, out _));
            Assert.Equal("192.168.31.230:3000", MoviePilotAddress.ToDisplayString(custom!));

            // 而显示串本身读回去还是同一个地址：来回走一趟不该越走越远。
            Assert.True(MoviePilotAddress.TryNormalize(MoviePilotAddress.ToDisplayString(custom!), out var again, out _));
            Assert.Equal(custom!.AbsoluteUri, again!.AbsoluteUri);
        });

        Test("MoviePilot 地址：拼出来的请求地址落在 api/v1 底下", () =>
        {
            Assert.True(MoviePilotAddress.TryNormalize("192.168.31.230:3001", out var address, out _));

            Assert.Equal(
                "http://192.168.31.230:3001/api/v1/dashboard/system",
                MoviePilotAddress.Combine(address!, "api/v1/dashboard/system").AbsoluteUri);

            // 前头多一个斜杠的写法也该落在同一个地方 —— 这是调用方最容易写错的那一下。
            Assert.Equal(
                "http://192.168.31.230:3001/api/v1/subscribe/",
                MoviePilotAddress.Combine(address!, "/api/v1/subscribe/").AbsoluteUri);
        });
    }

    // ── 客户端 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 登录、拆信封、失败翻译。用的是真机抄下来的那几份 JSON。
    /// </summary>
    private static void ClientTests()
    {
        Test("MoviePilot 客户端：登录发的是表单，拿的是 JWT", () =>
        {
            var transport = new StubTransport().Answer("login/access-token",
                """{"access_token":"jwt-abc","token_type":"bearer","super_user":true,"user_id":1,"user_name":"donxuelian"}""");

            using var client = new MoviePilotClient(transport);
            var session = client.SignInAsync(Base, "donxuelian", "tafei520.", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.Equal("jwt-abc", session.AccessToken);
            Assert.Equal("donxuelian", session.UserName);
            Assert.True(session.SuperUser, "这台服务器上这个账号是超级管理员，标志位该照实读出来");

            // 表单，不是 JSON。发 JSON 会被 FastAPI 回 422，而那个错看起来像是「参数没填」。
            var sent = transport.Only("login/access-token");
            Assert.Equal("POST", sent.Method);
            Assert.Contains("username=donxuelian", sent.Body);
            Assert.Contains("password=tafei520.", sent.Body);
            Assert.DoesNotContain("\"username\"", sent.Body, "这不是一个 JSON 正文");
        });

        Test("MoviePilot 客户端：密码不对时那句话是用户名密码不正确", () =>
        {
            // 这是唯一一种用户自己能修的失败，所以它必须说人话，不能是「服务器返回 401」。
            var transport = new StubTransport().Fail("login/access-token", HttpStatusCode.Unauthorized);

            using var client = new MoviePilotClient(transport);
            var error = Assert.Catch<MoviePilotException>(() =>
                client.SignInAsync(Base, "donxuelian", "wrong", CancellationToken.None).GetAwaiter().GetResult());

            Assert.Contains("用户名或密码", error.Message);
        });

        Test("MoviePilot 客户端：读取时只交出信封里的 data", () =>
        {
            // 每个接口都套着 {success, message, data}，调用方永远不该看见这层壳。
            var transport = new StubTransport().Answer("dashboard/system",
                """{"success":true,"message":"","data":{"hostname":"moviepilot-v3","operating_system":"Debian GNU/Linux 13 (trixie)","runtime":38998,"version":"v3.0.1"}}""");

            using var client = new MoviePilotClient(transport);
            var data = client.GetAsync(Base, "jwt", "dashboard/system", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.Equal(JsonValueKind.Object, data.ValueKind);
            Assert.False(data.TryGetProperty("success", out _), "那层壳不该漏出来");
            Assert.Equal("v3.0.1", data.GetProperty("version").GetString());
        });

        Test("MoviePilot 客户端：HTTP 200 但 success 是 false 也要当失败", () =>
        {
            // 有些接口用 200 揣着一个业务错误，只认状态码的话这种错会被当成成功 —— 上面拿到一份没有字段的
            // 空数据，屏上显示成「连接成功」。
            var transport = new StubTransport().Answer("dashboard/system",
                """{"success":false,"message":"SUPERUSER 对应用户不存在、未启用或非超级管理员","data":null}""");

            using var client = new MoviePilotClient(transport);
            var error = Assert.Catch<MoviePilotException>(() =>
                client.GetAsync(Base, "jwt", "dashboard/system", CancellationToken.None).GetAwaiter().GetResult());

            Assert.Contains("SUPERUSER", error.Message, "服务器自己那句话比任何转述都准");
        });

        Test("MoviePilot 客户端：读取遇到 401 是一种单独的失败，因为它要重新登录", () =>
        {
            // 这一档的处理办法和别的都不一样：地址没错、密码也没错，只是 JWT 到期了。
            var transport = new StubTransport().Fail("subscribe/", HttpStatusCode.Unauthorized);

            using var client = new MoviePilotClient(transport);
            Assert.Catch<MoviePilotTokenExpiredException>(() =>
                client.GetAsync(Base, "stale", "subscribe/", CancellationToken.None).GetAwaiter().GetResult());
        });

        Test("MoviePilot 客户端：连不上时那句话里带得出主机名", () =>
        {
            var transport = new StubTransport().Throw("dashboard/system",
                new HttpRequestException("由于目标计算机积极拒绝，无法连接。"));

            using var client = new MoviePilotClient(transport);
            var error = Assert.Catch<MoviePilotUnreachableException>(() =>
                client.GetAsync(Base, "jwt", "dashboard/system", CancellationToken.None).GetAwaiter().GetResult());

            Assert.Contains("192.168.31.230", error.Message, "连不上时得说连的是哪儿");
        });
    }

    // ── 连接测试 ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 三个接口拼成一条读数。夹具用的是真机抄下来的回话。
    /// </summary>
    private static void ProbeTests()
    {
        Test("MoviePilot 连接测试：三个接口拼出版本、媒体服务器和下载器", () =>
        {
            var transport = new StubTransport()
                .Answer("login/access-token",
                    """{"access_token":"jwt","super_user":true,"user_name":"donxuelian","user_id":1}""")
                .Answer("dashboard/system",
                    """{"success":true,"data":{"hostname":"moviepilot-v3","operating_system":"Debian GNU/Linux 13","version":"v3.0.1"}}""")
                .Answer("mediaserver/clients", """{"success":true,"data":[{"name":"EMBY","type":"emby"}]}""")
                .Answer("download/clients",
                    """{"success":true,"data":[{"name":"PT","type":"qbittorrent"},{"name":"BT","type":"qbittorrent"},{"name":"保种","type":"qbittorrent"}]}""");

            using var client = new MoviePilotClient(transport);
            var status = new MoviePilotProbe(client)
                .RunAsync(Base, "donxuelian", "tafei520.", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.Equal("v3.0.1", status.Version);
            Assert.Equal("moviepilot-v3", status.HostName);
            Assert.Equal(1, status.MediaServers.Count);
            Assert.Equal("EMBY", status.MediaServers[0]);
            Assert.Equal(3, status.Downloaders.Count);
            Assert.Contains("保种", string.Join("、", status.Downloaders), "中文名字要原样带得出来");
        });

        Test("MoviePilot 连接测试：下载器一个都没配不算失败", () =>
        {
            // 没配下载器的 MoviePilot 是完全正常的装机。因为一个空列表把整次测试判失败，
            // 等于把一个能用的服务器说成坏的。
            var transport = new StubTransport()
                .Answer("login/access-token", """{"access_token":"jwt","super_user":true,"user_name":"x","user_id":1}""")
                .Answer("dashboard/system", """{"success":true,"data":{"version":"v3.0.1","hostname":"mp"}}""")
                .Fail("mediaserver/clients", HttpStatusCode.NotFound)
                .Fail("download/clients", HttpStatusCode.NotFound);

            using var client = new MoviePilotClient(transport);
            var status = new MoviePilotProbe(client)
                .RunAsync(Base, "x", "y", CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal("v3.0.1", status.Version, "版本回来了就说明连接是通的");
            Assert.Equal(0, status.MediaServers.Count);
            Assert.Equal(0, status.Downloaders.Count);
        });

        Test("MoviePilot 连接测试：名单接受一个对象也接受一个列表", () =>
        {
            // 这两个接口在真机上回的是列表，但一个对象也是合法的 —— 认不出来就会显示成「无」，
            // 而「无」和「没读到」在屏上长得一模一样。
            Assert.Equal(2, MoviePilotProbe.Names(Json("""[{"name":"甲"},{"name":"乙"}]""")).Count);
            Assert.Equal(1, MoviePilotProbe.Names(Json("""{"name":"甲"}""")).Count);

            // 没有 name 的那些退回 type，两样都没有就跳过 —— 一个空字符串摆在名单里比不摆更糟。
            Assert.Equal("emby", MoviePilotProbe.Names(Json("""[{"type":"emby"}]"""))[0]);
            Assert.Equal(0, MoviePilotProbe.Names(Json("""[{"id":3}]""")).Count);
            Assert.Equal(0, MoviePilotProbe.Names(Json("""null""")).Count);
        });

        Test("MoviePilot 凭据：存下来的是密文，读回来的是明文", () =>
        {
            var settings = new MoviePilotSettings();
            var credentials = new MoviePilotCredentials(PassthroughSecretProtector.Instance);

            credentials.SetPassword(settings, "tafei520.");
            Assert.True(settings.HasSavedPassword, "存过就该说存过");
            Assert.Equal("tafei520.", credentials.GetPassword(settings));

            // 清空是真的清空 —— 用户在框里删掉之后不该还留着上一个。
            credentials.ClearPassword(settings);
            Assert.False(settings.HasSavedPassword);
            Assert.Equal("", credentials.GetPassword(settings));
        });

        Test("MoviePilot 设置：装机默认是关的、空的", () =>
        {
            // 新装的程序不该去连一台没人配过的服务器。
            var settings = new AppSettings();
            Assert.False(settings.MoviePilot.Enabled);
            Assert.Equal("", settings.MoviePilot.Url);
            Assert.False(settings.MoviePilot.HasSavedPassword);
            Assert.Null(settings.MoviePilot.LastConnected, "从没连过就该是 null，好和「连过」分得开");
        });
    }

    private static Uri Base => new("http://192.168.31.230:3001/");

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
