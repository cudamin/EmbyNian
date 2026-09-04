namespace EmbyNian.Mpv;

/// <summary>
/// What a shader is for. The 档位表 picks chains by name today; this is what lets a rule reason about one
/// without reading its file name — 任务书 5.1 and 5.4, and the reason 「不许靠文件名猜一个着色器干什么」 keeps
/// coming up: <c>FSRCNNX_x1_16-0-4-1_distort.glsl</c> reads like an upscaler and enlarges nothing.
/// </summary>
public enum ShaderRole
{
    /// <summary>
    /// Runs before anything else and changes no size: clamping, line repair. Nothing in the table has this role
    /// since Anime4K left the box on 2026-09-04; the role stays because 「前处理」 is one of the seven 任务书 5.1
    /// names and the next family to arrive will need it.
    /// </summary>
    Preprocess,

    /// <summary>Debanding. At most one per chain, and it turns mpv's own <c>deband</c> off.</summary>
    Deband,

    /// <summary>Noise removal. At most one per chain.</summary>
    Denoise,

    /// <summary>Enlarges the picture. At most one bundle per chain, and it must change resolution.</summary>
    LumaUpscale,

    /// <summary>Rebuilds chroma from luma. At most one per chain.</summary>
    ChromaReconstruct,

    /// <summary>Sharpening after the scaling is done. At most one per chain.</summary>
    PostSharpen,

    /// <summary>Shrinks the picture, or brings a multi-step enlargement back down to the target.</summary>
    Downscale
}

/// <summary>
/// One shader file, described by what it actually declares rather than by what its name suggests.
/// <para>
/// Every field except <see cref="Role"/> and <see cref="Bundle"/> is a fact stated in the file's own
/// <c>//!</c> directives, and a unit test reads all fifteen files and fails if any of them disagrees. That
/// test is the mechanical guard 任务书 5.1 asks for: a file with no <c>//!WIDTH</c> or <c>//!HEIGHT</c> cannot
/// be described as <see cref="ShaderRole.LumaUpscale"/>, which is exactly the mistake that shipped for months.
/// </para>
/// <para>
/// <b>Still no measured cost field, but the reason has changed.</b> 任务书 5.1 wanted one and forbade inventing
/// tiers for it. <c>vo-passes</c> turns out to work fine for the fragment-shader files — 2026-09-04, 1080p into
/// 2560×1440: <c>ravu-zoom-ar-r2</c> 4.7 ms, <c>CfL_Prediction_Lite</c> 1.4 ms, mpv's own
/// <c>ewa_lanczossharp</c> 6.2 ms, every Anime4K pass individually — and reports 0 for every pass of a
/// <c>//!COMPUTE</c> file after the first, which is all four ArtCNN weights. So a cost field would be honest for
/// eleven files and blank for the four that matter most. What is here instead is <see cref="ComputePasses"/>,
/// which is a fact from the file rather than a number from a stopwatch, and it is what the one real cost cliff
/// on this machine keys off; the wall-clock figures live in <c>artifacts/shader-probe/gpu-findings.md</c>.
/// </para>
/// </summary>
/// <param name="Name">What the log, the menu and this table call it — the file name without directory or extension.</param>
/// <param name="File">Relative to <see cref="ShaderGroupCatalog.ShaderRoot"/>, with forward slashes.</param>
/// <param name="Hook">
/// The first <c>//!HOOK</c> stage in the file, which is where the chain's order comes from: mpv runs LUMA and
/// CHROMA before the planes are merged, then MAIN, then the scaler, then POSTKERNEL, then OUTPUT. A chain
/// written out of that order is a chain whose comment lies about what runs when — see
/// <see cref="Stage"/>.
/// </param>
/// <param name="ChangesResolution">
/// Whether the file declares an output size of its own (<c>//!WIDTH</c> / <c>//!HEIGHT</c>). The single most
/// useful field here: it is the only thing that mechanically separates a real upscaler from a file that merely
/// sharpens at the source's own resolution.
/// </param>
/// <param name="Passes">How many <c>//!HOOK</c> blocks the file has; &gt;1 means it is internally multi-pass.</param>
/// <param name="Gate">
/// The file's own <c>//!WHEN</c> expression, or an empty string when it always runs. Carried because 「链里写了
/// 就一定在跑」 is false for most of this list, and because two of these were written down wrong once already.
/// </param>
public sealed record ShaderDescriptor(
    string Name,
    string File,
    ShaderRole Role,
    string Hook,
    bool ChangesResolution,
    int Passes,
    string Gate,
    bool ReadsLuma,
    bool ReadsChroma)
{
    /// <summary>
    /// Which shader package this file belongs to; its own name unless it is one file of a set that is chosen
    /// and applied together — 任务书 4: 「着色器包内部自带的多 pass 算一个，不算多个」. The exclusivity rules
    /// count packages, which is what makes that rule hold without a special case inside the rule itself.
    /// <para>
    /// <b>Nothing sets it right now</b>: Anime4K Mode A was the only multi-file family and it left the box on
    /// 2026-09-04, so every shipping shader is one file and one decision. Kept rather than deleted because the
    /// day a second such family arrives (Mode B, FSR, NVScaler — 任务书 6 lists them as 「要用再取」) the rules
    /// would otherwise start counting files instead of decisions, and the symptom is a <i>false</i> red gate
    /// that somebody would fix by weakening the rule.
    /// </para>
    /// </summary>
    public string Bundle { get; init; } = "";

    /// <summary>
    /// mpv options this shader does not work correctly without — 任务书 6 calls this the right abstraction and
    /// it is the only one here: SSimDownscaler is worse than absent without <c>dscale=mitchell</c> and
    /// <c>linear-downscaling=no</c>, and hdeband fights mpv's own debander. Held on the shader rather than on
    /// the cell so that adding it to a chain brings its prerequisites along.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Requires { get; init; } = [];

    /// <summary>
    /// How many of this file's passes are <c>//!COMPUTE</c> rather than ordinary fragment shaders. Zero for
    /// everything in the box except the four ArtCNN weights, all eight of whose passes are compute.
    /// <para>
    /// It is here because it is the only field that predicts the largest cost cliff on this machine. Measured
    /// 2026-09-04, same card, same film, same chain (<c>ArtCNN_C4F16 + CfL_Prediction_Lite</c>, 1080p into
    /// 2560×1440), only <c>gpu-api</c> changed: <b>45 fps on vulkan, 8.7 on d3d11</b>. The fragment-shader chains
    /// are within noise of each other on the two APIs, so the cliff belongs to compute passes rather than to
    /// d3d11 in general — which is exactly why this is a property of the file and
    /// <see cref="MpvRenderCheck"/> reads it instead of naming ArtCNN.
    /// </para>
    /// </summary>
    public int ComputePasses { get; init; }

    /// <summary>The bundle this file is applied as part of.</summary>
    public string Package => Bundle.Length > 0 ? Bundle : Name;

    /// <summary>Whether it always runs, or stands aside under some condition of its own.</summary>
    public bool Gated => Gate.Length > 0;

    /// <summary>
    /// Where <see cref="Hook"/> falls in mpv's own order. Not stored: it is a property of the stage name, and a
    /// second copy would be a second thing to keep in step.
    /// </summary>
    public int Stage => Hook switch
    {
        "LUMA" => 0,
        "CHROMA" => 1,
        "RGB" => 2,
        "MAIN" => 3,
        "PREKERNEL" => 4,
        "POSTKERNEL" => 5,
        "SCALED" => 6,
        _ => 7
    };

    /// <summary>
    /// Whether the shader writes the luma plane. Derived from the hook rather than stored: a LUMA hook writes
    /// luma and a CHROMA hook does not, and everything from MAIN onwards is working on merged colour, so it
    /// writes both. Two stored booleans would only be two more things able to disagree with the file.
    /// </summary>
    public bool WritesLuma => Hook != "CHROMA";

    /// <inheritdoc cref="WritesLuma"/>
    public bool WritesChroma => Hook != "LUMA";

    public override string ToString() => Name;
}
