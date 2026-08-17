namespace EmbyMpvClient.Infrastructure;

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
        "EmbyMpvClient"));

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string SettingsBackupFile => Path.Combine(Root, "settings.backup.json");

    public string ImageCacheDirectory => Path.Combine(Root, "cache", "images");

    public string LogDirectory => Path.Combine(Root, "logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ImageCacheDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
