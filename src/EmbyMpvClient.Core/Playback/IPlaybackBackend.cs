namespace EmbyMpvClient.Playback;

/// <summary>
/// How playback actually happens. Two implementations: the in-process libmpv player that renders
/// into the client's own window, and the user's <c>mpv.exe</c> in a window of its own.
/// <para>
/// The seam is what makes both possible at once. Everything above it — the planner, the progress
/// reports, the player chrome — only ever sees a <see cref="PlaybackRequest"/> going in and an
/// <see cref="IPlaybackHandle"/> coming out, so the choice is a per-play setting rather than an
/// architectural commitment.
/// </para>
/// </summary>
public interface IPlaybackBackend
{
    string DisplayName { get; }

    /// <summary>Null when the backend can run, otherwise the reason to show the user.</summary>
    string? Validate();

    Task<IPlaybackHandle> StartAsync(PlaybackRequest request, CancellationToken cancellationToken);
}

/// <summary>One running playback.</summary>
public interface IPlaybackHandle : IAsyncDisposable
{
    /// <summary>False when no control channel came up: position and pause state are unknown.</summary>
    bool HasControlChannel { get; }

    bool IsPaused { get; }

    /// <summary>Position in seconds, or null when it cannot be known.</summary>
    Task<double?> GetPositionAsync(CancellationToken cancellationToken);

    /// <summary>Raised when mpv pauses or resumes.</summary>
    event Action<bool>? PauseChanged;

    /// <summary>Completes when playback is over, whatever ended it.</summary>
    Task<PlaybackExit> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Asks mpv to quit, falling back to killing it if it will not.</summary>
    Task StopAsync();

    /// <summary>Shows a message on mpv's OSD; silently does nothing without a control channel.</summary>
    Task ShowMessageAsync(string text);

    /// <summary>Sets an mpv property while playback runs (volume, aid, sid, glsl-shaders…).</summary>
    Task SetPropertyAsync(string name, object? value, CancellationToken cancellationToken);

    /// <summary>The current track list, or empty when it cannot be read.</summary>
    Task<IReadOnlyList<MpvTrack>> GetTracksAsync(CancellationToken cancellationToken);

    /// <summary>A numeric property (e.g. volume), or null when it cannot be read.</summary>
    Task<double?> GetNumberAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// The live half of a playback: what the player chrome needs to draw itself and the one channel
/// it needs to act. Separate from <see cref="IPlaybackHandle"/> because a backend can run a file
/// to the end without any of it — an <c>mpv.exe</c> launched with the control channel switched
/// off still plays, it just cannot be driven — so the chrome asks for this and hides the controls
/// it did not get.
/// </summary>
public interface IPlayerControl
{
    /// <summary>The last known state; never stale by more than one mpv notification.</summary>
    PlayerStatus Status { get; }

    /// <summary>Raised whenever anything the chrome draws changed. Not on the UI thread.</summary>
    event Action<PlayerStatus>? StatusChanged;

    /// <summary>
    /// Raised when mpv publishes a track list — on file load and whenever tracks are added,
    /// which is what external subtitle files do a moment after playback starts.
    /// </summary>
    event Action<IReadOnlyList<MpvTrack>>? TracksChanged;

    /// <summary>
    /// Runs one mpv command (<c>seek</c>, <c>cycle</c>, <c>frame-step</c>, <c>sub-reload</c>…).
    /// One entry point rather than a method per action: mpv's command set is the vocabulary the
    /// player already speaks, and every command it grows becomes available without a new seam.
    /// </summary>
    Task CommandAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
