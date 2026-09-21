using System.Globalization;
using EmbyNian.Configuration;

namespace EmbyNian.Mpv;

/// <summary>
/// 独占模式下四颗方向键各自跳多少秒（2026-09-20）。
/// <para>
/// <b>独占模式的键盘不归 shell。</b> <c>input-default-bindings=yes</c>（见 <c>LibMpvPipelinePolicy</c>）
/// 之后，视频窗里按下的键由 mpv 自己那张内建表处理，而这台机器上那份 libmpv 的内建值是
/// <c>UP seek 60</c> / <c>DOWN seek -60</c> / <c>LEFT seek -5</c> / <c>RIGHT seek 5</c>
/// （实测 <c>work/probe-seek-keys.txt</c>：内建绑定表 + 位移读数）。所以「上下键改成 30 秒」这件事在独占
/// 模式里必须动 mpv 那一层 —— 改 shell 的快捷键表这边根本不经过它。
/// </para>
/// <para>
/// <b>改法是运行期的 <c>keybind</c> 命令。</b> mpv 文档把这条命令写成「primarily useful for the client
/// API」，它把新绑定落在 priority 11，比内建的 priority 0 高，而 mpv 只跑优先级最高的那一条。实测
/// <c>work/probe-keybind.txt</c>：绑上 <c>seek 30</c> 之后按 ↑ 的位移是 +29.8 秒，内建那条 <c>seek 60</c>
/// 不再执行（同一份实录里 LEFT/RIGHT 重绑同样的值只是把优先级抬了一层，位移不变）。
/// </para>
/// <para>
/// <b>为什么不写一份 input.conf。</b> <c>--input-conf</c> 也能覆盖（实测同样落在 priority 11），但那是
/// 一个装箱文件，里面的秒数在打包那一刻就定死了；而秒数是设置页上的两行，用户改完不该等下一次发版。走
/// <c>keybind</c> 就没有第二份会过期的数字，屏上印几就跳几 —— 两个管线取值的地方不同，值只有一个来源。
/// </para>
/// <para>
/// <b>集成模式与这个类无关。</b> 那边画面合成进 XAML 树、键盘归 shell 的快捷键表
/// （<c>ShortcutCatalog</c>，<c>input-default-bindings=no</c>），这里的四条一条都不会发出去。
/// </para>
/// </summary>
public static class MpvSeekKeys
{
    /// <summary>
    /// 四颗方向键此刻该绑的命令，键名用的是 input.conf 那套。次序固定：← → ↓ ↑ —— 宿主按这个次序发，
    /// 测试也按这个次序比。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Bindings(PlaybackSettings playback) =>
    [
        new("LEFT", Seek(-playback.SeekBackwardSeconds)),
        new("RIGHT", Seek(playback.SeekForwardSeconds)),
        new("DOWN", Seek(-playback.SeekBackwardLongSeconds)),
        new("UP", Seek(playback.SeekForwardLongSeconds))
    ];

    /// <summary>相对跳 N 秒。负数自带负号，正数不加号 —— mpv 两种写法都吃，这里取最短的那种。</summary>
    private static string Seek(int seconds) =>
        $"seek {seconds.ToString(CultureInfo.InvariantCulture)}";
}
