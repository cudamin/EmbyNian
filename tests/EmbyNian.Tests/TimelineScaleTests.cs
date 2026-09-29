using EmbyNian.Playback;

namespace EmbyNian.Tests;

internal static class TimelineScaleTests
{
    internal static void Register()
    {
        TestHarness.Test("时间轴：点击坐标覆盖首尾且不越界", () =>
        {
            Assert.Equal(0d, TimelineScale.At(-10, 800));
            Assert.Equal(0.5, TimelineScale.At(400, 800));
            Assert.Equal(1d, TimelineScale.At(900, 800));
            Assert.Equal(0d, TimelineScale.At(1, 0));
            Assert.Equal(0d, TimelineScale.At(double.NaN, 800));
        });
        TestHarness.Test("时间轴：剩余时间按当前倍速折算", () =>
        {
            Assert.Equal(600d, TimelineScale.Remaining(0.5, 1200, 1));
            Assert.Equal(300d, TimelineScale.Remaining(0.5, 1200, 2));
            Assert.Equal(1200d, TimelineScale.Remaining(0.5, 1200, 0.5));
            Assert.Equal(0d, TimelineScale.Remaining(1.1, 1200, 1));
            Assert.Equal(0d, TimelineScale.Remaining(0.5, double.NaN, 1));
            Assert.Equal(600d, TimelineScale.Remaining(0.5, 1200, 0));
        });
        TestHarness.Test("时间轴：快拖关键帧，慢拖精确", () =>
        {
            Assert.True(TimelineScale.PreferKeyframes(0, 0.1, 1200, 100));
            Assert.False(TimelineScale.PreferKeyframes(0, 0.001, 1200, 100));
            Assert.False(TimelineScale.PreferKeyframes(0, 0.5, 1200, 0));
        });
        TestHarness.Test("时间轴：章节命中只吸附最近的六像素", () =>
        {
            SkipChapter[] chapters = [new(0, "片头"), new(100, "第一章"), new(400, "第二章")];
            Assert.Equal(0.1, TimelineScale.SnapChapter(0.104, 1000, 1000, chapters));
            Assert.Equal(0.11, TimelineScale.SnapChapter(0.11, 1000, 1000, chapters));
            Assert.Equal(0.4, TimelineScale.SnapChapter(0.396, 1000, 1000, chapters));
            Assert.Equal(0.5, TimelineScale.SnapChapter(0.5, 1000, 0, chapters));
        });
    }
}
