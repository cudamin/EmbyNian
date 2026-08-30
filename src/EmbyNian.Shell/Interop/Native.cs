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

internal static partial class Native
{
    // ---- window styles --------------------------------------------------------
    public const int WsOverlappedWindow = 0x00CF0000;
    public const int WsClipChildren = 0x02000000;
    public const int WsChild = 0x40000000;
    public const int WsVisible = 0x10000000;

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

    public const uint WmDpiChanged = 0x02E0;

    /// <summary>
    /// WM_ACTIVATEAPP, sent when activation crosses a process boundary — which is the only kind of
    /// 「离开」 that should cost a fullscreen window its place in the topmost band. wParam is non-zero when
    /// this application is the one being activated.
    /// </summary>
    public const uint WmActivateApp = 0x001C;

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

    public static readonly IntPtr HwndTop = IntPtr.Zero;

    /// <summary>
    /// 置顶. Used rather than <c>SetForegroundWindow</c>, which the OS refuses when the calling process is
    /// not already in the foreground; a z-order change is always allowed.
    /// </summary>
    public static readonly IntPtr HwndTopMost = new(-1);

    public static readonly IntPtr HwndNoTopMost = new(-2);

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
    public static partial bool IsZoomed(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr window);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

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
        var opaque = new byte[32 * 32 / 8];
        Array.Fill(opaque, (byte)0xFF);

        return CreateCursor(IntPtr.Zero, 0, 0, 32, 32, opaque, new byte[32 * 32 / 8]);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr CreateCursor(IntPtr instance, int hotX, int hotY, int width, int height,
        [In] byte[] and, [In] byte[] xor);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyCursor(IntPtr cursor);

    /// <summary>GCLP_HCURSOR, the class-wide cursor <see cref="SetClassCursor"/> reads and writes.</summary>
    public const int ClassCursorIndex = -12;

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
    /// A mouse event with nowhere to go: <c>MOUSEEVENTF_MOVE</c> with a relative displacement of zero. The
    /// pointer does not move — which is the point, because the cursor hides precisely because nothing is
    /// moving — while the OS still recomputes which shape belongs on screen and asks the window under the
    /// pointer for it. Relative rather than absolute: an absolute injection is normalised through a
    /// 0..65535 grid and lands a pixel off, and a pixel is a movement to everything else in the player.
    /// </summary>
    public static bool NudgeCursorState()
    {
        var input = new Input { Type = InputMouse, Mouse = new MouseInput { Flags = MouseEventMove } };
        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }

    private const uint InputMouse = 0;
    private const uint MouseEventMove = 0x0001;

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    /// <summary>MOUSEINPUT. The union's largest member, and the only one <see cref="NudgeCursorState"/> uses.</summary>
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

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int virtualKey);

    /// <summary>
    /// Whether Shift is down right now. <c>KeyRoutedEventArgs</c> carries no modifier state and
    /// <c>Z</c> and <c>Shift+Z</c> arrive as the same <see cref="Windows.System.VirtualKey"/>, so the two
    /// halves of 字幕延迟 would otherwise be the same key. The high bit is 「pressed」; the low bit is the
    /// toggle state, which is meaningless for Shift.
    /// </summary>
    public static bool ShiftHeld => (GetKeyState(VkShift) & 0x8000) != 0;

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

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
