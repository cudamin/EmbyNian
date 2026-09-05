namespace EmbyNian.Mpv;

/// <summary>What one row of the 画面菜单 is.</summary>
public enum PlayerMenuKind
{
    /// <summary>Runs its <see cref="PlayerMenuNode.Commands"/> and echoes its <see cref="PlayerMenuNode.Notice"/>.</summary>
    Command,

    /// <summary>Opens its <see cref="PlayerMenuNode.Children"/>.</summary>
    Group,

    /// <summary>A rule between two runs of items. No label, no command, nothing to click.</summary>
    Separator
}

/// <summary>
/// One row of the 画面菜单: what it is called, what it does, and what to say on screen once it has.
/// </summary>
/// <param name="Kind">Item, submenu or rule.</param>
/// <param name="Label">What the row reads, taken from the bundled <c>input.conf</c>'s own menu text.</param>
/// <param name="Commands">
/// The mpv commands to run, in order, each already split into the argument list
/// <c>PlaybackService.CommandAsync</c> takes. Usually one; a 「重置」 row is several, because mpv has no
/// command that sets six properties at once and <c>input.conf</c>'s <c>;</c> chaining is input syntax
/// rather than something <c>mpv_command</c> understands.
/// </param>
/// <param name="Notice">
/// What to hand to <c>show-text</c> afterwards. May contain <c>${property}</c>, which mpv expands itself
/// — that is the point of routing this through mpv rather than the client's own toast: the value shown is
/// read after the command ran, so 「宽高比:16:9」 cannot disagree with what is actually set.
/// </param>
/// <param name="Children">The submenu's rows, empty for anything else.</param>
public sealed record PlayerMenuNode(
    PlayerMenuKind Kind,
    string Label,
    IReadOnlyList<IReadOnlyList<string>> Commands,
    string Notice,
    IReadOnlyList<PlayerMenuNode> Children);

/// <summary>
/// The right-click 画面菜单, as data.
/// <para>
/// Its rows come from the bundled mpv configuration's own uosc menu — the <c>#menu:</c> annotations in
/// <c>input.conf</c> — because that tree is what someone who has used this configuration already knows,
/// down to the wording of 「开/关 裁切填充」. Reading it at runtime is what a client that shipped uosc
/// would do, and this one cannot: playback starts with <c>--no-config</c> so that the user's own
/// <c>input.conf</c> cannot overwrite the parameters the client depends on, and uosc is Lua, which the
/// bundled <c>libmpv-2.dll</c> was built without. So the tree was walked once, at development time, and
/// what survived the walk is here.
/// </para>
/// <para>
/// What did not survive, and why — the menu is shorter than the configuration's by design:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Every <c>script-message</c>, <c>script-message-to</c> and <c>script-binding</c> row: 打开菜单, 历史,
/// 字幕下载/同步/AI 生成, 章节制作, 去黑边, 撤消跳转, 剪切与动图. They address scripts that are not
/// running, so the rows would be buttons that do nothing — the worst kind of menu entry.
/// </description></item>
/// <item><description>
/// uosc's own annotations (<c>#@state=</c>, <c>#@audio-devices</c>). A row cannot show a checkmark for a
/// property nobody has read, and reading properties would make opening the menu asynchronous; the
/// configuration's 「开/关 …」 wording already says the row toggles rather than sets, and the
/// <see cref="PlayerMenuNode.Notice"/> says which way it went.
/// </description></item>
/// <item><description>
/// Rows the client already owns better: 音轨/字幕轨/静音/延迟 (the 浮层 has them, with the tracks' real
/// names and the delay's current value), 上个/下个文件 (there is no playlist — 上一集/下一集 walk the
/// Emby season), 窗口缩放 and 置顶 (the window is the shell's, not mpv's).
/// </description></item>
/// <item><description>
/// 次字幕 rows, and anything about the second subtitle track: nothing in the client loads one.
/// </description></item>
/// </list>
/// <para>
/// 截屏 <b>used to be on that list and is not any more.</b> The reason it was — 「with <c>--no-config</c> there
/// is no <c>screenshot-directory</c>, so the files would land next to the executable without the user being
/// told where」 — stopped being true once <see cref="Infrastructure.AppPaths.ScreenshotDirectory"/> existed and
/// <see cref="MpvBaseline"/> started naming it on every launch, with the path and an 「打开」 button on the
/// 关于 card. The three rows differ in what is in the picture, so each one says so rather than being called
/// 「截屏 1/2/3」.
/// </para>
/// <para>
/// Kept as data in Core rather than built as a <c>MenuFlyout</c> in the shell so that the tree is
/// checkable without a window: what has to be right here is that every row runs something, that no
/// submenu is empty, and that the argument lists are mpv's real spelling.
/// </para>
/// </summary>
public static class PlayerMenuCatalog
{
    /// <summary>The menu, top level first. Order is the configuration's.</summary>
    public static IReadOnlyList<PlayerMenuNode> Root { get; } =
    [
        Group("导航",
            Item("上一章节", "章节:${chapter}", "add", "chapter", "-1"),
            Item("下一章节", "章节:${chapter}", "add", "chapter", "1"),
            Rule,
            Item("精准后退 1 秒", "后退 1 秒", "seek", "-1", "exact"),
            Item("精准前进 1 秒", "前进 1 秒", "seek", "1", "exact"),
            Item("上一帧", "当前帧:${estimated-frame-number}", "frame-back-step"),
            Item("下一帧", "当前帧:${estimated-frame-number}", "frame-step")),

        Group("画面",
            Group("切换 宽高比",
                Item("默认值", "宽高比:${video-aspect-override}", "set", "video-aspect-override", "no"),
                Item("16:9", "宽高比:${video-aspect-override}", "set", "video-aspect-override", "16:9"),
                Item("4:3", "宽高比:${video-aspect-override}", "set", "video-aspect-override", "4:3"),
                Item("2.35:1", "宽高比:${video-aspect-override}", "set", "video-aspect-override", "2.35:1"),
                Item("循环切换", "宽高比:${video-aspect-override}",
                    "cycle-values", "video-aspect-override", "16:9", "4:3", "2.35:1", "no")),
            Item("左旋转", "视频旋转:${video-rotate}", "cycle-values", "video-rotate", "0", "270", "180", "90"),
            Item("右旋转", "视频旋转:${video-rotate}", "cycle-values", "video-rotate", "0", "90", "180", "270"),
            Group("画面缩放",
                Item("画面缩小", "画面缩小:${video-zoom}", "add", "video-zoom", "-0.1"),
                Item("画面放大", "画面放大:${video-zoom}", "add", "video-zoom", "0.1"),
                Item("画面左移动", "画面左移动:${video-pan-x}", "add", "video-pan-x", "-0.1"),
                Item("画面右移动", "画面右移动:${video-pan-x}", "add", "video-pan-x", "0.1"),
                Item("画面上移动", "画面上移动:${video-pan-y}", "add", "video-pan-y", "-0.1"),
                Item("画面下移动", "画面下移动:${video-pan-y}", "add", "video-pan-y", "0.1")),
            // README's 缩放窗口时按画面比例联动 keeps the window the shape of the picture, so there is
            // normally nothing to crop. This row is for the times that is not what someone wants: a 2.35:1
            // film on a 16:9 screen, filled rather than letterboxed.
            Item("开/关 裁切填充", "裁切填充:${panscan}", "cycle-values", "panscan", "0.0", "1.0"),
            Steps("重置以上画面操作", "重置画面操作",
                ["set", "video-zoom", "0"],
                ["set", "panscan", "0"],
                ["set", "video-rotate", "0"],
                ["set", "video-pan-x", "0"],
                ["set", "video-pan-y", "0"],
                ["set", "video-aspect-override", "no"]),
            Rule,
            Item("开/关 自动 ICC 校色", "ICC 自动校色:${icc-profile-auto}", "cycle", "icc-profile-auto"),
            Group("调色",
                Item("对比度 -1", "对比度:${contrast}", "add", "contrast", "-1"),
                Item("对比度 +1", "对比度:${contrast}", "add", "contrast", "1"),
                Item("明度 -1", "明度:${brightness}", "add", "brightness", "-1"),
                Item("明度 +1", "明度:${brightness}", "add", "brightness", "1"),
                Item("伽马 -1", "伽马:${gamma}", "add", "gamma", "-1"),
                Item("伽马 +1", "伽马:${gamma}", "add", "gamma", "1"),
                Item("饱和度 -1", "饱和度:${saturation}", "add", "saturation", "-1"),
                Item("饱和度 +1", "饱和度:${saturation}", "add", "saturation", "1"),
                Item("色相 -1", "色相:${hue}", "add", "hue", "-1"),
                Item("色相 +1", "色相:${hue}", "add", "hue", "1"),
                Rule,
                Steps("重置", "重置调色",
                    ["set", "contrast", "0"],
                    ["set", "brightness", "0"],
                    ["set", "gamma", "0"],
                    ["set", "saturation", "0"],
                    ["set", "hue", "0"])),
            Group("去色带",
                Item("deband 开关", "去色带:${deband}", "cycle", "deband"),
                Item("deband 强度 +1", "去色带强度:${deband-iterations}", "add", "deband-iterations", "1"),
                Item("deband 强度 -1", "去色带强度:${deband-iterations}", "add", "deband-iterations", "-1")),
            Group("HDR 相关",
                Item("切换 HDR 映射曲线", "HDR 映射曲线:${tone-mapping}",
                    "cycle-values", "tone-mapping",
                    "auto", "spline", "bt.2390", "hable", "bt.2446a", "st2094-40", "st2094-10"),
                Item("切换 HDR 动态映射", "HDR 动态映射:${hdr-compute-peak}",
                    "cycle-values", "hdr-compute-peak", "yes", "no"),
                Item("切换 HDR 直通模式", "HDR 直通模式:${target-colorspace-hint}",
                    "cycle", "target-colorspace-hint"),
                Item("切换 显示器传输特性", "显示器传输特性:${target-trc}",
                    "cycle-values", "target-trc", "auto", "pq", "gamma2.2"),
                Item("切换 HDR 参考白亮度", "HDR 参考白亮度:${hdr-reference-white}",
                    "cycle-values", "hdr-reference-white", "100", "203"),
                Item("切换 色域映射模式", "色域映射模式:${gamut-mapping-mode}", "cycle", "gamut-mapping-mode"))),

        Group("视频",
            Group("切换 解码方式",
                Item("软解", "解码方式:${hwdec}", "set", "hwdec", "no"),
                Item("自动选择硬解加速模式", "解码方式:${hwdec}", "set", "hwdec", "auto-safe"),
                Item("自动选择 copy 硬解模式", "解码方式:${hwdec}", "set", "hwdec", "auto-copy-safe"),
                Rule,
                Item("nvdec 硬解", "解码方式:${hwdec}", "set", "hwdec", "nvdec"),
                Item("d3d11va 硬解", "解码方式:${hwdec}", "set", "hwdec", "d3d11va"),
                Item("nvdec-copy 硬解", "解码方式:${hwdec}", "set", "hwdec", "nvdec-copy"),
                Item("d3d11va-copy 硬解", "解码方式:${hwdec}", "set", "hwdec", "d3d11va-copy"),
                Item("d3d12va-copy 硬解", "解码方式:${hwdec}", "set", "hwdec", "d3d12va-copy")),
            Item("开/关 抖动补偿", "抖动补偿:${interpolation}", "cycle", "interpolation"),
            Item("开/关 反交错", "去交错:${deinterlace}", "cycle", "deinterlace"),
            Item("切换 帧同步模式", "帧同步模式:${video-sync}",
                "cycle-values", "video-sync",
                "display-resample", "display-tempo", "audio", "display-vdrop", "display-resample-vdrop")),

        Group("视频滤镜",
            Item("清空", "清空视频滤镜", "vf", "clr", ""),
            Rule,
            Item("开/关 去色块滤镜", "视频滤镜:${vf}", "vf", "toggle", "deblock=filter=weak:block=4"),
            Item("开/关 动态范围限制", "视频滤镜:${vf}", "vf", "toggle", "format=colorlevels=limited"),
            Item("开/关 垂直翻转", "视频滤镜:${vf}", "vf", "toggle", "vflip"),
            Item("开/关 水平翻转", "视频滤镜:${vf}", "vf", "toggle", "hflip"),
            Item("开/关 旋转 180", "视频滤镜:${vf}", "vf", "toggle", "rotate=angle=180*PI/180"),
            Item("开/关 伽马修正 2.2", "视频滤镜:${vf}", "vf", "toggle", "format:gamma=gamma2.2"),
            Item("开/关 强制帧数 59.94", "视频滤镜:${vf}", "vf", "toggle", "fps=fps=60/1.001"),
            Item("开/关 填充 16:9 的黑边并居中", "视频滤镜:${vf}", "vf", "toggle", "pad=aspect=16/9:x=-1:y=-1"),
            Item("开/关 色温修正 6500", "视频滤镜:${vf}", "vf", "toggle", "colortemperature=temperature=6500")),

        Group("截屏",
            // The three differ in what ends up in the file, and that is the only thing worth putting on the
            // labels: mpv's own words for them (subtitles / video / window) say nothing to somebody who has
            // not read the manual. 「带着色器」 matters here more than in most players — this client's whole
            // 画质档位 scheme is shaders, so 「what the chain did to this frame」 is a real question, and only
            // the first row answers it.
            Item("截图 — 屏上这一帧（带字幕和着色器）", "已保存到 ${screenshot-directory}", "screenshot"),
            Item("截图 — 原始画面（不带字幕和着色器）", "已保存到 ${screenshot-directory}", "screenshot", "video"),
            Item("截图 — 按窗口尺寸", "已保存到 ${screenshot-directory}", "screenshot", "window")),

        Group("音频",
            Group("音频通道输出方式",
                Item("7.1 声道输出", "音频通道输出方式:${audio-channels}", "set", "audio-channels", "7.1"),
                Item("5.1 声道输出", "音频通道输出方式:${audio-channels}", "set", "audio-channels", "5.1"),
                Item("双通道输出", "音频通道输出方式:${audio-channels}", "set", "audio-channels", "stereo"),
                Item("自动选择以上输出方式", "音频通道输出方式:${audio-channels}",
                    "set", "audio-channels", "7.1,5.1,stereo"),
                Item("循环切换", "音频通道输出方式:${audio-channels}",
                    "cycle-values", "audio-channels", "7.1,5.1,stereo", "7.1", "5.1", "stereo", "auto-safe", "auto")),
            // 音量均衡. The two filter strings are MpvOutputOptions', not this file's: 设置 → 音频输出 →
            // 音量均衡 sends the same two at launch, and a menu row that meant something slightly different
            // from the settings row of the same name is the 「界面在骗人」 shape all over again. This row is
            // still worth having — it is the only way to hear the three side by side within one film — but it
            // does not persist, because every playback is a fresh mpv under --no-config.
            Item("切换 音量均衡", "音量均衡:${af}",
                "cycle-values", "af",
                MpvOutputOptions.DynAudNorm,
                MpvOutputOptions.LoudNorm,
                ""),
            Item("清空 af 滤镜", "清空音频滤镜", "af", "clr", ""),
            Item("开/关 下混归一化", "5.1 下混归一化:${audio-normalize-downmix}",
                "cycle", "audio-normalize-downmix"),
            Rule,
            Item("切换 音频独占模式", "音频独占模式:${audio-exclusive}", "cycle", "audio-exclusive")),

        Group("字幕",
            Item("字幕上移", "字幕上移:${sub-pos}", "add", "sub-pos", "-1"),
            Item("字幕下移", "字幕下移:${sub-pos}", "add", "sub-pos", "1"),
            Item("字号 -0.1", "字幕缩小:${sub-scale}", "add", "sub-scale", "-0.1"),
            Item("字号 +0.1", "字幕放大:${sub-scale}", "add", "sub-scale", "0.1"),
            Rule,
            Item("跳转上一条字幕", "上一条字幕", "sub-seek", "-1"),
            Item("跳转下一条字幕", "下一条字幕", "sub-seek", "1"),
            Rule,
            Group("兼容性",
                Item("切换 渲染样式", "字幕渲染样式:${sub-ass-override}", "cycle", "sub-ass-override"),
                Item("切换 字幕时序修复", "字幕时序修复:${sub-fix-timing}", "cycle", "sub-fix-timing"),
                Item("切换 ass 字幕输出到黑边", "ass 字幕输出黑边:${sub-ass-force-margins}",
                    "cycle", "sub-ass-force-margins"),
                Item("切换 srt 字幕输出到黑边", "srt 字幕输出黑边:${sub-use-margins}", "cycle", "sub-use-margins"),
                Item("重载当前字幕", "重载当前字幕", "sub-reload")),
            // 字幕延迟 is deliberately not here: it is on the 浮层's ⚙ menu and on Z / Shift+Z, where the
            // current value is shown. 恢复初始 still resets it, because that is what 「初始」 means.
            Steps("恢复初始", "重置字幕状态",
                ["set", "sub-pos", "100"],
                ["set", "sub-scale", "1.0"],
                ["set", "sub-delay", "0"])),

        Group("片段循环",
            Item("设定/清除 片段循环", "片段循环:${ab-loop-a} - ${ab-loop-b}", "ab-loop"),
            Item("开/关 循环播放", "循环播放:${loop-file}", "cycle-values", "loop-file", "inf", "no"))
    ];

    /// <summary>
    /// Every node in the tree, parents before children. For anything that has to look at all of them —
    /// the tests that check no submenu is empty and every row runs something.
    /// </summary>
    public static IEnumerable<PlayerMenuNode> Flatten(IEnumerable<PlayerMenuNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;

            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    /// <summary>
    /// A rule between two runs of items. A property rather than a shared instance so that
    /// <see cref="Root"/> can use it: a static initializer reading another one declared below it gets
    /// null, and a null separator is a crash rather than a missing line.
    /// </summary>
    private static PlayerMenuNode Rule => new(PlayerMenuKind.Separator, string.Empty, [], string.Empty, []);

    private static PlayerMenuNode Item(string label, string notice, params string[] command) =>
        new(PlayerMenuKind.Command, label, [command], notice, []);

    private static PlayerMenuNode Steps(string label, string notice, params string[][] commands) =>
        new(PlayerMenuKind.Command, label, commands, notice, []);

    private static PlayerMenuNode Group(string label, params PlayerMenuNode[] children) =>
        new(PlayerMenuKind.Group, label, [], string.Empty, children);
}
