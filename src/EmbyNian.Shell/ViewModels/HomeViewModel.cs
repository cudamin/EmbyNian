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
/// The home page: 继续观看, 媒体库, 接下来看, 加上每个媒体库自己那一排最近添加 —— each from its own
/// source. 排哪几排、什么次序、哪几排显示，由设置里那份版面说（<see cref="HomeLayout"/>，「新增页里拖拽决定这些
/// 列表的顺序，勾选显示或者不勾选取消显示」）。整个服务器的「最近添加」那一排 2026-09-12 去掉了：
/// 「去掉最近添加，保留最近添加 电视节目、最近添加 电影」。
/// <para>
/// The server rows go out together and are reported on separately. They fail independently on a
/// real server — 接下来看 is empty for an account that only watches films, and some endpoints are
/// administrator-only — so one of them returning nothing must not cost the page the others.
/// </para>
/// <para>
/// 媒体库 costs no request at all: the shell already read the account's view list once, and this row is
/// that same list drawn as cards. It is the one row that is always there, even for an account that has
/// never played anything — and since the top tab strip was removed (2026-09-08), clicking one of these
/// cards is how a library is opened, so it earns its place on the page.
/// </para>
/// <para>
/// 勾掉的那一排连请求一起省掉：媒体库那几排各是一次「这个库的最近添加」，不看的库不该每次开主页都问一遍。
/// </para>
/// <para>
/// 设置 → 界面 → 「显示主页轮播大图」关掉之后这一页就是一叠普通的货架（<see cref="_banner"/>）：顶上那张大图
/// 没有了，所有内容都横着排、按各自在版面表上的位置站着。
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

    /// <summary>角标那个开关，<see cref="Attach"/> 时取一次的快照 —— 见那一段的说明。卡片尺寸从前也在这张
    /// 快照上（<c>_poster</c>/<c>_still</c>），2026-09-05「海报宽度」那行设置删掉之后就只剩这一个了。</summary>
    private bool _badges;

    /// <summary>
    /// 设置 → 界面 → 「显示主页轮播大图」（<c>UiSettings.ShowHomeBanner</c>）。关掉的时候顶上那张大图没有：
    /// 一张幻灯片都不造（<see cref="Slides"/> 空着，带子自己就收起来了），页面就是一叠横着排的货架。
    /// <para>
    /// 每次 <see cref="BuildShelves"/> 重读，而不是 <see cref="Attach"/> 时取一次快照 —— 拖拽次序和勾选是
    /// 改完当场生效的（<c>ShellPrefs</c>），这一项和它们同一张表上的东西，「改完要重开这一页才算」读起来就是坏的。
    /// 卡片尺寸那三个数不同：它们决定卡片按多宽解码，见 <see cref="_all"/>。
    /// </para>
    /// </summary>
    private bool _banner = true;

    /// <summary>
    /// 这一次要排哪几排、什么次序、哪几排显示（<see cref="HomeLayout.Plan"/>），和它们各自那一排。
    /// 每次 <see cref="Attach"/> 和每次版面改过（<see cref="ApplyLayoutAsync"/>）都重建：拖出来的新次序要在这里落地。
    /// </summary>
    private (HomeRowPlan Row, CardShelf Shelf)[] _all = [];

    /// <summary>
    /// 矮窗档开没开：开了就是把 <see cref="LibraryShelf"/> 从 <see cref="Shelves"/> 里摘掉、让它压到轮播左下角，
    /// 摘掉之后横排里它那个位置由下面那一排补上。判定（量窗口、量货架）在 <c>HomePage</c>，这一头只管执行和记状态
    /// —— 重载重建 <see cref="Shelves"/> 之后由 <see cref="ApplyLibraryOverlay"/> 原样恢复，重载前后不跳档。
    /// </summary>
    private bool _libraryOnBanner;

    /// <summary>矮窗档现在开没开。读数用（自检的版面比对、货号摘要要说清媒体库去了哪儿）。</summary>
    internal bool LibraryOnBanner => _libraryOnBanner;

    /// <summary>
    /// 刚刚那次 <see cref="Shelves"/> 的增删是矮窗档自己搬的（<see cref="ApplyLibraryOverlay"/> 里的搬进搬出），
    /// 不是重新装货。页面据此**不排重查**。
    /// <para>
    /// 少了这一格就是自己喂自己：翻档 → 集合变化 → 页面排一次重查 → 重查又翻档。2026-09-18 两场、09-21 一场
    /// 「矮窗档来回翻」都是这个环在滚，最后那场刷了三万行日志、把界面线程吃干二十七秒（见 <c>HomePage</c> 的
    /// <c>Shelves.CollectionChanged</c>）。
    /// </para>
    /// <para>
    /// 重建货架（<see cref="BuildShelves"/> 里那次 <c>Clear</c> 与逐个 <c>Add</c>）不落在这个记号里 —— 那才是
    /// 页面该重查的「货架集合真变了」。
    /// </para>
    /// </summary>
    internal bool ShelvesChangeIsOverlay { get; private set; }

    /// <summary>
    /// 媒体库那一排（版面钥匙 <see cref="HomeLayout.Libraries"/>），装到了东西才记 —— 勾掉或者空的账号没有这一排，
    /// 矮窗档整个不参与。它和 <see cref="LibraryFlowIndex"/> 都是每次 <see cref="LoadAsync"/> 重建横排时落定的。
    /// </summary>
    internal CardShelf? LibraryShelf { get; private set; }

    /// <summary>
    /// <see cref="LibraryShelf"/> 在横排里的位置（默认摆法，不是压上档的）。压上档要回默认时按它插回去，插错了
    /// 次序就是「拖拽版面被窗口高度改写」。
    /// </summary>
    internal int LibraryFlowIndex { get; private set; } = -1;

    /// <summary>
    /// 视图（量完几何之后）对矮窗档下的判断。开、关都走 <see cref="ApplyLibraryOverlay"/> 搬那一排；没有媒体库
    /// 那一排时开不了。
    /// </summary>
    internal void SetLibraryOverlay(bool on)
    {
        if (_libraryOnBanner == on) return;
        if (on && LibraryShelf is null) return;

        _libraryOnBanner = on;
        ApplyLibraryOverlay();
    }

    /// <summary>
    /// 按当前开关把媒体库那一排搬进搬出横排。重载（<see cref="LoadAsync"/> 重建 <see cref="Shelves"/>）之后也走
    /// 这一句恢复原状 —— 开着的时候重建出来的那一排立刻再摘掉，屏上不出现「先回横排再压回去」的一跳。
    /// </summary>
    private void ApplyLibraryOverlay()
    {
        if (LibraryShelf is not { } shelf)
        {
            _libraryOnBanner = false;
            return;
        }

        // 搬的时候挂记号：这一下集合变化是翻档自己造成的，页面据此不排重查（见 ShelvesChangeIsOverlay）。
        ShelvesChangeIsOverlay = true;
        try
        {
            if (_libraryOnBanner)
            {
                if (Shelves.Contains(shelf)) Shelves.Remove(shelf);
            }
            else if (!Shelves.Contains(shelf) && LibraryFlowIndex >= 0)
            {
                Shelves.Insert(Math.Min(LibraryFlowIndex, Shelves.Count), shelf);
            }
        }
        finally
        {
            ShelvesChangeIsOverlay = false;
        }
    }

    public HomeViewModel()
    {
        LoadedCount = NotLoaded;
        Slides = [];
    }

    /// <summary>
    /// 横着排的那几排，只留装到了东西的。Only the non-empty ones, rather than all four with the
    /// empty ones collapsed: an <c>ItemsRepeater</c> still spends its <c>StackLayout</c> spacing on a
    /// collapsed item, so an account with no 接下来看 would get a blank gap where the row would be.
    /// </summary>
    public ObservableCollection<CardShelf> Shelves { get; } = [];

    /// <summary>
    /// 需求 5 的那条大图轮播（<see cref="HomeCarousel"/>）：设置里那个来源（最近添加或随机）、那一类媒体
    /// （全部、电影或剧集）的头几张，each drawn as one full-width backdrop instead of a card.
    /// 2026-09-13「轮播图改用前十个最近添加」之后继续观看不再参加，同日下午来源、媒体、张数三样进了设置。
    /// 设置里关掉轮播（<see cref="_banner"/>）时这里是空的，带子自己就收起来。
    /// <para>
    /// A whole new list per load rather than a collection edited in place. The band swaps its slides as
    /// one thing — old artwork released, index back to the first, dots rebuilt — and an
    /// <c>ObservableCollection</c> cleared and refilled would put it through that eight times in a row,
    /// once per <c>Add</c>, restarting the fade each time.
    /// </para>
    /// <para>
    /// 从前这里还挂着一个 <c>HeroFilled</c>（「顶上那块是不是一张图」，页眉和标题栏墨色的联动靠它）—— 2026-09-10
    /// 「框成卡片」之后页眉和标题栏都不再压在图上，联动删了，它跟着删。
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<BannerSlide> Slides { get; set; }

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

        // 卡片尺寸不再来自设置（「海报宽度」那一行 2026-09-05 删掉了，卡片固定走 CardSize 的默认档），
        // 快照上只剩角标这一个真正的设置读数。
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

        _banner = ui?.ShowHomeBanner ?? true;

        if (ui is not null && !HomeLayout.Same(ui.HomeRows, plan))
        {
            ui.HomeRows = HomeLayout.Save(plan);
            _settings?.Save();
        }

        // 继续观看、媒体库、接下来看走 16:9 的宽卡（那三排讲的是「你在看的那一格画面」）；每个媒体库自己那一排
        // 走海报 —— 一整排新片的封面比一整排剧照读得快。媒体库那一排不上角标：一个库没有「已看」。
        // 媒体库现在也是横着排的普通一排（2026-09-08「移除轮播图右边的媒体库」之后，右边那一列没了），站在页面
        // 自己的纸上、走主题的墨（OnScrim 默认关）。点它的卡进库 —— 顶部标签栏删掉之后，这一排就是进各媒体库的入口。
        // 媒体库那一排比别的宽卡窄一档（2026-09-13「参考上图缩小媒体库图标的大小」，比例记在
        // CardSize.LibraryWidth 上）：一排「进哪座库」的入口格子，不需要剧照那么大。形状和宽度从一个 switch
        // 里出，两边分头写就会有一天各说各话。
        // 继续观看 / 接下来看 那一档 2026-09-14 从 CardSize.WideWidth(300) 收到 CardSize.HomeWideWidth(256)
        // （「缩小首页中的媒体库卡片和继续观看卡片，调整其尺寸使其更紧凑，同时保持布局对齐、间距协调以及各屏幕
        // 尺寸下的响应式显示效果」）—— 主页单独一档，不动 WideWidth，详情页的 更多单集 和媒体库网格那两处照旧。
        _all = [.. plan.Select(row =>
        {
            var (wide, width) = row.Key switch
            {
                HomeLayout.Libraries => (true, CardSize.LibraryWidth),
                HomeLayout.Resume or HomeLayout.NextUp => (true, CardSize.HomeWideWidth),
                _ => (false, CardSize.PosterWidth)
            };
            var badges = row.Key != HomeLayout.Libraries && _badges;

            // 这一排点进哪儿由版面说了算（HomeLayout.TargetOf）：库那几排认库，继续观看 / 接下来看各有
            // 自己的一页，详情页造出来的那些排是 None —— 牌子右端跟不跟大于号就看它。2026-09-14 之前这里
            // 只递一个库 id，于是那两排没有 id、牌子一直是死的。
            return (row, new CardShelf(
                row.Title, images, width, wide, badges,
                HomeLayout.LibraryId(row.Key), HomeLayout.TargetOf(row.Key)));
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

    /// <summary>播放只改变观看状态，不重建轮播，以免返回时丢掉已解码背景并重置轮播位置。</summary>
    internal Task RefreshPlaybackAsync() => LoadAsync(refreshSlides: false);

    private async Task LoadAsync(bool refreshSlides = true)
    {
        if (_session is null || _actions is null || _all.Length == 0) return;

        var token = BeginLoad();
        Subheading = "正在读取…";

        // Started together rather than awaited one after another: a round trip per row in sequence is
        // a page that takes a second per shelf, and none of them depends on another's answer.
        var resume = FetchAsync("继续观看", (client, ct) => client.GetResumeAsync(ShelfSize, ct), token);
        var nextUp = FetchAsync("接下来看", (client, ct) => client.GetNextUpAsync(ShelfSize, ct), token);

        // 轮播自己的那一次请求（2026-09-13「轮播图改用前十个最近添加」；同日补上设置里的四行 ——
        // 「新增在设置中设置轮播图要使用什么媒体，和要使用最近添加还是随机的还有数量的选项。还有封面的
        // 轮换的秒数」）。来源和媒体类型照设置走：最近添加走 /Items/Latest（不带 parentId，和主页那几排
        // 的勾选、次序互不相干），随机走 /Items SortBy=Random。请求条数跟着设置里的张数放大 —— 筛掉没有
        // 宽图、并掉同一部剧之后还得凑得出那个数。
        var carouselUi = _settings?.Settings.Ui;
        var carouselCount = Emby.HomeCarousel.ClampSlots(
            carouselUi?.CarouselCount ?? Emby.HomeCarousel.Slots);
        var carouselMedia = carouselUi?.CarouselMedia ?? Emby.CarouselMediaType.All;
        var latestSize = Math.Max(LatestSize, carouselCount * 3);

        var latest = refreshSlides ? FetchAsync("轮播", (client, ct) =>
            carouselUi is not null && carouselUi.CarouselSource == Emby.CarouselSource.Random
                ? client.GetRandomAsync(latestSize, Emby.HomeCarousel.RandomTypes(carouselMedia), ct)
                : client.GetLatestAsync(
                    null,
                    latestSize,
                    ct,
                    Emby.HomeCarousel.LatestTypes(carouselMedia) is { Count: > 0 } types
                        ? string.Join(',', types)
                        : null),
            token) : Task.FromResult(new List<EmbyItem>());

        // 媒体库那几排各问一次自己那个库的最近添加。只问勾着的那几排 —— 勾掉一排就是连这次请求一起省掉。
        // 整个服务器的「最近添加」那一排 2026-09-12 从版面上退役了，但它没有断请求 —— 轮播 2026-09-13 起
        // 单独问一次（见上面 latest）。
        var perLibrary = new Dictionary<string, Task<List<EmbyItem>>>(StringComparer.Ordinal);
        foreach (var (row, _) in _all)
        {
            if (!row.Visible || HomeLayout.LibraryId(row.Key) is not { Length: > 0 } id) continue;

            perLibrary[row.Key] = FetchAsync(
                row.Title, (client, ct) => client.GetLatestAsync(id, LatestSize, ct), token);
        }

        await Task.WhenAll(perLibrary.Values.Append(resume).Append(nextUp).Append(latest)).ConfigureAwait(true);

        if (!IsCurrent(token)) return;

        // 继续观看 and 接下来看 span every item the account has touched — music included. This app has no
        // music face, so a track card would open onto nothing; the rows are filtered here rather than by
        // asking the server, because Emby's own endpoints for these two take no type filter. Each library
        // row's own latest list is filtered the same way, at Items below.
        var resumed = WithoutMusic(resume.Result);
        var next = WithoutMusic(nextUp.Result);

        // 轮播那份（见上面 latest）同样滤掉音乐类：这条带只站电影和剧集。「全部媒体」那一档音乐从来
        // 进不了最近添加的前排，这层滤网在；「随机」那一档请求侧就没有音乐，它白跑一遍。
        var newest = WithoutMusic(latest.Result);

        // 一排装什么由它那把钥匙说，不再是「数组第几个」—— 次序现在是用户拖出来的，位置说明不了任何事。
        // 轮播不在这张表里：它认的是 latest 那一次请求，跟这几排谁勾谁不勾、拖成什么次序都没有关系。
        IReadOnlyList<EmbyItem> Items(string key) => key switch
        {
            HomeLayout.Resume => resumed,
            HomeLayout.Libraries => _libraryViews,
            HomeLayout.NextUp => next,
            _ => perLibrary.TryGetValue(key, out var library) ? WithoutMusic(library.Result) : []
        };

        // 各媒体库自己那排按版面次序连成的一串。从前轮播的补位从这儿来，2026-09-13 轮播改用整服最近添加
        // （见上面 latest）之后，这一串只剩一个读者：LoadedCount 的那个数。
        List<EmbyItem> added = [.. _all
            .Where(entry => HomeLayout.LibraryId(entry.Row.Key) is not null)
            .SelectMany(entry => Items(entry.Row.Key))];

        foreach (var (row, shelf) in _all)
        {
            if (!row.Visible)
            {
                shelf.Clear();
                continue;
            }

            // 媒体库那一排的第二行空着：一个库自己的 CardSubtitle 就是「媒体库」三个字，而那正是这一排牌子上写的
            // 词（同 CardItem 那个 subtitle 参数上写的演职人员那条 —— 一整排卡片底下重复一遍排名，一个字的信息
            // 都不多）。空串而不是 null：null 是「按条目自己算」。
            shelf.Fill(Items(row.Key), row.Key == HomeLayout.Libraries ? _ => "" : null);
        }

        // 装到了东西的排一律横着排。媒体库也在其中 —— 2026-09-08「移除轮播图右边的媒体库」之后不再单挑出来当
        // 右栏，就跟别的几排一样站在它自己在版面表上的位置上。
        Shelves.Clear();
        LibraryShelf = null;
        LibraryFlowIndex = -1;

        foreach (var (row, shelf) in _all)
        {
            if (!row.Visible || shelf.Cards.Count == 0) continue;

            // 媒体库那一排记下自己和位置：矮窗档（<see cref="SetLibraryOverlay"/>）搬的就是它，回默认档时按
            // 这个位置插回去。勾掉或者空的账号没有这一排，两个都空着，那一档整个不参与。
            if (row.Key == HomeLayout.Libraries)
            {
                LibraryShelf = shelf;
                LibraryFlowIndex = Shelves.Count;
            }

            Shelves.Add(shelf);
        }

        // 矮窗档跨重载不跳：上一趟压在轮播上的，这一趟重建完立刻再摘掉（<see cref="ApplyLibraryOverlay"/>）。
        ApplyLibraryOverlay();

        // 需求 5 → 2026-09-13「轮播图改用前十个最近添加」，同日下午张数进了设置：轮播不再站在继续观看上，
        // 只站设置里那个来源发回来的头若干张（筛掉没有宽图、并掉同一部剧，是 HomeCarousel 的事）。关掉轮播
        // 就一张都不造，带子自己收起来。
        if (refreshSlides) Slides = _banner ? BuildSlides(newest, carouselCount) : [];

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
    /// 自检：这一次的版面 ——「轮播开；继续观看✓›、媒体库✓、最近添加 · 电影✗」。屏上那几排
    /// （<see cref="Shelves"/>）只有装到了东西的才在，所以这一句是唯一能看出「勾掉的那一排真的没排」和「拖出来的
    /// 次序真的生效了」的地方。轮播那个开关也报在这儿：它只管顶上那张大图在不在，不再动媒体库的位置。
    /// <para>
    /// 标题后面那个 › 是「这一排的牌子点得动」—— 2026-09-14「新增点击图中红框的标题可以进入对应的页面」之后
    /// 「继续观看」也该带一个，所以它必须能从这一行读出来：从前这一句只报标题，那一排到底有没有去处，报告里
    /// 一个字也看不出来。
    /// </para>
    /// </summary>
    internal string LayoutSummary => _all.Length == 0
        ? "未读取"
        : $"轮播{(_banner ? "开" : "关")}；" + string.Join("、", _all.Select(entry =>
            $"{entry.Row.Title}{(entry.Row.Visible ? "✓" : "✗")}"
                + $"{(entry.Shelf.CanOpen ? "›" : "")}"
                + $"{(entry.Shelf.Cards.Count > 0 ? "" : "（空）")}"));

    /// <summary>
    /// 自检：屏上那几排真按版面来的 —— 次序一样、勾掉的那几排真没排。两边各算一次再比：版面那一份是设置文件说的
    /// （<see cref="HomeLayout.Plan"/>），屏上那一份是 <see cref="Shelves"/>，中间隔着「装到了东西才排」这一条。
    /// 媒体库 2026-09-08 从右栏改回横排之后，它和别的排一样参加比对，没有单独一档。
    /// <para>
    /// 矮窗档（<see cref="LibraryOnBanner"/>）是唯一的例外：媒体库那一排真在屏上，只是压在轮播左下角、不在横排
    /// 里 —— 这是这一页对矮窗口的安排，不是版面丢了哪一排。比对时按**钥匙**把它从期望里摘掉（按标题摘，撞上一个
    /// 恰好叫「媒体库」的媒体库名就比对不出真丢了排的那天），读数里单独交代它去了哪儿。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) LayoutRead()
    {
        var wanted = _all
            .Where(entry => entry.Row.Visible && entry.Shelf.Cards.Count > 0)
            .Select(entry => (entry.Row.Key, entry.Row.Title))
            .Where(entry => !_libraryOnBanner || LibraryShelf is null || entry.Key != HomeLayout.Libraries)
            .Select(entry => entry.Title)
            .ToList();

        var onScreen = Shelves.Select(shelf => shelf.Title).ToList();

        // 牌子上那个大于号该有的排：库那几排各有各的库，加上 2026-09-14 起接通的「继续观看」/「接下来看」。
        // 详情页那几排也会走到这儿（同一个 CardShelf），它们的目标是 None，约定上不在这一页上，所以按标题
        // 对不上也无所谓 —— 这一句问的是「这一页该有牌子的排，牌子是不是真有」。
        var opensWanted = _all
            .Where(entry => HomeLayout.TargetOf(entry.Row.Key) != HomeLayout.HomeRowTarget.None)
            .Select(entry => entry.Row.Title)
            .ToList();
        var opensMissing = opensWanted.Where(title => !CardOpen(title)).ToList();

        var ok = _all.Length > 0
            && wanted.SequenceEqual(onScreen, StringComparer.Ordinal)
            && opensMissing.Count == 0;

        return (ok, $"版面 {LayoutSummary}；横着排的 {(onScreen.Count == 0 ? "无" : string.Join('、', onScreen))}"
            + $"{(_all.Length == 0 ? "" : $"，该带大于号的 {opensWanted.Count} 排，没带的 {(opensMissing.Count == 0 ? "0" : string.Join('、', opensMissing))}")}"
            + (_libraryOnBanner ? "；媒体库那一排压在轮播左下角（矮窗档）" : ""));
    }

    /// <summary>
    /// 自检：这一页上标题叫 <paramref name="title"/> 的那一排，牌子报不报得可进。按标题找而不是按钥匙找，
    /// 因为屏上那份（<see cref="Shelves"/>）只有标题；标题在这一页是唯一的 —— 版面就是这么造的。
    /// </summary>
    private bool CardOpen(string title) =>
        _all.Any(entry => entry.Shelf.CanOpen
            && string.Equals(entry.Row.Title, title, StringComparison.Ordinal));

    /// <summary>
    /// The same rows without the music ones; see <see cref="EmbyItemType.IsMusic"/> for why they are
    /// hidden rather than shown and left to disappoint.
    /// </summary>
    private static List<EmbyItem> WithoutMusic(List<EmbyItem> items) =>
        items.Where(item => !EmbyItemType.IsMusic(item.Type)).ToList();

    /// <summary>
    /// The carousel's slides, out of the batch the band asked for itself
    /// (<see cref="HomeCarousel.Slides"/>). Empty until the page has been attached: a slide holds
    /// artwork, and there is nothing to fetch it with before then.
    /// </summary>
    private IReadOnlyList<BannerSlide> BuildSlides(IReadOnlyList<EmbyItem> latest, int slots) =>
        _images is not { } images
            ? []
            : [.. HomeCarousel.Slides(latest, slots).Select(item => new BannerSlide(item, images))];

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
    /// <para>
    /// 轮播被设置关掉那一档单说一句：那时张数为零是对的，而「0 张」和「服务器上一个宽图都没有」在报告里长得一样。
    /// </para>
    /// </summary>
    internal string BannerSummary => LoadedCount < 0
        ? "未读取"
        : !_banner
            ? "设置里关掉了（继续观看回到横着排的那一叠里）"
            : Slides.Count == 0
                ? "0 张（最近添加里没有带宽图的条目）"
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
    /// 点一排的牌子（标题或者它右边那个大于号）时去哪儿。
    /// </summary>
    /// <remarks>
    /// 2026-09-13 用户原话「在最近添加右边添加一个大于号，点击标题后可以进入对应媒体库」。走的是和点这一排
    /// 卡片完全同一个入口（<see cref="IShellActions.TryOpenLibrary"/>），所以磁盘栏的高亮和面包屑落点一致 ——
    /// 从牌子进库和从卡片进库是同一件事，只是不用先挑一张卡。
    /// <para>
    /// 库那几排之外，2026-09-14「新增点击图中红框的标题可以进入对应的页面」把「继续观看」也接上了：它不属于
    /// 任何库，进的是自己那一张列着「没看完」的网格（<see cref="IShellActions.OpenRow"/>）。那一头照旧构造一个
    /// <c>LibraryRequest</c> 交给同一个 <c>LibraryPage</c>，所以排序、筛选、翻页这些是现成的。
    /// </para>
    /// <para>
    /// 没有去处的排（<see cref="HomeLayout.HomeRowTarget.None"/>，也就是详情页那几排）这里什么也不做，
    /// UI 那一头也不会给它们画出大于号。
    /// </para>
    /// </remarks>
    internal void OpenShelf(CardShelf shelf)
    {
        if (_actions is null)
        {
            Log.Warn(Category, $"{shelf.Title} 的牌子点了，但外壳动作还没接上");
            return;
        }

        switch (shelf.Target)
        {
            case HomeLayout.HomeRowTarget.Library:
                if (shelf.LibraryId is { Length: > 0 } id)
                {
                    Log.Info(Category, $"进库（牌子）：{id}");
                    _actions.TryOpenLibrary(id);
                }
                else
                {
                    Log.Warn(Category, $"{shelf.Title} 的目标是库却没有库 id，牌子点了个寂寞");
                }
                break;

            case HomeLayout.HomeRowTarget.Resume:
            case HomeLayout.HomeRowTarget.NextUp:
                Log.Info(Category, $"开一排的页面：{shelf.Target}");
                _actions.OpenRow(shelf.Target);
                break;

            default:
                Log.Warn(Category, $"{shelf.Title} 的牌子收到了点击，但目标是无处可去的 {shelf.Target}");
                break;
        }
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
