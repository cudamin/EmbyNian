using EmbyNian.Playback;
using EmbyNian.Theming;

namespace EmbyNian.Tests;

/// <summary>
/// 加载遮罩垫底那张图的算法：铺满时取源的哪一块（<see cref="VideoPresentation.FillSource"/>），
/// 以及照遮罩的摆法把一张图烘成不透明的那一步（<see cref="CoverArtwork.Bake"/>）。
/// <para>
/// 两条都是给起播整屏那块 DWM 覆盖层用的（2026-09-25 用户令「不要黑屏，主页和背景图无缝切换」）：
/// 铺错一块，撤层那一刻图上会跳一下；烘错一档，撤层那一刻亮度会跳一下 —— 两样都正好落在这一趟要
/// 消灭的那一瞬里，所以它们不是「看看就行」的读数。
/// </para>
/// </summary>
internal static class CoverArtworkTests
{
    internal static void Register()
    {
        TestHarness.Test("遮罩垫图铺满：图与目标同比例时整幅取用", () =>
        {
            var source = VideoPresentation.FillSource(1280, 720, 2560, 1440, 0, 0);
            Assert.Equal(0d, source.Left);
            Assert.Equal(0d, source.Top);
            Assert.Equal(1280d, source.Width);
            Assert.Equal(720d, source.Height);
        });

        TestHarness.Test("遮罩垫图铺满：图比目标宽就裁左右，居中不越界", () =>
        {
            // 2:1 的图铺进 16:9：高度全留，宽度裁到 720 × 16/9 = 1280。
            var source = VideoPresentation.FillSource(2000, 1000, 1920, 1080, 0, 0);
            Assert.Equal(1000d, source.Height);
            Assert.True(Math.Abs(source.Width - 1000d * 16 / 9) < 0.001);
            Assert.True(Math.Abs(source.Left - (2000 - source.Width) / 2) < 0.001);
            Assert.True(source.Left >= 0 && source.Left + source.Width <= 2000);
        });

        TestHarness.Test("遮罩垫图铺满：图比目标高就裁上下，居中不越界", () =>
        {
            var source = VideoPresentation.FillSource(1000, 1000, 1920, 1080, 0, 0);
            Assert.Equal(1000d, source.Width);
            Assert.True(Math.Abs(source.Height - 1000d * 1080 / 1920) < 0.001);
            Assert.True(Math.Abs(source.Top - (1000 - source.Height) / 2) < 0.001);
            Assert.True(source.Top >= 0 && source.Top + source.Height <= 1000);
        });

        TestHarness.Test("遮罩垫图铺满：可见画面不在原点时整块跟着挪", () =>
        {
            // 缓冲里上下各有黑边（Picture 只圈中间那块）：取源要落在可见画面之内。
            var source = VideoPresentation.FillSource(1280, 720, 2560, 1440, 0, 40);
            Assert.Equal(40d, source.Top);
            Assert.Equal(720d, source.Height);
        });

        TestHarness.Test("遮罩垫图铺满：尺寸说不上来时原样取用", () =>
        {
            var source = VideoPresentation.FillSource(0, 720, 2560, 1440, 0, 0);
            Assert.Equal(0d, source.Width);
            Assert.Equal(720d, source.Height);
            var noTarget = VideoPresentation.FillSource(1280, 720, 0, 1440, 0, 0);
            Assert.Equal(1280d, noTarget.Width);
        });

        TestHarness.Test("遮罩垫图烘色：图压半透明进底色，alpha 写成不透明", () =>
        {
            var pixels = new byte[4 * 2];
            for (var i = 0; i < 2; i++)
            {
                pixels[i * 4] = 0xFF;     // B
                pixels[i * 4 + 1] = 0xFF; // G
                pixels[i * 4 + 2] = 0xFF; // R
                pixels[i * 4 + 3] = 0x00; // A
            }

            CoverArtwork.Bake(pixels, 1, 2, 4, ThemeColor.Rgb(0, 0, 0));

            // 白 255 与黑底各半 = 127.5，进偶数 = 128。
            Assert.Equal((byte)0x80, pixels[0]);
            Assert.Equal((byte)0x80, pixels[1]);
            Assert.Equal((byte)0x80, pixels[2]);
            for (var i = 0; i < 2; i++) Assert.Equal((byte)0xFF, pixels[i * 4 + 3]);
        });

        TestHarness.Test("遮罩垫图烘色：行宽大于像素宽时不动行尾的填充字节", () =>
        {
            var pixels = new byte[8 * 2];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = 0xFF;
            for (var i = 0; i < 2; i++)
            {
                pixels[i * 8 + 4] = 0x00;
                pixels[i * 8 + 5] = 0x00;
                pixels[i * 8 + 6] = 0x00;
                pixels[i * 8 + 7] = 0x00;
            }

            CoverArtwork.Bake(pixels, 1, 2, 8, ThemeColor.Rgb(0, 0, 0));

            Assert.Equal((byte)0x80, pixels[0]);
            Assert.Equal((byte)0xFF, pixels[3]);
            for (var i = 0; i < 2; i++)
            {
                Assert.Equal((byte)0x00, pixels[i * 8 + 4]);
                Assert.Equal((byte)0x00, pixels[i * 8 + 7]);
                Assert.Equal((byte)0x80, pixels[i * 8]);
            }
        });

        TestHarness.Test("遮罩垫图烘色：坏输入不越界、不抛", () =>
        {
            CoverArtwork.Bake([], 0, 0, 0, ThemeColor.Rgb(0, 0, 0));
            CoverArtwork.Bake(new byte[4], 1, 100, 4, ThemeColor.Rgb(0, 0, 0));
            CoverArtwork.Bake(new byte[4], 4, 1, 4, ThemeColor.Rgb(0, 0, 0));
            Assert.True(true);
        });
    }
}
