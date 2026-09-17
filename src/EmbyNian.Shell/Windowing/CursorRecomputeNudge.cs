using System.Collections.Generic;
using System.Runtime.InteropServices;
using EmbyNian.Diagnostics;
using EmbyNian.Shell.Interop;

namespace EmbyNian.Shell.Windowing;

/// <summary>
/// 重问光标的一扇小窗（第二十七报，2026-09-17）。
/// <para>
/// 集成模式藏匿期的慢性箭头（AyuGram 收消息，屏二无弹窗，屏一已藏的光标冒出来，坐标不动、无输入、
/// 我们全以为自己还藏着）已经在两天日志里定案机理：框架的输入站点把箭头发布到全局光标上之后，
/// 本线程队列的每拍 <c>SetCursor</c>（<see cref="HostWindow.KeepCursorHidden"/>）因为「形状不在本队列
/// 被显示」而够不着屏幕——全局光标是「最后发布者获胜」。要夺回来，只有一条不需要鼠标输入的路：
/// <b>指针下的窗口变了</b>（显示/隐藏/创建/销毁都会），win32k 重算光标并重新发 <c>WM_SETCURSOR</c>
/// 询问，而应答里的 <c>SetCursor</c> 是被 win32k 信任的改形路径（<see cref="HostWindow.IslandDispatch"/>
/// 和 <see cref="HostWindow.Route"/> 的拦截等的正是这一问）。
/// </para>
/// <para>
/// 本类就是制造那次「变了」的道具：一扇 4×4、alpha=1/255（hit-test 命中但肉眼不可见）、不夺焦点、
/// 不进任务栏的小窗，生在屏幕外；<see cref="Poke"/> 把它挪到指针处显出来、当拍收走。显示与收走各是
/// 一次「指针下的窗口变了」，重算的 <c>WM_SETCURSOR</c> 落到探针头上（同步派发，就在 <c>ShowWindow</c>
/// 的调用栈里），窗口过程替藏匿答透明；它同样会流经岛桥与宿主的既有拦截。整个显隐是同一线程上
/// 相邻的两句调用，消息循环没有任何一拍能看见它显着——不存在吃掉用户点击的窗口期。
/// </para>
/// <para>
/// 这是「重算」，不是「注入」：全程没有 <c>SendInput</c>/<c>SetCursorPos</c>，没有一丁点鼠标输入
/// 进系统（那是第 NudgeCursorState 一路的旧账，2026-09-14 已定罪退役）。动的只是一扇我们自己的窗口。
/// </para>
/// </summary>
internal sealed class CursorRecomputeNudge : IDisposable
{
    private const string Category = "重算光标";

    private const string ClassName = "EmbyNian.CursorRecomputeProbe";

    private static readonly WindowProcedure Procedure = ProbeDispatch;

    /// <summary>探针 HWND 到实例，窗口过程靠它找到自己的 <see cref="_owner"/>。与 HostWindow.Windows 同款。</summary>
    private static readonly Dictionary<IntPtr, CursorRecomputeNudge> Windows = new();

    private static bool _classRegistered;

    private readonly HostWindow _owner;

    private IntPtr _window;

    /// <summary>创建失败要记住：这是一条十赫兹拍子上的恢复路径，败一次就别再每秒重试一遍。</summary>
    private bool _failed;

    public CursorRecomputeNudge(HostWindow owner) => _owner = owner;

    /// <summary>
    /// 在 <paramref name="at"/>（屏幕物理像素）处把探针显出来再收走。成功返回真——「真」只表示
    /// 那两下窗口变化发生了，重算是否把箭头收回来由调用方下一拍自己看（屏上异形的连续计数）。
    /// </summary>
    internal bool Poke(NativePoint at)
    {
        if (_failed) return false;

        if (_window == IntPtr.Zero)
        {
            try
            {
                if (!Create())
                {
                    _failed = true;
                    return false;
                }
            }
            catch (Exception error)
            {
                _failed = true;
                Log.Warn(Category, "重算探针建不起来，升级路径退化为只重发布", error);
                return false;
            }
        }

        if (!Native.SetWindowPos(_window, Native.HwndTopMost, at.X, at.Y, 4, 4, Native.SwpNoActivate))
        {
            Log.Debug(Category, $"重算探针挪位失败（Win32 错误 {Marshal.GetLastWin32Error()}），这一拍作罢");
            return false;
        }

        // 显与收之间没有任何用户代码、任何消息循环的一拍：WM_SETCURSOR 的重算询问是同步派发的，
        // 就嵌在这两行调用的返回路上。收走之后哪怕再来一次重算，指针下的窗口也还是原来那个。
        Native.ShowWindow(_window, Native.SwShowNoActivate);
        Native.ShowWindow(_window, Native.SwHide);
        return true;
    }

    private bool Create()
    {
        EnsureClass();

        // 生在屏幕外（不显示）：「显出来」必须是 Poke 时序里的一次真变化，重算才落在那里。
        _window = Native.CreateWindowEx(
            Native.WsExLayered | Native.WsExToolWindow | Native.WsExNoActivate | Native.WsExTopMost,
            ClassName,
            null,
            Native.WsPopup,
            -100, -100, 4, 4,
            IntPtr.Zero,
            IntPtr.Zero,
            Native.GetModuleHandle(null),
            IntPtr.Zero);

        if (_window == IntPtr.Zero)
        {
            Log.Warn(Category, $"重算探针建不起来（Win32 错误 {Marshal.GetLastWin32Error()}），升级路径退化为只重发布");
            return false;
        }

        // alpha=1 而不是 0：alpha=0 的分层区域对鼠标是穿透的，就永远轮不到它被 WM_SETCURSOR 问到。
        Native.SetLayeredWindowAttributes(_window, 0, 1, Native.LwaAlpha);

        Windows[_window] = this;
        return true;
    }

    private static void EnsureClass()
    {
        if (_classRegistered) return;

        // 分配一次、不回收：RegisterClassEx 留的是指针本身（与主窗口类同一规矩，见 HostWindow.EnsureClassRegistered）。
        var className = Marshal.StringToHGlobalUni(ClassName);

        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = Native.GetModuleHandle(null),
            // 类光标留零＝「本类不设形状」：藏匿期的应答归 WM_SETCURSOR 的拦截，不给任何人留下
            // 「这个类自己画了个箭头」的后门。
            Cursor = IntPtr.Zero,
            Background = IntPtr.Zero,
            ClassName = className
        };

        if (Native.RegisterClassEx(ref windowClass) == 0)
            throw new InvalidOperationException($"注册重算探针窗口类失败，错误码 {Marshal.GetLastWin32Error()}");

        _classRegistered = true;
    }

    private static IntPtr ProbeDispatch(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (Windows.TryGetValue(window, out var probe))
            {
                // OS 在问「指针下此刻该是什么形状」。藏匿期答透明并 return TRUE——这是全局光标被
                // 框架发布了箭头之后唯一被 win32k 信任的改形路径；非藏匿期（理论上到不了）照常放行。
                if (message == Native.WmSetCursor && probe._owner.CursorHidden)
                {
                    Native.SetCursor(probe._owner.BlankCursor);
                    return new IntPtr(1);
                }

                if (message == Native.WmNcDestroy) Windows.Remove(window);
            }
        }
        catch (Exception error)
        {
            // 托管异常不许穿回原生帧（与 IslandDispatch 同一条纪律）。
            Log.Error(Category, "处理重算探针窗口消息时出错", error);
        }

        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_window == IntPtr.Zero) return;

        Windows.Remove(_window);
        Native.DestroyWindow(_window);
        _window = IntPtr.Zero;
    }
}
