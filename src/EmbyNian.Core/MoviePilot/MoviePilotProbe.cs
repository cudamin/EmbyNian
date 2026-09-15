using System.Text.Json;

namespace EmbyNian.MoviePilot;

/// <summary>
/// The connection test behind the settings card's 「测试连接」 button: sign in, then ask the three endpoints
/// that together say whether this MoviePilot is one this app can work with.
/// <para>
/// The three calls are what they are because no single endpoint reports all of it — the version comes from
/// the dashboard, and the configured media servers and downloaders each have their own endpoint. Doing it
/// here rather than in the view model is the usual rule: assembling one reported result out of three
/// responses is a judgment with a right answer, and a view model is a place nothing tests can reach.
/// </para>
/// <para>
/// Every call after the sign-in is best-effort on purpose. Version is the one fact that must be there — it
/// is the proof the connection works at all — but a MoviePilot with no downloader configured is a perfectly
/// normal installation, and failing the whole test because one list came back empty would turn a working
/// setup into an error message.
/// </para>
/// </summary>
public sealed class MoviePilotProbe(MoviePilotClient client)
{
    public async Task<MoviePilotStatus> RunAsync(
        Uri apiBase,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var session = await client.SignInAsync(apiBase, username, password, cancellationToken).ConfigureAwait(false);

        var system = await client
            .GetAsync(apiBase, session.AccessToken, "dashboard/system", cancellationToken)
            .ConfigureAwait(false);

        var servers = await NamesAsync(apiBase, session.AccessToken, "mediaserver/clients", cancellationToken)
            .ConfigureAwait(false);
        var downloaders = await NamesAsync(apiBase, session.AccessToken, "download/clients", cancellationToken)
            .ConfigureAwait(false);

        return new MoviePilotStatus(
            Text(system, "version") ?? "未知版本",
            Text(system, "hostname") ?? "",
            Text(system, "operating_system") ?? "",
            servers,
            downloaders);
    }

    /// <summary>
    /// The <c>name</c> of every entry in a list endpoint. Swallows its own failures: these two are the
    /// 「额外的信息」 half of the report, and a version that came back is already proof of a working
    /// connection.
    /// </summary>
    private async Task<IReadOnlyList<string>> NamesAsync(
        Uri apiBase,
        string token,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await client.GetAsync(apiBase, token, path, cancellationToken).ConfigureAwait(false);
            return Names(data);
        }
        catch (Exception error) when (error is MoviePilotException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Names out of either shape these endpoints use — a list of objects, or a single object. Kept apart
    /// from the call above because it is the part with a right answer per input, and it is what a test can
    /// hold down without a server.
    /// </summary>
    internal static IReadOnlyList<string> Names(JsonElement data)
    {
        var names = new List<string>();

        switch (data.ValueKind)
        {
            case JsonValueKind.Array:
                names.AddRange(data.EnumerateArray().Select(Text).OfType<string>());
                break;
            case JsonValueKind.Object:
                if (Text(data) is { } single) names.Add(single);
                break;
        }

        return names;
    }

    /// <summary>One entry's display name, or null when it has none worth showing.</summary>
    private static string? Text(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;

        foreach (var key in (string[])["name", "type"])
        {
            if (!entry.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind != JsonValueKind.String) continue;

            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return null;
    }

    /// <summary>A named string member of an object, or null when it is missing or not a string.</summary>
    private static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
