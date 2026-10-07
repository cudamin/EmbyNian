using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;

namespace EmbyNian.Playback;

public sealed partial class PlaybackService
{
    private async Task<PlaybackExit> MonitorAsync(EmbySessionScope scope, PlayingAttempt attempt, CancellationToken cancellationToken)
    {
        var handle = attempt.Handle;
        var request = attempt.Request;
        var exitTask = handle.WaitForExitAsync(cancellationToken);
        var interval = TimeSpan.FromSeconds(Math.Clamp(settings.Playback.ProgressReportIntervalSeconds, 1, 60));
        var stopEnabled = settings.Playback.StopReportEnabled && settings.Playback.ReportProgressToServer;
        var stopPercent = StopReportPercent(request.IsDonghua,
            settings.Playback.MarkWatchedPercent, settings.Playback.DonghuaMarkWatchedPercent);

        void OnPause(bool paused) => _ = ReportPauseAsync(scope, attempt, paused);
        void OnStatus(PlayerStatus status)
        {
            if (!IsCurrent(attempt) || !status.HasPosition || !double.IsFinite(status.Position)) return;
            var ticks = TimeFormat.ToTicks(status.Position);
            if (ShouldReportStop(ticks, request.RunTimeTicks, stopEnabled, stopPercent))
                _ = ReportThresholdAsync(scope, attempt, ticks);
        }
        handle.PauseChanged += OnPause;
        if (handle.HasControlChannel && handle is IPlayerControl control) control.StatusChanged += OnStatus;
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (true)
            {
                var tick = timer.WaitForNextTickAsync(cancellationToken).AsTask();
                if (await Task.WhenAny(exitTask, tick).ConfigureAwait(false) == exitTask) break;
                if (!await tick.ConfigureAwait(false)) break;
                if (!IsCurrent(attempt)) break;
                var position = await handle.GetPositionAsync(cancellationToken).ConfigureAwait(false);
                if (!IsCurrent(attempt) || exitTask.IsCompleted) break;
                if (position is not { } seconds || !double.IsFinite(seconds) || seconds < 0) continue;
                attempt.HasStarted |= seconds > request.StartSeconds + 0.5;
                var ticks = TimeFormat.ToTicks(seconds);
                Raise(ProgressChanged, new PlaybackProgress(ticks, request.RunTimeTicks, handle.IsPaused, request.Title, attempt.Generation));
                if (ShouldReportStop(ticks, request.RunTimeTicks, stopEnabled, stopPercent))
                    await ReportThresholdAsync(scope, attempt, ticks).ConfigureAwait(false);
                await ReportProgressAsync(scope, attempt, ticks, handle.IsPaused, "timeupdate", cancellationToken).ConfigureAwait(false);
            }
            return await exitTask.ConfigureAwait(false);
        }
        finally
        {
            attempt.Ending = true;
            if (handle is IPlayerControl player) player.StatusChanged -= OnStatus;
            handle.PauseChanged -= OnPause;
        }
    }

    private async Task ReportThresholdAsync(EmbySessionScope scope, PlayingAttempt attempt, long ticks)
    {
        if (!IsCurrent(attempt) || Interlocked.CompareExchange(ref attempt.ThresholdPending, 1, 0) != 0) return;
        try
        {
            await attempt.Reports.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsCurrent(attempt) || attempt.StopReported) return;
                attempt.StopReported = await ReportAsync(scope, "到阈值补报停止", (client, token) =>
                    client.ReportPlaybackStoppedAsync(Build(attempt, ticks, false, null), token)).ConfigureAwait(false);
            }
            finally { attempt.Reports.Release(); }
        }
        finally { Volatile.Write(ref attempt.ThresholdPending, 0); }
    }

    private async Task ReportPauseAsync(EmbySessionScope scope, PlayingAttempt attempt, bool paused)
    {
        try
        {
            if (!IsCurrent(attempt)) return;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var position = await attempt.Handle.GetPositionAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!IsCurrent(attempt) || position is not { } seconds || !double.IsFinite(seconds) || seconds < 0) return;
            var ticks = TimeFormat.ToTicks(seconds);
            Raise(ProgressChanged, new PlaybackProgress(ticks, attempt.Request.RunTimeTicks, paused,
                attempt.Request.Title, attempt.Generation));
            await ReportProgressAsync(scope, attempt, ticks, paused, paused ? "pause" : "unpause", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log.Warn(Category, "暂停状态读取失败", error); }
    }

    private async Task ReportProgressAsync(EmbySessionScope scope, PlayingAttempt attempt,
        long ticks, bool paused, string eventName, CancellationToken cancellationToken)
    {
        await attempt.Reports.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCurrent(attempt) || attempt.StopReported) return;
            await ReportAsync(scope, "进度", (client, token) => client.ReportPlaybackProgressAsync(
                Build(attempt, ticks, paused, eventName), token), cancellationToken).ConfigureAwait(false);
        }
        finally { attempt.Reports.Release(); }
    }

    private async Task<PlaybackResult> FinishAsync(EmbySessionScope scope, PlayingAttempt attempt, PlaybackExit exit, PlaybackTicket ticket)
    {
        var request = attempt.Request;
        var positionTicks = ResolveFinalPosition(exit, request, ticket);
        attempt.HasStarted |= exit.PositionSeconds is { } seconds && double.IsFinite(seconds) && seconds > request.StartSeconds + 0.5;
        await attempt.Reports.WaitAsync().ConfigureAwait(false);
        try
        {
            var report = Build(attempt, positionTicks, false, null);
            report.Failed = exit.IsFailure;
            var reported = attempt.StopReported || await ReportAsync(scope, "停止", (client, token) =>
                client.ReportPlaybackStoppedAsync(report, token)).ConfigureAwait(false);
            attempt.StopReported = reported;
            var watched = ShouldMarkWatched(exit, request, positionTicks)
                && await ReportAsync(scope, "标记已观看", (client, token) => client.MarkPlayedAsync(request.ItemId, token)).ConfigureAwait(false);
            var result = new PlaybackResult(exit, positionTicks, watched, reported) { HasStarted = attempt.HasStarted };
            Log.Info(Category, $"《{request.Title}》{result.ToChinese()}，位置 {TimeFormat.Clock(positionTicks)}");
            return result;
        }
        finally { attempt.Reports.Release(); }
    }

    private static long ResolveFinalPosition(PlaybackExit exit, PlaybackRequest request, PlaybackTicket ticket)
    {
        if (exit.Reason == PlaybackEndReason.EndOfFile && request.RunTimeTicks > 0) return request.RunTimeTicks;
        if (exit.PositionSeconds is { } seconds && double.IsFinite(seconds) && seconds >= 0) return TimeFormat.ToTicks(seconds);
        return Math.Max(0, ticket.StartTicks);
    }

    private bool ShouldMarkWatched(PlaybackExit exit, PlaybackRequest request, long positionTicks)
    {
        if (!settings.Playback.ReportProgressToServer) return false;
        if (exit.Reason == PlaybackEndReason.EndOfFile) return true;
        if (exit.PositionSeconds is null || request.RunTimeTicks <= 0) return false;
        var percent = StopReportPercent(request.IsDonghua, settings.Playback.MarkWatchedPercent, settings.Playback.DonghuaMarkWatchedPercent);
        return positionTicks >= request.RunTimeTicks * (Math.Clamp(percent, MarkWatchedFloor, 100) / 100d);
    }

    private static PlaybackReport Build(PlayingAttempt attempt, long positionTicks, bool paused, string? eventName) =>
        Build(attempt.Handle, attempt.Request, attempt.SessionId, attempt.TrackState.Selection, positionTicks, paused, eventName);

    /// <summary>Offline fixture seam: builds a report for the current handle without a live attempt.</summary>
    internal PlaybackReport BuildReportForTest(string eventName)
    {
        var request = new PlaybackRequest { MediaUrl = new Uri("http://offline.invalid/a.mkv"), Title = "测试" };
        return Build(_current, request, "test-session",
            (request.AudioStreamIndex, request.SubtitleStreamIndex), 0, false, eventName);
    }

    private static PlaybackReport Build(IPlaybackHandle? handle, PlaybackRequest request, string sessionId,
        (int? Audio, int? Subtitle) selection, long positionTicks, bool paused, string? eventName)
    {
        // 音量还要再问一层 VolumeKnown：通道建立了而内核的第一条真实读数未到时，Volume 仍是缺省 100 ——
        // 起播那条上报会把它当事实发给服务器，远端就显示一个没人设过的数（独立审查 2026-10-04）。未知就报 null，
        // 与「没有通道」同一待遇；静音标志不受这条影响，它没有「默认伪装成读数」的问题。
        var live = handle is IPlayerControl control && handle.HasControlChannel ? control : null;
        var status = live?.Status;
        var volume = status is { VolumeKnown: true, Volume: var known } && double.IsFinite(known)
            ? (int?)Math.Round(Math.Clamp(known, 0, AudioSettings.MaxVolume))
            : null;
        return new PlaybackReport
        {
            ItemId = request.ItemId,
            MediaSourceId = request.MediaSourceId,
            PlaySessionId = sessionId,
            PositionTicks = positionTicks,
            IsPaused = paused,
            IsMuted = status?.Muted,
            VolumeLevel = volume,
            EventName = eventName,
            CanSeek = live is not null,
            AudioStreamIndex = selection.Audio,
            SubtitleStreamIndex = selection.Subtitle
        };
    }

    /// <summary>Reports are bounded and retain the scope's cancellation all the way into HTTP.</summary>
    private async Task<bool> ReportAsync(EmbySessionScope scope, string what,
        Func<EmbyClient, CancellationToken, Task> report, CancellationToken cancellationToken = default)
    {
        if (!settings.Playback.ReportProgressToServer) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await scope.ExecuteAsync(report, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) { Log.Warn(Category, $"向服务器上报「{what}」失败", error); return false; }
    }
}
