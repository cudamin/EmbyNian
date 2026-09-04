namespace EmbyNian.Mpv;

/// <summary>
/// How far the picture has to be scaled to fill the output — the one number that decides whether a
/// luma upscaler belongs in the chain at all.
/// <para>
/// This replaced 「按片源分辨率挑配置组」. The old rule read the source alone: 4K 片源 → the light group,
/// 720p 片源 → the heavy one. That works only for one screen size, and the client never looked at the
/// output at all — so a 1080p file was treated identically whether it was being blown up to 4K or shown
/// at native size in a small window. The scale factor is the thing every upscaling shader in the chain
/// actually gates on, and it needs both halves to compute.
/// </para>
/// <para>
/// Four tiers, not five. A <c>Native</c> tier for 0.95–1.05 was specified and then withdrawn once the
/// shaders' own <c>//!WHEN</c> gates were read: <c>SSimDownscaler</c> only runs when something really is
/// being shrunk, so at 1.00× it sits in the chain costing nothing, and the only thing left to fix was the
/// OSD reading 「1.00× · 缩小档」. That is a label, not a tier — see <see cref="ShaderTier.Label"/>.
/// </para>
/// </summary>
public enum UpscaleTier
{
    /// <summary>
    /// &lt;1.05×: nothing is being enlarged, so an upscaler in the chain would sit idle. Includes 1:1,
    /// which the OSD calls 原生 rather than 缩小.
    /// </summary>
    Shrink,

    /// <summary>
    /// 1.05–&lt;1.45×. The awkward one: a 2× upscaler would compute a doubled picture and throw most of it
    /// away, and both 2× families here refuse to run below their own threshold (ravu-lite 1.414×, ArtCNN
    /// 1.3×). <c>ravu-zoom</c> is the only file on the list that scales straight to an arbitrary target,
    /// which is why it holds this tier for 真人.
    /// </summary>
    Slight,

    /// <summary>1.45–2.20×: a 2× upscaler's home ground, with 2.00× landing exactly on it.</summary>
    Sweet,

    /// <summary>
    /// &gt;2.20×: one 2× pass cannot cover it and the remainder goes to <c>scale</c>. Note that noise and
    /// banding are <b>not</b> this tier's business — those follow how poor the transfer is, not how far it
    /// is being enlarged, which is <see cref="ShaderTier.IsVintage"/>.
    /// </summary>
    Large
}

/// <summary>
/// How much GPU the chain may spend. A property of the machine, not of the film, so it is one stored
/// setting rather than something probed per playback.
/// <para>
/// <see cref="Low"/> must stay 0: settings.json holds enums as plain integers, so 0 is what a fresh
/// install and a file missing the key both read as — and 「核显」 is the honest default for the machine
/// this client is written on.
/// </para>
/// </summary>
public enum GpuTier
{
    /// <summary>Integrated graphics and entry-level cards: Vega iGPU, Iris Xe, GTX 1050.</summary>
    Low,

    /// <summary>An entry discrete card: GTX 1650, RX 6500 XT, Arc A380.</summary>
    Medium,

    /// <summary>RTX 3060 / RX 6700 and up.</summary>
    High
}

/// <summary>
/// One measurement: the factor, the tier it falls in, and any remark worth putting in the log next to it.
/// </summary>
/// <param name="Note">
/// Empty in the ordinary case. Carries 「问不出尺寸」 and 「宽比高比差得多」 — the two things a reader of the
/// log would otherwise have to guess at, and neither of which is an error.
/// </param>
public readonly record struct UpscaleMeasure(double Factor, UpscaleTier Tier, string Note = "")
{
    /// <summary>Whether the factor was computable at all; false means <see cref="Tier"/> is the fallback.</summary>
    public bool Measured => Factor > 0;

    /// <summary>The factor as the log and the OSD write it, e.g. <c>1.33×</c>.</summary>
    public string Times => Measured
        ? Factor.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "×"
        : "倍数未知";

    public override string ToString() => $"{Times} {ShaderTier.Label(this)}";
}

/// <summary>
/// Turns 「this file, that screen」 into a <see cref="UpscaleTier"/>. A pure function so every boundary
/// and every degenerate input can be pinned by a unit test — the old rule's blind spot was invisible on
/// screen precisely because nothing could assert on it.
/// </summary>
public static class ShaderTier
{
    /// <summary>At and above this the picture is being enlarged enough for an upscaler to be worth loading.</summary>
    public const double SlightFloor = 1.05;

    /// <summary>At and above this a 2× upscaler stops wasting most of what it computes — and starts running
    /// at all: ravu-lite's own gate is 1.414×, so 1.45 is the first factor at which this tier's file fires.</summary>
    public const double SweetFloor = 1.45;

    /// <summary>Above this — strictly — one 2× pass no longer covers the gap.</summary>
    public const double LargeFloor = 2.20;

    /// <summary>
    /// Inside 缩小, the factor at which the OSD stops saying 缩小 and says 原生 instead. Purely a label:
    /// see <see cref="UpscaleTier.Shrink"/> for why 1:1 is not a tier of its own.
    /// </summary>
    public const double NativeFloor = 0.95;

    /// <summary>
    /// How far past a boundary the factor has to go before the tier changes, once it is already in one.
    /// <para>
    /// Without it, dragging a window slowly past 1.45 swaps the upscaler between <c>ravu-zoom</c> and
    /// <c>ravu-lite</c> over and over, and every swap is a visible change in sharpness plus a shader
    /// reload. 0.05 is the same size as the narrowest gap between two boundaries elsewhere in the table.
    /// </para>
    /// </summary>
    public const double Hysteresis = 0.05;

    /// <summary>
    /// 老片源的分界：DVD 和更早的转制。Parallel to the tier, never folded into it — the reason is arithmetic:
    /// an NTSC DVD (480 lines) blown up to 1080p is 2.25× and lands in 大倍数, a PAL DVD (576 lines) of the
    /// very same film is 1.88× and lands in 甜点. Attaching 去带/降噪 to a tier would process one and not the
    /// other.
    /// </summary>
    public const int VintageHeight = 576;

    /// <summary>Whether the source is DVD-era. Height 0 means Emby never probed it — that is not a DVD.</summary>
    public static bool IsVintage(int sourceHeight) => sourceHeight is > 0 and <= VintageHeight;

    /// <summary>
    /// 高帧率片源的分界：above this a CNN upscaler comes out of the chain and the ravu family takes its place.
    /// <para>
    /// Measured on this machine on 2026-09-04, 1920×1080 into 2560×1440 on the shipped <c>gpu-api=vulkan</c>:
    /// <c>ArtCNN_C4F16 + CfL_Prediction_Lite</c> renders at 45 fps, i.e. 22 ms a frame, so a 24 fps film fits in
    /// about half the GPU while 60 fps would ask for 1.33 seconds of GPU per second of film.
    /// <c>ravu-zoom-ar-r2 + CfL_Prediction_Lite</c> is 7.4 ms against 7.2 ms for no chain at all — ravu-zoom
    /// outputs the target size, so mpv's own <c>ewa_lanczossharp</c> pass stops running and pays for most of it,
    /// and 60 fps still leaves the GPU half idle.
    /// </para>
    /// <para>
    /// A third axis parallel to the tier and to <see cref="IsVintage"/>, for the reason both of those are
    /// parallel: how many frames arrive each second has nothing to do with how far each one is enlarged.
    /// 30 rather than the 47 that 高帧率或高刷新率时使用音频同步 uses, because that number is about judder and this
    /// one is about arithmetic per second — 22 ms × 30 is already two thirds of the budget, and 29.97 (NTSC) has to
    /// stay on the near side of it.
    /// </para>
    /// </summary>
    public const double FastMotionFps = 30;

    /// <summary>
    /// Whether the frames arrive too fast for a CNN upscaler. 0 means Emby never probed the stream, which is
    /// not a reason to give up the good chain.
    /// </summary>
    public static bool IsFastMotion(double sourceFrameRate) => sourceFrameRate > FastMotionFps;

    /// <summary>
    /// Where a source of <paramref name="sourceWidth"/>×<paramref name="sourceHeight"/> lands when drawn
    /// into <paramref name="outputWidth"/>×<paramref name="outputHeight"/>, optionally staying in
    /// <paramref name="current"/> if the new factor is within <see cref="Hysteresis"/> of it.
    /// </summary>
    /// <remarks>
    /// The factor is <c>min(width ratio, height ratio)</c>. That is not a simplification of 「按较大的那个」
    /// — it is the picture's real enlargement, and the two statements are the same statement: the video is
    /// fitted into the output with its shape kept, so its drawn rectangle is exactly
    /// <c>source × min(ratios)</c>, and the height ratio of <b>that rectangle</b> equals its width ratio.
    /// Measuring against the surface instead and taking the larger ratio would file a 2.39:1 film one whole
    /// tier too high: a 1920×800 encode on a 2560×1440 screen is drawn 2560×1067, which is 1.33×, while its
    /// height ratio against the screen reads 1.8× and would load a 2× upscaler for mpv to shrink back again.
    /// It is also the arithmetic the shaders themselves gate on — ravu and ArtCNN each test width
    /// <b>and</b> height, which is <c>min</c> written the other way round.
    /// <para>
    /// The dimensions Emby reports are storage dimensions, so a genuinely anamorphic file (a stored 720×576
    /// meant to be shown at 16:9) is measured on the stored shape. Those are rare enough, and the cost of
    /// getting one wrong is one tier of shader — not a broken picture. Both ratios go in
    /// <see cref="UpscaleMeasure.Note"/> when they disagree by more than 5%, so the log says so.
    /// </para>
    /// <para>
    /// Anything missing — a source Emby never probed, a window whose size nobody could read — lands in
    /// <see cref="UpscaleTier.Slight"/> rather than in a guess. That tier is the safe one to be wrong in:
    /// its 真人 upscaler scales straight to whatever the target turns out to be, its 动画 one switches itself
    /// off below 1.3×, and every other shader in both chains carries its own gate. Falling back to 缩小
    /// instead would silently drop the upscaler for every unprobed file.
    /// </para>
    /// </remarks>
    public static UpscaleMeasure Measure(
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight,
        UpscaleTier? current = null)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
        {
            var missing = sourceWidth <= 0 || sourceHeight <= 0 ? "片源尺寸未知" : "输出尺寸未知";
            return new UpscaleMeasure(0, UpscaleTier.Slight, missing);
        }

        var wide = (double)outputWidth / sourceWidth;
        var tall = (double)outputHeight / sourceHeight;
        var factor = Math.Min(wide, tall);

        var note = Math.Abs(wide - tall) > 0.05 * Math.Max(wide, tall)
            ? $"宽比 {Fixed(wide)}、高比 {Fixed(tall)} 差得多，按画面真正铺开的那个算"
            : "";

        return new UpscaleMeasure(factor, Classify(factor, current), note);
    }

    /// <summary>The tier a known factor falls in. Split out so the boundaries have one spelling.</summary>
    public static UpscaleTier Classify(double factor) =>
        factor > LargeFloor ? UpscaleTier.Large
        : factor >= SweetFloor ? UpscaleTier.Sweet
        : factor >= SlightFloor ? UpscaleTier.Slight
        : UpscaleTier.Shrink;

    /// <summary>
    /// The same, but sticky: a factor within <see cref="Hysteresis"/> of the tier it is already in keeps
    /// that tier. Only the boundary behaviour differs — a factor well inside another tier moves.
    /// </summary>
    public static UpscaleTier Classify(double factor, UpscaleTier? current)
    {
        var tier = Classify(factor);
        if (current is not { } held || held == tier) return tier;

        var (low, high) = Band(held);
        return factor >= low - Hysteresis && factor <= high + Hysteresis ? held : tier;
    }

    /// <summary>
    /// A tier's own range, as the pair the hysteresis widens. The bounds are the same four constants
    /// <see cref="Classify(double)"/> tests, so there is no second place a boundary is written down.
    /// </summary>
    private static (double Low, double High) Band(UpscaleTier tier) => tier switch
    {
        UpscaleTier.Shrink => (double.NegativeInfinity, SlightFloor),
        UpscaleTier.Slight => (SlightFloor, SweetFloor),
        UpscaleTier.Sweet => (SweetFloor, LargeFloor),
        _ => (LargeFloor, double.PositiveInfinity)
    };

    /// <summary>The tier's name, as the settings dropdown and the player's 着色器 submenu list it.</summary>
    public static string Describe(UpscaleTier tier) => tier switch
    {
        UpscaleTier.Shrink => "缩小档",
        UpscaleTier.Slight => "微放大档",
        UpscaleTier.Sweet => "甜点档",
        _ => "大倍数档"
    };

    /// <summary>
    /// What one measurement is called on screen. Everything is <see cref="Describe(UpscaleTier)"/> except
    /// the top of 缩小: at 0.95–1.05 nothing is being resized to speak of, and 「1.00× · 缩小档」 reads like
    /// a mistake. The tier is unchanged — this is the label, and it is the whole of what the withdrawn
    /// <c>Native</c> tier would have bought.
    /// </summary>
    public static string Label(UpscaleMeasure measure) =>
        measure.Tier == UpscaleTier.Shrink && measure.Factor >= NativeFloor ? "原生" : Describe(measure.Tier);

    /// <summary>The factor range as the settings page and the player menu write it.</summary>
    public static string Range(UpscaleTier tier) => tier switch
    {
        UpscaleTier.Shrink => "<1.05 倍",
        UpscaleTier.Slight => "1.05–1.45 倍",
        UpscaleTier.Sweet => "1.45–2.2 倍",
        _ => ">2.2 倍"
    };

    public static string Describe(GpuTier gpu) => gpu switch
    {
        GpuTier.Low => "低档",
        GpuTier.Medium => "中档",
        _ => "高档"
    };

    /// <summary>
    /// The one line that explains a chain, for the launch log and for the player's 播放信息 panel: factor,
    /// what the picture is, which column of the table, the output size that was actually measured, and the
    /// files the chain loads — e.g.
    /// <c>1.33× · 微放大档 · 真人 · 低档 · 输出 2560×1440 · ravu-zoom-ar-r2 + CfL_Prediction_Lite</c>.
    /// <para>
    /// Written in one place because it is the only thing the user can send back when a film drops frames or
    /// looks different. Everything in it is a fact that was used to pick the chain, so a line that reads
    /// wrong is a decision that was wrong — which is the point.
    /// </para>
    /// </summary>
    public static string Explain(
        UpscaleMeasure measure,
        bool animated,
        GpuTier gpu,
        int outputWidth,
        int outputHeight,
        ShaderGroup? group)
    {
        var parts = new List<string>(7)
        {
            measure.Times,
            Label(measure),
            animated ? "动画" : "真人",
            Describe(gpu),
            outputWidth > 0 && outputHeight > 0 ? $"输出 {outputWidth}×{outputHeight}" : "输出尺寸未知"
        };

        if (group is not null) parts.Add(string.Join(" + ", group.ShaderFileNames));
        if (measure.Note.Length > 0) parts.Add(measure.Note);

        return string.Join(" · ", parts);
    }

    private static string Fixed(double value) =>
        value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}
