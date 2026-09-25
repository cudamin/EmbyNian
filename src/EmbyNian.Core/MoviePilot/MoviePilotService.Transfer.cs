using System.Text.Json;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;

namespace EmbyNian.MoviePilot;

/// <summary>
/// 「手动整理」那一半 MoviePilot 功能（partial，另一半会话管理在 <see cref="MoviePilotService"/>）。
/// <para>
/// 协议按上游 v3.0.1 核对过（2026-09-24，源码快照在 <c>work/moviepilot-protocol-20260924-M8nFhS</c>）：
/// 目的路径匹配 <c>transfer/manual/target-path</c>、成功历史 <c>transfer/manual/history</c>、执行
/// <c>transfer/manual?background=…</c>（<c>preview:true</c> 只出预览、不动文件）。这一层不猜路径：
/// 源路径先经 <c>storage/list</c> 换成 MoviePilot 自己认的 FileItem，换不出一个真实文件就停，绝不退而
/// 整理父目录 —— 那种「兜底」会把整个目录的文件搬走。
/// </para>
/// <para>
/// 只支持本地存储的源文件：MoviePilot 是另一台机器（或容器），Emby 报来的路径对它可见与否只有服务器
/// 自己知道，<c>storage/list</c> 就是那道问询。
/// </para>
/// </summary>
public sealed partial class MoviePilotService
{
    /// <summary>存储、媒体库目录和刮削源：表单那几颗下拉的内容。</summary>
    public async Task<MoviePilotTransferOptions> TransferOptionsAsync(CancellationToken cancellationToken)
    {
        var storages = MoviePilotTransfer.Array(await CallAsync((apiBase, token) =>
            client.GetAsync(apiBase, token, "storage/options", cancellationToken), cancellationToken)
            .ConfigureAwait(false))
            .Where(item => MoviePilotTransfer.Text(item, "name").Length > 0)
            .Select(item => new MoviePilotStorage(
                MoviePilotTransfer.Text(item, "name"), MoviePilotTransfer.Text(item, "type")))
            .ToList();

        var directories = MoviePilotTransfer.Array(await CallAsync((apiBase, token) =>
            client.GetAsync(apiBase, token, "storage/directories?directory_type=library", cancellationToken),
            cancellationToken).ConfigureAwait(false))
            .Where(item => MoviePilotTransfer.Text(item, "library_path").Length > 0)
            .Select(item => new MoviePilotDirectory(
                MoviePilotTransfer.Text(item, "library_storage") is { Length: > 0 } storage ? storage : "local",
                MoviePilotTransfer.Text(item, "library_path"),
                MoviePilotTransfer.Text(item, "transfer_type"),
                MoviePilotTransfer.Text(item, "overwrite_mode")))
            .ToList();

        var sources = MoviePilotTransfer.Array(await CallAsync((apiBase, token) =>
            client.GetAsync(apiBase, token, "media/source", cancellationToken), cancellationToken)
            .ConfigureAwait(false))
            .Where(item => MoviePilotTransfer.Text(item, "media_source").Length > 0)
            .Select(item => new MoviePilotMediaSource(
                MoviePilotTransfer.Text(item, "name"),
                MoviePilotTransfer.Text(item, "media_source"),
                MoviePilotTransfer.Array(TryMember(item, "media_types"))
                    .Select(one => one.ValueKind == JsonValueKind.String ? one.GetString() ?? "" : "")
                    .Where(kind => kind.Length > 0)
                    .ToList()))
            .ToList();

        if (storages.Count == 0) storages.Add(new MoviePilotStorage("本地", "local"));
        if (sources.Count == 0) sources.Add(new MoviePilotMediaSource("TheMovieDb", "themoviedb", ["电影", "电视剧"]));

        Log.Info(Category, $"MoviePilot 整理选项：{storages.Count} 个存储、{directories.Count} 个媒体库目录、{sources.Count} 个数据源");
        return new MoviePilotTransferOptions(storages, directories, sources);
    }

    /// <summary>
    /// 把 Emby 报来的源路径换成 MoviePilot 的 FileItem。列表里没有这一个文件（路径 MoviePilot 看不见、或
    /// 已被移走）就抛出人话 —— 调用方不该再往下走。
    /// </summary>
    public async Task<JsonElement> TransferFileAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var reply = await CallAsync((apiBase, token) => client.PostReplyAsync(apiBase, token,
            "storage/list?sort=name",
            new Dictionary<string, object?> { ["storage"] = "local", ["type"] = "file", ["path"] = sourcePath },
            cancellationToken), cancellationToken).ConfigureAwait(false);

        var files = MoviePilotTransfer.Array(reply.Data)
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .ToList();

        var file = files.FirstOrDefault(item =>
            MoviePilotTransfer.SamePath(MoviePilotTransfer.Text(item, "path"), sourcePath));
        if (file.ValueKind is not JsonValueKind.Object)
            throw new MoviePilotException(
                $"MoviePilot 看不到这个文件：{sourcePath}。它是 MoviePilot 那台机器上的路径吗？（不会改为整理父目录）");

        return file.Clone();
    }

    /// <summary>一批文件挨个换成 FileItem；哪一个换不出来整批就停，报的那句话里带上是哪一个。</summary>
    public async Task<IReadOnlyList<JsonElement>> TransferFilesAsync(
        IReadOnlyList<MoviePilotTransferFile> files, CancellationToken cancellationToken)
    {
        var resolved = new List<JsonElement>(files.Count);
        foreach (var file in files)
            resolved.Add(await TransferFileAsync(file.Path, cancellationToken).ConfigureAwait(false));
        return resolved;
    }

    /// <summary>
    /// 目的路径匹配： MoviePilot 按源文件算出该去哪个存储、哪个目录、用什么方式 —— 和官方前端的
    /// 「整理目的路径，选择自动将由后缀匹配路径匹配」同一趟调用。匹配不上时服务端回一个空对象，照实交出去。
    /// </summary>
    public async Task<MoviePilotTransferOptions> TransferTargetAsync(
        JsonElement file, CancellationToken cancellationToken)
    {
        var reply = await CallAsync((apiBase, token) => client.PostReplyAsync(apiBase, token,
            "transfer/manual/target-path",
            new Dictionary<string, object?> { ["fileitem"] = file }, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        var data = reply.Data;
        var storage = MoviePilotTransfer.Text(data, "target_storage");
        var path = MoviePilotTransfer.Text(data, "target_path");
        if (storage.Length == 0 || path.Length == 0)
            return new MoviePilotTransferOptions([], [], []);

        return new MoviePilotTransferOptions(
            [new MoviePilotStorage(storage, storage)],
            [new MoviePilotDirectory(
                storage, path, MoviePilotTransfer.Text(data, "transfer_type"), null)],
            []);
    }

    /// <summary>
    /// 预览（<c>preview:true</c>）：只让 MoviePilot 算一遍会怎么整理，不动任何文件。
    /// </summary>
    public async Task<MoviePilotTransferPreview> TransferPreviewAsync(
        MoviePilotTransferRequest request, IReadOnlyList<JsonElement> files, CancellationToken cancellationToken)
    {
        var result = await TransferRunAsync(request, files, background: false, preview: true, cancellationToken)
            .ConfigureAwait(false);
        return new MoviePilotTransferPreview(request, files, result);
    }

    /// <summary>
    /// 真正整理。<paramref name="background"/> 为真时加入 MoviePilot 的整理队列（不等结果就回「已接收」）；
    /// 为假时同步等它做完。
    /// <para>
    /// 有副作用（移动/复制文件、可能覆盖），调用方须先向用户确认 —— 话由
    /// <see cref="MoviePilotTransfer.Confirmation"/> 拼好。
    /// </para>
    /// </summary>
    public async Task<MoviePilotTransferResult> TransferSubmitAsync(
        MoviePilotTransferRequest request, IReadOnlyList<JsonElement> files, bool background,
        CancellationToken cancellationToken)
    {
        var result = await TransferRunAsync(request, files, background, preview: false, cancellationToken)
            .ConfigureAwait(false);

        Log.Info(Category,
            $"MoviePilot 手动整理（{(background ? "队列" : "同步")}）：{request.SourceDisplay} → {request.TargetPath}；{result.Summary}");
        return result;
    }

    private async Task<MoviePilotTransferResult> TransferRunAsync(
        MoviePilotTransferRequest request,
        IReadOnlyList<JsonElement> files,
        bool background,
        bool preview,
        CancellationToken cancellationToken)
    {
        var body = request.Body(files, preview);
        var reply = await CallAsync((apiBase, token) => client.PostReplyAsync(
            apiBase, token, $"transfer/manual?background={(background ? "true" : "false")}", body, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        return MoviePilotTransfer.ParseResult(reply.Data, reply.Success, reply.Message, preview);
    }

    /// <summary>media/source 里没有 media_types 时的容错：缺这个键就当不限类型。</summary>
    private static JsonElement TryMember(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) ? value : default;
}
