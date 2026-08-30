namespace EmbyNian.Mpv;

/// <summary>
/// Escaping for mpv options whose value is a list: elements are separated by a comma, and a
/// backslash escapes the next character. Both backends and the runtime shader switcher need
/// this, so it lives in one place instead of being duplicated per call site.
/// </summary>
internal static class MpvListValue
{
    public static string Escape(string value) => value.Replace("\\", "\\\\").Replace(",", "\\,");

    /// <summary>
    /// A <c>file-list</c> option such as <c>glsl-shaders</c>. Those separate entries with <c>;</c> on
    /// Windows rather than with the comma the plain list options use, so the character that has to be
    /// escaped differs too — and the entries are Windows paths, which are full of the backslash that
    /// does the escaping.
    /// </summary>
    public static string JoinFiles(IEnumerable<string> paths) =>
        string.Join(";", paths.Select(path => path.Replace("\\", "\\\\").Replace(";", "\\;")));
}
