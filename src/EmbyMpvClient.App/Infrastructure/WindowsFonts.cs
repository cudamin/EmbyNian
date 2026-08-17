using Microsoft.Win32;

namespace EmbyMpvClient.App.Infrastructure;

/// <summary>
/// The fonts the user can pick for subtitles: everything under C:\Windows\Fonts, shown by
/// display name instead of file name. The display names come from the registry, where Windows
/// keeps the font files' friendly names; a file shared by several faces (msyh.ttc holds both
/// YaHei and YaHei UI) is listed once under its first name.
/// </summary>
public static class WindowsFonts
{
    public sealed record FontEntry(string DisplayName, string FilePath);

    public static IReadOnlyList<FontEntry> List()
    {
        var entries = new List<FontEntry>();
        var names = ReadDisplayNames();

        try
        {
            var fontsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

            foreach (var file in Directory.EnumerateFiles(fontsDirectory, "*.*")
                         .Where(IsFontFile)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(file);
                var displayName = names.TryGetValue(fileName, out var name) ? name : Path.GetFileNameWithoutExtension(file);
                entries.Add(new FontEntry(displayName, file));
            }
        }
        catch (Exception)
        {
            // A locked or missing fonts folder must not take the settings page down.
        }

        return entries;
    }

    private static bool IsFontFile(string path) =>
        path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps a font file name to its display name; first entry wins for shared files.</summary>
    private static Dictionary<string, string> ReadDisplayNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            if (key is null) return names;

            foreach (var displayName in key.GetValueNames())
            {
                if (key.GetValue(displayName) is not string file || file.Length == 0) continue;

                var fileName = Path.GetFileName(file);
                if (!IsFontFile(fileName) || names.ContainsKey(fileName)) continue;

                // "Arial (TrueType)" -> "Arial"; "(OpenType)" -> ""; names without a suffix stay.
                var cleaned = displayName.Replace(" (TrueType)", "", StringComparison.OrdinalIgnoreCase)
                    .Replace(" (OpenType)", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();
                names[fileName] = cleaned.Length > 0 ? cleaned : Path.GetFileNameWithoutExtension(fileName);
            }
        }
        catch (Exception)
        {
        }

        return names;
    }
}