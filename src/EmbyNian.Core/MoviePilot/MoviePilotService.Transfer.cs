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
        var identity = CaptureIdentity();
        var storages = MoviePilotTransfer.Array(await CallAsync((apiBase, token) =>
            client.GetAsync(apiBase, token, "storage/options", cancellationToken), cancellationToken, identity)
            .ConfigureAwait(false))
            .Where(item => MoviePilotTransfer.Text(item, "name").Length > 0)
            .Select(item => new MoviePilotStorage(
                MoviePilotTransfer.Text(item, "name"), MoviePilotTransfer.Text(item, "type")))
            .ToList();

        var directories = MoviePilotTransfer.Array(await CallAsync((apiBase, token) =>
            client.GetAsync(apiBase, token, "storage/directories?directory_type=library", cancellationToken),
            cancellationToken, identity).ConfigureAwait(false))
            .Where(item => MoviePilotTransfer.Text(item, "library_path").Length > 0)
            .Select(item => new MoviePilotDirectory(
                MoviePilotTransfer.Text(item, "library_storage") is { Length: > 0 } storage ? storage : "local",
                MoviePilotTransfer.Text(item, "library_path"),
                MoviePilotTransfer.Text(item, "transfer_type"),
                MoviePilotTransfer.Text(item, "overwrite_mode"),
                MoviePilotTransfer.Bool(item, "scraping"),
                MoviePilotTransfer.Bool(item, "library_type_folder"),
                MoviePilotTransfer.Bool(item, "library_category_folder")))
            .ToList();

        var sources = ParseMediaSources(await CallAsync((apiBase, token) =>
            client.GetAsync(apiBase, token, "media/source", cancellationToken), cancellationToken, identity).ConfigureAwait(false))
            .Where(source => source.IsVideo).ToList();
        CheckIdentity(identity, cancellationToken);

        if (storages.Count == 0) storages.Add(new MoviePilotStorage("本地", "local"));

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
        if (file.ValueKind is not JsonValueKind.Object || !reply.Success)
            throw new MoviePilotException(
                $"MoviePilot 看不到这个文件：{sourcePath}。它是 MoviePilot 那台机器上的路径吗？（不会改为整理父目录）");

        MoviePilotTransfer.RequireFile(file, sourcePath);
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

        if (!reply.Success) throw new MoviePilotException(reply.Message);
        return ParseTransferTarget(reply.Data);
    }

    /// <summary>
    /// 预览（<c>preview:true</c>）：只让 MoviePilot 算一遍会怎么整理，不动任何文件。
    /// </summary>
    public async Task<MoviePilotTransferPreview> TransferPreviewAsync(
        MoviePilotTransferRequest request, IReadOnlyList<JsonElement> files, CancellationToken cancellationToken)
    {
        var identity = CaptureIdentity();
        ValidateHistoryConnection(request.Histories, identity);
        var snapshot = request with
        {
            Files = Array.AsReadOnly(request.Files.ToArray()),
            Histories = Array.AsReadOnly(request.Histories.ToArray())
        };
        var resolved = Array.AsReadOnly(files.Select(file => file.Clone()).ToArray());
        var result = await TransferRunAsync(snapshot, resolved, background: false, preview: true, cancellationToken, identity)
            .ConfigureAwait(false);
        CheckIdentity(identity, cancellationToken);
        var preview = new MoviePilotTransferPreview(snapshot, resolved, result);
        preview.Seal(TransferConnectionStamp(identity));
        return preview;
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
        MoviePilotTransferPreview preview, bool background, CancellationToken cancellationToken)
    {
        var identity = CaptureIdentity();
        CheckIdentity(identity, cancellationToken);
        preview.ClaimSubmission(TransferConnectionStamp(identity));
        ValidateHistoryConnection(preview.Request.Histories, identity);
        foreach (var group in preview.Request.Histories.GroupBy(history => history.Title))
        {
            CheckIdentity(identity, cancellationToken);
            var current = await FindTransferHistoriesAsync(group.Key, cancellationToken).ConfigureAwait(false);
            CheckIdentity(identity, cancellationToken);
            foreach (var history in group)
            {
                var fresh = current.FirstOrDefault(item => item.Id == history.Id);
                if (fresh is null || history.Snapshot.ValueKind != JsonValueKind.Object ||
                    !JsonElement.DeepEquals(fresh.Snapshot, history.Snapshot))
                    throw new MoviePilotTransferBlockedException("原整理记录已经改变，请重新查找记录和预览");
                MoviePilotTransfer.RequireFile(fresh.TransferFile, history.TransferPath);
            }
        }
        foreach (var path in preview.Request.Histories.Select(history => history.TargetPath)
                     .Where(path => path.Length > 0).Distinct(StringComparer.Ordinal))
        {
            CheckIdentity(identity, cancellationToken);
            var current = await FindTransferHistoriesAsync(path, cancellationToken).ConfigureAwait(false);
            CheckIdentity(identity, cancellationToken);
            var dependents = MoviePilotTransferHistoryMatch.Dependents(preview.Request.Histories, current);
            if (dependents.Count > 0)
                throw new MoviePilotTransferBlockedException("旧目标还被软链接记录引用（" +
                    string.Join("、", dependents.Select(history => $"#{history.Id}")) +
                    "）。重新整理可能让它们失效，请先在 MoviePilot 处理引用关系；本次没有提交。");
        }
        CheckIdentity(identity, cancellationToken);
        var body = preview.Request.Body(preview.Files, preview: false);
        var reply = await WriteAsync(identity, TransferKeys(preview), (apiBase, token) => client.PostReplyAsync(
            apiBase, token, $"transfer/manual?background={(background ? "true" : "false")}", body, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var result = MoviePilotTransfer.ParseResult(reply.Data, reply.Success, reply.Message, preview: false);
        result = MoviePilotTransfer.CompleteReceipt(result, preview);
        Log.Info(Category, $"MoviePilot 手动整理（{(background ? "队列" : "同步")}）：{result.Summary}");
        return result;
    }

    private static string TransferConnectionStamp(SessionIdentity identity) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{identity.ApiBase}\n{identity.Username}\n{identity.ProtectedPassword}")));

    private async Task<MoviePilotTransferResult> TransferRunAsync(
        MoviePilotTransferRequest request,
        IReadOnlyList<JsonElement> files,
        bool background,
        bool preview,
        CancellationToken cancellationToken,
        SessionIdentity identity)
    {
        CheckIdentity(identity, cancellationToken);
        ValidateHistoryConnection(request.Histories, identity);
        var body = request.Body(files, preview);
        var reply = await CallAsync((apiBase, token) => client.PostReplyAsync(
            apiBase, token, $"transfer/manual?background={(background ? "true" : "false")}", body, cancellationToken),
            cancellationToken, identity).ConfigureAwait(false);

        return MoviePilotTransfer.ParseResult(reply.Data, reply.Success, reply.Message, preview);
    }

    /// <summary>media/source 里没有 media_types 时的容错：缺这个键就当不限类型。</summary>
    private static JsonElement TryMember(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) ? value : default;

    /// <summary>
    /// 把 <c>media/source</c> 的回话拆成来源条目。名字没给的用标识兜底（屏上不留空名字）；
    /// media_types 缺失或空按「不限类型」处理，交给 <see cref="MoviePilotMediaSource.IsVideo"/> 判断。
    /// </summary>
    private static IReadOnlyList<MoviePilotMediaSource> ParseMediaSources(JsonElement data) =>
        MoviePilotTransfer.Array(data)
            .Where(item => MoviePilotTransfer.Text(item, "media_source").Length > 0)
            .Select(item => new MoviePilotMediaSource(
                MoviePilotTransfer.Text(item, "name") is { Length: > 0 } name
                    ? name : MoviePilotTransfer.Text(item, "media_source"),
                MoviePilotTransfer.Text(item, "media_source"),
                MoviePilotTransfer.Array(TryMember(item, "media_types"))
                    .Select(one => one.ValueKind == JsonValueKind.String ? one.GetString() ?? "" : "")
                    .Where(kind => kind.Length > 0)
                    .ToList()))
            .ToList();
}
