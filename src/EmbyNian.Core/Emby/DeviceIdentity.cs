namespace EmbyNian.Emby;

/// <summary>
/// What this app calls itself when talking to Emby. A stable <see cref="DeviceId"/>
/// lets the server recognise the same client across restarts instead of piling up
/// one "device" per launch.
/// </summary>
public sealed record DeviceIdentity(string Client, string DeviceName, string DeviceId, string Version)
{
    public const string ClientName = "EmbyNian";

    public static DeviceIdentity Create(string deviceId, string version) =>
        new(ClientName, Environment.MachineName, deviceId, version);

    /// <summary>
    /// Emby parses this header by splitting on quotes, so any quote or control
    /// character in a machine name would corrupt every request.
    /// </summary>
    public string ToAuthorizationHeader()
    {
        return $"MediaBrowser Client=\"{Sanitize(Client)}\", Device=\"{Sanitize(DeviceName)}\", " +
               $"DeviceId=\"{Sanitize(DeviceId)}\", Version=\"{Sanitize(Version)}\"";
    }

    internal static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var buffer = new char[value.Length];
        var length = 0;
        foreach (var character in value)
        {
            if (character is '"' or '\\' or ',' or ';') continue;
            if (char.IsControl(character)) continue;
            buffer[length++] = character;
        }

        var result = new string(buffer, 0, length).Trim();
        return result.Length == 0 ? "unknown" : result;
    }
}
