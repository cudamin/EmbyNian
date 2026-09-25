using System.Globalization;
using System.Text.Json;
using EmbyNian.Emby;

namespace EmbyNian.MoviePilot;

public sealed record MoviePilotStorage(string Name, string Type);

public sealed record MoviePilotMediaSource(string Name, string Id, IReadOnlyList<string> Types);

public sealed record MoviePilotDirectory(string Storage, string Path, string? TransferType, string? OverwriteMode);

public sealed record MoviePilotTransferOptions(
    IReadOnlyList<MoviePilotStorage> Storages,
    IReadOnlyList<MoviePilotDirectory> Directories,
    IReadOnlyList<MoviePilotMediaSource> MediaSources);

/// <summary>要整理的一个文件：Emby 侧的名字加完整路径，路径必须是 MoviePilot 看得见的那一份。</summary>
public sealed record MoviePilotTransferFile(string Name, string Path);

/// <summary>
/// 一次手动整理要带走的全部身份：文件（可一批 —— 季/整部剧是多个单集）、按什么类型整理、对上哪部片、
/// 季号集号。电影用自己的 TMDB 号，剧/季/单集都用所属剧集的 TMDB 号 —— MoviePilot 认的是剧，集号另算。
/// </summary>
public sealed record MoviePilotTransferContext(
    string Title,
    string Type,
    string MediaId,
    int? Season,
    string Episode,
    IReadOnlyList<MoviePilotTransferFile> Files)
{
    public static bool Supports(EmbyItem item) =>
        item.Type is EmbyItemType.Movie or EmbyItemType.Series or EmbyItemType.Season or EmbyItemType.Episode;
}

/// <summary>
/// 表单里长成什么样的整理请求。<see cref="Problem"/> 把每一处不能成立的话说成人话 —— 校验放 Core，弹窗和
/// 测试问的是同一份。
/// </summary>
public sealed record MoviePilotTransferRequest
{
    public const string TypeMovie = "电影";
    public const string TypeSeries = "电视剧";

    public IReadOnlyList<MoviePilotTransferFile> Files { get; init; } = [];
    public string TargetStorage { get; init; } = "";
    public string TargetPath { get; init; } = "";
    /// <summary>空 = 不指定，按目的目录配置来。</summary>
    public string TransferType { get; init; } = "";
    public string Type { get; init; } = TypeMovie;
    public string MediaSource { get; init; } = "themoviedb";
    public string MediaId { get; init; } = "";
    public string Season { get; init; } = "";
    public string Episode { get; init; } = "";
    public string Part { get; init; } = "";
    public string MinimumSize { get; init; } = "0";
    public bool TypeFolder { get; init; }
    public bool CategoryFolder { get; init; }
    public bool Scrape { get; init; }
    public bool FromHistory { get; init; }
    public bool Reorganize { get; init; }

    /// <summary>一批里第一个文件的路径，或「N 个文件」—— 显示和日志用，不参与协议。</summary>
    public string SourceDisplay => Files.Count switch
    {
        0 => "",
        1 => Files[0].Path,
        _ => $"{Files[0].Path} 等 {Files.Count} 个文件"
    };

    public string? Problem
    {
        get
        {
            if (Files.Count == 0) return "没有可整理的文件";
            if (TargetPath.Length > 0 && TargetStorage.Length == 0)
                return "指定目标路径时，请同时选择目标存储";
            if (TargetStorage == "local" && TargetPath.Length > 0 && !IsAbsolutePath(TargetPath))
                return "目标路径须为 MoviePilot 上的本地绝对路径，不能是网址或磁盘根目录";
            if (TransferType is not ("" or "copy" or "move" or "link" or "softlink"))
                return "请选择有效的整理方式";
            if (Type is not (TypeMovie or TypeSeries)) return "请选择电影或电视剧";
            if (MediaId.Length > 0 && MediaSource.Length == 0) return "指定媒体编号时，请选择数据源";
            if (MediaId.Length > 0 && MediaSource == "themoviedb" &&
                (!long.TryParse(MediaId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0))
                return "TheMovieDb 编号须为正整数；按片名查找请用编号框上的查找";
            if (!int.TryParse(MinimumSize, NumberStyles.None, CultureInfo.InvariantCulture, out var minimum) || minimum < 0)
                return "最小文件大小须为非负整数，单位 MB";
            if (Type == TypeSeries && Season.Length > 0 &&
                (!int.TryParse(Season, NumberStyles.None, CultureInfo.InvariantCulture, out var season) || season < 0))
                return "季号须为非负整数（特别篇为 0）";
            return null;
        }
    }

    /// <summary>
    /// 拼请求正文。文件走 <see cref="MoviePilotTransfer.RequireFile"/> 一一核对 —— 服务器认的和表单里的
    /// 对不上就抛，不会「顺手」整理别的路径。
    /// </summary>
    public Dictionary<string, object?> Body(IReadOnlyList<JsonElement> files, bool preview)
    {
        if (Problem is { } problem) throw new MoviePilotException(problem);
        if (files.Count != Files.Count) throw new MoviePilotException("文件清单和表单对不上，已停止");

        for (var index = 0; index < files.Count; index++)
            MoviePilotTransfer.RequireFile(files[index], Files[index].Path);

        var body = new Dictionary<string, object?>
        {
            [Files.Count == 1 ? "fileitem" : "fileitems"] = files.Count == 1 ? files[0] : (object?)files,
            ["type_name"] = Type,
            ["min_filesize"] = int.Parse(MinimumSize, CultureInfo.InvariantCulture),
            ["scrape"] = Scrape,
            ["library_type_folder"] = TypeFolder,
            ["library_category_folder"] = CategoryFolder,
            ["from_history"] = FromHistory,
            ["reorganize"] = Reorganize,
            ["skip_success"] = !Reorganize,
            ["preview"] = preview
        };

        if (TargetStorage.Length > 0) body["target_storage"] = TargetStorage;
        if (TargetPath.Length > 0) body["target_path"] = TargetPath;
        if (TransferType.Length > 0) body["transfer_type"] = TransferType;
        if (MediaId.Length > 0)
        {
            body["media_source"] = MediaSource;
            body["media_id"] = MediaId;
        }
        if (Part.Length > 0) body["episode_part"] = Part;
        if (Type == TypeSeries)
        {
            if (Season.Length > 0) body["season"] = int.Parse(Season, CultureInfo.InvariantCulture);
            if (Episode.Length > 0) body["episode_detail"] = Episode;
        }

        return body;
    }

    private static bool IsAbsolutePath(string value) => MoviePilotTransfer.IsAbsolutePath(value);
}

/// <summary>
/// 预览／提交回执里的一行。是个普通类而不是 record：XAML 的 DataTemplate 会为 x:DataType 生成激活代码，
/// init-only 属性在那里赋不了值（CS8852，编译过不去）；这一行的字段本来就一次填好不再变。
/// </summary>
public sealed class MoviePilotTransferLine
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Title { get; set; } = "";
    public string State { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";

    public string Status => State switch
    {
        "preview" => Success ? "可整理" : "无法整理",
        "accepted" => "已接收，等待整理",
        "completed" => "已完成",
        "failed" => "失败",
        "retry_wait" => "等待重试",
        "skipped" => "已跳过",
        "manual_review" => "需要人工复核",
        _ => "状态未确认"
    };

    /// <summary>预览清单里一行的大字：会搬到哪儿（或源是什么）。</summary>
    public string Headline =>
        Target is { Length: > 0 } ? Target : Source is { Length: > 0 } ? Source : Title is { Length: > 0 } ? Title : "（没有路径）";

    /// <summary>同一行的小字：状态加失败的那句话。</summary>
    public string StatusLine
    {
        get
        {
            var line = Status;
            if (Message is { Length: > 0 }) line += $" — {Message}";
            return line;
        }
    }
}

public sealed record MoviePilotTransferResult(
    bool Success, string Message, IReadOnlyList<MoviePilotTransferLine> Items, bool IsPreview)
{
    /// <summary>预览可整理才放行提交 —— 没看过预览就按整理，等于蒙着眼搬文件。</summary>
    public bool CanSubmit => IsPreview && Success && Items.Count > 0 &&
        Items.All(item => item.Success && item.Target.Length > 0);

    public string Summary => IsPreview
        ? $"预览 {Items.Count} 项：可整理 {Items.Count(item => item.Success)}，失败 {Items.Count(item => !item.Success)}"
        : $"已完成 {Items.Count(item => item.State == "completed")}，已接收 {Items.Count(item => item.State == "accepted")}，" +
          $"已跳过 {Items.Count(item => item.State == "skipped")}，失败或待处理 {Items.Count(item => item.State is not ("completed" or "accepted" or "skipped"))}";
}

public sealed record MoviePilotTransferPreview(
    MoviePilotTransferRequest Request, IReadOnlyList<JsonElement> Files, MoviePilotTransferResult Result);

public static class MoviePilotTransfer
{
    public static string PathName(string path) => path.Replace('\\', '/').TrimEnd('/').Split('/')[^1];

    /// <summary>
    /// 只认本地绝对路径（含 UNC）。MoviePilot 在另一台机器上，Emby 报来的相对路径或网址对它没有意义；
    /// 磁盘根目录（<c>/</c>、<c>D:\</c>）也挡下 —— 整理根目录等于把整个盘搬走。
    /// </summary>
    public static bool IsAbsolutePath(string value)
    {
        var path = value.Trim();
        if (path.Length == 0 || path.Contains("://", StringComparison.Ordinal) || path.Any(char.IsControl)) return false;

        var normal = path.Replace('\\', '/');
        if (normal.Split('/').Any(part => part is "." or "..")) return false;

        if (normal.StartsWith("//", StringComparison.Ordinal))
            return normal.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 3;

        if (normal.StartsWith('/')) return normal.Trim('/').Length > 0;

        return normal.Length > 3 && char.IsAsciiLetter(normal[0]) && normal[1] == ':' && normal[2] == '/' &&
            normal[3..].Trim('/').Length > 0;
    }

    /// <summary>Windows 路径不区分大小写；同一台机器上的判断才这样，远端路径按原样比。</summary>
    public static bool SamePath(string left, string right) =>
        string.Equals(
            left.Replace('\\', '/').TrimEnd('/'),
            right.Replace('\\', '/').TrimEnd('/'),
            left.Length > 1 && left[1] == ':' || left.StartsWith("\\\\", StringComparison.Ordinal)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    /// <summary>
    /// 「storage/list 回来的这份文件就是表单里那一个」—— 存储类型、文件身份、路径三者都对上才放行。
    /// 路径对不上时宁可整个停掉：改成整理父目录的「兜底」会把没点名的文件一起搬走。
    /// </summary>
    public static void RequireFile(JsonElement file, string path)
    {
        if (file.ValueKind != JsonValueKind.Object ||
            Text(file, "storage") is not ("" or "local") ||
            Text(file, "type") is not ("file" or "dir") ||
            !SamePath(Text(file, "path"), path))
            throw new MoviePilotException(
                $"MoviePilot 返回的文件和要整理的路径对不上（{path}），已停止；不会改为整理别的目录");
    }

    public static string ParentPath(string path)
    {
        var normal = path.Replace('\\', '/').TrimEnd('/');
        var last = normal.LastIndexOf('/');
        if (last < 0) return "/";
        if (last <= 2 && normal[1] == ':') return normal[..(last + 1)];
        return last == 0 ? "/" : normal[..last];
    }

    public static MoviePilotTransferResult ParseResult(JsonElement data, bool success, string message, bool preview)
    {
        var lines = new List<MoviePilotTransferLine>();
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                lines.Add(new MoviePilotTransferLine
                {
                    Source = Text(item, "source"),
                    Target = Text(item, "target"),
                    Title = Text(item, "title"),
                    State = preview ? "preview" : Text(item, "state"),
                    Success = Bool(item, "success"),
                    Message = Text(item, "message")
                });
            }
        }

        var detail = Text(data, "message");
        return new MoviePilotTransferResult(success, detail.Length > 0 ? detail : message, lines, preview);
    }

    /// <summary>按下去之前给用户看的那段话：会动哪些文件、怎么动、覆盖和清历史的后果。</summary>
    public static string Confirmation(MoviePilotTransferRequest request, MoviePilotTransferResult preview, bool background)
    {
        var mode = request.TransferType switch
        {
            "copy" => "复制",
            "move" => "移动（源文件会被移走）",
            "link" => "硬链接",
            "softlink" => "软链接",
            _ => "按目的目录的配置（可能是移动）"
        };

        return $"{(background ? "把整理加入 MoviePilot 队列" : "现在整理")}，预览共 {preview.Items.Count} 项。\n" +
            $"源：{request.SourceDisplay}\n整理方式：{mode}\n" +
            "目标同名文件按 MoviePilot 的覆盖规则处理，同名字幕、音轨可能一起整理；Emby 里记录的原路径可能失效。" +
            (request.Reorganize ? "\n重新整理会清理命中的成功历史和旧目标文件，这一步无法在本客户端撤销。" : "") +
            (request.FromHistory ? "\n已开启复用历史识别，旧历史里的媒体信息优先于本次填写的编号。" : "");
    }

    public static IEnumerable<JsonElement> Array(JsonElement data) =>
        data.ValueKind == JsonValueKind.Array ? data.EnumerateArray() : [];

    public static string Text(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    public static bool Bool(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
}
