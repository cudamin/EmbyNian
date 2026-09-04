namespace EmbyNian.Mpv;

/// <summary>
/// The 档位表: which shader chain a playback gets, as a function of five things and nothing else — what the
/// picture is (真人 / 动画), how far it is being enlarged (<see cref="UpscaleTier"/>), how much GPU there is
/// to spend (<see cref="GpuTier"/>), whether the source is DVD-era (<see cref="ShaderTier.IsVintage"/>) and
/// whether its frames arrive too fast for a CNN (<see cref="ShaderTier.IsFastMotion"/>).
/// <para>
/// Eight rows and three columns, doubled by the 老片源 axis and again by the 高帧率 axis. 显卡档 is a property
/// of the machine, so it selects a column rather than adding rows to the menus; the other two are properties
/// of the file, so they do not appear in the menus at all. The eight the user can see and pick from are
/// <see cref="For"/>, and their ids change with none of those three things.
/// </para>
/// <para>
/// <b>This file says which shaders each cell uses; <see cref="ShaderLibrary"/> says what each shader is.</b>
/// The options a chain needs are not written here either — every one of them is a prerequisite some shader
/// states about itself (<see cref="ShaderDescriptor.Requires"/>), so a cell that gains a shader gains its
/// prerequisites automatically. What is left in <see cref="OptionsFor"/> is the three scalers, which belong to
/// the chain as a whole rather than to any one file in it.
/// </para>
/// <para>
/// This replaced five named groups (2K-iGPU, 2K-iGPU-Anime, 2K-iGPU-Light, 2K-iGPU-Anime+, 2K-iGPU-SD)
/// that were chosen by 片源分辨率 alone. Two things were wrong with that: 「4K 片源」 and 「480p 片源」 only
/// mean anything relative to a screen size the client never looked at, and one of the five was carrying
/// <c>FSRCNNX_x1</c>, which does not enlarge anything at all — its group's own comment claimed the
/// arithmetic was being spent on a 2–3× upscale while every enlarged pixel came from
/// <c>scale=ewa_lanczossharp</c>. That particular mistake is now mechanically impossible:
/// <see cref="ShaderChainRules"/> refuses a 亮度放大 that does not declare an output size of its own.
/// </para>
/// <para>
/// <b>Measured on 2026-09-03, external mpv.exe + <c>vo-passes</c> over IPC, 640×360 4:2:0 into a 1280×720
/// window (2.00×), chain <c>ravu-lite-ar-r2 + CfL_Prediction_Lite</c>.</b> Four things came out of it and
/// the table depends on all four; a unit test pins them so they cannot quietly stop being true.
/// <list type="number">
/// <item>Luma changes size in <c>RAVU-Lite-AR (step2)</c>, which is where the 2× happens.</item>
/// <item>CfL's own two passes run <b>after</b> both ravu steps, so the luma it regresses chroma against is
/// the enlarged one — 1280×720, not 640×360.</item>
/// <item>CfL executes cleanly in this chain: shaderc reported 0 errors for every pass, and the screenshots
/// differ (the red/green edge goes from 4 mixed pixels to 3, and the magenta/cyan band visibly narrows).</item>
/// <item>The execution order does not follow the list — mpv runs every LUMA hook before every CHROMA hook
/// either way — but the list still decides which luma CfL binds. With CfL last <b>among the LUMA-stage
/// shaders</b>, chroma comes out at the final size and mpv performs <b>no chroma scaling at all</b> (the two
/// <c>ortho upscaling (spline36)</c> passes present without CfL disappear). With CfL first, chroma comes out at
/// the original size and <c>cscale</c> upscales it. So 色度重建放在亮度放大器后面 is the instruction, and
/// <c>cscale</c> is a no-op in every chain here — which is also the answer to 「以后单独试 bilinear」: there is
/// nothing to try.</item>
/// </list>
/// Chains are written in mpv's own stage order throughout, and a test enforces it — every shipping shader hooks
/// LUMA, CHROMA, POSTKERNEL or OUTPUT, so 色度重建 sits after the upscaler (LUMA) and before any post-sharpen.
/// </para>
/// </summary>
public static class ShaderGroupCatalog
{
    /// <summary>
    /// Where the shader files live: always next to the program. An ordinary build copies exactly the files
    /// <c>assets/shaders</c> holds — see <see cref="ShaderFiles"/> — so both backends load the same files
    /// from the same absolute paths and neither reads the user's own mpv installation at play time.
    /// </summary>
    public static string ShaderRoot => Path.Combine(AppContext.BaseDirectory, "shaders");

    /// <summary>
    /// One row of the table, before the 老片源 axis is applied, in mpv's own stage order.
    /// <para>
    /// <c>SSimDownscaler</c> appears only in 缩小. It used to be in every chain, on the grounds that the
    /// factor was measured against the monitor at full screen and a film in a small window was really being
    /// shrunk whatever tier it was filed under. That reasoning went away when the factor started following
    /// the actual render target: a film in a small window now <b>lands</b> in 缩小 and gets the downscaler,
    /// and carrying it elsewhere would only pull its three prerequisites along for nothing.
    /// </para>
    /// <para>
    /// <paramref name="fastMotion"/> is why the selector is <c>animated &amp;&amp; !fastMotion</c> rather than
    /// <c>animated</c>: above <see cref="ShaderTier.FastMotionFps"/> the 动画 rows use the 真人 chains, because
    /// the only thing that distinguishes them is a CNN upscaler and a CNN is the one shader here whose cost is
    /// measured in tens of milliseconds a frame. On this machine, 1080p into 2560×1440 on vulkan:
    /// <c>ArtCNN_C4F16 + CfL</c> is 22.2 ms a frame, <c>ravu-zoom-ar-r2 + CfL</c> is 7.4 ms and no chain at all is
    /// 7.2 — so 24 fps fits either way and 60 fps only fits the second. The demotion applies to every column, not
    /// just 低档 — the middle and top columns use <c>C4F32</c>, four times the arithmetic of <c>C4F16</c>, so a
    /// card four times faster is exactly break-even and 60 fps needs two and a half times more again.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ShaderDescriptor> Chain(bool animated, UpscaleTier tier, GpuTier gpu, bool fastMotion) =>
        (animated && !fastMotion, tier) switch
        {
            // 缩小 — identical for both halves of the table, and no upscaler belongs in either. 动画 gets no
            // line repair here either: that kind of shader was written to clean a picture up on its way to being
            // enlarged, and the old five groups did not do it for a shrinking 4K animated master either.
            (_, UpscaleTier.Shrink) => gpu switch
            {
                GpuTier.Low => [ShaderLibrary.ChromaLite, ShaderLibrary.SsimDownscaler],
                GpuTier.Medium => [ShaderLibrary.ChromaFull, ShaderLibrary.SsimDownscaler],
                _ => [ShaderLibrary.ChromaFull, ShaderLibrary.SsimDownscaler, ShaderLibrary.AdaptiveSharpen]
            },

            // 真人 · 微放大 — ravu-zoom scales the luma plane straight to the target, which suits a
            // non-integer 1.33× exactly: doubling and shrinking back would compute a whole pass for nothing,
            // and it is the only file here that fires at all across this whole tier (ravu-lite stands aside
            // below 1.414×, ArtCNN below 1.3×).
            // This is the cell this machine spends most of its time in (1080p on a 1440p screen).
            (false, UpscaleTier.Slight) => gpu switch
            {
                GpuTier.Low => [ShaderLibrary.RavuZoom, ShaderLibrary.ChromaLite],
                GpuTier.Medium => [ShaderLibrary.RavuZoom, ShaderLibrary.ChromaFull],
                _ => [ShaderLibrary.RavuZoom, ShaderLibrary.ChromaFull, ShaderLibrary.SsimSuperRes]
            },

            // 真人 · 甜点 — a 2× upscaler's home ground, and where extra arithmetic pays best. ravu-lite is
            // luma-only, sharper and cheaper than ravu-zoom, and introduces no half-pixel offset.
            (false, UpscaleTier.Sweet) => gpu switch
            {
                GpuTier.Low => [ShaderLibrary.RavuLiteR2, ShaderLibrary.ChromaLite],
                GpuTier.Medium => [ShaderLibrary.RavuLiteR3, ShaderLibrary.ChromaFull, ShaderLibrary.SsimSuperRes],
                _ => [ShaderLibrary.RavuLiteR4, ShaderLibrary.ChromaFull, ShaderLibrary.SsimSuperRes]
            },

            // 真人 · 大倍数 — one 2× pass cannot cover the factor, and the remainder goes to
            // scale=ewa_lanczossharp rather than to a second neural upscaler. Cleaning up the source is the
            // 老片源 axis's business, not this tier's: a 720p web rip blown up to 4K is 3× and perfectly
            // clean, while the DVD that needs the cleaning may land in 甜点 instead.
            (false, UpscaleTier.Large) => gpu switch
            {
                GpuTier.Low => [ShaderLibrary.RavuLiteR2, ShaderLibrary.ChromaLite],
                GpuTier.Medium => [ShaderLibrary.RavuLiteR3, ShaderLibrary.ChromaFull],
                _ => [ShaderLibrary.RavuLiteR4, ShaderLibrary.ChromaFull, ShaderLibrary.AdaptiveSharpen]
            },

            // 动画 · 微放大 and 动画 · 甜点 — ArtCNN throughout, the maintained line for animated upscaling
            // (Anime4K's last release was 2021-10). C4F16 in the low column is what a Vega iGPU holds — and that
            // is true on vulkan only: 22.2 ms a frame there against 114.6 on d3d11, measured 2026-09-04, which is
            // why 图形接口 ships as vulkan and why ShaderTier.FastMotionFps exists. It is there since 2026-09-04:
            // 「动画换 ArtCNN」 — the user's own words, after seeing the first batch.
            // It was held back until then because swapping an upscaler is the one change in this table that
            // alters how a picture looks rather than how sharp it is (任务书 0.4), which was his call to make.
            // ArtCNN's 1.3× gate means the 1.05–1.30 stretch of 微放大 falls through to scale=ewa_lanczossharp
            // — worth knowing, not worth avoiding: 1080p→1440p is 1.33×, the factor this machine lives at.
            (true, UpscaleTier.Slight) => gpu switch
            {
                GpuTier.Low => [ShaderLibrary.ArtCnnF16, ShaderLibrary.ChromaLite],
                GpuTier.Medium => [ShaderLibrary.ArtCnnF32, ShaderLibrary.ChromaFull],
                _ => [ShaderLibrary.ArtCnnF32Ds, ShaderLibrary.ChromaFull]
            },

            (true, UpscaleTier.Sweet) => gpu switch
            {
                GpuTier.Low => [ShaderLibrary.ArtCnnF16, ShaderLibrary.ChromaLite],
                GpuTier.Medium => [ShaderLibrary.ArtCnnF32, ShaderLibrary.ChromaFull],
                _ => [ShaderLibrary.ArtCnnF32Ds, ShaderLibrary.ChromaFull, ShaderLibrary.AdaptiveSharpen]
            },

            // 动画 · 大倍数 — the DN weights (denoise + soften) rather than DS: a 480p animated transfer's
            // problem is noise and banding, and sharpening those is the wrong direction. The low column takes
            // C4F16, the same file as the two cells above and what a Vega iGPU holds.
            //
            // This cell was Anime4K Mode A in the low column until 2026-09-04, when the user answered the
            // question the table had left open with 「换 ArtCNN」 — the second time, after the two cells above
            // had moved. What it loses is real and is worth writing down: Mode A was the only chain here built
            // for multi-step enlargement (two chained CNN passes, AutoDownscalePre stopping once the picture is
            // big enough), so above 2.2× one ArtCNN pass covers less than half the factor and the rest falls to
            // scale=ewa_lanczossharp. That is exactly what 真人 · 大倍数 already does with ravu-lite, so both
            // halves of the table now answer 「一个 2× 不够怎么办」 the same way — and Anime4K left the box with
            // this change, since no cell named it any more.
            _ => gpu switch
            {
                GpuTier.Low => [ShaderLibrary.ArtCnnF16, ShaderLibrary.ChromaLite],
                GpuTier.Medium => [ShaderLibrary.ArtCnnF32Dn, ShaderLibrary.ChromaFull],
                _ => [ShaderLibrary.ArtCnnF32Dn, ShaderLibrary.ChromaFull, ShaderLibrary.AdaptiveSharpen]
            }
        };

    /// <summary>
    /// What a DVD-era source adds in front of the row — 任务书 2.2's independent axis. <c>hdeband</c> in every
    /// column because it is 6.5 KB and the banding it fixes is on screen at every factor; <c>nlmeans_light</c>
    /// only above the low column, because it has no gate of its own and runs on luma and chroma both.
    /// <para>
    /// In front, not appended: these two are the only files here that want to see the picture before anything
    /// else has touched it, and enlarging noise is what the other order does. Both hook LUMA, so this is also
    /// where mpv runs them.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ShaderDescriptor> VintagePrefix(GpuTier gpu) =>
        gpu == GpuTier.Low
            ? [ShaderLibrary.Hdeband]
            : [ShaderLibrary.Hdeband, ShaderLibrary.NonLocalMeans];

    /// <summary>
    /// The mpv options a chain needs: the three scalers, which belong to the chain as a whole, plus whatever
    /// its shaders each state they cannot work without. Forty-eight hand-written option lists would be
    /// forty-eight chances for one to disagree with the shaders next to it — which is the
    /// 「SSimDownscaler 缺了 dscale=mitchell」 shape of problem in the first place.
    /// <para>
    /// Nothing here looks at a file name. Every option beyond the three below is a
    /// <see cref="ShaderDescriptor.Requires"/> entry, which is 任务书 6's 「着色器的前置 mpv 选项」 — the
    /// abstraction it says is the right one — and the reason a cell that gains a shader gains its
    /// prerequisites without anybody remembering to.
    /// </para>
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, string>> OptionsFor(IReadOnlyList<ShaderDescriptor> chain)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The remainder scaler. ravu-zoom outputs the target size and never reaches this; the ravu-lite
            // and ArtCNN chains hand it whatever a 2× pass did not cover, plus the whole of the stretch below
            // their own gates; the 缩小 chains never reach it at all. Named in every cell because the
            // alternative is 「上一部片子设过的值」.
            ["scale"] = "ewa_lanczossharp",

            // 色度. Measured to be a no-op in every chain here — CfL's last pass hands mpv chroma at the
            // final size, and mpv then performs no chroma scaling whatever (see the class remarks). It is
            // named anyway for the same reason as scale: the value that would otherwise be in force is the
            // previous film's. It is also what makes a chain switched off mid-film restore cleanly.
            ["cscale"] = "spline36",

            // 光域. mpv's own default, and what every chain here runs with: no shipping shader asks for anything
            // else since Anime4K left the box on 2026-09-04 — its two CNN upscale passes were the only ones, and
            // they asked because the user's own mpv.conf did. Named rather than left alone for the same reason as
            // the two above, and a shader that needs it off says so on itself (ShaderDescriptor.Requires) rather
            // than being special-cased here.
            ["sigmoid-upscaling"] = "yes"
        };

        foreach (var shader in chain)
        {
            foreach (var (name, value) in shader.Requires) map[name] = value;
        }

        // Deterministic order: the three above as written, then the prerequisites alphabetically. The launch
        // log prints this list and a test compares it, so 「whatever order the dictionary felt like」 is not an
        // option.
        return
        [
            .. LeadOptions.Select(name => new KeyValuePair<string, string>(name, map[name])),
            .. map.Where(pair => !LeadOptions.Contains(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        ];
    }

    private static readonly string[] LeadOptions = ["scale", "cscale", "sigmoid-upscaling"];

    // ---- what the rest of the app asks for --------------------------------------------------------

    /// <summary>
    /// The eight rows a user can see, in the order both menus list them: 真人 by rising factor, then 动画.
    /// </summary>
    private static readonly (bool Animated, UpscaleTier Tier)[] Rows =
    [
        (false, UpscaleTier.Shrink), (false, UpscaleTier.Slight), (false, UpscaleTier.Sweet), (false, UpscaleTier.Large),
        (true, UpscaleTier.Shrink), (true, UpscaleTier.Slight), (true, UpscaleTier.Sweet), (true, UpscaleTier.Large)
    ];

    /// <summary>
    /// The four kinds of file the table distinguishes, in the order <see cref="Kind"/> numbers them. Both are
    /// properties of what is being played rather than of what the user picked, which is why they multiply the
    /// cached columns instead of appearing in a menu.
    /// </summary>
    private static readonly (bool Vintage, bool FastMotion)[] Kinds =
        [(false, false), (true, false), (false, true), (true, true)];

    private static int Kind(bool vintage, bool fastMotion) => (vintage ? 1 : 0) + (fastMotion ? 2 : 0);

    /// <summary>
    /// One column of the table per (显卡档, 老片源, 高帧率), built once. Indexed by <see cref="GpuTier"/>'s value
    /// and then by <see cref="Kind"/> — 3 × 4 × 8 = 96 chains, of which any one playback can reach eight.
    /// </summary>
    private static readonly ShaderGroup[][][] Columns =
    [
        Column(GpuTier.Low), Column(GpuTier.Medium), Column(GpuTier.High)
    ];

    private static ShaderGroup[][] Column(GpuTier gpu) =>
    [
        .. Kinds.Select(kind =>
            Rows.Select(row => Make(row.Animated, row.Tier, gpu, kind.Vintage, kind.FastMotion)).ToArray())
    ];

    private static ShaderGroup Make(bool animated, UpscaleTier tier, GpuTier gpu, bool vintage, bool fastMotion)
    {
        var chain = Chain(animated, tier, gpu, fastMotion);
        IReadOnlyList<ShaderDescriptor> shaders = vintage ? [.. VintagePrefix(gpu), .. chain] : chain;
        return new ShaderGroup(animated, tier, vintage, fastMotion, shaders, OptionsFor(shaders));
    }

    /// <summary>
    /// The eight chains a playback of this kind of file on this machine can use. What the settings dropdown
    /// and the player's 着色器 submenu are projections of — there is no second list of chains anywhere.
    /// </summary>
    public static IReadOnlyList<ShaderGroup> For(GpuTier gpu, bool vintage = false, bool fastMotion = false) =>
        Columns[Enum.IsDefined(gpu) ? (int)gpu : 0][Kind(vintage, fastMotion)];

    /// <summary>All ninety-six chains. For the tests and for <see cref="ShaderFiles"/>, not for a menu.</summary>
    public static IReadOnlyList<ShaderGroup> All { get; } =
        [.. Columns.SelectMany(column => column.SelectMany(chains => chains))];

    /// <summary>The eight ids, in menu order. Stable across 显卡档, 老片源 and 高帧率 — see <see cref="ShaderGroup.Id"/>.</summary>
    public static IReadOnlyList<string> Ids { get; } = [.. Columns[0][0].Select(group => group.Id)];

    /// <summary>The chain for one cell.</summary>
    public static ShaderGroup Resolve(
        bool animated, UpscaleTier tier, GpuTier gpu, bool vintage = false, bool fastMotion = false) =>
        For(gpu, vintage, fastMotion).First(group => group.Animated == animated && group.Tier == tier);

    /// <summary>
    /// The chain a stored id names for this machine and this file, or null for an empty id or one this table
    /// never had.
    /// </summary>
    public static ShaderGroup? Find(string? id, GpuTier gpu, bool vintage = false, bool fastMotion = false)
    {
        var trimmed = (id ?? "").Trim();
        return trimmed.Length == 0
            ? null
            : For(gpu, vintage, fastMotion)
                .FirstOrDefault(group => string.Equals(group.Id, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// mpv's own default for every option any chain touches, so switching chains mid-playback cannot leave a
    /// previous one's scaler — or its shader list — behind. The values are what
    /// <c>mpv --no-config --list-options</c> reports for this build; <c>cscale</c> really is the empty string,
    /// which means 「follow scale」, and an empty <c>glsl-shaders</c> is an empty chain.
    /// <para>
    /// The list holds exactly the names some chain sets, and a test pins that both ways. A name missing here
    /// is an option that stays applied for the rest of the file after its chain is switched off; a name here
    /// that no chain sets is a property written for nothing on every switch — which is how
    /// <c>scale-antiring</c>, <c>dscale-antiring</c> and <c>linear-upscaling</c> were left behind when the
    /// nine ported groups went.
    /// </para>
    /// <para>
    /// <c>deband</c> is in the list, which it deliberately was not before. It is here because a 老片源 chain
    /// loads hdeband and has to switch mpv's own debander off, and it is safe here because
    /// <see cref="ShaderSwitch.Options"/> falls back to <b>this playback's launch value</b> for every neutral
    /// name before applying the new chain — so leaving an hdeband chain restores whatever 去色带 was set to,
    /// not mpv's factory default.
    /// </para>
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> NeutralOptions { get; } =
    [
        new("glsl-shaders", ""),
        new("scale", "lanczos"),
        new("cscale", ""),
        new("dscale", "hermite"),
        new("linear-downscaling", "yes"),
        new("correct-downscaling", "yes"),
        new("sigmoid-upscaling", "yes"),
        new("deband", "no")
    ];

    /// <summary>
    /// Every shader file the table names, deduplicated, in a stable order. <b>The single source for the file
    /// list.</b> A unit test compares it with what <c>assets/shaders</c> actually holds, both ways round: a
    /// file the table names and the repository lacks is mpv logging a load failure per frame while the picture
    /// merely 「looks a bit off」, and a file the repository holds and no chain names is weight nobody asked for.
    /// </summary>
    public static IReadOnlyList<string> ShaderFiles { get; } =
    [
        .. All.SelectMany(group => group.Files)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>
    /// Shader files the table names but that are not on disk. Used by the self-check and by
    /// <c>ShaderStaging</c>, which warns rather than fixing it: the files ship with an ordinary build now.
    /// </summary>
    public static IReadOnlyList<string> MissingShaderFiles()
    {
        var root = ShaderRoot;
        return
        [
            .. ShaderFiles
                .Select(shader => Path.Combine(root, shader.Replace('/', Path.DirectorySeparatorChar)))
                .Where(path => !File.Exists(path))
        ];
    }
}
