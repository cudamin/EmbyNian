using EmbyMpvClient.Emby;
using EmbyMpvClient.Infrastructure;
using static EmbyMpvClient.Tests.TestHarness;

namespace EmbyMpvClient.Tests;

/// <summary>URL building, address normalisation and the formatting helpers the UI leans on.</summary>
internal static class EmbyTests
{
    private static readonly Uri ApiBase = new("http://192.168.31.230:8896/emby/");

    public static void Register()
    {
        RegisterAddress();
        RegisterUrls();
        RegisterQuery();
        RegisterIdentity();
        RegisterTimeFormat();
        RegisterItemModel();
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
            Assert.Contains("SortBy=DateCreated", query);
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
    }

    private static void RegisterIdentity()
    {
        Test("设备标识：授权头里的引号与逗号被清掉", () =>
        {
            var device = new DeviceIdentity(DeviceIdentity.ClientName, """我的"电脑",""", "dev-1", "2.0.0");
            var header = device.ToAuthorizationHeader();

            Assert.Equal("MediaBrowser Client=\"Emby MPV Client\", Device=\"我的电脑\", DeviceId=\"dev-1\", Version=\"2.0.0\"", header);
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
}
