using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Mpv;

namespace EmbyNian.Services;

/// <summary>
/// The files mpv needs on disk before it is handed a shader path. A service rather than a method on the
/// composition root because it has state — it runs at most once per session — and because the thing that
/// needs it, the backend factory, can now say so in its constructor instead of reaching back into the root
/// that built it.
/// <para>
/// It used to copy files in from the user's own portable mpv installation, because that was the only place
/// they existed: first the shader collection, then just libmpv's sibling dlls. Both ship with the program
/// now — <c>assets/shaders</c> and <c>assets/mpv-runtime</c> in the repository, copied next to the exe by
/// an ordinary build — so nothing here reads that folder any more and there is nothing left to bring in.
/// What is left is the one thing a build cannot promise: saying so when a file is missing after all.
/// </para>
/// <para>
/// No interface: there is one implementation, nothing substitutes it, and the shader files either exist on
/// disk or do not. See <see cref="ISettingsService"/> for the case where one is worth having.
/// </para>
/// </summary>
public sealed class ShaderStaging(AppSettings settings)
{
    private const string Category = "app";

    private bool _checked;

    /// <summary>
    /// The 着色器档位 the settings page and the player's own menu offer: the eight cells of the table that
    /// this machine's 显卡档 selects. Shipped C# data rather than something scanned off disk — the client
    /// reads no mpv config file — and a projection rather than a copy, so there is no second list to keep in
    /// step when 显卡档 changes.
    /// </summary>
    public IReadOnlyList<ShaderGroup> Catalog => ShaderGroupCatalog.For(settings.Shaders.Gpu);

    /// <summary>
    /// The shader files ship with the program, so both backends load them from the same absolute paths and
    /// nothing depends on an mpv config directory existing.
    /// </summary>
    public string ShaderDirectory => ShaderGroupCatalog.ShaderRoot;

    /// <summary>
    /// Says so in the log when a file the 档位表 names is not next to the exe. It cannot fix it, and that is
    /// the point: the files arrive through the build now, so a gap here means the build output is broken
    /// rather than a machine that needs topping up.
    /// <para>
    /// Runs at most once per session, on the way into the first playback rather than during startup. The
    /// caller is already off the UI thread by then, and the line has to be in the log before mpv is handed
    /// the chain, which is the one ordering that actually matters — mpv itself only reports a missing shader
    /// once per frame, while on screen the picture merely 「looks a bit off」.
    /// </para>
    /// </summary>
    public void Ensure()
    {
        if (_checked) return;
        _checked = true;

        if (ShaderGroupCatalog.MissingShaderFiles() is { Count: > 0 } missing)
        {
            Log.Warn(Category, $"有 {missing.Count} 个着色器文件缺失，相关档位会被 mpv 忽略：{string.Join("、", missing.Take(5))}");
        }
    }
}
