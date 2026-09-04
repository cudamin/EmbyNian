using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>URL building, address normalisation and the formatting helpers the UI leans on.</summary>
internal static class EmbyTests
{
    private static readonly Uri ApiBase = new("http://192.168.31.230:8896/emby/");

    public static void Register()
    {
        RegisterAddress();
        RegisterDashboard();
        RegisterDiscovery();
        RegisterUrls();
        RegisterQuery();
        RegisterSort();
        RegisterFilters();
        RegisterIdentity();
        RegisterTimeFormat();
        RegisterItemModel();
        RegisterMetadataEdit();
        RegisterImageCache();
    }

    private static void RegisterAddress()
    {
        Test("服务器地址：补协议、补 /emby/", () =>
        {
            Assert.Equal("http://192.168.31.230:8896/emby/", EmbyServerAddress.Normalize("192.168.31.230:8896").AbsoluteUri);
            Assert.Equal("http://h:8096/emby/", EmbyServerAddress.Normalize("http://h:8096").AbsoluteUri);
            Assert.Equal("http://h:8096/emby/", EmbyServerAddress.Normalize("http://h:8096/").AbsoluteUri);
        });

        Test("服务器地址：用户粘贴的 /emby 不会变成 /emby/emby/", () =>
        {
            Assert.Equal("http://h:8096/emby/", EmbyServerAddress.Normalize("http://h:8096/emby").AbsoluteUri);
            Assert.Equal("http://h:8096/emby/", EmbyServerAddress.Normalize("http://h:8096/emby/").AbsoluteUri);
        });

        Test("服务器地址：反向代理的子路径要保留", () =>
        {
            Assert.Equal("https://nas.example.com/media/emby/", EmbyServerAddress.Normalize("https://nas.example.com/media").AbsoluteUri);
            Assert.Equal("https://nas.example.com/media/emby/", EmbyServerAddress.Normalize("https://nas.example.com/media/emby/").AbsoluteUri);
        });

        Test("服务器地址：查询串与片段被丢掉", () =>
        {
            Assert.Equal("http://h:8096/emby/", EmbyServerAddress.Normalize("http://h:8096/?x=1#y").AbsoluteUri);
        });

        Test("服务器地址：错误输入给出中文提示而不是异常", () =>
        {
            Assert.False(EmbyServerAddress.TryNormalize("", out _, out var empty));
            Assert.Contains("请填写", empty);

            Assert.False(EmbyServerAddress.TryNormalize("ftp://h", out _, out var scheme));
            Assert.Contains("http", scheme);

            Assert.Throws<ArgumentException>(() => EmbyServerAddress.Normalize("   "));
        });

        Test("服务器地址：显示回给用户时去掉 /emby", () =>
        {
            Assert.Equal("http://192.168.31.230:8896", EmbyServerAddress.ToDisplayString(ApiBase));
            Assert.Equal("https://nas.example.com/media",
                EmbyServerAddress.ToDisplayString(new Uri("https://nas.example.com/media/emby/")));
        });
    }

    private static void RegisterDashboard()
    {
        Test("控制台地址：网页端根目录 + dashboard 路由", () =>
        {
            Assert.Equal("http://192.168.31.230:8896/web/index.html#!/dashboard", EmbyWebConsole.Url(ApiBase));
            Assert.Equal("http://192.168.31.230:8896", EmbyWebConsole.Root(ApiBase));
        });

        Test("控制台地址：子路径与默认端口都跟着走", () =>
        {
            Assert.Equal("https://nas.example.com/media/web/index.html#!/dashboard",
                EmbyWebConsole.Url(EmbyServerAddress.Normalize("https://nas.example.com/media")));
            Assert.Equal("https://nas.example.com/web/index.html#!/dashboard",
                EmbyWebConsole.Url(EmbyServerAddress.Normalize("https://nas.example.com")));
        });

        Test("控制台免登录脚本：写进网页端自己的凭据条目", () =>
        {
            var script = EmbyWebConsole.SignInScript(ApiBase, "user-1", "token-1", "客厅 Emby");

            Assert.Contains(EmbyWebConsole.StorageKey, script);
            Assert.Contains("\"user-1\"", script);
            Assert.Contains("\"token-1\"", script);
            Assert.Contains("\"http://192.168.31.230:8896\"", script);
            Assert.Contains("ManualAddressOnly", script);
            Assert.Contains("server.Users = users", script);
        });

        Test("控制台免登录脚本：只对自己这台服务器的页面动手", () =>
        {
            var script = EmbyWebConsole.SignInScript(ApiBase, "user-1", "token-1", "Emby");

            Assert.Contains("location.host", script);
            Assert.Contains("\"192.168.31.230:8896\"", script);

            // 默认端口的 https 站点，location.host 里不带 :443。
            var plain = EmbyWebConsole.SignInScript(EmbyServerAddress.Normalize("https://nas.example.com"), "u", "t", "Emby");
            Assert.Contains("\"nas.example.com\"", plain);
            Assert.False(plain.Contains("nas.example.com:443", StringComparison.Ordinal));
        });

        Test("控制台免登录脚本：令牌只以 JSON 转义的形式出现一次", () =>
        {
            var script = EmbyWebConsole.SignInScript(ApiBase, "user-1", "tok\"en</script>", "Emby");

            Assert.False(script.Contains("tok\"en", StringComparison.Ordinal));
            Assert.False(script.Contains("</script>", StringComparison.Ordinal));
            Assert.Equal(1, Occurrences(script, "tok\\u0022en"));
        });

        Test("控制台免登录脚本：中文服务器名被转义，没名字时退回 Emby", () =>
        {
            var named = EmbyWebConsole.SignInScript(ApiBase, "u", "t", "  果服  ");
            Assert.False(named.Contains("果服", StringComparison.Ordinal));
            Assert.Contains("\\u679C\\u670D", named);

            Assert.Contains("\"Emby\"", EmbyWebConsole.SignInScript(ApiBase, "u", "t", "   "));
        });

        Test("控制台免登录脚本：没有账号或令牌就直接拒绝", () =>
        {
            Assert.Throws<ArgumentException>(() => EmbyWebConsole.SignInScript(ApiBase, "", "t", "Emby"));
            Assert.Throws<ArgumentException>(() => EmbyWebConsole.SignInScript(ApiBase, "u", "", "Emby"));
        });

        Test("控制台主题脚本：两个主题键都写成跟随浏览器深浅", () =>
        {
            var script = EmbyWebConsole.ThemeScript(ApiBase, "user-1");

            // 键带用户 id 前缀（appsettings.js 的 getKey），值是 auto —— 只有 auto 那条分支不过注册检查，
            // 深浅由 DashboardPage 设的 PreferredColorScheme 决定。
            Assert.Contains("\"user-1-appTheme\"", script);
            Assert.Contains("\"user-1-settingsTheme\"", script);
            Assert.Contains("\"auto\"", script);
            Assert.Equal("auto", EmbyWebConsole.FollowColorScheme);

            // 存服务器的那个键一个都不许碰：改它就等于改用户其他设备上的 Emby。
            Assert.False(script.Contains("accentColor", StringComparison.Ordinal));
        });

        Test("控制台主题脚本：只对自己这台服务器的页面动手，没有账号就拒绝", () =>
        {
            var script = EmbyWebConsole.ThemeScript(ApiBase, "user-1");
            Assert.Contains("location.host", script);
            Assert.Contains("\"192.168.31.230:8896\"", script);

            Assert.Throws<ArgumentException>(() => EmbyWebConsole.ThemeScript(ApiBase, ""));
        });
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static void RegisterDiscovery()
    {
        Test("局域网发现：解析 Emby 的 HTTP 头 + JSON 响应", () =>
        {
            const string payload = "HTTP/1.1 200 OK\r\nServer: EmbyServer\r\nContent-Type: application/json\r\n\r\n" +
                "{\"Address\":\"192.168.1.20\",\"Id\":\"server-1\",\"Name\":\"客厅 Emby\",\"Port\":8096,\"Version\":\"4.8.10.0\"}";

            Assert.True(EmbyServerDiscovery.TryParseResponse(
                Encoding.UTF8.GetBytes(payload),
                new IPEndPoint(IPAddress.Parse("192.168.1.20"), EmbyServerDiscovery.DiscoveryPort),
                out var server));

            Assert.Equal("客厅 Emby", server.Name);
            Assert.Equal("http://192.168.1.20:8096", server.Url);
            Assert.Equal("http://192.168.1.20:8096/emby/", server.ApiBase.AbsoluteUri);
            Assert.Equal("server-1", server.Id);
            Assert.Equal("4.8.10.0", server.Version);
        });

        Test("局域网发现：纯 JSON、HTTPS 与数字端口", () =>
        {
            const string payload = "{\"Name\":\"NAS\",\"Address\":\"nas.example.test\",\"Port\":8920,\"Protocol\":\"https\",\"Id\":\"nas-id\"}";
            Assert.True(EmbyServerDiscovery.TryParseResponse(Encoding.UTF8.GetBytes(payload), null, out var server));
            Assert.Equal("https://nas.example.test:8920", server.Url);
            Assert.Equal("https://nas.example.test:8920/emby/", server.ApiBase.AbsoluteUri);
        });

        Test("局域网发现：缺少地址时使用 UDP 来源地址", () =>
        {
            const string payload = "Server: EmbyServer\r\n\r\n{\"Name\":\"本机\",\"Port\":8097}";
            Assert.True(EmbyServerDiscovery.TryParseResponse(
                Encoding.UTF8.GetBytes(payload),
                new IPEndPoint(IPAddress.Parse("10.0.0.7"), EmbyServerDiscovery.DiscoveryPort),
                out var server));
            Assert.Equal("http://10.0.0.7:8097", server.Url);
        });

        Test("局域网发现：没有空行的 HTTP 头也能解析", () =>
        {
            const string payload = "HTTP/1.1 200 OK\nServer: EmbyServer\nEmby-Server-Name: 无空行\n" +
                "Emby-Server-Address: 192.168.1.21:8096\nEmby-Server-Id: no-blank";
            Assert.True(EmbyServerDiscovery.TryParseResponse(Encoding.UTF8.GetBytes(payload), null, out var server));
            Assert.Equal("无空行", server.Name);
            Assert.Equal("http://192.168.1.21:8096", server.Url);
        });

        Test("局域网发现：拒绝未知广播与非法地址", () =>
        {
            Assert.False(EmbyServerDiscovery.TryParseResponse(
                Encoding.UTF8.GetBytes("hello from another service"), null, out _));
            Assert.False(EmbyServerDiscovery.TryParseResponse(
                Encoding.UTF8.GetBytes("{\"Name\":\"bad\",\"Address\":\"ftp://host\"}"), null, out _));
        });
    }

    private static void RegisterUrls()
    {
        Test("URL：播放地址带容器后缀且不含令牌", () =>
        {
            var url = EmbyUrl.Stream(ApiBase, "42", "src1", "mkv");

            Assert.Equal("http://192.168.31.230:8896/emby/Videos/42/stream.mkv?Static=true&MediaSourceId=src1", url.AbsoluteUri);
            Assert.DoesNotContain("api_key", url.AbsoluteUri, "令牌只能走请求头");
            Assert.DoesNotContain("X-Emby-Token", url.AbsoluteUri);
        });

        Test("URL：多容器只取第一个后缀", () =>
        {
            Assert.Contains("/stream.mkv?", EmbyUrl.Stream(ApiBase, "42", "src1", "mkv,webm").AbsoluteUri);
            Assert.Contains("/stream?", EmbyUrl.Stream(ApiBase, "42", "src1", null).AbsoluteUri, "容器未知时不加后缀");
        });

        Test("URL：外挂字幕按编解码器选扩展名", () =>
        {
            Assert.Contains("/Subtitles/4/Stream.ass", EmbyUrl.Subtitle(ApiBase, "42", "src1", 4, "ass").AbsoluteUri);
            Assert.Contains("/Subtitles/4/Stream.srt", EmbyUrl.Subtitle(ApiBase, "42", "src1", 4, "subrip").AbsoluteUri);
            Assert.Contains("/Subtitles/4/Stream.vtt", EmbyUrl.Subtitle(ApiBase, "42", "src1", 4, "webvtt").AbsoluteUri);
            Assert.Contains("/Subtitles/4/Stream.srt", EmbyUrl.Subtitle(ApiBase, "42", "src1", 4, null).AbsoluteUri, "未知编解码器退回 srt");
        });

        Test("URL：图片地址带宽度与 tag，缺 tag 时不留空参数", () =>
        {
            var withTag = EmbyUrl.Image(ApiBase, "42", "Primary", "tag-1", 340).AbsoluteUri;
            Assert.Contains("Items/42/Images/Primary?maxWidth=340", withTag);
            Assert.Contains("tag=tag-1", withTag);

            Assert.DoesNotContain("tag=", EmbyUrl.Image(ApiBase, "42", "Primary", null, 340).AbsoluteUri, "null 参数应被跳过");
        });

        Test("URL：章节缩略图按序号入路径，同样不带 api_key", () =>
        {
            // The seek bar's hover preview asks for these by chapter index, so the index has to
            // land in the path rather than in an image-type query parameter.
            var url = EmbyUrl.ChapterImage(ApiBase, "42", 3, "chapter-tag", 320).AbsoluteUri;
            Assert.Contains("Items/42/Images/Chapter/3?maxWidth=320", url);
            Assert.Contains("tag=chapter-tag", url);
            Assert.DoesNotContain("api_key", url, "图片地址也走 X-Emby-Token 头，不能把令牌写进 URL");

            Assert.DoesNotContain(
                "tag=",
                EmbyUrl.ChapterImage(ApiBase, "42", 0, null, 320).AbsoluteUri,
                "没有 tag 的章节不留空参数");
        });

        Test("URL：参数值里的特殊字符被转义", () =>
        {
            var url = EmbyUrl.Combine(ApiBase, "Items", ("SearchTerm", "钢之炼金术师 & Co"));
            Assert.Contains("SearchTerm=%E9%92%A2", url.AbsoluteUri);
            Assert.DoesNotContain(" ", url.AbsoluteUri, "空格必须转义，否则请求直接失败");
            Assert.DoesNotContain("& Co", url.AbsoluteUri);
        });

        Test("URL：日志里只留路径，查询串一律抹掉", () =>
        {
            var redacted = EmbyHttp.Redact(new Uri("http://h:8096/emby/Users/AuthenticateByName?api_key=SECRET"));
            Assert.Equal("http://h:8096/emby/Users/AuthenticateByName", redacted);
            Assert.DoesNotContain("SECRET", redacted);
        });
    }

    private static void RegisterQuery()
    {
        Test("查询：分页与排序参数", () =>
        {
            var query = ItemQuery.Children("lib-1", startIndex: 100, limit: 50, EmbySortBy.DateAdded, descending: true).ToQueryString();

            Assert.Contains("ParentId=lib-1", query);
            Assert.Contains("StartIndex=100", query);
            Assert.Contains("Limit=50", query);
            Assert.Contains("SortBy=DateCreated%2CSortName", query, "复合键的逗号要转义");
            Assert.Contains("SortOrder=Descending", query);
        });

        Test("查询：搜索只找可播放的类型", () =>
        {
            var query = ItemQuery.Search("芙莉莲", 0, 40).ToQueryString();

            Assert.Contains("Recursive=true", query);
            Assert.Contains("IncludeItemTypes=Movie%2CSeries%2CEpisode", query);
            Assert.Contains("SearchTerm=", query);
            Assert.DoesNotContain("芙莉莲", query, "搜索词必须转义");
        });

        Test("查询：已看/未看过滤器", () =>
        {
            Assert.Contains("Filters=IsUnplayed", new ItemQuery { IsPlayed = false }.ToQueryString());
            Assert.Contains("Filters=IsPlayed", new ItemQuery { IsPlayed = true }.ToQueryString());
            Assert.DoesNotContain("Filters=", new ItemQuery().ToQueryString(), "不过滤时不该发这个参数");
        });

        Test("查询：媒体库根目录按自身类型递归展开", () =>
        {
            var movies = string.Join(',', ItemQuery.FlattenedTypes("movies"));
            Assert.Contains(EmbyItemType.Movie, movies);
            Assert.Contains(EmbyItemType.BoxSet, movies, "合集也摆在电影库里");
            // 没能刮到元数据的文件被 Emby 标成 Video，它们的名字还是原始文件名，
            // 正是最该出现在网格里的那些，不能漏掉
            Assert.Contains(EmbyItemType.Video, movies, "未识别的视频文件也要显示");

            Assert.Contains(EmbyItemType.Series, string.Join(',', ItemQuery.FlattenedTypes("tvshows")));
            Assert.Contains(EmbyItemType.MusicVideo, string.Join(',', ItemQuery.FlattenedTypes("musicvideos")));

            // 文件夹、合集、家庭视频照旧一层层翻，递归展开只对媒体库根目录成立
            Assert.Equal(0, ItemQuery.FlattenedTypes(null).Count, "普通文件夹不该被展开");
            Assert.Equal(0, ItemQuery.FlattenedTypes("boxsets").Count, "合集库不该被展开");
            Assert.Equal(0, ItemQuery.FlattenedTypes("homevideos").Count, "家庭视频不该被展开");
        });

        Test("图片：请求宽度按 80 像素一档，避免每个窗口宽度各存一份", () =>
        {
            // 宽度写进缓存文件名，不分档的话拖一次窗口就能给同一张海报存下几十份，
            // 每一份还都是一次新下载
            Assert.Equal(EmbyImageStore.RequestWidth(170), EmbyImageStore.RequestWidth(200), "相邻宽度要落进同一档");
            Assert.Equal(400, EmbyImageStore.RequestWidth(200), "两倍图，向上取整到 80 的倍数");

            // 卡片再小也要够清楚，再大也不必超过一整屏
            Assert.Equal(160, EmbyImageStore.RequestWidth(1));
            Assert.Equal(160, EmbyImageStore.RequestWidth(0), "宽度未知时不能算出 0");
            Assert.Equal(1280, EmbyImageStore.RequestWidth(4000));

            var widths = Enumerable.Range(1, 640).Select(EmbyImageStore.RequestWidth).Distinct().Count();
            Assert.True(widths <= 16, $"1–640 像素只应对应少数几档，实际 {widths} 档");
        });
    }

    /// <summary>
    /// 排序目录。这些值是从 Emby Theater 自带的 listcontroller.js 里抄下来的，不是猜的，
    /// 所以这里断言的重点是"没有抄错"：复合键的次序、每个键自己的默认方向、
    /// 以及哪些键在哪种列表里根本没有意义。
    /// </summary>
    private static void RegisterSort()
    {
        Test("排序：需求里的 16 个键一个不少", () =>
        {
            string[] required =
            [
                "公众评分", "加入日期", "发行年份", "发行日期", "媒体容器", "媒体码率",
                "家长分级", "影评指数", "播放次数", "文件名称", "文件大小", "时间长度",
                "最近播放", "最近更新", "标题名称", "源分辨率"
            ];

            foreach (var label in required)
                Assert.True(EmbySortBy.All.Any(option => option.Label == label), $"缺少排序键：{label}");

            foreach (var option in EmbySortBy.All)
            {
                Assert.True(option.Value.Length > 0, $"{option.Label} 没有 SortBy 值");
                Assert.True(EmbySortBy.Groups.Order.Contains(option.Group), $"{option.Label} 的分组不在菜单里");
            }

            var duplicated = EmbySortBy.All.GroupBy(option => option.Value).FirstOrDefault(group => group.Count() > 1);
            Assert.True(duplicated is null, $"有两个键用了同一个值：{duplicated?.Key}");
        });

        Test("排序：值和默认方向照 Emby 抄", () =>
        {
            // 一律带 SortName 收尾：光按 DateCreated 排，同一次扫描进来的条目顺序就是随机的
            Assert.Equal("DateCreated,SortName", Value("加入日期"));
            Assert.Equal("ProductionYear,PremiereDate,SortName", Value("发行日期"));
            Assert.Equal("DateLastContentAdded,SortName", Value("最近更新"));
            Assert.Equal("OfficialRating,SortName", Value("家长分级"));
            Assert.Equal("CommunityRating,SortName", Value("公众评分"));
            Assert.Equal("Resolution,SortName", Value("源分辨率"));

            // 用户列的是 VideoBitRate，Emby 实际排的是整个文件的码率
            Assert.Equal("TotalBitrate,SortName", Value("媒体码率"));

            // IsFolder 在前，子目录才会聚在一头，而不是按名字和文件混在一起
            Assert.Equal("IsFolder,Filename", Value("文件名称"));

            // 日期、评分、大小都是越大越有用；名字和时长反过来
            Assert.True(Option("加入日期").DescendingByDefault);
            Assert.True(Option("公众评分").DescendingByDefault);
            Assert.True(Option("文件大小").DescendingByDefault);
            Assert.False(Option("标题名称").DescendingByDefault);
            Assert.False(Option("时间长度").DescendingByDefault, "时长升序：先看短的那些");
            Assert.False(Option("顺序编号").DescendingByDefault);
        });

        Test("排序：剧集自己的播放日期是另一个键", () =>
        {
            var option = Option("最近播放");

            Assert.Equal("SeriesDatePlayed,SortName", option.ValueOf(EmbySortPreset.Series), "剧本身没有播放日期");
            Assert.Equal("DatePlayed,SortName", option.ValueOf(EmbySortPreset.Movie));
            Assert.Equal("DatePlayed,SortName", option.ValueOf(null));
        });

        Test("排序：无意义的键不出现在菜单里", () =>
        {
            var series = Labels(EmbySortPreset.Series);
            Assert.DoesNotContain("媒体码率", series, "剧集没有码率");
            Assert.DoesNotContain("源分辨率", series);
            Assert.DoesNotContain("文件大小", series);
            Assert.DoesNotContain("播放次数", series);
            Assert.Contains("最近更新", series, "剧集正需要这个键");

            var album = Labels(EmbySortPreset.Album);
            Assert.DoesNotContain("影评指数", album);
            Assert.DoesNotContain("媒体容器", album);

            var photo = Labels(EmbySortPreset.Photo);
            Assert.DoesNotContain("时间长度", photo);
            Assert.DoesNotContain("公众评分", photo);

            var movie = Labels(EmbySortPreset.Movie);
            Assert.DoesNotContain("顺序编号", movie, "电影没有集号");
            Assert.DoesNotContain("节目名称", movie);
            Assert.Contains("媒体码率", movie);
            Assert.Contains("文件名称", movie);

            // 单集是唯一按剧名排有意义的列表
            Assert.Contains("节目名称", Labels(EmbySortPreset.Episode));
            Assert.Contains("顺序编号", Labels(EmbySortPreset.Episode));
        });

        Test("排序：类型不明时不减菜单，只藏掉限定键", () =>
        {
            // 混合文件夹和搜索结果落在这里：不知道行是什么，就没有理由替用户砍键
            var unknown = Labels(null);

            Assert.Contains("媒体码率", unknown);
            Assert.Contains("时间长度", unknown);
            Assert.DoesNotContain("节目名称", unknown, "只对单集有意义的键不能漏出来");
        });

        Test("排序：源分辨率要服务器 4.7.3 以上", () =>
        {
            Assert.DoesNotContain("源分辨率", Labels(EmbySortPreset.Movie, new Version(4, 7, 2)));
            Assert.Contains("源分辨率", Labels(EmbySortPreset.Movie, new Version(4, 7, 3)));
            Assert.Contains("源分辨率", Labels(EmbySortPreset.Movie, new Version(4, 9, 1, 40)));

            // 版本还没探到的时候宁可多给一个键：服务器不认识顶多忽略，藏掉就是白藏
            Assert.Contains("源分辨率", Labels(EmbySortPreset.Movie), "版本未知时不该提前藏起来");
        });

        Test("排序：默认键按列表内容挑", () =>
        {
            Assert.Equal("标题名称", EmbySortBy.Default(EmbySortPreset.Movie).Label);
            Assert.Equal("标题名称", EmbySortBy.Default(null).Label);

            // 一季按字母排是唯一肯定错的顺序
            Assert.Equal("顺序编号", EmbySortBy.Default(EmbySortPreset.Episode).Label);
            Assert.Equal("顺序编号", EmbySortBy.Default(EmbySortPreset.Season).Label);
        });

        Test("排序：存下来的值还能认回标签", () =>
        {
            Assert.Equal("加入日期", EmbySortBy.LabelOf(EmbySortBy.DateAdded));
            Assert.Equal("标题名称", EmbySortBy.LabelOf(EmbySortBy.Name));
            Assert.True(EmbySortBy.Find("DateCreated") is null, "旧的单字段值不再是目录里的键");
            Assert.Equal("", EmbySortBy.LabelOf(null));
        });

        Test("排序：菜单按分组连着排，不会跳来跳回", () =>
        {
            // 菜单里靠分隔线分组，所以同一组必须是连续的一段
            var groups = EmbySortBy.All.Select(option => option.Group).ToList();
            var seen = new List<string>();

            foreach (var group in groups)
            {
                if (seen.Count > 0 && seen[^1] == group) continue;
                Assert.False(seen.Contains(group), $"分组 {group} 被拆成了两段");
                seen.Add(group);
            }

            Assert.Equal(string.Join(',', EmbySortBy.Groups.Order), string.Join(',', seen), "分组顺序要和菜单一致");
        });

        static EmbySortOption Option(string label) => EmbySortBy.All.First(option => option.Label == label);

        static string Value(string label) => Option(label).Value;

        static string Labels(string? preset, Version? version = null) =>
            string.Join(',', EmbySortBy.For(preset, version).Select(option => option.Label));
    }

    /// <summary>
    /// 筛选目录。这里的每个参数名、每个枚举值和每个分隔符都是从服务器自己的 OpenAPI 文档抄的
    /// （4.9.x 的 <c>/emby/openapi.json</c> 不需要令牌就能取），所以断言的重点和排序一样是"没有抄错"，
    /// 外加一件排序没有的事：Emby 对不认识的查询参数是直接忽略，抄错了不会报错，只会把整个库原样返回。
    /// </summary>
    private static void RegisterFilters()
    {
        // 工具栏底下那一行筛选条：那颗按钮上的数字说「筛了几条」，这一行说「筛的是哪几条」，而且每一格能被
        // 点掉。两句话必须对得上，所以这里逐条比的是「格数 = 那个数」和「点掉一格只掉一条」。
        Test("筛选条：一条筛选一格，顺序跟面板一样", () =>
        {
            var filters = new ItemFilters();
            filters.Toggles.Add("favorite");
            filters.Toggles.Add("unplayed");
            filters.Genres.Add("动画");
            filters.Years.Add("2019");
            filters.Tags.Add("剧场版");

            var chips = EmbyFilterBy.Chips(filters);

            // 开关按目录顺序（未播放在收藏前面），不是按点的顺序；三张列表按面板顺序：类型、标签、年份。
            Assert.Equal("未播放,收藏,类型：动画,标签：剧场版,年份：2019",
                string.Join(',', chips.Select(chip => chip.Label)));
            Assert.Equal(filters.Count, chips.Count);
        });

        Test("筛选条：点掉一格只掉那一条", () =>
        {
            var filters = new ItemFilters();
            filters.Toggles.Add("favorite");
            filters.Genres.Add("动画");
            filters.Genres.Add("科幻");

            var chips = EmbyFilterBy.Chips(filters);
            chips.First(chip => chip.Label == "类型：动画").Remove(filters);

            Assert.Equal("收藏,类型：科幻", string.Join(',', EmbyFilterBy.Chips(filters).Select(chip => chip.Label)));
            Assert.Equal(2, filters.Count);

            chips.First(chip => chip.Label == "收藏").Remove(filters);
            Assert.Equal("类型：科幻", string.Join(',', EmbyFilterBy.Chips(filters).Select(chip => chip.Label)));
        });

        Test("筛选条：认不出来的开关不出格，和那个数字一致", () =>
        {
            var filters = new ItemFilters();
            filters.Toggles.Add("这是以后的版本写进来的");
            filters.Toggles.Add("favorite");

            // 一格点不掉又说不出名字的筛选条比不出更糟 —— Count 也不数它，两处于是同一个答案。
            Assert.Equal(1, EmbyFilterBy.Chips(filters).Count);
            Assert.Equal(filters.Count, EmbyFilterBy.Chips(filters).Count);
            Assert.Equal(0, EmbyFilterBy.Chips(new ItemFilters()).Count);
        });

        Test("筛选：目录本身是自洽的", () =>
        {
            foreach (var option in EmbyFilterBy.All)
            {
                Assert.True(option.Id.Length > 0, "有个筛选项没有 id");
                Assert.True(option.Label.Length > 0, $"{option.Id} 没有标签");
                Assert.True(option.Key.Length > 0, $"{option.Label} 没有参数名");
                Assert.True(option.Value.Length > 0, $"{option.Label} 没有参数值");
                Assert.True(EmbyFilterBy.Groups.Order.Contains(option.Group), $"{option.Label} 的分组不在面板里");

                // 互斥项指向的必须是真的存在的 id，否则那条互斥永远不会生效
                foreach (var excluded in option.Excludes)
                    Assert.True(EmbyFilterBy.Find(excluded) is not null, $"{option.Label} 排斥了不存在的 {excluded}");
            }

            var duplicatedId = EmbyFilterBy.All.GroupBy(option => option.Id).FirstOrDefault(group => group.Count() > 1);
            Assert.True(duplicatedId is null, $"有两个筛选项用了同一个 id：{duplicatedId?.Key}");

            var duplicatedPair = EmbyFilterBy.All
                .GroupBy(option => $"{option.Key}={option.Value}")
                .FirstOrDefault(group => group.Count() > 1);
            Assert.True(duplicatedPair is null, $"有两个筛选项发同一个参数：{duplicatedPair?.Key}");
        });

        Test("筛选：分组连着排，顺序和面板一致", () =>
        {
            var seen = new List<string>();

            foreach (var group in EmbyFilterBy.All.Select(option => option.Group))
            {
                if (seen.Count > 0 && seen[^1] == group) continue;
                Assert.False(seen.Contains(group), $"分组 {group} 被拆成了两段");
                seen.Add(group);
            }

            Assert.Equal(string.Join(',', EmbyFilterBy.Groups.Order), string.Join(',', seen));
        });

        Test("筛选：分隔符按键分，抄错一个就是查不到东西", () =>
        {
            // 服务器文档原话：Genres / Tags / OfficialRatings / Studios 是 pipe delimeted，
            // Filters / Years / VideoTypes / SeriesStatus 是 comma delimeted。
            // 发错了不报错：Genres=动画,科幻 会被当成一个名叫「动画,科幻」的类型去找
            Assert.Equal("|", EmbyFilterBy.Delimiter("Genres"));
            Assert.Equal("|", EmbyFilterBy.Delimiter("Tags"));
            Assert.Equal("|", EmbyFilterBy.Delimiter("OfficialRatings"));
            Assert.Equal("|", EmbyFilterBy.Delimiter("Studios"));

            Assert.Equal(",", EmbyFilterBy.Delimiter("Filters"));
            Assert.Equal(",", EmbyFilterBy.Delimiter("Years"));
            Assert.Equal(",", EmbyFilterBy.Delimiter("VideoTypes"));
            Assert.Equal(",", EmbyFilterBy.Delimiter("SeriesStatus"));

            // 只收一个值的键必须答 null，不然两个值会被拼成 IsHD=true,false
            Assert.True(EmbyFilterBy.Delimiter("IsHD") is null);
            Assert.True(EmbyFilterBy.Delimiter("HasSubtitles") is null);
        });

        Test("筛选：同一个键的多个值合成一个参数", () =>
        {
            var filters = new ItemFilters();
            Assert.True(filters.Set(Option("unplayed"), true));
            Assert.True(filters.Set(Option("favorite"), true));

            var query = new ItemQuery { Filters = filters }.ToQueryString();

            // 一个 Filters= 而不是两个：同名参数出现两次，Emby 只读一个，另一个静静丢掉
            Assert.Equal(1, Occurrences(query, "Filters="), "Filters 必须只出现一次");
            Assert.Contains("Filters=IsUnplayed%2CIsFavorite", query, "逗号连接并转义");
        });

        Test("筛选：类型用竖线连接", () =>
        {
            var filters = new ItemFilters();
            filters.SetValue("Genres", "动画", true);
            filters.SetValue("Genres", "科幻", true);
            filters.SetValue("Years", "2019", true);
            filters.SetValue("Years", "2020", true);

            var query = new ItemQuery { Filters = filters }.ToQueryString();

            Assert.Equal(1, Occurrences(query, "Genres="));
            Assert.Contains("Genres=%E5%8A%A8%E7%94%BB%7C", query, "竖线是 %7C");
            Assert.Contains("Years=2019%2C2020", query, "年份是逗号");
            Assert.DoesNotContain("动画", query, "值必须转义");
        });

        Test("筛选：和 ItemQuery 自己那两个参数合并，不重复发", () =>
        {
            var filters = new ItemFilters();
            filters.Set(Option("favorite"), true);
            filters.SetValue("Genres", "科幻", true);

            // IsPlayed 也发 Filters=，Genre 也发 Genres=：旧客户端曾经用过这两个
            var query = new ItemQuery { IsPlayed = false, Genre = "动画", Filters = filters }.ToQueryString();

            Assert.Equal(1, Occurrences(query, "Filters="));
            Assert.Equal(1, Occurrences(query, "Genres="));
            Assert.Contains("Filters=IsUnplayed%2CIsFavorite", query);
            Assert.Contains("Genres=%E5%8A%A8%E7%94%BB%7C%E7%A7%91%E5%B9%BB", query, "先是 ItemQuery 自己的，再是面板的");
        });

        Test("筛选：什么都没选就不发筛选参数", () =>
        {
            var query = new ItemQuery { Filters = new ItemFilters() }.ToQueryString();

            Assert.DoesNotContain("Filters=", query);
            Assert.DoesNotContain("Genres=", query);
            Assert.DoesNotContain("IsHD=", query);
            Assert.True(new ItemFilters().IsEmpty);

            // 老的两个参数照旧
            Assert.Contains("Filters=IsPlayed", new ItemQuery { IsPlayed = true }.ToQueryString());
        });

        Test("筛选：互斥项会互相顶掉", () =>
        {
            var filters = new ItemFilters();
            filters.Set(Option("played"), true);
            filters.Set(Option("unplayed"), true);

            Assert.False(filters.Has("played"), "已播放和未播放同时开着，服务器答的是空");
            Assert.True(filters.Has("unplayed"));
            Assert.Equal(1, filters.Count);

            filters.Set(Option("hd"), true);
            filters.Set(Option("sd"), true);
            Assert.False(filters.Has("hd"));

            // 关掉一个不该带走别的
            Assert.True(filters.Set(Option("sd"), false));
            Assert.False(filters.Set(Option("sd"), false), "已经关了就不算改动");
            Assert.True(filters.Has("unplayed"));
        });

        Test("筛选：只收一个值的键，取第一个而不是拼起来", () =>
        {
            // 面板拦得住这种组合，但旧的 settings.json 里可能已经写着两条
            var filters = new ItemFilters { Toggles = ["hd", "sd"] };
            var query = new ItemQuery { Filters = filters }.ToQueryString();

            Assert.Equal(1, Occurrences(query, "IsHD="));
            Assert.Contains("IsHD=true", query, "目录里 hd 在 sd 前面，所以答的是 hd");
            Assert.DoesNotContain("IsHD=true%2Cfalse", query, "布尔参数不能拼成列表");
        });

        Test("筛选：不认识的 id 既不发也不算，还清得掉", () =>
        {
            var filters = new ItemFilters { Toggles = ["favorite", "someFutureFilter"] };

            Assert.Equal(1, filters.Count, "徽标上的数字不能算上发不出去的那条");
            Assert.False(filters.IsEmpty);
            Assert.False(filters.IsBlank);
            Assert.Contains("Filters=IsFavorite", new ItemQuery { Filters = filters }.ToQueryString());
            Assert.Equal(1, Occurrences(new ItemQuery { Filters = filters }.ToQueryString(), "Filters="));

            filters.Set(Option("favorite"), false);
            Assert.True(filters.IsEmpty, "剩下的那条什么都筛不掉");
            Assert.False(filters.IsBlank, "但它还在文件里，不能当没写过");

            Assert.True(filters.Clear(), "清除要把它一起带走，否则它永远躺在文件里");
            Assert.Equal(0, filters.Toggles.Count);
            Assert.True(filters.IsBlank);
            Assert.False(filters.Clear());
        });

        Test("筛选：答不了的选项不出现在面板里", () =>
        {
            // 电视剧库列的是剧，剧本身没有媒体流，问它有没有字幕答的是空
            var series = Labels(EmbySortPreset.Series);
            Assert.DoesNotContain("有字幕", series);
            Assert.DoesNotContain("高清", series);
            Assert.DoesNotContain("蓝光原盘", series);
            Assert.DoesNotContain("可继续播放", series, "剧自己没有断点");
            Assert.Contains("连载中", series, "剧集状态只在这里有意义");
            Assert.Contains("收藏", series);

            // 一季里列的是单集，这时候上面那些才答得出来
            var episode = Labels(EmbySortPreset.Episode);
            Assert.Contains("有字幕", episode);
            Assert.Contains("可继续播放", episode);
            Assert.Contains("未播出", episode);
            Assert.DoesNotContain("连载中", episode);
            Assert.DoesNotContain("有预告片", episode, "预告片挂在剧或电影上");

            var movie = Labels(EmbySortPreset.Movie);
            Assert.Contains("有字幕", movie);
            Assert.Contains("蓝光原盘", movie);
            Assert.Contains("3D", movie);
            Assert.DoesNotContain("连载中", movie);
            Assert.DoesNotContain("未播出", movie);

            var photo = Labels(EmbySortPreset.Photo);
            Assert.DoesNotContain("高清", photo);
            Assert.DoesNotContain("有字幕", photo);
            Assert.Contains("收藏", photo, "照片也能收藏");
        });

        Test("筛选：类型不明时不减面板，只藏掉限定键", () =>
        {
            var unknown = Labels(null);

            Assert.Contains("有字幕", unknown);
            Assert.Contains("高清", unknown);
            Assert.DoesNotContain("连载中", unknown, "只对剧有意义的键不能漏出来");
            Assert.DoesNotContain("未播出", unknown);

            // 版本还没探到就不该提前藏东西
            Assert.Equal(
                Labels(EmbySortPreset.Movie),
                Labels(EmbySortPreset.Movie, new Version(4, 9, 5)),
                "4.9 上没有任何选项要藏");
        });

        Test("筛选：动态分组各有各的清单", () =>
        {
            var filters = new ItemFilters();

            foreach (var list in EmbyFilterBy.Lists)
            {
                Assert.True(list.Label.Length > 0);
                filters.SetValue(list.Key, "x", true);
                Assert.Equal(1, filters.Values(list.Key).Count, $"{list.Label} 存到了别的清单里");
            }

            Assert.Equal(3, filters.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => filters.Values("OfficialRatings"));

            // 端点名就是键名，一个方法取三份清单靠的正是这个
            Assert.Equal("Genres,Tags,Years", string.Join(',', EmbyFilterBy.Lists.Select(list => list.Key)));
        });

        Test("筛选：副本是断开的", () =>
        {
            var filters = new ItemFilters();
            filters.Set(Option("favorite"), true);
            filters.SetValue("Genres", "动画", true);

            var copy = filters.Clone();
            copy.Set(Option("hd"), true);
            copy.SetValue("Genres", "科幻", true);

            Assert.Equal(2, filters.Count, "改副本不该动到原来那份");
            Assert.Equal(4, copy.Count);
        });

        Test("筛选：说人话的摘要", () =>
        {
            var filters = new ItemFilters();
            filters.Set(Option("favorite"), true);
            filters.Set(Option("unplayed"), true);
            filters.SetValue("Genres", "动画", true);
            filters.SetValue("Genres", "科幻", true);

            // 勾选次序不影响摘要：按目录顺序念，同一份选择每次都念成同一句
            Assert.Equal("未播放、收藏  ·  类型：动画、科幻", filters.Describe());
            Assert.Equal("", new ItemFilters().Describe());
        });

        static EmbyFilterOption Option(string id) => EmbyFilterBy.All.First(option => option.Id == id);

        static string Labels(string? preset, Version? version = null) =>
            string.Join(',', EmbyFilterBy.For(preset, version).Select(option => option.Label));

        static int Occurrences(string text, string needle)
        {
            var count = 0;
            for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
                count++;
            return count;
        }
    }

    private static void RegisterIdentity()
    {
        Test("设备标识：授权头里的引号与逗号被清掉", () =>
        {
            var device = new DeviceIdentity(DeviceIdentity.ClientName, """我的"电脑",""", "dev-1", "2.0.0");
            var header = device.ToAuthorizationHeader();

            Assert.Equal("MediaBrowser Client=\"EmbyNian\", Device=\"我的电脑\", DeviceId=\"dev-1\", Version=\"2.0.0\"", header);
        });

        Test("设备标识：空白名字回退成 unknown", () =>
        {
            Assert.Equal("unknown", DeviceIdentity.Sanitize("   "));
            Assert.Equal("unknown", DeviceIdentity.Sanitize("\"\""), "只剩引号也算空");
        });
    }

    private static void RegisterTimeFormat()
    {
        Test("时间：刻与秒互转", () =>
        {
            Assert.Equal(600, TimeFormat.ToSeconds(6_000_000_000));
            Assert.Equal(6_000_000_000, TimeFormat.ToTicks(600));
            Assert.Equal(0, TimeFormat.ToSeconds(null), "没有位置就是 0");
            Assert.Equal(0, TimeFormat.ToSeconds(-5), "负数不该往前跳");
            Assert.Equal(0, TimeFormat.ToTicks(-1));
        });

        Test("时间：进度条旁边的时钟", () =>
        {
            Assert.Equal("0:05", TimeFormat.Clock(TimeSpan.FromSeconds(5)));
            Assert.Equal("23:45", TimeFormat.Clock(TimeSpan.FromSeconds(1425)));
            Assert.Equal("1:23:45", TimeFormat.Clock(TimeSpan.FromSeconds(5025)));
            Assert.Equal("0:00", TimeFormat.Clock(TimeSpan.FromSeconds(-3)), "负数按 0 显示");
        });

        Test("时间：标题旁边的时长", () =>
        {
            Assert.Equal("2 小时 15 分", TimeFormat.Duration(TimeSpan.FromMinutes(135).Ticks));
            Assert.Equal("2 小时", TimeFormat.Duration(TimeSpan.FromHours(2).Ticks));
            Assert.Equal("45 分钟", TimeFormat.Duration(TimeSpan.FromMinutes(45).Ticks));
            Assert.Equal("", TimeFormat.Duration(null), "不知道时长就什么都不显示");
        });

        Test("文件大小：按 1024 进位", () =>
        {
            Assert.Equal("", TimeFormat.FileSize(null));
            Assert.Equal("512 B", TimeFormat.FileSize(512));
            Assert.Equal("8.4 GB", TimeFormat.FileSize((long)(8.4 * 1024 * 1024 * 1024)));
        });
    }

    private static void RegisterItemModel()
    {
        Test("条目：单集标题带上剧名与集号", () =>
        {
            var episode = new EmbyItem
            {
                Name = "旅途的终点",
                Type = EmbyItemType.Episode,
                SeriesName = "葬送的芙莉莲",
                ParentIndexNumber = 1,
                IndexNumber = 2
            };

            Assert.Equal("S01E02", episode.EpisodeCode);
            Assert.Equal("葬送的芙莉莲 S01E02 旅途的终点", episode.ToPlaybackTitle());
        });

        Test("条目：电影标题就是片名", () =>
        {
            var movie = new EmbyItem { Name = "你的名字", Type = EmbyItemType.Movie, ProductionYear = 2016 };
            Assert.Equal("你的名字", movie.ToPlaybackTitle());
            Assert.Equal("2016", movie.CardSubtitle);
        });

        Test("条目：续播判定要排除刚开头和快结束", () =>
        {
            var item = new EmbyItem { RunTimeTicks = 72_000_000_000 };

            item.UserData = new EmbyUserData { PlaybackPositionTicks = 36_000_000_000 };
            Assert.True(item.HasResumePosition, "看到一半应提示续播");

            item.UserData = new EmbyUserData { PlaybackPositionTicks = 0 };
            Assert.False(item.HasResumePosition, "没看过不该提示续播");

            item.UserData = new EmbyUserData { PlaybackPositionTicks = 71_900_000_000 };
            Assert.False(item.HasResumePosition, "只剩几秒时提示续播毫无意义");
        });

        Test("条目：进度优先用服务器给的百分比", () =>
        {
            var item = new EmbyItem
            {
                RunTimeTicks = 72_000_000_000,
                UserData = new EmbyUserData { PlaybackPositionTicks = 1, PlayedPercentage = 42 }
            };

            Assert.Equal(0.42, item.ProgressFraction);
        });

        Test("条目：媒体库和演职人员没有已观看/收藏这回事", () =>
        {
            // 「主页的媒体库不要收藏和已观看」：卡片上那两个按钮按这个走。
            Assert.False(new EmbyItem { Type = EmbyItemType.CollectionFolder }.TracksUserState, "媒体库是架子不是条目");
            Assert.False(new EmbyItem { Type = EmbyItemType.Person }.TracksUserState, "人名标不了已观看");

            Assert.True(new EmbyItem { Type = EmbyItemType.Movie }.TracksUserState);
            Assert.True(new EmbyItem { Type = EmbyItemType.Series }.TracksUserState);
            Assert.True(new EmbyItem { Type = EmbyItemType.Folder }.TracksUserState, "普通文件夹会汇总下面的观看状态");
            Assert.True(new EmbyItem { Type = EmbyItemType.BoxSet }.TracksUserState);

            // 服务器给了个没见过的类型，也不能替它把按钮藏掉。
            Assert.True(new EmbyItem { Type = "MusicAlbum" }.TracksUserState);
            Assert.True(new EmbyItem().TracksUserState, "类型缺失时按能标记算");

            // 大小写照 Emby 的容忍度来，和 IsPlayable 一致。
            Assert.False(EmbyItemType.TracksUserState("collectionfolder"));
        });

        Test("条目：音乐那几类一律屏蔽", () =>
        {
            // 需求 3「屏蔽媒体库里音乐的内容」：这个应用没有音乐界面 —— 没有专辑墙、没有曲目表、没有播放队列
            // —— 所以这几类连卡片都不摆，而不是摆出来点了没反应。
            Assert.True(EmbyItemType.IsMusic(EmbyItemType.Audio));
            Assert.True(EmbyItemType.IsMusic(EmbyItemType.MusicAlbum));
            Assert.True(EmbyItemType.IsMusic(EmbyItemType.MusicArtist));
            Assert.True(EmbyItemType.IsMusic(EmbyItemType.MusicVideo), "音乐视频放得出来，但它算音乐");

            Assert.False(EmbyItemType.IsMusic(EmbyItemType.Movie));
            Assert.False(EmbyItemType.IsMusic(EmbyItemType.Episode));
            Assert.False(EmbyItemType.IsMusic(null), "类型缺失的条目不当音乐屏蔽");

            // 大小写照 Emby 的容忍度来，和 IsPlayable 一致。
            Assert.True(EmbyItemType.IsMusic("musicalbum"));
        });

        Test("条目：音乐库整座挡在导航栏外", () =>
        {
            // 同一条需求的另一半：按 CollectionType 把整座库挡住，而不是等它的内容一条条被过滤 —— 一座过滤干净
            // 的音乐库就是导航栏里一个点开永远空着的条目。
            Assert.True(EmbyItemType.IsMusicLibrary("music"));
            Assert.True(EmbyItemType.IsMusicLibrary("MusicVideos"), "大小写照 Emby 的容忍度来");

            Assert.False(EmbyItemType.IsMusicLibrary("movies"));
            Assert.False(EmbyItemType.IsMusicLibrary("tvshows"));
            Assert.False(EmbyItemType.IsMusicLibrary(null), "没说自己是什么库的照常显示");
        });

        Test("片源：质量标签", () =>
        {
            var source = new MediaSource
            {
                Container = "mkv",
                Size = 9_006_744_000,
                MediaStreams = [new MediaStream { Index = 0, Type = "Video", Codec = "hevc", Height = 2160 }]
            };

            var label = source.ToQualityLabel();
            Assert.Contains("4K", label);
            Assert.Contains("HEVC", label);
            Assert.Contains("MKV", label);
            Assert.Contains("GB", label);
        });

        Test("轨道标签：服务器没给描述时自己拼一个", () =>
        {
            var stream = new MediaStream
            {
                Index = 2,
                Type = "Audio",
                DisplayLanguage = "日语",
                Codec = "eac3",
                ChannelLayout = "5.1"
            };

            var label = stream.ToDisplayLabel();
            Assert.Contains("日语", label);
            Assert.Contains("EAC3", label);
            Assert.Contains("5.1", label);

            var forcedExternal = new MediaStream { Index = 3, Type = "Subtitle", DisplayTitle = "中文", IsForced = true, IsExternal = true };
            Assert.Equal("中文（强制）（外挂）", forcedExternal.ToDisplayLabel());
        });
    }

    // ---- 编辑元数据 -------------------------------------------------------------

    /// <summary>
    /// One item as the server sends it. Deliberately more than the form can edit: <c>People</c> and
    /// <c>Studios</c> are here precisely so a test can prove they survive a save, which is the whole
    /// reason the edit is applied to the server's own JSON rather than to a rebuilt object.
    /// </summary>
    private static JsonObject Movie() => JsonNode.Parse("""
        {
          "Name": "你的名字",
          "OriginalTitle": "君の名は。",
          "Id": "1f4a",
          "Overview": "两个陌生人交换了身体。",
          "OfficialRating": "PG",
          "CommunityRating": 8.4,
          "CriticRating": 79,
          "ProductionYear": 2016,
          "PremiereDate": "2016-08-26T16:00:00.0000000Z",
          "Genres": ["动画", "爱情"],
          "Tags": ["新海诚"],
          "Studios": [{ "Name": "CoMix Wave Films", "Id": "9" }],
          "People": [{ "Name": "神木隆之介", "Type": "Actor" }],
          "RunTimeTicks": 63960000000
        }
        """)!.AsObject();

    private static void RegisterMetadataEdit()
    {
        Test("编辑元数据：读出表单的初始值", () =>
        {
            var form = ItemMetadataEdit.Read(Movie());

            Assert.Equal("你的名字", form.Name);
            Assert.Equal("君の名は。", form.OriginalTitle);
            Assert.Equal("", form.SortName, "没设过排序名称就该是空的，而不是算出来的那个");
            Assert.Equal("8.4", form.CommunityRating);
            Assert.Equal("79", form.CriticRating);
            Assert.Equal("2016", form.ProductionYear);
            Assert.Equal("2016-08-26", form.PremiereDate);
            Assert.Equal("动画、爱情", form.Genres);
            Assert.Equal("新海诚", form.Tags);
            Assert.Null(form.Problem);
        });

        Test("编辑元数据：只看不改就没有要发的东西", () =>
        {
            // 打开对话框、看一眼、保存。表单里每个数字都是从服务器的 JSON 里原样读出来的字符串，
            // 所以 8.4 不能变成 8.40，日期不能把服务器的时间部分抹掉——否则每次保存都算改过。
            var item = Movie();
            Assert.False(ItemMetadataEdit.Read(item).ApplyTo(item), "没动过的表单不该报告改动");
            Assert.Equal("2016-08-26T16:00:00.0000000Z", item["PremiereDate"]!.ToString(), "日期的时间部分要留着");
        });

        Test("编辑元数据：表单没碰过的字段要原样送回去", () =>
        {
            var item = Movie();
            var form = ItemMetadataEdit.Read(item) with { Name = "你的名字（重制）" };

            Assert.True(form.ApplyTo(item));
            Assert.Equal("你的名字（重制）", item["Name"]!.ToString());

            // Emby 的更新接口拿整个条目当权威，路上丢掉的字段就是用户刚刚抹掉的字段。
            Assert.Equal("神木隆之介", item["People"]![0]!["Name"]!.ToString(), "演员表得活着");
            Assert.Equal("CoMix Wave Films", item["Studios"]![0]!["Name"]!.ToString(), "制作公司得活着");
            Assert.Equal("1f4a", item["Id"]!.ToString());
        });

        Test("编辑元数据：清空的框写 null 而不是空串", () =>
        {
            // 对 Emby 来说 null 是「没有值」，"" 是「值恰好是空的」——一个空的排序名称
            // 会把条目排到整台服务器的最前面。
            var item = Movie();
            var form = ItemMetadataEdit.Read(item) with { OriginalTitle = "  ", Overview = "" };

            Assert.True(form.ApplyTo(item));
            Assert.True(item["OriginalTitle"] is null, "原始标题该是 JSON null");
            Assert.True(item["Overview"] is null, "简介该是 JSON null");

            // 再来一次不该又算改动：缺字段和 null 字段对绑定器是一回事。
            Assert.False(ItemMetadataEdit.Read(item).ApplyTo(item), "已经清空过的字段不该再报告改动");
        });

        Test("编辑元数据：排序名称写进 ForcedSortName", () =>
        {
            var item = Movie();
            Assert.True((ItemMetadataEdit.Read(item) with { SortName = "君の名は" }).ApplyTo(item));

            Assert.Equal("君の名は", item["ForcedSortName"]!.ToString());
            Assert.True(item["SortName"] is null, "算出来的 SortName 不是我们该写的那个");
        });

        Test("编辑元数据：日期只在天数真的动了以后才重写", () =>
        {
            var item = Movie();

            // 同一天，只是没有时间部分：服务器的 16:00 有人是故意设的。
            Assert.False((ItemMetadataEdit.Read(item) with { PremiereDate = "2016/8/26" }).ApplyTo(item), "同一天不该重写");
            Assert.Equal("2016-08-26T16:00:00.0000000Z", item["PremiereDate"]!.ToString());

            Assert.True((ItemMetadataEdit.Read(item) with { PremiereDate = "2016年12月2日" }).ApplyTo(item));
            Assert.Contains("2016-12-02", item["PremiereDate"]!.ToString());
        });

        Test("编辑元数据：列表按各种分隔符拆开并去重", () =>
        {
            var item = Movie();
            var form = ItemMetadataEdit.Read(item) with { Genres = "动画,爱情；剧情、动画 , 奇幻" };

            Assert.True(form.ApplyTo(item));
            Assert.Equal("动画、爱情、剧情、奇幻", ItemMetadataEdit.Read(item).Genres, "重复的类型不该是编辑器能造出来的东西");
            Assert.Equal(4, item["Genres"]!.AsArray().Count);
        });

        Test("编辑元数据：填错了要说清楚哪里错，并且拒绝保存", () =>
        {
            var form = ItemMetadataEdit.Read(Movie());

            Assert.Equal("名称不能为空", (form with { Name = "  " }).Problem);
            Assert.Equal("公众评分要填 0 到 10 之间的数字", (form with { CommunityRating = "12" }).Problem);
            Assert.Equal("影评指数要填 0 到 100 之间的数字", (form with { CriticRating = "很好" }).Problem);
            Assert.Equal("发行年份要填 1800 到 2200 之间的整数", (form with { ProductionYear = "20166" }).Problem);
            Assert.Equal("集号要填非负整数", (form with { IndexNumber = "-1" }).Problem);
            Assert.Equal("发行日期要填成 2024-05-01 这样的年-月-日", (form with { PremiereDate = "去年夏天" }).Problem);

            // 清空是合法的，只有填错才不是。
            Assert.Null((form with { CommunityRating = "", ProductionYear = "", PremiereDate = "" }).Problem);

            var item = Movie();
            Assert.Throws<InvalidOperationException>(() => (form with { Name = "" }).ApplyTo(item));
            Assert.Equal("你的名字", item["Name"]!.ToString(), "被拒的保存不该已经改了一半");
        });

        Test("编辑元数据：清掉评分和年份", () =>
        {
            var item = Movie();
            var form = ItemMetadataEdit.Read(item) with { CommunityRating = "", CriticRating = " ", ProductionYear = "" };

            Assert.True(form.ApplyTo(item));
            Assert.True(item["CommunityRating"] is null);
            Assert.True(item["CriticRating"] is null);
            Assert.True(item["ProductionYear"] is null);
        });

        Test("编辑元数据：单集能改季号和集号", () =>
        {
            var item = JsonNode.Parse("""
                { "Name": "旅途的终点", "ParentIndexNumber": 1, "IndexNumber": 2 }
                """)!.AsObject();

            var form = ItemMetadataEdit.Read(item);
            Assert.Equal("1", form.ParentIndexNumber);
            Assert.Equal("2", form.IndexNumber);

            Assert.True((form with { ParentIndexNumber = "2", IndexNumber = "13" }).ApplyTo(item));
            Assert.Equal(2, item["ParentIndexNumber"]!.GetValue<int>());
            Assert.Equal(13, item["IndexNumber"]!.GetValue<int>());
        });
    }

    /// <summary>
    /// The poster cache's size and its wipe. Both are static and take a directory precisely so they can
    /// be tested: the interesting part is not the arithmetic but which files are counted and which are
    /// deleted, and a button labelled 「清除缓存」 deserves proof that it only clears the cache.
    /// </summary>
    private static void RegisterImageCache()
    {
        Test("图片缓存：统计文件数与总大小", () =>
        {
            var root = TempDirectory();
            try
            {
                File.WriteAllBytes(Path.Combine(root, "a-Primary-tag-160.img"), new byte[1000]);
                File.WriteAllBytes(Path.Combine(root, "b-Thumb-tag-320.img"), new byte[24]);

                var usage = EmbyImageStore.Measure(root);
                Assert.Equal(2, usage.Files);
                Assert.Equal(1024L, usage.Bytes);
            }
            finally
            {
                Cleanup(root);
            }
        });

        Test("图片缓存：清除只删图片文件", () =>
        {
            var root = TempDirectory();
            try
            {
                File.WriteAllBytes(Path.Combine(root, "a-Primary-tag-160.img"), new byte[8]);
                File.WriteAllBytes(Path.Combine(root, "b-Backdrop-tag-640.img"), new byte[8]);
                File.WriteAllText(Path.Combine(root, "settings.json"), "{}");

                Assert.Equal(2, EmbyImageStore.Clear(root));
                Assert.Equal(0, EmbyImageStore.Measure(root).Files);
                Assert.True(File.Exists(Path.Combine(root, "settings.json")), "只应删除 .img");
                Assert.True(Directory.Exists(root), "目录本身要留下");
            }
            finally
            {
                Cleanup(root);
            }
        });

        Test("图片缓存：目录不存在时算作空，清除也不报错", () =>
        {
            var missing = Path.Combine(Path.GetTempPath(), $"embynian-absent-{Guid.NewGuid():N}");

            var usage = EmbyImageStore.Measure(missing);
            Assert.Equal(0, usage.Files);
            Assert.Equal(0L, usage.Bytes);
            Assert.Equal(0, EmbyImageStore.Clear(missing));
        });

        Test("图片缓存：请求宽度按步长收敛，避免每个窗口宽度各存一份", () =>
        {
            // The step is what keeps one poster from being cached at 27 widths; these are the two ends
            // of the clamp and one width in the middle that must round up to the same step as its
            // neighbours.
            Assert.Equal(160, EmbyImageStore.RequestWidth(1));
            Assert.Equal(1280, EmbyImageStore.RequestWidth(4000));
            Assert.Equal(EmbyImageStore.RequestWidth(171), EmbyImageStore.RequestWidth(170));
        });

        RegisterImageCachePolicy();
    }

    /// <summary>
    /// 淘汰规则：**最久没看过的先走**，以及「什么时候值得去动一次时间戳」。
    /// <para>
    /// 从前这两条都是错的，而两种错法屏上都看不见：按「最早下载」删，于是天天看的那部剧的海报因为下得早会先被删掉
    /// —— 用户看见的只是「怎么又在转」；而清理只在开机跑一次，一次会话里缓存可以一路涨过预算，谁也不会注意。
    /// </para>
    /// </summary>
    private static void RegisterImageCachePolicy()
    {
        RegisterScoreSource();

        var now = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

        Test("缓存上限：装机那个数还是从前写死的 400 MB", () =>
        {
            // 上限从写死在构造函数上变成了设置里的一行。默认值必须还是从前那个数，否则「从没动过这个设置的人」
            // 会在升级之后发现缓存行为变了 —— 而他什么都没改。
            Assert.Equal(400, ImageCachePolicy.DefaultMegabytes);
            Assert.Equal(400L * 1024 * 1024, ImageCachePolicy.BudgetBytes(ImageCachePolicy.DefaultMegabytes));
        });

        Test("缓存上限：手改过的数拨回范围内，0 当「按装机默认」", () =>
        {
            // 0 是「设置文件里没有这一键」——从旧版本升上来就是这一档，必须读成默认值而不是「一张都不缓存」。
            Assert.Equal(ImageCachePolicy.DefaultMegabytes, ImageCachePolicy.ClampMegabytes(0));
            Assert.Equal(ImageCachePolicy.MinMegabytes, ImageCachePolicy.ClampMegabytes(1));
            Assert.Equal(ImageCachePolicy.MinMegabytes, ImageCachePolicy.ClampMegabytes(-4000));
            Assert.Equal(ImageCachePolicy.MaxMegabytes, ImageCachePolicy.ClampMegabytes(999999));
            Assert.Equal(1500, ImageCachePolicy.ClampMegabytes(1500), "范围内的数一个字不动");
        });

        Test("缓存上限：范围两头说得通，而且换算过去不溢出", () =>
        {
            Assert.True(ImageCachePolicy.MinMegabytes < ImageCachePolicy.DefaultMegabytes);
            Assert.True(ImageCachePolicy.DefaultMegabytes < ImageCachePolicy.MaxMegabytes);

            // 一屏媒体库的海报本身就有十几兆，下限再低缓存就开始来回打转。
            Assert.True(ImageCachePolicy.MinMegabytes >= 100);

            Assert.Equal((long)ImageCachePolicy.MaxMegabytes * 1024 * 1024,
                ImageCachePolicy.BudgetBytes(ImageCachePolicy.MaxMegabytes));
            Assert.True(ImageCachePolicy.BudgetBytes(ImageCachePolicy.MaxMegabytes) > 0, "换成字节之后不许溢出");
        });

        Test("缓存上限：换个上限之后 Evict 按新的那个数删", () =>
        {
            // 这一条钉的是「调小当场生效」那半件事的算术那一头：同一批文件、同一次调用，只有预算变了。
            (string, long, DateTime)[] files =
            [
                ("a.img", 100L * 1024 * 1024, now.AddDays(-4)),
                ("b.img", 100L * 1024 * 1024, now.AddDays(-3)),
                ("c.img", 100L * 1024 * 1024, now.AddDays(-2))
            ];

            var total = 300L * 1024 * 1024;

            Assert.Equal(0, ImageCachePolicy.Evict(files, total, ImageCachePolicy.BudgetBytes(400)).Count,
                "400 MB 的预算装得下 300 MB");

            var trimmed = ImageCachePolicy.Evict(files, total, ImageCachePolicy.BudgetBytes(200));
            Assert.Equal(2, trimmed.Count, "200 MB 要削到八成也就是 160 MB，三张里得走两张");
            Assert.Equal("a.img", trimmed[0].Path, "最久没看过的先走");
        });

        Test("缓存淘汰：没超预算一张都不删", () =>
        {
            (string, long, DateTime)[] files =
            [
                ("a.img", 400, now.AddDays(-30)),
                ("b.img", 400, now)
            ];

            Assert.Equal(0, ImageCachePolicy.Evict(files, 800, 1000).Count);
            Assert.Equal(0, ImageCachePolicy.Evict(files, 1000, 1000).Count, "正好等于预算也不算超");
        });

        Test("缓存淘汰：最久没看过的先走，不是最早下载的", () =>
        {
            // 这就是那个毛病的形状：old 是上个月下的、每天都在看（时间戳被推到今天），fresh 是今天下的、看过一次
            // 就没再碰。按「最早下载」删会先删 old —— 而它正是最该留的那一张。
            (string, long, DateTime)[] files =
            [
                ("每天看的.img", 500, now.AddMinutes(-5)),
                ("下过一次就没碰的.img", 500, now.AddDays(-40)),
                ("上周看过的.img", 500, now.AddDays(-7))
            ];

            // 1500 → 预算 1000 → 削到 800，也就是要腾出 700，两张 500 就够。
            var doomed = ImageCachePolicy.Evict(files, 1500, 1000);

            Assert.Equal(2, doomed.Count);
            Assert.Equal("下过一次就没碰的.img", doomed[0].Path, "最久没看过的第一个走");
            Assert.Equal("上周看过的.img", doomed[1].Path);
        });

        Test("缓存淘汰：删到降回预算的八成就停手", () =>
        {
            var files = Enumerable.Range(0, 10)
                .Select(index => ($"{index}.img", 100L, now.AddDays(-index)))
                .ToList();

            // 1000 → 预算 500 → 削到 400，要腾出 600，也就是 6 张。一超就只削到预算上的话，下一张图落地又超了，
            // 于是每下载几张就走一趟目录枚举。
            var doomed = ImageCachePolicy.Evict(files, 1000, 500);

            Assert.Equal(6, doomed.Count);
            Assert.Equal("9.img", doomed[0].Path, "最旧的（9 天前）第一个走");
        });

        Test("缓存淘汰：预算是 0 就全删", () =>
        {
            (string, long, DateTime)[] files = [("a.img", 100, now), ("b.img", 100, now.AddDays(-1))];

            Assert.Equal(2, ImageCachePolicy.Evict(files, 200, 0).Count);
        });

        Test("缓存淘汰：时间戳一样时次序是稳定的", () =>
        {
            // 一屏卡片就是同一秒落地的一批。次序不稳的话「同一份缓存、同一次清理删的是同一批」这句话就钉不住。
            (string, long, DateTime)[] files =
            [
                ("c.img", 100, now),
                ("a.img", 100, now),
                ("b.img", 100, now)
            ];

            var first = ImageCachePolicy.Evict(files, 300, 200);
            var again = ImageCachePolicy.Evict(files, 300, 200);

            Assert.Equal("a.img", first[0].Path);
            Assert.True(first.Select(file => file.Path).SequenceEqual(again.Select(file => file.Path)));
        });

        Test("缓存淘汰：空缓存不炸", () =>
            Assert.Equal(0, ImageCachePolicy.Evict([], 0, 1000).Count));

        Test("推时间戳：刚看过的不重复写，久没看的才推", () =>
        {
            Assert.False(ImageCachePolicy.WorthTouching(now.AddMinutes(-1), now), "一分钟前刚推过");
            Assert.False(ImageCachePolicy.WorthTouching(now, now));
            Assert.True(ImageCachePolicy.WorthTouching(now - ImageCachePolicy.TouchInterval, now), "正好到期就推");
            Assert.True(ImageCachePolicy.WorthTouching(now.AddDays(-3), now));
        });

        Test("推时间戳：时间戳在未来的也要拉回来", () =>
        {
            // 手改过系统时钟、或者从别的机器上拷过来的缓存。留着一个未来的时间戳就意味着这一张永远排在最后、
            // 永远轮不到被淘汰。
            Assert.True(ImageCachePolicy.WorthTouching(now.AddYears(1), now));
        });

        // 上面几条钉的是次序，这一条钉的是它接到真磁盘上还成立 —— glob、排序和删除三件事一起走通，而
        // 「清除缓存」那个按钮的教训就是：算得对不等于删对了东西。
        Test("图片缓存：清理真的按最久没看过删，并且只删 .img", () =>
        {
            var root = TempDirectory();
            try
            {
                var stamp = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

                // 十张各 100 字节，第 index 张是 index 天前看的。
                for (var index = 0; index < 10; index++)
                {
                    var path = Path.Combine(root, $"item{index}-Primary-tag-400.img");
                    File.WriteAllBytes(path, new byte[100]);
                    File.SetLastWriteTimeUtc(path, stamp.AddDays(-index));
                }

                File.WriteAllText(Path.Combine(root, "settings.json"), "{}");

                // 1000 字节，预算 500 → 削到 400，也就是要删掉 6 张：9 天前那张起，一路删到 4 天前那张。
                var store = new EmbyImageStore(Session(), root, maxBytes: 500);
                store.PruneInBackground();

                Assert.True(
                    Spin(() => EmbyImageStore.Measure(root).Files == 4),
                    $"清理没把缓存降到 4 张（现在 {EmbyImageStore.Measure(root).Files} 张）");

                for (var index = 0; index <= 3; index++)
                    Assert.True(
                        File.Exists(Path.Combine(root, $"item{index}-Primary-tag-400.img")),
                        $"最近看过的第 {index} 张不该被删");

                for (var index = 4; index < 10; index++)
                    Assert.False(
                        File.Exists(Path.Combine(root, $"item{index}-Primary-tag-400.img")),
                        $"{index} 天没看过的第 {index} 张该走了");

                Assert.True(File.Exists(Path.Combine(root, "settings.json")), "清理也只碰 .img");
            }
            finally
            {
                Cleanup(root);
            }
        });
    }

    /// <summary>
    /// 一个没登录的会话，只为把 <see cref="EmbyImageStore"/> 构造出来 —— 清理那一路一次都不碰它。什么都不落盘：
    /// <c>AppPaths</c> 只拼字符串，那个临时目录名从来不会被建出来。
    /// </summary>
    private static EmbySession Session() => new(
        new Configuration.AppSettings(),
        new Configuration.SettingsStore(
            new AppPaths(Path.Combine(Path.GetTempPath(), $"embynian-prune-{Guid.NewGuid():N}")),
            Configuration.PassthroughSecretProtector.Instance),
        new Configuration.CredentialVault(Configuration.PassthroughSecretProtector.Instance),
        DeviceIdentity.Create("device-1", "3.0.0"));

    /// <summary>
    /// 等一件后台的事发生，最多五秒。清理是 fire-and-forget 的（开机时它不该拦住任何东西），所以这里只能等 ——
    /// 而等不到就是真的没做，不是「还没轮到」。
    /// </summary>
    private static bool Spin(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (done()) return true;
            Thread.Sleep(25);
        }

        return done();
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"embynian-images-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- 评分来源 --------------------------------------------------------------
    //
    // 「加入显示评分改为豆瓣评分的功能，可在设置使用豆瓣、tmdb、烂番茄等平台的评分」。这一族钉的全是屏上看着挺
    // 正常的坏法：0 分被当成有分、烂番茄的 92 被当成十分制、小数点跟着机器的文化走、以及标签说了一句服务器根本
    // 没答应的话（把一个来路不明的分说成豆瓣的）。

    private static void RegisterScoreSource()
    {
        Test("评分来源：公众评分照旧那样格式化，0 与缺失都当作没有", () =>
        {
            // 这四条是从 ItemDetail.Score 那一条原样搬过来的，行为一字不改。
            Assert.Equal("8.4", ItemScore.Resolve(new EmbyItem { CommunityRating = 8.4f }, ScoreSource.Community).Text);
            Assert.Equal("8", ItemScore.Resolve(new EmbyItem { CommunityRating = 8f }, ScoreSource.Community).Text);
            Assert.Equal("8.4", ItemScore.Resolve(new EmbyItem { CommunityRating = 8.44f }, ScoreSource.Community).Text);
            Assert.False(ItemScore.Resolve(new EmbyItem { CommunityRating = 0 }, ScoreSource.Community).Any);
            Assert.False(ItemScore.Resolve(new EmbyItem(), ScoreSource.Community).Any);
            Assert.Equal("公众评分", ItemScore.Resolve(new EmbyItem { CommunityRating = 8.4f }, ScoreSource.Community).Label);
        });

        Test("评分来源：烂番茄是百分数，取整远离零", () =>
        {
            var item = new EmbyItem { CommunityRating = 8.4f, CriticRating = 92.4f };
            var badge = ItemScore.Resolve(item, ScoreSource.Critic);

            Assert.Equal("92%", badge.Text, "0–100 和 0–10 是两种刻度，混了就是屏上一个「92.0 分」");
            Assert.Equal("烂番茄", badge.Label);

            Assert.Equal("93%", ItemScore.Resolve(new EmbyItem { CriticRating = 92.6f }, ScoreSource.Critic).Text);

            // 默认的 Math.Round 是「取偶」，92.5 会答 92；这里要的是符合直觉的 93。
            Assert.Equal("93%", ItemScore.Resolve(new EmbyItem { CriticRating = 92.5f }, ScoreSource.Critic).Text);
        });

        Test("评分来源：豆瓣和 TMDB 只在服务器认出那一家时才署它的名", () =>
        {
            var plain = new EmbyItem { CommunityRating = 8.4f };
            Assert.Equal("公众评分", ItemScore.Resolve(plain, ScoreSource.Douban).Label,
                "认不出豆瓣就老实写公众评分，不能把一个来路不明的分说成豆瓣的");

            var douban = new EmbyItem { CommunityRating = 8.4f, ProviderIds = { ["Douban"] = "26816519" } };
            Assert.Equal("豆瓣", ItemScore.Resolve(douban, ScoreSource.Douban).Label);
            Assert.Equal("8.4", ItemScore.Resolve(douban, ScoreSource.Douban).Text, "数还是同一个 —— 换的只有名字");
            Assert.Equal("公众评分", ItemScore.Resolve(douban, ScoreSource.Tmdb).Label, "有豆瓣不等于有 TMDB");

            var tmdb = new EmbyItem { CommunityRating = 7.9f, ProviderIds = { ["Tmdb"] = "1396" } };
            Assert.Equal("TMDB", ItemScore.Resolve(tmdb, ScoreSource.Tmdb).Label);
        });

        Test("评分来源：ProviderIds 的键大小写不敏感，而且不靠字典自带的比较器", () =>
        {
            // System.Text.Json 给带 setter 的集合属性新建一个默认比较器的字典，把属性初始化器里那个换掉 —— 所以
            // 真正会坏的那一格是「服务器发来小写键、走反序列化」，而失配的样子是标签静静退回「公众评分」。
            var json = """
                {"Name":"活着","Id":"x","CommunityRating":9.3,"ProviderIds":{"douban":"1292365","imdb":"tt0110081"}}
                """;

            var item = System.Text.Json.JsonSerializer.Deserialize<EmbyItem>(json, EmbyHttp.Json)!;

            Assert.True(ItemScore.Matched(item, ItemScore.DoubanProvider), "小写的 douban 也算认出来了");
            Assert.Equal("豆瓣", ItemScore.Resolve(item, ScoreSource.Douban).Label);
            Assert.False(ItemScore.Matched(item, ItemScore.TmdbProvider));
        });

        Test("评分来源：三级回落，只有两个数都没有才是空", () =>
        {
            // 缺的那一格屏上会莫名空掉：服务器明明有一个分，而用户看到的是「选了豆瓣评分就没分了」。
            var criticOnly = new EmbyItem { CriticRating = 92f };
            var badge = ItemScore.Resolve(criticOnly, ScoreSource.Douban);
            Assert.Equal("92%", badge.Text);
            Assert.Equal("烂番茄", badge.Label, "标签说的是实际取到的那一档");

            var communityOnly = new EmbyItem { CommunityRating = 8.4f };
            Assert.Equal("8.4", ItemScore.Resolve(communityOnly, ScoreSource.Critic).Text, "选了烂番茄但服务器没有");
            Assert.Equal("公众评分", ItemScore.Resolve(communityOnly, ScoreSource.Critic).Label);

            Assert.False(ItemScore.Resolve(new EmbyItem(), ScoreSource.Critic).Any);
            Assert.False(ItemScore.Resolve(new EmbyItem { CommunityRating = 0, CriticRating = 0 }, ScoreSource.Douban).Any);
        });

        Test("评分来源：下拉盖住枚举的每一档，标签互不相同", () =>
        {
            // 存着的值不在选项里时，设置页那一手会额外造一条「设置文件中的值」塞进下拉 —— 少一档就是屏上多一行
            // 看不懂的东西。Catalogue 由 Describe 生成，所以这一条是结构性事实，钉的是「别有人改回手写两份」。
            Assert.Equal(Enum.GetValues<ScoreSource>().Length, ItemScore.Catalogue.Count);

            foreach (var source in Enum.GetValues<ScoreSource>())
            {
                Assert.Equal(1, ItemScore.Catalogue.Count(row => row.Value == source), $"{source} 出现的次数");
                Assert.True(ItemScore.Describe(source).Trim().Length > 0, $"{source} 没有名字");
            }

            Assert.Equal(ItemScore.Catalogue.Count,
                ItemScore.Catalogue.Select(row => row.Label).Distinct(StringComparer.Ordinal).Count(),
                "两档同名，用户就分不出选的是哪一个");
        });

        Test("评分来源：公众评分那一档必须是 0", () =>
        {
            // 设置文件里枚举存的是整数，缺键读出来是 0 —— 那必须落在装机时的行为上。往中间插一档会把用户存着的
            // 选择悄悄换成另一档。
            Assert.Equal(0, (int)ScoreSource.Community);
        });
    }
}
