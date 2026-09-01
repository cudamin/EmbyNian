using System.ComponentModel;
using System.Runtime.CompilerServices;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Shell.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.Views;

/// <summary>
/// One poster card's worth of state: what to draw, and the artwork once it has arrived.
/// <para>
/// Public because <c>x:Bind</c> in a DataTemplate compiles against the type by name. A class rather
/// than a record: the poster arrives after construction, so the type has to raise change
/// notifications, and value equality on something holding a decoded bitmap is not a useful idea.
/// </para>
/// <para>
/// The artwork is loaded from here rather than from the control, and released again from here, because
/// the control is recycled and the item is not. That split is also what makes the release honest: a
/// discarded container drops its reference to the card, but the card is still in the page's list, so
/// nulling <see cref="Poster"/> is the only thing that actually frees the decoded surface.
/// </para>
/// </summary>
public sealed class CardItem : INotifyPropertyChanged
{
    private const string Category = "卡片";

    /// <summary>
    /// Segoe Fluent codepoints, spelled as numbers rather than as characters. The characters are in the
    /// Unicode private-use area: pasted into source they are invisible in a diff, and any tool that
    /// re-encodes the file can silently replace them with a replacement character.
    /// </summary>
    private const int MovieGlyph = 0xE8B2;
    private const int SeriesGlyph = 0xE7F4;
    private const int FolderGlyph = 0xE8B7;
    private const int PersonGlyph = 0xE77B;

    private readonly EmbyItem _item;
    private readonly EmbyImageStore _images;
    private readonly int _width;

    /// <summary>True for a 16:9 card. Kept, not just consumed, because it also decides the height.</summary>
    private readonly bool _wide;

    /// <summary>
    /// 设置 → 显示观看状态标记. False hides the corner badges — 已看, 收藏 and the unplayed count — and
    /// nothing else: the hover strip's buttons are how those states are *changed*, and a user who has
    /// turned the badges off has not asked to lose the controls.
    /// </summary>
    private readonly bool _indicators;

    /// <summary>What to write on the second line instead of the item's own; null to use the item's.</summary>
    private readonly string? _subtitle;

    /// <summary>Whether artwork exists to ask for, or null when the item has none worth asking for.</summary>
    private readonly string? _imageType;

    private CancellationTokenSource? _loading;
    private bool _missing;
    private BitmapImage? _poster;

    /// <param name="width">
    /// The card's width in device-independent pixels. Used three times: to pick the size to ask the
    /// server for, as the decode width, and as the width the card is drawn at — which is the point of
    /// taking it here rather than in the DataTemplate, because those three can then not disagree.
    /// </param>
    /// <param name="wide">
    /// True for a 16:9 card (继续观看, 接下来看, 媒体库), which prefers the episode still over the poster.
    /// </param>
    /// <param name="subtitle">
    /// Overrides the second line. Only the cast shelf uses it: a person's own <c>CardSubtitle</c> is the
    /// word 演职人员, which is what the whole row is called, and the useful line is their role.
    /// </param>
    /// <param name="indicators">False to draw no corner badges; see <see cref="_indicators"/>.</param>
    /// <param name="current">
    /// True for the one row that is the page you are already on; see <see cref="IsCurrentEpisode"/>.
    /// </param>
    public CardItem(
        EmbyItem item,
        EmbyImageStore images,
        int width,
        bool wide = false,
        string? subtitle = null,
        bool indicators = true,
        bool current = false)
    {
        _item = item;
        _images = images;
        _width = width;
        _wide = wide;
        _subtitle = subtitle;
        _indicators = indicators;
        IsCurrentEpisode = current;

        _imageType = wide ? PreferWide(item) : PreferTall(item);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The item behind the card. Whoever opens it needs the real thing, not a projection.</summary>
    public EmbyItem Item => _item;

    public string Title => _item.Name;

    /// <summary>
    /// 列表视图 row's own title: 「1. 集名」. Kept separate from <see cref="Title"/> because the numbered
    /// form belongs to a list read down the left edge, while the card under a hero keeps the plain name.
    /// </summary>
    public string RowTitle => ItemDetail.EpisodeRowTitle(_item);

    /// <summary>
    /// The dim line under a row title (「2016/4/17  ·  46 分钟」), or null when the item gives it nothing to
    /// say. Null rather than empty because <see cref="RowInfoVisibility"/> has to hide the line without
    /// reserving its height. Read off the item, like <see cref="RowTitle"/>: the row is one shape of one
    /// item, and a fill that had to pass the line in could only ever pass this same string.
    /// </summary>
    public string? RowInfo => ItemDetail.EpisodeRowInfo(_item) is { Length: > 0 } info ? info : null;

    public Visibility RowInfoVisibility => Show(RowInfo is not null);

    /// <summary>
    /// The row's synopsis, clamped to two lines by the row itself rather than shortened here. Null when
    /// the server sent none: an empty string would still claim the second line's height, and a row that
    /// has nothing to say is allowed to be shorter.
    /// </summary>
    public string? RowOverview => string.IsNullOrWhiteSpace(_item.Overview) ? null : _item.Overview.Trim();

    public Visibility RowOverviewVisibility => Show(RowOverview is not null);

    /// <summary>
    /// 「You are here」: this row is the episode whose page is open. An episode page lists its own siblings,
    /// so the list it appears in contains the page itself, and without a mark the reader has to compare
    /// titles with the one at the top of the screen to work out where they are.
    /// <para>
    /// Fixed at construction rather than settable: a fill is where the answer is known, and a card whose
    /// 「this is you」 could change after it was drawn would need a notification path for something that
    /// never happens — a different episode is a different page and a different fill.
    /// </para>
    /// </summary>
    public bool IsCurrentEpisode { get; }

    /// <summary>The accent bar down the row's left edge, which is how <see cref="IsCurrentEpisode"/> reads.</summary>
    public Visibility CurrentVisibility => Show(IsCurrentEpisode);

    public string Subtitle => _subtitle ?? _item.CardSubtitle;

    /// <summary>Drawn behind the poster, so an item with no artwork still looks deliberate.</summary>
    public string Glyph => char.ConvertFromUtf32(_item.Type switch
    {
        EmbyItemType.Series or EmbyItemType.Season or EmbyItemType.Episode => SeriesGlyph,
        EmbyItemType.BoxSet or EmbyItemType.Folder or EmbyItemType.CollectionFolder
            or EmbyItemType.Playlist => FolderGlyph,
        EmbyItemType.Person => PersonGlyph,
        _ => MovieGlyph
    });

    /// <summary>
    /// The size the card is drawn at, bound by the DataTemplate. Here rather than in the markup so that
    /// the drawn width is provably the width the bitmap was decoded for; see <see cref="CardSize"/>.
    /// </summary>
    public double CardWidth => _width;

    public double PosterHeight => CardSize.HeightFor(_width, _wide);

    public BitmapImage? Poster
    {
        get => _poster;
        private set
        {
            if (ReferenceEquals(_poster, value)) return;
            _poster = value;
            Raise();
        }
    }

    public bool Watched => _item.IsWatched;

    public bool IsFavorite => _item.UserData?.IsFavorite ?? false;

    /// <summary>Percent watched, for the bar across the bottom of the poster. Zero hides it.</summary>
    public double Progress => _item.HasResumePosition ? _item.ProgressFraction * 100 : 0;

    /// <summary>
    /// 「看到一半」那条线的两段。压在胶片格下边框上的进度不是一个控件，是两列宽度 —— 一个 Grid 分成
    /// <c>已看 : 还没看</c> 两份星号列，强调色那块 Border 占第一列，于是「长度就是进度」是布局算出来的，
    /// 而不是谁把百分比换算成像素。
    /// <para>
    /// 这么写还有一个用处：海报 170、剧照 300、单集行的图又是另一个宽度，三种宽度共用这一对属性，没有
    /// 哪一处需要知道自己多宽。
    /// </para>
    /// </summary>
    public GridLength ProgressDone => new(Progress, GridUnitType.Star);

    /// <inheritdoc cref="ProgressDone"/>
    public GridLength ProgressLeft => new(100 - Progress, GridUnitType.Star);

    /// <summary>How many episodes of a series are still unwatched, blank when none or not a series.</summary>
    public string UnplayedText =>
        _item.UserData?.UnplayedItemCount is { } count and > 0 ? count.ToString() : string.Empty;

    // Visibility rather than bool plus a converter: these are view-model properties on a view-model
    // that only a view uses, and a converter per flag would be four more files saying less.
    //
    // 角标和按钮各有一对，名字里带 Badge 的那对才受 _indicators 管：角标是「显示观看状态标记」这个开关
    // 说的东西，悬浮按钮上的图标不是 —— 关掉标记之后按钮还在，两个图标要是都被 _indicators 关掉，那颗
    // 按钮就成了一块什么都不画的空白。
    public Visibility WatchedBadgeVisibility => Show(_indicators && Watched);

    public Visibility FavoriteBadgeVisibility => Show(_indicators && IsFavorite);

    /// <summary>
    /// The two halves of each of the hover buttons' icon pairs: the white glyph shows when the state is
    /// off and the coloured one when it is on — same shape either way, so the button does not appear to
    /// change into a different button. Negated here rather than by a converter for the same reason as
    /// above.
    /// </summary>
    public Visibility WatchedVisibility => Show(Watched);

    /// <inheritdoc cref="WatchedVisibility"/>
    public Visibility NotWatchedVisibility => Show(!Watched);

    /// <inheritdoc cref="WatchedVisibility"/>
    public Visibility FavoriteVisibility => Show(IsFavorite);

    /// <inheritdoc cref="WatchedVisibility"/>
    public Visibility NotFavoriteVisibility => Show(!IsFavorite);

    /// <summary>What the hover strip's buttons promise to do, which is the opposite of the state shown.</summary>
    public string WatchedActionTip => Watched ? "标记为未观看" : "标记为已观看";

    public string FavoriteActionTip => IsFavorite ? "取消收藏" : "添加到收藏";

    /// <summary>
    /// The play button in the middle of the artwork, which only exists on a card the server would
    /// actually stream. A series, a folder and a person all open instead, and a button offering to play
    /// one of them would be offering nothing.
    /// </summary>
    public Visibility PlayVisibility => Show(_item.IsPlayable);

    /// <summary>
    /// 已观看 and 收藏 on the hover strip, which the 媒体库 row on the home page and the 演职人员 shelf
    /// on a detail page do without: neither a library nor a name has a watched state, so those two
    /// buttons there promised something the server would refuse (「主页的媒体库不要收藏和已观看」).
    /// <para>
    /// The item's type decides it, not the caller: the same library card can appear on another page, and
    /// which buttons make sense is a fact about the item rather than about where it is drawn. Separate
    /// from <see cref="_indicators"/> for the reason given there — that flag is a display preference and
    /// this one is whether the action exists at all.
    /// </para>
    /// </summary>
    public Visibility UserStateVisibility => Show(_item.TracksUserState);

    public string PlayActionTip =>
        _item.HasResumePosition ? $"继续播放（{TimeFormat.Clock(_item.ResumeTicks)}）" : "立即播放";

    public Visibility ProgressVisibility => Show(Progress > 0);

    /// <summary>The unplayed count is suppressed once everything is watched, so the two never stack.</summary>
    public Visibility UnplayedVisibility => Show(_indicators && !Watched && UnplayedText.Length > 0);

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Starts the artwork load if it has not been done. Idempotent, and safe to call again every time
    /// a recycled container is handed this card.
    /// </summary>
    public async Task EnsurePosterAsync()
    {
        if (_imageType is null || _missing || _poster is not null || _loading is not null) return;

        // Already decoded once this run: hand it straight over. Synchronous on purpose — a container
        // coming back to a poster it has shown before should not flicker through an empty frame first.
        if (PosterKey() is { } key && PosterCache.TryGet(key, out var kept))
        {
            Poster = kept;
            return;
        }

        var cts = new CancellationTokenSource();
        _loading = cts;

        try
        {
            var bytes = await _images.GetAsync(_item, _imageType, EmbyImageStore.RequestWidth(_width), cts.Token)
                .ConfigureAwait(true);

            if (cts.IsCancellationRequested) return;

            if (bytes is null || bytes.Length == 0)
            {
                // Remembered, so scrolling past an artwork-less item repeatedly does not re-ask the
                // server every time its container comes back. Only a genuine 「no such image」 reaches
                // here: a load this card gave up on throws instead, and is caught below without a mark.
                _missing = true;
                return;
            }

            var bitmap = await PosterLoader.DecodeAsync(bytes, _width).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            if (bitmap is not null && PosterKey() is { } name) PosterCache.Remember(name, bitmap);

            Poster = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            _missing = true;
            Log.Debug(Category, $"加载图片失败（{_item.Name}）：{error.Message}");
        }
        finally
        {
            // Null first, then dispose: there is no await between the two, so nothing can observe a
            // disposed source through the field.
            if (ReferenceEquals(_loading, cts)) _loading = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Drops the decoded bitmap and abandons any load in flight. Called when a container is recycled:
    /// without it, a long scroll ends up holding every poster it has ever passed.
    /// <para>
    /// Dropped from the card, not from <see cref="PosterCache"/> — that one has its own ceiling, and it
    /// is what makes scrolling back up instant instead of a second trip to the disk.
    /// </para>
    /// </summary>
    public void ReleasePoster()
    {
        _loading?.Cancel();
        _loading = null;
        Poster = null;
    }

    /// <summary>
    /// The name this card's decoded picture goes under, or null when the item has no artwork of the kind
    /// this card wants. Built each time rather than kept: the tag in it is the server's, and artwork
    /// replaced there has to read as a different picture.
    /// </summary>
    private string? PosterKey() =>
        _imageType is not null && EmbyImageStore.TagFor(_item, _imageType) is { } tag
            ? PosterCache.Key(_item.Id, _imageType, tag, _width)
            : null;

    /// <summary>Re-reads the badges after the item's user data changes. Requirement 6 calls this.</summary>
    public void RefreshUserData()
    {
        Raise(nameof(Watched));
        Raise(nameof(IsFavorite));
        Raise(nameof(Progress));
        Raise(nameof(ProgressDone));
        Raise(nameof(ProgressLeft));
        Raise(nameof(UnplayedText));
        Raise(nameof(WatchedBadgeVisibility));
        Raise(nameof(FavoriteBadgeVisibility));
        Raise(nameof(WatchedVisibility));
        Raise(nameof(FavoriteVisibility));
        Raise(nameof(NotWatchedVisibility));
        Raise(nameof(NotFavoriteVisibility));
        Raise(nameof(WatchedActionTip));
        Raise(nameof(FavoriteActionTip));
        Raise(nameof(PlayActionTip));
        Raise(nameof(ProgressVisibility));
        Raise(nameof(UnplayedVisibility));
    }

    /// <summary>
    /// Tall cards want the poster and will settle for a still; wide cards want the still and will
    /// settle for a backdrop, then the poster. Null means the item has no artwork at all, and the
    /// glyph behind the picture is what the card shows for good.
    /// </summary>
    private static string? PreferTall(EmbyItem item) =>
        item.PrimaryImageTag is not null ? EmbyImageStore.Primary
        : item.ThumbImageTag is not null ? EmbyImageStore.Thumb
        : null;

    private static string? PreferWide(EmbyItem item) =>
        item.ThumbImageTag is not null ? EmbyImageStore.Thumb
        : item.BackdropImageTags.Count > 0 ? EmbyImageStore.Backdrop
        : item.PrimaryImageTag is not null ? EmbyImageStore.Primary
        : null;

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property ?? string.Empty));
}
