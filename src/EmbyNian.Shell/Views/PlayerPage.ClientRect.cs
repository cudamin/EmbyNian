using System.Diagnostics;
using EmbyNian.Diagnostics;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Windowing;

namespace EmbyNian.Shell.Views;

public sealed partial class PlayerPage
{
    private bool? _fullscreenWanted;
    private bool? _maximizeWanted;
    private Task _windowChange = Task.CompletedTask;
    private VideoFrameOverlay? _windowFrame;
    private int _windowChangeGeneration;

    internal Task WindowChange => _windowChange;
    internal bool FullscreenFrameVisible => _windowFrame is not null;

    /// <summary>想要全屏／退出全屏；与最大化请求互斥（后一个请求作废前一个）。</summary>
    private void RequestFullscreen(bool on)
    {
        _fullscreenWanted = on;
        _maximizeWanted = null;
        StartWindowChange();
    }

    /// <summary>想要最大化／还原；与全屏请求互斥。</summary>
    private void RequestMaximize(bool on)
    {
        _maximizeWanted = on;
        _fullscreenWanted = null;
        StartWindowChange();
    }

    private void StartWindowChange()
    {
        if (_windowChange.IsCompleted) _windowChange = ChangeWindowAsync();
    }

    /// <summary>此刻还有没有没落地的窗口状态。最大化那一支比的是形态本身（全屏优先，见 WindowForms）。</summary>
    private bool Pending(Windowing.HostWindow window) =>
        (_fullscreenWanted is { } fullscreen && window.Fullscreen != fullscreen)
        || (_maximizeWanted is { } maximized && window.Form != WindowForms.Of(false, maximized));

    private void ApplyPending(Windowing.HostWindow window)
    {
        if (_fullscreenWanted is { } fullscreen && window.Fullscreen != fullscreen)
        {
            ApplyFullscreen(fullscreen);
            return;
        }

        if (_maximizeWanted is { } maximized && window.Form != WindowForms.Of(false, maximized))
            window.ToggleMaximize();
    }

    private async Task ChangeWindowAsync()
    {
        while (_window is { } window && Pending(window))
        {
            var generation = _windowChangeGeneration;
            var held = false;
            try
            {
                if (_videoTarget.HasAttachedVisual)
                {
                    _videoTarget.HoldGeometry(true);
                    held = true;
                    var frame = await _videoTarget.CaptureFrameAsync().WaitAsync(TimeSpan.FromMilliseconds(750));
                    if (generation != _windowChangeGeneration || _window != window || !_onStage) continue;

                    if (frame is not null)
                    {
                        _windowFrame = new VideoFrameOverlay(window.Handle, frame);
                        // 变大那一趟（进全屏／最大化）先按<b>目标</b>矩形摆好，再动窗口：合成器若先吐
                        // 一拍「新几何 + 旧内容」，屏上已经是对的样子。变小那一趟缩到哪要等窗口自己算
                        // （还原矩形 + 按比例整形），所以仍旧先按当前矩形摆、改完再摆一次。
                        var growing = _fullscreenWanted == true || _maximizeWanted == true;
                        var bounds = growing
                            ? (_fullscreenWanted == true
                                ? VideoFrameOverlay.FullscreenRect(window.Handle)
                                : VideoFrameOverlay.WorkArea(window.Handle))
                            : VideoFrameOverlay.ClientRect(window.Handle);
                        _windowFrame.Show(bounds, window.Fullscreen || window.TopMost || growing);
                        VideoFrameOverlay.Flush();
                    }
                }

                ApplyPending(window);
                if (_windowFrame is { } overlay)
                    overlay.Show(VideoFrameOverlay.ClientRect(window.Handle), window.Fullscreen || window.TopMost);
                if (held)
                {
                    _videoTarget.HoldGeometry(false);
                    held = false;
                }

                if (_windowFrame is not null)
                {
                    var clock = Stopwatch.StartNew();
                    var readyAt = -1L;
                    while (clock.ElapsedMilliseconds < 750 && generation == _windowChangeGeneration && _onStage)
                    {
                        _videoTarget.RefreshPresentation();
                        var client = window.ClientSize;
                        var layout = _videoTarget.HostSize;
                        var raster = _videoTarget.RasterizationScale;
                        var content = _videoTarget.AttachedContentSize;
                        var ready = VideoPresentation.ResizeSettled(content.Width, content.Height,
                            (int)Math.Round(layout.Width * raster), (int)Math.Round(layout.Height * raster),
                            client.Width, client.Height);
                        if (!ready) readyAt = -1;
                        else if (readyAt < 0) readyAt = clock.ElapsedMilliseconds;
                        else if (clock.ElapsedMilliseconds - readyAt >= 50) break;
                        await Task.Delay(16);
                    }
                    if (generation != _windowChangeGeneration) continue;
                    await _videoTarget.CommitPresentationAsync().WaitAsync(TimeSpan.FromMilliseconds(250));
                    VideoFrameOverlay.Flush();
                }
            }
            catch (Exception error)
            {
                Log.Warn("播放器", "窗口切换保留帧不可用，回退直接切换", error);
                if (generation == _windowChangeGeneration && _window == window && _onStage)
                    ApplyPending(window);
            }
            finally
            {
                _windowFrame?.Dispose();
                _windowFrame = null;
                if (held) _videoTarget.HoldGeometry(false);
            }
        }
        _fullscreenWanted = null;
        _maximizeWanted = null;
    }

    private void CancelWindowChange()
    {
        _windowChangeGeneration++;
        _fullscreenWanted = null;
        _maximizeWanted = null;
        _windowFrame?.Dispose();
        _windowFrame = null;
        _videoTarget.HoldGeometry(false);
    }

    /// <summary>
    /// 客户区跳变（进出全屏、最大化／还原，以及退全屏时按画面比例的那次整形）的那一拍：窗口外框瞬时
    /// 落位，画面<b>当场跟着落位</b> —— 同样没有过渡。
    /// <para>
    /// 用户令 2026-09-20：「去掉集成模式下切换全屏和窗口化的画面动画，参考其他播放器正常切换就好」。
    /// 这里原先交给 <c>CompositionVideoTarget.BeginPresentationMorph</c> 起一趟 240ms 的「画面跑动」：
    /// 旧缓冲按旧客户区矩形起手，一路长到新矩形。窗口是瞬时跳变的、画面却慢慢爬过去，读起来就是
    /// 「切一下慢半拍」；现在是一次落位，摆法（为什么是按画面比例居中，见
    /// <c>CompositionVideoTarget.SnapPresentation</c> 自己的注释）。
    /// </para>
    /// <para>
    /// 控件照旧不参与任何过渡：岛布局在当拍被窗口强制同步（<c>HostWindow.PublishClientRect</c>），
    /// 控件永远以真实尺寸待在真实位置，加载环和按钮不会被旧窗口的长宽比拉扁。
    /// </para>
    /// </summary>
    private void OnClientRectTransition(NativeRect from, NativeRect to)
    {
        // 只用得上 to：落点由窗口自己报的新客户区决定；from（跳变前的矩形）在没有过渡之后就没有用处
        // 了，参数还在只是因为事件的形状是「起止两块」。
        _ = from; // 显式丢掉，免得下一个人以为漏了它。

        // 岛布局同步由 HostWindow.PublishClientRect 在发布事件的同一次调用里做过一遍；这里再做一遍
        // 就是同一拍里两次全树强制布局，白吃跳窗那一拍的时间（60fps 连拍里约一帧的停顿）。
        UpdateMaximizeGlyph();
        _videoTarget.SnapPresentation(to);
    }

    private void EnterAutoFullscreen()
    {
        if (_window is null || !ViewModel.PictureInHostWindow) return;

        // 要不要自动全屏归 Core 那一个答主（2026-09-20 归一）：设置 ＋ 这是一场真播放 ＋ 现在还没全屏。
        // 此前这句合取式在四处各写一遍，这一处是「进场淡入落定那一拍的换手」，另三处见
        // WindowForms.WantsAutoFullscreen 自己的注释。PlaybackLifecycleActive 仍然要点名：工具预览
        // （--hide-cursor / --show-osd）也走这条进场路，但它们不该把窗口跳成全屏。
        if (!WindowForms.WantsAutoFullscreen(
            ViewModel.AutoFullscreenOnPlayback, ViewModel.PlaybackLifecycleActive, _window.Form)) return;

        SetFullscreen(true);
    }
}
