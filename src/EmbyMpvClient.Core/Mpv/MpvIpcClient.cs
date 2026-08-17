using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using EmbyMpvClient.Diagnostics;

namespace EmbyMpvClient.Mpv;

/// <summary>
/// The control channel to a running mpv: JSON commands out, events and replies in, over a
/// Windows named pipe.
/// <para>
/// Everything here is best-effort by design. mpv is a separate process the user can close at any
/// moment, so a broken pipe is a normal end-of-session and not an error to surface: the playback
/// session degrades to "no live position" instead of failing.
/// </para>
/// </summary>
public sealed class MpvIpcClient : IAsyncDisposable
{
    private const string Category = "mpv-ipc";

    private readonly NamedPipeClientStream _pipe;
    private readonly MpvIpcLineBuffer _buffer = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<MpvIpcMessage>> _pending = [];
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _closing = new();

    private int _nextRequestId;
    private int _nextObserverId;
    private Task? _reader;
    private volatile bool _disposed;

    private MpvIpcClient(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>Fired for every observed property that changes.</summary>
    public event Action<string, MpvIpcMessage>? PropertyChanged;

    /// <summary>Fired when mpv finishes a file, with the raw mpv reason.</summary>
    public event Action<string>? FileEnded;

    /// <summary>Fired once when the pipe closes, whatever the cause.</summary>
    public event Action? Closed;

    public bool IsConnected => !_disposed && _pipe.IsConnected;

    /// <summary>A pipe name unique to this launch, so it cannot collide with a manually started mpv.</summary>
    public static string CreatePipeName() => $"embympvclient-{Guid.NewGuid():N}";

    /// <summary>mpv wants the full path form on Windows; <see cref="NamedPipeClientStream"/> wants only the name.</summary>
    public static string ToPipePath(string pipeName) => $@"\\.\pipe\{pipeName}";

    /// <summary>
    /// Waits for mpv to create the pipe. mpv opens it a little after the process starts, so a
    /// single attempt would nearly always lose the race; the caller cancels
    /// <paramref name="cancellationToken"/> when the process exits, which stops the wait early
    /// instead of burning the whole timeout on a launch that already failed.
    /// </summary>
    public static async Task<MpvIpcClient?> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(200, cancellationToken).ConfigureAwait(false);
                var client = new MpvIpcClient(pipe);
                client.Start();
                Log.Debug(Category, $"已连接 mpv 控制通道：{pipeName}（等待 {deadline.ElapsedMilliseconds} ms）");
                return client;
            }
            catch (TimeoutException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (IOException)
            {
                // The pipe exists but is already taken; mpv only serves one client at a time.
                await pipe.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        Log.Warn(Category, $"在 {timeout.TotalSeconds:0.#} 秒内未能连接 mpv 控制通道，本次播放不上报进度");
        return null;
    }

    private void Start() => _reader = Task.Run(ReadLoopAsync);

    private async Task ReadLoopAsync()
    {
        var chunk = new byte[8192];

        try
        {
            while (!_closing.IsCancellationRequested)
            {
                var read = await _pipe.ReadAsync(chunk, _closing.Token).ConfigureAwait(false);
                if (read <= 0) break;

                foreach (var line in _buffer.Append(chunk.AsSpan(0, read))) Dispatch(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // mpv closed: the expected way for this loop to end.
        }
        catch (Exception error)
        {
            Log.Warn(Category, "读取 mpv 控制通道时出错", error);
        }
        finally
        {
            FailPending(new IOException("mpv 控制通道已关闭"));
            Closed?.Invoke();
        }
    }

    private void Dispatch(string line)
    {
        if (!MpvIpcMessage.TryParse(line, out var message))
        {
            Log.Debug(Category, $"忽略无法解析的 mpv 消息：{Truncate(line)}");
            return;
        }

        if (message.IsReply && _pending.TryRemove(message.RequestId!.Value, out var waiter))
        {
            waiter.TrySetResult(message);
            return;
        }

        switch (message.Event)
        {
            case "property-change" when message.PropertyName is { } name:
                Raise(() => PropertyChanged?.Invoke(name, message));
                break;
            case "end-file":
                Raise(() => FileEnded?.Invoke(message.Reason ?? MpvEndFileReason.Unknown));
                break;
        }
    }

    /// <summary>An event handler that throws must not kill the reader loop and with it the session.</summary>
    private static void Raise(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            Log.Warn(Category, "处理 mpv 事件的回调抛出异常", error);
        }
    }

    // ---- commands -------------------------------------------------------------

    /// <summary>
    /// Asks mpv to report changes to a property from now on. The first argument is an observer
    /// id the client picks; it comes back on every property-change, and reusing one would make
    /// mpv replace the earlier observation instead of adding another.
    /// </summary>
    public Task<bool> ObservePropertyAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync(cancellationToken, "observe_property", Interlocked.Increment(ref _nextObserverId), name);

    public async Task<double?> GetNumberAsync(string name, CancellationToken cancellationToken = default) =>
        (await RequestAsync(cancellationToken, "get_property", name).ConfigureAwait(false))?.AsDouble();

    public async Task<bool?> GetBooleanAsync(string name, CancellationToken cancellationToken = default) =>
        (await RequestAsync(cancellationToken, "get_property", name).ConfigureAwait(false))?.AsBoolean();

    /// <summary>
    /// A property whose payload is not a number or a flag, kept as its raw JSON — track-list
    /// arrives as an array of objects, which the caller parses itself.
    /// </summary>
    public async Task<string?> GetPropertyRawAsync(string name, CancellationToken cancellationToken = default) =>
        (await RequestAsync(cancellationToken, "get_property", name).ConfigureAwait(false))?.Data?.GetRawText();

    public Task<bool> SetPropertyAsync(string name, object? value, CancellationToken cancellationToken = default) =>
        SendAsync(cancellationToken, "set_property", name, value);

    public Task<bool> ShowTextAsync(string text, int milliseconds = 2500, CancellationToken cancellationToken = default) =>
        SendAsync(cancellationToken, "show-text", text, milliseconds);

    public Task<bool> QuitAsync(CancellationToken cancellationToken = default) =>
        SendAsync(cancellationToken, "quit");

    /// <summary>Sends a command and reports only whether mpv accepted it.</summary>
    public async Task<bool> SendAsync(CancellationToken cancellationToken, params object?[] command)
    {
        var reply = await RequestAsync(cancellationToken, command).ConfigureAwait(false);
        return reply?.IsSuccess ?? false;
    }

    /// <summary>
    /// Sends a command and waits for its reply. Returns null instead of throwing when the pipe
    /// is gone or mpv does not answer: every caller here treats an unanswered command as
    /// "no live data", never as a failure worth showing the user.
    /// </summary>
    public async Task<MpvIpcMessage?> RequestAsync(CancellationToken cancellationToken, params object?[] command)
    {
        if (!IsConnected) return null;

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var waiter = new TaskCompletionSource<MpvIpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = waiter;

        try
        {
            await WriteAsync(Encode(command, requestId), cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            await using var registration = timeout.Token.Register(() => waiter.TrySetCanceled()).ConfigureAwait(false);
            var reply = await waiter.Task.ConfigureAwait(false);

            if (!reply.IsSuccess)
                Log.Debug(Category, $"mpv 拒绝命令 {command.FirstOrDefault()}：{reply.Error}");

            return reply;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    internal static byte[] Encode(object?[] command, int requestId)
    {
        var payload = new StringBuilder("{\"command\":[");
        for (var index = 0; index < command.Length; index++)
        {
            if (index > 0) payload.Append(',');
            // The default encoder escapes non-ASCII as \uXXXX, which mpv's JSON parser handles
            // and which makes the whole line pure ASCII — one less thing to disagree about
            // across the pipe. Do not "fix" this with UnsafeRelaxedJsonEscaping.
            payload.Append(JsonSerializer.Serialize(command[index]));
        }

        payload.Append("],\"request_id\":").Append(requestId).Append("}\n");
        return Encoding.UTF8.GetBytes(payload.ToString());
    }

    private async Task WriteAsync(byte[] payload, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void FailPending(Exception error)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var waiter)) waiter.TrySetException(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _closing.CancelAsync().ConfigureAwait(false);

        try
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        if (_reader is not null)
        {
            try
            {
                await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException)
            {
            }
        }

        _writeGate.Dispose();
        _closing.Dispose();
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
