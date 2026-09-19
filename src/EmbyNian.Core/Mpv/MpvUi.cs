namespace EmbyNian.Mpv;

/// <summary>
/// 独占模式视频窗的 Lua UI（assets/mpv-ui 里的 uosc 嵌入版）在启动时的装配决定。
/// <para>
/// 两条事实决定了这个类的形状。其一，内置 libmpv 静态编入了 LuaJIT（<c>assets/mpv-runtime/README.md</c>
/// 有实测记录；<c>work/probe-uosc-idle.py</c> 用空播放器验证过 load-script 与握手），所以 uosc 可以
/// 直接由播放器加载，不需要换 DLL。其二，播放以 <c>config=no</c> 起播，mpv 的 script-opts、字体
/// 目录、脚本目录都不存在默认值 —— 这里给出的每一项都是这个环境下唯一的事实源。
/// </para>
/// <para>
/// 装箱文件缺失不算错误：UI 是添头，少了几份文件就退回「没有屏幕控件的独占播放」，返回 null 让
/// 调用方照旧起播。真正的播放参数（地址、轨道、着色器）不在这里 —— 那是 <see cref="Playback.PlaybackPlanner"/>
/// 的地界，这里只管「视频窗里画不画控件」。
/// </para>
/// </summary>
public static class MpvUi
{
    /// <summary>装箱的 uosc 入口，相对程序目录。mpv 的 scripts 选项吃正斜杠也吃反斜杠。</summary>
    public const string ScriptRelativePath = "mpv-ui/scripts/uosc/main.lua";

    /// <summary>uosc 的图标与贴图字体（Material Icons Rounded 等），相对程序目录。</summary>
    public const string FontsRelativeDir = "mpv-ui/fonts";

    /// <summary>uosc 文本用的字体。Windows 全都有，也不吃用户字幕字体设置 —— 那是字幕的事。</summary>
    public const string OsdFont = "Microsoft YaHei";

    /// <summary>启动 uosc 需要的全部 mpv 选项；装箱不齐时为 null（退回无 UI 播放）。</summary>
    public static IReadOnlyList<KeyValuePair<string, string>>? Bootstrap(string baseDirectory) =>
        File.Exists(ScriptPath(baseDirectory)) && Directory.Exists(FontsPath(baseDirectory))
            ? Build(ScriptPath(baseDirectory), FontsPath(baseDirectory))
            : null;

    /// <summary>选项本身，与「文件在不在」分开 —— 单元测试不落盘也能钉住内容。</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Build(string scriptPath, string fontsDirectory) =>
    [
        // mpv 自带的 OSC 必须关掉（uosc 自己也会设，但那要等脚本跑起来）；
        // 图标字体从装箱目录来，config=no 的播放器没有别的字体来源。
        new("osc", "no"),
        // mpv 自己的音量/跳转 OSD 条与 uosc 的控件重复（右侧音量条之外又冒出一根居中的大条），
        // uosc 自己会画位置与音量的反馈，mpv 的这层整个关掉。
        new("osd-bar", "no"),
        new("osd-on-seek", "no"),
        // 无边框：系统标题栏去掉，标题与最小化/最大化/关闭由 uosc 顶栏画进画面（与用户原
        // mpv 配置同款）；拖动窗口靠 window-dragging，画面上按住即可拖。
        new("border", "no"),
        new("window-dragging", "yes"),
        new("osd-fonts-dir", fontsDirectory),
        new("osd-font", OsdFont),
        new("scripts", scriptPath),
    ];

    public static string ScriptPath(string baseDirectory) =>
        Path.Combine(baseDirectory, ScriptRelativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string FontsPath(string baseDirectory) =>
        Path.Combine(baseDirectory, FontsRelativeDir.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>视频窗 Lua UI 与宿主之间一条消息的已解析形态。</summary>
public readonly record struct VideoWindowMessage(string Key, string Value);

/// <summary>
/// <c>embynian-*</c> 消息契约：uosc 嵌入版经 <c>script-message</c> 发给宿主的全部内容。
/// <para>
/// 只认带 <c>embynian-</c> 前缀的消息，其余（uosc 自己的版本广播等）一律丢弃 —— 队列里混进什么
/// 不由宿主决定，契约按前缀收窄是防串台的第一道闸。值域也在这张表里收窄：进度是可解析的小数、
/// 换集只有 ±1，之外的都当没有说过。
/// </para>
/// </summary>
public static class VideoWindowContract
{
    /// <summary>uosc 装载完成的握手，值是版本号。</summary>
    public const string Ready = "embynian-ready";

    /// <summary>请求跳转，值是 0–1 的进度比例（uosc 时间轴的宿主外跳转不需要它，留作扩展）。</summary>
    public const string Seek = "embynian-seek";

    /// <summary>上一集（-1）／下一集（1）。换集是 Emby 的导航，宿主是唯一知道单集列表的一方。</summary>
    public const string Episode = "embynian-episode";

    /// <summary>请求弹出选集菜单（值保留）；宿主把本季单集经 open-menu 推给 uosc 画出来。</summary>
    public const string Episodes = "embynian-episodes";

    /// <summary>选集菜单里点中的一项，值是 1 起算的集序号（对应宿主推送菜单时的次序）。</summary>
    public const string EpisodeIndex = "embynian-episode-index";

    /// <summary>
    /// 把一条 client-message 的参数解析成宿主消息；不是宿主的消息、值不合契约的，返回 null。
    /// </summary>
    public static VideoWindowMessage? Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 2 || !arguments[0].StartsWith("embynian-", StringComparison.Ordinal)) return null;

        var key = arguments[0];
        var value = arguments[1];

        if (key == Episode) return value is "-1" or "1" ? new VideoWindowMessage(key, value) : null;
        if (key == EpisodeIndex)
        {
            return int.TryParse(value, out var index) && index is >= 1 and <= 100000
                ? new VideoWindowMessage(key, value)
                : null;
        }

        if (key is Ready or Seek or Episodes) return new VideoWindowMessage(key, value);

        return null;
    }
}
