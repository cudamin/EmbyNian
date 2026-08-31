namespace EmbyNian.Playback;

/// <summary>
/// Reading the seek bar: which chapter a hovered moment falls in, and what to call it. Pure lookups over
/// a list of marks in playing order, kept here rather than in the player page because both of the two
/// chapter lists the client holds get asked the same two questions, and neither answer has anything to do
/// with WinUI.
/// <para>
/// Two lists, both right about different things. Emby's own marks — the ones the item arrived with — are
/// what the server's chapter images are indexed by, so a still can only ever be asked for by an index
/// into that list. mpv's <c>chapter-list</c> is what the file on disk actually contains, which is the list
/// to take a chapter's name from once mpv has published one: a file remuxed after the library scan is
/// exactly the case where the two disagree, and the picture on screen is the one to believe.
/// </para>
/// </summary>
public static class ChapterTimeline
{
    /// <summary>Slack so a mark's own second reads as inside it rather than just before it.</summary>
    private const double Tolerance = 0.001;

    /// <summary>
    /// The chapter <paramref name="seconds"/> falls in — the last one starting at or before it — or -1
    /// when there are no marks at all, or when the first of them starts after that moment. Assumes
    /// playing order, which is how both Emby and mpv send them.
    /// </summary>
    public static int IndexAt(IReadOnlyList<SkipChapter> chapters, double seconds)
    {
        var found = -1;

        for (var index = 0; index < chapters.Count; index++)
        {
            if (chapters[index].Start > seconds + Tolerance) break;
            found = index;
        }

        return found;
    }

    /// <summary>
    /// What the preview calls that chapter: its own title, or 「章节 N」 for the majority of files whose
    /// marks carry no names. Empty for no chapter at all — a blank line over the clock would read as a
    /// hole rather than as a caption, so the caller collapses the line instead of printing nothing.
    /// </summary>
    public static string Caption(IReadOnlyList<SkipChapter> chapters, int index)
    {
        if (index < 0 || index >= chapters.Count) return "";

        var title = chapters[index].Title;
        return string.IsNullOrWhiteSpace(title) ? $"章节 {index + 1}" : title.Trim();
    }
}
