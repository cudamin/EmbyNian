using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Emby;
using EmbyNian.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.ViewModels;

/// <summary>用户管理只操作捕获的管理员身份，页签编辑为草稿，点击保存才提交。</summary>
public sealed partial class ServerUsersViewModel : PageViewModel
{
    private EmbySessionScope? _scope;
    private EmbyManagedUser? _original;
    private UserEditorOptions? _options;
    private readonly List<ManagedUserRow> _allUsers = [];
    private bool _attached;
    private JsonObject? _pendingNewPolicy;
    private JsonObject? _initialEditorPolicy;

    public ObservableCollection<ManagedUserRow> Users { get; } = [];
    public ObservableCollection<UserPermissionGroup> PermissionGroups { get; } = [];
    public ObservableCollection<UserOptionRow> Features { get; } = [];
    public ObservableCollection<UserOptionRow> DeleteFolders { get; } = [];
    public ObservableCollection<UserOptionRow> Folders { get; } = [];
    public ObservableCollection<UserOptionRow> Channels { get; } = [];
    public ObservableCollection<UserOptionRow> Devices { get; } = [];
    public ObservableCollection<UserOptionRow> Unrated { get; } = [];
    public ObservableCollection<UserOptionRow> CopyOptions { get; } = [];
    public ObservableCollection<UserValueChoice> CopyUsers { get; } = [];
    public ObservableCollection<UserValueChoice> Providers { get; } = [];
    public ObservableCollection<UserValueChoice> Ratings { get; } = [];
    public ObservableCollection<string> Tags { get; } = [];
    public ObservableCollection<UserScheduleRow> Schedules { get; } = [];

    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial int Filter { get; set; }
    [ObservableProperty] public partial int Sort { get; set; }
    [ObservableProperty] public partial string CountText { get; set; } = "正在读取用户…";
    [ObservableProperty] public partial bool IsAdministrator { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListVisibility))]
    [NotifyPropertyChangedFor(nameof(EditorVisibility))]
    public partial bool Editing { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExistingVisibility))]
    [NotifyPropertyChangedFor(nameof(NewVisibility))]
    [NotifyPropertyChangedFor(nameof(AccessVisibility))]
    public partial bool IsNew { get; set; }
    [ObservableProperty] public partial string EditorTitle { get; set; } = "用户";
    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial string ConnectName { get; set; } = "";
    [ObservableProperty] public partial string SelectedProvider { get; set; } = "";
    [ObservableProperty] public partial string SelectedRating { get; set; } = "";
    [ObservableProperty] public partial string CopyFrom { get; set; } = "";
    [ObservableProperty] public partial bool NewAdministrator { get; set; }
    [ObservableProperty] public partial bool DeleteAll { get; set; }
    [ObservableProperty] public partial bool AllFolders { get; set; }
    [ObservableProperty] public partial bool AllChannels { get; set; }
    [ObservableProperty] public partial bool AllDevices { get; set; }
    [ObservableProperty] public partial bool IncludeTags { get; set; }
    [ObservableProperty] public partial bool AnyRestriction { get; set; }
    [ObservableProperty] public partial double RemoteBitrate { get; set; }
    [ObservableProperty] public partial double AutoRemoteQuality { get; set; }
    [ObservableProperty] public partial double StreamLimit { get; set; }
    [ObservableProperty] public partial string NewPassword { get; set; } = "";
    [ObservableProperty] public partial string ConfirmPassword { get; set; } = "";
    [ObservableProperty] public partial string ProfilePin { get; set; } = "";
    [ObservableProperty] public partial bool HasPassword { get; set; }
    [ObservableProperty] public partial string NewTag { get; set; } = "";
    [ObservableProperty] public partial string Tab { get; set; } = "profile";

    public Visibility ListVisibility => Show(!Editing);
    public Visibility EditorVisibility => Show(Editing);
    public Visibility NewVisibility => Show(IsNew);
    public Visibility ExistingVisibility => Show(!IsNew);
    public Visibility ProfileVisibility => Show(!IsNew && Tab == "profile");
    public Visibility AccessVisibility => Show(Tab == "access" || IsNew && CopyFrom.Length == 0);
    public Visibility ParentalVisibility => Show(!IsNew && Tab == "parentalcontrol");
    public Visibility PasswordVisibility => Show(!IsNew && Tab == "password");
    public Visibility SaveVisibility => Show(IsNew || Tab != "password");
    public Visibility CopyVisibility => Show(IsNew && CopyFrom.Length > 0);
    public Visibility DeleteFoldersVisibility => Show(!DeleteAll);
    public Visibility FoldersVisibility => Show(!AllFolders);
    public Visibility ChannelsVisibility => Show(Channels.Count > 0);
    public Visibility ChannelChoicesVisibility => Show(!AllChannels);
    public Visibility DeviceSectionVisibility => Show(!IsNew && _original?.IsAdministrator != true);
    public Visibility DevicesVisibility => Show(!AllDevices);
    public Visibility ScheduleVisibility => Show(_original?.IsAdministrator != true);
    public Visibility UnratedVisibility => Show(SelectedRating.Length > 0);
    public Visibility CombinationVisibility => Show(SelectedRating.Length > 0 && IncludeTags);
    public Visibility ProviderVisibility => Show(Providers.Count > 1 && _original?.IsAdministrator != true);
    public Visibility PinVisibility => Show(HasPassword);
    public bool CanUse => _attached && _scope?.IsCurrent == true && IsAdministrator && !Busy;
    public bool CanSave => CanUse && Editing;
    public bool CanEditFields => CanSave && _pendingNewPolicy is null;
    public string SaveLabel => _pendingNewPolicy is null ? "保存用户设置" : "重试保存初始权限";
    public bool CanManageExisting => CanEditFields && !IsNew;
    public string CurrentUserId => _original?.Id ?? "";

    internal Func<Task<PickedArtwork?>>? PickAvatar { get; set; }

    internal void Attach(EmbySession session)
    {
        Cancel();
        _attached = true;
        _scope = session.IsSignedIn ? session.Capture() : null;
        Editing = false;
        IsAdministrator = false;
        _original = null;
        _pendingNewPolicy = null;
        BusyChanged();
    }

    public override async Task ReloadAsync()
    {
        if (!_attached || Busy) return;
        if (_scope is not { IsCurrent: true } scope)
        {
            IsReady = true;
            CountText = "尚未连接服务器";
            Notify("请先登录", "在 EmbyNian → 服务器中登录管理员账号后打开用户管理。", InfoBarSeverity.Informational);
            return;
        }
        await RunAsync("读取用户失败", async token =>
        {
            var current = await scope.ExecuteAsync((client, ct) => client.GetManagedUserAsync(scope.Connection.UserId, ct), token);
            if (!Current(token)) return;
            IsAdministrator = current.IsAdministrator;
            if (!IsAdministrator)
            {
                Users.Clear();
                _allUsers.Clear();
                CountText = "需要服务器管理员权限";
                Notify("无法管理用户", "请使用服务器管理员账号登录。", InfoBarSeverity.Informational);
                return;
            }
            var users = await scope.ExecuteAsync((client, ct) => client.GetManagedUsersAsync(ct), token);
            if (!Current(token)) return;
            _allUsers.Clear();
            _allUsers.AddRange(users.Select(user => new ManagedUserRow(user)));
            ApplyFilter();
            _ = LoadAvatarsAsync(token);
        });
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private Task RefreshAsync() => ReloadAsync();

    [RelayCommand(CanExecute = nameof(CanUse))]
    private Task NewUserAsync() => OpenAsync(null);

    internal async Task OpenAsync(ManagedUserRow? row)
    {
        if (!CanUse || _scope is not { } scope) return;
        await RunAsync("打开用户失败", async token =>
        {
            var options = await scope.ExecuteAsync((client, ct) => client.GetUserEditorOptionsAsync(ct), token);
            var user = row is null ? null : await scope.ExecuteAsync((client, ct) => client.GetManagedUserAsync(row.Id, ct), token);
            if (!Current(token)) return;
            _options = options;
            _original = user;
            _pendingNewPolicy = null;
            PopulateEditor();
            Editing = true;
        });
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Back()
    {
        Editing = false;
        _original = null;
        NewPassword = ConfirmPassword = ProfilePin = "";
        BusyChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave || _scope is not { } scope) return;
        await RunAsync("保存未完成，请核对服务器状态后重试", async token =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(UserName);
            if (_pendingNewPolicy is { } pendingPolicy && _original is { } pendingUser)
            {
                await scope.ExecuteAsync((client, ct) => client.UpdateManagedUserPolicyAsync(pendingUser.Id, pendingPolicy, ct), token);
                _pendingNewPolicy = null;
            }
            else if (IsNew)
            {
                var created = await scope.ExecuteAsync((client, ct) => client.CreateManagedUserAsync(UserName,
                    CopyFrom, Selected(CopyOptions), ct), token);
                if (!Current(token)) return;
                // 创建已成功：后续权限写入失败也留在这个用户，重试不能再次创建同名账号。
                _original = created;
                var policy = (JsonObject)created.Policy.DeepClone();
                policy["IsAdministrator"] = NewAdministrator;
                if (CopyFrom.Length == 0)
                {
                    policy["EnableSubtitleManagement"] = NewAdministrator;
                    policy["EnableContentDeletion"] = NewAdministrator;
                    WriteAccess(policy, includeDevices: false);
                }
                _pendingNewPolicy = policy;
                PopulateEditor();
                scope.ThrowIfNotCurrent();
                await scope.ExecuteAsync((client, ct) => client.UpdateManagedUserPolicyAsync(created.Id, policy, ct), token);
                _pendingNewPolicy = null;
            }
            else if (_original is { } original)
            {
                var draft = BuildPolicy();
                var policy = _initialEditorPolicy is { } initial
                    ? EmbyUserPermissions.Merge(original.Policy, initial, draft) : draft;
                if (_initialEditorPolicy is { } accessBaseline && new[] { "EnableAllFolders", "EnabledFolders", "ExcludedSubFolders" }
                    .Any(key => !JsonNode.DeepEquals(accessBaseline[key], draft[key]))) policy["BlockedMediaFolders"] = null;
                await EmbyUserAdministration.SaveAsync(scope, original, policy, UserName, token);
                if (!Current(token)) return;
                if (ConnectName.Trim() != original.ConnectUserName)
                {
                    if (ConnectName.Trim().Length == 0)
                        await scope.ExecuteAsync((client, ct) => client.UnlinkManagedUserAsync(original.Id, ct), token);
                    else
                    {
                        var result = await scope.ExecuteAsync((client, ct) => client.LinkManagedUserAsync(original.Id, ConnectName.Trim(), ct), token);
                        if (!Current(token)) return;
                        if (EmbyManagedUser.Flag(result, "IsPending"))
                        {
                            await RefreshEditorAsync(token);
                            Notify("设置已保存", "Emby Connect 关联正在等待对方确认。", InfoBarSeverity.Success);
                            return;
                        }
                    }
                }
            }
            await RefreshEditorAsync(token);
            if (Current(token)) Notify("已保存", "用户设置已写入服务器。", InfoBarSeverity.Success);
        });
    }

    [RelayCommand(CanExecute = nameof(CanManageExisting))]
    private async Task DeleteAsync()
    {
        if (!CanManageExisting || _original is not { } user || _scope is not { } scope) return;
        await RunAsync("删除用户失败", async token =>
        {
            if (!await ConfirmAsync("删除用户", $"确定删除「{user.Name}」吗？账号、用户设置及观看记录将被删除，此操作无法撤销。", "删除")) return;
            if (!Current(token)) return;
            await scope.ExecuteAsync((client, ct) => client.DeleteManagedUserAsync(user.Id, ct), token);
            if (!Current(token)) return;
            _allUsers.RemoveAll(row => row.Id == user.Id);
            ApplyFilter();
            Editing = false;
            _original = null;
            NewPassword = ConfirmPassword = ProfilePin = "";
            Notify("已删除", $"用户「{user.Name}」已删除。", InfoBarSeverity.Success);
        });
    }

    [RelayCommand(CanExecute = nameof(CanManageExisting))]
    private Task SavePasswordAsync() => RunExistingAsync("保存密码失败", async (scope, user, token) =>
    {
        EmbyUserPermissions.ValidatePassword(user.IsAdministrator, NewPassword, ConfirmPassword);
        await scope.ExecuteAsync((client, ct) => client.UpdateManagedUserPasswordAsync(user.Id, NewPassword, ct), token);
        if (Current(token)) NewPassword = ConfirmPassword = "";
        return true;
    });

    [RelayCommand(CanExecute = nameof(CanManageExisting))]
    private Task SavePinAsync() => RunExistingAsync("保存个人 PIN 失败", async (scope, user, token) =>
    {
        await scope.ExecuteAsync((client, ct) => client.UpdateManagedUserPinAsync(user.Id, ProfilePin, ct), token);
        return true;
    });

    [RelayCommand(CanExecute = nameof(CanManageExisting))]
    private Task UploadAvatarAsync() => RunExistingAsync("上传头像失败", async (scope, user, token) =>
    {
        var picked = PickAvatar is null ? null : await PickAvatar();
        if (picked is not { } image || !Current(token)) return false;
        await scope.ExecuteAsync((client, ct) => client.UploadManagedUserImageAsync(user.Id, image.Bytes, ArtworkFile.ContentType(image.FileName), ct), token);
        return true;
    });

    [RelayCommand(CanExecute = nameof(CanManageExisting))]
    private Task RemoveAvatarAsync() => RunExistingAsync("移除头像失败", async (scope, user, token) =>
    {
        if (!await ConfirmAsync("移除头像", $"确定移除「{user.Name}」的头像吗？", "移除") || !Current(token)) return false;
        await scope.ExecuteAsync((client, ct) => client.DeleteManagedUserImageAsync(user.Id, ct), token);
        return true;
    });

    internal Task CopyDataAsync(string sourceId, IEnumerable<string> options) => RunExistingAsync("复制用户数据失败", async (scope, user, token) =>
    {
        var selected = options.ToArray();
        if (sourceId.Length == 0 || sourceId == user.Id || selected.Length == 0) throw new ArgumentException("请选择其他用户及需要复制的数据。");
        if (!await ConfirmAsync("复制用户数据", $"选中的数据将覆盖「{user.Name}」对应的用户数据。确定继续吗？", "复制") || !Current(token)) return false;
        await scope.ExecuteAsync((client, ct) => client.CopyManagedUserDataAsync(user.Id, sourceId, selected, ct), token);
        return true;
    }, refreshDraft: true);

    private async Task RunExistingAsync(string error, Func<EmbySessionScope, EmbyManagedUser, CancellationToken, Task<bool>> operation, bool refreshDraft = false)
    {
        if (!CanManageExisting || _scope is not { } scope || _original is not { } user) return;
        await RunAsync(error, async token =>
        {
            if (!await operation(scope, user, token) || !Current(token)) return;
            await RefreshEditorAsync(token, refreshDraft);
            if (Current(token)) Notify("操作完成", "已重新读取服务器上的用户信息。", InfoBarSeverity.Success);
        });
    }

    private async Task RefreshEditorAsync(CancellationToken token, bool refreshDraft = true)
    {
        if (_scope is not { } scope || _original is not { } user || !Current(token)) return;
        var fresh = await scope.ExecuteAsync((client, ct) => client.GetManagedUserAsync(user.Id, ct), token);
        if (!Current(token)) return;
        if (refreshDraft) _original = fresh;
        var index = _allUsers.FindIndex(row => row.Id == fresh.Id);
        var row = new ManagedUserRow(fresh);
        if (index < 0) _allUsers.Add(row); else _allUsers[index] = row;
        if (refreshDraft)
        {
            var tab = Tab;
            PopulateEditor();
            Tab = tab;
        }
        else
        {
            // 密码、PIN、头像操作不覆盖仍未保存的资料与权限草稿。
            HasPassword = fresh.HasPassword;
            ProfilePin = EmbyManagedUser.Text(fresh.Configuration, "ProfilePin");
        }
        ApplyFilter();
        _ = LoadAvatarsAsync(token);
    }

    private async Task LoadAvatarsAsync(CancellationToken token)
    {
        if (_scope is not { } scope) return;
        foreach (var row in _allUsers.Where(row => row.User.ImageTag.Length > 0 && row.Avatar is null).ToArray())
        {
            try
            {
                var bytes = await scope.ExecuteAsync((client, ct) => client.GetManagedUserImageAsync(row.Id, row.User.ImageTag, ct), token);
                if (!Current(token)) return;
                using var stream = new MemoryStream(bytes);
                var image = new BitmapImage();
                await image.SetSourceAsync(stream.AsRandomAccessStream());
                if (Current(token)) row.Avatar = image;
            }
            catch (OperationCanceledException) when (!Current(token)) { return; }
            catch (Exception) { /* 头像读取失败不阻止账号管理，卡片保留人物图标。 */ }
        }
    }

    private async Task RunAsync(string errorMessage, Func<CancellationToken, Task> action)
    {
        if (!_attached || _scope is not { IsCurrent: true } || Busy) return;
        var token = BeginLoad();
        try { await action(token); }
        catch (Exception error)
        {
            if (Current(token)) Report(_pendingNewPolicy is null ? errorMessage : "用户已创建，初始权限尚未保存；请点击重试", error);
        }
        finally { EndLoad(token); }
    }

    private bool Current(CancellationToken token) => _attached && IsCurrent(token) && _scope?.IsCurrent == true;
    private static IEnumerable<string> Selected(IEnumerable<UserOptionRow> rows) => rows.Where(row => row.Selected).Select(row => row.Id);

    private void ApplyFilter()
    {
        IEnumerable<ManagedUserRow> rows = _allUsers.Where(row => row.Name.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase)
            && (Filter switch { 1 => !row.User.IsDisabled, 2 => row.User.IsDisabled, 3 => row.User.IsAdministrator, _ => true }));
        rows = Sort switch
        {
            1 => rows.OrderByDescending(row => row.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => rows.OrderByDescending(row => EmbyManagedUser.Text(row.User.Document, "LastActivityDate"), StringComparer.Ordinal),
            3 => rows.OrderByDescending(row => EmbyManagedUser.Text(row.User.Document, "DateCreated"), StringComparer.Ordinal),
            _ => rows.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        Users.Clear();
        foreach (var row in rows) Users.Add(row);
        CountText = Users.Count == _allUsers.Count ? $"{Users.Count} 个用户" : $"显示 {Users.Count} / {_allUsers.Count} 个用户";
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnFilterChanged(int value) => ApplyFilter();
    partial void OnSortChanged(int value) => ApplyFilter();
    partial void OnEditingChanged(bool value) => BusyChanged();
    partial void OnIsAdministratorChanged(bool value) => BusyChanged();
    partial void OnIsNewChanged(bool value) => AnnounceEditor();
    partial void OnTabChanged(string value) => AnnounceEditor();
    partial void OnCopyFromChanged(string value)
    {
        if (value is null) CopyFrom = "";
        AnnounceEditor();
    }
    partial void OnSelectedProviderChanged(string value)
    {
        if (value is null) SelectedProvider = "";
    }
    partial void OnDeleteAllChanged(bool value) => OnPropertyChanged(nameof(DeleteFoldersVisibility));
    partial void OnAllFoldersChanged(bool value) => OnPropertyChanged(nameof(FoldersVisibility));
    partial void OnAllChannelsChanged(bool value) => OnPropertyChanged(nameof(ChannelChoicesVisibility));
    partial void OnAllDevicesChanged(bool value) => OnPropertyChanged(nameof(DevicesVisibility));
    partial void OnSelectedRatingChanged(string value)
    {
        if (value is null) SelectedRating = "";
        AnnounceEditor();
    }
    partial void OnIncludeTagsChanged(bool value) => AnnounceEditor();
    partial void OnHasPasswordChanged(bool value) => OnPropertyChanged(nameof(PinVisibility));

    private void AnnounceEditor()
    {
        foreach (var name in new[] { nameof(ProfileVisibility), nameof(AccessVisibility), nameof(ParentalVisibility), nameof(PasswordVisibility),
            nameof(SaveVisibility), nameof(CopyVisibility), nameof(ChannelsVisibility), nameof(DeviceSectionVisibility), nameof(ScheduleVisibility),
            nameof(UnratedVisibility), nameof(CombinationVisibility), nameof(ProviderVisibility), nameof(PinVisibility) }) OnPropertyChanged(name);
        BusyChanged();
    }

    protected override void BusyChanged()
    {
        OnPropertyChanged(nameof(CanUse));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanEditFields));
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(CanManageExisting));
        RefreshCommand.NotifyCanExecuteChanged();
        NewUserCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        SavePasswordCommand.NotifyCanExecuteChanged();
        SavePinCommand.NotifyCanExecuteChanged();
        UploadAvatarCommand.NotifyCanExecuteChanged();
        RemoveAvatarCommand.NotifyCanExecuteChanged();
    }

    public override void Cancel()
    {
        _attached = false;
        base.Cancel();
        NewPassword = ConfirmPassword = ProfilePin = "";
        BusyChanged();
    }
}
