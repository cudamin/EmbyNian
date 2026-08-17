using EmbyMpvClient.App.Security;
using EmbyMpvClient.Configuration;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Emby;
using EmbyMpvClient.Infrastructure;
using EmbyMpvClient.Mpv;
using EmbyMpvClient.Playback;

namespace EmbyMpvClient.App.Composition;

/// <summary>
/// The composition root: builds every long-lived object once, in dependency order, and hands
/// them to the shell. Written by hand rather than with a container — there are a dozen objects
/// and the wiring is the clearest documentation of how the app fits together.
/// </summary>
public sealed class AppHost : IDisposable
{
    private const string Category = "app";

    private AppHost(AppPaths paths, SettingsStore store, AppSettings settings)
    {
        Paths = paths;
        Store = store;
        Settings = settings;

        Vault = new CredentialVault(DpapiSecretProtector.Instance);
        Device = DeviceIdentity.Create(settings.DeviceId, AppInfo.Version);
        Session = new EmbySession(settings, store, Vault, Device);
        Images = new EmbyImageStore(Session, paths.ImageCacheDirectory);

        Shaders = new ShaderProfileResolver(settings.Shaders);
        Playback = new PlaybackService(Session, settings, CreateBackend, new PlaybackPlanner(settings, Shaders));

        RefreshShaderCatalog();
    }

    public static AppHost Create()
    {
        var paths = AppPaths.Default;
        paths.EnsureCreated();

        var store = new SettingsStore(paths, DpapiSecretProtector.Instance);
        var settings = store.Load().EnsureDeviceId();

        var host = new AppHost(paths, store, settings);
        host.EnsureShaderPack();
        return host;
    }

    public AppPaths Paths { get; }

    public SettingsStore Store { get; }

    public AppSettings Settings { get; }

    public CredentialVault Vault { get; }

    public DeviceIdentity Device { get; }

    public EmbySession Session { get; }

    public EmbyImageStore Images { get; }

    /// <summary>
    /// The window mpv draws into while playing embedded; the shell hands it to the host as soon
    /// as its handle exists. Null means the embedded backend refuses to start.
    /// </summary>
    public Func<IntPtr>? EmbeddedWindow { get; set; }

    /// <summary>
    /// Builds the player the settings ask for. Called once per playback, so switching between
    /// embedded libmpv and the standalone mpv.exe takes effect on the very next play.
    /// </summary>
    private IPlaybackBackend CreateBackend() => Settings.Mpv.Backend switch
    {
        MpvBackendKind.ExternalMpv => new MpvProcessBackend(Settings.Mpv),
        _ => new LibMpvBackend(Settings.Mpv, () => EmbeddedWindow?.Invoke() ?? IntPtr.Zero, MpvConfigDirectory)
    };

    public ShaderProfileResolver Shaders { get; }

    public PlaybackService Playback { get; }

    /// <summary>Shader groups found in the user's config files; refreshed after an edit.</summary>
    public IReadOnlyList<ShaderProfile> ShaderCatalog { get; private set; } = [];

    public MpvConfigFile MpvConfig => new(MpvConfigPath, Settings.Mpv.ConfigBackupsToKeep);

    public MpvConfigFile InputConfig => new(MpvInputConfigPath, Settings.Mpv.ConfigBackupsToKeep);

    public MpvConfigFile ShaderPackConfig => new(ShaderPackPath, Settings.Mpv.ConfigBackupsToKeep);

    /// <summary>Whether the embedded libmpv backend is the one that plays next.</summary>
    private bool Embedded => Settings.Mpv.Backend == MpvBackendKind.BuiltInLibMpv;

    /// <summary>Shaders and the config files ship with the program; the embedded backend must
    /// not reach into the external mpv config directory the standalone player uses.</summary>
    public string EmbeddedShaderDirectory => Path.Combine(AppContext.BaseDirectory, "shaders");

    public string EmbeddedConfigPath => Path.Combine(AppContext.BaseDirectory, "mpv.conf");

    public string EmbeddedInputConfigPath => Path.Combine(AppContext.BaseDirectory, "input.conf");

    public string EmbeddedPackPath => Path.Combine(AppContext.BaseDirectory, ShaderPack.FileName);

    /// <summary>The mpv.conf the active backend reads; embedded keeps its own copy.</summary>
    public string MpvConfigPath => Embedded ? EmbeddedConfigPath : Settings.Mpv.ConfigPath;

    /// <summary>The input.conf the active backend reads; embedded keeps its own copy.</summary>
    public string MpvInputConfigPath => Embedded ? EmbeddedInputConfigPath : Settings.Mpv.InputConfigPath;

    public string MpvConfigDirectory => Path.GetDirectoryName(MpvConfigPath) ?? AppContext.BaseDirectory;

    public string ShaderPackPath =>
        Embedded
            ? EmbeddedPackPath
            : string.IsNullOrWhiteSpace(Settings.Shaders.IncludeFile)
                ? ShaderPack.ResolvePath(Settings.Mpv.ConfigPath)
                : Settings.Shaders.IncludeFile;

    /// <summary>
    /// Rescans the config files that hold the shader groups. The embedded backend only ever
    /// reads its own pack next to the program; the external backend also offers groups the user
    /// wrote by hand into mpv.conf.
    /// </summary>
    public void RefreshShaderCatalog()
    {
        ShaderCatalog = Embedded
            ? ShaderProfileCatalog.Scan([EmbeddedPackPath])
            : ShaderProfileCatalog.Scan([Settings.Mpv.ConfigPath, ShaderPackPath]);
        Log.Debug(Category, $"共找到 {ShaderCatalog.Count} 个着色器配置组");
    }

    /// <summary>
    /// Creates the shader pack on first run. The default settings name groups from it, so
    /// without this a fresh install would ask mpv for a profile that does not exist.
    /// </summary>
    public void EnsureShaderPack()
    {
        if (Embedded)
        {
            EnsureEmbeddedResources();
            return;
        }

        if (!File.Exists(Settings.Mpv.ConfigPath)) return;
        if (!string.IsNullOrWhiteSpace(Settings.Shaders.IncludeFile)) return;

        // Already there: the constructor's scan has seen it, so do not parse a 111 KB mpv.conf twice
        // on every launch just to learn nothing changed.
        if (File.Exists(ShaderPackPath)) return;

        if (ShaderPack.EnsureCreated(Settings.Mpv.ConfigPath) is not null) RefreshShaderCatalog();
    }

    /// <summary>
    /// Makes the embedded backend self-contained: copies the config files, shaders and the
    /// dll dependencies from the external mpv installation once (program updates ship them
    /// afterwards), then the embedded player never reads that folder again.
    /// </summary>
    private void EnsureEmbeddedResources()
    {
        var externalMpvDirectory = Path.GetDirectoryName(Settings.Mpv.ConfigPath) ?? "";
        var externalRoot = Path.GetDirectoryName(externalMpvDirectory) ?? "";

        try
        {
            Directory.CreateDirectory(EmbeddedShaderDirectory);

            CopyFileOnce(Path.Combine(externalMpvDirectory, "mpv.conf"), EmbeddedConfigPath);
            CopyFileOnce(Path.Combine(externalMpvDirectory, "input.conf"), EmbeddedInputConfigPath);

            // libmpv-2.dll needs siblings like lua51.dll and vulkan-1.dll next to it; the
            // external mpv installation has them at its root, so bring those along once.
            if (Directory.Exists(externalRoot))
            {
                foreach (var dll in Directory.EnumerateFiles(externalRoot, "*.dll", SearchOption.TopDirectoryOnly))
                    CopyFileOnce(dll, Path.Combine(AppContext.BaseDirectory, Path.GetFileName(dll)));
            }

            // Directories mpv.conf writes into via ~~/files, ~~/cache and ~~/shaders.
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "files"));
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "cache"));

            if (Directory.Exists(Path.Combine(externalMpvDirectory, "shaders")))
                CopyTreeOnce(Path.Combine(externalMpvDirectory, "shaders"), EmbeddedShaderDirectory);

            if (File.Exists(EmbeddedConfigPath))
                Log.Info(Category, "内置播放器已就绪，不再依赖外部 mpv 配置目录");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warn(Category, "准备内置播放器资源失败", error);
        }

        if (File.Exists(EmbeddedPackPath)) return;

        try
        {
            ShaderPack.Write(EmbeddedPackPath, EmbeddedShaderDirectory);
            Log.Info(Category, $"已生成内置着色器配置组：{EmbeddedPackPath}");
            RefreshShaderCatalog();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warn(Category, $"写入内置着色器配置组失败：{EmbeddedPackPath}", error);
        }
    }

    /// <summary>Copies a file only when it is missing; an app update must not overwrite what the
    /// user has already tuned inside the program folder.</summary>
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

    public void SaveSettings()
    {
        try
        {
            Store.Save(Settings);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "保存设置失败", error);
        }
    }

    public void Dispose()
    {
        try
        {
            SaveSettings();
        }
        finally
        {
            Session.Dispose();
        }
    }
}

/// <summary>Version and title strings, read from the assembly so they cannot drift from the build.</summary>
public static class AppInfo
{
    public const string Title = "Emby MPV 客户端";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";

    public static string TitleWithVersion => $"{Title} {Version}";
}
