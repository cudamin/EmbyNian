using System.Globalization;
using System.Runtime.InteropServices;
using EmbyMpvClient.Configuration;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Mpv;

namespace EmbyMpvClient.Playback;

/// <summary>
/// The in-process player: loads <c>libmpv-2.dll</c> and renders the video into the window given by
/// <paramref name="windowProvider"/>. It is self-contained: <paramref name="configDirectory"/> is
/// the folder that holds the mpv.conf, shaders and scripts it may use, and the dll itself is
/// loaded from an explicit absolute path — nothing is read from an external mpv installation
/// unless the settings point at one.
/// </summary>
public sealed class LibMpvBackend(MpvSettings settings, Func<IntPtr> windowProvider, string configDirectory) : IPlaybackBackend
{
    private const string Category = "mpv";
    private const string LibraryName = "libmpv-2.dll";

    public string DisplayName => "内置 libmpv";

    public string? Validate() =>
        ResolveLibMpv() is null
            ? $"找不到 {LibraryName}。已查找：{string.Join("、", Probes())}。把它放到程序目录，或在设置里填写完整路径。"
            : null;

    /// <summary>
    /// Where the dll may live, in order: the setting, the program folder, then next to the
    /// configured mpv.exe. The setting accepts a folder as well as a file — pasting the folder a
    /// dll sits in is the more natural thing to do, and rejecting it looks like the path is wrong.
    /// </summary>
    private IEnumerable<string> Probes()
    {
        var configured = settings.LibMpvPath?.Trim() ?? "";
        if (configured.Length > 0)
        {
            yield return configured.EndsWith(LibraryName, StringComparison.OrdinalIgnoreCase)
                ? configured
                : Path.Combine(configured, LibraryName);
        }

        yield return Path.Combine(AppContext.BaseDirectory, LibraryName);

        if (Path.GetDirectoryName(settings.ExecutablePath) is { Length: > 0 } mpvFolder)
            yield return Path.Combine(mpvFolder, LibraryName);
    }

    private string? ResolveLibMpv() => Probes().FirstOrDefault(File.Exists);

    public Task<IPlaybackHandle> StartAsync(PlaybackRequest request, CancellationToken cancellationToken)
    {
        IntPtr context = IntPtr.Zero;
        try
        {
            var dllPath = ResolveLibMpv() ?? throw new InvalidOperationException(Validate() ?? $"找不到 {LibraryName}");

            // The dll is bound by absolute path rather than by name: a path from the settings is
            // otherwise validated here and then ignored by the loader, which only searches its
            // own directories. Those directories still matter for libmpv's own dependencies
            // (lua51.dll, vulkan-1.dll…), which it loads by name from wherever it sits.
            var dllFolder = Path.GetDirectoryName(dllPath) ?? "";
            LibMpvNative.UseLibrary(dllPath);
            LibMpvNative.EnsureDependencyDirectories(dllFolder, AppContext.BaseDirectory);

            context = LibMpvNative.mpv_create();
            if (context == IntPtr.Zero) throw new InvalidOperationException("mpv_create 返回了空句柄");

            ApplyOptions(context, request);

            var error = LibMpvNative.mpv_initialize(context);
            if (error < 0) throw new InvalidOperationException($"mpv 初始化失败：{Describe(error)}");
            cancellationToken.ThrowIfCancellationRequested();

            LibMpvNative.mpv_request_log_messages(context, "warn");

            var handle = new LibMpvHandle(context);
            handle.Start(request);
            context = IntPtr.Zero; // ownership moved to the handle
            Log.Info(Category, $"内置播放器已就绪（{dllPath}）");
            return Task.FromResult<IPlaybackHandle>(handle);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException($"{LibraryName} 无法加载（{error.Message}）——它必须是 64 位、与你 mpv 同期的构建");
        }
        finally
        {
            if (context != IntPtr.Zero) LibMpvNative.mpv_terminate_destroy(context);
        }
    }

    private void ApplyOptions(IntPtr context, PlaybackRequest request)
    {
        var window = windowProvider();
        if (window == IntPtr.Zero) throw new InvalidOperationException("播放窗口尚未就绪");

        // config-dir points at the client's own folder, so the mpv.conf, input.conf, shaders
        // and every ~~/ path resolve inside the program folder — never into an external mpv
        // installation. libmpv defaults to config=no (no config at all), so it must be enabled
        // explicitly or the mpv.conf, scripts and ~~ prefixes would all be dead.
        Set(context, "config-dir", configDirectory);
        Set(context, "config", "yes");

        // wid is an integer option; the typed form removes every parsing ambiguity, so the
        // video embeds into the client window instead of mpv opening a window of its own.
        var handle = window.ToInt64();
        var widError = LibMpvNative.mpv_set_option(context, "wid", LibMpvNative.FormatInt64, ref handle);
        if (widError < 0) Log.Warn(Category, $"设置 mpv 选项 wid 失败：{Describe(widError)}");
        else Log.Info(Category, $"内置播放器嵌入窗口 0x{window.ToInt64():X}");

        // The client draws its own player chrome over the video; mpv's on-screen controller
        // would duplicate it, and its keyboard bindings would act on top of the client's own —
        // one keypress seeking twice. Input belongs to exactly one of the two, and the client
        // is the one that can also drive the seek bar, so mpv's input is switched off whole.
        Set(context, "osc", "no");
        Set(context, "input-default-bindings", "no");
        Set(context, "input-vo-keyboard", "no");
        Set(context, "input-media-keys", "no");

        // The child window exists from initialize() rather than from the first decoded frame,
        // so the client can hook it and show its chrome without waiting for the video, and the
        // panel never flashes empty on a slow network.
        Set(context, "force-window", "immediate");

        // Playback ending must not take the player down with it: the client decides when the
        // context goes away, which is what lets it report a final position and, one day, load
        // the next episode into the same window.
        Set(context, "idle", "yes");
        Set(context, "keep-open", "no");

        // The seek bar hands mpv exact timestamps; without this a click lands on the preceding
        // keyframe, up to several seconds from where the user aimed.
        Set(context, "hr-seek", "yes");

        if (!string.IsNullOrWhiteSpace(request.IncludeFile)) Set(context, "include", request.IncludeFile);
        if (!string.IsNullOrWhiteSpace(request.ShaderProfile)) Set(context, "profile", request.ShaderProfile);

        if (request.OverrideMpvResume)
        {
            Set(context, "resume-playback", "no");
            Set(context, "save-position-on-quit", "no");
        }

        Set(context, "start", FormatSeconds(request.StartSeconds));

        if (request.AudioId is { } audioId) Set(context, "aid", audioId.ToString(CultureInfo.InvariantCulture));
        if (request.SubtitlesDisabled) Set(context, "sid", "no");
        else if (request.SubtitleId is { } subtitleId) Set(context, "sid", subtitleId.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(request.AudioLanguage)) Set(context, "alang", request.AudioLanguage);
        if (!string.IsNullOrWhiteSpace(request.SubtitleLanguage)) Set(context, "slang", request.SubtitleLanguage);
        if (!string.IsNullOrWhiteSpace(request.SubtitleFont)) Set(context, "sub-font", request.SubtitleFont);

        foreach (var subtitle in request.ExternalSubtitles) Set(context, "sub-files-append", subtitle.AbsoluteUri);

        // http-header-fields-append 是命令行专有写法，libmpv 的 mpv_set_option_string 认不出来
        // （option not found）。http-header-fields 才是可设置的选项：逗号分隔、反斜杠转义。
        if (request.HttpHeaders.Count > 0)
        {
            var fields = string.Join(",",
                request.HttpHeaders.Select(header => $"{header.Key}: {MpvListValue.Escape(header.Value)}"));
            Set(context, "http-header-fields", fields);
        }

        if (!string.IsNullOrWhiteSpace(request.Title)) Set(context, "force-media-title", request.Title);

        foreach (var argument in CommandLine.Split(settings.ExtraArguments)) ApplyExtraArgument(context, argument);
    }

    /// <summary>Replays the user's extra command-line arguments as libmpv options.</summary>
    private static void ApplyExtraArgument(IntPtr context, string argument)
    {
        if (!argument.StartsWith("--", StringComparison.Ordinal)) return;

        var body = argument[2..];
        var separator = body.IndexOf('=');
        if (separator > 0)
        {
            Set(context, body[..separator], body[(separator + 1)..]);
            return;
        }

        // --flag without a value: most mpv toggles default to yes.
        if (body.Length > 0) Set(context, body, "yes");
    }

    private static string FormatSeconds(double value) =>
        Math.Max(0, value).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Option errors are logged, not fatal — a bad shader path must not stop the film.</summary>
    private static void Set(IntPtr context, string name, string? value)
    {
        if (value is null) return;
        var error = LibMpvNative.mpv_set_option_string(context, name, value);
        if (error < 0) Log.Warn(Category, $"设置 mpv 选项 {name} 失败：{Describe(error)}");
    }

    internal static string Describe(int error)
    {
        var ptr = LibMpvNative.mpv_error_string(error);
        return ptr == IntPtr.Zero ? $"错误 {error}" : Marshal.PtrToStringUTF8(ptr) ?? $"错误 {error}";
    }
}

/// <summary>
/// One in-process playback: an event thread draining mpv's queue, which is also the only thread
/// that reads state out of it.
/// <para>
/// Nothing here polls. Every value the player chrome draws arrives as a property-change event and
/// is folded into a <see cref="PlayerStatus"/>, so the seek bar moves with the video rather than
/// with a timer, and the client asks mpv for nothing it has already been told.
/// </para>
/// </summary>
internal sealed class LibMpvHandle(IntPtr context) : IPlaybackHandle, IPlayerControl
{
    private const string Category = "mpv";
    private const int LogTailLines = 40;

    // Observed properties are identified by reply id rather than by name: the event carries the
    // id back, so dispatching costs an integer switch instead of marshalling a string out of
    // native memory dozens of times a second.
    private const ulong ObserveTimePos = 1;
    private const ulong ObserveDuration = 2;
    private const ulong ObservePause = 3;
    private const ulong ObserveVolume = 4;
    private const ulong ObserveMute = 5;
    private const ulong ObserveSpeed = 6;
    private const ulong ObserveCacheTime = 7;
    private const ulong ObservePausedForCache = 8;
    private const ulong ObserveTrackList = 9;

    private readonly Queue<string> _logTail = new(LogTailLines);
    private readonly TaskCompletionSource<PlaybackExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Serialises every native call against destruction: <c>mpv_terminate_destroy</c> must never
    /// run while another thread is inside the same handle, so it happens under this gate and a
    /// later call sees the destroyed flag and walks away instead of touching freed memory.
    /// </summary>
    private readonly SemaphoreSlim _apiGate = new(1, 1);

    private Thread? _eventThread;
    private long _lastPositionMs = -1;
    private volatile bool _stopRequested;
    private volatile bool _leaveLoop;
    private volatile int _destroyed;

    /// <summary>Only ever touched by the event thread, then published through <see cref="Status"/>.</summary>
    private PlayerStatus _status = new();

    public bool HasControlChannel => true;

    public bool IsPaused => _status.Paused;

    public PlayerStatus Status => _status;

    public event Action<bool>? PauseChanged;

    public event Action<PlayerStatus>? StatusChanged;

    public event Action<IReadOnlyList<MpvTrack>>? TracksChanged;

    /// <summary>Starts the event thread, subscribes to the state the chrome needs, then loads the file.</summary>
    internal void Start(PlaybackRequest request)
    {
        Observe(ObserveTimePos, "time-pos", LibMpvNative.FormatDouble);
        Observe(ObserveDuration, "duration", LibMpvNative.FormatDouble);
        Observe(ObservePause, "pause", LibMpvNative.FormatFlag);
        Observe(ObserveVolume, "volume", LibMpvNative.FormatDouble);
        Observe(ObserveMute, "mute", LibMpvNative.FormatFlag);
        Observe(ObserveSpeed, "speed", LibMpvNative.FormatDouble);
        Observe(ObserveCacheTime, "demuxer-cache-time", LibMpvNative.FormatDouble);
        Observe(ObservePausedForCache, "paused-for-cache", LibMpvNative.FormatFlag);

        // Notification only: the list itself is read on the event thread, where the node tree can
        // be walked and freed in one place.
        Observe(ObserveTrackList, "track-list", LibMpvNative.FormatNone);

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "libmpv-events" };
        _eventThread.Start();

        Command("loadfile", request.MediaUrl.AbsoluteUri, "replace");
        Log.Info(Category, $"内置播放器开始播放 {request.MediaUrl.AbsoluteUri}");
    }

    private void Observe(ulong id, string name, int format)
    {
        var error = LibMpvNative.mpv_observe_property(context, id, name, format);
        if (error < 0) Log.Warn(Category, $"订阅 mpv 属性 {name} 失败：{LibMpvBackend.Describe(error)}");
    }

    // ---- event loop -------------------------------------------------------------

    /// <summary>
    /// The one thread allowed to read from the context. It keeps running past the end of the file
    /// so mpv's log tail and its shutdown still arrive; the loop is left when mpv says it is going
    /// away or when disposal asks for it, and only then may the context be destroyed.
    /// </summary>
    private void EventLoop()
    {
        while (!_leaveLoop)
        {
            var pointer = LibMpvNative.mpv_wait_event(context, 1.0);
            if (pointer == IntPtr.Zero) continue;

            var mpvEvent = Marshal.PtrToStructure<LibMpvNative.MpvEvent>(pointer);
            switch (mpvEvent.EventId)
            {
                case LibMpvNative.EventShutdown:
                    Log.Info(Category, "内置播放器已关闭");
                    Finish(_stopRequested ? PlaybackEndReason.Stopped : PlaybackEndReason.UserQuit, 0);
                    return;

                case LibMpvNative.EventEndFile:
                    var end = Marshal.PtrToStructure<LibMpvNative.MpvEventEndFile>(mpvEvent.Data);
                    Log.Debug(Category, $"内置播放器报告文件结束：reason={end.Reason} error={end.Error}");
                    Finish(Classify(end.Reason), end.Error);
                    break;

                case LibMpvNative.EventFileLoaded:
                    Publish(_status with { Loaded = true });
                    PublishTracks();
                    break;

                case LibMpvNative.EventPropertyChange:
                    OnPropertyChange(mpvEvent);
                    break;

                case LibMpvNative.EventLogMessage:
                    RememberLog(mpvEvent.Data);
                    break;
            }
        }
    }

    private PlaybackEndReason Classify(int endFileReason) => endFileReason switch
    {
        LibMpvNative.EndFileEof or LibMpvNative.EndFileRedirect => PlaybackEndReason.EndOfFile,
        LibMpvNative.EndFileError => PlaybackEndReason.Error,
        _ => _stopRequested ? PlaybackEndReason.Stopped : PlaybackEndReason.UserQuit
    };

    private void OnPropertyChange(LibMpvNative.MpvEvent mpvEvent)
    {
        if (mpvEvent.ReplyUserData == ObserveTrackList)
        {
            PublishTracks();
            return;
        }

        var property = Marshal.PtrToStructure<LibMpvNative.MpvEventProperty>(mpvEvent.Data);

        // A property that has become unavailable arrives with no data — time-pos does this the
        // moment the file ends. The last known value is kept: it is the position the server has
        // to be told about, and zeroing it would report the film as unwatched.
        if (property.Data == IntPtr.Zero) return;

        var previous = _status;
        var status = previous;
        switch (mpvEvent.ReplyUserData)
        {
            case ObserveTimePos:
                var position = ReadDouble(property);
                Remember(position);
                status = status with { Position = Math.Max(0, position) };
                break;
            case ObserveDuration:
                status = status with { Duration = Math.Max(0, ReadDouble(property)) };
                break;
            case ObservePause:
                status = status with { Paused = ReadFlag(property) };
                break;
            case ObserveVolume:
                status = status with { Volume = ReadDouble(property) };
                break;
            case ObserveMute:
                status = status with { Muted = ReadFlag(property) };
                break;
            case ObserveSpeed:
                status = status with { Speed = ReadDouble(property) };
                break;
            case ObserveCacheTime:
                status = status with { CacheEnd = Math.Max(0, ReadDouble(property)) };
                break;
            case ObservePausedForCache:
                status = status with { Buffering = ReadFlag(property) };
                break;
            default:
                return;
        }

        Publish(status);

        // After the snapshot, so a handler that asks for Status sees the pause it was told about.
        if (status.Paused != previous.Paused) PauseChanged?.Invoke(status.Paused);
    }

    private static double ReadDouble(LibMpvNative.MpvEventProperty property) =>
        property.Format == LibMpvNative.FormatDouble ? Marshal.PtrToStructure<double>(property.Data) : 0;

    private static bool ReadFlag(LibMpvNative.MpvEventProperty property) =>
        property.Format == LibMpvNative.FormatFlag && Marshal.ReadInt32(property.Data) != 0;

    /// <summary>
    /// Publishes a snapshot, but only when it differs in something the chrome draws. mpv notifies
    /// time-pos observers on every frame; forwarding all of them would marshal sixty UI updates a
    /// second to redraw the same pixels.
    /// </summary>
    private void Publish(PlayerStatus status)
    {
        if (!status.DiffersFrom(_status))
        {
            _status = status;
            return;
        }

        _status = status;
        StatusChanged?.Invoke(status);
    }

    private void PublishTracks()
    {
        if (TracksChanged is null) return;

        var tracks = ReadTrackList();
        if (tracks is { Count: > 0 }) TracksChanged.Invoke(tracks);
    }

    private void RememberLog(IntPtr data)
    {
        var message = Marshal.PtrToStructure<LibMpvNative.MpvEventLogMessage>(data);
        var text = Marshal.PtrToStringUTF8(message.Text);
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (_logTail)
        {
            if (_logTail.Count == LogTailLines) _logTail.Dequeue();
            _logTail.Enqueue(text);
        }

        Log.Debug(Category, $"mpv：{text.TrimEnd()}");
    }

    // ---- position --------------------------------------------------------------

    /// <summary>
    /// The observed position, with no call into mpv: the event thread has already been told, and
    /// asking again would only add a chance of reading a different moment than the one on screen.
    /// </summary>
    public Task<double?> GetPositionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(LastPosition);

    private void Remember(double seconds) =>
        Interlocked.Exchange(ref _lastPositionMs, (long)Math.Max(0, seconds * 1000));

    private double? LastPosition =>
        Interlocked.Read(ref _lastPositionMs) is var milliseconds && milliseconds < 0 ? null : milliseconds / 1000d;

    // ---- runtime control --------------------------------------------------------

    /// <summary>
    /// mpv accepts every value this client ever sets (volume, aid, sid, glsl-shaders…) in its
    /// string form, so one string call covers them all. Off the caller's thread: a volume drag
    /// would otherwise put a native call under mpv's core lock on the UI thread, once per mouse
    /// move. Silently does nothing once destroyed — the playback ended and there is no one to tell.
    /// </summary>
    public Task SetPropertyAsync(string name, object? value, CancellationToken cancellationToken)
    {
        if (value is null) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        var text = ToMpvString(value);
        return Task.Run(() => Guard(() =>
        {
            var error = LibMpvNative.mpv_set_property_string(context, name, text);
            if (error < 0) Log.Warn(Category, $"设置 mpv 属性 {name} 失败：{LibMpvBackend.Describe(error)}");
            return true;
        }), CancellationToken.None);
    }

    public Task CommandAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count == 0) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        var copy = arguments.ToArray();
        return Task.Run(() => Guard(() =>
        {
            Command(copy);
            return true;
        }), CancellationToken.None);
    }

    public Task<IReadOnlyList<MpvTrack>> GetTracksAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Guard(ReadTrackList) ?? []);
    }

    public Task<double?> GetNumberAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var value = Guard(() =>
            LibMpvNative.mpv_get_property_double(context, name, LibMpvNative.FormatDouble, out var number) < 0
                ? null
                : (double?)number);

        return Task.FromResult(value);
    }

    private static string ToMpvString(object value) => value switch
    {
        bool flag => flag ? "yes" : "no",
        double number => number.ToString("0.###", CultureInfo.InvariantCulture),
        int number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    /// <summary>
    /// Runs one native call under the API gate. Returns null when the handle is already
    /// destroyed, so callers never touch a freed mpv context.
    /// </summary>
    private T? Guard<T>(Func<T> work)
    {
        _apiGate.Wait();
        try
        {
            return _destroyed != 0 ? default : work();
        }
        finally
        {
            _apiGate.Release();
        }
    }

    private IReadOnlyList<MpvTrack>? ReadTrackList()
    {
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<LibMpvNative.MpvNode>());
        try
        {
            Marshal.StructureToPtr(new LibMpvNative.MpvNode(), buffer, fDeleteOld: false);

            if (LibMpvNative.mpv_get_property(context, "track-list", LibMpvNative.FormatNode, buffer) < 0) return [];
            var root = Marshal.PtrToStructure<LibMpvNative.MpvNode>(buffer);
            var tracks = new List<MpvTrack>();

            foreach (var item in NodeChildren(root))
            {
                var map = NodeMap(item);
                var type = NodeString(map, "type");
                if (type is not ("audio" or "sub" or "video")) continue;

                var id = NodeInt(map, "id");
                if (id is null) continue;

                tracks.Add(new MpvTrack(
                    id.Value,
                    type,
                    NodeString(map, "lang"),
                    NodeString(map, "title"),
                    NodeFlag(map, "default"),
                    NodeFlag(map, "selected")));
            }

            return tracks;
        }
        finally
        {
            LibMpvNative.mpv_free_node_contents(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IEnumerable<LibMpvNative.MpvNode> NodeChildren(LibMpvNative.MpvNode node)
    {
        if (node.Format is not (LibMpvNative.FormatNodeArray or LibMpvNative.FormatNodeMap)) return [];

        var list = Marshal.PtrToStructure<LibMpvNative.MpvNodeList>(node.Union);
        var values = new LibMpvNative.MpvNode[Math.Max(0, list.Num)];
        for (var index = 0; index < values.Length; index++)
            values[index] = Marshal.PtrToStructure<LibMpvNative.MpvNode>(list.Values + index * Marshal.SizeOf<LibMpvNative.MpvNode>());
        return values;
    }

    private static IReadOnlyDictionary<string, LibMpvNative.MpvNode> NodeMap(LibMpvNative.MpvNode node)
    {
        if (node.Format != LibMpvNative.FormatNodeMap) return new Dictionary<string, LibMpvNative.MpvNode>();

        var list = Marshal.PtrToStructure<LibMpvNative.MpvNodeList>(node.Union);
        var map = new Dictionary<string, LibMpvNative.MpvNode>(Math.Max(0, list.Num), StringComparer.Ordinal);
        for (var index = 0; index < list.Num; index++)
        {
            var key = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(list.Keys, index * IntPtr.Size));
            if (key is null) continue;
            var value = Marshal.PtrToStructure<LibMpvNative.MpvNode>(list.Values + index * Marshal.SizeOf<LibMpvNative.MpvNode>());
            if (value.Format is < 1 or > 8)
            {
                Log.Warn(Category, $"轨道字段异常：key={key} 格式={value.Format} 索引={index}/{list.Num}");
                continue;
            }
            map[key] = value;
        }

        return map;
    }

    private static string? NodeString(IReadOnlyDictionary<string, LibMpvNative.MpvNode> map, string key) =>
        map.TryGetValue(key, out var node) && node.Format == LibMpvNative.FormatString
            ? Marshal.PtrToStringUTF8(node.Union)
            : null;

    private static int? NodeInt(IReadOnlyDictionary<string, LibMpvNative.MpvNode> map, string key)
    {
        if (!map.TryGetValue(key, out var node)) return null;

        // The union holds the value itself for FORMAT_INT64/FORMAT_DOUBLE — not a pointer —
        // so it is read as raw bits rather than dereferenced.
        return node.Format switch
        {
            LibMpvNative.FormatInt64 => unchecked((int)node.Union.ToInt64()),
            LibMpvNative.FormatDouble => (int)Math.Round(BitConverter.Int64BitsToDouble(node.Union.ToInt64())),
            _ => null
        };
    }

    private static bool NodeFlag(IReadOnlyDictionary<string, LibMpvNative.MpvNode> map, string key)
    {
        // mpv's C union declares the flag as a 4-byte int; the other half of the union is stale
        // heap bytes, so only the low word may be looked at — ToInt32 would reject the garbage.
        return map.TryGetValue(key, out var node) && node.Format == LibMpvNative.FormatFlag && (node.Union.ToInt64() & 1) != 0;
    }

    // ---- exit -------------------------------------------------------------------

    private void Finish(PlaybackEndReason reason, int error)
    {
        var message = reason == PlaybackEndReason.Error ? DescribeFailure(error) : null;
        _exit.TrySetResult(new PlaybackExit(reason, LastPosition, error, message));
    }

    private string DescribeFailure(int error)
    {
        lock (_logTail)
        {
            var detail = _logTail.LastOrDefault(line => line.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                                                       || line.Contains("error", StringComparison.OrdinalIgnoreCase)
                                                       || line.Contains("Cannot", StringComparison.OrdinalIgnoreCase));
            return detail ?? $"内置播放器未能播放该文件（错误 {error}）";
        }
    }

    public async Task<PlaybackExit> WaitForExitAsync(CancellationToken cancellationToken) =>
        await _exit.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

    public async Task StopAsync()
    {
        _stopRequested = true;
        Guard(() =>
        {
            Command("quit");
            return true;
        });

        // Whether or not mpv answers, the playback is over as far as the client is concerned;
        // disposal is what actually tears the context down, in the right order.
        if (!await ExitWithinAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
            Finish(PlaybackEndReason.Stopped, 0);
    }

    public Task ShowMessageAsync(string text) =>
        CommandAsync(["show-text", text], CancellationToken.None);

    /// <summary>
    /// Tears the player down in the only order libmpv allows: ask it to quit, let the event
    /// thread leave the loop, and destroy the context once nothing is inside it any more. The
    /// old code destroyed while that thread was blocked in <c>mpv_wait_event</c> on the same
    /// context, which is a use-after-free the process only sometimes survived.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Guard(() =>
        {
            if (!_exit.Task.IsCompleted) Command("quit");
            return true;
        });

        _leaveLoop = true;
        JoinWorker(TimeSpan.FromSeconds(3));

        // Only reached with the event thread gone (or wedged, in which case the context is left
        // alone on purpose: leaking it is survivable, freeing it under a live reader is not).
        if (_eventThread is not { IsAlive: true }) Destroy();

        _apiGate.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>mpv_terminate_destroy is only safe to call once; whoever wins the swap owns it.</summary>
    private void Destroy()
    {
        _apiGate.Wait();
        try
        {
            if (Interlocked.Exchange(ref _destroyed, 1) != 0) return;
            LibMpvNative.mpv_terminate_destroy(context);
        }
        finally
        {
            _apiGate.Release();
        }
    }

    private void JoinWorker(TimeSpan timeout)
    {
        if (_eventThread is not { IsAlive: true }) return;
        if (_eventThread.Join(timeout)) return;

        // A wedged native call must not hang shutdown; the background thread then dies with
        // the process. That is what IsBackground is for.
        Log.Warn(Category, "内置播放器事件线程未及时退出，本次播放的 mpv 上下文将留给进程退出时回收");
    }

    private async Task<bool> ExitWithinAsync(TimeSpan timeout)
    {
        try
        {
            await _exit.Task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Sends a command as a null-terminated UTF-8 argument list.</summary>
    private void Command(params string[] arguments)
    {
        var array = new IntPtr[arguments.Length + 1];
        var allocations = new List<IntPtr>(arguments.Length);

        try
        {
            for (var index = 0; index < arguments.Length; index++)
            {
                var pointer = Marshal.StringToCoTaskMemUTF8(arguments[index]);
                allocations.Add(pointer);
                array[index] = pointer;
            }

            array[^1] = IntPtr.Zero;
            var error = LibMpvNative.mpv_command(context, array);
            if (error < 0) Log.Warn(Category, $"mpv 命令 {arguments[0]} 失败：{LibMpvBackend.Describe(error)}");
        }
        finally
        {
            foreach (var pointer in allocations) Marshal.FreeCoTaskMem(pointer);
        }
    }
}
