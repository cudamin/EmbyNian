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
        SearchParserTests();
        ClientPostTests();
        SearchServiceTests();
        ImageServiceTests();
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
            Assert.True(MoviePilotAddress.TryNormalize("192.0.2.10:3001", out var address, out var error), error);
            Assert.NotNull(address);
            Assert.Equal("http", address!.Scheme, "没写协议时应当补 http");
            Assert.Equal("192.0.2.10", address.Host);
            Assert.Equal(3001, address.Port);
        });

        Test("MoviePilot 地址：没写端口时补 API 那个 3001，而不是网址默认的 80", () =>
        {
            // 这条是这张卡最容易搞错的地方：MoviePilot 的网页界面和 API 是两个端口，
            // http://host/ 解析出来会是 80，而 80 上什么都没有。
            Assert.True(MoviePilotAddress.TryNormalize("192.0.2.10", out var address, out _));
            Assert.Equal(3001, address!.Port);
        });

        Test("MoviePilot 地址：自己写的端口一律照用，包括写成网页那个 3000", () =>
        {
            // 明写着 3000 的人是在说「我的 API 就在 3000 上」。替他改掉等于让他对着一个能连通的地址看
            // 「连不上」，而真正的答案（这个端口上没有 API）是他自己一看就明白的。
            Assert.True(MoviePilotAddress.TryNormalize("192.0.2.10:3000", out var address, out _));
            Assert.Equal(3000, address!.Port);
        });

        Test("MoviePilot 地址：带路径的网址只取到主机和端口", () =>
        {
            // 从浏览器地址栏复制下来的一整串是常事，尾巴上的页面路径不该进到 API 地址里。
            Assert.True(MoviePilotAddress.TryNormalize("http://192.0.2.10:3001/#/dashboard", out var address, out _));
            Assert.Equal("http://192.0.2.10:3001/", address!.AbsoluteUri);
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
            Assert.True(MoviePilotAddress.TryNormalize("192.0.2.10:3001", out var address, out _));
            Assert.Equal("192.0.2.10", MoviePilotAddress.ToDisplayString(address!));
            Assert.Equal(3001, address!.Port, "收起来了，但用的时候仍是 3001");

            // 自己写的非默认端口要留在显示串里 —— 丢掉它等于把用户写的东西改掉了。
            Assert.True(MoviePilotAddress.TryNormalize("192.0.2.10:3000", out var custom, out _));
            Assert.Equal("192.0.2.10:3000", MoviePilotAddress.ToDisplayString(custom!));

            // 而显示串本身读回去还是同一个地址：来回走一趟不该越走越远。
            Assert.True(MoviePilotAddress.TryNormalize(MoviePilotAddress.ToDisplayString(custom!), out var again, out _));
            Assert.Equal(custom!.AbsoluteUri, again!.AbsoluteUri);
        });

        Test("MoviePilot 地址：拼出来的请求地址落在 api/v1 底下", () =>
        {
            Assert.True(MoviePilotAddress.TryNormalize("192.0.2.10:3001", out var address, out _));

            Assert.Equal(
                "http://192.0.2.10:3001/api/v1/dashboard/system",
                MoviePilotAddress.Combine(address!, "api/v1/dashboard/system").AbsoluteUri);

            // 前头多一个斜杠的写法也该落在同一个地方 —— 这是调用方最容易写错的那一下。
            Assert.Equal(
                "http://192.0.2.10:3001/api/v1/subscribe/",
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
                """{"access_token":"jwt-abc","token_type":"bearer","super_user":true,"user_id":1,"user_name":"docuser"}""");

            using var client = new MoviePilotClient(transport);
            var session = client.SignInAsync(Base, "docuser", "docpass.1", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.Equal("jwt-abc", session.AccessToken);
            Assert.Equal("docuser", session.UserName);
            Assert.True(session.SuperUser, "这台服务器上这个账号是超级管理员，标志位该照实读出来");

            // 表单，不是 JSON。发 JSON 会被 FastAPI 回 422，而那个错看起来像是「参数没填」。
            var sent = transport.Only("login/access-token");
            Assert.Equal("POST", sent.Method);
            Assert.Contains("username=docuser", sent.Body);
            Assert.Contains("password=docpass.1", sent.Body);
            Assert.DoesNotContain("\"username\"", sent.Body, "这不是一个 JSON 正文");
        });

        Test("MoviePilot 客户端：密码不对时那句话是用户名密码不正确", () =>
        {
            // 这是唯一一种用户自己能修的失败，所以它必须说人话，不能是「服务器返回 401」。
            var transport = new StubTransport().Fail("login/access-token", HttpStatusCode.Unauthorized);

            using var client = new MoviePilotClient(transport);
            var error = Assert.Catch<MoviePilotException>(() =>
                client.SignInAsync(Base, "docuser", "wrong", CancellationToken.None).GetAwaiter().GetResult());

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

            Assert.Contains("192.0.2.10", error.Message, "连不上时得说连的是哪儿");
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
                    """{"access_token":"jwt","super_user":true,"user_name":"docuser","user_id":1}""")
                .Answer("dashboard/system",
                    """{"success":true,"data":{"hostname":"moviepilot-v3","operating_system":"Debian GNU/Linux 13","version":"v3.0.1"}}""")
                .Answer("mediaserver/clients", """{"success":true,"data":[{"name":"EMBY","type":"emby"}]}""")
                .Answer("download/clients",
                    """{"success":true,"data":[{"name":"PT","type":"qbittorrent"},{"name":"BT","type":"qbittorrent"},{"name":"保种","type":"qbittorrent"}]}""");

            using var client = new MoviePilotClient(transport);
            var status = new MoviePilotProbe(client)
                .RunAsync(Base, "docuser", "docpass.1", CancellationToken.None)
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

            credentials.SetPassword(settings, "docpass.1");
            Assert.True(settings.HasSavedPassword, "存过就该说存过");
            Assert.Equal("docpass.1", credentials.GetPassword(settings));

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

    // ── 搜索结果解析 ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>media/search</c> 回来的那一片怎么拆成卡片，以及一条结果怎么拼成订阅正文。屏上「没搜到」和「拆错了」
    /// 长得一样，所以这几条钉的就是「字段对上了、几种写法都认得」。
    /// </summary>
    private static void SearchParserTests()
    {
        // media/search 在这台服务器上回的是裸数组；字段照 v3.0.1 MediaInfo 取（title/year/type/媒体身份对/海报）。
        const string Results =
            """
            [
              {"title":"星际穿越","year":2014,"type":"电影","media_source":"themoviedb","media_id":"157336",
               "poster_path":"https://image.tmdb.org/t/p/w500/x.jpg","overview":"一队探险者……"},
              {"title":"三体","year":2023,"type":"电视剧","media_source":"themoviedb","media_id":100,"poster_path":null}
            ]
            """;

        Test("MoviePilot 搜索：裸数组按 MediaInfo 字段拆成卡片", () =>
        {
            var list = MoviePilotMediaParser.Parse(Json(Results));

            Assert.Equal(2, list.Count);
            Assert.Equal("星际穿越", list[0].Title);
            Assert.Equal(2014, list[0].Year);
            Assert.Equal("电影", list[0].Type);
            Assert.Equal("themoviedb", list[0].MediaSource);
            Assert.Equal("157336", list[0].MediaId);
            Assert.Equal("https://image.tmdb.org/t/p/w500/x.jpg", list[0].PosterUrl);
            Assert.True(list[0].CanSubscribe, "身份对齐了就能订阅");

            // media_id 以数字回来也要能读成字符串 —— 订阅正文回填的就是它。
            Assert.Equal("100", list[1].MediaId);
            Assert.Null(list[1].PosterUrl, "海报是 null 就老实是 null，别塞个空串");
        });

        Test("MoviePilot 搜索：没有标题的当噪声跳过，认不出的整体回空", () =>
        {
            Assert.Equal(0, MoviePilotMediaParser.Parse(Json("""[{"year":2020,"media_id":"1"}]""")).Count);
            Assert.Equal(0, MoviePilotMediaParser.Parse(Json("""{}""")).Count);
            Assert.Equal(0, MoviePilotMediaParser.Parse(Json("""null""")).Count);

            // 万一哪天被信封或某个键包了一层，也别整片空掉。
            Assert.Equal(1, MoviePilotMediaParser.Parse(Json("""{"list":[{"title":"甲"}]}""")).Count);
        });

        Test("MoviePilot 搜索：订阅正文只带该带的，空值一律不放", () =>
        {
            var full = new MoviePilotMedia
            {
                Title = "星际穿越",
                Year = 2014,
                Type = "电影",
                MediaSource = "themoviedb",
                MediaId = "157336"
            };
            var body = MoviePilotMediaParser.SubscribeBody(full);
            Assert.Equal("星际穿越", body["name"]);
            Assert.Equal("电影", body["type"]);
            Assert.Equal("themoviedb", body["media_source"]);
            Assert.Equal("157336", body["media_id"]);

            // 有年份也不发 year：订阅 schema 的 year 是字符串，发数字会被判「请求参数不正确」（真机 v3.0.1 实测的
            // 那次订阅失败就是它）。身份对已经唯一定位这部片，年份多余。
            Assert.False(body.ContainsKey("year"), "订阅正文不带 year，避免 int 顶到 str 上的 422");

            // 缺身份的那条：不能凭空造一个 media_id 键出来。
            var bare = new MoviePilotMedia { Title = "只有名字" };
            var sparse = MoviePilotMediaParser.SubscribeBody(bare);
            Assert.False(sparse.ContainsKey("media_id"), "没有就不放这个键");
            Assert.False(bare.CanSubscribe, "缺身份对就不该让订阅");
        });

        Test("MoviePilot 资源：Context 列表拆成种子行，torrent_info 原样留着", () =>
        {
            const string Resources =
                """
                [
                  {"meta_info":{"resource_pix":"1080p"},
                   "torrent_info":{"title":"Some.Movie.1080p.WEB-DL","site_name":"站点A","size":5368709120,"seeders":12,"page_url":"http://x/y"}},
                  {"torrent_info":{"title":"Some.Movie.2160p","site_name":"站点B","size":21474836480,"seeders":3}}
                ]
                """;

            var list = MoviePilotMediaParser.ParseResources(Json(Resources), "themoviedb", "1");

            Assert.Equal(2, list.Count);
            Assert.Equal("Some.Movie.1080p.WEB-DL", list[0].Title);
            Assert.Equal("站点A", list[0].SiteName);
            Assert.Equal("1080p", list[0].Resolution);
            Assert.Equal(12, list[0].Seeders);
            Assert.Equal("5 GB", list[0].SizeText);
            Assert.Equal("", (list[0] with { Size = 0 }).SizeText);
            Assert.Equal("", (list[0] with { Size = -1 }).SizeText);
            Assert.Equal("1023 B", (list[0] with { Size = 1023 }).SizeText);
            Assert.Equal("1 KB", (list[0] with { Size = 1024 }).SizeText);
            Assert.Equal("1.5 MB", (list[0] with { Size = 1572864 }).SizeText);
            Assert.Equal("1 TB", (list[0] with { Size = 1099511627776 }).SizeText);
            Assert.Equal("themoviedb", list[0].MediaSource, "身份对盖的是搜的那部片");
            Assert.Equal("1", list[0].MediaId);
            Assert.Equal("Some.Movie.1080p.WEB-DL", list[0].TorrentInfo.GetProperty("title").GetString(), "整份种子信息原样留着");
        });

        Test("MoviePilot 资源：下载正文原样回传 torrent_info、带身份对", () =>
        {
            const string One = """[{"torrent_info":{"title":"T","site_name":"S","size":1073741824,"seeders":5}}]""";
            var res = MoviePilotMediaParser.ParseResources(Json(One), "themoviedb", "157336")[0];

            var body = MoviePilotMediaParser.DownloadBody(res);
            Assert.True(body.ContainsKey("torrent_in"), "下载要回传整份种子信息");
            Assert.Equal("themoviedb", body["media_source"]);
            Assert.Equal("157336", body["media_id"]);
            Assert.Equal("T", ((JsonElement)body["torrent_in"]!).GetProperty("title").GetString());
        });

        Test("MoviePilot 资源：没有 torrent_info 外壳的扁种子也认（search/title 兜底）", () =>
        {
            // search/media 回的是 Context（外面包一层 torrent_info）；万一哪个版本的关键词搜索直接回一层扁的种子
            // 对象，也要认得出来——认不出会在屏上变成「没搜到」，和真没结果分不开。
            const string Flat = """[{"title":"扁种子 1080p","site_name":"站点B","size":1073741824,"seeders":4}]""";
            var list = MoviePilotMediaParser.ParseResources(Json(Flat), null, null);

            Assert.Equal(1, list.Count);
            Assert.Equal("扁种子 1080p", list[0].Title);
            Assert.Equal("站点B", list[0].SiteName);
        });
    }

    // ── 写接口（订阅走的 POST） ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>PostAsync</c> 和 <c>GetAsync</c> 共用同一套回话处理，所以这里钉的是「发出去真是一趟带 JSON 正文的
    /// POST」，以及那三种失败（401 要重登、200 揣 success:false、拆信封）在写接口上一样成立。
    /// </summary>
    private static void ClientPostTests()
    {
        Test("MoviePilot 写接口：POST 发的是 JSON 正文，拿回信封里的 data", () =>
        {
            var transport = new StubTransport().Answer("subscribe/",
                """{"success":true,"message":"订阅成功","data":{"id":42}}""");

            using var client = new MoviePilotClient(transport);
            var body = new Dictionary<string, object?> { ["name"] = "星际穿越", ["media_source"] = "themoviedb", ["media_id"] = "157336" };
            var data = client.PostAsync(Base, "jwt", "subscribe/", body, CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(42, data.GetProperty("id").GetInt32());

            var sent = transport.Only("subscribe/");
            Assert.Equal("POST", sent.Method);
            // 共用的 JSON 选项把非 ASCII 转成 \uXXXX（合法 JSON，服务器照样解得回中文），所以断言看 ASCII 那几项。
            Assert.Contains("\"name\":", sent.Body);
            Assert.Contains("\"media_source\":\"themoviedb\"", sent.Body);
            Assert.Contains("\"media_id\":\"157336\"", sent.Body);
        });

        Test("MoviePilot 写接口：401 是令牌过期那一档，成功 200 揣 success:false 也当失败", () =>
        {
            var expired = new StubTransport().Fail("subscribe/", HttpStatusCode.Unauthorized);
            using (var client = new MoviePilotClient(expired))
                Assert.Catch<MoviePilotTokenExpiredException>(() =>
                    client.PostAsync(Base, "stale", "subscribe/", new { }, CancellationToken.None).GetAwaiter().GetResult());

            var refused = new StubTransport().Answer("subscribe/",
                """{"success":false,"message":"必须同时提供有效的 media_source 和 media_id"}""");
            using (var client = new MoviePilotClient(refused))
            {
                var error = Assert.Catch<MoviePilotException>(() =>
                    client.PostAsync(Base, "jwt", "subscribe/", new { }, CancellationToken.None).GetAwaiter().GetResult());
                Assert.Contains("media_source", error.Message, "服务器自己那句话要带出来");
            }
        });
    }

    // ── 连接服务：按需登录、搜、订阅、令牌过期重放 ─────────────────────────────────────────────────

    /// <summary>
    /// <see cref="MoviePilotService"/> 把「有没有登录、token 过没过期、过了怎么重登重放」收在一处。这几条钉的
    /// 就是那条链：现登录→搜→拆；空词一趟不发；401 只重登重放一次；没填账号说人话不发请求。
    /// </summary>
    private static void SearchServiceTests()
    {
        Test("MoviePilot 连接：先登录再搜，结果拆成卡片", () =>
        {
            var transport = new StubTransport()
                .Answer("login/access-token", """{"access_token":"jwt","super_user":true,"user_name":"docuser","user_id":1}""")
                .Answer("media/search", """[{"title":"星际穿越","year":2014,"type":"电影","media_source":"themoviedb","media_id":"157336"}]""");

            var list = ServiceOn(transport).SearchAsync("星际穿越", CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(1, list.Count);
            Assert.Equal("星际穿越", list[0].Title);
            Assert.Contains("media/search?title=", transport.SentTo("media/search")[0].Url);
            Assert.Equal(1, transport.Count("login/access-token"), "只登一次");
        });

        Test("MoviePilot 连接：空词一趟都不发", () =>
        {
            var transport = new StubTransport();
            var list = ServiceOn(transport).SearchAsync("   ", CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(0, list.Count);
            Assert.Equal(0, transport.Total, "空词不联网");
        });

        Test("MoviePilot 连接：令牌过期会重登一次再重放", () =>
        {
            var transport = new StubTransport()
                .Answer("login/access-token", """{"access_token":"jwt","super_user":true,"user_name":"docuser","user_id":1}""")
                .Sequence("media/search",
                    (HttpStatusCode.Unauthorized, ""),
                    (HttpStatusCode.OK, """[{"title":"三体","media_source":"themoviedb","media_id":"1"}]"""));

            var list = ServiceOn(transport).SearchAsync("三体", CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(1, list.Count);
            Assert.Equal(2, transport.Count("media/search"), "过期一次、重放一次");
            Assert.Equal(2, transport.Count("login/access-token"), "过期后重登了一次");
        });

        Test("MoviePilot 连接：订阅 POST 到 subscribe/ 带身份对", () =>
        {
            var transport = new StubTransport()
                .Answer("login/access-token", """{"access_token":"jwt","super_user":true,"user_name":"docuser","user_id":1}""")
                .Answer("media/search", """[{"title":"星际穿越","year":2014,"type":"电影","media_source":"themoviedb","media_id":"157336"}]""")
                .Answer("subscribe/", """{"success":true,"data":{"id":7}}""");

            var service = ServiceOn(transport);
            var media = service.SearchAsync("星际穿越", CancellationToken.None).GetAwaiter().GetResult().Single();
            service.SubscribeAsync(media, CancellationToken.None).GetAwaiter().GetResult();

            var sent = transport.Only("subscribe/");
            Assert.Equal("POST", sent.Method);
            Assert.Contains("themoviedb", sent.Body);
            Assert.Contains("157336", sent.Body);
        });

        Test("MoviePilot 连接：没填用户名密码时说人话、不发请求", () =>
        {
            var settings = new AppSettings();
            settings.MoviePilot.Enabled = true;
            settings.MoviePilot.Url = "192.0.2.10:3001";
            // 用户名密码空着——登录都无从谈起。

            var transport = new StubTransport();
            var service = new MoviePilotService(
                new MoviePilotClient(transport),
                new MoviePilotCredentials(PassthroughSecretProtector.Instance),
                settings);

            var error = Assert.Catch<MoviePilotException>(() =>
                service.SearchAsync("x", CancellationToken.None).GetAwaiter().GetResult());

            Assert.Contains("用户名", error.Message);
            Assert.Equal(0, transport.Total, "登不了就不该发出任何请求");
        });

        Test("MoviePilot 连接：精确资源下载保留媒体快照，POST 到 download/", () =>
        {
            var transport = new StubTransport()
                .Answer("login/access-token", """{"access_token":"jwt","super_user":true,"user_name":"docuser","user_id":1}""")
                .Answer("search/media/", """[{"media_info":{"title":"某片","media_source":"themoviedb","media_id":"157336","type":"电影"},"media_info_is_target":true,"match_status":"exact","torrent_info":{"title":"Some.1080p","site_name":"A","size":5368709120,"seeders":9}}]""")
                .Answer("download/", """{"success":true,"data":{"download_id":"abc"}}""");

            var media = new MoviePilotMedia { Title = "某片", Type = "电影", MediaSource = "themoviedb", MediaId = "157336" };
            var service = ServiceOn(transport);

            var resources = service.SearchResourcesAsync(media, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(1, resources.Count);
            var sent = transport.SentTo("search/media/")[0];
            Assert.Contains("search/media/157336", sent.Url, "按 media_id 精确搜");
            Assert.Contains("media_source=themoviedb", sent.Url);

            service.DownloadAsync(resources[0], CancellationToken.None).GetAwaiter().GetResult();
            var dl = transport.Only("download/");
            Assert.Equal("POST", dl.Method);
            Assert.Contains("torrent_in", dl.Body);
            Assert.Contains("media_in", dl.Body);
            Assert.Equal("电影", Json(dl.Body).GetProperty("media_in").GetProperty("type").GetString());
            Assert.Contains("Some.1080p", dl.Body);
        });

        Test("MoviePilot 连接：关键词搜版本走 search/title，回来的种子拆成行", () =>
        {
            var transport = new StubTransport()
                .Answer("login/access-token", """{"access_token":"jwt","super_user":true,"user_name":"docuser","user_id":1}""")
                .Answer("search/title", """{"success":true,"data":[{"torrent_info":{"title":"某剧 S01E01 1080p","site_name":"站点A","size":2147483648,"seeders":7}}]}""");

            var list = ServiceOn(transport).SearchByKeywordAsync("某剧 S01 E01", CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(1, list.Count);
            Assert.Equal("某剧 S01E01 1080p", list[0].Title);
            var sent = transport.SentTo("search/title")[0];
            Assert.Contains("search/title?keyword=", sent.Url);
            // 关键字里的空格得转义（%20），别把 S01 E01 当成两个查询参数。
            Assert.Contains("%20", sent.Url, "关键字里的空格要转义");
        });

        Test("MoviePilot 连接：关键词为空一趟都不发", () =>
        {
            var transport = new StubTransport();
            var list = ServiceOn(transport).SearchByKeywordAsync("  ", CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(0, list.Count);
            Assert.Equal(0, transport.Total, "空词不联网");
        });
    }

    private static void ImageServiceTests()
    {
        Test("MoviePilot 连接：订阅取图走 system/cache/image 的代理", () =>
        {
            var transport = new StubTransport()
                .Answer("cache/image", "JPGDATA");

            var service = ServiceOn(transport);

            var bytes = service.FetchImageAsync("https://image.tmdb.org/t/p/w500/abc.jpg", CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(1, transport.Total, "取图不读取下载列表");
            Assert.Equal("JPGDATA", System.Text.Encoding.UTF8.GetString(bytes));
            var image = transport.Only("cache/image");
            Assert.Equal("GET", image.Method);
            Assert.Contains("/api/v1/system/cache/image?url=", image.Url, "走服务器的图片代理");
            Assert.Contains("image.tmdb.org", image.Url);
            Assert.Contains("%2F", image.Url, "图址要整串转义，别把路径当查询参数拆开");
        });
    }

    /// <summary>一台配好地址账号密码、启用了的 MoviePilot 连接服务，接在给定的假传输层上。</summary>
    private static MoviePilotService ServiceOn(StubTransport transport)
    {
        var settings = new AppSettings();
        settings.MoviePilot.Enabled = true;
        settings.MoviePilot.Url = "192.0.2.10:3001";
        settings.MoviePilot.Username = "docuser";

        var credentials = new MoviePilotCredentials(PassthroughSecretProtector.Instance);
        credentials.SetPassword(settings.MoviePilot, "docpass.1");

        return new MoviePilotService(new MoviePilotClient(transport), credentials, settings);
    }

    private static Uri Base => new("http://192.0.2.10:3001/");

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
