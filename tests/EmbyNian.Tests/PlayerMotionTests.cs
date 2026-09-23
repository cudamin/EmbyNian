using EmbyNian.Playback;

namespace EmbyNian.Tests;

internal static class PlayerMotionTests
{
    internal static void Register()
    {
        TestHarness.Test("自动全屏进场只溶解，整页不缩放或位移", () =>
        {
            var run = PlayerMotion.FullscreenEnter(PlayerMotion.Pose.Entering, 1000);
            Assert.Equal(new PlayerMotion.Pose(0, 1, 0), run.At(1000));
            var previous = 0d;
            for (var elapsed = 0; elapsed <= PlayerMotion.FullscreenEnterMilliseconds; elapsed++)
            {
                var pose = run.At(1000 + elapsed);
                Assert.Equal(1d, pose.Scale);
                Assert.Equal(0d, pose.OffsetY);
                Assert.True(pose.Opacity >= previous && pose.Opacity <= 1);
                previous = pose.Opacity;
            }
            Assert.Equal(PlayerMotion.Pose.Visible, run.At(long.MaxValue));
        });
        TestHarness.Test("自动全屏重入承接当前透明度，不继承退场的缩放", () =>
        {
            var from = new PlayerMotion.Pose(0.4, PlayerMotion.ExitScale, PlayerMotion.ExitTravel);
            var run = PlayerMotion.FullscreenEnter(from, 1000);
            Assert.Equal(new PlayerMotion.Pose(0.4, 1, 0), run.At(500));
            Assert.True(run.At(1080).Opacity > 0.4);
            Assert.Equal(PlayerMotion.Pose.Visible, run.At(1000 + PlayerMotion.FullscreenEnterMilliseconds));
        });
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
        // 2026-09-20「换缓冲时画面有没有跳」的判据：矩形模型下呈现只由「这一帧该占的矩形」决定，
        // 缓冲换了（1600x900 → 1280x720，同一块矩形）摆出的屏上大小必须一模一样 —— 这正是旧
        // 「按缓冲重锚」模型做不到、反向切换会平移一截的那一处。
        TestHarness.Test("换缓冲不改呈现矩形，屏上大小与落点分毫不动", () =>
        {
            var frame = new VideoPresentation.Rect(40, 12, 1600, 900);
            var before = VideoPresentation.ForFrame(1600, 900, frame, 1);
            var after = VideoPresentation.ForFrame(1280, 720, frame, 1);
            // 缓冲尺寸变了，但矩形不动 ⇒ 缩放按新缓冲重折算，屏上仍是 1600x900 @ (40,12)。
            Assert.Equal(1d, before.ScaleX);
            Assert.Equal(1.25, after.ScaleX);
            Assert.Equal(1.25, after.ScaleY);
            Assert.Equal(40d, before.Left);
            Assert.Equal(40d, after.Left);
            Assert.Equal(12d, after.Top);
            Assert.True(Math.Abs(1280 * after.ScaleX - 1600) < 0.001);
            Assert.True(Math.Abs(720 * after.ScaleY - 900) < 0.001);
        });
        TestHarness.Test("呈现矩形按光栅化比例落到 DIP，缩放不重复折算", () =>
        {
            // 150% 屏：Visual 的尺寸是「缓冲 ÷ 1.5」，落点也除 1.5；缩放是两个物理像素长度之比，
            // 与 raster 无关 —— 多除一次就会在 150% 屏上把画面缩成三分之二。
            var frame = new VideoPresentation.Rect(0, 0, 1920, 1080);
            var placement = VideoPresentation.ForFrame(1920, 1080, frame, 1.5);
            Assert.Equal(1d, placement.ScaleX);
            Assert.Equal(1d, placement.ScaleY);
            Assert.Equal(0d, placement.Left);
            Assert.Equal(0d, placement.Top);
            // 屏上宽度 = (1920/1.5) × 1 × 1.5 = 1920。
            Assert.True(Math.Abs(1920 / 1.5 * placement.ScaleX * 1.5 - 1920) < 0.001);
        });
        TestHarness.Test("拖边时稳定缓冲可连续缩放与反向，松手换缓冲不改变最终画幅", () =>
        {
            foreach (var raster in new[] { 1d, 1.25, 1.5, 2d })
            {
                foreach (var ratio in new[] { 0.64, 0.72, 1d, 1.5, 0.65, 1.35, 0.7 })
                {
                    var frame = new VideoPresentation.Rect(0, 0, 1280 * ratio, 720 * ratio);
                    var placement = VideoPresentation.ForFrame(1280, 720, frame, raster);
                    Assert.True(Math.Abs(1280 / raster * placement.ScaleX * raster - frame.Width) < 0.001);
                    Assert.True(Math.Abs(720 / raster * placement.ScaleY * raster - frame.Height) < 0.001);
                    Assert.True(Math.Abs(placement.ScaleX - placement.ScaleY) < 0.001);
                    Assert.Equal(0d, placement.Left);
                    Assert.Equal(0d, placement.Top);
                }
                var final = new VideoPresentation.Rect(0, 0, 1920, 1080);
                var oldBuffer = VideoPresentation.ForFrame(1280, 720, final, raster);
                var newBuffer = VideoPresentation.ForFrame(1920, 1080, final, raster);
                Assert.Equal(1280 * oldBuffer.ScaleX, 1920 * newBuffer.ScaleX);
                Assert.Equal(720 * oldBuffer.ScaleY, 1080 * newBuffer.ScaleY);
                Assert.Equal(1d, newBuffer.ScaleX);
            }
        });
        TestHarness.Test("呈现矩形坏输入退回单位摆放，不产生无穷变换", () =>
        {
            Assert.Equal(VideoPresentation.Placement.Identity,
                VideoPresentation.ForFrame(0, 720, new VideoPresentation.Rect(0, 0, 800, 600), 1));
            Assert.Equal(VideoPresentation.Placement.Identity,
                VideoPresentation.ForFrame(1280, 720, new VideoPresentation.Rect(0, 0, 0, 600), 1));
            Assert.Equal(VideoPresentation.Placement.Identity,
                VideoPresentation.ForFrame(1280, 720, new VideoPresentation.Rect(0, 0, 800, 600), 0));
            Assert.Equal(VideoPresentation.Placement.Identity,
                VideoPresentation.ForFrame(1280, 720, new VideoPresentation.Rect(double.NaN, 0, 800, 600), 1));
        });
        // 2026-09-20 用户报「退出播放时下方瞬间出现大片空白」的根因不在排序，而在「那一帧按什么形状
        // 摆」。退场时窗口还的是浏览几何（用户上次拖的 1528x1251），播放时窗口是片子形状
        // （1528x858）—— 若拿窗口形状当画面形状去摆，16:9 的片子会被拉成接近正方形。
        // 正确的形状是视频自己的比例，退场那一刻唯一还带着它的就是 SourceAspect。
        //
        // 2026-09-20 晚重新设计退场之后，这一档的**摆法**从 contain 改成 cover（用户令「重新设计」）：
        // 窗口正在变形的那一帧，contain 会把最后一帧当场缩成一条信匣（四周一圈近黑），读起来是
        // 「画面缩了」；cover 是「同一幅满屏的画」，之后整幅溶解进浏览页 —— 读起来才是「画面退去」。
        // 留边那一档没有消失，它是留帧的默认摆法（换集期间的留帧走它）。
        TestHarness.Test("退场保留帧按视频比例铺满新宿主：不缩成信匣，也不被窗口形状拉变形", () =>
        {
            // 退场时的新宿主：浏览几何，比画面高得多。
            const double hostWidth = 1512;
            const double hostHeight = 1243;
            const double pictureAspect = 16d / 9d;

            // ① 退场那一趟（cover）：短边贴死、长边裁掉，比例仍然是画面自己的。
            var cover = VideoPresentation.FitScale(pictureAspect, 1, hostWidth, hostHeight, fill: true);
            var coverWidth = pictureAspect * cover;
            var coverHeight = cover;
            Assert.True(Math.Abs(coverHeight - hostHeight) < 0.001, "高度方向贴满（不留黑边）");
            Assert.True(coverWidth > hostWidth, "宽度方向超出并被裁掉");
            Assert.True(Math.Abs(coverWidth / coverHeight - pictureAspect) < 0.001, "比例不被窗口掰弯");
            // 铺满的判据正是探针那条「至少一个方向贴满」。
            Assert.True(Math.Abs(Math.Min(coverWidth / hostWidth, coverHeight / hostHeight) - 1) < 0.001);

            // ② 留帧的默认摆法（contain）没被换掉：宽度方向贴满、高度方向按比例留边 —— 这正是当初
            //    「退出播放时下方瞬间出现大片空白」要的样子（画面不能缩在旧尺寸里）。
            var contain = VideoPresentation.FitScale(pictureAspect, 1, hostWidth, hostHeight, fill: false);
            var containWidth = pictureAspect * contain;
            Assert.True(Math.Abs(containWidth - hostWidth) < 0.001, "宽度方向贴满");
            Assert.True(containWidth < coverWidth, "留边那一档确实比铺满小");

            // ③ 坏输入不产生无穷变换，也不猜一个尺寸出来。
            Assert.Equal(0d, VideoPresentation.FitScale(0, 1, hostWidth, hostHeight, fill: true));
            Assert.Equal(0d, VideoPresentation.FitScale(pictureAspect, 1, 0, hostHeight, fill: true));
            Assert.Equal(0d, VideoPresentation.FitScale(double.NaN, 1, hostWidth, hostHeight, fill: false));
            Assert.Equal(0d, VideoPresentation.FitScale(pictureAspect, 1, hostWidth, double.PositiveInfinity, fill: false));
        });

        // 2026-09-20「重新设计集成模式下退出播放界面返回主页的动画」的两条判据。旧的退场是「一边淡
        // 一边朝观者涨（1.018）」，画面那一支则是一步从全亮掉到 15%、再靠一块不透明的底淡出 —— 用户
        // 看到的是「沉黑一下」。新的两头都反了：页面往后退，画面一路溶解。
        TestHarness.Test("退场是往后退：缩到不足一，并沉回进场时它上来的那一侧", () =>
        {
            Assert.True(PlayerMotion.ExitScale < 1, "退场要往后退，不是朝观者涨");
            Assert.True(PlayerMotion.ExitTravel > 0, "往下沉，与进场从下方浮起对称");
            Assert.Equal(new PlayerMotion.Pose(0, PlayerMotion.ExitScale, PlayerMotion.ExitTravel),
                PlayerMotion.Pose.Leaving);

            var run = PlayerMotion.Page(false, PlayerMotion.Pose.Visible, 1000);
            var middle = run.At(1000 + PlayerMotion.ExitMilliseconds / 2);
            Assert.True(middle.Scale < 1 && middle.Scale > PlayerMotion.ExitScale, "半途的缩放落在两头之间");
            Assert.True(middle.OffsetY > 0 && middle.OffsetY < PlayerMotion.ExitTravel, "半途的位移落在两头之间");
            Assert.True(PlayerMotion.ExitMilliseconds >= 200, "溶解要留够时间，太短读起来还是硬切");
        });

        TestHarness.Test("退场的留帧是一路溶解，不是一步压到 15%", () =>
        {
            Assert.Equal(0d, PlayerMotion.ExitDimAt(-1));
            Assert.Equal(0d, PlayerMotion.ExitDimAt(0), "起手那一拍画面还亮着");
            Assert.Equal(1d, PlayerMotion.ExitDimAt(1));
            Assert.Equal(1d, PlayerMotion.ExitDimAt(2));

            var previous = PlayerMotion.ExitDimAt(0);
            for (var step = 1; step <= 20; step++)
            {
                var dim = PlayerMotion.ExitDimAt(step / 20d);
                Assert.True(dim > previous, $"每一步都必须比上一拍更暗（第 {step} 步）");
                previous = dim;
            }

            Assert.True(Math.Abs(PlayerMotion.ExitDimAt(0.5) - 0.5) < 1e-9, "匀速，不是先快后慢");
            // 起手那几拍必须还在「亮着」这一档里 —— 这正是旧的一步压到 0.85 做不到的。
            Assert.True(PlayerMotion.ExitDimAt(0.1) < 0.2, "头 10% 里画面还必须读得出内容");
        });
        // 2026-09-20「拖动窗口边缘闪烁」的判据。拖动期间每一拍 WM_SIZE 都让宿主尺寸跑到缓冲前面，
        // 旧版于是掉进 contain、把画面缩出新黑边，下一拍缓冲追上了又铺满 —— 一拍一个样就是闪。
        // 规则：缓冲一致就铺满（正片就绪）；缓冲正在追赶也铺满（拖动那一段）；只有「缓冲明显还
        // 不是这块宿主、又没在追赶」才按比例留边。
        TestHarness.Test("缓冲追赶新宿主期间继续铺满，不回退成留边", () =>
        {
            // 拖动中：宿主要了 1512x850，缓冲还是上一拍的 1512x1243，且标记了正在追赶。
            Assert.True(VideoPresentation.ShouldFill(1512, 1243, 1512, 850, resizePending: true),
                "追赶期间必须铺满，否则拖动会一拍一个样");
            // 同一组尺寸、没标追赶（例如刚开播，缓冲还是另一部片的）才允许留边。
            Assert.False(VideoPresentation.ShouldFill(1512, 1243, 1512, 850, resizePending: false));
            // 缓冲已一致：无论有没有追赶标记都铺满。
            Assert.True(VideoPresentation.ShouldFill(1512, 850, 1512, 850, resizePending: false));
            Assert.True(VideoPresentation.ShouldFill(1512, 850, 1512, 850, resizePending: true));
            // ±2px 落在容差内算一致，不掉进留边。
            Assert.True(VideoPresentation.ShouldFill(1511, 849, 1512, 850, resizePending: false));
        });
        // 2026-09-20 用户令「去掉集成模式下切换全屏和窗口化的画面动画，参考其他播放器正常切换就好」。
        // 窗口是瞬时跳变的，画面也必须当场落位；而落位那一拍缓冲还在旧尺寸上（mpv 重建缓冲 110~150ms），
        // 旧窗口形状又与新宿主形状恰恰不是一个比例 —— 于是摆法只有一个正确答案：按画面自己的比例居中
        // 放进去。铺满＝把上一帧按新窗口的长宽比拉变形；而「按比例摆」露出来的那块画面，与缓冲追上之后
        // 「铺满」摆出来的可见画面**是同一处**（下面最后一条就是钉这件事），所以中间没有一帧跳动。
        TestHarness.Test("客户区跳变当场落位：按画面比例进新宿主，与缓冲追上后的可见画面重合", () =>
        {
            // 窗口化时窗口被整形为片子形状 2.35:1，全屏宿主是 16:9。
            const int windowed = 1512;
            const int windowedHeight = 643;
            const int hostWidth = 2560;
            const int hostHeight = 1440;

            // 缓冲还没跟上：不铺满 —— 铺满就是把上一帧拉变形。
            Assert.False(VideoPresentation.ShouldFill(windowed, windowedHeight, hostWidth, hostHeight, resizePending: false));

            var box = VideoPresentation.Contain(windowed, windowedHeight, hostWidth, hostHeight, 1);
            Assert.True(Math.Abs(windowed * box.ScaleX - hostWidth) < 0.001, "宽边贴满屏幕");
            Assert.True(box.Left == 0 && box.Top > 0, "居中留黑边，不是铺满");
            Assert.True(Math.Abs(windowed * box.ScaleX / (windowedHeight * box.ScaleY) - (double)windowed / windowedHeight) < 0.001,
                "画面比例没被宿主掰弯");

            // 缓冲追上之后走铺满：mpv 把同一部片子按同一比例居中画进整块宿主，可见画面高
            // ＝ hostWidth ÷ (windowed ÷ windowedHeight) —— 与上面那块完全重合，换过去看不见跳。
            var letterboxed = hostWidth * ((double)windowedHeight / windowed);
            Assert.True(Math.Abs(windowedHeight * box.ScaleY - letterboxed) < 1.0,
                "两种摆法里「画面自己的框」是同一处");
        });
        TestHarness.Test("切换只在缓冲和布局都追上目标客户区后结束", () =>
        {
            Assert.False(VideoPresentation.ResizeSettled(2560, 1440, 1512, 850, 2560, 1440));
            Assert.False(VideoPresentation.ResizeSettled(1512, 850, 2560, 1440, 2560, 1440));
            Assert.False(VideoPresentation.ResizeSettled(1512, 850, 1512, 850, 2560, 1440));
            Assert.True(VideoPresentation.ResizeSettled(2560, 1440, 2559, 1439, 2560, 1440));
            Assert.False(VideoPresentation.ResizeSettled(0, 0, 0, 0, 0, 0));
            Assert.False(VideoPresentation.ResizeSettled(1512, 850, 2560, 1440, 1512, 850));
            Assert.True(VideoPresentation.ResizeSettled(1512, 850, 1512, 850, 1512, 850));
        });
        TestHarness.Test("呈现区域直接按目标框折算 DPI，不再分成缓冲大小和补偿缩放", () =>
        {
            var frame = new VideoPresentation.Rect(0, 0, 1512, 850);
            Assert.Equal(frame, VideoPresentation.ToDips(frame, 1));
            Assert.Equal(new VideoPresentation.Rect(0, 0, 1008, 850 / 1.5),
                VideoPresentation.ToDips(frame, 1.5));
            var fullscreen = new VideoPresentation.Rect(0, 0, 2560, 1440);
            Assert.Equal(fullscreen, VideoPresentation.ToDips(fullscreen, 1));
            Assert.Equal(new VideoPresentation.Rect(20, 10, 1280, 720),
                VideoPresentation.ToDips(new VideoPresentation.Rect(40, 20, 2560, 1440), 2));
            Assert.Equal(default(VideoPresentation.Rect), VideoPresentation.ToDips(frame, 0));
            Assert.Equal(default(VideoPresentation.Rect), VideoPresentation.ToDips(frame, double.NaN));
            Assert.Equal(default(VideoPresentation.Rect),
                VideoPresentation.ToDips(new VideoPresentation.Rect(0, 0, double.PositiveInfinity, 850), 1));
        });
        TestHarness.Test("保留帧拒绝无效步幅和溢出尺寸", () =>
        {
            Assert.Equal(1280 * 720 * 4, VideoFrame.BufferLength(1280, 720, 5120));
            Assert.Equal(0, VideoFrame.BufferLength(1280, 720, 5119));
            Assert.Equal(0, VideoFrame.BufferLength(0, 720, 5120));
            Assert.Equal(0, VideoFrame.BufferLength(int.MaxValue, int.MaxValue, int.MaxValue));
        });
        TestHarness.Test("全屏保留帧只取有效画面，退出时不把旧黑边再缩进窗口", () =>
        {
            var picture = VideoFrame.PictureRect(2560, 1440, 0, 175, 0, 176);
            Assert.Equal(new VideoPresentation.Rect(0, 175, 2560, 1089), picture);
            var scale = VideoPresentation.FitScale(picture.Width, picture.Height, 1512, 643, false);
            Assert.True(Math.Abs(picture.Width * scale - 1512) < 1);
            Assert.True(Math.Abs(picture.Height * scale - 643) < 1);
            Assert.Equal(new VideoPresentation.Rect(0, 0, 2560, 1440),
                VideoFrame.PictureRect(2560, 1440, -1, -1, 0, 0));
            Assert.Equal(new VideoPresentation.Rect(0, 0, 2560, 1440),
                VideoFrame.PictureRect(2560, 1440, 2000, 0, 2000, 0));
        });
    }
}
