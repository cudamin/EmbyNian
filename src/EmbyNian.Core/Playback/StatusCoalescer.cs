namespace EmbyNian.Playback;

/// <summary>
/// The gate on <c>StatusChanged</c>: it lets a snapshot through only when something the chrome draws has
/// moved far enough to be worth a marshalled UI update, and swallows the rest. mpv notifies its
/// <c>time-pos</c> observers on every frame — ~60 a second — and redrawing the seek bar that often is
/// sixty passes to move the thumb one pixel.
/// <para>
/// <b>The trap this closes, and why it is a class rather than an inline <c>if</c>.</b> The threshold has to
/// be measured against the last snapshot that was <em>let through</em>, never the last one merely
/// <em>seen</em>. A value that climbs in sub-threshold steps — the position again, ~0.03s per notification
/// against a 0.25s gate — advances the baseline with every step if the baseline is just "the latest value",
/// so the gap to the baseline is never wider than one step and the gate never opens. Folded into the
/// backend that way, it was invisible to 独占播放 (uosc reads mpv itself) but froze the 集成模式 seek bar
/// between the coarse events — a pause, a buffering stall, a whole second of cache — that did clear it.
/// Keeping the baseline here, apart from the caller's own freshest-value field, is the whole fix, and the
/// reason the behaviour can be pinned by a test without a live mpv.
/// </para>
/// </summary>
public sealed class StatusCoalescer
{
    private PlayerStatus _published = new();

    /// <summary>The last snapshot <see cref="ShouldPublish"/> returned true for.</summary>
    public PlayerStatus Published => _published;

    /// <summary>
    /// True when <paramref name="candidate"/> differs from the last published snapshot in something the
    /// chrome draws; that snapshot then becomes the new baseline. False leaves the baseline untouched —
    /// which is precisely what lets a run of small steps accumulate until one of them crosses the threshold.
    /// </summary>
    public bool ShouldPublish(PlayerStatus candidate)
    {
        if (!candidate.DiffersFrom(_published)) return false;

        _published = candidate;
        return true;
    }
}
