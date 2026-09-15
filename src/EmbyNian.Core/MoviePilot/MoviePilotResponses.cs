using System.Text.Json.Serialization;

namespace EmbyNian.MoviePilot;

/// <summary>
/// The body <c>POST api/v1/login/access-token</c> answers with. Only the members this app reads are here;
/// MoviePilot also returns an avatar URL, a numeric level and a permissions map, none of which the settings
/// card shows and none of which anything else needs yet.
/// </summary>
public sealed class MoviePilotSignInResult
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    /// <summary>
    /// Whether this account is the super admin. Not cosmetic: most of the API refuses a non-admin account
    /// with 「SUPERUSER 对应用户不存在、未启用或非超级管理员」, so this is the difference between a
    /// working connection and one that authenticates and then fails everything.
    /// </summary>
    [JsonPropertyName("super_user")]
    public bool SuperUser { get; set; }

    [JsonPropertyName("user_name")]
    public string? UserName { get; set; }

    [JsonPropertyName("user_id")]
    public int UserId { get; set; }
}

/// <summary>
/// What the settings card shows after a successful connection test. Assembled by
/// <see cref="MoviePilotProbe"/> out of three separate calls, because no single endpoint reports all of it.
/// </summary>
public sealed record MoviePilotStatus(
    string Version,
    string HostName,
    string OperatingSystem,
    IReadOnlyList<string> MediaServers,
    IReadOnlyList<string> Downloaders);
