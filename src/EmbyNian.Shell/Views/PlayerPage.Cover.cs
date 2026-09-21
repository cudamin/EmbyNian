using System.ComponentModel;
using EmbyNian.Diagnostics;
using EmbyNian.Playback;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace EmbyNian.Shell.Views;

public sealed partial class PlayerPage
{
    /// <summary>
    /// 遮罩最多等多久。**这是网，不是判据** —— 正常的等待由「mpv 说首帧已经交给视频输出」＋「那条交换链
    /// 真的被押过帧」结束（见 <see cref="PictureReveal"/>）。实测本地素材 485ms、走网络转码那一档是秒级，
    /// 所以这个数必须比任何真实起播都长，否则它自己就成了新的黑屏来源（旧值 600ms 正是如此：慢起播时它先
    /// 到点，遮罩照样在最黑的一刻揭开）。到点还没画面时揭开的是黑底，那比一直转圈更像「坏了」，所以同时
    /// 记一条 warn —— 别让它悄悄发生。
    /// </summary>
    private const int RevealCeilingMilliseconds = 6000;

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
        // 又要从头等一次首帧。会话交界那一刀在这一拍砍，而不是等挂链/换会话 —— 理由在
        // CompositionVideoTarget.BeginPictureWait 上（旧会话交还交换链那一下可以被合并掉）。
        _videoTarget.BeginPictureWait();
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
        if (PictureReady)
        {
            FadeCover();
            return;
        }

        // ResizeBuffers 可以复用同一个 COM 指针，不能只等「交换链换了」事件 —— 每拍都问一次
        // 「屏幕底下有真画面了吗」，那一位同时看着 mpv 的话与这条链的 Present 账。
        _revealTimer ??= CreateRevealTimer();
        _revealTimer.Start();
    }

    /// <summary>
    /// 遮罩底下真的有画面了没有。集成那一档是画面宿主的答（<see cref="CompositionVideoTarget.HasPicture"/>），
    /// 独占那一档 mpv 把画面放在自己的顶层窗口里，宿主那一问自动短路成「mpv 说开始了没有」。
    /// <para>
    /// 集成还要再加一道「缓冲已经追上宿主」（<c>IsContentReady</c>）：链挂在旧尺寸上的那 110~150ms 里揭，
    /// 看到的是「正片缩在左上角一小块」（2026-09-18 用户截图）。
    /// </para>
    /// </summary>
    private bool PictureReady => _videoTarget.HasPicture
        && (!ViewModel.PictureInHostWindow || _videoTarget.IsContentReady);

    /// <summary>
    /// mpv 说这一场播放真的开始了（它的 <c>playback-restart</c>）。转交给画面宿主 —— 它把这句话与「这条链
    /// 被押过帧」合成成 <see cref="CompositionVideoTarget.HasPicture"/>；遮罩若正等着，当场再看一眼
    /// （否则最多多等一个计时器周期）。
    /// </summary>
    private void NotePictureStarted()
    {
        _videoTarget.NotePlaybackStarted();
        if (Cover.Visibility == Visibility.Visible && !ViewModel.CoverUp && _onStage) HideCoverPlate();
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
            if (PictureReady)
            {
                FadeCover();
                return;
            }

            var waited = Now - _revealStarted;
            if (waited < RevealCeilingMilliseconds) return;

            // 这一刻两份证据都没来：mpv 没说过首帧上屏（它挂了、或者这一版根本没视频轨却没走
            // 「没链」那条短路）。揭开的是黑底，所以留一条痕 —— 常态播放里这句话一次都不该出现。
            Log.Warn(Category, $"等画面超过 {RevealCeilingMilliseconds}ms，遮罩照揭（底下的链尚无首帧）："
                + $"已挂链={_videoTarget.HasAttachedVisual}，缓冲={_videoTarget.AttachedContentSize}，"
                + $"宿主={_videoTarget.Size}，本窗口={ViewModel.PictureInHostWindow}");
            FadeCover();
        };
        return timer;
    }

    private void FadeCover()
    {
        _revealTimer?.Stop();
        if (_coverFade is not null) return;
        Log.Debug(Category, $"遮罩揭开：从收起遮罩起等了 {Now - _revealStarted}ms"
            + $"（首帧信号={_videoTarget.HasPicture}，缓冲已追上宿主={_videoTarget.IsContentReady}）");
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
