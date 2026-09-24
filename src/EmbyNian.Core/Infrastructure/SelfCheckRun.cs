using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EmbyNian.Configuration;

namespace EmbyNian.Infrastructure;

/// <summary>
/// Owns a single desktop self-check. Only settings.json is read from the real profile; all writes,
/// reports and caches belong to a fresh local directory. Dispose releases the cross-tree mutex but
/// keeps successful runs for evidence. Failed preparation deletes only the directory it just made.
/// </summary>
public sealed class SelfCheckRun : IDisposable
{
    // Neither the executable path nor the worktree belongs in this name: they share one desktop.
    public static string MutexName { get; } = @"Local\EmbyNian.SelfCheck.v1." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Environment.UserDomainName + "\\" + Environment.UserName)))[..24];

    private static int _active;
    private readonly Mutex _mutex;
    private bool _disposed;

    private SelfCheckRun(AppPaths paths, AppSettings settings, Mutex mutex)
    {
        Paths = paths;
        Settings = settings;
        _mutex = mutex;
    }

    public AppPaths Paths { get; }
    public AppSettings Settings { get; }

    public static SelfCheckRun Create(AppPaths source, string? target, ISecretProtector protector)
    {
        // Named mutexes are recursive per thread. Also reject re-entry inside this process, while
        // WaitOne (not createdNew) decides whether another process actually owns the desktop lock.
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new InvalidOperationException("本进程已有自检运行中");
        Mutex? mutex = null;
        var ownsMutex = false;
        string? created = null;
        try
        {
            mutex = new Mutex(false, MutexName);
            try { ownsMutex = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
                throw new InvalidOperationException("已有当前用户的自检运行中；请等它退出，不会关闭用户程序");
            target ??= Path.Combine(Path.GetDirectoryName(source.Root)!,
                "EmbyNian.SelfCheck", Guid.NewGuid().ToString("N"));
            var root = ValidateLocalPath(target);
            var sourceRoot = ValidateLocalPath(source.Root);
            if (IsWithin(root, sourceRoot) || IsWithin(sourceRoot, root))
                throw new IOException("自检目录不得与生产数据目录重叠");
            if (Path.Exists(root)) throw new IOException("自检目录必须全新，不能使用已有文件或目录");

            // CreateDirectoryW, unlike Directory.CreateDirectory, fails if another process created
            // the target after our check. We must never adopt/delete somebody else's directory.
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            ValidateLocalPath(root);
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("桌面自检仅支持 Windows");
            if (!CreateDirectory(root, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            created = root;
            var paths = new AppPaths(root);
            ValidateLocalPath(source.SettingsFile);
            var store = new SettingsStore(paths, protector);
            AppSettings settings;
            if (ExistsStrict(source.SettingsFile))
            {
                File.Copy(source.SettingsFile, paths.SettingsFile, overwrite: false);
                // File.Copy inherits ReadOnly. Only the independent copy's attributes may change.
                File.SetAttributes(paths.SettingsFile, File.GetAttributes(paths.SettingsFile) & ~FileAttributes.ReadOnly);
                settings = store.LoadStrict();
            }
            else
            {
                // A genuinely fresh account may show sample pages. A missing primary with a saved
                // backup is not a fresh account: do not hide that failure behind new defaults.
                if (ExistsStrict(source.SettingsBackupFile))
                    throw new FileNotFoundException("自检主设置缺失但存在备份；请先通过正常启动恢复设置");
                settings = SettingsMigration.NewDefaults();
                store.Save(settings); // only the isolated run; never create production settings
            }
            settings.Ui.WindowMaximized = false; // in memory only; copied DPAPI bytes stay untouched
            paths.EnsureCreated();
            File.WriteAllText(Path.Combine(paths.LogDirectory, "selfcheck-isolation.txt"),
                "EmbyNian.SelfCheckIsolation=1\n" + paths.Root + "\n", new UTF8Encoding(false));
            return new SelfCheckRun(paths, settings, mutex);
        }
        catch
        {
            try
            {
                if (created is not null) Directory.Delete(created, recursive: true);
            }
            finally
            {
                if (ownsMutex) mutex!.ReleaseMutex();
                mutex?.Dispose();
                Interlocked.Exchange(ref _active, 0);
            }
            throw;
        }
    }

    private static bool ExistsStrict(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool IsWithin(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reject relative, network, device and reparse paths, including their existing ancestors.</summary>
    public static string ValidateLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
            throw new IOException("自检目录必须是本地绝对路径");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows())
        {
            if (full.Length < 3 || full[1] != ':' || full[2] != '\\' || full[3..].Contains(':')
                || full[3..].Split('\\').Any(part => part.EndsWith('.') || part.EndsWith(' '))
                || new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Fixed)
                throw new IOException("自检目录必须位于本地固定磁盘");
        }
        string? canonical = null;
        for (var item = full; item is not null; item = Path.GetDirectoryName(item))
        {
            try
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("自检路径不能经过重解析点");
                if (canonical is null && OperatingSystem.IsWindows())
                {
                    // Resolve 8.3 short-name aliases before comparing with the production root.
                    var buffer = new StringBuilder(32768);
                    var length = GetLongPathName(item, buffer, buffer.Capacity);
                    if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
                    canonical = item == full ? buffer.ToString()
                        : Path.Combine(buffer.ToString(), Path.GetRelativePath(item, full));
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return canonical ?? full;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetLongPathName(string shortPath, StringBuilder longPath, int capacity);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, nint securityAttributes);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mutex.ReleaseMutex(); }
        finally
        {
            _mutex.Dispose();
            Interlocked.Exchange(ref _active, 0);
        }
    }
}
