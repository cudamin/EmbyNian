namespace EmbyNian.Mpv;

/// <summary>
/// One 「着色器配置组」: a named shader chain plus the scaler options that chain needs.
/// <para>
/// These used to be mpv <c>[profile]</c> sections in a generated <c>embynian.conf</c> that the
/// client pulled in with <c>--include</c> and activated with <c>--profile</c>. They are plain C# data
/// now: nothing has to be written to disk, parsed back, or kept in step with a file the user might
/// have edited, and the same list works for both backends — libmpv has no <c>--include</c> at all.
/// </para>
/// </summary>
/// <param name="Shaders">Shader files relative to the shader root, in the order mpv applies them.</param>
/// <param name="Options">
/// The scalers and toggles the chain depends on. They are part of the group rather than of 视频输出:
/// SSimDownscaler simply does not work without <c>dscale=mitchell</c> and <c>linear-downscaling=no</c>,
/// so a group that shipped without them would look worse than no group at all.
/// </param>
public sealed record ShaderGroup(
    string Name,
    string Description,
    IReadOnlyList<string> Shaders,
    IReadOnlyList<KeyValuePair<string, string>> Options)
{
    /// <summary>Shader file names without directories, for a compact tooltip.</summary>
    public IEnumerable<string> ShaderFileNames =>
        Shaders.Select(shader => shader.Split('/', '\\').LastOrDefault() ?? shader);

    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : $"{Name} — {Description}";

    /// <summary>The chain as absolute paths under <paramref name="shaderRoot"/>.</summary>
    public IReadOnlyList<string> ResolveShaderPaths(string shaderRoot) =>
        [.. Shaders.Select(shader => Path.Combine(shaderRoot, shader.Replace('/', Path.DirectorySeparatorChar)))];

    /// <summary>
    /// <c>glsl-shaders</c> plus the group's own options, ready to append to the option list.
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

/// <summary>
/// The shader groups that ship with the client.
/// <para>
/// All five target this machine — a Ryzen 5600G with Vega graphics driving a 1440p screen, playing
/// 1080p and 4K sources. Everything is sized for that: a 1080p source needs a 1.33× upscale, which is
/// cheap enough for a decent luma upscaler, while a 4K source is only ever downscaled, so no upscaling
/// shader in the chain would ever fire. The automatic rules choose from these five, and the settings
/// page and the player's own 着色器 menu offer the same five for picking by hand.
/// </para>
/// <para>
/// Nine more used to follow them — the <c>[profile]</c> sections ported from the user's own
/// <c>mpv.conf</c> under their original names (NNEDI3、NNEDI3+、ravu-zoom、FSRCNNX、AnimeJaNai、Ani4K、
/// AniSD、Anime4K、SSIM). He asked for them to be deleted on 2026-09-03. A settings file that still
/// names one of them is not broken by that: the group check in <c>SettingsMigration</c> maps a name this
/// catalogue no longer has back to the built-in default.
/// </para>
/// </summary>
public static class ShaderGroupCatalog
{
    /// <summary>Group applied to everything when 「所有视频」 is on.</summary>
    public const string DefaultGroupName = "2K-iGPU";

    /// <summary>Group applied when the item's metadata looks animated.</summary>
    public const string AnimeGroupName = "2K-iGPU-Anime";

    /// <summary>Group applied instead of the other two when the source is 4K.</summary>
    public const string HighResGroupName = "2K-iGPU-Light";

    /// <summary>Group applied instead of the other two when the source is 480p/576p/720p.</summary>
    public const string LowResGroupName = "2K-iGPU-SD";

    /// <summary>
    /// Where the <c>.glsl</c> files live: always next to the program. The publish step copies in exactly
    /// the files the groups below name — nothing else from the user's mpv collection — so both backends
    /// load the same files and neither reads the user's mpv installation at play time.
    /// </summary>
    public static string ShaderRoot => Path.Combine(AppContext.BaseDirectory, "shaders");

    /// <summary>
    /// Scalers shared by every group. <c>dscale</c>/<c>linear-downscaling</c>/<c>correct-downscaling</c>
    /// are what SSimDownscaler requires; <c>cscale=spline36</c> is a cheap win on chroma that costs
    /// nothing measurable on an iGPU.
    /// </summary>
    private static readonly KeyValuePair<string, string>[] SharedDownscale =
    [
        new("cscale", "spline36"),
        new("dscale", "mitchell"),
        new("linear-downscaling", "no"),
        new("correct-downscaling", "yes")
    ];

    public static IReadOnlyList<ShaderGroup> All { get; } =
    [
        // ravu-zoom scales the luma plane straight to the target size, which suits a non-integer
        // 1.33× exactly: doubling and shrinking back would compute a whole pass for nothing. r2 is
        // the version with headroom for an iGPU — visibly cheaper than r3 for a very small loss.
        // SSimDownscaler only fires when something has to shrink (a 4K source, or a small window),
        // so carrying it costs nothing on the 1080p case this group is for.
        new(DefaultGroupName,
            "1080p 实拍放大",
            ["ravu/ravu-zoom-ar-r2.glsl", "igv/SSimDownscaler.glsl"],
            [
                // ravu-zoom already output the target size; this is only a fallback.
                new("scale", "ewa_lanczossharp"),
                .. SharedDownscale,
                new("sigmoid-upscaling", "yes")
            ]),

        // Anime4K Mode A (Fast). The official order: repair the lines (Restore CNN), then upscale
        // twice, with AutoDownscalePre in between so it stops once the picture is already big enough.
        // The M/S CNN sizes are what an iGPU holds at 24–30fps; Clamp_Highlights leads so the CNN
        // cannot blow out highlights.
        new(AnimeGroupName,
            "动画（Anime4K Fast）",
            [
                "Anime4K/glsl/Restore/Anime4K_Clamp_Highlights.glsl",
                "Anime4K/glsl/Restore/Anime4K_Restore_CNN_M.glsl",
                "Anime4K/glsl/Upscale/Anime4K_Upscale_CNN_x2_M.glsl",
                "Anime4K/glsl/Upscale/Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K/glsl/Upscale/Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K/glsl/Upscale/Anime4K_Upscale_CNN_x2_S.glsl",
                "igv/SSimDownscaler.glsl"
            ],
            [
                new("scale", "ewa_lanczossharp"),
                .. SharedDownscale,
                // The CNN upscalers do their own linear/gamma handling; a second layer greys it out.
                new("sigmoid-upscaling", "no")
            ]),

        // 4K → 1440p is downscaling the whole way, so every upscaler in a chain would sit idle and
        // only cost bandwidth. SSimDownscaler is the one thing doing real work here, and it is
        // sharper than plain mitchell for almost no cost.
        new(HighResGroupName,
            "4K 片源省电缩放",
            ["igv/SSimDownscaler.glsl"],
            [
                // Never reached: the output is always smaller than the source.
                new("scale", "bilinear"),
                .. SharedDownscale
            ]),

        // Cleaner lines and screentones than the Anime4K chain above, and correspondingly more
        // expensive. Usually fine at 24fps on an iGPU; a 60fps OP/ED may drop frames, in which case go
        // back to 2K-iGPU-Anime.
        new("2K-iGPU-Anime+",
            "动画（ArtCNN，较吃性能）",
            ["Ani4K/Ani4Kv2_ArtCNN_C4F32_i2.glsl", "igv/SSimDownscaler.glsl"],
            [
                new("scale", "ewa_lanczossharp"),
                .. SharedDownscale
            ]),

        // 480p/576p/720p: a 2–3× upscale is where the extra arithmetic finally shows on screen,
        // so this one spends it on FSRCNNX.
        new(LowResGroupName,
            "低清片源增强",
            ["igv/FSRCNNX_x1_16-0-4-1_distort.glsl", "igv/SSimDownscaler.glsl"],
            [
                new("scale", "ewa_lanczossharp"),
                .. SharedDownscale,
                new("sigmoid-upscaling", "yes")
            ])
    ];

    /// <summary>Group names in catalogue order.</summary>
    public static IReadOnlyList<string> Names => [.. All.Select(group => group.Name)];

    /// <summary>
    /// mpv's own default for every option a group touches, so switching groups mid-playback cannot
    /// leave a previous group's scaler — or its shader chain — behind. The values are what
    /// <c>mpv --no-config --list-options</c> reports for this build; <c>cscale</c> really is the empty
    /// string, which means 「follow scale」, and an empty <c>glsl-shaders</c> is an empty chain.
    /// <para>
    /// Anything added to a group's <see cref="ShaderGroup.Options"/> has to appear here too, or turning
    /// that group off would keep its option applied for the rest of the file.
    /// </para>
    /// <para>
    /// <c>deband</c> is deliberately absent: no group sets it any more, because 去色带 —
    /// 「在动画中开启」 included — is the settings page's business, and resetting it here would undo
    /// the launch value every time the user picked a group from the player menu.
    /// </para>
    /// <para>
    /// The list holds nothing beyond what a group actually sets, and a test pins that both ways. An extra
    /// name here is a name every group switch writes for no reason — <c>scale-antiring</c>,
    /// <c>dscale-antiring</c> and <c>linear-upscaling</c> were in it for the nine ported groups and left
    /// with them.
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
        new("sigmoid-upscaling", "yes")
    ];

    /// <summary>The group of that name, or null for an empty name or one that no longer exists.</summary>
    public static ShaderGroup? Find(string? name)
    {
        var trimmed = (name ?? "").Trim();
        return trimmed.Length == 0
            ? null
            : All.FirstOrDefault(group => string.Equals(group.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Shader files a group names but that are not on disk. Used by the self-check: a group whose
    /// <c>.glsl</c> files are missing makes mpv log a load failure per frame and render nothing extra.
    /// </summary>
    public static IReadOnlyList<string> MissingShaderFiles()
    {
        var root = ShaderRoot;
        return
        [
            .. All.SelectMany(group => group.ResolveShaderPaths(root))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !File.Exists(path))
        ];
    }
}
