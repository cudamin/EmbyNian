using System.Globalization;
using System.Runtime.InteropServices;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Mpv;

namespace EmbyNian.Playback;

/// <summary>
/// The in-process player: loads <c>libmpv-2.dll</c> and renders per the settings' pipeline — the
/// integrated composition output onto the shell's SpriteVisual, or mpv's own top-level window for the
/// 独立播放 pipeline. It is self-contained: the dll is loaded from an explicit absolute path, and
/// libmpv is <c>config=no</c> by default, so no mpv.conf, input.conf or script from any mpv
/// installation is ever read. Everything the player does comes from the settings page by way of
/// <see cref="PlaybackRequest.PlayerOptions"/>.
/// </summary>
public sealed class LibMpvBackend(MpvSettings settings, Func<IVideoSurface?> surfaceProvider, Func<bool>? autoFullscreen = null) : IPlaybackBackend
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

            // 独占模式才装配视频窗的 Lua UI（uosc 嵌入版）：mpv 在这一档自建顶层窗口，屏幕控件
            // 只能画在它自己的 OSD 层里。集成模式的画面合成进 XAML 树，控件是 shell 的事，一概不装。
            // 装箱缺失降级为「没有屏幕控件的独占播放」，只记日志不拦起播。
            var uiOptions = settings.Pipeline == VideoPipelineKind.Standalone
                ? MpvUi.Bootstrap(AppContext.BaseDirectory)
                : null;
            if (uiOptions is null && settings.Pipeline == VideoPipelineKind.Standalone)
                Log.Warn(Category, $"独占模式未找到 Lua UI 装箱（{MpvUi.ScriptRelativePath}），本次播放没有屏幕控件");

            // ClientMessage 只在 Lua UI 在场时才值得收 —— 它是脚本与宿主的唯一通道。
            PruneEvents(context, keepClientMessage: uiOptions is not null);

            var (surface, pipelineOptions) = ApplyOptions(context, request, uiOptions);

            var error = LibMpvNative.mpv_initialize(context);
            if (error < 0) throw new InvalidOperationException($"mpv 初始化失败：{Describe(error)}");
            cancellationToken.ThrowIfCancellationRequested();

            LibMpvNative.mpv_request_log_messages(context, "warn");

            // Lua UI 只装一次，装的是 MpvUi.Build 交给 mpv 的 `scripts` 选项 —— 上面那一串选项在
            // mpv_initialize 之前就生效，脚本在 loadfile 之前已经把属性观察架好，文件一开控件就有
            // 数据。这里**不再**另发一条 load-script：那样的装载路径会把脚本命名成 main（按文件名），
            // 于是同一份 uosc 装两次（日志里两条「视频窗 Lua UI 已就绪」即此），而 main 那份的绑定
            // 名字是 main/…、控制条上的 script-binding uosc/… 调不到它，白跑一套渲染与观察、还把
            // 每条宿主消息的回声翻一倍。2026-09-19 撤。

            // 换片快路的签名：管线、管线必需项、Lua UI 项，加上这一票选项表的基线部分（去掉末尾的
            // 着色器链 —— 链是运行期能改的，换片时重写，不该把快路挡在门外）。判断在 Mpv.InlineSwitch：
            // 逐项相等才允许同一个实例换片，因为 mpv 的启动选项在 initialize 之后改不动。
            // 下一次的签名按当时的设置现算（管线换了就自然对不上）；独立管线的选项表不含画布尺寸，
            // 所以这里不传 size —— 集成管线本来就不走这条快路（surface 非空）。
            var signature = new PlaybackLaunchSignature(
                settings.Pipeline,
                pipelineOptions,
                [.. uiOptions ?? []],
                Baseline(request.PlayerOptions, request.ShaderOptionCount));

            var handle = new LibMpvHandle(context, surface, signature, next => new PlaybackLaunchSignature(
                settings.Pipeline,
                [.. LibMpvPipelinePolicy.Build(settings.Pipeline, next.PlayerOptions)
                    .Select(option => new KeyValuePair<string, string>(option.Name, option.Value))],
                [.. uiOptions ?? []],
                Baseline(next.PlayerOptions, next.ShaderOptionCount)));
            handle.Start(request);
            context = IntPtr.Zero; // ownership moved to the handle
            Log.Info(Category, uiOptions is null
                ? $"内置播放器已就绪（{dllPath}）"
                : $"内置播放器已就绪，视频窗 Lua UI 已装载（{dllPath}）");
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
    /// Applies the request followed by the pipeline's non-overridable rendering contract.
    /// Integrated output serves the shell's composition surface; standalone output owns a
    /// native D3D11 window, with exclusive fullscreen requested when that window goes fullscreen.
    /// A window by itself is not evidence of exclusive presentation. The selected surface (or
    /// null) stays with this session even if settings change during playback.
    /// <para>
    /// 第二个返回值是那串管线必需项本身：换片快路要拿它当签名的一部分（见 <see cref="Mpv.InlineSwitch"/>），
    /// 而它在这里就已经算好了 —— 再算一遍等于把「这一串是什么」写两处。
    /// </para>
    /// </summary>
    private (IVideoSurface? Surface, IReadOnlyList<KeyValuePair<string, string>> PipelineOptions) ApplyOptions(
        IntPtr context, PlaybackRequest request, IReadOnlyList<KeyValuePair<string, string>>? uiOptions)
    {
        // Nothing is said about config here: libmpv already defaults to config=no, so no mpv.conf,
        // input.conf or ~~/ path from any mpv installation is in play. Everything below, plus the
        // settings page's own options, is the whole of what this player is configured with.

        // Snapshot once: settings may change while a session starts. Standalone must never
        // call the provider, not even to discover that the shell has no usable surface.
        var pipeline = settings.Pipeline;
        var surface = SelectSurface(pipeline, surfaceProvider);

        // youtube-dl 钩子整个停掉。这里的媒体 URL 只有两种形状——Emby 的直连流和本机文件——ytdl 对
        // 它们没有任何用处，只有代价：每次起播先让 [generic] 提取器去抓一遍网页（两秒的延迟），而且
        // 那一趟不带 --http-header-fields（mpv 不把客户端的请求头传给 ytdl），服务器吃一个 401，
        // 「播放失败」的提示就被这条假错误顶掉——2026-09-16 用户报的 401 就是它：真死因是服务器上
        // 文件没了（404），提示却是 ytdl 的 Unauthorized。关掉之后，失败信息回到 mpv 自己那条诚实的。
        Set(context, "ytdl", "no");

        // Input ownership and force-window are part of the final pipeline contract below:
        // native window keys belong to mpv, integrated keys and global media keys to the shell.
        // mpv 的 osc 在独占模式由装箱的 uosc 嵌入版顶替（见 MpvUi.Bootstrap 的选项），此处不设 ——
        // Lua UI 不在场（集成模式/装箱缺失）时 libmpv 的 osc 本来就默认关闭。

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

        // 独占模式的 Lua UI 装配选项（osc=no、mpv 自带 OSD 条关闭、无边框、字体目录、脚本路径）。
        // 放在管线契约之前：契约是必需项，永远后写后赢。
        foreach (var option in uiOptions ?? []) Set(context, option.Key, option.Value);

        // 「开始播放后自动全屏」在独占模式的落地：起播即全屏，不先冒一个小窗再跳
        // （mpv 自建窗口没有宿主动画可借，直接以全屏出生就是最接近集成模式的形态）。
        // 边框永远去掉——标题与窗口按钮归 uosc 顶栏，这一位不跟随设置。
        if (pipeline == VideoPipelineKind.Standalone && (autoFullscreen?.Invoke() ?? false))
            Set(context, "fullscreen", "yes");

        // Ordinary options keep their order; pipeline-critical options are filtered and pinned
        // AFTER them, so even the default gpu-api=vulkan cannot replace D3D11. Required failures
        // abort before initialize/loadfile instead of falling back to a different presentation path.
        var contract = LibMpvPipelinePolicy.Build(pipeline, request.PlayerOptions, surface?.Size ?? default);
        var pipelineOptions = new List<KeyValuePair<string, string>>(contract.Count);
        foreach (var option in contract)
        {
            pipelineOptions.Add(new(option.Name, option.Value));

            var error = LibMpvNative.mpv_set_option_string(context, option.Name, option.Value);
            option.EnsureAccepted(error);
            if (error < 0) Log.Warn(Category, $"设置 mpv 选项 {option.Name} 失败：{Describe(error)}");
        }

        Log.Info(Category, surface is null
            ? "独立播放：mpv D3D11 原生 window，已请求 d3d11-exclusive-fs=yes（进入全屏时生效，非独占状态证明）；客户端不介入几何"
            : "集成播放：D3D11 composition，独占全屏关闭，画面由宿主合成");
        return (surface, pipelineOptions);
    }

    internal static IVideoSurface? SelectSurface(VideoPipelineKind pipeline, Func<IVideoSurface?> provider) =>
        LibMpvPipelinePolicy.RequiresSurface(pipeline)
            ? provider() ?? throw new InvalidOperationException("播放面板尚未就绪")
            : null;

    private static string FormatSeconds(double value) =>
        Math.Max(0, value).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Switches off every event type this client never reads — mpv.net's own opening move (its
    /// <c>MainPlayer.Init</c> disables the whole enum and keeps only what it consumes). An event
    /// the loop discards still costs a queue entry, a thread wakeup and a marshalled struct, and
    /// the loop reads exactly six: shutdown, log-message, end-file, file-loaded, property-change
    /// and queue-overflow. What remains — the three reply types (every call here is synchronous,
    /// so they were never fired anyway), start-file, the two reconfigs, seek,
    /// playback-restart and hook — was delivered only to be picked up and thrown away, so a seek
    /// burst or a resize storm queued nothing at all.
    /// <para>
    /// ClientMessage（Lua 脚本的 script-message）是这条规则的唯一条件豁免：独占模式装载 uosc 时，
    /// 它是脚本向宿主报「已就绪」和送换集请求的唯一通道，必须保留；其余播放照旧停订。
    /// </para>
    /// <para>
    /// Deliberately before <c>mpv_initialize</c>, so nothing mpv does during startup queues either.
    /// mpv keeps a few event types for itself ("some events can't be disabled"), but a refusal is
    /// an error return and nothing more: the loop already ignores whatever still arrives, so every
    /// entry here degrades to no-op at worst. The deprecated ids (idle, tick) are not named — the
    /// bundled 0.41 build does not send them, and an older dll in the user's own care is exactly
    /// the case where today's arrive-and-be-ignored behaviour was already the answer.
    /// </para>
    /// </summary>
    private static void PruneEvents(IntPtr context, bool keepClientMessage)
    {
        Span<int> unused =
        [
            LibMpvNative.EventGetPropertyReply,
            LibMpvNative.EventSetPropertyReply,
            LibMpvNative.EventCommandReply,
            LibMpvNative.EventStartFile,
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

        if (!keepClientMessage)
        {
            var error = LibMpvNative.mpv_request_event(context, LibMpvNative.EventClientMessage, 0);
            if (error < 0) Log.Debug(Category, $"停订 mpv 事件 {LibMpvNative.EventClientMessage} 未被接受：{Describe(error)}");
        }
    }

    /// <summary>
    /// 票里那份选项表去掉末尾那条着色器链之后的基线。链有多少项是票自己带着的
    /// （<see cref="PlaybackRequest.ShaderOptionCount"/>），所以这里不用去猜是哪一档。
    /// internal static：换片快路的签名只认基线这一节，测试看得见。
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> Baseline(
        IReadOnlyList<KeyValuePair<string, string>> options,
        int chainOptions)
    {
        var keep = Math.Max(0, options.Count - Math.Max(0, chainOptions));
        return keep == options.Count ? options : [.. options.Take(keep)];
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
/// arrive as an event of their own. Both converge on <see cref="RefreshComposition"/>. The surface
/// is an integrated-pipeline member only: 独立播放 has no panel to serve — mpv draws into its own
/// top-level window — so it arrives here with a null surface and none of this machinery arms.
/// </para>
/// <para>
/// 换片快路（<see cref="SwapToAsync"/>，2026-09-19）：独占模式的选集与连播在本实例上换源，
/// 不关窗口、不重装 Lua UI。签名的比对与要写哪些属性都在 <see cref="Mpv.InlineSwitch"/> 里。
/// </para>
/// </summary>
internal sealed class LibMpvHandle(
    IntPtr context,
    IVideoSurface? surface,
    PlaybackLaunchSignature? signature,
    Func<PlaybackRequest, PlaybackLaunchSignature>? signatureFor) : IPlaybackHandle, IPlayerControl, IPlayerHostMessages
{
    private const string Category = "mpv";
    private const int LogTailLines = 40;
    private const int ClientMessageMaxArgs = 32;

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

    /// <summary>
    /// <c>vo-configured</c>，只在独占模式订阅：出生时 <c>force-window=no</c>（不冒黑框），
    /// 这一位翻真＝窗口带着画面立起来了，此刻把 force-window 改回 yes，窗口从此跨 EOF 与跨换片都活着。
    /// </summary>
    private const ulong ObserveVoConfigured = 11;

    private readonly Queue<string> _logTail = new(LogTailLines);

    /// <summary>
    /// 这一跑的收场信号。换片快路会把它换成新的一只（见 <see cref="SwapToAsync"/>）：旧的被
    /// <see cref="HandOver"/> 解开，新的等下一集结束 —— 监视读的始终是「当前这一跑」的那只。
    /// </summary>
    private TaskCompletionSource<PlaybackExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Serialises control/property calls and composition handoff against destruction. The event
    /// reader is joined separately before destruction; queued callers then see the destroyed
    /// flag and walk away instead of touching freed memory.</summary>
    private readonly LibMpvLifetimeGate _apiGate = new();

    private Thread? _eventThread;
    private long _lastPositionMs = -1;
    private volatile bool _stopRequested;
    private volatile bool _leaveLoop;

    /// <summary>换片交接（见 <see cref="HandOver"/>）—— 收尾据此不拆实例。</summary>
    private volatile bool _handedOver;

    /// <summary>刚发过一条 <c>loadfile</c> 换片：事件线程在 file-loaded 那一拍清掉上一集的读数。</summary>
    private volatile bool _swapping;

    /// <summary>独占模式的窗口已经立起来了（`vo-configured`），`force-window` 已改回 yes —— 只做一次。</summary>
    private volatile bool _windowHeld;

    /// <summary>
    /// 「跟着一集走」那批属性的出厂值（<c>option-info/&lt;名字&gt;/default-value</c>），起播时读一次。
    /// 换片时哪个名字不在新票的选项表里就按它拨回去 —— 见 <see cref="Mpv.InlineSwitch.FilmScoped"/>。
    /// </summary>
    private IReadOnlyDictionary<string, string> _filmDefaults = new Dictionary<string, string>(StringComparer.Ordinal);


    /// <summary>Only ever touched by the event thread, then published through <see cref="Status"/>.</summary>
    private PlayerStatus _status = new();

    public bool HasControlChannel => true;

    public bool? PictureInHostWindow => surface is not null;

    public bool IsPaused => _status.Paused;

    public PlayerStatus Status => _status;

    public event Action<bool>? PauseChanged;

    public event Action<PlayerStatus>? StatusChanged;

    public event Action<IReadOnlyList<MpvTrack>>? TracksChanged;

    /// <summary>视频窗 Lua UI 发来的 <c>embynian-*</c> 消息；只在事件线程上发，订阅方负责调度。</summary>
    public event Action<string, string>? HostMessageReceived;

    /// <summary>
    /// uosc 的握手是否已到（<c>embynian-ready</c>）。脚本先于宿主订阅启动，这一位把迟到错过的
    /// 握手留成可读的状态，而不是让「没收到」永远无法与「没订阅」区分。
    /// </summary>
    public bool VideoWindowUiReady { get; private set; }

    /// <summary>Starts the event thread, subscribes to the state the chrome needs, then loads the file.</summary>
    internal void Start(PlaybackRequest request)
    {
        // 换片要用的「出厂值」在这一拍读：事件线程还没起来，没有任何并发，而且此刻还没有任何一集
        // 改过这些属性（见 InlineSwitch.FilmScoped 的说明）。
        _filmDefaults = ReadFilmDefaults();

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
        // 独立播放根本没有 surface（mpv 画在自己的顶层窗口里），这里什么都不订阅，也没有几何要喂。
        if (surface is not null)
        {
            Observe(ObserveSwapchain, "display-swapchain", LibMpvNative.FormatNone);

            surface.GeometryChanged += OnGeometryChanged;

            // First pass before the file loads: force-window=immediate may already have produced
            // a swapchain, and attaching it now is what keeps the panel black rather than
            // transparent while the stream opens.
            _ = Task.Run(RefreshComposition);
        }
        else
        {
            // 独占模式：出生时 force-window=no，窗口要等画面，所以「按住窗口」得等 vo-configured
            // 翻真（见 OnPropertyChange）。集成模式不需要——它的 force-window 一直是 immediate。
            Observe(ObserveVoConfigured, "vo-configured", LibMpvNative.FormatFlag);
        }

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "libmpv-events" };
        _eventThread.Start();

        Command("loadfile", request.MediaUrl.AbsoluteUri, "replace");
        Log.Info(Category, $"内置播放器开始播放 {request.MediaUrl.AbsoluteUri}");
    }

    // ---- taking the next file in the same window ---------------------------------
    //
    // 独占模式的选集与连播走的这条（2026-09-19 用户令「换集不要每次都关窗重开」）。判断在
    // Mpv.InlineSwitch 里（纯函数），这里只管执行：先把手头这一跑叫醒（HandOver），再在这一拍把
    // 下一票的运行期部分写下去、发一条 loadfile。窗口、全屏状态、整套 uosc 都不动。

    /// <summary>快路只对独占模式的内置会话开放：集成管线的画面挂在宿主合成树上，换源要动的几何不止一份。</summary>
    private bool Swapable => surface is null && signature is not null && signatureFor is not null;

    public bool WasHandedOver => _handedOver;

    public bool CanSwapTo(PlaybackRequest request) =>
        Swapable && InlineSwitch.SameSignature(signature!, signatureFor!(request));

    /// <summary>
    /// 叫醒正等着这一跑的监视（它去发「停止」与最后位置的上报），但**不 quit** —— 这正是「不关窗」
    /// 与「关窗重开」的分界。mpv 还活着、还是 idle，下一集的 loadfile 紧接着就来。
    /// </summary>
    public void HandOver()
    {
        _handedOver = true;
        _exit.TrySetResult(new PlaybackExit(PlaybackEndReason.Stopped, LastPosition, 0, null));
    }

    public Task<bool> SwapToAsync(PlaybackRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Swapable) return Task.FromResult(false);

        // 三处「不接」都要说得出理由：签名对不上（启动配置变了，事后改也无效）、已经在收场、
        // 命令被拒。前两处是判断，第三处是 mpv 的答复 —— 都退回「停掉重开」那条老路。
        if (!InlineSwitch.SameSignature(signature!, signatureFor!(request)))
        {
            Log.Info(Category, "换片快路让位：这一次的启动配置与正在跑的那份不同（管线或选项变了）");
            return Task.FromResult(false);
        }

        if (_stopRequested || _exit.Task.IsCompleted)
        {
            Log.Info(Category, "换片快路让位：这个实例已经在收场");
            return Task.FromResult(false);
        }

        foreach (var (name, value) in InlineSwitch.FilmScoped(_filmDefaults, request)) Write(name, value);
        foreach (var (name, value) in InlineSwitch.PerFile(request)) Write(name, value);

        // 上一集的位置当场作废：这一票要是打不开（候选版本还有下一版要试），收尾报的也不该是别人的位置。
        // 上一跑的最后位置已经在 HandOver 那一拍读走了，这里清掉不影响它的上报。
        Interlocked.Exchange(ref _lastPositionMs, -1);

        // 新一跑的收场信号；「这次 loadfile 是换片」的记号给事件线程用（忽略被替掉那份的 end-file、
        // 在 start-file 清掉上一集的读数）；交接旗翻回来，这一跑收尾时该拆就得拆（它是上一跑的事）。
        _exit = new TaskCompletionSource<PlaybackExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        _swapping = true;
        _handedOver = false;

        if (!Command("loadfile", request.MediaUrl.AbsoluteUri, "replace"))
        {
            _swapping = false;
            Log.Warn(Category, "换片快路让位：loadfile 未被接受");
            return Task.FromResult(false);
        }

        Log.Info(Category, $"独占换片：不关窗口，同一个 mpv 换源 —— {request.Title}");
        return Task.FromResult(true);
    }

    /// <summary>
    /// 问 mpv 要这批「跟着一集走」的属性的出厂默认值（<c>option-info/&lt;名字&gt;/default-value</c>）。
    /// 起播时读一次，在事件线程起来之前 —— 那一拍没有任何并发。
    /// </summary>
    private IReadOnlyDictionary<string, string> ReadFilmDefaults()
    {
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in InlineSwitch.PerFilmNames)
        {
            if (ReadText($"option-info/{name}/default-value") is { Length: > 0 } value) defaults[name] = value;
        }

        return defaults;
    }

    /// <summary>一条属性的文本值（同步，走 API 闸门）；读不到就是 null。</summary>
    private string? ReadText(string name) => Guard(() =>
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

    /// <summary>写一条属性，失败记一条日志（不拦下换片：写不进去的最坏结果是这一集沿用上一集的某项设置）。</summary>
    private void Write(string name, string value) => Guard(() =>
    {
        var error = LibMpvNative.mpv_set_property_string(context, name, value);
        if (error < 0) Log.Warn(Category, $"写 mpv 属性 {name} 失败：{LibMpvBackend.Describe(error)}");
        return true;
    });

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

                    // 换源那一下，mpv 会替**被换掉的那个文件**报一条 end-file，然后才 start-file（实测
                    // 2026-09-19：两次换源两条 end-file）。照单全收的话新一集的监视会当场以为播完了，
                    // 界面立刻退回浏览页 —— 所以从发出 loadfile 到新文件 start-file 之间，这条不算数。
                    if (_swapping)
                    {
                        Log.Debug(Category, "（这条属于被换掉的那个文件，不计入本次播放）");
                        break;
                    }

                    Finish(Classify(end.Reason), end.Error);
                    break;

                case LibMpvNative.EventStartFile:
                    // 新文件的装载从此开始：换片那一刻起，「这一跑」的读数就该重新算了（位置尤其要紧 ——
                    // 留着上一集的位置，这一集被停掉时上报的「看到哪儿」就是别人的）。
                    if (_swapping)
                    {
                        _swapping = false;
                        Interlocked.Exchange(ref _lastPositionMs, -1);
                        _status = _status with { Position = -1, Duration = 0, CacheEnd = 0, Loaded = false };
                        Log.Info(Category, "独占换片：同一个视频窗已换上新片源（窗口与 Lua UI 都没动）");
                    }
                    break;

                case LibMpvNative.EventFileLoaded:
                    // 只有声音的文件（没有视频轨）永远不会让 vo 立起来，force-window=no 之下就一个窗口都
                    // 没有 —— 那种片子还是得给扇窗，不然连 uosc 的控件都无处可画。视频文件走 vo-configured
                    // 那条路（见 OnPropertyChange），这里是给「没有画面可等」的情形兜底。
                    if (surface is null && !_windowHeld && (ReadText("vid") is null or "no"))
                    {
                        _windowHeld = true;
                        Write("force-window", "yes");
                        Log.Debug(Category, "这一版没有视频轨，独占窗口提前立起（force-window=yes）");
                    }

                    Publish(_status with { Loaded = true });
                    PublishTracks();
                    break;

                case LibMpvNative.EventPropertyChange:
                    OnPropertyChange(mpvEvent);
                    break;

                case LibMpvNative.EventClientMessage:
                    OnClientMessage(mpvEvent.Data);
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

        // 独占模式：窗口带着画面立起来了 —— 出生时是 force-window=no（不冒黑框），这一刻改回 yes，
        // 窗口此后跨 EOF、跨换片都活着（运行期可改，2026-09-19 实测：改这一下窗口不闪，且换源与播完
        // 之后窗口都还在）。只做一次；文件换掉时 vo-configured 可能再翻一遍，不必重复写。
        if (mpvEvent.ReplyUserData == ObserveVoConfigured)
        {
            var configured = Marshal.PtrToStructure<LibMpvNative.MpvEventProperty>(mpvEvent.Data);
            if (configured.Data != IntPtr.Zero && ReadFlag(configured) && !_windowHeld)
            {
                _windowHeld = true;
                Write("force-window", "yes");
                Log.Debug(Category, "独占窗口已立起：force-window 改回 yes（播完与换片都不再收窗）");
            }

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
    /// Lua 脚本的一条 <c>script-message</c>。参数指针在下一次 <c>mpv_wait_event</c> 前失效，
    /// 所以先把全部字符串复制成托管值，再谈解析与分发；解析按 <see cref="VideoWindowContract"/>
    /// 收窄 —— 不是宿主的消息在这里就丢掉，不上传不打扰。
    /// </summary>
    private void OnClientMessage(IntPtr data)
    {
        var message = Marshal.PtrToStructure<LibMpvNative.MpvEventClientMessage>(data);
        if (message.Count is < 1 or > ClientMessageMaxArgs) return;

        var arguments = new List<string>(message.Count);
        for (var index = 0; index < message.Count; index++)
        {
            var pointer = Marshal.ReadIntPtr(message.Args, index * Marshal.SizeOf<IntPtr>());
            if (pointer == IntPtr.Zero) return;
            arguments.Add(Marshal.PtrToStringUTF8(pointer) ?? "");
        }

        if (VideoWindowContract.Parse(arguments) is not { } parsed) return;

        if (parsed.Key == VideoWindowContract.Ready) VideoWindowUiReady = true;

        if (parsed.Key == VideoWindowContract.Ready)
            Log.Info(Category, $"视频窗 Lua UI 已就绪（uosc {parsed.Value}）");
        else
            Log.Debug(Category, $"视频窗消息：{parsed.Key} {parsed.Value}");

        HostMessageReceived?.Invoke(parsed.Key, parsed.Value);
    }

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

        _apiGate.Run(() =>
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
        }, composition: true);
    }

    private void StopComposition() => _apiGate.StopComposition(() =>
    {
        if (surface is null) return;
        surface.GeometryChanged -= OnGeometryChanged;
        surface.AttachSwapChain(IntPtr.Zero);
    });

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
    /// Runs one native call under the API gate. Returns the default value when the handle is
    /// already destroyed, so callers never touch a freed mpv context.
    /// </summary>
    private T? Guard<T>(Func<T> work) => _apiGate.Run(work);

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
        StopComposition();
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
        StopComposition();
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
        // Serialize detachment with the entire refresh (including its borrowed-pointer handoff).
        // Already queued geometry work cannot attach again after this boundary. The shell owns
        // AddRef and stale-dispatch rejection for work it sends to the UI thread.
        StopComposition();
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

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>mpv_terminate_destroy is only safe to call once, after the event thread exits.</summary>
    private void Destroy() => _apiGate.Destroy(() => LibMpvNative.mpv_terminate_destroy(context));

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

/// <summary>
/// Synchronous lifetime boundary shared by native calls, composition handoff and teardown.
/// No disposable semaphore: queued workers may arrive after teardown and must safely do nothing.
/// Kept separate from native calls so these ordering rules can be tested without loading mpv.
/// </summary>
internal sealed class LibMpvLifetimeGate
{
    private readonly Lock _sync = new();
    private bool _compositionStopped;
    private bool _destroyed;

    internal T? Run<T>(Func<T> work, bool composition = false)
    {
        lock (_sync)
        {
            return _destroyed || (composition && _compositionStopped) ? default : work();
        }
    }

    internal void StopComposition(Action detach)
    {
        lock (_sync)
        {
            if (_compositionStopped || _destroyed) return;
            _compositionStopped = true;
            detach();
        }
    }

    internal void Destroy(Action destroy)
    {
        lock (_sync)
        {
            if (_destroyed) return;
            _destroyed = true;
            _compositionStopped = true;
            destroy();
        }
    }
}
