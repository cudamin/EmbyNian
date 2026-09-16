using System.Globalization;
using System.Runtime.InteropServices;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Mpv;

namespace EmbyNian.Playback;

/// <summary>
/// The in-process player: loads <c>libmpv-2.dll</c> and renders the video onto the surface behind
/// <paramref name="surfaceProvider"/> — the integrated pipeline's <c>SwapChainPanel</c>, or the
/// 独立播放 pipeline's underlay window, per launch. It is self-contained: the dll is loaded from an
/// explicit absolute path, and libmpv is <c>config=no</c> by default, so no mpv.conf, input.conf or
/// script from any mpv installation is ever read. Everything the player does comes from the settings
/// page by way of <see cref="PlaybackRequest.PlayerOptions"/>.
/// </summary>
public sealed class LibMpvBackend(MpvSettings settings, Func<IVideoSurface?> surfaceProvider) : IPlaybackBackend
{
    private const string Category = "mpv";
    private const string LibraryName = "libmpv-2.dll";

    public string DisplayName => "内置 libmpv";

    public string? Validate() =>
        ResolveLibMpv() is null
            ? $"找不到 {LibraryName}。已查找：{string.Join("、", Probes())}。把它放回程序目录即可。"
            : null;

    /// <summary>
    /// Where the dll may live, in order: the program folder, then next to the configured mpv.exe. The
    /// client ships its own copy, so the first probe is the one that answers on every normal install;
    /// the second only matters to someone who deleted it and has an mpv of their own.
    /// <para>
    /// There used to be a 设置 field ahead of both — 「libmpv-2.dll、附加参数貌似没什么用，删除」. Since the
    /// bundled copy always won the probe order anyway, the only thing the field could actually do was
    /// point at a *different* libmpv ABI, which breaks playback rather than fixing it.
    /// </para>
    /// </summary>
    private IEnumerable<string> Probes() => Probes(settings);

    private static IEnumerable<string> Probes(MpvSettings settings)
    {
        yield return Path.Combine(AppContext.BaseDirectory, LibraryName);

        if (Path.GetDirectoryName(settings.ExecutablePath) is { Length: > 0 } mpvFolder)
            yield return Path.Combine(mpvFolder, LibraryName);
    }

    private string? ResolveLibMpv() => Locate(settings);

    /// <summary>
    /// The same search, for a caller that has settings but no backend — <see cref="AudioDeviceCatalogue"/>,
    /// which opens a throwaway context of its own to enumerate audio devices. Named and shared rather than
    /// written out again there: two answers to 「where is libmpv」 is one too many.
    /// </summary>
    public static string? Locate(MpvSettings settings) => Probes(settings).FirstOrDefault(File.Exists);

    public Task<IPlaybackHandle> StartAsync(PlaybackRequest request, CancellationToken cancellationToken)
    {
        IntPtr context = IntPtr.Zero;
        try
        {
            var dllPath = ResolveLibMpv() ?? throw new InvalidOperationException(Validate() ?? $"找不到 {LibraryName}");

            // The dll is bound by absolute path rather than by name: a path from the settings is
            // otherwise validated here and then ignored by the loader, which only searches its
            // own directories. Those directories still matter for libmpv's one dependency that is not
            // part of Windows — vulkan-1.dll, a static import, so libmpv does not load at all without
            // it — which an ordinary build now puts beside the exe from assets/mpv-runtime.
            var dllFolder = Path.GetDirectoryName(dllPath) ?? "";
            LibMpvNative.UseLibrary(dllPath);
            LibMpvNative.EnsureDependencyDirectories(dllFolder, AppContext.BaseDirectory);

            context = LibMpvNative.mpv_create();
            if (context == IntPtr.Zero) throw new InvalidOperationException("mpv_create 返回了空句柄");

            PruneEvents(context);

            var surface = ApplyOptions(context, request);

            var error = LibMpvNative.mpv_initialize(context);
            if (error < 0) throw new InvalidOperationException($"mpv 初始化失败：{Describe(error)}");
            cancellationToken.ThrowIfCancellationRequested();

            LibMpvNative.mpv_request_log_messages(context, "warn");

            var handle = new LibMpvHandle(context, surface);
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

    /// <summary>
    /// Applies everything up to <c>mpv_initialize</c> and hands back the surface the playback will
    /// render into — the same one <see cref="LibMpvHandle"/> keeps serving for its whole life.
    /// <para>
    /// Two pipelines, branched on <see cref="IVideoSurface.WindowHandle"/>. 集成模式: the D3D11
    /// composition output — mpv creates no window of its own, renders into a composition swapchain
    /// the client attaches to its XAML panel, and the video becomes visual-tree content — same tree
    /// as the chrome, no airspace, nothing behind the island. 独立播放: the surface <em>is</em> a
    /// window — mpv takes it over as <c>wid</c> and keeps the swapchain to itself (auto resolves to
    /// window presentation): it creates it, sizes it and presents it, the client never sees a
    /// pointer, and no composition size is ever written because mpv measures the window itself.
    /// </para>
    /// <para>
    /// gpu-api is pinned to d3d11 either way — both output modes exist only on that backend. A user
    /// pick from 视频输出 lands later through PlayerOptions and overrides it, which is theirs to do.
    /// </para>
    /// </summary>
    private IVideoSurface ApplyOptions(IntPtr context, PlaybackRequest request)
    {
        var surface = surfaceProvider();
        if (surface is null) throw new InvalidOperationException("播放面板尚未就绪");

        // Nothing is said about config here: libmpv already defaults to config=no, so no mpv.conf,
        // input.conf or ~~/ path from any mpv installation is in play. Everything below, plus the
        // settings page's own options, is the whole of what this player is configured with.

        Set(context, "gpu-api", "d3d11");

        if (surface.WindowHandle != IntPtr.Zero)
        {
            // 独立播放：wid 用 typed 形式设——整数选项没有任何字符串解析的歧义。不显式 pin 输出档：
            // 这份 fork 的选项面是 auto|window|composition（上游同选项的第二档叫 flipping），档名
            // 在两边不一致，pin 错名字等于自找致命错；而 auto 在有 wid 时就是 window 档，缺省已经
            // 是独占呈现的那一个。交换链、尺寸、DPI 全归 mpv 自己量——客户端没有一条几何路径要喂。
            var handle = surface.WindowHandle.ToInt64();
            var widError = LibMpvNative.mpv_set_option(context, "wid", LibMpvNative.FormatInt64, ref handle);
            if (widError < 0)
                throw new InvalidOperationException($"内置播放器无法接管视频窗口（wid：{Describe(widError)}）");

            Log.Info(Category, $"独立播放：mpv 独占窗口 0x{handle:X}，自建自呈现交换链");
        }
        else
        {
            // 集成模式：D3D11 composition 输出。mpv creates no window of its own; it renders into a
            // composition swapchain the client attaches to its XAML panel, and the video becomes
            // visual-tree content — same tree as the chrome, no airspace, nothing behind the island.

            // Fatal rather than a warning: without it mpv falls back to window mode and opens a window
            // of its own — a silently different player. The bundled dll has had the option since the
            // 2025-07 upstream commit; an older one swapped in by hand is exactly what this catches.
            var modeError = LibMpvNative.mpv_set_option_string(context, "d3d11-output-mode", "composition");
            if (modeError < 0)
                throw new InvalidOperationException(
                    $"内置 {LibraryName} 不支持 d3d11 合成输出（{Describe(modeError)}）。"
                    + "需要 2025-07 之后的构建；换一份新的 libmpv-2.dll 放回程序目录即可。");

            // Sized by the client rather than by a window — composition mode has no window for mpv to
            // measure. The panel's size goes in here and again on every geometry change (the handle's
            // RefreshComposition), so a resize between launch and first frame cannot strand a stale one.
            var (width, height) = surface.Size;
            if (width > 0 && height > 0) Set(context, "d3d11-composition-size", $"{width}x{height}");
        }

        // The client draws its own player chrome over the video; mpv's on-screen controller
        // would duplicate it, and its keyboard bindings would act on top of the client's own —
        // one keypress seeking twice. Input belongs to exactly one of the two, and the client
        // is the one that can also drive the seek bar, so mpv's input is switched off whole.
        //
        // Nothing is said about osc here. The bundled libmpv-2.dll is built without Lua, so the
        // option does not exist in it at all and setting it only produced a warning on every start;
        // there is no on-screen controller to switch off, because there is no interpreter to run it.
        Set(context, "input-default-bindings", "no");
        Set(context, "input-vo-keyboard", "no");
        Set(context, "input-media-keys", "no");

        // The vo exists from initialize() rather than from the first decoded frame, whichever
        // pipeline carries it: a wid embeds from init, and composition has its swapchain ready —
        // so the client can attach the surface before any frame is due and the panel never flashes
        // empty on a slow network.
        Set(context, "force-window", "immediate");

        // Playback ending must not take the player down with it: the client decides when the
        // context goes away, which is what lets it report a final position and, one day, load
        // the next episode into the same window.
        Set(context, "idle", "yes");
        Set(context, "keep-open", "no");

        // hr-seek and the rest of the client's floor come in through PlayerOptions
        // (see MpvBaseline), which is shared with the external mpv.exe backend.

        // No panscan here, and that is a floor rather than a verdict. Filling the window by cropping the
        // picture is the wrong default: a mismatch between the window and the file costs the sides of the
        // frame, and edge-anchored subtitles go with them. The shell locks the window to the video's own
        // aspect instead (WM_SIZING), so dragging one edge moves the other and there is normally nothing
        // left over to letterbox or crop. The exception a viewer may want is 设置 → 视频输出 →
        // 宽于 16:9 的片源默认裁切填充 (VideoSettings.FillWideSources), which comes in through PlayerOptions
        // below and only fires on a source that really is letterboxed.

        // Belt and braces: libmpv already defaults to config=no, which blocks watch_later files along
        // with mpv.conf, so there is nothing here for mpv to resume from. The client owning the start
        // position is meant to be the rule, though, not a side effect of somebody else's default.
        Set(context, "resume-playback", "no");
        Set(context, "save-position-on-quit", "no");

        Set(context, "start", FormatSeconds(request.StartSeconds));

        if (request.AudioId is { } audioId) Set(context, "aid", audioId.ToString(CultureInfo.InvariantCulture));
        if (request.SubtitlesDisabled) Set(context, "sid", "no");
        else if (request.SubtitleId is { } subtitleId) Set(context, "sid", subtitleId.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(request.AudioLanguage)) Set(context, "alang", request.AudioLanguage);
        if (!string.IsNullOrWhiteSpace(request.SubtitleLanguage)) Set(context, "slang", request.SubtitleLanguage);

        // 字幕字体 arrives with the rest of 字幕外观 through PlayerOptions below — one writer for sub-font.
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

        // 视频输出 / 音频输出 settings, and the 着色器配置组 after them.
        foreach (var (name, value) in request.PlayerOptions) Set(context, name, value);

        return surface;
    }

    private static string FormatSeconds(double value) =>
        Math.Max(0, value).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Switches off every event type this client never reads — mpv.net's own opening move (its
    /// <c>MainPlayer.Init</c> disables the whole enum and keeps only what it consumes). An event
    /// the loop discards still costs a queue entry, a thread wakeup and a marshalled struct, and
    /// the loop reads exactly six: shutdown, log-message, end-file, file-loaded, property-change
    /// and queue-overflow. What remains — the three reply types (every call here is synchronous,
    /// so they were never fired anyway), start-file, client-message, the two reconfigs, seek,
    /// playback-restart and hook — was delivered only to be picked up and thrown away, so a seek
    /// burst or a resize storm queued nothing at all.
    /// <para>
    /// Deliberately before <c>mpv_initialize</c>, so nothing mpv does during startup queues either.
    /// mpv keeps a few event types for itself ("some events can't be disabled"), but a refusal is
    /// an error return and nothing more: the loop already ignores whatever still arrives, so every
    /// entry here degrades to no-op at worst. The deprecated ids (idle, tick) are not named — the
    /// bundled 0.41 build does not send them, and an older dll in the user's own care is exactly
    /// the case where today's arrive-and-be-ignored behaviour was already the answer.
    /// </para>
    /// </summary>
    private static void PruneEvents(IntPtr context)
    {
        Span<int> unused =
        [
            LibMpvNative.EventGetPropertyReply,
            LibMpvNative.EventSetPropertyReply,
            LibMpvNative.EventCommandReply,
            LibMpvNative.EventStartFile,
            LibMpvNative.EventClientMessage,
            LibMpvNative.EventVideoReconfig,
            LibMpvNative.EventAudioReconfig,
            LibMpvNative.EventSeek,
            LibMpvNative.EventPlaybackRestart,
            LibMpvNative.EventHook
        ];

        foreach (var eventId in unused)
        {
            var error = LibMpvNative.mpv_request_event(context, eventId, 0);
            if (error < 0) Log.Debug(Category, $"停订 mpv 事件 {eventId} 未被接受：{Describe(error)}");
        }
    }

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
/// <para>
/// The panel is served the same way: the <c>display-swapchain</c> property is observed without a
/// format, so a new swapchain announces itself on this thread, and the panel's geometry changes
/// arrive as an event of their own. Both converge on <see cref="RefreshComposition"/>. 独立播放
/// 走的是另一条路——mpv 独占窗口与交换链，这条事件线程只管状态，不管画面。
/// </para>
/// </summary>
internal sealed class LibMpvHandle(IntPtr context, IVideoSurface? surface) : IPlaybackHandle, IPlayerControl
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

    /// <summary>
    /// <c>display-swapchain</c>, observed without a format: 「tell me it changed, not what to」.
    /// The value is an mpv-owned pointer with no text form, so the event thread reads it typed and
    /// hands it straight to the panel — the same shape as the track list above it.
    /// </summary>
    private const ulong ObserveSwapchain = 10;

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

        // Same shape: the change arrives here, the pointer is read where it arrives. 集成管线专属：
        // 独立播放里没有 display-swapchain 可观察，也没有几何要喂——mpv 自己量窗口，自己的 resize
        // 钩子把子窗口跟上宿主的每一次 SetWindowPos。
        if (surface is not null && surface.WindowHandle == IntPtr.Zero)
        {
            Observe(ObserveSwapchain, "display-swapchain", LibMpvNative.FormatNone);

            surface.GeometryChanged += OnGeometryChanged;

            // First pass before the file loads: force-window=immediate may already have produced
            // a swapchain, and attaching it now is what keeps the panel black rather than
            // transparent while the stream opens.
            _ = Task.Run(RefreshComposition);
        }

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

                case LibMpvNative.EventQueueOverflow:
                    // mpv drops events when the client falls too far behind and says so with this
                    // one. Observers re-notify on their next change, but a stall otherwise leaves
                    // no trace — without it a dropped file-loaded would read as mpv going quiet
                    // for no reason.
                    Log.Warn(Category, "mpv 事件队列溢出，溢出期间的通知已丢失");
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

        // Both unformatted observers land here with no data attached — the value is read where
        // the notification is handled, which for the swapchain is a typed property read below.
        if (mpvEvent.ReplyUserData == ObserveSwapchain)
        {
            RefreshComposition();
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

    // ---- composition output -----------------------------------------------------

    /// <summary>
    /// One geometry pass: size, then swapchain. Composition only — the 独立播放 pipeline never gets
    /// here, because nothing subscribes to it. Runs on the event thread (a property change said the
    /// swapchain moved) or on a worker (the panel said its geometry moved); the worker path goes
    /// through the API gate like every other call from outside this thread.
    /// <para>
    /// mpv reads its output size from <c>d3d11-composition-size</c> — there is no window for it
    /// to measure — and the panel learns which swapchain to composite from
    /// <c>display-swapchain</c>. A pass with nothing new to say costs two native calls, which is
    /// not worth bookkeeping to avoid: mpv-winui answers its own geometry changes the same way.
    /// </para>
    /// </summary>
    private void RefreshComposition()
    {
        if (surface is null) return;

        Guard(() =>
        {
            var (width, height) = surface.Size;
            if (width > 0 && height > 0)
            {
                LibMpvNative.mpv_set_property_string(
                    context, "d3d11-composition-size", $"{width}x{height}");
            }

            var error = LibMpvNative.mpv_get_property_int64(
                context, "display-swapchain", LibMpvNative.FormatInt64, out var chain);

            // Zero on any refusal — vo not up yet, playback torn down — and the panel reads that
            // as "composite nothing", which is the honest answer in both cases.
            surface.AttachSwapChain(error >= 0 && chain != 0 ? new IntPtr(chain) : IntPtr.Zero);
            return true;
        });
    }

    private void OnGeometryChanged() => _ = Task.Run(RefreshComposition);

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

    public Task<bool> CommandAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count == 0) return Task.FromResult(true);
        cancellationToken.ThrowIfCancellationRequested();

        var copy = arguments.ToArray();
        return Task.Run(() => Guard(() => Command(copy)), CancellationToken.None);
    }

    public Task<IReadOnlyList<MpvTrack>> GetTracksAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Guard(ReadTrackList) ?? []);
    }

    /// <summary>
    /// mpv's own chapter marks in one node read — the same shape <see cref="ReadTrackList"/> takes.
    /// The count probe stays the caller's readiness gate; this is the call that replaces walking
    /// the list two questions per chapter.
    /// </summary>
    public Task<IReadOnlyList<SkipChapter>> GetChaptersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Guard(ReadChapterList) ?? []);
    }

    private IReadOnlyList<SkipChapter>? ReadChapterList() =>
        LibMpvNodes.Read<IReadOnlyList<SkipChapter>?>(context, "chapter-list", root =>
        {
            var chapters = new List<SkipChapter>();

            foreach (var item in LibMpvNodes.Children(root))
            {
                var map = LibMpvNodes.Map(item);

                // A chapter without a time cannot be placed anywhere and is skipped whole; the
                // title is optional, and most chapters of a typical file have none.
                if (LibMpvNodes.Double(map, "time") is not { } start) continue;
                chapters.Add(new SkipChapter(start, LibMpvNodes.String(map, "title")));
            }

            return chapters;
        }, null);

    public Task<double?> GetNumberAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var value = Guard(() =>
            LibMpvNative.mpv_get_property_double(context, name, LibMpvNative.FormatDouble, out var number) < 0
                ? null
                : (double?)number);

        return Task.FromResult(value);
    }

    public Task<string?> GetTextAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var value = Guard(() =>
        {
            // mpv hands back a copy it allocated, so the string has to be marshalled out and the
            // copy released before the gate is dropped — nothing else will ever free it.
            var pointer = LibMpvNative.mpv_get_property_string(context, name);
            if (pointer == IntPtr.Zero) return null;

            try
            {
                return Marshal.PtrToStringUTF8(pointer);
            }
            finally
            {
                LibMpvNative.mpv_free(pointer);
            }
        });

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

    private IReadOnlyList<MpvTrack>? ReadTrackList() =>
        LibMpvNodes.Read<IReadOnlyList<MpvTrack>?>(context, "track-list", root =>
        {
            var tracks = new List<MpvTrack>();

            foreach (var item in LibMpvNodes.Children(root))
            {
                var map = LibMpvNodes.Map(item);
                var type = LibMpvNodes.String(map, "type");
                if (type is not ("audio" or "sub" or "video")) continue;

                var id = LibMpvNodes.Int(map, "id");
                if (id is null) continue;

                tracks.Add(new MpvTrack(
                    id.Value,
                    type,
                    LibMpvNodes.String(map, "lang"),
                    LibMpvNodes.String(map, "title"),
                    LibMpvNodes.Flag(map, "default"),
                    LibMpvNodes.Flag(map, "selected"))
                {
                    // All optional: a demuxer that cannot answer simply leaves the key out, and the
                    // picker then shows one fewer thing about the track rather than nothing at all.
                    Codec = LibMpvNodes.String(map, "codec"),
                    Channels = LibMpvNodes.String(map, "demux-channels"),
                    ChannelCount = LibMpvNodes.Int(map, "demux-channel-count") ?? 0,
                    SampleRate = LibMpvNodes.Int(map, "demux-samplerate") ?? 0,
                    BitRate = LibMpvNodes.Int(map, "demux-bitrate") ?? 0,
                    Forced = LibMpvNodes.Flag(map, "forced"),
                    External = LibMpvNodes.Flag(map, "external"),
                    Image = LibMpvNodes.Flag(map, "image"),
                    HearingImpaired = LibMpvNodes.Flag(map, "hearing-impaired")
                });
            }

            return tracks;
        }, []);

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

        // Before the context is destroyed: the surface must not keep compositing a swapchain whose
        // owner is about to go away. The hop to the UI thread is asynchronous, and doing it here —
        // while mpv is still alive and the property can no longer produce a new chain — is the
        // latest point where the order is guaranteed. 独立播放的窗口没有挂过任何交换链，摘除那一步
        // 不适用于它（真挂错了 AttachSwapChain 自己会喊）。
        if (surface is not null)
        {
            surface.GeometryChanged -= OnGeometryChanged;
            if (surface.WindowHandle == IntPtr.Zero) surface.AttachSwapChain(IntPtr.Zero);
        }

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
    private bool Command(params string[] arguments)
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

            // 返回值交出去，不只是写进日志：调用方要拿它决定「按了截图之后到底要不要说已保存」。
            return error >= 0;
        }
        finally
        {
            foreach (var pointer in allocations) Marshal.FreeCoTaskMem(pointer);
        }
    }
}
