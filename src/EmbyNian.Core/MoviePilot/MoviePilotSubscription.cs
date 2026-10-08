using System.Globalization;
using System.Text.Json;

namespace EmbyNian.MoviePilot;

public enum MoviePilotSubscriptionAction { Pause, Resume, Search, Reset, Delete }

public sealed record MoviePilotSubscription
{
    public required int Id { get; init; }
    public required MoviePilotMedia Media { get; init; }
    public required JsonElement Data { get; init; }
    internal string ConnectionStamp { get; init; } = "";
    public bool IsSeries => Media.Type == "电视剧";
    public string Title => Media.Display + (IsSeries && Media.Season is { } season ? $" · 第 {season} 季" : "");
    public string State => Text(Data, "state");
    public bool Paused => State == "S";
    public int? TotalEpisodes => Number(Data, "total_episode");
    public int? MissingDownloads => Number(Data, "lack_episode");
    public int StartEpisode => Math.Max(1, Number(Data, "start_episode") ?? 1);
    public string StateLabel => State switch { "S" => "已暂停", "R" => "订阅中", "N" or "P" => "待处理", _ => "状态未知" };
    public string ProgressCount => IsSeries && TotalEpisodes is > 0 && MissingDownloads is >= 0
        ? $"{Math.Clamp(TotalEpisodes.Value - MissingDownloads.Value, 0, TotalEpisodes.Value)} / {TotalEpisodes} 集"
        : "";
    public string ProgressLabel => ProgressCount.Length > 0 ? "订阅进度 " + ProgressCount : "";

    public static IReadOnlyList<MoviePilotSubscription> ParseList(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
            throw new MoviePilotException("MoviePilot 订阅列表格式不正确，请刷新重试");
        return data.EnumerateArray().Select(Parse).Where(item => item is not null)
            .Cast<MoviePilotSubscription>().DistinctBy(item => item.Id).ToArray();
    }

    public static MoviePilotSubscription? Parse(JsonElement item)
    {
        if (Number(item, "id") is not > 0 || item.ValueKind != JsonValueKind.Object) return null;
        var type = Text(item, "type");
        type = type.ToLowerInvariant() switch { "movie" => "电影", "tv" or "series" => "电视剧", _ => type };
        if (type is not ("电影" or "电视剧")) return null;
        return new MoviePilotSubscription
        {
            Id = Number(item, "id")!.Value,
            Data = item.Clone(),
            Media = new MoviePilotMedia
            {
                Title = Text(item, "name") is { Length: > 0 } name ? name : "未命名订阅",
                Year = Number(item, "year"),
                Season = Number(item, "season"),
                Type = type,
                MediaSource = Text(item, "media_source"),
                MediaId = Text(item, "media_id"),
                PosterUrl = Text(item, "poster"),
                Overview = Text(item, "description")
            }
        };
    }

    internal static JsonElement Member(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;
    public static string Text(JsonElement item, string name) => Scalar(Member(item, name));
    internal static string Scalar(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.String => item.GetString() ?? "",
        JsonValueKind.Number => item.GetRawText(),
        _ => ""
    };
    public static int? Number(JsonElement item, string name) =>
        int.TryParse(Text(item, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
}

public sealed record MoviePilotSubscriptionEpisode(int Number, string Title, string Description,
    IReadOnlyList<string> LibraryFiles, IReadOnlyList<string> DownloadFiles)
{
    public string ArtworkUrl { get; init; } = "";
    public IReadOnlyList<MoviePilotSubscriptionLibraryFile> LibraryEntries { get; init; } = [];
    public bool InLibrary => LibraryFiles.Count > 0;
    public string Label => Number == 0 ? "电影" : $"第 {Number} 集";
    public string Status => InLibrary ? "已入库" : DownloadFiles.Count > 0 ? "已下载 · 待入库" : "缺集";
    public string Files => string.Join('\n', LibraryFiles.Select(file => "入库：" + file)
        .Concat(DownloadFiles.Select(file => "下载：" + file)));
}

public sealed record MoviePilotSubscriptionLibraryFile(string Path, string ServerType, string ItemId);

public sealed record MoviePilotSubscriptionFiles(IReadOnlyList<MoviePilotSubscriptionEpisode> Episodes)
{
    public int LibraryCount => Episodes.Count(episode => episode.InLibrary);
    public int MissingCount => Episodes.Count(episode => !episode.InLibrary);
    public string Summary => Episodes.Count == 0 ? "服务器尚未提供集信息，无法判断入库情况"
        : $"已入库 {LibraryCount} / {Episodes.Count} 项 · 未入库 {MissingCount} 项";

    public static MoviePilotSubscriptionFiles Parse(JsonElement data, MoviePilotSubscription subscription)
    {
        var current = MoviePilotSubscription.Parse(MoviePilotSubscription.Member(data, "subscribe"));
        if (current is null)
            throw new MoviePilotException("MoviePilot 暂未提供这条订阅的文件统计，请稍后刷新");
        if (current.Id != subscription.Id)
            throw new MoviePilotException("订阅已不存在或文件统计身份不匹配，请刷新列表");
        var episodes = MoviePilotSubscription.Member(data, "episodes");
        if (episodes.ValueKind != JsonValueKind.Object)
            throw new MoviePilotException("MoviePilot 未返回有效的文件统计");
        var result = new List<MoviePilotSubscriptionEpisode>();
        foreach (var entry in episodes.EnumerateObject())
        {
            if (!int.TryParse(entry.Name, CultureInfo.InvariantCulture, out var number) || number < 0) continue;
            if (subscription.IsSeries && (number < current.StartEpisode || current.TotalEpisodes is > 0 && number > current.TotalEpisodes)) continue;
            if (!subscription.IsSeries && number != 0) continue;
            result.Add(new MoviePilotSubscriptionEpisode(number, MoviePilotSubscription.Text(entry.Value, "title"),
                MoviePilotSubscription.Text(entry.Value, "description"), Files(entry.Value, "library"), Files(entry.Value, "download"))
            { LibraryEntries = LibraryEntries(entry.Value), ArtworkUrl = MoviePilotSubscription.Text(entry.Value, "backdrop") });
        }
        // 元数据源可能少返回尚未播出的集；以订阅目标补齐，但不把“已下载”当作入库证据。
        var known = result.Select(episode => episode.Number).ToHashSet();
        if (subscription.IsSeries && current.TotalEpisodes is > 0 and <= 10000)
            for (var number = current.StartEpisode; number <= current.TotalEpisodes; number++)
                if (known.Add(number)) result.Add(new(number, "", "", [], []));
        return new(result.OrderBy(episode => episode.Number).ToArray());
    }

    private static IReadOnlyList<string> Files(JsonElement episode, string key)
    {
        var value = MoviePilotSubscription.Member(episode, key);
        if (value.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(value.GetString()) ? [] : [value.GetString()!];
        if (value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Select(file =>
        {
            var path = MoviePilotSubscription.Text(file, "file_path");
            var server = MoviePilotSubscription.Text(file, "server");
            var item = MoviePilotSubscription.Text(file, "itemid");
            return path.Length > 0 ? path : item.Length > 0 ? $"{server} · 媒体条目 {item}" : "";
        }).Where(file => file.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<MoviePilotSubscriptionLibraryFile> LibraryEntries(JsonElement episode)
    {
        var value = MoviePilotSubscription.Member(episode, "library");
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } path) return [new(path, "", "")];
        if (value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Select(file => new MoviePilotSubscriptionLibraryFile(
            MoviePilotSubscription.Text(file, "file_path"), MoviePilotSubscription.Text(file, "server_type"), MoviePilotSubscription.Text(file, "itemid"))).ToArray();
    }
}

/// <summary>只提交用户实际改动的可编辑字段，服务器维护的状态、入库事实及其他客户端的设置不回写。</summary>
public sealed record MoviePilotSubscriptionEdit(string Keyword, string Include, string Exclude,
    string Quality, string Resolution, string Effect, int TotalEpisodes, int StartEpisode)
{
    public static MoviePilotSubscriptionEdit From(MoviePilotSubscription subscription) => new(
        MoviePilotSubscription.Text(subscription.Data, "keyword"), MoviePilotSubscription.Text(subscription.Data, "include"),
        MoviePilotSubscription.Text(subscription.Data, "exclude"), MoviePilotSubscription.Text(subscription.Data, "quality"),
        MoviePilotSubscription.Text(subscription.Data, "resolution"), MoviePilotSubscription.Text(subscription.Data, "effect"),
        subscription.TotalEpisodes ?? 0, MoviePilotSubscription.Number(subscription.Data, "start_episode") ?? 0);

    public IReadOnlyDictionary<string, object?> Changes(MoviePilotSubscription original)
    {
        var before = From(original);
        if (TotalEpisodes is < 0 or > 10000 || StartEpisode < 0 || TotalEpisodes > 0 && StartEpisode > TotalEpisodes)
            throw new MoviePilotException("请填写有效的起始集和总集数，起始集不能大于总集数");
        var result = new Dictionary<string, object?> { ["id"] = original.Id };
        Add("keyword", before.Keyword, Keyword); Add("include", before.Include, Include); Add("exclude", before.Exclude, Exclude);
        Add("quality", before.Quality, Quality); Add("resolution", before.Resolution, Resolution); Add("effect", before.Effect, Effect);
        if (original.IsSeries && before.TotalEpisodes != TotalEpisodes) result["total_episode"] = TotalEpisodes;
        if (original.IsSeries && before.StartEpisode != StartEpisode) result["start_episode"] = StartEpisode;
        return result;
        void Add(string name, string oldValue, string newValue)
        {
            if (oldValue != newValue.Trim()) result[name] = string.IsNullOrWhiteSpace(newValue) ? null : newValue.Trim();
        }
    }
}
