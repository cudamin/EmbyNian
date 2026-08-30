using EmbyNian.Infrastructure;

namespace EmbyNian.Playback;

/// <summary>What the chrome should be showing, recomputed from every position update.</summary>
/// <param name="Remaining">
/// How much of the offer's life is left, 1 down to 0. Drawn as the countdown on the button, so an
/// offer that is about to expire looks like one.
/// </param>
public readonly record struct SkipPrompt(bool Visible, string Caption, string Tip, double Remaining)
{
    public static readonly SkipPrompt None = new(false, "", "", 0);
}

/// <summary>A jump that has been decided on: seek to <see cref="Target"/> and say so.</summary>
public readonly record struct SkipJump(SkipSection Section, double Target)
{
    /// <summary>The line put on the picture after a jump, in chapterskip.lua's wording.</summary>
    public string Notice =>
        $"已跳过{Section.Label}: {Clock(Section.Start)}-{Clock(Target)}";

    private static string Clock(double seconds) =>
        TimeFormat.Clock(TimeSpan.FromSeconds(Math.Max(0, seconds)));
}

/// <summary>
/// Runs the 跳过 offer for one file: which section the position is in, whether its offer still
/// stands, and what a jump would land on.
/// <para>
/// Every decision is remembered against the section's own start time rather than its index, so
/// <see cref="Refine"/> can swap Emby's chapter marks for mpv's better ones mid-file without
/// re-offering something the viewer already answered. A decline lasts as long as the position stays
/// in the section — seeking back into an opening is a deliberate act and gets a fresh offer — while
/// a completed jump is remembered for the whole file, so going back to watch an opening on purpose
/// is not thrown forward again a second time.
/// </para>
/// </summary>
public sealed class SkipCoordinator
{
    /// <summary>How long an unanswered offer stands, in seconds of playback.</summary>
    public const double PromptSeconds = 15;

    /// <summary>
    /// How far into a section playback can be and still count as having walked into it. Past this an
    /// automatic jump stays out of the way: landing in the middle of a credit roll means somebody
    /// seeked there, and throwing them out of it would undo what they just did.
    /// </summary>
    private const double EntryGrace = 2;

    private readonly HashSet<long> _jumped = [];

    private IReadOnlyList<SkipSection> _sections = [];
    private SkipSection? _armed;
    private double _armedAt;
    private double _duration;
    private bool _answered;

    /// <summary>Read from the settings on every update, so switching it takes effect mid-file.</summary>
    public SkipSectionMode Mode { get; set; } = SkipSectionMode.Ask;

    public SkipPrompt Prompt { get; private set; } = SkipPrompt.None;

    public IReadOnlyList<SkipSection> Sections => _sections;

    /// <summary>Starts a new file, forgetting every decision taken about the last one.</summary>
    public void Begin(IReadOnlyList<SkipSection> sections, double duration)
    {
        _sections = sections;
        _duration = duration;
        _jumped.Clear();
        _armed = null;
        _armedAt = 0;
        _answered = false;
        Prompt = SkipPrompt.None;
    }

    /// <summary>
    /// Swaps in a better section list for the file already playing — mpv's own chapter names arrive
    /// a second or two after playback starts, and they are the ones a release group wrote 「OP」 in.
    /// </summary>
    public void Refine(IReadOnlyList<SkipSection> sections, double duration)
    {
        _sections = sections;
        if (duration > 0) _duration = duration;

        // The armed section came from the list being replaced; whether it survived is decided by the
        // next position update, against whatever is now in front of the viewer.
        if (_armed is { } armed && !sections.Contains(armed))
        {
            _armed = null;
            _answered = false;
            Prompt = SkipPrompt.None;
        }
    }

    /// <summary>
    /// Takes a position report and returns a jump to make now, which only ever happens in
    /// 自动 mode. Also refreshes <see cref="Prompt"/>, which is what 询问 mode acts through.
    /// </summary>
    /// <param name="playing">
    /// False when there is nothing to skip over — no file, no control channel, minimised. A pause is
    /// not counted: the offer stays up, and its countdown stops with the position it is measured in.
    /// </param>
    public SkipJump? Advance(double position, bool playing)
    {
        if (Mode == SkipSectionMode.Off || !playing || position < 0) return Leave();

        if (SectionAt(position) is not { } section) return Leave();

        if (_armed is not { } armed || !Same(armed, section))
        {
            _armed = section;
            _armedAt = position;

            // A section already jumped over stays answered for the rest of the file. Somebody who
            // seeks back to watch an opening on purpose is not asking to be thrown forward again, and
            // because the key is the rounded start time this survives Refine swapping the chapter list.
            _answered = _jumped.Contains(Key(section));
        }

        if (_answered)
        {
            Prompt = SkipPrompt.None;
            return null;
        }

        if (Mode == SkipSectionMode.Auto)
        {
            Prompt = SkipPrompt.None;
            if (position - section.Start > EntryGrace) return null;

            _answered = true;
            _jumped.Add(Key(section));
            return new SkipJump(section, TargetOf(section));
        }

        var elapsed = position - _armedAt;
        if (elapsed >= PromptSeconds)
        {
            // Silence is an answer. Somebody watching the opening on purpose should not have a
            // button sitting over it for the whole ninety seconds.
            Prompt = SkipPrompt.None;
            return null;
        }

        Prompt = new SkipPrompt(true, section.Caption, Describe(section), 1 - elapsed / PromptSeconds);
        return null;
    }

    /// <summary>Takes the standing offer; null when there is nothing on screen to take.</summary>
    public SkipJump? Accept()
    {
        if (!Prompt.Visible || _armed is not { } section) return null;

        _answered = true;
        _jumped.Add(Key(section));
        Prompt = SkipPrompt.None;
        return new SkipJump(section, TargetOf(section));
    }

    /// <summary>Turns the offer down for as long as the position stays in this section.</summary>
    public void Decline()
    {
        if (_armed is null) return;

        _answered = true;
        Prompt = SkipPrompt.None;
    }

    /// <summary>
    /// Where a jump lands. Half a second short of the run time for a section that runs to the end of
    /// the file, so mpv finishes it the way it would have anyway instead of being asked to seek to a
    /// position that is not quite in the file.
    /// </summary>
    public double TargetOf(SkipSection section) =>
        _duration > 1 ? Math.Min(section.End, _duration - 0.5) : section.End;

    private SkipJump? Leave()
    {
        _armed = null;
        _answered = false;
        Prompt = SkipPrompt.None;
        return null;
    }

    private SkipSection? SectionAt(double position)
    {
        foreach (var section in _sections)
            if (section.Covers(position)) return section;

        return null;
    }

    private string Describe(SkipSection section)
    {
        var target = TimeFormat.Clock(TimeSpan.FromSeconds(TargetOf(section)));
        var source = section.Certain ? $"章节：{section.Label}" : $"推测的{section.Label}";
        return $"跳到 {target}（{source}） · 快捷键 Y";
    }

    /// <summary>The same section, allowing for two chapter lists that disagree by a rounding error.</summary>
    private static bool Same(SkipSection left, SkipSection right) =>
        left.Kind == right.Kind && Math.Abs(left.Start - right.Start) < 1;

    private static long Key(SkipSection section) => (long)Math.Round(section.Start);
}
