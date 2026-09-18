using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 播放器那块画面本身：一个只多了一样东西的 <see cref="Grid"/> —— 能从外面设它的光标。
/// <para>
/// <c>UIElement.ProtectedCursor</c> 是 protected 的，只有派生类碰得到，所以要它就得有自己的类型。十几行代码换来
/// 的是「鼠标停在画面上不自动隐藏」那件老账真正的修法：指针压在 XAML 内容上时，屏上那只光标由框架的输入管线画，
/// 界面线程上的 <c>SetCursor</c>／<c>ShowCursor</c>／窗口类光标一概不在那条路上（<see
/// cref="Windowing.HostWindow.BlankInputCursor"/> 记着真片子日志里的证据）。
/// </para>
/// <para>
/// 设在这块 Grid 上而不是设在 <see cref="PlayerPage"/> 上，是因为指针压在画面上时框架命中的正是这块可命中的透明
/// 背景（<c>Background="Transparent"</c>，标记里就写着「null 背景不可命中」）—— 设在它身上就不用赌框架会不会往
/// 父级找。社区里「设在 Page 上只有压在控件上才生效」的报告说的正是背景不可命中那种情形。
/// </para>
/// </summary>
internal sealed partial class PictureSurface : Grid
{
    /// <summary>
    /// 这块画面此刻要什么形状的光标。<c>null</c> 是「交回框架，它爱画什么画什么」，也就是显示正常的箭头。
    /// </summary>
    private InputPointerSource? _hiddenSource;
    private InputCursor? _restoreCursor;

    internal void BeginCursorHide(InputCursor? blank)
    {
        if (blank is null || XamlRoot?.ContentIsland is not { } island) return;
        _hiddenSource = InputPointerSource.GetForIsland(island);
        _restoreCursor = _hiddenSource.Cursor;
        Cursor = blank;
        KeepCursorHidden(blank);
    }

    internal void KeepCursorHidden(InputCursor? blank)
    {
        if (blank is null || _hiddenSource is null) return;
        if (!ReferenceEquals(Cursor, blank)) Cursor = blank;

        // XAML keeps a transparent policy; the input source can suppress the displayed cursor outright.
        if (_hiddenSource.Cursor is not null) _hiddenSource.Cursor = null;
    }

    /// <summary>Re-publishes the input source without briefly clearing XAML's transparent policy.</summary>
    internal void RepublishCursor(InputCursor? blank)
    {
        if (blank is null || _hiddenSource is null) return;

        if (!ReferenceEquals(Cursor, blank)) Cursor = blank;
        _hiddenSource.Cursor = blank;
        _hiddenSource.Cursor = null;
    }

    internal void EndCursorHide()
    {
        Cursor = null;
        if (_hiddenSource is not null) _hiddenSource.Cursor = _restoreCursor;
        _hiddenSource = null;
        _restoreCursor = null;
    }

    internal InputCursor? Cursor
    {
        get => ProtectedCursor;
        set => ProtectedCursor = value;
    }
}
