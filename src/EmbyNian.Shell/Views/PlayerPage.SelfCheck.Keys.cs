using System.Text;
using EmbyNian.Playback;
using Windows.System;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 播放器可重绑快捷键的自检（「参考上图在设置中新增快捷键功能」，2026-09-08）。放在单独一个文件里，避开当前
/// 工作树里没提交的那批（<c>PlayerPage.SelfCheck.Input.cs</c> 等）。
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>
    /// 快捷键派发这条线，屏上看不出对错、编译看不出、也没法在这台机器上用真键盘自动按一遍（注不进按键），
    /// 所以拿这一关盯三件事：
    /// <list type="bullet">
    /// <item>Core 那张表（<see cref="ShortcutCatalog.Actions"/>）里每个动作，页面这张 动作→处理器 表
    /// （<see cref="ShortcutHandlers"/>）里都有一条 —— 少一条就是那颗键按下去没反应，一个静默死键。</item>
    /// <item>每个动作的默认键都按得出来：<see cref="KeyStrokeInterop"/> 能从某个 <see cref="VirtualKey"/> 产出
    /// 这个 token，否则那是个谁也按不出的默认键（方括号那两颗尤其要盯 —— 它们没有 VirtualKey 名字，靠原始码）。</item>
    /// <item>默认键之间不相撞（<see cref="ShortcutCatalog.DefaultsAreUnique"/>）—— 撞了装机就带着一个
    /// <see cref="ShortcutCatalog.Rebind"/> 拦不住的冲突。</item>
    /// </list>
    /// 全在内存里比，不碰 mpv、不真播：建 <see cref="ShortcutHandlers"/> 只读键、不执行处理器。
    /// </summary>
    internal (bool Ok, string Detail) ProbeShortcuts()
    {
        var actions = ShortcutCatalog.Actions;
        var handlerIds = ShortcutHandlers.Keys.ToHashSet(StringComparer.Ordinal);

        var missing = actions.Where(action => !handlerIds.Contains(action.Id)).Select(action => action.Id).ToList();
        var extra = handlerIds.Where(id => actions.All(action => action.Id != id)).ToList();

        // KeyStrokeInterop 能产出的全部 token —— 扫 0..0xFF 而不是 Enum.GetValues：方括号（219/221）没有
        // VirtualKey 名字，不在枚举成员里，只有连着数字扫才盖得到。默认键的 Key 必须落在这个集合里，否则没有
        // 一个物理键按得出它，那颗键从装机起就是死的。
        var producible = new HashSet<string>(StringComparer.Ordinal);
        for (var code = 0; code <= 0xFF; code++)
            if (KeyStrokeInterop.Token((VirtualKey)code) is { } token) producible.Add(token);

        var unreachable = actions.Where(action => !producible.Contains(action.Default.Key)).Select(action => action.Id).ToList();
        var unique = ShortcutCatalog.DefaultsAreUnique();

        var ok = missing.Count == 0 && extra.Count == 0 && unreachable.Count == 0 && unique;

        var detail = new StringBuilder($"{actions.Count} 个动作、处理器 {handlerIds.Count} 个");
        if (missing.Count > 0) detail.Append($"；缺处理器：{string.Join('、', missing)}");
        if (extra.Count > 0) detail.Append($"；多出处理器：{string.Join('、', extra)}");
        if (unreachable.Count > 0) detail.Append($"；默认键按不出：{string.Join('、', unreachable)}");
        detail.Append(unique ? "；默认键互不相同" : "；默认键有相撞");

        return (ok, detail.ToString());
    }
}
