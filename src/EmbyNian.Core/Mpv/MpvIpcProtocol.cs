using System.Text;
using System.Text.Json;

namespace EmbyNian.Mpv;

/// <summary>
/// Reassembles mpv's newline-delimited JSON out of arbitrary pipe reads.
/// <para>
/// A read can land in the middle of a message, and two messages can arrive in one read, so the
/// framing cannot be done per read. v1 parsed each read as one message and silently dropped
/// whatever did not fit, which is why its position display froze at random.
/// </para>
/// </summary>
public sealed class MpvIpcLineBuffer
{
    private readonly List<byte> _pending = [];

    /// <summary>Bytes held back because the line they belong to has not finished arriving.</summary>
    public int PendingBytes => _pending.Count;

    public IReadOnlyList<string> Append(ReadOnlySpan<byte> data)
    {
        var lines = new List<string>();

        foreach (var value in data)
        {
            if (value == (byte)'\n')
            {
                Take(lines);
                continue;
            }

            _pending.Add(value);

            // A single absurd line means the other end is not mpv; drop it rather than grow
            // without bound.
            if (_pending.Count > 1 << 20) _pending.Clear();
        }

        return lines;
    }

    private void Take(List<string> lines)
    {
        var length = _pending.Count;
        if (length > 0 && _pending[length - 1] == (byte)'\r') length--;
        if (length > 0) lines.Add(Encoding.UTF8.GetString(_pending.ToArray(), 0, length));
        _pending.Clear();
    }
}

/// <summary>One decoded line from mpv's IPC socket: either an event or a command reply.</summary>
public readonly record struct MpvIpcMessage(
    string? Event,
    string? PropertyName,
    JsonElement? Data,
    int? RequestId,
    string? Error,
    string? Reason)
{
    public bool IsReply => RequestId.HasValue;

    public bool IsSuccess => Error is null || Error == "success";

    public static bool TryParse(string line, out MpvIpcMessage message)
    {
        message = default;
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return false;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            message = new MpvIpcMessage(
                Text(root, "event"),
                Text(root, "name"),
                root.TryGetProperty("data", out var data) ? data.Clone() : null,
                root.TryGetProperty("request_id", out var id) && id.TryGetInt32(out var value) ? value : null,
                Text(root, "error"),
                Text(root, "reason"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The payload as a number, for time-pos and duration.</summary>
    public double? AsDouble() =>
        Data is { ValueKind: JsonValueKind.Number } data && data.TryGetDouble(out var value) ? value : null;

    public bool? AsBoolean() => Data?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    public string? AsString() => Data is { ValueKind: JsonValueKind.String } data ? data.GetString() : null;
}

/// <summary>mpv's <c>end-file</c> reasons, and what they mean for the Emby stop report.</summary>
public static class MpvEndFileReason
{
    public const string Eof = "eof";
    public const string Stop = "stop";
    public const string Quit = "quit";
    public const string Error = "error";
    public const string Redirect = "redirect";
    public const string Unknown = "unknown";
}
