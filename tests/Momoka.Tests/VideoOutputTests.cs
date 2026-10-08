using Momoka.Configuration;
using Momoka.Infrastructure;
using Momoka.Mpv;
using static Momoka.Tests.TestHarness;

namespace Momoka.Tests;

internal static class VideoOutputTests
{
    public static void Register()
    {
        Test("视频设置：识别到的选项必须写成内核接受的规范大小写", () =>
        {
            var settings = new AppSettings();
            settings.Video.QualityPreset = " FAST ";
            settings.Video.Renderer = "GPU-NEXT";
            settings.Video.GpuApi = "D3D11";
            settings.Video.HardwareDecoding = "AUTO-SAFE";
            settings.Video.OutputLevels = "FULL";
            settings.Video.VideoSync = "DISPLAY-RESAMPLE";
            settings.Video.Tscale = "LANCZOS";
            settings.Video.Dither = "Fruit";
            settings.Video.Deband = "AUTO";
            settings.Video.HdrMode = "TONEMAP";
            settings.Video.DeinterlaceMode = "AUTO";
            settings.Video.DitherDepth = "AUTO";
            settings.Video.DebandStrength = "HIGH";
            settings.Video.ToneMapping = "Spline";
            settings.Video.HdrComputePeak = "YES";
            SettingsMigration.Normalize(settings);
            Assert.Equal("fast", settings.Video.QualityPreset);
            Assert.Equal("gpu-next", settings.Video.Renderer);
            Assert.Equal("d3d11", settings.Video.GpuApi);
            Assert.Equal("auto-safe", settings.Video.HardwareDecoding);
            Assert.Equal("full", settings.Video.OutputLevels);
            Assert.Equal("display-resample", settings.Video.VideoSync);
            Assert.Equal("lanczos", settings.Video.Tscale);
            Assert.Equal("fruit", settings.Video.Dither);
            Assert.Equal("auto", settings.Video.Deband);
            Assert.Equal("tonemap", settings.Video.HdrMode);
            Assert.Equal("auto", settings.Video.DeinterlaceMode);
            Assert.Equal("auto", settings.Video.DitherDepth);
            Assert.Equal("high", settings.Video.DebandStrength);
            Assert.Equal("spline", settings.Video.ToneMapping);
            Assert.Equal("yes", settings.Video.HdrComputePeak);
        });

        Test("视频设置：大小写变体归一后仍执行明确的 HDR 与去色带语义", () =>
        {
            var settings = new AppSettings();
            settings.Video.HdrMode = "TONEMAP";
            settings.Video.Deband = "YES";
            settings.Video.DeinterlaceMode = "YES";
            settings.Video.IccProfileAuto = true;
            SettingsMigration.Normalize(settings);
            var output = Last(MpvOutputOptions.Build(settings.Video, settings.Audio, source: new(3840, 2160, 10, 24, true)));
            Assert.Equal("bt.1886", output.GetValueOrDefault("target-trc", ""));
            Assert.Equal("bt.709", output.GetValueOrDefault("target-prim", ""));
            Assert.Equal("yes", output.GetValueOrDefault("deband", ""));
            Assert.Equal("yes", output.GetValueOrDefault("deinterlace", ""));
            Assert.False(output.ContainsKey("icc-profile-auto"));
        });

        Test("HDR 菜单：不能仅切标记却继续保留 SDR 目标", () =>
        {
            var hdr = PlayerMenuCatalog.Flatten(PlayerMenuCatalog.Root).Single(node => node.Label == "HDR 相关");
            var modes = hdr.Children.SingleOrDefault(node => node.Label == "输出模式");
            Assert.True(modes is not null, "应提供成套输出模式，而不是只 cycle 色彩空间标记");
            var current = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target-trc"] = "bt.1886",
                ["target-prim"] = "bt.709",
                ["target-peak"] = "600",
                ["icc-profile-auto"] = "yes",
                ["target-colorspace-hint"] = "no"
            };
            var output = modes!.Children.Single(node => node.Label.StartsWith("HDR 输出", StringComparison.Ordinal));
            Apply(output, current);
            Assert.Equal("auto", current["target-trc"]);
            Assert.Equal("auto", current["target-prim"]);
            Assert.Equal("auto", current["target-peak"]);
            Assert.Equal("auto", current["target-colorspace-hint"]);
            Assert.Equal("target", current["target-colorspace-hint-mode"]);
            Assert.Equal("no", current["icc-profile-auto"]);
            Assert.False(current.ContainsKey("tone-mapping"), "不能强制截高光");
            Apply(modes.Children.Single(node => node.Label == "映射到 SDR"), current);
            Assert.Equal("bt.1886", current["target-trc"]);
            Assert.Equal("bt.709", current["target-prim"]);
            Assert.Equal("auto", current["target-peak"]);
            Assert.Equal("no", current["target-colorspace-hint"]);
        });

        Test("HDR：四个亮度必须归一为内核接受的整数，对比度保留小数", () =>
        {
            var settings = new AppSettings();
            settings.Video.HdrMode = "passthrough";
            settings.Video.HdrPeakNits = 650.5;
            settings.Video.HdrReferenceWhiteNits = 203.4;
            settings.Video.HdrSubtitleNits = 150.5;
            settings.Video.HdrImageSubtitleNits = 220.6;
            settings.Video.HdrContrastRecovery = 0.55;
            SettingsMigration.Normalize(settings);
            Assert.Equal(651d, settings.Video.HdrPeakNits);
            Assert.Equal(203d, settings.Video.HdrReferenceWhiteNits);
            Assert.Equal(151d, settings.Video.HdrSubtitleNits);
            Assert.Equal(221d, settings.Video.HdrImageSubtitleNits);
            Assert.Equal(0.55, settings.Video.HdrContrastRecovery);
        });

        Test("HDR：未经保存的手动亮度同样只向内核发送整数", () =>
        {
            var video = new VideoSettings
            {
                HdrMode = "passthrough",
                HdrPeakNits = 650.5,
                HdrReferenceWhiteNits = 203.4,
                HdrSubtitleNits = 150.5,
                HdrImageSubtitleNits = 220.6,
                HdrContrastRecovery = 0.55
            };
            var output = Last(HdrOptions.Build(video, new SourceProfile(3840, 2160, 10, 24, true)));
            Assert.Equal("651", output["target-peak"]);
            Assert.Equal("203", output["hdr-reference-white"]);
            Assert.Equal("151", output["sub-hdr-peak"]);
            Assert.Equal("221", output["image-subs-hdr-peak"]);
            Assert.Equal("0.55", output["hdr-contrast-recovery"]);
        });

        Test("HDR：四个亮度参数独立且不会挤进 SDR 的 HDR 峰值", () =>
        {
            var video = new VideoSettings
            {
                HdrMode = "passthrough",
                HdrPeakNits = 600,
                HdrReferenceWhiteNits = 180,
                HdrSubtitleNits = 150,
                HdrImageSubtitleNits = 220
            };
            var hdr = new SourceProfile(3840, 2160, 10, 24, true);
            var output = Last(HdrOptions.Build(video, hdr));
            Assert.Equal("600", output["target-peak"]);
            Assert.Equal("180", output["hdr-reference-white"]);
            Assert.Equal("150", output["sub-hdr-peak"]);
            Assert.Equal("220", output["image-subs-hdr-peak"]);
            video.HdrMode = "tonemap";
            output = Last(HdrOptions.Build(video, hdr));
            Assert.False(output.ContainsKey("target-peak"));
            Assert.Equal("180", output["hdr-reference-white"]);
            Assert.Equal("150", output["sub-hdr-peak"]);
            Assert.Equal("220", output["image-subs-hdr-peak"]);
        });

        Test("HDR：元数据八种组合只移除明确关闭的层", () =>
        {
            for (var mask = 0; mask < 8; mask++)
            {
                var video = new VideoSettings
                {
                    DolbyVisionMetadata = (mask & 1) == 0,
                    DolbyVisionEnhancementLayer = (mask & 2) == 0,
                    Hdr10PlusMetadata = (mask & 4) == 0
                };
                var output = Last(HdrOptions.Build(video, null));
                var filter = output.GetValueOrDefault("vf", "");
                Assert.Equal(mask != 0, output.ContainsKey("vf"));
                Assert.Equal((mask & 1) != 0, filter.Contains("dolbyvision=no", StringComparison.Ordinal));
                Assert.Equal((mask & 2) != 0, filter.Contains("enhancement-layer=no", StringComparison.Ordinal));
                Assert.Equal((mask & 4) != 0, filter.Contains("hdr10plus=no", StringComparison.Ordinal));
            }
        });

        Test("着色器：直接放到目标尺寸的链不能附带永不执行的 SSimSuperRes", () =>
        {
            foreach (var group in ShaderGroupCatalog.All.Where(group => group.Shaders.Contains(ShaderLibrary.RavuZoom)))
                Assert.False(group.Shaders.Contains(ShaderLibrary.SsimSuperRes), group.DisplayName);
            Assert.True(ShaderGroupCatalog.All.Any(group => group.Shaders.Contains(ShaderLibrary.SsimSuperRes)), "仍保留超过两倍时可执行的组合");
        });

        Test("着色器：播放快照不跟随以后修改的偏好或关键词", () =>
        {
            var settings = new ShaderAutomationSettings { Enabled = true, Gpu = GpuTier.High, AnimeKeywords = ["动画"] };
            var snapshot = settings.Snapshot();
            settings.Enabled = false;
            settings.Gpu = GpuTier.Low;
            settings.AnimeKeywords.Clear();
            Assert.True(snapshot.Enabled);
            Assert.Equal(GpuTier.High, snapshot.Gpu);
            Assert.Equal("动画", snapshot.AnimeKeywords.Single());
        });

        Test("着色器：运行期菜单不能打破当前链的前置条件", () =>
        {
            var vintage = ShaderGroupCatalog.For(GpuTier.Low, vintage: true)[1];
            var plain = ShaderGroupCatalog.For(GpuTier.Low)[1];
            Assert.True(ShaderChainRules.ConflictsWith(vintage, ["cycle", "deband"]));
            Assert.True(ShaderChainRules.ConflictsWith(vintage, ["set", "deband", "yes"]));
            Assert.False(ShaderChainRules.ConflictsWith(vintage, ["set", "deband", "no"]));
            Assert.False(ShaderChainRules.ConflictsWith(plain, ["cycle", "deband"]));
            Assert.False(ShaderChainRules.ConflictsWith(null, ["cycle", "deband"]));
            Assert.False(ShaderChainRules.ConflictsWith(vintage, ["add", "brightness", "1"]));
        });

        Test("着色器：名单 JSON 保留空列表、分号和反斜线，畸形输入不可冒充空", () =>
        {
            Assert.Equal(0, MpvListValue.ParseStrings("[]")!.Count);
            Assert.Null(MpvListValue.ParseStrings(null));
            Assert.Null(MpvListValue.ParseStrings("[\"one\",2]"));
            Assert.Null(MpvListValue.ParseStrings("{\"one\":2}"));
            var paths = MpvListValue.ParseStrings("""["C:/one;two.glsl","C:\\back\\name.glsl"]""")!;
            Assert.Equal("C:/one;two.glsl", paths[0]);
            Assert.Equal(@"C:\back\name.glsl", paths[1]);
            Assert.Equal(@"C:/one\;two.glsl;C:\back\name.glsl", MpvListValue.JoinFiles(paths));
        });

        Test("HDR：非有限亮度不发给内核，有限数值逐项夹紧", () =>
        {
            var settings = new AppSettings();
            settings.Video.HdrPeakNits = double.NaN;
            settings.Video.HdrReferenceWhiteNits = double.PositiveInfinity;
            settings.Video.HdrSubtitleNits = -1;
            settings.Video.HdrImageSubtitleNits = 20000;
            settings.Video.HdrContrastRecovery = 3;
            SettingsMigration.Normalize(settings);
            Assert.Null(settings.Video.HdrPeakNits);
            Assert.Null(settings.Video.HdrReferenceWhiteNits);
            Assert.Equal(10d, settings.Video.HdrSubtitleNits);
            Assert.Equal(10000d, settings.Video.HdrImageSubtitleNits);
            Assert.Equal(2d, settings.Video.HdrContrastRecovery);
        });
    }

    private static Dictionary<string, string> Last(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in pairs) result[name] = value;
        return result;
    }

    private static void Apply(PlayerMenuNode node, Dictionary<string, string> values)
    {
        foreach (var command in node.Commands)
        {
            Assert.Equal("set", command[0]);
            values[command[1]] = command[2];
        }
    }
}
