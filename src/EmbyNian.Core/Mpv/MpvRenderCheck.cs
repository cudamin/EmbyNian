namespace EmbyNian.Mpv;

/// <summary>
/// Whether the 视频输出 settings can actually run the 着色器档位 — 任务书 5.6, which asks for <c>vo</c>,
/// <c>gpu-api</c> and <c>hwdec</c> to be part of the same scheme as the chains rather than three unrelated
/// dropdowns.
/// <para>
/// It reports and never overrides. These three are the user's to set, and a chain quietly rewriting 视频渲染
/// would be the 「界面在骗人」 problem again from the other end. So the chain states what it needs, this says
/// whether the settings meet it, and the answer goes in the launch log and the self-check report where somebody
/// can act on it.
/// </para>
/// <para>
/// What is still deliberately <b>not</b> here: 「AMD 一律用 vulkan」. The shipped 图形接口 became vulkan on
/// 2026-09-04 because a measurement on this machine said so, not because of the adapter's name — and the rule
/// below is keyed to what the chain contains rather than to which card is in the box, so a machine where d3d11
/// is the faster path is told nothing when its chain has no compute passes. See 任务书 5.6 and the standing
/// refusal to put GPU model strings in Core.
/// </para>
/// </summary>
public static class MpvRenderCheck
{
    /// <summary>
    /// mpv's <c>vo</c> that this scheme is written for. ArtCNN's and CfL's own documentation both ask for it,
    /// and it is the shipped default.
    /// </summary>
    public const string PreferredRenderer = "gpu-next";

    /// <summary>
    /// mpv's <c>gpu-api</c> this scheme is written for. Chosen by measurement rather than by upstream's
    /// preference: see <see cref="ShaderDescriptor.ComputePasses"/> for the numbers.
    /// </summary>
    public const string PreferredApi = "vulkan";

    /// <summary>
    /// Problems with the current 视频输出 settings, in plain words, or an empty list.
    /// </summary>
    /// <param name="renderer">mpv <c>vo</c>.</param>
    /// <param name="gpuApi">mpv <c>gpu-api</c>; empty means 「let mpv choose」, which on Windows is d3d11.</param>
    /// <param name="hardwareDecoding">mpv <c>hwdec</c>; empty means mpv's own default, which is no hardware decoding.</param>
    /// <param name="chain">
    /// The chain about to run, when there is one. Only used to ask whether it contains compute passes — the
    /// question is 「这条链吃不吃这个图形接口的亏」, and asking the chain is the only way to answer it without
    /// naming ArtCNN here.
    /// </param>
    public static IReadOnlyList<string> Problems(
        string? renderer,
        string? gpuApi,
        string? hardwareDecoding,
        ShaderGroup? chain = null)
    {
        var vo = (renderer ?? "").Trim();
        var api = (gpuApi ?? "").Trim();
        var hwdec = (hardwareDecoding ?? "").Trim();
        var problems = new List<string>();

        // mpv-prescalers records this one as a concrete failure rather than a preference: vo=gpu with
        // gpu-api=d3d11 reports rgba16f as unavailable, and the ravu hooks then do not load at all — a chain
        // that is configured, logged and silently absent from the picture.
        if (vo == "gpu" && api == "d3d11")
        {
            problems.Add("视频渲染是 gpu、图形接口是 d3d11：这个组合会报 rgba16f 格式不可用，ravu 那几条链根本加载不上"
                + $"（换成 {PreferredRenderer}，或者把图形接口换成 vulkan）");
        }
        else if (vo.Length > 0 && vo != PreferredRenderer)
        {
            problems.Add($"视频渲染是 {vo}，而这套档位是照 {PreferredRenderer} 调的（ArtCNN 和 CfL 的官方说明都写它）");
        }

        // The largest cost cliff on this machine, and the reason 图形接口 has been vulkan since 2026-09-04.
        // Measured that day, 1080p into 2560×1440, ArtCNN_C4F16 + CfL_Prediction_Lite, only gpu-api changed:
        // 45 fps on vulkan against 8.7 on d3d11, where 24 fps is the requirement. It is stated against the
        // chain's compute passes rather than against d3d11 in general because the fragment-shader chains are
        // within noise of each other on both APIs. 「自动」 counts as d3d11: that is what mpv picks on Windows.
        var compute = chain?.Shaders.Sum(shader => shader.ComputePasses) ?? 0;
        if (compute > 0 && (api.Length == 0 || api == "d3d11"))
        {
            problems.Add($"图形接口是 {(api.Length == 0 ? "自动挑选（Windows 上就是 Direct3D 11）" : "Direct3D 11")}，"
                + $"而这条链有 {compute} 个 compute pass：本机实测同一条链在 D3D11 上只有 8.7 fps、Vulkan 上 45 fps，"
                + $"24fps 的片子会跟不上（把图形接口换成 {PreferredApi}）");
        }

        // 「关闭（纯软件解码）」 is a real choice and stays one; it is only worth a line because a 4K file plus a
        // chain is the case where it costs the most, and because an empty value means the same thing without
        // anybody having chosen it.
        if (hwdec.Length == 0 || hwdec == "no")
            problems.Add("硬件解码是关的：4K 片源再叠一条着色器链时，解码这一头会先吃满 CPU");

        return problems;
    }
}
