namespace Momoka.Infrastructure;

/// <summary>先在同目录写完整临时文件，再替换目标；可将被替换的原件同时保留为备份。</summary>
public static class AtomicFile
{
    private static readonly object CommitGate = new();

    public static void WriteAllText(string path, string contents, System.Text.Encoding encoding, string? backupPath = null) =>
        Write(path, temporary => File.WriteAllText(temporary, contents, encoding), backupPath);

    public static void WriteAllBytes(string path, byte[] contents) =>
        Write(path, temporary => File.WriteAllBytes(temporary, contents), null);

    private static void Write(string path, Action<string> write, string? backupPath)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            write(temporary);
            lock (CommitGate)
            {
                if (backupPath is not null && File.Exists(path))
                    File.Replace(temporary, path, Path.GetFullPath(backupPath));
                else
                    File.Move(temporary, path, overwrite: true);
            }
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
