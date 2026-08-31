using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 一条横向卡片带的四条规则. 主页每个分区和详情页的 单集／全部剧季／演职人员／更多类似 都是那条带
/// （<c>ShelfStrip</c>），翻多远、落在哪、哪个箭头该在屏上、要让第几张卡露出来得滚到哪儿都在这里。
/// <para>
/// 这些数字从前由 <c>--self-check</c> 走一遍：得先构建、发布、开一个窗口，才知道翻页的算术还对不对。而值得
/// 问一遍的情形多半不是这台机器上跑一遍就见得到的 —— 一张比整个视口还宽的卡（窄窗口下的 16:9 剧照）、卡片
/// 还没量出来的那一拍、正好停在带尾的那一下、可滚动宽度是 0 的那条空带。现在它们钉在这儿，每次构建都跑。
/// </para>
/// <para>
/// 那几个数照旧写死（视口 900、间距 186、间隔 16、步长 744、可滚 2000），因为它们是真的：海报卡 170 宽加
/// 16 的间隔就是 186，(900+16)/186 取整是 4 张。拿 <c>CardStrip</c> 自己去算就成了同一句话说两遍。
/// </para>
/// </summary>
internal static class CardStripTests
{
    public static void Register()
    {
        Test("卡片带：一页翻几张卡", () =>
        {
            // 视口 900、间距 186：(900+16)/186 = 4.92 → 4 张 → 744。按整数倍翻，所以翻完之后卡片仍然左边沿
            // 对齐；按视口宽度直接翻会把某张卡切成两半留在边上，再翻一次把这个偏差累积下去。
            Near(744, CardStrip.StepFor(900, 186, 16), "4 张卡，一次翻整整 4 张");
            Assert.True(CardStrip.StepFor(900, 186, 16) <= 900, "不许翻过一屏，否则中间那些卡跳过去了");

            // 一张比整个视口还宽的卡：窗口很窄时的 16:9 剧照。至少翻一张，否则步长是 0、箭头点不动。
            Near(316, CardStrip.StepFor(200, 316, 16), "卡比视口宽，也得翻得动一张");

            // 卡片还没量出来的那一拍：退回视口的九成，留一成重叠好让人知道是同一条带。
            Near(810, CardStrip.StepFor(900, 0, 16), "量不到卡片就按视口的九成");

            // 视口是 0 才是「什么都还没有」：这时翻页不该动，而不是滚到某个猜出来的地方。
            Assert.Equal(0d, CardStrip.StepFor(0, 186, 16), "还没量过视口");
            Assert.Equal(0d, CardStrip.StepFor(-1, 186, 16), "视口读回负数也当没量过");
        });

        Test("卡片带：翻过去落在哪，两头夹住", () =>
        {
            Near(744, CardStrip.TargetFor(0, 1, 744, 2000), "从头往后翻一页");
            Near(2000, CardStrip.TargetFor(1800, 1, 744, 2000), "最后一页停在末尾，不越过去");
            Assert.Equal(0d, CardStrip.TargetFor(300, -1, 744, 2000), "往前翻过头就贴回开头");
            Near(1256, CardStrip.TargetFor(2000, -1, 744, 2000), "从带尾往前翻一整页");

            // 一屏放得下的带：没有「后面」，也就没有落点。
            Assert.Equal(0d, CardStrip.TargetFor(0, 1, 744, 0), "翻不动的带停在 0");
        });

        Test("卡片带：哪个箭头该在屏上", () =>
        {
            Assert.Equal((false, true), CardStrip.ArrowsFor(0, 2000, true), "在开头：只有往后");
            Assert.Equal((true, true), CardStrip.ArrowsFor(500, 2000, true), "在中间：两个都有");
            Assert.Equal((true, false), CardStrip.ArrowsFor(2000, 2000, true), "在末尾：只有往前");

            // 内容一屏放得下就两个都不要 —— 一个点了没反应的按钮比没有按钮更难解释。
            Assert.Equal((false, false), CardStrip.ArrowsFor(0, 0, true), "放得下就不要箭头");

            // 箭头是悬停才出现的，同卡片上那排按钮。
            Assert.Equal((false, false), CardStrip.ArrowsFor(500, 2000, false), "指针不在带上");

            // 一像素的余量：滚动位置是浮点数，「到头了」不该指望它正好等于可滚动的宽度。
            Assert.Equal((false, true), CardStrip.ArrowsFor(0.4, 2000, true), "还没真的滚起来");
            Assert.Equal((true, false), CardStrip.ArrowsFor(1999.6, 2000, true), "差半个像素就算到头");
        });

        Test("卡片带：要让第几张卡露出来", () =>
        {
            // OffsetFor 是「开在正在看的那一集上」那条：单集页的「更多来自」不该开在第一集上让人自己往后翻。
            Assert.Equal(0d, CardStrip.OffsetFor(0, 186, 900, 2000), "第一张本来就在开头");
            Assert.Equal(0d, CardStrip.OffsetFor(2, 186, 900, 2000), "落在第一屏里就不动");
            Near(930, CardStrip.OffsetFor(5, 186, 900, 2000), "再往后的对齐到左边沿");
            Near(2000, CardStrip.OffsetFor(40, 186, 900, 2000), "末尾照旧夹住，不留一片空白");
            Assert.Equal(0d, CardStrip.OffsetFor(5, 186, 900, 0), "翻不动的带上不动");
            Assert.Equal(0d, CardStrip.OffsetFor(5, 0, 900, 2000), "还没量到卡片，请求留着等布局");
        });

        Test("卡片带：整张露着就别动", () =>
        {
            // RevealFor 是焦点落到卡片上时走的那条。-1 是「别动」：鼠标按下去的那一刻卡片就拿到了焦点，而人按
            // 的那张卡当然看得见 —— 那一档要是还挪一下，就还是「点封面先滑走一大段」那个毛病，只是方向变了。
            Assert.Equal(-1d, CardStrip.RevealFor(2, 186, 16, 0, 900, 2000), "第 3 张整张露着");
            Near(930, CardStrip.RevealFor(5, 186, 16, 0, 900, 2000), "在右边外面：左边沿对到视口左边沿");
            Near(2000, CardStrip.RevealFor(40, 186, 16, 0, 900, 2000), "远处的夹在带尾");
            Assert.Equal(0d, CardStrip.RevealFor(0, 186, 16, 930, 900, 2000), "已经滚开了，第一张要退回去");
            Assert.Equal(-1d, CardStrip.RevealFor(5, 186, 16, 0, 900, 0), "翻不动的带上什么都不做");
            Assert.Equal(-1d, CardStrip.RevealFor(5, 0, 16, 0, 900, 2000), "还没量到卡片");
            Assert.Equal(-1d, CardStrip.RevealFor(-1, 186, 16, 0, 900, 2000), "问的不是带里的卡");

            // 卡片自己有多宽是间距减掉间隔（186-16=170）：右边沿按卡片算，不按下一张的起点算，否则贴着视口右
            // 边沿的那张卡会被判成「露不全」，白挪一次。
            Assert.Equal(-1d, CardStrip.RevealFor(3, 186, 16, 0, 728, 2000), "第 4 张 558…728，正好贴着右边沿");
            Near(558, CardStrip.RevealFor(3, 186, 16, 0, 726, 2000), "视口再窄一点（那一像素的余量之外）就得挪");
        });

        static void Near(double expected, double actual, string message) =>
            Assert.True(Math.Abs(expected - actual) < 0.01, $"期望 {expected:0.##}，实际 {actual:0.##}（{message}）");
    }
}
