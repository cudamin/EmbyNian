using EmbyNian.Configuration;
using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 字幕示例预览的换算（SubtitlePreviewPlan.Plan）：设置卡上那十几行外观合在一起该画成什么样子。
/// <para>
/// 钉的是三件事：出厂那套长什么样（这是一切对比的基准）、每个「不设置」落到 mpv 的哪个默认值
/// （预览不能在播放画得出东西的地方画出空）、以及 底板 三档各画些什么。像素比是调用方给的
/// （<c>scale</c>），测试用 1 好对数；「描边 1.65」「字号 38」「底板黑 69%」这三个 mpv 出厂值的出处
/// 写在 SubtitlePreviewPlan 自己的注释里。
/// </para>
/// </summary>
internal static class SubtitlePreviewTests
{
    public static void Register()
    {
        RegisterDefaults();
        RegisterUnsetFallbacks();
        RegisterPlate();
        RegisterRing();
    }

    private static void RegisterDefaults()
    {
        Test("字幕预览：出厂外观在 scale=1 下画成什么样", () =>
        {
            var plan = SubtitlePreviewPlan.Plan(new PlaybackSettings(), scale: 1, text: "字幕示例");

            Assert.Equal("Microsoft YaHei", plan.FontFamily, "预览的字体就是设置里那个族名（v14 起的默认）");
            Assert.Equal(50, plan.FontSize, "出厂字号 50，scale=1 时一个单位一个像素");
            Assert.False(plan.Bold, "出厂不加粗（v13 起的默认）");
            Assert.Equal("字幕示例", plan.Text);
            Assert.Equal("#FFFFFF", plan.TextColor);
            Assert.False(plan.Plate, "出厂没有底板");

            // 阴影在最底下（黑、60%），描边一圈八份（0.5、黑、不透明），共九层。
            Assert.Equal(9, plan.Layers.Count);
            var shadow = plan.Layers[0];
            Assert.Equal(0.5, shadow.X);
            Assert.Equal(0.5, shadow.Y);
            Assert.Equal("#000000", shadow.Color);
            Assert.True(Math.Abs(shadow.Opacity - 0.6) < 0.001, "底板颜色出厂是黑、不透明度出厂是 60%");
            Assert.Equal(0.5, plan.Layers[1].X, "描边第一份在正右方");
        });

        Test("字幕预览：scale 是调用方的像素比，字号、描边、阴影一起放大", () =>
        {
            var plan = SubtitlePreviewPlan.Plan(new PlaybackSettings(), scale: 1.3, text: "字幕示例");

            Assert.True(Math.Abs(plan.FontSize - 65) < 0.001);
            Assert.True(Math.Abs(plan.Layers[0].X - 0.65) < 0.001, "阴影跟着 scale 走");
            Assert.True(Math.Abs(plan.Layers[1].X - 0.65) < 0.001, "描边跟着 scale 走");
        });

        Test("字幕预览：字号填 0 表示用 mpv 自己的 38", () =>
        {
            var plan = SubtitlePreviewPlan.Plan(
                new PlaybackSettings { SubtitleFontSize = 0 }, scale: 1, text: "字幕示例");

            Assert.Equal(38, plan.FontSize);
        });
    }

    private static void RegisterUnsetFallbacks()
    {
        Test("字幕预览：「不设置」的各项落到 mpv 自己的默认值上", () =>
        {
            var plan = SubtitlePreviewPlan.Plan(new PlaybackSettings
            {
                SubtitleFontSize = 0,
                SubtitleColor = "",
                SubtitleBorderSize = "",
                SubtitleBorderColor = "",
                SubtitleShadowOffset = "",
                SubtitleBackColor = ""
            }, scale: 1, text: "字幕示例");

            Assert.Equal(38, plan.FontSize, "mpv 自己的字号");
            Assert.Equal("#FFFFFF", plan.TextColor, "mpv 自己的文字颜色");
            Assert.True(Math.Abs(plan.Layers[0].X - 1.65) < 0.001, "描边不设置是 mpv 自己的 1.65，不是出厂那档 0.5");
            Assert.Equal(8, plan.Layers.Count, "阴影不设置就是没有阴影（mpv 自己的 0），只剩描边那一圈八份");
        });

        Test("字幕预览：不是颜色的存值退到 mpv 的默认，而不是画成透明", () =>
        {
            var plan = SubtitlePreviewPlan.Plan(new PlaybackSettings
            {
                SubtitleColor = "黄色",
                SubtitleBorderColor = "#12345",
                SubtitleBackColor = "none"
            }, scale: 1, text: "字幕示例");

            Assert.Equal("#FFFFFF", plan.TextColor);
            Assert.Equal("#000000", plan.Layers[1].Color, "描边颜色退到 mpv 自己的黑");
            Assert.Equal("#000000", plan.Layers[0].Color, "底板颜色退到 mpv 自己的黑");
            Assert.True(Math.Abs(plan.Layers[0].Opacity - 0.69) < 0.001,
                "没挑过底板颜色时，透明度也是 mpv 自己那份（约 69%），不走设置里的 60%");
        });

        Test("字幕预览：描边填 0 就真的没有描边", () =>
        {
            var plan = SubtitlePreviewPlan.Plan(
                new PlaybackSettings { SubtitleBorderSize = "0" }, scale: 1, text: "字幕示例");

            Assert.Equal(1, plan.Layers.Count, "只剩阴影那一层");
            Assert.Equal(0.5, plan.Layers[0].X);
        });
    }

    private static void RegisterPlate()
    {
        Test("字幕预览：底板三档各画些什么", () =>
        {
            var subtitles = new PlaybackSettings { SubtitleBackColor = "#123456", SubtitleBackOpacity = 40 };

            var off = SubtitlePreviewPlan.Plan(subtitles, scale: 1, text: "字幕示例");
            Assert.False(off.Plate, "底板关着就没有板，颜色只给阴影上");
            Assert.Equal("#123456", off.Layers[0].Color);
            Assert.True(Math.Abs(off.Layers[0].Opacity - 0.4) < 0.001, "阴影用的是底板颜色那行的透明度");

            var box = SubtitlePreviewPlan.Plan(
                new PlaybackSettings
                {
                    SubtitleBackColor = "#123456",
                    SubtitleBackOpacity = 40,
                    SubtitleBackStyle = "background-box"
                }, scale: 1, text: "字幕示例");
            Assert.True(box.Plate);
            Assert.True(Math.Abs(box.PlateOpacity - 0.4) < 0.001, "贴着字的底板按那一行的透明度画");
            Assert.Equal(9, box.Layers.Count, "背景盒不收阴影，描边和阴影照旧");

            var opaque = SubtitlePreviewPlan.Plan(
                new PlaybackSettings
                {
                    SubtitleBackColor = "#123456",
                    SubtitleBackOpacity = 40,
                    SubtitleBackStyle = "opaque-box"
                }, scale: 1, text: "字幕示例");
            Assert.True(opaque.Plate);
            Assert.Equal(1, opaque.PlateOpacity, "整行不透明方框就是 100%");
            Assert.Equal(8, opaque.Layers.Count, "实心板把阴影盖得死死的，再画一层是白画");
        });
    }

    private static void RegisterRing()
    {
        Test("字幕预览：描边那圈是八个方向、四十五度一份", () =>
        {
            var plan = SubtitlePreviewPlan.Plan(
                new PlaybackSettings { SubtitleShadowOffset = "0", SubtitleBorderSize = "1" },
                scale: 1, text: "字幕示例");

            Assert.Equal(8, plan.Layers.Count);

            for (var step = 0; step < 8; step++)
            {
                var layer = plan.Layers[step];
                var expectedX = Math.Cos(step * Math.PI / 4);
                var expectedY = Math.Sin(step * Math.PI / 4);

                Assert.True(Math.Abs(layer.X - expectedX) < 0.001, $"第 {step} 份的横向偏移");
                Assert.True(Math.Abs(layer.Y - expectedY) < 0.001, $"第 {step} 份的纵向偏移");
            }

            Assert.Equal(1, plan.Layers[0].Opacity, "描边是不透明的");
        });
    }
}
