using EmbyNian.Diagnostics;
using EmbyNian.Infrastructure;

namespace EmbyNian.Mpv;

/// <summary>
/// 读取、保存一个 mpv 配置文件，并在原目录保留带时间戳的备份。保存采用原子替换，进程中断不会留下半个文件。
/// </summary>
public sealed class MpvConfigFile
{
    private const string Category = "mpv-config";

    private readonly int _backupsToKeep;
    private readonly MpvConfigKind? _kindOverride;

    public MpvConfigFile(string path, int backupsToKeep = 20)
        : this(path, null, backupsToKeep)
    {
    }

    /// <summary>
    /// Creates a file wrapper with an explicit syntax. This is useful when a user gives the editor a
    /// custom filename instead of the conventional literal <c>input.conf</c>.
    /// </summary>
    public MpvConfigFile(string path, MpvConfigKind kind, int backupsToKeep = 20)
        : this(path, (MpvConfigKind?)kind, backupsToKeep)
    {
    }

    private MpvConfigFile(string path, MpvConfigKind? kind, int backupsToKeep)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("配置文件路径不能为空", nameof(path));
        Path = System.IO.Path.GetFullPath(path);
        _kindOverride = kind;
        _backupsToKeep = Math.Max(1, backupsToKeep);
    }

    public string Path { get; }

    public string BackupDirectory => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Path) ?? ".",
        "EmbyNian-backups");

    public bool Exists => File.Exists(Path);

    public MpvConfigKind Kind => _kindOverride ??
        (string.Equals(System.IO.Path.GetFileName(Path), "input.conf", StringComparison.OrdinalIgnoreCase)
            ? MpvConfigKind.Input
            : MpvConfigKind.Mpv);

    public MpvConfigDocument Load() => MpvConfigDocument.Load(Path, Kind);

    /// <summary>写入前复制旧文件；不存在的文件首次保存不会伪造空备份。</summary>
    public string? Save(MpvConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var backup = CreateBackup();
        AtomicFile.WriteAllBytes(Path, document.SerializeToBytes());
        Log.Info(Category, backup is null
            ? $"已保存 {System.IO.Path.GetFileName(Path)}"
            : $"已保存 {System.IO.Path.GetFileName(Path)}，备份：{System.IO.Path.GetFileName(backup)}");
        PruneBackups();
        return backup;
    }

    public string? CreateBackup()
    {
        if (!Exists) return null;

        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var name = System.IO.Path.GetFileName(Path);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = System.IO.Path.Combine(BackupDirectory, $"{name}.{stamp}.bak");
            var attempt = 1;
            while (File.Exists(target))
                target = System.IO.Path.Combine(BackupDirectory, $"{name}.{stamp}-{attempt++}.bak");

            File.Copy(Path, target);
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
        var pattern = System.IO.Path.GetFileName(Path) + ".*.bak";
        return [.. new DirectoryInfo(BackupDirectory)
            .GetFiles(pattern)
            .OrderByDescending(file => file.LastWriteTimeUtc)];
    }

    public void RestoreFrom(string backupPath)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("找不到配置备份", backupPath);
        CreateBackup();
        AtomicFile.WriteAllBytes(Path, File.ReadAllBytes(backupPath));
        Log.Info(Category, $"已从备份恢复 {System.IO.Path.GetFileName(Path)}");
    }

    private void PruneBackups()
    {
        foreach (var stale in ListBackups().Skip(_backupsToKeep))
        {
            try { stale.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
