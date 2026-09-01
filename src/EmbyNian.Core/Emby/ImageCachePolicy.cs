namespace EmbyNian.Emby;

/// <summary>
/// 图片缓存的两条淘汰规则，从磁盘里拆出来单独放在这儿：**哪几张该删**，和**什么时候值得去动一次时间戳**。
/// <para>
/// 拆出来是因为这两条以前都是错的，而两种错法屏上都看不见。旧的淘汰规则按「最早写入」删，而那个时间是下载时刻
/// —— 天天看的那部剧的海报因为下得早，会先于上个月下过一次、再没碰过的那张被删掉；用户看见的只是「怎么又在转」。
/// 而清理从前只在开机跑一次，一次会话里缓存可以一路涨过预算，也没人会注意。
/// </para>
/// <para>
/// 磁盘那一头（<see cref="EmbyImageStore"/>）只负责列目录、删文件和推时间戳；次序和时机在这里，因此可以在没有
/// 缓存目录、没有服务器的地方一条条钉住。
/// </para>
/// </summary>
public static class ImageCachePolicy
{
    /// <summary>
    /// 清理一次要降到预算的几成。留出余量是为了别一超就删 —— 正好削到预算上，下一张图落地又超了，于是每下载几张
    /// 就走一趟目录枚举。
    /// </summary>
    public const double TrimTo = 0.8;

    /// <summary>
    /// 时间戳最多多久推一次。命中一张就写一次的话，一次快速滚动是几十次元数据写入，而滚动正是最不该多花时间的
    /// 地方；隔上这么久才推一次，「最久没看过」这个次序照样成立（判据是「上一次看这张是哪一天」，不是「哪一秒」），
    /// 代价却几乎是零。
    /// </summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// 这一张的时间戳值不值得推到现在。<paramref name="lastWrite"/> 比 <see cref="TouchInterval"/> 还新就不动。
    /// <para>
    /// 时间戳在未来（手改过系统时钟、从别的机器上拷过来的缓存）也算「值得推」：把它拉回现在，这一张就重新排进
    /// 正常的次序里，而留着一个未来的时间戳意味着它永远排在最后、永远不会被淘汰。
    /// </para>
    /// </summary>
    public static bool WorthTouching(DateTime lastWrite, DateTime now) =>
        lastWrite > now || now - lastWrite >= TouchInterval;

    /// <summary>
    /// 该删哪几张，按「最久没看过的先走」的次序，删到 <paramref name="total"/> 降回
    /// <paramref name="maxBytes"/> 的 <see cref="TrimTo"/> 为止。没超预算就一个都不删。
    /// </summary>
    /// <param name="files">缓存里的每一张：路径、多大、上一次看是什么时候。</param>
    /// <param name="total">现在一共占了多少字节 —— 调用方数过一遍了，不用在这里再加一次。</param>
    /// <param name="maxBytes">预算。0 或者负数表示「一张都不留」，那时候全删。</param>
    public static IReadOnlyList<(string Path, long Length)> Evict(
        IEnumerable<(string Path, long Length, DateTime LastSeenUtc)> files,
        long total,
        long maxBytes)
    {
        if (total <= maxBytes) return [];

        var target = maxBytes > 0 ? (long)(maxBytes * TrimTo) : 0;
        var doomed = new List<(string, long)>();

        // 次序稳定：时间戳一样的时候按路径排。同一秒里落地的一批（一屏卡片就是这样）否则每次跑出来的次序都不一样，
        // 而「同一份缓存、同一次清理，删的是同一批」是这一句能被钉住的前提。
        foreach (var file in files
            .OrderBy(file => file.LastSeenUtc)
            .ThenBy(file => file.Path, StringComparer.Ordinal))
        {
            if (total <= target) break;

            total -= file.Length;
            doomed.Add((file.Path, file.Length));
        }

        return doomed;
    }
}
