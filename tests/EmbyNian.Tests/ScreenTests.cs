using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 窗口开在哪块屏幕上. Every case worth being sure of is one the machine this runs on is not — a single
/// monitor, a screen narrower than the window, a work area that does not start at the origin — so the
/// arithmetic is asked directly rather than by arranging monitors.
/// </summary>
internal static class ScreenTests
{
    public static void Register()
    {
        Test("选屏幕：第几块就是第几块", () =>
        {
            bool[] two = [true, false];

            Assert.Equal(0, ScreenPlacement.Choose(1, two), "--screen 1 是主屏");
            Assert.Equal(1, ScreenPlacement.Choose(2, two), "--screen 2 是副屏");
            Assert.Equal(-1, ScreenPlacement.Choose(0, two), "0 就是「系统爱摆哪摆哪」");
            Assert.Equal(-1, ScreenPlacement.Choose(3, two), "没有第三块，不许悄悄换一块");
            Assert.Equal(-1, ScreenPlacement.Choose(1, []), "问不出屏幕");
        });

        Test("选屏幕：自检要的是「别在主屏上」", () =>
        {
            Assert.Equal(
                1,
                ScreenPlacement.Choose(ScreenPlacement.NotThePrimary, [true, false]),
                "两块屏就用副屏");

            Assert.Equal(
                0,
                ScreenPlacement.Choose(ScreenPlacement.NotThePrimary, [false, true]),
                "副屏排在前面也认得出");

            Assert.Equal(
                -1,
                ScreenPlacement.Choose(ScreenPlacement.NotThePrimary, [true]),
                "只有一块屏时是「那就算了」，不是「第 1 块」");
        });

        Test("摆窗口：在选中那块屏的工作区里居中", () =>
        {
            // 副屏在主屏右边：工作区 1080×1872，原点 2560,0。
            var work = new WindowBounds(2560, 0, 2560 + 1080, 1872);
            var seat = ScreenPlacement.Centre(work, 800, 600);

            Assert.Equal(800, seat.Width);
            Assert.Equal(600, seat.Height);
            Assert.Equal(2560 + 140, seat.Left, "水平居中，坐标是整个桌面的");
            Assert.Equal(636, seat.Top);
        });

        Test("摆窗口：不许比屏幕还大", () =>
        {
            // 竖着的 1080×1920 副屏，默认窗口 1280 宽——伸出桌面的那截读不回像素，正是自检要读的东西。
            var work = new WindowBounds(2560, 0, 2560 + 1080, 1872);
            var seat = ScreenPlacement.Centre(work, 1280, 839);

            Assert.Equal(1080, seat.Width, "宽度顶到工作区");
            Assert.Equal(839, seat.Height, "高度本来就塞得下");
            Assert.Equal(2560, seat.Left, "顶满了就贴着左边");
            Assert.True(seat.Right <= 2560 + 1080, "整个窗口都在这块屏幕里");
        });

        Test("摆窗口：没屏幕可摆就什么都不做", () =>
        {
            Assert.Equal(0, ScreenPlacement.Centre(default, 1280, 800).Width, "没有工作区");
            Assert.Equal(0, ScreenPlacement.Centre(new WindowBounds(0, 0, 1920, 1040), 0, 0).Width, "没有窗口");
        });
    }
}
