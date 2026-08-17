using System.Text;
using System.Text.Json;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Infrastructure;

namespace EmbyMpvClient.Configuration;

/// <summary>
/// Loads and saves settings.json. Load never throws: a corrupt file is preserved
/// under a new name and the app starts on defaults rather than refusing to launch.
/// </summary>
public sealed class SettingsStore(AppPaths paths, ISecretProtector protector)
{
    private const string Category = "settings";

    public AppPaths Paths { get; } = paths;

    public AppSettings Load()
    {
        if (!File.Exists(Paths.SettingsFile))
        {
            Log.Info(Category, $"未找到 {Paths.SettingsFile}，使用默认设置");
            return SettingsMigration.NewDefaults();
        }

        if (TryRead(Paths.SettingsFile, out var settings)) return settings;

        Log.Warn(Category, "主设置文件无法解析，尝试读取备份");
        if (File.Exists(Paths.SettingsBackupFile) && TryRead(Paths.SettingsBackupFile, out var fromBackup))
        {
            Quarantine(Paths.SettingsFile);
            return fromBackup;
        }

        Quarantine(Paths.SettingsFile);
        return SettingsMigration.NewDefaults();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            SettingsMigration.Normalize(settings);
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;

            Directory.CreateDirectory(Paths.Root);
            if (File.Exists(Paths.SettingsFile)) File.Copy(Paths.SettingsFile, Paths.SettingsBackupFile, overwrite: true);

            var json = JsonSerializer.Serialize(settings, SettingsSerializer.WriteOptions);
            AtomicFile.WriteAllText(Paths.SettingsFile, json, new UTF8Encoding(false));
            Log.Debug(Category, "设置已保存");
        }
        catch (Exception error)
        {
            Log.Error(Category, "保存设置失败", error);
            throw;
        }
    }

    private bool TryRead(string path, out AppSettings settings)
    {
        try
        {
            settings = SettingsMigration.FromJson(File.ReadAllText(path), protector);
            return true;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn(Category, $"读取 {path} 失败", error);
            settings = SettingsMigration.NewDefaults();
            return false;
        }
    }

    private static void Quarantine(string path)
    {
        try
        {
            var target = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, target, overwrite: true);
            Log.Warn(Category, $"已将损坏的设置文件保留为 {target}");
        }
        catch (Exception error)
        {
            Log.Warn(Category, "无法保留损坏的设置文件", error);
        }
    }
}
