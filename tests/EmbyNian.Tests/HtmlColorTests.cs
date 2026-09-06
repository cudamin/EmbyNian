using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// HTML 颜色代码的读写和 RGB↔HSV 的换算 —— 设置页那个拾色器（HtmlColorPicker）底下全部的算术。
/// <para>
/// 判据就是用户给的参考页（rapidtables 的 HTML 颜色选择器）：#AC5D5D 在那一页上读作 R 172、G 93、
/// B 93、H 0、S 46%、V 67%，这里就钉它这一组 —— 换算跟参考页对不上，屏上两个工具就是两个答案。
/// </para>
/// </summary>
internal static class HtmlColorTests
{
    public static void Register()
    {
        RegisterParseAndFormat();
        RegisterHsv();
    }

    private static void RegisterParseAndFormat()
    {
        Test("HTML颜色：#RRGGBB 读得进来，大小写和带不带井号都行", () =>
        {
            Assert.True(HtmlColor.TryParse("#AC5D5D", out var rgb));
            Assert.Equal(0xAC5D5D, rgb);

            Assert.True(HtmlColor.TryParse("#ac5d5d", out rgb));
            Assert.Equal(0xAC5D5D, rgb);

            Assert.True(HtmlColor.TryParse(" AC5D5D ", out rgb), "两头空白不算内容");
            Assert.Equal(0xAC5D5D, rgb);
        });

        Test("HTML颜色：不是六位十六进制的一律不认", () =>
        {
            Assert.False(HtmlColor.TryParse("", out _));
            Assert.False(HtmlColor.TryParse(null, out _));
            Assert.False(HtmlColor.TryParse("#FFF", out _), "三位缩写不是这一行的写法");
            Assert.False(HtmlColor.TryParse("#AC5D5D5D", out _), "八位带透明度的也不收，透明度是另一行");
            Assert.False(HtmlColor.TryParse("#AC5D", out _));
            Assert.False(HtmlColor.TryParse("#AC5D5G", out _), "G 不是十六进制");
            Assert.False(HtmlColor.TryParse("none", out _), "旧设置文件里那个「无背景」不是颜色");
            Assert.False(HtmlColor.TryParse("#FF FF FF", out _));
        });

        Test("HTML颜色：写出去一律是大写带井号的那一种", () =>
        {
            Assert.Equal("#AC5D5D", HtmlColor.Format(0xAC5D5D));
            Assert.Equal("#000000", HtmlColor.Format(0));
            Assert.Equal("#FFFFFF", HtmlColor.Format(0xFFFFFF));
            Assert.Equal("#0A0B0C", HtmlColor.Format(0x0A0B0C), "不足十六的前面补零");
        });

        Test("HTML颜色：打包和拆包三个通道对得上", () =>
        {
            var (r, g, b) = HtmlColor.Rgb(0xAC5D5D);
            Assert.Equal(172, r);
            Assert.Equal(93, g);
            Assert.Equal(93, b);

            Assert.Equal(0xAC5D5D, HtmlColor.Pack(172, 93, 93));
            Assert.Equal(0x123456, HtmlColor.Pack(HtmlColor.Rgb(0x123456)));
        });
    }

    private static void RegisterHsv()
    {
        Test("HTML颜色：#AC5D5D 的 HSV 与参考页一致（H0 S46 V67）", () =>
        {
            var (h, s, v) = HtmlColor.ToHsv(172, 93, 93);
            Assert.Equal(0, h, "纯红系色相是 0 度，参考页也是这么写的");
            Assert.Equal(46, s);
            Assert.Equal(67, v);
        });

        Test("HTML颜色：三原色、黑白灰的 HSV 都是整档", () =>
        {
            Assert.Equal((0, 100, 100), HtmlColor.ToHsv(255, 0, 0), "红");
            Assert.Equal((120, 100, 100), HtmlColor.ToHsv(0, 255, 0), "绿");
            Assert.Equal((240, 100, 100), HtmlColor.ToHsv(0, 0, 255), "蓝");
            Assert.Equal((0, 0, 100), HtmlColor.ToHsv(255, 255, 255), "白");
            Assert.Equal((0, 0, 0), HtmlColor.ToHsv(0, 0, 0), "黑");
            Assert.Equal((0, 0, 47), HtmlColor.ToHsv(120, 120, 120), "灰没有色相和饱和度");
        });

        Test("HTML颜色：HSV 回 RGB 与来路一致（常用色逐个走一遍）", () =>
        {
            // H/S/V 三根轴在界面上都是整数，量化本身就要丢一点精度 —— 允许每个通道差 1，换来的是拖动
            // 那块方的时候任何落点都能停在离它最近的颜色上，而不是一路漂。
            foreach (var rgb in new[] { 0xAC5D5D, 0xFFFFFF, 0x000000, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFF200, 0xB4E1FF })
            {
                var (r, g, b) = HtmlColor.Rgb(rgb);
                var (h, s, v) = HtmlColor.ToHsv(r, g, b);
                var (r2, g2, b2) = HtmlColor.FromHsv(h, s, v);

                Assert.True(Math.Abs(r2 - r) <= 1 && Math.Abs(g2 - g) <= 1 && Math.Abs(b2 - b) <= 1,
                    $"#{rgb:X6} 走一个来回差得太多：得 ({r2},{g2},{b2})");
            }
        });

        Test("HTML颜色：HSV 越界的轴被夹回来，360 和 0 是同一个色相", () =>
        {
            Assert.Equal(0xFF0000, HtmlColor.Pack(HtmlColor.FromHsv(0, 200, 200)), "色相 0 配满档的饱和度和明度是纯红");
            Assert.Equal(0x000000, HtmlColor.Pack(HtmlColor.FromHsv(0, -5, -5)), "夹到 0");
            Assert.Equal(HtmlColor.Pack(HtmlColor.FromHsv(0, 100, 100)), HtmlColor.Pack(HtmlColor.FromHsv(360, 100, 100)));
            Assert.Equal(HtmlColor.Pack(HtmlColor.FromHsv(0, 100, 100)), HtmlColor.Pack(HtmlColor.FromHsv(-360, 100, 100)));
        });
    }
}
