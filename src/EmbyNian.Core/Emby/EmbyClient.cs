using System.Text.Json.Nodes;
using EmbyNian.Diagnostics;

namespace EmbyNian.Emby;

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
        // The only browse call that used to ask for no fields, and it shows on the library cards: no
        // ChildCount for the 「N 项」 line under one, and no PrimaryImageAspectRatio, which is the only
        // thing that says whether a library's cover art is poster-shaped or a wide banner before the
        // grid has downloaded it.
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/Views", ("Fields", EmbyFields.Browse));
        var result = await http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken).ConfigureAwait(false);
        Log.Debug(Category, $"读取到 {result.Items.Count} 个媒体库");
        return result.Items;
    }

    public Task<ItemsResult> GetItemsAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/Items", query.ToParameters());
        return http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken);
    }

    /// <summary>
    /// 需求 5：the choices for one of <see cref="EmbyFilterBy.Lists"/> — which genres, tags or years the
    /// items in <paramref name="scope"/> actually have.
    /// <para>
    /// <c>/Genres</c>, <c>/Tags</c> and <c>/Years</c> each take the whole of <c>/Items</c>'s query and
    /// answer with the distinct values inside it, which is why one method serves all three: the key is
    /// the endpoint name. Emby 4.9 has no <c>/Items/Filters</c> — the endpoint other clients use for this
    /// was removed, and asking for it gets a 404 rather than an empty list.
    /// </para>
    /// <para>
    /// The scope deliberately keeps only where to look, not what is currently filtered. A list narrowed
    /// by the selection made from it is a list that empties itself as it is used: tick one genre and
    /// every other genre disappears, leaving no way back except 清除.
    /// </para>
    /// </summary>
    public async Task<List<string>> GetFilterValuesAsync(string key, ItemQuery scope, CancellationToken cancellationToken)
    {
        var query = new ItemQuery
        {
            ParentId = scope.ParentId,
            SearchTerm = scope.SearchTerm,
            IncludeItemTypes = scope.IncludeItemTypes,
            Recursive = scope.Recursive,
            SortBy = EmbySortBy.Name,
            // Years newest first, names A to Z. A year list starting at 1927 is one nobody scrolls.
            Descending = key == EmbyFilterBy.Keys.Years,
            // No fields: this answer is names, and a genre has no poster or overview worth the bytes.
            Fields = "",
            // Enough that no real library reaches it, and small enough to stay one round trip.
            Limit = 1000
        };

        var parameters = new List<(string, string?)> { ("UserId", Connection.UserId) };
        parameters.AddRange(query.ToParameters());

        var result = await http
            .GetJsonAsync<ItemsResult>(EmbyUrl.Combine(ApiBase, key, [.. parameters]), Context, cancellationToken)
            .ConfigureAwait(false);

        Log.Debug(Category, $"{key} 可选值 {result.Items.Count} 个");

        return [.. result.Items
            .Select(item => item.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)];
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

    /// <summary>
    /// 接下来播放. <paramref name="seriesId"/> narrows it to one show, which is what a series page's
    /// 「尚未观看」 row is: the same endpoint the home page uses, scoped, so the two can never disagree
    /// about which episode is next.
    /// </summary>
    public async Task<List<EmbyItem>> GetNextUpAsync(int limit, CancellationToken cancellationToken, string? seriesId = null)
    {
        var url = EmbyUrl.Combine(ApiBase, "Shows/NextUp",
            ("UserId", Connection.UserId),
            ("SeriesId", seriesId),
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
            ("SortBy", EmbySortBy.Number),
            ("SortOrder", "Ascending"),
            ("Fields", EmbyFields.Browse));
        var result = await http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken).ConfigureAwait(false);
        return result.Items;
    }

    /// <summary>
    /// 更多类似 — the recommendations Emby's own detail page shows under the cast row. Asked with the
    /// same browse fields, so a recommendation card is a card like any other and needs no template of
    /// its own.
    /// </summary>
    public async Task<List<EmbyItem>> GetSimilarAsync(string itemId, int limit, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Items/{itemId}/Similar",
            ("UserId", Connection.UserId),
            ("Limit", limit.ToString()),
            ("Fields", EmbyFields.Browse));
        var result = await http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken).ConfigureAwait(false);
        return result.Items;
    }

    // ---- user state -----------------------------------------------------------

    /// <summary>
    /// 标记为已观看. The answer is the item's user data as the server now sees it, which is more than an
    /// echo: marking a film played also clears its resume position, and marking a series played empties
    /// the remaining-episode count on every card that shows one. Null when the server sent no body.
    /// </summary>
    public Task<EmbyUserData?> MarkPlayedAsync(string itemId, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/PlayedItems/{itemId}");
        return http.PostForJsonAsync<EmbyUserData>(url, null, Context, cancellationToken);
    }

    /// <inheritdoc cref="MarkPlayedAsync"/>
    public Task<EmbyUserData?> MarkUnplayedAsync(string itemId, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/PlayedItems/{itemId}");
        return http.DeleteForJsonAsync<EmbyUserData>(url, Context, cancellationToken);
    }

    /// <inheritdoc cref="MarkPlayedAsync"/>
    public Task<EmbyUserData?> SetFavoriteAsync(string itemId, bool isFavorite, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/FavoriteItems/{itemId}");
        return isFavorite
            ? http.PostForJsonAsync<EmbyUserData>(url, null, Context, cancellationToken)
            : http.DeleteForJsonAsync<EmbyUserData>(url, Context, cancellationToken);
    }

    // ---- metadata editing -----------------------------------------------------

    /// <summary>
    /// The item exactly as the server sent it, unparsed. 编辑元数据 has to give the whole object back on
    /// save — Emby's update endpoint takes a complete item and treats every field it can edit as
    /// authoritative, so anything dropped on the way through is a field the user just erased. Keeping the
    /// JSON means only the fields the form actually touches can change, including the dozens
    /// <see cref="EmbyItem"/> has no property for.
    /// </summary>
    public Task<JsonObject> GetItemJsonAsync(string itemId, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/Items/{itemId}");
        return http.GetJsonAsync<JsonObject>(url, Context, cancellationToken);
    }

    /// <summary>
    /// Writes an edited item back. Administrators only — a normal account gets 403, which is a thing to
    /// report rather than to hide, since nothing about the item is wrong.
    /// </summary>
    public Task UpdateItemAsync(string itemId, JsonObject item, CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, $"Items/{itemId}"), item, Context, cancellationToken);

    // ---- playback reporting ---------------------------------------------------

    public Task ReportPlaybackStartAsync(PlaybackReport report, CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Sessions/Playing"), report, Context, cancellationToken);

    public Task ReportPlaybackProgressAsync(PlaybackReport report, CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Sessions/Playing/Progress"), report, Context, cancellationToken);

    public Task ReportPlaybackStoppedAsync(PlaybackReport report, CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Sessions/Playing/Stopped"), report, Context, cancellationToken);

    // ---- server dashboard ------------------------------------------------------

    /// <summary>
    /// What the server says about itself. <c>/System/Info</c> is administrators-only, so a normal
    /// account falls back to the public subset — same field names, fewer of them — with
    /// <see cref="EmbySystemInfo.IsRestricted"/> set so the page can say why the ports are missing.
    /// A 401 is deliberately not caught here: that is an expired token, and the session has to see it.
    /// </summary>
    public async Task<EmbySystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await http
                .GetJsonAsync<EmbySystemInfo>(EmbyUrl.Combine(ApiBase, "System/Info"), Context, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EmbyApiException error) when (error.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            Log.Debug(Category, "当前账户不是管理员，改用公开的服务器信息");
            var info = await http
                .GetJsonAsync<EmbySystemInfo>(EmbyUrl.Combine(ApiBase, "System/Info/Public"), Context, cancellationToken)
                .ConfigureAwait(false);
            info.IsRestricted = true;
            return info;
        }
    }

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
