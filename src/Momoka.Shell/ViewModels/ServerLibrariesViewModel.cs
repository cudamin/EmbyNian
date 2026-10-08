using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Momoka.Emby;
using Momoka.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Momoka.Shell.ViewModels;

public sealed partial class ServerLibrariesViewModel : PageViewModel
{
    private EmbySessionScope? _scope;
    private CancellationTokenSource? _lifetime;
    private EmbyVirtualFolder? _original;
    private JsonObject _draft = new();
    private JsonObject _baseline = new();
    private JsonObject _available = new();
    private JsonObject _configuration = new();
    private JsonObject _configurationBaseline = new();
    private JsonObject _metadata = new();
    private JsonObject _metadataBaseline = new();
    private EmbySystemInfo? _system;
    private string _taskId = "";
    private bool _building;
    private int _editGeneration;
    private bool _advancedLoaded;
    private bool _createUncertain;
    private bool _scanCancelling;
    public ObservableCollection<ManagedLibraryRow> Libraries { get; } = [];
    public ObservableCollection<LibraryPathRow> Paths { get; } = [];
    public ObservableCollection<LibraryFieldSection> Sections { get; } = [];
    public ObservableCollection<LibraryFieldSection> AdvancedSections { get; } = [];
    public ObservableCollection<LibraryProviderGroup> Providers { get; } = [];
    public ObservableCollection<LibraryFieldSection> ImageSections { get; } = [];
    public ObservableCollection<LibraryChoice> Languages { get; } = [];
    public ObservableCollection<LibraryChoice> Countries { get; } = [];
    public IReadOnlyList<LibraryChoice> ContentTypes { get; } = new[] { "movies", "tvshows", "music", "audiobooks", "books", "games", "musicvideos", "homevideos", "mixed" }
        .Select(type => new LibraryChoice(type, EmbyLibraryOptions.TypeName(type))).ToArray();

    [ObservableProperty] public partial bool IsAdministrator { get; set; }
    [ObservableProperty] public partial bool Editing { get; set; }
    [ObservableProperty] public partial bool IsNew { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial int ContentTypeIndex { get; set; } = -1;
    [ObservableProperty] public partial string Tab { get; set; } = "library";
    [ObservableProperty] public partial string CountText { get; set; } = "正在读取媒体库…";
    [ObservableProperty] public partial string ScanText { get; set; } = "扫描所有媒体库";
    [ObservableProperty] public partial bool ScanRunning { get; set; }
    [ObservableProperty] public partial double ScanProgress { get; set; }
    [ObservableProperty] public partial bool HasChanges { get; set; }
    public string EditorTitle => IsNew ? "新建媒体库" : _original?.Name ?? "媒体库";
    public string ContentTypeName => EmbyLibraryOptions.TypeName(CurrentType);
    public string CurrentLibraryId => _original?.Id ?? "";
    private string CurrentType => IsNew ? ContentTypeIndex >= 0 && ContentTypeIndex < ContentTypes.Count ? ContentTypes[ContentTypeIndex].Value.Replace("mixed", "", StringComparison.Ordinal) : "" : _original?.ContentType ?? "";
    public Visibility ListVisibility => Show(!Editing && Tab == "library");
    public Visibility AdvancedVisibility => Show(!Editing && Tab == "advanced");
    public Visibility EditorVisibility => Show(Editing);
    public Visibility TabsVisibility => Show(!Editing);
    public Visibility NewVisibility => Show(IsNew);
    public Visibility ExistingVisibility => Show(!IsNew);
    public Visibility PathsVisibility => Show(IsNew || _original?.CanManagePaths == true);
    public Visibility ScanVisibility => Show(ScanRunning);
    public bool CanUse => _lifetime is { IsCancellationRequested: false } && _scope?.IsCurrent == true && IsAdministrator && !Busy;
    public bool CanRefreshList => _lifetime is { IsCancellationRequested: false } && _scope?.IsCurrent == true && !Busy;
    public bool CanSave => CanUse && Editing && (!_createUncertain || !IsNew) && (!IsNew || ContentTypeIndex >= 0);
    public bool CanEditFields => CanUse && Editing;
    public bool CanSaveAdvanced => CanUse && _advancedLoaded;
    public bool CanScan => CanUse && _taskId.Length > 0 && !_scanCancelling;
    internal Func<Task<PickedArtwork?>>? PickImage { get; set; }

    internal void Attach(EmbySession session)
    {
        Cancel();
        _lifetime = new();
        _scope = session.IsSignedIn ? session.Capture() : null;
        IsAdministrator = false;
        Editing = false;
        _advancedLoaded = false;
        _createUncertain = false;
        _taskId = "";
        _scanCancelling = false;
        Libraries.Clear();
        _ = PollAsync(_lifetime.Token);
        Announce();
    }

    public override async Task ReloadAsync()
    {
        if (_lifetime is not { IsCancellationRequested: false } || Busy) return;
        if (_scope is not { IsCurrent: true } scope)
        {
            CountText = "尚未连接服务器";
            Notify("请先登录", "在 Momoka → 服务器中登录管理员账号后管理媒体库。", InfoBarSeverity.Informational);
            IsReady = true;
            return;
        }
        await RunAsync("读取媒体库失败", async token =>
        {
            var user = await scope.ExecuteAsync((client, ct) => client.GetManagedUserAsync(scope.Connection.UserId, ct), token);
            if (!Current(token)) return;
            IsAdministrator = user.IsAdministrator;
            if (!IsAdministrator)
            {
                Libraries.Clear(); CountText = "需要服务器管理员权限";
                Notify("无法管理媒体库", "请使用服务器管理员账号登录。", InfoBarSeverity.Informational);
                return;
            }
            var system = await scope.ExecuteAsync((client, ct) => client.GetSystemInfoAsync(ct), token);
            var folders = await scope.ExecuteAsync((client, ct) => client.GetVirtualFoldersAsync(ct), token);
            if (!Current(token)) return;
            _system = system;
            SetLibraries(folders);
            _createUncertain = false;
            await ReadTasksAsync(token);
            _ = LoadImagesAsync(_lifetime!.Token);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRefreshList))] private Task RefreshAsync() => ReloadAsync();
    [RelayCommand(CanExecute = nameof(CanUse))] private Task NewLibraryAsync() => OpenAsync(null);

    internal async Task OpenAsync(ManagedLibraryRow? row)
    {
        if (!CanUse || row?.IsCurrent == false || _scope is not { } scope) return;
        await RunAsync("打开媒体库失败", async token =>
        {
            var folders = row is null ? null : await scope.ExecuteAsync((client, ct) => client.GetVirtualFoldersAsync(ct), token);
            var current = row is null ? null : folders!.FirstOrDefault(folder => folder.Id == row.Id) ?? throw new InvalidOperationException("此媒体库已被移除，请刷新列表。");
            await LoadLocalizationAsync(token);
            var available = await scope.ExecuteAsync((client, ct) => client.GetLibraryAvailableOptionsAsync(current?.ContentType ?? "movies", row is null, ct), token);
            if (!Current(token)) return;
            _building = true;
            _editGeneration++;
            _original = current;
            IsNew = row is null;
            ContentTypeIndex = IsNew ? 0 : -1;
            Name = current?.Name ?? "电影";
            _available = available;
            _draft = current is null ? EmbyLibraryOptions.NewOptions(available, "movies") : (JsonObject)current.Options.DeepClone();
            _baseline = (JsonObject)_draft.DeepClone();
            Paths.Clear();
            foreach (var path in current?.Paths.OfType<JsonObject>() ?? []) Paths.Add(new((JsonObject)path.DeepClone()));
            BuildEditor();
            HasChanges = false;
            Editing = true;
            _building = false;
            Announce();
        });
    }

    internal async Task ChangeContentTypeAsync(int index)
    {
        if (_building || !IsNew || !CanEditFields || index < 0 || index >= ContentTypes.Count || index == ContentTypeIndex || _scope is not { } scope) return;
        await RunAsync("读取内容类型选项失败", async token =>
        {
            var type = ContentTypes[index].Value == "mixed" ? "" : ContentTypes[index].Value;
            var available = await scope.ExecuteAsync((client, ct) => client.GetLibraryAvailableOptionsAsync(type, true, ct), token);
            if (!Current(token)) return;
            _building = true;
            var oldDefaultName = ContentTypeIndex >= 0 ? ContentTypes[ContentTypeIndex].Name : "";
            ContentTypeIndex = index;
            if (Name == oldDefaultName || Name.Length == 0) Name = ContentTypes[index].Name;
            _available = available;
            _draft = EmbyLibraryOptions.NewOptions(available, type);
            _baseline = (JsonObject)_draft.DeepClone();
            BuildEditor();
            _building = false;
            HasChanges = true;
            Announce();
        });
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave || _scope is not { } scope) return;
        await RunAsync("保存未完成，请核对服务器状态后重试", async token =>
        {
            ValidateFields(Sections.Concat(ImageSections));
            ArgumentException.ThrowIfNullOrWhiteSpace(Name);
            EmbyLibraryOptions.Validate(_draft);
            if (IsNew)
            {
                var draft = (JsonObject)_draft.DeepClone();
                draft["PathInfos"] = new JsonArray(Paths.Select(path => (JsonNode)path.Document.DeepClone()).ToArray());
                EmbyLibraryOptions.ValidateNew(Name, draft);
                var folders = await scope.ExecuteAsync((client, ct) => client.GetVirtualFoldersAsync(ct), token);
                if (folders.Any(folder => folder.Name.Equals(Name.Trim(), StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("已经存在同名媒体库，请更换名称或刷新列表。");
                EnsureCurrent(token);
                _createUncertain = true;
                await scope.ExecuteAsync((client, ct) => client.CreateVirtualFolderAsync(Name, CurrentType, draft, ct), token);
                EnsureCurrent(token);
                _createUncertain = false;
                Editing = false;
                HasChanges = false;
                SetLibraries(await scope.ExecuteAsync((client, ct) => client.GetVirtualFoldersAsync(ct), token));
                Notify("媒体库已创建", "服务器已开始扫描新媒体库。", InfoBarSeverity.Success);
            }
            else if (_original is { } original)
            {
                var latest = await FindLibraryAsync(original.Id, token);
                EnsureCurrent(token);
                var merged = EmbyLibraryOptions.Merge(latest.Options, _baseline, _draft);
                if (!JsonNode.DeepEquals(merged, latest.Options))
                    await scope.ExecuteAsync((client, ct) => client.SaveVirtualFolderOptionsAsync(original.Id, merged, ct), token);
                EnsureCurrent(token);
                // 保存选项成功后重设基线；后续改名失败重试时不会再次覆写这些字段。
                _baseline = (JsonObject)_draft.DeepClone();
                if (Name.Trim() != original.Name)
                    await scope.ExecuteAsync((client, ct) => client.RenameVirtualFolderAsync(original.Id, Name, ct), token);
                EnsureCurrent(token);
                Editing = false; HasChanges = false;
                SetLibraries(await scope.ExecuteAsync((client, ct) => client.GetVirtualFoldersAsync(ct), token));
                Notify("媒体库设置已保存", null, InfoBarSeverity.Success);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task BackAsync()
    {
        var generation = _editGeneration;
        if (HasChanges && !await ConfirmAsync("放弃未保存的设置？", "媒体库选项尚未保存。已添加或移除的服务器目录会保留。", "放弃更改")) return;
        if (!CanUse || generation != _editGeneration) return;
        Editing = false; HasChanges = false;
        _editGeneration++;
        Paths.Clear();
        await ReloadAsync();
    }

    internal async Task RemoveAsync(ManagedLibraryRow row)
    {
        if (!CanUse || !row.IsCurrent || !row.Folder.CanRemove || _scope is not { } scope) return;
        if (!await ConfirmAsync("移除媒体库", $"从服务器移除“{row.Name}”？媒体文件会保留，服务器中的媒体库及相关数据将被移除。", "移除媒体库") || !CanUse || !scope.IsCurrent) return;
        await RunAsync("移除媒体库未完成", async token =>
        {
            await FindLibraryAsync(row.Id, token);
            EnsureCurrent(token);
            await scope.ExecuteAsync((client, ct) => client.RemoveVirtualFolderAsync(row.Id, ct), token);
            EnsureCurrent(token);
            SetLibraries(await scope.ExecuteAsync((client, ct) => client.GetVirtualFoldersAsync(ct), token));
            Notify("媒体库已移除", row.Name, InfoBarSeverity.Success);
        });
    }

    internal async Task ScanAsync(ManagedLibraryRow row, string mode, bool replaceImages, bool replaceThumbnails = false)
    {
        if (!CanUse || !row.IsCurrent || !row.Folder.CanRefresh || _scope is not { } scope) return;
        if ((mode == "all" || replaceImages || replaceThumbnails) && !await ConfirmAsync("刷新媒体库元数据", $"刷新“{row.Name}”：" + (mode == "all" ? "替换全部元数据" : "搜索缺失元数据")
            + (replaceImages ? "，替换现有图片" : "") + (replaceThumbnails ? "，替换视频预览缩略图" : "") + "。被替换的手工编辑内容可能丢失。", "刷新")) return;
        if (!CanUse || !scope.IsCurrent) return;
        await RunAsync("启动扫描失败", async token =>
        {
            EnsureCurrent(token);
            await scope.ExecuteAsync((client, ct) => client.ScanVirtualFolderAsync(row.Id, mode, replaceImages, ct, replaceThumbnails), token);
            if (Current(token)) Notify("扫描已提交", $"服务器正在处理“{row.Name}”。", InfoBarSeverity.Success);
        });
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ToggleScanAsync()
    {
        if (!CanScan || _scope is not { } scope) return;
        await RunAsync("更改扫描任务失败", async token =>
        {
            await ReadTasksAsync(token);
            EnsureCurrent(token);
            await scope.ExecuteAsync((client, ct) => client.SetLibraryTaskRunningAsync(_taskId, !ScanRunning, ct), token);
            await ReadTasksAsync(token);
        });
    }

    internal async Task<JsonArray?> BrowseAsync(string path, string username, string password)
    {
        JsonArray? result = null;
        if (!CanUse || _scope is not { } scope) return result;
        await RunAsync("无法读取服务器目录", async token =>
        {
            result = await scope.ExecuteAsync((client, ct) => client.GetServerDirectoriesAsync(path, username, password, ct), token);
            if (path.Length == 0) result.Add(new JsonObject { ["Name"] = "网络", ["Path"] = "Network" });
        });
        return result;
    }

    internal async Task<string> ParentDirectoryAsync(string path)
    {
        var result = path;
        if (!CanUse || _scope is not { } scope) return result;
        await RunAsync("无法读取上级目录", async token => result = await scope.ExecuteAsync((client, ct) => client.GetServerParentDirectoryAsync(path, ct), token));
        return result;
    }

    internal async Task<EmbyLibraryArtwork?> OpenArtworkAsync(ManagedLibraryRow row)
    {
        EmbyLibraryArtwork? result = null;
        if (!CanUse || !row.IsCurrent || _scope is not { } scope) return result;
        await RunAsync("读取媒体库图片失败", async token =>
        {
            var item = await scope.ExecuteAsync((client, ct) => client.GetItemAsync(row.Id, ct), token);
            EnsureCurrent(token);
            result = new(scope, item, _lifetime!.Token);
        });
        return result;
    }

    internal async Task<bool> SavePathAsync(JsonObject pathInfo, LibraryPathRow? original)
    {
        if (!CanEditFields || _scope is not { } scope) return false;
        var saved = false;
        await RunAsync("保存媒体目录失败", async token =>
        {
            var path = EmbyLibraryOptions.Text(pathInfo, "Path").Trim();
            if (Paths.Any(row => !ReferenceEquals(row, original) && row.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("此目录已在媒体库中。");
            await scope.ExecuteAsync((client, ct) => client.ValidateServerDirectoryAsync(path, EmbyLibraryOptions.Text(pathInfo, "Username"), EmbyLibraryOptions.Text(pathInfo, "Password"), false, ct), token);
            EnsureCurrent(token);
            pathInfo["Path"] = path;
            if (IsNew)
            {
                if (original is null) Paths.Add(new((JsonObject)pathInfo.DeepClone())); else Paths[Paths.IndexOf(original)] = new((JsonObject)pathInfo.DeepClone());
                HasChanges = true;
            }
            else if (_original is { } folder)
            {
                if (original is null) await scope.ExecuteAsync((client, ct) => client.AddLibraryPathAsync(folder.Id, pathInfo, ct), token);
                else await scope.ExecuteAsync((client, ct) => client.UpdateLibraryPathAsync(folder.Id, pathInfo, ct), token);
                EnsureCurrent(token);
                await RefreshPathsAsync(token);
            }
            saved = true;
        });
        return saved;
    }

    internal async Task RemovePathAsync(LibraryPathRow row)
    {
        if (!CanEditFields || _scope is not { } scope) return;
        var generation = _editGeneration;
        if (!IsNew && !await ConfirmAsync("移除媒体文件夹", $"从“{Name}”移除目录“{row.Path}”？文件会保留，但该目录的内容将不再属于此媒体库。", "移除文件夹")) return;
        if (!CanEditFields || generation != _editGeneration || !scope.IsCurrent || !Paths.Contains(row)) return;
        if (IsNew) { Paths.Remove(row); HasChanges = true; return; }
        await RunAsync("移除媒体目录失败", async token =>
        {
            await scope.ExecuteAsync((client, ct) => client.RemoveLibraryPathAsync(CurrentLibraryId, row.Path, ct), token);
            EnsureCurrent(token);
            await RefreshPathsAsync(token);
        });
    }

    private async Task RefreshPathsAsync(CancellationToken token)
    {
        var latest = await FindLibraryAsync(CurrentLibraryId, token);
        EnsureCurrent(token);
        Paths.Clear();
        foreach (var path in latest.Paths.OfType<JsonObject>()) Paths.Add(new((JsonObject)path.DeepClone()));
        // 目录通过专用接口立即保存；选项草稿中不再携带旧的目录快照。
        _draft["PathInfos"] = latest.Paths.DeepClone();
        _baseline["PathInfos"] = latest.Paths.DeepClone();
    }

    private async Task<EmbyVirtualFolder> FindLibraryAsync(string id, CancellationToken token)
    {
        EnsureCurrent(token);
        var folders = await _scope!.ExecuteAsync((client, ct) => client.GetVirtualFoldersAsync(ct), token);
        return folders.FirstOrDefault(folder => folder.Id == id) ?? throw new InvalidOperationException("此媒体库已被移除，请返回列表刷新。");
    }

    private async Task ReadTasksAsync(CancellationToken token)
    {
        try
        {
            var tasks = await _scope!.ExecuteAsync((client, ct) => client.GetLibraryTasksAsync(ct), token);
            if (token.IsCancellationRequested || _scope?.IsCurrent != true) return;
            var task = tasks.OfType<JsonObject>().FirstOrDefault(task => EmbyLibraryOptions.Text(task, "Key") == "RefreshLibrary");
            _taskId = task is null ? "" : EmbyLibraryOptions.Text(task, "Id");
            ScanRunning = task is not null && EmbyLibraryOptions.Text(task, "State") is "Running" or "Cancelling";
            _scanCancelling = task is not null && EmbyLibraryOptions.Text(task, "State") == "Cancelling";
            ScanProgress = task is null ? 0 : Math.Clamp(EmbyLibraryOptions.Number(task, "CurrentProgressPercentage"), 0, 100);
            ScanText = _scanCancelling ? "正在取消扫描…" : ScanRunning ? $"取消扫描 · {ScanProgress:0}%" : "扫描所有媒体库";
            Announce();
        }
        catch (Exception error) when (!token.IsCancellationRequested && _scope?.IsCurrent == true)
        {
            _taskId = ""; ScanText = "扫描状态读取失败";
            Report("媒体库已读取，扫描任务状态暂不可用", error);
            Announce();
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_scope?.IsCurrent != true) { Announce(); break; }
                if (!CanUse || Editing || Tab != "library") continue;
                var wasRunning = ScanRunning;
                await ReadTasksAsync(token);
                if ((!ScanRunning && !wasRunning) || Busy || Editing) continue;
                var folders = await _scope.ExecuteAsync((client, ct) => client.GetVirtualFoldersAsync(ct), token);
                if (!token.IsCancellationRequested && _scope.IsCurrent && !Editing && !Busy) SetLibraries(folders);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested && _scope?.IsCurrent == true) Report("自动刷新已暂停，请手动刷新", error); }
    }

    private void SetLibraries(IReadOnlyList<EmbyVirtualFolder> folders)
    {
        if (_scope?.IsCurrent != true || _lifetime is not { IsCancellationRequested: false }) return;
        var images = Libraries.ToDictionary(row => row.Id, row => row.Image);
        Libraries.Clear();
        foreach (var folder in folders) Libraries.Add(new(folder, _scope) { Image = images.GetValueOrDefault(folder.Id) });
        CountText = Libraries.Count == 0 ? "尚未添加媒体库" : $"{Libraries.Count} 个媒体库";
    }

    private async Task LoadImagesAsync(CancellationToken token)
    {
        var scope = _scope!;
        foreach (var row in Libraries.ToArray())
        {
            var tag = EmbyLibraryOptions.Text(row.Folder.Document, "PrimaryImageTag");
            if (tag.Length == 0) continue;
            try
            {
                var id = EmbyLibraryOptions.Text(row.Folder.Document, "PrimaryImageItemId", row.Id);
                var bytes = await scope.ExecuteAsync((client, ct) => client.GetImageBytesAsync(id, "Primary", tag, 640, ct), token);
                if (token.IsCancellationRequested || !scope.IsCurrent) return;
                using var stream = new MemoryStream(bytes);
                var image = new BitmapImage();
                await image.SetSourceAsync(stream.AsRandomAccessStream());
                if (!token.IsCancellationRequested && scope.IsCurrent)
                {
                    var current = Libraries.FirstOrDefault(item => item.Id == row.Id && EmbyLibraryOptions.Text(item.Folder.Document, "PrimaryImageTag") == tag);
                    if (current is not null) current.Image = image;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* 图片不可用时保留文件夹图标，管理功能仍可使用。 */ }
        }
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (_lifetime is not { IsCancellationRequested: false } || _scope is not { IsCurrent: true } || Busy) return;
        var token = BeginLoad();
        try { await action(token); }
        catch (Exception error)
        {
            if (Current(token)) Report(_createUncertain && IsNew ? "创建请求的结果尚未确认；请返回列表刷新后核对，避免重复创建" : message, error);
        }
        finally { _building = false; EndLoad(token); }
    }

    private bool Current(CancellationToken token) => _lifetime is { IsCancellationRequested: false } && IsCurrent(token) && _scope?.IsCurrent == true;
    private void EnsureCurrent(CancellationToken token) { token.ThrowIfCancellationRequested(); if (!Current(token)) throw new OperationCanceledException(); }
    private static void ValidateFields(IEnumerable<LibraryFieldSection> sections)
    {
        var error = sections.SelectMany(section => section.Fields).FirstOrDefault(field => field.Error.Length > 0);
        if (error is not null) throw new ArgumentException(error.Error);
    }
    partial void OnEditingChanged(bool value) => Announce();
    partial void OnIsNewChanged(bool value) => Announce();
    partial void OnIsAdministratorChanged(bool value) => Announce();
    partial void OnNameChanged(string value) { if (!_building && Editing) HasChanges = true; }
    partial void OnTabChanged(string value) => Announce();
    private void Announce()
    {
        foreach (var name in new[] { nameof(ListVisibility), nameof(EditorVisibility), nameof(AdvancedVisibility), nameof(TabsVisibility), nameof(NewVisibility), nameof(ExistingVisibility), nameof(PathsVisibility), nameof(ScanVisibility), nameof(EditorTitle), nameof(ContentTypeName) }) OnPropertyChanged(name);
        BusyChanged();
    }
    protected override void BusyChanged()
    {
        foreach (var name in new[] { nameof(CanUse), nameof(CanRefreshList), nameof(CanSave), nameof(CanEditFields), nameof(CanSaveAdvanced), nameof(CanScan) }) OnPropertyChanged(name);
        RefreshCommand.NotifyCanExecuteChanged();
        NewLibraryCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); BackCommand.NotifyCanExecuteChanged();
        ToggleScanCommand.NotifyCanExecuteChanged(); SaveAdvancedCommand.NotifyCanExecuteChanged();
    }
    public override void Cancel()
    {
        _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        _editGeneration++;
        Paths.Clear();
        base.Cancel();
        Announce();
    }
}
