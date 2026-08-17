using EmbyMpvClient.Diagnostics;

namespace EmbyMpvClient.Mpv;

/// <summary>
/// One shader configuration group: an mpv profile whose body assigns <c>glsl-shaders</c>.
/// The user's mpv.conf calls these 「着色器配置组」 and normally activates exactly one with a
/// global <c>profile=</c> line; this client overrides that per item on the command line.
/// </summary>
public sealed record ShaderProfile(string Name, string? Description, IReadOnlyList<string> Shaders, string SourceFile)
{
    /// <summary>Shader file names without directories, for a compact tooltip.</summary>
    public IEnumerable<string> ShaderFileNames =>
        Shaders.Select(shader => shader.Split('/', '\\').LastOrDefault() ?? shader);

    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : $"{Name} — {Description}";

    public override string ToString() => DisplayName;
}

/// <summary>Finds the shader groups declared across a set of mpv config files.</summary>
public static class ShaderProfileCatalog
{
    private const string Category = "mpv";

    public const string ShaderOption = "glsl-shaders";

    /// <summary>
    /// Scans the given files in order; a group defined later replaces an earlier one of the
    /// same name, matching how mpv itself treats a redefined profile.
    /// </summary>
    public static IReadOnlyList<ShaderProfile> Scan(IEnumerable<string> configPaths)
    {
        var found = new Dictionary<string, ShaderProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in configPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

            try
            {
                foreach (var profile in Scan(MpvConfigDocument.Load(path), path))
                    found[profile.Name] = profile;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Warn(Category, $"读取配置文件失败：{path}", error);
            }
        }

        return [.. found.Values.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Profiles that assign at least one shader. A profile whose body is
    /// <c>glsl-shaders=""</c> exists to *clear* shaders (the user's [8k-fix] does this) and is
    /// not something to offer as a shader group, so it is left out.
    /// </summary>
    public static IReadOnlyList<ShaderProfile> Scan(MpvConfigDocument document, string sourceFile = "")
    {
        var profiles = new List<ShaderProfile>();

        foreach (var section in document.Sections.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var shaders = document.GetValue(ShaderOption, section);
            if (shaders is null) continue;

            var entries = SplitShaderList(shaders);
            if (entries.Count == 0) continue;

            // mpv applies every profile-desc it sees, so the last one is what it ends up with;
            // the user's config sometimes has both an ASCII name and a Chinese description.
            var description = document.LinesIn(section)
                .Where(line => line.IsEnabled && line.Key == "profile-desc")
                .Select(line => Unquote(line.Value))
                .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));

            profiles.Add(new ShaderProfile(section, description, entries, sourceFile));
        }

        return profiles;
    }

    /// <summary>mpv separates entries with <c>;</c> (or <c>:</c> on Unix) and allows quoting.</summary>
    internal static IReadOnlyList<string> SplitShaderList(string value)
    {
        var unquoted = Unquote(value);
        if (unquoted.Length == 0) return [];
        return [.. unquoted.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    internal static string Unquote(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
        return text.Trim();
    }
}
