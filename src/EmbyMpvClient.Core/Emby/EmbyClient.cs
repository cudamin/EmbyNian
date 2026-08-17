using EmbyMpvClient.Diagnostics;

namespace EmbyMpvClient.Emby;

/// <summary>
/// Authenticated, user-scoped access to one Emby server. Immutable: a new sign-in
/// produces a new client rather than mutating shared state, which is what made v1's
/// settings object double as session state.
/// </summary>
public sealed class EmbyClient(EmbyHttp http, EmbyConnection connection)
{
    private const string Category = "emby";

    public EmbyConnection Connection { get; } = connection;

    public Uri ApiBase => Connection.ApiBase;

    private EmbyHttp.RequestContext Context => new(Connection.Device, Connection.AccessToken);

    // ---- browsing -------------------------------------------------------------

    public async Task<List<EmbyItem>> GetViewsAsync(CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/Views");
        var result = await http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken).ConfigureAwait(false);
        Log.Debug(Category, $"读取到 {result.Items.Count} 个媒体库");
        return result.Items;
    }

    public Task<ItemsResult> GetItemsAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/Items", query.ToParameters());
        return http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken);
    }

    public Task<EmbyItem> GetItemAsync(string itemId, CancellationToken cancellationToken, string fields = EmbyFields.Detail)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/Items/{itemId}", ("Fields", fields));
        return http.GetJsonAsync<EmbyItem>(url, Context, cancellationToken);
    }

    public async Task<List<EmbyItem>> GetResumeAsync(int limit, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/Items/Resume",
            ("Limit", limit.ToString()),
            ("Fields", EmbyFields.Browse),
            ("MediaTypes", "Video"));
        var result = await http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken).ConfigureAwait(false);
        return result.Items;
    }

    /// <summary>Note: this endpoint returns a bare array rather than an ItemsResult.</summary>
    public Task<List<EmbyItem>> GetLatestAsync(string? parentId, int limit, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/Items/Latest",
            ("Limit", limit.ToString()),
            ("Fields", EmbyFields.Browse),
            ("ParentId", parentId),
            ("IsPlayed", "false"),
            ("GroupItems", "true"));
        return http.GetJsonAsync<List<EmbyItem>>(url, Context, cancellationToken);
    }

    public async Task<List<EmbyItem>> GetNextUpAsync(int limit, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, "Shows/NextUp",
            ("UserId", Connection.UserId),
            ("Limit", limit.ToString()),
            ("Fields", EmbyFields.Browse));
        var result = await http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken).ConfigureAwait(false);
        return result.Items;
    }

    public async Task<List<EmbyItem>> GetSeasonsAsync(string seriesId, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Shows/{seriesId}/Seasons",
            ("UserId", Connection.UserId),
            ("Fields", EmbyFields.Browse));
        var result = await http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken).ConfigureAwait(false);
        return result.Items;
    }

    public async Task<List<EmbyItem>> GetEpisodesAsync(string seriesId, string? seasonId, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Shows/{seriesId}/Episodes",
            ("UserId", Connection.UserId),
            ("SeasonId", seasonId),
            ("Fields", EmbyFields.Browse));
        var result = await http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken).ConfigureAwait(false);
        return result.Items;
    }

    // ---- user state -----------------------------------------------------------

    public Task MarkPlayedAsync(string itemId, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/PlayedItems/{itemId}");
        return http.PostAsync(url, null, Context, cancellationToken);
    }

    public Task MarkUnplayedAsync(string itemId, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/PlayedItems/{itemId}");
        return http.DeleteAsync(url, Context, cancellationToken);
    }

    public Task SetFavoriteAsync(string itemId, bool isFavorite, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/FavoriteItems/{itemId}");
        return isFavorite
            ? http.PostAsync(url, null, Context, cancellationToken)
            : http.DeleteAsync(url, Context, cancellationToken);
    }

    // ---- playback reporting ---------------------------------------------------

    public Task ReportPlaybackStartAsync(PlaybackReport report, CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Sessions/Playing"), report, Context, cancellationToken);

    public Task ReportPlaybackProgressAsync(PlaybackReport report, CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Sessions/Playing/Progress"), report, Context, cancellationToken);

    public Task ReportPlaybackStoppedAsync(PlaybackReport report, CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Sessions/Playing/Stopped"), report, Context, cancellationToken);

    // ---- media ---------------------------------------------------------------

    public Task<byte[]> GetImageBytesAsync(string itemId, string imageType, string? tag, int maxWidth, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Image(ApiBase, itemId, imageType, tag, maxWidth);
        return http.GetBytesAsync(url, Context, cancellationToken);
    }

    /// <summary>The still for one chapter of an item; <paramref name="index"/> is its position in <c>Chapters</c>.</summary>
    public Task<byte[]> GetChapterImageBytesAsync(string itemId, int index, string? tag, int maxWidth, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.ChapterImage(ApiBase, itemId, index, tag, maxWidth);
        return http.GetBytesAsync(url, Context, cancellationToken);
    }

    public Uri StreamUrl(EmbyItem item, MediaSource? source) =>
        EmbyUrl.Stream(ApiBase, item.Id, source?.Id, source?.Container);

    public Uri SubtitleUrl(EmbyItem item, MediaSource source, MediaStream subtitle) =>
        EmbyUrl.Subtitle(ApiBase, item.Id, source.Id, subtitle.Index, subtitle.Codec);
}
