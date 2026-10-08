using Momoka.Configuration;
using Momoka.Diagnostics;
using Momoka.Emby;
using Momoka.Infrastructure;

namespace Momoka.Playback;

public sealed partial class PlaybackService
{
    private sealed class PlayOperation(CancellationToken cancellationToken, CancellationToken startupCancellation) : IDisposable
    {
        public CancellationTokenSource Startup { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupCancellation);
        public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Established { get; set; }
        public void Dispose() { Startup.Dispose(); Lifetime.Dispose(); }
    }

    private sealed class PlayingAttempt(IPlaybackHandle handle, PlaybackRequest request, long generation, PlaybackTrackState trackState)
    {
        private readonly object _stopGate = new();
        private Task? _stopTask;
        public IPlaybackHandle Handle { get; } = handle;
        public PlaybackRequest Request { get; } = request;
        public long Generation { get; } = generation;
        public PlaybackTrackState TrackState { get; } = trackState;
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public SemaphoreSlim Reports { get; } = new(1, 1);
        public volatile bool Ending;
        public bool StopReported { get; set; }
        public bool HasStarted { get; set; }
        public int ThresholdPending;
        public MpvBackendKind BackendKind { get; init; }
        public Action? Unsubscribe { get; set; }
        public Task StopAsync()
        {
            lock (_stopGate) return _stopTask ??= StopHandleAsync(Handle);
        }
    }

    public Task<PlaybackResult> PlayAsync(PlaybackTicket ticket, CancellationToken cancellationToken) =>
        PlayAsync(ticket, null, cancellationToken);

    /// <summary>Derived episode/version requests retain the identity that supplied their metadata.</summary>
    public async Task<PlaybackResult> PlayAsync(
        PlaybackTicket ticket, EmbySessionScope? identity, CancellationToken cancellationToken,
        CancellationToken startupCancellation = default)
    {
        if (!allowPlayback) throw new InvalidOperationException("自检模式禁止真实播放");
        cancellationToken.ThrowIfCancellationRequested();
        startupCancellation.ThrowIfCancellationRequested();
        EmbySessionScope scope;
        try { scope = identity ?? session.Capture(); }
        catch
        {
            if (_current is null) RaiseNowPlaying(null, Generation);
            throw;
        }

        using var operation = new PlayOperation(cancellationToken, startupCancellation);
        lock (_requestGate)
        {
            if (_latestRequest is { Established: false } previous) Cancel(previous.Startup);
            _latestRequest = operation;
            _operations.Add(operation);
        }
        try
        {
            var candidates = CandidateSources(ticket);
            for (var index = 0; index < candidates.Count; index++)
            {
                var attempt = index == 0 ? ticket : ticket with
                {
                    Source = candidates[index],
                    AudioStreamIndex = null,
                    SubtitleStreamIndex = null
                };
                var result = await PlayOneAsync(scope, attempt, operation).ConfigureAwait(false);
                if (!result.Exit.IsFailure || result.HasStarted || index == candidates.Count - 1) return result;
                ThrowIfSuperseded(operation);
                Log.Info(Category, $"《{attempt.Item.ToPlaybackTitle()}》这一版打不开，改试另一版（{index + 2}/{candidates.Count}）");
            }
            throw new InvalidOperationException("没有可用的播放候选");
        }
        finally
        {
            lock (_requestGate)
            {
                _operations.Remove(operation);
                if (ReferenceEquals(_latestRequest, operation)) _latestRequest = null;
                operation.Completed.TrySetResult();
            }
        }
    }

    private void ThrowIfSuperseded(PlayOperation operation)
    {
        operation.Startup.Token.ThrowIfCancellationRequested();
        lock (_requestGate)
        {
            if (!ReferenceEquals(_latestRequest, operation)) throw new OperationCanceledException("播放请求已被撤销");
        }
    }

    private static void Cancel(CancellationTokenSource source)
    {
        // CancelAsync changes IsCancellationRequested synchronously but does not run foreign callbacks under our lock.
        _ = ObserveCancellationAsync(source.CancelAsync());
    }

    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception error) { Log.Warn(Category, "播放取消回调失败", error); }
    }

    public Task StopAsync()
    {
        PlayOperation[] operations;
        PlayingAttempt? active;
        IPlaybackHandle? handle;
        lock (_requestGate)
        {
            _latestRequest = null;
            operations = [.. _operations];
            foreach (var operation in operations)
            {
                Cancel(operation.Startup);
                Cancel(operation.Lifetime);
            }
            active = _active;
            handle = _current;
        }
        return StopAndWaitAsync(operations, active, handle);
    }

    private static async Task StopAndWaitAsync(PlayOperation[] operations, PlayingAttempt? active, IPlaybackHandle? handle)
    {
        if (active is not null) await active.StopAsync().ConfigureAwait(false);
        else if (handle is not null) await StopHandleAsync(handle).ConfigureAwait(false);
        await Task.WhenAll(operations.Select(operation => operation.Completed.Task)).ConfigureAwait(false);
    }

    private async Task<PlaybackResult> PlayOneAsync(EmbySessionScope scope, PlaybackTicket ticket, PlayOperation operation)
    {
        ThrowIfSuperseded(operation);
        var connection = await scope.ExecuteAsync((client, _) => Task.FromResult(client.Connection), operation.Startup.Token)
            .ConfigureAwait(false);
        var audioDevice = await ResolveAudioDeviceAsync().WaitAsync(operation.Startup.Token).ConfigureAwait(false);
        ThrowIfSuperseded(operation);
        var request = planner.Plan(ticket, connection, audioDevice);
        var backendKind = settings.Mpv.Backend;
        var old = _active;
        var live = _current;
        var takeover = backendKind == MpvBackendKind.BuiltInLibMpv && live?.CanSwapTo(request) == true ? live : null;
        ThrowIfSuperseded(operation);
        if (takeover is not null) takeover.HandOver();
        else if (old is not null) await old.StopAsync().ConfigureAwait(false);
        else if (live is not null) await StopHandleAsync(live).ConfigureAwait(false);

        // Once an old handle has been handed over, the successor must acquire the gate even if cancelled:
        // it owns disposal of any retained instance the old run has already relinquished.
        await _gate.WaitAsync().ConfigureAwait(false);
        IPlaybackHandle? handle = _current;
        PlayingAttempt? attempt = null;
        try
        {
            ThrowIfSuperseded(operation);
            if (handle is not null && (!ReferenceEquals(handle, takeover)
                || !await handle.SwapToAsync(request, operation.Startup.Token).ConfigureAwait(false)))
            {
                await StopHandleAsync(handle).ConfigureAwait(false);
                await ReleaseAsync(handle).ConfigureAwait(false);
                handle = null;
            }
            ThrowIfSuperseded(operation);
            if (handle is null) handle = await backendFactory().StartAsync(request, operation.Startup.Token).ConfigureAwait(false);

            var trackState = new PlaybackTrackState(ticket.Source, request);
            lock (_requestGate)
            {
                ThrowIfSuperseded(operation);
                operation.Established = true;
                _current = handle;
                attempt = new PlayingAttempt(handle, request, Interlocked.Increment(ref _playbackGeneration), trackState)
                {
                    BackendKind = backendKind,
                    HasStarted = handle is IPlayerControl { Status: { HasPosition: true } status }
                        && status.Position > request.StartSeconds + 0.5
                };
                _active = attempt;
            }
            ShaderStateKnown = true;
            _launchOptions = request.PlayerOptions;
            _launchGroupOptionCount = request.ShaderOptionCount;
            LaunchShaderProfile = request.ShaderProfile;
            LaunchShaderDecision = request.ShaderDecision;
            LaunchShaderReason = request.ShaderReason;
            LaunchQualityPreset = settings.Video.QualityPreset;
            PlayingSource = ticket.Source;
            AudioDeviceInUse = null;
            LastLaunch = new LaunchRecord(DateTimeOffset.Now, request.Title, request.ShaderProfile,
                request.ShaderReason, LaunchQualityPreset, request.PlayerOptions);
            Subscribe(attempt);
            if (attempt.Handle is IPlayerControl trackControl && attempt.Handle.HasControlChannel)
            {
                var trackRevision = trackState.Revision;
                using var snapshotDeadline = CancellationTokenSource.CreateLinkedTokenSource(operation.Lifetime.Token);
                snapshotDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var initialTracks = await attempt.Handle.GetTracksAsync(snapshotDeadline.Token)
                        .WaitAsync(snapshotDeadline.Token).ConfigureAwait(false);
                    if (IsCurrent(attempt)) trackState.ObserveInitial(initialTracks, trackRevision);
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { Log.Warn(Category, "读取起播轨道快照失败，等待下一次轨道通知", error); }
            }
            RaiseNowPlaying(ticket.Item, attempt.Generation);
            await ReportAsync(scope, "开始", (client, token) => client.ReportPlaybackStartAsync(
                Build(attempt, TimeFormat.ToTicks(Math.Max(0, request.StartSeconds)), false, null), token), operation.Lifetime.Token)
                .ConfigureAwait(false);

            var ended = false;
            try
            {
                var exit = await MonitorAsync(scope, attempt, operation.Lifetime.Token).ConfigureAwait(false);
                ended = true;
                attempt.Ending = true;
                return await FinishAsync(scope, attempt, exit, ticket).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                attempt.Ending = true;
                if (!ended) await attempt.StopAsync().ConfigureAwait(false);
                double? position = null;
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    position = await handle.GetPositionAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception positionError) { Log.Warn(Category, "收尾读取播放位置失败", positionError); }
                if (error is not OperationCanceledException) Log.Warn(Category, "监视播放失败", error);
                var exit = new PlaybackExit(error is OperationCanceledException ? PlaybackEndReason.Stopped : PlaybackEndReason.Error,
                    position, 0, error is OperationCanceledException ? null : "播放状态监视失败");
                return await FinishAsync(scope, attempt, exit, ticket).ConfigureAwait(false);
            }
        }
        finally
        {
            if (attempt is not null)
            {
                attempt.Ending = true;
                try { attempt.Unsubscribe?.Invoke(); }
                catch (Exception error) { Log.Warn(Category, "退订播放事件失败", error); }
                if (ReferenceEquals(_active, attempt)) _active = null;
            }
            bool retain;
            lock (_requestGate)
            {
                retain = attempt is not null && handle?.WasHandedOver == true
                    && _latestRequest is { } next && !ReferenceEquals(next, operation) && !next.Startup.IsCancellationRequested;
                operation.Established = false;
            }
            try
            {
                if (handle is not null && !retain)
                {
                    if (attempt is null) await StopHandleAsync(handle).ConfigureAwait(false);
                    await ReleaseAsync(handle).ConfigureAwait(false);
                }
                _launchOptions = [];
                _launchGroupOptionCount = 0;
                LaunchShaderProfile = null;
                LaunchShaderDecision = null;
                LaunchShaderReason = null;
                LaunchQualityPreset = null;
                PlayingSource = null;
                if (attempt is not null) RaiseNowPlaying(null, attempt.Generation);
            }
            finally { _gate.Release(); }
        }
    }

    private void Subscribe(PlayingAttempt attempt)
    {
        if (attempt.Handle is not IPlayerControl control) return;
        void OnStatus(PlayerStatus status)
        {
            if (!IsCurrent(attempt)) return;
            attempt.HasStarted |= status.PictureStarted || status.HasPosition && status.Position > attempt.Request.StartSeconds + 0.5;
            Raise(StatusUpdated, new PlaybackUpdate<PlayerStatus>(attempt.Generation, status));
            Raise(StatusChanged, status);
        }
        void OnTracks(IReadOnlyList<MpvTrack> tracks)
        {
            if (!IsCurrent(attempt)) return;
            attempt.TrackState.Observe(tracks);
            Raise(TracksUpdated, new PlaybackUpdate<IReadOnlyList<MpvTrack>>(attempt.Generation, tracks));
            Raise(TracksChanged, tracks);
        }
        void OnHost(string key, string value)
        {
            if (!IsCurrent(attempt)) return;
            Raise(VideoWindowUpdated, new PlaybackUpdate<(string Key, string Value)>(attempt.Generation, (key, value)));
            try { VideoWindowMessage?.Invoke(key, value); }
            catch (Exception error) { Log.Warn(Category, "视频窗口通知失败", error); }
        }
        control.StatusChanged += OnStatus;
        control.TracksChanged += OnTracks;
        if (attempt.Handle is IPlayerHostMessages host) host.HostMessageReceived += OnHost;
        attempt.Unsubscribe = () =>
        {
            control.StatusChanged -= OnStatus;
            control.TracksChanged -= OnTracks;
            if (attempt.Handle is IPlayerHostMessages messages) messages.HostMessageReceived -= OnHost;
        };
    }
}
