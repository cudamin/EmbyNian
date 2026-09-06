using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 一张卡片画多大 —— 两种形状的高怎么算（外壳里 <c>CardItem.CardWidth</c> 和 <c>CardItem.PosterHeight</c>
/// 绑的就是它）。
/// <para>
/// 全应用只有 <c>CardSize</c> 固定的那一档：170 的海报、300 的剧照、124 的演职人员人像。从前 设置 → 海报宽度
/// 那根滑杆能把这些数整体拖走，那一行 2026-09-05 按用户的话删掉了 —— 所以这里钉的是「那几个数没有被谁动过」，
/// 加上高度算式的形状（2:3、16:9、整像素）。
/// </para>
/// </summary>
internal static class CardSizeTests
{
    public static void Register()
    {
        Test("卡片尺寸：默认那一档就是标记里从前写死的那几个数", () =>
        {
            // 这几个数从前在每个 DataTemplate 里各写一遍（CardWidth="300" PosterHeight="169"）。搬进来之后
            // 它们必须一个不差，否则解码宽度和绘制宽度就分岔了 —— 屏上是模糊，或者白占内存。
            Assert.Equal(170, CardSize.PosterWidth);
            Assert.Equal(300, CardSize.WideWidth);
            Assert.Equal(124, CardSize.CastWidth, "演职人员那一格");

            Assert.Equal(255d, CardSize.HeightFor(CardSize.PosterWidth, wide: false), "170 的海报高 255");
            Assert.Equal(169d, CardSize.HeightFor(CardSize.WideWidth, wide: true), "300 的剧照高 169");
            Assert.Equal(186d, CardSize.HeightFor(CardSize.CastWidth, wide: false), "124 的人像高 186");

            Assert.Equal((double)CardSize.PosterHeight, CardSize.HeightFor(CardSize.PosterWidth, wide: false),
                "常量和算出来的必须是同一个数");
            Assert.Equal((double)CardSize.WideHeight, CardSize.HeightFor(CardSize.WideWidth, wide: true));

            // 横排要多高：图片，加上图片底下那两行字（CardShelf.RowHeight 和 LibraryViewModel.RowHeight
            // 就是这么加的）。少加这一截，屏上是卡片标题被下一排压掉半行。
            Assert.Equal(314d, CardSize.HeightFor(CardSize.PosterWidth, wide: false) + CardSize.Chrome,
                "一排海报：255 + 59");
            Assert.Equal(228d, CardSize.HeightFor(CardSize.WideWidth, wide: true) + CardSize.Chrome,
                "一排剧照：169 + 59");
        });

        Test("卡片尺寸：三种宽度上高度都是整像素，形状也对", () =>
        {
            // 半个像素的高度交给框架，屏上就是图片下沿糊掉一行，或者底下那两行字被挤掉一截。
            foreach (var width in new[] { CardSize.PosterWidth, CardSize.WideWidth, CardSize.CastWidth })
            {
                var poster = CardSize.HeightFor(width, wide: false);
                var still = CardSize.HeightFor(width, wide: true);

                Assert.Equal(poster, Math.Floor(poster), $"{width} 宽的海报高得是整像素");
                Assert.Equal(still, Math.Floor(still), $"{width} 宽的剧照高得是整像素");

                Near(width * 3d / 2, poster, 0.5, $"{width} 宽的海报是 2:3");
                Near(width * 9d / 16, still, 0.5, $"{width} 宽的剧照是 16:9");
                Assert.True(still < poster, $"{width} 宽时剧照比海报矮");
            }
        });

        static void Near(double expected, double actual, double slack, string message) =>
            Assert.True(Math.Abs(expected - actual) <= slack,
                $"期望 {expected:0.##} 上下 {slack:0.##}，实际 {actual:0.##}（{message}）");
    }
}
