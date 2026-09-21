using System.Runtime.InteropServices;

namespace EmbyNian.Shell.Interop;

/// <summary>
/// The Win32 surface the shell needs. The window is ours rather than the framework's, so the parts
/// a normal WinUI app never touches — class registration, the message a resize arrives as, the DWM
/// attributes — all have to be declared here.
/// </summary>
internal delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

/// <summary>
/// WNDCLASSEXW. Deliberately blittable — the two string members are <see cref="IntPtr"/> rather than
/// <c>string</c> with a <c>MarshalAs</c>, because source-generated interop (<c>LibraryImport</c>)
/// only marshals structs whose fields are blittable. The caller allocates the name.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowClass
{
    public uint Size;
    public uint Style;
    public IntPtr Procedure;
    public int ClassExtra;
    public int WindowExtra;
    public IntPtr Instance;
    public IntPtr Icon;
    public IntPtr Cursor;
    public IntPtr Background;
    public IntPtr MenuName;
    public IntPtr ClassName;
    public IntPtr SmallIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

/// <summary>
/// 键盘钩子过程的形状（WH_KEYBOARD）：nCode 是钩子码，wParam 直接就是虚键码，lParam 和键盘消息共用
/// 同一副位标（第 30 位自动重复、31 抬起、29 Alt）。像 <see cref="WindowProcedure"/> 一样活在命名空间
/// 层；持有它的字段必须一直拉着 —— Windows 握着原始 thunk，委托被 GC 之后第一次按键就是进程崩溃。
/// </summary>
internal delegate IntPtr KeyboardHookProcedure(int code, IntPtr wParam, IntPtr lParam);

/// <summary>
/// GUITHREADINFO，键盘兜底只问它一件事：这一键按下时焦点 HWND 在哪。cbSize 必须先填上结构体自己的
/// 大小，系统才肯回话。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GuiThreadInfo
{
    public int Size;
    public uint Flags;
    public IntPtr ActiveWindow;
    public IntPtr FocusWindow;
    public IntPtr CaptureWindow;
    public IntPtr MenuOwnerWindow;
    public IntPtr MoveSizeWindow;
    public IntPtr CaretWindow;
    public NativeRect CaretRect;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MinMaxInfo
{
    public NativePoint Reserved;
    public NativePoint MaxSize;
    public NativePoint MaxPosition;
    public NativePoint MinTrackSize;
    public NativePoint MaxTrackSize;
}

/// <summary>
/// MONITORINFO, for the one thing fullscreen actually needs: the bounds of the monitor the window is
/// mostly on. <c>Monitor</c> rather than <c>Work</c> — fullscreen covers the taskbar too.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    public uint Size;
    public NativeRect Monitor;
    public NativeRect Work;
    public uint Flags;
}

/// <summary>
/// MONITORINFOEX — MONITORINFO plus the display device name (<c>\\.\DISPLAY1</c>), which is the only way to
/// ask <c>EnumDisplaySettings</c> about <b>this</b> monitor rather than the primary one. Wanted for the refresh
/// rate: 设置 → 视频输出 → 高帧率或高刷新率时使用音频同步 needs to know how fast the screen is.
/// <para>
/// The name is a <c>fixed char</c> buffer rather than a <c>ByValTStr</c> string because that keeps the struct
/// blittable, which is what <c>[LibraryImport]</c> source generation requires — a string field there is a
/// compile error, not a slow path.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct MonitorInfoEx
{
    public uint Size;
    public NativeRect Monitor;
    public NativeRect Work;
    public uint Flags;

    /// <summary>CCHDEVICENAME is 32 wide characters, fixed by the API.</summary>
    public fixed char Device[32];
}

/// <summary>
/// DEVMODEW. Only <see cref="DisplayFrequency"/> is read, but the struct is laid out by offset, so every field
/// before it has to be declared — the printer/display union (16 bytes either way) is spelled as its display
/// half, and the trailing driver fields are kept so <c>dmSize</c> can be the real 220 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct DeviceMode
{
    public fixed char DeviceName[32];

    public ushort SpecVersion;
    public ushort DriverVersion;
    public ushort Size;
    public ushort DriverExtra;
    public uint Fields;

    /// <summary>POINTL dmPosition + dmDisplayOrientation + dmDisplayFixedOutput.</summary>
    public int PositionX;
    public int PositionY;
    public uint DisplayOrientation;
    public uint DisplayFixedOutput;

    public short Color;
    public short Duplex;
    public short YResolution;
    public short TrueTypeOption;
    public short Collate;

    public fixed char FormName[32];

    public ushort LogPixels;
    public uint BitsPerPel;
    public uint PelsWidth;
    public uint PelsHeight;
    public uint DisplayFlags;

    /// <summary>
    /// The number this whole struct is declared for: whole Hz, so 144 or 60 (and 59 for a 59.94 mode).
    /// 0 or 1 both mean 「the hardware's default」 rather than a rate, per the API.
    /// </summary>
    public uint DisplayFrequency;

    public uint IcmMethod;
    public uint IcmIntent;
    public uint MediaType;
    public uint DitherType;
    public uint Reserved1;
    public uint Reserved2;
    public uint PanningWidth;
    public uint PanningHeight;
}

internal static partial class Native
{
    // ---- window styles --------------------------------------------------------
    public const int WsOverlappedWindow = 0x00CF0000;
    public const int WsClipChildren = 0x02000000;
    public const int WsChild = 0x40000000;
    public const int WsVisible = 0x10000000;

    /// <summary>WS_POPUP：不带边框与所有权的顶层窗口。重算光标的探针用它——不进宿主的子链，
    /// 销毁时也不会拖累任何人的 Z 序（第二十七报）。</summary>
    public const int WsPopup = unchecked((int)0x80000000);

    /// <summary>
    /// What is left of the window style in fullscreen: no caption, no border, no resize grip. Only the
    /// bits are dropped — the window is never recreated, so the XAML island and the video child both
    /// survive going fullscreen and coming back.
    /// </summary>
    public const int WsCaption = 0x00C00000;

    public const int WsThickFrame = 0x00040000;

    public const int GwlStyle = -16;

    // ---- messages -------------------------------------------------------------
    public const uint WmDestroy = 0x0002;

    /// <summary>
    /// WM_NCDESTROY, the last message a window ever gets. Where a procedure standing in front of somebody
    /// else's lets go of the state it kept for that window.
    /// </summary>
    public const uint WmNcDestroy = 0x0082;

    /// <summary>
    /// WM_SETICON, swap a window's big or small icon — wParam picks the size (0 小档, 1 大档). 发空句柄
    /// 并不是「摘掉图标」：系统画标题栏图标时的查找链是「WM_SETICON 给的 → 窗口类图标 → exe 资源里的
    /// 第一颗」，从第一环退出之后后两环会把图标原样补回来 —— 设置窗口 2026-09-13 发过空句柄，截图上
    /// 左上角照样有图标。那个位置真正要的是一颗全透明的图标（见 <c>SettingsWindow.CreateBlankIcon</c>），
    /// 不是空。
    /// </summary>
    public const uint WmSetIcon = 0x0080;
    public const uint WmSize = 0x0005;
    public const uint WmClose = 0x0010;
    public const uint WmEraseBackground = 0x0014;
    public const uint WmGetMinMaxInfo = 0x0024;

    /// <summary>
    /// WM_SIZING, sent throughout a resize drag with the rectangle the user's pointer implies. Rewriting
    /// that rectangle is how 缩放窗口时按画面比例联动 works: the window never takes a shape it then has to
    /// correct, so there is no frame at the wrong aspect and no snap-back at the end of the drag.
    /// <para>
    /// Distinct from <see cref="WmSize"/>, which arrives after the fact and can only be obeyed.
    /// </para>
    /// </summary>
    public const uint WmSizing = 0x0214;

    /// <summary>
    /// WM_EXITSIZEMOVE, sent once when a move or resize drag ends. The moment 「how big is this window and
    /// where」 is worth writing down: <see cref="WmSizing"/> and <see cref="WmSize"/> both arrive dozens of
    /// times per drag, and every intermediate rectangle the pointer passed through is a size the user did
    /// not choose.
    /// </summary>
    public const uint WmExitSizeMove = 0x0232;

    public const uint WmDpiChanged = 0x02E0;

    /// <summary>
    /// WM_ACTIVATEAPP, sent when activation crosses a process boundary — which is the only kind of
    /// 「离开」 that should cost a fullscreen window its place in the topmost band. wParam is non-zero when
    /// this application is the one being activated.
    /// </summary>
    public const uint WmActivateApp = 0x001C;

    /// <summary>
    /// WM_ACTIVATE, sent to the window itself whenever its activation state changes — including
    /// between two windows of this process, which <see cref="WmActivateApp"/> never reports.
    /// LOWORD(wParam) is WA_INACTIVE (0) / WA_ACTIVE (1) / WA_CLICKACTIVE (2); HIWORD is the
    /// minimized state and says nothing about focus. The cursor rule reads this one
    /// （「未激活不藏、失焦显示」，2026-09-16 照搬 mpv.net 的 ActiveForm == this）.
    /// </summary>
    public const uint WmActivate = 0x0006;

    /// <summary>
    /// WM_TIMER. <see cref="WmActivateApp"/> only speaks when activation crosses <em>this</em>
    /// application's boundary, so a fullscreen window that let another app have the foreground never hears
    /// which window comes forward next; a slow tick asks for it.
    /// </summary>
    public const uint WmTimer = 0x0113;

    // ---- SetWindowPos ---------------------------------------------------------
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;

    // ---- WinEvent hook（2026-09-21「进入全屏时任务栏仍可见」的守 band 顶） -------------------
    // explorer 会在前台切换／z 序重排之后把 Shell_TrayWnd 重新提到 topmost band 的顶上，压过全屏窗口
    // （实测 work/probe-tray-during-fs.txt：进全屏 190 毫秒内任务栏爬回画面）。轮询压不住这种毫秒级的
    // 爬回，只有事件驱动：挂 out-of-context 的 WinEvent hook，凡是任务栏（或它的子窗）发生 z 序重排或
    // 系统前台变化，当拍把自己重新提回 band 顶。回调跑在装钩线程（UI 线程）上，窗口是同线程的，
    // SetWindowPos 直接安全。
    public const uint EventSystemForeground = 0x0003;
    public const uint EventObjectReorder = 0x8004;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void WinEventDelegate(
        IntPtr hook, uint evt, IntPtr window, IntPtr idObject, IntPtr idChild, uint thread, uint time);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr modifier, WinEventDelegate procedure,
        uint process, uint thread, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(IntPtr hook);

    /// <summary>
    /// Throws away the old client pixels instead of blitting them to the new position. Right for a
    /// video surface: the frame is about to be redrawn at a different size anyway, and copying it
    /// first is what produces the stretched smear across a resize drag.
    /// </summary>
    public const uint SwpNoCopyBits = 0x0100;

    public const uint SwpNoOwnerZOrder = 0x0200;

    /// <summary>
    /// Recalculates the frame after <c>WS_CAPTION</c> has been dropped or restored. Without it the window
    /// keeps drawing the caption it no longer has until something else forces a frame change.
    /// </summary>
    public const uint SwpFrameChanged = 0x0020;

    /// <summary>
    /// Posts the request to the owning thread instead of waiting for it. For resizing a window that
    /// belongs to somebody else's thread, where the synchronous form of <c>SetWindowPos</c> would send
    /// <c>WM_WINDOWPOSCHANGING</c> to that thread and block until it answers — a form this app has
    /// needed whenever it touched mpv's own windows from inside <c>WM_SIZE</c> on the UI thread.
    /// </summary>
    public const uint SwpAsyncWindowPos = 0x4000;

    /// <summary>GW_CHILD: the first child of a window in z-order, or 0 when it has none.</summary>
    public const uint GwChild = 5;

    public static readonly IntPtr HwndTop = IntPtr.Zero;

    /// <summary>
    /// 置顶. Used rather than <c>SetForegroundWindow</c>, which the OS refuses when the calling process is
    /// not already in the foreground; a z-order change is always allowed.
    /// </summary>
    public static readonly IntPtr HwndTopMost = new(-1);

    public static readonly IntPtr HwndNoTopMost = new(-2);

    /// <summary>
    /// HWND_MESSAGE: 挂在这里的窗口不上屏、不被枚举，自检拿它造一个「看得见前台、没有一寸像素」的哑窗口
    /// —— <see cref="HostWindow"/> 判断「要不要为前台窗口让出置顶」时最小化/隐藏那一分支的试验品。
    /// </summary>
    public static readonly IntPtr HwndMessage = new(-3);

    // ---- ShowWindow ----------------------------------------------------------
    public const int SwShow = 5;
    public const int SwMinimize = 6;
    public const int SwRestore = 9;
    public const int SwMaximize = 3;

    /// <summary>
    /// WM_NCHITTEST, asked of our own window by the self-check. What the frame answers for a point is the
    /// only way to put 「the top strip belongs to the picture, and the top edge still resizes」 as something
    /// measurable rather than something to be looked at.
    /// </summary>
    public const uint WmNcHitTest = 0x0084;

    /// <summary>The <c>WM_NCHITTEST</c> answers the self-check names.</summary>
    public const int HitClient = 1;
    public const int HitCaption = 2;
    public const int HitTop = 12;

    /// <summary>
    /// The three the framework's own caption buttons answer with. A window that draws them but answers
    /// <see cref="HitClient"/> there has buttons that cannot be pressed — they are a picture, and the click
    /// goes to whatever XAML happens to be underneath.
    /// </summary>
    public const int HitMinButton = 8;
    public const int HitMaxButton = 9;
    public const int HitClose = 20;

    public const uint GaRoot = 2;

    /// <summary>Per-monitor v2. Set before any window exists so WinUI and our own HWND agree.</summary>
    public static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(IntPtr context);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr GetModuleHandle(string? name);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(ref WindowClass windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateWindowEx(
        int extendedStyle,
        string className,
        string? windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    public static partial IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    public static partial IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    /// <summary>IDC_ARROW, and the resource id the .NET SDK gives the icon it links into a WinExe.</summary>
    public static readonly IntPtr ArrowCursor = new(32512);

    /// <summary>IDC_WAIT. Only the self-check loads it, and only to find out whether the OS's own answer about
    /// the cursor moves when we set a shape it can name — 「藏起来了没有」 is unanswerable until something
    /// known-visible proves the question is being heard.</summary>
    public static readonly IntPtr WaitCursor = new(32514);

    public static readonly IntPtr ApplicationIconResource = new(32512);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetWindow(IntPtr window, uint command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustWindowRectExForDpi(ref NativeRect rect, int style, [MarshalAs(UnmanagedType.Bool)] bool menu, int extendedStyle, uint dpi);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr window);

    /// <summary>
    /// 一个（多半是别人的）窗口的标题，给要指名道姓的日志行用 —— 光一个地址说不出去年让出置顶的那个
    /// 0x5040C 是谁。跨进程可用；没有标题的窗口得到零。
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowText(IntPtr window, [Out] char[] text, int capacity);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr window);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 从一段 ICO 图像数据造一颗图标。设置窗口用它造「整幅全透明」的那颗占住标题栏 —— 见
    /// <c>SettingsWindow.CreateBlankIcon</c>。必须是 32bpp：单色 1bpp 的透明掩码 DWM 不认，
    /// 会画成一整块暗色（2026-09-13 截图放大实量）；32bpp 图标按 alpha 合成，alpha 全 0 才是真透明。
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr CreateIconFromResourceEx(
        byte[] presBits, uint size, [MarshalAs(UnmanagedType.Bool)] bool isIcon, uint version,
        int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("user32.dll")]
    public static partial IntPtr WindowFromPoint(NativePoint point);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetAncestor(IntPtr window, uint flags);

    /// <summary>
    /// The window class behind an hwnd. Only the self-check uses it, to name whatever window is really on
    /// screen where ours should be — an hwnd on its own tells a reader nothing.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassName(IntPtr window, [Out] char[] name, int capacity);

    /// <summary>
    /// Where the pointer really is, which XAML cannot always say. A <c>PointerExited</c> raised because
    /// the pointer left the window from over a child reports a position inside the page's own bounds —
    /// indistinguishable, by geometry alone, from one that merely stepped off a control onto the picture.
    /// Asking the OS is the only answer that is never ambiguous.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(IntPtr window, ref NativePoint point);

    /// <summary>
    /// Whether a mouse button is down this instant, asked of the OS rather than tracked from events. A
    /// drag on one of the sliders is a pointer that may hold perfectly still for as long as the hand takes
    /// to decide, and the chrome's patience for a still pointer has a limit — so the drag has to be
    /// visible to the timer. Pressed/released bookkeeping could answer that too, right up until a release
    /// arrives somewhere that raises no event on us and the chrome stays pinned open forever; the button's
    /// real state cannot get stuck.
    /// </summary>
    public static bool MouseButtonDown() =>
        (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0 || (GetAsyncKeyState(VkRightButton) & 0x8000) != 0;

    /// <summary>
    /// Whether the left button alone is down. A title-bar drag is the left button's gesture and nothing
    /// else's, and it is asked of the OS for the reason above and one more: the release that ends a drag can
    /// arrive somewhere that raises no event on us — over another monitor, over a window that opened on top —
    /// and a drag that only ends on an event it may never get is the stuck window this replaced.
    /// </summary>
    public static bool LeftButtonDown() => (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;

    private const int VkLeftButton = 0x01;
    private const int VkRightButton = 0x02;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    // ---- fullscreen and 置顶 ---------------------------------------------------
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr GetWindowLongPtr(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>
    /// The same <c>GetMonitorInfoW</c> asked for the longer struct, i.e. for the display device name.
    /// Declared separately rather than replacing the short one: everything else here only wants the bounds,
    /// and the short struct is the one every existing caller passes.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoEx(IntPtr monitor, ref MonitorInfoEx info);

    /// <summary>
    /// The current display mode of one monitor, named by its <c>\\.\DISPLAYn</c> device name. Null asks about
    /// the primary display, which is exactly the wrong answer on a two-monitor machine, so callers pass a name.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplaySettings(string? deviceName, uint modeNumber, ref DeviceMode mode);

    /// <summary>ENUM_CURRENT_SETTINGS — 「what the display is set to right now」 rather than a mode from its list.</summary>
    public const uint EnumCurrentSettings = unchecked((uint)-1);

    /// <summary>MONITOR_DEFAULTTONEAREST — the right answer for a window straddling two displays.</summary>
    public const uint MonitorDefaultToNearest = 2;

    // ---- 全屏时的任务栏 ---------------------------------------------------------
    // A borderless window the size of the monitor is not a fullscreen window as far as the shell is
    // concerned, and the taskbar is WS_EX_TOPMOST: it keeps drawing over the picture. Explorer does
    // have an automatic guess, but it only re-runs when a window is activated, and going fullscreen
    // here is a style change plus one SetWindowPos on a window that is already active — no activation,
    // so the guess never runs and 「全屏后 windows 任务栏还在」.
    //
    // ITaskbarList2::MarkFullscreenWindow is the documented way to say it outright, which is what
    // Chromium does for the same reason. Called through the vtable rather than a [ComImport] interface:
    // three slots is less machinery than a COM interop type, and it keeps this file's one-P/Invoke-per-
    // call shape.
    public const uint ClsCtxInprocServer = 1;

    /// <summary>CLSID_TaskbarList.</summary>
    private static readonly Guid TaskbarListClass = new("56FDF344-FD6D-11D0-958A-006097C9A090");

    /// <summary>IID_ITaskbarList2, whose one added method is <c>MarkFullscreenWindow</c>.</summary>
    private static readonly Guid TaskbarList2Interface = new("602D4995-B13A-429B-A66E-1935E44F4317");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ComRelease(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TaskbarListInit(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TaskbarListMarkFullscreen(
        IntPtr self, IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(
        ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId, out IntPtr instance);

    /// <summary>
    /// Tells the shell that <paramref name="window"/> is (or is no longer) fullscreen, so the taskbar
    /// drops out of the way and comes back. False if the shell could not be reached at all, which is a
    /// missing taskbar rather than a broken window — fullscreen itself has already happened by then.
    /// </summary>
    public static bool MarkFullscreen(IntPtr window, bool fullscreen)
    {
        var classId = TaskbarListClass;
        var interfaceId = TaskbarList2Interface;

        if (CoCreateInstance(ref classId, IntPtr.Zero, ClsCtxInprocServer, ref interfaceId, out var taskbar) < 0
            || taskbar == IntPtr.Zero)
            return false;

        try
        {
            // Slots 0-2 are IUnknown, 3-7 are ITaskbarList — HrInit first, which the documentation
            // requires before any other method on the object.
            var vtable = Marshal.ReadIntPtr(taskbar);
            var init = Marshal.GetDelegateForFunctionPointer<TaskbarListInit>(
                Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
            if (init(taskbar) < 0) return false;

            var mark = Marshal.GetDelegateForFunctionPointer<TaskbarListMarkFullscreen>(
                Marshal.ReadIntPtr(vtable, 8 * IntPtr.Size));
            return mark(taskbar, window, fullscreen) >= 0;
        }
        catch (Exception)
        {
            // A shell that answers with something other than an ITaskbarList2 is not worth a crash over.
            return false;
        }
        finally
        {
            var release = Marshal.GetDelegateForFunctionPointer<ComRelease>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(taskbar), 2 * IntPtr.Size));
            release(taskbar);
        }
    }

    public const int GwlExStyle = -20;
    public const int WsExTopMost = 0x00000008;

    /// <summary>WS_EX_LAYERED：整窗 alpha 可调。探针取 alpha=1——非零才有 hit-test（alpha=0 的分层
    /// 区域对鼠标是穿透的），但 1/255 的不透明度加上 4×4 的尺寸与「显出来当拍就收走」的时序，
    /// 人眼与帧率都不可能看见它（第二十七报）。</summary>
    public const int WsExLayered = 0x00080000;

    /// <summary>WS_EX_TOOLWINDOW：不进任务栏与 Alt-Tab。探针每秒至多显隐一轮，谁也不该在切换列表里看到它。</summary>
    public const int WsExToolWindow = 0x00000080;

    /// <summary>WS_EX_NOACTIVATE：显出来也不夺前台。藏匿期的播放窗口多半没有焦点（全屏置顶），
    /// 探针绝不能改这件事。</summary>
    public const int WsExNoActivate = 0x08000000;

    /// <summary>SW_HIDE，<see cref="ShowWindow"/> 的收走命令。0 是它的值，显式起见给个名字。</summary>
    public const int SwHide = 0;

    /// <summary>SW_SHOWNA：显示但不激活。与 WS_EX_NOACTIVATE 双保险。</summary>
    public const int SwShowNoActivate = 8;

    /// <summary>SetLayeredWindowAttributes 的 LWA_ALPHA：按 alpha 调整整窗不透明度。</summary>
    public const uint LwaAlpha = 2;

    /// <summary>
    /// Sets the whole-window alpha of a layered window. Only the cursor-recompute probe uses it,
    /// pinned at 1/255 — present to the hit test, absent to the eye; see <see cref="WsExLayered"/>.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);

    /// <summary>
    /// The taskbar itself. Only the self-check uses it, to report whether the shell really did stand
    /// aside — the observable half of <see cref="MarkFullscreen"/>, which otherwise can only say that
    /// the request was accepted.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindow(string? className, string? windowName);

    /// <summary>
    /// Which window is active process-wide — asked by the fullscreen z-order valve, to find out whether
    /// the window that took the foreground is one the picture would be covering, and by the self-check to
    /// qualify the taskbar reading: the shell lowers its tray for the <em>foreground</em> fullscreen
    /// window, so 「still topmost」 means something quite different when the active window is somebody
    /// else's.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    /// <summary>
    /// Which process a window belongs to, so the valve can tell one of our own popups — a XAML flyout is
    /// its own top-level window — from another application's window.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    /// <summary>
    /// A plain <c>WM_TIMER</c> on the host window itself, rather than a dispatcher timer: this class is
    /// where the message loop already is, and the tick has to be read alongside the activation messages it
    /// stands in for. Returns 0 on failure, which just means no ticks.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nuint SetTimer(IntPtr window, nuint id, uint milliseconds, IntPtr callback);

    /// <inheritdoc cref="SetTimer"/>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(IntPtr window, nuint id);

    /// <summary>
    /// MSG, for the one place that has to keep the message loop turning while it waits: a self-check
    /// probe that blocks the UI thread is a window the compositor and the shell both see as hung, and
    /// what such a window is asked to prove about itself is not what the app really does.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        private readonly uint _private;
    }

    public const uint PmRemove = 0x0001;

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(out NativeMessage message, IntPtr window, uint first, uint last, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref NativeMessage message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessage(ref NativeMessage message);

    /// <summary>
    /// The cursor's own show counter, which is what 「鼠标静止后自动隐藏」 comes down to. There is no
    /// WinUI equivalent: <c>UIElement.ProtectedCursor</c> can name a shape but has no way to say 「none」.
    /// Counted rather than boolean, exactly as the OS counts it, so a mismatched pair leaves the cursor
    /// permanently invisible over every window in the process.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);

    /// <summary>
    /// How long the OS itself allows between two clicks for them to count as a double click, in
    /// milliseconds. The ceiling on how long 点击画面暂停 is held back before it is issued — see
    /// <see cref="EmbyNian.Playback.PictureTap.HoldFor"/>, which caps it far below this on purpose.
    /// <para>
    /// Asked afresh per tap rather than cached: it is a Control Panel setting and can change while the app runs, and
    /// one user32 call is cheaper than keeping a copy in step with it.
    /// </para>
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial uint GetDoubleClickTime();

    /// <summary>
    /// The whole of what the OS will say about the desktop's cursor: the raw <c>CURSORINFO</c> flags, the shape
    /// handle, and where it believes the pointer is. Printed by the cursor probe as an outside witness and
    /// asserted on nowhere, for the reasons in <see cref="GetCursor"/>: 「藏起来了没有」 came back 「显示」 with
    /// the show counter already below zero, and the shape handle beside the flag is what tells 「the call did not
    /// take」 apart from 「this is not the cursor I set」.
    /// </summary>
    public static (int Flags, IntPtr Shape, NativePoint At)? CursorSnapshot()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) ? (info.Flags, info.Cursor, info.ScreenPosition) : null;
    }

    /// <summary>CURSORINFO. Blittable, so source-generated interop can pass it by reference.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public NativePoint ScreenPosition;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CursorInfo info);

    /// <summary>
    /// Moves the cursor. Only the self-check uses it, and only to put the pointer somewhere the question
    /// it is asking has a definite answer — 「is the cursor hidden over our own window」 cannot be settled
    /// while the cursor is over someone else's. It puts it back where it found it.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    /// <summary>
    /// Sets the cursor shape, or hides it outright with <see cref="IntPtr.Zero"/> — which is the other way
    /// to hide a cursor, and the one that does not go through a counter. It lasts until the next
    /// <c>WM_SETCURSOR</c> puts a shape back.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr SetCursor(IntPtr cursor);

    /// <summary>
    /// A cursor that draws nothing: 32×32, an all-ones AND mask over an all-zeros XOR mask, which is the
    /// bit pattern for 「transparent everywhere」.
    /// <para>
    /// Kept because 「none」 and 「a shape you cannot see」 are not the same thing to Windows, and only the
    /// second one can be handed to the places that hide a cursor for good. <see cref="SetCursor"/> takes
    /// <see cref="IntPtr.Zero"/> and removes the cursor from the calling thread's queue — for the length of
    /// that queue's next opinion, and only for windows that queue owns. A class cursor cannot be nothing in
    /// the same way: set to <see cref="IntPtr.Zero"/> it means 「this class does not set the cursor」, so the
    /// last shape stays on screen. Given a transparent one it means 「this class's cursor is blank」, which is
    /// the same answer no matter which thread asks or when.
    /// </para>
    /// </summary>
    public static IntPtr CreateBlankCursor()
    {
        // 掩码在 Core 里（EmbyNian.Playback.CursorMask），由单测钉着 —— 从这一轮起 WinUI 的输入管线也会照着这
        // 张掩码画光标，算错的下场不再是「藏不掉」而是「画面正中一块黑方块」。行距按 WORD 补齐那条尤其容易错。
        var (and, xor) = EmbyNian.Playback.CursorMask.Transparent(32, 32);
        if (and.Length == 0) return IntPtr.Zero;

        return CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr CreateCursor(IntPtr instance, int hotX, int hotY, int width, int height,
        [In] byte[] and, [In] byte[] xor);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyCursor(IntPtr cursor);

    /// <summary>GCLP_HCURSOR, the class-wide cursor <see cref="SetClassCursor"/> reads and writes.</summary>
    public const int ClassCursorIndex = -12;

    /// <summary>GCLP_HBRBACKGROUND, the class-wide brush <see cref="SetClassBackground"/> writes.</summary>
    public const int ClassBackgroundIndex = -10;

    /// <summary>
    /// The cursor a whole window class wants, and the only lever here that is not tied to a message queue:
    /// it can be set from this thread for a window created by another one, which is the case that matters —
    /// libmpv makes its own child window on its own thread, and the WinUI island's windows are made by the
    /// framework.
    /// </summary>
    /// <returns>The previous handle, which is what makes this reversible.</returns>
    [LibraryImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    public static partial IntPtr SetClassCursor(IntPtr window, int index, IntPtr value);

    /// <summary>
    /// A whole window class's background brush. Same <c>SetClassLongPtr</c> as the cursor above, different
    /// index: the XAML island windows are made by the framework and come with the class's default white
    /// background, so whatever the island's content does not paint — the moment a page is being swapped, the
    /// ring where the content is smaller than the island — shows up as white. 2026-09-17: three of the user's
    /// screenshots are exactly that (a 828×647 rectangle, an 875×645 one with the page's own gradient over
    /// it, and 8~10px bands along the picture's edges). Pointing the class at the app's base colour is the fix;
    /// the brush has to outlive the class, which is why it is the same static one the window erases with.
    /// </summary>
    /// <returns>The previous handle, which is what makes this reversible.</returns>
    [LibraryImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    public static partial IntPtr SetClassBackground(IntPtr window, int index, IntPtr value);

    /// <summary>
    /// The next immediate child of <paramref name="parent"/> after <paramref name="after"/>, or
    /// <see cref="IntPtr.Zero"/> at the end. <c>FindWindowEx</c> with neither a class nor a title is the
    /// enumeration without a callback, which source-generated interop cannot marshal.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW")]
    public static partial IntPtr NextChild(IntPtr parent, IntPtr after, IntPtr className, IntPtr windowName);

    /// <summary>
    /// The next top-level window of <paramref name="className"/> after <paramref name="after"/>, or
    /// <see cref="IntPtr.Zero"/> at the end. <see cref="FindWindow"/> finds only the first one, which is a
    /// window short for a class Windows makes one of per monitor — 「Shell_SecondaryTrayWnd」, the taskbar on
    /// every screen that is not the main one.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr NextWindow(IntPtr parent, IntPtr after, string? className, string? windowName);

    /// <summary>
    /// The shape the calling thread's queue currently has, or <see cref="IntPtr.Zero"/> for 「none」. This is
    /// the witness <c>GetCursorInfo</c> could not be: that one reports the desktop's cursor, which is
    /// recomputed when the pointer moves and therefore says nothing at all about a
    /// <see cref="SetCursor"/> made while the pointer holds still — and holding still is the entire
    /// circumstance in which the player hides it. This one is read straight out of the thread's own cursor
    /// state, so it answers the only question worth asking: did the call take.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetCursor();

    /// <summary>
    /// WM_SETCURSOR: 「the pointer moved over you, say what the cursor looks like」. Answered rather than passed
    /// on while the player wants no cursor, so that a move that ends over the picture does not put the arrow
    /// back. It is not the whole of hiding the cursor over a WinUI 3 island — the island's pointer input does
    /// not come this way at all — but it is the one place the OS itself asks.
    /// </summary>
    public const uint WmSetCursor = 0x0020;

    /// <summary>
    /// The two halves of a <c>WM_SETCURSOR</c>'s <c>lParam</c>: HTCLIENT in the low word — 「the pointer is
    /// over your client area」 — and the mouse message that prompted it in the high word. Only the self-check
    /// builds one, and it builds one because a real <c>WM_SETCURSOR</c> only arrives when the pointer moves,
    /// while the cursor hides precisely because it has stopped: asking the window directly is the one way to
    /// prove the interception answers without moving the mouse, which would ask for the cursor back.
    /// </summary>
    public const int HtClient = 1;

    /// <summary>WM_MOUSEMOVE, the prompt a synthetic <c>WM_SETCURSOR</c> claims to be answering.</summary>
    public const uint WmMouseMove = 0x0200;

    /// <summary>
    /// Which thread's message queue a window belongs to, for the one question 「藏起来了没有」 turns on that
    /// nothing else can answer: <c>SetCursor</c> is a per-queue call, so a shape set here only reaches the
    /// screen if the window the pointer is over belongs to this thread. The island's bridge window is created
    /// by WinUI rather than by us, and 「同一个线程」 is therefore a fact to measure rather than assume.
    /// </summary>
    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    /// <summary>
    /// 线程级键盘钩子（WH_KEYBOARD，<see cref="WhKeyboard"/>）。hMod 必须是 NULL：钩子过程就在本进程、
    /// dwThreadId 指本进程线程，没有注入这回事 —— 钩子只在键盘消息被送进本线程队列时被叫到（焦点在
    /// 别的进程时一次都不会响），所以它天生不抢别家应用的键，只兜「键到了我们家、XAML 却收不到」的底。
    /// 委托须由调用方一直拉着（见 <see cref="KeyboardHookProcedure"/>）。
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static partial IntPtr SetWindowsHookEx(int hook, KeyboardHookProcedure procedure, IntPtr module, uint threadId);

    /// <summary>摘钩子。窗口销毁时必调：线程活着而钩子悬着，每颗键都要多过一遍死委托。</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(IntPtr hook);

    /// <summary>钩子链的下一棒。放行时必须喊，否则线程上后来装的钩子从此听不见键盘。</summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// child 是否落在 parent 的窗口树里。键盘兜底拿它做两道闸：焦点不在宿主窗口树里 → 键不是发给
    /// 我们的，不接；焦点在岛里 → XAML 自己收得到，也不接。
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsChild(IntPtr parent, IntPtr child);

    /// <summary>
    /// 本线程的 GUI 状态快照。键盘兜底只读 <see cref="GuiThreadInfo.FocusWindow"/>：线程级钩子自己
    /// 分不出「焦点在岛里」还是「焦点掉在宿主/视频子窗口上」，这一问才是判据。threadId 必须是自己的
    /// 线程号 —— 传 0 问到的是前台线程，而前台可能是别家。
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    /// <summary>WH_KEYBOARD：线程级键盘钩子的 idHook 值。</summary>
    public const int WhKeyboard = 2;

    /// <summary>
    /// Refreshes the input queue with a one-pixel relative round trip. Both packets are submitted together,
    /// avoiding absolute-coordinate rounding and returning the pointer to its original position.
    /// </summary>
    public static bool NudgeCursorState()
    {
        if (!GetCursorPos(out var at)) return false;
        var right = GetSystemMetrics(SmXVirtualScreen) + GetSystemMetrics(SmCxVirtualScreen) - 1;
        var step = at.X < right ? 1 : -1;
        var outward = new Input { Type = InputMouse, Mouse = new MouseInput { Dx = step, Flags = MouseEventMove } };
        var homeward = new Input { Type = InputMouse, Mouse = new MouseInput { Dx = -step, Flags = MouseEventMove } };
        return SendInput(2, [outward, homeward], Marshal.SizeOf<Input>()) == 2;
    }

    private const uint InputMouse = 0;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventVirtualDesk = 0x4000;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    /// <summary>
    /// 虚拟桌面矩形，唤醒取证用（第九报，2026-09-15）。用户报「屏幕一全屏播放时，屏幕二的 AyuGram
    /// 收到消息会唤起屏幕一静止隐藏的鼠标指针」，而日志里那批唤醒三天来都是同一个签名：轮询一拍之内
    /// 读数横跳整整 60px、纵向恒 0、指针随后原地静止两秒重新藏起来。物理注入不会三天都恰好 60px；
    /// 坐标重排（显示器拓扑/DPI 变化把虚拟桌面原点搬动）则恰恰会整屏平移一个固定量。藏匿与唤醒两行
    /// 从此带上当时的虚拟桌面矩形：唤醒那一刻矩形没变＝读数变化是真的（有进程在注入指针位移，去查
    /// 注入者）；矩形变了＝指针根本没动，是桌面重排把坐标搬走了，唤醒是误报。
    /// </summary>
    public static (int X, int Y, int Width, int Height) VirtualScreen() =>
        (GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
         GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));

    /// <summary>CURSORINFO.Flags 的可见位：<see cref="CursorSnapshot"/> 返回的 Flags 里「形状正在屏幕上」
    /// 就是它。CurSuppressed（0x2）是平板模式的系统抑制，不算可见。</summary>
    public const int CurShowing = 0x0001;
    public const int CurSuppressed = 0x0002;

    /// <summary>
    /// Puts the pointer at a desktop coordinate through the <em>real</em> input queue, rather than by
    /// <c>SetCursorPos</c>.
    /// <para>
    /// The difference is the whole reason this exists. <c>SetCursorPos</c> moves the coordinate and produces no
    /// input, so a XAML island under the pointer hears nothing at all — measurably: the live cursor probe
    /// reports 「XAML 事件 0 次」 for a pointer it demonstrably moved. And a probe that never made the island
    /// speak is a probe measuring the one situation in which the bug cannot happen. <c>SendInput</c> goes
    /// through the queue, so the island receives a pointer move exactly as it does from a hand.
    /// </para>
    /// <para>
    /// Absolute coordinates are normalised to a 0..65535 grid, and the grid is the <em>virtual</em> desktop's
    /// only when <c>MOUSEEVENTF_VIRTUALDESK</c> is set — without it the grid is the primary monitor and every
    /// point on a second screen lands somewhere else entirely. <c>tools/poke.ps1</c> carries the same note for
    /// the same reason.
    /// </para>
    /// </summary>
    public static bool MovePointerTo(int x, int y)
    {
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 1 || height <= 1) return false;

        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);

        var input = new Input
        {
            Type = InputMouse,
            Mouse = new MouseInput
            {
                Dx = (int)Math.Round((double)(x - left) * 65535 / (width - 1)),
                Dy = (int)Math.Round((double)(y - top) * 65535 / (height - 1)),
                Flags = MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk
            }
        };

        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    /// <summary>MOUSEINPUT. The union's largest member, and the only one <see cref="MovePointerTo"/> uses.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint Data;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>
    /// INPUT. Sequential rather than explicit: on x64 the union is aligned to eight because
    /// <see cref="MouseInput.ExtraInfo"/> is pointer-sized, which is exactly the padding the runtime inserts
    /// after the type word — and x64 is the only architecture this ships for.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    /// <summary>GWLP_WNDPROC, for standing in front of a window whose procedure is somebody else's.</summary>
    public const int GwlpWndProc = -4;

    /// <summary>
    /// Hands a message on to the procedure that was there before ours. Not <c>DefWindowProc</c>: the island's
    /// own procedure is what draws the UI, and everything except the one message we answer has to reach it.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    public static partial IntPtr CallWindowProc(IntPtr procedure, IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(IntPtr window, ref NativePoint point);

    /// <summary>VK_SHIFT, for the one thing a <c>KeyRoutedEventArgs</c> cannot answer.</summary>
    public const int VkShift = 0x10;

    /// <summary>VK_CONTROL / VK_MENU（Alt），和 <see cref="VkShift"/> 一样是 <c>KeyRoutedEventArgs</c> 不带的东西 —— 重绑的组合键要认这两个修饰键。</summary>
    public const int VkControl = 0x11;

    /// <inheritdoc cref="VkControl"/>
    public const int VkMenu = 0x12;

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int virtualKey);

    /// <summary>
    /// Whether Shift is down right now. <c>KeyRoutedEventArgs</c> carries no modifier state and
    /// <c>Z</c> and <c>Shift+Z</c> arrive as the same <see cref="Windows.System.VirtualKey"/>, so the two
    /// halves of 字幕延迟 would otherwise be the same key. The high bit is 「pressed」; the low bit is the
    /// toggle state, which is meaningless for Shift.
    /// </summary>
    public static bool ShiftHeld => (GetKeyState(VkShift) & 0x8000) != 0;

    /// <summary>Ctrl 此刻按着没有，同 <see cref="ShiftHeld"/> —— 快捷键重绑要拼出带 Ctrl 的组合键。</summary>
    public static bool CtrlHeld => (GetKeyState(VkControl) & 0x8000) != 0;

    /// <summary>Alt 此刻按着没有，同 <see cref="ShiftHeld"/>。</summary>
    public static bool AltHeld => (GetKeyState(VkMenu) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    public static partial int FillRect(IntPtr deviceContext, ref NativeRect rect, IntPtr brush);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateSolidBrush(uint color);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDC(IntPtr window);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [LibraryImport("gdi32.dll")]
    public static partial uint GetPixel(IntPtr deviceContext, int x, int y);

    // ---- DWM -----------------------------------------------------------------
    public const int DwmUseImmersiveDarkMode = 20;
    public const int DwmWindowCornerPreference = 33;
    public const int DwmSystemBackdropType = 38;

    /// <summary>DWMWCP_ROUND — Win11's own window corners, for free.</summary>
    public const int DwmCornerRound = 2;

    /// <summary>DWMSBT_MAINWINDOW, i.e. Mica.</summary>
    public const int DwmBackdropMica = 2;

    /// <summary>
    /// DWMWA_EXTENDED_FRAME_BOUNDS — the pixels a window actually occupies on screen.
    /// <para>
    /// 从 Windows 10 起 <c>GetWindowRect</c> 量的是外面那一圈：带缩放边框的窗口四边各被多包进约 7px
    /// **看不见**的边框（拖窗口时手指够得着、屏幕上一寸像素都没有的那一圈）。它平时无害，唯独
    /// 「贴着两块屏幕交界的窗口」会把这圈假像素伸进隔壁屏 —— 2026-09-14 第二次报上来的「屏幕一全屏播放
    /// 时点击屏幕二的应用，屏幕一的任务栏爬回画面上」就是这么来的：那扇窗看得见的左边正贴在 2560（屏幕二
    /// 的左沿），量出来却是 2553，于是全屏那层判它「会挡着」、让出置顶，任务栏立刻回来了。实测那扇窗：
    /// 外框 (2553,0)-(3647,632)，可见边框 (2560,0)-(3640,625)。
    /// </para>
    /// </summary>
    public const int DwmExtendedFrameBounds = 9;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>
    /// The rectangle a window really draws in: what DWM reports for
    /// <see cref="DwmExtendedFrameBounds"/>, falling back to <see cref="GetWindowRect"/> only when DWM
    /// will not answer — hidden helper windows have no composition surface, and the outer rectangle beats
    /// no rectangle at all.
    /// <para>
    /// 凡是问「这两扇窗的矩形会不会互相盖住」的地方都用它，别用 <c>GetWindowRect</c>：那一圈假像素正是
    /// <see cref="DwmExtendedFrameBounds"/> 上说的那个病根。谁在用它见 <c>HostWindow.StandsClearOfPicture</c>。
    /// </para>
    /// </summary>
    public static bool GetVisibleFrame(IntPtr window, out NativeRect rect)
    {
        if (window != IntPtr.Zero
            && DwmGetWindowAttribute(window, DwmExtendedFrameBounds, out rect, Marshal.SizeOf<NativeRect>()) == 0)
        {
            return true;
        }

        return GetWindowRect(window, out rect);
    }

    // ---- 原始输入（第十六报：藏匿期判「手还是注入」的真实输入见证）------------------------------
    // 这一区的存在理由写在 RealInputWitness 的头注释里，这里只放 Win32 的原文。要点有两条：
    // 一是结构偏移全部手工读而不用 Marshal.PtrToStructure —— RAWINPUT 内部是 union，托管结构
    // marshal 在它上面踩过坑，而这一题真正要读的只有 header 里的 hDevice 和 RAWMOUSE 里三个数，
    // 手工偏移既稳又短。二是 RAWMOUSE 在 x64 上是 24 字节（lLastX/lLastY 是 LONG 不是 SHORT），
    // header 是 24 字节 —— 两个 24 都验证过（work/rawinput-phase.py 的探测输出），别「优化」。
    public const uint WmInput = 0x00FF;

    /// <summary>HID usage page 「Generic Desktop」，鼠标在它下面。</summary>
    public const ushort UsagePageGenericDesktop = 0x01;

    /// <summary>HID usage 「Mouse」。</summary>
    public const ushort UsageMouse = 0x02;

    /// <summary>RIDEV_INPUTSINK：即使窗口没有焦点也把 <see cref="WmInput"/> 送来。收见证的窗口是全屏
    /// 播放的窗口，焦点从来不在它身上时也要收 —— 否则见证在最有用的时刻恰好缺席。</summary>
    public const uint RidevInputsink = 0x00000100;

    /// <summary>GetRawInputData 的 uiCommand：要整条 RAWINPUT（header + 设备数据）。</summary>
    public const uint RidInput = 0x10000003;

    /// <summary>RAWINPUTHEADER.dwType 的鼠标值。键盘、HID 其它设备不进见证 —— 它们不搬光标。</summary>
    public const uint RimTypeMouse = 0;

    /// <summary>RAWINPUTHEADER 的大小（x64）。</summary>
    public const int RawInputHeaderSize = 24;

    /// <summary>RAWMOUSE 的大小（x64）。</summary>
    public const int RawMouseSize = 24;

    /// <summary>RAWINPUTHEADER.dwSize 之后的 RAWMOUSE 里三个字段在 RAWINPUT 缓冲里的偏移（x64）。</summary>
    public const int RawMouseFlagsOffset = 24;
    public const int RawMouseXOffset = 36;
    public const int RawMouseYOffset = 40;

    /// <summary>RAWMOUSE.usFlags 的 MOUSE_MOVE_ABSOLUTE。真手不会走这一路（相对移动），它出现时按
    /// 位移本身算 —— 见证只问「动了没有」，不问「相对还是绝对」。</summary>
    public const ushort MouseMoveAbsolute = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, int size);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetRawInputData(IntPtr rawInput, uint command, byte[] buffer, ref uint size, int headerSize);

    /// <summary>GetRawInputDeviceInfo 的 uiCommand：要设备接口名（\?\HID#VID_…&amp;PID_…）。
    /// 第十八报加的：藏匿期冒出来的「真输入」到底来自哪块硬件 —— 鼠标、触摸屏还是数位板 ——
    /// 路径里的 VID/PID 点得出来，光看 hDevice 句柄分不清。</summary>
    public const uint RidiDevicename = 0x20000007;

    // 十九报（2026-09-16）的事故记录，写给下一位：GetRawInputDeviceInfo 是 Raw Input 全家
    // 里唯一分 A/W 导出的函数，user32 里没有无后缀导出。LibraryImport 对不带 string 参数的
    // 签名不会按 StringMarshalling 自动补后缀 —— 按字面名找 GetRawInputDeviceInfo 找不到，
    // 第一次调用即 EntryPointNotFoundException，ResolveDeviceName 的 catch 把它吞成
    // 「句柄 0x…」fallback，于是十八报上线后所有设备（连物理雷蛇鼠标在内）一律点名失败。
    // work/devname-onestep.py 的对照实验钉死了语义：W 版 pcbSize 单位是字符（不是字节）；
    // NULL 探测成功时返回 0 而不是所需数，只有失败才是 0xFFFFFFFF —— 两步都要按这个判。
    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static partial uint GetRawInputDeviceInfo_Size(IntPtr device, uint command, IntPtr buffer, ref uint size);

    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    public static partial uint GetRawInputDeviceInfo(IntPtr device, uint command, byte[] buffer, ref uint size);

    /// <summary>RIDI_DEVICENAME 的两步取名：NULL 探出字符数，再按同数字符给缓冲。返回是否拿到名字，
    /// 名字经 <paramref name="name"/> 交回（UTF-16 解码、未截 VID 段的完整接口名）。</summary>
    public static bool TryGetRawInputDeviceName(IntPtr device, out string name)
    {
        name = string.Empty;

        var chars = 0u;
        var probe = GetRawInputDeviceInfo_Size(device, RidiDevicename, IntPtr.Zero, ref chars);
        if (probe == 0xFFFFFFFF || chars == 0 || chars > 520) return false;

        var buffer = new byte[chars * 2];
        var capacity = chars;
        var written = GetRawInputDeviceInfo(device, RidiDevicename, buffer, ref capacity);
        if (written == 0xFFFFFFFF) return false;

        name = System.Text.Encoding.Unicode.GetString(buffer).Split('\0')[0];
        return name.Length > 0;
    }
}
