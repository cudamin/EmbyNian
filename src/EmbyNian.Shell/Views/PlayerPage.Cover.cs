using System.ComponentModel;
using EmbyNian.Playback;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace EmbyNian.Shell.Views;

public sealed partial class PlayerPage
{
    private const int RevealWaitMilliseconds = 600;
    private Storyboard? _coverFade;
    private DispatcherQueueTimer? _revealTimer;
    private long _revealStarted;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.CoverUp)) return;
        if (ViewModel.CoverUp) ShowCoverPlate();
        else HideCoverPlate();
    }

    private void ShowCoverPlate()
    {
        ResetCover();
        Cover.Visibility = Visibility.Visible;
    }

    private void HideCoverPlate()
    {
        if (Cover.Visibility != Visibility.Visible) return;
        if (!_onStage || !HomeMotion.AnimationsEnabled || XamlRoot is null)
        {
            ResetCover();
            Cover.Visibility = Visibility.Collapsed;
            return;
        }

        _revealStarted = Now;
        if (_videoTarget.IsContentReady || !ViewModel.PictureInHostWindow)
        {
            FadeCover();
            return;
        }

        // ResizeBuffers 可以复用同一个 COM 指针，不能只等「交换链换了」事件。
        _revealTimer ??= CreateRevealTimer();
        _revealTimer.Start();
    }

    private DispatcherQueueTimer CreateRevealTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(32);
        timer.Tick += (_, _) =>
        {
            if (!_onStage || !Attached || ViewModel.CoverUp)
            {
                timer.Stop();
                return;
            }
            _videoTarget.RefreshPresentation();
            if (_videoTarget.IsContentReady || Now - _revealStarted >= RevealWaitMilliseconds) FadeCover();
        };
        return timer;
    }

    private void FadeCover()
    {
        _revealTimer?.Stop();
        if (_coverFade is not null) return;
        var board = new Storyboard();
        Animate(board, Cover, "Opacity", Cover.Opacity, 0, PlayerMotion.CoverMilliseconds);
        _coverFade = board;
        board.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_coverFade, board)) return;
            ResetCover();
            if (!ViewModel.CoverUp) Cover.Visibility = Visibility.Collapsed;
        };
        board.Begin();
    }

    private void CompleteCoverExit()
    {
        ResetCover();
        Cover.Visibility = Visibility.Collapsed;
    }

    private void ResetCover()
    {
        _revealTimer?.Stop();
        var board = _coverFade;
        _coverFade = null;
        board?.Stop();
        Cover.Opacity = 1;
    }
}
