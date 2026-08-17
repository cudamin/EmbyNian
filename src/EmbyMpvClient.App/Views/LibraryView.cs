using EmbyMpvClient.App.Controls;
using EmbyMpvClient.App.Theme;
using EmbyMpvClient.Emby;

namespace EmbyMpvClient.App.Views;

/// <summary>
/// Browses one library (or any folder) as a paged grid, and doubles as the library picker when no
/// folder is set. Sorting, the unwatched filter and paging are all server-side — v1 pulled every
/// item and filtered in memory, which is why 「仅未观看」 used to take as long as a full refresh.
/// </summary>
public sealed class LibraryView : AppView
{
    private readonly FlatButton _up = new()
    {
        Variant = ButtonVariant.Ghost,
        Glyph = Glyphs.ChevronLeft,
        Text = "全部媒体库"
    };

    private readonly TextBlock _title = new("", Fonts.Subtitle, Palette.Text);
    private readonly DropDown _sort = new();
    private readonly FlatButton _direction = new() { Variant = ButtonVariant.Secondary, Glyph = Glyphs.ChevronDown, Text = "升序" };
    private readonly FlatButton _unwatched = new() { Variant = ButtonVariant.Secondary, Glyph = Glyphs.Unwatched, Text = "仅未观看" };
    private readonly PosterGrid _grid = new();

    private readonly FlatButton _previous = new() { Variant = ButtonVariant.Secondary, Glyph = Glyphs.ChevronLeft, Text = "上一页" };
    private readonly FlatButton _next = new() { Variant = ButtonVariant.Secondary, Glyph = Glyphs.ChevronRight, Text = "下一页" };
    private readonly TextBlock _pageLabel = new("", Fonts.Small, Palette.TextDim);

    private EmbyItem? _target;
    private string _sortBy = EmbySortBy.Name;
    private bool _descending;
    private bool _unwatchedOnly;
    private int _start;
    private int _total;
    private bool _loaded;

    public LibraryView(IShell shell) : base(shell)
    {
        HeaderTitle = "媒体库";

        _grid.Bind(Host.Images);
        _grid.CardWidth = Host.Settings.Ui.PosterWidth;
        _grid.ShowWatchedIndicators = Host.Settings.Ui.ShowWatchedIndicators;
        _grid.EmptyMessage = "这个位置没有内容";
        _grid.ItemActivated += item => Shell.Open(item);
        _grid.PlayRequested += item => Shell.PlayAsync(item);
        _grid.ItemContextRequested += item => ItemMenu.Show(Shell, this, item, _target, ReloadPageAsync);

        _sort.Describe = value => value is SortOption option ? option.Label : value.ToString() ?? "";
        _sort.Fill(EmbySortBy.Choices.Select(choice => (object)new SortOption(choice.Label, choice.Value)).ToList());
        _sort.SelectedIndexChanged += (_, _) =>
        {
            if (_sort.SelectedItem is not SortOption option || option.Value == _sortBy) return;
            _sortBy = option.Value;
            _start = 0;
            Run(_ => LoadAsync(), "加载媒体库失败");
        };

        _direction.Click += (_, _) =>
        {
            _descending = !_descending;
            _direction.Text = _descending ? "降序" : "升序";
            _start = 0;
            Run(_ => LoadAsync(), "加载媒体库失败");
        };

        _unwatched.Click += (_, _) =>
        {
            _unwatchedOnly = !_unwatchedOnly;
            _unwatched.Checked = _unwatchedOnly;
            _start = 0;
            Run(_ => LoadAsync(), "加载媒体库失败");
        };

        _up.Click += (_, _) => Shell.OpenLibrary(null);
        _previous.Click += (_, _) => Page(-1);
        _next.Click += (_, _) => Page(1);

        foreach (var child in new Control[] { _up, _title, _sort, _direction, _unwatched, _grid, _previous, _next, _pageLabel })
            Controls.Add(child);
    }

    /// <summary>The folder being browsed; null shows the library picker.</summary>
    public EmbyItem? Target
    {
        get => _target;
        set
        {
            if (_loaded && _target?.Id == value?.Id) return;
            _target = value;
            _start = 0;
            _loaded = false;
        }
    }

    private int PageSize => Math.Clamp(Host.Settings.Ui.PageSize, 20, 400);

    private sealed record SortOption(string Label, string Value);

    public override Task EnterAsync() => _loaded ? Task.CompletedTask : LoadAsync();

    public override Task RefreshAsync()
    {
        _loaded = false;
        return LoadAsync();
    }

    private Task ReloadPageAsync() => LoadAsync();

    private void Page(int direction)
    {
        var next = _start + direction * PageSize;
        if (next < 0 || next >= Math.Max(1, _total)) return;
        _start = next;
        Run(_ => LoadAsync(), "加载媒体库失败");
    }

    private Task LoadAsync() => RunAsync(async cancellationToken =>
    {
        Shell.Busy();
        try
        {
            if (_target is null)
            {
                await LoadPickerAsync(cancellationToken).ConfigureAwait(true);
                return;
            }

            HeaderTitle = _target.Name;
            _title.Text = _target.Name;
            Host.Settings.Ui.LastLibraryId = _target.Id;

            var query = new ItemQuery
            {
                ParentId = _target.Id,
                StartIndex = _start,
                Limit = PageSize,
                SortBy = _sortBy,
                Descending = _descending,
                IsPlayed = _unwatchedOnly ? false : null
            };

            var result = await Host.Session
                .ExecuteAsync((client, token) => client.GetItemsAsync(query, token), cancellationToken)
                .ConfigureAwait(true);

            _total = result.TotalRecordCount;
            _grid.DescribeSubtitle = null;
            _grid.Aspect = 2 / 3d;
            _grid.ImageTypes = [EmbyImageStore.Primary];
            _grid.SetItems(result.Items);
            _loaded = true;
            ShowChrome(picker: false);
            PerformLayout();
            Invalidate(true);
        }
        finally
        {
            Shell.Idle();
        }
    }, "加载媒体库失败");

    private async Task LoadPickerAsync(CancellationToken cancellationToken)
    {
        HeaderTitle = "媒体库";
        _title.Text = "全部媒体库";

        var views = await Host.Session
            .ExecuteAsync((client, token) => client.GetViewsAsync(token), cancellationToken)
            .ConfigureAwait(true);

        _total = views.Count;
        _grid.Aspect = 16 / 9d;
        _grid.ImageTypes = [EmbyImageStore.Primary, EmbyImageStore.Thumb, EmbyImageStore.Backdrop];
        _grid.DescribeSubtitle = view => view.ChildCount is { } count ? $"{count} 项" : "";
        _grid.SetItems(views);
        _loaded = true;
        ShowChrome(picker: true);
        PerformLayout();
        Invalidate(true);
    }

    private void ShowChrome(bool picker)
    {
        _up.Visible = !picker;
        _sort.Visible = !picker;
        _direction.Visible = !picker;
        _unwatched.Visible = !picker;

        var paged = !picker && _total > PageSize;
        _previous.Visible = paged;
        _next.Visible = paged;
        _pageLabel.Visible = !picker;

        _previous.Enabled = _start > 0;
        _next.Enabled = _start + PageSize < _total;

        _pageLabel.Text = picker
            ? $"共 {_total} 个媒体库"
            : _total == 0
                ? "没有符合条件的内容"
                : $"第 {_start + 1}–{Math.Min(_start + PageSize, _total)} 项，共 {_total} 项";

        _unwatched.Checked = _unwatchedOnly;
        _direction.Text = _descending ? "降序" : "升序";
        LayoutChrome();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutChrome();
    }

    private void LayoutChrome()
    {
        var padding = Dpi.Scale(this, 20);
        var gap = Dpi.Scale(this, 8);
        var rowHeight = Dpi.Scale(this, 30);
        var toolbar = Dpi.Scale(this, 52);
        var footer = _pageLabel.Visible ? Dpi.Scale(this, 40) : 0;

        var x = padding;
        var top = (toolbar - rowHeight) / 2;

        if (_up.Visible)
        {
            _up.AutoSizeToContent(minimumWidth: Dpi.Scale(this, 96));
            _up.SetBounds(x, top, _up.Width, rowHeight);
            x = _up.Right + gap;
        }

        var right = Width - padding;

        if (_unwatched.Visible)
        {
            _unwatched.AutoSizeToContent(minimumWidth: Dpi.Scale(this, 96));
            _unwatched.SetBounds(right - _unwatched.Width, top, _unwatched.Width, rowHeight);
            right -= _unwatched.Width + gap;
        }

        if (_direction.Visible)
        {
            _direction.AutoSizeToContent(minimumWidth: Dpi.Scale(this, 76));
            _direction.SetBounds(right - _direction.Width, top, _direction.Width, rowHeight);
            right -= _direction.Width + gap;
        }

        if (_sort.Visible)
        {
            var width = Dpi.Scale(this, 130);
            _sort.SetBounds(right - width, top + Dpi.Scale(this, 2), width, rowHeight - Dpi.Scale(this, 4));
            right -= width + gap;
        }

        _title.SetBounds(x, top, Math.Max(Dpi.Scale(this, 60), right - x - gap), rowHeight);

        _grid.SetBounds(0, toolbar, Width, Math.Max(Dpi.Scale(this, 40), Height - toolbar - footer));

        if (footer == 0) return;

        var footerTop = Height - footer + (footer - rowHeight) / 2;
        _pageLabel.SetBounds(padding, footerTop, Math.Max(Dpi.Scale(this, 80), Width / 2), rowHeight);

        var buttonWidth = Dpi.Scale(this, 88);
        _next.SetBounds(Width - padding - buttonWidth, footerTop, buttonWidth, rowHeight);
        _previous.SetBounds(_next.Left - gap - buttonWidth, footerTop, buttonWidth, rowHeight);
    }
}
