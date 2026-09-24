using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Mpv;
using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// Executes the real process backend against this test executable posing as mpv. No media, network,
/// settings or credentials are read. A second local pipe reports only stage labels and the child PID.
/// This pins the Windows transport/lifetime contract, not compatibility with a real mpv build.
/// </summary>
internal static class MpvProcessTests
{
    private const string ChildPrefix = "embynian-fake-mpv:";
    private const string Token = "OFFLINE-PLACEHOLDER-NOT-A-CREDENTIAL";
    private const string Authorization = "MediaBrowser Client=\"测试,客户端\", Device=\"PC\"";
    private const string Media = "https://media.invalid/stream.mkv";
    private const string Subtitle = "https://media.invalid/sub%20title,1.ass";

    public static bool IsChild(string[] args) => args.Any(arg => arg.StartsWith("--force-media-title=" + ChildPrefix, StringComparison.Ordinal));

    public static void Register()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip("外部 mpv 安全起播子进程", "需要 Windows 命名管道和 PID 核验");
            return;
        }

        Test("安全起播：管道 PID 不匹配时零命令，拒绝冒名服务端", () => IdentityAsync().GetAwaiter().GetResult());
        Test("安全起播：逐步确认头、外挂字幕和续播点，再加载；关闭进度不开放控制", () => SuccessAsync(false).GetAwaiter().GetResult());
        Test("安全起播：打开 IPC 才观察属性和轮询位置", () => SuccessAsync(true).GetAwaiter().GetResult());
        Test("安全起播：取消未确认的认证设置会停止子进程", () => CancelAsync().GetAwaiter().GetResult());
        Test("安全起播：断线后停止子进程，不留空闲播放器", () => FailureAsync("disconnect", 1).GetAwaiter().GetResult());
        Test("安全起播：子进程未建立管道就退出时明确失败", () => FailureAsync("exit", 0).GetAwaiter().GetResult());
        Test("安全起播：取消发生在启动前，不创建子进程", () =>
        {
            var backend = Backend(false);
            Assert.Throws<OperationCanceledException>(() => backend.StartAsync(Request("unused", "hold"), new CancellationToken(true)).GetAwaiter().GetResult());
        });
        for (var index = 0; index < 4; index++)
        {
            var stage = index;
            Test($"安全起播：第 {stage + 1} 步被拒绝后不继续加载，且错误日志不回显头", () => FailureAsync($"fail{stage}", stage + 1).GetAwaiter().GetResult());
        }
        Test("安全起播：EOF 自然退出且没有 quit 掩盖结果", () => EndAsync("eof", true, PlaybackEndReason.EndOfFile).GetAwaiter().GetResult());
        Test("安全起播：关闭进度后 EOF 仍退出，但不生成已看完的状态", () => EndAsync("eof", false, PlaybackEndReason.Unknown).GetAwaiter().GetResult());
        Test("安全起播：媒体加载失败自然退出，不把诊断输出当用户错误", () => EndAsync("error", false, PlaybackEndReason.Error).GetAwaiter().GetResult());
        Test("安全起播：未答复命令超时后清理，不继续加载", () => FailureAsync("timeout", 1).GetAwaiter().GetResult());
    }

    private static MpvProcessBackend Backend(bool enabled) => new(new MpvSettings
    {
        ExecutablePath = Path.Combine(AppContext.BaseDirectory, "EmbyNian.Tests.exe"),
        EnableIpc = enabled
    });

    private static PlaybackRequest Request(string audit, string mode) => new()
    {
        MediaUrl = new Uri(Media),
        Title = $"{ChildPrefix}{audit}:{mode}",
        HttpHeaders = [new("X-Emby-Token", Token), new("X-Emby-Authorization", Authorization)],
        ExternalSubtitles = [new Uri(Subtitle)],
        StartSeconds = 1234.5
    };

    private static async Task IdentityAsync()
    {
        var name = MpvIpcClient.CreatePipeName();
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        var error = await CaptureAsync(() => MpvIpcClient.ConnectAsync(name, Environment.ProcessId + 1, TimeSpan.FromSeconds(2), deadline.Token));
        Assert.True(error is InvalidOperationException, "身份核验应当硬失败");
        await connected;
        var bytes = new byte[1];
        Assert.Equal(0, await server.ReadAsync(bytes, deadline.Token), "冒名管道收到的字节必须为 0");
    }

    private static async Task SuccessAsync(bool control)
    {
        await using var audit = new Audit();
        await using var handle = await Backend(control).StartAsync(Request(audit.Name, "hold"), audit.Token);
        Assert.Equal(control, handle.HasControlChannel);
        var player = (IPlayerControl)handle;
        var position = await handle.GetPositionAsync(audit.Token);
        Assert.Equal(control ? 1234.5 : null, position);
        await handle.ShowMessageAsync("test");
        await handle.SetPropertyAsync("pause", true, audit.Token);
        Assert.Equal(control, await player.CommandAsync(["cycle", "pause"], audit.Token));
        await handle.GetNumberAsync("duration", audit.Token);
        await handle.GetTextAsync("filename", audit.Token);
        await handle.GetTracksAsync(audit.Token);
        await handle.GetChaptersAsync(audit.Token);
        // More than one real poll period: off must remain silent, not merely miss the first tick.
        await Task.Delay(1150, audit.Token);
        await handle.StopAsync();
        await handle.WaitForExitAsync(audit.Token);
        await audit.Completion;
        audit.Check();
        Assert.Equal(4, audit.Events.Count(e => e.StartsWith("stage:", StringComparison.Ordinal)));
        Assert.Equal(control ? 8 : 0, audit.Events.Count(e => e == "observe"));
        Assert.Equal(control, audit.Events.Contains("get"));
        Assert.Equal(control, audit.Events.Contains("control"));
        Assert.True(audit.Events.Contains("quit"));
        audit.AssertExited();
    }

    private static async Task FailureAsync(string mode, int stages)
    {
        var logs = new RingBufferLogSink();
        Log.UseSink(logs);
        try
        {
            await using var audit = new Audit();
            var error = await CaptureAsync(() => Backend(false).StartAsync(Request(audit.Name, mode), audit.Token));
            Assert.True(error is InvalidOperationException, "失败不能变成无通道的成功句柄");
            await audit.Completion;
            audit.Check();
            Assert.Equal(stages, audit.Events.Count(e => e.StartsWith("stage:", StringComparison.Ordinal)));
            audit.AssertExited();
            Assert.DoesNotContain(Token, error!.ToString());
            Assert.DoesNotContain(Token, string.Join('\n', logs.Snapshot()));
            Assert.DoesNotContain(Authorization, string.Join('\n', logs.Snapshot()));
        }
        finally
        {
            Log.UseSink(NullLogSink.Instance);
        }
    }

    private static async Task CancelAsync()
    {
        await using var audit = new Audit();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(audit.Token);
        var start = Backend(false).StartAsync(Request(audit.Name, "cancel"), cancel.Token);
        await audit.HeaderReceived.Task.WaitAsync(audit.Token);
        await cancel.CancelAsync();
        var error = await CaptureAsync(() => start);
        Assert.True(error is OperationCanceledException, "用户取消必须传播为取消，而非播放失败");
        await audit.Completion;
        audit.Check();
        Assert.Equal(1, audit.Events.Count(e => e.StartsWith("stage:", StringComparison.Ordinal)));
        Assert.True(audit.Events.Contains("quit"));
        audit.AssertExited();
    }

    private static async Task EndAsync(string mode, bool control, PlaybackEndReason expected)
    {
        await using var audit = new Audit();
        await using var handle = await Backend(control).StartAsync(Request(audit.Name, mode), audit.Token);
        var exit = await handle.WaitForExitAsync(audit.Token);
        await audit.Completion;
        audit.Check();
        Assert.Equal(expected, exit.Reason);
        Assert.Null(exit.PositionSeconds);
        Assert.False(audit.Events.Contains("quit"), "必须自然退出，不能靠宿主 quit 冒充 EOF 生命周期");
        Assert.DoesNotContain(Token, exit.Message ?? "");
        audit.AssertExited();
    }

    private static async Task<Exception?> CaptureAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private sealed class Audit : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(20));
        private int _pid;
        public string Name { get; } = "embynian-audit-" + Guid.NewGuid().ToString("N");
        public CancellationToken Token => _deadline.Token;
        public ConcurrentQueue<string> Events { get; } = new();
        public TaskCompletionSource HeaderReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion { get; }

        public Audit()
        {
            _pipe = new NamedPipeServerStream(Name, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Completion = ReadAsync();
        }

        private async Task ReadAsync()
        {
            await _pipe.WaitForConnectionAsync(Token);
            using var reader = new StreamReader(_pipe, leaveOpen: true);
            while (await reader.ReadLineAsync(Token) is { } line)
            {
                if (line.StartsWith("pid:", StringComparison.Ordinal)) _pid = int.Parse(line[4..]);
                else Events.Enqueue(line);
                if (line == "stage:0") HeaderReceived.TrySetResult();
            }
        }

        public void Check()
        {
            Assert.True(Events.Contains("argv-ok"), "子进程必须实际检查它收到的 argv");
            Assert.False(Events.Any(e => e.StartsWith("bad", StringComparison.Ordinal)), string.Join(',', Events));
        }

        public void AssertExited()
        {
            Assert.True(_pid > 0, "必须获得被测子进程 PID");
            try
            {
                using var process = Process.GetProcessById(_pid);
                Assert.True(process.HasExited || process.WaitForExit(1000), "后端必须回收本次创建的播放器");
            }
            catch (ArgumentException)
            {
                // The process is already gone.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _deadline.CancelAsync();
            await _pipe.DisposeAsync();
            // Only our fixture child, as a safety net after a failed assertion. Never touches mpv/users' apps.
            if (_pid > 0)
            {
                try
                {
                    using var child = Process.GetProcessById(_pid);
                    if (!child.HasExited) child.Kill();
                }
                catch (ArgumentException) { }
            }
            try { await Completion; }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
            _deadline.Dispose();
        }
    }

    public static async Task<int> RunChildAsync(string[] args)
    {
        var title = args.Single(arg => arg.StartsWith("--force-media-title=" + ChildPrefix, StringComparison.Ordinal));
        var parts = title[("--force-media-title=" + ChildPrefix).Length..].Split(':');
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        await using var auditPipe = new NamedPipeClientStream(".", parts[0], PipeDirection.Out, PipeOptions.Asynchronous);
        await auditPipe.ConnectAsync(lifetime.Token);
        await using var audit = new StreamWriter(auditPipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await audit.WriteLineAsync($"pid:{Environment.ProcessId}");
        var line = string.Join(' ', args);
        var safe = !line.Contains(Token, StringComparison.Ordinal) && !line.Contains(Authorization, StringComparison.Ordinal)
            && !line.Contains("http-header", StringComparison.Ordinal) && !line.Contains("media.invalid", StringComparison.Ordinal)
            && args.Contains("--idle=once") && args.Contains("--keep-open=no") && args.Contains("--terminal=no");
        await audit.WriteLineAsync(safe ? "argv-ok" : "bad-argv");
        if (!safe) return 90;
        var mode = parts[1];
        if (mode == "exit") return 10;
        var pipePath = args.Single(arg => arg.StartsWith("--input-ipc-server=", StringComparison.Ordinal))["--input-ipc-server=".Length..];
        var name = pipePath[@"\\.\pipe\".Length..];
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(lifetime.Token);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var pending = reader.ReadLineAsync(lifetime.Token).AsTask();
        var stage = 0;
        while (await pending is { } request)
        {
            using var json = JsonDocument.Parse(request);
            var command = json.RootElement.GetProperty("command");
            var id = json.RootElement.GetProperty("request_id").GetInt32();
            var verb = command[0].GetString();
            pending = reader.ReadLineAsync(lifetime.Token).AsTask();
            if (verb == "quit")
            {
                await audit.WriteLineAsync("quit");
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { request_id = id, error = "success" }));
                return 0;
            }
            if (verb == "observe_property")
            {
                await audit.WriteLineAsync("observe");
            }
            else if (stage < 4)
            {
                var valid = stage switch
                {
                    0 => verb == "set_property" && command[1].GetString() == "http-header-fields"
                        && command[2].GetArrayLength() == 2 && command[2][0].GetString() == "X-Emby-Token: " + Token
                        && command[2][1].GetString() == "X-Emby-Authorization: " + Authorization,
                    1 => verb == "set_property" && command[1].GetString() == "options/sub-files"
                        && command[2].GetArrayLength() == 1 && command[2][0].GetString() == Subtitle,
                    2 => verb == "set_property" && command[1].GetString() == "options/start" && command[2].GetString() == "1234.5",
                    3 => verb == "loadfile" && command[1].GetString() == Media && command[2].GetString() == "replace",
                    _ => false
                };
                await audit.WriteLineAsync(valid ? $"stage:{stage}" : "bad-sequence");
                if (!valid) return 91;
                if (mode == "disconnect")
                {
                    await pipe.DisposeAsync();
                    await Task.Delay(Timeout.Infinite, lifetime.Token);
                }
                if (mode is "cancel" or "timeout") continue; // No ACK: parent must cancel/time out and send only quit.
                await Task.WhenAny(pending, Task.Delay(40, lifetime.Token));
                if (pending.IsCompleted) await audit.WriteLineAsync("bad-pipelined-before-ack");
                if (mode == $"fail{stage}")
                {
                    await writer.WriteLineAsync("not-json " + Token);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { request_id = id, error = Token }));
                    await Console.Error.WriteLineAsync("Failed " + Token);
                    await Console.Out.WriteLineAsync(Token);
                    continue;
                }
                stage++;
            }
            else
            {
                await audit.WriteLineAsync(verb is "get_property" or "get_property_string" ? "get" : "control");
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { request_id = id, error = "success", data = 1234.5 }));
            if (stage == 4 && mode is "eof" or "error")
            {
                await Task.Delay(100, lifetime.Token);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { @event = "end-file", reason = mode }));
                await Console.Error.WriteLineAsync("Cannot open " + Token);
                return mode == "eof" ? 0 : 2;
            }
        }
        return 0;
    }
}
