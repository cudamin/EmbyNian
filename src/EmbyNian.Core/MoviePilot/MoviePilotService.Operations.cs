using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbyNian.MoviePilot;

public enum MoviePilotOperationState
{
    Idle,
    Working,
    Submitted,
    Uncertain
}

public sealed partial class MoviePilotService
{
    private readonly object _operationGate = new();
    private readonly Dictionary<string, MoviePilotOperationState> _operations = new(StringComparer.Ordinal);

    public MoviePilotOperationState DownloadState(MoviePilotResource resource) => OperationState(DownloadKey(resource));
    public MoviePilotOperationState SubscribeState(MoviePilotMedia media) => OperationState(SubscribeKey(media));

    public string ConnectionStamp => Enabled ? TransferConnectionStamp(CaptureIdentity()) : "";

    public bool IsCurrentConnection(string stamp) => stamp.Length > 0 && Enabled &&
        MoviePilotAddress.TryNormalize(settings.MoviePilot.Url, out var address, out _) && address is not null &&
        stamp == TransferConnectionStamp(CaptureIdentity());

    private SessionIdentity RequireResultConnection(string stamp)
    {
        var identity = CaptureIdentity();
        CheckIdentity(identity, CancellationToken.None);
        if (stamp.Length == 0 || stamp != TransferConnectionStamp(identity))
            throw new MoviePilotOperationBlockedException("MoviePilot 连接已改变或结果已失效，请重新查询并确认");
        return identity;
    }

    private MoviePilotOperationState OperationState(string key)
    {
        lock (_operationGate) return _operations.GetValueOrDefault(key);
    }

    private async Task<T> WriteAsync<T>(SessionIdentity identity, IReadOnlyList<string> keys,
        Func<Uri, string, Task<T>> write, CancellationToken cancellationToken)
    {
        CheckIdentity(identity, cancellationToken);
        lock (_operationGate)
        {
            if (keys.Any(_operations.ContainsKey))
                throw new MoviePilotOperationBlockedException("此操作正在处理、已提交或结果待核对，请先在 MoviePilot 核对，不能重复发送");
            foreach (var key in keys) _operations[key] = MoviePilotOperationState.Working;
        }

        var sent = false;
        try
        {
            var result = await CallAsync(async (address, token) =>
            {
                sent = true;
                try { return await write(address, token).ConfigureAwait(false); }
                catch (MoviePilotTokenExpiredException) { sent = false; throw; }
            }, cancellationToken, identity).ConfigureAwait(false);
            lock (_operationGate)
                foreach (var key in keys) _operations[key] = MoviePilotOperationState.Submitted;
            return result;
        }
        catch (Exception error)
        {
            var rejected = error is MoviePilotException
            {
                Status: System.Net.HttpStatusCode.BadRequest or
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or
                    System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.UnprocessableEntity
            };
            lock (_operationGate)
            {
                foreach (var key in keys)
                    if (!sent || rejected) _operations.Remove(key);
                    else _operations[key] = MoviePilotOperationState.Uncertain;
            }
            if (!sent || rejected) throw;
            throw new MoviePilotOperationUncertainException("请求已发出，但没有取得明确结果；请先在 MoviePilot 核对，勿重复提交");
        }
    }

    private static string SubscribeKey(MoviePilotMedia media) => OperationKey(
        media.ConnectionStamp, "subscribe", media.MediaSource ?? "", media.MediaId ?? "", media.Type ?? "",
        media.Season?.ToString(CultureInfo.InvariantCulture) ?? "");

    private static string DownloadKey(MoviePilotResource resource)
    {
        var page = MoviePilotTransfer.Text(resource.TorrentInfo, "page_url");
        var enclosure = MoviePilotTransfer.Text(resource.TorrentInfo, "enclosure");
        var site = MoviePilotTransferHistory.Scalar(resource.TorrentInfo, "site");
        return OperationKey(resource.ConnectionStamp, "download", site.Length > 0 ? site : resource.SiteName ?? "",
            page.Length > 0 ? page : enclosure.Length > 0 ? enclosure : resource.Title,
            page.Length > 0 || enclosure.Length > 0 ? "" : resource.Size.ToString(CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<string> TransferKeys(MoviePilotTransferPreview preview) =>
        preview.Request.Histories.Select(history => OperationKey(preview.ConnectionStamp, "transfer-history",
                history.Id.ToString(CultureInfo.InvariantCulture)))
            .Concat(preview.Files.Select(file => OperationKey(preview.ConnectionStamp, "transfer-file",
                MoviePilotTransfer.Text(file, "path"))))
            .Distinct(StringComparer.Ordinal).ToArray();

    private static string OperationKey(params string[] parts) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
}
