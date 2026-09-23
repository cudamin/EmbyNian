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

    /// <summary>
    /// 用户正压在窗口边沿（或标题上）那个模态回环里 —— <c>WM_ENTERSIZEMOVE</c> 到 <c>WM_EXITSIZEMOVE</c>。
    /// <para>
    /// <b>它不是「尺寸正在变」的判据，而是「这会儿的尺寸变化是手拖出来的」那一扇门。</b>
    /// 拖标题移动窗口（<c>WM_ENTERSIZEMOVE</c> 一样会来，可客户区尺寸根本没变）、进全屏／最大化、
    /// 播放开始时按画面比例整形、程序化改窗口尺寸，都会改客户区；只有这一扇门里的那几次才该冻结播放 ——
    /// 冻结那笔账的放行口是拖边收尾，门外的尺寸变化没有人会来放开它（见 <see cref="BeginResizeFreeze"/>）。
    /// </para>
    /// </summary>
    private bool _resizeLoop;

    /// <summary>
    /// 拖边这一趟<b>是我们把播放冻住的</b>（还欠用户一次放开）——「松手之后按设定放开」这笔账就记在这一位上。
    /// 只有 <see cref="BeginResizeFreeze"/> 会立起它，只有 <see cref="ReleaseResizeFreezeAsync"/> 会收掉它。
    /// </summary>
    private bool _resizePaused;

    /// <summary>
    /// 那一下 <c>pause=yes</c> 的任务；放开之前先等它落地，免得 <c>pause=no</c> 跑在它前面、
    /// 净结果成了「停着」而没有人再来放开（见 <see cref="ReleaseResizeFreezeAsync"/>）。
    /// </summary>
    private Task _resizeFreeze = Task.CompletedTask;

    internal Task ResizeChange => _resizeChange;
    internal bool ResizeFrameVisible => _resizeFrame is not null;

    /// <summary>拖边这一趟的冻结账立着没有（探针读它，用户看不见）。</summary>
    internal bool ResizeFrozen => _resizePaused;

    /// <summary>
    /// 探针用的出口：把「冻住播放／放开」这一句接到哪儿去。
    /// <para>
    /// 真实那条路是 <see cref="PlayerViewModel.SetPaused"/>（→ <c>PlaybackService</c> → 当前那个 mpv 句柄），
    /// 也就是空格键与「轻点画面」走的那一句。<b>探针那一头绕过 PlaybackService 直接 new 了后端</b>
    /// （<c>PlayerMotionProbe</c> 要的是「不登录、不起真实播放」），于是 <c>PlaybackService._current</c> 是空的 ——
    /// 同一句在探针里会静静落进空气：拖动期间 mpv 照旧在放，`--probe-player-motion` 那几条新判据就什么都验不到
    /// （2026-09-23 实测：位置一路走掉了 24 拍）。接上这个出口，探针把它接到自己那个真实句柄上。
    /// 与 <c>ProbePictureStarted</c>／<c>ProbeSourceAspect</c> 是同一种做法：把真实那条路喂不到的东西递进去。
    /// </para>
    /// <para>
    /// <b>只管拖边那一趟。</b>窗口切换那一趟（<see cref="FreezeForHandoffAsync"/>／<c>ChangeWindowAsync</c>）
    /// 仍旧直连 <see cref="PlayerViewModel.SetPaused"/> —— 探针的窗口切换腿读的是几何与保留帧，不经过这里，
    /// 接上去反而会把那几腿的时序改掉。
    /// </para>
    /// </summary>
    internal Action<bool>? PauseRequested { get; set; }

    /// <summary>拖边这一趟发「暂停／恢复」的唯一出口。没接出口就是真实那条路（见 <see cref="PauseRequested"/>）。</summary>
    private void SetPaused(bool paused)
    {
        if (PauseRequested is { } sink)
        {
            sink(paused);
            return;
        }

        ViewModel.SetPaused(paused);
    }

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

        // 尺寸真的开始变了 —— **调整的「开始」在这一拍，不在 WM_ENTERSIZEMOVE**。
        //
        // 用户令 2026-09-23：「当检测到窗口大小调整时自动暂停 mpv 播放器」。这一拍（WM_SIZE）是整条路上
        // 唯一能证明「客户区真的变了」的地方；而 WM_ENTERSIZEMOVE 拖标题移动窗口时照样会来，拿它当触发点
        // 等于每次挪窗都冻一次、松手再放开 —— 一次纯粹的位置调整会平白断一下声音、断一下画面。
        if (_resizeLoop) BeginResizeFreeze();

        _videoTarget.ResizePresentation(client);
        _resizeFrame?.Show(client, _window.Fullscreen || _window.TopMost);
    }

    /// <summary>
    /// 一次拖动事务的开始与结束（<c>WM_ENTERSIZEMOVE</c> / <c>WM_EXITSIZEMOVE</c>）—— <b>几何那一半</b>。
    /// <para>
    /// 播放那一半（冻结与放开）不在这两个端点上：开始要等客户区真的报出新尺寸
    /// （<see cref="BeginResizeFreeze"/> 挂在 <see cref="OnClientSizeChanged"/> 上），放开的动作要等保留帧
    /// 撤掉之后（<see cref="FinishResizeAsync"/>）。理由都在那两处。
    /// </para>
    /// </summary>
    private void OnInteractiveResizeChanged(bool active)
    {
        if (active)
        {
            _resizeLoop = true;
            if (!_onStage || !ViewModel.PictureInHostWindow) return;
            _resizeGeneration++;
            _resizeFrame?.Dispose();
            _resizeFrame = null;
            _videoTarget.SetInteractiveResize(true);
        }
        else
        {
            _resizeLoop = false;
            // 松手之后有两条路，两条都管住那笔冻结账：有几何收尾的走 FinishResizeAsync（放开排在保留帧
            // 撤掉之后 —— 早一步放开，盖着的那帧还停在旧内容上而声音已经走过去了，撤掉时画面会往前跳），
            // 没有几何收尾的（这一趟没挂链）就当场放开。
            if (_videoTarget.IsInteractiveResize && _window is { } window)
                _resizeChange = FinishResizeAsync(window, ++_resizeGeneration);
            else
                _resizeChange = ReleaseResizeFreezeAsync();
        }
    }

    /// <summary>
    /// ResizeBuffers 与合成提交不是同一事务。松手时保留画面到新缓冲上屏，
    /// 否则会短暂用新尺寸裁旧像素。
    /// <para>
    /// <b>2026-09-23 起这一趟也管着「放开播放」。</b>拖动那一段 mpv 已经被冻住（见 <see cref="BeginResizeFreeze"/>），
    /// 放开的动作排在这里、而且排在保留帧撤掉之后：要是松手就放开，覆盖层盖的是那张静止帧而声音已经走过去了，
    /// 撤掉时画面会往前跳那一段 —— 与切全屏那一趟同一条理由（见 <see cref="FreezeForHandoffAsync"/>）。
    /// </para>
    /// </summary>
    private async Task FinishResizeAsync(HostWindow window, int generation)
    {
        if (_videoTarget.IsContentReady)
        {
            // 缓冲在松手之前就追上了（这一趟没怎么改尺寸）：没有覆盖层可撤，放开当场做。
            _videoTarget.SetInteractiveResize(false);
            await ReleaseResizeFreezeAsync();
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

        // 覆盖层已经撤掉，屏上那一帧就是接下来要接着放的那一帧 —— 这时候才谈放开。
        // `Current()` 为假＝这一趟被下一轮拖动（或取消）接手了：那笔冻结账归接手的那一趟，由它收尾时放开。
        if (Current()) await ReleaseResizeFreezeAsync();
    }

    /// <summary>
    /// 拖边真的开始改尺寸的那一拍：把播放冻住（用户令 2026-09-23，判据在
    /// <see cref="ResizeFreeze.Freezes"/>）。
    /// <para>
    /// <b>为什么冻的是 mpv 而不是别的。</b>集成模式里这扇窗是应用自己的，mpv 在进程内、没有自己的窗口，
    /// 「暂停播放器」就是往它发 <c>pause=yes</c>：画面与声音一起停住，而它照样会按新的 composition 尺寸重建
    /// 缓冲（实测 100ms，见 <c>work/probe-pause-resize.py</c>）—— 收尾那条「等画面就绪」因此仍然到得了，
    /// 覆盖层上那帧与撤掉时屏上那帧也才是同一帧。
    /// </para>
    /// <para>
    /// 一进这个模态回环只冻一次：<see cref="OnClientSizeChanged"/> 在拖动里每几毫秒就被叫一次，
    /// 这道守卫是它不重复发命令的唯一原因。用户自己暂停着的时候<b>一根手指都不碰</b>
    /// （<see cref="ResizeFreeze.Freezes"/> 的第一问），那时这一位也不会立起来，收尾自然也不会替他放开。
    /// </para>
    /// </summary>
    private void BeginResizeFreeze()
    {
        if (_resizePaused) return;
        if (!ResizeFreeze.Freezes(ViewModel.Paused, _onStage, ViewModel.PictureInHostWindow)) return;

        _resizePaused = true;
        _resizeFreeze = FreezeForResizeAsync();
    }

    private async Task FreezeForResizeAsync()
    {
        // 与抓帧那一趟同一笔账：暂停徽标不该冒出来（见 PlayerPage.Muted 与 HandoffPulseMuteMilliseconds）。
        _handoffMutedAt = Now;
        SetPaused(true);

        // 等它真的停住才让收尾去抓帧 —— 发号那一句是不等结果的那一路（`SetPropertyAsync` 发完就丢）。
        // 上限只给 12 拍（约 190ms）：真等不到也照走，坏处不过是覆盖层上那帧还在动，
        // 不至于把用户松手那一下拖住。
        for (var step = 0; step < 12 && !ViewModel.Paused; step++)
            await Task.Delay(16);
    }

    /// <summary>
    /// 松手之后按设定替用户放开（<see cref="ResizeFreeze.Resumes"/>：这一趟冻过 ∧ 设置里那一行说继续）。
    /// <para>
    /// <b>先等我们那一下 <c>pause=yes</c> 落地再发放开</b>：两条命令要是次序倒了，净结果就是「停着」，
    /// 而此后没有任何人会再来放开它 —— 那正是这条路上唯一一个不可接受的坏法。等错方向不花钱：
    /// 那时它顶多多停几十毫秒，而这段本来就被覆盖层盖着。
    /// </para>
    /// <para>
    /// 放开之后还要等状态真的翻过来才报「收尾完了」，因为读它的人（探针、自检）要的是屏上的事实，
    /// 而 mpv 把 <c>pause</c> 报回来还隔着一次往返。
    /// </para>
    /// </summary>
    private async Task ReleaseResizeFreezeAsync()
    {
        var froze = _resizePaused;
        _resizePaused = false;
        if (!ResizeFreeze.Resumes(froze, ViewModel.ResumeAfterWindowResize)) return;

        await _resizeFreeze;

        _handoffMutedAt = Now;
        SetPaused(false);
        for (var step = 0; step < 12 && ViewModel.Paused; step++)
            await Task.Delay(16);
    }

    private void CancelResizeChange()
    {
        _resizeGeneration++;
        _resizeLoop = false;
        _resizeFrame?.Dispose();
        _resizeFrame = null;
        _videoTarget.SetInteractiveResize(false);

        // 冻结那笔账不能丢给下一趟：窗口切换事务只看得见「现在停着」，看不出这是谁按的 ——
        // 一笔没人还的账就是「画面从此不再动」。这一句不等它落地（这里是同步返回），紧接着的窗口切换
        // 事务会自己决定要不要再冻一次；最坏也只是那一趟少冻一下（它把这次放开误读成「用户暂停着」，
        // 于是跳过冻结，而放开已经发出去了，播放照常回来）。
        _ = ReleaseResizeFreezeAsync();
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
