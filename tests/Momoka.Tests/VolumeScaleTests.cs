using Momoka.Configuration;
using Momoka.Playback;

namespace Momoka.Tests;

internal static class VolumeScaleTests
{
    internal static void Register()
    {
        TestHarness.Test("音量条：填充比例与 mpv 线性音量一致", () =>
        {
            foreach (var level in new double[] { 0, 1, 50, 99, 100, 101, 129, 130 })
                Assert.Equal(level / AudioSettings.MaxVolume, VolumeScale.Fraction(level));
        });
        TestHarness.Test("音量条：100附近没有隐藏的额外滚轮门槛", () =>
        {
            Assert.Equal(100d, VolumeScale.Step(99, 1));
            Assert.Equal(101d, VolumeScale.Step(100, 1));
            Assert.Equal(100d, VolumeScale.Step(101, -1));
            Assert.Equal(99d, VolumeScale.Step(100, -1));
        });
        TestHarness.Test("音量条：画面滚轮每格两档，滑条滚轮每格一档", () =>
        {
            Assert.Equal(52d, VolumeScale.Step(50, 2));
            Assert.Equal(51d, VolumeScale.Step(50, 1));
            Assert.Equal(48d, VolumeScale.Step(50, -2));
            Assert.Equal(49d, VolumeScale.Step(50, -1));
        });
        TestHarness.Test("音量条：上下界不越过0和130", () =>
        {
            Assert.Equal(130d, VolumeScale.Step(129, 2));
            Assert.Equal(0d, VolumeScale.Step(1, -2));
            Assert.Equal(1d, VolumeScale.Fraction(1000));
            Assert.Equal(0d, VolumeScale.Fraction(-1));
        });
        TestHarness.Test("音量条：非有限读数不进入布局", () =>
        {
            Assert.Equal(0d, VolumeScale.Clamp(double.NaN));
            Assert.Equal(0d, VolumeScale.Fraction(double.PositiveInfinity));
            Assert.Equal(0d, VolumeScale.Step(50, double.NaN));
        });
    }
}
