using EmbyNian.Mpv;

namespace EmbyNian.Playback;

/// <summary>
/// How large the picture can be drawn right now, and how large it would be at full screen — the two halves
/// 任务书 2.4 asks for a plan apiece. Physical pixels throughout.
/// </summary>
/// <param name="Width">The current render target: the client area while windowed, the monitor while not.</param>
/// <param name="MonitorWidth">The monitor this window is on, taskbar included — full-screen playback covers it.</param>
public readonly record struct ShaderSurface(
    int Width,
    int Height,
    int MonitorWidth,
    int MonitorHeight,
    bool Fullscreen)
{
    /// <summary>
    /// Whether <see cref="Width"/> is the monitor standing in for a size nobody could read. The caller logs
    /// one line when it is, and must <b>not</b> remember it as a known size: 任务书 2.3 —— 取不到时不许永久锁死
    /// 显示器原生分辨率.
    /// </summary>
    public bool Fallback { get; init; }

    /// <summary>The size in force. Full screen is the monitor whatever the window rect happens to say.</summary>
    public (int Width, int Height) Active => Fullscreen ? (MonitorWidth, MonitorHeight) : (Width, Height);

    public (int Width, int Height) Monitor => (MonitorWidth, MonitorHeight);

    public bool Known => Active.Width > 0 && Active.Height > 0;

    /// <summary>
    /// 输出尺寸从哪来（任务书 2.3）：当前实际渲染目标尺寸 → 最近一次成功取到的有效尺寸 → 所在显示器尺寸.
    /// <para>
    /// The order matters because the two wrong answers are both worse than a stale one. Assuming 4K would put
    /// every film in 大倍数 on a 1440p screen; keeping the monitor's native size for good would put a film
    /// playing in a quarter of the screen on a chain built for all of it, which on an iGPU drops frames for
    /// the whole film. The monitor is the last resort and says so in the log — that is the honest answer for
    /// the external mpv.exe backend, whose window is not ours to measure.
    /// </para>
    /// </summary>
    public static ShaderSurface Resolve(
        (int Width, int Height) target,
        (int Width, int Height) lastKnown,
        (int Width, int Height) monitor,
        bool fullscreen)
    {
        var (width, height, fallback) =
            Valid(target) ? (target.Width, target.Height, false)
            : Valid(lastKnown) ? (lastKnown.Width, lastKnown.Height, false)
            : (monitor.Width, monitor.Height, true);

        return new ShaderSurface(width, height, monitor.Width, monitor.Height, fullscreen) { Fallback = fallback };
    }

    private static bool Valid((int Width, int Height) size) => size.Width > 0 && size.Height > 0;
}

/// <summary>What one observation means for the chain that is already running.</summary>
public enum OutputChange
{
    /// <summary>Nothing the chain cares about moved.</summary>
    None,

    /// <summary>A new window size is pending. Nothing may be regenerated until it settles.</summary>
    Waiting,

    /// <summary>The output size moved but stayed in the same tier: record it, leave mpv alone.</summary>
    SizeOnly,

    /// <summary>The tier changed: a different chain applies.</summary>
    Rebuild
}

/// <param name="Discrete">
/// True when this came from a discrete event — full screen, or the window landing on another monitor — rather
/// than from a settled resize. The caller answers a discrete change with the plan it prepared at launch, and
/// a settled resize by working out a new one.
/// </param>
public readonly record struct OutputVerdict(OutputChange Change, bool Discrete, int Width, int Height);

/// <summary>
/// One playback's output-size bookkeeping: which tier is in force, what the picture is being drawn into, and
/// what each change to that means. 任务书 2.4 in one place, with no timer and no window in it, so every rule
/// it states can be driven from a unit test.
/// <para>
/// The three rules it exists to keep apart:
/// <list type="bullet">
/// <item><b>Dragging an edge regenerates nothing.</b> Every size seen mid-drag is only remembered; the chain
/// is left alone until <see cref="Settle"/> has passed with the size holding still. Regenerating per
/// <c>WM_SIZE</c> would recompile the chain dozens of times a second and stutter the whole way.</item>
/// <item><b>A settled resize inside one tier costs nothing.</b> The size is recorded — <c>ravu-zoom</c>
/// renders to whatever the target is, and the log and OSD should say the new number — but mpv is not touched.
/// </item>
/// <item><b>Full screen and a monitor change are discrete</b>, so they take effect at once and skip the
/// debounce entirely: they are one keypress or one drop, not a stream of intermediate sizes.</item>
/// </list>
/// </para>
/// </summary>
public sealed class OutputWatch(int sourceWidth, int sourceHeight, ShaderSurface surface, UpscaleTier tier)
{
    /// <summary>
    /// How long a window size has to hold still before it counts. The one place this number is written: 任务书
    /// 2.4 —— 防抖时长放统一的策略常量里，不要散在业务代码.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    private ShaderSurface _surface = surface;
    private UpscaleTier _tier = tier;
    private (int Width, int Height) _output = surface.Active;
    private ShaderSurface? _pending;
    private DateTimeOffset _pendingSince;

    /// <summary>The output size the chain in force was chosen for.</summary>
    public (int Width, int Height) Output => _output;

    /// <summary>The tier in force, which is also the 「current tier」 the next measurement is sticky around.</summary>
    public UpscaleTier Tier => _tier;

    public bool Fullscreen => _surface.Fullscreen;

    /// <summary>Whether a size is waiting for the drag to stop. For the tests and the self-check.</summary>
    public bool Settling => _pending is not null;

    /// <summary>
    /// The window said something. Full screen and a monitor change take effect here and now; a plain resize
    /// is only noted, and <see cref="Tick"/> is what eventually acts on it.
    /// </summary>
    public OutputVerdict Observe(ShaderSurface next, DateTimeOffset now)
    {
        // Back to where it already is — which is what happens when a drag ends on the size it started at.
        // The pending size goes with it, or the tick afterwards would act on a change that was undone.
        if (next == _surface)
        {
            _pending = null;
            return Verdict(OutputChange.None, discrete: false);
        }

        if (next.Fullscreen != _surface.Fullscreen || next.Monitor != _surface.Monitor)
        {
            _pending = null;
            _surface = next;
            return Apply(discrete: true);
        }

        // A plain resize. The clock restarts on every new size, so a drag that never stops never settles.
        _pending = next;
        _pendingSince = now;
        return Verdict(OutputChange.Waiting, discrete: false);
    }

    /// <summary>
    /// Called from the player's own ten-hertz tick. Turns a size that has held still for
    /// <see cref="Settle"/> into a verdict; says <see cref="OutputChange.None"/> the rest of the time.
    /// </summary>
    public OutputVerdict Tick(DateTimeOffset now)
    {
        if (_pending is not { } pending) return Verdict(OutputChange.None, discrete: false);
        if (now - _pendingSince < Settle) return Verdict(OutputChange.Waiting, discrete: false);

        _pending = null;
        _surface = pending;
        return Apply(discrete: false);
    }

    /// <summary>
    /// Records the surface now in force and says whether the tier moved with it. The measurement is sticky
    /// around <see cref="Tier"/>, so a size that merely crossed a boundary by a hair keeps the chain it has.
    /// </summary>
    private OutputVerdict Apply(bool discrete)
    {
        var size = _surface.Active;
        var measure = ShaderTier.Measure(sourceWidth, sourceHeight, size.Width, size.Height, _tier);
        _output = size;

        if (measure.Tier == _tier) return Verdict(OutputChange.SizeOnly, discrete);

        _tier = measure.Tier;
        return Verdict(OutputChange.Rebuild, discrete);
    }

    private OutputVerdict Verdict(OutputChange change, bool discrete) =>
        new(change, discrete, _output.Width, _output.Height);
}
