using System.Text;
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

    // ---- 着色器配置组 ----------------------------------------------------------

    private static void RegisterShaders()
    {
        Test("着色器：两个开关都关时不套用任何配置组", () =>
        {
            var settings = Shaders(all: false, anime: false);
            Assert.Null(settings.Resolve(looksAnimated: true, sourceWidth: 1920, sourceHeight: 1080));
            Assert.Null(settings.Resolve(looksAnimated: false, sourceWidth: 1920, sourceHeight: 1080));
        });

        Test("着色器：对所有视频启用时套用默认组", () =>
        {
            var settings = Shaders(all: true, anime: false);
            Assert.Equal("2K-iGPU", settings.Resolve(looksAnimated: false, sourceWidth: 1920, sourceHeight: 1080));
            Assert.Equal("2K-iGPU", settings.Resolve(looksAnimated: true, sourceWidth: 1920, sourceHeight: 1080), "动画开关没开就不该用动画组");
        });

        Test("着色器：动画开关单独打开时只对动画生效", () =>
        {
            var settings = Shaders(all: false, anime: true);
            Assert.Equal("2K-iGPU-Anime", settings.Resolve(looksAnimated: true, sourceWidth: 1920, sourceHeight: 1080));
            Assert.Null(settings.Resolve(looksAnimated: false, sourceWidth: 1920, sourceHeight: 1080), "非动画不套用配置组");
        });

        Test("着色器：4K 片源在 2K 屏上改用省电组", () =>
        {
            var settings = Shaders(all: true, anime: true);
            Assert.Equal("2K-iGPU-Light", settings.Resolve(looksAnimated: false, sourceWidth: 3840, sourceHeight: 2160));
            Assert.Equal("2K-iGPU-Light", settings.Resolve(looksAnimated: true, sourceWidth: 3840, sourceHeight: 2160), "4K 动画同样只会被缩小");
            Assert.Equal("2K-iGPU-Anime", settings.Resolve(looksAnimated: true, sourceWidth: 1920, sourceHeight: 1080), "1080p 才需要放大链");
            Assert.Equal("2K-iGPU", settings.Resolve(looksAnimated: false, sourceWidth: null, sourceHeight: null), "高度未知时按普通片源处理");
        });

        Test("着色器：DVD 之类的低清片源改用增强组", () =>
        {
            var settings = Shaders(all: true, anime: false);
            Assert.Equal("2K-iGPU-SD", settings.Resolve(looksAnimated: false, sourceWidth: 720, sourceHeight: 576),
                "576p 到 1440p 是 2.5 倍放大，重一点的链才划得来");
            Assert.Equal("2K-iGPU", settings.Resolve(looksAnimated: false, sourceWidth: 1280, sourceHeight: 0),
                "高度为 0 是服务器没给，不能当成 480p");
        });

        Test("着色器：8K 片源直接关掉着色器", () =>
        {
            var settings = Shaders(all: true, anime: true);
            Assert.Null(settings.Resolve(looksAnimated: false, sourceWidth: 7680, sourceHeight: 4320),
                "8K 解码本身就吃满核显，再叠着色器只会卡");

            settings.DisableForUltraHighRes = false;
            Assert.Equal("2K-iGPU-Light", settings.Resolve(looksAnimated: false, sourceWidth: 7680, sourceHeight: 4320),
                "关掉这条特例后仍按高分辨率规则走");
        });

        Test("着色器：清空高分辨率组即关闭该特例", () =>
        {
            var settings = Shaders(all: true, anime: true);
            settings.HighResProfile = "";
            Assert.Equal("2K-iGPU", settings.Resolve(looksAnimated: false, sourceWidth: 3840, sourceHeight: 2160));
        });

        Test("着色器：动画判定只看类型/风格与标签", () =>
        {
            var resolver = new ShaderGroupResolver(Shaders(all: false, anime: true));

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
            var resolver = new ShaderGroupResolver(Shaders(all: false, anime: true));
            var episode = Item("第 1 集", type: EmbyItemType.Episode);
            var series = Item("葬送的芙莉莲", type: EmbyItemType.Series, genres: ["动画"]);

            Assert.False(resolver.Resolve(episode, null).HasGroup, "单集自己通常没有风格");
            Assert.Equal("2K-iGPU-Anime", resolver.Resolve(episode, null, series).Group, "有剧集兜底时应命中");
        });

        Test("着色器：决策原因会写进日志", () =>
        {
            var resolver = new ShaderGroupResolver(Shaders(all: true, anime: true));
            var decision = resolver.Resolve(Item("某部电影"), Source1080p());
            Assert.True(decision.HasGroup, "开关已开");
            Assert.Contains("已对所有视频启用", decision.Reason);

            var high = resolver.Resolve(Item("某部电影"), Source4K());
            Assert.Contains("只会缩小", high.Reason);
        });

        Test("着色器组：每组都自带缩放器，路径是程序目录下的绝对路径", () =>
        {
            foreach (var group in ShaderGroupCatalog.All)
            {
                Assert.True(group.Shaders.Count > 0, $"{group.Name} 一个着色器都没有");

                var options = Options(group.ToMpvOptions(ShaderGroupCatalog.ShaderRoot));
                Assert.True(options.ContainsKey("glsl-shaders"), $"{group.Name} 没给出 glsl-shaders");
                Assert.True(options.ContainsKey("scale"), $"{group.Name} 没给出 scale，会沿用上一部片子的设置");

                foreach (var path in options["glsl-shaders"].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    Assert.True(Path.IsPathRooted(path), $"{path} 不是绝对路径；mpv 用 --no-config 启动，~~/ 已经无处可解析");
                }
            }
        });

        Test("着色器组：关闭配置组时把 mpv 自己的默认值还回去", () =>
        {
            var neutral = Options(ShaderGroupCatalog.NeutralOptions);

            Assert.Equal("", neutral["glsl-shaders"], "不清空的话着色器会一直挂着");
            Assert.Equal("lanczos", neutral["scale"], "mpv --no-config --list-options 报的默认值");
            Assert.Equal("hermite", neutral["dscale"]);
            Assert.Equal("", neutral["cscale"]);

            foreach (var group in ShaderGroupCatalog.All)
            {
                foreach (var (name, _) in group.ToMpvOptions(ShaderGroupCatalog.ShaderRoot))
                {
                    Assert.True(neutral.ContainsKey(name), $"{group.Name} 改了 {name}，但关闭时没有还原它");
                }
            }
        });

        Test("着色器组：客户端内置的组名与设置里的默认值对得上", () =>
        {
            var settings = new ShaderAutomationSettings();
            foreach (var name in new[] { settings.DefaultProfile, settings.AnimeProfile, settings.HighResProfile, settings.LowResProfile })
            {
                Assert.NotNull(ShaderGroupCatalog.Find(name), $"设置里默认选的 {name} 在内置目录里不存在");
            }
        });

        // 「把 mpv_config 里的：NNEDI3、NNEDI3+、ravu-zoom、FSRCNNX、AnimeJaNai、Ani4K、AniSD、Anime4K、
        // SSIM 这些着色器配置组复制一份添加到 EmbyNian 里」。名字是用户自己的 [profile] 段名，原样保留。
        Test("着色器组：从 mpv.conf 移植的九组都在，名字一字不改", () =>
        {
            string[] ported = ["NNEDI3", "NNEDI3+", "ravu-zoom", "FSRCNNX", "AnimeJaNai", "Ani4K", "AniSD", "Anime4K", "SSIM"];

            foreach (var name in ported)
            {
                var group = ShaderGroupCatalog.Find(name);
                Assert.NotNull(group, $"移植的配置组 {name} 不在内置目录里");

                var options = Options(group!.ToMpvOptions(ShaderGroupCatalog.ShaderRoot));
                Assert.Equal("ewa_lanczossharp", options["scale"], $"{name} 原来跑在 mpv.conf 的全局块下，放大器必须一致");
                Assert.Equal("mitchell", options["dscale"], $"{name} 链尾是 SSimDownscaler，它要求 dscale=mitchell");
                Assert.False(options.ContainsKey("deband"), $"{name} 不该动去色带，那是设置页的事");
            }

            Assert.Equal(14, ShaderGroupCatalog.All.Count, "内置五组加移植九组");
            Assert.Equal(ShaderGroupCatalog.All.Count, ShaderGroupCatalog.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "组名不能重复，否则 Find 只找得到第一个");
        });

        Test("着色器组：任何组都不再设置 deband，「在动画中开启」才不会被覆盖", () =>
        {
            foreach (var group in ShaderGroupCatalog.All)
            {
                Assert.False(Options(group.ToMpvOptions(ShaderGroupCatalog.ShaderRoot)).ContainsKey("deband"),
                    $"{group.Name} 设了 deband，会盖掉设置页的去色带选择");
            }

            Assert.False(Options(ShaderGroupCatalog.NeutralOptions).ContainsKey("deband"),
                "还原表里也不能有 deband：播放中切换配置组会把启动时的去色带值抹掉");
        });

        Test("着色器：动画判定与是否用了动画组无关", () =>
        {
            // 去色带 =「在动画中开启」读的是这个标记，所以它必须是「这部片是不是动画」，而不是
            // 「这次用了动画配置组吗」——否则关掉自动动画组就会连带把去色带也关了。
            var resolver = new ShaderGroupResolver(Shaders(all: true, anime: false));
            var decision = resolver.Resolve(Item("紫罗兰永恒花园", genres: ["动画"]), Source1080p());

            Assert.True(decision.Animated, "自动动画组关着，但这部片仍然是动画");
            Assert.Equal("2K-iGPU", decision.Group, "开关关着就不该换成动画组");

            var live = new ShaderGroupResolver(Shaders(all: true, anime: true))
                .Resolve(Item("某部电影", genres: ["剧情"]), Source1080p());
            Assert.False(live.Animated);
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

        Test("计划：着色器配置组连同它自己的缩放器一起变成 mpv 选项", () =>
        {
            var (planner, settings) = Planner();

            var request = planner.Plan(Ticket(), Connection());
            Assert.Equal("2K-iGPU", request.ShaderProfile);

            var options = Options(request.PlayerOptions);
            Assert.True(options.TryGetValue("glsl-shaders", out var shaders) && shaders.Length > 0,
                "配置组必须落到 glsl-shaders 上");
            Assert.Equal(Options(ShaderGroupCatalog.Find("2K-iGPU")!.Options)["scale"], options["scale"],
                "配置组自带的缩放器要跟着一起给出，否则这组的调法就不成立了");

            settings.Shaders.ApplyToAllVideos = false;
            settings.Shaders.AutoAnimeProfile = false;
            var plain = planner.Plan(Ticket(), Connection());
            Assert.Null(plain.ShaderProfile);
            Assert.False(Options(plain.PlayerOptions).ContainsKey("glsl-shaders"),
                "没有配置组时不必提 glsl-shaders，mpv 自己就是空的");
        });

        Test("计划：配置组不存在时照旧播放，只是不上着色器", () =>
        {
            var (planner, settings) = Planner();
            settings.Shaders.DefaultProfile = "并不存在的组";

            var request = planner.Plan(Ticket(), Connection());
            Assert.Equal("并不存在的组", request.ShaderProfile, "决策照实记下来，日志里才看得出问题");
            Assert.False(Options(request.PlayerOptions).ContainsKey("glsl-shaders"),
                "找不到组就别乱传路径，mpv 会因为文件不存在直接退出");
        });

        Test("计划：客户端自己的基线选项排在设置之前", () =>
        {
            var (planner, _) = Planner();
            var options = Options(planner.Plan(Ticket(), Connection()).PlayerOptions);

            // 这些原本靠 mpv.conf 全局生效，配置文件不读之后必须由客户端自己给出。
            Assert.Equal("yes", options["hr-seek"]);
            Assert.Equal("no", options["sub-auto"], "服务器已经把外挂字幕列全了，再扫一遍目录只会多出重复轨");
            Assert.Equal("no", options["audio-file-auto"]);
            Assert.Equal("no", options["icc-profile-auto"]);
        });

        Test("计划：轨道建议按偏好语言给出默认值", () =>
        {
            var (planner, settings) = Planner();
            settings.Playback.AudioTrack = AudioTrackMode.Language;
            settings.Playback.AudioLanguage = "日语";
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
            settings.Playback.AudioTrack = AudioTrackMode.Language;
            settings.Playback.AudioLanguage = "英语";

            var request = planner.Plan(Ticket(), Connection());

            Assert.Equal("zh-Hans,zh_hans,zh-CN,zh_CN,zhs,sc,chs,chi-Hans,zh,chi,zho,zh-Hant,zh_hant,zh-TW,zh_TW,zh-HK,zh_HK,zht,tc,chi-Hant",
                request.SubtitleLanguage);
            Assert.Equal("eng,en", request.AudioLanguage, "音轨只有一种语言，没有优先级列表");
        });

        Test("计划：空的语言优先级不传 slang/alang", () =>
        {
            var (planner, settings) = Planner();
            settings.Playback.SubtitleLanguages = [];
            settings.Playback.AudioTrack = AudioTrackMode.ServerDefault;
            settings.Playback.AudioLanguage = "日语";
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

            settings.AudioTrack = AudioTrackMode.Language;
            settings.AudioLanguage = "日语";
            Assert.Equal(1, TrackSelection.ChooseAudio(settings, source)!.Index);

            settings.AudioLanguage = "韩语";
            Assert.Equal(2, TrackSelection.ChooseAudio(settings, source)!.Index, "没有韩语音轨就回到默认轨，而不是没有声音");
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
            Assert.Equal("d3d11", options["gpu-api"]);
            Assert.Equal("auto", options["dither-depth"]);
            Assert.Equal("fruit", options["dither"]);
            Assert.Equal("6", options["dither-size-fruit"]);
            Assert.Equal("full", options["video-output-levels"], "「色彩范围默认使用 PC(0-255)」");
            Assert.False(options.ContainsKey("hwdec"), "硬解留空是「让 mpv 自己决定」，不能替用户挑一个");
            Assert.False(options.ContainsKey("profile"), "画质预设出厂是 default，什么都不该传");
        });

        Test("输出：每一项都能单独关回 mpv 自己的默认值", () =>
        {
            var blank = new VideoSettings
            {
                QualityPreset = "",
                Renderer = "",
                GpuApi = "",
                OutputLevels = "",
                Dither = "",
                Deband = "",
                HdrMode = "",
                HighFrameRateAudioSync = false
            };

            var options = MpvOutputOptions.Build(blank, new AudioSettings());
            Assert.Equal(0, options.Count, "全部选「不指定」时就该一个选项都不传");
        });

        // 「画质与着色器板块新增 mpv 画质预设配置 profile=high-quality、profile=default、profile=HQ」
        Test("输出：画质预设 default 什么都不传，high-quality 走 mpv 内置 profile", () =>
        {
            var none = MpvOutputOptions.Build(new VideoSettings { QualityPreset = "default", OutputLevels = "" }, new AudioSettings());
            Assert.False(Options(none).ContainsKey("profile"), "mpv 的 [default] 在 --no-config 下是空的，传了也是白传");

            var high = Options(MpvOutputOptions.Build(new VideoSettings { QualityPreset = "high-quality" }, new AudioSettings()));
            Assert.Equal("high-quality", high["profile"], "这个 profile 编在 mpv 里，不靠配置文件");
        });

        Test("输出：画质预设 HQ 展开成 mpv.conf 原来那十条", () =>
        {
            // HQ 是用户自己 mpv.conf 里的段名，mpv 里并没有这个 profile，传 profile=HQ 会让 mpv 直接报错退出。
            var options = Options(MpvOutputOptions.Build(new VideoSettings { QualityPreset = "HQ" }, new AudioSettings()));

            Assert.False(options.ContainsKey("profile"), "mpv 不认识 HQ，只能逐条展开");
            Assert.Equal("ewa_lanczossharp", options["scale"]);
            Assert.Equal("bilinear", options["cscale"]);
            Assert.Equal("lanczos", options["dscale"]);
            Assert.Equal("0.5", options["scale-antiring"]);
            Assert.Equal("yes", options["sigmoid-upscaling"]);
            Assert.Equal("no", options["linear-downscaling"]);
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

            var silly = Options(MpvOutputOptions.Build(new VideoSettings(), new AudioSettings { Volume = 3000 }));
            Assert.False(silly.ContainsKey("volume"), "手改过的设置文件不该把音量顶到天上去");
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

    private static (PlaybackPlanner Planner, AppSettings Settings) Planner()
    {
        var settings = new AppSettings();
        return (new PlaybackPlanner(settings, new ShaderGroupResolver(settings.Shaders)), settings);
    }

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
            Title = title,
            Height = height,
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
