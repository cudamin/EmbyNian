using EmbyNian.Diagnostics;
using EmbyNian.Playback;
using EmbyNian.Theming;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// 把遮罩背景图那份字节（JPEG/PNG）解成一段可直接交给 DWM 覆盖层的 BGRA 像素。
/// <para>
/// 起播自动全屏那一趟没有视频帧可抓，挡窗口长大的那块层从前铺的是遮罩同色的纯色 —— 那正是用户看到的
/// 「先全屏黑一片」；现在铺这张图（2026-09-25 用户令「不要黑屏，主页和背景图无缝切换」），烘法与遮罩
/// 一致见 <see cref="CoverArtwork"/>。
/// </para>
/// <para>
/// 解码失败只记一行、交回空：覆盖层退回纯色，与这条修复之前的行为一样 —— 那张图是添头，不是承重墙。
/// 调用方（<c>PlayerViewModel.LoadCoverBackdropAsync</c>）本来就在后台等这张图，多解一遍的几十毫秒
/// 落在那段等待里，不进播放路径。
/// </para>
/// </summary>
internal static class CoverBackdropImage
{
    /// <summary>解码 + 按遮罩烘色。失败或尺寸说不通时交回 <see langword="null"/>。</summary>
    internal static async Task<VideoFrame?> DecodeAsync(byte[] bytes, ThemeColor backdrop)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                // 摘掉再让 writer 收摊：不摘的话它的 Dispose 会把下面还要读的这条流一起关掉。
                writer.DetachStream();
            }

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            var width = (int)decoder.PixelWidth;
            var height = (int)decoder.PixelHeight;
            var pixels = data.DetachPixelData();
            var stride = width * 4;

            if (width <= 0 || height <= 0 || stride < 4 || (long)stride * height > pixels.Length) return null;

            CoverArtwork.Bake(pixels, width, height, stride, backdrop);
            return new VideoFrame(width, height, stride, pixels, new VideoPresentation.Rect(0, 0, width, height));
        }
        catch (Exception error)
        {
            Log.Info("播放", $"遮罩背景图没解出覆盖层那份（{error.Message}），起播整屏退回纯色");
            return null;
        }
    }
}
