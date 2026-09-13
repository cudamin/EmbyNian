using EmbyNian.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 从本机挑中的一张图：原样的字节，以及它的文件名（服务器要按后缀名认格式）。
/// </summary>
/// <param name="Bytes">图片本身，一个字节都没动过。</param>
/// <param name="FileName">它在本机叫什么。内容类型从它推，见 <see cref="ArtworkFile.ContentType"/>。</param>
public readonly record struct PickedArtwork(byte[] Bytes, string FileName);

/// <summary>
/// 「从本机传一张图」那个文件框，以及读回来的字节。
/// <para>
/// <b>为什么单独一个类：WinUI 3 的文件框必须拿到窗口句柄才弹得出来。</b>桌面版里
/// <see cref="FileOpenPicker"/> 是 WinRT 那一套（它在 UWP 里自己知道是谁在弹，桌面版没有那个上下文），所以
/// 官方写法是先 <c>InitializeWithWindow.Initialize(picker, hwnd)</c>。这个类把那一步、以及「拿不到句柄时怎么
/// 办」这两件事收在一处，免得每一个要用文件框的地方各写一遍。
/// </para>
/// <para>
/// 句柄是<b>从 XamlRoot 上问出来的</b>（<c>XamlRoot.ContentIslandEnvironment.AppWindowId</c> → 那个窗口），
/// 不是去应用里找「当前窗口」：这张表要挂在哪个窗口上是调用时定下的，而应用自己并不知道此刻是哪一扇窗在弹框。
/// </para>
/// <para>
/// <b>拿不到句柄时不抛异常，返回 null，并写一行日志。</b>抛出去的话，症状是「点了按钮，弹出一句看不懂的
/// 错」；返回 null 的症状是「什么都没发生」—— 两个都不好，所以调用方要自己判空并说一句人话。这一版里调用方
/// 是那张候选表（<c>CoverPickerDialog.OnUpload</c>），它拿 null 就当用户按了取消。
/// </para>
/// <para>
/// 大小上限 64 MB：一张封面/背景图撑死几兆，而没有上限的话，误选一个几十 G 的视频文件会让整个进程去读它。
/// </para>
/// </summary>
internal static class ArtworkFile
{
    private const string Category = "ui";

    private const long MaxBytes = 64L << 20;

    /// <summary>
    /// 弹一次文件框，读回选中那张图。用户取消、或者说这个环境拿不到窗口句柄，都返回 null。
    /// </summary>
    /// <param name="root">弹框那张表自己的 <see cref="XamlRoot"/> —— 窗口句柄从它身上问。</param>
    public static async Task<PickedArtwork?> PickAsync(XamlRoot root)
    {
        if (HandleOf(root) is not { } handle)
        {
            Log.Warn(Category, "拿不到这个 XamlRoot 所在的窗口，文件选择框弹不出来");
            return null;
        }

        var picker = new FileOpenPicker();

        // 只列图片。Emby 那几种图本来就是这几种格式，再列宽也帮不上什么忙。
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" })
            picker.FileTypeFilter.Add(extension);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return null;

        var properties = await file.GetBasicPropertiesAsync();
        if (properties.Size > MaxBytes)
        {
            Log.Warn(Category, $"这张图太大了：{properties.Size} 字节（上限 {MaxBytes}）");
            return null;
        }

        using var stream = await file.OpenReadAsync();
        var size = (int)stream.Size;
        if (size == 0) return null;

        // DataReader 而不是 stream.ReadAsync：WinRT 的 IRandomAccessStream 那一头要一个 IBuffer，
        // 而「把 IBuffer 塞回一个 byte[]」这一步没有现成的 API（Buffer.AsBuffer 是反过来的方向）。
        // DataReader 的 ReadBytes 就是为这件事准备的 —— 它按顺序把流灌进调用方的数组。
        var bytes = new byte[size];
        using (var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)size);
            reader.ReadBytes(bytes);
        }

        return new PickedArtwork(bytes, file.Name);
    }

    /// <summary>
    /// 这个 XamlRoot 所在窗口的句柄。问的是「这个内容岛属于哪个窗口」，和 <c>HostWindow</c> 里
    /// <c>IslandHandle</c> 那一句同一个走法（<c>Win32Interop.GetWindowFromWindowId</c>），只是反过来：
    /// 那边从自己造的内容岛问出子窗口，这边从一张表挂着的根问出宿主窗口。
    /// <para>
    /// 拿不到就是 null —— 那一位在窗口还没画出来、或者这个根已经摘掉的时候是空的。
    /// </para>
    /// </summary>
    private static IntPtr? HandleOf(XamlRoot root) =>
        root.ContentIslandEnvironment.AppWindowId is { Value: not 0 } id
            ? Win32Interop.GetWindowFromWindowId(id)
            : null;

    /// <summary>
    /// 这个文件名该报什么内容类型。按后缀名判 —— 上传那一趟要把 Content-Type 交给服务器，而服务器的图片
    /// 处理那一头是按它选解码器的（说成 <c>application/octet-stream</c> 多数时候也能成，但那是碰运气）。
    /// 认不出的后缀名落到 <c>image/jpeg</c>：文件框只列了那几种图片。
    /// </summary>
    public static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };
}
