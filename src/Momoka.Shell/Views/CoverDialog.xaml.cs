using System.Collections.ObjectModel;
using System.ComponentModel;
using Momoka.Diagnostics;
using Momoka.Emby;
using Momoka.Shell.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Momoka.Shell.Views;

/// <summary>一种图片的一格；背景图的序号和标签共同标识所见图片。</summary>
public sealed partial class ArtworkSlot : INotifyPropertyChanged
{
    private BitmapImage? _picture;
    private bool _busy;
    private int _generation;

    public ArtworkSlot(ArtworkKind kind, int index, string? tag)
    {
        Kind = kind;
        Index = index;
        Tag = tag;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ArtworkKind Kind { get; }
    public int Index { get; }
    public string? Tag { get; private set; }
    public string ImageType => Kind.ImageType;
    public bool Has => !string.IsNullOrEmpty(Tag);
    public string Caption => Kind.Many ? $"{Kind.Name} {Index + 1}" : Kind.Name;
    public string ViewName => $"查看{Caption}";
    public string DeleteName => $"删除{Caption}";
    public double FrameHeight => Kind.FrameHeight(Kind.Many ? 188 : 140);
    public string Hint => Kind.Many ? "另加一张背景图（保留现有图片）"
        : Has ? $"换一张{Kind.Name}" : $"还没有{Kind.Name}，点这里加一张";

    public BitmapImage? Picture
    {
        get => _picture;
        private set
        {
            if (ReferenceEquals(_picture, value)) return;
            _picture = value;
            Raise(nameof(Picture));
        }
    }

    public Visibility HasPicture => Has ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyMark => Has ? Visibility.Collapsed : Visibility.Visible;
    public bool Ready => !Busy;
    public bool CanDelete => Ready && Has;

    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            Raise(nameof(Busy));
            Raise(nameof(Ready));
            Raise(nameof(CanDelete));
        }
    }

    public void Retag(string? tag)
    {
        if (string.Equals(Tag, tag, StringComparison.Ordinal)) return;
        _generation++;
        Tag = tag;
        Picture = null;
        Raise(nameof(Has));
        Raise(nameof(HasPicture));
        Raise(nameof(EmptyMark));
        Raise(nameof(Hint));
        Raise(nameof(CanDelete));
    }

    internal void CancelLoad() => _generation++;

    internal async Task LoadAsync(Func<string, Task<byte[]?>> fetch)
    {
        if (Tag is not { Length: > 0 } tag) return;
        var generation = ++_generation;
        try
        {
            var bytes = await fetch(tag).ConfigureAwait(true);
            if (generation != _generation || bytes is not { Length: > 0 }) return;
            var picture = await PosterLoader.DecodeAsync(bytes, 140).ConfigureAwait(true);
            if (generation == _generation) Picture = picture;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log.Debug("ui", $"面板上一格取不回来（{ImageType}）：{error.Message}"); }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public readonly record struct ArtworkTarget(string ImageType, int Index);

/// <summary>封面管理。子面板复用这一张 ContentDialog，服务器能力均由固定身份的委托传入。</summary>
public sealed partial class CoverDialog : ContentDialog
{
    private const string Category = "ui";
    private const int ThumbnailWidth = 140;
    private const int ViewerWidth = 1280;
    private readonly EmbyItem _item;
    private readonly FindArtwork _find;
    private readonly FetchArtwork _fetch;
    private readonly SwapArtwork _swap;
    private readonly DeleteArtwork _delete;
    private readonly UploadArtwork _upload;
    private readonly FetchRemote _fetchRemote;
    private readonly Func<string, Task<EmbyItem?>> _reload;
    private readonly List<ArtworkSlot> _slots = [];
    private bool _busy;
    private bool _closed;
    private bool _uncertain;
    private TaskCompletionSource<ContentDialogResult>? _panelCompletion;
    private Task _initialLoad = Task.CompletedTask;

    public bool Touched { get; private set; }
    public bool NeedsRefresh { get; private set; }
    public ObservableCollection<ArtworkSlot> Groups { get; } = [];
    public ObservableCollection<ArtworkSlot> Backdrops { get; } = [];

    public CoverDialog(EmbyItem item, FindArtwork find, FetchArtwork fetch, SwapArtwork swap,
        DeleteArtwork delete, UploadArtwork upload, Func<string, Task<EmbyItem?>> reload, FetchRemote fetchRemote)
    {
        InitializeComponent();
        _item = item;
        _find = find;
        _fetch = fetch;
        _swap = swap;
        _delete = delete;
        _upload = upload;
        _reload = reload;
        _fetchRemote = fetchRemote;
        RequestedTheme = ThemeHost.Current.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        Title = $"修改媒体封面图 — {item.Name}";
        foreach (var kind in ItemArtwork.SingleKinds)
            Add(Groups, new ArtworkSlot(kind, 0, ItemArtwork.TagsOf(item, kind.ImageType).FirstOrDefault()));
        Rebuild(item);
        Opened += (_, _) =>
        {
            _closed = false;
            _initialLoad = LoadAsync();
        };
        Closing += (_, args) =>
        {
            if (_panelCompletion is { } panel)
            {
                args.Cancel = true;
                panel.TrySetResult(ContentDialogResult.None);
            }
            else if (_busy) args.Cancel = true;
        };
        Closed += (_, _) =>
        {
            _closed = true;
            foreach (var slot in _slots) slot.CancelLoad();
        };
    }

    private void Add(ObservableCollection<ArtworkSlot> into, ArtworkSlot slot)
    {
        into.Add(slot);
        _slots.Add(slot);
        slot.Busy = _busy;
    }

    private async Task LoadAsync()
    {
        foreach (var slot in _slots.ToArray())
        {
            if (_closed) return;
            await slot.LoadAsync(tag => _fetch(_item.Id, slot.ImageType, tag, ThumbnailWidth,
                slot.Kind.Many ? slot.Index : null)).ConfigureAwait(true);
        }
    }

    internal Task<byte[]?> FetchRemoteAsync(string address) => _closed
        ? Task.FromCanceled<byte[]?>(new CancellationToken(true)) : _fetchRemote(address);
    internal async Task<PickedArtwork?> PickArtworkAsync() => await ArtworkFile.PickAsync(XamlRoot).ConfigureAwait(true);
    private static ArtworkSlot? SlotOf(object sender) => (sender as FrameworkElement)?.Tag as ArtworkSlot;

    private bool BeginOperation(bool refresh = false)
    {
        if (_closed || _busy || (_uncertain && !refresh)) return false;
        _busy = true;
        foreach (var slot in _slots) slot.Busy = true;
        AddBackdropButton.IsEnabled = false;
        RefreshArtworkButton.IsEnabled = false;
        IsPrimaryButtonEnabled = false;
        Notice.Visibility = Visibility.Collapsed;
        return true;
    }

    private void EndOperation()
    {
        _busy = false;
        foreach (var slot in _slots) slot.Busy = _uncertain;
        AddBackdropButton.IsEnabled = !_uncertain;
        RefreshArtworkButton.IsEnabled = true;
        IsPrimaryButtonEnabled = true;
    }

    private async Task OperateAsync(string failure, Func<Task> action, bool refresh = false)
    {
        if (!BeginOperation(refresh)) return;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ShowProblem("登录身份已改变，请关闭此面板后重新打开。");
        }
        catch (Exception error)
        {
            Log.Warn(Category, failure, error);
            ShowProblem($"{failure}：{Failure.Describe(error)}"
                + (_uncertain ? "。结果尚不明确，请先刷新图片列表，不要重复提交。" : ""));
        }
        finally { EndOperation(); }
    }

    private async Task WriteAsync(Func<Task> write)
    {
        _uncertain = true;
        NeedsRefresh = true;
        await write().ConfigureAwait(true);
        Touched = true;
        await RefreshAsync().ConfigureAwait(true);
    }

    private async void OnSwap(object sender, RoutedEventArgs e)
    {
        if (SlotOf(sender) is not { } slot) return;
        await OperateAsync("换封面图失败", async () =>
        {
            var found = await _find(_item.Id, slot.ImageType).ConfigureAwait(true);
            var picker = new CoverPickerDialog(_item, slot.Kind, found, this);
            var result = await ShowPanelAsync(picker).ConfigureAwait(true);
            if (result != ContentDialogResult.Primary) return;
            if (picker.Uploaded is { Bytes.Length: > 0 } uploaded)
                await WriteAsync(() => _upload(_item.Id, slot.ImageType, uploaded)).ConfigureAwait(true);
            else if (picker.Picked is { } chosen)
                await WriteAsync(() => _swap(_item.Id, slot.ImageType, slot.Index, chosen)).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async void OnView(object sender, RoutedEventArgs e)
    {
        if (SlotOf(sender) is not { Tag.Length: > 0 } slot) return;
        await OperateAsync("看大图失败", async () =>
        {
            var bytes = await _fetch(_item.Id, slot.ImageType, slot.Tag!, ViewerWidth,
                slot.Kind.Many ? slot.Index : null).ConfigureAwait(true);
            if (bytes is not { Length: > 0 }) return;
            var picture = await PosterLoader.DecodeAsync(bytes, ViewerWidth).ConfigureAwait(true);
            if (picture is not null)
                await ShowPanelAsync(new ArtworkViewer(slot.Caption, picture)).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (SlotOf(sender) is not { Has: true } slot) return;
        await OperateAsync("删封面图失败", async () =>
        {
            var tag = slot.Tag;
            var tags = slot.Kind.Many ? Backdrops.Select(row => row.Tag).ToArray() : [tag];
            if (await AskAsync($"删掉这一张{slot.Caption}？", DeleteWarning(slot), "删掉") != ContentDialogResult.Primary)
                return;
            var fresh = await _reload(_item.Id).ConfigureAwait(true)
                ?? throw new InvalidOperationException("无法核对当前图片，请先刷新");
            var current = ItemArtwork.TagsOf(fresh, slot.ImageType);
            if (!tags.SequenceEqual(current, StringComparer.Ordinal)
                || string.IsNullOrEmpty(tag) || current.Count(value => value == tag) != 1)
            {
                await RefreshAsync().ConfigureAwait(true);
                throw new InvalidOperationException("图片列表已改变或标签不唯一，请确认新列表后再删除");
            }
            await WriteAsync(() => _delete(_item.Id, slot.ImageType, slot.Kind.Many ? slot.Index : null)).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async void OnAddBackdrop(object sender, RoutedEventArgs e) => await OperateAsync("上传背景图失败", async () =>
    {
        if (await PickArtworkAsync() is not { Bytes.Length: > 0 } picked) return;
        if (ItemArtwork.MultipleKinds.FirstOrDefault() is not { ImageType.Length: > 0 } kind) return;
        await WriteAsync(() => _upload(_item.Id, kind.ImageType, picked)).ConfigureAwait(true);
    }).ConfigureAwait(true);

    private async void OnRefreshArtwork(object sender, RoutedEventArgs e) =>
        await OperateAsync("刷新图片失败", RefreshAsync, refresh: true).ConfigureAwait(true);

    private async Task RefreshAsync()
    {
        var fresh = await _reload(_item.Id).ConfigureAwait(true)
            ?? throw new InvalidOperationException("服务器没有返回图片列表");
        if (_closed) return;
        foreach (var slot in Groups)
            slot.Retag(ItemArtwork.TagsOf(fresh, slot.ImageType).FirstOrDefault());
        Rebuild(fresh);
        _uncertain = false;
        await LoadAsync().ConfigureAwait(true);
    }

    private void Rebuild(EmbyItem fresh)
    {
        var desired = ItemArtwork.MultipleKinds.SelectMany(kind => ItemArtwork.TagsOf(fresh, kind.ImageType)
            .Select((tag, index) => (Kind: kind, Tag: tag, Index: index))).ToArray();
        for (var index = 0; index < desired.Length; index++)
        {
            var row = desired[index];
            if (index < Backdrops.Count && Backdrops[index].ImageType == row.Kind.ImageType)
                Backdrops[index].Retag(row.Tag);
            else Add(Backdrops, new ArtworkSlot(row.Kind, row.Index, row.Tag));
        }
        while (Backdrops.Count > desired.Length)
        {
            var slot = Backdrops[^1];
            slot.Retag(null);
            slot.CancelLoad();
            _slots.Remove(slot);
            Backdrops.RemoveAt(Backdrops.Count - 1);
        }
    }

    private static string DeleteWarning(ArtworkSlot slot) =>
        $"服务器上这一张「{slot.Caption}」会被删掉，删了就找不回来。使用它的海报、徽标或背景也会改变。";

    private Task<ContentDialogResult> AskAsync(string title, string body, string? confirm = null) => ShowPanelAsync(new ContentDialog
    {
        Title = title,
        Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
        PrimaryButtonText = confirm ?? "确定",
        CloseButtonText = "算了",
        DefaultButton = ContentDialogButton.Close
    });

    private void ShowProblem(string message)
    {
        if (_closed) return;
        Notice.Text = message;
        Notice.Visibility = Visibility.Visible;
    }

    // WinUI 同一 XamlRoot 只能有一张 ContentDialog；选择、查看、确认复用其内容区和命令按钮。
    private async Task<ContentDialogResult> ShowPanelAsync(ContentDialog panel)
    {
        if (_closed || _panelCompletion is not null) return ContentDialogResult.None;
        var saved = (Content, Title, PrimaryButtonText, SecondaryButtonText, CloseButtonText, DefaultButton, IsPrimaryButtonEnabled);
        var content = panel.Content;
        var completion = new TaskCompletionSource<ContentDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _panelCompletion = completion;
        panel.Content = null;
        Content = content;
        Title = panel.Title;
        PrimaryButtonText = panel.PrimaryButtonText;
        SecondaryButtonText = panel.SecondaryButtonText;
        CloseButtonText = string.IsNullOrEmpty(panel.CloseButtonText) ? "返回" : panel.CloseButtonText;
        DefaultButton = panel.DefaultButton;
        IsPrimaryButtonEnabled = panel.IsPrimaryButtonEnabled;
        var registration = panel.RegisterPropertyChangedCallback(IsPrimaryButtonEnabledProperty,
            (_, _) => IsPrimaryButtonEnabled = panel.IsPrimaryButtonEnabled);
        void Primary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        { args.Cancel = true; completion.TrySetResult(ContentDialogResult.Primary); }
        void Secondary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        { args.Cancel = true; completion.TrySetResult(ContentDialogResult.Secondary); }
        void Uploaded() => completion.TrySetResult(ContentDialogResult.Primary);
        PrimaryButtonClick += Primary;
        SecondaryButtonClick += Secondary;
        if (panel is CoverPickerDialog picker) picker.UploadCompleted += Uploaded;
        try { return await completion.Task.ConfigureAwait(true); }
        finally
        {
            panel.UnregisterPropertyChangedCallback(IsPrimaryButtonEnabledProperty, registration);
            PrimaryButtonClick -= Primary;
            SecondaryButtonClick -= Secondary;
            if (panel is CoverPickerDialog finishedPicker) { finishedPicker.UploadCompleted -= Uploaded; finishedPicker.CancelLoads(); }
            Content = null;
            panel.Content = content;
            (Content, Title, PrimaryButtonText, SecondaryButtonText, CloseButtonText, DefaultButton, IsPrimaryButtonEnabled) = saved;
            _panelCompletion = null;
        }
    }
}

public delegate Task<RemoteImageResult> FindArtwork(string itemId, string imageType);
public delegate Task<byte[]?> FetchArtwork(string itemId, string imageType, string tag, int width, int? index);
public delegate Task SwapArtwork(string itemId, string imageType, int index, RemoteImageInfo chosen);
public delegate Task DeleteArtwork(string itemId, string imageType, int? index);
public delegate Task UploadArtwork(string itemId, string imageType, PickedArtwork picked);
public delegate Task<byte[]?> FetchRemote(string address);
