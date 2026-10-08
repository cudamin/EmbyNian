using System.Net;
using System.Text.Json.Nodes;
using Momoka.Emby;
using Momoka.Shell.ViewModels;
using Momoka.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Momoka.Shell;

internal sealed partial class ShellNavigationProbe
{
    private async Task ServerLibrariesAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var server = new LibrariesProbeServer();
        fixture.Transport.Reply = server.ReplyAsync;
        var settings = new SettingsPage();
        Field(settings, "_request", new SettingsRequest(fixture.Services));
        _root.Children.Add(settings);
        try
        {
            settings.SelectedCategory = SettingsViewModel.ServerLibrariesCategory;
            await LayoutAsync(settings);
            var page = (ServerLibrariesPage)settings.CurrentPage;
            var vm = page.ViewModel;
            await UntilAsync(() => vm.IsReady);
            Require(vm.CanUse && vm.Libraries.Count == 2, "媒体库入口或读取失败：" + vm.NoticeMessage);
            foreach (var width in new[] { 1240, 760 })
            {
                ResizeInspect(width, 1000); await LayoutAsync(settings);
                await SaveUsersFrameAsync(settings, $"server-libraries-list-{width}.png");
                Require(Get<GridView>(page, "LibrariesGrid").Items.Count == 2, "媒体库卡片未绑定");
            }
            await vm.OpenAsync(vm.Libraries[0]);
            await LayoutAsync(page);
            Require(vm.Editing && vm.CanSave && vm.Paths.Count == 1 && vm.Providers.Count >= 5, "编辑器未完整读取：" + vm.NoticeMessage);
            Require(vm.Sections.SelectMany(section => section.Fields).Any(field => field.Key == "SubtitleDownloadLanguages" && field.Visibility == Visibility.Visible), "字幕下载语言没有按服务器能力显示");
            Require(vm.ImageSections.Count == 1, "图片设置缺失");
            await vm.SaveCommand.ExecuteAsync(null);
            Require(!vm.Editing && server.Writes.Count == 0, "未改动编辑器却覆写了配置：" + vm.NoticeMessage);

            await vm.OpenAsync(vm.Libraries[0]);
            await LayoutAsync(page);
            Get<TextBox>(page, "LibraryNameBox").Text = "精选电影";
            var realtime = Descendants(page).OfType<ToggleSwitch>().Single(control => AutomationProperties.GetName(control) == "启用实时监控");
            realtime.IsOn = false;
            var minimum = Descendants(page).OfType<NumberBox>().Single(control => AutomationProperties.GetName(control) == "最小恢复播放百分比");
            minimum.Value = 5;
            var metadataGroup = vm.Providers.Single(group => group.Kind == "metadata");
            metadataGroup.Providers[0].Enabled = false;
            metadataGroup.Move(metadataGroup.Providers[1], -1);
            vm.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "SubtitleDownloadLanguages").SetLanguages(["zh", "en"]);
            vm.ImageSections[0].Fields.Single(field => field.Label == "每个项目的最大背景图数量").SelectedIndex = 3;
            // 编辑期间另一个客户端改了图片尺寸与插件字段；保存不得把它们倒回去。
            server.Folders["movies"]["LibraryOptions"]!["Plugin"]!["Keep"] = 77;
            server.Folders["movies"]["LibraryOptions"]!["TypeOptions"]![0]!["ImageOptions"]![0]!["MinWidth"] = 1920;
            ResizeInspect(1240, 1000); await LayoutAsync(settings);
            await SaveUsersFrameAsync(settings, "server-libraries-editor-1240.png");
            ResizeInspect(760, 1000); await LayoutAsync(settings);
            await SaveUsersFrameAsync(settings, "server-libraries-editor-760.png");
            Get<ScrollView>(page, "LibraryEditorScroll").ScrollTo(0, 100000, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
            await SaveUsersFrameAsync(settings, "server-libraries-provider-images.png");
            await vm.SaveCommand.ExecuteAsync(null);
            var options = server.Folders["movies"]["LibraryOptions"]!.AsObject();
            Require(!vm.Editing && vm.NoticeSeverity == InfoBarSeverity.Success, "保存未完成：" + vm.NoticeMessage);
            Require(!EmbyLibraryOptions.Flag(options, "EnableRealtimeMonitor") && EmbyLibraryOptions.Number(options, "MinResumePct") == 5, "原生开关或数字输入未写回");
            Require(server.Folders["movies"]["Name"]!.ToString() == "精选电影", "改名未写回");
            Require(options["Plugin"]!["Keep"]!.ToString() == "77" && options["TypeOptions"]![0]!["ImageOptions"]![0]!["MinWidth"]!.ToString() == "1920", "覆盖了并发更新");
            Require(options["TypeOptions"]![0]!["ImageOptions"]![0]!["Limit"]!.ToString() == "3", "图片数量没写回");
            Require(options["TypeOptions"]![0]!["MetadataFetcherOrder"]![0]!.ToString() == "B" && !EmbyLibraryOptions.Strings(options["TypeOptions"]![0]!.AsObject(), "MetadataFetchers").Contains("A"), "提供者选择或排序没写回");
            Require(EmbyLibraryOptions.Strings(options, "SubtitleDownloadLanguages").SequenceEqual(new[] { "zh", "en" }), "下载语言没写回");
        }
        finally { (settings.CurrentPage as IShellContent)?.Release(); _root.Children.Remove(settings); ResizeInspect(1420, 980); }
    }

    private async Task ServerLibraryOperationsAsync()
    {
        using var fixture = CreateFixture(); await fixture.SelectAsync();
        var server = new LibrariesProbeServer(); fixture.Transport.Reply = server.ReplyAsync;
        var page = new ServerLibrariesPage(); _root.Children.Add(page);
        var vm = page.ViewModel; vm.Attach(fixture.Session);
        try
        {
            await vm.ReloadAsync(); await LayoutAsync(page);
            await vm.NewLibraryCommand.ExecuteAsync(null);
            await LayoutAsync(page);
            Get<ComboBox>(page, "LibraryContentType").SelectedIndex = 8;
            await UntilAsync(() => vm.ContentTypeIndex == 8 && !vm.Busy);
            Require(vm.Sections.SelectMany(section => section.Fields).Any(field => field.Key == "IntroDetection"), "混合媒体库选项没有重新载入");
            Get<TextBox>(page, "LibraryNameBox").Text = "离线混合库";
            var adding = (Task)Call(page, "ShowDirectoryAsync", null, false)!;
            await UntilAsync(() => Get<ContentDialog?>(page, "_dialog") is not null);
            var directory = Get<ContentDialog>(page, "_dialog"); await LayoutAsync(directory);
            var path = Descendants(directory).OfType<TextBox>().Single(control => AutomationProperties.GetAutomationId(control) == "LibraryDirectoryPath");
            path.Text = "/media/new";
            var password = Descendants(directory).OfType<PasswordBox>().Single(); password.Password = "fixture-directory-password";
            var primary = Descendants(directory).OfType<Button>().Single(control => control.Name == "PrimaryButton");
            ((IInvokeProvider)new ButtonAutomationPeer(primary).GetPattern(PatternInterface.Invoke)).Invoke();
            await adding;
            Require(vm.Paths.Count == 1 && !vm.Busy, "目录对话框确认未添加草稿：" + vm.NoticeMessage);
            Require(server.Folders.Count == 2, "新建草稿提前写了服务器");
            await SaveUsersFrameAsync(page, "server-libraries-new.png");
            await vm.SaveCommand.ExecuteAsync(null);
            Require(server.Creates == 1 && !vm.Editing && server.Folders["new"]["CollectionType"]!.ToString() == "", "混合库创建失败：" + vm.NoticeMessage);
            Require(!fixture.Transport.Requests.Any(url => url.Contains("fixture-directory-password", StringComparison.Ordinal)), "目录凭据进入 URL");
            var added = vm.Libraries.Single(row => row.Id == "new");
            await vm.OpenAsync(added);
            var modifiedPath = (JsonObject)vm.Paths[0].Document.DeepClone(); modifiedPath["NetworkPath"] = "\\\\nas\\new";
            Require(await vm.SavePathAsync(modifiedPath, vm.Paths[0]), "现有目录修改失败");
            Require(await vm.SavePathAsync(new JsonObject { ["Path"] = "/media/extra" }, null), "现有库加目录失败");
            vm.UseConfirm((_, _, _) => Task.FromResult(false));
            var count = server.Writes.Count;
            await vm.RemovePathAsync(vm.Paths[1]);
            Require(server.Writes.Count == count && vm.Paths.Count == 2, "取消移除目录仍写了服务器");
            vm.UseConfirm((_, _, _) => Task.FromResult(true));
            await vm.RemovePathAsync(vm.Paths[1]);
            Require(vm.Paths.Count == 1, "移除目录未刷新");
            await vm.BackCommand.ExecuteAsync(null);
            await vm.ToggleScanCommand.ExecuteAsync(null); Require(vm.ScanRunning, "扫描所有库未启动");
            await vm.ToggleScanCommand.ExecuteAsync(null); Require(!vm.ScanRunning, "扫描任务未取消");
            await vm.ScanAsync(added, "all", true);
            Require(server.ScanUrl.Contains("ReplaceAllImages=true", StringComparison.Ordinal), "完整刷新图片选项未提交");
            vm.UseConfirm((_, _, _) => Task.FromResult(false));
            count = server.Writes.Count; await vm.RemoveAsync(added); Require(server.Writes.Count == count, "取消移除库仍写了服务器");
            vm.UseConfirm((_, _, _) => Task.FromResult(true));
            await vm.RemoveAsync(added); Require(!server.Folders.ContainsKey("new") && vm.Libraries.Count == 2, "媒体库移除未刷新列表");

            await vm.NewLibraryCommand.ExecuteAsync(null); vm.Name = "结果不明测试";
            await vm.SavePathAsync(new JsonObject { ["Path"] = "/media/uncertain" }, null);
            server.FailCreateAfterWrite = true;
            await vm.SaveCommand.ExecuteAsync(null);
            Require(vm.Editing && !vm.CanSave && server.Creates == 2, "创建结果不明时还允许重复提交");
            await vm.SaveCommand.ExecuteAsync(null); Require(server.Creates == 2, "创建失败重试重复建库");
            await vm.BackCommand.ExecuteAsync(null); Require(vm.Libraries.Any(row => row.Name == "结果不明测试"), "返回刷新不能确认创建结果");
        }
        finally { page.Release(); _root.Children.Remove(page); }
    }

    private async Task ServerLibraryAdvancedAsync()
    {
        using var fixture = CreateFixture(); await fixture.SelectAsync();
        var server = new LibrariesProbeServer(); fixture.Transport.Reply = server.ReplyAsync;
        var page = new ServerLibrariesPage(); _root.Children.Add(page);
        var vm = page.ViewModel; vm.Attach(fixture.Session);
        try
        {
            await vm.ReloadAsync(); await LayoutAsync(page);
            Get<NavigationView>(page, "LibraryTabs").SelectedItem = Get<NavigationViewItem>(page, "LibraryAdvancedTab");
            await UntilAsync(() => vm.CanSaveAdvanced);
            await LayoutAsync(page);
            Require(vm.Tab == "advanced" && vm.AdvancedSections.Count == 3, "高级页签未接线");
            var fields = vm.AdvancedSections.SelectMany(section => section.Fields).ToArray();
            Require(fields.Single(field => field.Key == "MetadataPath").Text == "/metadata", "默认元数据目录没有显示");
            fields.Single(field => field.Key == "UseFileCreationTimeForDateAdded").Checked = true;
            fields.Single(field => field.Key == "PreferredMetadataLanguage").SelectedIndex = 1;
            server.Configuration["ServerName"] = "其他客户端改名";
            server.FailMetadataOnce = true;
            await vm.SaveAdvancedCommand.ExecuteAsync(null);
            Require(vm.NoticeSeverity == InfoBarSeverity.Error && vm.HasChanges, "两份配置只写完一份却报告成功");
            Require(server.Configuration["ServerName"]!.ToString() == "其他客户端改名", "高级设置覆盖服务器改名");
            var serverWrites = server.Writes.Count(write => write.Path.EndsWith("System/Configuration", StringComparison.Ordinal));
            await vm.SaveAdvancedCommand.ExecuteAsync(null);
            Require(vm.NoticeSeverity == InfoBarSeverity.Success && EmbyLibraryOptions.Flag(server.Metadata, "UseFileCreationTimeForDateAdded"), "高级配置重试失败");
            Require(server.Writes.Count(write => write.Path.EndsWith("System/Configuration", StringComparison.Ordinal)) == serverWrites, "重试重复提交已成功的配置");
            await SaveUsersFrameAsync(page, "server-libraries-advanced.png");
        }
        finally { page.Release(); _root.Children.Remove(page); }
    }

    private async Task ServerLibraryIdentityAsync()
    {
        using var fixture = CreateFixture(); await fixture.SelectAsync();
        var server = new LibrariesProbeServer(); fixture.Transport.Reply = server.ReplyAsync;
        var vm = new ServerLibrariesViewModel(); vm.Attach(fixture.Session);
        try
        {
            await vm.ReloadAsync(); await vm.OpenAsync(vm.Libraries[0]); vm.Name = "不得写入";
            var pending = Pending<HttpResponseMessage>(); server.PendingFolders = pending;
            var saving = vm.SaveCommand.ExecuteAsync(null);
            await UntilAsync(() => server.PendingReads > 0);
            vm.Cancel(); pending.SetResult(server.List()); await saving;
            Require(server.Writes.Count == 0 && !vm.CanSave, "离页后仍保存了旧媒体库");
            server.PendingFolders = null; vm.Attach(fixture.Session); await vm.ReloadAsync();
            var oldRow = vm.Libraries[0];
            var confirm = Pending<bool>(); vm.UseConfirm((_, _, _) => confirm.Task);
            var removing = vm.RemoveAsync(vm.Libraries[0]);
            await fixture.SelectAsync("b.invalid"); confirm.SetResult(true); await removing;
            Require(server.Writes.Count == 0, "切服后仍执行旧媒体库确认");
            vm.Attach(fixture.Session); await vm.ReloadAsync();
            await vm.ScanAsync(oldRow, "scan", false);
            await vm.OpenAsync(oldRow);
            Require(server.Writes.Count == 0 && !vm.Editing, "已打开的旧菜单借新身份继续操作");
            vm.Attach(fixture.Session); server.Administrator = false; await vm.ReloadAsync();
            Require(!vm.CanUse && vm.Libraries.Count == 0, "普通账号仍显示管理能力");
        }
        finally { vm.Cancel(); }
    }

    private sealed class LibrariesProbeServer
    {
        internal Dictionary<string, JsonObject> Folders { get; } = new()
        {
            ["movies"] = Folder("movies", "电影", "movies"),
            ["tv"] = Folder("tv", "电视剧", "tvshows")
        };
        internal JsonObject Configuration { get; private set; } = Parse("""{"ServerName":"离线服务器","PreferredMetadataLanguage":"en","MetadataCountryCode":"US","MetadataPath":"","Plugin":42}""");
        internal JsonObject Metadata { get; private set; } = Parse("""{"UseFileCreationTimeForDateAdded":false,"Unknown":77}""");
        internal List<(string Path, JsonObject Body)> Writes { get; } = [];
        internal int Creates { get; private set; }
        internal bool Administrator { get; set; } = true;
        internal bool FailMetadataOnce { get; set; }
        internal bool FailCreateAfterWrite { get; set; }
        internal bool Running { get; set; }
        internal string ScanUrl { get; private set; } = "";
        internal TaskCompletionSource<HttpResponseMessage>? PendingFolders { get; set; }
        internal int PendingReads { get; private set; }
        internal HttpResponseMessage List() => Json(new { Items = Folders.Values.ToArray(), TotalRecordCount = Folders.Count });

        internal async Task<HttpResponseMessage> ReplyAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                if (path.EndsWith("VirtualFolders/Query", StringComparison.Ordinal))
                { if (PendingFolders is { } pending) { PendingReads++; return await pending.Task; } return List(); }
                if (path.EndsWith("Users/user", StringComparison.Ordinal)) return Json(new { Id = "user", Policy = new { IsAdministrator = Administrator } });
                if (path.EndsWith("System/Info", StringComparison.Ordinal)) return Json(new { ServerName = "离线服务器", Version = "4.10.0.40", OperatingSystem = "Windows", InternalMetadataPath = "/metadata" });
                if (path.EndsWith("Libraries/AvailableOptions", StringComparison.Ordinal)) return Json(Available());
                if (path.EndsWith("Localization/Cultures", StringComparison.Ordinal)) return Json(new[] { new { TwoLetterISOLanguageName = "zh", DisplayName = "中文" }, new { TwoLetterISOLanguageName = "en", DisplayName = "English" } });
                if (path.EndsWith("Localization/Countries", StringComparison.Ordinal)) return Json(new[] { new { TwoLetterISORegionName = "CN", DisplayName = "中国" }, new { TwoLetterISORegionName = "US", DisplayName = "美国" } });
                if (path.EndsWith("ScheduledTasks", StringComparison.Ordinal)) return Json(new[] { new { Id = "scan", Key = "RefreshLibrary", State = Running ? "Running" : "Idle", CurrentProgressPercentage = Running ? 42 : 0 } });
                if (path.EndsWith("System/Configuration", StringComparison.Ordinal)) return Json(Configuration);
                if (path.EndsWith("System/Configuration/metadata", StringComparison.Ordinal)) return Json(Metadata);
                if (path.EndsWith("Environment/Drives", StringComparison.Ordinal)) return Json(new[] { new { Name = "media", Path = "/media", Type = "Directory" } });
                if (path.EndsWith("Environment/ParentPath", StringComparison.Ordinal)) return new(HttpStatusCode.OK) { Content = new StringContent("/media") };
                if (path.EndsWith("/Items/movies", StringComparison.Ordinal)) return Json(new { Id = "movies", Name = "电影", Type = "CollectionFolder", IsFolder = true });
                return FakeTransport.Standard(request);
            }
            if (path.EndsWith("Environment/ValidatePath", StringComparison.Ordinal)) return new(HttpStatusCode.NoContent);
            if (path.EndsWith("Environment/DirectoryContents", StringComparison.Ordinal)) return Json(new[] { new { Name = "movies", Path = "/media/movies", Type = "Directory" } });
            var bodyText = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            var body = bodyText.Length == 0 ? new JsonObject() : Parse(bodyText);
            Writes.Add((path, body));
            var id = EmbyLibraryOptions.Text(body, "Id");
            if (path.EndsWith("Library/VirtualFolders", StringComparison.Ordinal))
            {
                Creates++;
                Folders["new"] = Folder("new", Query(request, "name") ?? "新建", Query(request, "collectionType") ?? "");
                Folders["new"]["LibraryOptions"] = body["LibraryOptions"]!.DeepClone();
                if (FailCreateAfterWrite) { FailCreateAfterWrite = false; return new(HttpStatusCode.InternalServerError); }
            }
            else if (path.EndsWith("VirtualFolders/LibraryOptions", StringComparison.Ordinal)) Folders[id]["LibraryOptions"] = body["LibraryOptions"]!.DeepClone();
            else if (path.EndsWith("VirtualFolders/Name", StringComparison.Ordinal)) Folders[id]["Name"] = body["NewName"]!.ToString();
            else if (path.EndsWith("VirtualFolders/Delete", StringComparison.Ordinal)) Folders.Remove(id);
            else if (path.EndsWith("VirtualFolders/Paths", StringComparison.Ordinal)) Folders[id]["LibraryOptions"]!["PathInfos"]!.AsArray().Add(body["PathInfo"]!.DeepClone());
            else if (path.EndsWith("VirtualFolders/Paths/Update", StringComparison.Ordinal))
            {
                var paths = Folders[id]["LibraryOptions"]!["PathInfos"]!.AsArray();
                var existing = paths.OfType<JsonObject>().First(item => item["Path"]!.ToString() == body["PathInfo"]!["Path"]!.ToString());
                paths[paths.IndexOf(existing)] = body["PathInfo"]!.DeepClone();
            }
            else if (path.EndsWith("VirtualFolders/Paths/Delete", StringComparison.Ordinal))
            {
                var paths = Folders[id]["LibraryOptions"]!["PathInfos"]!.AsArray();
                var existing = paths.OfType<JsonObject>().First(item => item["Path"]!.ToString() == body["Path"]!.ToString()); paths.Remove(existing);
            }
            else if (path.EndsWith("ScheduledTasks/Running/scan", StringComparison.Ordinal)) Running = true;
            else if (path.EndsWith("ScheduledTasks/Running/scan/Delete", StringComparison.Ordinal)) Running = false;
            else if (path.EndsWith("/Refresh", StringComparison.Ordinal)) ScanUrl = request.RequestUri.ToString();
            else if (path.EndsWith("System/Configuration", StringComparison.Ordinal)) Configuration = (JsonObject)body.DeepClone();
            else if (path.EndsWith("System/Configuration/metadata", StringComparison.Ordinal))
            {
                if (FailMetadataOnce) { FailMetadataOnce = false; return new(HttpStatusCode.InternalServerError); }
                Metadata = (JsonObject)body.DeepClone();
            }
            else throw new InvalidOperationException("未经核对的媒体库写入：" + path);
            return new(HttpStatusCode.NoContent);
        }

        private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
        private static JsonObject Options() => Parse("""{"EnableRealtimeMonitor":true,"MinResumePct":2,"MaxResumePct":90,"AutoGenerateChapters":true,"ThumbnailImagesIntervalSeconds":10,"PathInfos":[{"Path":"/media/movies"}],"Plugin":{"Keep":42},"DisabledSubtitleFetchers":[],"TypeOptions":[{"Type":"Movie","MetadataFetchers":["A","B"],"MetadataFetcherOrder":["A","B"],"ImageFetchers":["A"],"ImageOptions":[{"Type":"Backdrop","Limit":1,"MinWidth":0}]}]}""");
        private static JsonObject Folder(string id, string name, string type) => new() { ["ItemId"] = id, ["Id"] = "guid-" + id, ["Name"] = name, ["CollectionType"] = type, ["LibraryOptions"] = Options() };
        private static JsonObject Available()
        {
            var options = Parse("""{"MetadataReaders":[{"Name":"Nfo","Features":["Collections"]}],"MetadataSavers":[{"Name":"Nfo"}],"SubtitleFetchers":[{"Name":"OpenSubtitles","Features":["RequiredSetup"],"SetupUrl":"#!/configurationpage?name=opensubtitles"}],"LyricsFetchers":[{"Name":"Lyrics"}],"TypeOptions":[{"Type":"Movie","MetadataFetchers":[{"Name":"A","DefaultEnabled":true,"Features":["Collections","Adult"]},{"Name":"B","DefaultEnabled":true}],"ImageFetchers":[{"Name":"A","DefaultEnabled":true}],"SupportedImageTypes":["Primary","Backdrop","Logo"],"DefaultImageOptions":[{"Type":"Primary","Limit":1},{"Type":"Backdrop","Limit":1,"MinWidth":0}]}]}""");
            options["DefaultLibraryOptions"] = Options(); return options;
        }
    }
}
