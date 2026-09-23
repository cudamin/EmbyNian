using EmbyNian.Emby;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 主页首屏的矮窗档 —— 窗口拉矮到那条线被第一屏裁掉之后，媒体库那一排压上轮播左下角、
/// 下一排补位；轮播被一屏压矮、上下裁切超出默认形状时才恢复默认（<see cref="HomeFold.LibraryOnBanner"/>，
/// 2026-09-13「轮播图上下裁切过多时隐藏，默认大小时不隐藏」—— 媒体库整个出屏但轮播还完好的那一档压着不放）。
/// 触发线 2026-09-13 改成「裁到那条线就上移」；2026-09-22 那条线又往下挪了一排 —— 现在是**媒体库下面那一排的
/// 再下面那一排的封面顶**（用户的话：「改为快要显示下方媒体库封面的时候上移」，配一张红线压在「最近添加 · 电视
/// 节目」封面顶上的截图）。线由 <see cref="HomeFold.CoversLine"/> 算；这里的数字只钉边界形状，不钉牌子的高。
/// <para>
/// 钉的是两条边界的形状，数字取 1422 宽的默认窗那一档：带子的设计形状 608（<see cref="HomeCarousel.NaturalHeight"/>），
/// 那条线在 922。形状与从前一字未动 —— 挪的是线上站着的那一排（见下面那组算术的测试）。
/// </para>
/// </summary>
internal static class HomeFoldTests
{
    public static void Register()
    {
        Test("矮窗档：那条线被裁掉 —— 压上", () =>
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

        Test("矮窗档：那条线落在下面那一排的再下面那一排的封面顶上", () =>
        {
            // 他用的一档：1422／1527 宽的窗口，带子 653；媒体库（排卡 176）在顶上一格，从 669 起，
            // 它下面那一排（继续观看，排卡 203、牌子 46、牌子底下让 12）在默认摆法里从 921 起。
            // 那条线 ＝ 再下面那一排的封面顶 ＝ 921 ＋（牌子 46 ＋ 空当 12 ＋ 排卡 203）＋ 排间空当 18 ＋ 牌子 46 ＋ 空当 12。
            var rowTop = 921.0;
            var line = HomeFold.CoversLine(921, 46, 12, 203, 18, hasBelow: true);

            Assert.Equal(921 + (46 + 12 + 203) + 18 + (46 + 12), line);
            Assert.Equal(1258, line);

            // 没有第二排（媒体库下面只有一排）：退回那一排自己的封面顶 —— 压上腾出的位置只够托它进第一屏。
            Assert.Equal(rowTop + 46 + 12, HomeFold.CoversLine(rowTop, 46, 12, 203, 18, hasBelow: false));

            // 线比「下面那一排自己的封面顶」整整低一排：这正是 2026-09-22 挪的那一格。
            Assert.Equal(1258 - (46 + 12 + 203) - 18, HomeFold.CoversLine(rowTop, 46, 12, 203, 18, hasBelow: false));
        });

        Test("矮窗档：量不到视口不做决定", () =>
        {
            Assert.False(HomeFold.LibraryOnBanner(0, 922, 608));
            Assert.False(HomeFold.LibraryOnBanner(-1, 922, 608));
        });
    }
}
