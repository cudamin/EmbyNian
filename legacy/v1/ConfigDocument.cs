using System.Text;

namespace EmbyMpvClient;

internal sealed class ConfigEntry
{
    public bool Enabled { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string Comment { get; set; } = "";
    public string Original { get; set; } = "";
    public bool IsSetting { get; set; }
}

internal static class ConfigDocument
{
    static ConfigDocument() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static (List<ConfigEntry> Entries, Encoding Encoding) Load(string path, bool inputFile)
    {
        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes);
        var text = encoding.GetString(bytes).TrimStart('\uFEFF');
        var entries = text.Replace("\r\n", "\n").Split('\n').Select(line => Parse(line, inputFile)).ToList();
        return (entries, encoding);
    }

    public static void Save(string path, IEnumerable<ConfigEntry> entries, Encoding encoding, bool inputFile)
    {
        var backupDir = Path.Combine(Path.GetDirectoryName(path)!, "EmbyMpvClient_Backups");
        Directory.CreateDirectory(backupDir);
        if (File.Exists(path))
            File.Copy(path, Path.Combine(backupDir, $"{Path.GetFileName(path)}.{DateTime.Now:yyyyMMdd-HHmmss}.bak"), true);

        var lines = entries.Select(e => Format(e, inputFile));
        File.WriteAllText(path, string.Join(Environment.NewLine, lines), encoding);
    }

    private static ConfigEntry Parse(string line, bool inputFile)
    {
        var entry = new ConfigEntry { Original = line };
        var trimmed = line.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("##")) return entry;
        var enabled = !trimmed.StartsWith('#');
        var body = enabled ? trimmed : trimmed[1..].TrimStart();
        if (string.IsNullOrWhiteSpace(body) || body.StartsWith('#')) return entry;

        var commentIndex = FindComment(body);
        var main = commentIndex >= 0 ? body[..commentIndex].TrimEnd() : body.TrimEnd();
        var comment = commentIndex >= 0 ? body[(commentIndex + 1)..].Trim() : "";
        if (inputFile)
        {
            var split = main.IndexOfAny([' ', '\t']);
            if (split <= 0) return entry;
            entry.Key = main[..split].Trim();
            entry.Value = main[split..].Trim();
        }
        else
        {
            var split = main.IndexOf('=');
            if (split < 1)
            {
                entry.Key = main.Trim();
                entry.Value = "yes";
            }
            else
            {
                entry.Key = main[..split].Trim();
                entry.Value = main[(split + 1)..].Trim();
            }
        }
        entry.Enabled = enabled;
        entry.Comment = comment;
        entry.IsSetting = entry.Key.Length > 0;
        return entry;
    }

    private static int FindComment(string value)
    {
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '"') quoted = !quoted;
            if (value[i] == '#' && !quoted && (i == 0 || char.IsWhiteSpace(value[i - 1]))) return i;
        }
        return -1;
    }

    private static string Format(ConfigEntry e, bool inputFile)
    {
        if (!e.IsSetting) return e.Original;
        var main = inputFile ? $"{e.Key,-18} {e.Value}" : (e.Value == "yes" ? e.Key : $"{e.Key}={e.Value}");
        if (!string.IsNullOrWhiteSpace(e.Comment)) main += "    # " + e.Comment;
        return e.Enabled ? main : "#" + main;
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(true);
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            _ = utf8.GetString(bytes);
            return new UTF8Encoding(false);
        }
        catch { return Encoding.GetEncoding(936); }
    }
}
