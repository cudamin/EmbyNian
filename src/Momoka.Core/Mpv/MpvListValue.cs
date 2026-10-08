using System.Text.Json;

namespace Momoka.Mpv;

/// <summary>
/// Escaping for mpv options whose value is a list: elements are separated by a comma, and a
/// backslash escapes the next character. Both backends and the runtime shader switcher need
/// this, so it lives in one place instead of being duplicated per call site.
/// </summary>
internal static class MpvListValue
{
    public static IReadOnlyList<string>? ParseStrings(string? json)
    {
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var result = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return null;
                result.Add(item.GetString()!);
            }
            return result;
        }
        catch (JsonException) { return null; }
    }

    public static string Escape(string value) => value.Replace("\\", "\\\\").Replace(",", "\\,");

    /// <summary>
    /// A <c>file-list</c> option uses semicolons on Windows. Backslashes are path characters;
    /// only a backslash immediately before a separator participates in escaping.
    /// </summary>
    public static string JoinFiles(IEnumerable<string> paths) =>
        string.Join(";", paths.Select(path => path.Replace(";", "\\;")));
}
