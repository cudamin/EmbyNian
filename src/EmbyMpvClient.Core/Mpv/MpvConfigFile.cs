using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Infrastructure;

namespace EmbyMpvClient.Mpv;

/// <summary>
/// Reads and writes an mpv config file, keeping timestamped copies. v1 wrote mpv.conf in
/// place with a single .bak; a bad edit to a 111 KB hand-tuned config was unrecoverable.
/// </summary>
public sealed class MpvConfigFile(string path, int backupsToKeep = 20)
{
    private const string Category = "mpv";

    public string Path => path;

    public string BackupDirectory => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(path) ?? ".",
        "EmbyMpvClient-backups");

    public bool Exists => File.Exists(path);

    public MpvConfigDocument Load() => MpvConfigDocument.Load(path);

    /// <summary>Backs up the current file, then writes atomically so a crash cannot truncate it.</summary>
    public void Save(MpvConfigDocument document)
    {
        var backup = CreateBackup();
        AtomicFile.WriteAllBytes(path, document.SerializeToBytes());
        Log.Info(Category, backup is null
            ? $"已保存 {System.IO.Path.GetFileName(path)}"
            : $"已保存 {System.IO.Path.GetFileName(path)}，备份：{System.IO.Path.GetFileName(backup)}");
        PruneBackups();
    }

    public string? CreateBackup()
    {
        if (!Exists) return null;

        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var name = System.IO.Path.GetFileName(path);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = System.IO.Path.Combine(BackupDirectory, $"{name}.{stamp}.bak");

            // Two saves inside one second must not overwrite the same backup.
            var attempt = 1;
            while (File.Exists(target))
                target = System.IO.Path.Combine(BackupDirectory, $"{name}.{stamp}-{attempt++}.bak");

            File.Copy(path, target);
            return target;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warn(Category, "创建配置备份失败", error);
            return null;
        }
    }

    public IReadOnlyList<FileInfo> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory)) return [];
        var pattern = System.IO.Path.GetFileName(path) + ".*.bak";
        return [.. new DirectoryInfo(BackupDirectory)
            .GetFiles(pattern)
            .OrderByDescending(file => file.LastWriteTimeUtc)];
    }

    public void RestoreFrom(string backupPath)
    {
        CreateBackup();
        AtomicFile.WriteAllBytes(path, File.ReadAllBytes(backupPath));
        Log.Info(Category, $"已从备份恢复 {System.IO.Path.GetFileName(path)}");
    }

    private void PruneBackups()
    {
        var backups = ListBackups();
        foreach (var stale in backups.Skip(Math.Max(1, backupsToKeep)))
        {
            try
            {
                stale.Delete();
            }
            catch (IOException)
            {
            }
        }
    }
}
