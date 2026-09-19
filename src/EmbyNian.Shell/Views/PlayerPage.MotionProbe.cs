using Microsoft.UI.Xaml;

namespace EmbyNian.Shell.Views;

public sealed partial class PlayerPage
{
    internal void BeginMotionProbe(bool fullscreen)
    {
        EnterPlayer();
        _ticker.Stop();
        if (fullscreen) SetFullscreen(true);
    }

    internal void EndMotionProbe() => LeavePlayer();

    internal (bool Active, bool Animating, bool Identity, bool CoverHidden, bool SurfaceReady) MotionProbeState =>
        (_onStage, _poseDriver is not null,
            Opacity == 1 && PageTransform.ScaleX == 1 && PageTransform.ScaleY == 1
                && PageTransform.TranslateY == 0,
            Cover.Visibility == Visibility.Collapsed, _videoTarget.IsContentReady);
}
