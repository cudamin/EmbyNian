using System.Text;
using System.Text.Json;
using EmbyNian.Diagnostics;
using EmbyNian.Infrastructure;

namespace EmbyNian.Configuration;

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
        if (File.Exists(Paths.SettingsFile))
        {
            if (TryRead(Paths.SettingsFile, out var settings)) return settings;

            Log.Warn(Category, "主设置文件无法解析，尝试读取备份");
            Quarantine(Paths.SettingsFile);
        }
        else
        {
            Log.Info(Category, $"未找到 {Paths.SettingsFile}，尝试读取备份");
        }

        // 主文件缺失也走恢复路径：上次启动可能刚隔离坏文件，还没来得及重新保存就退出了。
        if (File.Exists(Paths.SettingsBackupFile) && TryRead(Paths.SettingsBackupFile, out var fromBackup))
            return fromBackup;

        Log.Info(Category, "没有可用的设置文件，使用默认设置");
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
        // Deliberately every exception, not the three that used to be listed here (JSON, IO,
        // permissions). This class promises Load never throws, and the promise is what keeps the app
        // launchable: a file the migration cannot make sense of has to end up in the backup /
        // quarantine / defaults path, not on the way out of Main. The three-exception version let a
        // structurally odd document through — `{"Playback": null}` threw a NullReferenceException from
        // inside Normalize — and the window then never appeared at all.
        //
        // 代价说清楚：这一手也会把迁移代码自己的 bug 当成「文件坏了」，于是用户的设置被改名收起来、程序回到
        // 默认值。所以那一份**始终留在磁盘上**（Quarantine 只改名），异常整条写进日志。拿不准的时候，
        // 「打得开但设置回到默认」比「打不开」值钱。
        catch (Exception error)
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
