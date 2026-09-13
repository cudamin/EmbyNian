using EmbyNian.Emby;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 主页首屏的矮窗档 —— 窗口拉矮到「继续观看」牌子底下那条线被第一屏裁掉之后，媒体库那一排压上轮播左下角、
/// 下一排补位；轮播被一屏压矮、上下裁切超出默认形状时才恢复默认（<see cref="HomeFold.LibraryOnBanner"/>，
/// 2026-09-13「轮播图上下裁切过多时隐藏，默认大小时不隐藏」—— 媒体库整个出屏但轮播还完好的那一档压着不放）。
/// 触发线同日再改过一次：「窗口裁切超过继续观看下方的那条线的时候就触发媒体库上移」—— 线 ＝ 下一排的顶 ＋
/// 牌子的高，牌子多高页面量了递进来；这里的数字只钉边界形状，不钉牌子的高。
/// <para>
/// 钉的是两条边界的形状，数字取 1422 宽的默认窗那一档：带子的设计形状 608（<see cref="HomeCarousel.NaturalHeight"/>），
/// 「继续观看」牌子底下那条线在 922（媒体库那格顶 632 ＋ 排高 254 ＋ 排间空当 ＋ 牌子高，页面按这个账量）。
/// </para>
/// </summary>
internal static class HomeFoldTests
{
    public static void Register()
    {
        Test("矮窗档：继续观看牌子下那条线被裁掉 —— 压上", () =>
        {
            // 视口 900：线在 922、整个在第一屏外 —— 线以下（那一排的卡）一个像素都不露，这正是要压上轮播的那一档。
            Assert.True(HomeFold.LibraryOnBanner(viewport: 900, nextRowLine: 922, bandHeight: 608));
            Assert.True(HomeFold.LibraryOnBanner(700, 922, 608), "更矮一点还是同一档");
        });

        Test("矮窗档：高窗装得下那条线 —— 默认", () =>
        {
            // 线 922 < 视口：线以下还露得出来（哪怕只是那一排的卡露了头），媒体库留在横排里。
            Assert.False(HomeFold.LibraryOnBanner(1000, 922, 608));
            Assert.False(HomeFold.LibraryOnBanner(923, 922, 608), "线哪怕只露一像素，也算露");
        });

        Test("矮窗档：媒体库整个出了屏、轮播还是设计形状 —— 压着不放", () =>
        {
            // 视口 620：媒体库的顶 632 在第一屏外（按「只能看到轮播图」的旧规则这一档已经回默认），但视口还
            // 撑得住带子的设计形状 608 —— 剧照上下裁切和默认大小一个样，压上档继续，媒体库跟着那一排一起
            // 住进轮播左下角（2026-09-13「轮播图上下裁切过多时隐藏，默认大小时不隐藏」）。
            Assert.True(HomeFold.LibraryOnBanner(620, 922, 608));
            Assert.True(HomeFold.LibraryOnBanner(632, 922, 608), "媒体库的顶正好压线，一个像素都没露");
        });

        Test("矮窗档：轮播被一屏压矮、上下裁切超出默认档 —— 恢复默认", () =>
        {
            // 视口 607 差一像素够不到带子的设计形状 608：带子被压矮，剧照上下各多裁出一截 —— 该回默认了。
            Assert.False(HomeFold.LibraryOnBanner(607, 922, 608));
            Assert.False(HomeFold.LibraryOnBanner(600, 922, 608), "更矮只会裁得更多");
        });

        Test("矮窗档：两条边界的等号", () =>
        {
            Assert.True(HomeFold.LibraryOnBanner(922, 922, 608), "那条线 ＝ 视口底：线以下一个像素都没露，算裁掉");
            Assert.True(HomeFold.LibraryOnBanner(608, 922, 608), "视口 ＝ 设计形状：正好撑住，还没多裁");
        });

        Test("矮窗档：量不到视口不做决定", () =>
        {
            Assert.False(HomeFold.LibraryOnBanner(0, 922, 608));
            Assert.False(HomeFold.LibraryOnBanner(-1, 922, 608));
        });
    }
}
