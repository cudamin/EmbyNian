namespace EmbyNian.Mpv;

/// <summary>
/// The rules every chain in the 档位表 has to satisfy, as a function anything can call — 任务书 5.4. Written
/// against <see cref="ShaderDescriptor.Role"/> rather than against file names or pass counts, which is the
/// point: 「判断针对 Descriptor 的逻辑职责，不是数底层 pass 文件个数」.
/// <para>
/// It reports rather than throws. A unit test walks every chain in the table through it, and the self-check runs
/// it against the chains the shipped build actually offers — so a violation is a red gate rather than a crash
/// in front of the user, and the message says which cell and which rule.
/// </para>
/// </summary>
public static class ShaderChainRules
{
    /// <summary>
    /// Everything wrong with one chain, in plain words, or an empty list. Each rule exists because breaking it
    /// produced a real defect, or would produce one that nothing else here could see.
    /// </summary>
    public static IReadOnlyList<string> Problems(ShaderGroup group)
    {
        var problems = new List<string>();

        // 三条互斥（任务书 4 和 5.4）. Counted as packages rather than files, so a multi-file family counts as
        // one upscaler — see ShaderDescriptor.Bundle.
        Only(ShaderRole.LumaUpscale, "亮度放大器");
        Only(ShaderRole.PostSharpen, "后置锐化");
        Only(ShaderRole.Denoise, "独立降噪");
        Only(ShaderRole.Deband, "去带");
        Only(ShaderRole.ChromaReconstruct, "色度重建");

        // 「它是不是放大器」 must not be a guess about a file name. This is the rule that would have caught
        // FSRCNNX_x1_16-0-4-1_distort.glsl, which shipped for months as the 低清 group's upscaler while
        // declaring no output size of its own and enlarging nothing at all.
        foreach (var shader in group.Shaders.Where(shader => shader.Role == ShaderRole.LumaUpscale && !shader.ChangesResolution))
            problems.Add($"{shader.Name} 说自己是亮度放大器，可它没有声明任何输出尺寸 —— 它放不大任何东西");

        // 缩小档不该有放大器：那一档在缩小，一个放大器只会白占带宽（而且大多数会自己门控掉，于是既不出力也说不清）。
        if (group.Tier == UpscaleTier.Shrink && group.Packages(ShaderRole.LumaUpscale) > 0)
            problems.Add("缩小档挂了亮度放大器");

        // 每一格都要做色度重建 —— 4:2:0 的色度横竖都只有亮度的一半，而从前那五个组一个都没有。
        if (group.Packages(ShaderRole.ChromaReconstruct) == 0) problems.Add("这一格没有色度重建");

        // hdeband 和 mpv 内置的 deband 一起开是互相打架，所以带 hdeband 的链必须把后者关掉；反过来，没有
        // hdeband 的链不该去动设置页那一项。
        var options = group.Options.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var deband = group.Shaders.Any(shader => shader.Role == ShaderRole.Deband);
        var debandOff = options.TryGetValue("deband", out var value) && value == "no";

        if (deband && !debandOff) problems.Add("链里有去带着色器，但没有把 mpv 内置的 deband 关掉");
        if (!deband && options.ContainsKey("deband")) problems.Add("链里没有去带着色器，却动了设置页的去色带");

        // 每个着色器的运行前置条件都得真的落在选项里。丢一条的下场比不挂那个着色器更差 —— SSimDownscaler 没有
        // dscale=mitchell 就是这样。
        foreach (var shader in group.Shaders)
        {
            foreach (var (name, wanted) in shader.Requires)
            {
                if (!options.TryGetValue(name, out var got) || got != wanted)
                    problems.Add($"{shader.Name} 要求 {name}={wanted}，链给出的是 {got ?? "（没给）"}");
            }
        }

        // 链必须照 mpv 真正的执行次序写. mpv 跑完所有 LUMA 钩子才跑 CHROMA，跑完 CHROMA 才是合并之后的 MAIN，
        // 最后才是 POSTKERNEL 和 OUTPUT —— 所以「写在前面」只有在同一个钩子阶段内才等于「先跑」。乱序的链不会
        // 跑错，但它的注释会骗人，而 CfL 那件事正是靠「同一阶段内的次序真的有意义」才成立的。
        for (var index = 1; index < group.Shaders.Count; index++)
        {
            var previous = group.Shaders[index - 1];
            var current = group.Shaders[index];

            if (current.Stage < previous.Stage)
                problems.Add($"{current.Name}（{current.Hook}）写在了 {previous.Name}（{previous.Hook}）后面，而 mpv 先跑前者");
        }

        return problems;

        void Only(ShaderRole role, string what)
        {
            var count = group.Packages(role);
            if (count > 1) problems.Add($"挂了 {count} 个{what}");
        }
    }

    /// <summary>Every chain in the table that breaks a rule, as 「格名：毛病」 lines. Empty when all is well.</summary>
    public static IReadOnlyList<string> ProblemsInTable() =>
    [
        .. ShaderGroupCatalog.All.SelectMany(group =>
            Problems(group).Select(problem => $"{group.Name}{Kind(group)}：{problem}"))
    ];

    private static string Kind(ShaderGroup group) => (group.Vintage, group.FastMotion) switch
    {
        (true, true) => "（老片源、高帧率）",
        (true, false) => "（老片源）",
        (false, true) => "（高帧率）",
        _ => ""
    };
}
