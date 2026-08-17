using EmbyMpvClient.Configuration;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Emby;
using EmbyMpvClient.Infrastructure;
using EmbyMpvClient.Mpv;

namespace EmbyMpvClient.Playback;

/// <summary>Live playback state for the now-playing bar.</summary>
public readonly record struct PlaybackProgress(long PositionTicks, long RunTimeTicks, bool IsPaused, string Title)
{
    public double Fraction => RunTimeTicks > 0 ? Math.Clamp(PositionTicks / (double)RunTimeTicks, 0, 1) : 0;

    public string Clock => RunTimeTicks > 0
        ? $"{TimeFormat.Clock(PositionTicks)} / {TimeFormat.Clock(RunTimeTicks)}"
        : TimeFormat.Clock(PositionTicks);
}

/// <summary>
/// Runs one playback from start to finish and keeps the server informed while it does.
/// <para>
/// The server-facing half of playback lives here rather than in the UI, which is what v1 did:
/// there, the form that happened to start playback also owned the progress timer, so closing
/// that window stopped the reporting and Emby kept the item marked as playing forever.
/// </para>
/// </summary>
public sealed class PlaybackService(
    EmbySession session,
    AppSettings settings,
    Func<IPlaybackBackend> backendFactory,
    PlaybackPlanner planner)
{
    private const string Category = "playback";

    private readonly SemaphoreSlim _gate = new(1, 1);

    private IPlaybackHandle? _current;

    /// <summary>Fires roughly once per progress report; the UI uses it for the now-playing bar.</summary>
    public event Action<PlaybackProgress>? ProgressChanged;

    /// <summary>Fires when a playback starts and when it ends (null item).</summary>
    public event Action<EmbyItem?>? NowPlayingChanged;

    /// <summary>
    /// Fires whenever anything the player chrome draws changed — many times a second while the
    /// video runs, and never on the UI thread. Subscribers marshal.
    /// </summary>
    public event Action<PlayerStatus>? StatusChanged;

    /// <summary>Fires when mpv publishes a track list, so the pickers never have to poll for one.</summary>
    public event Action<IReadOnlyList<MpvTrack>>? TracksChanged;

    public bool IsPlaying => _current is not null;

    /// <summary>The last known player state; all defaults when nothing is playing.</summary>
    public PlayerStatus Status => (_current as IPlayerControl)?.Status ?? new PlayerStatus();

    /// <summary>
    /// True when playback can actually be driven. False for an external mpv whose control
    /// channel is switched off: it plays, but the client's chrome would be a row of dead buttons.
    /// </summary>
    public bool CanControl => _current is IPlayerControl && _current.HasControlChannel;

    public PlaybackPlanner Planner => planner;

    /// <summary>The backend is chosen per play from the settings, so switching mpv styles needs no restart.</summary>
    public string? Validate() => backendFactory().Validate();

    /// <summary>
    /// Starts playback, replacing anything already running. Returns once mpv has exited, so the
    /// caller can await the whole playback and act on the result.
    /// </summary>
    public async Task<PlaybackResult> PlayAsync(PlaybackTicket ticket, CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var connection = session.Connection ?? throw new InvalidOperationException("尚未登录 Emby");
        var request = planner.Plan(ticket, connection);
        var playSessionId = Guid.NewGuid().ToString("N");

        IPlaybackHandle? handle = null;
        try
        {
            var backend = backendFactory();
            handle = await backend.StartAsync(request, cancellationToken).ConfigureAwait(false);
            _current = handle;
            Subscribe(handle);
            NowPlayingChanged?.Invoke(ticket.Item);

            await ReportAsync("开始", client => client.ReportPlaybackStartAsync(
                Build(request, playSessionId, ticket.StartTicks, false, null), cancellationToken)).ConfigureAwait(false);

            var exit = await MonitorAsync(handle, request, playSessionId, ticket, cancellationToken).ConfigureAwait(false);
            return await FinishAsync(exit, request, playSessionId, ticket).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_current, handle)) _current = null;
            if (handle is not null)
            {
                Unsubscribe(handle);
                await handle.DisposeAsync().ConfigureAwait(false);
            }

            NowPlayingChanged?.Invoke(null);
            _gate.Release();
        }
    }

    /// <summary>
    /// Forwards the backend's live state under this service's own events, so the UI subscribes
    /// once at startup instead of re-wiring itself around every handle that comes and goes.
    /// </summary>
    private void Subscribe(IPlaybackHandle handle)
    {
        if (handle is not IPlayerControl control) return;

        control.StatusChanged += OnStatusChanged;
        control.TracksChanged += OnTracksChanged;
    }

    private void Unsubscribe(IPlaybackHandle handle)
    {
        if (handle is not IPlayerControl control) return;

        control.StatusChanged -= OnStatusChanged;
        control.TracksChanged -= OnTracksChanged;
    }

    private void OnStatusChanged(PlayerStatus status) => StatusChanged?.Invoke(status);

    private void OnTracksChanged(IReadOnlyList<MpvTrack> tracks) => TracksChanged?.Invoke(tracks);

    /// <summary>Stops whatever is playing; safe to call when nothing is.</summary>
    public async Task StopAsync()
    {
        var handle = _current;
        if (handle is null) return;

        try
        {
            await handle.StopAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "停止播放失败", error);
        }
    }

    // ---- runtime control --------------------------------------------------------

    // The calls below are the live control channel the now-playing bar uses. Every one of them
    // is best-effort: playback may end between the null check and the call, and a handle that
    // is being torn down must not be touched. All failures just leave the UI as it was.

    public async Task SetPropertyAsync(string name, object? value)
    {
        var handle = _current;
        if (handle is null) return;

        try
        {
            await handle.SetPropertyAsync(name, value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Log.Warn(Category, $"设置 mpv 属性 {name} 失败", error);
        }
    }

    public async Task<IReadOnlyList<MpvTrack>> GetTracksAsync()
    {
        var handle = _current;
        if (handle is null) return [];

        try
        {
            return await handle.GetTracksAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "读取 mpv 轨道列表失败", error);
            return [];
        }
    }

    public async Task<double?> GetNumberAsync(string name)
    {
        var handle = _current;
        if (handle is null) return null;

        try
        {
            return await handle.GetNumberAsync(name, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Log.Warn(Category, $"读取 mpv 属性 {name} 失败", error);
            return null;
        }
    }

    /// <summary>
    /// Runs one mpv command — <c>cycle pause</c>, <c>seek 10</c>, <c>frame-step</c>. This is how
    /// every button and key in the player chrome acts: mpv's own command vocabulary, so nothing
    /// in between has to grow a method per control.
    /// </summary>
    public async Task CommandAsync(params string[] arguments)
    {
        if (_current is not IPlayerControl control) return;

        try
        {
            await control.CommandAsync(arguments, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Log.Warn(Category, $"执行 mpv 命令 {string.Join(' ', arguments)} 失败", error);
        }
    }

    /// <summary>
    /// Swaps the active shader group mid-playback. <paramref name="shaders"/> are the raw
    /// <c>glsl-shaders</c> entries from the config, whose <c>~~/</c> prefix only resolves at
    /// config-parse time — which already happened — so each one is rewritten to an absolute
    /// path before being set. Null clears the list, which is what 「自动」 means when there is
    /// no startup group to fall back to.
    /// </summary>
    public async Task SetShaderGroupAsync(IReadOnlyList<string>? shaders)
    {
        if (_current is null) return;

        // The embedded backend loads shader files from the program folder; the external player
        // resolves the same entries against its own config directory.
        var root = settings.Mpv.Backend == MpvBackendKind.BuiltInLibMpv
            ? Path.Combine(AppContext.BaseDirectory, "shaders")
            : Path.GetDirectoryName(settings.Mpv.ConfigPath) ?? "";
        var value = shaders is null
            ? ""
            : string.Join(";", shaders.Select(shader => MpvListValue.Escape(ResolveShaderPath(shader, root))));

        await SetPropertyAsync("glsl-shaders", value).ConfigureAwait(false);
    }

    private static string ResolveShaderPath(string shader, string configDirectory) =>
        shader.StartsWith("~~/", StringComparison.Ordinal) && configDirectory.Length > 0
            ? Path.Combine(configDirectory, shader[3..])
            : shader;

    private async Task<PlaybackExit> MonitorAsync(
        IPlaybackHandle handle,
        PlaybackRequest request,
        string playSessionId,
        PlaybackTicket ticket,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(settings.Playback.ProgressReportIntervalSeconds, 1, 60));
        var exitTask = handle.WaitForExitAsync(cancellationToken);

        void OnPauseChanged(bool paused) => _ = ReportPauseAsync(handle, request, playSessionId, paused);

        handle.PauseChanged += OnPauseChanged;

        try
        {
            using var timer = new PeriodicTimer(interval);

            while (true)
            {
                var tick = timer.WaitForNextTickAsync(cancellationToken).AsTask();
                if (await Task.WhenAny(exitTask, tick).ConfigureAwait(false) == exitTask) break;

                var position = await handle.GetPositionAsync(cancellationToken).ConfigureAwait(false);
                if (position is null) continue;

                var ticks = TimeFormat.ToTicks(position.Value);
                ProgressChanged?.Invoke(new PlaybackProgress(ticks, request.RunTimeTicks, handle.IsPaused, request.Title));

                await ReportAsync("进度", client => client.ReportPlaybackProgressAsync(
                    Build(request, playSessionId, ticks, handle.IsPaused, "timeupdate"), cancellationToken))
                    .ConfigureAwait(false);
            }

            return await exitTask.ConfigureAwait(false);
        }
        finally
        {
            handle.PauseChanged -= OnPauseChanged;
        }
    }

    private async Task ReportPauseAsync(IPlaybackHandle handle, PlaybackRequest request, string playSessionId, bool paused)
    {
        // Reported immediately rather than on the next tick: a pause the server learns about
        // five seconds late shows up as five seconds of phantom playback on other clients.
        var position = await handle.GetPositionAsync(CancellationToken.None).ConfigureAwait(false);
        var ticks = TimeFormat.ToTicks(position ?? 0);

        ProgressChanged?.Invoke(new PlaybackProgress(ticks, request.RunTimeTicks, paused, request.Title));

        await ReportAsync(paused ? "暂停" : "继续", client => client.ReportPlaybackProgressAsync(
            Build(request, playSessionId, ticks, paused, paused ? "pause" : "unpause"), CancellationToken.None))
            .ConfigureAwait(false);
    }

    private async Task<PlaybackResult> FinishAsync(
        PlaybackExit exit,
        PlaybackRequest request,
        string playSessionId,
        PlaybackTicket ticket)
    {
        var positionTicks = ResolveFinalPosition(exit, request, ticket);
        var watched = ShouldMarkWatched(exit, request, positionTicks);

        var report = Build(request, playSessionId, positionTicks, false, null);
        report.Failed = exit.IsFailure;
        var reported = await ReportAsync("停止", client =>
            client.ReportPlaybackStoppedAsync(report, CancellationToken.None)).ConfigureAwait(false);

        if (watched)
        {
            await ReportAsync("标记已观看", client =>
                client.MarkPlayedAsync(request.ItemId, CancellationToken.None)).ConfigureAwait(false);
        }

        var result = new PlaybackResult(exit, positionTicks, watched, reported);
        Log.Info(Category, $"《{request.Title}》{result.ToChinese()}，位置 {TimeFormat.Clock(positionTicks)}");
        return result;
    }

    /// <summary>
    /// Reaching the end of the file means the runtime, not whatever the last poll happened to
    /// see: mpv stops answering time-pos before it exits, so the final poll is usually a second
    /// or two short and the item would come back as "98% watched".
    /// </summary>
    private static long ResolveFinalPosition(PlaybackExit exit, PlaybackRequest request, PlaybackTicket ticket)
    {
        if (exit.Reason == PlaybackEndReason.EndOfFile && request.RunTimeTicks > 0) return request.RunTimeTicks;
        if (exit.PositionSeconds is { } seconds and > 0) return TimeFormat.ToTicks(seconds);
        return ticket.StartTicks;
    }

    private bool ShouldMarkWatched(PlaybackExit exit, PlaybackRequest request, long positionTicks)
    {
        if (!settings.Playback.ReportProgressToServer) return false;
        if (exit.Reason == PlaybackEndReason.EndOfFile) return true;

        // Without a control channel there is no position to judge by, and mpv's exit code says
        // nothing about how much was watched. Guessing from elapsed wall-clock time would mark
        // a file watched because it was left paused, so nothing is marked at all.
        if (exit.PositionSeconds is null || request.RunTimeTicks <= 0) return false;

        var threshold = Math.Clamp(settings.Playback.MarkWatchedPercent, 50, 100) / 100d;
        return positionTicks >= request.RunTimeTicks * threshold;
    }

    private static PlaybackReport Build(PlaybackRequest request, string playSessionId, long positionTicks, bool paused, string? eventName) =>
        new()
        {
            ItemId = request.ItemId,
            MediaSourceId = request.MediaSourceId,
            PlaySessionId = playSessionId,
            PositionTicks = positionTicks,
            IsPaused = paused,
            EventName = eventName,
            AudioStreamIndex = request.AudioStreamIndex,
            SubtitleStreamIndex = request.SubtitleStreamIndex
        };

    /// <summary>
    /// A failed report must never interrupt playback: the file is already on screen, and the
    /// server catching up late is far better than an error dialog over the video.
    /// </summary>
    private async Task<bool> ReportAsync(string what, Func<EmbyClient, Task> report)
    {
        if (!settings.Playback.ReportProgressToServer) return false;

        try
        {
            await session.ExecuteAsync((client, _) => report(client), CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            Log.Warn(Category, $"向服务器上报「{what}」失败", error);
            return false;
        }
    }
}
