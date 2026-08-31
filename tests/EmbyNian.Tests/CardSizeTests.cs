using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 一张卡片画多大 —— 两种形状怎么换算、图片多高（外壳里 <c>CardItem.CardWidth</c> 和
/// <c>CardItem.PosterHeight</c> 绑的就是它）。
/// <para>
/// 屏上永远只看得到默认那一档：170 的海报、300 的剧照。设置 → 海报宽度 那根滑杆能从 120 拖到 340，而截图、
/// 自检、逐页拍照一次都不会去动它 —— 也就是说「拖走之后两种形状还成不成比例」这条规则在真实的运行里一次也
/// 验不到。这里把整根滑杆走一遍。
/// </para>
/// </summary>
internal static class CardSizeTests
{
    /// <summary>设置 → 海报宽度 能拖到的两头（<c>SettingsViewModel</c> 里那一行 <c>120, 340</c>）。</summary>
    private const int Narrowest = 120;

    private const int Widest = 340;

    public static void Register()
    {
        Test("卡片尺寸：默认那一档就是标记里从前写死的那几个数", () =>
        {
            // 这几个数从前在每个 DataTemplate 里各写一遍（CardWidth="300" PosterHeight="169"）。搬进来之后
            // 它们必须一个不差，否则解码宽度和绘制宽度就分岔了 —— 屏上是模糊，或者白占内存。
            Assert.Equal(300, CardSize.WideFor(CardSize.PosterWidth), "默认宽度下剧照还是 300，动过设置才变");
            Assert.Equal(124, CardSize.CastFor(CardSize.PosterWidth), "演职人员那一格还是 124");

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

        Test("卡片尺寸：滑杆拖走之后两种形状还是同一个比例", () =>
        {
            // 从前 300 和 124 是两个写死的字面量：海报拖到 240，剧照还是 300 —— 一排 300 的 16:9 挨着一格
            // 240 的海报，看上去就是做错了。
            Assert.Equal(212, CardSize.WideFor(Narrowest), "滑杆拖到最窄");
            Assert.Equal(424, CardSize.WideFor(240), "240 × 300 ÷ 170 = 423.5；整数除法会算成 423");
            Assert.Equal(600, CardSize.WideFor(Widest), "拖到最宽正好整数");

            Assert.Equal(88, CardSize.CastFor(Narrowest));
            Assert.Equal(175, CardSize.CastFor(240), "240 × 124 ÷ 170 = 175.06");
            Assert.Equal(248, CardSize.CastFor(Widest));

            // 人像还是 2:3，宽度换算完照旧要给出整像素。175 × 1.5 = 262.5 正好卡在中间，Math.Round 往偶数收。
            Assert.Equal(262d, CardSize.HeightFor(CardSize.CastFor(240), wide: false), "正中间那一档收成 262");
        });

        Test("卡片尺寸：整根滑杆上都是整像素，而且越拖越大", () =>
        {
            double lastPoster = 0, lastWide = 0;
            int lastWideWidth = 0, lastCastWidth = 0;

            for (var width = Narrowest; width <= Widest; width++)
            {
                var poster = CardSize.HeightFor(width, wide: false);
                var still = CardSize.HeightFor(width, wide: true);

                // 半个像素的高度交给框架，屏上就是图片下沿糊掉一行，或者底下那两行字被挤掉一截。
                Assert.Equal(poster, Math.Floor(poster), $"{width} 宽的海报高得是整像素");
                Assert.Equal(still, Math.Floor(still), $"{width} 宽的剧照高得是整像素");

                Near(width * 3d / 2, poster, 0.5, $"{width} 宽的海报是 2:3");
                Near(width * 9d / 16, still, 0.5, $"{width} 宽的剧照是 16:9");
                Assert.True(still < poster, $"{width} 宽时剧照比海报矮");

                var wideWidth = CardSize.WideFor(width);
                var castWidth = CardSize.CastFor(width);

                Near(width * 300d / 170, wideWidth, 0.5, $"海报 {width} 时剧照的宽");
                Near(width * 124d / 170, castWidth, 0.5, $"海报 {width} 时人像的宽");
                Assert.True(wideWidth > width, $"海报 {width} 时剧照更宽");
                Assert.True(castWidth < width, $"海报 {width} 时人像更窄");

                Assert.True(poster >= lastPoster && still >= lastWide, $"拖到 {width} 时高度没往回缩");
                Assert.True(wideWidth >= lastWideWidth && castWidth >= lastCastWidth,
                    $"拖到 {width} 时另外两种宽度没往回缩");

                (lastPoster, lastWide, lastWideWidth, lastCastWidth) = (poster, still, wideWidth, castWidth);
            }
        });

        Test("卡片尺寸：两头拖到底也不会算出个负数或者零", () =>
        {
            // 滑杆到不了 0，但 LibraryViewModel 那边的宽度是从设置文件读回来的 —— 手改过的文件里可以是任何数，
            // 而一张 0 宽的卡片会让解码那一步拿到一个空位图。
            Assert.Equal(0, CardSize.WideFor(0), "0 就是 0，不是负数");
            Assert.Equal(0d, CardSize.HeightFor(0, wide: true));
            Assert.True(CardSize.WideFor(1) >= 1, "1 像素也得留下一个像素");
            Assert.Equal(2d, CardSize.HeightFor(1, wide: false), "1 × 1.5 收成 2");
        });

        static void Near(double expected, double actual, double slack, string message) =>
            Assert.True(Math.Abs(expected - actual) <= slack,
                $"期望 {expected:0.##} 上下 {slack:0.##}，实际 {actual:0.##}（{message}）");
    }
}
