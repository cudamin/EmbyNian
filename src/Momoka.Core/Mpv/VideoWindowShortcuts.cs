using Momoka.Playback;

namespace Momoka.Mpv;

/// <summary>Translate the shared shortcut tokens to mpv's key spelling without executing commands.</summary>
public static class VideoWindowShortcuts
{
    public static IReadOnlyDictionary<string, string> Bindings(IReadOnlyDictionary<string, string> bindings)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var action in ShortcutCatalog.Actions)
            if (Key(action.Default) is { } key) result[key] = "";
        foreach (var (id, stroke) in ShortcutCatalog.Resolve(bindings))
            if (Key(stroke) is { } key && string.IsNullOrEmpty(result.GetValueOrDefault(key))) result[key] = id;
        return result;
    }

    public static string? Key(KeyStroke stroke)
    {
        if (stroke.IsEmpty) return null;
        var key = stroke.Key switch
        {
            "Space" => "SPACE",
            "Back" => "BS",
            "Delete" => "DEL",
            "Insert" => "INS",
            "Home" => "HOME",
            "End" => "END",
            "PageUp" => "PGUP",
            "PageDown" => "PGDWN",
            "Left" => "LEFT",
            "Right" => "RIGHT",
            "Up" => "UP",
            "Down" => "DOWN",
            "BracketLeft" => "[",
            "BracketRight" => "]",
            { Length: 1 } value when char.IsAsciiLetterOrDigit(value[0]) => value.ToLowerInvariant(),
            { Length: 2 or 3 } value when value[0] == 'F' && int.TryParse(value.AsSpan(1), out var number)
                && number is >= 1 and <= 12 => value,
            _ => null
        };
        if (key is null) return null;
        return (stroke.Ctrl ? "Ctrl+" : "") + (stroke.Alt ? "Alt+" : "") + (stroke.Shift ? "Shift+" : "") + key;
    }
}
