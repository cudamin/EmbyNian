using System.Text;
using EmbyMpvClient.Configuration;
using EmbyMpvClient.Emby;
using EmbyMpvClient.Mpv;
using EmbyMpvClient.Playback;
using static EmbyMpvClient.Tests.TestHarness;

namespace EmbyMpvClient.Tests;

/// <summary>
/// Covers the pure half of the playback layer: argument building, track numbering, shader
/// selection, IPC framing and the planner. Everything here runs without mpv and without a
/// server — the parts that need either are checked by the smoke run instead.
/// </summary>
internal static class PlaybackTests
{
    public static void Register()
    {
        RegisterCommandLine();
        RegisterTrackMapping();
        RegisterArguments();
        RegisterShaders();
        RegisterIpc();
        RegisterPlanner();
    }

    // ---- 附加参数拆分 ----------------------------------------------------------

    private static void RegisterCommandLine()
    {
        Test("命令行拆分：空白分隔与引号", () =>
        {
            var arguments = CommandLine.Split("""--vo=gpu-next --title="我的 标题" --fullscreen""");
            Assert.Equal(3, arguments.Count);
            Assert.Equal("--vo=gpu-next", arguments[0]);
            Assert.Equal("--title=我的 标题", arguments[1]);
            Assert.Equal("--fullscreen", arguments[2]);
        });

        Test("命令行拆分：反斜杠属于路径而不是转义", () =>
        {
            var arguments = CommandLine.Split("""--sub-file-paths="D:\字幕\新番" """);
            Assert.Equal(1, arguments.Count);
            Assert.Equal(@"--sub-file-paths=D:\字幕\新番", arguments[0]);
        });

        Test("命令行拆分：空文本与多余空格", () =>
        {
            Assert.Equal(0, CommandLine.Split(null).Count);
            Assert.Equal(0, CommandLine.Split("   ").Count);
            Assert.Equal(2, CommandLine.Split("  --a    --b  ").Count);
        });
    }

    // ---- 轨道编号映射 ----------------------------------------------------------

    private static void RegisterTrackMapping()
    {
        Test("轨道映射：Emby 容器索引转 mpv 的按类型编号", () =>
        {
            var source = SourceWith(
                Stream(0, "Video"),
                Stream(1, "Audio", language: "jpn"),
                Stream(2, "Audio", language: "chi"),
                Stream(3, "Subtitle", codec: "ass"),
                Stream(4, "Subtitle", codec: "pgssub"));

            var map = MpvTrackMap.Build(source);
            Assert.Equal(1, map.AudioId(1), "第一条音轨是 --aid=1");
            Assert.Equal(2, map.AudioId(2), "容器索引 2 是第二条音轨");
            Assert.Equal(1, map.SubtitleId(3), "字幕从 1 重新计数");
            Assert.Equal(2, map.SubtitleId(4));
            Assert.Null(map.AudioId(0), "视频轨没有音轨编号");
            Assert.Equal(0, map.ExternalSubtitleIndexes.Count);
        });

        Test("轨道映射：外挂文本字幕排在内封之后", () =>
        {
            var source = SourceWith(
                Stream(0, "Video"),
                Stream(1, "Audio"),
                Stream(2, "Subtitle", codec: "ass"),
                Stream(3, "Subtitle", codec: "srt", external: true),
                Stream(4, "Subtitle", codec: "subrip", external: true));

            var map = MpvTrackMap.Build(source);
            Assert.Equal(1, map.SubtitleId(2));
            Assert.Equal(2, map.SubtitleId(3), "第一个 --sub-file 接在内封字幕后面");
            Assert.Equal(3, map.SubtitleId(4));
            Assert.Equal(2, map.ExternalSubtitleIndexes.Count);
            Assert.Equal(3, map.ExternalSubtitleIndexes[0], "顺序必须与命令行上 --sub-file 的顺序一致");
        });

        Test("轨道映射：无法交给 mpv 的外挂轨道不占编号", () =>
        {
            var source = SourceWith(
                Stream(0, "Video"),
                Stream(1, "Audio"),
                Stream(2, "Audio", external: true),
                Stream(3, "Subtitle", codec: "pgssub", external: true),
                Stream(4, "Subtitle", codec: "srt", external: true));

            var map = MpvTrackMap.Build(source);
            Assert.Null(map.AudioId(2), "Emby 无法单独提供外挂音轨");
            Assert.Null(map.SubtitleId(3), "外挂图形字幕无法转成文件");
            Assert.Equal(1, map.SubtitleId(4), "被跳过的外挂轨道不能让编号错位");
            Assert.Equal(2, map.UnavailableIndexes.Count);
            Assert.False(map.CanSelect(3), "不可用的轨道要能被界面识别出来");
            Assert.True(map.CanSelect(4), "可用的外挂字幕仍然可选");
        });

        Test("轨道映射：服务器乱序发送时按索引重排", () =>
        {
            var source = SourceWith(
                Stream(3, "Subtitle", codec: "ass"),
                Stream(1, "Audio"),
                Stream(0, "Video"),
                Stream(2, "Audio"));

            var map = MpvTrackMap.Build(source);
            Assert.Equal(1, map.AudioId(1), "编号必须按容器索引而不是数组顺序");
            Assert.Equal(2, map.AudioId(2));
        });

        Test("轨道优选：按语言挑选并以服务器默认值决胜", () =>
        {
            MediaStream[] streams =
            [
                Stream(1, "Audio", language: "eng"),
                Stream(2, "Audio", language: "jpn"),
                Stream(3, "Audio", language: "jpn")
            ];

            Assert.Equal(2, TrackPreference.Choose(streams, "jpn", null)!.Index, "同语言下取第一条");
            Assert.Equal(3, TrackPreference.Choose(streams, "jpn", 3)!.Index, "服务器默认轨优先");
            Assert.Equal(1, TrackPreference.Choose(streams, "eng", null)!.Index);
            Assert.Equal(1, TrackPreference.Choose(streams, "kor", null)!.Index, "无匹配语言时不返回 null");
            Assert.Null(TrackPreference.Choose([], "chi", null), "没有轨道时返回 null");
        });

        Test("轨道优选：中文代码互认（chi / zh-CN / chs）", () =>
        {
            Assert.True(TrackPreference.LanguageMatches(Stream(1, "Subtitle", language: "zh-CN"), "chi"), "zh-CN 应匹配 chi");
            Assert.True(TrackPreference.LanguageMatches(Stream(1, "Subtitle", language: "chs"), "chi"), "chs 应匹配 chi");
            Assert.True(TrackPreference.LanguageMatches(Stream(1, "Subtitle", language: "jpn"), "ja"), "jpn 应匹配 ja");
            Assert.True(TrackPreference.LanguageMatches(Stream(1, "Subtitle", displayLanguage: "简体中文"), "chi"), "显示语言也参与匹配");
            Assert.False(TrackPreference.LanguageMatches(Stream(1, "Subtitle", language: "eng"), "chi"));
            Assert.False(TrackPreference.LanguageMatches(Stream(1, "Subtitle", language: "chi"), "  "), "空偏好不算匹配");
        });

        Test("轨道优选：强制字幕开关生效", () =>
        {
            MediaStream[] streams =
            [
                Stream(1, "Subtitle", language: "chi", forced: true),
                Stream(2, "Subtitle", language: "chi")
            ];

            Assert.Equal(2, TrackPreference.Choose(streams, "chi", null)!.Index, "默认避开强制字幕");
            Assert.Equal(1, TrackPreference.Choose(streams, "chi", null, preferForced: true)!.Index);
        });
    }

    // ---- 启动参数 --------------------------------------------------------------

    private static void RegisterArguments()
    {
        Test("启动参数：--include 必须排在 --profile 之前", () =>
        {
            var arguments = MpvArgumentBuilder.Build(Request(includeFile: @"C:\mpv\embympvclient.conf", profile: "2K-iGPU"));

            var include = IndexOfPrefix(arguments, "--include=");
            var profile = IndexOfPrefix(arguments, "--profile=");
            Assert.True(include >= 0, "应带上 --include");
            Assert.True(profile > include, $"--profile 必须在 --include 之后（实际 {include} / {profile}）");
        });

        Test("启动参数：没有配置组时不写 --profile 与 --include", () =>
        {
            var line = Line(MpvArgumentBuilder.Build(Request()));
            Assert.DoesNotContain("--profile=", line);
            Assert.DoesNotContain("--include=", line);
        });

        Test("启动参数：始终显式给出 --start 并关掉 mpv 自身续播", () =>
        {
            var fromStart = Line(MpvArgumentBuilder.Build(Request()));
            Assert.Contains("--start=0", fromStart, "「从头播放」不能变成「从 mpv 上次停下的地方播放」");
            Assert.Contains("--resume-playback=no", fromStart);
            Assert.Contains("--save-position-on-quit=no", fromStart);

            Assert.Contains("--start=1234.5", Line(MpvArgumentBuilder.Build(Request(start: 1234.5))));
        });

        Test("启动参数：交还续播给 mpv 时不再覆盖它的设置", () =>
        {
            var line = Line(MpvArgumentBuilder.Build(Request(overrideResume: false)));
            Assert.DoesNotContain("--resume-playback=no", line);
            Assert.DoesNotContain("--save-position-on-quit=no", line);
            Assert.Contains("--start=0", line, "位置仍然由客户端给出");
        });

        Test("启动参数：必须让 mpv 在播完后退出（mpv.conf 里 idle=yes）", () =>
        {
            var line = Line(MpvArgumentBuilder.Build(Request()));
            Assert.Contains("--idle=no", line);
            Assert.Contains("--keep-open=no", line);
        });

        Test("启动参数：音轨、字幕与外挂字幕", () =>
        {
            var request = Request() with
            {
                AudioId = 2,
                SubtitleId = 3,
                ExternalSubtitles = [new Uri("http://server/emby/Videos/1/1/Subtitles/4/Stream.ass")]
            };

            var arguments = MpvArgumentBuilder.Build(request);
            Assert.Contains("--aid=2", Line(arguments));
            Assert.Contains("--sid=3", Line(arguments));
            Assert.Contains("--sub-file=http://server/emby/Videos/1/1/Subtitles/4/Stream.ass", Line(arguments));
        });

        Test("启动参数：关闭字幕时用 --sid=no 覆盖 slang", () =>
        {
            var line = Line(MpvArgumentBuilder.Build(Request() with { SubtitleId = 3, SubtitlesDisabled = true }));
            Assert.Contains("--sid=no", line);
            Assert.DoesNotContain("--sid=3", line);
        });

        Test("启动参数：请求头逐条追加，URL 排在 -- 之后", () =>
        {
            var arguments = MpvArgumentBuilder.Build(Request() with
            {
                HttpHeaders =
                [
                    new("X-Emby-Token", "abc123"),
                    new("X-Emby-Authorization", """MediaBrowser Client="Emby MPV Client", Device="PC" """)
                ]
            });

            var appends = arguments.Count(argument => argument.StartsWith("--http-header-fields-append=", StringComparison.Ordinal));
            Assert.Equal(2, appends, "每个请求头一条 -append，含逗号的值才不会被拆成两条");

            var separator = arguments.ToList().IndexOf("--");
            Assert.True(separator >= 0, "URL 前必须有 --");
            Assert.Equal(arguments.Count - 1, separator + 1, "URL 必须是最后一个参数");
            Assert.Contains("Videos/1/stream.mkv", arguments[^1]);
        });

        Test("启动参数：附加参数插在 URL 之前", () =>
        {
            var arguments = MpvArgumentBuilder.Build(Request() with { ExtraArguments = ["--fullscreen", "--volume=80"] });
            var separator = arguments.ToList().IndexOf("--");
            Assert.True(arguments.ToList().IndexOf("--fullscreen") < separator, "附加参数不能落到 -- 之后被当成文件");
        });

        Test("启动参数：日志里必须看不到 Emby 令牌", () =>
        {
            var arguments = MpvArgumentBuilder.Build(Request() with
            {
                HttpHeaders = [new("X-Emby-Token", "SECRET-TOKEN")]
            });

            var redacted = Line(MpvArgumentBuilder.Redact(arguments));
            Assert.DoesNotContain("SECRET-TOKEN", redacted);
            Assert.Contains("X-Emby-Token: ***", redacted);
            Assert.Contains("SECRET-TOKEN", Line(arguments), "真正传给 mpv 的参数当然还带着令牌");
        });

        Test("启动参数：IPC 管道只在给出时出现", () =>
        {
            Assert.DoesNotContain("--input-ipc-server", Line(MpvArgumentBuilder.Build(Request())));
            Assert.Contains(@"--input-ipc-server=\\.\pipe\embympvclient-x",
                Line(MpvArgumentBuilder.Build(Request(), @"\\.\pipe\embympvclient-x")));
        });

        Test("IPC 管道名：每次启动都不同，避免撞上手动开的 mpv", () =>
        {
            var first = MpvIpcClient.CreatePipeName();
            Assert.False(first == MpvIpcClient.CreatePipeName(), "管道名必须唯一");
            Assert.Equal($@"\\.\pipe\{first}", MpvIpcClient.ToPipePath(first));
            Assert.DoesNotContain("mpvsocket", first, "不能用 mpv.conf 里那个全局管道名");
        });
    }

    // ---- 着色器配置组 ----------------------------------------------------------

    private static void RegisterShaders()
    {
        Test("着色器：两个开关都关时不动 mpv.conf 的选择", () =>
        {
            var settings = Shaders(all: false, anime: false);
            Assert.Null(settings.Resolve(looksAnimated: true, sourceHeight: 1080));
            Assert.Null(settings.Resolve(looksAnimated: false, sourceHeight: 1080));
        });

        Test("着色器：对所有视频启用时套用默认组", () =>
        {
            var settings = Shaders(all: true, anime: false);
            Assert.Equal("2K-iGPU", settings.Resolve(looksAnimated: false, sourceHeight: 1080));
            Assert.Equal("2K-iGPU", settings.Resolve(looksAnimated: true, sourceHeight: 1080), "动画开关没开就不该用动画组");
        });

        Test("着色器：动画开关单独打开时只对动画生效", () =>
        {
            var settings = Shaders(all: false, anime: true);
            Assert.Equal("2K-iGPU-Anime", settings.Resolve(looksAnimated: true, sourceHeight: 1080));
            Assert.Null(settings.Resolve(looksAnimated: false, sourceHeight: 1080), "非动画应沿用 mpv.conf");
        });

        Test("着色器：4K 片源在 2K 屏上改用省电组", () =>
        {
            var settings = Shaders(all: true, anime: true);
            Assert.Equal("2K-iGPU-Light", settings.Resolve(looksAnimated: false, sourceHeight: 2160));
            Assert.Equal("2K-iGPU-Light", settings.Resolve(looksAnimated: true, sourceHeight: 2160), "4K 动画同样只会被缩小");
            Assert.Equal("2K-iGPU-Anime", settings.Resolve(looksAnimated: true, sourceHeight: 1080), "1080p 才需要放大链");
            Assert.Equal("2K-iGPU", settings.Resolve(looksAnimated: false, sourceHeight: null), "高度未知时按普通片源处理");
        });

        Test("着色器：清空高分辨率组即关闭该特例", () =>
        {
            var settings = Shaders(all: true, anime: true);
            settings.HighResProfile = "";
            Assert.Equal("2K-iGPU", settings.Resolve(looksAnimated: false, sourceHeight: 2160));
        });

        Test("着色器：动画判定只看类型/风格与标签", () =>
        {
            var resolver = new ShaderProfileResolver(Shaders(all: false, anime: true));

            Assert.True(resolver.LooksAnimated(ShaderProfileResolver.StyleHints(
                Item("紫罗兰永恒花园", genres: ["动画", "剧情"]))), "Genres 命中");
            Assert.True(resolver.LooksAnimated(ShaderProfileResolver.StyleHints(
                Item("某部片", tags: ["番剧"]))), "Tags 命中");
            Assert.True(resolver.LooksAnimated(ShaderProfileResolver.StyleHints(
                Item("某部片", genres: ["Animation"]))), "英文关键词大小写无关");
            Assert.False(resolver.LooksAnimated(ShaderProfileResolver.StyleHints(
                Item("动画简史", genres: ["纪录片"]))), "标题带「动画」的纪录片不能被误判");
        });

        Test("着色器：单集借用剧集的类型/风格", () =>
        {
            var resolver = new ShaderProfileResolver(Shaders(all: false, anime: true));
            var episode = Item("第 1 集", type: EmbyItemType.Episode);
            var series = Item("葬送的芙莉莲", type: EmbyItemType.Series, genres: ["动画"]);

            Assert.False(resolver.Resolve(episode, null).HasProfile, "单集自己通常没有风格");
            Assert.Equal("2K-iGPU-Anime", resolver.Resolve(episode, null, series).Profile, "有剧集兜底时应命中");
        });

        Test("着色器：决策原因会写进日志", () =>
        {
            var resolver = new ShaderProfileResolver(Shaders(all: true, anime: true));
            var decision = resolver.Resolve(Item("某部电影"), Source1080p());
            Assert.True(decision.HasProfile, "开关已开");
            Assert.Contains("已对所有视频启用", decision.Reason);

            var high = resolver.Resolve(Item("某部电影"), Source4K());
            Assert.Contains("只会缩小", high.Reason);
        });
    }

    // ---- IPC 分帧与解析 --------------------------------------------------------

    private static void RegisterIpc()
    {
        Test("IPC 分帧：一次读到两条消息", () =>
        {
            var buffer = new MpvIpcLineBuffer();
            var lines = buffer.Append(Bytes("{\"event\":\"pause\"}\n{\"event\":\"unpause\"}\n"));
            Assert.Equal(2, lines.Count);
            Assert.Equal("{\"event\":\"pause\"}", lines[0]);
            Assert.Equal(0, buffer.PendingBytes);
        });

        Test("IPC 分帧：消息被切成两次读取（v1 就是在这里丢消息的）", () =>
        {
            var buffer = new MpvIpcLineBuffer();
            Assert.Equal(0, buffer.Append(Bytes("{\"data\":12")).Count, "半条消息先不产出");
            Assert.True(buffer.PendingBytes > 0, "残留字节要留着");

            var lines = buffer.Append(Bytes("3.5,\"request_id\":7}\n"));
            Assert.Equal(1, lines.Count);
            Assert.True(MpvIpcMessage.TryParse(lines[0], out var message), "拼起来必须是合法 JSON");
            Assert.Equal(123.5, message.AsDouble());
            Assert.Equal(7, message.RequestId);
        });

        Test("IPC 分帧：CRLF 与空行", () =>
        {
            var buffer = new MpvIpcLineBuffer();
            var lines = buffer.Append(Bytes("{\"event\":\"seek\"}\r\n\n{\"event\":\"idle\"}\n"));
            Assert.Equal(2, lines.Count, "空行不产出消息");
            Assert.Equal("{\"event\":\"seek\"}", lines[0], "行尾的 \\r 要去掉");
        });

        Test("IPC 分帧：UTF-8 汉字跨读取边界", () =>
        {
            var buffer = new MpvIpcLineBuffer();
            var payload = Bytes("{\"data\":\"第一集\"}\n");
            // 10 落在「第」这个三字节字符的中间：分帧必须按字节缓存，等到换行才解码。
            Assert.Equal(0, buffer.Append(payload.AsSpan(0, 10)).Count);
            var lines = buffer.Append(payload.AsSpan(10));
            Assert.Equal(1, lines.Count);
            Assert.True(MpvIpcMessage.TryParse(lines[0], out var message), "汉字不能被拆坏");
            Assert.Equal("第一集", message.AsString());
        });

        Test("IPC 解析：属性变化事件", () =>
        {
            Assert.True(MpvIpcMessage.TryParse("""{"event":"property-change","name":"pause","data":true}""", out var message), "应能解析");
            Assert.Equal("property-change", message.Event);
            Assert.Equal("pause", message.PropertyName);
            Assert.Equal(true, message.AsBoolean());
            Assert.False(message.IsReply, "事件不是命令回复");
        });

        Test("IPC 解析：文件结束原因", () =>
        {
            Assert.True(MpvIpcMessage.TryParse("""{"event":"end-file","reason":"eof"}""", out var message), "应能解析");
            Assert.Equal(MpvEndFileReason.Eof, message.Reason);
        });

        Test("IPC 解析：成功与失败的回复", () =>
        {
            Assert.True(MpvIpcMessage.TryParse("""{"data":42.5,"request_id":3,"error":"success"}""", out var ok), "应能解析");
            Assert.True(ok.IsReply, "带 request_id 的是回复");
            Assert.True(ok.IsSuccess, "error=success 表示成功");
            Assert.Equal(42.5, ok.AsDouble());

            Assert.True(MpvIpcMessage.TryParse("""{"error":"property unavailable","request_id":4}""", out var bad), "应能解析");
            Assert.False(bad.IsSuccess, "属性不可用时不能当成功");
            Assert.Null(bad.AsDouble(), "没有 data 时取不到数值");
        });

        Test("IPC 解析：非 JSON 与终端输出不应抛异常", () =>
        {
            Assert.False(MpvIpcMessage.TryParse("", out _));
            Assert.False(MpvIpcMessage.TryParse("[vo/gpu-next] reconfig", out _), "mpv 的终端输出不是消息");
            Assert.False(MpvIpcMessage.TryParse("{不是 JSON", out _));
        });

        Test("IPC 编码：命令行序列化为 mpv 期望的形状", () =>
        {
            var payload = Encoding.UTF8.GetString(MpvIpcClient.Encode(["get_property", "time-pos"], 9));
            Assert.Equal("{\"command\":[\"get_property\",\"time-pos\"],\"request_id\":9}\n", payload);
        });

        Test("IPC 编码：引号与汉字能原样还原", () =>
        {
            const string text = """《第 1 集》"引号"\反斜杠""";
            var payload = Encoding.UTF8.GetString(MpvIpcClient.Encode(["show-text", text], 1));

            // 默认编码器会把引号和汉字都写成 \uXXXX，整条命令因此是纯 ASCII，
            // 这是好事：管道两端不会再有编码分歧。要验证的是还原后一模一样。
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var command = document.RootElement.GetProperty("command");
            Assert.Equal("show-text", command[0].GetString());
            Assert.Equal(text, command[1].GetString(), "转义后必须能还原成原文");
            Assert.Equal(1, document.RootElement.GetProperty("request_id").GetInt32());
            Assert.True(payload.EndsWith("\n", StringComparison.Ordinal), "mpv 按行读取，必须以换行结尾");
            Assert.True(payload.All(character => character < 128), "载荷应为纯 ASCII");
        });
    }

    // ---- 计划层 ----------------------------------------------------------------

    private static void RegisterPlanner()
    {
        Test("计划：URL、请求头与令牌位置", () =>
        {
            var (planner, settings) = Planner();
            settings.Shaders.ApplyToAllVideos = false;
            settings.Shaders.AutoAnimeProfile = false;

            var request = planner.Plan(Ticket(), Connection());

            Assert.Contains("Videos/42/stream.mkv", request.MediaUrl.AbsoluteUri);
            Assert.DoesNotContain("api_key", request.MediaUrl.AbsoluteUri, "令牌不能出现在 URL 里");
            Assert.Equal("X-Emby-Token", request.HttpHeaders[0].Key);
            Assert.Equal("token-abc", request.HttpHeaders[0].Value);
            Assert.Contains("MediaBrowser Client=", request.HttpHeaders[1].Value);
        });

        Test("计划：轨道索引换成 mpv 编号，外挂字幕换成 URL", () =>
        {
            var (planner, _) = Planner();
            var ticket = Ticket() with { AudioStreamIndex = 2, SubtitleStreamIndex = 4 };

            var request = planner.Plan(ticket, Connection());

            Assert.Equal(2, request.AudioId, "容器里第二条音轨");
            Assert.Equal(2, request.SubtitleId, "外挂字幕排在内封之后");
            Assert.Equal(1, request.ExternalSubtitles.Count);
            Assert.Contains("Subtitles/4", request.ExternalSubtitles[0].AbsoluteUri);
            Assert.Equal(4, request.SubtitleStreamIndex, "上报给服务器的仍是 Emby 的索引");
        });

        Test("计划：续播位置换算成秒，关闭字幕时不上报字幕轨", () =>
        {
            var (planner, _) = Planner();
            var ticket = Ticket() with { StartTicks = 6_000_000_000, SubtitleStreamIndex = 3, SubtitlesDisabled = true };

            var request = planner.Plan(ticket, Connection());

            Assert.Equal(600, request.StartSeconds, "10 分钟 = 600 秒");
            Assert.True(request.SubtitlesDisabled, "用户关掉了字幕");
            Assert.Null(request.SubtitleStreamIndex, "关掉字幕后不能再告诉服务器选了某条字幕");
        });

        Test("计划：默认覆盖 mpv 自身续播，开关打开后交还", () =>
        {
            var (planner, settings) = Planner();
            Assert.True(planner.Plan(Ticket(), Connection()).OverrideMpvResume, "默认由服务器管进度");

            settings.Playback.LetMpvManageResume = true;
            Assert.False(planner.Plan(Ticket(), Connection()).OverrideMpvResume);
        });

        Test("计划：着色器配置组连同 --include 一起给出", () =>
        {
            var include = Path.Combine(Path.GetTempPath(), $"embympvclient-test-{Guid.NewGuid():N}.conf");
            File.WriteAllText(include, "[2K-iGPU]\nglsl-shaders=\"~~/shaders/x.glsl\"\n");

            try
            {
                var (planner, settings) = Planner();
                settings.Shaders.IncludeFile = include;

                var request = planner.Plan(Ticket(), Connection());
                Assert.Equal("2K-iGPU", request.ShaderProfile);
                Assert.Equal(include, request.IncludeFile);

                settings.Shaders.ApplyToAllVideos = false;
                settings.Shaders.AutoAnimeProfile = false;
                var plain = planner.Plan(Ticket(), Connection());
                Assert.Null(plain.ShaderProfile);
                Assert.Null(plain.IncludeFile, "没有配置组就不该让 mpv 多读一个文件");
            }
            finally
            {
                File.Delete(include);
            }
        });

        Test("计划：配置文件不存在时宁可不传 --include", () =>
        {
            var (planner, settings) = Planner();
            settings.Shaders.IncludeFile = Path.Combine(Path.GetTempPath(), "embympvclient-不存在.conf");
            settings.Mpv.ConfigPath = Path.Combine(Path.GetTempPath(), "不存在的目录", "mpv.conf");

            var request = planner.Plan(Ticket(), Connection());
            Assert.Equal("2K-iGPU", request.ShaderProfile, "配置组照样传，用户可能写在 mpv.conf 里");
            Assert.Null(request.IncludeFile, "缺失的 --include 会让 mpv 每次启动都报错");
        });

        Test("计划：轨道建议按偏好语言给出默认值", () =>
        {
            var (planner, settings) = Planner();
            settings.Playback.PreferredAudioLanguage = "jpn";
            settings.Playback.PreferredSubtitleLanguage = "chi";

            var (audio, subtitle) = planner.SuggestTracks(Source());

            Assert.Equal(1, audio!.Index, "日语音轨");
            Assert.Equal(3, subtitle!.Index, "中文字幕");
        });

        Test("计划：时长优先取片源自身的时长", () =>
        {
            var (planner, _) = Planner();
            var request = planner.Plan(Ticket(), Connection());
            Assert.Equal(72_000_000_000, request.RunTimeTicks, "2 小时");
        });

        Test("计划：语言优先级转成 mpv 的 slang/alang", () =>
        {
            var (planner, settings) = Planner();
            settings.Playback.SubtitleLanguagePriority = "Simplified Chinese>Chinese>Traditional Chinese";
            settings.Playback.AudioLanguagePriority = "英语, 日语";

            var request = planner.Plan(Ticket(), Connection());

            Assert.Equal("zh-Hans,zh_hans,zh-CN,zh_CN,zhs,sc,chs,chi-Hans,zh,chi,zho,zh-Hant,zh_hant,zh-TW,zh_TW,zh-HK,zh_HK,zht,tc,chi-Hant",
                request.SubtitleLanguage);
            Assert.Equal("eng,en,jpn,ja", request.AudioLanguage);
        });

        Test("计划：空的语言优先级不传 slang/alang", () =>
        {
            var (planner, settings) = Planner();
            settings.Playback.SubtitleLanguagePriority = "";
            settings.Playback.AudioLanguagePriority = "";
            var request = planner.Plan(Ticket(), Connection());
            Assert.Null(request.SubtitleLanguage);
            Assert.Null(request.AudioLanguage);
        });

        Test("计划：设置的字幕字体存在时传给 mpv，不存在时忽略", () =>
        {
            var (planner, settings) = Planner();
            var missing = Path.Combine(Path.GetTempPath(), $"embympv-{Guid.NewGuid():N}.ttf");
            settings.Playback.SubtitleFontPath = missing;

            var request = planner.Plan(Ticket(), Connection());
            Assert.Null(request.SubtitleFont, "不存在的字体不能带进 --sub-font");

            var real = Path.GetTempFileName();
            try
            {
                settings.Playback.SubtitleFontPath = real;
                var withFont = planner.Plan(Ticket(), Connection());
                Assert.Equal(real, withFont.SubtitleFont);
            }
            finally
            {
                File.Delete(real);
            }
        });

        Test("语言优先级：中文名、英文名与原始 mpv 码混用", () =>
        {
            Assert.Equal("zh,chi,zho", TrackLanguagePriority.ToMpvValue("中文"));
            Assert.Equal("eng,en", TrackLanguagePriority.ToMpvValue("English"));
            Assert.Equal("jpn,ja", TrackLanguagePriority.ToMpvValue("日語")); // 繁体写法也认
            Assert.Equal("yue,zh-HK", TrackLanguagePriority.ToMpvValue("粤语"));
            Assert.Equal("cmn,zh-CN,zh", TrackLanguagePriority.ToMpvValue("普通话"));
            Assert.Equal("jpn,ja,xyz", TrackLanguagePriority.ToMpvValue("日语 > xyz"), "未知名字原样透传");
            Assert.Equal("zh,chi,zho,eng,en", TrackLanguagePriority.ToMpvValue("中文>English"));
            Assert.Equal("zh,chi,zho,eng,en", TrackLanguagePriority.ToMpvValue("中文，English"), "顿号分隔也能拆");
            Assert.Equal("zh,chi,zho,eng,en", TrackLanguagePriority.ToMpvValue("中文→English"), "箭头分隔也能拆");
            Assert.Equal("eng,en", TrackLanguagePriority.ToMpvValue("英语,English"), "重复的码只保留一份");
            Assert.Null(TrackLanguagePriority.ToMpvValue(""));
            Assert.Null(TrackLanguagePriority.ToMpvValue(null));
            Assert.Null(TrackLanguagePriority.ToMpvValue("   "));
        });

        Test("启动参数：语言优先级与字幕字体落到命令行", () =>
        {
            var request = Request() with
            {
                SubtitleLanguage = "zh,chi",
                AudioLanguage = "jpn",
                SubtitleFont = @"C:\Windows\Fonts\msyh.ttc"
            };

            var line = Line(MpvArgumentBuilder.Build(request));
            Assert.Contains("--slang=zh,chi", line);
            Assert.Contains("--alang=jpn", line);
            Assert.Contains("--sub-font=C:\\Windows\\Fonts\\msyh.ttc", line);
        });
    }

    // ---- 测试用数据 ------------------------------------------------------------

    private static (PlaybackPlanner Planner, AppSettings Settings) Planner()
    {
        var settings = new AppSettings();
        return (new PlaybackPlanner(settings, new ShaderProfileResolver(settings.Shaders)), settings);
    }

    private static (MediaStream? Audio, MediaStream? Subtitle) Planner_SuggestTracks(this PlaybackPlanner planner, MediaSource source) =>
        planner.SuggestTracks(source);

    private static EmbyConnection Connection() => new(
        new Uri("http://192.168.31.230:8896/emby/"),
        "token-abc",
        "user-1",
        "我",
        "果服",
        DeviceIdentity.Create("device-1", "2.0.0"));

    private static PlaybackTicket Ticket() => new()
    {
        Item = Item("某部电影", id: "42"),
        Source = Source(),
        StartTicks = 0
    };

    /// <summary>1080p 电影：日/中双音轨、一条内封 ASS、一条外挂 SRT、一条无法使用的外挂 PGS。</summary>
    private static MediaSource Source() => new()
    {
        Id = "src1",
        Container = "mkv",
        RunTimeTicks = 72_000_000_000,
        MediaStreams =
        [
            Stream(0, "Video", height: 1080),
            Stream(1, "Audio", language: "jpn"),
            Stream(2, "Audio", language: "chi"),
            Stream(3, "Subtitle", codec: "ass", language: "chi"),
            Stream(4, "Subtitle", codec: "srt", language: "chi", external: true),
            Stream(5, "Subtitle", codec: "pgssub", language: "eng", external: true)
        ]
    };

    private static MediaSource Source1080p() => SourceWith(Stream(0, "Video", height: 1080));

    private static MediaSource Source4K() => SourceWith(Stream(0, "Video", height: 2160));

    private static ShaderAutomationSettings Shaders(bool all, bool anime) => new()
    {
        ApplyToAllVideos = all,
        AutoAnimeProfile = anime
    };

    private static EmbyItem Item(
        string name,
        string id = "1",
        string type = EmbyItemType.Movie,
        List<string>? genres = null,
        List<string>? tags = null) =>
        new()
        {
            Id = id,
            Name = name,
            Type = type,
            Genres = genres ?? [],
            Tags = tags ?? []
        };

    private static PlaybackRequest Request(
        double start = 0,
        string? includeFile = null,
        string? profile = null,
        bool overrideResume = true) => new()
    {
        MediaUrl = new Uri("http://server/emby/Videos/1/stream.mkv?Static=true"),
        Title = "片名",
        StartSeconds = start,
        IncludeFile = includeFile,
        ShaderProfile = profile,
        OverrideMpvResume = overrideResume
    };

    private static MediaSource SourceWith(params MediaStream[] streams) =>
        new() { Id = "src1", Container = "mkv", MediaStreams = [.. streams] };

    private static MediaStream Stream(
        int index,
        string type,
        string? language = null,
        string? displayLanguage = null,
        string? codec = null,
        int? height = null,
        bool external = false,
        bool forced = false) =>
        new()
        {
            Index = index,
            Type = type,
            Language = language,
            DisplayLanguage = displayLanguage,
            Codec = codec,
            Height = height,
            IsExternal = external,
            IsForced = forced
        };

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Line(IEnumerable<string> arguments) => string.Join(' ', arguments);

    private static int IndexOfPrefix(IReadOnlyList<string> arguments, string prefix)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].StartsWith(prefix, StringComparison.Ordinal)) return index;
        }

        return -1;
    }
}
