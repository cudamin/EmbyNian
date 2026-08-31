using EmbyNian.Emby;

namespace EmbyNian.Shell.Views;

/// <summary>
/// Everything <see cref="LibraryPage"/> needs to show one grid. A library root, a folder, a season and
/// a set of search results differ only in the fields below, which is why there is one page and not
/// four.
/// <para>
/// It carries the container and the shell rather than letting the page reach for a static: a page that
/// can only work when a particular global happens to be set is a page that cannot be tested, and the
/// second window this app will eventually want would silently share the first one's session.
/// </para>
/// </summary>
internal sealed record LibraryRequest
{
    /// <summary>
    /// Where <see cref="LibraryPage.OnNavigatedTo"/> gets its services. The page resolves the four it
    /// needs in that one block and keeps them in fields; nothing else on the page or in its view model
    /// ever sees the container.
    /// </summary>
    public required IServiceProvider Services { get; init; }

    public required string Title { get; init; }

    /// <summary>The navigation pane's tag, so its highlight can be restored. Empty for a drill-down.</summary>
    public string Tag { get; init; } = "";

    public string? ParentId { get; init; }

    /// <summary>
    /// Emby's own collection type for a library root ("movies", "tvshows"). What decides whether the
    /// query is recursive: see <see cref="ItemQuery.FlattenedTypes"/>.
    /// </summary>
    public string? CollectionType { get; init; }

    public string? SearchTerm { get; init; }

    /// <summary>
    /// Whether this grid is the search page, which is a different question from whether it has anything
    /// to search for: the page opens with an empty box and nothing on it, and 「no term」 has to stay
    /// distinguishable from 「not a search」. Sent as a query, an empty term would fall through to the
    /// folder branch with no parent, and the server answers that with its whole library root.
    /// </summary>
    public bool IsSearch => SearchTerm is not null;

    /// <summary>
    /// 按类型浏览的那一格：详情页上那一行类型点一个就到这儿。<see cref="ParentId"/> 是空的 —— 一个类型不是
    /// 一个文件夹，它横跨这个账号能看的所有媒体库，所以查询是全库递归加一个 <c>Genres</c>
    /// （见 <c>ItemQuery.Genre</c>）。
    /// </summary>
    public string? Genre { get; init; }

    /// <summary>
    /// 演职人员：whose credits this grid lists, or null for every other kind of grid. Set only by
    /// <see cref="ForPerson"/>; see <see cref="ItemQuery.PersonId"/> for why it is one id and not a list.
    /// </summary>
    public string? PersonId { get; init; }

    /// <summary>
    /// The item type whose children this grid lists, when it is a drill-down. Null for a library root
    /// and for a search. What tells the sort menu that these rows are seasons or episodes, which
    /// decides both the default order and which keys are worth offering.
    /// </summary>
    public string? ParentType { get; init; }

    /// <summary>Which kind of row this grid holds; see <see cref="EmbySortPreset"/>.</summary>
    public string? SortPreset => CollectionType is not null ? EmbySortPreset.ForCollection(CollectionType)
        : ParentId is not null ? EmbySortPreset.ForChildrenOf(ParentType)
        : null;

    /// <summary>
    /// 页头场记板最上面那行代号 —— 见 <see cref="PageSlate"/>。
    /// <para>
    /// 这一页是五种网格合一的，标题只写「这一格叫什么」（库名、文件夹名、人名、搜索词），代号补的是
    /// 「这一格是什么」。所以它按上面那几个字段一路问下去，而不是写一个常量：往剧里点进去列的是季、
    /// 往季里点进去列的是集，页头因此会说出你正站在树的哪一层，这是标题给不了的一句话。
    /// </para>
    /// </summary>
    public string Eyebrow => IsSearch ? "SEARCH"
        : PersonId is not null ? "CREDITS"
        : Genre is not null ? "GENRE"
        : CollectionType is not null ? "LIBRARY"
        : ParentType switch
        {
            "Series" => "SEASONS",
            "Season" => "EPISODES",
            "BoxSet" => "COLLECTION",
            _ => "FOLDER"
        };

    /// <summary>
    /// The request for opening one of this page's own items. Deliberately drops
    /// <see cref="CollectionType"/> and <see cref="Tag"/>: below a library root the folder tree is
    /// what the user is walking, and it is no longer any single pane entry's destination.
    /// </summary>
    public LibraryRequest Child(EmbyItem item) => For(Services, item);

    /// <summary>
    /// The same thing for a caller that has no request of its own to descend from — the home page, and
    /// the context menu, which both open items without being a grid.
    /// </summary>
    public static LibraryRequest For(IServiceProvider services, EmbyItem item) => new()
    {
        Services = services,
        Title = item.Name,
        ParentId = item.Id,
        ParentType = item.Type
    };

    /// <summary>
    /// The search page. The same grid as every other one — sort, filters, view shapes, paging and the
    /// letter bar all come along — which is why searching is a request and not a page of its own.
    /// </summary>
    /// <param name="term">
    /// What to look for, or empty for the page the 搜索 button opens: a box waiting to be typed in.
    /// </param>
    public static LibraryRequest Search(IServiceProvider services, string term = "") => new()
    {
        Services = services,
        Title = SearchTitle(term),
        Tag = SearchTag,
        SearchTerm = term
    };

    /// <summary>
    /// What the search page is called, for whichever term it is showing. One rule and two callers: the
    /// request above, and the box on the page — which retitles itself in place rather than navigating.
    /// </summary>
    public static string SearchTitle(string term) => term.Length == 0 ? "搜索" : $"搜索：{term}";

    /// <summary>The pane tag the search page carries. No pane entry points at it; the title bar does.</summary>
    public const string SearchTag = "search";

    /// <summary>
    /// One person's credits, for a portrait tapped on the 详情页's 演职人员 shelf.
    /// <para>
    /// Not <see cref="For"/> with the person's id as the parent, which is the obvious thing and answers an
    /// empty grid: a person is not a folder and has no children. Emby relates people to items by a
    /// separate query parameter, which is what <see cref="PersonId"/> becomes.
    /// </para>
    /// </summary>
    public static LibraryRequest ForPerson(IServiceProvider services, EmbyItem person) => new()
    {
        Services = services,
        Title = person.Name,
        PersonId = person.Id
    };

    /// <summary>
    /// 按类型浏览，见 <see cref="Genre"/>。标题就是那个类型自己 —— 页头那行代号已经说了这是个类型
    /// （GENRE），标题再写一遍「类型：动画」就是同一句话说两遍。
    /// </summary>
    public static LibraryRequest ForGenre(IServiceProvider services, string genre) => new()
    {
        Services = services,
        Title = genre.Trim(),
        Genre = genre.Trim()
    };
}
