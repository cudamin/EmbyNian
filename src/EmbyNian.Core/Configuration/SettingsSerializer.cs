using System.Text.Encodings.Web;
using System.Text.Json;

namespace EmbyNian.Configuration;

internal static class SettingsSerializer
{
    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Without the relaxed encoder every Chinese server name is written as \uXXXX,
        // which makes a hand-inspected settings.json unreadable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
