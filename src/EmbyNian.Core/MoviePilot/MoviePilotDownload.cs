using System.Globalization;
using System.Text.Json;

namespace EmbyNian.MoviePilot;

/// <summary>
/// 下载管理里的一条正在下载的任务（<c>GET download/</c> 回来的每一行 <c>DownloaderTorrent</c>）。
/// <para>
/// 字段按上游 v2 与 v3.0.1 两个版本的 schema 共同的那一部分收 —— 两版逐字段比对过（协议快照在
/// <c>work/moviepilot-protocol-20260924-M8nFhS</c>），<c>media</c> 那一小块在 v2 是普通字典、v3 是
/// <c>DownloadTaskMedia</c>，序列化出来键名一致（<c>type/title/poster/backdrop/image</c>），所以一份解析两头通吃。
/// 海报和背景图是 MoviePilot 在识别入库时留下的完整网址（下载历史里存的本来就是 <c>https://image.tmdb.org/…</c>
/// 这一串），只认 <c>http(s)</c> 绝对地址 —— 相对路径和空值一样当「没有图」。
/// </para>
/// </summary>
public sealed record MoviePilotDownloadTask(
    string Hash,
    string Title,
    string Year,
    string SeasonEpisode,
    string MediaType,
    double Size,
    double Progress,
    string State,
    string Speed,
    string LeftTime,
    string SiteName,
    string PosterUrl,
    string BackdropUrl)
{
    /// <summary>
    /// 卡片副行（也是主读数）给的两个宽度：进度条吃 <see cref="Progress"/>（0–100），文字吃这些拼好的串。
    /// </summary>
    public bool IsDownloading => State is "downloading" or "";

    /// <summary>这条任务想拿来当封面的那张图：海报优先，退而背景图。</summary>
    public string ImageUrl => PosterUrl.Length > 0 ? PosterUrl : BackdropUrl;
}

/// <summary>
/// 把 <c>download/</c> 的回话拆成任务行，加上屏幕上那两行字和跨轮询的差量计划。
/// <para>
/// 「每种输入有唯一正确答案」的活按项目规矩住 Core 并配测试。对形状放宽的理由同
/// <see cref="MoviePilotMediaParser"/>：认不出来在屏上是「那一排少一张卡」，和「真的没在下载」长得一样。
/// </para>
/// </summary>
public static partial class MoviePilotDownload
{
    /// <summary>
    /// 一片正在下载的任务。<c>download/</c> 回的是裸数组；被信封或某个键（<c>list</c>/<c>items</c>/<c>data</c>）
    /// 包了一层也认。没有 <c>hash</c> 的行跳过 —— hash 是跨轮询对账的唯一钥匙，没有它既没法续、也没法删。
    /// </summary>
    public static IReadOnlyList<MoviePilotDownloadTask> Parse(JsonElement data)
    {
        if (FindArray(data) is not { } array) return [];

        var list = new List<MoviePilotDownloadTask>();
        foreach (var item in array.EnumerateArray())
            if (ToTask(item) is { } task)
                list.Add(task);

        return list;
    }

    /// <summary>
    /// 卡片标题底下那一行：百分比打头，下载中跟着速度和剩余时间，暂停就说暂停。
    /// 速度串原样取用（后端给的就是「2.5 M」这种已排好版的紧凑容量），尾巴上补一个 <c>/s</c>；
    /// 为零或为空就不占那一格。
    /// </summary>
    public static string InfoLine(MoviePilotDownloadTask task)
    {
        var percent = PercentText(task.Progress);
        if (!task.IsDownloading) return $"已暂停 · {percent}";

        var parts = new List<string> { percent };
        if (LooksLikeSpeed(task.Speed)) parts.Add(SpeedText(task.Speed));
        if (task.LeftTime.Length > 0) parts.Add($"剩 {task.LeftTime}");
        return string.Join(" · ", parts);
    }

    /// <summary>悬停提示里的容量：「5.37 GB」。看不出大小时交回空串。</summary>
    public static string SizeText(double bytes)
    {
        if (bytes <= 0 || !double.IsFinite(bytes)) return "";

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size.ToString(unit == 0 ? "0" : "0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>「42%」，四舍五入同 MoviePilot 自己的卡片。显式远离零 —— 42.5 这种正中点不留给格式化器去猜。</summary>
    public static string PercentText(double progress) =>
        $"{Math.Round(double.Clamp(progress, 0, 100), MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}%";

    /// <summary>速度串是不是值得占一格：「0」「0.0」「」都不算。</summary>
    private static bool LooksLikeSpeed(string speed)
    {
        var text = speed.Trim();
        if (text.Length == 0) return false;
        return !double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) || value > 0;
    }

    private static string SpeedText(string speed) =>
        speed.Contains('/', StringComparison.Ordinal) ? speed : $"{speed.TrimEnd()}/s";

    private static MoviePilotDownloadTask? ToTask(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var hash = MoviePilotTransfer.Text(item, "hash");
        if (hash.Length == 0) return null;

        // 标题三级回退：识别入库留下的媒体名 → 种子标题 → 种子主文件名。三处都空的任务没东西可画，跳过。
        var media = item.TryGetProperty("media", out var mediaElement) &&
            mediaElement.ValueKind == JsonValueKind.Object
                ? mediaElement
                : default(JsonElement?);
        var title = FirstNonEmpty(
            media is { } m ? MoviePilotTransfer.Text(m, "title") : null,
            MoviePilotTransfer.Text(item, "title"),
            MoviePilotTransfer.Text(item, "name"));
        if (title.Length == 0) return null;

        var progress = Number(item, "progress") ?? 0;
        return new MoviePilotDownloadTask(
            Hash: hash,
            Title: title,
            Year: MoviePilotTransfer.Text(item, "year"),
            SeasonEpisode: MoviePilotTransfer.Text(item, "season_episode"),
            MediaType: media is { } present ? MoviePilotTransfer.Text(present, "type") : "",
            Size: Number(item, "size") ?? 0,
            Progress: double.Clamp(progress, 0, 100),
            State: MoviePilotTransfer.Text(item, "state"),
            Speed: MoviePilotTransfer.Text(item, "dlspeed"),
            LeftTime: MoviePilotTransfer.Text(item, "left_time"),
            SiteName: MoviePilotTransfer.Text(item, "site_name"),
            PosterUrl: media is { } withImage
                ? AbsoluteUrl(FirstNonEmpty(
                    MoviePilotTransfer.Text(withImage, "poster"),
                    MoviePilotTransfer.Text(withImage, "image")))
                : "",
            BackdropUrl: media is { } withBackdrop ? AbsoluteUrl(MoviePilotTransfer.Text(withBackdrop, "backdrop")) : "");
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    /// <summary>只认写明了协议的网址；相对路径、空串、「没有」都归成空串。</summary>
    private static string AbsoluteUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : "";

    private static double? Number(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : null;

    private static JsonElement? FindArray(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array) return data;
        if (data.ValueKind != JsonValueKind.Object) return null;

        foreach (var key in (string[])["list", "items", "data"])
            if (data.TryGetProperty(key, out var inner) && inner.ValueKind == JsonValueKind.Array)
                return inner;

        return null;
    }
}

/// <summary>
/// 一轮差量里「这一张卡要插到第几格」。<see cref="Index"/> 是这条任务在服务器那一串里的位置，插入时由调用方
/// 夹到当前行尾 —— 下载器基本不重排，照原样插才能让卡片原地更新而不是整排重画。
/// </summary>
public sealed record MoviePilotDownloadChange(MoviePilotDownloadTask Task, int Index);

/// <summary>
/// 一次轮询结果对上一轮的差量：新增（带插入位置）、消失（按 hash）、变了（原样交回，调用方就地更新属性）。
/// <para>
/// 「实时推送」的屏上表现就落在这份计划上：进度数字跳动是**同一张卡**被改了属性，不是整排拆了重搭 ——
/// 拆了重搭既闪又丢焦点。纯函数住 Core，钉住四条规则：没了的删、多了的按服务器的次序插、两边都有的不动位置、
/// **值没变的不惊动**（任务是个 record，值相等就当没发生 —— 安静的一轮一份空计划，连属性通知都不发）。
/// </para>
/// </summary>
public sealed record MoviePilotDownloadPlan(
    IReadOnlyList<MoviePilotDownloadChange> Additions,
    IReadOnlyList<string> Removals,
    IReadOnlyList<MoviePilotDownloadTask> Updates)
{
    /// <summary>三样都空 = 这一轮什么都没变。</summary>
    public bool IsEmpty => Additions.Count == 0 && Removals.Count == 0 && Updates.Count == 0;
}

public static partial class MoviePilotDownload
{
    /// <summary>
    /// 上一轮的行（按屏上次序）对这一轮的行做对账。两边都以 <see cref="MoviePilotDownloadTask.Hash"/> 认人；
    /// 下一轮里 hash 相同但内容变了（进度、速度……）的任务进 <see cref="MoviePilotDownloadPlan.Updates"/>，
    /// 由调用方就地改卡；内容一模一样的不出现 —— record 的值相等就是「没变」的那个说法。
    /// </summary>
    public static MoviePilotDownloadPlan Plan(
        IReadOnlyList<MoviePilotDownloadTask> current,
        IReadOnlyList<MoviePilotDownloadTask> next)
    {
        var byHash = new Dictionary<string, MoviePilotDownloadTask>(StringComparer.Ordinal);
        foreach (var task in current) byHash[task.Hash] = task;

        var additions = new List<MoviePilotDownloadChange>();
        for (var index = 0; index < next.Count; index++)
            if (!byHash.ContainsKey(next[index].Hash))
                additions.Add(new MoviePilotDownloadChange(next[index], index));

        var removals = current
            .Where(task => !next.Any(candidate => candidate.Hash == task.Hash))
            .Select(task => task.Hash)
            .ToList();

        var updates = next
            .Where(task => byHash.TryGetValue(task.Hash, out var before) && !before.Equals(task))
            .ToList();

        return new MoviePilotDownloadPlan(additions, removals, updates);
    }
}
