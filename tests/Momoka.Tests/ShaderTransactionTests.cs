using System.Reflection;
using Momoka.Configuration;
using Momoka.Emby;
using Momoka.Infrastructure;
using Momoka.Mpv;
using Momoka.Playback;
using static Momoka.Tests.TestHarness;

namespace Momoka.Tests;

internal static class ShaderTransactionTests
{
    public static void Register()
    {
        Test("着色器事务：缺失目标文件时不得关闭旧处理或报告成功", () => MissingFile().GetAwaiter().GetResult());
        Test("着色器事务：回滚失败时明确标记状态未知，重新应用可恢复", () => FailedRollback().GetAwaiter().GetResult());
        Test("着色器事务：中途拒绝恢复旧链与全部前置参数", () => Rejected(false).GetAwaiter().GetResult());
        Test("着色器事务：中途异常同样恢复旧链", () => Rejected(true).GetAwaiter().GetResult());
        Test("着色器事务：缺少默认基线不猜值也不开始写入", () => MissingDefault().GetAwaiter().GetResult());
        Test("着色器事务：缺少旧值不能安全回滚就不开始写入", () => MissingSnapshot().GetAwaiter().GetResult());
        Test("着色器事务：新链在全部前置参数就位后才挂入", () => ShaderLast().GetAwaiter().GetResult());
        Test("着色器事务：同片同时切链必须串行，最终保持完整一套", () => Serial().GetAwaiter().GetResult());
        Test("着色器事务：读取默认值时换片，停止剩余读写", () => ChangedFilm().GetAwaiter().GetResult());
        Test("着色器事务：失败回滚不能写进同句柄的新代次", () => RollbackGeneration().GetAwaiter().GetResult());
    }

    private static async Task MissingFile()
    {
        using var f = new Fixture();
        var group = ShaderGroupCatalog.For(GpuTier.Low)[1] with
        {
            Shaders = [ShaderLibrary.RavuZoom with { File = "missing-stage7.glsl" }]
        };
        Assert.False(await f.Service.SetShaderGroupAsync(group));
        Assert.Equal(0, f.Handle.Commands.Count);
        Assert.True(f.Service.ShaderStateKnown);
    }

    private static async Task FailedRollback()
    {
        using var f = new Fixture();
        f.Handle.RejectAt = 3;
        f.Handle.RejectFrom = 4;
        Assert.False(await f.Service.SetShaderGroupAsync(ShaderGroupCatalog.For(GpuTier.Low)[1]));
        Assert.False(f.Service.ShaderStateKnown);
        f.Handle.RejectAt = 0;
        f.Handle.RejectFrom = 0;
        Assert.True(await f.Service.SetShaderGroupAsync(null));
        Assert.True(f.Service.ShaderStateKnown);
    }

    private static async Task Rejected(bool throws)
    {
        using var f = new Fixture();
        var before = new Dictionary<string, string>(f.Handle.Values);
        f.Handle.RejectAt = 3;
        f.Handle.ThrowOnReject = throws;
        Assert.False(await f.Service.SetShaderGroupAsync(ShaderGroupCatalog.For(GpuTier.Low)[1]));
        foreach (var (name, value) in before)
            Assert.Equal(value, f.Handle.Values[name], $"失败后 {name} 没回到切换前");
    }

    private static async Task MissingDefault()
    {
        using var f = new Fixture();
        f.Handle.Missing = "option-info/dscale/default-value";
        Assert.False(await f.Service.SetShaderGroupAsync(null), "缺少恢复基线不得冒充完整关闭");
        Assert.Equal(0, f.Handle.Commands.Count);
    }

    private static async Task MissingSnapshot()
    {
        using var f = new Fixture();
        f.Handle.Missing = "scale";
        Assert.False(await f.Service.SetShaderGroupAsync(null));
        Assert.Equal(0, f.Handle.Commands.Count);
    }

    private static async Task ShaderLast()
    {
        using var f = new Fixture();
        Assert.True(await f.Service.SetShaderGroupAsync(ShaderGroupCatalog.For(GpuTier.Low)[1]));
        Assert.Equal("glsl-shaders", f.Handle.Commands[^1][1]);
        Assert.Equal("ewa_lanczossharp", f.Handle.Values["scale"]);
        Assert.Contains("ravu", f.Handle.Values["glsl-shaders"]);
    }

    private static async Task Serial()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handle.BeforeCommand = async count =>
        {
            if (count != 1) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(3));
        };
        var first = f.Service.SetShaderGroupAsync(ShaderGroupCatalog.For(GpuTier.Low)[1]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var reads = f.Handle.Reads.Count;
        var second = f.Service.SetShaderGroupAsync(null);
        var premature = second.IsCompleted || f.Handle.Reads.Count != reads;
        release.TrySetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(premature, "第二次切链越过第一批写入开始读写");
        Assert.Equal("", f.Handle.Values["glsl-shaders"]);
        Assert.Equal("lanczos", f.Handle.Values["scale"]);
        Assert.Equal("hermite", f.Handle.Values["dscale"]);
    }

    private static async Task ChangedFilm()
    {
        using var f = new Fixture();
        var readsAtSwap = 0;
        f.Handle.BeforeRead = name =>
        {
            if (name == "profile-list")
            {
                Field(f.Service, "_playbackGeneration", 2L);
                readsAtSwap = f.Handle.Reads.Count;
            }
            return Task.CompletedTask;
        };
        Assert.False(await f.Service.SetShaderGroupAsync(null));
        Assert.Equal(readsAtSwap, f.Handle.Reads.Count, "过期后不应继续问旧上下文");
        Assert.Equal(0, f.Handle.Commands.Count);
    }

    private static async Task RollbackGeneration()
    {
        using var f = new Fixture();
        f.Handle.RejectAt = 2;
        f.Handle.BeforeCommand = count =>
        {
            if (count == 2) Field(f.Service, "_playbackGeneration", 2L);
            return Task.CompletedTask;
        };
        Assert.False(await f.Service.SetShaderGroupAsync(ShaderGroupCatalog.For(GpuTier.Low)[1]));
        Assert.Equal(2, f.Handle.Commands.Count, "回滚不能落入同句柄下一场播放");
    }

    private static void Field(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class Fixture : IDisposable
    {
        internal readonly TransactionHandle Handle = new();
        internal readonly PlaybackService Service;
        private readonly EmbySession _session;
        internal Fixture()
        {
            var settings = new AppSettings();
            var store = new SettingsStore(new AppPaths(Path.Combine(Path.GetTempPath(), "stage7-shader-" + Guid.NewGuid().ToString("N"))), PassthroughSecretProtector.Instance);
            _session = new EmbySession(settings, store, new CredentialVault(PassthroughSecretProtector.Instance), DeviceIdentity.Create("stage7", "1"), new OfflineHttp());
            Service = new PlaybackService(_session, settings, () => throw new InvalidOperationException("No backend may be started"), new PlaybackPlanner(settings, new ShaderGroupResolver(settings.Shaders)));
            Field(Service, "_current", Handle);
            Field(Service, "_playbackGeneration", 1L);
        }
        public void Dispose() => _session.Dispose();
    }

    private sealed class OfflineHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("No HTTP may be sent from shader transaction tests");
    }

    private sealed class TransactionHandle : IPlaybackHandle, IPlayerControl
    {
        internal readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
        {
            ["glsl-shaders"] = "C:/stage7/old.glsl",
            ["scale"] = "bilinear",
            ["cscale"] = "bilinear",
            ["dscale"] = "bilinear",
            ["linear-downscaling"] = "no",
            ["correct-downscaling"] = "no",
            ["sigmoid-upscaling"] = "no",
            ["deband"] = "yes"
        };
        internal readonly List<string[]> Commands = [];
        internal readonly List<string> Reads = [];
        internal int RejectAt;
        internal int RejectFrom;
        internal bool ThrowOnReject;
        internal string? Missing;
        internal Func<int, Task>? BeforeCommand;
        internal Func<string, Task>? BeforeRead;
        public bool HasControlChannel => true;
        public bool IsPaused => true;
        public PlayerStatus Status => new();
        public event Action<bool>? PauseChanged { add { } remove { } }
        public event Action<PlayerStatus>? StatusChanged { add { } remove { } }
        public event Action<IReadOnlyList<MpvTrack>>? TracksChanged { add { } remove { } }
        public async Task<bool> CommandAsync(IReadOnlyList<string> arguments, CancellationToken token)
        {
            Commands.Add([.. arguments]);
            var count = Commands.Count;
            if (BeforeCommand is { } wait) await wait(count);
            if (count == RejectAt || (RejectFrom > 0 && count >= RejectFrom))
            {
                if (ThrowOnReject) throw new InvalidOperationException("Injected shader command exception");
                return false;
            }
            Values[arguments[1]] = arguments[2];
            return true;
        }
        public async Task<string?> GetTextAsync(string name, CancellationToken token)
        {
            Reads.Add(name);
            if (BeforeRead is { } wait) await wait(name);
            if (name == Missing) return null;
            if (name == "profile-list") return "[]";
            if (name.StartsWith("option-info/", StringComparison.Ordinal))
                return ShaderGroupCatalog.NeutralOptions.First(option => name == $"option-info/{option.Key}/default-value").Value;
            return Values.GetValueOrDefault(name);
        }
        public Task<IReadOnlyList<string>?> GetStringListAsync(string name, CancellationToken token)
        {
            Reads.Add(name);
            return Task.FromResult<IReadOnlyList<string>?>(name == Missing ? null : [Values[name]]);
        }
        public Task SetPropertyAsync(string name, object? value, CancellationToken token) => throw new NotSupportedException();
        public Task<PlaybackExit> WaitForExitAsync(CancellationToken token) => throw new NotSupportedException();
        public Task StopAsync() => Task.CompletedTask;
        public Task<double?> GetPositionAsync(CancellationToken token) => Task.FromResult<double?>(null);
        public Task ShowMessageAsync(string text) => Task.CompletedTask;
        public Task<IReadOnlyList<MpvTrack>> GetTracksAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<MpvTrack>>([]);
        public Task<double?> GetNumberAsync(string name, CancellationToken token) => Task.FromResult<double?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
