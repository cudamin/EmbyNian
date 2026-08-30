using System.Text.RegularExpressions;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;

namespace EmbyNian.Playback;

/// <summary>Which end of the file a recognised section belongs to.</summary>
public enum SkipSectionKind
{
    /// <summary>Everything before the episode proper: a recap, then the titles.</summary>
    Opening,

    /// <summary>Everything after it: the credit roll, then the next-episode trailer.</summary>
    Ending
}

/// <summary>What the client does when playback walks into a recognised section.</summary>
public enum SkipSectionMode
{
    /// <summary>Offer the 跳过 button and wait to be asked.</summary>
    Ask,

    /// <summary>Jump over it without asking.</summary>
    Auto,

    /// <summary>Recognise nothing and offer nothing.</summary>
    Off
}

/// <summary>One chapter mark, from Emby's item or from mpv's own <c>chapter-list</c>.</summary>
/// <param name="Start">Where the chapter begins, in seconds.</param>
/// <param name="Title">The chapter's name; null or empty for the majority that have none.</param>
public readonly record struct SkipChapter(double Start, string? Title);

/// <summary>
/// A stretch of the file worth jumping over: <see cref="Start"/>..<see cref="End"/>, with a label to
/// put on the button and a note of whether a chapter name said so or the position implied it.
/// </summary>
public readonly record struct SkipSection(
    SkipSectionKind Kind,
    double Start,
    double End,
    string Label,
    bool Certain)
{
    public double Length => End - Start;

    /// <summary>What the button says: 跳过片头, 跳过片尾, 跳过预告 …</summary>
    public string Caption => "跳过" + Label;

    /// <summary>Whether the section is what is playing at <paramref name="position"/> seconds.</summary>
    public bool Covers(double position) =>
        End > 0 && position >= Start - 0.5 && position < End - 0.5;
}

/// <summary>
/// Works out which parts of a file are an opening or an ending, from its chapter marks.
/// <para>
/// Modelled on chapterskip.lua from the mpv config this client is built around, down to its numbers:
/// nothing at all for a file of 200 seconds or less or with a single chapter, chapter titles matched
/// against a category table first, and — only for chapters at either end of the list, running 80 to
/// 100 seconds, inside the first 200 or last 300 seconds — a positional guess. That is why an
/// unnamed 90-second chapter is taken for a title sequence while an unnamed 4-minute one is not.
/// </para>
/// <para>
/// A title is trusted about what a section contains but not about where it sits: 「OP」 four minutes
/// into the file is a mislabelled chapter, and an opening that ends within fifteen seconds is a
/// distributor logo. A file with no chapters gets nothing — the previous version guessed a flat
/// 0–90 seconds for any episode, which put a 跳过 button on every episode in the library and jumped
/// into the middle of the ones that had no titles to skip.
/// </para>
/// </summary>
public static class SkipSectionPlanner
{
    /// <summary>Below this run time there is no room for a title sequence (lua: intro_time_window).</summary>
    public const double ShortestFile = 200;

    /// <summary>How late a section can begin and still be an opening.</summary>
    public const double IntroWindow = 200;

    /// <summary>How far from the end an inferred ending has to begin.</summary>
    public const double OutroWindow = 300;

    /// <summary>A section shorter than this is a marker between two scenes, not a section.</summary>
    private const double ShortestSection = 8;

    /// <summary>An "opening" over before this is the distributor's logo; after it, the episode.</summary>
    private const double EarliestEnd = 15;

    // The window an unnamed chapter has to fall in to be taken for titles or credits. Openings and
    // endings are cut to the music, and the music is ninety seconds long.
    private const double InferredShortest = 80;
    private const double InferredLongest = 100;

    /// <summary>
    /// chapterskip.lua's category table, in the same order it checks them. Word-bounded so 「OP」
    /// matches "OP" and "OP2" without matching "Open Water", and 「ED」 matches "ED" and "The ED"
    /// without matching "Watched".
    /// </summary>
    private static readonly (Regex Pattern, SkipSectionKind Kind, string Label)[] Categories =
    [
        (Pattern(@"^op\d*\b|\bop\d*$|^opening|opening$|^intro\b|^introduction|^title\b|^titles$|^title sequence|オープニング|片头|片頭|主题曲|主題曲|开场|開場"),
            SkipSectionKind.Opening, "片头"),

        (Pattern(@"^ed\d*\b|\bed\d*$|^ending|ending$|^credits|credits$|^staff\b|エンディング|片尾|ED曲|工作人员"),
            SkipSectionKind.Ending, "片尾"),

        (Pattern(@"^preview|preview$|次回予告|预告|預告"),
            SkipSectionKind.Ending, "预告"),

        (Pattern(@"^prologue|^recap\b|前情提要|^序章|^回顾|^回顧"),
            SkipSectionKind.Opening, "序章")
    ];

    /// <summary>Every section in <paramref name="item"/>, from the chapters the server sent.</summary>
    public static IReadOnlyList<SkipSection> Resolve(EmbyItem? item) =>
        item is null ? [] : Resolve(FromEmby(item.Chapters), TimeFormat.ToSeconds(item.RunTimeTicks));

    /// <summary>Emby's chapter records as the planner's own shape.</summary>
    public static IReadOnlyList<SkipChapter> FromEmby(IReadOnlyList<ChapterInfo> chapters)
    {
        var converted = new List<SkipChapter>(chapters.Count);
        foreach (var chapter in chapters) converted.Add(new SkipChapter(chapter.StartSeconds, chapter.Name));
        return converted;
    }

    /// <summary>
    /// Every section in a file of <paramref name="duration"/> seconds with these
    /// <paramref name="chapters"/>, in playing order and with touching sections folded together.
    /// </summary>
    public static IReadOnlyList<SkipSection> Resolve(IReadOnlyList<SkipChapter> chapters, double duration)
    {
        if (duration <= ShortestFile || chapters.Count <= 1) return [];

        var found = new List<SkipSection>();
        for (var index = 0; index < chapters.Count; index++)
        {
            // A chapter's span runs to the next mark, or to the end of the file for the last one.
            var start = chapters[index].Start;
            var end = index + 1 < chapters.Count ? chapters[index + 1].Start : duration;
            if (start < 0 || end <= start) continue;

            var section = FromTitle(chapters[index].Title, start, end, duration)
                          ?? FromPosition(index, chapters.Count, start, end, duration);

            if (section is { } match) found.Add(match);
        }

        return Merge(found);
    }

    private static Regex Pattern(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A chapter that says what it is — wherever it sits, since cold opens push it second.</summary>
    private static SkipSection? FromTitle(string? title, double start, double end, double duration)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var trimmed = title.Trim();

        foreach (var (pattern, kind, label) in Categories)
        {
            if (!pattern.IsMatch(trimmed)) continue;
            return Accept(kind, start, end, label, certain: true, duration);
        }

        return null;
    }

    /// <summary>
    /// A chapter with no name in the one place, and of the one length, that an opening or an ending
    /// can be. Applied to films as well as episodes, as in the reference: a 90-second chapter that
    /// starts a film is its title sequence and one that ends it is its credit roll.
    /// </summary>
    private static SkipSection? FromPosition(int index, int count, double start, double end, double duration)
    {
        var length = end - start;
        if (length < InferredShortest || length > InferredLongest) return null;

        // Outermost chapters only. An unnamed 90-second chapter in the middle is a scene.
        if (index > 1 && index < count - 2) return null;

        if (index < (count + 1) / 2)
        {
            return start < IntroWindow
                ? Accept(SkipSectionKind.Opening, start, end, "片头", certain: false, duration)
                : null;
        }

        return start >= duration - OutroWindow
            ? Accept(SkipSectionKind.Ending, start, end, "片尾", certain: false, duration)
            : null;
    }

    /// <summary>Drops a section whose position contradicts what it claims to be.</summary>
    private static SkipSection? Accept(
        SkipSectionKind kind,
        double start,
        double end,
        string label,
        bool certain,
        double duration)
    {
        if (end - start < ShortestSection) return null;

        if (kind == SkipSectionKind.Opening)
        {
            if (end < EarliestEnd || start >= IntroWindow) return null;
        }
        else if (start < duration / 2)
        {
            // An 「ED」 in the first half of the file is a mislabel, not a credit roll.
            return null;
        }

        return new SkipSection(kind, start, end, label, certain);
    }

    /// <summary>
    /// Folds sections that touch into one jump. A recap followed by the titles is one thing to get
    /// past, and left apart it would mean pressing 跳过 twice to reach the episode. The surviving
    /// label is the one at the destination end, so a merged opening still reads 跳过片头.
    /// </summary>
    private static IReadOnlyList<SkipSection> Merge(List<SkipSection> found)
    {
        if (found.Count < 2) return found;

        found.Sort((left, right) => left.Start.CompareTo(right.Start));

        var merged = new List<SkipSection> { found[0] };
        for (var index = 1; index < found.Count; index++)
        {
            var next = found[index];
            var last = merged[^1];

            if (last.Kind != next.Kind || next.Start > last.End + 1)
            {
                merged.Add(next);
                continue;
            }

            merged[^1] = last with
            {
                End = Math.Max(last.End, next.End),
                Label = last.Kind == SkipSectionKind.Opening ? next.Label : last.Label,
                Certain = last.Certain && next.Certain
            };
        }

        return merged;
    }
}
