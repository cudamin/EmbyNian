namespace EmbyNian.Playback;

/// <summary>
/// 「画面真的上屏了吗」—— 加载遮罩（那块垫着背景图的遮挡层）可以揭开的唯一判据。
/// <para>
/// <b>为什么需要它。</b>遮罩原来在 <c>file-loaded</c> 那一拍就揭（<c>PlayerViewModel.ApplyStatus</c> 里的
/// <c>Loaded &amp;&amp; !Buffering</c>）。那是「demuxer 打开了文件」，不是「有画面可看」——两者之间隔着一整段
/// 解码、首帧着色器编译，走网络时还要等服务器把第一段吐出来。遮罩一揭，屏幕底下是那条<b>已经被挂上、
/// 但还没有被押过任何一帧</b>的交换链（mpv 建链时画的是黑），于是用户看到的就是
/// 「背景图 → 一段黑屏 → 正片」。
/// </para>
/// <para>
/// <b>实测（<c>work/probe-first-frame.py</c>，本地 18MB、moov 在片尾的 mp4，composition 管线无窗口）。</b>
/// 以 loadfile 为 0：
/// </para>
/// <list type="bullet">
/// <item><c>display-swapchain</c> 在 <b>32.7ms</b> 出现，而客户端一挂上它，<c>IsContentReady</c>（挂链 ＋
/// 缓冲尺寸等于宿主）就为真 —— 那一刻这条链上已经被 mpv 押过 <b>2 帧</b>，那 2 帧是空的。</item>
/// <item><c>file-loaded</c> 在 <b>110.1ms</b> —— 旧判据在这里揭遮罩。</item>
/// <item>第一条正片帧押上屏在 <b>473.8ms</b>（前 360ms 是一条纯黑链）。</item>
/// <item>mpv 的 <c>playback-restart</c> 事件在 <b>485.4ms</b>，即首帧之后 12ms。</item>
/// </list>
/// <para>
/// 所以「押过帧」这件事必须按交换链自己的账去数：<see cref="CompositionVideoTarget"/> 逐个读链的
/// <c>IDXGISwapChain::GetLastPresentCount</c>（vtbl 槽 17），拿它当「这条链被押过没有」的唯一证据 ——
/// 缓冲尺寸、DPI、窗口几何都答不了这个问题。
/// </para>
/// <para>
/// <b>为什么不能只数 Present。</b>建链那一下 mpv 会连押两三帧空画面（实测 32.7ms 那一刻计数已经是 2），
/// 而客户端的自动全屏、尺寸整形恰好也在这一段里发生 —— 每次重建链都会再空押一次。只认「押过帧」会在
/// 启动期当场为真，等于什么都没拦。真正把「空帧」与「正片帧」分开的是 mpv 自己的话：
/// <c>playback-restart</c> 由 <c>handle_playback_restart()</c> 在 <c>video_status</c> 从 READY 走向
/// PLAYING 那一拍发出（mpv 0.41 <c>player/playloop.c</c>），而 READY 是 <c>vo_queue_frame()</c> 把<b>第一条
/// 帧交给视频输出</b>之后才置上的（<c>player/video.c</c>）—— mpv 把「正在播放」那句 OSD 提示也挂在同一拍。
/// </para>
/// <para>
/// <b>两道闸缺一不可，且各有分工。</b><paramref name="playbackStarted"/> 负责「这不是空链」，
/// <paramref name="presentsSinceAttach"/> 负责「这条链确实收得下画面」（音频文件压根没有链，那一位由
/// <paramref name="swapChainAttached"/> 为假短路掉，否则那类文件永远等不到帧）。两者都在遮罩亮起时从头
/// 计过一遍 —— 见 <c>CompositionVideoTarget.BeginPictureWait</c>。
/// </para>
/// </summary>
public static class PictureReveal
{
    /// <summary>
    /// 遮罩可以揭了吗。<paramref name="swapChainAttached"/> 为假且 mpv 已宣布播放开始，就是「这一场没有画面
    /// 要等」（音频文件、或画面在 mpv 自建窗口里的独立管线那一档）。
    /// </summary>
    public static bool Ready(bool playbackStarted, bool swapChainAttached, int presentsSinceAttach) =>
        playbackStarted && (!swapChainAttached || presentsSinceAttach > 0);
}
