namespace EmbyNian.Playback;

/// <summary>由播放器复制出来的独立 BGR32 画面；不持有交换链或本机指针。</summary>
public sealed record VideoFrame(int Width, int Height, int Stride, byte[] Pixels, VideoPresentation.Rect Picture)
{
    public static int BufferLength(int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride <= 0 || (long)width * 4 > stride) return 0;
        var length = (long)stride * height;
        return length <= 256 * 1024 * 1024 ? (int)length : 0;
    }

    public static VideoPresentation.Rect PictureRect(int width, int height,
        int left, int top, int right, int bottom)
    {
        if (width <= 0 || height <= 0) return default;
        left = Math.Clamp(left, 0, width);
        top = Math.Clamp(top, 0, height);
        right = Math.Clamp(right, 0, width);
        bottom = Math.Clamp(bottom, 0, height);
        return (long)left + right < width && (long)top + bottom < height
            ? new VideoPresentation.Rect(left, top, width - left - right, height - top - bottom)
            : new VideoPresentation.Rect(0, 0, width, height);
    }
}
