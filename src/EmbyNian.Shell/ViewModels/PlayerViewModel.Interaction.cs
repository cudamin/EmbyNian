using EmbyNian.Emby;
using EmbyNian.Mpv;

namespace EmbyNian.Shell.ViewModels;

public sealed partial class PlayerViewModel
{
    private int _interactionVersion;

    internal readonly record struct InteractionContext(int Attempt, int Generation, long PlaybackGeneration, EmbyItem? Item, int Version);

    internal InteractionContext CaptureInteraction() => new(_playbackAttempt, _generation, _playback.Generation, _nowPlaying, _interactionVersion);

    internal bool IsCurrentInteraction(InteractionContext context) => !_lifetime.IsCancellationRequested
        && context.Version == _interactionVersion && context.Attempt == _playbackAttempt && context.Generation == _generation
        && context.PlaybackGeneration == _playback.Generation && ReferenceEquals(context.Item, _nowPlaying);

    private VideoMenuSnapshot<EmbyItem>? _episodeMenuSnapshot;
    private InteractionContext _episodeMenuContext;
    private VideoMenuSnapshot<MediaSource>? _versionMenuSnapshot;
    private InteractionContext _versionMenuContext;
    private VideoMenuSnapshot<PlayerMenuNode>? _pictureMenuSnapshot;
    private InteractionContext _pictureMenuContext;
    private int _pictureMenuRequest;

    private void InvalidateMenuInteractions()
    {
        _interactionVersion++;
        _episodeMenuSnapshot = null;
        _versionMenuSnapshot = null;
        _pictureMenuSnapshot = null;
        _pictureMenuRequest++;
    }

    private void SelectEpisodeMenu(string value)
    {
        if (_episodeMenuSnapshot is not { } menu || !IsCurrentInteraction(_episodeMenuContext)
            || !menu.TryResolve(value, out var episode)) return;
        _episodeMenuSnapshot = null;
        SwitchEpisode(episode);
    }

    private void SelectVersionMenu(string value)
    {
        if (_versionMenuSnapshot is not { } menu || !IsCurrentInteraction(_versionMenuContext)
            || !menu.TryResolve(value, out var source)) return;
        _versionMenuSnapshot = null;
        SwitchVersion(source);
    }

    private void SelectPictureMenu(string value)
    {
        if (_pictureMenuSnapshot is not { } menu || !IsCurrentInteraction(_pictureMenuContext)
            || !menu.TryResolve(value, out var node)) return;
        _ = RunMenuNodeAsync(node);
    }
}
