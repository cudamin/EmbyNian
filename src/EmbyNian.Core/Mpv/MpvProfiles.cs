using System.Text.Json;

namespace EmbyNian.Mpv;

/// <summary>Expands the running player's profile-list without copying version-specific defaults.</summary>
public static class MpvProfiles
{
    public static IReadOnlyList<KeyValuePair<string, string>> Expand(
        IReadOnlyList<KeyValuePair<string, string>> options, string? profilesJson)
    {
        if (!options.Any(option => option.Key == "profile")) return options;
        if (string.IsNullOrWhiteSpace(profilesJson))
            throw new InvalidOperationException("播放器没有提供画质预设内容，无法安全恢复着色器基线");

        using var document = JsonDocument.Parse(profilesJson);
        var profiles = document.RootElement.EnumerateArray()
            .Where(profile => profile.TryGetProperty("name", out _) && profile.TryGetProperty("options", out _))
            .ToDictionary(profile => profile.GetProperty("name").GetString()!,
                profile => profile.GetProperty("options"), StringComparer.Ordinal);
        var expanded = new List<KeyValuePair<string, string>>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, value) in options) Add(name, value);
        return expanded;

        void Add(string name, string value)
        {
            if (name != "profile")
            {
                expanded.Add(new(name, value));
                return;
            }

            foreach (var profileName in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!profiles.TryGetValue(profileName, out var profile) || !visiting.Add(profileName))
                    throw new InvalidOperationException($"画质预设 {profileName} 缺失或循环引用");
                foreach (var option in profile.EnumerateArray())
                    Add(option.GetProperty("key").GetString()!, option.GetProperty("value").GetString()!);
                visiting.Remove(profileName);
            }
        }
    }
}
