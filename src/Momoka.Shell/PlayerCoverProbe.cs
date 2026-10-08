using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using Momoka.Configuration;
using Momoka.Emby;
using Momoka.Infrastructure;
using Momoka.Playback;
using Momoka.Services;
using Momoka.Shell.Platform;
using Momoka.Shell.ViewModels;
using Momoka.Shell.Views;
using Momoka.Shell.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Momoka.Shell;

/// <summary>借用本地动效探针的窗口，走正式起播准备入口；假传输仅交图片，媒体详情始终挂起且可取消。</summary>
internal static class PlayerCoverProbe
{
    internal static async Task RunAsync(AppPaths paths, ShellPage shell, HostWindow window,
        IUiDispatcher ui, Action<string> write, CancellationToken token)
    {
        var settings = new AppSettings();
        settings.Mpv.Pipeline = VideoPipelineKind.Integrated;
        settings.Playback.AutoFullscreenOnPlayback = true;
        var store = new SettingsStore(new AppPaths(Path.Combine(paths.Root, "cover-fixture")), PassthroughSecretProtector.Instance);
        var vault = new CredentialVault(PassthroughSecretProtector.Instance);
        using var transport = new CoverTransport();
        using var session = new EmbySession(settings, store, vault, DeviceIdentity.Create("cover-probe", "test"), transport);
        var server = new ServerProfile { Name = "离线背景图", Url = "https://cover.invalid" };
        var account = new AccountProfile
        {
            Username = "fixture",
            UserId = "fixture",
            ProtectedAccessToken = PassthroughSecretProtector.Instance.Protect("offline-fixture-token")
        };
        server.Accounts.Add(account);
        settings.Servers.Add(server);
        Check(await session.TryRestoreAsync(server, account, token), "假身份就绪");
        var images = new EmbyImageStore(session, Path.Combine(paths.Root, "cover-images"));
        var shaders = new ShaderGroupResolver(settings.Shaders);
        var backend = new RejectBackend();
        var playback = new PlaybackService(session, settings, () => backend,
            new PlaybackPlanner(settings, shaders, paths.ShaderCacheDirectory, paths.ScreenshotDirectory), allowPlayback: false);
        var vm = new PlayerViewModel(playback, new SettingsService(store, settings), session, images, shaders, ui);
        var page = shell.PlayerRoot;
        var original = page.ViewModel;
        var originallyMaximized = window.IsMaximized;
        var starts = new List<Task>();
        var pending = new List<TaskCompletionSource<byte[]?>>();
        var shownWithCover = false;
        var cover = (FrameworkElement)page.FindName("Cover");
        var ring = (ProgressRing)page.FindName("CoverRing");
        var artwork = (ImageBrush)((Grid)page.FindName("CoverArtwork")).Background;
        // 在页面订阅之前观察，确保窗口进场的任何同步合成发生前遮罩就已立好。
        vm.PlayerShown += () => shownWithCover = vm.CoverUp && cover.Visibility == Visibility.Visible
            && cover.Opacity == 1 && ring.IsActive;
        page.Detach();
        page.Attach(vm, shell, window);
        window.Activate();
        try
        {
            var red = await ArtworkAsync(210, 45, 55, patterned: true);
            var blue = await ArtworkAsync(35, 90, 210);
            transport.Images["first"] = Task.FromResult<byte[]?>(red);
            // 模拟冷态布局迟到；单看最终截图无法抓到中间被缩小的加载层。
            cover.MaxWidth = 640;
            cover.MaxHeight = 360;
            Start("first");
            await Until(() => transport.Details.ContainsKey("first"));
            Check(shownWithCover, "正式首次进场事件之前遮罩已可见、不透明且转圈已启动");
            await LoadTask();
            Check(vm.CoverBackdrop is not null && vm.CoverBackdropFrame is not null, "首次背景图及原生覆盖层像素一起就绪");
            await Task.Delay(300, token);
            await CompositionPlaybackProbe.SaveScreenAsync(window, Path.Combine(paths.LogDirectory, "cover-delayed-layout.png"));
            Check(page.StartupHandoverPending, "布局迟到超过旧撤层时限时仍保留整屏背景");
            cover.MaxWidth = double.PositiveInfinity;
            cover.MaxHeight = double.PositiveInfinity;
            await Until(() => window.Fullscreen && !page.StartupHandoverPending);
            Check(page.StartupCoverSettled, "布局恢复后按真实出帧交接而非超时撤层");
            await Task.Delay(240, token);
            await CompositionPlaybackProbe.SaveScreenAsync(window, Path.Combine(paths.LogDirectory, "cover-first-fullscreen.png"));

            // 同一进程后续三次重新进场：从还原窗、最大化窗进入，检查交接不依赖上次的热布局。
            for (var round = 2; round <= 4; round++)
            {
                await Stop();
                if (round == 3) window.ToggleMaximize();
                Start("first");
                await Until(() => window.Fullscreen && !page.StartupHandoverPending);
                var raster = page.XamlRoot!.RasterizationScale;
                var client = window.ClientSize;
                Check(page.StartupCoverSettled && Math.Abs(cover.ActualWidth * raster - client.Width) <= 1
                    && Math.Abs(cover.ActualHeight * raster - client.Height) <= 1,
                    $"第 {round} 次重新进场加载背景铺满客户区");
            }

            // 加载尚未交接就退出全屏、停止并立即重进，旧 await 不得清掉下一轮的覆盖层。
            await Stop();
            cover.MaxWidth = 640;
            Start("first");
            await Until(() => window.Fullscreen && page.StartupHandoverPending);
            page.SetFullscreen(false);
            Check(!page.StartupHandoverPending, "加载中退出全屏同步撤掉原生覆盖层");
            await page.WindowChange;
            await Stop();
            Start("first");
            await Until(() => window.Fullscreen && page.StartupHandoverPending);
            await Task.Delay(240, token);
            Check(page.StartupHandoverPending, "取消后重新进场仍保持新一轮覆盖层");
            await Stop();
            Check(!page.StartupHandoverPending, "加载中停止同步结束覆盖层交接");
            cover.MaxWidth = double.PositiveInfinity;
            Start("first");
            await Until(() => window.Fullscreen && !page.StartupHandoverPending);

            var slow = DelayImage("second");
            Start("second");
            Check(vm.CoverBackdrop is null && vm.CoverBackdropFrame is null, "新媒体请求发起即清除旧背景的两份图");
            var loading = LoadTask();
            Start("second");
            Check(ReferenceEquals(loading, LoadTask()), "同条目在途请求复用完整解码任务");
            Check(!transport.Details.ContainsKey("second"), "同条目在途背景未就绪时仍等待");
            await Until(() => transport.Details.ContainsKey("second"));
            Check(vm.CoverBackdrop is null && vm.CoverBackdropFrame is null && artwork.ImageSource is null,
                "背景等待超时后实际加载层不显示上一媒体的图");
            await CompositionPlaybackProbe.SaveScreenAsync(window, Path.Combine(paths.LogDirectory, "cover-second-waiting.png"));
            slow.SetResult(blue);
            await loading;
            Check(vm.CoverBackdrop is not null && vm.CoverBackdropFrame is not null
                && ReferenceEquals(artwork.ImageSource, vm.CoverBackdrop), "新背景返回后两份图及页面绑定一起就绪");
            await Task.Delay(160, token);
            await CompositionPlaybackProbe.SaveScreenAsync(window, Path.Combine(paths.LogDirectory, "cover-second-fullscreen.png"));

            var currentImage = vm.CoverBackdrop;
            var currentFrame = vm.CoverBackdropFrame;
            Start("second");
            Check(ReferenceEquals(currentImage, vm.CoverBackdrop) && ReferenceEquals(currentFrame, vm.CoverBackdropFrame),
                "同条目再次准备复用当前背景且不清空");

            var late = DelayImage("late");
            Start("late");
            var lateLoad = LoadTask();
            transport.Images["newest"] = Task.FromResult<byte[]?>(red);
            Start("newest");
            await LoadTask();
            currentImage = vm.CoverBackdrop;
            currentFrame = vm.CoverBackdropFrame;
            late.SetResult(blue);
            await lateLoad;
            Check(currentImage is not null && currentFrame is not null
                && ReferenceEquals(currentImage, vm.CoverBackdrop) && ReferenceEquals(currentFrame, vm.CoverBackdropFrame),
                "上一媒体迟到的下载不能覆盖当前两份背景");

            transport.Images["missing"] = Task.FromResult<byte[]?>(null);
            Start("missing");
            await LoadTask();
            Check(vm.CoverBackdrop is null && vm.CoverBackdropFrame is null, "404 无图保持空白背景");
            transport.Images["missing"] = Task.FromResult<byte[]?>(blue);
            Start("missing");
            await LoadTask();
            Check(vm.CoverBackdrop is not null && vm.CoverBackdropFrame is not null, "无图后同条目仍可重试成功");
            transport.Images["broken"] = Task.FromResult<byte[]?>([1, 2, 3]);
            Start("broken");
            await LoadTask();
            Check(vm.CoverBackdrop is null && vm.CoverBackdropFrame is null, "解码失败不沿用旧图");

            var stopped = DelayImage("stopped");
            Start("stopped");
            var stoppedLoad = LoadTask();
            await vm.StopAsync();
            await Task.WhenAll(starts);
            stopped.SetResult(red);
            await stoppedLoad;
            Check(vm.CoverBackdrop is null && vm.CoverBackdropFrame is null, "退出之后迟到背景不会复活");
            Check(backend.Starts == 0 && transport.Writes == 0, "准备期回归零媒体启动、零服务器写请求");
        }
        finally
        {
            cover.MaxWidth = double.PositiveInfinity;
            cover.MaxHeight = double.PositiveInfinity;
            foreach (var image in pending) image.TrySetResult(null);
            await vm.StopAsync();
            await Task.WhenAll(starts);
            await vm.ShutdownAsync();
            page.Detach();
            if (window.IsMaximized != originallyMaximized) window.ToggleMaximize();
            page.Attach(original, shell, window);
        }

        void Start(string id) => starts.Add(vm.PlayAsync(new EmbyItem
        { Id = id, Name = "离线加载页", Type = EmbyItemType.Movie, BackdropImageTags = [id] }));
        async Task Stop()
        {
            await vm.StopAsync();
            await Task.WhenAll(starts);
            await Until(() => !page.PlayerVisible);
        }
        Task LoadTask() => (Task)typeof(PlayerViewModel)
            .GetField("_coverBackdropLoad", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        TaskCompletionSource<byte[]?> DelayImage(string id)
        {
            var response = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(response);
            transport.Images[id] = response.Task;
            return response;
        }
        void Check(bool passed, string name)
        {
            if (!passed) throw new InvalidOperationException("加载页回归失败：" + name);
            write("通过：加载页 " + name);
        }
        async Task Until(Func<bool> condition)
        {
            var end = Environment.TickCount64 + 5000;
            while (!condition() && Environment.TickCount64 < end) await Task.Delay(20, token);
            if (!condition()) throw new TimeoutException("加载页状态等待超时");
        }
    }

    private static async Task<byte[]> ArtworkAsync(byte red, byte green, byte blue, bool patterned = false)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        const int width = 320;
        const int height = 240;
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var x = index / 4 % width;
            var y = index / 4 / width;
            var mark = patterned && (x % 40 < 3 || y % 40 < 3);
            pixels[index] = mark ? (byte)240 : blue;
            pixels[index + 1] = mark ? (byte)240 : green;
            pixels[index + 2] = mark ? (byte)240 : red;
            pixels[index + 3] = 255;
        }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var output = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(output);
        return output.ToArray();
    }

    private sealed class RejectBackend : IPlaybackBackend
    {
        public int Starts { get; private set; }
        public string DisplayName => "拒绝媒体启动的背景图探针";
        public string? Validate() => null;
        public Task<IPlaybackHandle> StartAsync(PlaybackRequest request, CancellationToken cancellationToken)
        {
            Starts++;
            throw new InvalidOperationException("背景图探针禁止启动媒体");
        }
    }

    private sealed class CoverTransport : HttpMessageHandler
    {
        internal ConcurrentDictionary<string, Task<byte[]?>> Images { get; } = new();
        internal ConcurrentDictionary<string, bool> Details { get; } = new();
        internal int Writes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get) Writes++;
            if (request.RequestUri!.Host != "cover.invalid" || request.Method != HttpMethod.Get)
                throw new InvalidOperationException("背景图探针拒绝真实目标与写请求");
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/Views", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"Items\":[],\"TotalRecordCount\":0}", Encoding.UTF8, "application/json") };
            if (path.EndsWith("/System/Info/Public", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"ServerName\":\"offline-cover\"}", Encoding.UTF8, "application/json") };
            var segments = path.Split('/');
            if (path.Contains("/Images/", StringComparison.Ordinal))
            {
                var bytes = await Images[segments[^3]];
                return bytes is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
            if (path.Contains("/Items/", StringComparison.Ordinal))
            {
                Details.TryAdd(segments[^1], true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            throw new InvalidOperationException("背景图探针出现未安排的请求");
        }
    }
}
