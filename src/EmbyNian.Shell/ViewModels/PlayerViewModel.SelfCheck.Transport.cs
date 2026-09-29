using EmbyNian.Playback;

namespace EmbyNian.Shell.ViewModels;

public sealed partial class PlayerViewModel
{
    internal void AttachTimelineProbe(IPlaybackHandle? handle)
    {
        if (_playback.IsPlaying) throw new InvalidOperationException("不能替换真实播放通道");
        if (handle is not null && (!CompositionPlaybackProbe.IsRequested(Environment.GetCommandLineArgs())
            && !PlayerMotionProbe.IsRequested(Environment.GetCommandLineArgs())))
            throw new InvalidOperationException("本地句柄只允许隔离探针使用");
        ResetTimelineDrag();
        _timelineProbeHandle = handle;
    }

    internal void ApplyTimelineProbeStatus(PlayerStatus status)
    {
        if (_timelineProbeHandle is null || _playback.IsPlaying) return;
        ApplyStatus(status);
    }

    internal void SetTransportProbeStatus(PlayerStatus status, IReadOnlyList<SkipChapter> chapters)
    {
        if (_playback.IsPlaying) throw new InvalidOperationException("不能在真实播放期间替换自检状态");
        ResetTimelineDrag();
        _speedTouched = 0;
        ChapterMarks = chapters;
        ApplyStatus(status);
        ChaptersChanged?.Invoke();
    }

    internal (bool Ok, string Detail) ProbeTimelineState()
    {
        if (_playback.IsPlaying) return (false, "正在播放，拒绝合成状态");
        var status = Status;
        var marks = ChapterMarks;
        try
        {
            foreach (var paused in new[] { false, true })
            {
                SetTransportProbeStatus(new PlayerStatus { Duration = 1200, Position = 120, Paused = paused, Loaded = true }, []);
                if (!BeginTimelineDrag()) return (false, "拖动没有开始");
                MoveTimeline(0.6);
                ApplyStatus(new PlayerStatus { Duration = 1200, Position = 120, Paused = true, Loaded = true });
                if (!Scrubbing || Math.Abs(SeekValue - 600) > 0.001 || _timelineWasPaused != paused)
                    return (false, "旧播放回声覆盖了拖动位置或原暂停状态");
                var completion = EndTimelineDragAsync();
                if (!completion.IsCompletedSuccessfully || TimelineBusy || !TimelinePauseFeedbackSuppressed)
                    return (false, "松手后状态没有完整收尾");
            }
            SetTransportProbeStatus(new PlayerStatus { Duration = 1200, Loaded = true }, []);
            BeginTimelineDrag();
            ResetTimelineDrag();
            if (TimelineBusy || _seekPending is not null) return (false, "换片后残留拖动");
            SetTransportProbeStatus(new PlayerStatus(), []);
            if (BeginTimelineDrag()) return (false, "没有时长仍然开始拖动");
            return (true, "播放/暂停两种起点都记住原状态；旧回声不抢进度；松手、换片与未知时长正常收尾（无真实起播）");
        }
        finally
        {
            SetTransportProbeStatus(status, marks);
        }
    }
}
