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

        // ProtectedCursor is cached XAML policy, not the cursor currently published by InputSite.
        // Re-publish through the island without moving the pointer. Both states are invisible;
        // the intermediate null defeats the input source's same-object setter fast path.
        _hiddenSource.Cursor = null;
        _hiddenSource.Cursor = blank;
    }

    /// <summary>
    /// 把「这块画面要透明」整条重说一遍（第二十七报，2026-09-17）：藏匿期屏上挂了外来箭头、
    /// <see cref="KeepCursorHidden"/> 那套「同对象短路的防」每拍重申却收不回来时用的那一下。
    /// <para>
    /// 与每拍重申差在两处，各对着一种「说了不算」。<c>Cursor = null; Cursor = blank;</c> 是给
    /// <c>ProtectedCursor</c> 的：同对象同值的赋值走框架的快路径，什么也不会重新推导，断开一次
    /// 才逼它把这块画面的形状从头算。<c>InputPointerSource.GetForIsland</c> 重取再 null→blank 是给
    /// 源头的：两天日志里我们的透明句柄在全局再未出现，而旧源上的翻来覆去毫无效果——源的那头
    /// （InputSite 的发布点）很可能已经换了对象或换了值，重取一个才是对着现在这家说话。
    /// </para>
    /// <para>
    /// 两次赋值之间没有指针事件，本方法自己不会触发站点发布；但下一次任何人触发的发布读到的
    /// 都是我们刚立的值。是否真收回来由调用方的检测下一拍自己看，救不回来就升级指针处 1px
    /// 往返（第二十九报；二十七报那扇无输入重算小窗因真机连九十一拍无效而退役）。
    /// </para>
    /// </summary>
    internal void RepublishCursor(InputCursor? blank)
    {
        if (blank is null || XamlRoot?.ContentIsland is not { } island) return;

        Cursor = null;
        Cursor = blank;

        var source = InputPointerSource.GetForIsland(island);
        source.Cursor = null;
        source.Cursor = blank;
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
