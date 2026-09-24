using EmbyNian.Playback;

namespace EmbyNian.Tests;

/// <summary>
/// 「进度条到底会不会动」这条判据，钉在 <see cref="StatusCoalescer"/> 上。
/// <para>
/// 事故形状（2026-09-23 用户报「集成模式下进度条的数据不会实时刷新」）：<c>time-pos</c> 每帧报一次、
/// 1× 播放下每次只挪约 0.03 秒，够不着 0.25 秒的合流门槛。原来的 <c>Publish</c> 把「最新值」与「上次发布值」
/// 当成同一个字段，于是基线跟着每一帧往前爬，门缝永远只有一步宽、永远开不了——独占播放不受影响
/// （uosc 直接读 mpv），集成模式那条 WinUI 进度条却只在暂停、缓冲、整秒缓存这些粗事件上跳一下。
/// 合流器把基线单独拿出来，累计的一小步才能攒过门槛。
/// </para>
/// </summary>
internal static class StatusCoalescerTests
{
    internal static void Register()
    {
        // 主判据（这一条会在病根回来时当场变红）：一帧只挪一点，攒到阈值仍要周期性发布。
        TestHarness.Test("状态合流：位置每帧只挪一点，累计过阈值仍周期性发布", () =>
        {
            var coalescer = new StatusCoalescer();
            var frame = new PlayerStatus { Duration = 120, Position = 0 };

            // 起手那一帧总要过（从空快照到「有片长、有位置」本身就是一次改变）。
            Assert.True(coalescer.ShouldPublish(frame), "第一帧有内容，必发");

            // 约 10 秒的帧，每帧 +0.033 秒——单看每一步都够不着 0.25。基线要是跟着每帧走，
            // 这个计数就会停在 0，进度条一格都不挪。
            var published = 0;
            var position = 0.0;
            for (var tick = 0; tick < 300; tick++)
            {
                position += 0.033;
                if (coalescer.ShouldPublish(frame with { Position = position })) published++;
            }

            // 10 秒 / 0.25 秒 ≈ 40 次；给足富余，只要不是「零次」就说明门开得了。
            Assert.True(published >= 30, $"位置累计过阈值该周期性发布，实际只发了 {published} 次");
        });

        // 门槛本身还在：够不着的一小步不发，原地不动更不发，攒过阈值才发——而且比的是「上次发布值」。
        TestHarness.Test("状态合流：够不着阈值不发，攒过了才发，基线是上次发布值", () =>
        {
            var coalescer = new StatusCoalescer();
            var start = new PlayerStatus { Duration = 120, Position = 10 };

            Assert.True(coalescer.ShouldPublish(start), "第一帧必发");
            Assert.False(coalescer.ShouldPublish(start with { Position = 10.1 }), "挪 0.1 秒够不着 0.25，不发");
            Assert.False(coalescer.ShouldPublish(start with { Position = 10.1 }), "还是那一帧，更不发");

            // 关键：基线仍是上次发布的 10（不是刚被吞掉的 10.1），所以 10.3 相对 10 已过阈值。
            Assert.True(coalescer.ShouldPublish(start with { Position = 10.3 }), "从上次发布的 10 挪到 10.3，过阈值，发");
            Assert.Equal(10.3, coalescer.Published.Position);
        });

        // 粗事件照旧即时过门：暂停、缓冲这类离散翻面本来就一次跨过阈值。
        TestHarness.Test("状态合流：暂停这类离散事件即时发布", () =>
        {
            var coalescer = new StatusCoalescer();
            var playing = new PlayerStatus { Duration = 120, Position = 30 };

            Assert.True(coalescer.ShouldPublish(playing), "第一帧必发");
            Assert.False(coalescer.ShouldPublish(playing), "同一帧不发");
            Assert.True(coalescer.ShouldPublish(playing with { Paused = true }), "暂停翻面必发");
        });
    }
}
