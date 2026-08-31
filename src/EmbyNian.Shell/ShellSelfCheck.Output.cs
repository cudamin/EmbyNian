using System.Text;
using EmbyNian.Diagnostics;
using EmbyNian.Shell.Views;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EmbyNian.Shell;

/// <summary>
/// 报告怎么落地：崩在半路时那一份、<c>--dump-ui</c> 要的那棵树、那张 PNG，以及写文件本身。
/// <para>
/// <c>WinExe</c> 没有控制台，报告文件是唯一的出口，所以这一段坏了等于什么都没验 —— 写文件那句从来不许抛。
/// 拆成几个文件的缘由见主文件 <see cref="ShellSelfCheck"/> 的类注释。
/// </para>
/// </summary>
internal static partial class ShellSelfCheck
{
    /// <summary>
    /// Turns a UI-thread crash into the report the walk never got to write. Called from
    /// <see cref="App"/>'s XAML exception handler, which fires while the process is on its way down, so
    /// this does the least it can: name the page it died on and the exception, and stop the timer so a
    /// tick cannot arrive on a half-torn-down shell.
    /// </summary>
    public static void ReportCrash(StartupOptions options, Exception? error, string message)
    {
        _timer?.Stop();
        ExitCode = 1;

        var where = _stage switch
        {
            0 => "主页",
            1 => "媒体库页面",
            2 => "服务器页面",
            3 => "诊断页面",

            // The settings stage walks its own cards, so which one it died on is worth saying: a template
            // that throws does so the first time its card is opened, and that is the card named here.
            _ => _settings.Count > 0 ? $"设置页面（「{_settings[^1].Category}」之后）" : "设置页面"
        };
        Write(options, "selfcheck-shell.txt", $"""
            {AppIdentity.TitleWithVersion} — WinUI 3 外壳自检
            时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}
            数据目录：{options.Paths.Root}

            [失败] 界面线程未处理异常 — 走到「{where}」时崩溃
            {error?.GetType().FullName ?? "未知异常"}：{message}

            {error?.StackTrace ?? "（没有调用栈）"}

            结果：1 项失败
            """);
    }

    /// <summary>
    /// Writes a PNG of the shell beside the report, under <c>--dump-ui</c>.
    /// <para>
    /// <see cref="RenderTargetBitmap"/> draws the visual tree through the compositor into a bitmap of our
    /// own, which is a different question from the one <see cref="SampleScreen"/> asks: this says what the
    /// app drew, the pixel probe says what the display showed. When they disagree, the fault is between
    /// the two — the island, the compositor, the driver — and not in any of the pages.
    /// </para>
    /// <para>
    /// Never throws: an unwritten picture is a worse report, not a failed run, and the exit code has
    /// already been decided by then.
    /// </para>
    /// </summary>
    private static async Task ShootAsync(ShellPage shell, StartupOptions options)
    {
        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(shell);

            var buffer = await bitmap.GetPixelsAsync();
            var pixels = new byte[buffer.Length];
            DataReader.FromBuffer(buffer).ReadBytes(pixels);

            var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

            // RenderTargetBitmap hands back premultiplied BGRA at the island's own pixel size, so the
            // 96 dpi written here is nominal: the bitmap is already scaled and there is nothing to undo.
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth,
                (uint)bitmap.PixelHeight,
                96,
                96,
                pixels);

            await encoder.FlushAsync();

            var bytes = new byte[stream.Size];
            var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);

            options.Paths.EnsureCreated();
            var path = Path.Combine(options.Paths.LogDirectory, "selfcheck-shell.png");
            File.WriteAllBytes(path, bytes);

            Log.Info(Category, $"界面截图已写出 {bitmap.PixelWidth}x{bitmap.PixelHeight} → {path}");
        }
        catch (Exception error)
        {
            Log.Warn(Category, "界面截图失败", error);
        }
    }

    private static void Write(StartupOptions options, string name, string content)
    {
        try
        {
            options.Paths.EnsureCreated();
            File.WriteAllText(Path.Combine(options.Paths.LogDirectory, name), content, Encoding.UTF8);
        }
        catch (Exception error)
        {
            Log.Error(Category, $"写入自检报告 {name} 失败", error);
        }
    }
}
