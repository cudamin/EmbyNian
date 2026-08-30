using System.Collections.Concurrent;
using EmbyNian.Diagnostics;
using EmbyNian.Infrastructure;

namespace EmbyNian.Emby;

/// <summary>
/// How much disk the poster cache is holding, as <see cref="EmbyImageStore.Measure"/> found it.
/// <c>default</c> is an empty cache, which is also what a missing directory reports.
/// </summary>
public readonly record struct ImageCacheUsage(int Files, long Bytes);

/// <summary>
/// Disk-backed poster/backdrop store. v1 re-downloaded every image on every navigation;
/// caching by image tag means a re-visit is instant and a changed artwork still refreshes,
/// because Emby's tag is part of the cache key.
/// </summary>
public sealed class EmbyImageStore
{
    private const string Category = "images";

    /// <summary>
    /// The one pattern that says which files here are ours. Named rather than repeated because
    /// <see cref="Clear"/> deletes what it matches: the cache lives under a folder this class made, but
    /// a wrong glob there is the difference between clearing artwork and clearing a folder.
    /// </summary>
    private const string Glob = "*.img";

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

    /// <summary>
    /// The three kinds nothing used to ask for. Emby holds five artworks per item and sends every tag it
    /// has in <see cref="EmbyItem.ImageTags"/>, so these need no new field in a query — only somewhere in
    /// the UI to belong, which is what <see cref="ItemArtwork"/> decides.
    /// </summary>
    public const string Logo = "Logo";

    public const string Banner = "Banner";

    public const string Art = "Art";

    /// <summary>
    /// The width to ask the server for a control <paramref name="controlWidth"/> pixels wide: twice
    /// over, so the picture survives a high-DPI screen and a little growth, rounded up to a step.
    /// <para>
    /// The step is what keeps the cache honest. The width is part of the file name, so without it every
    /// intermediate width of a window drag became its own copy of every poster — this machine's cache
    /// holds the same artwork at 27 different widths — and each new one was a fresh download. A step of
    /// 80 collapses those to eight without asking for much more than is needed.
    /// </para>
    /// </summary>
    public static int RequestWidth(int controlWidth) =>
        Math.Clamp((int)Math.Ceiling(Math.Max(1, controlWidth) * 2 / 80.0) * 80, 160, 1280);

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

            // A card-sized poster is tens of kilobytes. Anything this big came back unresized — an
            // animated PNG is the usual reason, and only its first frame survives the decode here, so
            // the card ends up showing something that looks nothing like it does in a browser.
            if (bytes.Length > 512 * 1024)
                Log.Debug(Category, $"图片未被服务器缩放（{description}，{bytes.Length / 1024} KB）");

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

    /// <summary>
    /// The budget <see cref="PruneInBackground"/> trims back to. Shown beside the measured size, because
    /// 「1.1 GB」 on its own does not tell anyone whether the cache is misbehaving or working as asked.
    /// </summary>
    public long MaxBytes => _maxBytes;

    /// <summary>Trims the oldest files when the cache grows past its budget. Fire-and-forget at startup.</summary>
    public void PruneInBackground() => Task.Run(Prune);

    /// <summary>Off the UI thread: a full cache is thousands of files, and the answer is only a caption.</summary>
    public Task<ImageCacheUsage> MeasureAsync() => Task.Run(() => Measure(_directory));

    /// <summary>Off the UI thread, same reason, and it reports how many files actually went.</summary>
    public Task<int> ClearAsync() => Task.Run(() => Clear(_directory));

    /// <summary>
    /// What the cache in <paramref name="directory"/> is holding.
    /// <para>
    /// Static and directory-shaped, so it can be exercised without a session, a server or a window. The
    /// arithmetic is not the interesting part — the glob is, and the only thing that proves
    /// <see cref="Clear"/> deletes artwork rather than whatever else shares the folder is a test that
    /// puts something else in there first.
    /// </para>
    /// </summary>
    public static ImageCacheUsage Measure(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles(Glob);
            return new ImageCacheUsage(files.Length, files.Sum(file => file.Length));
        }
        catch (DirectoryNotFoundException)
        {
            return default;
        }
        catch (Exception error)
        {
            Log.Warn(Category, "统计图片缓存失败", error);
            return default;
        }
    }

    /// <summary>
    /// Deletes every cached image and returns how many went. A file the UI is still reading stays, which
    /// is why the count is returned rather than assumed: 「已清除 0 个文件」 is worth seeing.
    /// <para>
    /// Nothing needs to be told about this. A card already on screen holds a decoded bitmap and keeps
    /// drawing it, and the next request for anything else finds no file and downloads it again.
    /// </para>
    /// </summary>
    public static int Clear(string directory)
    {
        var removed = 0;
        try
        {
            foreach (var file in new DirectoryInfo(directory).GetFiles(Glob))
            {
                try
                {
                    file.Delete();
                    removed++;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            Log.Info(Category, $"图片缓存已清除 {removed} 个文件");
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, "清除图片缓存失败", error);
        }

        return removed;
    }

    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_directory).GetFiles(Glob);
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
