using EmbyNian.Shell.Interop;

namespace EmbyNian.Shell.Views;

public sealed partial class PlayerPage
{
    /// <summary>
    /// 客户区跳变（进出全屏、最大化／还原、退出播放还原）的那一拍：窗口外框瞬时落位，画面那半由
    /// <see cref="Windowing.CompositionVideoTarget.BeginPresentationMorph"/> 接住——旧缓冲按旧客户区
    /// 矩形起手，240ms 长到新矩形。控件不参与任何过渡：岛布局在当拍被窗口强制同步，控件永远以
    /// 真实尺寸待在真实位置，加载环和按钮不再被旧窗口的长宽比拉扁。
    /// </summary>
    private void OnClientRectTransition(NativeRect from, NativeRect to)
    {
        // 岛布局同步由 HostWindow.PublishClientRect 在发布事件的同一次调用里做过一遍；这里再做一遍
        // 就是同一拍里两次全树强制布局，白吃跳窗那一拍的时间（60fps 连拍里约一帧的停顿）。
        UpdateMaximizeGlyph();
        _videoTarget.BeginPresentationMorph(from, to);
    }

    private void EnterAutoFullscreen()
    {
        if (_window is null || _window.Fullscreen || !ViewModel.AutoFullscreenOnPlayback
            || !ViewModel.PictureInHostWindow || !ViewModel.PlaybackLifecycleActive) return;
        SetFullscreen(true);
    }
}
