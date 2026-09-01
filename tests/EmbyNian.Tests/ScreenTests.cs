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

        // ---------------------------------------------------------- 记住上次的窗口

        Test("沿用上次的窗口：装得下就一个像素不动", () =>
        {
            var main = new WindowBounds(0, 0, 2560, 1392);
            var saved = new WindowBounds(300, 200, 300 + 1471, 200 + 839);

            var seat = ScreenPlacement.Restore(saved, [main], 900, 560);

            Assert.Equal(300, seat.Left, "左边沿照原样");
            Assert.Equal(200, seat.Top, "上边沿照原样");
            Assert.Equal(1471, seat.Width);
            Assert.Equal(839, seat.Height);
        });

        Test("沿用上次的窗口：没记过就交给外壳自己算", () =>
        {
            var main = new WindowBounds(0, 0, 2560, 1392);

            Assert.Equal(0, ScreenPlacement.Restore(default, [main], 900, 560).Width, "第一次运行");
            Assert.Equal(
                0,
                ScreenPlacement.Restore(new WindowBounds(100, 100, 100, 100), [main], 900, 560).Width,
                "宽高是 0 —— 上一次是最大化着关掉的，还原尺寸从来没量到过");
            Assert.Equal(
                0,
                ScreenPlacement.Restore(new WindowBounds(0, 0, 1471, 839), [], 900, 560).Width,
                "问不出屏幕");
        });

        Test("沿用上次的窗口：认得出它上次在哪块屏", () =>
        {
            // 主屏 2560×1392 在原点，副屏是它右边那块竖着的 1080×1872。
            var main = new WindowBounds(0, 0, 2560, 1392);
            var side = new WindowBounds(2560, 0, 2560 + 1080, 1872);

            // 整个在副屏里。
            var saved = new WindowBounds(2600, 100, 2600 + 1000, 100 + 700);
            var seat = ScreenPlacement.Restore(saved, [main, side], 900, 560);

            Assert.Equal(2600, seat.Left, "留在副屏上，没被拽回主屏");
            Assert.Equal(1000, seat.Width);
        });

        Test("沿用上次的窗口：跨着两块屏时算哪边压得多", () =>
        {
            var main = new WindowBounds(0, 0, 2560, 1392);
            var side = new WindowBounds(2560, 0, 2560 + 1080, 1872);

            // 1000 宽的窗口从 2360 起，压在副屏上 800、压在主屏上 200 —— 副屏压得多。
            var saved = new WindowBounds(2360, 100, 2360 + 1000, 100 + 700);
            var seat = ScreenPlacement.Restore(saved, [main, side], 900, 560);

            Assert.Equal(2560, seat.Left, "整个挪进压得多那块屏，不许留一半在外面");
            Assert.Equal(1000, seat.Width, "尺寸没动 —— 它在那块屏里装得下");
            Assert.True(seat.Right <= side.Right, "右边沿在副屏里");
        });

        Test("沿用上次的窗口：屏幕拔掉了就留尺寸、丢位置", () =>
        {
            // 上次在副屏上，这次只剩主屏。照原样摆回去就是一个整个在桌面外面的窗口 —— 标题栏都看不见，
            // 鼠标既拖不动也关不掉。
            var main = new WindowBounds(0, 0, 1920, 1040);
            var saved = new WindowBounds(2600, 100, 2600 + 1200, 100 + 800);

            var seat = ScreenPlacement.Restore(saved, [main], 900, 560);

            Assert.Equal(1200, seat.Width, "他拉出来的尺寸留着 —— 显示器没了不是「忘掉多大」的理由");
            Assert.Equal(800, seat.Height);
            Assert.Equal(360, seat.Left, "位置只能丢，改成在主屏上居中");
            Assert.Equal(120, seat.Top);
        });

        Test("沿用上次的窗口：屏幕变小了就跟着收，并且整个在里面", () =>
        {
            // 上次 2560 宽的屏，这次是 1366×768 的笔记本屏。
            var laptop = new WindowBounds(0, 0, 1366, 728);
            var saved = new WindowBounds(200, 150, 200 + 2000, 150 + 900);

            var seat = ScreenPlacement.Restore(saved, [laptop], 900, 560);

            Assert.Equal(1366, seat.Width, "宽顶到工作区");
            Assert.Equal(728, seat.Height, "高顶到工作区");
            Assert.Equal(0, seat.Left, "顶满了就贴左上角");
            Assert.Equal(0, seat.Top);
        });

        Test("沿用上次的窗口：伸出右下角就往里推，不是压小", () =>
        {
            var main = new WindowBounds(0, 0, 1920, 1040);

            // 1200×800 的窗口摆在 900,500 —— 右边和下边都出了工作区，可它本身装得下。
            var saved = new WindowBounds(900, 500, 900 + 1200, 500 + 800);
            var seat = ScreenPlacement.Restore(saved, [main], 900, 560);

            Assert.Equal(1200, seat.Width, "尺寸不动");
            Assert.Equal(800, seat.Height);
            Assert.Equal(720, seat.Left, "贴到右边沿");
            Assert.Equal(240, seat.Top, "贴到下边沿");
            Assert.True(seat.Right <= 1920 && seat.Bottom <= 1040, "整个在工作区里");
        });

        Test("沿用上次的窗口：下限压得住，但压不过屏幕自己", () =>
        {
            var main = new WindowBounds(0, 0, 1920, 1040);

            // 手改设置文件写了个 300 宽。
            var tiny = ScreenPlacement.Restore(new WindowBounds(0, 0, 300, 200), [main], 900, 560);
            Assert.Equal(900, tiny.Width, "抬到窗口自己的下限");
            Assert.Equal(560, tiny.Height);

            // 而下限比屏幕还宽的时候，屏幕说了算 —— 否则窗口的边会被推到桌面外面去。
            var pocket = new WindowBounds(0, 0, 800, 480);
            var squeezed = ScreenPlacement.Restore(new WindowBounds(0, 0, 300, 200), [pocket], 900, 560);
            Assert.Equal(800, squeezed.Width, "下限让给屏幕");
            Assert.Equal(480, squeezed.Height);
            Assert.True(squeezed.Right <= 800 && squeezed.Bottom <= 480, "还是整个在里面");
        });

        Test("沿用上次的窗口：只贴着一角也算在那块屏上", () =>
        {
            var main = new WindowBounds(0, 0, 1920, 1040);
            var side = new WindowBounds(1920, 0, 1920 + 1920, 1040);

            // 只有 20×20 落在主屏里，其余都在副屏 —— 压得多的是副屏。
            var saved = new WindowBounds(1900, 1020, 1900 + 1000, 1020 + 700);
            var seat = ScreenPlacement.Restore(saved, [main, side], 900, 560);

            Assert.Equal(1920, seat.Left, "算的是压住的面积，不是「碰到了就算」");
            Assert.Equal(340, seat.Top, "贴到副屏的下边沿");
        });
    }
}
