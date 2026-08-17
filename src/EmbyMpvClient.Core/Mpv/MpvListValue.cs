namespace EmbyMpvClient.Mpv;

/// <summary>
/// Escaping for mpv options whose value is a list: elements are separated by a comma, and a
/// backslash escapes the next character. Both backends and the runtime shader switcher need
/// this, so it lives in one place instead of being duplicated per call site.
/// </summary>
internal static class MpvListValue
{
    public static string Escape(string value) => value.Replace("\\", "\\\\").Replace(",", "\\,");
}
