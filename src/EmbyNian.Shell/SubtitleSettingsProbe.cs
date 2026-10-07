using System.Text;
using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.MoviePilot;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Shell.Platform;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Shell.Views;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EmbyNian.Shell;

internal static class SubtitleSettingsProbe
{
    internal static int ExitCode { get; private set; } = 1;

    internal static async Task RunAsync(StartupOptions options)
    {
        HostWindow? window = null;
        var report = new StringBuilder();
        using var moviePilot = new MoviePilotClient(new OfflineHttp());
        using var stopping = new CancellationTokenSource(TimeSpan.FromMinutes(options.InspectSubtitles ? 10 : 2));
        try
        {
            options.Paths.EnsureCreated();
            var settings = new AppSettings();
            settings.Servers.Clear();
            settings.Ui.CompactMode = false;
            var service = new SettingsService(new SettingsStore(options.Paths, ProbeSecrets.Instance), settings);
            service.Save();
            ThemeHost.Apply(options.Theme ?? settings.Ui.Theme);
            var page = new SettingsPage();
            var pushes = 0;
            page.ViewModel.Attach(service, new ShaderStaging(settings), new FontLibrary(), options.Paths,
                new OfflineLauncher(), new AudioDeviceCatalogue(() => null),
                () => { pushes++; return Task.CompletedTask; }, _ => Task.CompletedTask,
                new MoviePilotProbe(moviePilot), new MoviePilotCredentials(ProbeSecrets.Instance));
            page.ViewModel.SelectedCategory = "字幕";
            await page.ViewModel.ReloadAsync();
            window = new HostWindow { Content = page, FreeSizing = true };
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += () => closed.TrySetResult();
            window.Show(false, options.Screen == 0 ? ScreenPlacement.NotThePrimary : options.Screen,
                new WindowBounds(80, 80, 1160, 940));
            await LayoutAsync(page);
            Record("隔离", true, "新数据目录、无账号、无登录、无 PlaybackService；HTTP 委托禁止联网");
            var colorInput = new HtmlColorPicker { Margin = new Thickness(32) };
            window.Content = colorInput;
            await LayoutAsync(colorInput);
            RecordResult("颜色精确输入", await HtmlColorPicker.ProbeExactInputAsync(colorInput));
            window.Content = page;
            await LayoutAsync(page);
            RecordResult("字幕预览模型", SettingSubtitlePreviewRow.Probe());
            RecordResult("字幕删除确认", await SubtitleDialog.ProbeDeleteConfirmationAsync());
            RecordResult("字幕操作收尾", await SubtitleDialog.ProbeOperationsAsync(page.XamlRoot));

            var check = new EgPicker { DisplayMemberPath = "Text", Width = 400 };
            var unavailable = new TrackRow("无法加载", null, false) { IsAvailable = false };
            check.ItemsSource = new[] { new TrackRow("自动", null, false), unavailable };
            window.Content = check;
            await LayoutAsync(check);
            check.IsDropDownOpen = true;
            await LayoutAsync(check);
            var disabled = check.ContainerFromItem(unavailable) is ComboBoxItem { IsEnabled: false };
            Record("不可用字幕禁选", disabled, "真实 ComboBoxItem 容器状态");
            check.IsDropDownOpen = false;
            window.Content = page;
            await LayoutAsync(page);

            var rows = page.ViewModel.Sections.Single(section => section.Category == "字幕").Rows;
            var fontSize = rows.OfType<SettingSliderRow>().Single(row => row.Label == "字号");
            fontSize.Value = 55;
            Record("外观实时委托", pushes > 0 && settings.Playback.SubtitleFontSize == 55, "改变真实设置行后写入临时文件并调用实时外观委托");
            using (var locked = new FileStream(options.Paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fontSize.Value = 60;
                Record("未保存状态", page.ViewModel.HasUnsavedChanges && page.ViewModel.RetrySaveCommand.CanExecute(null), "临时文件独占锁模拟保存失败");
                await CaptureAsync(page, options.Paths, "settings-unsaved.png");
            }
            page.ViewModel.RetrySaveCommand.Execute(null);
            Record("保存重试", !page.ViewModel.HasUnsavedChanges && !service.HasUnsavedChanges, "释放临时文件锁后保存成功");
            fontSize.Value = 50;
            var restore = await page.ViewModel.ProbeSubtitleRestoreAsync();
            Record("恢复同步字幕", restore, "恢复默认与导入备份均推送外观，仍是临时设置");
            Record("字幕应用失败可见", await page.ViewModel.ProbeSubtitlePushFailureAsync(), "假应用委托失败显示提示，不伪装全部成功");
            rows = page.ViewModel.Sections.Single(section => section.Category == "字幕").Rows;
            var preview = rows.OfType<SettingSubtitlePreviewRow>().Single();
            var sizeRow = rows.OfType<SettingSliderRow>().Single(row => row.Label == "字号");
            var scaleRow = rows.OfType<SettingSliderRow>().Single(row => row.Label == "字幕缩放（%）");
            var opacityRow = rows.OfType<SettingSliderRow>().Single(row => row.Label == "底板不透明度（%）");
            var backRow = rows.OfType<SettingColorRow>().Single(row => row.Label == "底板颜色");
            var borderRow = rows.OfType<SettingColorRow>().Single(row => row.Label == "描边颜色");
            var styleRow = rows.OfType<SettingChoiceRow>().Single(row => row.Label == "字幕底板");
            sizeRow.Value = 50;
            backRow.Color = "#4A334D";
            borderRow.Color = "#152C42";
            opacityRow.Value = 40;
            for (var index = 0; index < styleRow.Choices.Count; index++)
            {
                styleRow.Selected = styleRow.Choices[index];
                preview.Refresh();
                await LayoutAsync(page);
                await CaptureAsync(page, options.Paths, $"settings-style-{index}.png");
            }
            scaleRow.Value = 150;
            await CaptureAsync(page, options.Paths, "settings-scale-150.png");
            scaleRow.Value = 100;
            var picker = new HtmlColorPicker { Color = "#123456", Margin = new Thickness(32) };
            window.Content = picker;
            await LayoutAsync(picker);
            await CaptureAsync(picker, options.Paths, "color-picker.png");
            window.Content = page;
            await LayoutAsync(page);

            var stream = new MediaStream { Index = 1, Type = "Subtitle", Codec = "srt", Title = "简体中文字幕", IsExternal = true };
            var source = new MediaSource { Id = "fixture", Name = "离线示例.mkv", MediaStreams = [stream] };
            var dialog = new SubtitleDialog(new EmbyItem { Id = "fixture", Name = "字幕删除确认（离线夹具）" }, source,
                _ => Task.FromResult(new List<RemoteSubtitleInfo>()), _ => Task.CompletedTask, _ => Task.CompletedTask)
            { XamlRoot = page.XamlRoot };
            var shown = dialog.ShowAsync();
            await LayoutAsync(dialog);
            dialog.ShowDeleteConfirmationForProbe();
            await LayoutAsync(dialog);
            await CaptureAsync((UIElement)dialog.Content, options.Paths, "delete-confirmation.png");
            if (options.InspectSubtitles)
            {
                File.WriteAllText(Path.Combine(options.Paths.LogDirectory, "subtitle-probe-ready.txt"), Environment.ProcessId.ToString());
                await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromMinutes(8), stopping.Token));
            }
            dialog.Hide();
            await shown;
            ExitCode = 0;
        }
        catch (Exception error)
        {
            report.AppendLine($"[失败] {error}");
        }
        finally
        {
            File.WriteAllText(Path.Combine(options.Paths.LogDirectory, "subtitle-probe.txt"), report.ToString(), Encoding.UTF8);
            window?.Dispose();
            Application.Current.Exit();
        }

        void Record(string name, bool ok, string detail)
        {
            report.AppendLine($"[{(ok ? "通过" : "失败")}] {name} — {detail}");
            File.WriteAllText(Path.Combine(options.Paths.LogDirectory, "subtitle-probe.txt"), report.ToString(), Encoding.UTF8);
            if (!ok) throw new InvalidOperationException(name);
        }
        void RecordResult(string name, (bool Ok, string Detail) result) => Record(name, result.Ok, result.Detail);
    }

    private static async Task LayoutAsync(FrameworkElement element)
    {
        for (var i = 0; i < 20; i++)
        {
            element.UpdateLayout();
            if (element.XamlRoot is not null && element.ActualWidth > 0 && element.ActualHeight > 0)
            {
                await Task.Delay(200);
                return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("字幕探针界面未完成布局");
    }

    private static async Task CaptureAsync(UIElement element, AppPaths paths, string name)
    {
        await Task.Delay(250);
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var buffer = await bitmap.GetPixelsAsync();
        var pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        using var result = new DataReader(output.GetInputStreamAt(0));
        await result.LoadAsync((uint)output.Size);
        var bytes = new byte[output.Size];
        result.ReadBytes(bytes);
        File.WriteAllBytes(Path.Combine(paths.LogDirectory, name), bytes);
    }

    private sealed class OfflineHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("离线字幕探针禁止 HTTP"));
    }

    private sealed class OfflineLauncher : ISystemLauncher
    {
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) { }
    }

    private sealed class ProbeSecrets : ISecretProtector
    {
        public static ProbeSecrets Instance { get; } = new();
        public string Protect(string value) => throw new InvalidOperationException("离线字幕探针没有凭据");
        public string Unprotect(string value) => throw new InvalidOperationException("离线字幕探针没有凭据");
    }
}
