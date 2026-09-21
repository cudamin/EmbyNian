using EmbyNian.Configuration;

namespace EmbyNian.Playback;

/// <summary>
/// 独占模式那扇 mpv 窗口的窗口策略：一组必须在 <c>mpv_initialize</c> 之前设好的选项。
/// <para>
/// 参考项目（一份 mpv 便携配置，2026-08-12 版）把窗口行为整组写在 <c>mpv.conf</c> 里；本项目此前对 mpv
/// 自建的窗口<b>一个窗口选项都没发过</b>，全靠 mpv 的编译默认值，等于把「窗口长什么样」交给一个我们既
/// 没读过也没钉住的默认。这一组把那份配置里与窗口形态有关的三条搬过来。
/// </para>
/// <para>
/// <b>它不碰渲染，也不碰全屏。</b>渲染契约（<c>vo</c>/<c>gpu-*</c>/<c>d3d11-*</c>/<c>force-window</c>）归
/// <see cref="LibMpvPipelinePolicy"/>；全屏归运行期的属性写入（见 <see cref="LibMpvBackend"/> —— 起播即全屏
/// 那一档要让窗口以全屏尺寸<b>出生</b>，先冒一个小窗再跳过去就不叫瞬时了）。三条都是普通选项：某条不被
/// 这个构建认账时只是少一个特性（<c>LibMpvBackend</c> 会打一行警告），不该拦住播放。
/// </para>
/// <para>
/// 三条都不是随设置变的，所以<b>不进启动签名</b>（<c>Mpv.InlineSwitch</c> 那道闸只关心「换片能不能复用
/// 同一个实例」，常量不影响它）。
/// </para>
/// </summary>
internal static class StandaloneWindowPolicy
{
    /// <summary>
    /// 三条各自的理由，都照抄参考项目的原值：
    /// <list type="bullet">
    ///   <item><c>keepaspect-window=yes</c>：窗口形状锁在画面比例上（参考项目 mpv.conf 第 97 行注释里说明
    ///   的默认值，这里写明）。集成模式的同义约束是 <c>HostWindow</c> 收到 <c>WM_SIZING</c> 时的
    ///   <c>AspectLock</c>；独占模式的窗口归 mpv，那条约束只能由 mpv 自己执行 —— 不写明就是继承默认值，
    ///   哪一天默认值变了没人知道。</item>
    ///   <item><c>autofit-smaller=40%x30%</c>：窗口出生尺寸的下限（参考项目 mpv.conf 第 95 行原值）。低分辨
    ///   率片子默认会开出一扇很小的窗，那份配置的注释写得很直白：「例如在 4k 屏上打开 480p 视频初始窗口过小」。
    ///   <b>只影响窗口出生那一刻</b>，之后用户拖成什么样就是什么样。</item>
    ///   <item><c>snap-window=yes</c>：拖动窗口时吸附到屏幕边缘（Windows 专有，参考项目 mpv.conf 第 109 行
    ///   原值）。它与全屏切换无关，同属那一条「窗口归 mpv 管」的策略，一起搬。</item>
    /// </list>
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> Options(VideoPipelineKind pipeline)
    {
        if (pipeline != VideoPipelineKind.Standalone) return [];

        return
        [
            new("keepaspect-window", "yes"),
            new("autofit-smaller", "40%x30%"),
            new("snap-window", "yes")
        ];
    }
}
