using System.Text;

namespace EmbyNian.Playback;

/// <summary>
/// 一次按键：一个键，加上三个修饰键的开关。
/// <para>
/// <see cref="Key"/> 是一个**规范 token 串**（"Space"、"Left"、"F"、"BracketLeft"…），不是
/// <c>Windows.System.VirtualKey</c> —— Core 这一层不碰 WinRT/UI（见 CLAUDE.md）。外壳那边有一张
/// <c>KeyStrokeInterop</c>（VirtualKey ↔ token）是全项目唯一出现 <c>VirtualKey</c> 的地方，它产出的
/// token 就是这里认的这套词汇；修饰键三个开关由外壳在按下那一刻从系统读（<c>KeyRoutedEventArgs</c> 不带
/// 修饰键，见 <c>Native.ShiftHeld</c> 那段注释）。
/// </para>
/// </summary>
public readonly record struct KeyStroke(string Key, bool Ctrl, bool Alt, bool Shift)
{
    /// <summary>「没有键」——用来表示某个动作被显式解绑，或一次解析失败。<see cref="Key"/> 为空串。</summary>
    public static readonly KeyStroke None = new("", false, false, false);

    /// <summary>没有键就是空。<c>default(KeyStroke)</c> 的 Key 是 null，这里一并当空看，所以别拿它和别的比。</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Key);
}

/// <summary>播放器里一个可重绑的动作：稳定 Id、屏上中文名、装机默认键。</summary>
public sealed record ShortcutAction(string Id, string Label, KeyStroke Default);

/// <summary>
/// 播放器键盘快捷键这件事的全部判断，做成纯函数放在 Core —— 单元测试进不到外壳那个程序集，而「这个键触发
/// 哪个动作」「这个组合键有没有和别人撞」「存到文件里长什么样、显示成什么样」都是一给定输入就有唯一答案的
/// 判断，留在视图模型里就是没人看着的判断（CLAUDE.md 分层那条）。
/// <para>
/// <b>存储只存改动。</b> 设置文件里那张 <c>Dictionary&lt;string,string&gt;</c>（<see cref="Configuration"/>
/// 里的 <c>ShortcutSettings.Bindings</c>）只记用户动过的：缺键 = 用装机默认；空串 = 用户显式解绑（和默认不
/// 一样，默认都是有键的）；否则是一个 <see cref="Serialize"/> 出来的 token 串。装机是空字典，所以新装的人
/// 和从旧版本升上来的人拿到的都是这里定义的那套默认，日后加一个动作也不用迁移 —— 老用户的字典里没有它，
/// 自然落到它的默认上。
/// </para>
/// <para>
/// <b>Esc 和 Y 不在这张表里。</b> 它俩是固定键（Esc 全屏则退否则停、Y 只在出现跳过提示时确认跳过），留在
/// <c>PlayerPage.OnKeyDown</c> 里原样处理，不参与重绑 —— 见 <see cref="ReservedKeys"/>。所以捕获到这两个键的
/// 时候要拦住（<see cref="IsReserved"/>），不让任何可重绑动作占上它们，否则同一个键会有两种意思。
/// </para>
/// </summary>
public static class ShortcutCatalog
{
    /// <summary>
    /// 不许绑的键（整颗键、连着任何修饰键都不许）：Esc 是全局「退出」、还兼做重绑方框的「取消」；Y 只在出现
    /// 跳过提示时有效、它的名字还印在播放画面的那句提示上（<c>SkipCoordinator</c>，Core，被测试钉着）。这两个
    /// 留作固定键比让人改掉、再让画面上的提示对不上要稳。
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedKeys = ["Escape", "Y"];

    /// <summary>19 个可重绑动作，次序就是设置页上从上到下的次序。默认值和改造前那张 switch 表一模一样。</summary>
    public static IReadOnlyList<ShortcutAction> Actions { get; } = Build();

    private static KeyStroke K(string key, bool shift = false) => new(key, false, false, shift);

    private static IReadOnlyList<ShortcutAction> Build() =>
    [
        new("toggle-pause", "播放 / 暂停", K("Space")),
        new("seek-backward", "快退", K("Left")),
        new("seek-forward", "快进", K("Right")),
        new("volume-up", "音量增大", K("Up")),
        new("volume-down", "音量减小", K("Down")),
        new("toggle-fullscreen", "全屏切换", K("F")),
        new("toggle-mute", "静音", K("M")),
        new("previous-episode", "上一集", K("P")),
        new("next-episode", "下一集", K("N")),
        new("toggle-pin", "窗口置顶", K("T")),
        new("chapter-previous", "上一章节", K("PageUp")),
        new("chapter-next", "下一章节", K("PageDown")),
        new("speed-down", "减速（−0.1）", K("BracketLeft")),
        new("speed-up", "加速（+0.1）", K("BracketRight")),
        new("speed-reset", "恢复常速", K("Back")),
        new("subtitle-delay-decrease", "字幕延迟 −0.1 秒", K("Z")),
        new("subtitle-delay-increase", "字幕延迟 +0.1 秒", K("Z", shift: true)),
        new("audio-delay-decrease", "音频延迟 −0.1 秒", K("X")),
        new("audio-delay-increase", "音频延迟 +0.1 秒", K("X", shift: true))
    ];

    /// <summary>这个动作此刻真正生效的键：动过就用动过的，没动过用默认，显式解绑（空串）就是 <see cref="KeyStroke.None"/>。</summary>
    private static KeyStroke Effective(IReadOnlyDictionary<string, string> bindings, ShortcutAction action)
    {
        if (!bindings.TryGetValue(action.Id, out var stored)) return action.Default;
        if (string.IsNullOrEmpty(stored)) return KeyStroke.None;

        // 认不出来的写法（手改设置文件改坏了）退回默认，而不是让这个动作变成没有键 —— 「界面在骗人」里
        // 最轻的一种也比一个默认动作莫名其妙失灵好。真正的垃圾键由 Clean 在读盘时清掉。
        return TryParse(stored, out var stroke) ? stroke : action.Default;
    }

    /// <summary>每个动作此刻生效的键。设置页刷新每一行的显示、自检核对时都用它。</summary>
    public static IReadOnlyDictionary<string, KeyStroke> Resolve(IReadOnlyDictionary<string, string> bindings) =>
        Actions.ToDictionary(action => action.Id, action => Effective(bindings, action), StringComparer.Ordinal);

    /// <summary>
    /// 按下这个组合键该触发哪个动作，没有就返回 null。空组合键谁都不触发。
    /// <para>
    /// 正常情况下 <see cref="Rebind"/> 保证不会两个动作共用一个键，所以最多命中一个；万一手改文件造出了重复，
    /// 按 <see cref="Actions"/> 的次序返回头一个 —— 派发是确定的，不会随机。
    /// </para>
    /// </summary>
    public static string? Lookup(IReadOnlyDictionary<string, string> bindings, KeyStroke stroke)
    {
        if (stroke.IsEmpty) return null;

        foreach (var action in Actions)
            if (Effective(bindings, action) == stroke) return action.Id;

        return null;
    }

    /// <summary>一次重绑的结果：要么成功（<see cref="Bindings"/> 是新的一份），要么被占用（<see cref="Conflict"/> 是占用者的 Id、绑定原封不动）。</summary>
    public readonly record struct RebindResult(IReadOnlyDictionary<string, string> Bindings, string? Conflict);

    /// <summary>
    /// 把 <paramref name="id"/> 这个动作绑到 <paramref name="stroke"/> 上。
    /// <para>
    /// <b>冲突就拦下（用户的选择，2026-09-08）。</b> 这个键已经是别的动作的了，就返回那个占用者的 Id、一个绑定
    /// 都不改；调用方负责提示「先清掉那边再绑」。这样保证任何时候都不会两个动作共用一个键，派发不含糊，也不会
    /// 悄悄把别处的键抹掉。绑到自己的默认键上就把这条改动记录删掉（回到「没动过」），别的写进字典。
    /// </para>
    /// <para>空键、保留键（Esc/Y）都当空操作原样返回 —— 方框那边本来就拦在前面，这里是第二道防线，可被测试钉住。</para>
    /// </summary>
    public static RebindResult Rebind(IReadOnlyDictionary<string, string> bindings, string id, KeyStroke stroke)
    {
        var action = Actions.FirstOrDefault(a => a.Id == id);
        if (action is null || stroke.IsEmpty || IsReserved(stroke)) return new(bindings, null);

        foreach (var other in Actions)
        {
            if (other.Id == id) continue;
            if (Effective(bindings, other) == stroke) return new(bindings, other.Id);
        }

        var next = new Dictionary<string, string>(bindings, StringComparer.Ordinal);
        if (stroke == action.Default) next.Remove(id);
        else next[id] = Serialize(stroke);

        return new(next, null);
    }

    /// <summary>把这个动作解绑（× 那颗按钮）。存空串而不是删键：默认都是有键的，删键会退回默认，空串才是「就是不要」。</summary>
    public static IReadOnlyDictionary<string, string> Clear(IReadOnlyDictionary<string, string> bindings, string id)
    {
        if (Actions.All(a => a.Id != id)) return bindings;

        var next = new Dictionary<string, string>(bindings, StringComparer.Ordinal) { [id] = "" };
        return next;
    }

    /// <summary>读盘时把手改坏的清掉：认不出的动作 Id、解析不了的写法、落到保留键上的，全丢；空串（显式解绑）留着。</summary>
    public static Dictionary<string, string> Clean(IReadOnlyDictionary<string, string>? bindings)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (bindings is null) return result;

        var ids = Actions.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, value) in bindings)
        {
            if (!ids.Contains(id)) continue;
            if (string.IsNullOrEmpty(value)) { result[id] = ""; continue; }
            if (!TryParse(value, out var stroke) || IsReserved(stroke)) continue;
            result[id] = Serialize(stroke);
        }

        return result;
    }

    /// <summary>这个键是不是保留键（整颗键、连着任何修饰键）。</summary>
    public static bool IsReserved(KeyStroke stroke) => ReservedKeys.Contains(stroke.Key, StringComparer.Ordinal);

    /// <summary>动作的中文名，找不到就把 Id 原样给回去（不该发生，给个不炸的兜底）。</summary>
    public static string Label(string id) => Actions.FirstOrDefault(a => a.Id == id)?.Label ?? id;

    /// <summary>存进设置文件那份写法：修饰键按 Ctrl+Alt+Shift 的固定次序，加号连键。空键是空串。</summary>
    public static string Serialize(KeyStroke stroke)
    {
        if (stroke.IsEmpty) return "";

        var parts = new List<string>(4);
        if (stroke.Ctrl) parts.Add("Ctrl");
        if (stroke.Alt) parts.Add("Alt");
        if (stroke.Shift) parts.Add("Shift");
        parts.Add(stroke.Key);
        return string.Join('+', parts);
    }

    /// <summary>把 <see cref="Serialize"/> 的写法读回来。空串或只有修饰键、或有两个非修饰键，都算解析失败。</summary>
    public static bool TryParse(string? text, out KeyStroke stroke)
    {
        stroke = KeyStroke.None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        bool ctrl = false, alt = false, shift = false;
        string? key = null;

        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                default:
                    if (key is not null) return false;
                    key = part;
                    break;
            }
        }

        if (key is null) return false;

        stroke = new KeyStroke(key, ctrl, alt, shift);
        return true;
    }

    /// <summary>屏上显示那份写法：修饰键中间带空格，特殊键翻成人看得懂的字（空格、←、[、Esc…）。空键是「未设置」。</summary>
    public static string Format(KeyStroke stroke)
    {
        if (stroke.IsEmpty) return "未设置";

        var text = new StringBuilder();
        if (stroke.Ctrl) text.Append("Ctrl + ");
        if (stroke.Alt) text.Append("Alt + ");
        if (stroke.Shift) text.Append("Shift + ");
        text.Append(Display(stroke.Key));
        return text.ToString();
    }

    private static string Display(string key) => key switch
    {
        "Space" => "空格",
        "Left" => "←",
        "Right" => "→",
        "Up" => "↑",
        "Down" => "↓",
        "Back" => "Backspace",
        "BracketLeft" => "[",
        "BracketRight" => "]",
        "Escape" => "Esc",
        _ => key
    };

    /// <summary>测试不变量：没有两个动作共用同一个默认键 —— 否则装机就带着一个 <see cref="Rebind"/> 拦不住的冲突。</summary>
    public static bool DefaultsAreUnique()
    {
        var seen = new HashSet<KeyStroke>();
        foreach (var action in Actions)
            if (!seen.Add(action.Default)) return false;

        return true;
    }
}
