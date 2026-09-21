using System.Runtime.InteropServices;
using EmbyNian.Playback;

namespace EmbyNian.Mpv;

internal static class LibMpvFrame
{
    internal static VideoFrame? Capture(IntPtr context)
    {
        var args = new[] { Marshal.StringToCoTaskMemUTF8("screenshot-raw"),
            Marshal.StringToCoTaskMemUTF8("window"), IntPtr.Zero };
        var result = Marshal.AllocHGlobal(Marshal.SizeOf<LibMpvNative.MpvNode>());
        Marshal.StructureToPtr(new LibMpvNative.MpvNode(), result, false);
        try
        {
            if (LibMpvNative.mpv_command_ret(context, args, result) < 0) return null;
            var node = Marshal.PtrToStructure<LibMpvNative.MpvNode>(result);
            if (node.Format != LibMpvNative.FormatNodeMap) return null;
            var list = Marshal.PtrToStructure<LibMpvNative.MpvNodeList>(node.Union);
            var width = 0;
            var height = 0;
            var stride = 0;
            string? format = null;
            var bytes = default(ByteArray);
            for (var index = 0; index < list.Num; index++)
            {
                var key = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(list.Keys, index * IntPtr.Size));
                var value = Marshal.PtrToStructure<LibMpvNative.MpvNode>(
                    list.Values + index * Marshal.SizeOf<LibMpvNative.MpvNode>());
                if (value.Format == LibMpvNative.FormatInt64)
                {
                    var number = value.Union.ToInt64();
                    if (number is <= 0 or > int.MaxValue) continue;
                    switch (key)
                    {
                        case "w": width = (int)number; break;
                        case "h": height = (int)number; break;
                        case "stride": stride = (int)number; break;
                    }
                }
                else if (key == "format" && value.Format == LibMpvNative.FormatString)
                    format = Marshal.PtrToStringUTF8(value.Union);
                else if (key == "data" && value.Format == 9 && value.Union != IntPtr.Zero)
                    bytes = Marshal.PtrToStructure<ByteArray>(value.Union);
            }

            var length = VideoFrame.BufferLength(width, height, stride);
            if (format is not ("bgr0" or "bgra") || length == 0
                || bytes.Data == IntPtr.Zero || bytes.Size < (nuint)length) return null;
            var pixels = new byte[length];
            Marshal.Copy(bytes.Data, pixels, 0, length);
            var picture = LibMpvNodes.Read(context, "osd-dimensions", root =>
            {
                var map = LibMpvNodes.Map(root);
                if (LibMpvNodes.Int(map, "w") != width || LibMpvNodes.Int(map, "h") != height)
                    return new VideoPresentation.Rect(0, 0, width, height);
                return VideoFrame.PictureRect(width, height,
                    LibMpvNodes.Int(map, "ml") ?? 0, LibMpvNodes.Int(map, "mt") ?? 0,
                    LibMpvNodes.Int(map, "mr") ?? 0, LibMpvNodes.Int(map, "mb") ?? 0);
            }, new VideoPresentation.Rect(0, 0, width, height));
            return new VideoFrame(width, height, stride, pixels, picture);
        }
        finally
        {
            LibMpvNative.mpv_free_node_contents(result);
            Marshal.FreeHGlobal(result);
            foreach (var arg in args)
                if (arg != IntPtr.Zero) Marshal.FreeCoTaskMem(arg);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByteArray
    {
        public IntPtr Data;
        public nuint Size;
    }
}
