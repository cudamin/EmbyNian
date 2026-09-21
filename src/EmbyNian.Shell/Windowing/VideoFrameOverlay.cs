using System.ComponentModel;
using System.Runtime.InteropServices;
using EmbyNian.Playback;
using EmbyNian.Shell.Interop;

namespace EmbyNian.Shell.Windowing;

/// <summary>只在视频换尺寸时短暂承接画面的无输入层；像素与窗口位置一次交给 DWM，不依赖岛布局。</summary>
internal sealed partial class VideoFrameOverlay : IDisposable
{
    private const string ClassName = "EmbyNianVideoFrame";
    private const int Transparent = 0x20;
    private static readonly WindowProcedure Procedure = Dispatch;
    private static bool _registered;
    private IntPtr _window;
    private readonly VideoFrame _frame;

    internal VideoFrameOverlay(IntPtr owner, VideoFrame frame)
    {
        _frame = frame;
        if (!_registered)
        {
            var name = Marshal.StringToHGlobalUni(ClassName);
            try
            {
                var windowClass = new WindowClass
                {
                    Size = (uint)Marshal.SizeOf<WindowClass>(),
                    Instance = Native.GetModuleHandle(null),
                    ClassName = name,
                    Procedure = Marshal.GetFunctionPointerForDelegate(Procedure)
                };
                if (Native.RegisterClassEx(ref windowClass) == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                _registered = true;
            }
            finally { Marshal.FreeHGlobal(name); }
        }

        _window = Native.CreateWindowEx(
            Native.WsExLayered | Native.WsExToolWindow | Native.WsExNoActivate | Transparent,
            ClassName, "", Native.WsPopup, 0, 0, 1, 1, owner, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        if (_window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal unsafe void Show(NativeRect bounds, bool topmost)
    {
        if (_window == IntPtr.Zero || bounds.Width <= 0 || bounds.Height <= 0) return;
        var screen = Native.GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo
        {
            Size = (uint)Marshal.SizeOf<BitmapInfo>(),
            Width = bounds.Width,
            Height = -bounds.Height,
            Planes = 1,
            BitCount = 32
        };
        var bitmap = CreateDIBSection(memory, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (screen == IntPtr.Zero || memory == IntPtr.Zero || bitmap == IntPtr.Zero)
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screen);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var old = SelectObject(memory, bitmap);
        try
        {
            new Span<byte>((void*)bits, checked(bounds.Width * bounds.Height * 4)).Clear();
            var picture = _frame.Picture;
            var scale = VideoPresentation.FitScale(picture.Width, picture.Height, bounds.Width, bounds.Height, false);
            var width = (int)Math.Round(picture.Width * scale);
            var height = (int)Math.Round(picture.Height * scale);
            var source = new BitmapInfo
            {
                Size = (uint)Marshal.SizeOf<BitmapInfo>(),
                Width = _frame.Stride / 4,
                Height = -_frame.Height,
                Planes = 1,
                BitCount = 32
            };
            SetStretchBltMode(memory, 4);
            SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);
            fixed (byte* pixels = _frame.Pixels)
            {
                if (StretchDIBits(memory, (bounds.Width - width) / 2, (bounds.Height - height) / 2,
                        width, height, (int)picture.Left, (int)picture.Top, (int)picture.Width, (int)picture.Height,
                        (IntPtr)pixels, ref source, 0, 0x00CC0020) == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var position = new NativePoint { X = bounds.Left, Y = bounds.Top };
            var size = new NativePoint { X = bounds.Width, Y = bounds.Height };
            var origin = new NativePoint();
            if (!UpdateLayeredWindow(_window, screen, ref position, ref size, memory, ref origin,
                    0, IntPtr.Zero, 4))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.SetWindowPos(_window, topmost ? Native.HwndTopMost : Native.HwndTop,
                0, 0, 0, 0, Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);
            Native.ShowWindow(_window, Native.SwShowNoActivate);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    internal static NativeRect ClientRect(IntPtr window)
    {
        Native.GetClientRect(window, out var client);
        var origin = new NativePoint();
        Native.ClientToScreen(window, ref origin);
        return new NativeRect
        {
            Left = origin.X,
            Top = origin.Y,
            Right = origin.X + client.Width,
            Bottom = origin.Y + client.Height
        };
    }

    internal static NativeRect FullscreenRect(IntPtr window)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        return Native.GetMonitorInfo(Native.MonitorFromWindow(window, Native.MonitorDefaultToNearest), ref info)
            ? info.Monitor : ClientRect(window);
    }

    /// <summary>
    /// 最大化的目标矩形：所在显示器的工作区（任务栏除外）。进全屏／最大化之前先按它摆覆盖层 ——
    /// <b>窗口还没变大，屏上已经是变完的样子</b>，合成器那一拍「新几何 + 旧内容」就没有空隙可露。
    /// </summary>
    internal static NativeRect WorkArea(IntPtr window)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        return Native.GetMonitorInfo(Native.MonitorFromWindow(window, Native.MonitorDefaultToNearest), ref info)
            ? info.Work : ClientRect(window);
    }

    internal static void Flush() => DwmFlush();

    public void Dispose()
    {
        if (_window == IntPtr.Zero) return;
        Native.DestroyWindow(_window);
        _window = IntPtr.Zero;
    }

    private static IntPtr Dispatch(IntPtr window, uint message, IntPtr wParam, IntPtr lParam) => message switch
    {
        0x0084 => new IntPtr(-1),
        0x0021 => new IntPtr(3),
        _ => Native.DefWindowProc(window, message, wParam, lParam)
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr device);
    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr device, IntPtr item);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr item);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr device);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial IntPtr CreateDIBSection(IntPtr device, ref BitmapInfo info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
    [LibraryImport("gdi32.dll")]
    private static partial int SetStretchBltMode(IntPtr device, int mode);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetBrushOrgEx(IntPtr device, int x, int y, IntPtr previous);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int StretchDIBits(IntPtr device, int x, int y, int width, int height,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight, IntPtr bits,
        ref BitmapInfo info, uint usage, uint operation);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindow(IntPtr window, IntPtr destination, ref NativePoint position,
        ref NativePoint size, IntPtr source, ref NativePoint origin, uint key, IntPtr blend, uint flags);
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmFlush();
}
