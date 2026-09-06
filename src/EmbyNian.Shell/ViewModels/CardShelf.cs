using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Shell.Views;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// One shelf of cards: a heading and the cards under it. Used by the home page's four horizontal rows, by
/// the detail page's 演职人员 and 全部剧季 rows, and — through <see cref="FillRows"/> — by its 单集 list,
/// which is the same data drawn down the page instead of across it.
/// <para>
/// The home page used to be three near-identical blocks of XAML — a heading, a fixed-height
/// <c>ScrollView</c> and an <c>ItemsRepeater</c>, written out once per row with the height and the
/// template swapped — and a matching <c>Fill(row, section, …)</c> call per row in code-behind. Adding
/// 媒体库 as a fourth row under that arrangement meant copying twenty lines of markup and getting the
/// two magic heights right by hand. Here a row is data, the page draws whatever rows it is handed, and
/// a fifth one is one line in <see cref="HomeViewModel"/>.
/// </para>
/// <para>
/// It was called <c>HomeShelf</c> while the home page was the only page with rows. The detail page has
/// two of exactly this shape, and its markup carried the same two literal heights — 228 and 246 — that
/// this type exists to derive.
/// </para>
/// <para>
/// Public because <c>x:Bind</c> in a DataTemplate compiles against the type by name; see
/// <see cref="CardItem"/>.
/// </para>
/// </summary>
public sealed partial class CardShelf : ObservableObject
{
    private readonly EmbyImageStore _images;
    private readonly int _width;
    private readonly bool _wide;
    private readonly bool _indicators;

    /// <param name="wide">
    /// True for a 16:9 row, which prefers the episode still over the poster — a 继续观看 card is about
    /// the scene you stopped at, not the cover art.
    /// </param>
    internal CardShelf(string title, EmbyImageStore images, int width, bool wide, bool indicators)
    {
        Title = title;
        _images = images;
        _width = width;
        _wide = wide;
        _indicators = indicators;

        RowHeight = CardSize.HeightFor(width, wide) + CardSize.Chrome;

        // 读数跟着内容走。挂在集合的事件上而不是在 Clear/Fill/FillRows 里各喊一遍：那三个方法之外还有
        // 别人往 Cards 里加东西（详情页换季就是），漏喊一处的症状是牌子右端那个数字停在上一季。
        Cards.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Note));
    }

    /// <summary>
    /// The heading above the row. Settable, and observable, because one row's heading is a fact about
    /// its contents rather than about the row: 详情页's 单集 heading reads
    /// 「更多来自：第一季 · 12 集，已看 3 集」, and picking another season from the drop-down rewrites it
    /// without rebuilding the shelf. The home page's four headings are assigned once and never again,
    /// which this still allows.
    /// </summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>
    /// 这一排是不是压在一张剧照上。主页第一排是（大图占满第一屏，这一排坐在它的下半截上），详情页的单集带在
    /// 集页上也是 —— 两处要的是同一件事：牌子和卡片底下那两行字换成压在图上那套不随主题走的浅墨，否则晴昼那套
    /// 浅色主题的近黑字会消失在暗罩里。开关本身在 <see cref="Views.ShelfHead.OnScrim"/>、
    /// <see cref="Views.ShelfStrip.OnScrim"/> 和 <see cref="Views.PosterCard.OnScrim"/>；这一支是让标记绑得到它。
    /// </summary>
    [ObservableProperty]
    public partial bool OnScrim { get; set; }

    public ObservableCollection<CardItem> Cards { get; } = [];

    /// <summary>
    /// 牌子右端那个读数，「24 项」。空的时候是空字符串，<see cref="Views.ShelfHead"/> 会把那一格整个收起 ——
    /// 一带还没载入完的时候写个「0 项」，说的是「这里什么都没有」，而事实是「还不知道」。
    /// </summary>
    public string Note => Cards.Count > 0 ? $"{Cards.Count} 项" : string.Empty;

    /// <summary>
    /// How tall the row's scroller has to be, picture plus the two lines of text under it. A horizontal
    /// scroller inside a vertical stack has no other way to know: its content measures itself
    /// unconstrained in the scrolling direction, so the vertical parent would give it either nothing or
    /// everything. Computed from the card's own size rather than typed in as a literal, which is how the
    /// old markup's 228 and 314 came to be two numbers nobody could re-derive.
    /// </summary>
    public double RowHeight { get; }

    /// <summary>
    /// Empties the row, releasing what was in it. Its own method rather than <c>Fill([])</c>: an empty
    /// collection expression cannot pick between the two overloads below — there are no elements to pick
    /// by — and 「this row now has nothing in it」 is worth saying in one word anyway.
    /// </summary>
    internal void Clear()
    {
        // AbandonPoster 而不是 ReleasePoster：这一批卡整个作废，在路上那几趟的结果没有任何人在等
        // （两者的分工见 CardItem.ReleasePoster 上那段）。
        foreach (var card in Cards) card.AbandonPoster();
        Cards.Clear();
    }

    /// <summary>
    /// Replaces the row's contents, releasing what was there. Whatever the row held before is still
    /// holding decoded bitmaps until it is told otherwise, and a reload that skipped this would leak
    /// one poster per card per refresh.
    /// </summary>
    /// <param name="subtitle">Overrides every card's second line, from the item it belongs to.</param>
    internal void Fill(IEnumerable<EmbyItem> items, Func<EmbyItem, string?>? subtitle = null) =>
        Fill(items.Select(item => (item, subtitle?.Invoke(item))));

    /// <summary>
    /// The same, for a row whose second line does not come from the item at all.
    /// <para>
    /// 演职人员 is the one such row: the line under a portrait is the role, which belongs to the credit
    /// rather than to the person. Paired here rather than looked up by id, because the same actor can
    /// hold two credits on one title — a director who also appears, a voice actor with two parts — and a
    /// lookup keyed on the person would have to decide which of the two to throw away.
    /// </para>
    /// </summary>
    internal void Fill(IEnumerable<(EmbyItem Item, string? Subtitle)> items)
    {
        Clear();

        foreach (var (item, subtitle) in items)
            Cards.Add(new CardItem(item, _images, _width, _wide, subtitle, _indicators));
    }

    /// <summary>
    /// The same, for a shelf drawn as 列表 rows rather than as cards — 详情页's 单集 list. Its own path
    /// because a row asks two things a card never does: there is no second line to override (a row reads
    /// its own <see cref="CardItem.RowTitle"/>, <see cref="CardItem.RowInfo"/> and
    /// <see cref="CardItem.RowOverview"/> off the item), and one of the rows may be the page the reader is
    /// already on.
    /// </summary>
    /// <param name="page">
    /// The item whose page this shelf is on, or null. Only used to mark the 「you are here」 row, so a season
    /// or movie page — and a season switched in the drop-down that does not contain the open episode —
    /// simply marks nothing.
    /// </param>
    internal void FillRows(IEnumerable<EmbyItem> items, EmbyItem? page)
    {
        Clear();

        foreach (var item in items)
            Cards.Add(new CardItem(
                item, _images, _width, _wide, subtitle: null, _indicators,
                ItemDetail.IsCurrentEpisode(item, page)));
    }
}
