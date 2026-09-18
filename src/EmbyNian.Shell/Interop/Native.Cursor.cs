using System.Runtime.InteropServices;
using EmbyNian.Playback;

namespace EmbyNian.Shell.Interop;

internal static partial class Native
{
    /// <summary>Checks the bitmap, since WinUI publishes a copy of a custom HCURSOR.</summary>
    internal static bool CursorIsTransparent(IntPtr cursor)
    {
        if (cursor == IntPtr.Zero || !GetIconInfo(cursor, out var icon)) return false;
        var dc = IntPtr.Zero;
        try
        {
            if (icon.Color != IntPtr.Zero || icon.Mask == IntPtr.Zero
                || GetBitmapObject(icon.Mask, Marshal.SizeOf<CursorBitmap>(), out var bitmap) == 0
                || bitmap.Width is <= 0 or > 512 || bitmap.Height is <= 0 or > 1024
                || bitmap.Height % 2 != 0 || bitmap.BitsPixel != 1 || bitmap.Planes != 1) return false;

            var stride = (bitmap.Width + 31) / 32 * 4;
            var length = stride * bitmap.Height;
            Span<byte> bits = stackalloc byte[length];
            var info = new CursorBitmapInfo
            {
                Size = 40,
                Width = bitmap.Width,
                Height = -bitmap.Height,
                Planes = 1,
                BitsPixel = 1,
                ColorsUsed = 2,
                White = 0x00FFFFFF
            };
            dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return false;
            unsafe
            {
                fixed (byte* buffer = bits)
                {
                    if (GetCursorBitmapBits(dc, icon.Mask, 0, (uint)bitmap.Height,
                        (IntPtr)buffer, ref info, 0) != bitmap.Height) return false;
                }
            }
            return CursorMask.IsTransparent(bits, bitmap.Width, bitmap.Height / 2, stride);
        }
        finally
        {
            if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc);
            if (icon.Mask != IntPtr.Zero) DeleteCursorBitmap(icon.Mask);
            if (icon.Color != IntPtr.Zero) DeleteCursorBitmap(icon.Color);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorIconInfo
    {
        public int IsIcon;
        public uint HotX;
        public uint HotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorBitmapInfo
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitsPixel;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
        public uint Black;
        public uint White;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfo(IntPtr cursor, out CursorIconInfo info);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static partial int GetBitmapObject(IntPtr bitmap, int size, out CursorBitmap value);

    [LibraryImport("gdi32.dll", EntryPoint = "GetDIBits")]
    private static partial int GetCursorBitmapBits(IntPtr dc, IntPtr bitmap, uint start, uint lines,
        IntPtr bits, ref CursorBitmapInfo info, uint usage);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteCursorBitmap(IntPtr bitmap);
}
