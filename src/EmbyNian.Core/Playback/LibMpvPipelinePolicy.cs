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
        // 两条管线都 **no**（参考项目 dyphire/mpv-config：`d3d11-exclusive-fs` 那行是注释掉的 ——
        // 也就是关，mpv 出厂就是关，而 `d3d11-flip` 那行同样注释着，翻转模型「性能最好」的默认因此留着）。
        //
        // 独占模式此前是 `yes`，正是它让「独占模式下切全屏和窗口化」卡顿、闪烁、画面像倒退一下、慢半拍
        // 才铺满（2026-09-20 用户报）：exclusive-fs=yes 会在进/退全屏那一刻向 DXGI 申请独占全屏，交换链
        // 重建、显示模式切换，而这恰恰是别的播放器（以及这份参考配置）为了切换顺滑都不开的东西。关掉之后
        // 走的是无边框窗口化全屏（DWM 合成、翻转模型），切换像普通播放器一样干净；代价只是放弃「独占全屏」
        // 那点极致性能 —— 对局域网放电影几乎无感。集成模式本来就是 `no`（画面合成进 XAML 树，压根没有可独占
        // 的顶层交换链），这一改把两条并到同一个值上。仍显式钉死而不省略：mpv 的默认是 no 不代表可以不写，
        // 哪天默认变了得由这里说了算（本项目「不继承没钉住的默认」）。
        options.Add(new("d3d11-exclusive-fs", "no", Required: true));
        // 集成模式必须 immediate：合成交换链要在文件加载前就存在，宿主才能把它接进 XAML 面板
        // （见 LibMpvBackend 里合成附加的第一遍，否则面板一直黑）。
        //
        // 独占模式用 **no**：mpv 自建顶层窗口，而 immediate/yes 都会在拿到视频尺寸**之前**就把窗口生出来 ——
        // 2026-09-19 本机实测（work/probe-mpv-window.py，真实窗口、枚举本进程的窗口矩形）：
        //   yes：initialize 后 0.1s 冒出 960x540 的黑窗（居中），文件一开跳成 1280x720 ⇒ 用户报的
        //        「启播的时候有时候会有个黑框会闪一下」就是这一跳；源打不开（库里那版 404）时那个黑框
        //        还一直挂着，因为窗口已经生出来了没什么可关。
        //   no：initialize 与「打不开的源」全程**没有窗口**；能放的源在 0.1s 时以**终值尺寸**出生
        //        （1280x720，无中间尺寸）。auto-fullscreen 那一档同样是一出生就整屏（实测 0→2560x1440）。
        // 代价是播完（EOF）时窗口会被 mpv 收掉 —— 所以画面一上来就把它按住，见 LibMpvHandle：
        // 观察 vo-configured 翻真之后把 force-window 改回 yes（运行期可改，实测窗口不闪、且能跨 loadfile
        // 与 EOF 活着，这正是「不关窗换片」要的那扇窗）。
        options.Add(new("force-window", integrated ? "immediate" : "no", Required: true));
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
