using System.Net;
using System.Text.Json.Nodes;
using Momoka.Configuration;
using Momoka.Emby;
using Momoka.MoviePilot;
using Momoka.Shell.ViewModels;
using Momoka.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Momoka.Shell;

internal sealed partial class ShellNavigationProbe
{
    private async Task MoviePilotSubscriptionsAsync()
    {
        using var server = new SubscriptionProbeServer();
        server.Poster = await FixtureImageAsync();
        var (settings, service) = SubscriptionService(server);
        var view = new MoviePilotSubscriptionsView();
        var scroll = new ScrollViewer { Content = view, Padding = new Thickness(28) };
        var host = new Page { Content = scroll, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(ThemeHost.ToColor(ThemeHost.Current.Colors.Window)) };
        _root.Children.Add(host);
        MoviePilotSubscription? opened = null;

        // 「开应用／返回主页闪一下空板块」那一条（2026-10-08）：整块板块只在真有订阅时才立着 ——
        // 从前它认的是「MoviePilot 开着」，Attach 和 ReloadAsync 都在数据到达之前就把它摆上屏，屏上先出两块
        // 「暂无…」的空牌子、等异步请求回来才填卡片。
        //
        // 这一条用一块单独的视图模型量，而且**把那一趟列表请求扣在手里**（PendingList）：响应没放行之前，屏上
        // 该是什么、放开之后又该是什么，两头都读得到，不靠「哪一拍先跑」的运气。控件的 x:Bind 要等上场才求值，
        // 所以这里量的是 Visible 本身（绑定源），控件外那层 StackPanel 只是它的落点。
        await SubscriptionSectionVisibilityAsync(server);

        view.Attach(service, subscription => opened = subscription);
        try
        {
            var vm = view.ViewModel;
            await UntilAsync(() => vm.IsReady);
            Require(vm.Movies.Count == 3 && vm.Series.Count == 2, "订阅没有分成电影和电视剧：" + vm.NoticeMessage);
            Require(((StackPanel)view.Content).Visibility == Visibility.Visible, "订阅读回来了，板块却没有出现");
            foreach (var width in new[] { 1240, 760 })
            {
                ResizeInspect(width, 1000); await LayoutAsync(scroll);
                var covers = Descendants(view).OfType<Button>().Where(button => button.Name == "SubscriptionCover").ToArray();
                Require(covers.Length >= 2 && covers.All(button => button.Tag is MoviePilotSubscriptionCard), "模板封面没有绑定真实订阅或句柄");
                Require(Math.Abs(covers[0].ActualWidth - vm.Movies[0].CardWidth) < 1, "封面按钮未占满卡片宽度");
                await UntilAsync(() => vm.Movies[0].Poster is not null);
                InvokeSubscriptionButton(covers[0]);
                Require(opened?.Id == 1, "封面点击没有打开媒体详情");
                await SaveUsersFrameAsync(host, $"mp-subscriptions-home-{width}.png");
            }
            var poster = Descendants(view).OfType<MoviePilotSubscriptionPoster>().First();
            var more = Get<Button>(poster, "SubscriptionMore");
            poster.SetHovered(false);
            Require(more.Visibility == Visibility.Collapsed, "未悬停封面时订阅管理按钮仍然显示");
            poster.SetHovered(true); await LayoutAsync(poster);
            var morePosition = more.TransformToVisual(poster).TransformPoint(default);
            Require(more.Visibility == Visibility.Visible && more.ActualWidth == 32 && more.ActualHeight == 32
                && Math.Abs(poster.ActualWidth - morePosition.X - more.ActualWidth - 7) < 1
                && Math.Abs(poster.ActualHeight - morePosition.Y - more.ActualHeight - 5) < 1, "更多按钮没有与媒体库封面右下角对齐");
            var seriesPoster = Descendants(view).OfType<MoviePilotSubscriptionPoster>().First(item => item.Card?.Subscription.IsSeries == true);
            seriesPoster.SetHovered(true); await LayoutAsync(seriesPoster);
            var progress = Get<Border>(seriesPoster, "SubscriptionProgress");
            var progressPosition = progress.TransformToVisual(seriesPoster).TransformPoint(default);
            var seriesMore = Get<Button>(seriesPoster, "SubscriptionMore");
            Require(progress.Visibility == Visibility.Visible && progressPosition.X >= 0
                && progressPosition.Y >= seriesPoster.ActualHeight - progress.ActualHeight - 9
                && progressPosition.X + progress.ActualWidth < seriesMore.TransformToVisual(seriesPoster).TransformPoint(default).X,
                "订阅进度没有放在封面内部左下角，或与更多按钮重叠");
            Require(Descendants(progress).OfType<TextBlock>().Single().Text == seriesPoster.Card!.ProgressCount,
                "封面集数丢失或仍带有订阅进度标签");
            Require(!Descendants(view).OfType<TextBlock>().Any(text => text.Text is "订阅中" or "入库情况见文件统计" or "订阅进度"), "旧状态标签或说明仍然显示");
            Get<Button>(view, "RefreshSubscriptions").Focus(FocusState.Programmatic);
            poster.SetHovered(false); await Task.Delay(250);
            poster.SetHovered(true); await Task.Delay(80);
            if (HomeMotion.AnimationsEnabled) Require(poster.Zoom > 1 && poster.Zoom <= 1.045, "悬停未产生图片推近动画");
            await SaveUsersFrameAsync(host, "mp-subscription-hover.png");
            poster.SetHovered(false); await Task.Delay(250);
            Require(Math.Abs(poster.Zoom - 1) < 0.001 && more.Visibility == Visibility.Collapsed, "鼠标离开后缩放没有复原或管理按钮没有隐藏");
            seriesPoster.SetHovered(false);
            var first = vm.Movies[0];
            await vm.ReloadAsync();
            Require(ReferenceEquals(first, vm.Movies[0]), "刷新重建卡片而丢失行状态");
            vm.UseConfirm((_, _, _) => Task.FromResult(false));
            await vm.ManageAsync(first, MoviePilotSubscriptionAction.Delete);
            Require(server.Writes.Count == 0 && vm.Movies.Count == 3, "取消确认仍然取消订阅");
            vm.UseConfirm((_, _, _) => Task.FromResult(true));
            await vm.ManageAsync(first, MoviePilotSubscriptionAction.Pause);
            Require(first.Subscription.Paused, "暂停后状态没有刷新");
            await vm.ManageAsync(first, MoviePilotSubscriptionAction.Resume);
            Require(!first.Subscription.Paused, "暂停后不能恢复");
            await vm.ManageAsync(first, MoviePilotSubscriptionAction.Search);
            await vm.ManageAsync(first, MoviePilotSubscriptionAction.Reset);
            Require(server.Writes.Count == 4 && server.Writes.Any(write => write == "POST /api/v1/subscribe/search/1"), "菜单动作没有对应实际请求");
            await vm.ManageAsync(first, MoviePilotSubscriptionAction.Delete);
            Require(vm.Movies.Count == 2 && !vm.Owns(first), "取消订阅后旧卡片仍可操作");
            settings.MoviePilot.Enabled = false;
            await vm.ReloadAsync();
            Require(vm.Visible == Visibility.Collapsed && vm.Movies.Count == 0, "关闭集成后仍显示旧订阅");
        }
        finally { view.Release(); _root.Children.Remove(host); ResizeInspect(1420, 980); }
    }

    private async Task MoviePilotSubscriptionDetailAsync()
    {
        using var server = new SubscriptionProbeServer();
        server.Poster = await FixtureImageAsync();
        var (_, service) = SubscriptionService(server);
        var subscription = (await service.SubscriptionsAsync(CancellationToken.None)).First(item => item.IsSeries);
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        fixture.Transport.Reply = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.Contains("/Episodes", StringComparison.Ordinal)
            ? Json(new { Items = new[] { new EmbyItem { Id = "play-1", Type = EmbyItemType.Episode, ParentIndexNumber = 1, IndexNumber = 1, SeriesId = "linked-show", Path = "/library/01.mkv" } } })
            : Json(new { Items = new[] { new EmbyItem { Id = "linked-show", Type = EmbyItemType.Series, ProviderIds = new() { ["Tmdb"] = "4" } } } }));
        var played = new List<MoviePilotPlaybackTarget>();
        var page = new MoviePilotSubscriptionPage();
        _root.Children.Add(page);
        page.Attach(new(service, subscription, fixture.Session.Capture(), target => { played.Add(target); return Task.CompletedTask; }));
        try
        {
            var vm = page.ViewModel;
            await UntilAsync(() => vm.IsReady);
            Require(vm.Episodes.Count == 4 && vm.Episodes.Count(episode => episode.InLibrary) == 1, "入库和下载被混为一谈：" + vm.NoticeMessage);
            Require(vm.Episodes[1].Status == "已下载 · 待入库", "待入库集状态丢失");
            Require(vm.Episodes[0].CanPlay && !vm.Episodes[1].CanPlay, "已入库没有播放入口或缺集允许播放");
            foreach (var width in new[] { 1240, 760 })
            {
                ResizeInspect(width, 1000); await LayoutAsync(page);
                await UntilAsync(() => vm.Episodes[0].Artwork is not null);
                var picture = Descendants(page).OfType<Border>().First(border => border.Name == "EpisodeArtwork");
                var play = Descendants(page).OfType<Button>().First(button => button.Name == "PlaySubscriptionEpisode" && button.Visibility == Visibility.Visible);
                Require(picture.ActualWidth > picture.ActualHeight && picture.ActualHeight > 100, "集封面没有按横版显示");
                Require(play.Content is FontIcon && play.TransformToVisual(page).TransformPoint(default).X
                    > picture.TransformToVisual(page).TransformPoint(default).X + picture.ActualWidth, "播放图标不在封面和剧情右侧");
                Require(!Descendants(page).OfType<TextBlock>().Any(text => text.Text.StartsWith("入库：", StringComparison.Ordinal)
                    || text.Text.StartsWith("下载：", StringComparison.Ordinal)), "剧情下方仍然显示文件路径");
                await SaveUsersFrameAsync(page, $"mp-subscription-detail-{width}.png");
            }
            var playButton = Descendants(page).OfType<Button>().First(button => button.Name == "PlaySubscriptionEpisode" && button.IsEnabled);
            InvokeSubscriptionButton(playButton);
            await UntilAsync(() => played.Count == 1);
            Require(played[0].Item.Id == "play-1" && played[0].Parent?.Id == "linked-show", "播放按钮没有送到正确的 Emby 剧集");
            var filter = Get<ComboBox>(page, "EpisodeFilter");
            filter.SelectedIndex = 1;
            Require(vm.Episodes.Count == 1, "已入库筛选没有驱动视图模型");
            filter.SelectedIndex = 2;
            Require(vm.Episodes.Count == 3, "缺集筛选错误");
            filter.SelectedIndex = 0;
            var editor = new MoviePilotSubscriptionEditor();
            editor.Set(subscription);
            _root.Children.Remove(page); _root.Children.Add(editor);
            try
            {
                await LayoutAsync(editor);
                Get<TextBox>(editor, "IncludeInput").Text = "WEB-DL";
                Get<NumberBox>(editor, "TotalInput").Value = 6;
                await SaveUsersFrameAsync(editor, "mp-subscription-editor.png");
                await vm.EditAsync(vm.Selected!, _ => Task.FromResult<MoviePilotSubscriptionEdit?>(editor.Read()));
                Require(server.Items[4]["include"]!.ToString() == "WEB-DL" && server.Items[4]["total_episode"]!.GetValue<int>() == 6, "编辑未保存输入字段");
                Require(server.Items[4]["sites"]![0]!.GetValue<int>() == 8, "编辑覆盖了未改动的站点");
                Require(vm.Episodes.Count == 6, "保存总集数后未刷新缺集");
            }
            finally { _root.Children.Remove(editor); _root.Children.Add(page); }
            vm.UseConfirm((_, _, _) => Task.FromResult(true));
            var oldEpisode = vm.Episodes[0];
            fixture.Session.SignOut();
            await oldEpisode.PlayCommand.ExecuteAsync(null);
            Require(played.Count == 1, "Emby 退出后旧播放按钮仍可调用");
            await vm.ManageAsync(vm.Selected!, MoviePilotSubscriptionAction.Delete);
            Require(vm.Deleted && !vm.CanManage, "取消订阅后详情仍允许写入");
        }
        finally { page.Release(); _root.Children.Remove(page); ResizeInspect(1420, 980); }
    }

    private async Task MoviePilotSubscriptionIdentityAsync()
    {
        using var server = new SubscriptionProbeServer();
        var (settings, service) = SubscriptionService(server);
        var vm = new MoviePilotSubscriptionsViewModel();
        vm.Attach(service);
        await vm.ReloadAsync();
        var card = vm.Movies[0];
        var confirm = Pending<bool>();
        vm.UseConfirm((_, _, _) => confirm.Task);
        var changing = vm.ManageAsync(card, MoviePilotSubscriptionAction.Delete);
        settings.MoviePilot.Username = "another-user";
        confirm.SetResult(true);
        await changing;
        Require(server.Writes.Count == 0, "切账号后旧确认仍发送写请求");
        await vm.ReloadAsync();
        Require(!vm.Owns(card), "切账号重载后沿用旧卡片");
        var late = Pending<HttpResponseMessage>();
        server.PendingList = late;
        var loading = vm.ReloadAsync();
        vm.Cancel();
        late.SetResult(Json(new { success = true, data = server.Items.Values.ToArray() }));
        await loading;
        Require(vm.Movies.Count == 0 && !vm.CanManage, "离页后迟到请求复活列表");
    }

    /// <summary>
    /// 订阅板块的显隐判据（2026-10-08「开应用／返回主页会闪一下空板块」）：把列表那一趟扣在手里，读「数据还没
    /// 回来」和「数据回来了」两头。判据是 <see cref="MoviePilotSubscriptionsViewModel.Visible"/>，不掺时序：
    /// 假传输同步完成与否都不影响结论。
    /// </summary>
    private async Task SubscriptionSectionVisibilityAsync(SubscriptionProbeServer server)
    {
        var (_, service) = SubscriptionService(server);
        var vm = new MoviePilotSubscriptionsViewModel();

        // 1. MoviePilot 开着、卡片一张都没有：整块收着。从前这里就是 Visible —— Attach 一上来凭 Enabled 把
        //    空板块摆上屏，正是开应用／返回主页看到的那一帧。
        vm.Attach(service);
        Require(vm.Visible == Visibility.Collapsed, "一条订阅也没有，空板块却要立起来（开应用／返回主页会闪一下）");

        // 2. **列表还扣在手里**（真实的开应用那一拍）：整块仍须收着，等数据真回来才和内容一起出现。
        var gate = Pending<HttpResponseMessage>();
        server.PendingList = gate;
        var loading = vm.ReloadAsync();
        Require(vm.Visible == Visibility.Collapsed, "列表还扣在手里，板块就已经立起来了");
        gate.SetResult(Json(new { success = true, data = server.Items.Values.ToArray() }));
        await loading;
        Require(vm.Movies.Count == 3 && vm.Visible == Visibility.Visible, "订阅读回来了，板块却没有立起来");

        // 3. 一条订阅都没有的账号：读完了也不该出现这块（从前只看「开着 MoviePilot」就会摆出来）。
        var empty = Pending<HttpResponseMessage>();
        server.PendingList = empty;
        var reloading = vm.ReloadAsync();
        empty.SetResult(Json(new { success = true, data = Array.Empty<object>() }));
        await reloading;
        Require(vm.Movies.Count == 0 && vm.Series.Count == 0 && vm.Visible == Visibility.Collapsed,
            "一条订阅都没有，空板块还是立着");

        server.PendingList = null;
        vm.Cancel();
    }

    private static void InvokeSubscriptionButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private async Task SeasonTrailingPlayAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var image = await FixtureImageAsync();
        var season = Season("a");
        season.Name = "第一季";
        season.ImageTags["Primary"] = "fixture";
        var episode = Episode("a");
        episode.ImageTags["Primary"] = "fixture";
        episode.ParentIndexNumber = 1;
        episode.Name = "新的旅程";
        episode.Overview = "一次突如其来的重逢，让熟悉的生活有了新的方向。";
        fixture.Transport.Reply = (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/Images/", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) });
            if (path.EndsWith("/Items/a", StringComparison.Ordinal)) return Task.FromResult(Json(season));
            if (path.EndsWith("/Episodes", StringComparison.Ordinal)) return Task.FromResult(Json(new { Items = new[] { episode } }));
            return Task.FromResult(FakeTransport.Standard(request));
        };
        var played = new List<EmbyItem>();
        IReadOnlyList<EmbyItem>? siblings = null;
        fixture.Actions.CapturePlay = (item, episodes) => { played.Add(item); siblings = episodes; };
        var frame = new Frame();
        _root.Children.Add(frame);
        frame.Navigate(typeof(DetailPage), DetailRequest.For(fixture.Services, season));
        try
        {
            var page = (DetailPage)frame.Content;
            await UntilAsync(() => page.IsReady);
            foreach (var width in new[] { 1240, 760 })
            {
                ResizeInspect(width, 1000); await LayoutAsync(page);
                var list = Get<ItemsRepeater>(page, "EpisodeList");
                var container = (FrameworkElement)list.GetOrCreateElement(0);
                container.StartBringIntoView();
                await LayoutAsync(page);
                var row = Descendants(container).OfType<EpisodeRow>().Single();
                var play = Get<Button>(row, "TrailingPlay");
                var picture = Get<Border>(row, "Art");
                Require(row.ShowTrailingPlay && play.Visibility == Visibility.Visible && play.Content is FontIcon, "季页面没有显示右侧播放图标");
                Require(play.TransformToVisual(row).TransformPoint(default).X >= picture.ActualWidth + 10, "季页面播放图标位置错误");
                await UntilAsync(() => row.Card?.Poster is not null);
                await SaveUsersFrameAsync(page, $"season-trailing-play-{width}.png");
                var opened = fixture.Actions.Opened;
                InvokeSubscriptionButton(play);
                Require(played.LastOrDefault()?.Id == episode.Id && siblings?.Any(item => item.Id == episode.Id) == true, "季页面播放图标没有携带正确的集和剧集列表");
                Require(fixture.Actions.Opened == opened, "播放图标同时触发行打开");
            }
        }
        finally { (frame.Content as IShellContent)?.Release(); _root.Children.Remove(frame); ResizeInspect(1420, 980); }
    }

    private static (AppSettings Settings, MoviePilotService Service) SubscriptionService(SubscriptionProbeServer server)
    {
        var settings = new AppSettings();
        settings.MoviePilot.Enabled = true; settings.MoviePilot.Url = "https://moviepilot.invalid:3001";
        settings.MoviePilot.Username = "fixture";
        var credentials = new MoviePilotCredentials(PassthroughSecretProtector.Instance);
        credentials.SetPassword(settings.MoviePilot, "fixture-password");
        return (settings, new MoviePilotService(new MoviePilotClient(server), credentials, settings));
    }

    private sealed class SubscriptionProbeServer : HttpMessageHandler
    {
        internal Dictionary<int, JsonObject> Items { get; } = Enumerable.Range(1, 5).ToDictionary(id => id, id => new JsonObject
        {
            ["id"] = id,
            ["name"] = id < 4 ? new[] { "星际远航", "夏日来信", "海岸线" }[id - 1] : "漫长的旅途",
            ["year"] = "2026",
            ["type"] = id < 4 ? "电影" : "电视剧",
            ["season"] = id < 4 ? null : id - 3,
            ["media_source"] = "themoviedb",
            ["media_id"] = id.ToString(),
            ["state"] = "R",
            ["poster"] = "https://poster.invalid/" + id + ".png",
            ["total_episode"] = id < 4 ? 0 : 4,
            ["lack_episode"] = 2,
            ["start_episode"] = 1,
            ["description"] = "一段关于相遇、离别与重逢的故事。订阅详情可查看媒体简介、管理订阅，并核对每集的入库情况。",
            ["sites"] = new JsonArray(8),
            ["include"] = ""
        });
        internal List<string> Writes { get; } = [];
        internal byte[] Poster { get; set; } = [];
        internal TaskCompletionSource<HttpResponseMessage>? PendingList { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.RequestUri!.Host == "moviepilot.invalid", "订阅探针拒绝真实网络");
            var path = request.RequestUri.AbsolutePath;
            if (path == "/api/v1/system/cache/image") return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Poster) };
            if (path.EndsWith("login/access-token", StringComparison.Ordinal)) return Json(new { access_token = "fixture-token" });
            if (request.Method != HttpMethod.Get)
            {
                Writes.Add(request.Method.Method + " " + path);
                if (path == "/api/v1/subscribe/" && request.Method == HttpMethod.Put)
                {
                    var patch = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                    var id = patch["id"]!.GetValue<int>();
                    foreach (var pair in patch.Where(pair => pair.Key != "id")) Items[id][pair.Key] = pair.Value?.DeepClone();
                }
                else if (request.Method == HttpMethod.Delete) Items.Remove(int.Parse(path.Split('/')[^1]));
                else if (path.Contains("/status/", StringComparison.Ordinal)) Items[int.Parse(path.Split('/')[^1])]["state"] = Query(request, "state");
                return Json(new { success = true, data = (object?)null });
            }
            if (path == "/api/v1/subscribe/") return PendingList is { } pending ? await pending.Task : Json(new { success = true, data = Items.Values.ToArray() });
            var itemId = int.Parse(path.Split('/')[^1]);
            if (!Items.TryGetValue(itemId, out var item)) return new(HttpStatusCode.NotFound);
            if (path.Contains("/files/", StringComparison.Ordinal)) return Json(new
            {
                success = true,
                data = new
                {
                    subscribe = item,
                    episodes = new Dictionary<string, object>
                    {
                        ["1"] = new { title = "新的开始", description = "第一集", backdrop = "https://poster.invalid/episode-1.png", library = new[] { new { file_path = "/媒体库/漫长的旅途/S01E01.mkv" } }, download = Array.Empty<object>() },
                        ["2"] = new { title = "重逢", description = "第二集", backdrop = "https://poster.invalid/episode-2.png", library = Array.Empty<object>(), download = new[] { new { file_path = "/下载/S01E02.mkv" } } },
                        ["3"] = new { title = "远方", description = "第三集", backdrop = "https://poster.invalid/episode-3.png", library = Array.Empty<object>(), download = Array.Empty<object>() }
                    }
                }
            });
            return Json(new { success = true, data = item });
        }
    }
}
