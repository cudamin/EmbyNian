namespace EmbyNian.Infrastructure;

/// <summary>
/// Every on-disk location the app owns, resolved from a single root so tests can
/// point the whole app at a temp directory.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EmbyNian"));

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string SettingsBackupFile => Path.Combine(Root, "settings.backup.json");

    public string ImageCacheDirectory => Path.Combine(Root, "cache", "images");

    /// <summary>
    /// Where mpv keeps its compiled shader cache. It used to land in the mpv config directory via
    /// <c>gpu-shader-cache-dir="~~/cache/shaders_cache"</c>; with no config directory left, the client
    /// has to name a real one or every playback recompiles the whole chain.
    /// </summary>
    public string ShaderCacheDirectory => Path.Combine(Root, "cache", "shaders");

    public string LogDirectory => Path.Combine(Root, "logs");

    /// <summary>
    /// Where 截图 land.
    /// <para>
    /// This directory is the whole reason the client has a screenshot feature at all. The commands always
    /// worked; what was missing was somewhere to put the files — under <c>--no-config</c> mpv has no
    /// <c>screenshot-directory</c>, so they landed beside the executable and nobody was told where. Named
    /// here, next to the log and cache directories, and 设置 → 关于 shows the path with a button that opens it.
    /// </para>
    /// </summary>
    public string ScreenshotDirectory => Path.Combine(Root, "screenshots");

    /// <summary>
    /// Where the embedded Emby console's WebView2 keeps its profile. Named explicitly because a
    /// non-packaged app otherwise gets <c>&lt;exe&gt;.WebView2</c> beside the executable, which in the
    /// publish directory may not be writable — and because that profile holds Emby cookies and the web
    /// client's own localStorage, so it belongs with the rest of this app's data.
    /// <para>
    /// Deliberately not in <see cref="EnsureCreated"/>: most launches never open the console, and an
    /// empty profile directory would then be created for nothing. The page creates it when it needs it.
    /// </para>
    /// </summary>
    public string WebViewDirectory => Path.Combine(Root, "webview2");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ImageCacheDirectory);
        Directory.CreateDirectory(ShaderCacheDirectory);
        Directory.CreateDirectory(LogDirectory);

        // Created up front rather than on the first screenshot: mpv creates it itself if it can, but a
        // failure there is a warning in a log nobody is reading while a film plays — and the 关于 card's
        // 「打开」 button has to have somewhere to open from the first launch.
        Directory.CreateDirectory(ScreenshotDirectory);
    }

    /// <summary>
    /// Carries a previous release's data directory over to this one, once.
    /// <para>
    /// The app was called EmbyMpvClient up to v2, so everything a user cares about — server
    /// profiles, the DPAPI-wrapped password and access token, the persisted DeviceId that stops
    /// Emby registering a new device per launch, window geometry, mpv settings and the poster and
    /// shader caches — sits under the old name. Without this the renamed build starts on an empty
    /// folder and silently looks like a fresh install.
    /// </para>
    /// <para>
    /// Gated on <see cref="SettingsFile"/> being absent rather than on the root being absent:
    /// <see cref="EnsureCreated"/> runs while logging starts up, so by the time anything reads
    /// settings the new directory already exists. Copies rather than moves, so rolling back to the
    /// old build is still possible, and never throws — a failed migration must not stop startup.
    /// </para>
    /// </summary>
    /// <returns>The directory migrated from, or null when nothing was carried over.</returns>
    public string? MigrateFrom(string legacyRoot)
    {
        if (string.IsNullOrWhiteSpace(legacyRoot)) return null;

        try
        {
            legacyRoot = Path.GetFullPath(legacyRoot);
            if (string.Equals(legacyRoot, Root, StringComparison.OrdinalIgnoreCase)) return null;
            if (File.Exists(SettingsFile)) return null;
            if (!File.Exists(Path.Combine(legacyRoot, "settings.json"))) return null;

            CopyTree(legacyRoot, Root);
            return legacyRoot;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Carries a previous release's data over, from the first candidate that has any.
    /// <para>
    /// Order matters and is the caller's: <see cref="PriorRoots"/> puts the unpackaged build's directory
    /// first, because that is the one a user actually has data in today, and the two old product names
    /// after it. The first candidate holding a <c>settings.json</c> wins; once anything has been copied
    /// <see cref="MigrateFrom"/> refuses the rest by itself, since it is gated on this root not having
    /// a settings file yet.
    /// </para>
    /// </summary>
    /// <returns>The directory migrated from, or null when nothing was carried over.</returns>
    public string? MigrateFromAny(IEnumerable<string> priorRoots)
    {
        foreach (var candidate in priorRoots)
            if (MigrateFrom(candidate) is { } migrated) return migrated;

        return null;
    }

    /// <summary>
    /// Every directory this install might inherit data from, best first.
    /// <para>
    /// The interesting one is the first: packaging the app as MSIX can move
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> into the package's own
    /// <c>LocalCache\Local</c>, and then a user who has been running the loose build starts the packaged
    /// one on an empty folder — no servers, no saved password, a new device id registered with Emby.
    /// <see cref="UnvirtualizeLocalAppData"/> works out where the loose build's data is and it is tried
    /// first. When the app is not packaged, or the platform hands back the real path anyway, that
    /// candidate is simply absent and the list is what it always was.
    /// </para>
    /// </summary>
    public static IEnumerable<string> PriorRoots
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (UnvirtualizeLocalAppData(localAppData) is { } real)
                yield return Path.Combine(real, "EmbyNian");

            foreach (var name in (string[])["EmbyGearless", "EmbyMpvClient"])
                yield return Path.Combine(localAppData, name);
        }
    }

    /// <summary>
    /// Turns a packaged app's redirected LocalAppData back into the machine's real one, or returns null
    /// when the path was not redirected.
    /// <para>
    /// MSIX gives a package its own view at
    /// <c>&lt;LocalAppData&gt;\Packages\&lt;PackageFamilyName&gt;\LocalCache\Local</c>. Recognising it by
    /// that shape — the three fixed trailing segments and a <c>Packages</c> before the family name —
    /// rather than by asking for package identity keeps this a pure function: Core takes no Windows App
    /// SDK reference, and this is the one judgment in the migration that has a single right answer per
    /// input, so it is the part worth a test rather than a comment.
    /// </para>
    /// </summary>
    public static string? UnvirtualizeLocalAppData(string localAppData)
    {
        if (string.IsNullOrWhiteSpace(localAppData)) return null;

        var parts = Path.GetFullPath(localAppData)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        // …\Packages\<family>\LocalCache\Local — five segments from the tail, and the real root is
        // whatever sat in front of them.
        if (parts.Length < 5) return null;
        if (!parts[^1].Equals("Local", StringComparison.OrdinalIgnoreCase)) return null;
        if (!parts[^2].Equals("LocalCache", StringComparison.OrdinalIgnoreCase)) return null;
        if (!parts[^4].Equals("Packages", StringComparison.OrdinalIgnoreCase)) return null;

        var real = string.Join(Path.DirectorySeparatorChar, parts[..^4]);
        // A rooted path lost its trailing separator to the split ("C:" is not "C:\").
        if (real.Length == 2 && real[1] == ':') real += Path.DirectorySeparatorChar;
        return real;
    }

    /// <summary>
    /// The data directory of the release this one was renamed from, used by <see cref="MigrateFrom"/>.
    /// v3 shipped as EmbyGearless between the EmbyMpvClient v2 name and this one, so its directory is
    /// tried first; the v2 name stays as the fallback for machines that never ran v3.
    /// </summary>
    public static string LegacyDefaultRoot
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var name in (string[])["EmbyGearless", "EmbyMpvClient"])
            {
                var candidate = Path.Combine(localAppData, name);
                if (Directory.Exists(candidate)) return candidate;
            }
            return Path.Combine(localAppData, "EmbyMpvClient");
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.EnumerateFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            try { File.Copy(file, target); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var directory in Directory.EnumerateDirectories(from))
            CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
    }
}
