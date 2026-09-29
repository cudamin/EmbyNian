using System.Globalization;
using System.Text.RegularExpressions;
using EmbyNian.Theming;

namespace EmbyNian.Playback;

public enum TimelineSectionKind { Opening, Ending, Advertisement }

public readonly record struct TimelineRange(double Start, double End);
public sealed record TimelineChapter(double Start, string Title, int SourceIndex, bool EndOnly = false);
public sealed record TimelineSection(double Start, double End, TimelineSectionKind Kind);

/// <summary>Chapter artwork follows uosc, independently of the automatic-skip planner.</summary>
public sealed partial class TimelineChapterMap
{
    public static TimelineChapterMap Empty { get; } = new([], []);

    public IReadOnlyList<TimelineChapter> Chapters { get; }
    public IReadOnlyList<TimelineSection> Sections { get; }

    private TimelineChapterMap(IReadOnlyList<TimelineChapter> chapters, IReadOnlyList<TimelineSection> sections)
    {
        Chapters = chapters;
        Sections = sections;
    }

    public static ThemeColor Foreground { get; } = ThemeColor.Parse("#FFFBFE");
    public static ThemeColor Background { get; } = ThemeColor.Parse("#1C1B1F");
    public static ThemeColor OpeningColor { get; } = ThemeColor.Parse("#6430ABF9");
    public static ThemeColor EndingColor => OpeningColor;
    public static ThemeColor AdvertisementColor { get; } = ThemeColor.Parse("#80C54E4E");

    public static ThemeColor Color(TimelineSectionKind kind) => kind switch
    {
        TimelineSectionKind.Advertisement => AdvertisementColor,
        TimelineSectionKind.Ending => EndingColor,
        _ => OpeningColor
    };

    public static TimelineChapterMap Build(IReadOnlyList<SkipChapter> source)
    {
        if (source.Count == 0) return Empty;
        var chapters = source.Select((chapter, index) => new TimelineChapter(chapter.Start, chapter.Title ?? "", index))
            .Where(chapter => double.IsFinite(chapter.Start) && chapter.Start >= 0)
            .OrderBy(chapter => chapter.Start).ToArray();
        for (var i = 0; i < chapters.Length; i++)
        {
            var title = chapters[i].Title;
            var numbered = ChapterNumber().Match(title);
            if (numbered.Success && long.TryParse(numbered.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                title = $"章节 {number}";
            if (title is "" or "(unnamed)") title = $"章节 {i + 1}";
            chapters[i] = chapters[i] with { Title = title };
        }

        var sections = new List<TimelineSection>();
        var sponsors = new List<(int Section, int Start, int End)>();
        for (var i = 0; i < chapters.Length; i++)
        {
            var chapter = chapters[i];
            var title = chapter.Title.ToLowerInvariant();
            var end = i + 1 < chapters.Length ? chapters[i + 1].Start : double.PositiveInfinity;
            if (i + 1 < chapters.Length && Opening().IsMatch(title))
                sections.Add(new(chapter.Start, end, TimelineSectionKind.Opening));
            if (Ending().IsMatch(title)) sections.Add(new(chapter.Start, end, TimelineSectionKind.Ending));

            var start = SponsorStart().Match(title);
            if (start.Success)
            {
                var pattern = "segment end *\\(" + Regex.Escape(start.Groups[1].Value) + "\\)";
                for (var j = i + 1; j < chapters.Length; j++)
                {
                    if (!Regex.IsMatch(chapters[j].Title.ToLowerInvariant(), pattern, RegexOptions.CultureInvariant)) continue;
                    sponsors.Add((sections.Count, i, j));
                    sections.Add(new(chapter.Start, chapters[j].Start, TimelineSectionKind.Advertisement));
                    chapters[j] = chapters[j] with { EndOnly = true };
                    break;
                }
            }
            else if (!chapter.EndOnly && (title.Contains("[sponsorblock]:", StringComparison.Ordinal)
                || title.StartsWith("sponsor", StringComparison.Ordinal)))
                sections.Add(new(chapter.Start, end, TimelineSectionKind.Advertisement));
        }

        for (var i = 0; i + 1 < sponsors.Count; i++)
        {
            var current = sponsors[i];
            var next = sponsors[i + 1];
            var range = sections[current.Section];
            var following = sections[next.Section];
            if (following.Start >= range.End) continue;
            var midpoint = (range.End + following.Start) / 2;
            sections[current.Section] = range with { End = midpoint - 0.01 };
            sections[next.Section] = following with { Start = midpoint };
            chapters[current.End] = chapters[current.End] with { Start = midpoint - 0.01 };
            chapters[next.Start] = chapters[next.Start] with { Start = midpoint };
        }

        return new(chapters.OrderBy(chapter => chapter.Start).ToArray(), sections.ToArray());
    }

    public int IndexAt(double seconds)
    {
        for (var i = Chapters.Count - 1; i >= 0; i--)
            if (Chapters[i].Start <= seconds + 0.001) return i;
        return -1;
    }

    public string CaptionAt(double seconds)
    {
        var index = IndexAt(seconds);
        return index < 0 || Chapters[index].EndOnly ? "" : Chapters[index].Title;
    }

    [GeneratedRegex("^Chapter ([0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChapterNumber();

    // Keep these aliases aligned with uosc's chapter_range_patterns; tests compare both renderers.
    [GeneratedRegex("^op |^op$| op$|^opening$| opening$|^intro[ \\t\\r\\n\\f\\v]*start|^intro$|オープニング$|^片头$|片头开始$", RegexOptions.CultureInvariant)]
    private static partial Regex Opening();

    [GeneratedRegex("^ed |^ed$| ed$|^ending |^ending$| ending$|^end$|エンディング$|^片尾$|片尾开始$|^credits$", RegexOptions.CultureInvariant)]
    private static partial Regex Ending();

    [GeneratedRegex("segment start *\\(([a-z0-9]+)\\)", RegexOptions.CultureInvariant)]
    private static partial Regex SponsorStart();
}
