namespace EmbyNian.Playback;

/// <summary>
/// 拖动窗口边沿改大小这一趟：要不要冻住 mpv、松手之后要不要替用户放开（用户令 2026-09-23）。
/// <para>
/// <b>被调整的是哪一扇窗。</b>集成模式里画面合成在<b>应用自己的窗口</b>（<c>HostWindow</c>，也就是播放页手里那个
/// <c>_window</c>）的视觉树里，进程内的 mpv 没有自己的窗口 —— 所以「暂停 mpv」就是往它发 <c>pause=yes</c>，
/// 画面与声音一起停住，而尺寸事件（<c>WM_ENTERSIZEMOVE</c> / <c>WM_SIZE</c> / <c>WM_EXITSIZEMOVE</c>）也全来自
/// 这扇窗。独占模式（以及外部 mpv.exe）的画面在 mpv 自建的顶层窗口里，用户拖的是它那扇窗，
/// <c>PlayerViewModel.PictureInHostWindow</c> 为假 —— 这一整条路都不参与，那一问就是
/// <see cref="Freezes"/> 里第三个参数。
/// </para>
/// <para>
/// <b>为什么开始那一下要冻。</b>拖动这一段故意不给 mpv 新尺寸（<c>CompositionVideoTarget</c> 把交给 mpv 的
/// composition 尺寸按在旧值上，画面靠合成变换拉伸着跟窗口走），松手之后要等 mpv 重建缓冲，而那一小段本来由一张
/// 静止帧盖着（<c>PlayerPage.FinishResizeAsync</c> 的 <c>VideoFrameOverlay</c>）。让 mpv 在拖动时就停住，
/// 盖上那帧与撤掉时屏上那帧是同一帧，中间不跳内容 —— 与 2026-09-22 切全屏那一趟同一条理由
/// （<c>PlayerPage.FreezeForHandoffAsync</c>）。
/// </para>
/// <para>
/// <b>实测依据</b>（<c>work/probe-pause-resize.py</c>，无窗口对照：播放中 vs 暂停中各改一次 composition 尺寸）：
/// 暂停之后 mpv 照样在 <b>100ms</b> 内按新尺寸重建了缓冲（与播放中同速），而且只押一帧就静止 —— 所以
/// 「等画面就绪」那条等待到得了，「同帧」也成立。这一点是动手前量过的，量不下来这个方案只会把 750ms 的保险丝吃满。
/// </para>
/// <para>
/// <b>两个容易写反的地方。</b>①用户<b>自己</b>按下的暂停一根手指都不碰：他要的就是停着，我们不许把它
/// 「恢复」掉 —— 这是 <see cref="Freezes"/> 的第一问。②「放开」只对<b>真的冻过</b>的那一趟答应（那笔账记在
/// 页面自己身上：<c>PlayerPage._resizePaused</c>）—— <c>WM_ENTERSIZEMOVE</c> 在<b>拖动标题移动窗口</b>时照样会来，
/// 那种趟里客户区尺寸根本没变（<c>WM_SIZE</c> 不来，冻结因此不会开始），若照「松开就放开」办，用户自己按下的
/// 暂停会被一次单纯的挪窗悄悄解开。
/// </para>
/// </summary>
public static class ResizeFreeze
{
    /// <summary>
    /// 调整真的开始那一刻（客户区报出新尺寸的那一拍）要不要冻住 mpv。
    /// <para>
    /// 三问缺一不可：<paramref name="userPaused"/> 为假（现在停着是用户自己按的，不是我们要冻的）、
    /// <paramref name="onStage"/>（播放页真的在台上 —— 浏览时拖窗口本来也没有在播的东西）、
    /// <paramref name="pictureInHostWindow"/>（集成模式，画面确实嵌在这扇窗里）。
    /// </para>
    /// </summary>
    public static bool Freezes(bool userPaused, bool onStage, bool pictureInHostWindow) =>
        !userPaused && onStage && pictureInHostWindow;

    /// <summary>
    /// 松手之后要不要替用户放开。<paramref name="froze"/> 是「这一趟是我们冻的」，<paramref name="resumeAfterResize"/>
    /// 是设置里那一行（<c>PlaybackSettings.ResumeAfterWindowResize</c>，装机默认开）：关掉它＝松开之后停在暂停，
    /// 要用户自己按播放。
    /// </summary>
    public static bool Resumes(bool froze, bool resumeAfterResize) => froze && resumeAfterResize;
}
