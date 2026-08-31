using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 一排东西排不下就换行. 详情页上「媒体源／音频／字幕」那三个下拉用的就是它（外壳里的 <c>WrapRow</c>）。
/// <para>
/// 屏上那一遍读的是真实的那三个下拉，而它们有多宽是按这台服务器上最长那条轨道名撑出来的 —— 换句话说「这一次
/// 到底会不会换行」跟片源里的音轨叫什么名字有关，碰得到全靠运气。这里的宽高是给定的，所以四种坏法都问得到：
/// 正好差半个像素、一个比整行还宽的盒子、宽度无限的容器、一个都不显示的空排。
/// </para>
/// </summary>
internal static class WrapLayoutTests
{
    public static void Register()
    {
        Test("换行：宽窗口下就是一个横排", () =>
        {
            // 三个下拉，各 200 宽、32 高，间距 14 —— 一行 628，行宽 900 装得下。
            var plan = WrapLayout.Place(Boxes(200, 200, 200), 900, 14, 10);

            Assert.Equal(1, plan.Rows);
            Assert.Equal((0d, 0d), plan.Spots[0], "第一个贴着左沿，不留缩进");
            Assert.Equal((214d, 0d), plan.Spots[1]);
            Assert.Equal((428d, 0d), plan.Spots[2]);
            Assert.Equal(628d, plan.Width, "整排只占它需要的那么宽");
            Assert.Equal(32d, plan.Height, "一行就是一行的高");
        });

        Test("换行：装不下的那个下去，不是被切在右沿上", () =>
        {
            // 「字幕」那一格从前就是这么缺一块的：一行 628，窗口窄到 500。
            var plan = WrapLayout.Place(Boxes(200, 200, 200), 500, 14, 10);

            Assert.Equal(2, plan.Rows);
            Assert.Equal((0d, 0d), plan.Spots[0]);
            Assert.Equal((214d, 0d), plan.Spots[1], "两个还塞得下：414 ≤ 500");
            Assert.Equal((0d, 42d), plan.Spots[2], "第三个换行，回到左沿，往下让 32+10");
            Assert.Equal(414d, plan.Width, "最宽那一行是第一行");
            Assert.Equal(74d, plan.Height, "两行 32，中间一个 10 的行距");
        });

        Test("换行：一个盒子占一行", () =>
        {
            var plan = WrapLayout.Place(Boxes(200, 200, 200), 300, 14, 10);

            Assert.Equal(3, plan.Rows);
            Assert.Equal((0d, 0d), plan.Spots[0]);
            Assert.Equal((0d, 42d), plan.Spots[1]);
            Assert.Equal((0d, 84d), plan.Spots[2]);
            Assert.Equal(200d, plan.Width);
            Assert.Equal(116d, plan.Height, "三行 32 加两个 10");
        });

        Test("换行：差半个像素不算装不下", () =>
        {
            // 宽度是浮点数。两个 200 加 14 的间距正好 414，行宽读回来差那么一点点 —— 白换一行的话，屏上就是
            // 明明还有地方却摊成了两行。
            Assert.Equal(1, WrapLayout.Place(Boxes(200, 200), 414, 14, 10).Rows, "正好装满");
            Assert.Equal(1, WrapLayout.Place(Boxes(200, 200), 413.7, 14, 10).Rows, "差三成像素照旧算装得下");
            Assert.Equal(2, WrapLayout.Place(Boxes(200, 200), 413, 14, 10).Rows, "真的差一个像素就换行");
        });

        Test("换行：比一行还宽的那个留在本行", () =>
        {
            // 换行也没有更宽的一行在等着 —— 它只会被裁在自己那一格里（下拉的模板以省略号收尾），
            // 而不该把后面的顶出去。交回去的宽度也不许超过这一格，否则整排会被框架的对齐算法往右推。
            var plan = WrapLayout.Place(Boxes(700, 200), 500, 14, 10);

            Assert.Equal(2, plan.Rows);
            Assert.Equal((0d, 0d), plan.Spots[0], "第一个再宽也留在第一行");
            Assert.Equal((0d, 42d), plan.Spots[1]);
            Assert.Equal(500d, plan.Width, "占了 700，交回去的仍是给我的这 500");
        });

        Test("换行：宽度无限就永远不换行", () =>
        {
            // 放进一个横向能滚的容器里量出来的就是无限宽。
            foreach (var limit in new[] { double.PositiveInfinity, double.NaN })
            {
                var plan = WrapLayout.Place(Boxes(400, 400, 400), limit, 14, 10);

                Assert.Equal(1, plan.Rows, $"上限 {limit}");
                Assert.Equal(1228d, plan.Width, "这时候交回去的是真占了多宽");
                Assert.Equal(32d, plan.Height);
            }

            Assert.True(double.IsPositiveInfinity(WrapLayout.Bound(double.NaN)), "NaN 也是「没有上限」");
            Assert.Equal(900d, WrapLayout.Bound(900), "量得出来的宽度原样用");
        });

        Test("换行：一个都不显示时是 0 行", () =>
        {
            // 这一条目没有文件选项那一行时就是这样 —— 高度必须是 0，不能空出一行的位置来。
            var plan = WrapLayout.Place([], 900, 14, 10);

            Assert.Equal(0, plan.Rows);
            Assert.Equal(0d, plan.Width);
            Assert.Equal(0d, plan.Height);
            Assert.Equal(0, plan.Spots.Count);
        });

        Test("换行：行高按本行最高的那个", () =>
        {
            // 三个下拉一样高，但这个面板不该假定这件事：某一行里混着一个更高的东西时，下一行得从最高的那个下面
            // 开始，否则两行叠在一起。
            var plan = WrapLayout.Place([(200, 32), (200, 48), (200, 32)], 500, 14, 10);

            Assert.Equal((0d, 58d), plan.Spots[2], "第二行从 48 那个下面起，加 10 的行距");
            Assert.Equal(90d, plan.Height, "48 + 10 + 32");
        });

        static (double Width, double Height)[] Boxes(params double[] widths) =>
            [.. widths.Select(width => (width, 32d))];
    }
}
