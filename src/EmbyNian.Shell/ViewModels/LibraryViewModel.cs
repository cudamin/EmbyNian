using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Services;
using EmbyNian.Shell.Views;
using Microsoft.UI.Xaml;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// One line of the sort flyout: a key to choose, or the rule that a group boundary was crossed.
/// <para>
/// The flyout is still built out of <c>RadioMenuFlyoutItem</c>s by the page, because a
/// <c>MenuFlyout</c> has an <c>Items</c> list and no <c>ItemsSource</c> — there is nothing for a
/// template to bind to. What moved here is every decision that loop used to make for itself: which
/// keys this grid may offer, where the separators go, and which one is ticked. The page's loop is now
/// a translation with no judgement in it, and the list can be read without a window on screen.
/// </para>
/// </summary>
/// <param name="Option">The key, or null for a separator.</param>
public sealed record SortEntry(EmbySortOption? Option, bool IsChecked)
{
    /// <summary>The group boundary. One instance: it carries no state and there are eighteen of them.</summary>
    public static SortEntry Divider { get; } = new(null, false);

    public bool IsSeparator => Option is null;

    public string Label => Option?.Label ?? "";
}

/// <summary>
/// A library, a folder, a season, a person's credits or a set of search results: one grid, whose only
/// difference from the next is the <see cref="LibraryRequest"/> behind it.
/// <para>
/// The paging is hand-written on purpose. <c>ISupportIncrementalLoading</c> is honoured only by the
/// ListViewBase controls; <c>ItemsView</c> and <c>ItemsRepeater</c> ignore it, so the collection would
/// have implemented an interface nothing calls. The page watches its own scroll offset instead and asks
/// here for the next page — which is the one part of this that has to stay in the view, because how far
/// down a list the user is is not something a view model can know.
/// </para>
/// <para>
/// 需求 4 (sort) and 需求 5 (filters) both live here rather than in their controls. Both outlive the
/// flyout they are set from: they go into every query, into the button that shows them, and into
/// settings, so the flyout is the one place that must not own them.
/// </para>
/// </summary>
public sealed partial class LibraryViewModel : PageViewModel
{
    private const string Category = "媒体库";

    /// <summary>Segoe Fluent codepoints; see the note in <see cref="CardItem"/> on why numbers.</summary>
    private const int AscendingIcon = 0xE70E;
    private const int DescendingIcon = 0xE70D;

    /// <summary>
    /// <see cref="_total"/> before the first page comes back, which is what
    /// <see cref="HasMore"/> reads as 「ask and find out」.
    /// </summary>
    private const int NotLoaded = -1;

    /// <summary>
    /// Which grid this is: the navigation payload, and at the same time the witness that
    /// <see cref="Attach"/> has run. The four services below arrive in that same call as non-nullable
    /// parameters, so the one gate every entry point already had covers all five.
    /// </summary>
    private LibraryRequest? _request;
    private IShellActions? _actions;

    private ISettingsService? _settings;
    private EmbySession? _session;
    private EmbyImageStore? _images;
    private IServerCapabilities? _capabilities;

    /// <summary>Total the server reported; <see cref="NotLoaded"/> until the first page has come back.</summary>
    private int _total = NotLoaded;

    /// <summary>
    /// 需求 5：what the filter panel is set to. Owned here rather than by the panel, because it outlives
    /// the flyout: it goes into every query, into the badge, and into settings.
    /// </summary>
    private ItemFilters _filters = new();

    /// <summary>
    /// 设置 → 显示观看状态标记, read once per navigation so a card built later in a long scroll agrees
    /// with the ones above it.
    /// </summary>
    private bool _indicators = true;

    /// <summary>
    /// Set while <see cref="SortDescending"/> is being pushed to match a key's default, so its own
    /// change handler does not fire a second reload of the query the key change is already reloading.
    /// </summary>
    private bool _syncingDirection;

    public LibraryViewModel()
    {
        Heading = "媒体库";
        Eyebrow = "LIBRARY";
        Subheading = "正在读取…";
        EmptyNotice = "这里没有内容";
        SearchText = "";
        SortKey = EmbySortBy.Name;
        SortLabel = EmbySortBy.LabelOf(EmbySortBy.Name);
        FilterTooltip = "筛选";
        CardWidth = CardSize.PosterWidth;
    }

    /// <summary>
    /// Raised after a page of rows has been added. The page answers it by asking for one more if the
    /// window is still not full; see <see cref="LibraryPage"/>.
    /// </summary>
    internal event Action? PageArrived;

    public ObservableCollection<CardItem> Cards { get; } = [];

    [ObservableProperty]
    public partial string Heading { get; set; }

    /// <summary>
    /// 场记板上那行代号 —— LIBRARY / SEASONS / EPISODES / SEARCH / CREDITS。规则在
    /// <see cref="LibraryRequest.Eyebrow"/>：这一页是五种网格合一的，标题只说得出「叫什么」。
    /// </summary>
    [ObservableProperty]
    public partial string Eyebrow { get; set; }

    /// <summary>How much of the library is on screen, or why none of it is.</summary>
    [ObservableProperty]
    public partial string Subheading { get; set; }

    /// <summary>
    /// 设置 → 海报宽度, which every card in this grid is built and drawn at.
    /// <para>
    /// The grid used to be the one place the setting did not reach: the markup carried
    /// <c>CardWidth="170" PosterHeight="255" MinItemWidth="170"</c> as literals, so moving the slider
    /// resized the home page's rows and left the library — the page the setting is obviously about —
    /// exactly as it was. Now the same number sizes the cell, the card and the bitmap it is decoded for.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowHeight))]
    [NotifyPropertyChangedFor(nameof(CellWidth))]
    public partial int CardWidth { get; set; }

    /// <summary>
    /// How tall one cell of the grid has to be: the picture plus the two lines of text under it.
    /// Computed rather than typed in, which is how the old markup's <c>MinItemHeight="308"</c> came to
    /// be six pixels short of the card it was sizing. Same arithmetic as
    /// <see cref="CardShelf.RowHeight"/>, for the same reason.
    /// </summary>
    public double RowHeight => CardItem.HeightFor(CardBuildWidth, CardBuildWide) + CardSize.Chrome;

    /// <summary>The cell width the live layout should be using, in the current view.</summary>
    /// <remarks>
    /// Read by 海报 and 缩略图 only. 列表 lays out with a <c>StackLayout</c> — a row spans the page, and
    /// nothing but the page's own width can say how wide that is — so neither measurement reaches it.
    /// </remarks>
    public double CellWidth => CardBuildWidth;

    /// <summary>The <c>SortBy</c> value currently in effect.</summary>
    internal string SortKey { get; private set; }

    /// <summary>What the sort button says. Set together with <see cref="SortKey"/>; see <see cref="ShowSort"/>.</summary>
    [ObservableProperty]
    public partial string SortLabel { get; set; }

    /// <summary>
    /// Bound two-way to the button's toggle half, so clicking it is a sort change rather than a picture
    /// of one. The glyph and the tooltip are derived from it by the generator, which is what stops the
    /// three from ever disagreeing — the old code set all three by hand in one method and had to be
    /// trusted to call it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortGlyph))]
    [NotifyPropertyChangedFor(nameof(SortTooltip))]
    public partial bool SortDescending { get; set; }

    public string SortGlyph => Glyph(SortDescending ? DescendingIcon : AscendingIcon);

    public string SortTooltip => SortDescending ? "降序，点击改为升序" : "升序，点击改为降序";

    /// <summary>
    /// How this grid lays its rows out. Changing it rebuilds the cards at the new shape's size — the
    /// decode width has to follow the drawn width or every bitmap comes back blurry.
    /// <para>
    /// Both cell measurements hang off it as well, and they have to: the shape decides the card size, and
    /// a cell that keeps the old shape's numbers clips the new cards to it. 海报 → 缩略图 was drawing
    /// 16:9 stills into 170-wide poster cells, and 列表 was worse — the row's text sits to the right of
    /// its picture, so a too-narrow cell cut the title, the info line and the synopsis away entirely.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewGlyph))]
    [NotifyPropertyChangedFor(nameof(ViewLabel))]
    [NotifyPropertyChangedFor(nameof(RowHeight))]
    [NotifyPropertyChangedFor(nameof(CellWidth))]
    public partial LibraryView View { get; set; }

    /// <summary>What the view button shows; see <see cref="ViewGlyphOf"/>.</summary>
    public string ViewGlyph => Glyph(ViewGlyphOf(View));

    public string ViewLabel => View switch
    {
        LibraryView.Thumb => "缩略图",
        LibraryView.List => "列表",
        _ => "海报"
    };

    /// <summary>Segoe Fluent codepoints for the three view shapes.</summary>
    internal static int ViewGlyphOf(LibraryView view) => view switch
    {
        LibraryView.Thumb => 0xE8A9,   // 网格（宽格）
        LibraryView.List => 0xE8FD,   // 列表
        _ => 0xE80A                    // 平铺
    };

    /// <summary>Whether this grid is wide-shaped (16:9), which is what the thumb view draws.</summary>
    internal bool ViewIsWide => View == LibraryView.Thumb;

    /// <summary>
    /// The width a card is built at in the current view. The poster and thumb shapes follow 设置 →
    /// 海报宽度; the list view's still is the same wide width the detail page's episode rows use, so
    /// the two list styles in the app agree with each other.
    /// </summary>
    internal int CardBuildWidth => View switch
    {
        LibraryView.Thumb => CardSize.WideFor(CardWidth),
        LibraryView.List => CardSize.WideWidth,
        _ => CardWidth
    };

    internal bool CardBuildWide => View != LibraryView.Poster;

    /// <summary>How many filters are on, for the badge on the filter button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterBadgeVisibility))]
    public partial int FilterCount { get; set; }

    public Visibility FilterBadgeVisibility => Show(FilterCount > 0);

    /// <summary>
    /// Whether the letter bar belongs on this grid — Emby's own rule: name-ordered, and enough rows
    /// that jumping is worth the trip. Recomputed on every page arrival, because the count is what
    /// the second half of the rule reads.
    /// </summary>
    public Visibility AlphaVisibility => Show(
        SortKey == EmbySortBy.Name && !SortDescending && _total is > 30);

    /// <summary>Whether this grid has anything the player could start, for the two play buttons.</summary>
    internal bool HasPlayable => Cards.Any(card => card.Item.IsPlayable);

    // ---------------------------------------------------------------- 播放命令

    /// <summary>CanExecute is read off the loaded rows: an empty grid or one of folders only offers
    /// nothing to start, and a button that promises 播放全部 and plays nothing is worse than one that
    /// stays grey until the first page lands.</summary>
    [RelayCommand(CanExecute = nameof(HasPlayable))]
    private Task PlayAll() => PlayAllAsync();

    [RelayCommand(CanExecute = nameof(HasPlayable))]
    private Task PlayRandom() => PlayRandomAsync();

    /// <summary>Called by the page after a page of rows lands, when the two commands' answer and the
    /// letter bar's rule both change. The collection itself is not watched: the page already has an
    /// event for this exact moment.</summary>
    internal void RowsArrived()
    {
        PlayAllCommand.NotifyCanExecuteChanged();
        PlayRandomCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(AlphaVisibility));
    }

    [ObservableProperty]
    public partial string FilterTooltip { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    public partial bool ShowEmptyNotice { get; set; }

    public Visibility EmptyVisibility => Show(ShowEmptyNotice);

    /// <summary>
    /// What the middle of an empty grid says. A property rather than the literal the markup used to
    /// carry, because the search page opens empty on purpose and 「这里没有内容」 would read as a
    /// failed search nobody ran.
    /// </summary>
    [ObservableProperty]
    public partial string EmptyNotice { get; set; }

    /// <summary>
    /// Whether the search box belongs on this grid. Only the search page has one — every other grid is
    /// already 「these rows」, and a box on it would be a second, different question in the same header.
    /// </summary>
    public Visibility SearchVisibility => Show(_request?.IsSearch == true);

    /// <summary>What the box holds, so it survives the page being rebuilt around it.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; }

    /// <summary>What the filter panel is currently set to; the panel edits it in place.</summary>
    internal ItemFilters Filters => _filters;

    /// <summary>Which kind of row this grid holds, as the sort catalogue sees it.</summary>
    internal string? SortPreset => _request?.SortPreset;

    internal int LoadedCount => Cards.Count;

    /// <summary>The first row this grid loaded, for <c>--play</c>. Null before the first page arrives.</summary>
    internal EmbyItem? FirstItem => Cards.Count > 0 ? Cards[0].Item : null;

    /// <summary>
    /// Whether the server has rows this grid has not asked for yet. True before the first page, because
    /// 「nothing has been asked」 and 「everything has arrived」 must not look the same to the pager.
    /// </summary>
    internal bool HasMore => _total < 0 || Cards.Count < _total;

    /// <summary>
    /// Handed the navigation payload and the four capabilities one grid draws from, once per navigation to
    /// the page.
    /// <para>
    /// <paramref name="settings"/> is kept rather than only read here, because this grid writes back to it
    /// too — the sort and the filters are remembered per library root. <paramref name="capabilities"/> is
    /// kept for the same shape of reason: the server version may still have been in flight when the page
    /// opened, so the sort menu asks for it when it is opened rather than now.
    /// </para>
    /// </summary>
    internal void Attach(
        LibraryRequest request,
        IShellActions actions,
        ISettingsService settings,
        EmbySession session,
        EmbyImageStore images,
        IServerCapabilities capabilities)
    {
        _request = request;
        _actions = actions;
        _settings = settings;
        _session = session;
        _images = images;
        _capabilities = capabilities;

        var ui = settings.Settings.Ui;

        // Clamped again rather than trusted, on the same terms as HomeViewModel.Attach: a hand-edited
        // settings.json is a supported way to configure this app, and one poster the width of the
        // screen is a decode nobody asked for.
        CardWidth = Math.Clamp(ui.PosterWidth, 120, 340);
        _indicators = ui.ShowWatchedIndicators;

        Heading = request.Title;
        Eyebrow = request.Eyebrow;
        Subheading = "正在读取…";
        SearchText = request.SearchTerm ?? "";
        EmptyNotice = request.IsSearch ? "输入关键字开始搜索" : "这里没有内容";
        OnPropertyChanged(nameof(SearchVisibility));

        RestoreSort();
        RestoreFilters();
        RestoreView();
    }

    /// <summary>
    /// Runs the search page's box. Reloads in place rather than navigating: a new page per search would
    /// stack an entry on the back stack for every word typed, and the grid the user is looking at —
    /// its sort, its view shape, its filters — is the same grid either way.
    /// </summary>
    internal Task SearchAsync(string term)
    {
        if (_request is not { IsSearch: true } request) return Task.CompletedTask;

        var query = term.Trim();
        if (string.Equals(query, request.SearchTerm, StringComparison.Ordinal)) return Task.CompletedTask;

        _request = request with { SearchTerm = query, Title = LibraryRequest.SearchTitle(query) };
        SearchText = query;
        Heading = _request.Title;

        return ReloadAsync();
    }

    public override Task ReloadAsync() => LoadAsync(reset: true);

    /// <summary>
    /// Reads one page of rows: the first, or the next.
    /// </summary>
    /// <param name="reset">
    /// True to replace what is on screen. A reset supersedes whatever is in flight — the user changed
    /// the sort, and the rows now arriving belong to a query nobody is looking at. An append waits its
    /// turn instead, because two appends at once would both start from the same offset.
    /// </param>
    private async Task LoadAsync(bool reset)
    {
        if (_request is null || _actions is null) return;
        if (!reset && (Busy || !HasMore)) return;

        var token = BeginLoad();

        try
        {
            var start = reset ? 0 : Cards.Count;

            // No query at all: the search page with an empty box. Asked anyway, it would go out as
            // 「list everything under no parent」 and come back as the server's whole root.
            if (BuildQuery(start) is not { } query)
            {
                foreach (var card in Cards) card.ReleasePoster();
                Cards.Clear();

                _total = 0;
                Subheading = "输入关键字开始搜索";
                EmptyNotice = "输入关键字开始搜索";
                ShowEmptyNotice = true;

                EndLoad(token);
                PageArrived?.Invoke();
                return;
            }

            var result = await _session!
                .ExecuteAsync((client, ct) => client.GetItemsAsync(query, ct), token)
                .ConfigureAwait(true);

            if (!IsCurrent(token)) return;

            if (reset)
            {
                // Released, not just dropped: a card still holds its decoded bitmap until it is told
                // otherwise, and a reload that skipped this would leak one poster per card per refresh.
                foreach (var card in Cards) card.ReleasePoster();
                Cards.Clear();
            }

            _total = result.TotalRecordCount;

            foreach (var item in result.Items)
                Cards.Add(new CardItem(item, _images!, CardBuildWidth, CardBuildWide, indicators: _indicators));

            // A server that hands back nothing has nothing more to give, whatever its count said.
            if (result.Items.Count == 0) _total = Cards.Count;

            Subheading = Describe();
            EmptyNotice = "这里没有内容";
            ShowEmptyNotice = Cards.Count == 0;

            EndLoad(token);
            PageArrived?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, $"读取「{_request.Title}」失败", error);

            if (!IsCurrent(token)) return;

            Report($"读取「{_request.Title}」失败", error);
            Subheading = "读取失败";

            // Ready, not stuck. IsReady means the first load has finished, whether it found anything or
            // not, and the two tooling paths that wait on it (--show-library, --play) used to sit
            // through their full ten-second poll whenever a library failed to read.
            EndLoad(token);
        }
    }

    /// <summary>
    /// The next page, if there is one and nothing else is already asking. Called by the page when the
    /// scroll comes within a viewport of the bottom.
    /// </summary>
    internal void LoadMore()
    {
        if (Busy || !HasMore) return;
        _ = LoadAsync(reset: false);
    }

    /// <summary>
    /// Loads pages until <paramref name="index"/> is materialised — the letter bar's request, whose
    /// target the grid may not have scrolled anywhere near yet. Stops at the end; a letter past the
    /// last row lands on the last row.
    /// </summary>
    internal async Task LoadUntilAsync(int index)
    {
        while (!Busy && HasMore && Cards.Count <= index)
            await LoadAsync(reset: false).ConfigureAwait(true);
    }

    /// <summary>
    /// The sort flyout's contents, rebuilt on every open rather than once. Which keys apply depends on
    /// what this grid lists and on a server version that may still have been in flight when the page
    /// opened, and the tick has to follow a sort restored from settings — all three are known at open
    /// time and none of them reliably before it.
    /// </summary>
    internal IReadOnlyList<SortEntry> SortEntries()
    {
        if (_request is null) return [];

        var preset = _request.SortPreset;
        var entries = new List<SortEntry>();
        string? previousGroup = null;

        foreach (var option in EmbySortBy.For(preset, _capabilities!.ServerVersion))
        {
            // Separators rather than headings: nineteen keys need the grouping to be visible, and a
            // heading row in a MenuFlyout can only be faked with a disabled item.
            if (previousGroup is not null && option.Group != previousGroup) entries.Add(SortEntry.Divider);

            previousGroup = option.Group;
            entries.Add(new SortEntry(option, option.ValueOf(preset) == SortKey));
        }

        return entries;
    }

    /// <summary>A key was picked out of the flyout.</summary>
    internal void ChooseSort(EmbySortOption option)
    {
        if (_request is null || _actions is null) return;

        var key = option.ValueOf(_request.SortPreset);

        // Emby's own behaviour, and the only sensible one: a date key opens newest first and a name key
        // A to Z. Choosing 加入日期 and being shown the oldest thing in the library is never what was
        // meant, however the direction happened to be left by the previous key.
        if (key == SortKey && option.DescendingByDefault == SortDescending) return;

        SortKey = key;
        ShowSort(option.Label, option.DescendingByDefault);
        RememberSort();
        Requery();

        Log.Debug(Category, $"排序方式：{option.Label}（{key}）{(SortDescending ? "降序" : "升序")}");
    }

    /// <summary>
    /// A view shape was picked. Rebuilds the grid at the new shape's size rather than resizing the old
    /// cards: a bitmap decoded for 170px drawn at 300 is a blurry bitmap, and the wide shape wants the
    /// still, not the poster, from the image store.
    /// </summary>
    internal void ChooseView(LibraryView view)
    {
        if (view == View) return;

        View = view;
        RememberView();
        Requery();

        Log.Debug(Category, $"视图切换：{ViewLabel}");
    }

    /// <summary>
    /// 播放全部: the grid's first playable row, with the whole grid as the playlist — Emby's own
    /// btnPlay. Falls back to the first page's rows when not everything has arrived; the player asks
    /// the server for what follows.
    /// </summary>
    internal async Task PlayAllAsync()
    {
        if (_request is null || _actions is null) return;

        var items = await PlayableItemsAsync().ConfigureAwait(true);
        if (items.Count == 0) return;

        Log.Info(Category, $"播放全部：{items.Count} 项，从「{items[0].Name}」开始");
        await _actions.PlayAsync(items[0], episodes: items).ConfigureAwait(true);
    }

    /// <summary>
    /// 随机播放: the same list, shuffled — Emby's btnShuffle. Shuffled here rather than by asking the
    /// server to sort randomly, so 选集 shows the order the user is actually going to hear.
    /// </summary>
    internal async Task PlayRandomAsync()
    {
        if (_request is null || _actions is null) return;

        var items = await PlayableItemsAsync().ConfigureAwait(true);
        if (items.Count == 0) return;

        var random = Random.Shared;
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (items[index], items[swap]) = (items[swap], items[index]);
        }

        Log.Info(Category, $"随机播放：{items.Count} 项，从「{items[0].Name}」开始");
        await _actions.PlayAsync(items[0], episodes: items).ConfigureAwait(true);
    }

    /// <summary>
    /// The playable rows this grid can hand the player right now. Loaded rows first; when the server
    /// has more than one page and the user has not scrolled, the rest are asked for in one go — 播放
    /// 全部 with only the first hundred is 播放前一百.
    /// </summary>
    private async Task<List<EmbyItem>> PlayableItemsAsync()
    {
        var items = Cards.Select(card => card.Item).Where(item => item.IsPlayable).ToList();

        if (HasMore && _request is { } request)
        {
            var token = BeginLoad();
            try
            {
                var start = Cards.Count;
                while (true)
                {
                    if (BuildQuery(start) is not { } query) break;

                    var page = await _session!
                        .ExecuteAsync((client, ct) => client.GetItemsAsync(query, ct), token)
                        .ConfigureAwait(true);
                    if (!IsCurrent(token) || page.Items.Count == 0) break;

                    items.AddRange(page.Items.Where(item => item.IsPlayable));
                    start += page.Items.Count;
                    if (start >= page.TotalRecordCount) break;
                }

                EndLoad(token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                Log.Warn(Category, "读取全部条目失败", error);
                if (IsCurrent(token)) EndLoad(token);
            }
        }

        return items;
    }

    /// <summary>
    /// 字母跳转：which row the letter bar should land on. The count before the letter, in the grid's
    /// own sort, is the index — asked of the server with <c>Limit 0</c> rather than walked locally,
    /// because not every row of the library is in memory. Null when the query cannot be answered.
    /// </summary>
    /// <returns>The index of the first row at or after the letter, and the letter to light up.</returns>
    internal async Task<(int Index, string Letter)?> JumpToAsync(string letter)
    {
        if (_request is null) return null;

        try
        {
            // The bare count first: 「jump to Z in a library of 3 items」 is 「stay put」, and only the
            // total makes that say so.
            var total = await CountAsync(null).ConfigureAwait(true);
            if (total is null) return null;

            // # is the server's own 「not A–Z」 bucket and has no NameStartsWithOrGreater spelling; the
            // first row is the only honest answer for it.
            if (letter == "#") return (0, CurrentLetterOf(0));

            var before = await CountAsync(letter).ConfigureAwait(true);
            if (before is null) return null;

            var index = SortDescending ? (int)(total - before) : (int)before;
            index = Math.Clamp(index, 0, Math.Max(0, (int)total - 1));

            return (index, CurrentLetterOf(index));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception error)
        {
            Log.Warn(Category, $"字母跳转失败（{letter}）", error);
            return null;
        }
    }

    /// <summary>One count query with <c>Limit 0</c>, or null when it cannot be answered.</summary>
    private async Task<long?> CountAsync(string? letter)
    {
        if (_request is null) return null;

        // The counting query is the grid's own query with the paging stripped out and the letter
        // applied, so the count and the rows can never disagree about what is being counted.
        // ItemQuery is a class with init-only setters, so the copy is a construction rather than a
        // `with` — the one thing it changes is everything it does not name.
        if (BuildQuery(0) is not { } source) return null;

        var query = new ItemQuery
        {
            ParentId = source.ParentId,
            SearchTerm = source.SearchTerm,
            Recursive = source.Recursive,
            IncludeItemTypes = source.IncludeItemTypes,
            SortBy = source.SortBy,
            Descending = source.Descending,
            StartIndex = 0,
            Limit = 0,
            Fields = "",
            IsPlayed = source.IsPlayed,
            Genre = source.Genre,
            PersonId = source.PersonId,
            Filters = source.Filters,
            MediaTypes = source.MediaTypes,
            NameStartsWith = letter
        };

        var result = await _session!
            .ExecuteAsync((client, ct) => client.GetItemsAsync(query, ct), CancellationToken.None)
            .ConfigureAwait(true);

        return result.TotalRecordCount;
    }

    /// <summary>
    /// The letter a given row starts with — what the bar should light up after a jump or a scroll.
    /// SortName when the server sent one, else the name; uppercased, and # for anything that is not a
    /// letter, which is the bar's own first bucket.
    /// </summary>
    internal string CurrentLetterOf(int index)
    {
        if (index < 0 || index >= Cards.Count) return "#";

        var name = Cards[index].Item.SortName is { Length: > 0 } sort ? sort : Cards[index].Item.Name;
        var first = name.TrimStart().FirstOrDefault();
        return char.IsAsciiLetter(first) ? first.ToString().ToUpperInvariant() : "#";
    }

    /// <summary>
    /// 需求 5：something was ticked in the panel, which edits <see cref="Filters"/> in place. Applied at
    /// once, with the panel still open: the grid is right behind it, and it is the answer to the tick.
    /// </summary>
    internal void ApplyFilters()
    {
        ShowFilterState();
        RememberFilters();
        Requery();

        Log.Debug(Category, _filters.IsEmpty ? "清除筛选" : $"筛选：{_filters.Describe()}");
    }

    /// <summary>
    /// Where a card goes when it is invoked: wherever the shell says. <see cref="ShellPage.OpenItem"/>
    /// is the one place the 「detail page or another grid」 choice is made, so a poster in a library, one
    /// on the home page and 打开 in the context menu all agree about the same item.
    /// <para>
    /// This grid used to make that choice again for itself, and made it differently: a series, a season
    /// and a box set all went straight to a child grid. Opening 「99.9 刑事专业律师」 from its own library
    /// therefore landed on a bare grid of two season folders — no 简介, no 演职人员, no 单集列表 — while
    /// the very same poster clicked on the home page opened the series page. A movie went to the player
    /// for the same reason, which is the other half of 点卡片进详情页、点正中央的播放键才播放: the play key
    /// is what plays, and a card without one (a series, a season, a folder, a person) had nothing to lose
    /// by opening instead. A box set and a plain folder still answer with another grid — that is
    /// <see cref="DetailRequest.Supports"/>'s answer, not this page's.
    /// </para>
    /// </summary>
    internal void Open(CardItem card)
    {
        if (_actions is null) return;

        _actions.OpenItem(card.Item);
    }

    /// <summary>
    /// Tooling: invokes the first row that drills down, exactly as a click on it would, and answers which
    /// row that was. Null when this grid holds nothing with a detail page — a library of box sets or of
    /// playlists answers a grid for every row it holds, so this is deliberately not
    /// <see cref="FirstItem"/>, which would leave the caller waiting for a page nobody opened.
    /// <para>
    /// Through <see cref="Open"/> rather than by asking the shell for a detail page directly, because that
    /// substitution is what let the routing above stay wrong: a check that opens the page itself proves
    /// the page works and says nothing about whether clicking a card ever reaches it.
    /// </para>
    /// </summary>
    internal EmbyItem? OpenFirstDetail()
    {
        if (Cards.FirstOrDefault(card => DetailRequest.Supports(card.Item)) is not { } card) return null;

        Open(card);
        return card.Item;
    }

    /// <summary>
    /// Every episode on this page, or null when its rows are not episodes — a mixed grid's neighbours
    /// are not each other's episodes, and offering them as 选集 would be wrong rather than merely
    /// unhelpful. What makes 「选集」 and 上一集/下一集 work off this grid instead of asking the server for a
    /// list it has already sent.
    /// </summary>
    internal IReadOnlyList<EmbyItem>? Episodes()
    {
        var episodes = Cards
            .Select(card => card.Item)
            .Where(item => item.Type == EmbyItemType.Episode)
            .ToList();

        return episodes.Count == Cards.Count && episodes.Count > 0 ? episodes : null;
    }

    /// <summary>
    /// Which genres, tags or years the rows this grid is listing actually have.
    /// <para>
    /// The scope is this page's own query, which <see cref="EmbyClient.GetFilterValuesAsync"/> strips
    /// down to where to look: a list narrowed by the selection made from it empties itself as it is used.
    /// </para>
    /// </summary>
    internal async Task<IReadOnlyList<string>> FetchFilterValuesAsync(string key, CancellationToken cancellationToken)
    {
        if (_request is null) return [];

        if (BuildQuery(0) is not { } scope) return [];

        return await _session!
            .ExecuteAsync((client, token) => client.GetFilterValuesAsync(key, scope, token), cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// A sort or filter change: the count the server reported is about a query that no longer exists,
    /// so the pager must not go on believing it.
    /// </summary>
    private void Requery()
    {
        _total = NotLoaded;
        _ = LoadAsync(reset: true);
    }

    partial void OnSortDescendingChanged(bool value)
    {
        if (_syncingDirection) return;

        RememberSort();
        Requery();
    }

    /// <summary>Puts the button in step with a newly chosen key, without that counting as a second change.</summary>
    private void ShowSort(string label, bool descending)
    {
        SortLabel = label;

        _syncingDirection = true;
        try { SortDescending = descending; }
        finally { _syncingDirection = false; }
    }

    /// <summary>
    /// The live settings object, read through the service every time rather than copied into a field: it is
    /// one instance for the process and the settings page edits it in place, so a copy taken at
    /// <see cref="Attach"/> would go stale the moment 海报宽度 was moved.
    /// </summary>
    private AppSettings Settings => _settings!.Settings;

    /// <summary>
    /// The sort this grid should open on: what it was last left in, or the key that suits what it lists.
    /// A season opened in alphabetical order is the one ordering that is always wrong, which is why the
    /// default is per preset rather than a single global one.
    /// </summary>
    private void RestoreSort()
    {
        var request = _request!;
        var preset = request.SortPreset;
        var fallback = EmbySortBy.Default(preset);

        SortKey = fallback.ValueOf(preset);
        var label = fallback.Label;
        var descending = fallback.DescendingByDefault;

        if (MemoryKey() is { } memoryKey
            && Settings.Ui.Sort.TryGetValue(memoryKey, out var saved)
            && EmbySortBy.Find(saved.By) is { } option
            && option.AppliesTo(preset, _capabilities!.ServerVersion))
        {
            SortKey = saved.By;
            label = option.Label;
            descending = saved.Descending;
        }

        ShowSort(label, descending);
    }

    private void RememberSort()
    {
        if (MemoryKey() is not { } memoryKey) return;

        var ui = Settings.Ui;
        ui.Sort[memoryKey] = new LibrarySort { By = SortKey, Descending = SortDescending };

        // Not saved here: the settings file is written on exit, and a sort change is not worth a disk
        // write on every click.
    }

    /// <summary>
    /// The view shape this grid opens in, on the same terms as the sort: remembered per library root,
    /// the catalogue's default elsewhere.
    /// </summary>
    private void RestoreView()
    {
        if (MemoryKey() is { } memoryKey
            && Settings.Ui.Views.TryGetValue(memoryKey, out var saved))
        {
            View = saved;
        }
    }

    private void RememberView()
    {
        if (MemoryKey() is not { } memoryKey) return;

        Settings.Ui.Views[memoryKey] = View;
    }

    /// <summary>
    /// 需求 5：the filters this grid should open on, restored on the same terms as the sort. A clone,
    /// because the panel edits this object in place and settings must not change under the file writer
    /// until <see cref="RememberFilters"/> says so.
    /// </summary>
    private void RestoreFilters()
    {
        _filters = MemoryKey() is { } memoryKey
            && Settings.Ui.Filters.TryGetValue(memoryKey, out var saved)
                ? saved.Clone()
                : new ItemFilters();

        ShowFilterState();
    }

    private void RememberFilters()
    {
        if (MemoryKey() is not { } memoryKey) return;

        var ui = Settings.Ui;

        // IsBlank, not IsEmpty: a selection this build cannot read is still the user's, and dropping it
        // the moment they touch the panel would lose it for the build that can.
        if (_filters.IsBlank) ui.Filters.Remove(memoryKey);
        else ui.Filters[memoryKey] = _filters.Clone();
    }

    private void ShowFilterState()
    {
        FilterCount = _filters.Count;
        FilterTooltip = FilterCount == 0 ? "筛选" : _filters.Describe();
    }

    /// <summary>
    /// Where this grid's sort and filters are remembered, or null for a grid whose state is not worth
    /// keeping. Only library roots qualify: a folder or a season would add an entry to settings.json
    /// every time one was opened.
    /// </summary>
    private string? MemoryKey() =>
        _request is { CollectionType: not null, ParentId: { Length: > 0 } id } ? id : null;

    private string Describe() =>
        _total <= 0 ? (Cards.Count == 0 ? "没有内容" : $"共 {Cards.Count} 项")
        : Cards.Count >= _total ? $"共 {_total} 项"
        : $"已加载 {Cards.Count} / {_total} 项";

    /// <summary>
    /// The query this grid is showing, or null when there is nothing to ask: the search page before a
    /// term is typed. Null rather than a query with an empty term, because Emby answers 「no term, no
    /// parent」 with its library root — a wrong answer that looks like a right one.
    /// </summary>
    private ItemQuery? BuildQuery(int start)
    {
        var request = _request!;
        var limit = _settings!.PageSize;

        if (request.IsSearch)
        {
            if (request.SearchTerm is not { Length: > 0 } term) return null;

            return new ItemQuery
            {
                SearchTerm = term,
                Recursive = true,
                // 音乐 is deliberately absent: this app has no music face, so MusicVideo rows would open
                // onto a grid of things it cannot show.
                IncludeItemTypes =
                [
                    EmbyItemType.Movie,
                    EmbyItemType.Series,
                    EmbyItemType.Episode,
                    EmbyItemType.Video
                ],
                SortBy = SortKey,
                Descending = SortDescending,
                StartIndex = start,
                Limit = limit,
                Filters = _filters
            };
        }

        // 演职人员：a person's credits. Ahead of the folder query on purpose — a person has no children,
        // so the query below would answer with the server's library list instead of an empty grid, which
        // is a far more confusing wrong answer.
        if (request.PersonId is { Length: > 0 } personId)
        {
            return new ItemQuery
            {
                PersonId = personId,
                Recursive = true,
                IncludeItemTypes = ItemQuery.CreditedTypes,
                SortBy = SortKey,
                Descending = SortDescending,
                StartIndex = start,
                Limit = limit,
                Filters = _filters
            };
        }

        var types = ItemQuery.FlattenedTypes(request.CollectionType);

        return new ItemQuery
        {
            ParentId = request.ParentId,
            // Recursive only at a library root. Below one, the folder tree is what the user is walking:
            // flattening a series would put every episode of every season on one page.
            Recursive = types.Count > 0,
            IncludeItemTypes = types,
            SortBy = SortKey,
            Descending = SortDescending,
            StartIndex = start,
            Limit = limit,
            // Sent unconditionally: an empty selection contributes no parameters, so there is nothing to
            // branch on and no way for the query and the panel to disagree.
            Filters = _filters
        };
    }

    private static string Glyph(int codepoint) => char.ConvertFromUtf32(codepoint);
}
