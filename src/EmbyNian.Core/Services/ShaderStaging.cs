using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Mpv;

namespace EmbyNian.Services;

/// <summary>
/// The files mpv needs on disk before it is handed a shader path: the shader collection itself and
/// libmpv's sibling DLLs. A service rather than a method on the composition root because it has state —
/// it runs at most once per session — and because the thing that needs it, the backend factory, can now
/// say so in its constructor instead of reaching back into the root that built it.
/// <para>
/// No interface: there is one implementation, nothing substitutes it, and the shader files either exist on
/// disk or do not. See <see cref="ISettingsService"/> for the case where one is worth having.
/// </para>
/// </summary>
public sealed class ShaderStaging(AppSettings settings)
{
    private const string Category = "app";

    private bool _ready;

    /// <summary>
    /// The 着色器配置组 the settings page offers. Shipped C# data rather than something scanned off disk:
    /// the client no longer reads any mpv config file, so a group is a list of shader files plus the
    /// scalers that go with it, applied as ordinary mpv options.
    /// </summary>
    public IReadOnlyList<ShaderGroup> Catalog => ShaderGroupCatalog.All;

    /// <summary>
    /// The shader files ship with the program, so both backends load them from the same absolute paths and
    /// nothing depends on an mpv config directory existing.
    /// </summary>
    public string ShaderDirectory => ShaderGroupCatalog.ShaderRoot;

    /// <summary>Whether the embedded libmpv backend is the one that plays next.</summary>
    private bool Embedded => settings.Mpv.Backend == MpvBackendKind.BuiltInLibMpv;

    /// <summary>
    /// Brings the shader files and libmpv's own dll dependencies into the program folder, from whichever
    /// mpv installation the settings point at. A published build already ships them, so this only ever
    /// fills a gap — and once filled, that folder is never read again.
    /// <para>
    /// Runs at most once per session, on the way into the first playback rather than during startup. The
    /// caller is already off the UI thread by then, and the work has to have happened before mpv is handed
    /// a shader path, which is the one ordering that actually matters.
    /// </para>
    /// </summary>
    public void Ensure()
    {
        if (_ready) return;
        _ready = true;

        var externalMpvRoot = Path.GetDirectoryName(settings.Mpv.ExecutablePath) ?? "";

        try
        {
            Directory.CreateDirectory(ShaderDirectory);

            // libmpv-2.dll needs siblings like lua51.dll and vulkan-1.dll next to it; the external mpv
            // installation has them beside mpv.exe, so bring those along once.
            if (Embedded && Directory.Exists(externalMpvRoot))
            {
                foreach (var dll in Directory.EnumerateFiles(externalMpvRoot, "*.dll", SearchOption.TopDirectoryOnly))
                    CopyFileOnce(dll, Path.Combine(AppContext.BaseDirectory, Path.GetFileName(dll)));
            }

            // The user's own shader collection lives under their mpv config directory, which is where
            // these files came from in the first place.
            var externalShaders = Path.Combine(externalMpvRoot, "portable_config", "shaders");
            if (Directory.Exists(externalShaders)) CopyTreeOnce(externalShaders, ShaderDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warn(Category, "准备着色器文件失败", error);
        }

        if (ShaderGroupCatalog.MissingShaderFiles() is { Count: > 0 } missing)
        {
            Log.Warn(Category, $"有 {missing.Count} 个着色器文件缺失，相关配置组会被 mpv 忽略：{string.Join("、", missing.Take(5))}");
        }
    }

    /// <summary>
    /// Copies a file only when it is missing; an app update must not overwrite what the user has already
    /// tuned inside the program folder.
    /// </summary>
    private static void CopyFileOnce(string source, string target)
    {
        if (!File.Exists(source) || File.Exists(target)) return;
        File.Copy(source, target);
        Log.Info(Category, $"已内置 {Path.GetFileName(target)}");
    }

    private static void CopyTreeOnce(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            if (File.Exists(destination)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
