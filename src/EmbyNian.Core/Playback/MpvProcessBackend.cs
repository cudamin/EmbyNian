using System.Diagnostics;
using System.Text.Json;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Mpv;

namespace EmbyNian.Playback;

/// <summary>Launches the user's own <c>mpv.exe</c> and controls it over a named pipe.</summary>
public sealed class MpvProcessBackend(MpvSettings settings) : IPlaybackBackend
{
    private const string Category = "mpv";

    public string DisplayName => "外部 mpv.exe";

    public string? Validate()
    {
        var path = settings.ExecutablePath;
        if (string.IsNullOrWhiteSpace(path)) return "尚未设置 mpv.exe 的路径";
        if (!File.Exists(path)) return $"找不到 mpv.exe：{path}";
        return null;
    }

    public async Task<IPlaybackHandle> StartAsync(PlaybackRequest request, CancellationToken cancellationToken)
    {
        if (Validate() is { } problem) throw new InvalidOperationException(problem);

        cancellationToken.ThrowIfCancellationRequested();
        var pipeName = MpvIpcClient.CreatePipeName();
        var arguments = MpvArgumentBuilder.Build(request, MpvIpcClient.ToPipePath(pipeName));

        var start = new ProcessStartInfo
        {
            FileName = settings.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            // So that anything mpv itself resolves relative to its own folder — a codec list, a
            // fontconfig cache — behaves as it would if it had been launched from there by hand.
            WorkingDirectory = Path.GetDirectoryName(settings.ExecutablePath) ?? Environment.CurrentDirectory
        };

        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        Log.Info(Category, "启动外部 mpv：通过验证身份的管道传递播放请求");
        if (request.ShaderProfile is { } profile)
            Log.Info(Category, $"着色器配置组：{profile}（{request.ShaderReason}）");

        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 mpv 进程");
        var handle = new MpvProcessHandle(process);
        try
        {
            await handle.AttachAsync(pipeName, request, settings.EnableIpc, cancellationToken).ConfigureAwait(false);
            return handle;
        }
        catch
        {
            // Attach 一失败，这个句柄就没人接了：进程不带自毁，客户端这边一场空，mpv 却在前台继续放。
            // 交给句柄自己收拾 —— DisposeAsync 开头会送还活着的 mpv 走（见它那句注释），所以这里只管
            // 放回去再重抛，让调用方看到真正的失败原因。
            await handle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>One mpv process plus its control channel.</summary>
internal sealed class MpvProcessHandle(Process process) : IPlaybackHandle, IPlayerControl
{
    private const string Category = "mpv";
    private readonly CancellationTokenSource _finished = new();

    // Always retained until quit/disposal. _ipc is only the optional live-control capability;
    // disabling progress never makes authentication fall back to argv or opens UI control.
    private MpvIpcClient? _startup;
    private MpvIpcClient? _ipc;
    private Task? _outputDrain;
    private int _disposeStarted;
    private Task? _poller;
    /// <summary>
    /// Milliseconds, or -1 for "not known yet". A nullable double cannot be volatile, and the
    /// poller, the session's own polling call and the exit path all touch this from different
    /// threads, so it is one interlocked integer instead of a lock.
    /// </summary>
    private long _lastPositionMs = -1;
    private volatile string? _endReason;
    private volatile bool _stopRequested;

    /// <summary>
    /// Written by the pipe's reader thread and by the position poller, both serialised by
    /// <see cref="_statusGate"/>; read by the UI at any time.
    /// </summary>
    private PlayerStatus _status = new();
    private readonly object _statusGate = new();

    public bool HasControlChannel => _ipc is { IsConnected: true };

    /// <summary>
    /// 外部 mpv.exe 的画面永远在它自己的顶层窗口里（<see cref="Mpv.MpvArgumentBuilder"/> 从不传
    /// wid，也没有任何嵌入参数），与渲染管线档位无关 —— 这里直接表态，不再让上层回退到
    /// 「管线设置」去猜：管线档位描述的是内置 libmpv 怎么出画面，对外部后端什么也没说。
    /// </summary>
    public bool? PictureInHostWindow => false;

    public bool IsPaused => _status.Paused;

    public PlayerStatus Status => _status;

    public event Action<bool>? PauseChanged;

    public event Action<PlayerStatus>? StatusChanged;

    public event Action<IReadOnlyList<MpvTrack>>? TracksChanged;

    internal async Task AttachAsync(
        string pipeName, PlaybackRequest request, bool enableControl, CancellationToken cancellationToken)
    {
        // mpv can echo headers in errors. Drain both streams without retaining or publishing their text.
        _outputDrain = Task.WhenAll(
            process.StandardError.BaseStream.CopyToAsync(Stream.Null),
            process.StandardOutput.BaseStream.CopyToAsync(Stream.Null));

        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnExited(object? sender, EventArgs arguments) => Cancel(giveUp);

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += OnExited;
            if (process.HasExited) Cancel(giveUp);

            _startup = await MpvIpcClient.ConnectAsync(pipeName, process.Id, TimeSpan.FromSeconds(10), giveUp.Token)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("无法建立 mpv 安全启动通道，未发送播放请求");
            _startup.Closed += () => Cancel(_finished);

            if (enableControl)
            {
                _ipc = _startup;
                _ipc.FileEnded += reason => _endReason = reason switch
                {
                    MpvEndFileReason.Eof or MpvEndFileReason.Error or MpvEndFileReason.Quit or MpvEndFileReason.Stop => reason,
                    _ => MpvEndFileReason.Unknown
                };
                _ipc.PropertyChanged += OnPropertyChanged;
                // Position stays on the poller: time-pos would notify across the pipe on every frame.
                foreach (var property in (string[])
                         ["pause", "duration", "volume", "mute", "speed", "paused-for-cache", "demuxer-cache-time", "track-list"])
                    await _ipc.ObservePropertyAsync(property, giveUp.Token).ConfigureAwait(false);
            }

            foreach (var command in MpvArgumentBuilder.LoadCommands(request))
            {
                giveUp.Token.ThrowIfCancellationRequested();
                if (!await _startup.SendAsync(giveUp.Token, command).ConfigureAwait(false))
                    throw new InvalidOperationException("mpv 未确认安全起播命令，已停止启动；请检查外部播放器版本");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("mpv 在安全起播完成前退出了");
        }
        finally
        {
            process.Exited -= OnExited;
        }

        if (!enableControl)
        {
            // No file-end observation either: reporting EOF upstream would mark the item watched
            // despite opting out of progress. idle=once still lets the process exit on its own.
            Log.Info(Category, "已关闭 mpv 进度与控制，仅保留安全起播和退出通道，本次不上报进度");
            return;
        }

        // External mpv owns its window. This is command acceptance, not proof of a rendered frame.
        Update(status => status with { Loaded = true, PictureStarted = true });
        var stopping = _finished.Token;
        _poller = Task.Run(() => PollPositionAsync(stopping), CancellationToken.None);
    }

    /// <summary>
    /// Both cancellations are raced against disposal — mpv can exit at the same moment the
    /// session tears the handle down — and cancelling a disposed source throws.
    /// </summary>
    private static void Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnPropertyChanged(string name, MpvIpcMessage message)
    {
        if (name == "track-list")
        {
            if (TracksChanged is not null) _ = PublishTracksAsync();
            return;
        }

        Update(status => name switch
        {
            "pause" => message.AsBoolean() is { } paused ? status with { Paused = paused } : status,
            "duration" => message.AsDouble() is { } duration ? status with { Duration = Math.Max(0, duration) } : status,
            "volume" => message.AsDouble() is { } volume ? status with { Volume = volume } : status,
            "mute" => message.AsBoolean() is { } muted ? status with { Muted = muted } : status,
            "speed" => message.AsDouble() is { } speed ? status with { Speed = speed } : status,
            "paused-for-cache" => message.AsBoolean() is { } waiting ? status with { Buffering = waiting } : status,
            "demuxer-cache-time" => message.AsDouble() is { } cache ? status with { CacheEnd = Math.Max(0, cache) } : status,
            _ => status
        });
    }

    private async Task PublishTracksAsync()
    {
        try
        {
            var tracks = await GetTracksAsync(_finished.Token).ConfigureAwait(false);
            if (tracks.Count > 0) TracksChanged?.Invoke(tracks);
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Folds a change into the snapshot and tells the chrome, but only when something it draws
    /// actually moved. Under a lock because two threads produce changes — the pipe's reader and
    /// the position poller — and a lost update here would leave the bar showing a stale value
    /// until the next unrelated notification.
    /// </summary>
    private void Update(Func<PlayerStatus, PlayerStatus> change)
    {
        PlayerStatus status;
        bool differs;
        bool pauseChanged;

        lock (_statusGate)
        {
            var previous = _status;
            status = change(previous);
            differs = status.DiffersFrom(previous);
            pauseChanged = status.Paused != previous.Paused;
            _status = status;
        }

        if (differs) StatusChanged?.Invoke(status);
        if (pauseChanged) PauseChanged?.Invoke(status.Paused);
    }

    /// <summary>
    /// One request per second, rather than observing time-pos: mpv notifies observers of that
    /// property on every frame, and nothing here needs 60 updates a second across a pipe. Polling
    /// also means the last value is at most a second stale when mpv exits, which is what the stop
    /// report sent to Emby uses.
    /// </summary>
    private async Task PollPositionAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_ipc is not { IsConnected: true }) break;
                if (await _ipc.GetNumberAsync("time-pos", cancellationToken).ConfigureAwait(false) is { } position)
                {
                    Remember(position);
                    Update(status => status with { Position = Math.Max(0, position) });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Remember(double seconds) =>
        Interlocked.Exchange(ref _lastPositionMs, (long)Math.Max(0, seconds * 1000));

    private double? LastPosition =>
        Interlocked.Read(ref _lastPositionMs) is var milliseconds && milliseconds < 0 ? null : milliseconds / 1000d;

    public async Task<double?> GetPositionAsync(CancellationToken cancellationToken)
    {
        if (_ipc is not { IsConnected: true }) return LastPosition;
        var position = await _ipc.GetNumberAsync("time-pos", cancellationToken).ConfigureAwait(false);
        if (position is not null) Remember(position.Value);
        return LastPosition;
    }

    public async Task<PlaybackExit> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        Cancel(_finished);
        if (_ipc is not null)
        {
            try
            {
                await _ipc.Completion.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Exit is certain even if another inherited pipe handle prevents EOF on the pipe.
            }
        }

        var exitCode = SafeExitCode();
        var reason = Classify(exitCode);
        var message = reason == PlaybackEndReason.Error ? $"mpv 播放失败（退出代码 {exitCode}）" : null;

        Log.Info(Category, $"mpv 已退出（代码 {exitCode}，原因 {_endReason ?? "未知"}）");
        return new PlaybackExit(reason, LastPosition, exitCode, message);
    }

    private PlaybackEndReason Classify(int exitCode) => _endReason switch
    {
        MpvEndFileReason.Eof => PlaybackEndReason.EndOfFile,
        MpvEndFileReason.Error => PlaybackEndReason.Error,
        MpvEndFileReason.Quit or MpvEndFileReason.Stop => _stopRequested ? PlaybackEndReason.Stopped : PlaybackEndReason.UserQuit,
        // Without a control channel the exit code is all there is; mpv returns non-zero only
        // when it could not play the file.
        _ => exitCode == 0 ? PlaybackEndReason.Unknown : PlaybackEndReason.Error
    };

    public async Task StopAsync()
    {
        _stopRequested = true;

        if (_startup is { IsConnected: true })
        {
            await _startup.QuitAsync().ConfigureAwait(false);
            if (await WaitForExitWithinAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false)) return;
        }

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn(Category, "结束 mpv 进程失败", error);
        }
    }

    public async Task ShowMessageAsync(string text)
    {
        if (_ipc is { IsConnected: true }) await _ipc.ShowTextAsync(text).ConfigureAwait(false);
    }

    public async Task SetPropertyAsync(string name, object? value, CancellationToken cancellationToken)
    {
        if (value is null || _ipc is not { IsConnected: true }) return;
        await _ipc.SetPropertyAsync(name, value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// mpv's IPC takes the same command names as its command line, so an argument list from the
    /// client's chrome goes over the pipe unchanged. True only when mpv answered that it accepted the
    /// command — <see cref="MpvIpcClient.SendAsync"/> already waits for that reply, the answer was just
    /// being dropped here.
    /// </summary>
    public async Task<bool> CommandAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count == 0) return true;
        if (_ipc is not { IsConnected: true }) return false;

        return await _ipc.SendAsync(cancellationToken, [.. arguments]).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MpvTrack>> GetTracksAsync(CancellationToken cancellationToken)
    {
        if (_ipc is not { IsConnected: true }) return [];

        var raw = await _ipc.GetPropertyRawAsync("track-list", cancellationToken).ConfigureAwait(false);
        if (raw is null) return [];

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var tracks = new List<MpvTrack>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!TryText(element, "type", out var type) || type is not ("audio" or "sub" or "video")) continue;
                if (!element.TryGetProperty("id", out var id) || !id.TryGetInt32(out var trackId)) continue;

                tracks.Add(new MpvTrack(
                    trackId,
                    type,
                    TryText(element, "lang", out var language) ? language : null,
                    TryText(element, "title", out var title) ? title : null,
                    TryFlag(element, "default"),
                    TryFlag(element, "selected"))
                {
                    // Everything below is optional in mpv's own output — an old build, or a demuxer
                    // that knows less — so each one is read on its own and left at zero when absent.
                    Codec = TryText(element, "codec", out var codec) ? codec : null,
                    Channels = TryText(element, "demux-channels", out var channels) ? channels : null,
                    ChannelCount = TryNumber(element, "demux-channel-count"),
                    SampleRate = TryNumber(element, "demux-samplerate"),
                    BitRate = TryNumber(element, "demux-bitrate"),
                    Forced = TryFlag(element, "forced"),
                    External = TryFlag(element, "external"),
                    Image = TryFlag(element, "image"),
                    HearingImpaired = TryFlag(element, "hearing-impaired")
                });
            }

            return tracks;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// mpv's chapter marks in one read. The count probe stays the caller's readiness gate; this
    /// is the one round trip that replaces walking the list two questions per chapter — over the
    /// pipe, twenty chapters used to be forty-two of them.
    /// </summary>
    public async Task<IReadOnlyList<SkipChapter>> GetChaptersAsync(CancellationToken cancellationToken)
    {
        if (_ipc is not { IsConnected: true }) return [];

        var raw = await _ipc.GetPropertyRawAsync("chapter-list", cancellationToken).ConfigureAwait(false);
        if (raw is null) return [];

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var chapters = new List<SkipChapter>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.Number) continue;

                string? title = element.TryGetProperty("title", out var titleElement)
                                && titleElement.ValueKind == JsonValueKind.String
                    ? titleElement.GetString()
                    : null;

                // The time keeps its fraction: rounding it into an int would spend half the skip
                // planner's ±0.5 s tolerance on arithmetic before any real drift.
                chapters.Add(new SkipChapter(time.GetDouble(), title));
            }

            return chapters;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TryText(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return true;
        }

        value = "";
        return false;
    }

    private static bool TryFlag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    /// <summary>
    /// A track-list number, rounded and clamped to an int; 0 when the field is missing or is not one.
    /// mpv writes the bitrate as a JSON double, so <c>GetInt32</c> alone would drop it.
    /// </summary>
    private static int TryNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number) return 0;
        if (property.TryGetInt32(out var whole)) return whole;
        return property.TryGetDouble(out var number) && number > 0 && number < int.MaxValue
            ? (int)Math.Round(number)
            : 0;
    }

    /// <summary>
    /// True: every read here is a JSON line down the pipe carrying a request id, and
    /// <see cref="MpvIpcClient.RequestAsync"/> matches each reply back to its own waiter, so reads
    /// issued together do not confuse one another. What they save is the waiting — a round trip is
    /// mpv's scheduling latency, not this client's, and twenty-one of them in single file can outlast
    /// the second of statistics they were meant to fill.
    /// </summary>
    public bool ReadsOverlap => true;

    public async Task<double?> GetNumberAsync(string name, CancellationToken cancellationToken)
    {
        if (_ipc is not { IsConnected: true }) return null;
        return await _ipc.GetNumberAsync(name, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetTextAsync(string name, CancellationToken cancellationToken)
    {
        if (_ipc is not { IsConnected: true }) return null;
        return await _ipc.GetTextAsync(name, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WaitForExitWithinAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private int SafeExitCode()
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        // 句柄一放出去，这个进程就没人管了。正常收尾那一头 mpv 已经退了，这一下只花一次 HasExited 的钱；
        // 句柄还活着就被 Dispose 的那些路（起播失败、上报那一句抛了别的错）却正是 mpv 被漏在外面继续放
        // 的地方 —— 从前这里只拆观察用的管线，从不开口请 mpv 走，两个后端的 Dispose 语义也从此一致。
        try
        {
            if (!process.HasExited) await StopAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "释放句柄时结束 mpv 进程失败", error);
        }

        Cancel(_finished);

        if (_poller is not null)
        {
            try
            {
                await _poller.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException)
            {
            }
        }

        if (_startup is not null) await _startup.DisposeAsync().ConfigureAwait(false);
        if (_outputDrain is not null)
        {
            try
            {
                await _outputDrain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception error) when (error is TimeoutException or IOException or ObjectDisposedException)
            {
                // Diagnostic text is deliberately discarded, also on failure.
            }
        }

        _finished.Dispose();
        process.Dispose();
    }
}
