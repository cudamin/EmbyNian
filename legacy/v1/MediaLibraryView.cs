using System.Diagnostics;

namespace EmbyMpvClient;

internal sealed class MediaLibraryView : UserControl
{
    private readonly Func<EmbyApiClient> _getApi;
    private readonly AppSettings _settings;
    private readonly Action<string> _setStatus;
    private readonly FlowLayoutPanel _libraries = new() { Dock = DockStyle.Top, Height = 48, WrapContents = false, AutoScroll = true };
    private readonly ListView _items = new() { Dock = DockStyle.Fill, View = View.LargeIcon, MultiSelect = false, HideSelection = false };
    private readonly ImageList _posters = new() { ImageSize = new Size(168, 252), ColorDepth = ColorDepth.Depth32Bit };
    private readonly TextBox _search = UiTheme.CreateTextBox("搜索电影、剧集或单集");
    private readonly Stack<(string Id, string Title)> _history = new();
    private CancellationTokenSource? _posterCancellation;

    public MediaLibraryView(Func<EmbyApiClient> getApi, AppSettings settings, Action<string> setStatus)
    {
        _getApi = getApi;
        _settings = settings;
        _setStatus = setStatus;
        BackColor = UiTheme.Background;
        Padding = new Padding(28, 8, 28, 24);

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 52 };
        _search.SetBounds(0, 1, 340, 36);
        _search.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SearchAsync(); } };
        var searchButton = UiTheme.CreateButton("搜索", true);
        searchButton.Location = new Point(352, 0);
        searchButton.Click += async (_, _) => await SearchAsync();
        var refreshButton = UiTheme.CreateButton("刷新");
        refreshButton.Location = new Point(446, 0);
        refreshButton.Click += async (_, _) => await LoadLibrariesAsync();
        toolbar.Controls.AddRange([_search, searchButton, refreshButton]);

        _libraries.BackColor = UiTheme.Background;
        var back = UiTheme.CreateButton("返回");
        back.Click += async (_, _) => await GoBackAsync();
        _libraries.Controls.Add(back);

        _items.LargeImageList = _posters;
        _items.TileSize = new Size(198, 304);
        _items.BackColor = UiTheme.Background;
        _items.ForeColor = UiTheme.Text;
        _items.BorderStyle = BorderStyle.None;
        _items.Alignment = ListViewAlignment.Top;
        _items.AutoArrange = true;
        _items.Font = UiTheme.CreateFont(9.5F);
        _items.DoubleClick += async (_, _) => await OpenSelectedAsync();

        Controls.Add(_items);
        Controls.Add(_libraries);
        Controls.Add(toolbar);
    }

    public async Task LoadLibrariesAsync()
    {
        try
        {
            _setStatus("正在读取媒体库...");
            var views = await _getApi().GetViewsAsync();
            while (_libraries.Controls.Count > 1) _libraries.Controls.RemoveAt(1);
            foreach (var view in views)
            {
                var button = UiTheme.CreateButton(view.Name);
                button.Click += async (_, _) => await LoadItemsAsync(view.Id, view.Name, true);
                _libraries.Controls.Add(button);
            }
            _history.Clear();
            if (views.Count > 0) await LoadItemsAsync(views[0].Id, views[0].Name, true);
            else _setStatus("没有找到媒体库");
        }
        catch (Exception ex) { ShowError("读取媒体库失败，请检查连接信息", ex); }
    }

    private async Task LoadItemsAsync(string parentId, string title, bool addHistory)
    {
        try
        {
            _setStatus($"正在载入 {title}...");
            var items = await _getApi().GetItemsAsync(parentId);
            if (addHistory && (_history.Count == 0 || _history.Peek().Id != parentId)) _history.Push((parentId, title));
            PopulateItems(items);
            _setStatus($"{title}：{items.Count} 项");
        }
        catch (Exception ex) { ShowError("载入项目失败", ex); }
    }

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(_search.Text)) { await LoadLibrariesAsync(); return; }
        try
        {
            _setStatus("正在搜索...");
            var items = await _getApi().GetItemsAsync(search: _search.Text.Trim());
            PopulateItems(items);
            _setStatus($"搜索结果：{items.Count} 项");
        }
        catch (Exception ex) { ShowError("搜索失败", ex); }
    }

    private async Task OpenSelectedAsync()
    {
        if (_items.SelectedItems.Count == 0 || _items.SelectedItems[0].Tag is not EmbyItem item) return;
        if (item.Type is "Movie" or "Episode" or "Video" or "MusicVideo") { Play(item); return; }
        await LoadItemsAsync(item.Id, item.Name, true);
    }

    private async Task GoBackAsync()
    {
        if (_history.Count > 1)
        {
            _history.Pop();
            var previous = _history.Peek();
            await LoadItemsAsync(previous.Id, previous.Title, false);
        }
        else await LoadLibrariesAsync();
    }

    private void Play(EmbyItem item)
    {
        if (!File.Exists(_settings.MpvPath))
        {
            MessageBox.Show(this, "找不到 mpv.exe，请在“连接与路径”中设置。", "路径错误");
            return;
        }
        var startInfo = new ProcessStartInfo { FileName = _settings.MpvPath, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_settings.MpvPath)! };
        startInfo.ArgumentList.Add(_getApi().GetStreamUrl(item));
        startInfo.ArgumentList.Add($"--title={item.Name}");
        startInfo.ArgumentList.Add("--force-media-title=" + item.Name);
        Process.Start(startInfo);
        _setStatus("正在使用 MPV 播放：" + item.Name);
    }

    private void PopulateItems(IReadOnlyList<EmbyItem> items)
    {
        _posterCancellation?.Cancel();
        _posterCancellation?.Dispose();
        _posterCancellation = new CancellationTokenSource();
        _items.BeginUpdate();
        _items.Items.Clear();
        foreach (Image image in _posters.Images) image.Dispose();
        _posters.Images.Clear();
        _posters.Images.Add(CreatePlaceholder());
        foreach (var item in items)
        {
            var metadata = string.Join("  ·  ", new[] { item.ProductionYear?.ToString(), item.DisplayType }.Where(value => !string.IsNullOrWhiteSpace(value)));
            _items.Items.Add(new ListViewItem(string.IsNullOrEmpty(metadata) ? item.Name : $"{item.Name}\n{metadata}", 0) { Tag = item });
        }
        _items.EndUpdate();
        _ = LoadPostersAsync(items, _posterCancellation.Token);
    }

    private async Task LoadPostersAsync(IReadOnlyList<EmbyItem> items, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(6);
        var tasks = items.Select(async (item, index) =>
        {
            await gate.WaitAsync(token);
            try
            {
                var bytes = await _getApi().GetImageAsync(item, cancellationToken: token);
                await using var stream = new MemoryStream(bytes);
                using var source = Image.FromStream(stream);
                var poster = new Bitmap(source, _posters.ImageSize);
                if (token.IsCancellationRequested) { poster.Dispose(); return; }
                BeginInvoke(() =>
                {
                    if (token.IsCancellationRequested || index >= _items.Items.Count) { poster.Dispose(); return; }
                    _posters.Images.Add(poster);
                    _items.Items[index].ImageIndex = _posters.Images.Count - 1;
                });
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { gate.Release(); }
        });
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
    }

    private static Bitmap CreatePlaceholder()
    {
        var bitmap = new Bitmap(168, 252);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(UiTheme.SurfaceRaised);
        using var brush = new SolidBrush(UiTheme.TextMuted);
        using var font = UiTheme.CreateFont(10F);
        var text = "正在加载";
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, brush, (168 - size.Width) / 2, (252 - size.Height) / 2);
        return bitmap;
    }

    private void ShowError(string title, Exception ex)
    {
        _setStatus(title);
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _posterCancellation?.Cancel(); _posterCancellation?.Dispose(); _posters.Dispose(); }
        base.Dispose(disposing);
    }
}
