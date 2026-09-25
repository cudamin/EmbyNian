using System.Text.Json;

namespace EmbyNian.MoviePilot;

/// <summary>
/// 把 <c>media/search</c> 的回话拆成 <see cref="MoviePilotMedia"/>，以及把一条结果拼成订阅正文。
/// <para>
/// 「每种输入有唯一正确答案」的活，按项目规矩住在 Core 并配测试（见 <c>MoviePilotTests</c>），夹具用真机抄下来
/// 的 JSON。对形状和字段名都放宽，理由同 <see cref="MoviePilotProbe.Names"/>：认不出来会在屏上显示成「没搜到」，
/// 而「没搜到」和「拆错了」长得一模一样，所以宁可多认几种写法，也不因为一个字段名对不上就整片空掉。
/// </para>
/// </summary>
public static class MoviePilotMediaParser
{
    /// <summary>
    /// 一片搜索结果。<c>media/search</c> 在这台服务器上回的是裸数组；万一被信封或某个键（<c>list</c>/<c>items</c>/
    /// <c>data</c>）包了一层也认，免得服务器换个版本就整片空掉。
    /// </summary>
    public static IReadOnlyList<MoviePilotMedia> Parse(JsonElement data)
    {
        if (FindArray(data) is not { } array) return [];

        var list = new List<MoviePilotMedia>();
        foreach (var item in array.EnumerateArray())
            if (ToMedia(item) is { } media)
                list.Add(media);

        return list;
    }

    /// <summary>
    /// 新增订阅的正文：只带 MoviePilot 真正会读的那几项 —— <c>name</c>、<c>type</c>，以及身份对
    /// <c>media_source</c>+<c>media_id</c>。空值一律不放进去。
    /// <para>
    /// <b>故意不带 <c>year</c></b>：真机实测（v3.0.1）订阅回「请求参数不正确」的病根就在这里 —— 订阅 schema 的
    /// <c>year</c> 是 <c>Optional[str]</c>，而我们的 <see cref="MoviePilotMedia.Year"/> 是数字，发过去 pydantic v2
    /// 不把 int 当 str，整条请求 422。年份本就多余：<c>media_source</c>+<c>media_id</c>（TMDB id 那一对）已经唯一
    /// 定位这部片，服务器会自己按它取全元数据。少发一个字段，就少一处能把整次订阅顶回来的地方。
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, object?> SubscribeBody(MoviePilotMedia media)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = media.Title
        };

        if (!string.IsNullOrWhiteSpace(media.Type)) body["type"] = media.Type;
        if (!string.IsNullOrWhiteSpace(media.MediaSource)) body["media_source"] = media.MediaSource;
        if (!string.IsNullOrWhiteSpace(media.MediaId)) body["media_id"] = media.MediaId;

        return body;
    }

    /// <summary>
    /// 一片资源搜索结果（<c>search/media/{id}</c> 回的 <c>Context</c> 列表）拆成种子行。没有 <c>torrent_info</c>
    /// 或它没有标题的跳过。身份对由调用方按「搜的是哪部片」传进来，盖在每一条上 —— 精确搜出来的种子都属于那一部。
    /// </summary>
    public static IReadOnlyList<MoviePilotResource> ParseResources(JsonElement data, string? mediaSource, string? mediaId)
    {
        if (FindArray(data) is not { } array) return [];

        var list = new List<MoviePilotResource>();
        foreach (var context in array.EnumerateArray())
            if (ToResource(context, mediaSource, mediaId) is { } resource)
                list.Add(resource);

        return list;
    }

    /// <summary>
    /// 下载正文：整份 <c>torrent_info</c> 原样回传给 <c>download/add</c> 的 <c>torrent_in</c>（它收的是完整对象，
    /// 不是一个链接），带上身份对好让服务器认出是哪部片。下载器与保存路径不填，走 MoviePilot 的默认。
    /// </summary>
    public static IReadOnlyDictionary<string, object?> DownloadBody(MoviePilotResource resource)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["torrent_in"] = resource.TorrentInfo
        };

        if (!string.IsNullOrWhiteSpace(resource.MediaSource)) body["media_source"] = resource.MediaSource;
        if (!string.IsNullOrWhiteSpace(resource.MediaId)) body["media_id"] = resource.MediaId;

        return body;
    }

    private static MoviePilotResource? ToResource(JsonElement context, string? mediaSource, string? mediaId)
    {
        if (context.ValueKind != JsonValueKind.Object) return null;

        // 常态是 Context 外面包一层 torrent_info（search/media 和 search/title 都这样）。万一某个版本直接回一层
        // 扁的种子对象（没有 torrent_info 外壳），就把这一层当种子用 —— 认不出来会在屏上变成「没搜到」，和真的
        // 没结果长得一样，所以宁可多认一种形状（同 Parse/Names 的宽容）。
        var torrent = context.TryGetProperty("torrent_info", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object
            ? wrapped
            : context;

        var title = Text(torrent, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        var resolution = context.TryGetProperty("meta_info", out var meta) && meta.ValueKind == JsonValueKind.Object
            ? Text(meta, "resource_pix")
            : null;

        return new MoviePilotResource
        {
            Title = title!,
            SiteName = Text(torrent, "site_name"),
            Size = Long(torrent, "size"),
            Seeders = Number(torrent, "seeders") ?? 0,
            Resolution = resolution,
            PageUrl = Text(torrent, "page_url"),
            TorrentInfo = torrent.Clone(),
            // 身份对优先用「搜的是哪部片」那一对；种子自己带的当兜底。
            MediaSource = string.IsNullOrWhiteSpace(mediaSource) ? Text(torrent, "media_source") : mediaSource,
            MediaId = string.IsNullOrWhiteSpace(mediaId) ? Text(torrent, "media_id") : mediaId
        };
    }

    /// <summary>一条 <c>MediaInfo</c> 拆成模型；没有标题的当噪声跳过 —— 一张没有名字的卡片没什么可显示的。</summary>
    private static MoviePilotMedia? ToMedia(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var title = Text(item, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        return new MoviePilotMedia
        {
            Title = title!,
            Year = Number(item, "year"),
            Type = Text(item, "type"),
            Overview = Text(item, "overview"),
            PosterUrl = Text(item, "poster_path"),
            MediaSource = Text(item, "media_source"),
            MediaId = Text(item, "media_id")
        };
    }

    /// <summary>裸数组直接用；对象就从常见的几个键里找那一片数组。</summary>
    private static JsonElement? FindArray(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array) return data;

        if (data.ValueKind == JsonValueKind.Object)
            foreach (var key in (string[])["list", "items", "data", "results"])
                if (data.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.Array)
                    return nested;

        return null;
    }

    /// <summary>一个字符串成员：字符串原样，数字取其文本（<c>media_id</c>、<c>year</c> 都可能以数字回来）。</summary>
    private static string? Text(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() is { Length: > 0 } text ? text : null,
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    /// <summary>一个整数成员：数字直接取，字符串（如 <c>"2014"</c>）再试着解析一次。取不到就是 null。</summary>
    private static int? Number(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>一个 64 位整数成员（体积可能上 GB，超 int）。数字直接取，字符串再试一次；取不到就是 0。</summary>
    private static long Long(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }
}
