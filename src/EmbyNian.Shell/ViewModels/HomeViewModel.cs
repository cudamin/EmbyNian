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
/// though 继续观看 得了第一屏右边那一栏（<see cref="Rail"/>，「把图中的媒体库和继续观看位置调换」的下一步）:
/// 上次停在哪儿是一个用过的账号打开主页最想看见的一句话，而媒体库那一排在侧边栏里另有一份。
/// </para>
/// <para>
/// 勾掉的那一排连请求一起省掉：媒体库那几排各是一次「这个库的最近添加」，不看的库不该每次开主页都问一遍。
/// </para>
/// <para>
/// 设置 → 界面 → 「显示主页轮播大图」关掉之后这一页就是一叠普通的货架（<see cref="_banner"/>）：顶上那张大图和
/// 它右边那一栏一起没有，继续观看回到横着排的那一叠里、按它在版面表上的位置站着。
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
    /// 设置 → 界面 → 「显示主页轮播大图」（<c>UiSettings.ShowHomeBanner</c>）。关掉的时候第一屏那两栏一起没有：
    /// 一张幻灯片都不造（<see cref="Slides"/> 空着，带子自己就收起来了），继续观看也不再当右栏、回到下面横着排的
    /// 那一叠里按它在版面表上的位置站着。
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

    public HomeViewModel()
    {
        LoadedCount = NotLoaded;
        Slides = [];
    }

    /// <summary>
    /// 横着排的那几排，只留装到了东西的。Only the non-empty ones, rather than all four with the
    /// empty ones collapsed: an <c>ItemsRepeater</c> still spends its <c>StackLayout</c> spacing on a
    /// collapsed item, so an account with no 接下来看 would get a blank gap where the row would be.
    /// <para>
    /// 媒体库不在这里 —— 它是 <see cref="Rail"/>，第一屏右边那一栏竖着排的那一列。
    /// </para>
    /// </summary>
    public ObservableCollection<CardShelf> Shelves { get; } = [];

    /// <summary>
    /// 第一屏右边那一栏：媒体库，竖着排一列（「参考上图修改轮播页面」那张图上，第一屏是并排两栏 —— 左边轮播
    /// 大图、右边那一列）。<see langword="null"/> 是「这一栏不出现」：媒体库空着、在 设置 → 主页 里被勾掉了、
    /// 或者轮播整个被关掉了（<see cref="_banner"/>）—— 前两种时大图占满整个第一屏，最后一种时连大图一起没有、
    /// 媒体库回到下面横着排的那一叠里。
    /// <para>
    /// **这一栏 2026-09-06 从继续观看换成了媒体库**（「把首页轮播图右边的继续观看更换为媒体库列表」）。同一句
    /// 话里还有「侧边栏也改」，两件是一件：左边那条栏里的媒体库入口没了之后，这一栏就是它们在第一屏上的落点。
    /// </para>
    /// <para>
    /// 按钥匙认（<see cref="HomeLayout.Libraries"/>）而不是按位置认，和 <see cref="Shelves"/> 里其余几排是两件事：
    /// 那一栏里的卡是 16:9 的剧照卡、竖着排，而拖拽表里排第一的可能是海报那种排（最近添加），一列 2:3 的海报在
    /// 一条卡宽的栏里只放得下两张。栏宽由 <see cref="HomeCarousel.RailWidth"/> 按卡宽算，见那一段。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RailVisibility))]
    public partial CardShelf? Rail { get; set; }

    /// <inheritdoc cref="Rail"/>
    public Visibility RailVisibility => Show(Rail is not null);

    /// <summary>第一屏右边那一栏有多宽（<see cref="HomeCarousel.RailWidth"/>）；没有那一栏时是 0。</summary>
    internal double RailWidth => Rail is null ? 0 : HomeCarousel.RailWidth(CardSize.WideWidth);

    /// <summary>
    /// 需求 5 的那条大图轮播（<see cref="HomeCarousel"/>）: 继续观看的头几个条目，不够时由最近添加补上，each
    /// drawn as one full-width backdrop instead of a card. 设置里关掉轮播（<see cref="_banner"/>）时这里是空的，
    /// 带子自己就收起来了。
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

        // 继续观看、媒体库、接下来看走 16:9 的宽卡（那三排讲的是「你在看的那一格画面」）；最近添加和每个媒体库
        // 自己那一排走海报 —— 一整排新片的封面比一整排剧照读得快。媒体库那一排不上角标：一个库没有「已看」。
        // 媒体库**在轮播开着的时候**另有两处不同：卡宽走 HomeCarousel.RailCard（封顶在装机那一档），而它的
        // OnScrim 一直是开的 —— 那时它是第一屏右边那一栏（Rail），站在和大图同一支深底上
        // （EgBannerBaseBrush），所以牌子和卡片底下那两行字要走压在图上那套不随主题走的浅墨。横着的那几排在第一
        // 屏外面、站在页面自己的纸上，照旧走主题的墨；轮播关掉之后媒体库也变成那样的一排。
        //
        // **右栏从继续观看换成媒体库是 2026-09-06 他的话**（「把首页轮播图右边的继续观看更换为媒体库列表」），
        // 和同一句里「侧边栏也改」是一件事：左边那条栏里的媒体库入口没了之后，这一栏就是它们在第一屏上的落点。
        // 换过去在形状上是白拿的 —— 媒体库那一排本来就是宽卡（上面那个 wide 判断里就有它），所以栏宽、卡宽、
        // 一栏放得下几张全都不用动。继续观看跟着变成横着的一排，站在它自己在版面表上的位置。
        _all = [.. plan.Select(row =>
        {
            var rail = _banner && row.Key == HomeLayout.Libraries;
            var wide = row.Key is HomeLayout.Resume or HomeLayout.Libraries or HomeLayout.NextUp;
            var width = rail ? HomeCarousel.RailCard(CardSize.WideWidth) : wide ? CardSize.WideWidth : CardSize.PosterWidth;
            var badges = row.Key != HomeLayout.Libraries && _badges;

            return (row, new CardShelf(row.Title, images, width, wide, badges) { OnScrim = rail });
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

        // 一排装什么由它那把钥匙说，不再是「数组第几个」—— 次序现在是用户拖出来的，位置说明不了任何事。
        // 轮播不走这个局部函数：它只认继续观看和最近添加两排，两个具名参数直接交过去（见下面 Slides 那一句）。
        IReadOnlyList<EmbyItem> Items(string key) => key switch
        {
            HomeLayout.Resume => resumed,
            HomeLayout.Libraries => _libraryViews,
            HomeLayout.NextUp => next,
            HomeLayout.Latest => added,
            _ => perLibrary.TryGetValue(key, out var library) ? WithoutMusic(library.Result) : []
        };

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

        // 媒体库挑出来当第一屏右边那一栏（Rail），其余装到了东西的横着排在它下面。按钥匙挑而不是按位置挑，
        // 理由在 Rail 那一段：那一栏里的卡必须是 16:9 的剧照卡，而拖拽表里排第一的可能是海报那种排。
        // 轮播关掉的时候一栏都不挑（_banner），媒体库就跟着别的几排横着排在自己那个位置上。
        CardShelf? rail = null;
        Shelves.Clear();

        foreach (var (row, shelf) in _all)
        {
            if (!row.Visible || shelf.Cards.Count == 0) continue;

            if (_banner && row.Key == HomeLayout.Libraries) rail = shelf;
            else Shelves.Add(shelf);
        }

        Rail = rail;

        // 需求 5：轮播站在这几排的头几个条目上，不额外问服务器一次 —— 那些条目已经在手上了。**先用继续观看，
        // 不够再用最近添加**（「首页的轮播图有继续观看就用继续观看，没有或者继续观看不够就用最近添加」），哪几个
        // 上得了台是 HomeCarousel 的事（要有宽图、一个剧集只占一张）。关掉轮播就一张都不造，带子自己收起来。
        Slides = _banner ? BuildSlides(resumed, added) : [];

        LoadedCount = resumed.Count + next.Count + added.Count;

        // Only when there is nothing at all. An account whose libraries are listed but which has never
        // played anything is not an empty account, and telling it so under a row of its own libraries
        // would be plainly untrue.
        ShowEmptyNotice = Shelves.Count == 0 && Rail is null;

        Subheading = LoadedCount == 0
            ? _session.ServerDisplayName
            : $"{_session.ServerDisplayName}  ·  {LoadedCount} 项";

        EndLoad(token);
    }

    /// <summary>
    /// 自检：这一次的版面 ——「轮播开；继续观看✓、媒体库✓（右栏）、最近添加 · 电影✗」。屏上那几排
    /// （<see cref="Shelves"/>）只有装到了东西的才在，所以这一句是唯一能看出「勾掉的那一排真的没排」和「拖出来的
    /// 次序真的生效了」的地方。轮播那个开关也报在这儿：它一关，媒体库就从右栏变成横着的一排，而下面那几行读数
    /// 一个都不会说这件事。
    /// </summary>
    internal string LayoutSummary => _all.Length == 0
        ? "未读取"
        : $"轮播{(_banner ? "开" : "关")}；" + string.Join("、", _all.Select(entry =>
            $"{entry.Row.Title}{(entry.Row.Visible ? "✓" : "✗")}"
                + $"{(_banner && entry.Row.Key == HomeLayout.Libraries ? "（右栏）" : "")}"
                + $"{(entry.Shelf.Cards.Count > 0 ? "" : "（空）")}"));

    /// <summary>
    /// 自检：屏上那几排真按版面来的 —— 次序一样、勾掉的那几排真没排。两边各算一次再比：版面那一份是设置文件说的
    /// （<see cref="HomeLayout.Plan"/>），屏上那一份是 <see cref="Shelves"/>，中间隔着「装到了东西才排」这一条。
    /// <para>
    /// 轮播开着的时候媒体库两边都不算：它是第一屏右边那一栏（<see cref="Rail"/>），不在那几排里。它到底出没出现
    /// 单报一句 —— 少了那一句，「勾掉媒体库之后右边那一栏还在」就没人看得见。轮播关掉的时候它就是普通的一排，
    /// 照常参加比对，而右栏必须不在。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) LayoutRead()
    {
        var wanted = _all
            .Where(entry => entry.Row.Visible && entry.Shelf.Cards.Count > 0)
            .Where(entry => !_banner || entry.Row.Key != HomeLayout.Libraries)
            .Select(entry => entry.Row.Title)
            .ToList();

        var onScreen = Shelves.Select(shelf => shelf.Title).ToList();

        // 那一栏在不在，和版面里媒体库那一行说的必须一致 —— 加上轮播那个开关：关着就一定没有右栏。
        var railWanted = _banner && _all.Any(entry =>
            entry.Row.Key == HomeLayout.Libraries && entry.Row.Visible && entry.Shelf.Cards.Count > 0);
        var railOk = railWanted == Rail is not null;

        var ok = _all.Length > 0 && railOk && wanted.SequenceEqual(onScreen, StringComparer.Ordinal);

        return (ok, $"版面 {LayoutSummary}；横着排的 {(onScreen.Count == 0 ? "无" : string.Join('、', onScreen))}"
            + $"；右栏 {(Rail is null ? "无" : $"{Rail.Title} {Rail.Cards.Count} 项，宽 {RailWidth:0}")}"
            + (railOk ? "" : "（和版面说的对不上）"));
    }

    /// <summary>
    /// The same rows without the music ones; see <see cref="EmbyItemType.IsMusic"/> for why they are
    /// hidden rather than shown and left to disappoint.
    /// </summary>
    private static List<EmbyItem> WithoutMusic(List<EmbyItem> items) =>
        items.Where(item => !EmbyItemType.IsMusic(item.Type)).ToList();

    /// <summary>
    /// The carousel's slides, out of the two rows this page already has: 继续观看 first, 最近添加 filling in
    /// behind it (<see cref="HomeCarousel.Slides"/>). Empty until the page has been attached: a slide holds
    /// artwork, and there is nothing to fetch it with before then.
    /// </summary>
    private IReadOnlyList<BannerSlide> BuildSlides(
        IReadOnlyList<EmbyItem> resume,
        IReadOnlyList<EmbyItem> latest) =>
        _images is not { } images
            ? []
            : [.. HomeCarousel.Slides(resume, latest).Select(item => new BannerSlide(item, images))];

    /// <summary>
    /// 右栏里和台上那张幻灯片对应的那一张框起来，其余的取消 ——「轮播图滚动到对应媒体时右边要自动框出对应媒体」。
    /// 交回它是第几张，右栏里没有对应的（或者根本没有右栏）就是 -1；页面拿这个数把它滚进视野。
    /// <para>
    /// 对应关系由 <see cref="HomeCarousel.MatchIndex"/> 定（单测钉着），这里只管把那一位翻到卡片上 —— 每次都整列
    /// 走一遍，所以「上一张的框」不需要另有人去收：那一位是 <see cref="CardItem.Framed"/>，相等就不发通知。
    /// </para>
    /// </summary>
    internal int Frame(EmbyItem? slide)
    {
        if (Rail?.Cards is not { } cards) return -1;

        var index = slide is null ? -1 : HomeCarousel.MatchIndex(slide, [.. cards.Select(card => card.Item)]);

        for (var at = 0; at < cards.Count; at++) cards[at].Framed = at == index;

        return index;
    }

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
                ? "0 张（继续观看和最近添加里都没有带宽图的条目）"
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
