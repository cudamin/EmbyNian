using System.Net;
using System.Text.Json.Nodes;
using EmbyNian.Emby;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 卡片「更多」菜单背后那十几个接口，加上下载那一路。
/// <para>
/// <b>这一层到 2026-09-05 之前一条测试都没有</b>，而它是整个客户端里唯一一片会往服务器上写东西的代码：删除
/// （连磁盘上的文件一起删）、刮削（覆盖手改过的元数据）、下载、换封面、加合集、删字幕、扫库。自检那一关只敢
/// 按只读的方式问四个接口 —— 会写东西的一律不碰，理由正当，可那就意味着这几条从来没有被任何一道闸门看过。
/// </para>
/// <para>
/// 它们本来就在 Core（<see cref="EmbyClient"/>），测试工程一直够得着，缺的只是有人来写。钉的是三件事：
/// <b>动词</b>（一次删除必须是 DELETE 一趟，不是 POST，也不是两趟）、<b>路径</b>（错一个段落服务器答 404，
/// 而屏上写的是「失败」，看不出错在哪儿）、<b>决定语义的那几个参数</b>（刮削和刷新是同一个接口的两套参数，
/// 分岔全在参数上）。
/// </para>
/// </summary>
internal static class ItemActionTests
{
    private const string Base = "http://192.168.31.230:8896/emby/";

    public static void Register()
    {
        RegisterWrites();
        RegisterMetadata();
        RegisterCollections();
        RegisterImagesAndSubtitles();
        RegisterDownload();
        RegisterPurePlans();
    }

    // ── 会写东西的那几条 ──────────────────────────────────────────────────────────────────────────────

    private static void RegisterWrites()
    {
        Test("更多菜单：删除只发一趟 DELETE，打在这个条目自己身上", () =>
        {
            var stub = new StubTransport().Answer("Items/m1", "");
            var client = Client(stub);

            Wait(client.DeleteItemAsync("m1", CancellationToken.None));

            var sent = stub.Only("Items/m1");
            Assert.Equal("DELETE", sent.Method, "删除必须是 DELETE —— POST 到同一个地址是「更新」");
            Assert.Equal($"{Base}Items/m1", sent.Url);
            Assert.Equal(1, stub.Total, "一次点击一趟请求");
        });

        Test("更多菜单：刮削覆盖元数据，刷新不覆盖，两档都不许动图片", () =>
        {
            // 同一个接口两套参数，分岔全在参数上 —— 这一条是这一批里最要紧的：ReplaceAllImages 要是跟着
            // ReplaceAllMetadata 一起变 true，用户刚在「修改媒体封面图」里挑的那张会被紧接着的一次刮削扔掉。
            var scrape = new StubTransport().Answer("Refresh", "");
            Wait(Client(scrape).RefreshItemAsync("m1", replace: true, CancellationToken.None));

            var one = scrape.Only("Items/m1/Refresh");
            Assert.Equal("POST", one.Method);
            Assert.Contains("MetadataRefreshMode=FullRefresh", one.Url);
            Assert.Contains("ReplaceAllMetadata=true", one.Url);
            Assert.Contains("ReplaceAllImages=false", one.Url, "刮削也不许替换图片");
            Assert.Contains("Recursive=true", one.Url);

            var refresh = new StubTransport().Answer("Refresh", "");
            Wait(Client(refresh).RefreshItemAsync("m1", replace: false, CancellationToken.None));

            var two = refresh.Only("Items/m1/Refresh");
            Assert.Contains("MetadataRefreshMode=Default", two.Url, "刷新是「缺什么补什么」");
            Assert.Contains("ReplaceAllMetadata=false", two.Url);
            Assert.Contains("ReplaceAllImages=false", two.Url);
        });

        Test("更多菜单：自检那一趟刮削真的什么都不改（ValidationOnly）", () =>
        {
            // 自检要确认这条路通着，又绝不能真去刮用户的库。那一档靠的就是这个参数，所以它值一条测试 ——
            // 哪天有人把 mode 那个参数「顺手」删掉，自检就会开始在每一轮里刮一次他的第一个条目。
            var stub = new StubTransport().Answer("Refresh", "");

            Wait(Client(stub).RefreshItemAsync("m1", replace: false, CancellationToken.None, mode: "ValidationOnly"));

            var sent = stub.Only("Refresh");
            Assert.Contains("MetadataRefreshMode=ValidationOnly", sent.Url);
            Assert.Contains("ImageRefreshMode=ValidationOnly", sent.Url);
            Assert.Contains("ReplaceAllMetadata=false", sent.Url);
        });

        Test("更多菜单：扫描媒体库打在整台服务器上，不带条目 id", () =>
        {
            var stub = new StubTransport().Answer("Library/Refresh", "");
            Wait(Client(stub).ScanLibraryAsync(CancellationToken.None));

            var sent = stub.Only("Library/Refresh");
            Assert.Equal("POST", sent.Method);
            Assert.Equal($"{Base}Library/Refresh", sent.Url);
        });

        Test("更多菜单：从继续观看中移除只改那一位，进度一个字不动", () =>
        {
            // 它和「标记为未观看」不是一件事：后者把进度清成零，这一条只是让那一排不再列它。所以地址里除了
            // Hide 不该有别的东西 —— 多一个参数就可能是在动进度。
            var stub = new StubTransport().Answer("HideFromResume", """{ "Played": false }""");
            Wait(Client(stub).HideFromResumeAsync("m1", hide: true, CancellationToken.None));

            var on = stub.Only("HideFromResume");
            Assert.Equal("POST", on.Method);
            Assert.Contains("Users/u1/Items/m1/HideFromResume", on.Url);
            Assert.Contains("Hide=true", on.Url);
            Assert.DoesNotContain("PlaybackPosition", on.Url, "这一条不许碰进度");

            var off = new StubTransport().Answer("HideFromResume", """{ "Played": false }""");
            Wait(Client(off).HideFromResumeAsync("m1", hide: false, CancellationToken.None));
            Assert.Contains("Hide=false", off.Only("HideFromResume").Url, "反方向也要发得出去");
        });
    }

    // ── 编辑元数据 ────────────────────────────────────────────────────────────────────────────────────

    private static void RegisterMetadata()
    {
        Test("更多菜单：编辑元数据发出去的就是改过的那份 JSON，一个字段不少", () =>
        {
            // 这一条要问的是请求体，不是地址。编辑那张表是「读回整份 JSON、改几个字段、整份发回去」——
            // 服务器拿到的要是一份被裁过的 JSON，没写进表里的那些字段（外部 id、锁定标记、演职人员）就会被
            // 一次「保存」抹掉，而屏上只会说「已保存」。
            var stub = new StubTransport().Answer("Items/m1", "");
            var json = new JsonObject
            {
                ["Id"] = "m1",
                ["Name"] = "攻壳机动队",
                ["Overview"] = "改过的简介",
                ["ProviderIds"] = new JsonObject { ["Tmdb"] = "9323" }
            };

            Wait(Client(stub).UpdateItemAsync("m1", json, CancellationToken.None));

            var sent = stub.Only("Items/m1");
            Assert.Equal("POST", sent.Method, "更新是 POST 到条目自己 —— DELETE 到同一个地址是删除");

            // 请求体照原样解回来再问，而不是拿字符串去找中文：这一层的序列化器把非 ASCII 转义成 \uXXXX（合法
            // JSON，服务器照样读得懂），所以「文字在不在」只有解回来才问得准。要钉的是**字段一个不少**。
            var body = JsonNode.Parse(sent.Body)!.AsObject();
            Assert.Equal("改过的简介", body["Overview"]!.GetValue<string>());
            Assert.Equal("攻壳机动队", body["Name"]!.GetValue<string>());
            Assert.Equal("9323", body["ProviderIds"]!["Tmdb"]!.GetValue<string>(), "表里没有的字段也要原样带回去");
            Assert.Equal(4, body.Count, "整份发回去，不是只发改过的那几个");
        });

        Test("更多菜单：编辑元数据先按 id 读回整份 JSON，用的是用户那条路径", () =>
        {
            // 走 Users/{id}/Items/{itemId} 而不是 Items/{itemId}：后者不带这个账号的 UserData，而那张表上
            // 「已观看」那类东西正是从这里来的。
            var stub = new StubTransport().Answer("Users/u1/Items/m1", """{ "Id": "m1", "Name": "攻壳机动队" }""");

            var json = Wait(Client(stub).GetItemJsonAsync("m1", CancellationToken.None));

            Assert.Equal("GET", stub.Only("Users/u1/Items/m1").Method);
            Assert.Equal("攻壳机动队", json["Name"]!.GetValue<string>());
        });
    }

    // ── 合集 ──────────────────────────────────────────────────────────────────────────────────────────

    private static void RegisterCollections()
    {
        Test("更多菜单：加入已有合集，条目 id 走 Ids", () =>
        {
            var stub = new StubTransport().Answer("Collections/c1/Items", "");
            Wait(Client(stub).AddToCollectionAsync("c1", ["m1"], CancellationToken.None));

            var sent = stub.Only("Collections/c1/Items");
            Assert.Equal("POST", sent.Method);
            Assert.Contains("Ids=m1", sent.Url);
        });

        Test("更多菜单：新建合集带着名字和第一个条目，答回来的是它的 id", () =>
        {
            var stub = new StubTransport().Answer("Collections", """{ "Id": "c9", "Name": "我的收藏夹" }""");

            var made = Wait(Client(stub).CreateCollectionAsync("我的收藏夹", ["m1"], CancellationToken.None));

            var sent = stub.Only("Collections");
            Assert.Equal("POST", sent.Method);
            Assert.Contains("Ids=m1", sent.Url);
            Assert.DoesNotContain("我的收藏夹", sent.Url, "合集名要转义 —— 中文和空格都不能裸着进地址");
            Assert.Equal("c9", made.Id);
            Assert.Equal("我的收藏夹", made.Name);
        });

        Test("更多菜单：列合集不带字段集，只要 id 和名字", () =>
        {
            // 那张表上只画名字，而 Fields 每多一项就是服务器多算一遍、多传一份 —— 一台合集上百的服务器上
            // 这一趟是开对话框之前唯一要等的东西。
            var stub = new StubTransport()
                .Answer("IncludeItemTypes=BoxSet", """{ "Items": [ { "Id": "c1", "Name": "宫崎骏" } ], "TotalRecordCount": 1 }""");

            var found = Wait(Client(stub).GetCollectionsAsync(CancellationToken.None));

            Assert.Equal(1, found.Count);
            Assert.Equal("宫崎骏", found[0].Name);
            Assert.Contains("Fields=&", stub.Only("IncludeItemTypes=BoxSet").Url + "&");
        });
    }

    // ── 封面图与字幕 ──────────────────────────────────────────────────────────────────────────────────

    private static void RegisterImagesAndSubtitles()
    {
        Test("更多菜单：候选封面按类型问，各家语言都要", () =>
        {
            var stub = new StubTransport()
                .Answer("RemoteImages", """{ "Images": [ { "Url": "http://x/1.jpg", "ProviderName": "TheMovieDb" } ], "TotalRecordCount": 1 }""");

            var found = Wait(Client(stub).GetRemoteImagesAsync("m1", EmbyImageStore.Primary, CancellationToken.None));

            var sent = stub.Only("RemoteImages");
            Assert.Equal("GET", sent.Method);
            Assert.Contains($"Type={EmbyImageStore.Primary}", sent.Url);
            Assert.Contains("IncludeAllLanguages=true", sent.Url, "只要本语言的话，多数条目一张候选都挑不到");
            Assert.Equal(1, found.Images.Count);
        });

        Test("更多菜单：选中的封面交给服务器去取，地址和刮削源都带上", () =>
        {
            // 图片由服务器代取，不是客户端下了再上传 —— 刮削源那些地址客户端多半连不上。所以这一趟里
            // ImageUrl 和 ProviderName 缺一个服务器就不知道去哪儿取。
            var stub = new StubTransport().Answer("RemoteImages/Download", "");

            Wait(Client(stub).ApplyRemoteImageAsync(
                "m1", EmbyImageStore.Primary, "http://x/1.jpg", "TheMovieDb", CancellationToken.None));

            var sent = stub.Only("RemoteImages/Download");
            Assert.Equal("POST", sent.Method);
            Assert.Contains("ProviderName=TheMovieDb", sent.Url);
            Assert.Contains("ImageUrl=http", sent.Url);
            Assert.DoesNotContain("://x/1.jpg", sent.Url, "候选图的地址要转义，它自己就是一个带 ? 和 & 的地址");
        });

        Test("更多菜单：搜字幕点名是哪个文件、哪种语言", () =>
        {
            var stub = new StubTransport().Answer("RemoteSearch/Subtitles", "[]");

            Wait(Client(stub).SearchSubtitlesAsync("m1", "src-1", "chi", CancellationToken.None));

            var sent = stub.Only("RemoteSearch/Subtitles");
            Assert.Equal("GET", sent.Method);
            Assert.Contains("Items/m1/RemoteSearch/Subtitles/chi", sent.Url);
            Assert.Contains("MediaSourceId=src-1", sent.Url, "一个条目可能有几个文件，字幕挂在文件上");
        });

        Test("更多菜单：下字幕那个 id 要转义 —— 它是插件自己造的记号", () =>
        {
            // 字幕插件的 id 里出现斜杠是常事（opensubtitles 那种「provider/12345」），不转义就把路由整条
            // 打断，服务器答 404，而屏上写的是「字幕操作失败」。
            var stub = new StubTransport().Answer("RemoteSearch/Subtitles", """{ "Index": 3 }""");

            Wait(Client(stub).DownloadSubtitleAsync("m1", "src-1", "os/12345", CancellationToken.None));

            var sent = stub.Only("RemoteSearch/Subtitles");
            Assert.Equal("POST", sent.Method);
            Assert.Contains("Subtitles/os%2F12345", sent.Url, "斜杠必须转义");
            Assert.DoesNotContain("Subtitles/os/12345", sent.Url);
        });

        Test("更多菜单：删字幕按轨道序号，DELETE 一趟", () =>
        {
            var stub = new StubTransport().Answer("Subtitles/3", "");
            Wait(Client(stub).DeleteSubtitleAsync("m1", 3, CancellationToken.None));

            var sent = stub.Only("Subtitles/3");
            Assert.Equal("DELETE", sent.Method);
            Assert.Equal($"{Base}Items/m1/Subtitles/3", sent.Url);
        });
    }

    // ── 下载 ──────────────────────────────────────────────────────────────────────────────────────────

    private static void RegisterDownload()
    {
        Test("下载到设备：走原始文件那条路，边下边报进度，落到指定路径", () =>
        {
            var stub = new StubTransport().Answer("Items/m1/Download", "0123456789");
            var path = Path.Combine(Path.GetTempPath(), $"embynian-dl-{Guid.NewGuid():N}.mkv");
            var reports = new List<long>();

            try
            {
                var bytes = Wait(Client(stub).DownloadToFileAsync(
                    "m1",
                    path,
                    new Progress<(long Done, long? Total)>(state => reports.Add(state.Done)),
                    CancellationToken.None));

                Assert.Equal("GET", stub.Only("Items/m1/Download").Method);
                Assert.Equal(10L, bytes);
                Assert.True(File.Exists(path), "下完那一份要真的在磁盘上");
                Assert.Equal("0123456789", File.ReadAllText(path));
                Assert.False(File.Exists(path + ".part"), "临时那一份下完要改名，不许留在原地");
            }
            finally
            {
                File.Delete(path);
                File.Delete(path + ".part");
            }
        });

        Test("下载到设备：服务器说了有多少就必须收到这么多，少了当失败且不留残件", () =>
        {
            // 2026-09-05 那轮代码审查加的判断。少了它，磁盘上留下的是一个名字正确、大小不对的影片 —— 双击
            // 能打开、播到一半没了，而那时候没有任何东西说过「这次下载失败了」。
            var stub = new StubTransport().Understate("Items/m1/Download", "0123", claimed: 10);
            var path = Path.Combine(Path.GetTempPath(), $"embynian-dl-{Guid.NewGuid():N}.mkv");

            try
            {
                var error = Assert.Catch<IOException>(() => Wait(Client(stub).DownloadToFileAsync(
                    "m1", path, null, CancellationToken.None)));

                Assert.Contains("下载不完整", error.Message, "这句话就是那道检查自己写的 —— 它证明红的是我们那一行");
                Assert.Contains("服务器说有 10 字节", error.Message);

                Assert.False(File.Exists(path), "没下全就不该有这个文件");
                Assert.False(File.Exists(path + ".part"), "半截那一份也要清掉");
            }
            finally
            {
                File.Delete(path);
                File.Delete(path + ".part");
            }
        });

        Test("下载到设备：服务器拒绝时不留下一个空文件", () =>
        {
            var stub = new StubTransport().Fail("Items/m1/Download", HttpStatusCode.Forbidden);
            var path = Path.Combine(Path.GetTempPath(), $"embynian-dl-{Guid.NewGuid():N}.mkv");

            try
            {
                Assert.Throws<EmbyApiException>(() => Wait(Client(stub).DownloadToFileAsync(
                    "m1", path, null, CancellationToken.None)));

                Assert.False(File.Exists(path), "403（账号没有下载权限）之后目录里该干干净净");
                Assert.False(File.Exists(path + ".part"));
            }
            finally
            {
                File.Delete(path);
                File.Delete(path + ".part");
            }
        });
    }

    // ── 搬进 Core 的那几段判断 ────────────────────────────────────────────────────────────────────────

    private static void RegisterPurePlans()
    {
        Test("下载计划：一叠单集该拿哪两个 id 去问", () =>
        {
            var series = new EmbyItem { Id = "s1", Name = "攻壳机动队", Type = EmbyItemType.Series };
            Assert.Equal(("s1", (string?)null), DownloadPlan.EpisodeQuery(series), "剧用自己的 id，没有季筛选");

            var season = new EmbyItem
            {
                Id = "se2", Name = "第 2 季", Type = EmbyItemType.Season, SeriesId = "s1", SeriesName = "攻壳机动队"
            };
            Assert.Equal(("s1", "se2"), DownloadPlan.EpisodeQuery(season),
                "季要用它所属剧的 id 去问，自己那个 id 只当季筛选 —— 单集列表那个接口只认剧");

            var orphan = new EmbyItem { Id = "se3", Name = "第 3 季", Type = EmbyItemType.Season };
            Assert.Equal(((string?)null, (string?)null), DownloadPlan.EpisodeQuery(orphan),
                "季却没有 SeriesId 时要答「问不出来」，不许拿季的 id 硬试 —— 那会以「一集都没有」的样子回来");

            var movie = new EmbyItem { Id = "m1", Name = "攻壳机动队", Type = EmbyItemType.Movie };
            Assert.Equal(((string?)null, (string?)null), DownloadPlan.EpisodeQuery(movie), "一个文件不走这条路");
        });

        Test("下载计划：进度那一行，服务器没说总长度时只报已下载", () =>
        {
            Assert.Contains("50%", DownloadPlan.Portion(500, 1000));
            Assert.Contains("0%", DownloadPlan.Portion(0, 1000));
            Assert.Contains("100%", DownloadPlan.Portion(1000, 1000));

            Assert.Contains("已下载", DownloadPlan.Portion(500, null), "没说总长度就只报已经下了多少");
            Assert.DoesNotContain("%", DownloadPlan.Portion(500, null));

            // 0 和负数都要当成「没说」：拿它做除数会抛，而那一下抛在进度回调里，看着是下载卡住了。
            Assert.DoesNotContain("%", DownloadPlan.Portion(500, 0));
            Assert.DoesNotContain("%", DownloadPlan.Portion(500, -1));
        });

        Test("下载计划：下完那一句四个分支各说对了什么", () =>
        {
            var one = DownloadPlan.Summary("攻壳机动队", @"D:\视频\EmbyNian", @"D:\视频\EmbyNian\攻壳机动队.mkv", 1, 1, 0);
            Assert.Contains(@"攻壳机动队.mkv", one.Text, "一个文件要报文件自己的路径 —— 这是唯一一次看得到落点的机会");
            Assert.False(one.Warning);

            var many = DownloadPlan.Summary("攻壳机动队", @"D:\视频\EmbyNian\攻壳机动队", "", 24, 24, 0);
            Assert.Contains("24 个文件", many.Text);
            Assert.Contains(@"EmbyNian\攻壳机动队", many.Text, "多个文件要报文件夹");
            Assert.DoesNotContain("跳过", many.Text, "一个都没跳过就别提跳过");
            Assert.False(many.Warning);

            var partial = DownloadPlan.Summary("攻壳机动队", @"D:\视频", "", 24, 19, 5);
            Assert.Contains("19 个文件", partial.Text);
            Assert.Contains("5 个跳过", partial.Text, "少下了几个必须说出来，不然「已下载 19 个」读起来像下全了");
            Assert.False(partial.Warning);

            var none = DownloadPlan.Summary("攻壳机动队", @"D:\视频", "", 3, 0, 3);
            Assert.Contains("没有一个文件下得下来", none.Text);
            Assert.True(none.Warning, "一个都没下成不是「成功了 0 个」，是一句警告");
        });

        Test("更多菜单：删除那句问话说清了删什么、删到哪一步", () =>
        {
            var movie = new EmbyItem { Id = "m1", Name = "攻壳机动队", Type = EmbyItemType.Movie };
            var (title, body) = ItemMenu.DeletePrompt(movie);

            Assert.Contains("电影", title, "标题要说清删的是什么类型");
            Assert.Contains("攻壳机动队", body);
            Assert.Contains("服务器磁盘上的文件", body, "「连文件一起删」是这句话存在的理由");
            Assert.Contains("无法撤销", body);
            Assert.DoesNotContain("单集", body, "一部电影里没有单集");

            var series = new EmbyItem { Id = "s1", Name = "攻壳机动队", Type = EmbyItemType.Series };
            Assert.Contains("里面的所有单集都会被删除", ItemMenu.DeletePrompt(series).Body,
                "一叠单集要多说这一句 —— 少了它，一次点击和用户以为的事情差着二十四个文件");

            var season = new EmbyItem { Id = "se1", Name = "第 1 季", Type = EmbyItemType.Season, SeriesId = "s1" };
            Assert.Contains("里面的所有单集都会被删除", ItemMenu.DeletePrompt(season).Body);

            var nameless = new EmbyItem { Id = "x1", Name = "某个东西", Type = "" };
            Assert.Equal("删除这个条目", ItemMenu.DeletePrompt(nameless).Title,
                "类型名问不出来时退到「条目」，不然标题是「删除这个」，读起来像断在半截上");

            // 服务器发来一个我们没有中文名的类型时，标题里用的是它自己那个词（EmbyItemType.ToChinese 的兜底）。
            // 「删除这个 Photo」不好看，但它是真话，比「删除这个条目」少骗人一次。
            var unknown = new EmbyItem { Id = "x2", Name = "某张照片", Type = "Photo" };
            Assert.Equal("删除这个Photo", ItemMenu.DeletePrompt(unknown).Title);
        });
    }

    // ── 脚手架 ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 一个接着假传输层的客户端。不经过 <c>EmbySession</c>：这一批问的是「这一条命令发出去的是什么」，而会话
    /// 那一层管的是令牌和重登，两件事分开钉住比合在一起容易读。
    /// </summary>
    private static EmbyClient Client(StubTransport transport) =>
        new(new EmbyHttp(transport),
            new EmbyConnection(
                new Uri(Base),
                "token-1",
                "u1",
                "donxuelian",
                "果服",
                DeviceIdentity.Create("device-1", "3.0.0")));

    /// <summary>抛原始异常而不是 <see cref="AggregateException"/> —— 有几条断言问的正是抛的是哪一种。</summary>
    private static T Wait<T>(Task<T> task)
    {
        Wait((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static void Wait(Task task) => task.GetAwaiter().GetResult();
}
