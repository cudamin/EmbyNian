using EmbyNian.Configuration;
using EmbyNian.Mpv;
using EmbyNian.Playback;

namespace EmbyNian.Tests;

/// <summary>
/// 独占模式「同一个窗口换片」的判断（<see cref="InlineSwitch"/>，2026-09-19）。全部是纯数据：
/// 签名比什么、哪些属性要拨回去、这一票自己那几条怎么拼。执行只有 <c>LibMpvHandle.SwapToAsync</c>
/// 那几十行属性写，测试在这里把规则钉死 —— 换片错一位的代价是「新一集继承了上一集的宽高比/音量/
/// 字幕延迟」，屏上看不出来但下一集就是不对。
/// </summary>
internal static class InlineSwitchTests
{
    internal static void Register()
    {
        // 签名逐项相等才允许同一个实例换片：管线、管线必需项、Lua UI 项、票里选项表的基线部分。
        // 任何一条不同都只能关窗重开 —— mpv 的启动选项在 initialize 之后改不动。
        TestHarness.Test("换片签名：四条全等才接，差一条就不接", () =>
        {
            var running = Signature(
                VideoPipelineKind.Standalone,
                [new("vo", "gpu-next"), new("hwdec", "d3d11va")],
                [new("osc", "no")],
                [new("volume", "100"), new("deband", "yes")]);

            Assert.True(InlineSwitch.SameSignature(running, running), "同样的签名必须相等");

            Assert.False(InlineSwitch.SameSignature(running, running with { Pipeline = VideoPipelineKind.Integrated }),
                "管线不同不接");
            Assert.False(InlineSwitch.SameSignature(running, running with { PipelineOptions = [new("vo", "gpu")] }),
                "管线必需项不同不接");
            Assert.False(InlineSwitch.SameSignature(running, running with { UiOptions = [new("osc", "yes")] }),
                "Lua UI 项不同不接");
            Assert.False(InlineSwitch.SameSignature(running, running with { BaselineOptions = [new("volume", "80")] }),
                "基线选项不同不接");
            Assert.False(InlineSwitch.SameSignature(running, running with
            {
                BaselineOptions = [new("deband", "yes"), new("volume", "100")],
            }), "顺序也算不同（mpv 按序解析重复选项）");
        });

        // 着色器链的尾巴不参与签名：链是运行期能改的（这个客户端本来就能在播时换档），换片时按新票
        // 重写一遍即可。把它算进去，「同一部剧里 720p 与 1080p 各一集」会白丢掉快路。
        TestHarness.Test("换片签名：选项表尾部那条着色器链不算数", () =>
        {
            var options = new List<KeyValuePair<string, string>>
            {
                new("volume", "100"),
                new("glsl-shaders", "a.hook"),
                new("deband", "yes"),
            };

            var baseline = LibMpvBackend.Baseline(options, chainOptions: 2);

            Assert.Equal(1, baseline.Count);
            Assert.Equal(("volume", "100"), (baseline[0].Key, baseline[0].Value));
            Assert.Equal(3, LibMpvBackend.Baseline(options, chainOptions: 0).Count);
            Assert.Equal(0, LibMpvBackend.Baseline(options, chainOptions: 9).Count);
        });

        // 「这一集改过、下一集不该继承」的名单必须盖住两类名字：画面菜单碰得到的属性，以及任何
        // 着色器链会碰的名字。少一个就是「上一集把画面拉成 16:9 / 加了滤镜 / 调了色，下一集照旧」。
        TestHarness.Test("跟着一集走的名字：菜单与着色器链碰过的都在名单里，位置类不在", () =>
        {
            foreach (var name in new[]
                     {
                         "video-aspect-override", "video-rotate", "video-zoom", "video-pan-x", "video-pan-y",
                         "panscan", "contrast", "brightness", "gamma", "saturation", "hue", "hwdec",
                         "interpolation", "deinterlace", "video-sync", "vf", "af", "audio-channels",
                         "sub-pos", "sub-scale", "sub-ass-override", "ab-loop-a", "ab-loop-b", "loop-file",
                     })
                Assert.True(InlineSwitch.PerFilmNames.Contains(name), $"{name} 必须跟着一集走");

            foreach (var (name, _) in ShaderGroupCatalog.NeutralOptions)
                Assert.True(InlineSwitch.PerFilmNames.Contains(name), $"着色器链碰的 {name} 必须在名单里");

            // 位置类不是设置：换文件时 mpv 自己就拨回去了，写它反而把新片子拖到旧章节上。
            Assert.False(InlineSwitch.PerFilmNames.Contains("chapter"), "chapter 是位置不是设置");
            Assert.Equal(InlineSwitch.PerFilmNames.Count, InlineSwitch.PerFilmNames.Distinct().Count());
        });

        // 拨回去的值：新票说到的名字按新票（同一个名字出现多次取最后一次，与 mpv 解析重复选项一致），
        // 新票没说到就问 mpv 的出厂默认（选项在两集之间被改过时，下一集该跟上）；连 mpv 都没有默认值的
        // 那几个（实测：vf/af/glsl-shaders/cscale）兜空串 —— 漏了这一手，上一集的滤镜与着色器链会跟过来。
        TestHarness.Test("拨回原样：新票优先，其次是出厂默认，最后是空串兜底", () =>
        {
            var defaults = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["panscan"] = "0.0",
                ["sub-scale"] = "1",
                ["video-aspect-override"] = "no",
            };

            var next = Request() with
            {
                PlayerOptions =
                [
                    new("panscan", "1.0"),
                    new("sub-scale", "1.2"),
                    new("sub-scale", "1.5"),   // 最后一次出现才是 mpv 认的那个
                ],
            };

            var plan = InlineSwitch.FilmScoped(defaults, next).ToDictionary(entry => entry.Key, entry => entry.Value);

            Assert.Equal("1.0", plan["panscan"]);                       // 新票优先于出厂默认
            Assert.Equal("1.5", plan["sub-scale"]);                     // 重复项取最后一次
            Assert.Equal("no", plan["video-aspect-override"]);          // 新票没说 → 出厂默认
            Assert.Equal("", plan["glsl-shaders"]);                     // mpv 也没有默认值 → 空串（空链）
            Assert.Equal("", plan["vf"]);                               // 同上：没有滤镜
            Assert.Equal("", plan["cscale"]);                           // NeutralOptions 记的就是空串
            Assert.True(plan.ContainsKey("sub-delay"), "延迟这类固定名单上的名字也该有归属");
            Assert.Equal(InlineSwitch.PerFilmNames.Count, plan.Count);
        });

        // 这一票自己那几条：轨道是「显式写了就照写，没写就 auto」，外挂字幕是替换语义（上一集的
        // 外挂字幕不能跟过来），最后一定把 pause 放回 no（上一集是暂停着被换掉的）。
        TestHarness.Test("这一票自己的属性：轨道 auto/显式、字幕替换、pause 归位", () =>
        {
            var bare = InlineSwitch.PerFile(Request()).ToDictionary(entry => entry.Key, entry => entry.Value);

            Assert.Equal("auto", bare["aid"]);
            Assert.Equal("auto", bare["sid"]);
            Assert.Equal("", bare["sub-files"]);
            Assert.Equal("", bare["alang"]);
            Assert.Equal("", bare["slang"]);
            Assert.Equal("0", bare["start"]);
            Assert.Equal("no", bare["pause"]);

            var chosen = InlineSwitch.PerFile(Request() with
            {
                AudioId = 2,
                SubtitleId = 5,
                StartSeconds = 754.25,
                AudioLanguage = "jpn",
                SubtitleLanguage = "chi",
                ExternalSubtitles = [new Uri("http://server/a,b.srt"), new Uri("http://server/c.srt")],
            }).ToDictionary(entry => entry.Key, entry => entry.Value);

            Assert.Equal("2", chosen["aid"]);
            Assert.Equal("5", chosen["sid"]);
            Assert.Equal("754.25", chosen["start"]);                               // 小数点永远是点
            Assert.Equal("jpn", chosen["alang"]);
            Assert.Equal("chi", chosen["slang"]);
            Assert.Equal("http://server/a\\,b.srt,http://server/c.srt", chosen["sub-files"]);
            Assert.Equal("no", chosen["pause"]);

            var muted = InlineSwitch.PerFile(Request() with { SubtitlesDisabled = true })
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            Assert.Equal("no", muted["sid"]);                                      // 明确关字幕 ≠ 没表态
        });
    }

    private static PlaybackLaunchSignature Signature(
        VideoPipelineKind pipeline,
        IReadOnlyList<KeyValuePair<string, string>> pipelineOptions,
        IReadOnlyList<KeyValuePair<string, string>> uiOptions,
        IReadOnlyList<KeyValuePair<string, string>> baseline) =>
        new(pipeline, pipelineOptions, uiOptions, [], baseline);

    private static PlaybackRequest Request() => new()
    {
        MediaUrl = new Uri("http://server/emby/Videos/1/stream.mkv"),
        Title = "与你相恋到生命尽头 S01E08",
    };
}
