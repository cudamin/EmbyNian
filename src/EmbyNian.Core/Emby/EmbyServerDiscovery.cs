using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace EmbyNian.Emby;

/// <summary>
/// A server announced by Emby's UDP discovery endpoint.  <see cref="Url"/> is the
/// user-facing address (without the <c>/emby/</c> API suffix); the address has already
/// been checked and normalised by the parser.
/// </summary>
public sealed record EmbyDiscoveredServer
{
    public required string Name { get; init; }

    public required string Url { get; init; }

    public Uri ApiBase { get; init; } = null!;

    public string? Id { get; init; }

    public string? Version { get; init; }

    /// <summary>The UDP endpoint that answered. Useful for diagnostics, not persisted.</summary>
    public IPEndPoint? Source { get; init; }

    public string VersionLabel => string.IsNullOrWhiteSpace(Version) ? "" : $"Emby {Version}";

    public string DisplayName => string.IsNullOrWhiteSpace(VersionLabel)
        ? $"{Name} — {Url}"
        : $"{Name} — {Url}（{VersionLabel}）";
}

/// <summary>
/// Emby-compatible LAN discovery over UDP port 7359.
///
/// Emby versions in the wild have emitted both an HTTP-header response and a JSON
/// response.  The parser intentionally accepts both forms, while only producing
/// HTTP/HTTPS addresses and bounded strings.  This keeps random broadcast traffic
/// from becoming an invalid server profile.
/// </summary>
public sealed class EmbyServerDiscovery
{
    public const int DiscoveryPort = 7359;
    public const int DefaultHttpPort = 8096;
    public const int DefaultHttpsPort = 8920;
    public const string ProbeMessage = "who is EmbyServer?";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    /// <summary>
    /// Broadcasts the Emby probe and collects replies until <paramref name="timeout"/>
    /// expires. A missing network adapter or a blocked broadcast is an empty result,
    /// not a login failure.
    /// </summary>
    public async Task<IReadOnlyList<EmbyDiscoveredServer>> DiscoverAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var duration = timeout.GetValueOrDefault(DefaultTimeout);
        if (duration <= TimeSpan.Zero) return [];

        using var udp = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };

        var probe = Encoding.UTF8.GetBytes(ProbeMessage);
        var destinations = BroadcastDestinations().ToList();

        // Always try the global broadcast. Directed broadcasts cover networks where
        // Windows does not route 255.255.255.255 through every adapter.
        if (!destinations.Any(endpoint => endpoint.Address.Equals(IPAddress.Broadcast)))
            destinations.Insert(0, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));

        foreach (var destination in destinations)
        {
            try
            {
                await udp.SendAsync(probe, probe.Length, destination).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // One disconnected/VPN adapter must not prevent the other adapters
                // from receiving a response.
            }
        }

        var found = new Dictionary<string, EmbyDiscoveredServer>(StringComparer.OrdinalIgnoreCase);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);

        while (!deadline.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            if (!TryParseResponse(packet.Buffer, packet.RemoteEndPoint, out var server)) continue;

            var key = DiscoveryKey(server);
            if (!found.ContainsKey(key)) found.Add(key, server);
        }

        return found.Values
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(server => server.Url, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Parses one UDP response. Kept public and pure so protocol changes can be
    /// tested offline without opening a socket.
    /// </summary>
    public static bool TryParseResponse(
        ReadOnlySpan<byte> payload,
        IPEndPoint? source,
        out EmbyDiscoveredServer server)
    {
        server = null!;
        if (payload.Length == 0 || payload.Length > 64 * 1024) return false;

        var text = Encoding.UTF8.GetString(payload).Trim('\0', ' ', '\r', '\n', '\t');
        if (text.Length == 0) return false;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var recognised = false;
        var body = text;

        // Most Emby builds answer with an HTTP-like header block. Header names are
        // deliberately treated case-insensitively; a few forks changed casing.
        var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var separatorLength = 4;
        if (separator < 0)
        {
            separator = text.IndexOf("\n\n", StringComparison.Ordinal);
            separatorLength = 2;
        }

        if (separator >= 0)
        {
            var headers = text[..separator];
            body = text[(separator + separatorLength)..];
            ParseHeaderLines(headers, fields, ref recognised);

            // A valid HTTP status plus an Emby server marker is enough to use the
            // sender address when older servers omit the explicit address header.
            recognised |= headers.Contains("EmbyServer", StringComparison.OrdinalIgnoreCase) ||
                          headers.Contains("Emby-Server", StringComparison.OrdinalIgnoreCase);
        }
        else if (!text.TrimStart().StartsWith('{'))
        {
            // Some Emby releases end the datagram after the last header and omit
            // the conventional blank line. Treat the complete payload as headers;
            // this is also how the original Emby Theater client handled replies.
            ParseHeaderLines(text, fields, ref recognised);
            recognised |= text.Contains("EmbyServer", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("Emby-Server", StringComparison.OrdinalIgnoreCase);
        }

        // Newer builds/forks put the same values in a JSON body. Parse only objects;
        // arbitrary arrays/strings are not discovery announcements.
        if (body.TrimStart().StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(body, new JsonDocumentOptions
                {
                    MaxDepth = 16,
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in json.RootElement.EnumerateObject())
                    {
                        string? value = property.Value.ValueKind switch
                        {
                            JsonValueKind.String => property.Value.GetString(),
                            JsonValueKind.Number => property.Value.GetRawText(),
                            _ => null
                        };
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        fields[property.Name] = value.Trim();
                        recognised |= IsDiscoveryField(property.Name);
                    }
                }
            }
            catch (JsonException)
            {
                // Header-only responses commonly have an empty/non-JSON body.
                // Treat malformed body text as absent rather than rejecting headers.
            }
        }

        if (!recognised) return false;

        // EndpointAddress is the spelling used by current Emby servers; the older
        // Address/Url names remain supported for compatible forks.
        var addressText = First(fields,
            "Emby-Server-Address", "X-Emby-Server-Address", "EndpointAddress", "ServerAddress",
            "LocalAddress", "Address", "Url", "URL", "Location");
        var protocol = First(fields, "Emby-Server-Protocol", "X-Emby-Server-Protocol", "Protocol", "Scheme");
        var portText = First(fields, "Emby-Server-Port", "X-Emby-Server-Port", "ServerPort", "Port");
        var name = First(fields, "Emby-Server-Name", "X-Emby-Server-Name", "ServerName", "Name") ?? "";
        var id = First(fields, "Emby-Server-Id", "X-Emby-Server-Id", "ServerId", "Id");
        var version = First(fields, "Emby-Server-Version", "X-Emby-Server-Version", "ServerVersion", "Version");

        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535) port = 0;
        var scheme = protocol?.Trim().ToLowerInvariant() switch
        {
            "https" or "ssl" => Uri.UriSchemeHttps,
            _ => Uri.UriSchemeHttp
        };

        // If only EndpointAddress/Address is present, its scheme/port are the
        // strongest signal and override protocol defaults in TryBuildAddress.
        if (!TryBuildAddress(addressText, scheme, port, source?.Address, out var apiBase)) return false;

        var displayUrl = EmbyServerAddress.ToDisplayString(apiBase);
        if (name.Length == 0) name = apiBase.Host;

        server = new EmbyDiscoveredServer
        {
            Name = Limit(name, 160),
            Url = displayUrl,
            ApiBase = apiBase,
            Id = LimitOptional(id, 128),
            Version = LimitOptional(version, 64),
            Source = source
        };
        return true;
    }

    private static bool TryBuildAddress(
        string? advertised,
        string scheme,
        int port,
        IPAddress? sourceAddress,
        out Uri apiBase)
    {
        apiBase = null!;
        var text = advertised?.Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            if (sourceAddress is null) return false;
            text = sourceAddress.ToString();
        }

        if (!text.Contains("://", StringComparison.Ordinal)) text = scheme + "://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(parsed.Host)) return false;

        // A separate port field is authoritative only when the address did not carry
        // one. This handles both `Address=host` and `Address=http://host` responses.
        if (parsed.IsDefaultPort && port > 0)
        {
            var builder = new UriBuilder(parsed) { Port = port };
            parsed = builder.Uri;
        }

        try
        {
            apiBase = EmbyServerAddress.Normalize(parsed.AbsoluteUri);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IEnumerable<IPEndPoint> BroadcastDestinations()
    {
        yield return new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);

        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }

        foreach (var adapter in adapters)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            IPInterfaceProperties properties;
            try { properties = adapter.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            foreach (var address in properties.UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null)
                    continue;

                var ip = address.Address.GetAddressBytes();
                var mask = address.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < broadcast.Length; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPEndPoint(new IPAddress(broadcast), DiscoveryPort);
            }
        }
    }

    private static bool IsDiscoveryField(string key) =>
        key.Contains("Emby", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Address", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("EndpointAddress", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("LocalAddress", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Url", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("URL", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Location", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ServerName", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ServerId", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Version", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ServerVersion", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Port", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ServerPort", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Protocol", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Scheme", StringComparison.OrdinalIgnoreCase);

    private static void ParseHeaderLines(
        string headers,
        IDictionary<string, string> fields,
        ref bool recognised)
    {
        foreach (var line in headers.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0) continue;
            fields[key] = value;
            recognised |= IsDiscoveryField(key);
        }
    }

    private static string? First(IReadOnlyDictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? LimitOptional(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : Limit(value.Trim(), max);

    private static string DiscoveryKey(EmbyDiscoveredServer server) =>
        !string.IsNullOrWhiteSpace(server.Id) ? "id:" + server.Id : "url:" + server.ApiBase.Authority + server.ApiBase.AbsolutePath;
}
