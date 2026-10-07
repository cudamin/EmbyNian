using EmbyNian.Diagnostics;
using EmbyNian.Mpv;

namespace EmbyNian.Playback;

/// <summary>
/// One entry of mpv's <c>audio-device-list</c>.
/// </summary>
/// <param name="Name">
/// What <c>audio-device</c> is set to — <c>auto</c>, or something like
/// <c>wasapi/{0.0.0.00000000}.{9c3d1b2e-…}</c>. Nobody copies that by hand, which is the whole reason this
/// list has to be read from mpv rather than typed into a box.
/// </param>
/// <param name="Description">The human-readable name Windows gives the endpoint.</param>
public readonly record struct AudioDevice(string Name, string Description)
{
    /// <summary>What the settings page shows: the description, falling back to the raw name.</summary>
    public string Label => string.IsNullOrWhiteSpace(Description) ? Name : Description;
}

/// <summary>
/// The machine's audio output devices, as mpv sees them.
/// <para>
/// <b>Why this exists at all:</b> 音频独占模式 could only ever take over 「whatever Windows currently calls the
/// default device」. Plug in headphones and which one gets taken over depends on what Windows decided at that
/// moment — the user could neither see it nor choose it. Every mainstream player has this row.
/// </para>
/// <para>
/// <b>Why it opens its own libmpv context.</b> The settings window has no mpv instance in hand — nothing is
/// playing while settings are open, and that is the normal case rather than an edge one. So this does exactly
/// what <c>mpv --audio-device=help</c> does: create a context, initialise it, read the property, destroy it. No
/// window, no file, no audio device actually opened; <c>vo=null</c> and <c>video=no</c> keep it from ever
/// wanting a window. It is the only way to get the real device names right, and the alternatives are worse —
/// the player's own menu would have to become asynchronous (<see cref="PlayerMenuCatalog"/> is deliberately
/// static data), and asking the user to type a GUID is not a feature.
/// </para>
/// <para>
/// <b>Registered as the concrete class</b>, per the project's rule about not inventing an interface for one
/// implementation: this class has exactly one public method and it is already the narrow surface the settings
/// page is supposed to see.
/// </para>
/// </summary>
public sealed class AudioDeviceCatalogue
{
    private const string Category = "mpv";

    /// <summary>
    /// mpv's own first entry: let it pick. <c>audio-device-list</c> always starts with it, and
    /// <see cref="Selectable"/> drops it — 「跟随系统默认设备」 is already that answer, spelled in Chinese and
    /// stored as the empty string, so keeping mpv's English 「Autoselect device」 alongside it would offer one
    /// behaviour twice under two labels and two stored values.
    /// </summary>
    public const string AutoDevice = "auto";

    /// <summary>
    /// What the settings row may offer: everything mpv reported except its own <c>auto</c> entry.
    /// <para>
    /// <b>Here rather than in the settings row, and it is not tidying.</b> 「要不要留 mpv 那一项」 has exactly one
    /// right answer for a given list, and this project keeps that kind of judgment in Core where a unit test can
    /// reach it — the settings page lives in the shell assembly, which the tests cannot see, and the self-check
    /// only prints how many entries the drop-down ended up with rather than asserting anything about them. Left
    /// up there, deleting this filter would leave all four gates green and put 「跟随系统默认设备」 and
    /// 「Autoselect device」 back on screen one under the other, with only a person's eye to notice.
    /// </para>
    /// </summary>
    public static IReadOnlyList<AudioDevice> Selectable(IEnumerable<AudioDevice> devices) =>
        [.. devices.Where(device => !string.Equals(device.Name, AutoDevice, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// 设置里存的那台设备现在还认不认。还在名单里就交回 mpv 自己的拼写；名单里没有 —— 耳机拔了、
    /// HDMI 端点变了、设置带到了另一台机器 —— 交回空串，也就是「跟随系统默认」。
    /// <para>
    /// 这是设置页那行说明承诺的回退，而 mpv 自己不兑现它：随包内核（v0.41.0-923 实测）对指定设备失败
    /// 会一路报错到「Could not open/initialize audio device -&gt; no sound」，日志自己写着「forced with
    /// the --audio-device option … Try unsetting it」—— 退不退，是调用方的决定。空串与 null 由调用方
    /// 区分：这里的返回值直接进 <see cref="Mpv.MpvOutputOptions.Build"/> 的 <c>audioDevice</c> 覆盖位，
    /// null 在那儿意味着「不核对，照设置原样」。
    /// </para>
    /// </summary>
    public static string UsableDevice(string stored, IReadOnlyList<AudioDevice> devices)
    {
        var name = (stored ?? "").Trim();
        if (name.Length == 0) return "";

        foreach (var device in devices)
            if (string.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase))
                return device.Name;

        return "";
    }

    private readonly Func<string?> _libraryPath;
    private readonly Func<IReadOnlyList<AudioDevice>> _read;
    private readonly object _loadGate = new();
    private Task<IReadOnlyList<AudioDevice>>? _loading;
    private IReadOnlyList<AudioDevice>? _cached;

    /// <param name="libraryPath">
    /// Where <c>libmpv-2.dll</c> is, or null when it cannot be found. Handed in rather than probed again
    /// here: <see cref="LibMpvBackend"/> already owns that search and its answer is the one that matters.
    /// </param>
    public AudioDeviceCatalogue(Func<string?> libraryPath) : this(libraryPath, null)
    {
    }

    internal AudioDeviceCatalogue(Func<string?> libraryPath, Func<IReadOnlyList<AudioDevice>>? read)
    {
        _libraryPath = libraryPath;
        _read = read ?? Read;
    }

    /// <summary>
    /// Re-enumerates on each visit or playback check. Only concurrent callers share an in-flight read;
    /// the last completed list is a display snapshot, never evidence that an endpoint is still online.
    /// </summary>
    public Task<IReadOnlyList<AudioDevice>> LoadAsync()
    {
        lock (_loadGate)
        {
            if (_loading is { IsCompleted: false }) return _loading;
            return _loading = Task.Run(() =>
            {
                IReadOnlyList<AudioDevice> devices;
                try { devices = Selectable(_read()); }
                catch (Exception error)
                {
                    Log.Warn(Category, "读取音频输出设备列表失败，本次名单不可用", error);
                    devices = [];
                }
                Volatile.Write(ref _cached, devices);
                return devices;
            });
        }
    }

    /// <summary>The last completed enumeration, including an empty result; not a live availability check.</summary>
    public IReadOnlyList<AudioDevice> Known => Volatile.Read(ref _cached) ?? [];

    private IReadOnlyList<AudioDevice> Read()
    {
        if (_libraryPath() is not { Length: > 0 } dllPath)
        {
            Log.Warn(Category, "找不到 libmpv-2.dll，音频输出设备列表读不到，设置里那一行只留「自动」");
            return [];
        }

        var context = IntPtr.Zero;
        try
        {
            LibMpvNative.UseLibrary(dllPath);
            LibMpvNative.EnsureDependencyDirectories(Path.GetDirectoryName(dllPath) ?? "", AppContext.BaseDirectory);

            context = LibMpvNative.mpv_create();
            if (context == IntPtr.Zero) throw new InvalidOperationException("mpv_create 返回了空句柄");

            // Nothing must happen in this context beyond enumerating. video=no and vo=null are what keep it
            // from ever asking for a window — this runs while the settings window is open, and a stray mpv
            // window appearing over it would be a memorable bug.
            Set(context, "config", "no");
            Set(context, "load-scripts", "no");
            Set(context, "video", "no");
            Set(context, "vo", "null");
            Set(context, "idle", "yes");
            Set(context, "terminal", "no");

            var error = LibMpvNative.mpv_initialize(context);
            if (error < 0) throw new InvalidOperationException($"mpv 初始化失败：{LibMpvBackend.Describe(error)}");

            var devices = LibMpvNodes.Read(context, "audio-device-list", root =>
            {
                var list = new List<AudioDevice>();
                foreach (var item in LibMpvNodes.Children(root))
                {
                    var map = LibMpvNodes.Map(item);
                    var name = LibMpvNodes.String(map, "name");
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    list.Add(new AudioDevice(name, LibMpvNodes.String(map, "description") ?? ""));
                }

                return list;
            }, new List<AudioDevice>());

            var selectable = Selectable(devices);
            Log.Info(Category, $"读到 {devices.Count} 个音频输出设备，可选 {selectable.Count} 个（去掉 mpv 自己那一项 auto）");
            return selectable;
        }
        catch (Exception failure)
        {
            // Never fatal: this is one row of the settings page, and the settings page opening is worth more
            // than that row being complete.
            Log.Warn(Category, "读取音频输出设备列表失败，设置里那一行只留「自动」", failure);
            return [];
        }
        finally
        {
            if (context != IntPtr.Zero) LibMpvNative.mpv_terminate_destroy(context);
        }
    }

    private static void Set(IntPtr context, string name, string value)
    {
        var error = LibMpvNative.mpv_set_option_string(context, name, value);
        if (error < 0) throw new InvalidOperationException($"枚举音频设备时设置 mpv 选项 {name} 失败：{LibMpvBackend.Describe(error)}");
    }
}
