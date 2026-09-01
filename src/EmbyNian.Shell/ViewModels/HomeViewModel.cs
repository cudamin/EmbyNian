using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Services;
using EmbyNian.Shell.Views;
using Microsoft.UI.Xaml;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// The home page: 继续观看, 媒体库, 接下来看, 最近添加, 加上每个媒体库自己那一排最近添加 —— each from its own
/// source. 排哪几排、什么次序、哪几排显示，由设置里那份版面说（<see cref="HomeLayout"/>，「新增页里拖拽决定这些
/// 列表的顺序，勾选显示或者不勾选取消显示」）。
/// <para>
/// The server rows go out together and are reported on separately. They fail independently on a
/// real server — 接下来看 is empty for an account that only watches films, and some endpoints are
/// administrator-only — so one of them returning nothing must not cost the page the others.
/// </para>
/// <para>
/// 媒体库 costs no request at all: the shell already read the account's view list once to fill the
/// navigation pane, and this row is that same list drawn as cards. It is the one row that is always
/// there, even for an account that has never played anything — which is why it stays on the page even
/// though 继续观看 now goes first（「把图中的媒体库和继续观看位置调换」）: 上次停在哪儿是一个用过的账号打开
/// 主页最想看见的一句话，而媒体库那一排在侧边栏里另有一份。
/// </para>
/// <para>
/// 勾掉的那一排连请求一起省掉：媒体库那几排各是一次「这个库的最近添加」，不看的库不该每次开主页都问一遍。
/// </para>
/// </summary>
public sealed partial class HomeViewModel : PageViewModel
{
    private const string Category = "主页";

    /// <summary>How many items each of the two 「what you were doing」 rows asks for.</summary>
    private const int ShelfSize = 12;

    /// <summary>最近添加 gets more, because posters are narrower than stills and the row looks thin.</summary>
    private const int LatestSize = 24;

    /// <summary>
    /// <see cref="LoadedCount"/> before the first load. The self-check already speaks this language —
    /// it reports a negative count as 未读取 — and it keeps 「nothing came back」 distinct from
    /// 「nothing has been asked yet」, which is the difference between an empty server and a slow one.
    /// </summary>
    private const int NotLoaded = -1;

    private EmbySession? _session;
    private IShellActions? _actions;
    private EmbyImageStore? _images;
    private ISettingsService? _settings;
    private IReadOnlyList<EmbyItem> _libraryViews = [];

    /// <summary>卡片尺寸和角标那三个数，<see cref="Attach"/> 时取一次的快照 —— 见那一段的说明。</summary>
    private int _poster;
    private int _still;
    private bool _badges;

    /// <summary>
    /// 这一次要排哪几排、什么次序、哪几排显示（<see cref="HomeLayout.Plan"/>），和它们各自那一排。
    /// 每次 <see cref="Attach"/> 和每次版面改过（<see cref="ApplyLayoutAsync"/>）都重建：设置里改的卡片尺寸和
    /// 拖出来的新次序都要在这里落地。
    /// </summary>
    private (HomeRowPlan Row, CardShelf Shelf)[] _all = [];

    public HomeViewModel()
    {
        LoadedCount = NotLoaded;
        Slides = [];
    }

    /// <summary>
    /// The rows that have something in them. Only the non-empty ones, rather than all four with the
    /// empty ones collapsed: an <c>ItemsRepeater</c> still spends its <c>StackLayout</c> spacing on a
    /// collapsed item, so an account with no 接下来看 would get a blank gap where the row would be.
    /// </summary>
    public ObservableCollection<CardShelf> Shelves { get; } = [];

    /// <summary>
    /// 需求 5 的那条大图轮播（<see cref="HomeCarousel"/>）: the first few of the same three server rows,
    /// drawn as one full-width backdrop each instead of a card.
    /// <para>
    /// A whole new list per load rather than a collection edited in place. The band swaps its slides as
    /// one thing — old artwork released, index back to the first, dots rebuilt — and an
    /// <c>ObservableCollection</c> cleared and refilled would put it through that eight times in a row,
    /// once per <c>Add</c>, restarting the fade each time.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroFilled))]
    public partial IReadOnlyList<BannerSlide> Slides { get; set; }

    /// <summary>
    /// 顶上那一整块到底是不是一张图。页眉压在图上时要换成 <c>EgOnScrim</c> 那套墨色（见
    /// <see cref="Views.PageSlate.OnScrim"/>）；一张幻灯片也没有的时候那一块是页面自己的底色，跟主题走的
    /// 墨色才读得清 —— 白字配浅色主题的浅底等于没有字。
    /// </summary>
    public bool HeroFilled => Slides.Count > 0;

    /// <summary>Which server, and how much of it is on the page.</summary>
    [ObservableProperty]
    public partial string? Subheading { get; set; }

    /// <summary>
    /// How many cards the server's own rows produced — 媒体库 excluded, because it is this account's
    /// shelf list rather than anything on it. What the self-check counts to prove real data arrived.
    /// </summary>
    [ObservableProperty]
    public partial int LoadedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    public partial bool ShowEmptyNotice { get; set; }

    public Visibility EmptyVisibility => Show(ShowEmptyNotice);

    /// <summary>
    /// Handed the shell and the three capabilities this page draws from, once per navigation to it.
    /// <para>
    /// <paramref name="settings"/> is read here and not kept: the card sizes are a snapshot per
    /// navigation, on purpose (see <see cref="_all"/>). The session and the image store are kept — every
    /// row's request goes through the one, and the carousel builds a slide per load out of the other.
    /// </para>
    /// </summary>
    internal void Attach(
        IShellActions actions,
        IReadOnlyList<EmbyItem> libraryViews,
        ISettingsService settings,
        EmbySession session,
        EmbyImageStore images)
    {
        _actions = actions;
        _libraryViews = libraryViews;
        _session = session;
        _images = images;
        _settings = settings;

        var ui = settings.Settings.Ui;

        // Clamped again here rather than trusted: the settings row offers 120–300 and the migration
        // clamps to 120–340, but a hand-edited settings.json is a supported way to configure this app
        // and a 4000px decode width is one poster the size of the screen.
        _poster = Math.Clamp(ui.PosterWidth, 120, 340);
        _still = CardSize.WideFor(_poster);
        _badges = ui.ShowWatchedIndicators;

        BuildShelves();
    }

    /// <summary>
    /// 按存档里那份版面（<see cref="HomeLayout.Plan"/>）造出这一次的每一排。归一化之后的版面顺手写回设置文件 ——
    /// 服务器上新加的媒体库因此自己排到末尾、删掉的自己消失、改了名的把新名字带回去，而拖拽那张表读的就是这一份。
    /// </summary>
    private void BuildShelves()
    {
        if (_images is not { } images) return;

        var ui = _settings?.Settings.Ui;
        var plan = HomeLayout.Plan(ui?.HomeRows, _libraryViews);

        if (ui is not null && !HomeLayout.Same(ui.HomeRows, plan))
        {
            ui.HomeRows = HomeLayout.Save(plan);
            _settings?.Save();
        }

        // 继续观看、媒体库、接下来看走 16:9 的宽卡（那三排讲的是「你在看的那一格画面」）；最近添加和每个媒体库
        // 自己那一排走海报 —— 一整排新片的封面比一整排剧照读得快。媒体库那一排不上角标：一个库没有「已看」。
        _all = [.. plan.Select(row =>
        {
            var wide = row.Key is HomeLayout.Resume or HomeLayout.Libraries or HomeLayout.NextUp;
            var width = wide ? _still : _poster;
            var badges = row.Key != HomeLayout.Libraries && _badges;

            return (row, new CardShelf(row.Title, images, width, wide, badges));
        })];
    }

    /// <summary>
    /// 版面改过了（拖拽排序、或者勾掉了一排）：重建每一排再读一遍。设置窗口那一头改完喊一声
    /// （<c>ShellPrefs</c>），主页这一头照这句话重排 ——「改完要重启才算」的设置读起来就是坏的。
    /// </summary>
    internal Task ApplyLayoutAsync()
    {
        BuildShelves();
        return LoadAsync();
    }

    public override Task ReloadAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        if (_session is null || _actions is null || _all.Length == 0) return;

        var token = BeginLoad();
        Subheading = "正在读取…";

        // Started together rather than awaited one after another: a round trip per row in sequence is
        // a page that takes a second per shelf, and none of them depends on another's answer.
        var resume = FetchAsync("继续观看", (client, ct) => client.GetResumeAsync(ShelfSize, ct), token);
        var nextUp = FetchAsync("接下来看", (client, ct) => client.GetNextUpAsync(ShelfSize, ct), token);
        var latest = FetchAsync("最近添加", (client, ct) => client.GetLatestAsync(null, LatestSize, ct), token);

        // 媒体库那几排各问一次自己那个库的最近添加。只问勾着的那几排 —— 勾掉一排就是连这次请求一起省掉。
        var perLibrary = new Dictionary<string, Task<List<EmbyItem>>>(StringComparer.Ordinal);
        foreach (var (row, _) in _all)
        {
            if (!row.Visible || HomeLayout.LibraryId(row.Key) is not { Length: > 0 } id) continue;

            perLibrary[row.Key] = FetchAsync(
                row.Title, (client, ct) => client.GetLatestAsync(id, LatestSize, ct), token);
        }

        await Task.WhenAll(perLibrary.Values.Append(resume).Append(nextUp).Append(latest)).ConfigureAwait(true);

        if (!IsCurrent(token)) return;

        // 最近添加 spans every library this account has, 继续观看 and 接下来看 every item it has touched —
        // music included in all three. This app has no music face, so a track card would open onto
        // nothing; the rows are filtered here rather than by asking the server, because Emby's own
        // 「latest」 endpoint takes one parent id and these three take none.
        var resumed = WithoutMusic(resume.Result);
        var next = WithoutMusic(nextUp.Result);
        var added = WithoutMusic(latest.Result);

        // 每一排装什么由它那把钥匙说，不再是「数组第几个」—— 次序现在是用户拖出来的，位置说明不了任何事。
        foreach (var (row, shelf) in _all)
        {
            if (!row.Visible)
            {
                shelf.Clear();
                continue;
            }

            shelf.Fill(row.Key switch
            {
                HomeLayout.Resume => resumed,
                HomeLayout.Libraries => _libraryViews,
                HomeLayout.NextUp => next,
                HomeLayout.Latest => added,
                _ => perLibrary.TryGetValue(row.Key, out var library) ? WithoutMusic(library.Result) : []
            });
        }

        Shelves.Clear();
        foreach (var (row, shelf) in _all)
            if (row.Visible && shelf.Cards.Count > 0) Shelves.Add(shelf);

        // 第一排压在大图上（大图占满第一屏，这一排坐在它的下半截上，见 HomePage.SyncGlass），所以它的牌子和卡片
        // 底下那两行字要换成压在图上那套浅墨；后面几排在图的下沿外面，照旧走主题的墨。按位置定而不是按名字定：
        // 哪一排排在最前面取决于这个账号有没有继续观看，而压着图的永远是最前面那一排。
        for (var index = 0; index < Shelves.Count; index++)
            Shelves[index].OnScrim = index == 0;

        // 需求 5：轮播站在这三行的头几个条目上，不额外问服务器一次 —— 「最近添加」的第一张剧照就是这条带的第
        // 一张幻灯片，而它已经在手上了。哪几个上得了台是 HomeCarousel 的事（要有宽图、一个剧集只占一张）。
        Slides = BuildSlides(resumed, next, added);

        LoadedCount = resumed.Count + next.Count + added.Count;

        // Only when there is nothing at all. An account whose libraries are listed but which has never
        // played anything is not an empty account, and telling it so under a row of its own libraries
        // would be plainly untrue.
        ShowEmptyNotice = Shelves.Count == 0;

        Subheading = LoadedCount == 0
            ? _session.ServerDisplayName
            : $"{_session.ServerDisplayName}  ·  {LoadedCount} 项";

        EndLoad(token);
    }

    /// <summary>
    /// 自检：这一次的版面 ——「继续观看✓、媒体库✓、最近添加 · 电影✗」。屏上那几排（<see cref="Shelves"/>）只有
    /// 装到了东西的才在，所以这一句是唯一能看出「勾掉的那一排真的没排」和「拖出来的次序真的生效了」的地方。
    /// </summary>
    internal string LayoutSummary => _all.Length == 0
        ? "未读取"
        : string.Join("、", _all.Select(entry =>
            $"{entry.Row.Title}{(entry.Row.Visible ? "✓" : "✗")}{(entry.Shelf.Cards.Count > 0 ? "" : "（空）")}"));

    /// <summary>
    /// 自检：屏上那几排真按版面来的 —— 次序一样、勾掉的那几排真没排。两边各算一次再比：版面那一份是设置文件说的
    /// （<see cref="HomeLayout.Plan"/>），屏上那一份是 <see cref="Shelves"/>，中间隔着「装到了东西才排」这一条。
    /// </summary>
    internal (bool Ok, string Detail) LayoutRead()
    {
        var wanted = _all
            .Where(entry => entry.Row.Visible && entry.Shelf.Cards.Count > 0)
            .Select(entry => entry.Row.Title)
            .ToList();

        var onScreen = Shelves.Select(shelf => shelf.Title).ToList();
        var ok = _all.Length > 0 && wanted.SequenceEqual(onScreen, StringComparer.Ordinal);

        return (ok, $"版面 {LayoutSummary}；屏上 {(onScreen.Count == 0 ? "无" : string.Join('、', onScreen))}");
    }

    /// <summary>
    /// The same rows without the music ones; see <see cref="EmbyItemType.IsMusic"/> for why they are
    /// hidden rather than shown and left to disappoint.
    /// </summary>
    private static List<EmbyItem> WithoutMusic(List<EmbyItem> items) =>
        items.Where(item => !EmbyItemType.IsMusic(item.Type)).ToList();

    /// <summary>
    /// The carousel's slides, out of the three rows this page already has. Empty until the page has been
    /// attached: a slide holds artwork, and there is nothing to fetch it with before then.
    /// </summary>
    private IReadOnlyList<BannerSlide> BuildSlides(
        IReadOnlyList<EmbyItem> resumed,
        IReadOnlyList<EmbyItem> next,
        IReadOnlyList<EmbyItem> added) => _images is not { } images
        ? []
        : [.. HomeCarousel.Slides(resumed, next, added).Select(item => new BannerSlide(item, images))];

    /// <summary>
    /// 播放 on the carousel. Straight to the shell with no parent and no sibling list, same as a card on
    /// this page: a slide is one item out of three unrelated rows, so there is no 选集 it belongs to.
    /// </summary>
    internal Task PlayAsync(BannerSlide slide) =>
        _actions is null ? Task.CompletedTask : _actions.PlayAsync(slide.Item);

    /// <summary>详情 on the carousel, and the same routing decision as <see cref="Open(CardItem)"/>.</summary>
    internal void Open(BannerSlide slide) => _actions?.OpenItem(slide.Item);

    /// <summary>
    /// 自检: how many slides the band got, and what artwork the items behind them actually have
    /// (<see cref="ItemArtwork.Census"/>). The census is the empirical half — 主页轮播 stands on 背景图,
    /// an episode almost never has one of its own, and whether this server hands down its series'
    /// backdrop is a question only a real account can answer.
    /// </summary>
    internal string BannerSummary => LoadedCount < 0
        ? "未读取"
        : Slides.Count == 0
            ? "0 张（三行里没有带宽图的条目）"
            : $"{Slides.Count} 张，{ItemArtwork.Census([.. Slides.Select(slide => slide.Item)])}";

    /// <summary>
    /// Where a card goes when it is clicked.
    /// <para>
    /// Everything that is not a library defers to <see cref="ShellPage.OpenItem"/>, which is the one
    /// place the 「detail page or another grid」 choice is made. This page used to make that choice
    /// again for itself and get it wrong: it sent a series or a season straight to a child grid, so
    /// clicking a series on the home page skipped the series page — the synopsis, the cast and the
    /// episode list — and dropped the user into a bare grid of season folders.
    /// </para>
    /// </summary>
    internal void Open(CardItem card)
    {
        if (_actions is null) return;

        // A library's own card routes through the pane entry that opens it, so the pane highlight and
        // the breadcrumb end up exactly where clicking it in the pane would have put them.
        if (card.Item.Type is EmbyItemType.CollectionFolder && _actions.TryOpenLibrary(card.Item.Id)) return;

        _actions.OpenItem(card.Item);
    }

    /// <summary>
    /// One row's worth of items, with its own failure kept to itself. Returns an empty list rather than
    /// throwing, so <c>Task.WhenAll</c> above never has to decide which failure won.
    /// </summary>
    private async Task<List<EmbyItem>> FetchAsync(
        string what,
        Func<EmbyClient, CancellationToken, Task<List<EmbyItem>>> operation,
        CancellationToken token)
    {
        try
        {
            return await _session!.ExecuteAsync(operation, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception error)
        {
            Log.Warn(Category, $"读取{what}失败", error);

            // First failure wins the bar: three stacked messages saying the server is unreachable is
            // two more than anyone needs, and the other two are in the log either way.
            if (IsCurrent(token) && !NoticeOpen) Report(what, error);

            return [];
        }
    }
}
