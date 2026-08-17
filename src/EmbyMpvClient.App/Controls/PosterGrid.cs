using EmbyMpvClient.App.Theme;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Emby;

namespace EmbyMpvClient.App.Controls;

/// <summary>
/// A virtualised card grid for Emby items: draws every cell itself, fetches artwork for the
/// visible rows only, and scrolls with its own dark scrollbar.
/// <para>
/// v1 used a FlowLayoutPanel of PictureBoxes and downloaded every poster of every page up front,
/// which is why opening a 900-item library froze the window for half a minute. Here the layout is
/// arithmetic and the artwork requests follow the viewport.
/// </para>
/// </summary>
public sealed class PosterGrid : Control
{
    private const int PosterRadius = 8;

    private readonly Dictionary<string, Image?> _artwork = new(StringComparer.Ordinal);
    private readonly HashSet<string> _requested = new(StringComparer.Ordinal);
    private readonly SlimScrollBar _bar = new();
    private readonly WheelScroller _wheel;

    private readonly CancellationTokenSource _artworkLifetime = new();
    private IReadOnlyList<EmbyItem> _items = [];
    private EmbyImageStore? _images;
    private int _offset;
    private int _hoverIndex = -1;
    private int _selectedIndex = -1;
    public PosterGrid()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        BackColor = Palette.Window;
        Font = Fonts.Body;
        TabStop = true;

        Controls.Add(_bar);
        _bar.Scrolled += fraction => ScrollTo((int)Math.Round(fraction * MaximumOffset));
        _wheel = new WheelScroller(
            this,
            delta => ScrollBy(-delta / 120 * Dpi.Scale(this, Horizontal ? 220 : 110)),
            // A shelf lives inside a scrolling page: the plain wheel belongs to the page, so a
            // horizontal row only takes it with Shift held.
            () => !Horizontal || (ModifierKeys & Keys.Shift) != 0);
    }

    /// <summary>Raised when the user opens an item (click, Enter or double click).</summary>
    public event Action<EmbyItem>? ItemActivated;

    /// <summary>Raised on the secondary action — used for 「立即播放」 without opening the detail page.</summary>
    public event Action<EmbyItem>? ItemContextRequested;

    /// <summary>
    /// Raised when the user clicks the play badge in the middle of a card: a playable item
    /// starts straight away, without going through the detail page.
    /// </summary>
    public event Action<EmbyItem>? PlayRequested;

    /// <summary>Card width in device-independent pixels; the grid widens cells to fill the row.</summary>
    public int CardWidth { get; set; } = 170;

    /// <summary>Artwork aspect ratio as width/height: 2/3 for posters, 16/9 for episode stills.</summary>
    public double Aspect { get; set; } = 2 / 3d;

    /// <summary>Image types to try in order; the first one the item actually has is used.</summary>
    public string[] ImageTypes { get; set; } = [EmbyImageStore.Primary];

    /// <summary>One horizontally scrolling row, for the home page shelves.</summary>
    public bool Horizontal { get; set; }

    public bool ShowWatchedIndicators { get; set; } = true;

    /// <summary>Second line of each card; null uses <see cref="EmbyItem.CardSubtitle"/>.</summary>
    public Func<EmbyItem, string>? DescribeSubtitle { get; set; }

    public string EmptyMessage { get; set; } = "没有内容";

    public IReadOnlyList<EmbyItem> Items => _items;

    public EmbyItem? SelectedItem => _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;

    public void Bind(EmbyImageStore images) => _images = images;

    public void SetItems(IReadOnlyList<EmbyItem> items, bool keepScroll = false)
    {
        _items = items;
        _selectedIndex = items.Count == 0 ? -1 : Math.Clamp(_selectedIndex, -1, items.Count - 1);
        _hoverIndex = -1;
        if (!keepScroll) _offset = 0;

        ForgetArtworkFor(items);
        LayoutBar();
        Invalidate();
    }

    /// <summary>Drops cached bitmaps for items that are no longer on screen, keeping memory flat.</summary>
    private void ForgetArtworkFor(IReadOnlyList<EmbyItem> items)
    {
        var keep = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _artwork.Keys.ToList())
        {
            if (keep.Contains(key)) continue;
            _artwork[key]?.Dispose();
            _artwork.Remove(key);
            _requested.Remove(key);
        }
    }

    // ---- geometry ---------------------------------------------------------------

    private int Padding16 => Dpi.Scale(this, 16);

    private int Gap => Dpi.Scale(this, 14);

    private int TextHeight => Dpi.Scale(this, 4) + Fonts.Body.Height + Dpi.Scale(this, 2) + Fonts.Small.Height;

    private int BarThickness => Dpi.Scale(this, 10);

    private (int Columns, int CellWidth, int CellHeight) Metrics()
    {
        var preferred = Math.Max(Dpi.Scale(this, 90), Dpi.Scale(this, CardWidth));

        if (Horizontal)
        {
            var cellWidth = preferred;
            return (Math.Max(1, _items.Count), cellWidth, (int)(cellWidth / Aspect) + TextHeight);
        }

        var available = Math.Max(preferred, Width - Padding16 * 2 - BarThickness);
        var columns = Math.Max(1, (available + Gap) / (preferred + Gap));
        var width = (available - Gap * (columns - 1)) / columns;
        return (columns, width, (int)(width / Aspect) + TextHeight);
    }

    private int ContentExtent()
    {
        if (_items.Count == 0) return 0;
        var (columns, cellWidth, cellHeight) = Metrics();

        if (Horizontal) return Padding16 * 2 + _items.Count * (cellWidth + Gap) - Gap;

        var rows = (int)Math.Ceiling(_items.Count / (double)columns);
        return Padding16 * 2 + rows * (cellHeight + Gap) - Gap;
    }

    private int Viewport => Horizontal ? Width : Height;

    private int MaximumOffset => Math.Max(0, ContentExtent() - Viewport);

    private Rectangle CellBounds(int index)
    {
        var (columns, cellWidth, cellHeight) = Metrics();
        if (Horizontal)
            return new Rectangle(Padding16 + index * (cellWidth + Gap) - _offset, Padding16, cellWidth, cellHeight);

        var row = index / columns;
        var column = index % columns;
        return new Rectangle(
            Padding16 + column * (cellWidth + Gap),
            Padding16 + row * (cellHeight + Gap) - _offset,
            cellWidth,
            cellHeight);
    }

    private int IndexAt(Point location)
    {
        for (var index = 0; index < _items.Count; index++)
        {
            if (CellBounds(index).Contains(location)) return index;
        }

        return -1;
    }

    /// <summary>Preferred height for a horizontal shelf, so the caller can size the row.</summary>
    public int PreferredRowHeight()
    {
        var (_, _, cellHeight) = Metrics();
        return cellHeight + Padding16 * 2;
    }

    // ---- scrolling --------------------------------------------------------------

    public void ScrollBy(int amount) => ScrollTo(_offset + amount);

    private void ScrollTo(int value)
    {
        var clamped = Math.Clamp(value, 0, MaximumOffset);
        if (clamped == _offset) return;
        _offset = clamped;
        LayoutBar();
        Invalidate();
    }

    private void EnsureVisible(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var bounds = CellBounds(index);

        if (Horizontal)
        {
            if (bounds.X < Padding16) ScrollBy(bounds.X - Padding16);
            else if (bounds.Right > Width - Padding16) ScrollBy(bounds.Right - Width + Padding16);
            return;
        }

        if (bounds.Y < Padding16) ScrollBy(bounds.Y - Padding16);
        else if (bounds.Bottom > Height - Padding16) ScrollBy(bounds.Bottom - Height + Padding16);
    }

    private void LayoutBar()
    {
        var needed = !Horizontal && MaximumOffset > 0;
        _bar.Visible = needed;
        if (!needed) return;

        _bar.SetBounds(Width - BarThickness, 0, BarThickness, Height);
        _bar.BringToFront();
        _bar.Update(Height / (double)Math.Max(1, ContentExtent()), MaximumOffset == 0 ? 0 : _offset / (double)MaximumOffset);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _wheel.Attach();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _wheel.Detach();
        base.OnHandleDestroyed(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _offset = Math.Clamp(_offset, 0, MaximumOffset);
        LayoutBar();
    }

    // ---- input ------------------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var index = IndexAt(e.Location);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverIndex = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        var index = IndexAt(e.Location);
        if (index < 0) return;

        _selectedIndex = index;
        Invalidate();

        if (e.Button == MouseButtons.Left)
        {
            var item = _items[index];
            // The play badge is drawn over the middle of the poster on hover; clicking it
            // starts the playback, clicking anywhere else on the card opens the detail page.
            if (item.IsPlayable && PlayBadgeContains(PosterBounds(CellBounds(index)), e.Location)) PlayRequested?.Invoke(item);
            else ItemActivated?.Invoke(item);
        }
        else if (e.Button == MouseButtons.Right) ItemContextRequested?.Invoke(_items[index]);

        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData switch
    {
        Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.Enter => true,
        Keys.PageUp or Keys.PageDown => true,
        _ => base.IsInputKey(keyData)
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_items.Count == 0)
        {
            base.OnKeyDown(e);
            return;
        }

        var (columns, _, cellHeight) = Metrics();
        var page = Horizontal ? 1 : Math.Max(1, Height / Math.Max(1, cellHeight)) * columns;
        var target = _selectedIndex < 0 ? 0 : _selectedIndex;

        switch (e.KeyCode)
        {
            case Keys.Left: target--; break;
            case Keys.Right: target++; break;
            case Keys.Up: target -= Horizontal ? 1 : columns; break;
            case Keys.Down: target += Horizontal ? 1 : columns; break;
            case Keys.Home: target = 0; break;
            case Keys.End: target = _items.Count - 1; break;
            case Keys.PageUp: target -= page; break;
            case Keys.PageDown: target += page; break;
            case Keys.Enter:
                if (SelectedItem is { } item) ItemActivated?.Invoke(item);
                e.Handled = true;
                return;
            default:
                base.OnKeyDown(e);
                return;
        }

        e.Handled = true;
        _selectedIndex = Math.Clamp(target, 0, _items.Count - 1);
        EnsureVisible(_selectedIndex);
        Invalidate();
    }

    // ---- painting ---------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        e.Graphics.Clear(BackColor);

        if (_items.Count == 0)
        {
            Draw.Text(e.Graphics, EmptyMessage, Fonts.Subtitle, Palette.TextFaint, new Rectangle(0, 0, Width, Height), Draw.Centered);
            return;
        }

        var viewport = new Rectangle(0, 0, Width, Height);
        for (var index = 0; index < _items.Count; index++)
        {
            var bounds = CellBounds(index);
            if (!bounds.IntersectsWith(viewport)) continue;
            PaintCard(e.Graphics, _items[index], bounds, index);
        }
    }

    private void PaintCard(Graphics graphics, EmbyItem item, Rectangle bounds, int index)
    {
        var hovered = index == _hoverIndex;
        var selected = index == _selectedIndex;
        var poster = PosterBounds(bounds);

        Draw.Fill(graphics, poster, PosterRadius, Palette.SurfaceAlt);

        var image = Artwork(item, poster.Width);
        if (image is not null)
        {
            Draw.ImageCover(graphics, image, poster, PosterRadius);
        }
        else
        {
            Draw.Text(
                graphics,
                GlyphFor(item),
                Fonts.IconAt(Math.Max(18f, poster.Height / 6f)),
                Palette.TextFaint,
                poster,
                Draw.Centered);
        }

        if (hovered || selected)
        {
            Draw.Border(graphics, poster, PosterRadius, hovered ? Palette.Accent : Palette.BorderStrong, 2);
        }

        if (hovered && item.IsPlayable)
        {
            // A play affordance on hover, so a card reads as something you can start; folders
            // and other non-playable items keep the plain hover border.
            var badge = Dpi.Scale(this, 40);
            var circle = new Rectangle(poster.X + (poster.Width - badge) / 2, poster.Y + (poster.Height - badge) / 2, badge, badge);
            using var brush = new SolidBrush(Color.FromArgb(0xC8, Palette.Accent));
            graphics.FillEllipse(brush, circle);
            Draw.Text(graphics, Glyphs.Play, Fonts.IconAt(badge / 2.6f), Palette.TextOnAccent, circle, Draw.Centered);
        }

        PaintBadges(graphics, item, poster);

        if (item.HasResumePosition)
        {
            var height = Dpi.Scale(this, 4);
            var track = new Rectangle(
                poster.X + Dpi.Scale(this, 6),
                poster.Bottom - height - Dpi.Scale(this, 6),
                poster.Width - Dpi.Scale(this, 12),
                height);
            Draw.ProgressBar(graphics, track, item.ProgressFraction, Color.FromArgb(0xB0, Palette.Window), Palette.Accent);
        }

        var titleTop = poster.Bottom + Dpi.Scale(this, 4);
        Draw.Text(
            graphics,
            item.Name,
            Fonts.Body,
            selected || hovered ? Palette.Text : Palette.Mix(Palette.Text, Palette.TextDim, 0.25),
            new Rectangle(bounds.X, titleTop, bounds.Width, Fonts.Body.Height),
            Draw.SingleLine);

        var subtitle = DescribeSubtitle?.Invoke(item) ?? item.CardSubtitle;
        Draw.Text(
            graphics,
            subtitle,
            Fonts.Small,
            Palette.TextFaint,
            new Rectangle(bounds.X, titleTop + Fonts.Body.Height + Dpi.Scale(this, 2), bounds.Width, Fonts.Small.Height),
            Draw.SingleLine);
    }

    private void PaintBadges(Graphics graphics, EmbyItem item, Rectangle poster)
    {
        if (!ShowWatchedIndicators) return;

        var size = Dpi.Scale(this, 20);
        var margin = Dpi.Scale(this, 6);
        var corner = new Rectangle(poster.Right - size - margin, poster.Y + margin, size, size);

        if (item.IsWatched)
        {
            using var brush = new SolidBrush(Palette.Accent);
            graphics.FillEllipse(brush, corner);
            Draw.Text(graphics, Glyphs.Check, Fonts.IconAt(size / 2.2f), Palette.TextOnAccent, corner, Draw.Centered);
        }
        else if (item.UserData?.UnplayedItemCount is > 0)
        {
            var text = item.UserData.UnplayedItemCount!.Value.ToString();
            var width = Math.Max(size, Draw.Measure(text, Fonts.Small).Width + Dpi.Scale(this, 10));
            var badge = new Rectangle(poster.Right - width - margin, poster.Y + margin, width, size);
            Draw.Fill(graphics, badge, size / 2, Palette.Accent);
            Draw.Text(graphics, text, Fonts.Small, Palette.TextOnAccent, badge, Draw.Centered);
        }

        if (item.UserData?.IsFavorite == true)
        {
            var heart = new Rectangle(poster.X + margin, poster.Y + margin, size, size);
            Draw.Text(graphics, Glyphs.HeartFilled, Fonts.IconAt(size / 1.8f), Palette.Danger, heart, Draw.Centered);
        }
    }

    private static string GlyphFor(EmbyItem item) => item.Type switch
    {
        EmbyItemType.Series or EmbyItemType.Season or EmbyItemType.Episode => Glyphs.Series,
        EmbyItemType.MusicVideo => Glyphs.Music,
        EmbyItemType.BoxSet or EmbyItemType.Folder or EmbyItemType.CollectionFolder => Glyphs.Folder,
        _ => Glyphs.Movie
    };

    /// <summary>The poster area of a cell: the artwork, excluding the title and subtitle lines.</summary>
    private Rectangle PosterBounds(Rectangle bounds) =>
        new(bounds.X, bounds.Y, bounds.Width, bounds.Height - TextHeight);

    /// <summary>True when <paramref name="location"/> is inside the hover play badge of a poster.</summary>
    private bool PlayBadgeContains(Rectangle poster, Point location)
    {
        var radius = Dpi.Scale(this, 40) / 2d;
        var center = new Point(poster.X + poster.Width / 2, poster.Y + poster.Height / 2);
        var dx = location.X - center.X;
        var dy = location.Y - center.Y;
        return dx * dx + dy * dy <= radius * radius;
    }

    // ---- artwork ----------------------------------------------------------------

    private Image? Artwork(EmbyItem item, int width)
    {
        if (_artwork.TryGetValue(item.Id, out var cached)) return cached;
        if (_images is null || !_requested.Add(item.Id)) return null;

        var requestWidth = Math.Max(160, width * 2);
        _ = LoadArtworkAsync(item, requestWidth, _artworkLifetime.Token);
        return null;
    }

    private async Task LoadArtworkAsync(EmbyItem item, int width, CancellationToken cancellationToken)
    {
        var store = _images;
        if (store is null) return;

        try
        {
            byte[]? bytes = null;
            foreach (var type in ImageTypes.Concat([EmbyImageStore.Primary, EmbyImageStore.Thumb, EmbyImageStore.Backdrop]).Distinct())
            {
                bytes = await store.GetAsync(item, type, width, cancellationToken).ConfigureAwait(false);
                if (bytes is { Length: > 0 }) break;
            }

            var image = bytes is { Length: > 0 } ? Decode(bytes) : null;
            if (cancellationToken.IsCancellationRequested)
            {
                image?.Dispose();
                return;
            }

            if (!IsHandleCreated)
            {
                image?.Dispose();
                return;
            }

            BeginInvoke(() =>
            {
                if (IsDisposed) { image?.Dispose(); return; }
                _artwork[item.Id] = image;
                Invalidate();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Debug("ui", $"加载封面失败（{item.Name}）：{error.Message}");
        }
    }

    /// <summary>
    /// Copies into a standalone bitmap so the stream can be closed straight away: an Image
    /// created over a MemoryStream keeps a reference to it and throws later if it is disposed.
    /// </summary>
    private static Image? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            return new Bitmap(decoded);
        }
        catch (ArgumentException)
        {
            // Not an image the server could render; the placeholder glyph is the answer.
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _artworkLifetime.Cancel();
            _artworkLifetime.Dispose();
            foreach (var image in _artwork.Values) image?.Dispose();
            _artwork.Clear();
        }

        base.Dispose(disposing);
    }
}

/// <summary>A titled shelf: heading, optional "more" button and one horizontally scrolling row.</summary>
public sealed class PosterShelf : Control
{
    private readonly PosterGrid _row = new() { Horizontal = true };
    private readonly FlatButton _more = new() { Variant = ButtonVariant.Ghost, Glyph = Glyphs.ChevronRight, Visible = false };

    public PosterShelf(string title, EmbyImageStore images)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Window;
        Text = title;
        Font = Fonts.Subtitle;

        _row.Bind(images);
        _row.BackColor = Palette.Window;
        Controls.Add(_row);
        Controls.Add(_more);
    }

    public PosterGrid Row => _row;

    public FlatButton MoreButton => _more;

    public int HeaderHeight => Dpi.Scale(this, 34);

    public void SetItems(IReadOnlyList<EmbyItem> items)
    {
        _row.SetItems(items);
        Visible = items.Count > 0;
        Height = HeaderHeight + _row.PreferredRowHeight();
        PerformLayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _row.SetBounds(0, HeaderHeight, Width, Math.Max(10, Height - HeaderHeight));
        _more.SetBounds(Width - Dpi.Scale(this, 34), 0, Dpi.Scale(this, 30), Dpi.Scale(this, 26));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        Draw.Text(
            e.Graphics,
            Text,
            Fonts.Subtitle,
            Palette.Text,
            new Rectangle(Dpi.Scale(this, 16), 0, Width - Dpi.Scale(this, 60), HeaderHeight),
            Draw.LeftMiddle);
    }
}
