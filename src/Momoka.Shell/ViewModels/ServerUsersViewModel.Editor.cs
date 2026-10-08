using System.Text.Json.Nodes;
using Momoka.Emby;

namespace Momoka.Shell.ViewModels;

public sealed partial class ServerUsersViewModel
{
    private void PopulateEditor()
    {
        if (_options is not { } options) return;
        IsNew = _original is null;
        var policy = _original?.Policy ?? new JsonObject
        {
            ["EnableAllFolders"] = true,
            ["EnableAllChannels"] = true,
            ["EnableAllDevices"] = true
        };
        UserName = _original?.Name ?? "";
        EditorTitle = _original?.Name ?? "新建用户";
        ConnectName = _original?.ConnectUserName ?? "";
        Tab = "profile";
        NewAdministrator = false;
        CopyFrom = "";
        NewPassword = ConfirmPassword = "";
        ProfilePin = _original is null ? "" : EmbyManagedUser.Text(_original.Configuration, "ProfilePin");
        HasPassword = _original?.HasPassword == true;
        RemoteBitrate = Number(policy, "RemoteClientBitrateLimit") / 1_000_000;
        AutoRemoteQuality = Number(policy, "AutoRemoteQuality") / 1_000_000;
        StreamLimit = Number(policy, "SimultaneousStreamLimit");
        DeleteAll = EmbyManagedUser.Flag(policy, "EnableContentDeletion");
        AllFolders = EmbyManagedUser.Flag(policy, "EnableAllFolders");
        AllChannels = EmbyManagedUser.Flag(policy, "EnableAllChannels");
        AllDevices = EmbyManagedUser.Flag(policy, "EnableAllDevices");
        IncludeTags = EmbyManagedUser.Flag(policy, "IsTagBlockingModeInclusive");
        AnyRestriction = EmbyManagedUser.Flag(policy, "AllowTagOrRating");

        PermissionGroups.Clear();
        foreach (var group in EmbyUserPermissions.Profile.Where(permission =>
            (permission.Key != "IsDisabled" || _original?.IsAdministrator != true || _original.IsDisabled)
            && (permission.Key != "EnableRemoteAccess" || EmbyManagedUser.Flag(options.ServerConfiguration, "EnableRemoteAccess", true)))
            .GroupBy(permission => permission.Group))
            PermissionGroups.Add(new(group.Key, group.Select(permission => new UserOptionRow(permission.Key, permission.Label,
                EmbyManagedUser.Flag(policy, permission.Key, permission.Key == "EnableRemoteAccess"), permission.Note)).ToArray()));

        Features.Clear();
        var restricted = EmbyManagedUser.Strings(policy, "RestrictedFeatures");
        foreach (var feature in options.Features.OfType<JsonObject>())
        {
            var id = EmbyManagedUser.Text(feature, "Id");
            if (id.Length > 0 && !id.Contains('.', StringComparison.Ordinal))
                Features.Add(new(id, EmbyManagedUser.Text(feature, "Name"), !restricted.Contains(id, StringComparer.Ordinal)));
        }
        Providers.Clear();
        foreach (var provider in options.Providers.OfType<JsonObject>())
            Providers.Add(new(EmbyManagedUser.Text(provider, "Id"), EmbyManagedUser.Text(provider, "Name")));
        SelectedProvider = EmbyManagedUser.Text(policy, "AuthenticationProviderId");
        if (SelectedProvider.Length > 0 && Providers.All(provider => provider.Id != SelectedProvider)) Providers.Add(new(SelectedProvider, SelectedProvider));
        if (SelectedProvider.Length == 0 && Providers.Count == 1) SelectedProvider = Providers[0].Id;

        DeleteFolders.Clear();
        var deletionIds = EmbyManagedUser.Strings(policy, "EnableContentDeletionFromFolders");
        foreach (var folder in Items(options.DeleteFolders).Concat(Items(options.DeleteChannels)))
            DeleteFolders.Add(new(Identity(folder), EmbyManagedUser.Text(folder, "Name"), DeleteAll || deletionIds.Contains(Identity(folder))));

        Folders.Clear();
        var enabledFolders = EmbyManagedUser.Strings(policy, "EnabledFolders");
        var excluded = EmbyManagedUser.Strings(policy, "ExcludedSubFolders");
        foreach (var folder in options.Folders.OfType<JsonObject>())
        {
            var id = Identity(folder);
            var choice = new UserOptionRow(id, EmbyManagedUser.Text(folder, "Name"), AllFolders || enabledFolders.Contains(id),
                enabled: EmbyManagedUser.Flag(folder, "IsUserAccessConfigurable", true));
            foreach (var sub in (folder["SubFolders"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (!EmbyManagedUser.Flag(sub, "IsUserAccessConfigurable", true)) continue;
                var subId = id + "_" + EmbyManagedUser.Text(sub, "Id");
                choice.Children.Add(new(subId, EmbyManagedUser.Text(sub, "Path"),
                    AllFolders || choice.Selected && !excluded.Contains(subId) && !excluded.Contains(id + "_" + Identity(sub)),
                    alternateId: id + "_" + Identity(sub)));
            }
            var synchronizing = false;
            choice.PropertyChanged += (_, e) =>
            {
                if (synchronizing || e.PropertyName != nameof(UserOptionRow.Selected)) return;
                synchronizing = true;
                foreach (var child in choice.Children) child.Selected = choice.Selected;
                synchronizing = false;
            };
            foreach (var child in choice.Children)
                child.PropertyChanged += (_, e) =>
                {
                    if (synchronizing || e.PropertyName != nameof(UserOptionRow.Selected)) return;
                    synchronizing = true;
                    choice.Selected = choice.Children.Any(value => value.Selected);
                    synchronizing = false;
                };
            Folders.Add(choice);
        }
        Channels.Clear();
        foreach (var channel in Items(options.Channels))
            Channels.Add(new(Identity(channel), EmbyManagedUser.Text(channel, "Name"),
                AllChannels || EmbyManagedUser.Strings(policy, "EnabledChannels").Contains(Identity(channel))));
        Devices.Clear();
        foreach (var device in Items(options.Devices))
        {
            var id = EmbyManagedUser.Text(device, "ReportedDeviceId");
            if (id.Length == 0) id = Identity(device);
            Devices.Add(new(id, EmbyManagedUser.Text(device, "Name") + " · " + EmbyManagedUser.Text(device, "AppName"),
                AllDevices || EmbyManagedUser.Strings(policy, "EnabledDevices").Contains(id)));
        }

        Ratings.Clear();
        Ratings.Add(new("", "不限"));
        foreach (var group in options.Ratings.OfType<JsonObject>().GroupBy(rating => EmbyManagedUser.Text(rating, "Value")))
            Ratings.Add(new(group.Key, string.Join(" / ", group.Select(rating => EmbyManagedUser.Text(rating, "Name")))));
        SelectedRating = EmbyManagedUser.Text(policy, "MaxParentalRating");
        if (SelectedRating.Length > 0 && Ratings.All(rating => rating.Id != SelectedRating)) Ratings.Add(new(SelectedRating, SelectedRating));
        Unrated.Clear();
        foreach (var (id, name) in new[] { ("Book", "图书"), ("Game", "游戏"), ("ChannelContent", "频道内容"), ("LiveTvChannel", "直播电视"),
            ("Movie", "电影"), ("Music", "音乐"), ("Trailer", "预告片"), ("Series", "电视节目") })
            Unrated.Add(new(id, name, EmbyManagedUser.Strings(policy, "BlockUnratedItems").Contains(id)));
        Tags.Clear();
        foreach (var tag in EmbyManagedUser.Strings(policy, "BlockedTags")) Tags.Add(tag);
        NewTag = "";
        Schedules.Clear();
        foreach (var schedule in (policy["AccessSchedules"] as JsonArray ?? []).OfType<JsonObject>())
            Schedules.Add(new((JsonObject)schedule.DeepClone()));
        CopyOptions.Clear();
        foreach (var option in (options.CopyOptions["DataOptions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = EmbyManagedUser.Text(option, "Id");
            CopyOptions.Add(new(id, EmbyManagedUser.Text(option, "Name"), id is "userpolicy" or "userconfiguration",
                EmbyManagedUser.Text(option, "ShortOverview")));
        }
        CopyUsers.Clear();
        CopyUsers.Add(new("", "不复制"));
        foreach (var user in _allUsers.Where(row => row.Id != _original?.Id)) CopyUsers.Add(new(user.Id, user.Name));
        OnPropertyChanged(nameof(CopyFrom));
        // 以控件实际表示的初值比较草稿，不能把“缺省值 → 显式默认值”误算为用户修改。
        _initialEditorPolicy = BuildPolicy();
        AnnounceEditor();
    }

    private JsonObject BuildPolicy()
    {
        var policy = (JsonObject)(_original?.Policy.DeepClone() ?? new JsonObject());
        // 页签共用一份草稿；保存全部非密码字段，避免丢掉另一页的修改。
        {
            foreach (var option in PermissionGroups.SelectMany(group => group.Options)) policy[option.Id] = option.Selected;
            policy["AuthenticationProviderId"] = SelectedProvider;
            policy["RemoteClientBitrateLimit"] = Bitrate(RemoteBitrate, double.MaxValue);
            policy["AutoRemoteQuality"] = Bitrate(AutoRemoteQuality, 200);
            if (!double.IsFinite(StreamLimit) || StreamLimit < 0 || StreamLimit > 50 || StreamLimit != Math.Truncate(StreamLimit))
                throw new ArgumentException("同时播放流数量须为 0 至 50 的整数，0 表示不限。");
            policy["SimultaneousStreamLimit"] = (int)StreamLimit;
            policy["EnableContentDeletion"] = DeleteAll;
            policy["EnableContentDeletionFromFolders"] = DeleteAll ? new JsonArray() : WithUnlisted(policy, "EnableContentDeletionFromFolders", DeleteFolders);
            policy["RestrictedFeatures"] = EmbyUserPermissions.Array(Features.Where(row => !row.Selected).Select(row => row.Id)
                .Concat(EmbyManagedUser.Strings(policy, "RestrictedFeatures").Where(id => Features.All(row => row.Id != id))));
        }
        WriteAccess(policy, includeDevices: _original?.IsAdministrator != true);
        {
            policy["MaxParentalRating"] = SelectedRating.Length == 0 ? null : int.Parse(SelectedRating, System.Globalization.CultureInfo.InvariantCulture);
            policy["IsTagBlockingModeInclusive"] = IncludeTags;
            policy["AllowTagOrRating"] = AnyRestriction;
            policy["BlockUnratedItems"] = WithUnlisted(policy, "BlockUnratedItems", Unrated);
            policy["BlockedTags"] = EmbyUserPermissions.Array(Tags);
            if (_original?.IsAdministrator != true) policy["AccessSchedules"] = new JsonArray(Schedules.Select(row => row.Value.DeepClone()).ToArray());
        }
        return policy;
    }

    private void WriteAccess(JsonObject policy, bool includeDevices)
    {
        policy["EnableAllFolders"] = AllFolders;
        policy["EnabledFolders"] = AllFolders ? new JsonArray() : WithUnlisted(policy, "EnabledFolders", Folders);
        var children = Folders.SelectMany(row => row.Children).ToArray();
        policy["ExcludedSubFolders"] = AllFolders ? new JsonArray() : EmbyUserPermissions.Array(children.Where(row => !row.Selected).Select(row => row.Id)
            .Concat(EmbyManagedUser.Strings(policy, "ExcludedSubFolders").Where(id => children.All(row => row.Id != id && row.AlternateId != id))));
        policy["EnableAllChannels"] = AllChannels;
        policy["EnabledChannels"] = AllChannels ? new JsonArray() : WithUnlisted(policy, "EnabledChannels", Channels);
        if (includeDevices)
        {
            policy["EnableAllDevices"] = AllDevices;
            policy["EnabledDevices"] = AllDevices ? new JsonArray() : WithUnlisted(policy, "EnabledDevices", Devices);
        }
        policy["BlockedMediaFolders"] = null;
    }

    private static JsonArray WithUnlisted(JsonObject policy, string key, IEnumerable<UserOptionRow> choices)
    {
        var rows = choices.ToArray();
        return EmbyUserPermissions.Array(Selected(rows).Concat(EmbyManagedUser.Strings(policy, key).Where(id => rows.All(row => row.Id != id))));
    }

    private static double Number(JsonObject value, string key) =>
        double.TryParse(value[key]?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static long Bitrate(double value, double maximum)
    {
        if (!double.IsFinite(value) || value < 0 || value > maximum || value > long.MaxValue / 1_000_000d)
            throw new ArgumentException("码率必须是有效的非负数。");
        return (long)Math.Round(value * 1_000_000);
    }
    private static IEnumerable<JsonObject> Items(JsonObject result) => (result["Items"] as JsonArray ?? []).OfType<JsonObject>();
    private static string Identity(JsonObject item) => EmbyManagedUser.Text(item, "Guid") is { Length: > 0 } guid ? guid : EmbyManagedUser.Text(item, "Id");

    internal void AddTag()
    {
        var tag = NewTag.Trim();
        if (tag.Length > 0 && !Tags.Contains(tag, StringComparer.Ordinal)) Tags.Add(tag);
        NewTag = "";
    }
}
