using EmbyNian.Emby;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell;

internal sealed partial class ShellNavigationProbe
{
    private async Task ServerDashboardAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        fixture.Transport.Reply = (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/System/Info", StringComparison.Ordinal))
                return Task.FromResult(Json(new EmbySystemInfo
                {
                    ServerName = "家庭影音服务器",
                    Version = "4.9.1.0",
                    OperatingSystemDisplayName = "Linux",
                    LocalAddress = "http://emby.local:8096",
                    WanAddress = "https://media.example.com"
                }));
            if (path.EndsWith("/Sessions", StringComparison.Ordinal))
                return Task.FromResult(Json(new[]
                {
                    new EmbyDashboardSession
                    {
                        DeviceName = "客厅电视", Client = "Emby for Android TV", ApplicationVersion = "2.1.0", UserName = "家庭账号",
                        LastActivityDate = DateTimeOffset.Now,
                        NowPlayingItem = new EmbyItem { Name = "星际探索", RunTimeTicks = TimeSpan.FromHours(2).Ticks },
                        PlayState = new() { PositionTicks = TimeSpan.FromMinutes(48).Ticks, PlayMethod = "DirectPlay" }
                    },
                    new EmbyDashboardSession
                    {
                        DeviceName = "书房电脑", Client = "EmbyNian", UserName = "家庭账号", LastActivityDate = DateTimeOffset.Now
                    }
                }));
            if (path.EndsWith("/ActivityLog/Entries", StringComparison.Ordinal))
                return Task.FromResult(Json(new EmbyActivityResult
                {
                    TotalRecordCount = 2,
                    Items =
                    [
                        new() { Name = "家庭账号开始播放 星际探索", ShortOverview = "客厅电视 · 直接播放", Date = DateTimeOffset.Now },
                        new() { Name = "媒体库扫描完成", ShortOverview = "电影和剧集媒体库已更新", Date = DateTimeOffset.Now.AddMinutes(-8) }
                    ]
                }));
            return Task.FromResult(Json(Array.Empty<object>()));
        };

        var page = new SettingsPage();
        Field(page, "_request", new SettingsRequest(fixture.Services));
        _root.Children.Add(page);
        try
        {
            page.SelectedCategory = SettingsViewModel.ServerDashboardCategory;
            await LayoutAsync(page);
            var dashboard = (ServerDashboardPage)page.CurrentPage;
            await UntilAsync(() => dashboard.ViewModel.IsReady);
            Require(dashboard.ViewModel.HasSnapshot && dashboard.ViewModel.OnlineCount == "2"
                && dashboard.ViewModel.PlayingCount == "1", "原生控制台未读取或渲染会话数据");
            Require(dashboard.ViewModel.Activities.Count == 2 && dashboard.ViewModel.Sessions[0].Progress == 40,
                "活动列表或播放进度错误");
            var navigation = Get<ListView>(page, "CategoryNavigation");
            Require(navigation.Items.Cast<string>().SequenceEqual(page.Categories), "分组丢失或重排了导航分类");
            Require(page.ViewModel.CategoryGroups.Select(group => group.Name).SequenceEqual(new[] { "EmbyNian", "EmbyServer" }),
                "设置分区标题错误");
            Require(navigation.SelectedItem as string == SettingsViewModel.ServerDashboardCategory, "左栏未选中原生控制台");

            foreach (var width in new[] { 1240, 1018, 760 })
            {
                ResizeInspect(width, width == 1018 ? 753 : 1000);
                await LayoutAsync(page);
                await Task.Delay(420);
                var columns = Get<Grid>(dashboard, "DashboardColumns");
                var activity = Get<Border>(dashboard, "ActivityCard");
                Require(Grid.GetColumn(activity) == (width == 760 ? 0 : 1), "控制台没有随宽度切换列数");
                Require(activity.ActualWidth > 200 && activity.ActualWidth <= columns.ActualWidth, "活动卡片横向溢出");
                Require(Get<ItemsControl>(dashboard, "DashboardSessionList").Items.Count == 2, "设备行未实现");
                if (width == 760)
                {
                    var picker = Get<ComboBox>(page, "CompactCategoryNavigation");
                    Require(picker.Visibility == Visibility.Visible && picker.SelectedItem as string == SettingsViewModel.ServerDashboardCategory,
                        "紧凑导航没有同步选中项");
                }
                await SaveReturnFrameAsync(page, $"server-dashboard-{width}.png");
            }

            var late = Pending<System.Net.Http.HttpResponseMessage>();
            fixture.Transport.Reply = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/Sessions", StringComparison.Ordinal)
                ? late.Task : Task.FromResult(Json(new EmbySystemInfo { ServerName = "迟到响应" }));
            var load = dashboard.ViewModel.ReloadAsync();
            dashboard.Release();
            late.SetResult(Json(Array.Empty<EmbyDashboardSession>()));
            await load;
            Require(dashboard.ViewModel.ServerName == "家庭影音服务器" && !dashboard.ViewModel.CanRefresh,
                "离页后迟到响应更新了控制台");

            using var disconnected = new ServerDashboardViewModel();
            using var empty = CreateFixture();
            disconnected.Attach(empty.Session);
            await disconnected.ReloadAsync();
            Require(disconnected.IsReady && disconnected.NoticeOpen && !disconnected.HasSnapshot, "未登录状态没有提示");
        }
        finally
        {
            page.Release();
            _root.Children.Remove(page);
            ResizeInspect(1420, 980);
        }
    }
}
