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

    /// <summary>
    /// 退场底与舞台铺到了多高，以及这一刻客户区多高 —— 专给探针判「底够不够高」用（2026-09-20 加）。
    /// <para>
    /// 为什么要有这个读数：原先「退出播放」那一段只读 <c>PlacedRect</c>/<c>HostSize</c>/<c>ExitProbeState</c>，
    /// 前者管的是<b>画面</b>那一半，后两者只回答「底有没有立着」这个是非题 —— <b>没有任何一处问过它铺了多高</b>。
    /// 「底立起来了，但只铺到窗口变高之前的高度」这种错法于是能一路全绿。
    /// </para>
    /// <para>
    /// <b>已知限度（2026-09-20 实测，务必先读再改）</b>：这条读数在<b>应用内探针里验不到那个 bug</b>。
    /// 探针的进场不走窗口（<c>BeginMotionProbe</c> 只跑页面动画），退场那几拍窗口客户区与底高恒等；
    /// 我试过在退场前先把客户区压矮、也试过按画面比例缩窗口，两次都把修复回退成用户截图那天的次序重跑，
    /// 这个计数照样是 0 —— 因为 <c>LeavePlayer</c> 里「窗口变高」与「强制布局」是<b>同一个同步块</b>里
    /// 前后两句，中间没有能让 16ms 计时器插进来的空档。真机上用户看到的那一瞬来自合成器在两次
    /// <c>SetWindowPos</c> 之间吐的一帧，它不是托管计时器能观察到的东西。
    /// </para>
    /// <para>
    /// 所以它现在的价值是<b>回归网</b>而不是<b>复现器</b>：任何一天有人把退场底改成异步立起、或者把
    /// <c>SynchronizePlaybackLayout</c> 从同步块里挪出去，这条就会当场翻红。真正的取证手段是外面那套
    /// 密抓 —— <c>work/catch-exit-frame.py</c>（PrintWindow 逐帧 + 底部底色带判据）。
    /// </para>
    /// </summary>
    internal (double BackdropHeight, double StageHeight, double WindowHeight) ExitCoverProbeState =>
        (ExitBackdrop.ActualHeight, Stage.ActualHeight, _window?.ClientSize.Height ?? 0);

    internal (bool Active, bool Animating, bool Identity, bool CoverHidden, bool SurfaceReady) MotionProbeState =>
        (_onStage, _poseDriver is not null,
            Opacity == 1 && PageTransform.ScaleX == 1 && PageTransform.ScaleY == 1
                && PageTransform.TranslateY == 0,
            Cover.Visibility == Visibility.Collapsed, _videoTarget.IsContentReady);

    /// <summary>
    /// 退场那一趟的三样读数，专给 <c>PlayerMotionProbe</c> 的「退出播放」段用。
    /// <para>
    /// 三样都是「只在退场期间才该成立」的：退场底立着、最后一帧在溶解（<c>Dim</c> 被逐拍拧上去）、
    /// 并且那一帧是<b>铺满</b>新宿主的（<c>Fill</c>）。正片播放时它们必须全是反的 —— 一个留在屏上的
    /// 退场底会盖住整趟播放，一个没归零的压暗系数会让下一部片子永远是暗的，一面开着的铺满旗会让
    /// 画面被裁掉边角。探针两处都读，所以它同时盖住「退场时立了」和「进场时收了」。
    /// </para>
    /// <para>
    /// <c>Dim</c> 是<b>过程量</b>（2026-09-20 晚重新设计后从全亮一路加到 1），探针读它一整条曲线：
    /// 只看「大于 0」是不够的 —— 旧的一步压到 0.85 也大于 0，那正是「一次硬切」的写法。
    /// </para>
    /// <para>
    /// <c>ThinLine</c> 是 2026-09-20 第四张截图补的：底边那条细进度线画在**页面**的底边上，页面淡到零
    /// 它还是满亮 —— 用户量到的是中缝上那条 3px、颜色 (224,228,234) 的白线。<c>Render()</c> 里的判据早就写了
    /// <c>!_inputSuspended</c>，可退场这条路上从来没有人调过 <c>Render()</c>，那一句等于没写；
    /// `PlayerPage.SelfCheck.Picture.cs` 的 `ProbeThinLine` 自己会调 <c>Render()</c>，所以它一直是绿的。
    /// 这个读数让**真实退场那一趟**也被看着。
    /// </para>
    /// </summary>
    internal (bool Backdrop, double Dim, bool Fill, bool ThinLine) ExitProbeState =>
        (ExitBackdrop.Visibility == Visibility.Visible, _videoTarget.RetainDim, _videoTarget.RetainFill,
            ThinLine.Visibility == Visibility.Visible);

    /// <summary>
    /// 退场保留态里那一帧的<b>形状来源</b>，专给「退出播放」那一段判「最后一帧有没有被按竖形摆放」用。
    /// <para>
    /// 2026-09-20：原先 <c>ClearAttachedForRetain</c> 优先信 <see cref="CompositionVideoTarget.PictureAspect"/>，
    /// 在窗口被掰成竖形的台位上那个数也是竖的，于是最后一帧被 contain 成 42% 宽的窄带。现在改成
    /// 先信「退场前最后一份呈现矩形」，这个读数就是给那条修正留下的把柄：它必须与
    /// <c>PlacedRect</c> 同比例（±2%）。
    /// </para>
    /// </summary>
    internal (double Width, double Height) RetainedShapeProbeState => _videoTarget.RetainedShapeProbe;

    /// <summary>
    /// 探针专用：直接写进片子码流比例。真实那条路是 <c>PlayerViewModel.PrepareShaderPlans</c> 在换片时
    /// 报一次（<c>SourceAspectChanged</c>）；探针自己 new 了 <c>LibMpvBackend</c>，绕过了那个入口，
    /// 所以这里补上，否则「保留帧按画面真实比例摆放」这条修正等于没被验到。
    /// </summary>
    internal void ProbeSourceAspect(double aspect) => _videoTarget.SourceAspect = aspect;

    /// <summary>
    /// 探针专用：「mpv 说首帧已经交给视频输出」这一句的手动转交。真实那条路上它从状态快照来
    /// （<c>OnStatusApplied</c> → <see cref="NotePictureStarted"/>），而探针自己 new 了后端、不经过
    /// <c>PlayerViewModel</c> 那条订阅，所以由探针自己把后端读到的那一位递进来 ——
    /// 与 <see cref="ProbeSourceAspect"/> 同一个理由。
    /// </summary>
    internal void ProbePictureStarted() => NotePictureStarted();

    /// <summary>
    /// 遮罩那一场的三样读数，专给「加载遮罩揭得早不早」用：屏幕底下有没有真画面、遮罩这一刻的不透明度、
    /// 以及这一场已经等了多少毫秒。判据是「<c>HasPicture</c> 为假的那一段里 <c>Opacity</c> 必须一直是 1」
    /// —— 遮罩还在等首帧就先淡下去，用户看到的就是背景图与正片之间那段黑。
    /// </summary>
    internal (bool HasPicture, double Opacity, bool ContentReady) CoverProbeState =>
        (_videoTarget.HasPicture, Cover.Opacity, _videoTarget.IsContentReady);
}
