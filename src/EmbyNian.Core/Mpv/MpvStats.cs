namespace EmbyNian.Mpv;

/// <summary>
/// 播放统计面板的 Lua 脚本：<c>assets/mpv-ui/scripts/stats.lua</c>，mpv 内置 <c>stats.lua</c> 的简体中文覆盖版。
/// <para>
/// <b>为什么是「覆盖内置脚本」而不是自己画面板。</b> 统计项要与参考项目
/// （<c>https://github.com/dyphire/mpv-config</c> 所用的上游 <c>stats.lua</c>）逐项一致，而 mpv 的内置脚本
/// 没有任何 i18n 机制 —— <c>script-opts/stats.conf</c> 只管样式与行为，一个字的文案都不承载。所以中文化
/// 唯一的路是放一份同名脚本把它顶掉，<c>load-stats-overlay=no</c>（见 <see cref="MpvBaseline"/>）负责让
/// 内置那份不加载，否则两份脚本会同时申请 <c>stats/display-stats</c> 这个名字。
/// </para>
/// <para>
/// <b>装载走运行期的 <c>load-script</c> 命令，不走 <c>scripts</c> 选项。</b> 两条管线的脚本进法不同：独占
/// 模式的 uosc 由 <see cref="MpvUi"/> 交给 <c>scripts</c> 选项。而 <c>scripts</c> 这个选项名在重复设置时是
/// <b>覆盖</b>（实测 <c>--scripts=a --scripts=b</c> 只装载 b），<c>script</c>（单数）才是 <b>累加</b>
/// （<c>--script=a --script=b</c> 两份都在）。往唯一的 <c>scripts</c> 值里再塞一份，最坏结果是 uosc 被顶掉
/// 而只剩统计；<c>load-script</c> 是运行期命令（在 <c>mpv_initialize</c> 之后、<c>loadfile</c> 之前发，
/// 与方向键的 <c>keybind</c> 同一处同一时机），装的是单个文件、脚本名取文件名 <c>stats</c> ——
/// <c>script-binding stats/…</c> 因此能解析到它，而它与 uosc 各装各的，互不替代。
/// </para>
/// <para>
/// <b>两种模式各由谁叫出来。</b> 独占模式的键盘归 mpv（<c>input-default-bindings=yes</c>），所以那里额外
/// 绑 <c>i</c>/<c>I</c>（<see cref="Keys"/>，与 <see cref="MpvSeekKeys"/> 同款运行期 <c>keybind</c>）——
/// 不必赌 mpv 内建表里有没有这两颗。集成模式 <c>input-default-bindings=no</c>，键归 shell，播放页那颗
/// 「统计」按钮发 <see cref="CycleCommand"/>，画出来的面板由 mpv 画进 OSD 层再合成进画面。
/// </para>
/// </summary>
public static class MpvStats
{
    /// <summary>装箱的统计脚本，相对程序目录。文件名必须是 <c>stats.lua</c>：mpv 用文件名当脚本名。</summary>
    public const string ScriptRelativePath = "mpv-ui/scripts/stats.lua";

    /// <summary>脚本对外暴露的两个绑定名，前缀必须是脚本名 <c>stats</c>。</summary>
    public const string ShowOnceBinding = "stats/display-stats";

    /// <inheritdoc cref="ShowOnceBinding"/>
    public const string ToggleBinding = "stats/display-stats-toggle";

    /// <summary>只显示一次（mpv 内置键位里的 <c>i</c>，到点自动收）。</summary>
    public const string ShowOnceCommand = "script-binding " + ShowOnceBinding;

    /// <summary>常驻开关，保留与 mpv 内置统计脚本相同的入口。</summary>
    public const string ToggleCommand = "script-binding " + ToggleBinding;

    /// <summary>播放统计 → 着色器统计 → 关闭，状态由 Lua 持有，按钮和独占键位共用。</summary>
    public const string CycleBinding = "stats/cycle-stats";

    public const string CycleCommand = "script-binding " + CycleBinding;

    /// <summary>统计脚本的绝对路径，交给运行期的 <c>load-script</c>。</summary>
    public static string ScriptPath(string baseDirectory) =>
        Path.Combine(baseDirectory, ScriptRelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>装箱在不在。不在只是「这次播放没有统计面板」，由调用方决定怎么记日志。</summary>
    public static bool Exists(string baseDirectory) => File.Exists(ScriptPath(baseDirectory));

    /// <summary>
    /// 独占模式的两颗开关键。键名用 input.conf 那套（<c>i</c> 小写、<c>I</c> 大写是两颗不同的键），
    /// 次序固定为「一次性、三态循环」。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Keys() =>
    [
        new("i", ShowOnceCommand),
        new("I", CycleCommand)
    ];
}
