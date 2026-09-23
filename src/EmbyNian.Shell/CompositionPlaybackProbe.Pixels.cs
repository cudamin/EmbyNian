using System.ComponentModel;
using System.Runtime.InteropServices;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Windowing;

namespace EmbyNian.Shell;

internal static partial class CompositionPlaybackProbe
{
    // GetPixel 每问一点都会等待屏幕，连续读点既跨帧又堵住 UI 提交。
    // 一次 BitBlt 取得同一帧，后续颜色判断只读内存。
    private static ScreenPixels CapturePixels(HostWindow window)
    {
        if (!Native.GetClientRect(window.Handle, out var client) || client.Width <= 0 || client.Height <= 0)
            throw new InvalidOperationException("像素探针无法读取客户区");
        var origin = new NativePoint();
        if (!Native.ClientToScreen(window.Handle, ref origin))
            throw new InvalidOperationException("像素探针无法定位客户区");
        foreach (var (x, y) in new[] { (0.05, 0.2), (0.95, 0.2), (0.05, 0.8), (0.95, 0.8) })
        {
            var point = new NativePoint
            {
                X = origin.X + (int)(client.Width * x),
                Y = origin.Y + (int)(client.Height * y)
            };
            if (Native.GetAncestor(Native.WindowFromPoint(point), 2) != window.Handle)
                throw new InvalidOperationException("像素探针窗口被遮挡或移到屏外，不能把其他窗口的像素当作视频");
        }

        var screen = Native.GetDC(IntPtr.Zero);
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            if (screen == IntPtr.Zero) throw new Win32Exception();
            memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) throw new Win32Exception();
            var info = new ProbeBitmapInfo
            {
                Size = (uint)Marshal.SizeOf<ProbeBitmapInfo>(),
                Width = client.Width,
                Height = -client.Height,
                Planes = 1,
                BitCount = 32
            };
            bitmap = CreateDIBSection(memory, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new Win32Exception();
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception();
            if (!BitBlt(memory, 0, 0, client.Width, client.Height, screen, origin.X, origin.Y, 0x00CC0020))
                throw new Win32Exception();
            if (!GdiFlush()) throw new Win32Exception();
            var bytes = new byte[checked(client.Width * client.Height * 4)];
            Marshal.Copy(bits, bytes, 0, bytes.Length);
            return new ScreenPixels(client.Width, client.Height, bytes);
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private sealed record ScreenPixels(int Width, int Height, byte[] Bytes)
    {
        internal char At(double x, double y)
        {
            var column = (int)Math.Round((Width - 1) * Math.Clamp(x, 0, 1));
            var row = (int)Math.Round((Height - 1) * Math.Clamp(y, 0, 1));
            var index = (row * Width + column) * 4;
            return ColorLetter((uint)(Bytes[index + 2] | Bytes[index + 1] << 8 | Bytes[index] << 16));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeBitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial IntPtr CreateCompatibleDC(IntPtr device);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial IntPtr CreateDIBSection(IntPtr device, ref ProbeBitmapInfo info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial IntPtr SelectObject(IntPtr device, IntPtr item);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GdiFlush();
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr item);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr device);
}
