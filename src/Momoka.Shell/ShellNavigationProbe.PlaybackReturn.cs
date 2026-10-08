using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using Momoka.Emby;
using Momoka.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Momoka.Shell;

internal sealed partial class ShellNavigationProbe
{
    private async Task HomePlaybackReturnAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        fixture.Settings.Ui.ShowHomeBanner = true;
        var picture = await FixtureImageAsync();
        var items = Enumerable.Range(0, 6).Select(index => new EmbyItem
        {
            Id = "return-" + index,
            Name = "离线影片 " + (index + 1),
            Type = EmbyItemType.Movie,
            ImageTags = new() { ["Primary"] = "poster" },
            BackdropImageTags = ["backdrop"]
        }).ToArray();
        var library = new EmbyItem
        {
            Id = "library",
            Name = "离线媒体库",
            Type = EmbyItemType.CollectionFolder,
            CollectionType = "movies",
            ImageTags = new() { ["Primary"] = "library" }
        };
        TaskCompletionSource<HttpResponseMessage>? pending = null;
        TaskCompletionSource<bool>? entered = null;
        fixture.Transport.Reply = (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/Images/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(picture) });
            if (path.EndsWith("/Views", StringComparison.Ordinal))
                return Task.FromResult(Json(new { Items = new[] { library } }));
            if (path.EndsWith("/Items/Resume", StringComparison.Ordinal) && pending is { } reply)
            {
                entered!.TrySetResult(true);
                return reply.Task;
            }
            if (path.EndsWith("/Items/Latest", StringComparison.Ordinal)) return Task.FromResult(Json(items));
            if (path.EndsWith("/Items/Resume", StringComparison.Ordinal) || path.EndsWith("/Shows/NextUp", StringComparison.Ordinal))
                return Task.FromResult(Json(new { Items = items }));
            return Task.FromResult(FakeTransport.Standard(request));
        };
        fixture.Session.TakeRestoredViews();
        var shell = new ShellPage();
        shell.Attach(fixture.Services);
        _root.Children.Add(shell);
        try
        {
            await (Task)Call(shell, "EnterShellAsync")!;
            await UntilAsync(() => shell.ActiveContentReady);
            var frame = Get<Frame>(shell, "ContentFrame");
            var home = (HomePage)frame.Content;
            var repeater = Get<ItemsRepeater>(home, "ShelfRepeater");
            foreach (var width in new[] { 1420, 1000 })
            {
                ResizeInspect(width, 980);
                await LayoutAsync(shell);
                await Task.Delay(800);
                Require(home.ViewModel.Shelves.Count >= 3, "主页夹具未实现足够货架");

                // 正常进场的正对照，随后在同一拍切出、返回，必须收掉仍在运行的动画。
                HomeMotion.Enter(home, repeater, home.ViewModel.Shelves.Count);
                if (HomeMotion.AnimationsEnabled)
                    Require(ShelfTargets(repeater).Any(target => target.Opacity < 1), "正常入场正对照没有开始");
                shell.ShowPlayer(true);
                shell.ShowPlayer(false);
                Require(ReferenceEquals(home, frame.Content), "播放返回重建了主页");
                AssertShelvesSettled(repeater);
                await SaveReturnFrameAsync(shell, $"home-return-{width}-shown.png");

                pending = Pending<HttpResponseMessage>();
                entered = Pending<bool>();
                shell.RefreshActive();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Task.Delay(220);
                AssertShelvesSettled(repeater);
                if (width == 1000)
                {
                    // 请求仍在途中时跨过真实布局边界，证明补位检查并非一直读取一个没有翻档的页面。
                    Require(home.ViewModel.LibraryOnBanner, "补位夹具未进入媒体库上移档");
                    ResizeInspect(width, 300);
                    await UntilAsync(() => !home.ViewModel.LibraryOnBanner);
                    Require(Get<HomeFoldMotion?>(home, "_fold") is null && Get<HomeFoldMotion?>(home, "_foldLate") is null,
                        "返回后切换布局仍播放媒体库位移动画");
                    ResizeInspect(width, 980);
                    await UntilAsync(() => home.ViewModel.LibraryOnBanner);
                }
                items[0].Name = "刷新已完成 · " + width;
                pending.SetResult(Json(new { Items = items }));
                pending = null;
                await UntilAsync(() => !home.ViewModel.Busy);
                Require(home.ViewModel.Shelves.SelectMany(shelf => shelf.Cards).Any(card => card.Item.Name == items[0].Name),
                    "没有实际收到停止后的数据刷新");

                // 跨过 440ms 动画与最多 325ms 分排延迟，捕获迟到的 ElementPrepared/Loaded 回调。
                for (var sample = 0; sample < 50; sample++)
                {
                    shell.UpdateLayout();
                    AssertShelvesSettled(repeater);
                    Require(Get<HomeFoldMotion?>(home, "_fold") is null && Get<HomeFoldMotion?>(home, "_foldLate") is null,
                        "返回后的媒体库重新播放位移动画");
                    Require(ReferenceEquals(home, frame.Content), "刷新期间重建了主页");
                    await Task.Delay(16);
                }
                await SaveReturnFrameAsync(shell, $"home-return-{width}-refreshed.png");
            }
        }
        finally
        {
            pending?.TrySetResult(Json(new { Items = items }));
            Call(shell, "ReleaseContent");
            shell.SignInRoot.Detach();
            _root.Children.Remove(shell);
            ResizeInspect(1420, 980);
        }
    }

    private static IEnumerable<FrameworkElement> ShelfTargets(ItemsRepeater repeater)
    {
        for (var index = 0; index < (repeater.ItemsSourceView?.Count ?? 0); index++)
            if (repeater.TryGetElement(index) is FrameworkElement row && HomeMotion.TargetOf(row) is { } target)
                yield return target;
    }

    private static void AssertShelvesSettled(ItemsRepeater repeater)
    {
        var targets = ShelfTargets(repeater).ToArray();
        Require(targets.Length > 0, "没有可检查的已实现货架");
        foreach (var target in targets)
            Require(target.Opacity == 1 && (target.RenderTransform is not TranslateTransform drift || drift.Y == 0),
                "返回后的货架重新淡入或上浮");
    }

    private async Task SaveReturnFrameAsync(FrameworkElement element, string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
        stream.Seek(0);
        using var output = File.Create(Path.Combine(_options.Paths.LogDirectory, name));
        await stream.AsStreamForRead().CopyToAsync(output);
    }
}
