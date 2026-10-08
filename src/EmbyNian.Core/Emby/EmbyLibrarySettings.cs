using System.Globalization;
using System.Text.Json.Nodes;

namespace EmbyNian.Emby;

public sealed record LibraryChoice(string Value, string Name);

public sealed record LibrarySetting(string Key, string Group, string Label, string Kind = "toggle", string Default = "false",
    string Note = "", double Minimum = 0, double Maximum = int.MaxValue, LibraryChoice[]? Choices = null,
    string Types = "*", string Dependency = "")
{
    public bool AppliesTo(string type) => Types == "*" || Types.Split('|').Contains(type.Length == 0 ? "mixed" : type, StringComparer.Ordinal);

    public string Read(JsonObject options) => Key switch
    {
        "MultiVersion" => (EmbyLibraryOptions.Flag(options, "EnableMultiVersionByFiles"), EmbyLibraryOptions.Flag(options, "EnableMultiVersionByMetadata")) switch
        { (true, true) => "both", (true, false) => "files", (false, true) => "metadata", _ => "none" },
        "Thumbnails" => Schedule(options, "EnableChapterImageExtraction", "ExtractChapterImagesDuringLibraryScan"),
        "IntroDetection" => Schedule(options, "EnableMarkerDetection", "EnableMarkerDetectionDuringLibraryScan"),
        "SampleIgnoreSize" => (EmbyLibraryOptions.Number(options, Key) / 1024 / 1024).ToString(CultureInfo.InvariantCulture),
        _ => EmbyLibraryOptions.Text(options, Key, Default)
    };

    public void Write(JsonObject options, string value)
    {
        if (Key is "MultiVersion")
        {
            options["EnableMultiVersionByFiles"] = value is "both" or "files";
            options["EnableMultiVersionByMetadata"] = value is "both" or "metadata";
        }
        else if (Key is "Thumbnails" or "IntroDetection")
        {
            options[Key == "Thumbnails" ? "EnableChapterImageExtraction" : "EnableMarkerDetection"] = value.Length > 0;
            options[Key == "Thumbnails" ? "ExtractChapterImagesDuringLibraryScan" : "EnableMarkerDetectionDuringLibraryScan"] = value == "scanandtask";
        }
        else if (Kind == "toggle") options[Key] = bool.Parse(value);
        else if (Kind == "number" || Kind == "numberchoice")
        {
            if (!double.TryParse(value, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)
                || number < Minimum || number > Maximum || number != Math.Truncate(number)) throw new ArgumentException($"{Label} 的数值无效。");
            options[Key] = checked((int)(Key == "SampleIgnoreSize" ? number * 1024 * 1024 : number));
        }
        else options[Key] = value;
    }

    private static string Schedule(JsonObject options, string enabled, string duringScan) => !EmbyLibraryOptions.Flag(options, enabled) ? "" : EmbyLibraryOptions.Flag(options, duringScan) ? "scanandtask" : "task";
}

/// <summary>按 Emby 4.10 的 libraryoptionseditor 定义可见选项；提供者和语言由服务器补充。</summary>
public static class EmbyLibrarySettings
{
    private const string Normal = "movies|tvshows|homevideos|musicvideos|music|audiobooks|books|games|mixed";
    private const string Video = "movies|tvshows|homevideos|musicvideos|mixed";
    private const string Movies = "movies|homevideos|musicvideos|mixed";
    private static readonly LibraryChoice[] Schedule = [new("", "从不"), new("task", "作为计划任务"), new("scanandtask", "作为计划任务和添加媒体时")];
    private static LibraryChoice[] Days(params int[] days) => [new("0", "从不"), .. days.Select(day => new LibraryChoice(day.ToString(CultureInfo.InvariantCulture), $"每 {day} 天"))];
    private static LibraryChoice[] Age() => [new("0", "不限制时间"), .. new[] { 14, 30, 60, 90, 120, 180 }.Select(day => new LibraryChoice(day.ToString(CultureInfo.InvariantCulture), $"{day} 天"))];
    private static LibraryChoice[] Numbers(params int[] values) => values.Select(value => new LibraryChoice(value.ToString(CultureInfo.InvariantCulture), value.ToString(CultureInfo.InvariantCulture))).ToArray();

    public static IReadOnlyList<LibrarySetting> All { get; } =
    [
        new("MusicFolderStructure", "文件夹结构", "音乐文件夹结构", "choice", "", Choices: [new("", "其他或非结构化"), new("artist_album_track", "艺术家 / 专辑 / 曲目"), new("album_track", "专辑 / 曲目")], Types: "music|audiobooks"),
        new("PreferredMetadataLanguage", "媒体库设置", "元数据下载语言", "language", "", Dependency: "metadata"),
        new("MetadataCountryCode", "媒体库设置", "分级国家或地区", "country", "", Dependency: "metadata"),
        new("PreferredImageLanguage", "媒体库设置", "图片下载语言", "language", "", Dependency: "metadata"),
        new("EnablePhotos", "媒体库设置", "启用照片", Types: "homevideos"),
        new("ImportPlaylists", "媒体库设置", "导入播放列表文件", Default: "true", Types: "music|audiobooks|musicvideos|mixed"),
        new("EnableEmbeddedTitles", "媒体库设置", "优先使用内嵌标题而不是文件名", Types: "movies|tvshows|homevideos|musicvideos|audiobooks|mixed"),
        new("EnableRealtimeMonitor", "媒体库设置", "启用实时监控", Note: "文件有变化时立即扫描；部分网络文件系统不支持实时监控。", Types: Normal),
        new("ExcludeFromSearch", "媒体库设置", "从全局搜索中排除", Types: Normal),
        new("MergeTopLevelFolders", "文件夹选项", "在文件夹视图中合并顶层文件夹的内容", Types: Normal),
        new("ForceCollapseSingleItemFolders", "文件夹选项", "使用旧版文件夹扫描模式", Note: "尝试折叠仅含一个视频的文件夹；可能影响附加内容和多版本识别。", Types: "movies|mixed"),
        new("SampleIgnoreSize", "忽略文件", "忽略小于此大小的 sample 文件（MB）", "number", "0", Maximum: 2047, Types: Video),
        new("EnablePlexIgnore", "忽略文件", "将 .plexignore 作为 .embyignore 的别名", Types: Normal),
        new("EnableAutomaticSeriesGrouping", "自动分组", "自动合并分布在多个文件夹中的剧集", Types: "tvshows"),
        new("EnableMultiPartItems", "自动分组", "启用多部分项目", Types: Movies),
        new("MultiVersion", "自动分组", "多版本项目分组", "choice", "none", Choices: [new("both", "根据文件和元数据检测"), new("files", "根据文件检测"), new("metadata", "根据元数据检测"), new("none", "无")], Types: Movies),
        new("ImportCollections", "合集", "从元数据下载器导入合集信息", Dependency: "collections"),
        new("MinCollectionItems", "合集", "最小自动合集大小", "numberchoice", "2", Minimum: 1, Maximum: 4, Choices: Numbers(1, 2, 3, 4), Dependency: "collectionsize"),
        new("EnableAdultMetadata", "元数据下载", "允许成人元数据", Dependency: "adult"),
        new("AutomaticRefreshIntervalDays", "元数据下载", "首次导入后自动刷新元数据", "numberchoice", "0", Choices: Days(30, 60, 90), Dependency: "metadata"),
        new("PlaceholderMetadataRefreshIntervalDays", "元数据下载", "自动刷新占位标题（例如 TBA）的集元数据", "numberchoice", "0", Choices: Days(2, 3, 7, 14, 30, 60, 90), Types: "tvshows|mixed", Dependency: "metadata"),
        new("SaveLocalMetadata", "图片保存", "保存媒体图片到媒体文件夹", Types: Normal, Dependency: "images"),
        new("CacheImages", "图片保存", "在服务器元数据文件夹中缓存图片", Types: Normal),
        new("SaveMetadataHidden", "图片保存", "将元数据和图片保存为隐藏文件", Types: Normal, Dependency: "windows"),
        new("DownloadImagesInAdvance", "图片保存", "预先下载图片", Note: "在扫描时下载所有图片，而不是等到浏览时再下载。", Dependency: "images"),
        new("AutoGenerateChapters", "章节", "为没有内嵌章节的视频生成章节", Types: Video),
        new("AutoGenerateChapterIntervalMinutes", "章节", "生成章节间隔（分钟）", "numberchoice", "5", Minimum: 1, Choices: Numbers(3, 4, 5, 10, 15, 20), Types: Video, Dependency: "chapters"),
        new("Thumbnails", "视频预览缩略图", "生成视频预览缩略图", "choice", "", Note: "由服务器执行；需要有效的 Emby Premiere 授权。", Choices: Schedule, Types: Video),
        new("ThumbnailImagesIntervalSeconds", "视频预览缩略图", "缩略图间隔", "numberchoice", "10", Minimum: -1, Choices: [new("10", "10 秒"), new("-1", "章节标记")], Types: Video, Dependency: "thumbnails"),
        new("SaveLocalThumbnailSets", "视频预览缩略图", "保存视频预览缩略图到媒体文件夹", Types: Video, Dependency: "thumbnailsets"),
        new("IntroDetection", "片头检测", "启用片头检测", "choice", "", Note: "由服务器执行；需要有效的 Emby Premiere 授权。", Choices: Schedule, Types: "tvshows|mixed"),
        new("SubtitleDownloadLanguages", "自动字幕下载", "下载语言", "languages", "", Dependency: "subtitles"),
        new("RequirePerfectSubtitleMatch", "自动字幕下载", "要求字幕与视频完美匹配", Dependency: "subtitles"),
        new("ForcedSubtitlesOnly", "自动字幕下载", "仅搜索强制字幕", Dependency: "subtitles"),
        new("SubtitleDownloadMaxAgeDays", "自动字幕下载", "停止为超过此时间添加的媒体下载字幕", "numberchoice", "180", Choices: Age(), Dependency: "subtitles"),
        new("SkipSubtitlesIfAudioTrackMatches", "自动字幕下载", "默认音轨语言匹配时跳过", Dependency: "subtitles"),
        new("SkipSubtitlesIfEmbeddedSubtitlesPresent", "自动字幕下载", "已经包含匹配的内嵌字幕时跳过", Dependency: "subtitles"),
        new("SaveSubtitlesWithMedia", "自动字幕下载", "将下载的字幕保存到媒体文件夹", Dependency: "subtitles"),
        new("LyricsDownloadLanguages", "自动歌词下载", "下载语言", "languages", "", Dependency: "lyrics"),
        new("LyricsDownloadMaxAgeDays", "自动歌词下载", "停止为超过此时间添加的媒体下载歌词", "numberchoice", "180", Choices: Age(), Dependency: "lyrics"),
        new("SaveLyricsWithMedia", "自动歌词下载", "将歌词保存到媒体文件夹", Dependency: "lyrics"),
        new("MinResumePct", "播放", "最小恢复播放百分比", "number", "2", Maximum: 100, Types: Video + "|audiobooks"),
        new("MaxResumePct", "播放", "最大恢复播放百分比", "number", "90", Minimum: 1, Maximum: 100, Types: Video + "|audiobooks"),
        new("MinResumeDurationSeconds", "播放", "最小恢复播放时长（秒）", "number", "120", Types: Video + "|audiobooks")
    ];

    public static IReadOnlyList<LibrarySetting> Advanced { get; } =
    [
        new("UseFileCreationTimeForDateAdded", "高级", "使用文件创建时间作为添加日期", Note: "关闭时使用媒体导入时间。"),
        new("MetadataPath", "元数据", "元数据路径", "text", "", Note: "填写服务器上的路径；改动后需迁移已有元数据并运行扫描。"),
        new("MetadataNetworkPath", "元数据", "共享网络路径（可选）", "text", ""),
        new("EnableSavedMetadataForPeople", "元数据", "启用人物元数据的读取和写入"),
        new("EnableExternalContentInSuggestions", "元数据", "在推荐中启用外部内容"),
        new("PreferredMetadataLanguage", "首选元数据语言", "元数据下载语言", "language", ""),
        new("MetadataCountryCode", "首选元数据语言", "分级国家或地区", "country", "")
    ];
}
