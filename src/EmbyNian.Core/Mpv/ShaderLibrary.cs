namespace EmbyNian.Mpv;

/// <summary>
/// Every shader file that ships, described once. The 档位表 (<see cref="ShaderGroupCatalog"/>) says which cells
/// use which of these; this says what each one <b>is</b>.
/// <para>
/// The split matters because the two change for different reasons. A cell changes when someone decides a
/// different picture is wanted; an entry here changes only when the file itself changes upstream — and when it
/// does, the test that reads all fifteen files against these descriptions is what notices.
/// </para>
/// <para>
/// Where each file came from, its licence, and the one local edit are in <c>assets/shaders/README.md</c>. That
/// file is the single provenance record and this one deliberately does not repeat it. It also records the six
/// Anime4K Mode A files that left the box on 2026-09-04 and where to get them again.
/// </para>
/// </summary>
public static class ShaderLibrary
{
    /// <summary>SSimDownscaler's own documentation: without these it is worse than not being there at all.</summary>
    private static readonly KeyValuePair<string, string>[] LinearLightOff =
    [
        new("dscale", "mitchell"),
        new("linear-downscaling", "no"),
        new("correct-downscaling", "yes")
    ];

    public static readonly ShaderDescriptor SsimDownscaler = new(
        "SSimDownscaler", "igv/SSimDownscaler.glsl", ShaderRole.Downscale,
        Hook: "POSTKERNEL", ChangesResolution: true, Passes: 4,
        Gate: "NATIVE_CROPPED.h POSTKERNEL.h >", ReadsLuma: false, ReadsChroma: false)
    {
        Requires = LinearLightOff
    };

    public static readonly ShaderDescriptor SsimSuperRes = new(
        "SSimSuperRes", "igv/SSimSuperRes.glsl", ShaderRole.PostSharpen,
        Hook: "POSTKERNEL", ChangesResolution: true, Passes: 4,
        Gate: "NATIVE_CROPPED.h OUTPUT.h <", ReadsLuma: false, ReadsChroma: false);

    public static readonly ShaderDescriptor AdaptiveSharpen = new(
        "adaptive-sharpen", "igv/adaptive-sharpen.glsl", ShaderRole.PostSharpen,
        Hook: "OUTPUT", ChangesResolution: false, Passes: 1,
        Gate: "", ReadsLuma: false, ReadsChroma: false);

    public static readonly ShaderDescriptor ChromaFull = new(
        "CfL_Prediction", "CfL/CfL_Prediction.glsl", ShaderRole.ChromaReconstruct,
        Hook: "CHROMA", ChangesResolution: true, Passes: 4,
        Gate: "CHROMA.w LUMA.w <", ReadsLuma: true, ReadsChroma: true);

    public static readonly ShaderDescriptor ChromaLite = new(
        "CfL_Prediction_Lite", "CfL/CfL_Prediction_Lite.glsl", ShaderRole.ChromaReconstruct,
        Hook: "CHROMA", ChangesResolution: true, Passes: 2,
        Gate: "CHROMA.w LUMA.w <", ReadsLuma: true, ReadsChroma: false);

    public static readonly ShaderDescriptor Hdeband = new(
        "hdeband", "an3223/hdeband.glsl", ShaderRole.Deband,
        Hook: "LUMA", ChangesResolution: false, Passes: 3,
        Gate: "", ReadsLuma: false, ReadsChroma: false)
    {
        // mpv's own debander and this one fight each other. The only option here that overrides something the
        // user can see (设置 → 视频输出 → 去色带), and that row says so.
        Requires = [new KeyValuePair<string, string>("deband", "no")]
    };

    public static readonly ShaderDescriptor NonLocalMeans = new(
        "nlmeans_light", "an3223/nlmeans_light.glsl", ShaderRole.Denoise,
        Hook: "LUMA", ChangesResolution: true, Passes: 6,
        Gate: "", ReadsLuma: false, ReadsChroma: false);

    public static readonly ShaderDescriptor RavuZoom = new(
        "ravu-zoom-ar-r2", "ravu/ravu-zoom-ar-r2.hook", ShaderRole.LumaUpscale,
        Hook: "LUMA", ChangesResolution: true, Passes: 1,
        Gate: "HOOKED.w OUTPUT.w < HOOKED.h OUTPUT.h < *", ReadsLuma: false, ReadsChroma: false);

    public static readonly ShaderDescriptor RavuLiteR2 = RavuLite(2);
    public static readonly ShaderDescriptor RavuLiteR3 = RavuLite(3);
    public static readonly ShaderDescriptor RavuLiteR4 = RavuLite(4);

    public static readonly ShaderDescriptor ArtCnnF16 = ArtCnn("ArtCNN_C4F16", 8);
    public static readonly ShaderDescriptor ArtCnnF32 = ArtCnn("ArtCNN_C4F32", 8);
    public static readonly ShaderDescriptor ArtCnnF32Ds = ArtCnn("ArtCNN_C4F32_DS", 8);
    public static readonly ShaderDescriptor ArtCnnF32Dn = ArtCnn("ArtCNN_C4F32_DN", 8);

    /// <summary>
    /// The ravu-lite family: doubles LUMA in one pass, and stands aside below √2. 甜点's floor of 1.45 is the
    /// first factor above that gate, which is the whole reason 微放大 uses ravu-zoom instead.
    /// </summary>
    private static ShaderDescriptor RavuLite(int radius) => new(
        $"ravu-lite-ar-r{radius}", $"ravu/ravu-lite-ar-r{radius}.hook", ShaderRole.LumaUpscale,
        Hook: "LUMA", ChangesResolution: true, Passes: 2,
        Gate: "HOOKED.w OUTPUT.w / 0.707106 < HOOKED.h OUTPUT.h / 0.707106 < *",
        ReadsLuma: false, ReadsChroma: false);

    /// <summary>
    /// The ArtCNN family: eight passes on LUMA, gated at 1.3×, all four weights identical in shape — and all
    /// eight passes <c>//!COMPUTE</c>, which is the whole of why <c>gpu-api</c> matters so much for these cells
    /// (see <see cref="ShaderDescriptor.ComputePasses"/>). Written as one function because four hand-copied
    /// descriptions are four chances for one to drift.
    /// </summary>
    private static ShaderDescriptor ArtCnn(string name, int passes) => new(
        name, $"ArtCNN/{name}.glsl", ShaderRole.LumaUpscale,
        Hook: "LUMA", ChangesResolution: true, Passes: passes,
        Gate: "OUTPUT.w LUMA.w / 1.3 > OUTPUT.h LUMA.h / 1.3 > *", ReadsLuma: true, ReadsChroma: false)
    {
        ComputePasses = passes
    };

    /// <summary>Every descriptor, for the file-versus-description test and for <see cref="ShaderGroupCatalog.ShaderFiles"/>.</summary>
    public static readonly ShaderDescriptor[] All =
    [
        SsimDownscaler, SsimSuperRes, AdaptiveSharpen,
        ChromaFull, ChromaLite,
        Hdeband, NonLocalMeans,
        RavuZoom, RavuLiteR2, RavuLiteR3, RavuLiteR4,
        ArtCnnF16, ArtCnnF32, ArtCnnF32Ds, ArtCnnF32Dn
    ];
}
