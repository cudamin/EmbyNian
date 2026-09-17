using EmbyNian.Configuration;

namespace EmbyNian.Playback;

/// <summary>
/// The in-process renderer's final authority, applied after the planner's baseline, presets,
/// output settings and shader chain. Does not change the external mpv.exe option list.
/// </summary>
internal static class LibMpvPipelinePolicy
{
    internal static bool RequiresSurface(VideoPipelineKind pipeline) => pipeline switch
    {
        VideoPipelineKind.Integrated => true,
        VideoPipelineKind.Standalone => false,
        _ => throw new ArgumentOutOfRangeException(nameof(pipeline), pipeline, "未知渲染管线")
    };

    /// <summary>
    /// Keep ordinary options in order (including duplicates and list operations), remove all
    /// client-owned rendering/input options, then append the required pipeline contract.
    /// In particular, never even send wid or composition-size in the standalone pipeline.
    /// </summary>
    internal static IReadOnlyList<LibMpvPipelineOption> Build(
        VideoPipelineKind pipeline,
        IReadOnlyList<KeyValuePair<string, string>> playerOptions,
        (int Width, int Height) surfaceSize = default)
    {
        var integrated = RequiresSurface(pipeline);
        var options = new List<LibMpvPipelineOption>();
        foreach (var (name, value) in playerOptions)
        {
            if (!IsOwnedOption(name)) options.Add(new(name, value));
        }

        // These names/values are verified against the bundled fork, not upstream's "flipping".
        // A single renderer (no fallback list) prevents quietly switching to a different pipeline.
        options.Add(new("vo", "gpu-next", Required: true));
        options.Add(new("gpu-api", "d3d11", Required: true));
        options.Add(new("gpu-context", "d3d11", Required: true));
        options.Add(new("d3d11-output-mode", integrated ? "composition" : "window", Required: true));
        // window/auto alone still uses DWM. This requests exclusive fullscreen when mpv's
        // fullscreen state is enabled; it does NOT claim DXGI/the driver granted exclusivity.
        options.Add(new("d3d11-exclusive-fs", integrated ? "no" : "yes", Required: true));
        options.Add(new("force-window", "immediate", Required: true));
        options.Add(new("input-default-bindings", integrated ? "no" : "yes", Required: true));
        options.Add(new("input-vo-keyboard", integrated ? "no" : "yes", Required: true));
        // The shell owns global media keys even when mpv owns the focused video window's keys.
        options.Add(new("input-media-keys", "no", Required: true));

        if (integrated)
        {
            // Physical pixels, with a placeholder until the shell has completed layout.
            var (width, height) = surfaceSize;
            options.Add(new("d3d11-composition-size",
                width > 0 && height > 0 ? $"{width}x{height}" : "1x1", Required: true));
        }

        return options;
    }

    private static bool IsOwnedOption(string name)
    {
        // Also filter CLI-style negations and list suffixes (vo-append, vo-clr, ...), so a
        // later fallback or an alias cannot reintroduce embedding or replace the GPU backend.
        var key = name.Trim().TrimStart('-');
        if (key.StartsWith("no-", StringComparison.OrdinalIgnoreCase)) key = key[3..];
        string[] owned =
        [
            "wid", "vo", "gpu-api", "gpu-context", "d3d11-output-mode",
            "d3d11-exclusive-fs", "d3d11-composition-size", "force-window",
            "input-default-bindings", "input-vo-keyboard", "input-media-keys"
        ];
        return owned.Any(option => key.Equals(option, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(option + "-", StringComparison.OrdinalIgnoreCase));
    }
}

internal readonly record struct LibMpvPipelineOption(string Name, string Value, bool Required = false)
{
    internal void EnsureAccepted(int error)
    {
        if (Required && error < 0)
            throw new InvalidOperationException(
                $"内置 libmpv 的必需管线选项 {Name}={Value} 设置失败（错误 {error}），已停止启动，不能回退到其他渲染管线。");
    }
}
