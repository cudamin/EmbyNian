namespace EmbyNian.Configuration;

/// <summary>
/// A filesystem path the way a person hands one over: with stray whitespace, and possibly wearing a pair of
/// double quotes.
/// <para>
/// In Core rather than in the settings page's path box, which is its only caller, because it is a judgment
/// with one right answer and the wrong answer is invisible: a value stored with its quotes still on makes
/// every later <see cref="Path.GetFullPath(string)"/> throw, which is a box that has permanently stopped
/// finding its file. A previous build did exactly that. Unit tests reach this project and not the shell, so
/// this is where the rule can be pinned down.
/// </para>
/// </summary>
public static class TypedPath
{
    /// <summary>
    /// Whitespace and one pair of surrounding double quotes off a path that was typed or pasted.
    /// <para>
    /// Explorer's 「复制为路径」 puts the path on the clipboard already quoted, so pasting one in is the
    /// ordinary way to fill a path box — and a double quote cannot appear in a Windows path, so a value
    /// wearing them can only have come from that. Applied on the way in and again on the way out, so a value
    /// a previous build stored with its quotes still on heals the first time it is read.
    /// </para>
    /// </summary>
    public static string Clean(string? path)
    {
        var value = (path ?? "").Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Trim() : value;
    }
}
