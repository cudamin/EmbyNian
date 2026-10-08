using System.Globalization;
using System.Text.Json.Nodes;

namespace EmbyNian.Emby;

public sealed class EmbyVirtualFolder(JsonObject document)
{
    public JsonObject Document { get; } = document;
    public string Id => EmbyLibraryOptions.Text(Document, "ItemId") is { Length: > 0 } id ? id : EmbyLibraryOptions.Text(Document, "Id");
    public string Name => EmbyLibraryOptions.Text(Document, "Name");
    public string ContentType => EmbyLibraryOptions.Text(Document, "CollectionType");
    public JsonObject Options => Document["LibraryOptions"] as JsonObject ?? new();
    public JsonArray Paths => Options["PathInfos"] is JsonArray { Count: > 0 } paths ? paths :
        new JsonArray((Document["Locations"] as JsonArray ?? []).Select(path => (JsonNode)new JsonObject { ["Path"] = path?.ToString() }).ToArray());
    public bool CanManagePaths => ContentType is not ("boxsets" or "playlists");
    public bool CanRemove => ContentType != "boxsets";
    public bool CanRefresh => ContentType is not ("boxsets" or "livetv");
}

/// <summary>媒体库的草稿合并和协议语义。未知插件字段保留；只提交用户实际修改的字段。</summary>
public static class EmbyLibraryOptions
{
    public static string Text(JsonObject document, string key, string fallback = "") => document[key]?.ToString() ?? fallback;
    public static bool Flag(JsonObject document, string key, bool fallback = false) => bool.TryParse(document[key]?.ToString(), out var value) ? value : fallback;
    public static double Number(JsonObject document, string key, double fallback = 0) => double.TryParse(document[key]?.ToString(), CultureInfo.InvariantCulture, out var value) ? value : fallback;
    public static string[] Strings(JsonObject document, string key) => (document[key] as JsonArray ?? []).Select(item => item?.ToString() ?? "").ToArray();
    public static JsonArray Array(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    public static JsonObject Merge(JsonObject latest, JsonObject original, JsonObject draft)
    {
        var merged = (JsonObject)latest.DeepClone();
        foreach (var key in original.Select(pair => pair.Key).Union(draft.Select(pair => pair.Key)))
        {
            original.TryGetPropertyValue(key, out var before);
            draft.TryGetPropertyValue(key, out var after);
            if (JsonNode.DeepEquals(before, after)) continue;
            if (!draft.ContainsKey(key)) merged.Remove(key);
            else if (before is JsonObject beforeObject && after is JsonObject afterObject)
                merged[key] = Merge(merged[key] as JsonObject ?? new(), beforeObject, afterObject);
            else if (key is "TypeOptions" or "ImageOptions" && after is JsonArray afterArray)
                merged[key] = MergeTypes(merged[key] as JsonArray ?? [], before as JsonArray ?? [], afterArray);
            else merged[key] = after?.DeepClone();
        }
        return merged;
    }

    private static JsonArray MergeTypes(JsonArray latest, JsonArray original, JsonArray draft)
    {
        var result = (JsonArray)latest.DeepClone();
        foreach (var after in draft.OfType<JsonObject>())
        {
            var type = Text(after, "Type");
            var before = original.OfType<JsonObject>().FirstOrDefault(item => Text(item, "Type") == type) ?? new JsonObject();
            if (JsonNode.DeepEquals(before, after)) continue;
            var current = result.OfType<JsonObject>().FirstOrDefault(item => Text(item, "Type") == type);
            var merged = Merge(current ?? new(), before, after);
            // 编辑器的基线可能为缺失的类型补了展示默认值。第一次保存该类型时，
            // Type 虽然相对基线没有变化，仍是服务器识别这条配置所必需的键。
            merged["Type"] = type;
            if (current is null) result.Add(merged); else result[result.IndexOf(current)] = merged;
        }
        return result;
    }

    public static JsonObject TypeOptions(JsonObject document, string type, string key = "TypeOptions")
    {
        if (document[key] is not JsonArray items) document[key] = items = [];
        var item = items.OfType<JsonObject>().FirstOrDefault(item => Text(item, "Type") == type);
        if (item is null) items.Add(item = new JsonObject { ["Type"] = type });
        return item;
    }

    public static JsonObject NewOptions(JsonObject available, string type)
    {
        var options = available["DefaultLibraryOptions"] is JsonObject defaults ? (JsonObject)defaults.DeepClone() : new JsonObject();
        if (available["DefaultLibraryOptions"] is null)
            foreach (var item in (available["TypeOptions"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var target = TypeOptions(options, Text(item, "Type"));
                foreach (var key in new[] { "MetadataFetchers", "ImageFetchers" })
                    target[key] = Array((item[key] as JsonArray ?? []).OfType<JsonObject>().Where(provider => Flag(provider, "DefaultEnabled")).Select(provider => Text(provider, "Name")));
            }
        options["ContentType"] = type;
        options["EnableArchiveMediaFiles"] = false;
        options["EnableInternetProviders"] = true;
        options["EnableAudioResume"] = type == "audiobooks";
        return options;
    }

    public static void ValidateNew(string name, JsonObject options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var paths = (options["PathInfos"] as JsonArray ?? []).OfType<JsonObject>().Select(item => Text(item, "Path")).ToArray();
        if (paths.Length == 0 || paths.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("请至少添加一个媒体文件夹。");
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw new ArgumentException("不能重复添加同一个媒体文件夹。");
        Validate(options);
    }

    public static void Validate(JsonObject options)
    {
        var min = Number(options, "MinResumePct", 2);
        var max = Number(options, "MaxResumePct", 90);
        if (!double.IsFinite(min) || !double.IsFinite(max) || min < 0 || max > 100 || max <= min)
            throw new ArgumentException("最大恢复播放百分比必须大于最小值，且均在 0～100 之间。");
    }

    public static string TypeName(string type) => type switch
    {
        "movies" or "Movie" => "电影",
        "tvshows" or "Series" => "电视剧",
        "music" => "音乐",
        "audiobooks" or "AudioBook" => "有声读物",
        "books" or "Book" => "书籍",
        "games" or "Game" => "游戏",
        "musicvideos" or "MusicVideo" => "音乐视频",
        "homevideos" or "Video" => "家庭视频和照片",
        "boxsets" or "BoxSet" => "合集",
        "playlists" or "Playlist" => "播放列表",
        "Season" => "季",
        "Episode" => "集",
        "MusicAlbum" => "音乐专辑",
        "MusicArtist" => "音乐人",
        "Audio" => "歌曲",
        "Photo" => "照片",
        "" or "mixed" => "混合内容",
        _ => type
    };
}
