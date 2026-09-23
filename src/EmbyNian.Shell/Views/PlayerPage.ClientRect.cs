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
    private int _resizeGeneration;
    private VideoFrameOverlay? _resizeFrame;
    private Task _resizeChange = Task.CompletedTask;

    internal Task ResizeChange => _resizeChange;
    internal bool ResizeFrameVisible => _resizeFrame is not null;

    /// <summary>
    /// 缓冲与布局追上客户区后，仍给合成器一小段提交时间；描述符更新不等于新画面已上屏。
    /// </summary>
    private const int HandoffSettleMilliseconds = 120;

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
        if (!_windowChange.IsCompleted) return;
        CancelResizeChange();
        _windowChange = ChangeWindowAsync();
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
            var resume = false;
            try
            {
                // 先把播放冻住，再抓帧。
                //
                // 用户 2026-09-22：「可以想办法在切全屏和窗口化的时候暂停播放，和截图无缝衔接嘛」。
                // 实测（`work/probe-pause-resize.py`，无窗口对照：播放中 vs 暂停中，各把 composition 尺寸
                // 从 1280x720 改成 1920x1080）：**暂停之后 mpv 照样在 100ms 内按新尺寸重建了缓冲**
                // （描述符 1280x720 -> 1920x1080，与播放中同速），而押帧计数 `120 -> 121` 之后就静止 ——
                // 播放中那一边是每 100ms 涨三帧。两件事一次拿到：等待判据（`ResizeSettled`）到得了，
                // 且抓到的这一帧与撤掉保留帧时屏上那一帧**同帧**。
                // 「播放中抓帧」给不了后一件：覆盖层每多盖一毫秒，撤掉时就多跳一毫秒的内容 ——
                // 用户看到的那一下「退回」有它一半。
                resume = await FreezeForHandoffAsync();

                if (_videoTarget.HasAttachedVisual && Cover.Visibility != Microsoft.UI.Xaml.Visibility.Visible)
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
                    else
                    {
                        // 取不到帧＝这一趟没有任何东西替画面挡着，合成器那一拍「新几何 + 旧内容」会原样
                        // 露出来。2026-09-22 之前这里是静默的，于是「切换时闪一下」在日志里分不出是
                        // 「抓帧没成」还是「盖得不够久」—— 这两件事的修法完全不同，得先能分开。
                        Log.Warn("播放器", "窗口切换取不到帧，这一趟没有保留帧遮挡");
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

                if (_windowFrame is not null || Cover.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
                {
                    var covered = _windowFrame is not null;
                    var clock = Stopwatch.StartNew();
                    var settledAt = -1L;
                    var content = _videoTarget.AttachedContentSize;
                    var layoutWidth = 0;
                    var layoutHeight = 0;
                    var clientWidth = 0;
                    var clientHeight = 0;

                    // 缓冲、布局和合成提交各自异步完成，保留帧必须盖住这段交接。
                    while (clock.ElapsedMilliseconds < 750 && generation == _windowChangeGeneration && _onStage)
                    {
                        _videoTarget.RefreshPresentation();
                        var client = window.ClientSize;
                        var layout = _videoTarget.HostSize;
                        var raster = _videoTarget.RasterizationScale;
                        content = _videoTarget.AttachedContentSize;
                        layoutWidth = (int)Math.Round(layout.Width * raster);
                        layoutHeight = (int)Math.Round(layout.Height * raster);
                        clientWidth = client.Width;
                        clientHeight = client.Height;
                        var settled = _windowFrame is null && !_videoTarget.HasAttachedVisual
                            ? VideoPresentation.Matches(layoutWidth, layoutHeight, clientWidth, clientHeight)
                            : VideoPresentation.ResizeSettled(content.Width, content.Height,
                                layoutWidth, layoutHeight, clientWidth, clientHeight);

                        if (!settled) settledAt = -1;
                        else if (settledAt < 0) settledAt = clock.ElapsedMilliseconds;
                        var ready = settled
                            && clock.ElapsedMilliseconds - settledAt >= HandoffSettleMilliseconds;
                        if (ready) break;
                        await Task.Delay(16);
                    }
                    if (generation != _windowChangeGeneration) continue;
                    await _videoTarget.CommitPresentationAsync().WaitAsync(TimeSpan.FromMilliseconds(250));
                    VideoFrameOverlay.Flush();
                    if (covered)
                        Log.Debug("播放器", $"窗口切换保留帧：盖了 {clock.ElapsedMilliseconds}ms"
                            + $"（缓冲 {content.Width}x{content.Height}、布局 {layoutWidth}x{layoutHeight}、"
                            + $"客户区 {clientWidth}x{clientHeight}）");
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

                // 替用户把播放放开 —— 但要等**整趟事务**走完，不是这一趟走完。快速反向（连点全屏／
                // 窗口化）会连着跑好几趟，每趟收尾都放开的话，下一趟抓到的就是在动的帧、白丢一次
                // 「同帧」，声音还会跟着「断-续-断-续」。`Pending` 还立着就继续冻着。
                // 静默锚在这里再推一次：mpv 把 `pause=false` 报回来还要一程，那一下同样不是用户按的
                //（见 `PlayerPage.Muted` 与 `HandoffPulseMuteMilliseconds`）。
                if (resume && !Pending(window))
                {
                    _handoffMutedAt = Now;
                    ViewModel.SetPaused(false);
                }
            }
        }
        _fullscreenWanted = null;
        _maximizeWanted = null;
    }

    /// <summary>
    /// 切换窗口这一趟先把播放冻住；返回「这一趟要不要替用户把播放放开」。
    /// <para>
    /// 用户 2026-09-22 的原话是「可以想办法在切全屏和窗口化的时候暂停播放，和截图无缝衔接嘛」。
    /// 两件事靠它一起成立：抓到的帧是**静止**的那一帧（撤掉保留帧时屏上还是同一帧，中间不跳内容），
    /// 而 mpv 在暂停态下照样会按新的 composition 尺寸重建缓冲（实测 100ms，见
    /// <c>work/probe-pause-resize.py</c>），所以「等画面就绪」那条等待仍然到得了 ——
    /// 这一点是动手前专门量过的，量不下来这个方案就只会把 750ms 保险丝吃满。
    /// </para>
    /// <para>
    /// <b>用户自己暂停着的时候一根手指都不碰</b>：那时返回 false，既不改它的暂停、收尾也不会替它放开。
    /// 只有「本来在放、被我们冻住」才记着这一笔要在收尾还回去。
    /// </para>
    /// </summary>
    private async Task<bool> FreezeForHandoffAsync()
    {
        if (ViewModel.Paused) return false;

        _handoffMutedAt = Now;
        ViewModel.SetPaused(true);

        // 等它真的停住再抓帧 —— `SetPaused` 是不等结果的那一路（`SetPropertyAsync` 发完就丢）。
        // 上限只给 12 拍（约 190ms）：真等不到也照走，坏处不过是抓到一帧在动的画面，
        // 不至于把用户按下去的那一下拖住。
        for (var step = 0; step < 12 && !ViewModel.Paused; step++)
            await Task.Delay(16);

        return true;
    }

    private void CancelWindowChange()
    {
        _windowChangeGeneration++;
        _startupHandoverPending = false;
        _fullscreenWanted = null;
        _maximizeWanted = null;
        _windowFrame?.Dispose();
        _windowFrame = null;
        CancelResizeChange();
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

    /// <summary>
    /// 连续调整窗口时，窗口自己报出来的真实客户区（<c>WM_SIZE</c> 那一拍，早于 XAML 的 <c>SizeChanged</c>）。
    /// <para>
    /// <b>只把客户区交给画面层，不在这里手动排树</b>（2026-09-22 定）。这里一度试过对页面、Root、VideoHost
    /// 各来一次 Measure/Arrange，想让 XAML 布局追平 <c>WM_SIZE</c> —— 实测无效，而且本来就不必：
    /// 岛的尺寸在这一拍已经是新的（探针实测 <c>island=1048,589</c> 与客户区同步），落后的是那棵树自己的布局，
    /// 它晚一拍（16ms）就追上了，肉眼看不见，也没有任何东西依赖它 —— 画面的摆放读的是窗口报出来的客户区
    /// （<see cref="Windowing.CompositionVideoTarget.ResizePresentation"/> 里钉住的 <c>_snappedClient</c>），
    /// 不吃这一拍。反过来，每次 <c>WM_SIZE</c> 都全树排两遍，拖动时是白吃的开销。
    /// </para>
    /// </summary>
    private void OnClientSizeChanged(NativeRect client)
    {
        if (!_onStage || !ViewModel.PictureInHostWindow || _window is null) return;
        _videoTarget.ResizePresentation(client);
        _resizeFrame?.Show(client, _window.Fullscreen || _window.TopMost);
    }

    private void OnInteractiveResizeChanged(bool active)
    {
        if (active)
        {
            if (!_onStage || !ViewModel.PictureInHostWindow) return;
            _resizeGeneration++;
            _resizeFrame?.Dispose();
            _resizeFrame = null;
            _videoTarget.SetInteractiveResize(true);
        }
        else if (_videoTarget.IsInteractiveResize && _window is { } window)
        {
            _resizeChange = FinishResizeAsync(window, ++_resizeGeneration);
        }
    }

    /// <summary>
    /// ResizeBuffers 与合成提交不是同一事务。松手时保留画面到新缓冲上屏，
    /// 否则会短暂用新尺寸裁旧像素；不暂停视频或音频，也不在整段拖动中抓帧。
    /// </summary>
    private async Task FinishResizeAsync(HostWindow window, int generation)
    {
        if (_videoTarget.IsContentReady)
        {
            _videoTarget.SetInteractiveResize(false);
            return;
        }
        VideoFrameOverlay? overlay = null;
        bool Current() => generation == _resizeGeneration && _window == window && _onStage;
        try
        {
            if (_videoTarget.HasAttachedVisual && Cover.Visibility != Microsoft.UI.Xaml.Visibility.Visible)
            {
                var frame = await _videoTarget.CaptureFrameAsync().WaitAsync(TimeSpan.FromMilliseconds(350));
                if (!Current()) return;
                if (frame is not null)
                {
                    overlay = new VideoFrameOverlay(window.Handle, frame);
                    _resizeFrame = overlay;
                    overlay.Show(VideoFrameOverlay.ClientRect(window.Handle), window.Fullscreen || window.TopMost);
                    VideoFrameOverlay.Flush();
                }
            }

            _videoTarget.SetInteractiveResize(false);
            var watch = Stopwatch.StartNew();
            var settledAt = -1L;
            while (Current() && watch.ElapsedMilliseconds < 750)
            {
                _videoTarget.RefreshPresentation();
                if (!_videoTarget.IsContentReady) settledAt = -1;
                else if (settledAt < 0) settledAt = watch.ElapsedMilliseconds;
                if (settledAt >= 0 && watch.ElapsedMilliseconds - settledAt >= HandoffSettleMilliseconds) break;
                await Task.Delay(16);
            }
            if (!Current()) return;
            await _videoTarget.CommitPresentationAsync().WaitAsync(TimeSpan.FromMilliseconds(250));
            VideoFrameOverlay.Flush();
        }
        catch (Exception error)
        {
            Log.Warn("播放器", "窗口缩放收尾保留帧不可用，回退直接更新", error);
        }
        finally
        {
            if (Current()) _videoTarget.SetInteractiveResize(false);
            if (ReferenceEquals(_resizeFrame, overlay)) _resizeFrame = null;
            overlay?.Dispose();
        }
    }

    private void CancelResizeChange()
    {
        _resizeGeneration++;
        _resizeFrame?.Dispose();
        _resizeFrame = null;
        _videoTarget.SetInteractiveResize(false);
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
