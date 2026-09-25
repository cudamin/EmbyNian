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

    /// <summary>
    /// 取一个条目。<paramref name="order"/> 是媒体源的排法（设置页「媒体源排序」，2026-09-24）：只有点名
    /// 「新入库在前」才去问各版本的入库时间并重排（多一次旁路请求，见 <see cref="OrderVersionsByAddedAsync"/>）；
    /// 「默认」就是服务器给的次序，一个字节都不多问。详情页与播放器起播都从这一条拿条目，所以那两处界面
    /// 读到的版本表次序天然一致。
    /// </summary>
    public async Task<EmbyItem> GetItemAsync(string itemId, CancellationToken cancellationToken,
        string fields = EmbyFields.Detail, MediaSourceOrder order = MediaSourceOrder.Default)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/Items/{itemId}", ("Fields", fields));
        var item = await http.GetJsonAsync<EmbyItem>(url, Context, cancellationToken).ConfigureAwait(false);
        if (order == MediaSourceOrder.NewestFirst)
            await OrderVersionsByAddedAsync(item, cancellationToken).ConfigureAwait(false);
        return item;
    }

    /// <summary>
    /// 多版本条目的版本表按<b>入库时间</b>重排，最新入库的排最前（2026-09-24 用户令）。只在设置点名
    /// 「新入库在前」时被调（<see cref="GetItemAsync"/> 的 <c>order</c>）。拿不到时间就保持
    /// 服务器给的次序 —— 一次排序的旁路请求不该绊倒详情本身，失败只记一笔。
    /// <para>
    /// 每一版的入库时间服务器不在媒体源上给（MediaSourceInfo 一个日期字段都没有），但每一版在服务器上
    /// 本来就是一个条目（<see cref="MediaSource.ItemId"/>），它的 <c>DateCreated</c> 就是入库时间。实测
    /// （Emby 4.10，探针在 <c>work/versions-probe</c>）：备选版本条目被服务器藏起来，
    /// <c>/Users/{uid}/Items?Ids=…</c> 只回主条目；<b>不带用户前缀的 <c>/Items?Ids=…</c> 全都认</b>，
    /// 一条请求问齐全部版本。单版本条目根本不发这一条。
    /// </para>
    /// </summary>
    private async Task OrderVersionsByAddedAsync(EmbyItem item, CancellationToken cancellationToken)
    {
        var ids = item.MediaSources
            .Select(source => source.ItemId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count < 2) return;

        try
        {
            var url = EmbyUrl.Combine(ApiBase, "Items",
                ("Ids", string.Join(',', ids)),
                ("Fields", "DateCreated"));
            var result = await http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken).ConfigureAwait(false);
            var added = result.Items
                .Where(version => version.DateCreated is not null)
                .ToDictionary(version => version.Id, version => version.DateCreated!.Value);

            ItemDetail.OrderVersionsNewestFirst(item, added);
            Log.Debug(Category, $"《{item.Name}》{item.MediaSources.Count} 版已按入库时间排好（最新在前）");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.Warn(Category, "版本的入库时间没拿到，保持服务器次序", error);
        }
    }

    /// <summary>
    /// 服务器自己那条 Resume 接口 —— <b>「继续观看」的全部口径都在它手里</b>：有播放进度、没标已看、
    /// 没被 <c>HideFromResume</c> 移出。主页那一排和那一排点进去的那一页都从这里拿，两处就不可能再对不上。
    /// <para>
    /// 2026-09-22 用户报「首页的继续观看怎么只有一项了」：查下来那一排本来就只有一项（服务器上「有播放进度」
    /// 的九条里，五条已标已看、三条被移出继续观看），真正错的是点进去那一页 —— 它走通用查询
    /// <c>Filters=IsResumable</c>，装了九条，于是「一排 1 项、进去 9 项」。
    /// </para>
    /// <para>
    /// 这个差别在通用查询里补不出来，所以那一页只能问接口本身：实测 <c>Filters=IsResumable,IsUnplayed</c>
    /// 只收到四条（还差被移出的那三条），而「被移出」这个条件通用查询里没有词可以表达 —— 把
    /// <c>NotHiddenFromResume</c> 塞进 Filters，服务器一个都不认、静默丢掉，照样回四条（未知筛选词不报错）。
    /// </para>
    /// <para>
    /// <c>StartIndex</c> 这条接口是真认的（实测 <c>StartIndex=1&amp;Limit=1</c> 会真的跳过头一条），所以点进去
    /// 那一页照旧能往下翻。<c>SortBy</c> 与 <c>Filters</c> 一概不发：这一页的次序和口径都由服务器定，
    /// 界面上那两颗键在这一页也就收起来了（见 <c>LibraryViewModel.SortFilterVisibility</c>）。
    /// </para>
    /// </summary>
    /// <param name="startIndex">翻页用的起点；<see cref="GetResumeAsync"/> 传 0。</param>
    public async Task<ItemsResult> QueryResumeAsync(int startIndex, int limit, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/Items/Resume",
            ("StartIndex", startIndex.ToString()),
            ("Limit", limit.ToString()),
            ("Fields", EmbyFields.Browse),
            ("MediaTypes", "Video"));
        return await http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>主页那一排只要清单，不要总数 —— 接口本身见 <see cref="QueryResumeAsync"/>。</summary>
    public async Task<List<EmbyItem>> GetResumeAsync(int limit, CancellationToken cancellationToken) =>
        (await QueryResumeAsync(startIndex: 0, limit, cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>Note: this endpoint returns a bare array rather than an ItemsResult.</summary>
    /// <param name="includeItemTypes">
    /// 逗号分隔的类型表（<see cref="HomeCarousel.LatestTypes"/> 的产物），null = 不加参数 —— 轮播「全部
    /// 媒体」那一档（用户的话，2026-09-13「轮播图要使用什么媒体」）。
    /// </param>
    public Task<List<EmbyItem>> GetLatestAsync(
        string? parentId,
        int limit,
        CancellationToken cancellationToken,
        string? includeItemTypes = null)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/Items/Latest",
            ("Limit", limit.ToString()),
            ("Fields", EmbyFields.Browse),
            ("ParentId", parentId),
            ("IsPlayed", "false"),
            ("GroupItems", "true"),
            ("IncludeItemTypes", includeItemTypes));
        return http.GetJsonAsync<List<EmbyItem>>(url, Context, cancellationToken);
    }

    /// <summary>
    /// 随机的一批条目 —— 轮播「随机挑选」来源那一趟（「要使用最近添加还是随机的」，用户的话，2026-09-13）。
    /// <see cref="GetLatestAsync"/> 走的 /Items/Latest 没有随机这一档，走通用的 /Items、SortBy=Random ——
    /// 和浏览页排序菜单里「随机」同一个值（<see cref="EmbySortBy.Random"/>）。
    /// </summary>
    public async Task<List<EmbyItem>> GetRandomAsync(
        int limit,
        IReadOnlyList<string> includeItemTypes,
        CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/Items", new ItemQuery
        {
            Recursive = true,
            IncludeItemTypes = includeItemTypes,
            SortBy = EmbySortBy.Random,
            Limit = limit
        }.ToParameters());

        var result = await http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken).ConfigureAwait(false);
        return result.Items;
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

    public async Task<List<EmbyItem>> GetEpisodesAsync(string seriesId, string? seasonId, CancellationToken cancellationToken, string fields = EmbyFields.Browse)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Shows/{seriesId}/Episodes",
            ("UserId", Connection.UserId),
            ("SeasonId", seasonId),
            ("SortBy", EmbySortBy.Number),
            ("SortOrder", "Ascending"),
            ("Fields", fields));
        var result = await http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken).ConfigureAwait(false);
        return result.Items;
    }

    /// <summary>
    /// 服务器上现有的合集，「添加到合集」那张表列的就是这些。
    /// <para>
    /// 不要字段：这张表只写名字。上限给到能装下任何一台真实服务器，而且仍旧是一趟请求。
    /// </para>
    /// </summary>
    public async Task<List<EmbyItem>> GetCollectionsAsync(CancellationToken cancellationToken)
    {
        var result = await GetItemsAsync(
            new ItemQuery
            {
                Recursive = true,
                IncludeItemTypes = [EmbyItemType.BoxSet],
                SortBy = EmbySortBy.Name,
                Fields = "",
                Limit = 500
            },
            cancellationToken).ConfigureAwait(false);

        Log.Debug(Category, $"读取到 {result.Items.Count} 个合集");
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

    /// <summary>
    /// 从继续观看中移除。**进度留着** —— 服务器只是给这个条目按下一位「不要列进继续观看」，下次打开它照旧从
    /// 上次的地方接着播。想连进度一起清掉的是 <see cref="MarkUnplayedAsync"/>，那是另一条菜单。
    /// </summary>
    /// <inheritdoc cref="MarkPlayedAsync"/>
    public Task<EmbyUserData?> HideFromResumeAsync(string itemId, bool hide, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/Items/{itemId}/HideFromResume",
            ("Hide", hide ? "true" : "false"));
        return http.PostForJsonAsync<EmbyUserData>(url, null, Context, cancellationToken);
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

    // ---- 合集、刮削、扫库、删除 ------------------------------------------------

    /// <summary>新建一个合集，并把这几个条目放进去。答案里带着新合集的 id。</summary>
    public Task<CollectionCreationResult> CreateCollectionAsync(
        string name,
        IReadOnlyList<string> itemIds,
        CancellationToken cancellationToken) =>
        http.PostJsonAsync<CollectionCreationResult>(
            EmbyUrl.Combine(ApiBase, "Collections",
                ("Name", name),
                ("Ids", string.Join(',', itemIds)),
                // 不锁：锁住的合集不再接受刮削，而这一步只是「把几样东西归到一处」。
                ("IsLocked", "false")),
            null,
            Context,
            cancellationToken);

    /// <summary>把这几个条目加进一个已有的合集。已经在里头的会被服务器忽略，不是错。</summary>
    public Task AddToCollectionAsync(
        string collectionId,
        IReadOnlyList<string> itemIds,
        CancellationToken cancellationToken) =>
        http.PostAsync(
            EmbyUrl.Combine(ApiBase, $"Collections/{collectionId}/Items", ("Ids", string.Join(',', itemIds))),
            null,
            Context,
            cancellationToken);

    /// <summary>
    /// 刷新元数据，或者刮削一遍。同一个接口两套参数：
    /// <list type="bullet">
    /// <item><b>刷新</b>（<paramref name="replace"/> 为 false）：缺什么补什么，现有的一个字不动。</item>
    /// <item><b>刮削</b>：按名字重新问一遍刮削源，抓回来的盖掉现在的。</item>
    /// </list>
    /// <para>
    /// <b>图片两档都不覆盖</b>（<c>ReplaceAllImages=false</c>）：用户刚在「修改媒体封面图」里挑的那张封面，
    /// 不能被紧接着的一次刮削扔掉。缺图的条目照旧会补上 —— 那是「补缺」而不是「覆盖」。
    /// </para>
    /// <para>
    /// <paramref name="mode"/> 只有验证用得上（<c>ValidationOnly</c>）：那一档什么都不改，用来确认这条路真的
    /// 通着，而不用拿用户的真实库去试一次覆盖。
    /// </para>
    /// </summary>
    public Task RefreshItemAsync(
        string itemId,
        bool replace,
        CancellationToken cancellationToken,
        string? mode = null)
    {
        var refresh = mode ?? (replace ? "FullRefresh" : "Default");

        return http.PostAsync(
            EmbyUrl.Combine(ApiBase, $"Items/{itemId}/Refresh",
                // 递归：一部剧刷新的时候，季和单集跟着一起来 —— 不然「刷新了却还是缺集名」。
                ("Recursive", "true"),
                ("MetadataRefreshMode", refresh),
                ("ImageRefreshMode", refresh),
                ("ReplaceAllMetadata", replace ? "true" : "false"),
                ("ReplaceAllImages", "false")),
            null,
            Context,
            cancellationToken);
    }

    /// <summary>
    /// 重新扫描媒体库 —— 整台服务器扫一遍，也就是服务器控制台上那颗「扫描媒体库」。管理员才能用。
    /// <para>
    /// 不按单个库扫：一张卡片上问不出它属于哪个库（单集的 <c>ParentId</c> 是季，不是库），而「在继续观看上扫
    /// 整台、在媒体库里只扫这一个」会让同一条菜单在两处做两件事。
    /// </para>
    /// </summary>
    public Task ScanLibraryAsync(CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Library/Refresh"), null, Context, cancellationToken);

    /// <summary>
    /// 删除一个条目 —— <b>连磁盘上的文件一起删</b>，不可撤销。调用方必须先问过用户（见
    /// <c>ItemCommands</c> 那一句确认）。账号没有删除权限时服务器答 403，那是要报给用户的话。
    /// </summary>
    public Task DeleteItemAsync(string itemId, CancellationToken cancellationToken) =>
        http.DeleteAsync(EmbyUrl.Combine(ApiBase, $"Items/{itemId}"), Context, cancellationToken);

    // ---- playback reporting ---------------------------------------------------

    public Task ReportPlaybackStartAsync(PlaybackReport report, CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Sessions/Playing"), report, Context, cancellationToken);

    public Task ReportPlaybackProgressAsync(PlaybackReport report, CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Sessions/Playing/Progress"), report, Context, cancellationToken);

    public Task ReportPlaybackStoppedAsync(PlaybackReport report, CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Sessions/Playing/Stopped"), report, Context, cancellationToken);

    // ---- notifications ---------------------------------------------------------
    //
    // 「把本项目的通知改为 emby 的，让 emby 负责转发」（用户令 2026-09-25）：通知条目的增删改查走服务器 4.8+
    // 那套「用户通知」接口 —— 就是官方网页端 /settings/notifications.html 用的四条路。四条都带 UserId 查询
    // 参数：官方网页端就是这么问的，而且实测不带它时 Defaults 一类端点直接空引用 500。

    /// <summary>装在服务器上的通知服务（通知渠道）。这台服务器上装了官方 Webhooks 插件时，答案就是它。</summary>
    public Task<List<NotificationServiceInfo>> GetNotificationServicesAsync(CancellationToken cancellationToken) =>
        http.GetJsonAsync<List<NotificationServiceInfo>>(
            EmbyUrl.Combine(ApiBase, "Notifications/Services", ("UserId", Connection.UserId)), Context, cancellationToken);

    /// <summary>可订阅的事件清单，按类分节。名字服务器已按用户的语言本地化，界面上直接用，不再自带一份词表。</summary>
    public Task<List<NotificationCategoryInfo>> GetNotificationTypesAsync(CancellationToken cancellationToken) =>
        http.GetJsonAsync<List<NotificationCategoryInfo>>(
            EmbyUrl.Combine(ApiBase, "Notifications/Types", ("UserId", Connection.UserId)), Context, cancellationToken);

    /// <summary>当前用户已配置的通知条目（「通知」页的列表本体）。</summary>
    public Task<List<UserNotificationInfo>> GetNotificationsAsync(CancellationToken cancellationToken) =>
        http.GetJsonAsync<List<UserNotificationInfo>>(
            EmbyUrl.Combine(ApiBase, "Notifications/Services/Configured", ("UserId", Connection.UserId)),
            Context, cancellationToken);

    /// <summary>一条新通知的铺底（官方网页端「添加」的起点）：服务键、启用状态和该服务自己的默认选项，
    /// 事件表是空的 —— 事件由编辑器选。服务键取自 <see cref="NotificationServiceInfo.Id"/>。</summary>
    public Task<UserNotificationInfo> GetNotificationDefaultsAsync(string notifierKey, CancellationToken cancellationToken) =>
        http.GetJsonAsync<UserNotificationInfo>(
            EmbyUrl.Combine(ApiBase, "Notifications/Services/Defaults",
                ("UserId", Connection.UserId), ("NotifierKey", notifierKey)), Context, cancellationToken);

    /// <summary>保存一条通知（新建与编辑同一条路，整份 POST —— 官方网页端就是这个节拍）。</summary>
    public Task SaveNotificationAsync(UserNotificationInfo entry, CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Notifications/Services/Configured"), entry, Context, cancellationToken);

    /// <summary>删一条通知。官方网页端删的是「这条 + 这个用户」，两个参数都在查询串上。</summary>
    public Task DeleteNotificationAsync(string entryId, CancellationToken cancellationToken) =>
        http.DeleteAsync(EmbyUrl.Combine(ApiBase, "Notifications/Services/Configured",
            ("Id", entryId), ("UserId", Connection.UserId)), Context, cancellationToken);

    /// <summary>让服务器按这条通知的配置真发一次。目的地不通、令牌不对时服务器把原因写进错误回应里，这里不吞。</summary>
    public Task TestNotificationAsync(UserNotificationInfo entry, CancellationToken cancellationToken) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Notifications/Services/Test"), entry, Context, cancellationToken);

    /// <summary>「限定用户」选择框的数据源（管理员才用得上，但读这四个名字没有权限门槛）。</summary>
    public Task<List<NotificationUser>> GetNotificationUsersAsync(CancellationToken cancellationToken) =>
        http.GetJsonAsync<List<NotificationUser>>(
            EmbyUrl.Combine(ApiBase, "Users",
                ("SortBy", "SortName"), ("SortOrder", "Ascending"), ("EnableImages", "false")),
            Context, cancellationToken);

    /// <summary>「限定媒体库」选择框的数据源。</summary>
    public Task<List<NotificationLibrary>> GetNotificationLibrariesAsync(CancellationToken cancellationToken) =>
        http.GetJsonAsync<List<NotificationLibrary>>(
            EmbyUrl.Combine(ApiBase, "Library/VirtualFolders",
                ("SortBy", "SortName"), ("SortOrder", "Ascending"), ("EnableImages", "false")),
            Context, cancellationToken);

    /// <summary>「限定设备」选择框的数据源。</summary>
    public async Task<List<NotificationDevice>> GetNotificationDevicesAsync(CancellationToken cancellationToken)
    {
        var list = await http.GetJsonAsync<NotificationDeviceList>(
            EmbyUrl.Combine(ApiBase, "Devices"), Context, cancellationToken).ConfigureAwait(false);
        return list.Items;
    }

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

    // ---- 封面图与字幕 ---------------------------------------------------------

    /// <summary>
    /// 「修改媒体封面图」的候选：各家刮削源上这个条目有哪些这一种图。
    /// <para>
    /// <c>IncludeAllLanguages</c> 开着 —— 中文封面在服务器的默认语言之外，关着的话这台服务器上常常一张都
    /// 列不出来。上限 60 张：多到足够挑，又还是一趟请求。
    /// </para>
    /// </summary>
    public Task<RemoteImageResult> GetRemoteImagesAsync(
        string itemId,
        string imageType,
        CancellationToken cancellationToken) =>
        http.GetJsonAsync<RemoteImageResult>(
            EmbyUrl.Combine(ApiBase, $"Items/{itemId}/RemoteImages",
                ("Type", imageType),
                ("IncludeAllLanguages", "true"),
                ("Limit", "60")),
            Context,
            cancellationToken);

    /// <summary>挑中的那一张交给服务器去取并挂到条目上。管理员才能用。</summary>
    public Task ApplyRemoteImageAsync(
        string itemId,
        string imageType,
        string imageUrl,
        string? providerName,
        CancellationToken cancellationToken) =>
        http.PostAsync(
            EmbyUrl.Combine(ApiBase, $"Items/{itemId}/RemoteImages/Download",
                ("Type", imageType),
                ("ProviderName", providerName),
                ("ImageUrl", imageUrl)),
            null,
            Context,
            cancellationToken);

    /// <summary>
    /// 候选图的缩略图，<b>由服务器代取</b>（<c>/Images/Remote</c>）。不让客户端直接去连刮削源：那些地址在
    /// 国内多半连不上，而服务器既然能列出这一批，就一定连得上。
    /// </summary>
    public Task<byte[]> GetRemoteImageBytesAsync(string imageUrl, CancellationToken cancellationToken) =>
        http.GetBytesAsync(
            EmbyUrl.Combine(ApiBase, "Images/Remote", ("ImageUrl", imageUrl)),
            Context,
            cancellationToken);

    /// <summary>
    /// 把服务器上这个条目的一种图删掉。<b>不可撤销</b> —— 服务器不会替你留着备份，调用方必须先问过用户。
    /// <para>
    /// <paramref name="index"/> 只对背景图那种「一个条目一列」的图种有意义（Emby 的地址
    /// <c>/Items/{id}/Images/{type}/{index}</c> 是它本来的形状）；其余几种一个条目各只有一张，index 传 null，
    /// 地址就是不带序号的短形式。传错了不会出错 —— 服务器按序号取，越界答 404。
    /// </para>
    /// <para>
    /// 删掉的可能正是界面上某一处正在用的图（剧名上方那枚徽标、页尾那条横幅、详情页背后那张背景图），所以
    /// 调用方删完必须让那一页重读：<see cref="EmbyItem.ImageTags"/> 上那个标签跟着就没了，而重读才知道。
    /// </para>
    /// </summary>
    public Task DeleteImageAsync(
        string itemId,
        string imageType,
        int? index,
        CancellationToken cancellationToken) =>
        http.DeleteAsync(
            EmbyUrl.Combine(ApiBase, ImagePath(itemId, imageType, index)),
            Context,
            cancellationToken);

    /// <summary>
    /// 从本机传一张图上去，设成这个条目的这一种图。
    /// <para>
    /// 正文就是图片的原样字节（<see cref="EmbyHttp.PostBytesAsync"/>），不套 JSON —— Emby 这个接口收的就是
    /// 一张图本身。<paramref name="contentType"/> 交给服务器去认，所以调用方要按后缀名说清是
    /// <c>image/jpeg</c> 还是 <c>image/png</c>：说成 <c>application/octet-stream</c> 也能成，但服务器的
    /// 图片处理那一头是按它选解码器的。
    /// </para>
    /// <para>
    /// 和 <see cref="ApplyRemoteImageAsync"/> 是两条路：那一条是「服务器，你去刮削源把这张取回来」，这一条是
    /// 「这一张，从我手上收下」。管理员才能用。
    /// </para>
    /// </summary>
    public Task UploadImageAsync(
        string itemId,
        string imageType,
        byte[] bytes,
        string contentType,
        CancellationToken cancellationToken) =>
        http.PostBytesAsync(
            EmbyUrl.Combine(ApiBase, $"Items/{itemId}/Images/{imageType}"),
            bytes,
            contentType,
            Context,
            cancellationToken);

    /// <summary>
    /// 一种图在服务器上那个地址的尾部（<c>Items/{id}/Images/{type}</c>，背景图多一个序号）。
    /// </summary>
    internal static string ImagePath(string itemId, string imageType, int? index) =>
        index is { } position
            ? $"Items/{itemId}/Images/{imageType}/{position}"
            : $"Items/{itemId}/Images/{imageType}";

    /// <summary>
    /// 按语言搜一遍这个文件的字幕。<paramref name="language"/> 是三字母代码（<c>chi</c>、<c>eng</c>），
    /// 服务器把它交给装着的字幕插件。一个插件都没装的时候答的是空数组，不是错。
    /// </summary>
    public Task<List<RemoteSubtitleInfo>> SearchSubtitlesAsync(
        string itemId,
        string mediaSourceId,
        string language,
        CancellationToken cancellationToken) =>
        http.GetJsonAsync<List<RemoteSubtitleInfo>>(
            EmbyUrl.Combine(ApiBase, $"Items/{itemId}/RemoteSearch/Subtitles/{Uri.EscapeDataString(language)}",
                ("MediaSourceId", mediaSourceId)),
            Context,
            cancellationToken);

    /// <summary>
    /// 下载一条字幕挂到这个文件上，答案是它成了第几条轨道。
    /// <para>
    /// 那个 id 要转义：它是字幕插件自己造的记号，里头出现斜杠的话，不转义就把路由整条打断。
    /// </para>
    /// </summary>
    public Task<SubtitleDownloadResult?> DownloadSubtitleAsync(
        string itemId,
        string mediaSourceId,
        string subtitleId,
        CancellationToken cancellationToken) =>
        http.PostForJsonAsync<SubtitleDownloadResult>(
            EmbyUrl.Combine(ApiBase, $"Items/{itemId}/RemoteSearch/Subtitles/{Uri.EscapeDataString(subtitleId)}",
                ("MediaSourceId", mediaSourceId)),
            null,
            Context,
            cancellationToken);

    /// <summary>删掉一条外挂字幕。内嵌在容器里的那些删不掉，服务器会拒绝。</summary>
    public Task DeleteSubtitleAsync(string itemId, int index, CancellationToken cancellationToken) =>
        http.DeleteAsync(EmbyUrl.Combine(ApiBase, $"Items/{itemId}/Subtitles/{index}"), Context, cancellationToken);

    // ---- 下载到设备 -----------------------------------------------------------

    /// <summary>
    /// 把这个条目的原始文件下到 <paramref name="path"/>，返回一共多少字节。走的是服务器的下载接口，也就是
    /// 原封不动的那个文件（不转码）。账号没有下载权限时服务器答 403。
    /// </summary>
    public Task<long> DownloadToFileAsync(
        string itemId,
        string path,
        IProgress<(long Done, long? Total)>? progress,
        CancellationToken cancellationToken) =>
        http.DownloadToFileAsync(
            EmbyUrl.Combine(ApiBase, $"Items/{itemId}/Download"),
            path,
            Context,
            progress,
            cancellationToken);
}
