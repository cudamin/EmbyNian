using EmbyNian.Emby;
using EmbyNian.Playback;
using EmbyNian.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EmbyNian.Shell;

internal sealed partial class ShellNavigationProbe
{
    private async Task InspectAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var image = await FixtureImageAsync();
        var item = Series();
        item.Name = "离线示例 · 媒体导航与封面管理";
        item.BackdropImageTags = ["a", "b"];
        item.ImageTags["Primary"] = "poster";
        item.ImageTags["Logo"] = "logo";
        fixture.Transport.Reply = (request, _) => request.RequestUri!.AbsolutePath.Contains("/Images/", StringComparison.Ordinal)
            ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(image) })
            : Task.FromResult(FakeTransport.Standard(request));
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12) };
        var frame = new Frame();
        Grid.SetRow(frame, 1);
        layout.Children.Add(controls);
        layout.Children.Add(frame);
        _root.Children.Add(layout);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = false;
        void Add(string id, string label, Func<Task> action)
        {
            var button = new Button { Content = label };
            AutomationProperties.SetAutomationId(button, id);
            button.Click += async (_, _) =>
            {
                if (busy) return;
                busy = true;
                try { await action(); }
                finally { busy = false; }
            };
            controls.Children.Add(button);
        }
        Add("InspectHome", "首页", () =>
        {
            frame.Navigate(typeof(HomePage), new HomeRequest(fixture.Services,
                [new EmbyItem { Id = "library", Name = "离线媒体库", Type = EmbyItemType.CollectionFolder, CollectionType = "movies", ImageTags = new() { ["Primary"] = "library" } }], null));
            return Task.CompletedTask;
        });
        Add("InspectLibrary", "媒体库", () =>
        {
            frame.Navigate(typeof(LibraryPage), LibraryRequest.Search(fixture.Services, "离线示例"));
            return Task.CompletedTask;
        });
        Add("InspectDetail", "剧集详情", () =>
        {
            frame.Navigate(typeof(DetailPage), DetailRequest.For(fixture.Services, item));
            return Task.CompletedTask;
        });
        Add("InspectFile", "单集文件", () =>
        {
            frame.Navigate(typeof(DetailPage), DetailRequest.For(fixture.Services, Episode("a")));
            return Task.CompletedTask;
        });
        Add("InspectNotifications", "通知", () =>
        {
            frame.Navigate(typeof(NotificationsPage), new NotificationsRequest(fixture.Services));
            return Task.CompletedTask;
        });
        Add("InspectCover", "封面管理", async () =>
        {
            var dialog = new CoverDialog(item,
                (_, _) => Task.FromResult(new RemoteImageResult { Images = [new RemoteImageInfo { Url = "https://fixture.invalid/art", ProviderName = "离线候选", Width = 320, Height = 200 }] }),
                (_, _, _, _, _) => Task.FromResult<byte[]?>(image),
                (_, type, _, _) => { item.ImageTags[type] = "changed"; return Task.CompletedTask; },
                (_, type, index) =>
                {
                    if (type == "Backdrop" && index is { } at) item.BackdropImageTags.RemoveAt(at);
                    else item.ImageTags.Remove(type);
                    return Task.CompletedTask;
                },
                (_, _, _) => throw new InvalidOperationException("检查面板不向服务器上传本机文件"),
                _ => Task.FromResult<EmbyItem?>(item), _ => Task.FromResult<byte[]?>(image))
            { XamlRoot = _root.XamlRoot };
            await dialog.ShowAsync();
        });
        Add("InspectNarrow", "窄窗口", () => { ResizeInspect(1000, 880); return Task.CompletedTask; });
        Add("InspectWide", "宽窗口", () => { ResizeInspect(1420, 900); return Task.CompletedTask; });
        Add("InspectDone", "结束检查", () => { finished.TrySetResult(); return Task.CompletedTask; });
        frame.Navigate(typeof(LibraryPage), LibraryRequest.Search(fixture.Services, "离线示例"));
        await LayoutAsync(layout);
        File.WriteAllText(Path.Combine(_options.Paths.LogDirectory, "shell-probe-ready.txt"), Environment.ProcessId.ToString());
        await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromMinutes(8)));
        if (frame.Content is IShellContent page) page.Release();
        _root.Children.Remove(layout);
    }

    private void ResizeInspect(int width, int height)
    {
        if (_window is null) return;
        var app = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_window.Handle));
        app.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    private static async Task<byte[]> FixtureImageAsync()
    {
        const int width = 320;
        const int height = 200;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var ridge = y > height / 2 + (int)(Math.Sin(x / 40.0) * 30);
                pixels[offset] = (byte)(ridge ? 65 : 100 + y / 3);
                pixels[offset + 1] = (byte)(ridge ? 90 : 125 + y / 3);
                pixels[offset + 2] = (byte)(ridge ? 45 : 45 + x / 3);
                pixels[offset + 3] = 255;
            }
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
