using EmbyNian.Playback;

namespace EmbyNian.Tests;

internal static class PlayerMotionTests
{
    internal static void Register()
    {
        TestHarness.Test("播放器进场先遮住浏览页，再完成轻微位移", () =>
        {
            var run = PlayerMotion.Page(true, PlayerMotion.Pose.Entering, 1000);
            Assert.Equal(PlayerMotion.Pose.Entering, run.At(1000));
            Assert.Equal(1d, run.At(1000 + PlayerMotion.EnterFadeMilliseconds).Opacity);
            Assert.True(run.At(1000 + PlayerMotion.EnterFadeMilliseconds).Scale > 1);
            Assert.Equal(PlayerMotion.Pose.Visible, run.At(1000 + PlayerMotion.EnterMilliseconds));
            Assert.Equal(PlayerMotion.Pose.Visible, run.At(long.MaxValue));
        });
        TestHarness.Test("播放器中途退出从当前帧接上，不闪回全不透明", () =>
        {
            var entering = PlayerMotion.Page(true, PlayerMotion.Pose.Entering, 1000);
            var current = entering.At(1060);
            var leaving = PlayerMotion.Page(false, current, 1060);
            Assert.Equal(current, leaving.At(1060));
            Assert.True(leaving.At(1090).Opacity < current.Opacity);
            Assert.Equal(PlayerMotion.Pose.Leaving, leaving.At(2000));
        });
        TestHarness.Test("播放器退场中重进不归零，旧终点不再支配新动画", () =>
        {
            var leaving = PlayerMotion.Page(false, PlayerMotion.Pose.Visible, 1000);
            var current = leaving.At(1100);
            var entering = PlayerMotion.Page(true, current, 1100);
            Assert.Equal(current, entering.At(1100));
            Assert.True(entering.At(1130).Opacity > current.Opacity);
            Assert.Equal(PlayerMotion.Pose.Visible, entering.At(2000));
        });
        TestHarness.Test("播放器转场时钟提前不产生负进度或回弹", () =>
        {
            var run = PlayerMotion.Page(true, PlayerMotion.Pose.Entering, 1000);
            Assert.Equal(PlayerMotion.Pose.Entering, run.At(0));
            var previous = run.At(1000);
            for (var elapsed = 1; elapsed <= PlayerMotion.EnterMilliseconds; elapsed++)
            {
                var next = run.At(1000 + elapsed);
                Assert.True(next.Opacity >= previous.Opacity && next.Opacity <= 1);
                Assert.True(next.Scale <= previous.Scale && next.Scale >= 1);
                Assert.True(next.OffsetY <= previous.OffsetY && next.OffsetY >= 0);
                previous = next;
            }
        });
        TestHarness.Test("窗口跳变时旧缓冲按旧客户区矩形落位，屏上与跳变前全等", () =>
        {
            Assert.Equal(VideoPresentation.Placement.Identity,
                VideoPresentation.ForRect(1280, 720, 0, 0, 1280, 720, 0, 0, 1));
            var moved = VideoPresentation.ForRect(1064, 792, 2568, 536, 1064, 792, 2560, 0, 1);
            Assert.Equal(new VideoPresentation.Placement(1, 1, 8, 536), moved);
            var shrunk = VideoPresentation.ForRect(800, 600, 0, 0, 400, 300, 0, 0, 1);
            Assert.Equal(new VideoPresentation.Placement(0.5, 0.5, 0, 0), shrunk);
            var scaled = VideoPresentation.ForRect(1280, 720, 0, 0, 1280, 720, 0, 0, 2);
            Assert.Equal(new VideoPresentation.Placement(1, 1, 0, 0), scaled);
        });
        TestHarness.Test("没有矩形信息时按比例居中收进宿主，坏输入不产生无穷变换", () =>
        {
            var contain = VideoPresentation.Contain(1280, 720, 1920, 800, 1);
            Assert.Equal(0d, contain.Top);
            Assert.True(contain.Left > 0);
            Assert.True(Math.Abs(contain.ScaleY * 720 - 800) < 0.001);
            Assert.Equal(new VideoPresentation.Placement(1.5, 1.5, 0, 0),
                VideoPresentation.Contain(1280, 720, 1920, 1080, 1));
            Assert.Equal(VideoPresentation.Placement.Identity, VideoPresentation.Contain(0, 720, 800, 600, 1));
            Assert.Equal(VideoPresentation.Placement.Identity, VideoPresentation.Contain(1280, 720, double.NaN, 600, 1));
            Assert.Equal(VideoPresentation.Placement.Identity, VideoPresentation.Contain(1280, 720, 800, 600, 0));
            Assert.Equal(VideoPresentation.Placement.Identity,
                VideoPresentation.ForRect(0, 720, 0, 0, 800, 600, 0, 0, 1));
        });
        TestHarness.Test("呈现跑动在两块摆放之间插值并夹住进度", () =>
        {
            var from = new VideoPresentation.Placement(2, 2, 10, 20);
            var middle = VideoPresentation.Between(from, VideoPresentation.Placement.Identity, 0.5);
            Assert.Equal(new VideoPresentation.Placement(1.5, 1.5, 5, 10), middle);
            Assert.Equal(from, VideoPresentation.Between(from, VideoPresentation.Placement.Identity, -1));
            Assert.Equal(VideoPresentation.Placement.Identity,
                VideoPresentation.Between(from, VideoPresentation.Placement.Identity, 2));
        });
        TestHarness.Test("未知视频尺寸不冒充已就绪，±2px 内算追平", () =>
        {
            Assert.False(VideoPresentation.Matches(0, 0, 0, 0));
            Assert.False(VideoPresentation.Matches(1280, 720, 1920, 1080));
            Assert.True(VideoPresentation.Matches(1919, 1079, 1920, 1080));
            Assert.False(VideoPresentation.Matches(1917, 1080, 1920, 1080));
        });
    }
}
