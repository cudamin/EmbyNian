using System.Globalization;
using System.Reflection;
using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

internal static class AudioOutputTests
{
    public static void Register()
    {
        Test("音频设备：下一次枚举不沿用进程内旧名单", () =>
        {
            IReadOnlyList<AudioDevice> current = [new("wasapi/removed", "已拔下的耳机")];
            var catalogue = new AudioDeviceCatalogue(() => null, () => current);
            Assert.Equal(1, catalogue.LoadAsync().GetAwaiter().GetResult().Count);
            current = [];
            var devices = catalogue.LoadAsync().GetAwaiter().GetResult();
            Assert.Equal("", AudioDeviceCatalogue.UsableDevice("wasapi/removed", devices));
            Assert.Equal(0, catalogue.Known.Count, "本次取空不能让设置页继续展示旧在线名单");
        });

        Test("音频设备：并发只共享在途枚举，下一轮重新读取", () =>
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var reads = 0;
            var catalogue = new AudioDeviceCatalogue(() => null, () =>
            {
                Interlocked.Increment(ref reads);
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)), "枚举夹具没有释放");
                return [new("auto", "默认"), new("wasapi/A", "耳机")];
            });
            var first = catalogue.LoadAsync();
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var second = catalogue.LoadAsync();
                Assert.True(ReferenceEquals(first, second));
                release.Set();
                Assert.Equal(1, first.GetAwaiter().GetResult().Count);
                Assert.Equal(1, reads);
                Assert.Equal(1, catalogue.LoadAsync().GetAwaiter().GetResult().Count);
                Assert.Equal(2, reads);
            }
            finally { release.Set(); }
        });

        Test("音频设备：枚举异常后不留旧名单且下次能恢复", () =>
        {
            var fail = false;
            var catalogue = new AudioDeviceCatalogue(() => null, () => fail
                ? throw new IOException("offline fixture") : [new("wasapi/A", "耳机")]);
            Assert.Equal(1, catalogue.LoadAsync().GetAwaiter().GetResult().Count);
            fail = true;
            Assert.Equal(0, catalogue.LoadAsync().GetAwaiter().GetResult().Count);
            Assert.Equal(0, catalogue.Known.Count);
            fail = false;
            Assert.Equal(1, catalogue.LoadAsync().GetAwaiter().GetResult().Count);
        });

        Test("音量记忆：原生连调始终保留最新值，重复回声不推迟结算", () =>
        {
            var memory = new VolumeMemory();
            Assert.Equal(41, memory.Observe(Status(41), true, 0));
            Assert.Equal(63, memory.Observe(Status(63), true, 200));
            var due = memory.DueAt;
            var revision = memory.Revision;
            Assert.Equal(63, memory.Observe(Status(63), true, 1000));
            Assert.Equal(due, memory.DueAt);
            Assert.Equal(revision, memory.Revision);
            Assert.Equal(63, memory.Pending);
            memory.Saved(revision);
            Assert.Null(memory.Pending);
        });

        Test("音量记忆：无控制、未装载、未观测和非有限值均不记", () =>
        {
            var memory = new VolumeMemory();
            Assert.Null(memory.Observe(Status(80), false, 0));
            Assert.Null(memory.Observe(Status(80) with { Loaded = false }, true, 0));
            Assert.Null(memory.Observe(new PlayerStatus { Loaded = true }, true, 0));
            foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                Assert.Null(memory.Observe(Status(value), true, 0));
            Assert.Null(memory.Pending);
        });

        Test("音量记忆：UI旧回声被挡，但确认后的原生调整不被挡", () =>
        {
            var memory = new VolumeMemory();
            memory.Observe(Status(37), true, 0);
            memory.Saved(memory.Revision);
            var request = memory.Request(60, 10);
            Assert.Null(memory.Observe(Status(37), true, 20));
            Assert.True(memory.Accept(request, 60, 30));
            Assert.Null(memory.Observe(Status(37), true, 40));
            Assert.Equal(60, memory.Observe(Status(60), true, 50));
            Assert.Equal(70, memory.Observe(Status(70), true, 60));
            Assert.Equal(70, memory.Pending);
        });

        Test("音量记忆：拒绝与换片作废请求，旧结算不清新值", () =>
        {
            var memory = new VolumeMemory();
            var rejected = memory.Request(80, 0);
            Assert.True(memory.Reject(rejected));
            Assert.Null(memory.Pending);
            var first = memory.Request(50, 10);
            var second = memory.Request(60, 20);
            Assert.False(memory.Accept(first, 50, 30));
            Assert.True(memory.Accept(second, 60, 40));
            var old = memory.Revision;
            memory.Observe(Status(60), true, 45);
            memory.Observe(Status(70), true, 50);
            memory.Saved(old);
            Assert.Equal(70, memory.Pending);
            var pending = memory.Request(90, 60);
            memory.ResetObservation();
            Assert.False(memory.Accept(pending, 90, 70));
            Assert.Equal(70, memory.Pending, "观察重置不能丢掉尚未成功保存的音量");
        });

        Test("音量记忆：迟到命令确认不能覆盖更新的原生读数", () =>
        {
            var memory = new VolumeMemory();
            var request = memory.Request(60, 0);
            Assert.Equal(60, memory.Observe(Status(60), true, 10));
            Assert.Equal(70, memory.Observe(Status(70), true, 20));
            Assert.False(memory.Accept(request, 60, 30));
            Assert.Equal(70, memory.Pending);
        });

        Test("音量状态：首次真实100也通知，零和上限都是有效读数", () =>
        {
            var unknown = new PlayerStatus();
            Assert.False(unknown.VolumeKnown);
            Assert.True((unknown with { VolumeKnown = true }).DiffersFrom(unknown));
            Assert.Equal(0, VolumeMemory.Level(0));
            Assert.Equal(AudioSettings.MaxVolume, VolumeMemory.Level(130));
            Assert.Equal(0, VolumeMemory.Level(-1));
            Assert.Equal(AudioSettings.MaxVolume, VolumeMemory.Level(999));
            Assert.Null(VolumeMemory.Level(double.NaN));
        });

        Test("音量记忆：静音期真实读数暂存，保护后或结算时生效", () =>
        {
            var memory = new VolumeMemory();
            var request = memory.Request(60, 0);
            Assert.True(memory.Accept(request, 60, 5));
            Assert.Null(memory.Observe(Status(70), true, 100), "保护窗内的真实70仍不能顶掉请求回声");
            Assert.Equal((70, 60), (memory.Queued, memory.Pending));
            Assert.Equal(70, memory.Observe(Status(70), true, VolumeMemory.SettleMilliseconds + 100));
            Assert.Null(memory.Queued);
            Assert.Equal(70, memory.Pending);

            var next = new VolumeMemory();
            var other = next.Request(60, 0);
            Assert.True(next.Accept(other, 60, 5));
            Assert.Null(next.Observe(Status(70), true, 100));
            next.FlushQueued(VolumeMemory.SettleMilliseconds + 200);
            Assert.Null(next.Queued);
            Assert.Equal(70, next.Pending, "结算前必须把已收到的真实读数转成待保存值");
        });

        Test("设置清洗：空文档的空设备不抛且起播走系统默认", () =>
        {
            using var paths = new TempPaths();
            var settings = SettingsMigration.FromJson("""{"SchemaVersion":23,"Audio":{"Device":null}}""", PassthroughSecretProtector.Instance);
            Assert.Equal("", settings.Audio.Device, "null 设备必须归一成「跟随系统默认」，不能带进播放规划");
        });

        Test("上报：通道已开但内核音量未知时宁缺毋假", () =>
        {
            using var f = new Fixture();
            f.Handle.Control = true;
            f.Handle.Current = new PlayerStatus { Loaded = true };
            var unknown = f.Service.BuildReportForTest("timeupdate");
            Assert.Null(unknown.VolumeLevel, "默认100不能冒充读数发给服务器");
            f.Handle.Current = new PlayerStatus { Loaded = true, Volume = 37, VolumeKnown = true };
            Assert.Equal(37, f.Service.BuildReportForTest("timeupdate").VolumeLevel);
            f.Handle.Control = false;
            Assert.Null(f.Service.BuildReportForTest("timeupdate").VolumeLevel);
        });

        Test("音频延迟：相对调整用内核基准、串行累加、清零不改全局", () =>
            DelayCommandsAsync().GetAwaiter().GetResult());
        Test("音频延迟：拒绝、无通道与不可读结果不冒充成功", () =>
            DelayFailuresAsync().GetAwaiter().GetResult());
        Test("音频延迟：读回中换片丢弃旧结果及排队动作", () =>
            DelayReplacementAsync().GetAwaiter().GetResult());
        Test("音量命令：确认后成功，无通道或拒绝时失败", () =>
            VolumeCommandsAsync().GetAwaiter().GetResult());
    }

    private static PlayerStatus Status(double volume) => new() { Loaded = true, Volume = volume, VolumeKnown = true };

    private static async Task DelayCommandsAsync()
    {
        using var f = new Fixture();
        f.Settings.Audio.DelayMilliseconds = 500;
        f.Handle.Delay = 0.9;
        Assert.Equal(1.0, await f.Service.ChangeAudioDelayAsync(0.1, true));
        var values = await Task.WhenAll(
            f.Service.ChangeAudioDelayAsync(0.1, true), f.Service.ChangeAudioDelayAsync(0.1, true));
        Assert.True(Math.Abs(values[1]!.Value - 1.2) < 0.00001);
        Assert.Equal(0.0, await f.Service.ChangeAudioDelayAsync(0, false));
        Assert.Equal(500, f.Settings.Audio.DelayMilliseconds);
        Assert.Equal("add", f.Handle.Commands[0][0]);
        Assert.Equal("set", f.Handle.Commands[^1][0]);
    }

    private static async Task DelayFailuresAsync()
    {
        using var f = new Fixture();
        f.Handle.Reject = true;
        Assert.Null(await f.Service.ChangeAudioDelayAsync(0.1, true, true));
        Assert.Equal(1, f.Handle.Commands.Count, "拒绝后不能发送成功提示");
        f.Handle.Reject = false;
        f.Handle.Read = _ => Task.FromResult<double?>(null);
        Assert.Null(await f.Service.ChangeAudioDelayAsync(0.1, true, true));
        Assert.Equal(2, f.Handle.Commands.Count);
        f.Handle.Control = false;
        Assert.Null(await f.Service.ChangeAudioDelayAsync(0.1, true));
        Assert.Equal(2, f.Handle.Commands.Count);
        Assert.Null(await f.Service.ChangeAudioDelayAsync(double.NaN, true));
    }

    private static async Task DelayReplacementAsync()
    {
        using var f = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handle.Read = _ => { entered.TrySetResult(); return release.Task; };
        var first = f.Service.ChangeAudioDelayAsync(0.1, true, true);
        await entered.Task.WaitAsync(timeout.Token);
        var queued = f.Service.ChangeAudioDelayAsync(0.1, true, true);
        var next = new Handle();
        Set(f.Service, "_current", next);
        Set(f.Service, "_playbackGeneration", 2L);
        release.TrySetResult(0.1);
        Assert.Null(await first.WaitAsync(timeout.Token));
        Assert.Null(await queued.WaitAsync(timeout.Token));
        Assert.Equal(0, next.Commands.Count);
        Assert.Equal(1, f.Handle.Commands.Count);
    }

    private static async Task VolumeCommandsAsync()
    {
        using var f = new Fixture();
        Assert.True(await f.Service.SetVolumeAsync(130));
        Assert.Equal("130", f.Handle.Commands[0][2]);
        f.Handle.Reject = true;
        Assert.False(await f.Service.SetVolumeAsync(80));
        f.Handle.Control = false;
        Assert.False(await f.Service.SetVolumeAsync(50));
        Assert.False(await f.Service.SetVolumeAsync(double.NaN));
        Assert.Equal(2, f.Handle.Commands.Count);
    }

    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class TempPaths : IDisposable
    {
        internal AppPaths Value { get; } = new(Path.Combine(Path.GetTempPath(), "audio-settings-" + Guid.NewGuid().ToString("N")));
        public void Dispose() { try { Directory.Delete(Value.Root, recursive: true); } catch (IOException) { } }
    }

    private sealed class Fixture : IDisposable
    {
        internal AppSettings Settings { get; } = new();
        internal Handle Handle { get; } = new();
        internal PlaybackService Service { get; }
        private readonly EmbySession _session;
        public Fixture()
        {
            var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "audio-fixture-" + Guid.NewGuid().ToString("N")));
            _session = new EmbySession(Settings, new SettingsStore(paths, PassthroughSecretProtector.Instance),
                new CredentialVault(PassthroughSecretProtector.Instance), DeviceIdentity.Create("offline", "1"), new OfflineHttp());
            Service = new PlaybackService(_session, Settings, () => new Backend(),
                new PlaybackPlanner(Settings, new ShaderGroupResolver(Settings.Shaders)));
            Set(Service, "_current", Handle);
        }
        public void Dispose() => _session.Dispose();
    }

    private sealed class Backend : IPlaybackBackend
    {
        public string DisplayName => "offline";
        public string? Validate() => null;
        public Task<IPlaybackHandle> StartAsync(PlaybackRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No playback in audio command fixture");
    }

    private sealed class OfflineHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No HTTP in audio command fixture");
    }

    private sealed class Handle : IPlaybackHandle, IPlayerControl
    {
        internal readonly List<string[]> Commands = [];
        internal bool Control = true;
        internal bool Reject;
        internal double Delay;
        internal Func<string, Task<double?>>? Read;
        internal PlayerStatus Current { get; set; } = new();
        public bool HasControlChannel => Control;
        public bool IsPaused => false;
        public PlayerStatus Status => Current;
        public event Action<bool>? PauseChanged { add { } remove { } }
        public event Action<PlayerStatus>? StatusChanged { add { } remove { } }
        public event Action<IReadOnlyList<MpvTrack>>? TracksChanged { add { } remove { } }
        public Task<double?> GetPositionAsync(CancellationToken cancellationToken) => Task.FromResult<double?>(null);
        public Task<PlaybackExit> WaitForExitAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PlaybackExit(PlaybackEndReason.Stopped, null, 0, null));
        public Task StopAsync() => Task.CompletedTask;
        public Task ShowMessageAsync(string text) => Task.CompletedTask;
        public Task SetPropertyAsync(string name, object? value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MpvTrack>> GetTracksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MpvTrack>>([]);
        public Task<double?> GetNumberAsync(string name, CancellationToken cancellationToken) =>
            Read?.Invoke(name) ?? Task.FromResult<double?>(Delay);
        public Task<string?> GetTextAsync(string name, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<bool> CommandAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Commands.Add([.. arguments]);
            if (Reject) return Task.FromResult(false);
            if (arguments.Count == 3 && arguments[1] == "audio-delay")
            {
                var value = double.Parse(arguments[2], CultureInfo.InvariantCulture);
                Delay = arguments[0] == "add" ? Delay + value : value;
            }
            return Task.FromResult(true);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
