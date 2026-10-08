using System.Net;
using System.Text.Json.Nodes;
using Momoka.Emby;
using Momoka.Shell.ViewModels;
using Momoka.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Momoka.Shell;

internal sealed partial class ShellNavigationProbe
{
    private async Task ServerUsersAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var server = new UsersProbeServer();
        fixture.Transport.Reply = server.ReplyAsync;
        var settings = new SettingsPage();
        Field(settings, "_request", new SettingsRequest(fixture.Services));
        _root.Children.Add(settings);
        try
        {
            settings.SelectedCategory = SettingsViewModel.ServerUsersCategory;
            await LayoutAsync(settings);
            var page = (ServerUsersPage)settings.CurrentPage;
            var vm = page.ViewModel;
            await UntilAsync(() => vm.IsReady);
            Require(vm.Users.Count == 3 && vm.CanUse, "用户列表没有读取完整或管理入口未启用：" + vm.NoticeMessage);
            foreach (var width in new[] { 1240, 760 })
            {
                ResizeInspect(width, 1000);
                await LayoutAsync(settings);
                await SaveUsersFrameAsync(settings, $"server-users-{width}.png");
                Require(Get<GridView>(page, "UsersGrid").Items.Count == 3, "用户卡片未实现");
            }
            var search = Get<TextBox>(page, "UsersSearchBox");
            search.Text = "家庭";
            Require(vm.Users.Count == 1, "输入搜索词没有即时过滤");
            search.Text = "";
            Get<ComboBox>(page, "UsersFilterBox").SelectedIndex = 2;
            Require(vm.Users.Count == 1 && vm.Users[0].User.IsDisabled, "禁用筛选没有生效");
            Get<ComboBox>(page, "UsersFilterBox").SelectedIndex = 0;
            await vm.OpenAsync(vm.Users.First(row => row.Id == "family"));
            await LayoutAsync(page);
            Require(vm.Editing && vm.UserName == "家庭账号" && vm.SelectedProvider == "local", "用户资料未载入：" + vm.NoticeMessage);
            Require(Get<ComboBox>(page, "UserProviderBox").SelectedValue as string == "local", "身份验证下拉框没有正确绑定服务器值");
            ResizeInspect(1240, 1000);
            await LayoutAsync(settings);
            await SaveUsersFrameAsync(settings, "server-users-profile.png");

            Get<NavigationView>(page, "UserTabs").SelectedItem = Get<NavigationViewItem>(page, "UserAccessTab");
            Get<CheckBox>(page, "UserAllFoldersCheck").IsChecked = false;
            vm.UserName = "家庭账号（修改资料后切到访问）";
            Require(vm.Tab == "access" && !vm.AllFolders, "访问页签或全库开关没有接线");
            vm.Folders[0].Children[1].Selected = false;
            Require(vm.Folders[0].Selected, "一个子目录取消错误地关闭了整个媒体库");
            await SaveUsersFrameAsync(settings, "server-users-access.png");
            await vm.SaveCommand.ExecuteAsync(null);
            Require(!vm.NoticeSeverity.Equals(InfoBarSeverity.Error), "访问设置保存失败：" + vm.NoticeMessage);
            var policy = server.Users["family"]["Policy"]!.AsObject();
            Require(!policy["EnableAllFolders"]!.GetValue<bool>() && policy["ExcludedSubFolders"]!.AsArray().Any(node => node?.ToString() == "movies_two"), "子目录权限没有写回服务器");
            Require(policy["FuturePermission"]!["Keep"]!.GetValue<int>() == 42, "保存权限丢失未知字段");
            Require(server.Users["family"]["Name"]!.ToString() == "家庭账号（修改资料后切到访问）", "保存访问时丢失资料草稿");

            Get<NavigationView>(page, "UserTabs").SelectedItem = Get<NavigationViewItem>(page, "UserParentalTab");
            Get<ComboBox>(page, "UserRatingBox").SelectedValue = "7";
            vm.IncludeTags = true;
            vm.AnyRestriction = true;
            Get<TextBox>(page, "UserNewTagBox").Text = "合家欢";
            Call(page, "OnAddTag", page, new RoutedEventArgs());
            vm.Schedules.Add(new(EmbyUserPermissions.Schedule("Saturday", 8, 22)));
            await SaveUsersFrameAsync(settings, "server-users-parental.png");
            await vm.SaveCommand.ExecuteAsync(null);
            policy = server.Users["family"]["Policy"]!.AsObject();
            Require(policy["MaxParentalRating"]!.GetValue<int>() == 7 && policy["AllowTagOrRating"]!.GetValue<bool>()
                && policy["BlockedTags"]!.AsArray().Count == 1 && policy["AccessSchedules"]!.AsArray().Count == 1, "家长控制保存不完整：" + vm.NoticeMessage);

            Get<NavigationView>(page, "UserTabs").SelectedItem = Get<NavigationViewItem>(page, "UserPasswordTab");
            await SaveUsersFrameAsync(settings, "server-users-password.png");
            Get<PasswordBox>(page, "UserNewPasswordBox").Password = "fixture-password";
            vm.UserName = "密码操作保留的草稿";
            Get<PasswordBox>(page, "UserConfirmPasswordBox").Password = "different";
            var before = server.Writes.Count;
            await vm.SavePasswordCommand.ExecuteAsync(null);
            Require(server.Writes.Count == before && vm.NoticeSeverity == InfoBarSeverity.Error, "不一致密码仍然被提交");
            Get<PasswordBox>(page, "UserConfirmPasswordBox").Password = "fixture-password";
            await vm.SavePasswordCommand.ExecuteAsync(null);
            Require(server.Writes.Last().Path.EndsWith("/Password", StringComparison.Ordinal) && vm.NewPassword.Length == 0, "密码保存未发送或未清除输入");
            Require(vm.UserName == "密码操作保留的草稿", "保存密码覆盖了未保存的资料草稿");
            Get<PasswordBox>(page, "UserPinBox").Password = "0123";
            await vm.SavePinCommand.ExecuteAsync(null);
            Require(server.Users["family"]["Configuration"]!["ProfilePin"]!.ToString() == "0123", "个人 PIN 未保存");
            Require(server.Writes.All(write => !write.Path.Contains("fixture-password", StringComparison.Ordinal)), "密码出现在地址中");
        }
        finally { settings.ReleaseHosted(); _root.Children.Remove(settings); }
    }

    private async Task ServerUsersMutationsAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var server = new UsersProbeServer();
        fixture.Transport.Reply = server.ReplyAsync;
        var page = new ServerUsersPage();
        _root.Children.Add(page);
        var vm = page.ViewModel;
        vm.Attach(fixture.Session);
        try
        {
            await vm.ReloadAsync();
            await vm.NewUserCommand.ExecuteAsync(null);
            await LayoutAsync(page);
            Get<TextBox>(page, "NewUserNameBox").Text = "离线新用户";
            Get<ComboBox>(page, "NewUserCopyFrom").SelectedValue = "family";
            Require(vm.CopyFrom == "family" && vm.CopyVisibility == Visibility.Visible, "新建用户复制下拉框绑定失败");
            await SaveUsersFrameAsync(page, "server-users-new.png");
            server.FailPolicyOnce = true;
            await vm.SaveCommand.ExecuteAsync(null);
            Require(server.Creates == 1 && !vm.IsNew && vm.NoticeSeverity == InfoBarSeverity.Error, "创建后的部分失败没有保留已创建身份");
            await vm.SaveCommand.ExecuteAsync(null);
            Require(server.Creates == 1 && vm.NoticeSeverity == InfoBarSeverity.Success && vm.CurrentUserId == "new", "重试错误地重新创建用户");

            var deleting = vm.DeleteCommand.ExecuteAsync(null);
            await UntilAsync(() => Get<ContentDialog?>(page, "_dialog") is not null);
            var dialog = Get<ContentDialog>(page, "_dialog");
            await LayoutAsync(dialog);
            Require(dialog.DefaultButton == ContentDialogButton.Close, "删除确认没有默认取消");
            await SaveUsersFrameAsync(dialog, "server-users-delete-confirm.png");
            dialog.Hide();
            await deleting;
            Require(server.Users.ContainsKey("new"), "取消删除仍移除了用户");

            vm.UseConfirm((_, _, _) => Task.FromResult(true));
            await vm.CopyDataAsync("family", ["userconfiguration"]);
            var copy = server.Writes.Last(write => write.Path.EndsWith("/CopyData", StringComparison.Ordinal));
            Require(copy.Path.EndsWith("/Users/family/CopyData", StringComparison.Ordinal)
                && copy.Body["ToUserIds"]![0]!.ToString() == "new", "复制方向或目标错误");
            await vm.DeleteCommand.ExecuteAsync(null);
            Require(!server.Users.ContainsKey("new") && !vm.Editing, "删除成功后未更新列表");

            await vm.OpenAsync(vm.Users.First(row => row.Id == "family"));
            Get<TextBox>(page, "UserNameBox").Text = "家庭新名称";
            vm.ConnectName = "fixture-connect";
            var policiesBeforeRename = server.Writes.Count(write => write.Path.EndsWith("/Policy", StringComparison.Ordinal));
            await vm.SaveCommand.ExecuteAsync(null);
            Require(server.Users["family"]["Name"]!.ToString() == "家庭新名称"
                && server.Users["family"]["ConnectUserName"]!.ToString() == "fixture-connect", "资料改名或 Connect 关联没有保存");
            Require(server.Writes.Count(write => write.Path.EndsWith("/Policy", StringComparison.Ordinal)) == policiesBeforeRename,
                "仅改名却把控件默认值当作权限修改提交");
            vm.PickAvatar = () => Task.FromResult<PickedArtwork?>(new([1, 2, 3], "avatar.png"));
            await vm.UploadAvatarCommand.ExecuteAsync(null);
            Require(server.Avatar == "AQID", "头像上传没有发送 Base64 正文");
            await vm.RemoveAvatarCommand.ExecuteAsync(null);
            Require(server.Avatar.Length == 0, "头像删除未发送");
        }
        finally { page.Release(); _root.Children.Remove(page); }
    }

    private async Task ServerUsersIdentityAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var server = new UsersProbeServer();
        fixture.Transport.Reply = server.ReplyAsync;
        var page = new ServerUsersPage();
        _root.Children.Add(page);
        var vm = page.ViewModel;
        vm.Attach(fixture.Session);
        try
        {
            await vm.ReloadAsync();
            await vm.OpenAsync(vm.Users.First(row => row.Id == "family"));
            var confirmation = Pending<bool>();
            vm.UseConfirm((_, _, _) => confirmation.Task);
            var deletion = vm.DeleteCommand.ExecuteAsync(null);
            await fixture.SelectAsync("b.invalid");
            confirmation.SetResult(true);
            await deletion;
            Require(server.Writes.Count == 0, "切服之后仍接受旧删除确认");
            vm.Attach(fixture.Session);
            await vm.ReloadAsync();
            await vm.OpenAsync(vm.Users.First(row => row.Id == "family"));
            var pending = Pending<HttpResponseMessage>();
            server.PendingUser = pending;
            vm.UserName = "不应保存";
            var saving = vm.SaveCommand.ExecuteAsync(null);
            page.Release();
            pending.SetResult(Json(server.Users["family"]));
            await saving;
            Require(server.Writes.Count == 0 && !vm.CanUse, "离页之后仍提交旧用户设置");
            server.PendingUser = null;
            server.Users["user"]["Policy"]!["IsAdministrator"] = false;
            vm.Attach(fixture.Session);
            await vm.ReloadAsync();
            Require(!vm.IsAdministrator && !vm.CanUse && vm.Users.Count == 0, "普通账号仍有用户管理能力");
        }
        finally { page.Release(); _root.Children.Remove(page); }
    }

    private async Task SaveUsersFrameAsync(FrameworkElement element, string name)
    {
        await Task.Delay(300);
        await SaveReturnFrameAsync(element, name);
    }

    private sealed class UsersProbeServer
    {
        internal Dictionary<string, JsonObject> Users { get; } = new()
        {
            ["user"] = User("user", "服务器管理员", true),
            ["family"] = User("family", "家庭账号", false),
            ["guest"] = User("guest", "来宾账号", false, disabled: true)
        };
        internal List<(string Path, JsonObject Body)> Writes { get; } = [];
        internal int Creates { get; private set; }
        internal bool FailPolicyOnce { get; set; }
        internal string Avatar { get; private set; } = "";
        internal TaskCompletionSource<HttpResponseMessage>? PendingUser { get; set; }

        internal async Task<HttpResponseMessage> ReplyAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                if (path.EndsWith("Users/Query", StringComparison.Ordinal)) return Json(new { Items = Users.Values.ToArray(), TotalRecordCount = Users.Count });
                if (path.EndsWith("Library/SelectableMediaFolders", StringComparison.Ordinal)) return Json(ParseArray("""[{"Id":"movies","Guid":"movies","Name":"电影","SubFolders":[{"Id":"one","Path":"/media/family"},{"Id":"two","Path":"/media/other"}]}]"""));
                if (path.EndsWith("Library/MediaFolders", StringComparison.Ordinal)) return Json(new { Items = new[] { new { Id = "movies", Name = "电影" } } });
                if (path.EndsWith("Auth/Providers", StringComparison.Ordinal)) return Json(new[] { new { Id = "local", Name = "Emby 内置验证" }, new { Id = "ldap", Name = "LDAP" } });
                if (path.EndsWith("/Features", StringComparison.Ordinal)) return Json(new[] { new { Id = "collections", Name = "合集" }, new { Id = "future.hidden", Name = "不可展示的内部功能" } });
                if (path.EndsWith("Localization/ParentalRatings", StringComparison.Ordinal)) return Json(new[] { new { Name = "G", Value = 1 }, new { Name = "PG", Value = 7 } });
                if (path.EndsWith("/Channels", StringComparison.Ordinal)) return Json(new { Items = new[] { new { Id = "channel", Name = "示例频道" } } });
                if (path.EndsWith("/Devices", StringComparison.Ordinal)) return Json(new { Items = new[] { new { Id = "device", ReportedDeviceId = "tv", Name = "客厅电视", AppName = "Emby" } } });
                if (path.EndsWith("Users/CopyDataOptions", StringComparison.Ordinal)) return Json(new { DataOptions = new[] { new { Id = "userpolicy", Name = "用户权限" }, new { Id = "userconfiguration", Name = "用户设置" }, new { Id = "userdata", Name = "观看记录与收藏" } } });
                if (path.EndsWith("System/Configuration", StringComparison.Ordinal)) return Json(new { EnableRemoteAccess = true });
                var id = path.Split('/').Last();
                if (Users.TryGetValue(id, out var user)) return PendingUser is { } pending && id == "family" ? await pending.Task : Json(user);
                return FakeTransport.Standard(request);
            }
            var bodyText = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            if (path.EndsWith("/Images/Primary", StringComparison.Ordinal)) { Avatar = bodyText; return new(HttpStatusCode.NoContent); }
            if (path.EndsWith("/Images/Primary/Delete", StringComparison.Ordinal)) { Avatar = ""; return new(HttpStatusCode.NoContent); }
            var body = bodyText.Length > 0 ? JsonNode.Parse(bodyText)!.AsObject() : new JsonObject();
            Writes.Add((path, body));
            if (path.EndsWith("Users/New", StringComparison.Ordinal))
            {
                Creates++;
                Users["new"] = User("new", body["Name"]!.ToString(), false);
                return Json(Users["new"]);
            }
            var parts = path.Split('/');
            var userIndex = Array.IndexOf(parts, "Users");
            if (userIndex < 0 || !Users.TryGetValue(parts[userIndex + 1], out var target)) throw new InvalidOperationException("不支持的用户写入");
            var suffix = string.Join('/', parts.Skip(userIndex + 2));
            switch (suffix)
            {
                case "": Users[parts[userIndex + 1]] = body; break;
                case "Policy":
                    if (FailPolicyOnce) { FailPolicyOnce = false; return new(HttpStatusCode.InternalServerError); }
                    target["Policy"] = body.DeepClone(); break;
                case "Configuration/Partial":
                    foreach (var pair in body) target["Configuration"]![pair.Key] = pair.Value?.DeepClone();
                    break;
                case "Password": target["HasConfiguredPassword"] = body["NewPw"]?.ToString().Length > 0; break;
                case "Delete": Users.Remove(parts[userIndex + 1]); break;
                case "Connect/Link": target["ConnectUserName"] = body["ConnectUsername"]!.ToString(); return Json(new { IsPending = false });
                case "Connect/Link/Delete": target["ConnectUserName"] = ""; break;
                case "CopyData":
                    Require(body["ToUserIds"] is JsonArray && body["CopyOptions"] is JsonArray, "复制数据协议错误");
                    break;
                default: throw new InvalidOperationException("未核对的用户写入：" + suffix);
            }
            return new(HttpStatusCode.NoContent);
        }

        private static JsonArray ParseArray(string value) => JsonNode.Parse(value)!.AsArray();
        private static JsonObject User(string id, string name, bool admin, bool disabled = false) => new()
        {
            ["Id"] = id,
            ["Name"] = name,
            ["HasConfiguredPassword"] = true,
            ["Policy"] = new JsonObject
            {
                ["IsAdministrator"] = admin,
                ["IsDisabled"] = disabled,
                ["AuthenticationProviderId"] = "local",
                ["EnableMediaPlayback"] = true,
                ["EnableAllFolders"] = true,
                ["EnableAllChannels"] = true,
                ["EnableAllDevices"] = true,
                ["EnableRemoteAccess"] = true,
                ["FuturePermission"] = new JsonObject { ["Keep"] = 42 },
                ["RestrictedFeatures"] = new JsonArray("future.hidden")
            },
            ["Configuration"] = new JsonObject { ["ProfilePin"] = "" }
        };
    }
}
