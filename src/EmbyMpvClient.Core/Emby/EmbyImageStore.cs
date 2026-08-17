using System.Collections.Concurrent;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Infrastructure;

namespace EmbyMpvClient.Emby;

/// <summary>
/// Disk-backed poster/backdrop store. v1 re-downloaded every image on every navigation;
/// caching by image tag means a re-visit is instant and a changed artwork still refreshes,
/// because Emby's tag is part of the cache key.
/// </summary>
public sealed class EmbyImageStore
{
    private const string Category = "images";

    private readonly EmbySession _session;
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inFlight = new();

    public EmbyImageStore(EmbySession session, string directory, long maxBytes = 400L * 1024 * 1024)
    {
        _session = session;
        _directory = directory;
        _maxBytes = maxBytes;
        Directory.CreateDirectory(directory);
    }

    public const string Primary = "Primary";
    public const string Backdrop = "Backdrop";
    public const string Thumb = "Thumb";

    /// <summary>Returns null when the item simply has no such image; throws only on real errors.</summary>
    public Task<byte[]?> GetAsync(EmbyItem item, string imageType, int width, CancellationToken cancellationToken)
    {
        var tag = imageType switch
        {
            Primary => item.PrimaryImageTag,
            Thumb => item.ThumbImageTag,
            Backdrop => item.BackdropImageTags.FirstOrDefault(),
            _ => item.ImageTags.TryGetValue(imageType, out var value) ? value : null
        };

        return tag is null
            ? Task.FromResult<byte[]?>(null)
            : GetAsync(item.Id, imageType, tag, width, cancellationToken);
    }

    public Task<byte[]?> GetAsync(string itemId, string imageType, string tag, int width, CancellationToken cancellationToken) =>
        Fetch(
            BuildKey(itemId, imageType, tag, width),
            (client, token) => client.GetImageBytesAsync(itemId, imageType, tag, width, token),
            $"{itemId}/{imageType}",
            cancellationToken);

    /// <summary>
    /// The still for one chapter, for the seek bar's hover preview. Cached exactly like a poster,
    /// which matters more here than for artwork: scrubbing a long episode walks over every chapter
    /// and would otherwise re-download the same frames on every pass.
    /// </summary>
    public Task<byte[]?> GetChapterAsync(string itemId, int index, string? tag, int width, CancellationToken cancellationToken) =>
        Fetch(
            BuildKey(itemId, $"Chapter{index}", tag ?? "untagged", width),
            (client, token) => client.GetChapterImageBytesAsync(itemId, index, tag, width, token),
            $"{itemId}/Chapter/{index}",
            cancellationToken);

    private Task<byte[]?> Fetch(
        string key,
        Func<EmbyClient, CancellationToken, Task<byte[]>> download,
        string description,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, key);

        if (TryReadCached(path, out var cached)) return Task.FromResult<byte[]?>(cached);

        // Collapse duplicate requests: a fast scroll asks for the same poster repeatedly.
        var task = _inFlight.GetOrAdd(key, _ => DownloadAsync(download, description, path, cancellationToken));
        _ = task.ContinueWith(completed => _inFlight.TryRemove(key, out _), TaskScheduler.Default);
        return task;
    }

    private async Task<byte[]?> DownloadAsync(
        Func<EmbyClient, CancellationToken, Task<byte[]>> download,
        string description,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _session.ExecuteAsync<byte[]>(download, cancellationToken).ConfigureAwait(false);

            if (bytes.Length == 0) return null;

            try
            {
                AtomicFile.WriteAllBytes(path, bytes);
            }
            catch (Exception error)
            {
                Log.Warn(Category, "写入图片缓存失败", error);
            }

            return bytes;
        }
        catch (EmbyApiException error)
        {
            Log.Debug(Category, $"获取图片失败（{description}）：{error.Message}");
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static bool TryReadCached(string path, out byte[] bytes)
    {
        bytes = [];
        try
        {
            if (!File.Exists(path)) return false;
            bytes = File.ReadAllBytes(path);
            return bytes.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string BuildKey(string itemId, string imageType, string tag, int width)
    {
        // Emby ids and tags are hex strings, so they are already filename-safe;
        // sanitise anyway rather than trusting server output with a path.
        static string Safe(string value) => string.Concat(value.Where(char.IsLetterOrDigit));
        return $"{Safe(itemId)}-{Safe(imageType)}-{Safe(tag)}-{width}.img";
    }

    /// <summary>Trims the oldest files when the cache grows past its budget. Fire-and-forget at startup.</summary>
    public void PruneInBackground() => Task.Run(Prune);

    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_directory).GetFiles("*.img");
            var total = files.Sum(file => file.Length);
            if (total <= _maxBytes) return;

            var target = (long)(_maxBytes * 0.8);
            var removed = 0;
            foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
            {
                if (total <= target) break;
                total -= file.Length;
                try
                {
                    file.Delete();
                    removed++;
                }
                catch (IOException)
                {
                }
            }

            Log.Info(Category, $"图片缓存已清理 {removed} 个文件");
        }
        catch (Exception error)
        {
            Log.Warn(Category, "清理图片缓存失败", error);
        }
    }
}
