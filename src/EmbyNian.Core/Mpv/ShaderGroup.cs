namespace EmbyNian.Mpv;

/// <summary>
/// One cell of the 档位表: the shader chain for a given kind of picture, scale factor and 显卡档, plus the
/// scaler options that chain depends on.
/// <para>
/// These were named groups until 2026-09-03 — five of them, picked by hand in the settings page and chosen
/// automatically by 片源分辨率. A name is the wrong unit: it made 「哪一组」 a preference, when it is a
/// calculation with one right answer (see <see cref="ShaderTier.Measure"/>). What is left of the old shape
/// is <see cref="Id"/>, which is stable across 显卡档 so a hand-picked override survives changing that
/// setting, and everything the two menus need to draw a row.
/// </para>
/// </summary>
/// <param name="Animated">Which half of the table: 动画 chains repair lines and use CNN upscalers.</param>
/// <param name="Tier">Which row: how far the picture is being enlarged.</param>
/// <param name="Vintage">
/// Whether the 老片源 axis was applied — the source is 576 lines or shorter, so the chain carries
/// <c>hdeband</c> (and <c>nlmeans_light</c> above the low column) ahead of everything else. Held as a field
/// rather than worked out from the file names: 「链里有没有 hdeband」 and 「这个片源是不是老片源」 are two
/// different questions and only one of them is a decision.
/// </param>
/// <param name="FastMotion">
/// Whether the 高帧率 axis was applied — the source runs above <see cref="ShaderTier.FastMotionFps"/>, so the
/// 动画 rows drop the CNN upscaler and take the same chain the 真人 rows use. <see cref="Animated"/> stays true
/// through it: the film is still animated, only the arithmetic no longer fits at that frame rate, and the id,
/// the menus and the log line all keep saying 动画 — otherwise the page would be lying about what was detected,
/// and a hand-picked 动画 row would silently move to the other half of the table.
/// </param>
/// <param name="Shaders">
/// Shader files relative to <see cref="ShaderGroupCatalog.ShaderRoot"/>, written in mpv's own pipeline
/// order — 去带 → 降噪 → 亮度放大 → 后置锐化 → 色度重建 → OUTPUT.
/// <para>
/// <b>The order is not merely cosmetic, and it is not entirely up to this list either</b> — both halves of
/// that were measured on 2026-09-03 with the external mpv.exe and <c>vo-passes</c> (see
/// <see cref="ShaderGroupCatalog"/>). mpv runs every LUMA hook before every CHROMA hook whatever order they
/// are listed in, so <c>ravu</c> before <c>CfL</c> in this list is describing what happens rather than
/// causing it. What the list <i>does</i> decide is which luma a CHROMA hook binds: CfL listed after the
/// upscaler predicts chroma from the enlarged luma and hands mpv chroma at the final size, while CfL listed
/// first predicts from the original luma and leaves <c>cscale</c> to finish the job. So 「色度重建放最后」 is
/// a real instruction, and the pipeline order is both the readable order and the correct one.
/// </para>
/// </param>
/// <param name="Options">
/// The scalers and toggles the chain depends on, derived from the chain itself by
/// <see cref="ShaderGroupCatalog"/> rather than written out per cell. SSimDownscaler simply does not work
/// without <c>dscale=mitchell</c> and <c>linear-downscaling=no</c>, and hdeband fights mpv's own
/// <c>deband</c>; a cell that shipped without its options would look worse than no chain at all.
/// </param>
public sealed record ShaderGroup(
    bool Animated,
    UpscaleTier Tier,
    bool Vintage,
    bool FastMotion,
    IReadOnlyList<ShaderDescriptor> Shaders,
    IReadOnlyList<KeyValuePair<string, string>> Options)
{
    /// <summary>
    /// What settings.json stores for a hand-picked override, and what the player menu ticks by. Derived
    /// from the two axes that identify a row, so it does <b>not</b> change when 显卡档 changes — that
    /// setting picks a different chain for the same id, which is exactly what it should do. <see
    /// cref="Vintage"/> and <see cref="FastMotion"/> are left out for the same reason: they follow the file
    /// being played, not the choice.
    /// </summary>
    public string Id => $"{(Animated ? "anime" : "live")}-{Tier.ToString().ToLowerInvariant()}";

    public string Name => $"{(Animated ? "动画" : "真人")} · {ShaderTier.Describe(Tier)}";

    /// <summary>Row text in the settings dropdown and the player's 着色器 submenu.</summary>
    public string DisplayName => $"{Name}（{ShaderTier.Range(Tier)}）";

    /// <summary>
    /// What this chain actually loads, generated from the chain rather than written by hand: a description
    /// that can disagree with the chain is a description nobody can trust, and this one is shown in the player
    /// menu's dim right-hand column while a film is being A/B'd.
    /// </summary>
    public string Description => string.Join(" + ", ShaderFileNames);

    /// <summary>Shader names as the tables in the notes spell them.</summary>
    public IEnumerable<string> ShaderFileNames => Shaders.Select(shader => shader.Name);

    /// <summary>The files this chain loads, relative to the shader root and in the order it loads them.</summary>
    public IEnumerable<string> Files => Shaders.Select(shader => shader.File);

    /// <summary>
    /// How many distinct shader packages the chain applies in one role — 任务书 4: 「着色器包内部自带的多 pass
    /// 算一个，不算多个」. Every shipping shader is one file and one decision today, so this is a count of files;
    /// it counts <see cref="ShaderDescriptor.Package"/> rather than files so that a multi-file family added later
    /// still counts once, without a special case in the rules.
    /// </summary>
    public int Packages(ShaderRole role) =>
        Shaders.Where(shader => shader.Role == role).Select(shader => shader.Package).Distinct(StringComparer.Ordinal).Count();

    /// <summary>The chain as absolute paths under <paramref name="shaderRoot"/>.</summary>
    public IReadOnlyList<string> ResolveShaderPaths(string shaderRoot) =>
        [.. Files.Select(file => Path.Combine(shaderRoot, file.Replace('/', Path.DirectorySeparatorChar)))];

    /// <summary>
    /// <c>glsl-shaders</c> plus the chain's own options, ready to append to the option list.
    /// The paths are absolute: with <c>--no-config</c> there is no config directory left for mpv to
    /// resolve a <c>~~/</c> prefix against, so a relative entry would silently load nothing.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToMpvOptions(string shaderRoot)
    {
        var options = new List<KeyValuePair<string, string>>(Options.Count + 1)
        {
            new("glsl-shaders", MpvListValue.JoinFiles(ResolveShaderPaths(shaderRoot)))
        };
        options.AddRange(Options);
        return options;
    }

    public override string ToString() => DisplayName;
}
