using System.Text;
using System.Text.RegularExpressions;
using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Mpv;
using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

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
        RegisterTrackDisplay();
        RegisterArguments();
        RegisterShaders();
        RegisterIpc();
        RegisterPlanner();
        RegisterTrackSelection();
        RegisterOutputOptions();
        RegisterSkipSections();
        RegisterChapterTimeline();
        RegisterSkipCoordinator();
        RegisterChromeReveal();
        RegisterPictureTap();
        RegisterCursorMask();
        RegisterPulseArt();
        RegisterPlaybackStats();
        RegisterAspectLock();
        RegisterPlayerMenu();
        RegisterEpisodeNavigation();
        RegisterPlaybackGate();
    }

    // ---- 启动命令文本 ----------------------------------------------------------

    // The 「附加参数拆分」 tests that used to sit here are gone with the setting itself: CommandLine.Split
    // no longer exists, and the class only has to render a list for the log now.
    private static void RegisterCommandLine()
    {
        Test("启动命令文本：只给含空格的参数加引号", () =>
        {
            var text = CommandLine.Describe(["--vo=gpu-next", "--title=我的 标题", "--fullscreen"]);
            Assert.Equal("""--vo=gpu-next "--title=我的 标题" --fullscreen""", text);
        });

        Test("启动命令文本：空参数也要引号，否则读日志时会消失", () =>
        {
            Assert.Equal("""--a "" --b""", CommandLine.Describe(["--a", "", "--b"]));
            Assert.Equal("", CommandLine.Describe([]));
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

    // ---- 轨道显示信息 ----------------------------------------------------------

    private static void RegisterTrackDisplay()
    {
        Test("轨道显示：语言代码译成中文名", () =>
        {
            Assert.Equal("普通话", TrackLanguagePriority.Describe("cmn"));
            Assert.Equal("英语", TrackLanguagePriority.Describe("en"));
            Assert.Equal("中文", TrackLanguagePriority.Describe("zh"));
            Assert.Equal("简体中文", TrackLanguagePriority.Describe("zh-Hans"));
            Assert.Equal("匈牙利语", TrackLanguagePriority.Describe("hu"), "优先级列表里没有的语言也要有名字");
            Assert.Equal("葡萄牙语", TrackLanguagePriority.Describe("pt-BR"), "地区后缀不该让语言变成两个字母");
            Assert.Equal("", TrackLanguagePriority.Describe(null));
            Assert.Equal("qqq", TrackLanguagePriority.Describe("qqq"), "认不出来也要把原文留着");
        });

        Test("轨道显示：音轨列出语言、标题、编码、声道与码率", () =>
        {
            var track = new MpvTrack(1, "audio", "cmn", "国语配音", Default: true, Selected: true)
            {
                Codec = "eac3",
                Channels = "5.1(side)",
                ChannelCount = 6,
                SampleRate = 48000,
                BitRate = 640000
            };

            Assert.Equal("普通话 · 国语配音（默认）", track.DisplayName);
            Assert.Equal("E-AC3 · 5.1 声道 · 640 kbps", track.DisplayDetail);
            Assert.Equal("普通话", track.DisplayLabel, "收起来的按钮上只放得下语言");
        });

        Test("轨道显示：缺少的字段不留下空的分隔符", () =>
        {
            var bare = new MpvTrack(2, "audio", null, null, Default: false, Selected: false);
            Assert.Equal("", bare.DisplayName, "什么都不知道时交给调用方写「轨道 n」");
            Assert.Equal("", bare.DisplayDetail);

            var codecOnly = new MpvTrack(3, "audio", "en", null, false, false) { Codec = "aac", ChannelCount = 2 };
            Assert.Equal("英语", codecOnly.DisplayName);
            Assert.Equal("AAC · 立体声", codecOnly.DisplayDetail, "没有码率就不写码率");

            var rateOnly = new MpvTrack(4, "audio", "en", null, false, false) { Codec = "flac", ChannelCount = 2, SampleRate = 44100 };
            Assert.Equal("FLAC · 立体声 · 44.1 kHz", rateOnly.DisplayDetail, "没有码率时退回采样率");
        });

        Test("轨道显示：标题与语言重复时只写一次", () =>
        {
            var track = new MpvTrack(1, "audio", "eng", "English", false, false) { Codec = "ac3", ChannelCount = 2 };
            Assert.Equal("英语", track.DisplayName, "标题只是语言名的另一种写法，不必再写一遍");
        });

        Test("轨道显示：字幕标出格式与强制、外挂、听障", () =>
        {
            var forced = new MpvTrack(1, "sub", "zh", "简体", false, false)
            {
                Codec = "subrip",
                Forced = true
            };
            Assert.Equal("中文 · 简体（强制）", forced.DisplayName);
            Assert.Equal("SRT", forced.DisplayDetail);

            var external = new MpvTrack(2, "sub", "hu", null, false, false)
            {
                Codec = "ass",
                External = true,
                HearingImpaired = true
            };
            Assert.Equal("匈牙利语（听障）（外挂）", external.DisplayName);
            Assert.Equal("ASS", external.DisplayDetail);

            var picture = new MpvTrack(3, "sub", "eng", null, false, false)
            {
                Codec = "hdmv_pgs_subtitle",
                Image = true
            };
            Assert.Equal("PGS · 图形", picture.DisplayDetail, "图形字幕改不了字体，界面要说清楚");
        });

        Test("轨道显示：认不出的编码原样大写", () =>
        {
            var track = new MpvTrack(1, "audio", null, "备用", false, false) { Codec = "pcm_s24le", ChannelCount = 1 };
            Assert.Equal("PCM · 单声道", track.DisplayDetail, "pcm 的各种写法都归 PCM");

            var unknown = new MpvTrack(2, "audio", null, "备用", false, false) { Codec = "wavpack", ChannelCount = 8 };
            Assert.Equal("WAVPACK · 7.1 声道", unknown.DisplayDetail, "没见过的编码名本身也是信息");
        });
    }

    // ---- 启动参数 --------------------------------------------------------------

    private static void RegisterArguments()
    {
        Test("启动参数：设置项按给定顺序排列，最后一个说了算", () =>
        {
            // 附加参数 is gone as of v5, so the only thing that can override a setting is another entry
            // later in PlayerOptions — which is exactly how 画质预设 → 视频输出 → 着色器配置组 is layered.
            var request = Request() with
            {
                PlayerOptions = [new("vo", "gpu-next"), new("hwdec", "auto-safe"), new("hwdec", "no")]
            };

            var arguments = MpvArgumentBuilder.Build(request);
            var first = IndexOfPrefix(arguments, "--hwdec=auto-safe");
            var last = IndexOfPrefix(arguments, "--hwdec=no");

            Assert.Contains("--vo=gpu-next", Line(arguments));
            Assert.True(first >= 0 && last > first,
                $"mpv 取最后一次出现的值，顺序必须原样保留（实际 {first} / {last}）");
        });

        Test("启动参数：不再往命令行末尾追加手写参数", () =>
        {
            var line = Line(MpvArgumentBuilder.Build(Request()));
            Assert.DoesNotContain("--fullscreen", line, "附加参数已删除，不该再有任何来源不明的参数");
        });

        Test("启动参数：--no-config 排在最前，且不再有 --include / --profile", () =>
        {
            var arguments = MpvArgumentBuilder.Build(Request());

            Assert.Equal("--no-config", arguments[0],
                "必须是第一个参数：后面的选项才不会因为配置文件已经写过而变成空操作");

            var line = Line(arguments);
            Assert.DoesNotContain("--include=", line, "配置文件已经不读了，--include 也一起退役");
            Assert.DoesNotContain("--profile=", line, "着色器配置组现在是普通 mpv 选项，不再是 mpv.conf 里的 profile");
            Assert.DoesNotContain("--config-dir=", line);
        });

        Test("启动参数：始终显式给出 --start 并关掉 mpv 自身续播", () =>
        {
            var fromStart = Line(MpvArgumentBuilder.Build(Request()));
            Assert.Contains("--start=0", fromStart, "「从头播放」不能变成「从 mpv 上次停下的地方播放」");
            Assert.Contains("--resume-playback=no", fromStart);
            Assert.Contains("--save-position-on-quit=no", fromStart);

            Assert.Contains("--start=1234.5", Line(MpvArgumentBuilder.Build(Request(start: 1234.5))));
        });

        Test("启动参数：必须让 mpv 在播完后退出", () =>
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
                    new("X-Emby-Authorization", """MediaBrowser Client="EmbyNian", Device="PC" """)
                ]
            });

            var appends = arguments.Count(argument => argument.StartsWith("--http-header-fields-append=", StringComparison.Ordinal));
            Assert.Equal(2, appends, "每个请求头一条 -append，含逗号的值才不会被拆成两条");

            var separator = arguments.ToList().IndexOf("--");
            Assert.True(separator >= 0, "URL 前必须有 --");
            Assert.Equal(arguments.Count - 1, separator + 1, "URL 必须是最后一个参数");
            Assert.Contains("Videos/1/stream.mkv", arguments[^1]);
        });

        Test("启动参数：设置项插在 URL 之前", () =>
        {
            var arguments = MpvArgumentBuilder.Build(Request() with
            {
                PlayerOptions = [new("fullscreen", "yes"), new("volume", "80")]
            });

            var separator = arguments.ToList().IndexOf("--");
            Assert.True(arguments.ToList().IndexOf("--fullscreen=yes") < separator, "选项不能落到 -- 之后被当成文件");
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
            Assert.Contains(@"--input-ipc-server=\\.\pipe\embynian-x",
                Line(MpvArgumentBuilder.Build(Request(), @"\\.\pipe\embynian-x")));
        });

        Test("IPC 管道名：每次启动都不同，避免撞上手动开的 mpv", () =>
        {
            var first = MpvIpcClient.CreatePipeName();
            Assert.False(first == MpvIpcClient.CreatePipeName(), "管道名必须唯一");
            Assert.Equal($@"\\.\pipe\{first}", MpvIpcClient.ToPipePath(first));
            Assert.DoesNotContain("mpvsocket", first, "不能用 mpv 惯用的那个固定管道名");
        });
    }

    // ---- 着色器档位 ------------------------------------------------------------

    private static void RegisterShaders()
    {
        RegisterUpscaleTier();
        RegisterOutputWatch();

        Test("着色器：关掉开关就一条链都不上", () =>
        {
            var settings = Shaders(enabled: false);
            Assert.Null(settings.Resolve(true, 1920, 1080, 2560, 1440).Group);
            Assert.Null(settings.Resolve(false, 1920, 1080, 2560, 1440).Group);
        });

        Test("着色器：档位由放大倍数挑，不再由片源分辨率挑", () =>
        {
            var settings = Shaders();

            // 同一个 1080p 片源，屏幕不同就该落在不同的档 —— 这正是从前那套规则表达不了的事。
            Assert.Equal("live-slight", settings.Resolve(false, 1920, 1080, 2560, 1440).Group?.Id, "1080p 上 1440p 是 1.33 倍");
            Assert.Equal("live-sweet", settings.Resolve(false, 1920, 1080, 3840, 2160).Group?.Id, "同一个片源上 4K 是 2 倍");
            Assert.Equal("live-shrink", settings.Resolve(false, 1920, 1080, 1920, 1080).Group?.Id, "原尺寸窗口里一个放大器都不该有");
            Assert.Equal("live-shrink", settings.Resolve(false, 3840, 2160, 2560, 1440).Group?.Id, "4K 上 1440p 全程在缩小");
            Assert.Equal("live-large", settings.Resolve(false, 720, 480, 2560, 1440).Group?.Id, "480p 上 1440p 是 3 倍");
        });

        Test("着色器：动画走另外半张表，开关关掉就照实拍处理", () =>
        {
            Assert.Equal("anime-slight", Shaders().Resolve(true, 1920, 1080, 2560, 1440).Group?.Id);
            Assert.Equal("live-slight", Shaders(anime: false).Resolve(true, 1920, 1080, 2560, 1440).Group?.Id,
                "自动识别动画关掉之后，动画片也走实拍那一半");
        });

        Test("着色器：手动指定压过自动，而且换显卡档还在", () =>
        {
            var low = Shaders(manual: "anime-large");
            Assert.Equal("anime-large", low.Resolve(false, 1920, 1080, 2560, 1440).Group?.Id, "手动指定压过 1.33 倍算出来的那一档");

            // 手动指定存的是「哪一行」，显卡档换的是「哪一列」—— 所以改显卡档不会把这个选择弄丢。
            var high = Shaders(manual: "anime-large", gpu: GpuTier.High);
            Assert.Equal("anime-large", high.Resolve(false, 1920, 1080, 2560, 1440).Group?.Id);
            Assert.False(
                string.Equals(
                    low.Resolve(false, 1920, 1080, 2560, 1440).Group!.Description,
                    high.Resolve(false, 1920, 1080, 2560, 1440).Group!.Description,
                    StringComparison.Ordinal),
                "同一个 id 在两个显卡档下挂的链应该不一样");

            Assert.Equal("live-slight", Shaders(manual: "这个档位并不存在").Resolve(false, 1920, 1080, 2560, 1440).Group?.Id,
                "认不出来的 id 退回自动，而不是退回「不上着色器」");
        });

        Test("着色器：8K 片源直接关掉着色器", () =>
        {
            var settings = Shaders();
            Assert.Null(settings.Resolve(false, 7680, 4320, 2560, 1440).Group,
                "8K 解码本身就吃满核显，再叠着色器只会卡");

            settings.DisableForUltraHighRes = false;
            Assert.Equal("live-shrink", settings.Resolve(false, 7680, 4320, 2560, 1440).Group?.Id,
                "关掉这条特例后照常按倍数走，而 8K 上 1440p 是在缩小");
        });

        Test("着色器：动画判定只看类型/风格与标签", () =>
        {
            var resolver = new ShaderGroupResolver(Shaders());

            Assert.True(resolver.LooksAnimated(ShaderGroupResolver.StyleHints(
                Item("紫罗兰永恒花园", genres: ["动画", "剧情"]))), "Genres 命中");
            Assert.True(resolver.LooksAnimated(ShaderGroupResolver.StyleHints(
                Item("某部片", tags: ["番剧"]))), "Tags 命中");
            Assert.True(resolver.LooksAnimated(ShaderGroupResolver.StyleHints(
                Item("某部片", genres: ["Animation"]))), "英文关键词大小写无关");
            Assert.False(resolver.LooksAnimated(ShaderGroupResolver.StyleHints(
                Item("动画简史", genres: ["纪录片"]))), "标题带「动画」的纪录片不能被误判");
        });

        Test("着色器：单集借用剧集的类型/风格", () =>
        {
            var resolver = new ShaderGroupResolver(Shaders());
            var episode = Item("第 1 集", type: EmbyItemType.Episode);
            var series = Item("葬送的芙莉莲", type: EmbyItemType.Series, genres: ["动画"]);

            Assert.Equal("live-slight", resolver.Resolve(episode, Source1080p(), null, (2560, 1440)).Group?.Id,
                "单集自己通常没有风格");
            Assert.Equal("anime-slight", resolver.Resolve(episode, Source1080p(), series, (2560, 1440)).Group?.Id,
                "有剧集兜底时应命中动画那一半");
        });

        Test("着色器：决策原因写成一行，任务书 3.7 那几样都在", () =>
        {
            var resolver = new ShaderGroupResolver(Shaders());

            var decision = resolver.Resolve(Item("某部电影"), Source1080p(), null, (2560, 1440));
            Assert.NotNull(decision.Group);

            // 例：1.33× · 微放大档 · 真人 · 低档 · 输出 2560×1440 · ravu-zoom-ar-r2 + CfL_Prediction_Lite
            Assert.Contains("1.33×", decision.Reason);
            Assert.Contains("微放大档", decision.Reason);
            Assert.Contains("真人", decision.Reason);
            Assert.Contains("低档", decision.Reason);
            Assert.Contains("输出 2560×1440", decision.Reason);
            Assert.Contains("ravu-zoom-ar-r2", decision.Reason, "链上的文件名要写出来，否则掉帧的反馈没法用");

            var unknown = resolver.Resolve(Item("某部电影"), Source1080p(), null);
            Assert.Contains("输出尺寸未知", unknown.Reason, "问不出屏幕尺寸也要说清楚，不能装作量过");
        });

        Test("着色器：1:1 播放时那一行写「原生」而不是「缩小档」", () =>
        {
            // 撤掉第五档之后剩下的就是这个标签：SSimDownscaler 自带门控，1.00 倍时挂着不花钱，
            // 别扭的只有 OSD 上「1.00× · 缩小档」这句话。
            var decision = new ShaderGroupResolver(Shaders()).Resolve(Item("某部电影"), Source1080p(), null, (1920, 1080));

            Assert.Equal("live-shrink", decision.Group?.Id);
            Assert.Contains("1.00×", decision.Reason);
            Assert.Contains("原生", decision.Reason);
            Assert.DoesNotContain("缩小档", decision.Reason);
        });

        Test("着色器：老片源是另一根轴，可以单独关掉", () =>
        {
            // PAL 的 DVD 放到 1080p 是 1.88 倍，落甜点档 —— 而它照样要去带。这是把去带塞进大倍数档时
            // 那个 bug 的回归测试：同一张碟的 NTSC 版（480 线、2.25 倍）落大倍数，PAL 版一条都没有。
            var pal = Shaders().Resolve(false, 720, 576, 1920, 1080);
            Assert.Equal("live-sweet", pal.Group?.Id);
            Assert.True(pal.Group!.Vintage, "576 线是老片源");
            Assert.Contains("hdeband", pal.Group.Description);
            Assert.Contains("老片源修复", pal.Reason);

            var ntsc = Shaders().Resolve(false, 720, 480, 1920, 1080);
            Assert.Equal("live-large", ntsc.Group?.Id);
            Assert.Contains("hdeband", ntsc.Group!.Description, "同一张碟的另一个区，处理必须一样");

            var off = Shaders(vintage: false).Resolve(false, 720, 576, 1920, 1080);
            Assert.False(off.Group!.Vintage);
            Assert.DoesNotContain("hdeband", off.Group.Description);

            var modern = Shaders().Resolve(false, 1280, 720, 1920, 1080);
            Assert.False(modern.Group!.Vintage, "720p 不是老片源，哪怕它也在放大");
        });

        Test("着色器：「这是哪种片子」只有一个写手，菜单和自动挑用的是同一个答案", () =>
        {
            // 播放器 ⚙ 菜单要照这次播的文件列那八行 —— 一张 DVD 的行里带 hdeband，一部 60fps 的番在动画那三行上
            // 也是 ravu。它从前是拿「当前生效的那条链」反推这两根轴的，可着色器关掉、8K 片源、开播前那一刻都没有
            // 当前链，于是退回「不是老片源、不是高帧率」那一列：点一下动画微放大，60fps 的片子拿到 ArtCNN，正是
            // 高帧率这根轴专门要挡住的那条（一帧 22 毫秒），而且一点就钉住一整部片子。
            // 所以规则收在 Kind 一处，这一条钉的就是「Resolve 和菜单读的是同一句话」。
            (int Height, double Fps, bool Vintage, bool FastMotion)[] rows =
            [
                (1080, 23.976, false, false),
                (576, 23.976, true, false),
                (1080, 59.94, false, true),
                (480, 59.94, true, true),
                (0, 0, false, false)
            ];

            foreach (var row in rows)
            {
                var settings = Shaders();
                Assert.Equal((row.Vintage, row.FastMotion), settings.Kind(row.Height, row.Fps),
                    $"{row.Height} 线 / {row.Fps}fps");

                // 和真正挑链那条路对齐：同一个文件，Kind 说的两根轴必须就是链身上那两根。8K 和关掉着色器
                // 那两种情况没有链可比，所以这里只走有链的组合。
                var chain = settings.Resolve(true, 1920, row.Height, 2560, 1440, row.Fps).Group!;
                Assert.Equal(row.Vintage, chain.Vintage, $"{row.Height} 线：老片源这根轴两边要一致");
                Assert.Equal(row.FastMotion, chain.FastMotion, $"{row.Fps}fps：高帧率这根轴两边要一致");
            }

            // 老片源修复关掉之后，Kind 也得跟着说「不是老片源」—— 否则菜单会去列一张这次根本不会用的表。
            Assert.Equal((false, false), Shaders(vintage: false).Kind(576, 23.976));
            Assert.Equal((false, true), Shaders(vintage: false).Kind(576, 59.94), "高帧率那根轴不受它管");
        });

        Test("着色器：动画判定与用了哪半张表无关", () =>
        {
            // 去色带 =「在动画中开启」读的是这个标记，所以它必须是「这部片是不是动画」，而不是
            // 「这次用了动画那半张表吗」—— 否则关掉自动识别动画就会连带把去色带也关了。
            var resolver = new ShaderGroupResolver(Shaders(anime: false));
            var decision = resolver.Resolve(Item("紫罗兰永恒花园", genres: ["动画"]), Source1080p(), null, (2560, 1440));

            Assert.True(decision.Animated, "自动识别动画关着，但这部片仍然是动画");
            Assert.Equal("live-slight", decision.Group?.Id, "开关关着就不该换到动画那一半");

            var live = new ShaderGroupResolver(Shaders())
                .Resolve(Item("某部电影", genres: ["剧情"]), Source1080p(), null, (2560, 1440));
            Assert.False(live.Animated);
        });

        Test("着色器档位：九十六格每一格都有链，路径是程序目录下的绝对路径", () =>
        {
            Assert.Equal(96, ShaderGroupCatalog.All.Count, "三个显卡档 × 八个档位 × 老片源与否 × 高帧率与否");

            foreach (var group in ShaderGroupCatalog.All)
            {
                Assert.True(group.Shaders.Count > 0, $"{group.Name} 一个着色器都没有");

                var options = Options(group.ToMpvOptions(ShaderGroupCatalog.ShaderRoot));
                Assert.True(options.ContainsKey("glsl-shaders"), $"{group.Name} 没给出 glsl-shaders");
                Assert.True(options.ContainsKey("scale"), $"{group.Name} 没给出 scale，会沿用上一部片子的设置");
                Assert.True(options.ContainsKey("cscale"), $"{group.Name} 没给出 cscale");

                foreach (var path in options["glsl-shaders"].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    Assert.True(Path.IsPathRooted(path), $"{path} 不是绝对路径；mpv 用 --no-config 启动，~~/ 已经无处可解析");
                }
            }
        });

        Test("着色器档位：八个 id 稳定、不重复，而且换显卡档、换老片源、换高帧率都是同一批 id", () =>
        {
            string[] expected =
            [
                "live-shrink", "live-slight", "live-sweet", "live-large",
                "anime-shrink", "anime-slight", "anime-sweet", "anime-large"
            ];

            Assert.Equal(string.Join("、", expected), string.Join("、", ShaderGroupCatalog.Ids), "档位 id 和次序");

            foreach (var gpu in new[] { GpuTier.Low, GpuTier.Medium, GpuTier.High })
            {
                foreach (var vintage in new[] { false, true })
                {
                    foreach (var fast in new[] { false, true })
                    {
                        var column = ShaderGroupCatalog.For(gpu, vintage, fast);
                        Assert.Equal(8, column.Count, $"{gpu}／老片源={vintage}／高帧率={fast} 这一列应该正好八格");
                        Assert.Equal(
                            string.Join("、", expected),
                            string.Join("、", column.Select(group => group.Id)),
                            "id 必须跨显卡档、老片源和高帧率一致，否则改一下设置或者换一部片子就会把用户手动指定的那一档弄丢");
                    }
                }
            }
        });

        Test("着色器档位：关掉一条链时把每个它动过的选项都还回去", () =>
        {
            var neutral = Options(ShaderGroupCatalog.NeutralOptions);

            Assert.Equal("", neutral["glsl-shaders"], "不清空的话着色器会一直挂着");
            Assert.Equal("lanczos", neutral["scale"], "mpv --no-config --list-options 报的默认值");
            Assert.Equal("hermite", neutral["dscale"]);
            Assert.Equal("", neutral["cscale"]);

            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in ShaderGroupCatalog.All)
            {
                foreach (var (name, _) in group.ToMpvOptions(ShaderGroupCatalog.ShaderRoot))
                {
                    touched.Add(name);
                    Assert.True(neutral.ContainsKey(name), $"{group.Name} 改了 {name}，但关闭时没有还原它");
                }
            }

            // 反过来也要对上：还原表里没人会设的名字，每次切档都白写一遍。移植的九组删掉时
            // scale-antiring、dscale-antiring、linear-upscaling 就是这样留下来的。
            foreach (var (name, _) in ShaderGroupCatalog.NeutralOptions)
            {
                Assert.True(touched.Contains(name), $"还原表里的 {name} 没有任何档位会动，切一次档就白写一遍");
            }
        });

        Test("着色器档位：A→B→A 和 B→A→B 都不留残渣", () =>
        {
            // 走的是 PlaybackService.SetShaderGroupAsync 真正调的那个函数（ShaderSwitch.Options），不是照它
            // 重写一遍 —— 重写一遍的测试会在真代码漂移之后照旧通过。少一个还原名字，B 设过的东西就会在切回 A
            // 之后留一整个文件。
            KeyValuePair<string, string>[] launch =
            [
                new("deband", "yes"),
                new("scale", "spline36")
            ];

            var a = ShaderGroupCatalog.Resolve(false, UpscaleTier.Shrink, GpuTier.Low);
            var onlyA = Live(launch, [a]);

            foreach (var b in ShaderGroupCatalog.All)
            {
                Assert.Equal(Join(onlyA), Join(Live(launch, [a, b, a])), $"A→{b.Name}→A 之后和只上过 A 不一样");
                Assert.Equal(Join(Live(launch, [b])), Join(Live(launch, [b, a, b])), $"{b.Name}→A→{b.Name} 之后不一样");
            }

            // 关掉着色器也算一档：文件清空、去色带回到启动时那个 yes。
            var off = Live(launch, [a, null]);
            Assert.Equal("", off["glsl-shaders"], "关掉之后着色器不许还挂着");
            Assert.Equal("yes", off["deband"], "回落的是本次启动的值，不是 mpv 出厂值");

            static Dictionary<string, string> Live(
                IReadOnlyList<KeyValuePair<string, string>> launch,
                IReadOnlyList<ShaderGroup?> sequence)
            {
                var live = Options(launch);

                foreach (var group in sequence)
                {
                    // 每次切换交给 mpv 的是「还原表里的每个名字 + 新链自己的」，而屏上那台 mpv 保留的是上一次
                    // 之后的全部状态 —— 所以这里往 live 上叠，而不是每次从头来。
                    foreach (var (name, value) in ShaderSwitch.Options(launch, 0, group, @"C:\shaders"))
                        live[name] = value;
                }

                return live;
            }

            static string Join(Dictionary<string, string> live) =>
                string.Join("\n", live.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        });

        Test("着色器档位：九十六格每一格都过得了那套规则", () =>
        {
            // 三条互斥、缩小档不许有放大器、每格都要有色度重建、hdeband 与内置 deband 互斥、每个着色器的运行
            // 前置条件都落在了选项里、链照 mpv 真正的执行次序写 —— 全在 ShaderChainRules 里，判的是 Descriptor
            // 上的逻辑职责，不是数文件名。这一条红了，报告里直接写出是哪一格哪一条。
            Assert.Equal("", string.Join("\n", ShaderChainRules.ProblemsInTable()));
        });

        Test("着色器档位：说自己是放大器就必须真的能改尺寸（FSRCNNX_x1 那一类错的机械闸门）", () =>
        {
            foreach (var shader in ShaderLibrary.All.Where(shader => shader.Role == ShaderRole.LumaUpscale))
                Assert.True(shader.ChangesResolution, $"{shader.Name} 挂着亮度放大器的职责，却没有声明输出尺寸");

            // 反过来验一次这条闸门真的咬得住：编一个「说是放大器、其实不改尺寸」的描述塞进一条链里，规则必须报。
            var fake = new ShaderDescriptor(
                "FSRCNNX_x1", "igv/FSRCNNX_x1.glsl", ShaderRole.LumaUpscale,
                Hook: "LUMA", ChangesResolution: false, Passes: 1, Gate: "", ReadsLuma: false, ReadsChroma: false);

            var broken = new ShaderGroup(false, UpscaleTier.Sweet, false, false, [fake, ShaderLibrary.ChromaLite], []);
            Assert.Contains("放不大任何东西", string.Join("\n", ShaderChainRules.Problems(broken)));
        });

        Test("着色器档位：光域这一项跟着链里有没有人要求它走，如今哪一格都没人要求", () =>
        {
            foreach (var group in ShaderGroupCatalog.All)
            {
                var wantsOff = group.Shaders.Any(shader =>
                    shader.Requires.Any(pair => pair.Key == "sigmoid-upscaling" && pair.Value == "no"));

                Assert.Equal(wantsOff ? "no" : "yes", Options(group.Options)["sigmoid-upscaling"],
                    $"{group.Name}：光域这一项只该跟着链里有没有人要求它走");

                // Anime4K Mode A 那两个 CNN pass 是唯一要求关掉它的，2026-09-04 随「动画大倍数也换 ArtCNN」出箱。
                // 所以现在每一格都该是 mpv 的出厂值；谁再往箱子里放一个要求关光域的着色器，这一条当场红，而那正
                // 是需要有人想一想的时刻 —— 关光域会改变整条链的放大观感，不是一个文件自己的事。
                Assert.False(wantsOff, $"{group.Name}：现在没有一个着色器要求关掉 sigmoid-upscaling");
            }
        });

        Test("着色器档位：hdeband 只在老片源那一半，排最前面，nlmeans 只在中高档", () =>
        {
            // 走三重循环而不是拿 Contains 反查，是因为 96 格里同一条链会在两根「跟着文件走」的轴上各出现一次，
            // 「不在低档那一列里」不再等于「不是低档」。
            foreach (var gpu in new[] { GpuTier.Low, GpuTier.Medium, GpuTier.High })
            {
                foreach (var vintage in new[] { false, true })
                {
                    foreach (var fast in new[] { false, true })
                    {
                        foreach (var group in ShaderGroupCatalog.For(gpu, vintage, fast))
                        {
                            var deband = group.Shaders.Any(shader => shader.Role == ShaderRole.Deband);
                            Assert.Equal(vintage, deband, $"{group.Name}：去带跟的是片源有多老，不是要放多大");

                            if (!deband) continue;

                            Assert.Equal("hdeband", group.Shaders[0].Name, $"{group.Name}：去带要在最前面");
                            Assert.Equal(gpu != GpuTier.Low, group.Packages(ShaderRole.Denoise) > 0,
                                $"{group.Name}（{gpu}）：低档只加去带，降噪要到中高档");
                        }
                    }
                }
            }
        });

        Test("着色器：高帧率片源把动画那三档换成便宜的链，档位名字不变", () =>
        {
            // 2026-09-04 实测：同一条 ArtCNN_C4F16 + CfL 的链，1080p 放到 2560×1440，在 vulkan 上 22 毫秒一帧
            // （45 fps 上限），所以 24fps 的番占掉大约一半显卡，而 60fps 的片子要每秒 1.33 秒的显卡时间 —— 换不了。
            // ravu-zoom 那条是 6.6 毫秒，什么帧率都够。
            var settings = Shaders();

            var normal = settings.Resolve(true, 1920, 1080, 2560, 1440, 23.976);
            var fast = settings.Resolve(true, 1920, 1080, 2560, 1440, 59.94);

            Assert.Equal("anime-slight", normal.Group?.Id);
            Assert.Equal("anime-slight", fast.Group?.Id, "id 跟的是「这是动画、这是微放大」，不是「链换没换」");
            Assert.True(fast.Group!.Animated, "片子还是动画，不能因为帧率高就说它是真人");
            Assert.True(fast.Group.FastMotion);
            Assert.False(normal.Group!.FastMotion);

            Assert.Contains("ArtCNN", normal.Group.Description);
            Assert.DoesNotContain("ArtCNN", fast.Group.Description, "60fps 上 CNN 放大器要退场");
            Assert.Equal(
                ShaderGroupCatalog.Resolve(false, UpscaleTier.Slight, GpuTier.Low).Description,
                fast.Group.Description,
                "退场之后走的就是真人那一格的链");

            Assert.Contains("高帧率片源", fast.Reason, "屏上那一行要说清为什么链变了");
            Assert.DoesNotContain("高帧率片源", normal.Reason);

            // 三个档都要换，而缩小档两半本来就一样，所以那一格不该多出一句解释。
            foreach (var tier in new[] { UpscaleTier.Slight, UpscaleTier.Sweet, UpscaleTier.Large })
            {
                Assert.Equal(
                    ShaderGroupCatalog.Resolve(false, tier, GpuTier.Low).Description,
                    ShaderGroupCatalog.Resolve(true, tier, GpuTier.Low, fastMotion: true).Description,
                    $"{tier}：高帧率的动画走真人那条链");
            }

            var shrink = settings.Resolve(true, 3840, 2160, 2560, 1440, 59.94);
            Assert.Equal("anime-shrink", shrink.Group?.Id);
            Assert.DoesNotContain("高帧率片源", shrink.Reason, "缩小档两半本来就是同一条链，没什么可解释的");

            // 中高档也一样降级：那两列用的是 C4F32，算术量是 C4F16 的四倍，所以「卡快四倍」刚好抵平，60fps 还要
            // 再多两倍半。这台机器上验不了那两列，但比例是算得出来的。
            foreach (var gpu in new[] { GpuTier.Medium, GpuTier.High })
            {
                Assert.DoesNotContain("ArtCNN",
                    ShaderGroupCatalog.Resolve(true, UpscaleTier.Sweet, gpu, fastMotion: true).Description,
                    $"{gpu}：高帧率片源同样不上 CNN");
            }
        });

        Test("着色器：帧率这根轴的边界，29.97 要留在近侧", () =>
        {
            Assert.False(ShaderTier.IsFastMotion(0), "问不出帧率不是放弃好链的理由");
            Assert.False(ShaderTier.IsFastMotion(23.976));
            Assert.False(ShaderTier.IsFastMotion(25), "PAL");
            Assert.False(ShaderTier.IsFastMotion(29.97), "NTSC 必须留在近侧，否则半个美剧库都降级");
            Assert.False(ShaderTier.IsFastMotion(30));
            Assert.True(ShaderTier.IsFastMotion(50));
            Assert.True(ShaderTier.IsFastMotion(59.94));

            // 帧率读的是片源的视频轨，走的是真实那条路（ShaderGroupResolver → MediaStream.FrameRate），
            // 不是测试自己再算一遍。
            var resolver = new ShaderGroupResolver(Shaders());
            var series = Item("某部番", type: EmbyItemType.Series, genres: ["动画"]);
            var episode = Item("第 1 集", type: EmbyItemType.Episode);

            var slow = resolver.Resolve(episode, Source1080p(frameRate: 23.976), series, (2560, 1440));
            var quick = resolver.Resolve(episode, Source1080p(frameRate: 59.94), series, (2560, 1440));

            Assert.Contains("ArtCNN", slow.Group!.Description);
            Assert.DoesNotContain("ArtCNN", quick.Group!.Description);
        });

        Test("着色器档位：设置里的装机默认值在表里找得到", () =>
        {
            var settings = new ShaderAutomationSettings();

            Assert.Equal(GpuTier.Low, settings.Gpu, "装机默认是低档：这台机器的核显，也是缺键时读出来的那一档");
            Assert.Equal("", settings.ManualGroup, "装机默认走自动");
            Assert.True(settings.Enabled);
            Assert.True(settings.RestoreVintageSources, "DVD 那一代的片源默认要去带");
            Assert.Equal(8, ShaderGroupCatalog.For(settings.Gpu).Count);
        });

        RegisterOldVersusNew();
        RegisterShaderDescriptions();
        RegisterShippedShaderFiles();
    }

    /// <summary>
    /// 放大倍数那个纯函数。这一族是整次重构的地基：从前的规则只看片源分辨率，「4K 片源」「480p 片源」离了屏幕
    /// 尺寸根本不成句，而当时的代码从来没问过输出有多大 —— 于是这件事在屏幕上完全看不出来，也没有一条断言碰得到它。
    /// </summary>
    private static void RegisterUpscaleTier()
    {
        Test("放大倍数：任务书那张片源→输出组合表，一格一格对", () =>
        {
            // 片源宽高、输出宽高、应得的倍数、应落的档
            (int SourceWidth, int SourceHeight, int OutWidth, int OutHeight, double Factor, UpscaleTier Tier)[] table =
            [
                (1920, 1080, 1920, 1080, 1.00, UpscaleTier.Shrink),
                (1920, 1080, 2560, 1440, 1.33, UpscaleTier.Slight),
                (1920, 1080, 3840, 2160, 2.00, UpscaleTier.Sweet),
                (1280, 720, 1920, 1080, 1.50, UpscaleTier.Sweet),
                (1280, 720, 2560, 1440, 2.00, UpscaleTier.Sweet),
                (1280, 720, 3840, 2160, 3.00, UpscaleTier.Large),
                (854, 480, 1920, 1080, 2.25, UpscaleTier.Large),
                (854, 480, 2560, 1440, 3.00, UpscaleTier.Large),
                (854, 480, 3840, 2160, 4.50, UpscaleTier.Large),

                // 576p 的两格是 2.2 那条独立轴的回归测试：PAL 的 DVD 放到 1080p 落甜点档，去带不能跟着倍数走。
                (720, 576, 1920, 1080, 1.88, UpscaleTier.Sweet),
                (720, 576, 2560, 1440, 2.50, UpscaleTier.Large),

                (3840, 2160, 2560, 1440, 0.67, UpscaleTier.Shrink)
            ];

            foreach (var row in table)
            {
                var measure = ShaderTier.Measure(row.SourceWidth, row.SourceHeight, row.OutWidth, row.OutHeight);

                Assert.True(Math.Abs(measure.Factor - row.Factor) < 0.02,
                    $"{row.SourceHeight}p 上 {row.OutHeight}p 应该是 {row.Factor} 倍，算出来是 {measure.Factor:0.00}");
                Assert.Equal(row.Tier, measure.Tier, $"{row.Factor} 倍该落在哪一档");
            }
        });

        Test("放大倍数：四档的分界正好落在那几个数上，1.00 倍的标签是「原生」", () =>
        {
            Assert.Equal(UpscaleTier.Shrink, ShaderTier.Classify(1.049999), "1.05 以下还是缩小档");
            Assert.Equal(UpscaleTier.Slight, ShaderTier.Classify(1.05), "1.05 本身算微放大");
            Assert.Equal(UpscaleTier.Slight, ShaderTier.Classify(1.449999));
            Assert.Equal(UpscaleTier.Sweet, ShaderTier.Classify(1.45), "1.45 本身算甜点");
            Assert.Equal(UpscaleTier.Sweet, ShaderTier.Classify(2.20), "2.20 本身还算甜点档");
            Assert.Equal(UpscaleTier.Large, ShaderTier.Classify(2.200001));
            Assert.Equal(UpscaleTier.Shrink, ShaderTier.Classify(0), "0 倍是退化输入，落在最省的那一档");

            // 撤掉第五档换来的那个标签：档还是缩小档，屏上写的是「原生」。
            Assert.Equal("原生", ShaderTier.Label(new UpscaleMeasure(1.00, UpscaleTier.Shrink)));
            Assert.Equal("原生", ShaderTier.Label(new UpscaleMeasure(0.95, UpscaleTier.Shrink)));
            Assert.Equal("缩小档", ShaderTier.Label(new UpscaleMeasure(0.94, UpscaleTier.Shrink)));
            Assert.Equal("缩小档", ShaderTier.Label(new UpscaleMeasure(0.67, UpscaleTier.Shrink)));
            Assert.Equal("微放大档", ShaderTier.Label(new UpscaleMeasure(1.33, UpscaleTier.Slight)));
        });

        Test("放大倍数：0.05 回差 —— 慢慢拖窗口经过分界时不来回换档", () =>
        {
            // 没有回差，1.45 附近拖一下窗口就是 ravu-zoom 和 ravu-lite 来回换，每次换都是一次锐度当场变化
            // 加一次着色器重新加载。
            Assert.Equal(UpscaleTier.Slight, ShaderTier.Classify(1.46, UpscaleTier.Slight), "1.44→1.46 不换档");
            Assert.Equal(UpscaleTier.Sweet, ShaderTier.Classify(1.52, UpscaleTier.Slight), "1.44→1.52 换档");
            Assert.Equal(UpscaleTier.Slight, ShaderTier.Classify(1.44, UpscaleTier.Slight));
            Assert.Equal(UpscaleTier.Sweet, ShaderTier.Classify(1.44, UpscaleTier.Sweet), "已经在甜点档里就黏在甜点档");

            // 在 1.44 和 1.46 之间来回时结果稳定：起点是哪一档，走完还是哪一档。
            foreach (var start in new[] { UpscaleTier.Slight, UpscaleTier.Sweet })
            {
                var tier = start;
                for (var round = 0; round < 8; round++) tier = ShaderTier.Classify(round % 2 == 0 ? 1.46 : 1.44, tier);
                Assert.Equal(start, tier, "来回拖八次之后还该是起点那一档");
            }

            // 回差只放宽 0.05，不是「永远不换」。
            Assert.Equal(UpscaleTier.Large, ShaderTier.Classify(2.26, UpscaleTier.Sweet));
            Assert.Equal(UpscaleTier.Sweet, ShaderTier.Classify(2.24, UpscaleTier.Sweet));
        });

        Test("放大倍数：宽高取小的那个，2.39:1 的片子才不会被抬高一整档", () =>
        {
            // 1920×800 铺到 2560×1440，实际画出来是 2560×1067，也就是 1.33 倍；只看高度会读成 1.8 倍，
            // 于是挂上一个 2 倍放大器，再被 mpv 缩回去 —— 算力全花在被丢掉的像素上。
            var measure = ShaderTier.Measure(1920, 800, 2560, 1440);

            Assert.True(Math.Abs(measure.Factor - 1.333) < 0.01, $"应该是 1.33 倍，算出来是 {measure.Factor:0.00}");
            Assert.Equal(UpscaleTier.Slight, measure.Tier);
            Assert.Contains("宽比", measure.Note, "宽高两个比差得多的时候要记一句，否则日志读不出这是加信封片源");

            // 4:3 的 DVD 反过来是被高度限住的，那一边取小同样成立。
            Assert.True(Math.Abs(ShaderTier.Measure(720, 480, 2560, 1440).Factor - 3.0) < 0.01);

            // 等比片源不该有那句话。
            Assert.Equal("", ShaderTier.Measure(1920, 1080, 2560, 1440).Note);
        });

        Test("放大倍数：问不出尺寸时落在微放大档，而且说清楚是问不出来", () =>
        {
            foreach (var (sourceWidth, sourceHeight, outWidth, outHeight, missing) in new[]
            {
                (0, 1080, 2560, 1440, "片源尺寸未知"),
                (1920, 0, 2560, 1440, "片源尺寸未知"),
                (1920, 1080, 0, 1440, "输出尺寸未知"),
                (1920, 1080, 2560, 0, "输出尺寸未知")
            })
            {
                var measure = ShaderTier.Measure(sourceWidth, sourceHeight, outWidth, outHeight);

                Assert.Equal(UpscaleTier.Slight, measure.Tier,
                    "微放大档的放大器（真人 ravu-zoom）能直接放到任意倍数，动画那一档自己带 1.3 倍门控，猜错也不会硬跑");
                Assert.False(measure.Measured, "没量到就不能假装量到了");
                Assert.Equal(missing, measure.Note);
            }
        });

        Test("老片源：≤576 线是另一根轴，高度问不出来时不算老片源", () =>
        {
            Assert.True(ShaderTier.IsVintage(576), "PAL 的 DVD");
            Assert.True(ShaderTier.IsVintage(480), "NTSC 的 DVD");
            Assert.False(ShaderTier.IsVintage(577));
            Assert.False(ShaderTier.IsVintage(720));
            Assert.False(ShaderTier.IsVintage(0), "0 是「服务器没探过」，不是一张 DVD");
            Assert.False(ShaderTier.IsVintage(-1));
        });
    }

    /// <summary>
    /// 窗口变化怎么影响链（任务书 2.3 和 2.4）。这一族是整个方案里唯一有状态的部分，也是唯一「做错了屏上看不出
    /// 来」的部分：拖窗口时每帧重生成链的症状是掉帧，而掉帧看着像片源码率高。所以判定做成了一个不带计时器、
    /// 不认识窗口的纯状态机，时间由调用方交进来。
    /// </summary>
    private static void RegisterOutputWatch()
    {
        static ShaderSurface Windowed(int width, int height) => new(width, height, 2560, 1440, false);
        static ShaderSurface Full() => new(2560, 1440, 2560, 1440, true);

        Test("输出尺寸：先问渲染目标，再用最近一次量到的，最后才退到显示器", () =>
        {
            (int Width, int Height) monitor = (2560, 1440);

            var direct = ShaderSurface.Resolve((1600, 900), (1280, 720), monitor, false);
            Assert.Equal(1600, direct.Width, "问得到就用当前渲染目标");
            Assert.False(direct.Fallback);

            var stale = ShaderSurface.Resolve(default, (1280, 720), monitor, false);
            Assert.Equal(1280, stale.Width, "问不到就用最近一次成功量到的");
            Assert.False(stale.Fallback, "一个旧的真尺寸不算兜底");

            var guessed = ShaderSurface.Resolve(default, default, monitor, false);
            Assert.Equal(2560, guessed.Width, "两样都没有才退到显示器");
            Assert.True(guessed.Fallback, "退到显示器要说一声 —— 这是外部 mpv.exe 那个后端的答案");

            // 不许假定 4K：兜底用的是这块屏，而不是一个写死的数。
            Assert.Equal(1080, ShaderSurface.Resolve(default, default, (1920, 1080), false).Height);

            // 全屏时铺满的是显示器，窗口矩形怎么说都不算。
            var full = ShaderSurface.Resolve((1280, 720), default, monitor, true);
            Assert.Equal(2560, full.Active.Width);
            Assert.Equal(1440, full.Active.Height);
        });

        Test("窗口：连续 resize 期间一次都不重新生成链，停稳 400 毫秒才算一次", () =>
        {
            var watch = new OutputWatch(1920, 1080, Windowed(2560, 1440), UpscaleTier.Slight);
            var start = DateTimeOffset.UnixEpoch;

            for (var step = 1; step <= 20; step++)
            {
                var moment = start + TimeSpan.FromMilliseconds(step * 30);
                var seen = watch.Observe(Windowed(2560 - step * 40, 1440 - step * 22), moment);

                Assert.Equal(OutputChange.Waiting, seen.Change, $"第 {step} 次拖动就重新生成链了");
                Assert.Equal(OutputChange.Waiting, watch.Tick(moment).Change, "拖动过程中每一跳都不许有结论");
            }

            var settled = start + TimeSpan.FromMilliseconds(20 * 30);
            Assert.Equal(OutputChange.Waiting, watch.Tick(settled + TimeSpan.FromMilliseconds(399)).Change, "399 毫秒还不算停稳");

            var verdict = watch.Tick(settled + TimeSpan.FromMilliseconds(401));
            Assert.Equal(OutputChange.Rebuild, verdict.Change, "停稳之后才算一次");
            Assert.False(verdict.Discrete, "这是停稳的 resize，不是离散事件");
            Assert.Equal(1760, verdict.Width, "落定的是最后那个尺寸，中间十九个都只是被记下过");
            Assert.False(watch.Settling);
        });

        Test("窗口：停稳后不跨档只更新尺寸，跨档才重新生成，大小两个方向都测", () =>
        {
            var watch = new OutputWatch(1920, 1080, Windowed(2560, 1440), UpscaleTier.Slight);
            var clock = DateTimeOffset.UnixEpoch;

            // 2560×1440 → 2400×1350 是 1.25 倍，还在微放大档里。
            watch.Observe(Windowed(2400, 1350), clock);
            var same = watch.Tick(clock + TimeSpan.FromMilliseconds(500));
            Assert.Equal(OutputChange.SizeOnly, same.Change, "同一档里只该记下新尺寸");
            Assert.Equal(2400, same.Width);
            Assert.Equal(UpscaleTier.Slight, watch.Tier);
            Assert.Equal(2400, watch.Output.Width, "ravu-zoom 按目标尺寸渲染，所以尺寸本身要更新");

            // 缩到 1600×900 是 0.83 倍，跨到缩小档。
            clock += TimeSpan.FromSeconds(1);
            watch.Observe(Windowed(1600, 900), clock);
            var smaller = watch.Tick(clock + TimeSpan.FromMilliseconds(500));
            Assert.Equal(OutputChange.Rebuild, smaller.Change);
            Assert.Equal(UpscaleTier.Shrink, watch.Tier);

            // 反方向：拉到 3200×1800 是 1.67 倍，跨到甜点档。
            clock += TimeSpan.FromSeconds(1);
            watch.Observe(Windowed(3200, 1800), clock);
            var bigger = watch.Tick(clock + TimeSpan.FromMilliseconds(500));
            Assert.Equal(OutputChange.Rebuild, bigger.Change);
            Assert.Equal(UpscaleTier.Sweet, watch.Tier);

            // 回差在这里也管事：3200×1800 → 2800×1575 是 1.458 倍，还在甜点档（放宽后的下界是 1.40）。
            clock += TimeSpan.FromSeconds(1);
            watch.Observe(Windowed(2800, 1575), clock);
            Assert.Equal(OutputChange.SizeOnly, watch.Tick(clock + TimeSpan.FromMilliseconds(500)).Change);
            Assert.Equal(UpscaleTier.Sweet, watch.Tier);
        });

        Test("窗口：拖回原尺寸就把待定的那一个丢掉", () =>
        {
            var watch = new OutputWatch(1920, 1080, Windowed(2560, 1440), UpscaleTier.Slight);
            var clock = DateTimeOffset.UnixEpoch;

            watch.Observe(Windowed(1600, 900), clock);
            Assert.True(watch.Settling);

            var back = watch.Observe(Windowed(2560, 1440), clock + TimeSpan.FromMilliseconds(50));
            Assert.Equal(OutputChange.None, back.Change);
            Assert.False(watch.Settling, "拖回起点之后那一跳不能再去改链");
            Assert.Equal(OutputChange.None, watch.Tick(clock + TimeSpan.FromSeconds(5)).Change);
        });

        Test("窗口：进退全屏立刻换预案，不等防抖", () =>
        {
            // 小窗 1600×900（0.83 倍，缩小档）里按下全屏，铺满 2560×1440 就是 1.33 倍的微放大档。
            var watch = new OutputWatch(1920, 1080, Windowed(1600, 900), UpscaleTier.Shrink);
            var clock = DateTimeOffset.UnixEpoch;

            var entered = watch.Observe(Full(), clock);
            Assert.Equal(OutputChange.Rebuild, entered.Change, "全屏当场生效");
            Assert.True(entered.Discrete, "离散事件：调用方拿开播时预备好的那一套，不重新判定");
            Assert.Equal(2560, entered.Width);
            Assert.Equal(UpscaleTier.Slight, watch.Tier);
            Assert.False(watch.Settling, "全屏不进防抖");

            var left = watch.Observe(Windowed(1600, 900), clock);
            Assert.Equal(OutputChange.Rebuild, left.Change, "退全屏同样当场生效");
            Assert.True(left.Discrete);
            Assert.Equal(UpscaleTier.Shrink, watch.Tier);

            // 拖动中途按下全屏：待定的那个尺寸要跟着丢掉，否则接下来那一跳会去处理一次已经作废的变化。
            watch.Observe(Windowed(2000, 1125), clock);
            Assert.True(watch.Settling);
            watch.Observe(Full(), clock + TimeSpan.FromMilliseconds(100));
            Assert.False(watch.Settling);
        });

        Test("窗口：拖到另一台显示器是离散事件，尺寸没变也要报出来", () =>
        {
            // 同样大小的窗口挪到一块 4K 屏上：窗口尺寸一个像素没变，所以链不用变 —— 可全屏那一套预案是按
            // 显示器算的，它刚刚过期了，调用方必须重算。这就是为什么这一档报的是「离散」而不是「没事」。
            var watch = new OutputWatch(1920, 1080, Windowed(1600, 900), UpscaleTier.Shrink);

            var moved = watch.Observe(new ShaderSurface(1600, 900, 3840, 2160, false), DateTimeOffset.UnixEpoch);

            Assert.Equal(OutputChange.SizeOnly, moved.Change, "窗口尺寸没变，链也就不用变");
            Assert.True(moved.Discrete, "但这是离散事件，两套方案都得重算");
            Assert.False(watch.Settling, "换显示器不等防抖");
        });
    }

    /// <summary>
    /// 新旧并行对照（任务书 3.9）：拿新判定跑一遍旧五个组各自的触发条件，把差异打印出来。在不能真实播放的
    /// 前提下这是唯一能看出行为变化的办法 —— 所以它主要是打印，只对**低档那一列**加一条断言：除了「多了色度
    /// 重建」、「换掉那个根本不放大的 FSRCNNX_x1」、「老片源多了去带」，以及「在放大的链里去掉本来就不出手的
    /// SSimDownscaler」之外，不许有别的变化。第一批验收就是这一句。
    /// </summary>
    private static void RegisterOldVersusNew()
    {
        Test("新旧并行对照：旧五个组的触发条件下，低档那一列只有说得出理由的变化", () =>
        {
            // 旧规则（v6）：高清阈值 1600 压过低清阈值 720，两者都压过「动画」那一支。
            static string OldGroup(bool animated, int height) =>
                height >= 1600 ? "2K-iGPU-Light"
                : height is > 0 and <= 720 ? "2K-iGPU-SD"
                : animated ? "2K-iGPU-Anime"
                : "2K-iGPU";

            static string[] OldChain(string group) => group switch
            {
                "2K-iGPU" => ["ravu-zoom-ar-r2", "SSimDownscaler"],
                "2K-iGPU-Light" => ["SSimDownscaler"],
                "2K-iGPU-Anime" =>
                [
                    "Anime4K_Clamp_Highlights", "Anime4K_Restore_CNN_M", "Anime4K_Upscale_CNN_x2_M",
                    "Anime4K_AutoDownscalePre_x2", "Anime4K_AutoDownscalePre_x4", "Anime4K_Upscale_CNN_x2_S",
                    "SSimDownscaler"
                ],
                _ => ["FSRCNNX_x1_16-0-4-1_distort", "SSimDownscaler"]
            };

            (string What, bool Animated, int Width, int Height)[] rows =
            [
                ("真人 1080p", false, 1920, 1080),
                ("真人 720p", false, 1280, 720),
                ("真人 576p（PAL DVD）", false, 720, 576),
                ("真人 480p（NTSC DVD）", false, 854, 480),
                ("真人 4K", false, 3840, 2160),
                ("动画 1080p", true, 1920, 1080),
                ("动画 720p", true, 1280, 720),
                ("动画 4K", true, 3840, 2160)
            ];

            Console.WriteLine();
            Console.WriteLine("  ── 新旧对照（低档、输出 2560×1440，也就是这台机器全屏）");

            foreach (var row in rows)
            {
                var oldGroup = OldGroup(row.Animated, row.Height);
                var old = OldChain(oldGroup);
                var decision = Shaders().Resolve(row.Animated, row.Width, row.Height, 2560, 1440);
                var chain = decision.Group!;
                var now = chain.ShaderFileNames.ToArray();

                var removed = old.Except(now, StringComparer.Ordinal).ToArray();
                var added = now.Except(old, StringComparer.Ordinal).ToArray();

                Console.WriteLine($"     {row.What}：{oldGroup} → {chain.Name}");
                Console.WriteLine($"       旧：{string.Join(" + ", old)}");
                Console.WriteLine($"       新：{string.Join(" + ", now)}");
                if (removed.Length > 0) Console.WriteLine($"       去掉：{string.Join("、", removed)}");
                if (added.Length > 0) Console.WriteLine($"       加上：{string.Join("、", added)}");

                var swapped = removed.Any(name => name.Contains("FSRCNNX", StringComparison.Ordinal));

                // 动画那三档从 Anime4K Mode A 换成 ArtCNN 是用户 2026-09-04 亲口说的「动画换 ArtCNN」（微放大和
                // 甜点先换，大倍数当天第二句话跟着换），所以这里也是一条说得出理由的变化 —— 但只在动画那一半、
                // 而且只换成 ArtCNN，别的都不算。
                var toArtCnn = row.Animated && added.Any(name => name.StartsWith("ArtCNN", StringComparison.Ordinal));

                foreach (var gone in removed)
                {
                    var excuse =
                        gone.Contains("FSRCNNX", StringComparison.Ordinal)
                        || (gone == "SSimDownscaler" && chain.Tier != UpscaleTier.Shrink)
                        || (toArtCnn && gone.StartsWith("Anime4K", StringComparison.Ordinal));

                    Assert.True(excuse, $"{row.What}：低档那一列去掉了 {gone}，而这一条没有理由");
                }

                foreach (var fresh in added)
                {
                    var excuse =
                        fresh.StartsWith("CfL_Prediction", StringComparison.Ordinal)
                        || fresh == "hdeband"
                        || swapped
                        || toArtCnn;

                    Assert.True(excuse, $"{row.What}：低档那一列多了 {fresh}，而这一条没有理由");
                }
            }

            Console.WriteLine("     2K-iGPU-Anime+（Ani4Kv2 转手的 ArtCNN）旧规则从来不会自动选中它，只能手选");
        });
    }

    /// <para>
    /// 从前这一条比的是 C# 目录和 csproj 里手抄的那份清单。现在着色器文件在仓库里、csproj 用通配符整棵拷，
    /// 那份手抄的清单没了，能跑偏的地方也换了：表里点了一个磁盘上没有的文件（发出去就是 mpv 每帧报一次加载
    /// 失败，画面只「看起来差一点」，四道闸门一条都不会红），或者仓库里躺着一个没有任何档位用得上的文件
    /// （白装几百 KB，还得跟着上游更新）。两个方向分开报，报告里直接写出是哪个文件。
    /// </para>
    /// </summary>
    private static void RegisterShippedShaderFiles()
    {
        const string name = "着色器档位：装箱的文件和档位表点名的一字不差";

        var repo = RepositoryRoot();
        var root = repo is null ? null : Path.Combine(repo, "assets", "shaders");
        if (root is null || !Directory.Exists(root))
        {
            Skip(name, "找不到仓库里的 assets/shaders");
            return;
        }

        Test(name, () =>
        {
            var onDisk = Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".glsl", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".hook", StringComparison.OrdinalIgnoreCase))
                .Select(path => Normalize(Path.GetRelativePath(root, path)))
                .ToHashSet(StringComparer.Ordinal);

            var named = ShaderGroupCatalog.ShaderFiles.Select(Normalize).ToHashSet(StringComparer.Ordinal);

            Assert.Equal("", Join(named.Except(onDisk)), "档位表点名了、assets/shaders 里却没有的着色器");
            Assert.Equal("", Join(onDisk.Except(named)), "assets/shaders 里装着、可没有一个档位用得上的着色器");
        });

        static string Normalize(string path) => path.Replace('\\', '/').ToLowerInvariant();

        static string Join(IEnumerable<string> paths) =>
            string.Join("、", paths.OrderBy(path => path, StringComparer.Ordinal));
    }

    /// <summary>
    /// 渲染层契约（任务书 5.5）：Core 判出来的那条链，交到 mpv 手上时必须还是那一条。
    /// <para>
    /// 这一层是唯一抓得住「Core 判得对、渲染层跑成另一条链」的地方，而这个项目已经栽过一次同类问题 —— 当年那个 HQ
    /// 预设写 <c>cscale=bilinear</c>、配置组随后写 <c>spline36</c>、组总赢，于是设置页显示「画质预设 = HQ」而生效的
    /// 是别的东西，界面在骗人。所以这里读的是**最终那份选项表按 mpv 的 last-wins 规则解析之后**的值，不是中间
    /// 某一层写了什么。
    /// </para>
    /// </summary>
    private static void RegisterRendererContract()
    {
        Test("渲染层契约：最终生效的每一项都和链说的一样，多余的都不许有", () =>
        {
            foreach (var preset in MpvOutputOptions.QualityPresets.Select(choice => choice.Value))
            {
                var (planner, settings) = Planner();
                settings.Video.QualityPreset = preset;

                var request = planner.Plan(Ticket(), Connection());
                var chain = ShaderGroupCatalog.Resolve(false, UpscaleTier.Slight, GpuTier.Low);

                // last-wins，和 mpv 自己解析这串参数的规则一致。
                var live = Options(request.PlayerOptions);

                foreach (var (name, wanted) in chain.ToMpvOptions(ShaderGroupCatalog.ShaderRoot))
                {
                    Assert.True(live.TryGetValue(name, out var got) && got == wanted,
                        $"画质预设 {preset} 之下，{name} 最终是 {got ?? "（没给）"}，而链要的是 {wanted}");
                }

                // 反方向，钉的是任务书 3.6 那一句「scale / cscale / dscale 只由档位链设置」，外加着色器列表本身：
                // 这四个名字如果被链之外的东西写过，设置页就会显示一个与实际不符的值 —— 那正是「画质预设写
                // bilinear、组随后写 spline36、组总赢、界面在骗人」那次事故。
                //
                // 还原表里其余几个名字是**共有的**，故意不在这里判：去色带是设置页的一行（链只在挂了 hdeband
                // 时接管它），光域那两项是画质预设的（链只在挂了 SSimDownscaler 时接管）。它们「被链接管时以链
                // 为准」已经由上面那一段正向断言钉住了。
                string[] chainOnly = ["glsl-shaders", "scale", "cscale", "dscale"];

                var chainNames = chain.ToMpvOptions(ShaderGroupCatalog.ShaderRoot)
                    .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var name in chainOnly.Where(name => !chainNames.Contains(name)))
                {
                    Assert.False(live.ContainsKey(name),
                        $"画质预设 {preset} 之下，{name} 被写成了 {live.GetValueOrDefault(name)}，可这条链没点它 —— 那就是链之外的东西在动缩放");
                }

                Assert.Equal(chain.ToMpvOptions(ShaderGroupCatalog.ShaderRoot).Count, request.ShaderOptionCount,
                    "交给 SetShaderGroupAsync 的那个「链贡献了几项」必须准，否则切档时回落的基准就错了");
            }
        });

        Test("渲染层契约：链里的着色器文件一个不多一个不少，次序也一样", () =>
        {
            var (planner, _) = Planner();
            var request = planner.Plan(Ticket(), Connection());
            var chain = ShaderGroupCatalog.Resolve(false, UpscaleTier.Slight, GpuTier.Low);

            var live = Options(request.PlayerOptions)["glsl-shaders"];
            var loaded = live.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFileName).ToArray();

            Assert.Equal(
                string.Join(" + ", chain.Files.Select(file => file.Split('/')[^1])),
                string.Join(" + ", loaded),
                "渲染层加载的文件和链说的必须一字不差 —— 包括次序，色度重建那一条结论就靠它");
        });

        Test("渲染层契约：关掉着色器之后没有任何一条链的选项残留", () =>
        {
            var (planner, settings) = Planner();
            settings.Shaders.Enabled = false;

            var request = planner.Plan(Ticket(), Connection());
            var live = Options(request.PlayerOptions);

            Assert.Equal(0, request.ShaderOptionCount);
            Assert.False(live.ContainsKey("glsl-shaders"), "没有链的时候不该给 glsl-shaders");
            Assert.False(live.ContainsKey("dscale"), "SSimDownscaler 的前置条件只该跟着它自己走");
        });

        // 票上的刷新率必须真的走到 mpv 那一步。这一段是「接线」而不是「规则」—— 规则由 MpvOutputOptions 那几条单测
        // 钉着，可忘了把 ticket.DisplayRefreshHz 传下去的话，规则永远拿到 0、永远不出手，而画面和日志都看不出来。
        Test("渲染层契约：票上写的屏幕刷新率会走到 video-sync", () =>
        {
            var (planner, settings) = Planner();
            settings.Video.Interpolation = true;

            var slow = Options(planner.Plan(Ticket() with { DisplayRefreshHz = 60 }, Connection()).PlayerOptions);
            Assert.Equal("display-resample", slow["video-sync"], "60Hz 上插值该照常生效");
            Assert.Equal("yes", slow["interpolation"]);

            var fast = Options(planner.Plan(Ticket() with { DisplayRefreshHz = 144 }, Connection()).PlayerOptions);
            Assert.Equal("audio", fast["video-sync"], "144Hz 上显示同步只剩算力开销，票上的刷新率没接通就会看不出来");
            Assert.Equal("no", fast["interpolation"]);

            var unknown = Options(planner.Plan(Ticket(), Connection()).PlayerOptions);
            Assert.Equal("display-resample", unknown["video-sync"], "读不到刷新率时不许替他做决定");
        });

        Test("运行条件：vo=gpu 配 d3d11 会被点出来，默认那一套不会", () =>
        {
            // mpv-prescalers 的 README 记的是一条具体故障，不是偏好：这个组合报 rgba16f 不可用，ravu 那几条链
            // 根本加载不上 —— 配置齐全、日志正常、画面上什么都没多。
            var broken = string.Join("\n", MpvRenderCheck.Problems("gpu", "d3d11", "auto-safe"));
            Assert.Contains("rgba16f", broken);

            Assert.Equal("", string.Join("\n", MpvRenderCheck.Problems("gpu-next", "vulkan", "auto-safe")),
                "装机默认这一套不该有话说");
            Assert.Equal("", string.Join("\n", MpvRenderCheck.Problems("gpu-next", "d3d11", "auto-safe")),
                "没有链的时候 d3d11 也没什么可说的 —— 那道坎是 compute pass 的事，不是 d3d11 本身的事");

            Assert.Contains("硬件解码是关的", string.Join("\n", MpvRenderCheck.Problems("gpu-next", "vulkan", "")));
            Assert.Contains("gpu-next", string.Join("\n", MpvRenderCheck.Problems("gpu", "vulkan", "auto-safe")));
        });

        Test("运行条件：带 compute pass 的链撞上 d3d11 要被点出来（2026-09-04 那五倍）", () =>
        {
            // 同一条链、同一张卡、同一段片子，只换图形接口：vulkan 45 fps、d3d11 8.7 fps，而 24fps 的片子要 24。
            // 判据是链里有没有 compute pass，不是「d3d11 慢」—— 片元着色器那几条链在两个接口上差别在噪声里，
            // 所以这一条不许写成对 d3d11 的一概而论，也不许在这里点 ArtCNN 的名字。
            var artcnn = ShaderGroupCatalog.Resolve(animated: true, UpscaleTier.Slight, GpuTier.Low);
            var ravu = ShaderGroupCatalog.Resolve(animated: false, UpscaleTier.Slight, GpuTier.Low);

            Assert.True(artcnn.Shaders.Sum(shader => shader.ComputePasses) > 0, "动画微放大那一格该有 compute pass");
            Assert.Equal(0, ravu.Shaders.Sum(shader => shader.ComputePasses), "ravu 那一格一个都没有");

            Assert.Contains("compute pass", string.Join("\n",
                MpvRenderCheck.Problems("gpu-next", "d3d11", "auto-safe", artcnn)));
            Assert.Contains("compute pass", string.Join("\n",
                MpvRenderCheck.Problems("gpu-next", "", "auto-safe", artcnn)),
                "「自动挑选」在 Windows 上就是 d3d11，不能因为它没写字就放过去");

            Assert.Equal("", string.Join("\n", MpvRenderCheck.Problems("gpu-next", "vulkan", "auto-safe", artcnn)),
                "vulkan 上这条链是够快的");
            Assert.Equal("", string.Join("\n", MpvRenderCheck.Problems("gpu-next", "d3d11", "auto-safe", ravu)),
                "没有 compute pass 的链在 d3d11 上没问题，不该被连坐");
        });
    }

    /// <para>
    /// 这是任务书 5.1 要的那道机械闸门。判一个着色器「是不是放大器」不能靠文件名 —— <c>FSRCNNX_x1</c> 名字像
    /// 放大器、通篇没有一条 <c>//!WIDTH</c>，那一格因此几个月里一倍都没放大过。有了这一条，描述和文件对不上就
    /// 当场红，包括上游哪天换了门控数字。
    /// </para>
    /// </summary>
    private static void RegisterShaderDescriptions()
    {
        const string name = "着色器描述：每一条都和文件里的 //! 指令一致";

        var repo = RepositoryRoot();
        var root = repo is null ? null : Path.Combine(repo, "assets", "shaders");
        if (root is null || !Directory.Exists(root))
        {
            Skip(name, "找不到仓库里的 assets/shaders");
            return;
        }

        Test(name, () =>
        {
            foreach (var shader in ShaderLibrary.All)
            {
                var path = Path.Combine(root, shader.File.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), $"{shader.Name} 点的文件不在：{shader.File}");

                var directives = File.ReadAllLines(path)
                    .Select(line => line.TrimEnd())
                    .Where(line => line.StartsWith("//!", StringComparison.Ordinal))
                    .ToArray();

                var hooks = directives.Where(line => line.StartsWith("//!HOOK ", StringComparison.Ordinal)).ToArray();
                Assert.True(hooks.Length > 0, $"{shader.Name} 一个 //!HOOK 都没有");
                Assert.Equal(hooks[0]["//!HOOK ".Length..], shader.Hook, $"{shader.Name} 的第一个钩子阶段");
                Assert.Equal(hooks.Length, shader.Passes, $"{shader.Name} 的 pass 数");

                var sizes = directives.Any(line =>
                    line.StartsWith("//!WIDTH", StringComparison.Ordinal) || line.StartsWith("//!HEIGHT", StringComparison.Ordinal));
                Assert.Equal(sizes, shader.ChangesResolution, $"{shader.Name} 有没有声明自己的输出尺寸");

                var when = directives.FirstOrDefault(line => line.StartsWith("//!WHEN ", StringComparison.Ordinal));
                Assert.Equal(when is null ? "" : when["//!WHEN ".Length..], shader.Gate, $"{shader.Name} 的门控");

                Assert.Equal(directives.Contains("//!BIND LUMA"), shader.ReadsLuma, $"{shader.Name} 读不读亮度");
                Assert.Equal(directives.Contains("//!BIND CHROMA"), shader.ReadsChroma, $"{shader.Name} 读不读色度");

                // compute pass 的数目：这是「同一条链 d3d11 8.7 fps、vulkan 45 fps」那道坎唯一的判据，上游哪天
                // 把某个 pass 从 compute 改成片元（或者反过来），这一条当场红。
                var compute = directives.Count(line => line.StartsWith("//!COMPUTE", StringComparison.Ordinal));
                Assert.Equal(compute, shader.ComputePasses, $"{shader.Name} 的 compute pass 数");
            }
        });
    }

    private static string? RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln"))) return directory.FullName;
        }

        return null;
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
        RegisterRendererContract();

        Test("计划：URL、请求头与令牌位置", () =>
        {
            var (planner, settings) = Planner();
            settings.Shaders.Enabled = false;

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

        Test("计划：默认不让 mpv 记住播放位置", () =>
        {
            var (planner, _) = Planner();
            var line = Line(MpvArgumentBuilder.Build(planner.Plan(Ticket() with { StartTicks = 6_000_000_000 }, Connection())));

            // --no-config 已经把 watch_later 文件一起挡掉了，这两条是双保险：进度归服务器管，
            // mpv 不该另存一份跟服务器对不上的位置。
            Assert.Contains("--resume-playback=no", line);
            Assert.Contains("--save-position-on-quit=no", line);
            Assert.Contains("--start=600", line, "位置由客户端给出：10 分钟 = 600 秒");
        });

        Test("计划：着色器档位连同它自己的缩放器一起变成 mpv 选项", () =>
        {
            var (planner, settings) = Planner();

            var request = planner.Plan(Ticket(), Connection());
            var expected = ShaderGroupCatalog.Resolve(animated: false, UpscaleTier.Slight, GpuTier.Low);
            Assert.Equal(expected.Name, request.ShaderProfile, "1080p 上 1440p 是 1.33 倍，落在微放大档");

            var options = Options(request.PlayerOptions);
            Assert.True(options.TryGetValue("glsl-shaders", out var shaders) && shaders.Length > 0,
                "档位必须落到 glsl-shaders 上");
            Assert.Equal(Options(expected.Options)["scale"], options["scale"],
                "档位自带的缩放器要跟着一起给出，否则这一档的调法就不成立了");
            Assert.Equal(expected.Options.Count + 1, request.ShaderOptionCount,
                "切档时要靠这个数认出「挂着色器之前」那一段，多一个少一个都会让还原读错值");

            settings.Shaders.Enabled = false;
            var plain = planner.Plan(Ticket(), Connection());
            Assert.Null(plain.ShaderProfile);
            Assert.Equal(0, plain.ShaderOptionCount);
            Assert.False(Options(plain.PlayerOptions).ContainsKey("glsl-shaders"),
                "没有档位时不必提 glsl-shaders，mpv 自己就是空的");
        });

        Test("计划：问不出屏幕尺寸时照旧播放，落在微放大档", () =>
        {
            var (planner, _) = Planner();

            // 输出尺寸问不出来（多显示器插拔的那一刻、命令行拉起来的那一次）不该让画面完全没有着色器。
            var request = planner.Plan(Ticket() with { OutputWidth = 0, OutputHeight = 0 }, Connection());

            Assert.Equal(ShaderGroupCatalog.Resolve(false, UpscaleTier.Slight, GpuTier.Low).Name, request.ShaderProfile);
            Assert.Contains("输出尺寸未知", request.ShaderReason!);
            Assert.True(Options(request.PlayerOptions).ContainsKey("glsl-shaders"));
        });

        Test("计划：客户端自己的基线选项排在设置之前", () =>
        {
            var (planner, _) = Planner();
            var options = Options(planner.Plan(Ticket(), Connection()).PlayerOptions);

            // 这些原本靠 mpv.conf 全局生效，配置文件不读之后必须由客户端自己给出。
            Assert.Equal("yes", options["hr-seek"]);
            Assert.Equal("no", options["sub-auto"], "服务器已经把外挂字幕列全了，再扫一遍目录只会多出重复轨");
            Assert.Equal("no", options["audio-file-auto"]);
            Assert.Equal("no", options["icc-profile-auto"], "装机默认不做 ICC 校色，设置里那个开关才把它抬成 yes");
        });

        Test("计划：打开自动 ICC 校色之后，发给 mpv 的最后一个值是 yes", () =>
        {
            var (planner, settings) = Planner();
            settings.Video.IccProfileAuto = true;

            var pairs = planner.Plan(Ticket(), Connection()).PlayerOptions;

            // 基线先发 no、视频输出压在上面，靠的就是「后发的说了算」。所以这里不只看最终值，还要确认这两条
            // 真的都在列表里、顺序没被谁调过 —— 只断言最终值的话，哪天基线那条被挪到后面去，测试照样绿。
            Assert.Equal("yes", Options(pairs)["icc-profile-auto"], "最后落到 mpv 手里的必须是 yes");

            var floor = -1;
            var lifted = -1;
            for (var index = 0; index < pairs.Count; index++)
            {
                if (!string.Equals(pairs[index].Key, "icc-profile-auto", StringComparison.OrdinalIgnoreCase)) continue;
                if (pairs[index].Value == "no") floor = index;
                if (pairs[index].Value == "yes") lifted = index;
            }

            Assert.True(floor >= 0 && lifted > floor, $"设置那条必须排在基线那条后面（实际 {floor} / {lifted}）");
        });

        Test("输出：宽片裁切填充只对真的有黑边的片源出手", () =>
        {
            // 交给一个 16:9 的文件就是白裁一刀 —— 那时候本来就没有黑边可填。mpv 自己的 panscan 默认是 0，而每次
            // 播放都是新起的 mpv，所以「关」一条都不必发。
            var wide = new SourceProfile(1920, 800, 8, 24, false);
            var flat = new SourceProfile(1920, 1080, 8, 24, false);

            var on = Options(MpvOutputOptions.Build(
                new VideoSettings { FillWideSources = true }, new AudioSettings(), source: wide));
            Assert.Equal("1.0", on["panscan"]);

            var sixteenNine = Options(MpvOutputOptions.Build(
                new VideoSettings { FillWideSources = true }, new AudioSettings(), source: flat));
            Assert.False(sixteenNine.ContainsKey("panscan"), "16:9 的片源没有黑边，裁它没有意义");

            var off = Options(MpvOutputOptions.Build(new VideoSettings(), new AudioSettings(), source: wide));
            Assert.False(off.ContainsKey("panscan"), "关着的时候一条都不发");

            var unknown = Options(MpvOutputOptions.Build(
                new VideoSettings { FillWideSources = true }, new AudioSettings()));
            Assert.False(unknown.ContainsKey("panscan"), "问不出片源形状时不许猜");
        });

        Test("输出：音频输出设备只在挑过的时候下发", () =>
        {
            // 空串是「跟随系统默认设备」，也就是 mpv 自己的 auto —— 一条都不发。这一项存在的全部意义是
            // 「独占模式该占哪一个」：不发的时候占的是 Windows 那一刻认的默认设备，而那是看不见也选不了的。
            var auto = Options(MpvOutputOptions.Build(new VideoSettings(), new AudioSettings()));
            Assert.False(auto.ContainsKey("audio-device"));

            var picked = Options(MpvOutputOptions.Build(
                new VideoSettings(),
                new AudioSettings { Device = "wasapi/{0.0.0.00000000}.{9c3d1b2e}", ExclusiveMode = true }));

            Assert.Equal("wasapi/{0.0.0.00000000}.{9c3d1b2e}", picked["audio-device"]);
            Assert.Equal("yes", picked["audio-exclusive"], "挑了设备之后独占模式照旧要发");
        });

        Test("音频设备：没有描述就拿设备名当标签", () =>
        {
            // 屏上那一行显示的是描述；描述是空的时候必须退到设备名，而不是一个空白的下拉项 —— 一个看不出是
            // 什么的选项和没有这一项一样糟。
            Assert.Equal("扬声器 (Realtek)", new AudioDevice("wasapi/abc", "扬声器 (Realtek)").Label);
            Assert.Equal("wasapi/abc", new AudioDevice("wasapi/abc", "").Label);
            Assert.Equal(AudioDeviceCatalogue.AutoDevice, new AudioDevice("auto", "  ").Label);
        });

        Test("计划：截图有落点、有格式、有片名加时间码的模板", () =>
        {
            // 截图这个功能从前一条都没有，理由是「--no-config 之下没有 screenshot-directory，文件会落到 exe
            // 旁边而不告诉用户」。所以这三条一起下发才算把那个理由消掉了 —— 少了落点那一条，症状正是当年那个。
            var (planner, _) = Planner(@"D:\shots");
            var options = Options(planner.Plan(Ticket(), Connection()).PlayerOptions);

            Assert.Equal(@"D:\shots", options["screenshot-directory"]);
            Assert.Equal("png", options["screenshot-format"], "看画质的图不能先过一遍有损压缩");
            Assert.True(options["screenshot-template"].Contains("%wH"), "模板里要带时间码");

            // 没有落点的时候一条都不发：那是测试和命令行那条路，不是屏上那条。
            var (bare, _) = Planner();
            var without = Options(bare.Plan(Ticket(), Connection()).PlayerOptions);
            Assert.False(without.ContainsKey("screenshot-directory"));
            Assert.False(without.ContainsKey("screenshot-format"));
            Assert.False(without.ContainsKey("screenshot-template"));
        });

        Test("截图模板：片名进得去，非法文件名字符进不去", () =>
        {
            // 时间码用 %wH.%wM.%wS 而不是 mpv 现成的 %p：后者是 HH:MM:SS，而冒号在 Windows 文件名里非法。
            Assert.Equal("攻壳机动队 %wH.%wM.%wS", MpvBaseline.ScreenshotTemplate("攻壳机动队"));

            // 服务器上的剧名带 : / ? 是常事，而这三个都不许进文件名。走的是「下载到设备」那同一份规矩
            // （DownloadPlan.Safe），所以这个仓库里只有一个答案说「文件名里能放什么」。
            var messy = MpvBaseline.ScreenshotTemplate("攻壳/机动队: SAC?2045");
            foreach (var illegal in new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' })
            {
                Assert.False(messy.Contains(illegal), $"模板里不许出现 {illegal}");
            }

            // 百分号得自己再挡一次：Safe() 没理由管它，可 mpv 会把它读成一个自己的格式符。
            Assert.False(MpvBaseline.ScreenshotTemplate("100%纯度 %n").Contains("%n"),
                "片名里的百分号不许变成 mpv 的格式符");
            Assert.True(MpvBaseline.ScreenshotTemplate("100%纯度").StartsWith("100纯度"));

            // 片名一个字都不剩的时候不能落成一个只有时间码的名字，也不能是 mpv 默认那个 mpv-shotNNNN。
            Assert.Equal("EmbyNian %wH.%wM.%wS", MpvBaseline.ScreenshotTemplate("  ..  "));
            Assert.Equal("EmbyNian %wH.%wM.%wS", MpvBaseline.ScreenshotTemplate(null));
        });

        Test("计划：轨道建议按偏好语言给出默认值", () =>
        {
            var (planner, settings) = Planner();
            settings.Playback.AudioLanguages = ["日语"];
            settings.Playback.SubtitleLanguages = ["中文"];

            var auto = planner.SuggestTracks(Source());

            Assert.Equal(1, auto.Audio!.Index, "日语音轨");
            Assert.Equal(3, auto.Subtitle.Stream!.Index, "中文字幕");
            Assert.False(auto.Subtitle.Disabled);
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
            settings.Playback.SubtitleLanguages = ["简体中文", "中文", "繁体中文"];
            settings.Playback.AudioLanguages = ["英语"];

            var request = planner.Plan(Ticket(), Connection());

            Assert.Equal("zh-Hans,zh_hans,zh-CN,zh_CN,zhs,sc,chs,chi-Hans,zh,chi,zho,zh-Hant,zh_hant,zh-TW,zh_TW,zh-HK,zh_HK,zht,tc,chi-Hant",
                request.SubtitleLanguage);
            Assert.Equal("eng,en", request.AudioLanguage);
        });

        Test("计划：音轨语言是列表，按顺序展开成 alang", () =>
        {
            // v9 之前这一项只放得下一种语言，「日语 > 粤语 > 英语」根本表达不出来 —— 而 mpv 的 alang 本来就吃列表。
            var (planner, settings) = Planner();
            settings.Playback.AudioLanguages = ["日语", "粤语", "英语"];

            var request = planner.Plan(Ticket(), Connection());

            Assert.Equal("jpn,ja,yue,zh-HK,eng,en", request.AudioLanguage,
                "三种语言按填的顺序展开，每种的候选码也按它自己的顺序");
        });

        Test("计划：空的语言优先级不传 slang/alang", () =>
        {
            var (planner, settings) = Planner();
            settings.Playback.SubtitleLanguages = [];
            settings.Playback.AudioLanguages = [];
            var request = planner.Plan(Ticket(), Connection());
            Assert.Null(request.SubtitleLanguage);
            Assert.Null(request.AudioLanguage, "跟随默认音轨时不该把语言写进 --alang");
        });

        Test("计划：字幕字体按字体族名传给 mpv，路径会被换成族名", () =>
        {
            var (planner, settings) = Planner();

            // --sub-font 只认字体族名。v3 存的是 C:\Windows\Fonts 下的文件路径，mpv 找不到这个「族」
            // 就悄悄退回 sans-serif；当时没人发现，是因为用户自己的 mpv.conf 里另写了一个真族名。
            settings.Playback.SubtitleFontFamily = @"C:\Windows\Fonts\msyh.ttc";
            Assert.Equal("Microsoft YaHei", planner.Plan(Ticket(), Connection()).SubtitleFont, "认得的文件换成族名");

            settings.Playback.SubtitleFontFamily = @"D:\字体\我自己的字体.ttf";
            Assert.Equal(FontFamilies.Default, planner.Plan(Ticket(), Connection()).SubtitleFont,
                "认不出来的文件宁可退回微软雅黑，也不能把路径当族名传出去");

            settings.Playback.SubtitleFontFamily = "思源黑体 CN";
            Assert.Equal("思源黑体 CN", planner.Plan(Ticket(), Connection()).SubtitleFont);

            settings.Playback.SubtitleFontFamily = "  ";
            Assert.Equal(FontFamilies.Default, planner.Plan(Ticket(), Connection()).SubtitleFont, "不填时用系统一定有的族");
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

    // ---- 自动选轨 --------------------------------------------------------------

    private static void RegisterTrackSelection()
    {
        Test("选轨：字幕按优先级顺序命中第一个有的语言", () =>
        {
            var source = SourceWith(
                Stream(0, "Video", height: 1080),
                Stream(1, "Subtitle", language: "chi", title: "繁體中文"),
                Stream(2, "Subtitle", language: "eng"));

            var settings = Playback(subtitles: ["简体中文", "英语", "繁体中文"]);
            var auto = TrackSelection.Resolve(settings, source);

            Assert.Equal(2, auto.Subtitle.Stream!.Index, "没有简体，就该轮到英语，而不是直接挑第一条");

            settings.SubtitleLanguages = ["繁体中文", "英语"];
            Assert.Equal(1, TrackSelection.Resolve(settings, source).Subtitle.Stream!.Index, "繁体排在前面就该选它");
        });

        Test("选轨：简体优先时不会挑走标着繁体的轨道", () =>
        {
            // 两条都只写 chi，差别只在标题上——这正是交给 mpv 的 slang 做不到的事。
            var source = SourceWith(
                Stream(0, "Video", height: 1080),
                Stream(1, "Subtitle", language: "chi", title: "繁體中文特效"),
                Stream(2, "Subtitle", language: "chi", title: "简体中文"));

            var auto = TrackSelection.Resolve(Playback(subtitles: ["简体中文"]), source);
            Assert.Equal(2, auto.Subtitle.Stream!.Index);
        });

        Test("选轨：一个语言都对不上时按开关决定回退还是关掉", () =>
        {
            var source = SourceWith(
                Stream(0, "Video", height: 1080),
                Stream(1, "Subtitle", language: "kor"));

            var settings = Playback(subtitles: ["简体中文"]);
            Assert.Equal(1, TrackSelection.Resolve(settings, source).Subtitle.Stream!.Index, "默认回退到文件自带的字幕");

            settings.SubtitleFallbackToDefault = false;
            var off = TrackSelection.Resolve(settings, source).Subtitle;
            Assert.Null(off.Stream);
            Assert.True(off.Disabled, "关掉回退就该明确不显示字幕");
        });

        Test("选轨：字幕模式 强制/外语/关闭", () =>
        {
            var source = SourceWith(
                Stream(0, "Video", height: 1080),
                Stream(1, "Audio", language: "chi"),
                Stream(2, "Subtitle", language: "chi"),
                Stream(3, "Subtitle", language: "chi", forced: true));

            var settings = Playback(subtitles: ["中文"]);

            settings.SubtitleMode = SubtitleMode.ForcedOnly;
            Assert.Equal(3, TrackSelection.Resolve(settings, source).Subtitle.Stream!.Index, "只要强制字幕");

            settings.SubtitleMode = SubtitleMode.ForeignAudioOnly;
            Assert.True(TrackSelection.Resolve(settings, source).Subtitle.Disabled, "音轨已经是中文，不需要字幕");

            settings.SubtitleMode = SubtitleMode.Off;
            Assert.True(TrackSelection.Resolve(settings, source).Subtitle.Disabled);
        });

        Test("选轨：只要强制字幕但文件一条都没有时不退回整轨", () =>
        {
            var source = SourceWith(
                Stream(0, "Video", height: 1080),
                Stream(1, "Subtitle", language: "chi"));

            var settings = Playback(subtitles: ["中文"]);
            settings.SubtitleMode = SubtitleMode.ForcedOnly;

            Assert.True(TrackSelection.Resolve(settings, source).Subtitle.Disabled, "整轨翻译不是强制字幕的替代品");
        });

        Test("选轨：音轨选默认或单选一种语言，没有优先级", () =>
        {
            var source = new MediaSource
            {
                Id = "src1",
                DefaultAudioStreamIndex = 2,
                MediaStreams =
                [
                    Stream(0, "Video", height: 1080),
                    Stream(1, "Audio", language: "jpn"),
                    Stream(2, "Audio", language: "chi")
                ]
            };

            var settings = Playback(subtitles: []);
            Assert.Equal(2, TrackSelection.ChooseAudio(settings, source)!.Index, "跟随文件自己的默认轨");

            settings.AudioLanguages = ["日语"];
            Assert.Equal(1, TrackSelection.ChooseAudio(settings, source)!.Index);

            settings.AudioLanguages = ["韩语"];
            Assert.Equal(2, TrackSelection.ChooseAudio(settings, source)!.Index, "没有韩语音轨就回到默认轨，而不是没有声音");

            // 列表按顺序问：韩语这个文件没有，粤语也没有（chi 不是 yue），落到日语。这一条是「音轨语言从
            // 单选变成优先级列表」的核心 —— 单选表达不出「首选韩语，其次日语」。
            settings.AudioLanguages = ["韩语", "日语"];
            Assert.Equal(1, TrackSelection.ChooseAudio(settings, source)!.Index, "第一种没有就问第二种，而不是直接回默认轨");
        });
    }

    // ---- 视频与音频输出 ---------------------------------------------------------

    private static void RegisterOutputOptions()
    {
        Test("输出：出厂设置给出的正是原先 mpv.conf 里那几条", () =>
        {
            // 配置文件不读之后，客户端不设的就没人设了，所以出厂值不再是一片空白。
            var options = Options(MpvOutputOptions.Build(new VideoSettings(), new AudioSettings(), new PlaybackSettings()));

            Assert.Equal("gpu-next", options["vo"]);
            Assert.Equal("vulkan", options["gpu-api"],
                "v8 起图形接口出厂是 vulkan：同一条带 compute pass 的链在 d3d11 上只有 8.7 fps、vulkan 上 45（2026-09-04 实测）");
            Assert.Equal("auto", options["dither-depth"]);
            Assert.Equal("fruit", options["dither"]);
            Assert.Equal("6", options["dither-size-fruit"]);
            Assert.Equal("full", options["video-output-levels"], "「色彩范围默认使用 PC(0-255)」");
            Assert.Equal("auto-safe", options["hwdec"],
                "v7 起硬解出厂就是「自动」：留空等于 mpv 的 no，也就是纯软件解码，那不是谁挑的，是装机时碰巧带着的");
            Assert.False(options.ContainsKey("profile"), "画质预设出厂是 default，什么都不该传");
        });

        Test("输出：每一项都能单独关回 mpv 自己的默认值", () =>
        {
            var blank = new VideoSettings
            {
                QualityPreset = "",
                Renderer = "",
                GpuApi = "",
                HardwareDecoding = "",
                OutputLevels = "",
                Dither = "",
                Deband = "",
                HdrMode = "",
                HighFrameRateAudioSync = false
            };

            var options = MpvOutputOptions.Build(blank, new AudioSettings());
            Assert.Equal(0, options.Count, "全部选「不指定」时就该一个选项都不传");
        });

        // 「画质与着色器板块新增 mpv 画质预设配置 profile=high-quality、profile=default」，加上 2026-09-04 换掉的那一
        // 项：HQ（用户 mpv.conf 里那个手抄的段）删掉、fast（mpv 自己内置的）补上。第 0 项当天试过改名叫「无」（空值），
        // 用户一句「改回 default」送回来了 —— 两种写法都是什么都不传，所以那是他的用词，不是这一层的行为。
        Test("输出：画质预设就这三项，全都是 mpv 自己内置的 profile", () =>
        {
            Assert.Equal(
                "default、fast、high-quality",
                string.Join("、", MpvOutputOptions.QualityPresets.Select(choice => choice.Value)),
                "多一项少一项都要有人想一想：预设是逐字交给 mpv 的 profile 名，mpv 不认识就直接退出，什么都不播");

            Assert.Equal("default", MpvOutputOptions.QualityPresets[0].Value,
                "SettingsMigration 认不出来的值退回第 0 项，那一项必须是「不套用」");
        });

        Test("输出：画质预设 default 什么都不传，fast 和 high-quality 走 mpv 内置 profile", () =>
        {
            var none = MpvOutputOptions.Build(new VideoSettings { QualityPreset = "default", OutputLevels = "" }, new AudioSettings());
            Assert.False(Options(none).ContainsKey("profile"), "mpv 的 [default] 在 --no-config 下是空的，传了也是白传");

            var high = Options(MpvOutputOptions.Build(new VideoSettings { QualityPreset = "high-quality" }, new AudioSettings()));
            Assert.Equal("high-quality", high["profile"], "这个 profile 编在 mpv 里，不靠配置文件");

            var fast = Options(MpvOutputOptions.Build(new VideoSettings { QualityPreset = "fast" }, new AudioSettings()));
            Assert.Equal("fast", fast["profile"], "fast 同样编在 mpv 里（发布件那份 libmpv 和外部 mpv.exe 都有）");
        });

        Test("输出：画质预设只交一个 profile 名，三个缩放器一个都不碰", () =>
        {
            // 删掉的那个 HQ 是用户 mpv.conf 里手抄来的一段，会逐条展开成 scale-antiring / 光域那几项；而 2026-09-03
            // 之所以先把三个缩放器从它身上拿掉，是因为档位链最后才发出去、谁都盖不住它，于是设置页写着「画质预设 =
            // HQ」而生效的是链里的 cscale=spline36 —— 界面在骗人。现在三项预设全是 mpv 内置的 profile 名，客户端
            // 这一侧一条选项都不再手写，那件事从根上没了地方发生。
            foreach (var preset in MpvOutputOptions.QualityPresets.Select(choice => choice.Value))
            {
                var options = MpvOutputOptions.Build(
                    new VideoSettings
                    {
                        QualityPreset = preset,
                        Renderer = "",
                        GpuApi = "",
                        HardwareDecoding = "",
                        OutputLevels = "",
                        Dither = "",
                        Deband = "",
                        HdrMode = "",
                        HighFrameRateAudioSync = false
                    },
                    new AudioSettings());

                Assert.Equal(
                    preset == "default" ? "" : "profile",
                    string.Join("、", options.Select(pair => pair.Key)),
                    $"画质预设 {preset} 除了一个 profile 名不该再传任何东西");
            }
        });

        Test("输出：画质预设排在其他视频设置之前，谁在后面谁说了算", () =>
        {
            // 顺序就是覆盖关系：画质预设是底，视频输出压在上面，着色器配置组最后。
            var options = MpvOutputOptions.Build(
                new VideoSettings { QualityPreset = "high-quality", Renderer = "gpu" },
                new AudioSettings());

            var preset = -1;
            var renderer = -1;
            for (var index = 0; index < options.Count; index++)
            {
                if (options[index].Key == "profile") preset = index;
                if (options[index].Key == "vo") renderer = index;
            }

            Assert.True(preset >= 0 && renderer > preset, $"画质预设必须在最前（实际 {preset} / {renderer}）");
        });

        Test("输出：视频设置落到 mpv 选项名上", () =>
        {
            var video = new VideoSettings
            {
                Renderer = "gpu-next",
                GpuApi = "d3d11",
                HardwareDecoding = "d3d11va",
                OutputLevels = "limited",
                Deinterlace = true,
                NetworkCacheMegabytes = 200
            };

            var options = Options(MpvOutputOptions.Build(video, new AudioSettings()));

            Assert.Equal("gpu-next", options["vo"]);
            Assert.Equal("d3d11", options["gpu-api"]);
            Assert.Equal("d3d11va", options["hwdec"]);
            Assert.Equal("limited", options["video-output-levels"]);
            Assert.Equal("yes", options["deinterlace"]);
            Assert.Equal("yes", options["cache"]);
            Assert.Equal((200L * 1024 * 1024).ToString(), options["demuxer-max-bytes"]);
        });

        Test("输出：开插值必须同时打开显示同步", () =>
        {
            var interpolation = Options(MpvOutputOptions.Build(new VideoSettings { Interpolation = true }, new AudioSettings()));
            Assert.Equal("yes", interpolation["interpolation"]);
            Assert.Equal("display-resample", interpolation["video-sync"], "没有显示同步的插值在 mpv 里是无效的");

            var chosen = Options(MpvOutputOptions.Build(
                new VideoSettings { Interpolation = true, VideoSync = "display-vdrop" },
                new AudioSettings()));
            Assert.Equal("display-vdrop", chosen["video-sync"], "用户自己选了就不覆盖");
        });

        Test("输出：音频直通按目录顺序拼成 audio-spdif", () =>
        {
            var audio = new AudioSettings
            {
                PassthroughCodecs = ["truehd", "ac3"],
                Channels = "5.1",
                DynamicRange = "0.5",
                ExclusiveMode = true,
                DelayMilliseconds = -250
            };

            var options = Options(MpvOutputOptions.Build(new VideoSettings(), audio));

            Assert.Equal("ac3,truehd", options["audio-spdif"], "顺序按目录，而不是按用户勾选的先后");
            Assert.Equal("5.1", options["audio-channels"]);
            Assert.Equal("0.5", options["ad-lavc-ac3drc"]);
            Assert.Equal("yes", options["audio-exclusive"]);
            Assert.Equal("-0.25", options["audio-delay"]);
        });

        Test("输出：音量记的是上次离开播放器时那个值", () =>
        {
            // mpv 每次都是新起的，而且自己的配置被挡掉了，所以「没人设过的音量」永远是 100。要让换一集之后音量
            // 还是上次那个，只能靠这个选项 ——「调整完音量后换个媒体播放音量会变回 100」说的就是它。
            var remembered = Options(MpvOutputOptions.Build(new VideoSettings(), new AudioSettings { Volume = 37 }));
            Assert.Equal("37", remembered["volume"]);

            var muted = Options(MpvOutputOptions.Build(new VideoSettings(), new AudioSettings { Volume = 0 }));
            Assert.Equal("0", muted["volume"], "拉到底也是个值，不能当成没设过");

            var untouched = Options(MpvOutputOptions.Build(new VideoSettings(), new AudioSettings()));
            Assert.False(untouched.ContainsKey("volume"), "100 本来就是新起的 mpv 的音量，不必多说一句");

            // 天花板从 100 抬到 130 那一下的回归测试。从前这里写的是 `is >= 0 and < 100`，本意是「100 不必发」，
            // 可天花板一抬，存着的 130 就一次都发不出去：mpv 每次从 100 开始，而屏上那根滑杆停在 130 —— 又一个
            // 界面在骗人。这一条必须钉住上限那个值本身发得出去。
            var boosted = Options(MpvOutputOptions.Build(
                new VideoSettings(), new AudioSettings { Volume = AudioSettings.MaxVolume }));
            Assert.Equal("130", boosted["volume"], "存着 130 就要真的发出 volume=130");

            var silly = Options(MpvOutputOptions.Build(new VideoSettings(), new AudioSettings { Volume = 3000 }));
            Assert.False(silly.ContainsKey("volume"),
                "手改过的设置文件不该把音量顶到天上去 —— mpv.exe 碰到超出 volume-max 的值会直接退出");
        });

        Test("输出：音量均衡和下混归一化在 af 上共存", () =>
        {
            // af 是个列表选项，而这两件事都是「让对白听得清」。写成一条 af 里的两个滤镜就会互相覆盖，所以
            // 音量均衡走 af、下混归一化走 audio-normalize-downmix，四种组合都要成立。
            var neither = Options(MpvOutputOptions.Build(new VideoSettings(), new AudioSettings()));
            Assert.False(neither.ContainsKey("af"), "都不开就一条 af 都不发");
            Assert.False(neither.ContainsKey("audio-normalize-downmix"), "都不开也不发下混归一化");

            var normalizeOnly = Options(MpvOutputOptions.Build(
                new VideoSettings(), new AudioSettings { VolumeNormalize = MpvOutputOptions.DynAudNorm }));
            Assert.Equal(MpvOutputOptions.DynAudNorm, normalizeOnly["af"]);
            Assert.False(normalizeOnly.ContainsKey("audio-normalize-downmix"));

            var downmixOnly = Options(MpvOutputOptions.Build(
                new VideoSettings(), new AudioSettings { NormalizeDownmix = true }));
            Assert.False(downmixOnly.ContainsKey("af"));
            Assert.Equal("yes", downmixOnly["audio-normalize-downmix"]);

            var both = MpvOutputOptions.Build(new VideoSettings(), new AudioSettings
            {
                VolumeNormalize = MpvOutputOptions.LoudNorm,
                NormalizeDownmix = true
            });

            var pairs = Options(both);
            Assert.Equal(MpvOutputOptions.LoudNorm, pairs["af"], "两个都开时 af 还是那一条完整的滤镜串");
            Assert.Equal("yes", pairs["audio-normalize-downmix"]);
            Assert.Equal(1, both.Count(option => option.Key == "af"), "af 只许出现一次，否则后一条盖掉前一条");
        });

        Test("音量均衡：滤镜串只写一处，播放器菜单和设置页读的是同一份", () =>
        {
            // 这两处从前各写一遍同样的滤镜串。写两遍就会飘，而「菜单里的音量均衡和设置里的音量均衡不是一回事」
            // 正是这个项目一直在还的那类债。
            var row = PlayerMenuCatalog.Flatten(PlayerMenuCatalog.Root)
                .FirstOrDefault(node => node.Label == "切换 音量均衡");

            Assert.NotNull(row, "播放器菜单里得有「切换 音量均衡」这一行");

            var arguments = row!.Commands[0];
            Assert.True(arguments.Contains(MpvOutputOptions.DynAudNorm), "菜单里那一档必须是 MpvOutputOptions 的原串");
            Assert.True(arguments.Contains(MpvOutputOptions.LoudNorm), "另一档同理");

            // 目录里那三档：空值加上这两条，一个不多一个不少。
            Assert.Equal(
                $"|{MpvOutputOptions.DynAudNorm}|{MpvOutputOptions.LoudNorm}",
                string.Join("|", MpvOutputOptions.VolumeNormalizers.Select(choice => choice.Value)),
                "音量均衡就这三档，而且「不启用」必须是第 0 项（SettingsMigration 认不出的值退到那里）");
        });

        Test("输出：字幕外观带上颜色的不透明度", () =>
        {
            var subtitles = new PlaybackSettings
            {
                SubtitleFontSize = 48,
                SubtitleColor = "#FFF200",
                SubtitleBorderSize = "3",
                SubtitleBackColor = "#000000",
                SubtitleBackOpacity = 50
            };

            var options = Options(MpvOutputOptions.Build(new VideoSettings(), new AudioSettings(), subtitles));

            Assert.Equal("48", options["sub-font-size"]);
            Assert.Equal("1.000/0.949/0.000/1.000", options["sub-color"], "文字本身永远不透明");
            Assert.Equal("3", options["sub-border-size"]);
            Assert.Equal("0.000/0.000/0.000/0.500", options["sub-back-color"]);
        });

        Test("输出：选「无背景」时背景色是全透明而不是缺省", () =>
        {
            var options = Options(MpvOutputOptions.Build(
                new VideoSettings(),
                new AudioSettings(),
                new PlaybackSettings
                {
                    SubtitleBackColor = MpvOutputOptions.NoBackground,
                    SubtitleBackOpacity = 80,
                    SubtitleFontSize = 0
                }));

            Assert.Equal("0.000/0.000/0.000/0.000", options["sub-back-color"]);
            Assert.False(options.ContainsKey("sub-font-size"), "字号填 0 表示用 mpv 自己的默认字号");
        });

        Test("输出：字幕出厂样式是细描边配粗体", () =>
        {
            var options = Options(MpvOutputOptions.Build(new VideoSettings(), new AudioSettings(), new PlaybackSettings()));

            Assert.Equal("50", options["sub-font-size"]);
            Assert.Equal("yes", options["sub-bold"]);
            Assert.Equal("0.5", options["sub-border-size"]);
            Assert.Equal("0.000/0.000/0.000/1.000", options["sub-border-color"]);
            Assert.Equal("0.5", options["sub-shadow-offset"]);
            Assert.Equal("gb18030", options["sub-codepage"], "mpv 先按 UTF-8 试，这条只对不是 UTF-8 的老字幕生效");
            Assert.False(options.ContainsKey("sub-back-color"), "默认不画背景框，字幕自己的样式说了算");
        });

        Test("输出：宽画面的图形字幕才拉伸到画面", () =>
        {
            var subtitles = new PlaybackSettings();

            var wide = Options(MpvOutputOptions.Build(
                new VideoSettings(), new AudioSettings(), subtitles, new SourceProfile(3840, 1600, 10, 24, false)));
            Assert.Equal("yes", wide["stretch-image-subs-to-screen"], "2.39:1 的编码切掉了黑边，PGS 会掉到画面外");

            var standard = Options(MpvOutputOptions.Build(
                new VideoSettings(), new AudioSettings(), subtitles, new SourceProfile(1920, 1080, 8, 24, false)));
            Assert.False(standard.ContainsKey("stretch-image-subs-to-screen"), "16:9 的片源不需要动");
        });

        Test("输出：去色带只对 8bit 片源自动打开", () =>
        {
            var video = new VideoSettings();

            var eightBit = Options(MpvOutputOptions.Build(video, new AudioSettings(), null, new SourceProfile(1920, 1080, 8, 24, false)));
            Assert.Equal("yes", eightBit["deband"]);
            Assert.Equal("1", eightBit["deband-iterations"], "核显只吃得下一轮");
            Assert.Equal("48", eightBit["deband-threshold"]);
            Assert.Equal("16", eightBit["deband-range"]);
            Assert.Equal("16", eightBit["deband-grain"]);

            var tenBit = Options(MpvOutputOptions.Build(video, new AudioSettings(), null, new SourceProfile(3840, 2160, 10, 24, false)));
            Assert.Equal("no", tenBit["deband"], "10bit 片源本来就有去色带要凭空造出来的精度");
        });

        // 「去色带的选项中新增，在动漫中开启」
        Test("输出：去色带「在动画中开启」只看是不是动画，不看位深", () =>
        {
            var video = new VideoSettings { Deband = MpvOutputOptions.Anime };
            var tenBitSource = new SourceProfile(1920, 1080, 10, 24, false);

            var anime = Options(MpvOutputOptions.Build(video, new AudioSettings(), null, tenBitSource, animated: true));
            Assert.Equal("yes", anime["deband"], "动画的平涂大色块最容易出色带，10bit 也一样");
            Assert.Equal("1", anime["deband-iterations"]);

            var live = Options(MpvOutputOptions.Build(video, new AudioSettings(), null, new SourceProfile(1920, 1080, 8, 24, false)));
            Assert.Equal("no", live["deband"], "实拍片不开：这一项选的就是「只在动画里开」");
        });

        Test("输出：去色带其余几档不受动画标记影响", () =>
        {
            var source = new SourceProfile(1920, 1080, 10, 24, false);

            Assert.Equal("yes", Options(MpvOutputOptions.Build(
                new VideoSettings { Deband = "yes" }, new AudioSettings(), null, source, animated: false))["deband"]);
            Assert.Equal("no", Options(MpvOutputOptions.Build(
                new VideoSettings { Deband = "no" }, new AudioSettings(), null, source, animated: true))["deband"]);
            Assert.False(Options(MpvOutputOptions.Build(
                new VideoSettings { Deband = "" }, new AudioSettings(), null, source, animated: true)).ContainsKey("deband"),
                "选「不设置」就是一条都不传");
        });

        Test("输出：HDR 只对 HDR 片源发选项，两种模式各自成套", () =>
        {
            var sdr = Options(MpvOutputOptions.Build(
                new VideoSettings(), new AudioSettings(), null, new SourceProfile(1920, 1080, 8, 24, false)));
            Assert.False(sdr.ContainsKey("tone-mapping"), "SDR 片源上这些选项条条都是空操作");

            var hdr = new SourceProfile(3840, 2160, 10, 24, true);

            var tonemap = Options(MpvOutputOptions.Build(new VideoSettings(), new AudioSettings(), null, hdr));
            Assert.Equal("no", tonemap["target-colorspace-hint"]);
            Assert.Equal("auto", tonemap["tone-mapping"]);
            Assert.Equal("auto", tonemap["hdr-compute-peak"]);

            var passthrough = Options(MpvOutputOptions.Build(
                new VideoSettings { HdrMode = "passthrough" }, new AudioSettings(), null, hdr));
            Assert.Equal("yes", passthrough["target-colorspace-hint"]);
            Assert.Equal("clip", passthrough["tone-mapping"], "交给显示器映射之后再映射一次就是映射两遍");
            Assert.Equal("no", passthrough["hdr-compute-peak"]);
        });

        Test("输出：自动 ICC 校色关着一条都不发，打开了才发 yes", () =>
        {
            var off = Options(MpvOutputOptions.Build(new VideoSettings(), new AudioSettings()));
            Assert.False(off.ContainsKey("icc-profile-auto"),
                "装机默认是关的，而基线那条 icc-profile-auto=no 已经把「关」说清楚了，这里再发一遍就是两层写同一个选项");

            var on = Options(MpvOutputOptions.Build(new VideoSettings { IccProfileAuto = true }, new AudioSettings()));
            Assert.Equal("yes", on["icc-profile-auto"]);
        });

        Test("输出：高帧率片源回退到音频同步", () =>
        {
            var video = new VideoSettings { Interpolation = true };

            var high = Options(MpvOutputOptions.Build(video, new AudioSettings(), null, new SourceProfile(1920, 1080, 8, 60, false)));
            Assert.Equal("audio", high["video-sync"], "显示同步已经没有多余的节拍可以重采样了");
            Assert.Equal("no", high["interpolation"], "60fps 的片源没有东西可插");

            var normal = Options(MpvOutputOptions.Build(video, new AudioSettings(), null, new SourceProfile(1920, 1080, 8, 23.976, false)));
            Assert.Equal("display-resample", normal["video-sync"]);
            Assert.Equal("yes", normal["interpolation"]);
        });

        // 2026-09-04 实测（interp-cost.ps1，1080p 全屏到 2560×1440、gpu-next + vulkan、不挂链）：显示同步会把 mpv
        // 最后一趟渲染（混帧 + 色彩编码 + 抖动，约 1.1 毫秒）从每视频帧 24 次改成每次刷新 144 次，Windows 自己的
        // 进程 GPU 计数器从 24.7% 涨到 50.1%；而 144 ÷ 24 = 6.000，没有节奏要补，vo-passes 几乎只报
        // 「frame mixing (1 frame)」。用户自己那份 mpv.conf 里的 [fps-fix] 用的就是 display-fps > 120 这条规则。
        Test("输出：高刷新率屏幕同样回退到音频同步，插值跟着不生效", () =>
        {
            var video = new VideoSettings { Interpolation = true };
            var film = new SourceProfile(1920, 1080, 8, 23.976, false);

            var fast = Options(MpvOutputOptions.Build(video, new AudioSettings(), null, film, false, 144));
            Assert.Equal("audio", fast["video-sync"], "144Hz 上显示同步只剩算力开销");
            Assert.Equal("no", fast["interpolation"], "插值开着也要显式发 no —— 复用的 mpv 实例上「不发」等于沿用上一部片子的 yes");
            Assert.False(fast.ContainsKey("tscale"), "插值没生效就不该留下 tscale");

            // 一档一档地过边界：120 本身不算超过，60Hz 屏和「读不到刷新率」都照旧走显示同步。
            foreach (var hz in new[] { 0.0, 60, 100, 120 })
            {
                var kept = Options(MpvOutputOptions.Build(video, new AudioSettings(), null, film, false, hz));
                Assert.Equal("display-resample", kept["video-sync"], $"{hz}Hz：这里显示同步是划算的");
                Assert.Equal("yes", kept["interpolation"], $"{hz}Hz：插值应该生效");
            }

            Assert.Equal("display-resample",
                Options(MpvOutputOptions.Build(
                    new VideoSettings { Interpolation = true, HighFrameRateAudioSync = false },
                    new AudioSettings(), null, film, false, 144))["video-sync"],
                "关掉那个开关就该把显示同步还给任何屏幕");

            // 两条规则各自都要能说出自己为什么出手 —— 日志和设置页读的是同一句话。
            var (_, _, byRefresh) = MpvOutputOptions.ResolveSync(video, film, 144);
            var (_, _, byFrameRate) = MpvOutputOptions.ResolveSync(video, new SourceProfile(1920, 1080, 8, 59.94, false), 60);
            var (_, _, neither) = MpvOutputOptions.ResolveSync(video, film, 60);

            Assert.Contains("144", byRefresh ?? "", "那句话要带上量到的刷新率");
            Assert.Contains("59.94", byFrameRate ?? "", "那句话要带上片源帧率");
            Assert.True(neither is null, "没回退就不该有话说");
        });

        Test("输出：设置页读到的「此刻生效」和真正发出去的 video-sync 是同一个答案", () =>
        {
            // 「界面在骗人」那一类的机械闸门。从前 video-sync 有两个写手：设置里存的那一项，和插值那条
            // 「留空就改成 display-resample」，再加高帧率那条又写一遍 —— 设置页显示第一个，mpv 收到最后一个。
            // 现在只有 ResolveSync 一个写手，这一条把它和 Build 真正发出去的东西对起来，两个方向都对。
            // 刷新率那一轴 2026-09-04 加进来：它是第四个可能改写这两项的东西，也就是第四个能让两边分家的地方。
            foreach (var stored in new[] { "", "audio", "display-resample", "display-vdrop" })
            {
                foreach (var interpolation in new[] { false, true })
                {
                    foreach (var fallback in new[] { false, true })
                    {
                        foreach (var fps in new[] { 0.0, 23.976, 59.94 })
                        {
                            foreach (var hz in new[] { 0.0, 60, 144 })
                            {
                                var video = new VideoSettings
                                {
                                    VideoSync = stored,
                                    Interpolation = interpolation,
                                    HighFrameRateAudioSync = fallback
                                };
                                SourceProfile? source = fps > 0 ? new SourceProfile(1920, 1080, 8, fps, false) : null;

                                var (sync, live, _) = MpvOutputOptions.ResolveSync(video, source, hz);
                                var sent = Options(MpvOutputOptions.Build(video, new AudioSettings(), null, source, false, hz));
                                var what = $"存「{(stored.Length == 0 ? "不指定" : stored)}」、插值 {interpolation}、"
                                    + $"回退 {fallback}、片源 {(fps > 0 ? fps + "fps" : "帧率未知")}、"
                                    + $"屏幕 {(hz > 0 ? hz + "Hz" : "刷新率未知")}";

                                Assert.Equal(sync, sent.TryGetValue("video-sync", out var emitted) ? emitted : "",
                                    $"{what}：video-sync");

                                // 插值开着却被回退规则否掉时必须显式发 no —— 什么都不发在一个复用的 mpv 实例上
                                // 等于沿用上一部片子的 yes。
                                Assert.Equal(live ? "yes" : interpolation ? "no" : "",
                                    sent.TryGetValue("interpolation", out var flag) ? flag : "",
                                    $"{what}：interpolation");

                                Assert.Equal(live, sent.ContainsKey("tscale"), $"{what}：tscale 只跟着真的开着的插值走");
                            }
                        }
                    }
                }
            }
        });
    }

    // ---- 跳过片头片尾 ----------------------------------------------------------

    private static void RegisterSkipSections()
    {
        Test("跳过：命名章节同时给出片头和片尾", () =>
        {
            var sections = SkipSectionPlanner.Resolve(Episode(
                Chapter(0, "片头"),
                Chapter(88, "正片"),
                Chapter(1380, "片尾")));

            Assert.Equal(2, sections.Count);

            Assert.Equal(SkipSectionKind.Opening, sections[0].Kind);
            Assert.Equal(0, sections[0].Start);
            Assert.Equal(88, sections[0].End);
            Assert.True(sections[0].Certain, "章节名给出的区间不是推测");
            Assert.Equal("跳过片头", sections[0].Caption);

            Assert.Equal(SkipSectionKind.Ending, sections[1].Kind);
            Assert.Equal(1380, sections[1].Start);
            Assert.Equal(1500, sections[1].End, "最后一个章节一直放到片尾");
        });

        Test("跳过：冷开场时命名章节不在第一位", () =>
        {
            var sections = SkipSectionPlanner.Resolve(Episode(
                Chapter(0, "前情提要"),
                Chapter(65, "OP"),
                Chapter(155, "正片")));

            // 前情提要 紧接着 OP，合成一次跳跃：分开就要按两下才进正片。
            Assert.Equal(1, sections.Count);
            Assert.Equal(0, sections[0].Start);
            Assert.Equal(155, sections[0].End);
            Assert.Equal("跳过片头", sections[0].Caption, "合并后用落点那一段的名字");
        });

        Test("跳过：ED / Preview 都算片尾，并且合成一段", () =>
        {
            var sections = SkipSectionPlanner.Resolve(Episode(
                Chapter(0, "正片"),
                Chapter(1320, "ED"),
                Chapter(1410, "Preview")));

            Assert.Equal(1, sections.Count);
            Assert.Equal(SkipSectionKind.Ending, sections[0].Kind);
            Assert.Equal(1320, sections[0].Start);
            Assert.Equal(1500, sections[0].End);
            Assert.Equal("跳过片尾", sections[0].Caption, "合并后用起点那一段的名字");
        });

        Test("跳过：电影也认命名章节", () =>
        {
            var sections = SkipSectionPlanner.Resolve(Watchable(
                EmbyItemType.Movie,
                Chapter(0, "Opening Titles"),
                Chapter(75, "Act One")));

            Assert.Equal(1, sections.Count);
            Assert.Equal(75, sections[0].End);
        });

        Test("跳过：没有章节名时按位置和长度推测", () =>
        {
            var sections = SkipSectionPlanner.Resolve(Episode(Chapter(0), Chapter(92), Chapter(1200)));

            Assert.Equal(1, sections.Count);
            Assert.Equal(92, sections[0].End);
            Assert.False(sections[0].Certain, "章节长度只是推测");
        });

        Test("跳过：无名章节的长度必须落在 80–100 秒", () =>
        {
            Assert.Equal(0, SkipSectionPlanner.Resolve(Episode(Chapter(0), Chapter(420), Chapter(1200))).Count);
            Assert.Equal(0, SkipSectionPlanner.Resolve(Episode(Chapter(0), Chapter(9), Chapter(1200))).Count);
        });

        Test("跳过：中间的无名章节是一场戏，不是片头片尾", () =>
        {
            var sections = SkipSectionPlanner.Resolve(Episode(
                Chapter(0),
                Chapter(300),
                Chapter(600),
                Chapter(690),
                Chapter(900),
                Chapter(1200)));

            Assert.Equal(0, sections.Count, "第 690 秒那段 90 秒的章节在正片中间");
        });

        Test("跳过：位置和名字冲突时以位置为准", () =>
        {
            Assert.Equal(
                0,
                SkipSectionPlanner.Resolve(Episode(Chapter(0, "正片"), Chapter(600, "OP"), Chapter(700, "继续"))).Count,
                "十分钟处的「OP」是标错的章节");

            Assert.Equal(
                0,
                SkipSectionPlanner.Resolve(Episode(Chapter(0, "ED"), Chapter(120, "正片"))).Count,
                "前半段的「ED」不是片尾");

            Assert.Equal(
                0,
                SkipSectionPlanner.Resolve(Episode(Chapter(0, "片头"), Chapter(6, "正片"), Chapter(1200))).Count,
                "六秒就结束的「片头」是发行商 logo");
        });

        Test("跳过：没有章节或文件太短就什么都不给", () =>
        {
            Assert.Equal(0, SkipSectionPlanner.Resolve(Episode()).Count, "没有章节就不猜，免得每集都冒出按钮");
            Assert.Equal(0, SkipSectionPlanner.Resolve(Watchable(EmbyItemType.Movie)).Count);
            Assert.Equal(0, SkipSectionPlanner.Resolve(null).Count);

            var short3Minutes = Watchable(EmbyItemType.Episode, Chapter(0), Chapter(85), Chapter(150));
            short3Minutes.RunTimeTicks = TimeSpan.FromMinutes(3).Ticks;
            Assert.Equal(0, SkipSectionPlanner.Resolve(short3Minutes).Count, "三分钟的文件没有片头可跳");
        });

        Test("跳过：Covers 只在区间内成立", () =>
        {
            var section = new SkipSection(SkipSectionKind.Opening, 65, 155, "片头", Certain: true);

            Assert.False(section.Covers(30), "冷开场还没放完");
            Assert.True(section.Covers(65));
            Assert.True(section.Covers(154));
            Assert.False(section.Covers(154.6), "临界半秒内不再提示，免得点了原地不动");
            Assert.False(section.Covers(160));
            Assert.False(section.Covers(-1), "尚未拿到位置时不提示");
        });
    }

    // ---- 进度条上的章节读数 ----------------------------------------------------

    private static void RegisterChapterTimeline()
    {
        Test("章节读数：落在最后一个不晚于该时刻的章节里", () =>
        {
            IReadOnlyList<SkipChapter> marks =
            [
                new SkipChapter(0, "片头"),
                new SkipChapter(90, "正片"),
                new SkipChapter(1380, "片尾")
            ];

            Assert.Equal(0, ChapterTimeline.IndexAt(marks, 0));
            Assert.Equal(0, ChapterTimeline.IndexAt(marks, 89.9));
            Assert.Equal(1, ChapterTimeline.IndexAt(marks, 90), "章节起点那一秒算在这一章里");
            Assert.Equal(1, ChapterTimeline.IndexAt(marks, 1379));
            Assert.Equal(2, ChapterTimeline.IndexAt(marks, 5000), "超出末尾仍留在最后一章");
        });

        Test("章节读数：没有章节，或者停在第一个之前，都是没有", () =>
        {
            Assert.Equal(-1, ChapterTimeline.IndexAt([], 42), "服务器没抽过章节的文件");
            Assert.Equal(-1, ChapterTimeline.IndexAt([new SkipChapter(12, null)], 5), "第一个标记之前");
            Assert.Equal(-1, ChapterTimeline.IndexAt([new SkipChapter(0, null)], -3), "还没量到位置");
        });

        Test("章节读数：没有名字就叫「章节 N」，编号从 1 起", () =>
        {
            IReadOnlyList<SkipChapter> marks =
            [
                new SkipChapter(0, null),
                new SkipChapter(90, "  正片  "),
                new SkipChapter(600, "   ")
            ];

            Assert.Equal("章节 1", ChapterTimeline.Caption(marks, 0));
            Assert.Equal("正片", ChapterTimeline.Caption(marks, 1), "两头的空白不要带进浮层");
            Assert.Equal("章节 3", ChapterTimeline.Caption(marks, 2), "只有空白等于没有名字");
        });

        Test("章节读数：没有章节时给空串，让那一行整条收起来", () =>
        {
            Assert.Equal("", ChapterTimeline.Caption([], 0));
            Assert.Equal("", ChapterTimeline.Caption([new SkipChapter(0, "片头")], -1));
            Assert.Equal("", ChapterTimeline.Caption([new SkipChapter(0, "片头")], 7), "越界不能抛");
        });
    }

    // ---- 跳过的提示与跳跃 ------------------------------------------------------

    private static void RegisterSkipCoordinator()
    {
        Test("跳过：进入区间后提示，15 秒无人理会就收回", () =>
        {
            var skips = Coordinator(out _);

            Assert.Null(skips.Advance(10, playing: true), "询问模式不会自己跳");
            Assert.True(skips.Prompt.Visible);
            Assert.Equal("跳过片头", skips.Prompt.Caption);
            Assert.Equal(1, skips.Prompt.Remaining, "刚出现时倒计时是满的");
            Assert.Contains("快捷键 Y", skips.Prompt.Tip);
            Assert.Contains("1:30", skips.Prompt.Tip, "提示里写清落点");

            skips.Advance(17.5, playing: true);
            Assert.True(skips.Prompt.Visible);
            Assert.Equal(0.5, skips.Prompt.Remaining, "倒计时随播放位置走");

            skips.Advance(25.1, playing: true);
            Assert.False(skips.Prompt.Visible, "不理会本身就是回答");

            skips.Advance(60, playing: true);
            Assert.False(skips.Prompt.Visible, "过期之后不会在同一段里再冒出来");
        });

        Test("跳过：没有可跳的文件时收回提示，恢复后重新计时", () =>
        {
            var skips = Coordinator(out _);

            skips.Advance(10, playing: true);
            Assert.True(skips.Prompt.Visible);

            // 最小化、失去控制通道、播放结束都走这条路。
            skips.Advance(10, playing: false);
            Assert.False(skips.Prompt.Visible);

            skips.Advance(12, playing: true);
            Assert.True(skips.Prompt.Visible);
            Assert.Equal(1, skips.Prompt.Remaining, "重新出现就重新给满 15 秒");
        });

        Test("跳过：接受之后给出落点，并且不再提示", () =>
        {
            var skips = Coordinator(out _);

            skips.Advance(10, playing: true);
            var jump = skips.Accept();

            Assert.NotNull(jump);
            Assert.Equal(90, jump!.Value.Target);
            Assert.Equal("已跳过片头: 0:00-1:30", jump.Value.Notice);
            Assert.False(skips.Prompt.Visible);

            Assert.Null(skips.Accept(), "没有待回答的提示就没有可接受的跳跃");

            skips.Advance(20, playing: true);
            Assert.False(skips.Prompt.Visible, "已经跳过的一段不会在回看时再追着问");
        });

        Test("跳过：拒绝只在本段内有效，退回去会重新提示", () =>
        {
            var skips = Coordinator(out _);

            skips.Advance(10, playing: true);
            skips.Decline();
            Assert.False(skips.Prompt.Visible);

            skips.Advance(20, playing: true);
            Assert.False(skips.Prompt.Visible, "同一段里不再纠缠");

            // 离开区间再回来是一次刻意的操作，值得重新问一次。
            skips.Advance(600, playing: true);
            skips.Advance(10, playing: true);
            Assert.True(skips.Prompt.Visible);
        });

        Test("跳过：自动模式只在刚进入区间时跳", () =>
        {
            var skips = Coordinator(out _);
            skips.Mode = SkipSectionMode.Auto;

            var jump = skips.Advance(0.5, playing: true);
            Assert.NotNull(jump);
            Assert.Equal(90, jump!.Value.Target);
            Assert.False(skips.Prompt.Visible, "自动模式不显示按钮");

            skips.Advance(600, playing: true);
            Assert.Null(skips.Advance(10, playing: true), "同一段只自动跳一次");

            var fresh = Coordinator(out _);
            fresh.Mode = SkipSectionMode.Auto;
            Assert.Null(fresh.Advance(40, playing: true), "拖到片头中间是有意为之，不该被甩出去");
        });

        Test("跳过：关闭之后既不提示也不跳", () =>
        {
            var skips = Coordinator(out _);
            skips.Mode = SkipSectionMode.Off;

            Assert.Null(skips.Advance(10, playing: true));
            Assert.False(skips.Prompt.Visible);
        });

        Test("跳过：换用 mpv 的章节表不会重复提问", () =>
        {
            var skips = Coordinator(out var sections);

            skips.Advance(10, playing: true);
            Assert.NotNull(skips.Accept());

            // mpv 报回同一段片头，只差了个舍入误差；已经跳过就不该再问。
            skips.Refine(
                [new SkipSection(SkipSectionKind.Opening, 0.2, 90, "片头", Certain: true), sections[1]],
                1500);

            skips.Advance(20, playing: true);
            Assert.False(skips.Prompt.Visible);

            // 新表里多出来的片尾照样提示。
            skips.Advance(1400, playing: true);
            Assert.True(skips.Prompt.Visible);
            Assert.Equal("跳过片尾", skips.Prompt.Caption);
        });

        Test("跳过：跳到文件结尾时留半秒给 mpv 自己收尾", () =>
        {
            var skips = Coordinator(out _);

            var jump = skips.Advance(1400, playing: true);
            Assert.Null(jump, "询问模式先给按钮");

            var accepted = skips.Accept();
            Assert.NotNull(accepted);
            Assert.Equal(1499.5, accepted!.Value.Target, "不要求 mpv 跳到刚好不在文件里的位置");
        });

        Test("跳过：换片重新开始，忘掉上一集的决定", () =>
        {
            var skips = Coordinator(out var sections);

            skips.Advance(10, playing: true);
            skips.Accept();

            skips.Begin(sections, 1500);
            skips.Advance(10, playing: true);
            Assert.True(skips.Prompt.Visible, "上一集的片头不是这一集的");
        });
    }

    /// <summary>A coordinator over one 25-minute episode: 0–90 片头, 1380–1500 片尾, 询问 mode.</summary>
    private static SkipCoordinator Coordinator(out IReadOnlyList<SkipSection> sections)
    {
        sections =
        [
            new SkipSection(SkipSectionKind.Opening, 0, 90, "片头", Certain: true),
            new SkipSection(SkipSectionKind.Ending, 1380, 1500, "片尾", Certain: true)
        ];

        var skips = new SkipCoordinator();
        skips.Begin(sections, 1500);
        return skips;
    }

    // ---- 播放器控件显隐 --------------------------------------------------------

    // 需求 9、10、11 全在这条规则里。WinForms 版本把它焊在 40 毫秒的指针轮询上，没法单独检验；
    // 搬进 Core 之后时间是参数，于是可以把每一条要求钉成一个用例。
    private static void RegisterChromeReveal()
    {
        Test("播放器控件：指针进底部五分之一只出进度条", () =>
        {
            var chrome = Chrome(out var now);

            // 先让它收起来，否则读到的是开场那次全显示。
            Assert.True(chrome.Tick(now + 1000));
            Assert.Equal(new ChromeState(false, false, false), chrome.State);

            chrome.Pointer(y: 950, height: 1000, ChromePart.None, railNear: -1, now + 1100);

            // 「显示进度条的时候不需要同步显示音量条」——推翻了原先的「进度条出来时音量条也要出来」：
            // 指针进底部五分之一是在要进度条，跟音量一点关系都没有。
            Assert.Equal(new ChromeState(true, false, false), chrome.State);
            Assert.Equal(0d, chrome.RailStrength, "没出来的音量条强度是零");
        });

        Test("播放器控件：进度条和音量条是两个互不相干的请求", () =>
        {
            // 需求 9 的另一半：拆开之后两者可以各自成立，也可以同时成立，但谁都不再是谁的理由。
            var chrome = Chrome(out var now);
            chrome.Tick(now + 1000);

            chrome.Pointer(y: 950, height: 1000, ChromePart.None, railNear: 0.5, now + 1100);
            Assert.Equal(new ChromeState(true, false, true), chrome.State, "在底部又在右边缘，两个请求都算数");

            // 挪出右边缘：进度条留下，音量条走。
            chrome.Pointer(y: 950, height: 1000, ChromePart.None, railNear: -1, now + 1200);
            Assert.Equal(new ChromeState(true, false, false), chrome.State);

            // 反过来也一样：进了右边缘的中段，进度条不跟着出来。
            chrome.Pointer(y: 500, height: 1000, ChromePart.None, railNear: 0.5, now + 1300);
            Assert.Equal(new ChromeState(false, false, true), chrome.State);
        });

        Test("播放器控件：指针在画面中间什么都不出来", () =>
        {
            var chrome = Chrome(out var now);
            chrome.Tick(now + 1000);

            // 中间三分之三是死区，字幕就在那里。
            foreach (var y in new[] { 210d, 500d, 790d })
            {
                chrome.Pointer(y, height: 1000, ChromePart.None, railNear: -1, now + 1100);
                Assert.Equal(new ChromeState(false, false, false), chrome.State, $"y={y}");
            }
        });

        Test("播放器控件：指针进顶部五分之一只出标题栏", () =>
        {
            var chrome = Chrome(out var now);
            chrome.Tick(now + 1000);

            chrome.Pointer(y: 40, height: 1000, ChromePart.None, railNear: -1, now + 1100);
            Assert.Equal(new ChromeState(false, true, false), chrome.State, "上面的条不带音量条");
        });

        Test("播放器控件：静止 650 毫秒就收起", () =>
        {
            var chrome = Chrome(out var now);

            chrome.Pointer(y: 950, height: 1000, ChromePart.None, railNear: -1, now);
            Assert.True(chrome.State.Bar);

            Assert.False(chrome.Tick(now + 649), "还没到点就不该动");
            Assert.True(chrome.State.Bar);

            Assert.True(chrome.Tick(now + 650), "「鼠标静止后自动隐藏的速度再快些」");
            Assert.Equal(new ChromeState(false, false, false), chrome.State);
        });

        Test("播放器控件：滚轮改音量时单独亮出音量条", () =>
        {
            var chrome = Chrome(out var now);
            chrome.Tick(now + 1000);

            // 指针停在画面正中，滚轮却要有读数：「鼠标滚轮调整音量时要显示音量条」。
            chrome.Pointer(y: 500, height: 1000, ChromePart.None, railNear: -1, now + 1100);
            Assert.Equal(new ChromeState(false, false, false), chrome.State);

            chrome.FlashRail(now + 1200);
            Assert.Equal(new ChromeState(false, false, true), chrome.State, "只亮音量条，不把整套控件拉出来");

            // 宽限窗口比指针的空闲窗口长，数字要来得及看清。
            Assert.False(chrome.Tick(now + 2399));
            Assert.True(chrome.State.Rail);

            Assert.True(chrome.Tick(now + 2400));
            Assert.False(chrome.State.Rail);
        });

        Test("播放器控件：右边缘进入区不受命中测试影响", () =>
        {
            var chrome = Chrome(out var now);
            chrome.Tick(now + 1000);

            // 「音量条判定有问题，我鼠标移到窗口右边有时候不会显示」：跳过按钮和顶部条都压在右边缘上，
            // 命中测试只能选一个赢家，所以进入区是单独一问。
            chrome.Pointer(y: 100, height: 1000, ChromePart.Skip, railNear: 1, now + 1100);
            Assert.True(chrome.State.Rail, "命中到跳过按钮也不该把音量条挡回去");

            chrome.Pointer(y: 40, height: 1000, ChromePart.Title, railNear: 1, now + 1200);
            Assert.Equal(new ChromeState(false, true, true), chrome.State, "顶部条和音量条可以同时在");
        });

        Test("播放器控件：指针停在控件上就不算静止", () =>
        {
            var chrome = Chrome(out var now);

            chrome.Pointer(y: 500, height: 1000, ChromePart.Bar, railNear: -1, now);
            Assert.True(chrome.State.Bar, "停在控件上就是到了，跟分区无关");

            // 同一个位置再问一次，时间往前走——手停在按钮上不该让按钮消失。
            // （上限在「指针停在控件上两秒后连鼠标一起收」那条：一直有事件进来就一直算活动。）
            Assert.False(chrome.Pointer(y: 500, height: 1000, ChromePart.Bar, railNear: -1, now + 5000));
            Assert.True(chrome.State.Bar);
        });

        Test("播放器控件：面板打开或还在加载时钉住不放", () =>
        {
            var chrome = Chrome(out var now);
            chrome.Tick(now + 1000);
            Assert.False(chrome.State.Any);

            // 面板是从进度条上的按钮点开的，所以指针在进度条上，弹出层自己接走了后面的指针事件。
            chrome.Pointer(y: 950, height: 1000, ChromePart.Bar, railNear: -1, now + 1050);

            chrome.SetHold(true, now + 1100);
            Assert.Equal(new ChromeState(true, true, true), chrome.State);
            Assert.False(chrome.Tick(now + 9000), "钉住期间时间流逝也不收");

            chrome.SetHold(false, now + 9100);
            Assert.True(chrome.State.Bar, "面板关掉了，指针还落在进度条上");

            // 指针挪回死区才收——放开钉子本身不该当成「早就静止了」。
            chrome.Pointer(y: 500, height: 1000, ChromePart.None, railNear: -1, now + 9200);
            Assert.False(chrome.State.Any);

            // 还在加载的文件没有画面可挡，控件也是唯一的出路。暂停以前也钉在这里，
            // 现在不钉了：「别什么进度条标题音量条都持久显示在画面上」。
            chrome.SetKeep(true, now + 9800);
            Assert.Equal(new ChromeState(true, true, true), chrome.State, "还没出画面的时候控件是唯一的出路");

            chrome.SetKeep(false, now + 9900);
            Assert.False(chrome.State.Any, "加载完就交回给指针——它还在死区里");
        });

        Test("播放器控件：键盘命令在画面中间也给反馈", () =>
        {
            var chrome = Chrome(out var now);
            chrome.Tick(now + 1000);

            chrome.Pointer(y: 500, height: 1000, ChromePart.None, railNear: -1, now + 1100);
            Assert.False(chrome.State.Any);

            chrome.WakeFully(now + 1200);
            Assert.Equal(new ChromeState(true, true, true), chrome.State);

            Assert.True(chrome.Tick(now + 2400), "宽限窗口过了就交回给指针的位置");
            Assert.False(chrome.State.Any);
        });

        Test("播放器控件：指针离开画面照样收起", () =>
        {
            var chrome = Chrome(out var now);

            chrome.Pointer(y: 950, height: 1000, ChromePart.None, railNear: -1, now);
            Assert.True(chrome.State.Bar);

            // 指针在别的窗口上了，它什么都没有在要求，不必等空闲窗口。
            Assert.True(chrome.PointerLeft(now + 10));
            Assert.False(chrome.State.Any, "离开画面就立刻收，跟走进死区一样");
        });

        Test("播放器控件：从音量条上离开窗口也要收起音量条", () =>
        {
            // 「鼠标移到窗口右边显示音量条之后，再移出窗口，音量条不会自动隐藏」。
            // 停在控件上算活动，所以空闲窗口对它不起作用——离开必须自己说出来，
            // 而外壳过去是拿事件里的坐标判断的，从子元素上离开时那个坐标还在画面里。
            foreach (var (part, near, what) in new (ChromePart Part, double Near, string What)[]
            {
                (ChromePart.Volume, 1d, "指针压在音量条上"),
                (ChromePart.None, 1d, "指针只在右边缘进入区")
            })
            {
                var chrome = Chrome(out var now);
                chrome.Tick(now + 1000);
                Assert.False(chrome.State.Any, what);

                chrome.Pointer(y: 500, height: 1000, part, near, now + 1100);
                Assert.True(chrome.State.Rail, what);

                // 光是等一会儿不行：停着的指针本身就是活动，这也正是它以前钉住不放的原因。
                // 等到超过停留上限才轮到时间说话，见下一条用例。
                var patience = now + 1100 + ChromeReveal.ParkedIdleMilliseconds - 1;
                Assert.False(chrome.Tick(patience), $"{what}：手还搭在上面的时候不该收");
                Assert.True(chrome.State.Rail, what);

                Assert.True(chrome.PointerLeft(patience + 100), what);
                Assert.False(chrome.State.Any, $"{what}：移出窗口就该收");
            }
        });

        Test("播放器控件：指针停在控件上两秒后连鼠标一起收", () =>
        {
            // 「全屏时最下方的进度条不会自动隐藏，鼠标也不会自动隐藏」。停在控件上买到的是耐心而不是豁免：
            // 窗口模式下外壳发现指针离开客户区会替它清掉停留位置，全屏时客户区就是整块屏幕，
            // 没有地方可离开，于是这个闩以前永远打不开。
            var chrome = Chrome(out var now);

            // 点⛶进全屏之后就是这个状态：指针最后一次落在进度条上，之后一动不动。
            chrome.Pointer(y: 950, height: 1000, ChromePart.Bar, railNear: -1, now);
            Assert.True(chrome.State.Bar);

            Assert.False(chrome.Tick(now + ChromeReveal.ParkedIdleMilliseconds - 1), "手在按钮上犹豫的时候不该抽走按钮");
            Assert.True(chrome.State.Bar);
            Assert.False(chrome.CursorHidden);

            Assert.True(chrome.Tick(now + ChromeReveal.ParkedIdleMilliseconds));
            Assert.Equal(new ChromeState(false, false, false), chrome.State, "没人在用了就该收");
            Assert.True(chrome.CursorHidden, "鼠标跟着一起走——它们本来就是同一条规则");

            // 一动就都回来，跟平时一样。
            Assert.True(chrome.Pointer(y: 950, height: 1000, ChromePart.Bar, railNear: -1, now + 5000));
            Assert.True(chrome.State.Bar);
            Assert.False(chrome.CursorHidden);
        });

        Test("播放器控件：停在右边缘进入区也有同一个上限", () =>
        {
            var chrome = Chrome(out var now);
            chrome.Tick(now + 1000);

            chrome.Pointer(y: 500, height: 1000, ChromePart.None, railNear: 1, now + 1100);
            Assert.True(chrome.State.Rail);

            Assert.True(chrome.Tick(now + 1100 + ChromeReveal.ParkedIdleMilliseconds), "指针撂在右边缘上不该把音量条钉死");
            Assert.False(chrome.State.Any);
        });

        // 需求 10：「加大音量条的尺寸，显示方式改为淡入淡出，鼠标指针越接近右边的中心显示越明显」。
        // 尺寸和淡入淡出是页面的事（XAML 里的尺寸、OpacityTransition，由自检去量），
        // 「越接近越明显」是这里的算术：两个方向各占一半，横着看进右边缘多深，竖着看离画面正中多近。
        Test("播放器控件：音量条越往右越明显", () =>
        {
            var chrome = Chrome(out var now);
            chrome.Tick(now + 1000);

            double At(double near)
            {
                chrome.Pointer(y: 500, height: 1000, ChromePart.None, near, now + 1100);
                Assert.True(chrome.State.Rail, $"near={near} 时音量条本来就该在");
                return chrome.RailStrength;
            }

            // 刚跨过进入区的内边界：已经决定要显示，所以给下限而不是给零——
            // 一条决定要显示却淡到五个百分点的音量条不是含蓄，是故障。
            Assert.Equal(ChromeReveal.RailFloor, At(0), "刚进来就给下限");

            var quarter = At(0.25);
            var half = At(0.5);
            var most = At(0.75);
            var edge = At(1);

            Assert.True(quarter > ChromeReveal.RailFloor, $"往里一点就该比下限亮：{quarter}");
            Assert.True(half > quarter, $"{half} 应当亮过 {quarter}");
            Assert.True(most > half, $"{most} 应当亮过 {half}");
            Assert.Equal(1d, edge, "贴着右边缘就是满的");
        });

        Test("播放器控件：音量条越靠画面中心越明显", () =>
        {
            var chrome = Chrome(out var now);
            chrome.Tick(now + 1000);

            double At(double y)
            {
                chrome.Pointer(y, height: 1000, ChromePart.None, railNear: 1, now + 1100);
                return chrome.RailStrength;
            }

            var centre = At(500);
            var quarter = At(250);
            var top = At(10);

            Assert.Equal(1d, centre, "右边缘的正中最明显");
            Assert.True(quarter < centre, $"离中心远一点就该淡一点：{quarter}");
            Assert.True(top < quarter, $"{top} 应当淡过 {quarter}");

            // 右边缘的上下两角是去顶部按钮、去进度条的路，路过不算在要音量条。
            Assert.True(top <= ChromeReveal.RailFloor + 0.02, $"贴着角落只给下限左右：{top}");
            Assert.True(top >= ChromeReveal.RailFloor, $"再淡也不低于下限：{top}");
        });

        Test("播放器控件：不是靠近换来的音量条一律给足", () =>
        {
            // 强度只给「靠近」这一种理由打分。滚轮、键盘、手已经搭在滑杆上、面板钉住、还在加载——
            // 这些都是用户明摆着要看的读数，把一个人正在读的数字调暗是拿分寸回答没人问的问题。
            var wheel = Chrome(out var now);
            wheel.Tick(now + 1000);
            wheel.Pointer(y: 500, height: 1000, ChromePart.None, railNear: -1, now + 1100);
            wheel.FlashRail(now + 1200);
            Assert.Equal(1d, wheel.RailStrength, "滚轮改音量");

            var keys = Chrome(out now);
            keys.Tick(now + 1000);
            keys.WakeFully(now + 1100);
            Assert.Equal(1d, keys.RailStrength, "键盘命令");

            var held = Chrome(out now);
            held.Tick(now + 1000);
            held.SetHold(true, now + 1100);
            Assert.Equal(1d, held.RailStrength, "面板钉住");

            var loading = Chrome(out now);
            loading.Tick(now + 1000);
            loading.SetKeep(true, now + 1100);
            Assert.Equal(1d, loading.RailStrength, "还在加载");

            // 手搭在滑杆上是在瞄，不是在靠近：哪怕进入区的读数说它才刚跨进来，也给足。
            var onRail = Chrome(out now);
            onRail.Tick(now + 1000);
            onRail.Pointer(y: 500, height: 1000, ChromePart.Volume, railNear: 0, now + 1100);
            Assert.Equal(1d, onRail.RailStrength, "手已经搭在音量条上");
        });

        Test("播放器控件：只有强度变了也要通知页面重画", () =>
        {
            var chrome = Chrome(out var now);
            chrome.Tick(now + 1000);

            chrome.Pointer(y: 500, height: 1000, ChromePart.None, railNear: 0.2, now + 1100);
            var before = chrome.RailStrength;

            Assert.True(chrome.Pointer(y: 500, height: 1000, ChromePart.None, railNear: 0.8, now + 1200),
                "三个开关一个没动，动的只有强度——页面还是得重画");
            Assert.Equal(new ChromeState(false, false, true), chrome.State);
            Assert.True(chrome.RailStrength > before, $"{chrome.RailStrength} 应当亮过 {before}");

            // 反过来：强度量化到百分位，指针在桌上抖一抖不该换来一次重画。
            Assert.False(chrome.Pointer(y: 500, height: 1000, ChromePart.None, railNear: 0.8001, now + 1300),
                "百分位没变就没有新闻");
        });

        Test("播放器控件：只有指针停在画面上且没有控件时才藏鼠标", () =>
        {
            var chrome = Chrome(out var now);

            chrome.Pointer(y: 500, height: 1000, ChromePart.None, railNear: -1, now);
            Assert.False(chrome.CursorHidden, "刚动过就藏，等于在移动中间把指针弄丢");

            // 控件和鼠标等的不是同一个静止：死区本来就什么都不显示，控件 650 毫秒就收，
            // 这时候把指针也弄丢，人就在一次移动的中途失去了准头。
            chrome.Tick(now + ChromeReveal.IdleMilliseconds);
            Assert.False(chrome.State.Any);
            Assert.False(chrome.CursorHidden, "控件收了鼠标还得在");

            Assert.True(chrome.Tick(now + ChromeReveal.CursorIdleMilliseconds), "「鼠标静止不动两秒之后要自动隐藏」");
            Assert.True(chrome.CursorHidden);

            var moved = now + ChromeReveal.CursorIdleMilliseconds + 50;
            chrome.Pointer(y: 500, height: 1000, ChromePart.None, railNear: -1, moved);
            Assert.False(chrome.CursorHidden, "一动就得回来");

            // 指针不在这幅画面上时绝不藏：鼠标指针是整个进程共用的。
            chrome.PointerLeft(moved + 100);
            chrome.Tick(moved + 100 + ChromeReveal.CursorIdleMilliseconds);
            Assert.False(chrome.State.Any);
            Assert.False(chrome.CursorHidden);
        });

        Test("播放器控件：动了但说不出动到哪，也得当活动算", () =>
        {
            // 日志里的「静止 156ms」就出在这儿：拿系统位置判静止的那一头记下了动的时刻，
            // 转成画面坐标的那一头失手了（窗口还没尺寸、读不出点），规则就从一个自己没收到的
            // 时刻开始数两秒，把正在移动的手底下的指针给弄丢了。
            var chrome = Chrome(out var now);

            chrome.Pointer(y: 500, height: 1000, ChromePart.None, railNear: -1, now);

            // 一直动，只是每次都说不出动到哪：两秒的窗口从最后一次动起算，永远熬不到期。
            for (var t = now + 100; t <= now + 5000; t += 100)
            {
                chrome.Moved(t);
                chrome.Tick(t);
                Assert.False(chrome.CursorHidden, $"第 {t - now} 毫秒：手还在动，指针不能藏");
            }

            // 停下之后照旧藏，而且是从停下那一刻开始数的两秒。
            var stopped = now + 5000;
            for (var t = stopped; t < stopped + ChromeReveal.CursorIdleMilliseconds; t += 100)
            {
                chrome.Tick(t);
                Assert.False(chrome.CursorHidden, $"停了 {t - stopped} 毫秒，还不到两秒，不许藏");
            }

            Assert.True(chrome.Tick(stopped + ChromeReveal.CursorIdleMilliseconds), "停了两秒还是要藏");
            Assert.True(chrome.CursorHidden);

            // 藏着的时候动一下，同样只说得出「动了」——也得立刻回来。
            Assert.True(chrome.Moved(stopped + ChromeReveal.CursorIdleMilliseconds + 50));
            Assert.False(chrome.CursorHidden, "一动就得回来，哪怕不知道动到哪");
        });

        Test("播放器控件：状态推送再密也不算指针动过", () =>
        {
            // 「鼠标指针还是不会自动隐藏」的真正原因，也是「别什么进度条标题音量条都持久显示在画面上」的：
            // mpv 每秒推四份以上的状态快照，页面每一份都会拿去问一次加载闩，而那个闩把「没在加载」
            // 当成一次活动——空闲时钟一秒被重置四回，谁都熬不到期。
            var chrome = Chrome(out var now);

            // 指针最后落在底部五分之一，之后一动不动：这就是看片时把手放下的样子。
            chrome.Pointer(y: 950, height: 1000, ChromePart.None, railNear: -1, now);
            Assert.True(chrome.State.Bar);

            // 四赫兹，整整四秒，每一份都说「已经出画面了」。
            for (var t = now; t <= now + 4000; t += 250) chrome.SetKeep(false, t);

            Assert.False(chrome.State.Any, "推得再密也不是指针动了，控件该在 650 毫秒就收");
            Assert.True(chrome.CursorHidden, "「鼠标静止不动两秒之后要自动隐藏」");

            // 真从「加载中」翻过来的那一下仍然要重新起算：那一秒里控件是唯一的出路，刚交回来就收
            // 等于把出路从手底下抽走。
            var load = Chrome(out var start);
            load.Pointer(y: 950, height: 1000, ChromePart.None, railNear: -1, start);

            load.SetKeep(true, start + 100);
            Assert.Equal(new ChromeState(true, true, true), load.State, "还没出画面的时候控件是唯一的出路");

            // 加载期间同样推得很密，同样不该被算成活动。
            for (var t = start + 100; t <= start + 3000; t += 250) load.SetKeep(true, t);

            load.SetKeep(false, start + 3100);
            Assert.True(load.State.Bar, "刚交回给指针的这一下不算「早就静止了」");
            Assert.False(load.CursorHidden);

            Assert.True(load.Tick(start + 3100 + ChromeReveal.IdleMilliseconds), "从交回来的那一刻起算");
            Assert.False(load.State.Any);
        });

        Test("播放器控件：换片重新全显示", () =>
        {
            var chrome = Chrome(out var now);
            chrome.Tick(now + 1000);
            Assert.False(chrome.State.Any);

            chrome.Reset(now + 1100);
            Assert.Equal(new ChromeState(true, true, true), chrome.State, "上一集收起来的状态不属于这一集");
            Assert.Equal(1d, chrome.RailStrength, "开场那次全显示是给足的，不是淡的");
            Assert.False(chrome.CursorHidden);
        });

        Test("播放器控件：没有待办时不必继续轮询", () =>
        {
            var chrome = Chrome(out var now);

            Assert.True(chrome.Pending(now), "开场是全显示的，等着收");

            chrome.Tick(now + 1000);
            Assert.False(chrome.Pending(now + 1000), "都收起来了就该把定时器停掉");

            chrome.FlashRail(now + 1100);
            Assert.True(chrome.Pending(now + 1100));

            // 控件都收了、鼠标还在，也算有待办：藏鼠标等的是它自己那两秒，而定时器要是这时候停了，
            // 就再没有人来问「该藏了吗」。
            var idle = Chrome(out var start);
            idle.Pointer(y: 500, height: 1000, ChromePart.None, railNear: -1, start);
            idle.Tick(start + ChromeReveal.IdleMilliseconds);
            Assert.False(idle.State.Any);
            Assert.True(idle.Pending(start + ChromeReveal.IdleMilliseconds), "还差一次藏鼠标");

            idle.Tick(start + ChromeReveal.CursorIdleMilliseconds);
            Assert.True(idle.CursorHidden);
            Assert.False(idle.Pending(start + ChromeReveal.CursorIdleMilliseconds), "藏完了才真的没事");
        });
    }

    /// <summary>A reveal rule at a fixed clock, still in its opening 「everything showing」 state.</summary>
    private static ChromeReveal Chrome(out long now)
    {
        now = 100_000;
        return new ChromeReveal();
    }

    // ---- 播放统计面板 ----------------------------------------------------------

    private static void RegisterPlaybackStats()
    {
        Test("播放统计：读得到的都排成行，读不到的一行都不占", () =>
        {
            var rows = PlaybackStats.Format(new Dictionary<string, string?>
            {
                ["time-pos"] = "125.4",
                ["duration"] = "3600",
                ["speed"] = "1",
                ["video-codec"] = "h264",
                ["width"] = "1920",
                ["height"] = "1080"
            });

            var labels = rows.Select(row => row.Label).ToArray();

            Assert.Equal("2:05.4 / 1:00:00.0", Value(rows, "位置"));
            Assert.Equal("1.00×", Value(rows, "倍速"));
            Assert.Equal("h264", Value(rows, "编码"), "只有视频编码时不留分隔符");
            Assert.Equal("1920×1080", Value(rows, "分辨率"));

            // 一个没给的项目就该整行不见——写「未知」看起来像读失败了。
            Assert.False(labels.Contains("缓存"), "没给缓存就不该有缓存行");
            Assert.False(labels.Contains("硬件解码"));
            Assert.False(labels.Contains("丢帧"), "两个计数器都没给才算读不到");
        });

        Test("播放统计：面板问的每一项都用得上", () =>
        {
            var readings = PlaybackStats.Fields.ToDictionary(field => field, _ => (string?)"1");
            readings["hwdec-current"] = "d3d11va";
            readings["video-codec"] = "hevc";
            readings["audio-codec-name"] = "eac3";
            readings["current-vo"] = "gpu-next";
            readings["video-params/pixelformat"] = "yuv420p10";

            var rows = PlaybackStats.Format(readings);

            // README 数的是十三行；问的属性比行多，因为有几行是两个属性拼的。
            Assert.Equal(13, rows.Count, string.Join("、", rows.Select(row => row.Label)));
            Assert.Equal(PlaybackStats.Fields.Count, PlaybackStats.Fields.Distinct().Count(), "问重了就是白问一次");
            Assert.True(rows.All(row => row.Value.Length > 0), "有行就得有值");
        });

        Test("播放统计：缓存有速度才写速度，没速度不写「0 B/s」", () =>
        {
            Assert.Equal("12.5 秒 · 2.4 MB/s", Value(PlaybackStats.Format(new Dictionary<string, string?>
            {
                ["demuxer-cache-duration"] = "12.53",
                ["cache-speed"] = "2516582"
            }), "缓存"));

            // 本地文件读满了就不再取，写成 0 B/s 会被当成卡住。
            Assert.Equal("12.5 秒", Value(PlaybackStats.Format(new Dictionary<string, string?>
            {
                ["demuxer-cache-duration"] = "12.53",
                ["cache-speed"] = "0"
            }), "缓存"));
        });

        Test("播放统计：音视频同步带正负号，往哪边偏才是要看的", () =>
        {
            Assert.Equal("-0.012 秒", Value(Sync("-0.0124"), "音视频同步"));
            Assert.Equal("+0.031 秒", Value(Sync("0.0312"), "音视频同步"));
            Assert.Equal("0.000 秒", Value(Sync("0"), "音视频同步"));

            static IReadOnlyList<PlaybackStatRow> Sync(string value) =>
                PlaybackStats.Format(new Dictionary<string, string?> { ["avsync"] = value });
        });

        Test("播放统计：帧率对得上就只写一个数", () =>
        {
            Assert.Equal("23.976", Value(Fps("23.976", "23.9758"), "帧率"), "差不到百分之一就是同一个");
            Assert.Equal("23.976 → 21.4", Value(Fps("23.976", "21.4"), "帧率"), "跟不上才要写实际值");
            Assert.Equal("59.94", Value(Fps(null, "59.94"), "帧率"), "容器没写就用实测的");

            static IReadOnlyList<PlaybackStatRow> Fps(string? declared, string? actual) =>
                PlaybackStats.Format(new Dictionary<string, string?>
                {
                    ["container-fps"] = declared,
                    ["estimated-vf-fps"] = actual
                });
        });

        Test("播放统计：码率按十进制千位，缓存速度按 1024", () =>
        {
            var rows = PlaybackStats.Format(new Dictionary<string, string?>
            {
                ["video-bitrate"] = "8000000",
                ["audio-bitrate"] = "640000",
                ["cache-speed"] = "1048576",
                ["demuxer-cache-duration"] = "1"
            });

            // 谁都把这个文件叫 8 Mbps，这里就不能写成 7.63。
            Assert.Equal("视频 8.00 Mbps · 音频 640 kbps", Value(rows, "码率"));
            Assert.Equal("1.0 秒 · 1.0 MB/s", Value(rows, "缓存"), "字节数还是 1024 进位");
        });

        Test("播放统计：软件解码写成人话，丢帧零也要报", () =>
        {
            var rows = PlaybackStats.Format(new Dictionary<string, string?>
            {
                ["hwdec-current"] = "no",
                ["frame-drop-count"] = "0",
                ["decoder-frame-drop-count"] = "3",
                ["audio-params/channel-count"] = "6",
                ["audio-params/samplerate"] = "48000"
            });

            Assert.Equal("软件解码", Value(rows, "硬件解码"), "「硬件解码：no」不是一句话");
            Assert.Equal("输出 0 · 解码 3", Value(rows, "丢帧"), "「丢帧 0」正是卡顿时要的答案");
            Assert.Equal("6 声道 · 48 kHz", Value(rows, "声道与采样率"));
        });

        Test("播放统计：一项都读不到就是空面板，不是一屏问号", () =>
        {
            Assert.Equal(0, PlaybackStats.Format(new Dictionary<string, string?>()).Count);
            Assert.Equal(0, PlaybackStats.Format(
                PlaybackStats.Fields.ToDictionary(field => field, _ => (string?)null)).Count);
            Assert.Equal(0, PlaybackStats.Format(
                PlaybackStats.Fields.ToDictionary(field => field, _ => (string?)"  ")).Count, "空白也算读不到");
        });
    }

    private static string Value(IReadOnlyList<PlaybackStatRow> rows, string label) =>
        rows.FirstOrDefault(row => row.Label == label).Value ?? "";

    // ---- 窗口比例联动 ----------------------------------------------------------

    private static void RegisterAspectLock()
    {
        Test("比例联动：拖左右边配高度，拖上下边配宽度", () =>
        {
            // 16:9 的片子，边框 16×39：客户区宽 1600 就该配高 900。
            var wide = AspectLock.Apply(Rect(100, 100, 1716, 100 + 1000), ResizeEdge.Right, 16d / 9, 16, 39);
            Assert.Equal(1600, wide.Width - 16);
            Assert.Equal(900, wide.Height - 39);
            Assert.Equal(100, wide.Left, "拖右边，左边不该动");
            Assert.Equal(100, wide.Top);

            var tall = AspectLock.Apply(Rect(100, 100, 100 + 800, 100 + 939), ResizeEdge.Bottom, 16d / 9, 16, 39);
            Assert.Equal(900, tall.Height - 39, "拖下边高度就是给定的");
            Assert.Equal(1600, tall.Width - 16, "宽度跟着高度算");
        });

        Test("比例联动：抓着的那个角不动", () =>
        {
            var proposed = Rect(100, 100, 1716, 1139);

            // 拖右下角：左上角是锚。
            var bottomRight = AspectLock.Apply(proposed, ResizeEdge.BottomRight, 16d / 9, 16, 39);
            Assert.Equal(100, bottomRight.Left);
            Assert.Equal(100, bottomRight.Top);

            // 拖左上角：右下角是锚，窗口朝指针那边长。
            var topLeft = AspectLock.Apply(proposed, ResizeEdge.TopLeft, 16d / 9, 16, 39);
            Assert.Equal(1716, topLeft.Right);
            Assert.Equal(1139, topLeft.Bottom);
            Assert.Equal(1600, topLeft.Width - 16, "角的宽度照给定的算");

            // 拖左边：右边是锚，上边不动。
            var left = AspectLock.Apply(proposed, ResizeEdge.Left, 16d / 9, 16, 39);
            Assert.Equal(1716, left.Right);
            Assert.Equal(100, left.Top);
        });

        Test("比例联动：最小尺寸压得住比例", () =>
        {
            // 窄到 200 时按 16:9 只有 112 高，可窗口最矮 600——那就只能加宽。
            var bounds = AspectLock.Apply(Rect(0, 0, 216, 639), ResizeEdge.Right, 16d / 9, 16, 39, 320, 600);
            Assert.Equal(600, bounds.Height - 39);
            Assert.Equal(1067, bounds.Width - 16, "高度顶住了就反过来算宽度");
        });

        Test("比例联动：没有比例可依就原样放过", () =>
        {
            var proposed = Rect(0, 0, 800, 600);

            // 0 是常态：mpv 还没开文件的时候就是这个数，不能拿猜的比例去改窗口。
            Assert.Equal(proposed, AspectLock.Apply(proposed, ResizeEdge.Right, 0, 16, 39));
            Assert.Equal(proposed, AspectLock.Apply(proposed, ResizeEdge.Right, double.NaN, 16, 39));
            Assert.Equal(proposed, AspectLock.Apply(proposed, ResizeEdge.Right, 40, 16, 39), "40:1 不是画面");
            Assert.Equal(proposed, AspectLock.Apply(proposed, ResizeEdge.None, 16d / 9, 16, 39), "不是在拖边");
            Assert.Equal(proposed, AspectLock.Apply(proposed, ResizeEdge.Right, 16d / 9, 900, 700), "边框比窗口还大");
        });

        Test("比例联动：比例取自 mpv 的显示尺寸，拿不到才退回服务器的", () =>
        {
            // 变形宽银幕：存的是 1920×1080，显示是 2560×1080，只有前者会算错。
            Assert.Equal(2560d / 1080, AspectLock.Ratio(2560, 1080, 1920, 1080));
            Assert.Equal(1920d / 1080, AspectLock.Ratio(0, 0, 1920, 1080), "开播前先用服务器给的");
            Assert.Equal(0, AspectLock.Ratio(0, 0, 0, 0), "两边都没有就是「不要改」");
        });

        Test("比例联动：改完的窗口再拖一次不会越改越歪", () =>
        {
            var once = AspectLock.Apply(Rect(0, 0, 1616, 1039), ResizeEdge.Right, 16d / 9, 16, 39);
            var twice = AspectLock.Apply(once, ResizeEdge.Right, 16d / 9, 16, 39);
            Assert.Equal(once, twice, "同一个矩形送回去应当纹丝不动");
        });

        Test("开播即配比例：客户区变成画面的形状，中心不动", () =>
        {
            // 1280×800 的窗口、播放时没有标题栏（边框 16×16）：客户区 1264×784 是 1.612，
            // 16:9 的片子在里面上下各留 36 像素黑边。宽度说话，高度跟上。
            var fitted = AspectLock.Fit(Rect(100, 100, 1380, 900), 16d / 9, 16, 16);
            Assert.Equal(1264, fitted.Width - 16);
            Assert.Equal(711, fitted.Height - 16, "1264 ÷ 16:9");
            Assert.Equal(740, fitted.Left + fitted.Width / 2, "左右中心不动");
            Assert.True(Math.Abs(fitted.Top + fitted.Height / 2.0 - 500) <= 1, "上下中心也不动（取整差一个像素）");
        });

        Test("开播即配比例：已经是这个形状就一动不动", () =>
        {
            // 用户自己拖到位的窗口不该在开播那一刻再跳一下。
            var already = Rect(0, 0, 1616, 916);
            Assert.Equal(already, AspectLock.Fit(already, 16d / 9, 16, 16), "1600×900 已经是 16:9");
            Assert.Equal(already, AspectLock.Fit(already, 16d / 9 + 0.0004, 16, 16), "差一个像素不算黑边");
        });

        Test("开播即配比例：不许长到屏幕外面去", () =>
        {
            // 2:1 的宽银幕，1920×1080 的桌面上工作区只有 1920×1040：先按宽度算高度还塞得下，
            // 竖着的片子就得反过来按高度算宽度。
            var work = Rect(0, 0, 1920, 1040);

            var wide = AspectLock.Fit(Rect(0, 0, 2400, 900), 2.0, 16, 16, work);
            Assert.Equal(1904, wide.Width - 16, "宽度顶到工作区");
            Assert.Equal(952, wide.Height - 16);
            Assert.Equal(0, wide.Left, "被推回工作区里面");

            var tall = AspectLock.Fit(Rect(0, 0, 1200, 900), 9d / 16, 16, 16, work);
            Assert.Equal(1024, tall.Height - 16, "高度顶到工作区");
            Assert.Equal(576, tall.Width - 16, "宽度改跟高度算");
            Assert.True(tall.Bottom <= 1040, "整个窗口都在工作区内");
        });

        Test("开播即配比例：最小尺寸和不认识的比例都拦得住", () =>
        {
            // 高度压不下去就只能加宽，和拖边时同一条规矩。
            var floored = AspectLock.Fit(Rect(0, 0, 916, 700), 16d / 9, 16, 16, default, 900, 600);
            Assert.Equal(600, floored.Height - 16);
            Assert.Equal(1067, floored.Width - 16, "高度顶住了就反过来算宽度");

            var window = Rect(0, 0, 1280, 800);
            Assert.Equal(window, AspectLock.Fit(window, 0, 16, 16), "还没开文件");
            Assert.Equal(window, AspectLock.Fit(window, double.NaN, 16, 16));
            Assert.Equal(window, AspectLock.Fit(window, 40, 16, 16), "40:1 不是画面");
            Assert.Equal(window, AspectLock.Fit(window, 16d / 9, 1400, 900), "边框比窗口还大");
        });

        Test("浏览窗口：比例不算侧边栏那一条", () =>
        {
            // 「锁定比例大小改为 16:9，计算比例时要排除侧边栏」：锁的是侧边栏右边那一片，所以客户区比 16:9
            // 宽出那一条（这里 49）。1487 宽的窗口减掉 16 的边框是 1471 的客户区，减掉 49 就是 1422 的页面，
            // 1422 ÷ 16:9 = 800，窗口于是 839 高。
            var dragged = AspectLock.Apply(
                Rect(0, 0, 1487, 1000), ResizeEdge.Right, 16d / 9, 16, 39, 0, 0, 49);

            Assert.Equal(1487, dragged.Width, "宽领头那一档宽度不动");
            Assert.Equal(839, dragged.Height, "高度按页面那一片算");
            Assert.Equal(800, dragged.Height - 39);
            Assert.Equal(1422, dragged.Width - 16 - 49);

            // 拖上下边沿那一档反过来：高度说话，宽度是页面加上那一条再加边框。
            var vertical = AspectLock.Apply(
                Rect(0, 0, 900, 839), ResizeEdge.Bottom, 16d / 9, 16, 39, 0, 0, 49);
            Assert.Equal(1487, vertical.Width);

            // 开窗那一下（Fit）走同一条算术，工作区也按同一条量。
            var fitted = AspectLock.Fit(Rect(0, 0, 1487, 900), 16d / 9, 16, 39, default, 0, 0, 49);
            Assert.Equal(839, fitted.Height);
            Assert.Equal(1487, fitted.Width);

            // 不给这个参数就是原来那条规矩：播放中的窗口整块都是画面，一个像素也不让出去。
            Assert.Equal(
                AspectLock.Apply(Rect(0, 0, 1487, 1000), ResizeEdge.Right, 16d / 9, 16, 39, 0, 0, 0),
                AspectLock.Apply(Rect(0, 0, 1487, 1000), ResizeEdge.Right, 16d / 9, 16, 39));
        });

        Test("浏览窗口：最小尺寸说的还是整个客户区", () =>
        {
            // 扣掉侧边栏之后仍然不许把窗口挤到最小尺寸以下：900×560 是对客户区说的，不是对页面说的。
            var bounds = AspectLock.Apply(
                Rect(0, 0, 500, 500), ResizeEdge.Right, 16d / 9, 16, 39, 900, 560, 49);

            Assert.Equal(560, bounds.Height - 39, "高度顶住了下限");
            Assert.True(bounds.Width - 16 >= 900, $"客户区只剩 {bounds.Width - 16} 宽");
            Assert.Equal(996, bounds.Width - 16 - 49, "页面按高度反算");

            // 只差一点点的那一档：页面的下限是 900 减去那一条，而不是 900。
            var floored = AspectLock.Fit(
                Rect(0, 0, 400, 2000), 16d / 9, 16, 39, default, 900, 0, 49);
            Assert.Equal(900, floored.Width - 16, "客户区正好卡在下限上");
            Assert.Equal(851, floored.Width - 16 - 49);
        });

        static WindowBounds Rect(int left, int top, int right, int bottom) => new(left, top, right, bottom);
    }

    // ---- 右键画面菜单 ----------------------------------------------------------

    private static void RegisterPlayerMenu()
    {
        Test("画面菜单：每一行都真的会做事", () =>
        {
            foreach (var node in PlayerMenuCatalog.Flatten(PlayerMenuCatalog.Root))
            {
                switch (node.Kind)
                {
                    case PlayerMenuKind.Command:
                        Assert.True(node.Label.Length > 0, "命令行要有名字");
                        Assert.True(node.Commands.Count > 0, node.Label);
                        Assert.True(node.Commands.All(command => command.Count > 0), node.Label);
                        Assert.True(node.Commands.All(command => command[0].Length > 0), node.Label);
                        Assert.Equal(0, node.Children.Count, node.Label);
                        break;

                    case PlayerMenuKind.Group:
                        // 空的子菜单是点开一片空白，比没有这一项还糟。
                        Assert.True(node.Children.Count > 0, $"{node.Label} 是空子菜单");
                        Assert.Equal(0, node.Commands.Count, node.Label);
                        break;

                    case PlayerMenuKind.Separator:
                        Assert.Equal("", node.Label);
                        Assert.Equal(0, node.Commands.Count);
                        break;
                }
            }
        });

        Test("画面菜单：做了事就会在画面上说一句", () =>
        {
            // 没有 uosc，从 IPC 发的命令默认不出 OSD，所以反馈只能自己补。
            foreach (var node in PlayerMenuCatalog.Flatten(PlayerMenuCatalog.Root)
                .Where(node => node.Kind == PlayerMenuKind.Command))
            {
                Assert.True(node.Notice.Length > 0, node.Label);
            }
        });

        Test("画面菜单：不留调不动脚本的行", () =>
        {
            foreach (var node in PlayerMenuCatalog.Flatten(PlayerMenuCatalog.Root))
            {
                foreach (var command in node.Commands)
                {
                    // libmpv 里没有 Lua，随附配置里那些 script-message 行按下去什么都不会发生。
                    Assert.False(command[0].StartsWith("script-", StringComparison.Ordinal),
                        $"{node.Label}：{command[0]}");
                }
            }
        });

        Test("画面菜单：README 点名的项目在它说的位置上", () =>
        {
            var picture = PlayerMenuCatalog.Root.Single(node => node.Label == "画面");
            var panscan = picture.Children.Single(node => node.Label == "开/关 裁切填充");

            // README:16 说「右键菜单 → 画面 → 开 / 关 裁切填充」，就得真在画面下面一层。
            Assert.Equal("cycle-values panscan 0.0 1.0", string.Join(' ', panscan.Commands[0]));

            foreach (var group in new[] { "导航", "画面", "视频", "视频滤镜", "音频", "字幕", "片段循环" })
            {
                Assert.True(PlayerMenuCatalog.Root.Any(node => node.Label == group), group);
            }
        });

        Test("画面菜单：一行按不动的重置要把该重的都重了", () =>
        {
            var reset = PlayerMenuCatalog.Root
                .Single(node => node.Label == "画面").Children
                .Single(node => node.Label == "重置以上画面操作");

            // mpv 没有一条命令能同时设六个属性，配置里的 `;` 串联是输入语法，发不进去。
            Assert.Equal(6, reset.Commands.Count);
            foreach (var property in new[]
                     { "video-zoom", "panscan", "video-rotate", "video-pan-x", "video-pan-y", "video-aspect-override" })
            {
                Assert.True(reset.Commands.Any(command => command.Count == 3 && command[1] == property), property);
            }
        });

        Test("画面菜单：同一层里没有两行同名", () =>
        {
            Check(PlayerMenuCatalog.Root, "根");

            static void Check(IReadOnlyList<PlayerMenuNode> nodes, string where)
            {
                var labels = nodes
                    .Where(node => node.Kind != PlayerMenuKind.Separator)
                    .Select(node => node.Label)
                    .ToArray();

                Assert.Equal(labels.Length, labels.Distinct().Count(), where);

                foreach (var node in nodes.Where(node => node.Children.Count > 0)) Check(node.Children, node.Label);
            }
        });
    }

    // ---- 剧集导航 --------------------------------------------------------------

    /// <summary>
    /// 上一集 / 下一集 over a whole-series episode list — the list the client asks for the moment a step
    /// runs off the end of the season it is holding. The order is the server's
    /// (<c>ParentIndexNumber,IndexNumber,SortName</c> ascending, verified against a real 4.9 server), so
    /// these cases are the shapes that order really produces: a clean season boundary, a season with one
    /// episode in it, and numbering with a hole in it.
    /// </summary>
    private static void RegisterEpisodeNavigation()
    {
        var episodes = new List<EmbyItem>
        {
            QueueEpisode("s1e1", "season1", 1, 1),
            QueueEpisode("s1e2", "season1", 1, 2),
            QueueEpisode("s2e1", "season2", 2, 1),
            QueueEpisode("s2e2", "season2", 2, 2)
        };

        Test("剧集导航：季末下一集进入下一季第一集", () =>
        {
            var destination = EpisodeNavigation.Step(episodes, "s1e2", 1);

            Assert.Equal("s2e1", destination?.Episode.Id);
            Assert.Equal(2, destination?.Siblings.Count ?? 0);
            Assert.Equal("s2e1", destination?.Siblings[0].Id);
            Assert.Equal("s2e2", destination?.Siblings[1].Id);
        });

        Test("剧集导航：季首上一集回到上一季最后一集", () =>
        {
            var destination = EpisodeNavigation.Step(episodes, "s2e1", -1);

            Assert.Equal("s1e2", destination?.Episode.Id);
            Assert.Equal(2, destination?.Siblings.Count ?? 0);
            Assert.Equal("s1e1", destination?.Siblings[0].Id);
        });

        Test("剧集导航：整部剧两端没有相邻单集", () =>
        {
            Assert.Null(EpisodeNavigation.Step(episodes, "s1e1", -1));
            Assert.Null(EpisodeNavigation.Step(episodes, "s2e2", 1));
            Assert.Null(EpisodeNavigation.Step(episodes, "missing", 1));
        });

        Test("剧集导航：缺少 SeasonId 时按季号归回目标季", () =>
        {
            var loose = new List<EmbyItem>
            {
                QueueEpisode("a", null, 1, 1),
                QueueEpisode("b", null, 1, 2),
                QueueEpisode("c", null, 2, 1)
            };

            var destination = EpisodeNavigation.Step(loose, "b", 1);
            Assert.Equal("c", destination?.Episode.Id);
            Assert.Equal(1, destination?.Siblings.Count ?? 0);
        });

        // 死神's shape on the real server: 本篇 holds one episode, 千年血战篇 starts at 41. The local
        // season list is then a single row, so every step off it is a server round trip, and the step has
        // to be a move along the list rather than 「the next episode number」.
        Test("剧集导航：只有一集的季也能跨到下一季", () =>
        {
            var thin = new List<EmbyItem>
            {
                QueueEpisode("only", "season1", 1, 1),
                QueueEpisode("next41", "season2", 2, 41),
                QueueEpisode("next42", "season2", 2, 42)
            };

            var forward = EpisodeNavigation.Step(thin, "only", 1);
            Assert.Equal("next41", forward?.Episode.Id);
            Assert.Equal(2, forward?.Siblings.Count ?? 0);

            var back = EpisodeNavigation.Step(thin, "next41", -1);
            Assert.Equal("only", back?.Episode.Id);
            Assert.Equal(1, back?.Siblings.Count ?? 0);
        });

        // 少年同盟's shape: season 1 runs 1..10 then jumps to 12. Walking by position keeps the missing
        // file out of it; arithmetic on IndexNumber would have looked for an episode 11 that is not there.
        Test("剧集导航：集号有缺口时走列表位置而不是集号", () =>
        {
            var gapped = new List<EmbyItem>
            {
                QueueEpisode("e10", "season1", 1, 10),
                QueueEpisode("e12", "season1", 1, 12),
                QueueEpisode("e13", "season1", 1, 13)
            };

            Assert.Equal("e12", EpisodeNavigation.Step(gapped, "e10", 1)?.Episode.Id);
            Assert.Equal("e12", EpisodeNavigation.Step(gapped, "e13", -1)?.Episode.Id);
        });

        // The picker that opens after a cross-season step has to hold that season and nothing else: it is
        // the list 选集 shows and the list the next step walks, so a series-wide list left in place would
        // make 选集 a menu of the whole show.
        Test("剧集导航：跨季后的选集列表只剩目标那一季", () =>
        {
            var destination = EpisodeNavigation.Step(episodes, "s1e2", 1);

            Assert.NotNull(destination);
            Assert.True(destination!.Siblings.All(sibling => sibling.SeasonId == "season2"));
            Assert.Equal(destination.Episode.Id, destination.Siblings[0].Id);
        });

        // Only ±1 is a step. An offset of 0 or 2 is a caller bug, and a silent answer to it would be a
        // 下一集 that skipped an episode.
        Test("剧集导航：只接受相邻一步", () =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => EpisodeNavigation.Step(episodes, "s1e1", 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => EpisodeNavigation.Step(episodes, "s1e1", 2));
        });
    }

    // ---- 播放闸门 --------------------------------------------------------------

    /// <summary>
    /// The one part of <see cref="PlaybackService"/> that can be tested without mpv or a server: what
    /// happens to its semaphore when the play attempt fails before a backend is ever built.
    /// </summary>
    private static void RegisterPlaybackGate()
    {
        Test("播放闸门：未登录时抛错后闸门要放开，第二次播放不能卡死", () =>
        {
            var service = SignedOutService();

            // Signed out, so PlayAsync throws at its 尚未登录 check — before any backend is asked for,
            // which is why the factory in SignedOutService throws if it is ever called.
            Assert.Throws<InvalidOperationException>(
                () => service.PlayAsync(Ticket(), CancellationToken.None).GetAwaiter().GetResult());

            // The real defect this covers is not the first failure but the second call. The gate is a
            // SemaphoreSlim(1, 1) released only from PlayAsync's finally; while that finally opened below
            // the 尚未登录 check, the first failure left the gate held and every later play waited on it
            // forever — no exception, no timeout, just the cover art staying up for the rest of the
            // process. A leak therefore shows up here as a hang, so this is given a deadline rather than
            // being awaited outright.
            var second = Attempt(service);
            Assert.True(second.Wait(TimeSpan.FromSeconds(5)), "第二次播放没能在 5 秒内返回：闸门泄漏了");
            Assert.True(
                second.Result is InvalidOperationException,
                "第二次播放也应该以未登录失败，而不是别的错误");
        });

        Test("播放闸门：闸门连续放开，第三次播放同样立刻失败", () =>
        {
            var service = SignedOutService();

            // Once, because one release could be a coincidence; three times, because a gate that is
            // released on every failure path is the actual invariant.
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var play = Attempt(service);
                Assert.True(play.Wait(TimeSpan.FromSeconds(5)), $"第 {attempt} 次播放卡住了");
                Assert.True(play.Result is InvalidOperationException, $"第 {attempt} 次播放应该以未登录失败");
            }
        });
    }

    /// <summary>
    /// A service whose session was never signed in. Nothing here touches the disk: <c>AppPaths</c> only
    /// joins strings and <c>SettingsStore</c> only remembers them, so the temp root below is a name that
    /// is never created and never needs cleaning up.
    /// </summary>
    private static PlaybackService SignedOutService()
    {
        var settings = new AppSettings();
        var session = new EmbySession(
            settings,
            new SettingsStore(
                new AppPaths(Path.Combine(Path.GetTempPath(), $"embynian-gate-{Guid.NewGuid():N}")),
                PassthroughSecretProtector.Instance),
            new CredentialVault(PassthroughSecretProtector.Instance),
            DeviceIdentity.Create("device-1", "2.0.0"));

        return new PlaybackService(
            session,
            settings,
            () => throw new AssertionException("未登录就不该走到后端"),
            new PlaybackPlanner(settings, new ShaderGroupResolver(settings.Shaders)));
    }

    /// <summary>
    /// One play attempt on a worker thread, returning whatever it threw instead of faulting the task.
    /// Returning the exception rather than letting it escape is what makes the deadline above meaningful:
    /// <c>Task.Wait(timeout)</c> throws on a faulted task instead of reporting the timeout, so a task that
    /// never faults is the only way to tell 「failed fast」 apart from 「never came back」.
    /// </summary>
    private static Task<Exception?> Attempt(PlaybackService service) => Task.Run(() =>
    {
        try
        {
            service.PlayAsync(Ticket(), CancellationToken.None).GetAwaiter().GetResult();
            return null;
        }
        catch (Exception error)
        {
            return (Exception?)error;
        }
    });

    // ---- 点画面那一下 ----------------------------------------------------------
    //
    // 「双击画面全屏的时候会触发暂停和开始」。从前是单击立刻暂停、双击再撤回 —— 净状态对，可屏上闪两次徽标，
    // mpv 也真暂停了一下又恢复。现在先攥住那一下，攥得住就一次暂停都没发出去。这一族钉的正是那个次序。

    private static void RegisterPictureTap()
    {
        Test("点画面：攥的时间不超过上限，也不超过系统那个数", () =>
        {
            // 这台机器上系统答 500 —— 照它攥就是每次点画面暂停都要等半秒。
            Assert.Equal(PictureTap.HoldCapMilliseconds, PictureTap.HoldFor(500));
            Assert.Equal(80, PictureTap.HoldFor(80), "系统那个数更小就听它的");
            Assert.Equal(PictureTap.HoldCapMilliseconds, PictureTap.HoldFor(0), "问不出来就用上限");
            Assert.Equal(PictureTap.HoldCapMilliseconds, PictureTap.HoldFor(-1));
        });

        Test("点画面：徽标静音期比攥的上限长", () =>
        {
            // 撤回那一下的状态沿要走 mpv 一趟回来，静音期短于攥的时间就会漏出那一次徽标。
            Assert.True(PictureTap.PulseMuteMilliseconds > PictureTap.HoldCapMilliseconds);
        });

        Test("点画面：单击攥住，不当场下发", () =>
        {
            var tap = new PictureTap();
            Assert.False(tap.Pending, "什么都没发生时不该攥着");
            Assert.False(tap.Issued);

            tap.First(paused: false);
            Assert.True(tap.Pending, "攥着");
            Assert.False(tap.Issued, "还没下发 —— 这就是这次修法的全部");
        });

        Test("点画面：攥够了才下发一次", () =>
        {
            var tap = new PictureTap();
            tap.First(paused: false);

            Assert.True(tap.Elapsed(), "到期这一下才是真的下发");
            Assert.False(tap.Pending);
            Assert.True(tap.Issued);

            Assert.False(tap.Elapsed(), "定时器晚一拍再跳一次，不许凭空再切一下播放");
        });

        Test("点画面：快的双击 —— 一次暂停都没发出去，也没什么要还", () =>
        {
            var tap = new PictureTap();
            tap.First(paused: false);

            Assert.Null(tap.Second(), "null 就是「那一次暂停从来没发生过」");
            Assert.False(tap.Pending, "攥着的那一下作废了");
            Assert.False(tap.Issued);
        });

        Test("点画面：慢的双击 —— 把按下之前那个值还回去", () =>
        {
            var tap = new PictureTap();
            tap.First(paused: true);
            Assert.True(tap.Elapsed());

            Assert.Equal(true, tap.Second(), "还的是按下之前那个值，不是再切一次");
            Assert.False(tap.Issued, "还完就清干净");
            Assert.Null(tap.Second(), "第二下不会来两次；来了也没有第二份账");
        });

        Test("点画面：作废之后到期不再下发", () =>
        {
            // 点在控制条上的那一下必须作废：不作废，接着一次落在控制条上的双击就会拿着上一次单击记下的
            // pause 去「撤回」，把正在放的片子停掉。
            var tap = new PictureTap();
            tap.First(paused: false);
            tap.Forget();

            Assert.False(tap.Pending);
            Assert.False(tap.Elapsed());
            Assert.Null(tap.Second());
        });

        Test("点画面：新的一次单击把上一次的账清掉", () =>
        {
            var tap = new PictureTap();
            tap.First(paused: false);
            Assert.True(tap.Elapsed());

            tap.First(paused: true);
            Assert.True(tap.Pending);
            Assert.False(tap.Issued, "上一次已经下发过的那一笔不能留着，否则这一次的双击会去还上一次的值");
        });
    }

    // ---- 透明光标的掩码 --------------------------------------------------------
    //
    // 从这次起 WinUI 的输入管线会照着这张掩码画光标（见 PlayerPage.Chrome.cs 的 SetCursorHidden），所以算错
    // 不再是「藏不掉」而是「画面正中多一块黑方块」。

    private static void RegisterCursorMask()
    {
        Test("透明光标：32×32 那一档 AND 全 1、XOR 全 0", () =>
        {
            var (and, xor) = CursorMask.Transparent(32, 32);

            // 32 宽 → 每行 4 字节 → 32 行 128 字节。AND=1、XOR=0 逐像素就是「透明」。
            Assert.Equal(128, and.Length);
            Assert.Equal(128, xor.Length);
            Assert.True(and.All(value => value == 0xFF), "AND 掩码不是全 1，光标就不是整只透明");
            Assert.True(xor.All(value => value == 0x00), "XOR 掩码不是全 0，透明处会被反色");
        });

        Test("透明光标：行距按 WORD 补齐，不是按字节", () =>
        {
            // 这一条最容易写错，而错了整张掩码逐行错位、画出来是一块斜纹。
            Assert.Equal(2, CursorMask.Stride(1));
            Assert.Equal(2, CursorMask.Stride(16));
            Assert.Equal(4, CursorMask.Stride(17), "17 像素要两个 WORD");
            Assert.Equal(4, CursorMask.Stride(24), "24 像素占 3 字节，但要补到 4");
            Assert.Equal(4, CursorMask.Stride(32));
            Assert.Equal(6, CursorMask.Stride(33));

            var (and, _) = CursorMask.Transparent(24, 10);
            Assert.Equal(40, and.Length, "24×10 是 4 字节一行乘 10 行");
        });

        Test("透明光标：宽或高不是正数时返回空数组，不抛", () =>
        {
            // 抛异常会把「藏不掉鼠标」升级成「播放器起不来」。
            foreach (var (width, height) in new[] { (0, 32), (32, 0), (-1, 32), (32, -1), (0, 0) })
            {
                var (and, xor) = CursorMask.Transparent(width, height);
                Assert.Equal(0, and.Length, $"{width}×{height}");
                Assert.Equal(0, xor.Length, $"{width}×{height}");
            }

            Assert.Equal(0, CursorMask.Stride(0));
            Assert.Equal(0, CursorMask.Stride(-8));
        });
    }

    // ---- 暂停/播放徽标的几何 ----------------------------------------------------
    //
    // 「暂停和开始的图标太丑了弄一个白色三角形方块和两个白色的长方块就可以」。形状是「核心多边形 ＋ 圆接头描边」
    // 拼出来的，于是「屏上最后多大」是一道要算的题 —— 而它替掉的那两个图标字量出来是 122.5×122.5 和 113.3×123.3，
    // 新形状必须落在同一档分量上，不然屏上就是「换了个图标顺手大了一圈」。

    private static void RegisterPulseArt()
    {
        Test("暂停徽标：描边之后成品 122×122，和它替掉的那个字形同一档分量", () =>
        {
            var (width, height) = PulseArt.Stroked(PulseArt.Pause, PulseArt.Ink);
            Assert.Equal(122.0, width);
            Assert.Equal(122.0, height);

            // 两条各 32 宽、间距 46；描边往外长 6，于是成品是两条 44 宽、间距 34。
            var left = PulseArt.Bounds([PulseArt.Pause[0]]);
            var right = PulseArt.Bounds([PulseArt.Pause[1]]);
            Assert.Equal(32.0, left.Right - left.Left);
            Assert.Equal(32.0, right.Right - right.Left);
            Assert.Equal(46.0, right.Left - left.Right, "两条之间的空隙");
        });

        Test("播放徽标：等腰、尖角朝右，描边之后成品 112×122", () =>
        {
            var (width, height) = PulseArt.Stroked(PulseArt.Play, PulseArt.Ink);
            Assert.Equal(112.0, width);
            Assert.Equal(122.0, height);

            var triangle = PulseArt.Play[0];
            Assert.Equal(3, triangle.Count);
            Assert.Equal(triangle[0].X, triangle[2].X, "底边两点必须同一个 x，否则不是等腰");
            Assert.Equal((triangle[0].Y + triangle[2].Y) / 2, triangle[1].Y, "尖角要落在底边中点的高度上");
            Assert.True(triangle[1].X > triangle[0].X, "尖角朝右");
        });

        Test("暂停/播放徽标：两个形状都落在方框正中，最粗那道描边也没顶出去", () =>
        {
            foreach (var (name, figures) in new[] { ("暂停", PulseArt.Pause), ("播放", PulseArt.Play) })
            {
                var (x, y) = PulseArt.Centre(figures);
                Assert.Equal(PulseArt.Box / 2, x, $"{name}没有水平居中");
                Assert.Equal(PulseArt.Box / 2, y, $"{name}没有垂直居中");

                // 最粗的那一档（深色描边）往外长 11，顶出方框的下场是被 Canvas 裁掉一条边。
                var (width, height) = PulseArt.Stroked(figures, PulseArt.Rim);
                Assert.True(width <= PulseArt.Box, $"{name}的描边横向顶出了方框：{width} > {PulseArt.Box}");
                Assert.True(height <= PulseArt.Box, $"{name}的描边纵向顶出了方框：{height} > {PulseArt.Box}");
            }
        });

        Test("暂停/播放徽标：背后那道描边比白的那层粗，屏上才看得见一道边", () =>
        {
            // 白三角压在白墙上等于没画，这一圈就是为它存在的；粗细反过来的话它整个躲在白层底下。
            Assert.True(PulseArt.Rim > PulseArt.Ink);
            Assert.Equal(5.0, (PulseArt.Rim - PulseArt.Ink) / 2, "屏上看得见的那道边");
        });
    }

    // ---- 测试用数据 ------------------------------------------------------------

    /// <summary>A 25-minute item of the given type; long enough that the run time never disqualifies it.</summary>
    private static EmbyItem Watchable(string type, params ChapterInfo[] chapters) => new()
    {
        Id = "intro-1",
        Name = "片头测试",
        Type = type,
        RunTimeTicks = TimeSpan.FromMinutes(25).Ticks,
        Chapters = [.. chapters]
    };

    private static EmbyItem Episode(params ChapterInfo[] chapters) =>
        Watchable(EmbyItemType.Episode, chapters);

    private static EmbyItem QueueEpisode(string id, string? seasonId, int season, int number) => new()
    {
        Id = id,
        Type = EmbyItemType.Episode,
        SeriesId = "series1",
        SeasonId = seasonId,
        ParentIndexNumber = season,
        IndexNumber = number
    };

    private static ChapterInfo Chapter(double seconds, string? name = null) => new()
    {
        StartPositionTicks = TimeSpan.FromSeconds(seconds).Ticks,
        Name = name
    };

    private static (PlaybackPlanner Planner, AppSettings Settings) Planner(string? screenshots = null)
    {
        var settings = new AppSettings();
        return (new PlaybackPlanner(settings, new ShaderGroupResolver(settings.Shaders), null, screenshots), settings);
    }

    private static EmbyConnection Connection() => new(
        new Uri("http://192.168.31.230:8896/emby/"),
        "token-abc",
        "user-1",
        "我",
        "果服",
        DeviceIdentity.Create("device-1", "2.0.0"));

    /// <summary>
    /// 计划层用的票。输出尺寸给成 1440p 那块屏：<c>Source()</c> 是 1080p，于是这张票落在「实拍 · 微放大档」，
    /// 也就是这台机器上最常见的那一格。
    /// </summary>
    private static PlaybackTicket Ticket() => new()
    {
        Item = Item("某部电影", id: "42"),
        Source = Source(),
        StartTicks = 0,
        OutputWidth = 2560,
        OutputHeight = 1440
    };

    /// <summary>1080p 电影：日/中双音轨、一条内封 ASS、一条外挂 SRT、一条无法使用的外挂 PGS。</summary>
    private static MediaSource Source() => new()
    {
        Id = "src1",
        Container = "mkv",
        RunTimeTicks = 72_000_000_000,
        MediaStreams =
        [
            Stream(0, "Video", width: 1920, height: 1080),
            Stream(1, "Audio", language: "jpn"),
            Stream(2, "Audio", language: "chi"),
            Stream(3, "Subtitle", codec: "ass", language: "chi"),
            Stream(4, "Subtitle", codec: "srt", language: "chi", external: true),
            Stream(5, "Subtitle", codec: "pgssub", language: "eng", external: true)
        ]
    };

    // 宽高都要给：放大倍数取的是宽比和高比里小的那个，只给高度就等于「片源尺寸未知」。
    private static MediaSource Source1080p(double? frameRate = null) =>
        SourceWith(Stream(0, "Video", width: 1920, height: 1080, frameRate: frameRate));

    private static MediaSource Source4K() => SourceWith(Stream(0, "Video", width: 3840, height: 2160));

    private static ShaderAutomationSettings Shaders(
        bool enabled = true,
        bool anime = true,
        bool vintage = true,
        GpuTier gpu = GpuTier.Low,
        string manual = "") => new()
    {
        Enabled = enabled,
        AutoAnimeProfile = anime,
        RestoreVintageSources = vintage,
        Gpu = gpu,
        ManualGroup = manual
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

    private static PlaybackRequest Request(double start = 0) => new()
    {
        MediaUrl = new Uri("http://server/emby/Videos/1/stream.mkv?Static=true"),
        Title = "片名",
        StartSeconds = start
    };

    private static MediaSource SourceWith(params MediaStream[] streams) =>
        new() { Id = "src1", Container = "mkv", MediaStreams = [.. streams] };

    private static MediaStream Stream(
        int index,
        string type,
        string? language = null,
        string? displayLanguage = null,
        string? codec = null,
        string? title = null,
        int? width = null,
        int? height = null,
        double? frameRate = null,
        bool external = false,
        bool forced = false) =>
        new()
        {
            Index = index,
            Type = type,
            Language = language,
            DisplayLanguage = displayLanguage,
            Codec = codec,
            Title = title,
            Width = width,
            Height = height,
            AverageFrameRate = frameRate,
            IsExternal = external,
            IsForced = forced
        };

    /// <summary>Playback settings with the subtitle priority list under test and nothing else set.</summary>
    private static PlaybackSettings Playback(IEnumerable<string> subtitles) =>
        new() { SubtitleLanguages = [.. subtitles] };

    /// <summary>The built mpv options as a lookup, so a test names the option it cares about.</summary>
    private static Dictionary<string, string> Options(IReadOnlyList<KeyValuePair<string, string>> options)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in options) map[name] = value;
        return map;
    }

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
