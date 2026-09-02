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

    /// <summary>
    /// One download per artwork, however many callers want it at once, and belonging to none of them.
    /// Which caller cancels is not allowed to decide whether the others get their picture — see
    /// <see cref="SharedWork{TResult}"/> for what that cost before it was written down.
    /// </summary>
    private readonly SharedWork<byte[]?> _downloads = new();

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

    /// <summary>
    /// Which tag identifies <paramref name="imageType"/> on this item, or null when it has none of that
    /// kind. Public because the poster the shelves keep in memory is keyed by it: the tag is what makes
    /// artwork replaced on the server a different picture rather than the same one, and the memory cache
    /// has to agree with the disk cache about that or a changed poster never comes back.
    /// </summary>
    public static string? TagFor(EmbyItem item, string imageType) => imageType switch
    {
        Primary => item.PrimaryImageTag,
        Thumb => item.ThumbImageTag,
        Backdrop => item.BackdropImageTags.FirstOrDefault(),
        _ => item.ImageTags.TryGetValue(imageType, out var value) ? value : null
    };

    /// <summary>Returns null when the item simply has no such image; throws only on real errors.</summary>
    public Task<byte[]?> GetAsync(EmbyItem item, string imageType, int width, CancellationToken cancellationToken) =>
        TagFor(item, imageType) is { } tag
            ? GetAsync(item.Id, imageType, tag, width, cancellationToken)
            : Task.FromResult<byte[]?>(null);

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

    private async Task<byte[]?> Fetch(
        string key,
        Func<EmbyClient, CancellationToken, Task<byte[]>> download,
        string description,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, key);

        // A hit is a file read, and it used to be a synchronous one — on the UI thread, because that is
        // who asks. A screen of forty cards therefore stopped forty times waiting on the disk, which is
        // exactly the stutter a fast scroll showed. Off the thread, the scroll keeps moving.
        if (await ReadCachedAsync(path, cancellationToken).ConfigureAwait(false) is { } cached)
        {
            // 看过一眼就把这张的时间戳往前推 —— 淘汰按的就是这个时间（见 Prune）。不这么做的话「最旧」说的是
            // 「最早下载的」，于是天天看的那部剧的海报因为下得早，会先于上个月下过一次再没碰过的那张被删掉。
            //
            // **扔到线程池上，不等它。** 这一路是界面线程在走（要图的是卡片），而上面那句 ReadCachedAsync 之所以
            // 是异步的，正是因为「一屏四十张卡片各停一次等磁盘」就是快速滚动时那一下卡顿。`ConfigureAwait(false)`
            // 多数时候会把这里挪到池线程上，可那只是多数：一次同步完成的读会让下面这几句原地跑在界面线程上。推时
            // 间戳只影响淘汰次序，屏上没有任何东西等它，所以让它自己去跑。
            _ = Task.Run(() => Touch(path));
            return cached;
        }

        // Collapse duplicate requests: a fast scroll asks for the same poster repeatedly. The shared
        // download belongs to nobody, so a caller that walks away no longer empties the answer the
        // caller beside it was waiting for.
        return await _downloads
            .RunAsync(key, () => DownloadAsync(download, description, path), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<byte[]?> DownloadAsync(
        Func<EmbyClient, CancellationToken, Task<byte[]>> download,
        string description,
        string path)
    {
        try
        {
            // CancellationToken.None on purpose: whoever asked first may be gone by now, and the bytes
            // are still wanted — by the other cards waiting on this one download, and by the disk cache
            // that makes the next visit instant. The request is bounded by the HTTP timeout instead.
            var bytes = await _session.ExecuteAsync<byte[]>(download, CancellationToken.None).ConfigureAwait(false);

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

            // 又下了一张。攒够一批就再清一次 —— 从前只在开机清，一次长会话里缓存可以一路涨过预算。
            if (Interlocked.Increment(ref _sincePrune) >= PruneEvery)
            {
                Interlocked.Exchange(ref _sincePrune, 0);
                PruneInBackground();
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
            // Only the HTTP timeout can land here now that no caller's token reaches the request.
            return null;
        }
    }

    private static async Task<byte[]?> ReadCachedAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把这一张的时间戳推到现在，让 <see cref="Prune"/> 眼里的「最旧」真的是「最久没看过的」。
    /// <para>
    /// 写的是最后修改时间而不是最后访问时间：Windows 从 Vista 起默认关掉 NTFS 的最后访问时间更新
    /// （<c>NtfsDisableLastAccessUpdate</c>），所以那个字段在多数机器上根本不动 —— 拿它当依据的淘汰规则会退化成
    /// 「随便删」。
    /// </para>
    /// <para>
    /// **只在这一张确实旧了的时候才写**（<see cref="ImageCachePolicy.WorthTouching"/>）。每次命中都写一次的话，
    /// 一次快速滚动就是几十次元数据写入，而滚动正是最不该多花时间的地方；隔一段才推一次，「最久没看过」这个次序
    /// 照样成立，代价却几乎是零。
    /// </para>
    /// </summary>
    private static void Touch(string path)
    {
        try
        {
            if (!ImageCachePolicy.WorthTouching(File.GetLastWriteTimeUtc(path), DateTime.UtcNow)) return;

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // 推不动就算了：淘汰次序差一点点，而这条路上没有任何东西值得为它失败。
        }
        catch (UnauthorizedAccessException)
        {
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

    /// <summary>
    /// 距离上一次清理又下载了多少张就再清一次。**清理从前只在开机跑一次** —— 一次会话里连着刷两小时媒体库，缓存
    /// 就一路涨过预算，要等下次启动才收得回来。
    /// <para>
    /// 按下载张数而不是按时间：涨得快是因为在下载，闲着的会话不需要一遍遍去数一个没变过的目录。200 张海报大约
    /// 十几兆，也就是两次清理之间最多超出这么多；代价是每 200 次下载多走一趟目录枚举。
    /// </para>
    /// </summary>
    private const int PruneEvery = 200;

    private int _sincePrune;

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

            // 删哪几张由 Core 那一句说（ImageCachePolicy.Evict）：最久没看过的先走，删到降回预算的八成为止。
            // 「最久没看过」靠的是命中时把时间戳往前推，见 Touch。
            var doomed = ImageCachePolicy.Evict(
                files.Select(file => (file.FullName, file.Length, file.LastWriteTimeUtc)),
                total,
                _maxBytes);

            var removed = 0;
            var freed = 0L;
            foreach (var (path, length) in doomed)
            {
                try
                {
                    File.Delete(path);
                    removed++;
                    freed += length;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            Log.Info(Category,
                $"图片缓存已清理 {removed} 个文件，腾出 {freed / (1024 * 1024)} MB"
                    + $"（清理前 {total / (1024 * 1024)} MB，预算 {_maxBytes / (1024 * 1024)} MB）");
        }
        catch (Exception error)
        {
            Log.Warn(Category, "清理图片缓存失败", error);
        }
    }
}
