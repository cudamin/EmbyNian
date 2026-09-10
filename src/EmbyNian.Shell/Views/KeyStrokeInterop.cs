using Windows.System;

namespace EmbyNian.Shell.Views;

/// <summary>
/// <see cref="VirtualKey"/> ↔ <see cref="Playback.ShortcutCatalog"/> 的 token 串，全项目**唯一**碰
/// <c>VirtualKey</c> 的地方 —— Core 那一层不认 WinRT，只认 token（见 <c>KeyStroke</c> 的注释）。
/// <para>
/// 只翻裸键，修饰键不在这里：<c>KeyRoutedEventArgs</c> 不带修饰键，Ctrl/Alt/Shift 是在按下那一刻由
/// <c>Native.Ctrl/Alt/ShiftHeld</c> 从系统读的（派发和捕获两头共用这套读法）。方括号没有 <c>VirtualKey</c>
/// 名字，用原始 OEM 码（219=VK_OEM_4「[」、221=VK_OEM_6「]」），和改造前那张 switch 表里的写法一致。
/// </para>
/// </summary>
internal static class KeyStrokeInterop
{
    /// <summary>裸键翻成 token；认不出的返回 null（那一下就当没有快捷键 —— 派发时不处理，捕获时不收）。</summary>
    public static string? Token(VirtualKey key) => key switch
    {
        >= VirtualKey.A and <= VirtualKey.Z => key.ToString(),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + (key - VirtualKey.Number0))).ToString(),
        >= VirtualKey.F1 and <= VirtualKey.F12 => key.ToString(),
        VirtualKey.Space => "Space",
        VirtualKey.Enter => "Enter",
        VirtualKey.Back => "Back",
        VirtualKey.Delete => "Delete",
        VirtualKey.Insert => "Insert",
        VirtualKey.Home => "Home",
        VirtualKey.End => "End",
        VirtualKey.PageUp => "PageUp",
        VirtualKey.PageDown => "PageDown",
        VirtualKey.Left => "Left",
        VirtualKey.Right => "Right",
        VirtualKey.Up => "Up",
        VirtualKey.Down => "Down",
        VirtualKey.Escape => "Escape",
        (VirtualKey)219 => "BracketLeft",
        (VirtualKey)221 => "BracketRight",
        _ => null
    };
}
