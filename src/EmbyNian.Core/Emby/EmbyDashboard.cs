using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbyNian.Emby;

/// <summary>
/// What the 服务器 page reads out of <c>/System/Info</c>. That endpoint is administrators-only; a
/// normal account gets the public subset instead, which uses the same field names, so one class
/// covers both and <see cref="IsRestricted"/> records which one answered.
/// </summary>
public sealed class EmbySystemInfo
{
    public string? ServerName { get; set; }

    public string? Version { get; set; }

    public string? Id { get; set; }

    public string? OperatingSystem { get; set; }

    public string? OperatingSystemDisplayName { get; set; }

    /// <summary>The port the server answers plain HTTP on; admin-only.</summary>
    public int? HttpServerPortNumber { get; set; }

    public int? HttpsPortNumber { get; set; }

    public bool EnableHttps { get; set; }

    /// <summary>"http://172.17.0.4:8096" — the address to hand to another device on the same network.</summary>
    public string? LocalAddress { get; set; }

    /// <summary>"http://67.159.48.146:8096" — only set when the server knows its public address.</summary>
    public string? WanAddress { get; set; }

    public bool HasUpdateAvailable { get; set; }

    public bool HasPendingRestart { get; set; }

    public string? SystemUpdateLevel { get; set; }

    /// <summary>Set by the client when only the public subset came back, so the page can explain the gaps.</summary>
    [JsonIgnore]
    public bool IsRestricted { get; set; }
}

/// <summary>Where one session is in its current file, as the server last heard it.</summary>
public sealed class PlayerStateInfo
{
    public long PositionTicks { get; set; }

    public bool CanSeek { get; set; }

    public bool IsPaused { get; set; }

    public bool IsMuted { get; set; }

    public int? VolumeLevel { get; set; }

    public int? AudioStreamIndex { get; set; }

    public int? SubtitleStreamIndex { get; set; }

    public string? MediaSourceId { get; set; }

    /// <summary>"DirectPlay", "DirectStream" or "Transcode".</summary>
    public string? PlayMethod { get; set; }

    public string? RepeatMode { get; set; }
}

/// <summary>
/// Present only while the server is re-encoding for a session. Its absence is the answer to
/// "is this a direct play", and the two Is*Direct flags split that per track.
/// </summary>
public sealed class EmbyTranscodingInfo
{
    public string? Container { get; set; }

    public string? VideoCodec { get; set; }

    public string? AudioCodec { get; set; }

    public bool IsVideoDirect { get; set; }

    public bool IsAudioDirect { get; set; }

    /// <summary>Bits per second of the outgoing stream.</summary>
    public int? Bitrate { get; set; }

    public int? AudioChannels { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public float? Framerate { get; set; }

    public double? CompletionPercentage { get; set; }

    /// <summary>Why the server had to transcode: "VideoCodecNotSupported", "ContainerBitrateExceedsLimit", …</summary>
    public List<string> TranscodeReasons { get; set; } = [];
}

/// <summary>One connected client, playing or idle, as <c>/Sessions</c> reports it.</summary>
public sealed class EmbySessionInfo
{
    public string Id { get; set; } = "";

    public string? UserId { get; set; }

    public string? UserName { get; set; }

    /// <summary>The application name it authenticated with: "Emby Theater", "Emby for Android".</summary>
    public string? Client { get; set; }

    public string? DeviceName { get; set; }

    public string? DeviceId { get; set; }

    public string? ApplicationVersion { get; set; }

    public string? RemoteEndPoint { get; set; }

    public DateTimeOffset? LastActivityDate { get; set; }

    public bool SupportsRemoteControl { get; set; }

    public EmbyItem? NowPlayingItem { get; set; }

    public PlayerStateInfo? PlayState { get; set; }

    public EmbyTranscodingInfo? TranscodingInfo { get; set; }

    [JsonIgnore]
    public bool IsPlaying => NowPlayingItem is not null;

    [JsonIgnore]
    public long PositionTicks => PlayState?.PositionTicks ?? 0;

    /// <summary>0..1 through the current file; 0 when nothing is playing or the runtime is unknown.</summary>
    [JsonIgnore]
    public double ProgressFraction
    {
        get
        {
            var total = NowPlayingItem?.RunTimeTicks ?? 0;
            return total <= 0 ? 0 : Math.Clamp(PositionTicks / (double)total, 0, 1);
        }
    }

    /// <summary>"Emby Theater 3.0.20  ·  SB" — what a person recognises the session by.</summary>
    [JsonIgnore]
    public string DeviceLabel
    {
        get
        {
            var parts = new List<string>(3);
            var client = string.Join(" ", new[] { Client, ApplicationVersion }.Where(part => !string.IsNullOrWhiteSpace(part)));
            if (client.Length > 0) parts.Add(client);
            if (!string.IsNullOrWhiteSpace(DeviceName)) parts.Add(DeviceName!);
            if (!string.IsNullOrWhiteSpace(RemoteEndPoint)) parts.Add(RemoteEndPoint!);
            return string.Join("  ·  ", parts);
        }
    }
}

/// <summary>One line of the server's activity log.</summary>
public sealed class EmbyActivityEntry
{
    public string Name { get; set; } = "";

    public string? Overview { get; set; }

    /// <summary>The one-line form; the server puts the detail in <see cref="Overview"/>.</summary>
    public string? ShortOverview { get; set; }

    /// <summary>The event kind: "AuthenticationFailed", "VideoPlayback", "SessionStarted", …</summary>
    public string? Type { get; set; }

    public string? ItemId { get; set; }

    public DateTimeOffset? Date { get; set; }

    public string? UserId { get; set; }

    /// <summary>"Debug", "Info", "Warn", "Error" or "Fatal".</summary>
    [JsonConverter(typeof(LooseStringConverter))]
    public string? Severity { get; set; }
}

public sealed class EmbyActivityResult
{
    public List<EmbyActivityEntry> Items { get; set; } = [];

    public int TotalRecordCount { get; set; }
}

/// <summary>
/// Reads a JSON string, number or boolean as a <see cref="string"/>. Emby writes its enums as names,
/// but the activity log's severity is a number in some builds, and a whole feed is not worth losing
/// to one field's spelling.
/// </summary>
internal sealed class LooseStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException($"无法把 {reader.TokenType} 读成字符串")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
