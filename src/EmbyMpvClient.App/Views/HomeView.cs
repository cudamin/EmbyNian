using EmbyMpvClient.App.Controls;
using EmbyMpvClient.App.Theme;
using EmbyMpvClient.Emby;

namespace EmbyMpvClient.App.Views;

/// <summary>
/// The landing page: 继续观看, 接下来播放, the libraries themselves and one 最近添加 shelf per video
/// library. Everything here is a shelf, so the page never needs to know how many items came back —
/// an empty shelf hides itself.
/// </summary>
public sealed class HomeView : AppView
{
    private const int MaximumLatestShelves = 6;

    private readonly ScrollHost _scroll = new() { Dock = DockStyle.Fill };
    private readonly PosterShelf _resume;
    private readonly PosterShelf _nextUp;
    private readonly PosterShelf _libraries;
    private readonly Dictionary<string, PosterShelf> _latest = new(StringComparer.Ordinal);
    private readonly List<PosterShelf> _order = [];
    private readonly TextBlock _empty = new("这个账号下没有可播放的媒体库。", Fonts.Body, Palette.TextDim) { Visible = false };

    private bool _loaded;
    private bool _laying;

    public HomeView(IShell shell) : base(shell)
    {
        HeaderTitle = "主页";

        _resume = Landscape("继续观看");
        _nextUp = Landscape("接下来播放");

        _libraries = new PosterShelf("媒体库", Host.Images);
        _libraries.Row.CardWidth = 220;
        _libraries.Row.Aspect = 16 / 9d;
        _libraries.Row.ImageTypes = [EmbyImageStore.Primary, EmbyImageStore.Thumb, EmbyImageStore.Backdrop];
        _libraries.Row.ShowWatchedIndicators = false;
        _libraries.Row.DescribeSubtitle = CollectionLabel;
        _libraries.Row.ItemActivated += library => Shell.OpenLibrary(library);

        Controls.Add(_scroll);
        _scroll.Content.Controls.Add(_empty);
        foreach (var shelf in new[] { _resume, _nextUp, _libraries }) Register(shelf);

        // The host hides part of the content when a scrollbar appears, so the shelves follow it.
        _scroll.Content.Resize += (_, _) => LayoutShelves();
    }

    private PosterShelf Landscape(string title)
    {
        var shelf = new PosterShelf(title, Host.Images);
        shelf.Row.CardWidth = 240;
        shelf.Row.Aspect = 16 / 9d;
        shelf.Row.ImageTypes = [EmbyImageStore.Thumb, EmbyImageStore.Backdrop, EmbyImageStore.Primary];
        shelf.Row.ShowWatchedIndicators = Host.Settings.Ui.ShowWatchedIndicators;
        return shelf;
    }

    private PosterShelf Portrait(string title)
    {
        var shelf = new PosterShelf(title, Host.Images);
        shelf.Row.CardWidth = Host.Settings.Ui.PosterWidth;
        shelf.Row.ShowWatchedIndicators = Host.Settings.Ui.ShowWatchedIndicators;
        return shelf;
    }

    /// <summary>Wires a shelf's activation and context menu, then parents it.</summary>
    private void Register(PosterShelf shelf)
    {
        if (!ReferenceEquals(shelf, _libraries))
        {
            shelf.Row.ItemActivated += item => Shell.Open(item);
            shelf.Row.PlayRequested += item => Shell.PlayAsync(item);
            shelf.Row.ItemContextRequested += item => ItemMenu.Show(Shell, this, item, changed: RefreshAsync);
        }

        shelf.Visible = false;
        _scroll.Content.Controls.Add(shelf);
    }

    public override Task EnterAsync() => _loaded ? Task.CompletedTask : LoadAsync();

    public override Task RefreshAsync()
    {
        _loaded = false;
        return LoadAsync();
    }

    private Task LoadAsync() => RunAsync(async cancellationToken =>
    {
        Shell.Busy("正在加载主页…");
        try
        {
            var views = await Host.Session
                .ExecuteAsync((client, token) => client.GetViewsAsync(token), cancellationToken)
                .ConfigureAwait(true);

            var libraries = views.Where(IsVideoLibrary).ToList();

            var resume = await Host.Session
                .ExecuteAsync((client, token) => client.GetResumeAsync(14, token), cancellationToken)
                .ConfigureAwait(true);

            var nextUp = await Host.Session
                .ExecuteAsync((client, token) => client.GetNextUpAsync(14, token), cancellationToken)
                .ConfigureAwait(true);

            _resume.SetItems(resume);
            _nextUp.SetItems(nextUp);
            _libraries.SetItems(libraries);

            _order.Clear();
            _order.Add(_resume);
            _order.Add(_nextUp);
            _order.Add(_libraries);

            foreach (var library in libraries.Take(MaximumLatestShelves))
            {
                var shelf = ShelfFor(library);
                var latest = await Host.Session
                    .ExecuteAsync((client, token) => client.GetLatestAsync(library.Id, 14, token), cancellationToken)
                    .ConfigureAwait(true);

                shelf.SetItems(latest);
                _order.Add(shelf);
            }

            // Shelves whose library vanished (or fell past the cap) stay parented but never lay out.
            foreach (var shelf in _latest.Values.Where(shelf => !_order.Contains(shelf)))
                shelf.Visible = false;

            _loaded = true;
            LayoutShelves();
            _scroll.ScrollToTop();
        }
        finally
        {
            Shell.Idle();
        }
    }, "加载主页失败");

    private PosterShelf ShelfFor(EmbyItem library)
    {
        if (_latest.TryGetValue(library.Id, out var existing)) return existing;

        var shelf = Portrait($"最近添加 · {library.Name}");
        shelf.MoreButton.Visible = true;
        shelf.MoreButton.Click += (_, _) => Shell.OpenLibrary(library);
        Register(shelf);
        _latest[library.Id] = shelf;
        return shelf;
    }

    /// <summary>Music, books and live TV cannot be handed to mpv as a single stream, so they are out.</summary>
    private static bool IsVideoLibrary(EmbyItem view) => view.CollectionType switch
    {
        "music" or "books" or "livetv" or "playlists" or "photos" => false,
        _ => true
    };

    private static string CollectionLabel(EmbyItem view) => view.CollectionType switch
    {
        "movies" => "电影",
        "tvshows" => "剧集",
        "homevideos" => "家庭视频",
        "musicvideos" => "音乐视频",
        "boxsets" => "合集",
        "trailers" => "预告片",
        "mixed" => "混合内容",
        _ => "媒体库"
    };

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutShelves();
    }

    private void LayoutShelves()
    {
        if (_laying) return;
        _laying = true;
        try
        {
            var padding = Dpi.Scale(this, 8);
            var gap = Dpi.Scale(this, 14);
            var width = _scroll.Content.Width - padding * 2;
            if (width <= 0) return;

            var y = padding;
            var shown = 0;

            foreach (var shelf in _order)
            {
                if (!shelf.Visible) continue;
                shelf.SetBounds(padding, y, width, shelf.Height);
                y = shelf.Bottom + gap;
                shown++;
            }

            _empty.Visible = _loaded && shown == 0;
            if (_empty.Visible)
            {
                _empty.SetBounds(Dpi.Scale(this, 24), Dpi.Scale(this, 24), width, Dpi.Scale(this, 24));
                y = _empty.Bottom + padding;
            }

            _scroll.Content.Height = Math.Max(y, 1);
            _scroll.RefreshExtent();
        }
        finally
        {
            _laying = false;
        }
    }
}
