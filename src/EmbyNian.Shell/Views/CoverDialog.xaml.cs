using System.ComponentModel;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Shell.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 面板上的一格：一种图，或者背景图那一排里的一张。
/// <para>
/// 一格自己会去取自己那张缩略图，也会在自己被改动之后重新取一遍 —— 面板因此不必知道「哪几张要刷」。这和从前来
/// 的那一份（<c>CoverChoice</c>：只装一个候选，图由对话框统一取）不一样的地方就在这儿：那一版一格只活一次，
/// 而这一版一格会活完整个面板的生命周期，中途被换、被删、被重新取。
/// </para>
/// <para>
/// 公开的，因为 <c>x:Bind</c> 在 DataTemplate 里是按类型名编译的。
/// </para>
/// </summary>
public sealed partial class ArtworkSlot : INotifyPropertyChanged
{
    private BitmapImage? _picture;
    private bool _busy;

    /// <param name="kind">哪一种图。背景图那一排里几张共用同一个 <paramref name="kind"/>。</param>
    /// <param name="index">
    /// 背景图里这是第几张（从 0 数起）；其余几种一律 0。删的时候要它 —— 服务器按序号取。
    /// </param>
    /// <param name="tag">
    /// 这个条目这一版图在服务器上那个标签，没有图就是空。取缩略图用的就是它，所以「换过了」这件事的判据是它变了
    /// （同 <see cref="EmbyImageStore.TagFor"/> 那段说明）。
    /// </param>
    public ArtworkSlot(ArtworkKind kind, int index, string? tag)
    {
        Kind = kind;
        Index = index;
        Tag = tag;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ArtworkKind Kind { get; }

    public int Index { get; }

    /// <summary>服务器上这一版图的标签，没有图就是空。</summary>
    public string? Tag { get; private set; }

    public string ImageType => Kind.ImageType;

    /// <summary>这一格有没有图。<see cref="ArtworkKind.Many"/> 的那一种上，这说的是「这一张」。</summary>
    public bool Has => !string.IsNullOrEmpty(Tag);

    /// <summary>
    /// 格子上那一行字。
    /// <para>
    /// 单张的那几种写中文名（「封面海报图」）；背景图那一排写「背景图 1」「背景图 2」—— 一排里五张一模一样的
    /// 「背景图」是没法点的。参考图上单张那几格底下写的是尺寸，而尺寸在这儿没用：用户挑图看的是画面，不是
    /// 一千二百九十乘一千九百三十六。
    /// </para>
    /// </summary>
    public string Caption => Kind.Many ? $"{Kind.Name} {Index + 1}" : Kind.Name;

    /// <summary>指针停在那一格上时的一句。</summary>
    public string Hint => Has ? $"换一张{Kind.Name}" : $"还没有{Kind.Name}，点这里加一张";

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

    /// <summary>有图时那两颗键的可见性，也是空格子那颗加号的可见性（反过来）。</summary>
    public Visibility HasPicture => Has ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyMark => Has ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>正忙着这一格（在换、在删）。</summary>
    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            Raise(nameof(Busy));
            Raise(nameof(Ready));
        }
    }

    /// <summary>这一格现在能不能动。</summary>
    public bool Ready => !_busy;

    /// <summary>
    /// 这一格现在是哪一版图。换图和删图之后调用方拿服务器新给的标签刷新它 —— 服务器给的标签才是「现在那一版」，
    /// 而缓存是按标签存的。
    /// </summary>
    public void Retag(string? tag)
    {
        if (string.Equals(Tag, tag, StringComparison.Ordinal)) return;

        Tag = tag;

        // 标签一变，屏上那张就一定过期了。清掉：留着旧那张而只管标签的话，因为 Picture 是按引用比对的，
        // 屏上会照样画着已经被删掉的那张图。
        if (string.IsNullOrEmpty(tag)) Picture = null;

        Raise(nameof(Has));
        Raise(nameof(HasPicture));
        Raise(nameof(EmptyMark));
        Raise(nameof(Hint));
    }

    /// <summary>
    /// 这一格的缩略图。取不到就空着（那两颗键照旧能用，而它们要的是标签，不是这张缩略图）。
    /// </summary>
    /// <param name="fetch">
    /// 按标签取这一版的图字节。交标签进去而不是让这一格自己拼图种，是因为取图那一路在调用方手上、按
    /// <see cref="EmbyImageStore.TagFor"/> 那一套走。
    /// </param>
    internal async Task LoadAsync(Func<string, Task<byte[]?>> fetch)
    {
        if (Tag is not { Length: > 0 } tag) return;

        try
        {
            var bytes = await fetch(tag).ConfigureAwait(true);
            if (bytes is not { Length: > 0 }) return;

            Picture = await PosterLoader.DecodeAsync(bytes, ThumbnailWidth).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Debug("ui", $"面板上一格取不回来（{ImageType}）：{error.Message}");
        }
    }

    /// <summary>缩略图解到多宽。和 <c>EgArtworkFrameWidth</c> 一致，见 <see cref="PosterLoader.DecodeAsync"/>。</summary>
    private const int ThumbnailWidth = 140;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 面板上那三颗键（换、看、删）指的是服务器上哪一张：哪一种图，以及那一排里的第几张。
/// <para>
/// 一个 record 而不是两个参数，因为它得原样穿过好几层（键的 Tag → 处理函数 → 委托），而漏掉「第几张」的症状
/// 是「删第二张背景图删掉了第一张」—— 那种错在屏上看着像是服务器记错了账。
/// </para>
/// </summary>
/// <param name="ImageType">服务器那边的图种名。</param>
/// <param name="Index">多张的那几种里这是第几张；单张的一律 0。</param>
public readonly record struct ArtworkTarget(string ImageType, int Index);

/// <summary>
/// 「修改媒体封面图」那张面板（2026-09-13 按用户给的参考图重做）。
/// <para>
/// 从前的形状是「列一屏候选，挑一张换掉海报」—— 它只改得了 <c>Primary</c>，而一个条目有六种图。这一版是
/// 每一种图各占一格的面板：哪一格动过就只刷哪一格。
/// </para>
/// <para>
/// <b>它自己不做任何网络上的事。</b>四个动作（取候选、取图字节、换一张、删一张、传一张）都是交进来的委托，
/// 因为这张表不该知道服务器是怎么连上的 —— 这条规矩是上一版立下的，这一版多出几个动作照旧遵守。
/// </para>
/// </summary>
public sealed partial class CoverDialog : ContentDialog
{
    private const string Category = "ui";

    private readonly EmbyItem _item;
    private readonly FindArtwork _find;
    private readonly FetchArtwork _fetch;
    private readonly SwapArtwork _swap;
    private readonly DeleteArtwork _delete;
    private readonly UploadArtwork _upload;
    private readonly FetchRemote _fetchRemote;

    /// <summary>重读条目。改过之后要的是服务器上现在这一版（ImageTags 变了），见 <see cref="RefreshAsync"/>。</summary>
    private readonly Func<string, Task<EmbyItem?>> _reload;

    private readonly List<ArtworkSlot> _slots = [];

    /// <summary>
    /// 这一趟动过没有。交回给 <c>ItemCommands</c>：动过才需要让那一页重读（封面换了之后
    /// <see cref="EmbyItem.ImageTags"/> 上那个标签也变了，而缓存是按标签存的）。
    /// </summary>
    public bool Touched { get; private set; }

    /// <param name="find">按图种取候选：<c>(itemId, imageType)</c>。</param>
    /// <param name="fetch">按「图种 + 标签」取这一版的图：<c>(itemId, imageType, tag, width)</c>。</param>
    /// <param name="swap">把挑中的一张挂上去。多张的那几种要给序号，所以序号在第三个。</param>
    /// <param name="delete">删掉一张。<b>不可撤销</b>，确认由这张表自己问。</param>
    /// <param name="upload">从本机传一张上去。</param>
    /// <param name="reload">重新问一遍这个条目（改完之后要「现在那一版」的标签）。</param>
    /// <param name="fetchRemote">
    /// 按地址取一张候选图。候选表要用它 —— 走的是「服务器代取」，而这一路也得只有一处接线。
    /// </param>
    public CoverDialog(
        EmbyItem item,
        FindArtwork find,
        FetchArtwork fetch,
        SwapArtwork swap,
        DeleteArtwork delete,
        UploadArtwork upload,
        Func<string, Task<EmbyItem?>> reload,
        FetchRemote fetchRemote)
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

        // 上面六格 = 一个条目各一张的那几种；底下一排 = 可以有不止一张的那一种（背景图）。
        foreach (var kind in ItemArtwork.SingleKinds) Add(Groups, [new ArtworkSlot(kind, 0, TagOf(kind))]);
        foreach (var kind in ItemArtwork.MultipleKinds) Add(Backdrops, SlotsFor(kind));

        // 不 await：一格一格慢慢回来，面板先画出来。
        _ = LoadAsync();
    }

    /// <summary>这个条目这一种图现在的标签，服务器上没有就是空。</summary>
    private string? TagOf(ArtworkKind kind) =>
        ItemArtwork.TagsOf(_item, kind.ImageType) is [{ Length: > 0 } first] ? first : null;

    /// <summary>上面那一栏：一个条目各一张的那几种图。</summary>
    public List<ArtworkSlot> Groups { get; } = [];

    /// <summary>底下一排：可以有不止一张的那一种，一张一格。</summary>
    public List<ArtworkSlot> Backdrops { get; } = [];

    /// <summary>
    /// 按图种铺出一格一格。多张的那一种按服务器上实际有几张铺几格 —— 一张都没有的时候不铺空格子：底下那颗
    /// 「再加一张」已经在说这件事了，再摆一个空框就是同一句话说两遍。
    /// <para>
    /// 标签在这儿交给每一格，但格子里的图是后到的（<see cref="LoadAsync"/>）；这两步之所以分开，是因为
    /// 「服务器上有哪几张」是手上这个条目的数据说了算，而缩略图要一趟网络。
    /// </para>
    /// </summary>
    private List<ArtworkSlot> SlotsFor(ArtworkKind kind)
    {
        var tags = ItemArtwork.TagsOf(_item, kind.ImageType);
        var slots = new List<ArtworkSlot>(tags.Count);

        for (var index = 0; index < tags.Count; index++) slots.Add(new ArtworkSlot(kind, index, tags[index]));

        return slots;
    }

    private void Add(List<ArtworkSlot> into, List<ArtworkSlot> slots)
    {
        into.AddRange(slots);
        _slots.AddRange(slots);
    }

    /// <summary>一格一格把自己的缩略图取回来。逐格 await：一张慢的不会把后面的堵死在自己后面。</summary>
    private async Task LoadAsync()
    {
        foreach (var slot in _slots) await slot.LoadAsync(tag => FetchAsync(slot, tag)).ConfigureAwait(true);
    }

    /// <summary>取这一格那一版的图字节。宽度交给服务器，见 <see cref="EmbyImageStore.RequestWidth"/>。</summary>
    private Task<byte[]?> FetchAsync(ArtworkSlot slot, string tag) =>
        _fetch(_item.Id, slot.ImageType, tag, ThumbnailWidth);

    /// <summary>
    /// 按地址取一张候选图 —— 那张候选表要的。候选图不属于这个条目（它们还在刮削源上），所以这条路走的是
    /// 「服务器代取」，见 <c>EmbyClient.GetRemoteImageBytesAsync</c>。
    /// </summary>
    internal Task<byte[]?> FetchRemoteAsync(string address) => _fetchRemote(address);

    /// <summary>缩略图解到多宽。140 就是 <c>EgArtworkFrameWidth</c>，再宽是白解。</summary>
    private const int ThumbnailWidth = 140;

    private static ArtworkSlot? SlotOf(object sender) => (sender as FrameworkElement)?.Tag as ArtworkSlot;

    // ---- 三颗键 ---------------------------------------------------------------

    /// <summary>
    /// 换这一种图。先问服务器这一种图有哪些候选，再弹那一张小表让人挑一个，挑中的交给服务器去取。
    /// <para>
    /// 换图和上传是同一个入口下的两件事（候选表里那颗「从本机传一张」），因为对用户来说是同一句话
    /// 「这一格不要这张了」。空格子点下去也走这一条 —— 那是「这儿少一张」，跟换掉一张是同一件事的两头。
    /// </para>
    /// </summary>
    private async void OnSwap(object sender, RoutedEventArgs e)
    {
        if (SlotOf(sender) is not { } slot || !slot.Ready) return;

        var kind = slot.Kind;
        slot.Busy = true;

        try
        {
            var found = await _find(_item.Id, kind.ImageType).ConfigureAwait(true);

            var picker = new CoverPickerDialog(_item, kind, found, this) { XamlRoot = XamlRoot };

            if (await picker.ShowAsync() != ContentDialogResult.Primary) return;

            if (picker.Picked is { } chosen)
            {
                await _swap(_item.Id, kind.ImageType, slot.Index, chosen).ConfigureAwait(true);
                Touched = true;
                await RefreshAsync().ConfigureAwait(true);
                return;
            }

            if (picker.Uploaded is { Bytes.Length: > 0 } picked)
            {
                await _upload(_item.Id, kind.ImageType, picked).ConfigureAwait(true);
                Touched = true;
                await RefreshAsync().ConfigureAwait(true);
            }
        }
        catch (Exception error)
        {
            Log.Warn(Category, "换封面图失败", error);
            await ShowProblemAsync($"换「{kind.Name}」没成：{Failure.Describe(error)}").ConfigureAwait(true);
        }
        finally
        {
            slot.Busy = false;
        }
    }

    /// <summary>看大图。只读 —— 摆出来看一眼，不动服务器。</summary>
    private async void OnView(object sender, RoutedEventArgs e)
    {
        if (SlotOf(sender) is not { } slot || slot.Tag is not { Length: > 0 } tag) return;

        try
        {
            var bytes = await _fetch(_item.Id, slot.ImageType, tag, ViewerWidth).ConfigureAwait(true);
            if (bytes is not { Length: > 0 }) return;

            var picture = await PosterLoader.DecodeAsync(bytes, ViewerWidth).ConfigureAwait(true);
            if (picture is null) return;

            await new ArtworkViewer(slot.Caption, picture) { XamlRoot = XamlRoot }.ShowAsync();
        }
        catch (Exception error)
        {
            Log.Warn(Category, "看大图失败", error);
            await ShowProblemAsync($"这一张看不了：{Failure.Describe(error)}").ConfigureAwait(true);
        }
    }

    /// <summary>大图解到多宽。这一张要铺满对话框，所以比缩略图宽一档 —— 仍然不是原图，见 <see cref="PosterLoader"/>。</summary>
    private const int ViewerWidth = 1280;

    /// <summary>
    /// 删掉这一张。<b>这一页上唯一不可撤销的动作</b>，所以先问一句 —— 而且这句话要说清会波及什么：
    /// 删的可能正是剧名上方那枚徽标、页尾那条横幅。
    /// </summary>
    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (SlotOf(sender) is not { } slot || !slot.Ready || !slot.Has) return;

        if (await AskAsync($"删掉这一张{slot.Caption}？", DeleteWarning(slot), "删掉") != ContentDialogResult.Primary)
            return;

        slot.Busy = true;

        try
        {
            await _delete(_item.Id, slot.ImageType, slot.Kind.Many ? slot.Index : null).ConfigureAwait(true);

            Touched = true;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "删封面图失败", error);
            await ShowProblemAsync($"删「{slot.Caption}」没成：{Failure.Describe(error)}").ConfigureAwait(true);
        }
        finally
        {
            slot.Busy = false;
        }
    }

    /// <summary>再加一张背景图。它和上面那六格「换」的区别只是：这一颗不先问候选，直接开文件框。</summary>
    private async void OnAddBackdrop(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await PickArtworkAsync() is not { Bytes.Length: > 0 } picked) return;

            // 只能用第一个可以有不止一张的图种（现在就是背景图）。没有那一种时这一颗键根本不存在。
            if (ItemArtwork.MultipleKinds.FirstOrDefault() is not { ImageType.Length: > 0 } kind) return;

            await _upload(_item.Id, kind.ImageType, picked).ConfigureAwait(true);

            Touched = true;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "上传背景图失败", error);
            await ShowProblemAsync($"传这张图没成：{Failure.Describe(error)}").ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 开文件框挑一张图。选不到（用户取消、或者这个环境拿不到窗口句柄）就是空 —— 拿 null 的调用方什么都不做，
    /// 这一句在这里而不是在每一处调用点上，是因为两种「选不到」在用户看来是同一件事：什么都没发生。
    /// </summary>
    internal async Task<PickedArtwork?> PickArtworkAsync() => await ArtworkFile.PickAsync(XamlRoot).ConfigureAwait(true);

    /// <summary>
    /// 改完之后把面板整个重读一遍：手上这个条目是打开面板那一刻拿到的，服务器上现在的图得重新问它。
    /// <para>
    /// 整个重读而不是只刷动过那一格，是因为序号那件事 —— 背景图删掉中间一张之后，后面每一张的序号都往前挪了
    /// 一位，只刷一格会把「第三张」的标签留在已经变成第二张的那一格上。重问一遍只有一趟请求，而面板上总共
    /// 十来格。
    /// </para>
    /// </summary>
    private async Task RefreshAsync()
    {
        // 服务器上现在这一版：条目本身已经变了（ImageTags 少了一个/多了一个），所以重新问一遍它。
        var fresh = await _reload(_item.Id).ConfigureAwait(true);
        if (fresh is null) return;

        var groups = Groups.ToArray();
        var backdrops = Backdrops.ToArray();

        foreach (var slot in groups)
            slot.Retag(ItemArtwork.TagsOf(fresh, slot.ImageType) is [{ Length: > 0 } first] ? first : null);

        // 背景图那一排的条数会变（删一张少一格、传一张多一格），所以整排重建。
        Rebuild(backdrops, fresh, ItemArtwork.MultipleKinds);

        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>把背景图那一排按服务器上现在的张数重建：删掉一张、传上一张之后，格子的个数都变了。</summary>
    private void Rebuild(IReadOnlyList<ArtworkSlot> existing, EmbyItem fresh, IReadOnlyList<ArtworkKind> kinds)
    {
        foreach (var kind in kinds)
        {
            var tags = ItemArtwork.TagsOf(fresh, kind.ImageType);

            // 够用就只换标签（格子在屏上就不会抖），不够就补。
            for (var index = 0; index < tags.Count; index++)
            {
                if (index < existing.Count && existing[index].ImageType == kind.ImageType)
                {
                    existing[index].Retag(tags[index]);
                    continue;
                }

                var slot = new ArtworkSlot(kind, index, tags[index]);
                Backdrops.Add(slot);
                _slots.Add(slot);
            }

            // 多的那几格去掉。先 Retag(null) 清掉图，再摘 —— 直接把对象摘掉的话，屏上那一格会留到下一次重排。
            for (var index = Backdrops.Count - 1; index >= tags.Count; index--)
            {
                Backdrops[index].Retag(null);
                _slots.Remove(Backdrops[index]);
                Backdrops.RemoveAt(index);
            }
        }
    }

    /// <summary>删掉这一张会波及什么。剧名上方那枚徽标和页尾那条横幅是服务器上这一种图撑着的。</summary>
    private static string DeleteWarning(ArtworkSlot slot)
    {
        var lines = new List<string> { $"服务器上这一张「{slot.Caption}」会被删掉，删了就找不回来。" };

        switch (slot.ImageType)
        {
            case EmbyImageStore.Primary:
                lines.Add("详情页左边那张海报会空出来。");
                break;
            case EmbyImageStore.Logo:
                lines.Add("详情页剧名上方那枚徽标会空出来（这个条目还有横幅图的话，那一格会退回横幅图）。");
                break;
            case EmbyImageStore.Banner:
                lines.Add("详情页剧名上方那一格和页尾那条横幅都可能空出来。");
                break;
            case EmbyImageStore.Backdrop:
                lines.Add("详情页和主页轮播背后的那张画面会换成下一张备选。");
                break;
            case EmbyImageStore.Thumb:
                lines.Add("卡片上那种宽的缩略图会改成用海报。");
                break;
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>问一句话。默认落在「算了」那一边 —— 这是不可撤销的动作，回车不该替人做决定。</summary>
    private async Task<ContentDialogResult> AskAsync(string title, string body, string? confirm = null) =>
        await new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = confirm ?? "确定",
            CloseButtonText = "算了",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        }.ShowAsync();

    /// <summary>弹一句报错。这一个面板里挂掉的每一件事都得说一句 —— 「点了没反应」是最难查的那种。</summary>
    private Task ShowProblemAsync(string message) =>
        new ContentDialog
        {
            Title = "修改媒体封面图",
            Content = message,
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot
        }.ShowAsync().AsTask();
}

/// <summary>按图种问服务器这一种图有哪些候选：<c>(itemId, imageType)</c>。</summary>
public delegate Task<RemoteImageResult> FindArtwork(string itemId, string imageType);

/// <summary>按标签取这一版的图字节：<c>(itemId, imageType, tag, width)</c>。</summary>
public delegate Task<byte[]?> FetchArtwork(string itemId, string imageType, string tag, int width);

/// <summary>
/// 把挑中的一张挂上去。序号在第二个 —— 背景图那一排得知道改的是第几张，而单张的那几种传 0、被忽略。
/// </summary>
public delegate Task SwapArtwork(string itemId, string imageType, int index, RemoteImageInfo chosen);

/// <summary>删掉一张。<paramref name="index"/> 为空就是「不带序号的短地址」，见 <c>EmbyClient.DeleteImageAsync</c>。</summary>
public delegate Task DeleteArtwork(string itemId, string imageType, int? index);

/// <summary>从本机传一张图上去。文件名跟着走，服务器按后缀名认格式。</summary>
public delegate Task UploadArtwork(string itemId, string imageType, PickedArtwork picked);

/// <summary>
/// 按地址取一张候选图。候选图还在刮削源上，所以这一路走的是「服务器代取」——
/// <c>(address)</c>，见 <c>EmbyClient.GetRemoteImageBytesAsync</c>。
/// </summary>
public delegate Task<byte[]?> FetchRemote(string address);
