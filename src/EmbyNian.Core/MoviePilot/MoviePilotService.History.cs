using System.Globalization;
using System.Text.Json;

namespace EmbyNian.MoviePilot;

public sealed partial class MoviePilotService
{
    public async Task<MoviePilotTransferHistoryPage> TransferHistoryAsync(
        string title, int page, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new MoviePilotException("请输入片名或文件名查找原整理记录");
        var identity = CaptureIdentity();
        var data = await CallAsync((apiBase, token) => client.GetAsync(apiBase, token,
            $"history/transfer?title={Uri.EscapeDataString(title.Trim())}&page={Math.Max(1, page)}&count=100",
            cancellationToken), cancellationToken).ConfigureAwait(false);
        var items = MoviePilotTransferHistory.Member(data, "list");
        if (items.ValueKind != JsonValueKind.Array)
            throw new MoviePilotException("MoviePilot 没有返回可识别的整理记录列表");
        CheckIdentity(identity, cancellationToken);
        var stamp = TransferConnectionStamp(identity);
        var records = items.EnumerateArray().Select(MoviePilotTransferHistory.Parse)
            .Where(item => item is not null).Select(item => item! with { ConnectionStamp = stamp }).ToList();
        var total = int.TryParse(MoviePilotTransferHistory.Scalar(data, "total"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var count) ? count : records.Count;
        return new MoviePilotTransferHistoryPage(records, total);
    }

    public async Task<IReadOnlyList<MoviePilotTransferHistory>> FindTransferHistoriesAsync(
        string title, CancellationToken cancellationToken)
    {
        var identity = CaptureIdentity();
        var records = new List<MoviePilotTransferHistory>();
        var ids = new HashSet<long>();
        for (var page = 1; page <= 50; page++)
        {
            CheckIdentity(identity, cancellationToken);
            var result = await TransferHistoryAsync(title, page, cancellationToken).ConfigureAwait(false);
            CheckIdentity(identity, cancellationToken);
            var added = 0;
            foreach (var record in result.Items)
                if (ids.Add(record.Id)) { records.Add(record); added++; }
            if (records.Count >= result.Total) return records;
            if (added == 0) throw new MoviePilotException("整理记录分页未返回完整数据，请缩小片名或文件名后重试");
        }
        throw new MoviePilotException("整理记录过多，请使用更完整的片名或文件名缩小范围");
    }

    public async Task<IReadOnlyList<JsonElement>> TransferHistoryFilesAsync(
        MoviePilotTransferRequest request, CancellationToken cancellationToken)
    {
        if (request.Histories.Count != request.Files.Count || request.Histories.Count == 0)
            throw new MoviePilotException("请先找到并核对原整理记录");
        var identity = CaptureIdentity();
        ValidateHistoryConnection(request.Histories, identity);
        var files = new List<JsonElement>();
        foreach (var history in request.Histories)
        {
            CheckIdentity(identity, cancellationToken);
            MoviePilotTransfer.RequireFile(history.TransferFile, history.TransferPath);
            files.Add(await TransferFileAsync(history.TransferPath, cancellationToken).ConfigureAwait(false));
        }
        CheckIdentity(identity, cancellationToken);
        request.Body(files, preview: true);
        return files;
    }

    public async Task<MoviePilotTransferOptions> TransferHistoryTargetAsync(
        IReadOnlyList<MoviePilotTransferHistory> histories, CancellationToken cancellationToken)
    {
        if (histories.Count == 0) throw new MoviePilotException("请先选择原整理记录");
        var identity = CaptureIdentity();
        ValidateHistoryConnection(histories, identity);
        var body = new Dictionary<string, object?>
        {
            [histories.Count == 1 ? "logid" : "logids"] = histories.Count == 1
                ? histories[0].Id : histories.Select(history => history.Id).ToArray()
        };
        var data = await CallAsync((apiBase, token) => client.PostAsync(apiBase, token,
            "transfer/manual/target-path", body, cancellationToken), cancellationToken, identity).ConfigureAwait(false);
        CheckIdentity(identity, cancellationToken);
        return ParseTransferTarget(data);
    }

    private static void ValidateHistoryConnection(IEnumerable<MoviePilotTransferHistory> histories, SessionIdentity identity)
    {
        var stamp = TransferConnectionStamp(identity);
        if (histories.Any(history => history.ConnectionStamp != stamp))
            throw new MoviePilotTransferBlockedException("MoviePilot 连接配置已改变，请重新查找原整理记录");
    }

    private static MoviePilotTransferOptions ParseTransferTarget(JsonElement data)
    {
        var storage = MoviePilotTransfer.Text(data, "target_storage");
        var path = MoviePilotTransfer.Text(data, "target_path");
        if (storage.Length == 0 || path.Length == 0) return new MoviePilotTransferOptions([], [], []);
        return new MoviePilotTransferOptions([new MoviePilotStorage(storage, storage)],
            [new MoviePilotDirectory(storage, path, MoviePilotTransfer.Text(data, "transfer_type"), null,
                MoviePilotTransfer.Bool(data, "scrape"), MoviePilotTransfer.Bool(data, "library_type_folder"),
                MoviePilotTransfer.Bool(data, "library_category_folder"))], []);
    }
}
