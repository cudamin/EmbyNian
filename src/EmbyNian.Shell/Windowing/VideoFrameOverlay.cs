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

    /// <summary>要盖的那一帧；纯色模式（<see cref="_solid"/>）时为空。</summary>
    private readonly VideoFrame? _frame;

    /// <summary>
    /// 这一帧按<b>铺满</b>摆（cover，超出的方向裁掉）还是按 contain 摆（留边）。默认 contain。
    /// <para>
    /// 两条起播整屏的路要 cover：那块层显示的是加载遮罩垫底的背景图，而遮罩里那张图是 <c>UniformToFill</c>
    /// —— 覆盖层留边、遮罩裁切，撤层那一刻图上会跳一下（2026-09-25，用户令「不要黑屏，主页和背景图无缝切换」）。
    /// 视频画面那一趟（窗口切换抓的帧）仍旧 contain，与留帧的默认摆法同一套。
    /// </para>
    /// </summary>
    private readonly bool _cover;

    /// <summary>
    /// 无帧的纯色模式：整块按 <see cref="_fill"/> 铺满。起播自动全屏那一趟还在加载、抓不到画面帧，
    /// 用加载遮罩同色的纯色层盖住窗口长到全屏那一拍（见 <c>PlayerPage.ChangeWindowAsync</c>）。
    /// </summary>
    private readonly bool _solid;

    /// <summary>纯色模式的填充色，分层窗口的 BGRA（不透明，见 <see cref="Show"/> 的 ULW_OPAQUE）。</summary>
    private readonly uint _fill;

    private VideoFrameOverlay(IntPtr owner)
    {
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

    /// <summary>盖一帧真画面（视频换尺寸／进退全屏那一拍的承接，按 contain 摆）。</summary>
    internal VideoFrameOverlay(IntPtr owner, VideoFrame frame, bool cover = false) : this(owner)
    {
        _frame = frame;
        _cover = cover;
    }

    /// <summary>盖一块纯色（没有画面可抓时，如起播加载中长到全屏）。<paramref name="fill"/> 是不透明 BGRA。</summary>
    internal VideoFrameOverlay(IntPtr owner, uint fill) : this(owner)
    {
        _solid = true;
        _fill = fill;
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
            if (_solid)
            {
                // 无帧：整块铺不透明纯色（ULW_OPAQUE 下按不透明显示，与加载遮罩同色）。
                new Span<uint>((void*)bits, bounds.Width * bounds.Height).Fill(_fill);
            }
            else if (_frame is { } frame)
            {
                var picture = frame.Picture;
                var source = new BitmapInfo
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfo>(),
                    Width = frame.Stride / 4,
                    Height = -frame.Height,
                    Planes = 1,
                    BitCount = 32
                };
                SetStretchBltMode(memory, 4);
                SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);
                fixed (byte* pixels = frame.Pixels)
                {
                    // 铺满那一档：目标恒是整块，改的是「从源里取哪一块」；留边那一档：源取可见画面，目标居中。
                    var from = _cover
                        ? VideoPresentation.FillSource((int)picture.Width, (int)picture.Height,
                            bounds.Width, bounds.Height, (int)picture.Left, (int)picture.Top)
                        : picture;
                    var scale = _cover
                        ? 1d
                        : VideoPresentation.FitScale(picture.Width, picture.Height, bounds.Width, bounds.Height, false);
                    var width = _cover ? bounds.Width : (int)Math.Round(picture.Width * scale);
                    var height = _cover ? bounds.Height : (int)Math.Round(picture.Height * scale);
                    if (StretchDIBits(memory, (bounds.Width - width) / 2, (bounds.Height - height) / 2,
                            width, height, (int)Math.Round(from.Left), (int)Math.Round(from.Top),
                            (int)Math.Round(from.Width), (int)Math.Round(from.Height),
                            (IntPtr)pixels, ref source, 0, 0x00CC0020) == 0)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
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
