using System.Net;
using Momoka.Emby;
using Momoka.Shell.ViewModels;
using Momoka.Shell.Views;
using Microsoft.UI.Xaml;

namespace Momoka.Shell;

internal sealed partial class ShellNavigationProbe
{
    private async Task ResumeQueryAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        fixture.Transport.Reply = (request, _) => Query(request, "StartIndex") is { } offset
            ? Task.FromResult(Items(20, 100, "page" + offset)) : Task.FromResult(FakeTransport.Standard(request));
        using var vm = fixture.Library();
        await vm.ReloadAsync();
        vm.Cancel();
        vm.Resume();
        await UntilAsync(() => !vm.Busy);
        await vm.LoadUntilAsync(25);
        Require(vm.Cards.Count == 40 && vm.Cards[20].Item.Id.StartsWith("page20", StringComparison.Ordinal),
            "切回 Emby 必须恢复查询并接收下一页");
    }

    private async Task SeasonRefreshRaceAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        using var vm = fixture.Detail();
        await vm.ReloadAsync();
        var write = Pending<HttpResponseMessage>();
        var season = Pending<HttpResponseMessage>();
        var enteredWrite = Pending<bool>();
        var enteredSeason = Pending<bool>();
        fixture.Transport.Reply = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("PlayedItems", StringComparison.Ordinal))
            {
                enteredWrite.TrySetResult(true);
                return write.Task;
            }
            if (Query(request, "SeasonId") == "b")
            {
                enteredSeason.TrySetResult(true);
                return season.Task;
            }
            return Task.FromResult(FakeTransport.Standard(request));
        };
        var saving = vm.ToggleWatchedCommand.ExecuteAsync(null);
        await enteredWrite.Task;
        vm.SelectedSeason = vm.Seasons.Last();
        await enteredSeason.Task;
        try
        {
            write.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            await saving;
            Require(vm.IsReady && !vm.Busy && vm.Episodes.Count > 0 && vm.PlayTarget is not null,
                "整页刷新被旧切季标志取消，留下空列表或空播放目标");
        }
        finally
        {
            season.TrySetResult(Json(new { Items = new[] { Episode("b") } }));
            await SettleAsync();
        }
    }

    private async Task NavigationRestoreRaceAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var request = LibraryRequest.Search(fixture.Services, "old");
        request.LoadedCount = 60;
        request.ScrollOffset = 300;
        var page = new LibraryPage();
        page.ViewModel.Attach(request, fixture.Actions, fixture.SettingsService, fixture.Session, fixture.Images, fixture.Capabilities);
        Field(page, "_released", false);
        Field(page, "_navigation", 1);
        var reply = Pending<HttpResponseMessage>();
        var entered = Pending<bool>();
        var newRequests = 0;
        fixture.Transport.Reply = (message, _) =>
        {
            if (Query(message, "SearchTerm") == "old")
            {
                entered.TrySetResult(true);
                return reply.Task;
            }
            newRequests++;
            return Task.FromResult(Items(20, 200, "new"));
        };
        try
        {
            var restoring = (Task)Call(page, "LoadNavigationAsync", request, 1)!;
            await entered.Task;
            await page.ViewModel.SearchAsync("new");
            reply.SetResult(Items(20, 200, "old"));
            await restoring;
            Require(newRequests == 1 && page.ViewModel.Cards.Count == 20,
                "旧导航恢复给新搜索补页或恢复了旧位置");
        }
        finally
        {
            reply.TrySetResult(Items(20, 200));
            page.Release();
            page.ViewModel.Dispose();
        }
    }

    private async Task PagingFailureAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var count = 0;
        fixture.Transport.Reply = (request, _) => Query(request, "StartIndex") is not { } offset ? Task.FromResult(FakeTransport.Standard(request))
            : Task.FromResult(offset == "0" ? Items(20, 200) : Fail());
        HttpResponseMessage Fail() { count++; return new HttpResponseMessage(HttpStatusCode.InternalServerError); }
        using var vm = fixture.Library();
        await vm.ReloadAsync();
        await vm.LoadUntilAsync(120);
        Require(count == 1 && vm.Cards.Count == 20 && !vm.Busy, "失败分页必须立即结束且保留已读卡片");
    }

    private async Task PagingCancelAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var entered = Pending<bool>();
        var reply = Pending<HttpResponseMessage>();
        var count = 0;
        fixture.Transport.Reply = (request, _) =>
        {
            if (Query(request, "StartIndex") is not { } offset) return Task.FromResult(FakeTransport.Standard(request));
            if (offset == "0") return Task.FromResult(Items(20, 200));
            count++;
            entered.TrySetResult(true);
            return reply.Task;
        };
        using var vm = fixture.Library();
        await vm.ReloadAsync();
        var pending = vm.LoadUntilAsync(120);
        await entered.Task;
        vm.Cancel();
        reply.SetResult(Items(20, 200, "late"));
        await pending;
        await SettleAsync();
        Require(count == 1 && vm.Cards.Count == 20 && !vm.Busy, "离页后补页不能重新启动");
    }

    private async Task QueryFailureAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        fixture.Transport.Reply = (request, _) => Query(request, "SearchTerm") is not { } term ? Task.FromResult(FakeTransport.Standard(request))
            : Task.FromResult(term == "first" ? Items(20, 200, "first") : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var vm = fixture.Library();
        await vm.ReloadAsync();
        await vm.SearchAsync("second");
        var count = fixture.Transport.Requests.Count;
        vm.LoadMore();
        await SettleAsync();
        Require(count == fixture.Transport.Requests.Count && vm.Cards.All(card => card.Item.Id.StartsWith("first", StringComparison.Ordinal)),
            "新查询失败后不允许旧数量作为新偏移");
    }

    private async Task SearchStateAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var calls = 0;
        fixture.Transport.Reply = (request, _) => Query(request, "SearchTerm") is null ? Task.FromResult(FakeTransport.Standard(request))
            : Task.FromResult(++calls == 1 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Items(1, 1));
        var request = LibraryRequest.Search(fixture.Services);
        using var vm = new LibraryViewModel();
        vm.Attach(request, fixture.Actions, fixture.SettingsService, fixture.Session, fixture.Images, fixture.Capabilities);
        await vm.SearchAsync("submitted");
        await vm.SearchAsync("submitted");
        Require(calls == 2 && vm.Cards.Count == 1 && request.SearchTerm == "submitted", "同词必须可重试且原导航载荷保存新词");
        using var returned = new LibraryViewModel();
        returned.Attach(request, fixture.Actions, fixture.SettingsService, fixture.Session, fixture.Images, fixture.Capabilities);
        Require(returned.SearchText == "submitted" && returned.Heading.Contains("submitted", StringComparison.Ordinal), "返回重建丢失搜索词");
    }

    private async Task PlaybackBusyAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        using var vm = fixture.Library();
        await vm.ReloadAsync();
        var reply = Pending<HttpResponseMessage>();
        fixture.Transport.Reply = (request, _) => Query(request, "SearchTerm") is null ? Task.FromResult(FakeTransport.Standard(request)) : reply.Task;
        var loading = vm.SearchAsync("second");
        try
        {
            Require(vm.Busy && !vm.PlayAllCommand.CanExecute(null) && !vm.PlayRandomCommand.CanExecute(null), "新查询中旧卡片不能组成播放列表");
            await vm.PlayAllAsync();
            Require(fixture.Actions.Plays == 0, "播放入口未守忙碌状态");
        }
        finally { reply.TrySetResult(Items(1, 1)); await loading; }
    }

    private async Task JumpCancelAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        using var vm = fixture.Library();
        await vm.ReloadAsync();
        var reply = Pending<HttpResponseMessage>();
        var entered = Pending<bool>();
        var counts = 0;
        fixture.Transport.Reply = (request, _) =>
        {
            if (Query(request, "Limit") != "0") return Task.FromResult(FakeTransport.Standard(request));
            counts++;
            entered.TrySetResult(true);
            return reply.Task;
        };
        var jump = vm.JumpToAsync("Z");
        await entered.Task;
        await vm.SearchAsync("new");
        reply.SetResult(Items(0, 100));
        Require(await jump is null && counts == 1, "已过期字母跳转不允许发第二次计数或补页");
    }

    private async Task SeasonCancelAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        using var vm = fixture.Detail();
        await vm.ReloadAsync();
        var first = vm.Seasons.First();
        var second = vm.Seasons.Last();
        var reply = Pending<HttpResponseMessage>();
        var entered = Pending<bool>();
        fixture.Transport.Reply = (request, _) =>
        {
            if (Query(request, "SeasonId") != second.Id) return Task.FromResult(FakeTransport.Standard(request));
            entered.TrySetResult(true);
            return reply.Task;
        };
        vm.SelectedSeason = second;
        await entered.Task;
        vm.SelectedSeason = first;
        reply.SetResult(Json(new { Items = new[] { Episode(second.Id) } }));
        await SettleAsync();
        Require(!vm.Busy && vm.SelectedSeason == first && vm.Episodes.All(item => item.SeasonId == first.Id)
            && vm.PlayTarget?.SeasonId == first.Id, "旧季请求覆盖了重新选中的季");
    }

    private async Task SeasonFailureAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        using var vm = fixture.Detail();
        await vm.ReloadAsync();
        var first = vm.SelectedSeason;
        fixture.Transport.Reply = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/Items/ep-b", StringComparison.Ordinal)
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)) : Task.FromResult(FakeTransport.Standard(request));
        vm.SelectedSeason = vm.Seasons.Last();
        await UntilAsync(() => !vm.Busy);
        Require(vm.SelectedSeason == first && vm.Episodes.All(item => item.SeasonId == first?.Id)
            && vm.PlayTarget?.SeasonId == first?.Id, "新季目标失败不能留下混合状态");
    }

    private async Task EmptySeasonAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        using var vm = fixture.Detail();
        await vm.ReloadAsync();
        fixture.Transport.Reply = (request, _) => Query(request, "SeasonId") == "b"
            ? Task.FromResult(Json(new { Items = Array.Empty<EmbyItem>() })) : Task.FromResult(FakeTransport.Standard(request));
        vm.SelectedSeason = vm.Seasons.Last();
        await UntilAsync(() => !vm.Busy);
        Require(vm.Episodes.Count == 0 && vm.EpisodeVisibility == Visibility.Visible && vm.SeasonVisibility == Visibility.Visible
            && vm.PlayTarget is null, "空季必须保留选择器且清除播放目标");
    }

    private async Task DetailWriteAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        using var vm = fixture.Detail();
        await vm.ReloadAsync();
        var reply = Pending<HttpResponseMessage>();
        var entered = Pending<bool>();
        fixture.Transport.Reply = (request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.Contains("PlayedItems", StringComparison.Ordinal)) return Task.FromResult(FakeTransport.Standard(request));
            entered.TrySetResult(true);
            return reply.Task;
        };
        var writing = vm.ToggleWatchedCommand.ExecuteAsync(null);
        await entered.Task;
        vm.Cancel();
        await fixture.SelectAsync("b.invalid");
        var count = fixture.Transport.Requests.Count;
        reply.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        await writing;
        Require(fixture.Transport.Requests.Count == count, "旧写入完成后重新读取了详情");
    }

    private async Task HiddenHomeAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        fixture.Settings.Ui.HomeRows = [new() { Key = HomeLayout.Resume, Visible = false }, new() { Key = HomeLayout.NextUp, Visible = false }];
        using var vm = new HomeViewModel();
        vm.Attach(fixture.Actions, [], fixture.SettingsService, fixture.Session, fixture.Images);
        var count = fixture.Transport.Requests.Count;
        await vm.ReloadAsync();
        Require(count == fixture.Transport.Requests.Count && !vm.NoticeOpen, "隐藏行和关闭轮播不应联网或报错");
    }

    private static Task NotificationEmptyAsync()
    {
        using var vm = new NotificationsViewModel();
        var changed = false;
        vm.PropertyChanged += (_, args) => changed |= args.PropertyName == nameof(vm.EmptyVisibility);
        vm.ShowEmpty = true;
        Require(changed && vm.EmptyVisibility == Visibility.Visible, "空态派生属性漏通知");
        return Task.CompletedTask;
    }

    private async Task NotificationCancelAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var reply = Pending<HttpResponseMessage>();
        var entered = Pending<bool>();
        fixture.Transport.Reply = (_, _) => { entered.TrySetResult(true); return reply.Task; };
        using var vm = new NotificationsViewModel();
        vm.Attach(fixture.Session, fixture.SettingsService);
        var shown = 0;
        vm.ShowEditor = _ => { shown++; return Task.CompletedTask; };
        var task = vm.AddNotificationCommand.ExecuteAsync(null);
        await entered.Task;
        vm.Cancel();
        reply.SetResult(Json(new[] { new { Id = "fake", Name = "fake" } }));
        await task;
        Require(shown == 0 && !vm.NoticeOpen, "离页后弹出通知编辑器或写入旧错误");
    }

    private async Task StaleCardAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var card = new CardItem(new EmbyItem { Id = "same", Name = "旧身份", Type = EmbyItemType.Movie }, fixture.Images, 170);
        await fixture.SelectAsync("b.invalid");
        var count = fixture.Transport.Requests.Count;
        ItemCommands.ToggleWatched(fixture.Session, fixture.Actions, card);
        ItemCommands.ToggleFavorite(fixture.Session, fixture.Actions, card);
        await SettleAsync();
        Require(count == fixture.Transport.Requests.Count, "旧卡片向新身份发送了命令");
    }
}
