using System.Reflection;
using System.Runtime.InteropServices;
using Momoka.Diagnostics;
using Momoka.Configuration;
using Momoka.Mpv;
using Momoka.Playback;
using static Momoka.Tests.TestHarness;

namespace Momoka.Tests;

internal static class BackendLifecycleTests
{
    public static void Register()
    {
        Test("内置起播：预取消优先于DLL定位和原生初始化", () =>
        {
            var backend = new LibMpvBackend(new MpvSettings { ExecutablePath = "absent/mpv.exe" },
                new PlaybackSettings(), () => throw new AssertionException("不能取画布"));
            Assert.Throws<OperationCanceledException>(() => backend.StartAsync(Request(), new CancellationToken(true)).GetAwaiter().GetResult());
        });

        Test("内置换片：完整生产签名排除逐片截图标题和着色器", () =>
        {
            var first = Request() with { PlayerOptions = [new("screenshot-template", "first"), new("scale", "one")], ShaderOptionCount = 1 };
            var next = Request() with { PlayerOptions = [new("screenshot-template", "next"), new("scale", "two")], ShaderOptionCount = 1 };
            var settings = new MpvSettings { Pipeline = VideoPipelineKind.Standalone };
            Assert.True(InlineSwitch.SameSignature(
                LibMpvBackend.Signature(settings, new PlaybackSettings(), first, []),
                LibMpvBackend.Signature(settings, new PlaybackSettings(), next, [])));
            settings.Backend = MpvBackendKind.ExternalMpv;
            Assert.False(InlineSwitch.SameSignature(
                LibMpvBackend.Signature(new MpvSettings { Pipeline = VideoPipelineKind.Standalone }, new PlaybackSettings(), first, []),
                LibMpvBackend.Signature(settings, new PlaybackSettings(), next, [])));
        });

        Test("内置换片：依赖的start-file和结束事件必须在订阅表", () =>
        {
            Assert.False(LibMpvBackend.UnusedEvents.Contains(LibMpvNative.EventStartFile));
            Assert.False(LibMpvBackend.UnusedEvents.Contains(LibMpvNative.EventEndFile));
            Assert.False(LibMpvBackend.UnusedEvents.Contains(LibMpvNative.EventPlaybackRestart));
        });

        Test("内置换片：旧命令执行票不通过新影片的原生闸门", () =>
        {
            var gate = new LibMpvLifetimeGate();
            var old = gate.Generation;
            var called = 0;
            gate.NextGeneration();
            Assert.False(gate.RunFor(old, () => { called++; return true; }));
            Assert.True(gate.RunFor(gate.Generation, () => { called++; return true; }));
            Assert.Equal(1, called);
            gate.Destroy(() => { });
            Assert.False(gate.RunFor(gate.Generation, () => { called++; return true; }));
            Assert.Equal(1, called);
        });

        Test("原生模块：成功绑定后只允许同一绝对路径并复用模块", () =>
        {
            var module = new NativeModuleBinding();
            var loads = 0;
            var first = Path.GetFullPath("first/libmpv-2.dll");
            var second = Path.GetFullPath("second/libmpv-2.dll");
            module.Select(first);
            Assert.Equal(new IntPtr(1), module.Resolve(_ => { loads++; return new IntPtr(1); }));
            module.Select(first);
            Assert.Equal(new IntPtr(1), module.Resolve(_ => { loads++; return new IntPtr(2); }));
            Assert.Throws<InvalidOperationException>(() => module.Select(second));
            Assert.Equal(1, loads);
        });

        Test("原生模块：加载失败未钉死路径，可修正后重试", () =>
        {
            var module = new NativeModuleBinding();
            module.Select(Path.GetFullPath("first/libmpv-2.dll"));
            Assert.Throws<DllNotFoundException>(() => module.Resolve(_ => throw new DllNotFoundException()));
            module.Select(Path.GetFullPath("second/libmpv-2.dll"));
            Assert.Equal(new IntPtr(2), module.Resolve(_ => new IntPtr(2)));
        });

        Test("原生诊断：错误正文和头部占位凭据不进入日志或播放结果", () =>
        {
            const string secret = "OFFLINE-NATIVE-PLACEHOLDER";
            var logs = new RingBufferLogSink();
            var text = Marshal.StringToCoTaskMemUTF8("Failed X-Emby-Token: " + secret);
            var memory = Marshal.AllocHGlobal(Marshal.SizeOf<LibMpvNative.MpvEventLogMessage>());
            Log.UseSink(logs);
            try
            {
                Marshal.StructureToPtr(new LibMpvNative.MpvEventLogMessage { Text = text, LogLevel = 20 }, memory, false);
                typeof(LibMpvHandle).GetMethod("RememberLog", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [memory]);
                var failure = (string)typeof(LibMpvHandle).GetMethod("DescribeFailure", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [-13])!;
                Assert.DoesNotContain(secret, failure);
                Assert.DoesNotContain(secret, string.Join("\n", logs.Snapshot()));
                Assert.Contains("-13", failure);
                Assert.True(logs.Snapshot().Count > 0);
            }
            finally
            {
                Log.UseSink(NullLogSink.Instance);
                Marshal.FreeHGlobal(memory);
                Marshal.FreeCoTaskMem(text);
            }
        });

        Test("外部退出：主动停止优先于缺失结束事件和强杀非零码", () =>
        {
            Assert.Equal(PlaybackEndReason.Stopped, MpvProcessHandle.Classify(null, 1, true));
            Assert.Equal(PlaybackEndReason.Stopped, MpvProcessHandle.Classify(null, 0, true));
            Assert.Equal(PlaybackEndReason.Error, MpvProcessHandle.Classify(null, 1, false));
            Assert.Equal(PlaybackEndReason.EndOfFile, MpvProcessHandle.Classify(MpvEndFileReason.Eof, 0, false));
        });
    }

    private static PlaybackRequest Request() => new() { MediaUrl = new Uri("https://media.invalid/file.mkv"), Title = "offline" };
}
