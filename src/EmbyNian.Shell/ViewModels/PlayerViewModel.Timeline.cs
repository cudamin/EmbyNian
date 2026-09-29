using System.Globalization;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;

namespace EmbyNian.Shell.ViewModels;

public sealed partial class PlayerViewModel
{
    private bool _timelineDragging;
    private bool _timelineEnding;
    private bool _timelineWasPaused;
    private int _timelineEpoch;
    private Task _timelineCommand = Task.CompletedTask;
    private long _timelineQuietUntil;
    private long _timelineLastMove;
    private long _timelineLastSeek;
    private double _timelineLastFraction;
    private bool _timelineKeyframes;
    private IPlaybackHandle? _timelineProbeHandle;

    private Task SetTimelinePausedAsync(bool paused) => _timelineProbeHandle is { } probe
        ? probe.SetPropertyAsync("pause", paused, _lifetime.Token) : _playback.SetPropertyAsync("pause", paused);

    private Task<bool> SendTimelineCommandAsync(params string[] arguments) => _timelineProbeHandle is IPlayerControl probe
        ? probe.CommandAsync(arguments, _lifetime.Token) : _playback.CommandAsync(arguments);

    internal bool TimelineBusy => _timelineDragging || _timelineEnding;
    internal bool TimelinePauseFeedbackSuppressed => TimelineBusy || Now < _timelineQuietUntil;

    internal bool BeginTimelineDrag()
    {
        if (!Status.HasDuration || _timelineEnding || _timelineDragging) return false;
        _timelineDragging = true;
        _timelineWasPaused = Status.Paused;
        _timelineLastMove = 0;
        _timelineLastSeek = 0;
        _timelineKeyframes = false;
        _timelineEpoch++;
        _timelineCommand = SetTimelinePausedAsync(true);
        return true;
    }

    internal void MoveTimeline(double fraction)
    {
        if (!Status.HasDuration || _timelineEnding) return;
        fraction = TimelineScale.Fraction(fraction);
        if (_timelineDragging)
        {
            _timelineKeyframes = _timelineLastMove > 0 && TimelineScale.PreferKeyframes(
                _timelineLastFraction, fraction, Status.Duration, Now - _timelineLastMove);
            _timelineLastMove = Now;
            _timelineLastFraction = fraction;
        }
        SeekValue = fraction * SeekScale;
        _seekTouched = Now;
        _seekPending = Math.Clamp(fraction, 0, 1);
        FlushTimelineSeek();
    }

    private void FlushTimelineSeek()
    {
        if (_timelineEnding || !_timelineCommand.IsCompleted || _seekPending is not { } fraction) return;
        if (_timelineDragging && Now - _timelineLastSeek < 100) return;
        _timelineLastSeek = Now;
        _seekPending = null;
        _seekSent = fraction;
        _seekSentAt = Now;
        _timelineCommand = SendTimelineCommandAsync("seek",
            (fraction * 100).ToString(CultureInfo.InvariantCulture),
            _timelineDragging && _timelineKeyframes ? "absolute-percent+keyframes" : "absolute-percent+exact");
    }

    internal async Task EndTimelineDragAsync()
    {
        if (!_timelineDragging) return;
        _timelineDragging = false;
        _timelineEnding = true;
        var epoch = _timelineEpoch;
        var generation = _generation;
        var attempt = _playbackAttempt;
        var paused = _timelineWasPaused;
        var fraction = Math.Clamp(SeekValue / SeekScale, 0, 1);
        _seekPending = null;
        try
        {
            await _timelineCommand.ConfigureAwait(true);
            if (!Current()) return;
            _seekSent = fraction;
            _seekSentAt = Now;
            await SendTimelineCommandAsync("seek", (fraction * 100).ToString(CultureInfo.InvariantCulture),
                "absolute-percent+exact").ConfigureAwait(true);
            if (!Current()) return;
            await SetTimelinePausedAsync(paused).ConfigureAwait(true);
        }
        finally
        {
            if (epoch == _timelineEpoch)
            {
                _timelineEnding = false;
                _timelineQuietUntil = Now + ScrubGraceMilliseconds;
            }
        }

        bool Current() => epoch == _timelineEpoch && generation == _generation
            && attempt == _playbackAttempt && !_lifetime.IsCancellationRequested;
    }

    private void ResetTimelineDrag()
    {
        _timelineEpoch++;
        _timelineDragging = false;
        _timelineEnding = false;
        _timelineCommand = Task.CompletedTask;
        _seekPending = null;
        _seekSent = null;
        _seekTouched = 0;
        _timelineQuietUntil = 0;
    }

    internal void SeekTimelineBy(int seconds)
    {
        if (!Status.HasDuration || TimelineBusy) return;
        var position = Scrubbing || _seekSent is not null ? SeekValue / SeekScale * Status.Duration : Status.Position;
        MoveTimeline((position + seconds) / Status.Duration);
    }

    internal void SeekTimelineChapter(double seconds)
    {
        if (TimelineBusy || !Status.HasDuration) return;
        MoveTimeline(seconds / Status.Duration);
    }

    private void UpdateRemainingClock()
    {
        RemainingClock = !Status.HasDuration ? "−0:00" : "−" + TimelineScale.Clock(
            TimelineScale.Remaining(SeekValue / SeekScale, Status.Duration, SpeedValue), Status.Duration);
    }
}
