using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Services;
using EmbyNian.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// The poster grid's view. What is left here is the four things XAML and a view model cannot do for
/// themselves: a navigation parameter turned into an <c>Attach</c>, the scroll offset that drives the
/// paging, the construction of a <c>MenuFlyout</c>'s items — it has an <c>Items</c> list and no
/// <c>ItemsSource</c>, so there is nothing for a template to bind — and the context menu, which needs the
/// element it was raised over.
/// <para>
/// Everything else is in <see cref="LibraryViewModel"/>: the query, the paging arithmetic, the sort and
/// filter state, and what the two buttons say. None of it needs a window on screen any more, and the
/// page no longer names an element it might one day rename.
/// </para>
/// </summary>
public sealed partial class LibraryPage : Page, IShellContent
{
    private const string Category = "媒体库";

    private LibraryRequest? _request;

    /// <summary>
    /// Resolved on navigation, for the three things this page cannot ask the view model for: the card
    /// menus need a session, the sizing probe needs the raw 设置 value that the view model has already
    /// clamped, and the filter panel needs the server version to know which switches to offer.
    /// </summary>
    private ISettingsService? _settings;

    private EmbySession? _session;
    private IServerCapabilities? _capabilities;
    private IShellActions? _actions;

    private ScrollView? _scroll;

    public LibraryPage()
    {
        InitializeComponent();

        // Before the first load can raise it. The view model asks for one more page when it has just
        // added one; whether the window is full is the one thing only the view can answer.
        ViewModel.PageArrived += PumpIfShort;

        // The view shape is the view model's, applying it to the live controls is the view's — the
        // template and the menu ticks are both named elements, which is exactly what a view model
        // must not reach for.
        ViewModel.PropertyChanged += OnViewModelChanged;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.View)) ApplyView();
        if (e.PropertyName == nameof(LibraryViewModel.SortLabel)
            || e.PropertyName == nameof(LibraryViewModel.SortDescending))
            Alpha.SetCurrent(null);
    }

    /// <summary>
    /// Created with the page rather than on navigation, so <c>x:Bind</c> never has a null root; see
    /// <see cref="HomePage.ViewModel"/>.
    /// </summary>
    internal LibraryViewModel ViewModel { get; } = new();

    /// <summary>For the self-check and the two tooling paths, which have to see the page from outside.</summary>
    internal int LoadedCount => ViewModel.LoadedCount;

    // Kept for the shell self-check and breadcrumb code, both of which need the page's title from
    // outside. Reads the view model rather than the slate: the slate is what draws it, and a probe
    // that walks the tree would be testing the drawing instead of the answer.
    internal string HeadingText => ViewModel.Heading;

    internal string SortKey => ViewModel.SortKey;

    internal bool SortDescending => ViewModel.SortDescending;

    internal string? SortPreset => ViewModel.SortPreset;

    internal bool IsReady => ViewModel.IsReady;

    internal EmbyItem? FirstItem => ViewModel.FirstItem;

    /// <summary>
    /// 需求 4: what artwork the server actually holds for the items on this page — 「60 个条目 —— 海报 60、
    /// 徽标 3、缩略图 58、横幅图 0、艺术图 0、背景图 41」. Counted over everything loaded rather than off the
    /// realised cards, because the question is the catalogue's, not the viewport's: whether an artwork this
    /// build now draws will ever be seen on this server.
    /// </summary>
    internal string ArtworkCensus => ItemArtwork.Census([.. ViewModel.Cards.Select(card => card.Item)]);

    /// <summary>
    /// Tooling: clicks the first row that drills down and says which one it was, for the self-check's
    /// detail stage. Goes through the grid's own invoke handler, so what it proves includes 「a click on a
    /// series reaches the series page」.
    /// </summary>
    internal EmbyItem? OpenFirstDetail() => ViewModel.OpenFirstDetail();

    /// <summary>What the filter panel is currently set to; read by the self-check.</summary>
    internal ItemFilters Filters => ViewModel.Filters;

    /// <summary>
    /// 搜索页's state, for the self-check: whether this grid is the search one, whether its box is on
    /// screen, what is in the box, what the middle of the page says and how many rows arrived.
    /// </summary>
    internal (bool IsSearch, bool BoxShown, string Text, string Notice, int Rows) SearchState => (
        _request?.IsSearch == true,
        SearchInput.Visibility == Visibility.Visible,
        SearchInput.Text ?? string.Empty,
        ViewModel.EmptyNotice,
        ViewModel.LoadedCount);

    /// <summary>
    /// What size this grid draws at and where that size came from: 设置 → 海报宽度, the width the view
    /// model clamped it to, and the cell the live layout is actually using.
    /// <para>
    /// The last two are read off the layout the grid is holding rather than recomputed, which is the point
    /// of the probe — this grid's cell, card and decode width used to be three literals in the markup, so
    /// the setting reached every page but this one. A binding that silently never fired would show up here
    /// as <c>NaN</c>, not as a number this method worked out for itself. Zero means a live layout with no
    /// cell to speak of, which is 列表: see <see cref="Shape"/>.
    /// </para>
    /// </summary>
    internal (int Setting, int Card, double CellWidth, double CellHeight) Sizing
    {
        get
        {
            var (_, _, cell, row) = Shape();
            return (_settings?.Settings.Ui.PosterWidth ?? 0, ViewModel.CardWidth, cell, row);
        }
    }

    /// <summary>
    /// Tooling: puts each of the three view shapes on the live grid in turn, says what the layout made of
    /// each one, and puts the original back.
    /// <para>
    /// Assigns the view model's <c>View</c> rather than calling <see cref="LibraryViewModel.ChooseView"/>:
    /// that also saves the choice and re-queries the server, and a probe may do neither — the walk would
    /// leave the library on 列表 and hand the next stage of the self-check an empty grid to click into.
    /// What is under test is the half that a click cannot get right on its own: a view change reaching the
    /// live layout. Both cell measurements are computed from the shape and were not announced when it
    /// changed, so 缩略图 drew 16:9 stills into 170-wide poster cells, and 列表 — a row is a picture with
    /// its text beside it — had the title, the info line and the synopsis cut off past the cell's edge.
    /// </para>
    /// <para>
    /// A launch cannot see that on its own, which is why this walk exists rather than one more reading of
    /// whatever view the app happened to start in: the cell bindings are evaluated with the saved view
    /// already in place, so every view is correct until something changes it.
    /// </para>
    /// </summary>
    internal (IReadOnlyList<(string Label, string Layout, double Cell, double Row)> Views, bool Restored) WalkViews()
    {
        var original = ViewModel.View;
        var seen = new List<(string, string, double, double)>(3);

        foreach (var view in Enum.GetValues<LibraryView>())
        {
            ViewModel.View = view;

            // The layout is swapped by ApplyView off the property change, and the cell numbers are pushed
            // into it by two bindings; a measured pass is what turns both into something to read.
            Cards.UpdateLayout();
            seen.Add(Shape());
        }

        ViewModel.View = original;
        Cards.UpdateLayout();

        // Whether the page was handed back the way it was found, answered here because this is the only
        // scope that remembers what that was: everything the report reads off the page afterwards is
        // already the restored value, so a check comparing those to each other would agree with itself.
        // Enum.GetValues comes back in numeric order, so each shape's reading sits at its own value.
        var restored = ViewModel.View == original && Shape() == seen[(int)original];

        return (seen, restored);
    }

    /// <summary>
    /// What the grid's live layout currently is, read off the layout the grid is holding rather than off
    /// the one in the markup: the two are the same object until something swaps them, and 「the swap put
    /// the right object back」 is precisely what the walk above is asking. A <c>StackLayout</c> has no cell
    /// to report — the row it lays out is as wide as the page.
    /// </summary>
    private (string Label, string Layout, double Cell, double Row) Shape() => Cards.Layout switch
    {
        UniformGridLayout grid => (ViewModel.ViewLabel, nameof(UniformGridLayout), grid.MinItemWidth, grid.MinItemHeight),
        { } other => (ViewModel.ViewLabel, other.GetType().Name, 0, 0),
        _ => (ViewModel.ViewLabel, "没有布局", 0, 0)
    };

    /// <summary>
    /// What one格子 paints under the pointer, measured off a realised container rather than read back out
    /// of the markup. 三个读数：静止、指针在上面、按下去。
    /// <para>
    /// 这一份存在的理由是它问的那句话在截图里看不见，而且它靠的机制是会悄悄失效的那一种：
    /// <c>ItemContainer</c> 的三个状态把 <c>PART_CommonVisual</c> 的 <c>Fill</c> 换成
    /// <c>{ThemeResource ItemContainerPointerOverBackground}</c>，而那个键是从模板里往外查的 ——
    /// 本页把它改成了透明（见 LibraryPage.xaml 顶上那段），一旦 WinUI 改了键名，或者这份覆盖没被查到，
    /// 灰底就又回来了，压在卡片那圈胶片格外面，而闸门全绿。
    /// </para>
    /// </summary>
    /// <remarks>
    /// 第一个名字不叫 <c>Rest</c>：那是 <c>ValueTuple</c> 给第八个字段留的名字，编译器不让用 —— 同
    /// <see cref="PosterCard"/> 里那一份。
    /// </remarks>
    internal (bool Found, string Still, string Over, string Pressed) ContainerHalo()
    {
        if (Find<ItemContainer>(Cards) is not { } box) return (false, "无格子", "无格子", "无格子");

        var (still, over, pressed) = (Fill("UnselectedNormal"), Fill("UnselectedPointerOver"), Fill("UnselectedPressed"));

        // 量完把它交回静止：这个格子还在页面上，接着往下走的自检看到的得是没人碰过的那一张。
        VisualStateManager.GoToState(box, "UnselectedNormal", false);

        return (true, still, over, pressed);

        string Fill(string state)
        {
            VisualStateManager.GoToState(box, state, false);

            // 模板里那块矩形就叫这个名字；按名字找而不是「第一个矩形」——同一层还有选中框和复选框。
            return Find<Microsoft.UI.Xaml.Shapes.Rectangle>(box, "PART_CommonVisual") switch
            {
                { Fill: SolidColorBrush solid } => $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
                { Fill: null } => "不画",
                null => "找不到那块矩形",
                _ => "不是纯色",
            };
        }
    }

    /// <summary>视觉树里第一个这种东西，可以再挑名字。找不到就是 null —— 没实现出来的容器不是错。</summary>
    private static T? Find<T>(DependencyObject node, string? name = null) where T : FrameworkElement
    {
        if (node is T hit && (name is null || hit.Name == name)) return hit;

        var children = VisualTreeHelper.GetChildrenCount(node);

        for (var index = 0; index < children; index++)
            if (Find<T>(VisualTreeHelper.GetChild(node, index), name) is { } found) return found;

        return null;
    }

    /// <inheritdoc />
    public object? NavigationRequest => _request;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not LibraryRequest request)
        {
            Log.Warn(Category, "导航参数缺失，无法确定要显示哪个媒体库");
            return;
        }

        _request = request;

        // The shell reads this back to restore its pane highlight after a Frame.GoBack.
        Tag = request.Tag;

        // The one place this page resolves anything. Deliberately a single block: it is the line that goes
        // when the shell stops handing a container around, and spreading it would turn one deletion into a
        // hunt.
        var services = request.Services;
        _settings = services.GetRequiredService<ISettingsService>();
        _session = services.GetRequiredService<EmbySession>();
        _capabilities = services.GetRequiredService<IServerCapabilities>();
        _actions = services.GetRequiredService<IShellActions>();

        ViewModel.Attach(
            request,
            _actions,
            _settings,
            _session,
            services.GetRequiredService<EmbyImageStore>(),
            _capabilities);

        ApplyView();

        _ = ViewModel.ReloadAsync();
    }

    public void Release()
    {
        ViewModel.Cancel();
        FilterPane.Dismiss();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Release();
        base.OnNavigatedFrom(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The search page opens with an empty box and nothing to look at, so the caret belongs in the box.
        // Here rather than in OnNavigatedTo: nothing can take focus before it is in the tree.
        if (_request is { IsSearch: true } && ViewModel.SearchText.Length == 0)
            SearchInput.Focus(FocusState.Programmatic);

        if (_scroll is not null) return;

        // ItemsView exposes the scroller it owns; there is no other way to know how far down the list
        // the user is, and no other place to hang the paging trigger.
        _scroll = Cards.ScrollView;

        if (_scroll is null)
        {
            Log.Warn(Category, "取不到 ItemsView.ScrollView，滚动分页将不可用");
            return;
        }

        _scroll.ViewChanged += OnViewChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_scroll is null) return;

        _scroll.ViewChanged -= OnViewChanged;
        _scroll = null;
    }

    /// <summary>
    /// The paging trigger. One viewport of slack, which is what keeps a fast scroll from ever reaching
    /// the bottom of what has been loaded.
    /// </summary>
    private void OnViewChanged(ScrollView sender, object args)
    {
        var remaining = sender.ExtentHeight - (sender.VerticalOffset + sender.ViewportHeight);
        if (remaining < sender.ViewportHeight) ViewModel.LoadMore();

        SyncAlphaFromScroll();
    }

    /// <summary>
    /// Asks for one more page when the last did not fill the window, and keeps the two play buttons
    /// and the letter bar's rule in step with the rows that just landed.
    /// </summary>
    private void PumpIfShort()
    {
        ViewModel.RowsArrived();
        SyncAlphaFromScroll();

        if (!ViewModel.HasMore) return;

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_scroll is null || !ViewModel.HasMore) return;
            if (_scroll.ExtentHeight <= _scroll.ViewportHeight + 1) ViewModel.LoadMore();
        });
    }

    private void OnItemInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is CardItem card) ViewModel.Open(card);
    }

    /// <summary>
    /// 需求 6：右键一张卡片。The list this grid is showing is handed along so 选集 works off it, exactly
    /// as it does for an invoke, and a metadata save reloads the page rather than patching the card —
    /// an edit can move the item's title, its artwork and its position in the sort all at once.
    /// </summary>
    private void OnCardContextRequested(UIElement sender, ContextRequestedEventArgs args) =>
        ItemCommands.Handle(_session, _actions, sender, args, ViewModel.Episodes(), Reload);

    /// <summary>需求 6：the same commands from the strip that appears on a card under the pointer.</summary>
    private void OnCardActionRequested(object? sender, CardActionEventArgs args) =>
        ItemCommands.Handle(_session, _actions, sender, args, ViewModel.Episodes(), Reload);

    private void Reload() => _ = ViewModel.ReloadAsync();

    /// <summary>
    /// 需求 4：fills the sort flyout. Rebuilt on every open rather than once — the view model decides
    /// what belongs in it and why; see <see cref="LibraryViewModel.SortEntries"/>.
    /// </summary>
    private void OnSortMenuOpening(object sender, object e) => BuildSortMenu();

    /// <summary>
    /// Builds the flyout and hands back the labels it put there, so the self-check reports on the real
    /// menu rather than on a copy of this loop.
    /// </summary>
    internal IReadOnlyList<string> BuildSortMenu()
    {
        SortMenu.Items.Clear();

        var labels = new List<string>();

        foreach (var entry in ViewModel.SortEntries())
        {
            if (entry.Option is not { } option)
            {
                SortMenu.Items.Add(new MenuFlyoutSeparator());
                continue;
            }

            // No GroupName: the flyout is thrown away and rebuilt on the next open, so the radio state
            // only has to be right for as long as the menu is up, and a shared group name would
            // otherwise leave this page's items registered against the next page's.
            var item = new RadioMenuFlyoutItem
            {
                Text = option.Label,
                Tag = option,
                IsChecked = entry.IsChecked
            };

            item.Click += OnSortKeyChosen;
            SortMenu.Items.Add(item);
            labels.Add(option.Label);
        }

        return labels;
    }

    private void OnSortKeyChosen(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: EmbySortOption option }) ViewModel.ChooseSort(option);
    }

    /// <summary>Tooling: drops the sort menu open where a screenshot can see it.</summary>
    internal void ShowSortMenu() => SortButton.Flyout?.ShowAt(SortButton);

    /// <summary>Tooling: the same for the filter panel.</summary>
    internal void ShowFilterPanel() => FilterButton.Flyout?.ShowAt(FilterButton);

    /// <summary>
    /// 需求 5：fills the panel for this grid. Rebuilt on every open rather than once, for the reasons the
    /// sort menu is — and the server version in particular may still have been in flight when the page
    /// opened.
    /// </summary>
    private void OnFilterOpening(object? sender, object e)
    {
        if (_capabilities is null) return;

        FilterPane.Open(ViewModel.Filters, ViewModel.SortPreset, _capabilities.ServerVersion, ViewModel.FetchFilterValuesAsync);
    }

    /// <summary>
    /// Builds the filter panel through the page's own handler and hands back what it offered, so the
    /// self-check reports on the real panel rather than on a copy of this loop. Same reason
    /// <see cref="BuildSortMenu"/> is internal.
    /// </summary>
    internal IReadOnlyList<string> BuildFilterPanel()
    {
        OnFilterOpening(this, new object());
        return FilterPane.Labels;
    }

    /// <inheritdoc cref="FilterPanel.EveryTemplateResolves"/>
    /// <remarks>Read after <see cref="BuildFilterPanel"/>: there are no blocks to ask about before it.</remarks>
    internal bool FilterTemplatesResolve => FilterPane.EveryTemplateResolves;

    /// <summary>自检：the view shape in force, by name, and whether the letter bar is showing.</summary>
    internal string ViewName => ViewModel.View.ToString();

    internal bool AlphaShown => Alpha.Visibility == Visibility.Visible;

    private void OnFilterChanged(object? sender, EventArgs e) => ViewModel.ApplyFilters();

    /// <summary>
    /// 搜索页's own box. Handed to the view model rather than turned into a navigation: this is already
    /// the right page, and one back-stack entry per word typed is not a history anyone wants to walk.
    /// </summary>
    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e) =>
        _ = ViewModel.SearchAsync(e.QueryText ?? sender.Text ?? string.Empty);

    /// <summary>A view shape was picked out of the view menu. The Tag carries the enum name — the
    /// menu is markup, and an enum-typed Tag would need a converter either way.</summary>
    private void OnViewChosen(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string name } && Enum.TryParse<LibraryView>(name, out var view))
            ViewModel.ChooseView(view);
    }

    /// <summary>
    /// The letter bar was clicked. The view model answers with the row to land on — a count the server
    /// did, not a walk of rows this grid happens to hold — and this page does the scrolling, which is
    /// the one half the view model cannot do: how far down the list the user is belongs to the view.
    /// </summary>
    private async void OnAlphaJump(object? sender, string letter)
    {
        if (await ViewModel.JumpToAsync(letter).ConfigureAwait(true) is not { } jump) return;

        // Not every row is loaded yet; the scroll target has to be materialised first, which one page
        // at a time is how the grid loads. Asking for the missing pages in one go is the pager's job.
        await ViewModel.LoadUntilAsync(jump.Index).ConfigureAwait(true);

        Alpha.SetCurrent(jump.Letter);
        BringItemIntoView(jump.Index);
    }

    /// <summary>
    /// Brings one item to the top of the viewport through <see cref="ItemsView"/>'s own layout. The
    /// uniform grid is free to change its column count and stretch its cells as the window resizes; asking
    /// the control to locate the item keeps column and row spacing in that calculation instead of trying
    /// to reproduce the layout's arithmetic here.
    /// </summary>
    private void BringItemIntoView(int index)
    {
        if (index < 0 || index >= ViewModel.Cards.Count) return;

        Cards.StartBringItemIntoView(index, new BringIntoViewOptions
        {
            AnimationDesired = false,
            HorizontalAlignmentRatio = 0,
            VerticalAlignmentRatio = 0
        });
    }

    /// <summary>
    /// Lights the letter bar up for wherever the user has scrolled to. Called from the scroll event,
    /// cheaply: <see cref="ItemsView.TryGetItemIndex"/> asks the live layout which item is nearest the
    /// viewport's top-left corner, so responsive column counts, stretched cells and layout spacing are
    /// already reflected in the answer.
    /// </summary>
    private void SyncAlphaFromScroll()
    {
        if (_scroll is null || Alpha.Visibility != Visibility.Visible) return;
        if (!Cards.TryGetItemIndex(0, 0, out var index)) return;

        Alpha.SetCurrent(ViewModel.CurrentLetterOf(index));
    }

    /// <summary>Applies the current view shape to the live controls: which template the grid draws
    /// with, which layout arranges those templates, and whether the letter bar can be seen. Called on
    /// navigation and on every change.</summary>
    /// <remarks>
    /// The layout has to move with the template. 海报 and 缩略图 are grids of cells and share one
    /// <c>UniformGridLayout</c> sized from the view model; 列表 is one row per line, which no cell width
    /// can express — see the two layouts in the page's resources.
    /// </remarks>
    private void ApplyView()
    {
        Cards.ItemTemplate = ViewModel.View switch
        {
            LibraryView.Thumb => (DataTemplate)Resources["ThumbTemplate"],
            LibraryView.List => (DataTemplate)Resources["ListTemplate"],
            _ => (DataTemplate)Resources["PosterTemplate"]
        };

        Cards.Layout = ViewModel.View == LibraryView.List ? (Layout)Resources["ListLayout"] : CardLayout;

        foreach (var item in ViewMenu.Items.OfType<RadioMenuFlyoutItem>())
            item.IsChecked = item.Tag is string name && Enum.TryParse<LibraryView>(name, out var view) && view == ViewModel.View;
    }
}
