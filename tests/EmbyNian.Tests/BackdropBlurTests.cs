using EmbyNian.Emby;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 集页头图的模糊（「增加集页面背景图的模糊程度」），不开窗就答得了的那几问：尺寸一个像素不动、纯色
/// 原样、尖峰被摊开而且摊开的总量不变。算法本身见 <see cref="BackdropBlur"/>。
/// </summary>
internal static class BackdropBlurTests
{
    public static void Register()
    {
        Test("模糊：裁切半径是基础加占比乘加量，量化成档，门槛以内不糊", () =>
        {
            // 「当窗口拉宽导致背景图下方被裁切的时候，触发背景图模糊，裁切越多越模糊」：电影和剧没有基础
            // 半径，裁多少加多少；占比还没爬过半档就是 0，爬过了整档一跳。
            Assert.Equal(0, BackdropBlur.CropRadius(0, 0.25, 0, 32, 8));
            Assert.Equal(32, BackdropBlur.CropRadius(1, 0.25, 0, 32, 8));

            // 「提高拉长窗口后，背景被裁切触发背景图模糊的阈值」：门槛四分之一，以内不起糊，刚过线从
            // 第一档起步。
            Assert.Equal(0, BackdropBlur.CropRadius(0.1, 0.25, 0, 32, 8));
            Assert.Equal(0, BackdropBlur.CropRadius(0.25, 0.25, 0, 32, 8));
            Assert.Equal(8, BackdropBlur.CropRadius(0.26, 0.25, 0, 32, 8));
            Assert.Equal(8, BackdropBlur.CropRadius(0.3, 0.25, 0, 32, 8));

            // 越界钳住：占比不到 0 或超过 1 都按边界算（0 在门槛以内，1 远在外面）。
            Assert.Equal(0, BackdropBlur.CropRadius(-0.5, 0.25, 0, 32, 8));
            Assert.Equal(32, BackdropBlur.CropRadius(1.5, 0.25, 0, 32, 8));

            // 集页的常驻基础：门槛只拦裁切加成，不拦基础 —— 不裁切也站在 24 上，裁切往上加、量化不掉到
            // 基础以下。
            Assert.Equal(24, BackdropBlur.CropRadius(0, 0.25, BackdropBlur.EpisodeHeroRadius, 32, 8));
            Assert.Equal(32, BackdropBlur.CropRadius(0.3, 0.25, BackdropBlur.EpisodeHeroRadius, 32, 8));
        });

        Test("模糊：纯色图原样，尺寸不变", () =>
        {
            const int width = 24;
            const int height = 12;
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 30;
                pixels[i + 1] = 120;
                pixels[i + 2] = 210;
                pixels[i + 3] = 255;
            }

            var before = pixels.ToArray();
            BackdropBlur.BoxBlur(pixels, width, height, BackdropBlur.EpisodeHeroRadius);

            Assert.Equal(before.Length, pixels.Length);
            Assert.True(before.AsSpan().SequenceEqual(pixels), "纯色图过一遍模糊不该有任何变化");
        });

        Test("模糊：尖峰被摊开，中心降下来、四邻升上去", () =>
        {
            const int width = 31;
            const int height = 31;
            var pixels = new byte[width * height * 4];
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

            // 正中间一个白点，其余全黑。
            var center = (height / 2 * width + width / 2) * 4;
            pixels[center] = pixels[center + 1] = pixels[center + 2] = 255;

            BackdropBlur.BoxBlur(pixels, width, height, 6);

            var middle = pixels[center];
            var near = pixels[center + 4];
            Assert.True(middle < 255, $"中心该被摊薄，还是 {middle}");
            Assert.True(near > 0, "紧挨着的像素该分到光");

            // 能量守恒：全图的亮度总和大体不变（取整误差每像素最多半个）。
            var total = 0L;
            for (var i = 0; i < pixels.Length; i += 4) total += pixels[i];
            Assert.True(Math.Abs(total - 255d) <= width * height * 0.5, $"总亮度 {total} 离 255 太远");
        });

        Test("模糊：边沿不外延黑色，角上还是原来的色", () =>
        {
            const int width = 16;
            const int height = 16;
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 200;
                pixels[i + 1] = 200;
                pixels[i + 2] = 200;
                pixels[i + 3] = 255;
            }

            BackdropBlur.BoxBlur(pixels, width, height, 6);

            // 四角和四边中点都不该被「图外的黑」拉暗 —— 夹取边沿的意思是边上是 200 就一直是 200。
            foreach (var at in new[] { 0, (width - 1) * 4, (height - 1) * width * 4, pixels.Length - 4 })
                Assert.Equal((byte)200, pixels[at]);
        });

        Test("模糊：半径为零原样不动，尺寸对不上要喊", () =>
        {
            var pixels = new byte[8 * 2 * 4];
            var before = pixels.ToArray();
            BackdropBlur.BoxBlur(pixels, 8, 2, 0);
            Assert.True(before.AsSpan().SequenceEqual(pixels));

            Assert.Throws<ArgumentException>(() => BackdropBlur.BoxBlur(new byte[10], 8, 2, 1));
        });
    }
}
