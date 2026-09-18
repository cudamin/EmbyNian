using System.Runtime.InteropServices;
using EmbyNian.Diagnostics;

namespace EmbyNian.Shell.Interop;

/// <summary>
/// Shares only this process's cursor-publishing input queue during a hide. The island HWND belongs to
/// the UI thread, but WinUI publishes its cursor from a separate input thread.
/// </summary>
internal sealed partial class CursorVisibilityEvents : IDisposable
{
    private readonly WinEventProcedure _callback;
    private readonly IntPtr _window;
    private readonly uint _uiThread = Native.GetCurrentThreadId();
    private IntPtr _hook;
    private IntPtr _publisherHandle;
    private uint _publisherThread;
    private uint _attachedThread;
    private bool _hidden;
    private bool _handling;

    internal CursorVisibilityEvents(IntPtr window, Action changed)
    {
        _window = window;
        _callback = (_, kind, _, objectId, _, threadId, _) =>
        {
            if (_hook == IntPtr.Zero || _handling || objectId != -9 || kind is not (0x8002 or 0x8003 or 0x800C)) return;
            _handling = true;
            try
            {
                if (PointerOwned() && threadId != _uiThread) ObservePublisher(threadId);
                Refresh();
                if (_hidden && IsAttached) changed();
            }
            catch (Exception error) { Log.Warn("播放", "处理系统光标输入队列失败", error); }
            finally { _handling = false; }
        };
        // Restrict the observer to our process; another application's queue must never be attached.
        _hook = SetWinEventHook(0x8002, 0x800C, IntPtr.Zero, _callback, (uint)Environment.ProcessId, 0, 0);
        if (_hook == IntPtr.Zero) Log.Warn("播放", "系统光标变化监听不可用，保留定时恢复");
    }

    internal bool Installed => _hook != IntPtr.Zero;
    internal bool IsAttached => _attachedThread != 0;

    internal void SetHidden(bool hidden)
    {
        _hidden = hidden;
        Refresh();
    }

    internal void Refresh()
    {
        if (!_hidden || !PointerOwned())
        {
            Detach();
            return;
        }
        if (_attachedThread != 0 || _publisherThread == 0 || Native.MouseButtonDown()) return;
        if (_publisherHandle == IntPtr.Zero || WaitForSingleObject(_publisherHandle, 0) != 258) return;
        if (ChangeAttachment(_publisherThread, true))
        {
            _attachedThread = _publisherThread;
            Log.Debug("播放", $"已接入本进程光标发布队列：界面 {_uiThread}，输入 {_publisherThread}");
        }
    }

    private bool PointerOwned() => Native.GetCursorPos(out var at)
        && Native.GetAncestor(Native.WindowFromPoint(at), Native.GaRoot) == _window;

    private void ObservePublisher(uint thread)
    {
        if (thread == _publisherThread) return;
        var handle = OpenThread(0x00100800, false, thread);
        if (handle == IntPtr.Zero) return;
        if (GetProcessIdOfThread(handle) != (uint)Environment.ProcessId)
        {
            CloseHandle(handle);
            return;
        }
        Detach();
        if (_publisherHandle != IntPtr.Zero) CloseHandle(_publisherHandle);
        _publisherHandle = handle;
        _publisherThread = thread;
    }

    private void Detach()
    {
        if (_attachedThread == 0) return;
        if (!ChangeAttachment(_attachedThread, false))
            Log.Warn("播放", "解除光标输入队列失败");
        _attachedThread = 0;
    }

    private unsafe bool ChangeAttachment(uint target, bool attach)
    {
        byte* keys = stackalloc byte[256];
        var saved = GetKeyboardState((IntPtr)keys);
        if (attach && !saved) return false;
        try { return AttachThreadInput(_uiThread, target, attach); }
        finally
        {
            // AttachThreadInput resets the key-state table, including held shortcut modifiers.
            if (saved) SetKeyboardState((IntPtr)keys);
        }
    }

    public void Dispose()
    {
        _hidden = false;
        Detach();
        var hook = _hook;
        _hook = IntPtr.Zero;
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        if (_publisherHandle != IntPtr.Zero) CloseHandle(_publisherHandle);
        _publisherHandle = IntPtr.Zero;
        GC.KeepAlive(_callback);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProcedure(IntPtr hook, uint kind, IntPtr window,
        int objectId, int childId, uint threadId, uint time);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetWinEventHook(uint first, uint last, IntPtr module,
        WinEventProcedure callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(IntPtr hook);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint thread, uint target, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetKeyboardState(IntPtr keys);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetKeyboardState(IntPtr keys);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint thread);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetProcessIdOfThread(IntPtr thread);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
