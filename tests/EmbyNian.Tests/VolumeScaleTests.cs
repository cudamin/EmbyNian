using EmbyNian.Playback;

namespace EmbyNian.Tests;

/// <summary>
/// 音量条刻度（2026-09-22 用户令：「100 到 101 这一刻度区间的显示长度拉长、这一段要多滚几格才动数值，
/// 其他区间照旧」）的两半：<see cref="VolumeScale"/> 的换算，以及滚轮在轴上的那一格该怎么落。
/// <para>
/// 判据全是纯函数，所以 mpv 不在场也能钉死 —— 这正是它住在 Core 的理由。屏上那两处接线（滑杆绑轴坐标、
/// 滚轮走棘轮）由自检报告里的「滑杆上限」与「上方的数字对滑杆」两条看着，不在这里重复。
/// </para>
/// </summary>
internal static class VolumeScaleTests
{
    internal static void Register()
    {
        // 只有 100→101 那一段被拉长：其余每一格 1 个单位，这一格 4 个；100 以上整体抬高 3 个单位。
        TestHarness.Test("音量条刻度：只有 100→101 那一段被拉长，别的比例照旧", () =>
        {
            Assert.Equal(0d, VolumeScale.Axis(0));
            Assert.Equal(50d, VolumeScale.Axis(50));
            Assert.Equal(100d, VolumeScale.Axis(100));

            Assert.Equal(1d, VolumeScale.Width(99));
            Assert.Equal(4d, VolumeScale.Width(100), "100→101 在这一版上占 4 个轴单位");
            Assert.Equal(1d, VolumeScale.Width(101));

            Assert.Equal(104d, VolumeScale.Axis(101));
            Assert.Equal(105d, VolumeScale.Axis(102));
            Assert.Equal(133d, VolumeScale.Axis(130), "130 档被抬高 3 个单位");
            Assert.Equal(133d, VolumeScale.MaximumAxis);
        });

        // 滑杆的位置反算回音量：拖到哪一档就是哪一档；落在 100 与 101 之间的轴值给这两档之间的小数，
        // 由调用方就近收（100.5 正好是这一段的中点）。
        TestHarness.Test("音量条刻度：位置反算回音量", () =>
        {
            foreach (var level in new double[] { 0, 1, 37.5, 99, 100, 101, 102, 118, 130 })
                Assert.Equal(level, VolumeScale.Level(VolumeScale.Axis(level)), $"音量 {level} 来回一趟");

            Assert.Equal(100d, VolumeScale.Level(100));
            Assert.Equal(100.5, VolumeScale.Level(102), "段中：落在两档之间的小数");
            Assert.Equal(101d, VolumeScale.Level(104));
        });

        // 滚轮一格是 2 个轴单位，而这一段宽 4 —— 所以 100→101 要两格，第一格只把"路"留在轴上。
        TestHarness.Test("滚轮：100→101 要两格，第一格不动数值", () =>
        {
            var (level, axis) = VolumeScale.Step(100, VolumeScale.Axis(100), 2);
            Assert.Equal(100d, level, "第一格不该触发数值变化");
            Assert.Equal(102d, axis, "走掉的那半格要留在轴上，等下一次接着走");

            (level, axis) = VolumeScale.Step(level, axis, 2);
            Assert.Equal(101d, level, "第二格才跨到 101");
            Assert.Equal(104d, axis);
        });

        // 回程要一样：从 101 往回也是一格不动、两格到 100。只保一个方向就等于把这条刻度做成单行道。
        TestHarness.Test("滚轮：101→100 与去程对称", () =>
        {
            var (level, axis) = VolumeScale.Step(101, VolumeScale.Axis(101), -2);
            Assert.Equal(101d, level);
            Assert.Equal(102d, axis);

            (level, axis) = VolumeScale.Step(level, axis, -2);
            Assert.Equal(100d, level);
            Assert.Equal(100d, axis);
        });

        // 「其他音量区间的正常滚动灵敏度不变」：一格两档，进出这一段之后照旧。
        TestHarness.Test("滚轮：其他区间仍是一格两档", () =>
        {
            var (level, axis) = VolumeScale.Step(50, VolumeScale.Axis(50), 2);
            Assert.Equal(52d, level);
            Assert.Equal(52d, axis);

            (level, _) = VolumeScale.Step(101, VolumeScale.Axis(101), 2);
            Assert.Equal(103d, level, "刚越过 100 之后的下一格照旧是两档");

            var (down, downAxis) = VolumeScale.Step(3, VolumeScale.Axis(3), -2);
            Assert.Equal(1d, down);
            Assert.Equal(1d, downAxis);
        });

        // 两端不越界：天花板仍是 130（轴 133），地板是 0。
        TestHarness.Test("滚轮：两端不越界", () =>
        {
            var (level, axis) = VolumeScale.Step(129, VolumeScale.Axis(129), 2);
            Assert.Equal(130d, level);
            Assert.Equal(VolumeScale.MaximumAxis, axis);

            (level, axis) = VolumeScale.Step(level, axis, 20);
            Assert.Equal(130d, level);
            Assert.Equal(VolumeScale.MaximumAxis, axis);

            (level, axis) = VolumeScale.Step(1, VolumeScale.Axis(1), -20);
            Assert.Equal(0d, level);
            Assert.Equal(0d, axis);
        });

        // 步长本来就不小于这一格宽的那一档（方向键一步 5 个轴单位）不该被拖慢：一步跨过去，余下的 1 个单位
        // 落到 102（这一段之后每格 1 个单位）。
        TestHarness.Test("滚轮刻度不该拖慢本来就更大的步长", () =>
        {
            var (level, _) = VolumeScale.Step(100, VolumeScale.Axis(100), 5);
            Assert.Equal(102d, level, "一步 5 个轴单位：跨过 100→101 之后余下的 1 个单位落在 102");

            var (back, _) = VolumeScale.Step(102, VolumeScale.Axis(102), -5);
            Assert.Equal(100d, back, "回程也一样");
        });
    }
}
