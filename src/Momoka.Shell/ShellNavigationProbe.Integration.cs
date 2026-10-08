using System.Collections.ObjectModel;
using System.Net;
using Momoka.Configuration;
using Momoka.Diagnostics;
using Momoka.Emby;
using Momoka.Shell.ViewModels;
using Momoka.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Momoka.Shell;

internal sealed partial class ShellNavigationProbe
{
    private async Task ShellHistoryAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var shell = new ShellPage();
        shell.Attach(fixture.Services);
        _root.Children.Add(shell);
        try
        {
            await LayoutAsync(shell);
            shell.GoTo("home");
            await UntilAsync(() => shell.ActiveContentReady);
            shell.OpenChild(new LibraryRequest { Services = fixture.Services, Title = "离线媒体库", ParentId = "library" });
            await UntilAsync(() => shell.ActiveContentReady);
            shell.OpenDetail(DetailRequest.For(fixture.Services, Series()));
            await UntilAsync(() => shell.ActiveContentReady);
            shell.GoTo("home");
            Call(shell, "GoBack");
            await UntilAsync(() => shell.ActiveContentReady);
            var trail = Get<ObservableCollection<Crumb>>(shell, "_trail");
            Require(trail.Select(crumb => crumb.Label).SequenceEqual(new[] { "主页", "离线媒体库", Series().Name }),
                "返回详情未恢复完整路径");
            Require(Get<Button>(shell, "HomeButton").IsEnabled, "详情页主页按钮未恢复");
            Call(shell, "GoForward");
            await UntilAsync(() => shell.ActiveContentReady);
            Require(shell.CurrentTag == "home" && !Get<Button>(shell, "HomeButton").IsEnabled, "前进到主页的按钮状态不一致");
            Call(shell, "GoBack");
            await UntilAsync(() => shell.ActiveContentReady);
            Require(Get<Button>(shell, "HomeButton").IsEnabled && trail.Count == 3, "第二次返回的路径或按钮错误");
        }
        finally
        {
            Call(shell, "ReleaseContent");
            shell.SignInRoot.Detach();
            _root.Children.Remove(shell);
        }
    }

    private async Task ShellIdentityAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var shell = new ShellPage();
        shell.Attach(fixture.Services);
        _root.Children.Add(shell);
        var reply = Pending<HttpResponseMessage>();
        var entered = Pending<bool>();
        try
        {
            await LayoutAsync(shell);
            fixture.Session.TakeRestoredViews();
            fixture.Transport.Reply = (request, _) =>
            {
                if (request.RequestUri!.Host == "a.invalid" && request.RequestUri.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
                {
                    entered.TrySetResult(true);
                    return reply.Task;
                }
                if (request.RequestUri.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
                    return Task.FromResult(Json(new { Items = new[] { new EmbyItem { Id = "b-library", Name = "B 的库", Type = EmbyItemType.CollectionFolder, CollectionType = "movies" } } }));
                return Task.FromResult(FakeTransport.Standard(request));
            };
            var enteringA = (Task)Call(shell, "EnterShellAsync")!;
            await entered.Task;
            await fixture.SelectAsync("b.invalid");
            await (Task)Call(shell, "EnterShellAsync")!;
            reply.SetResult(Json(new { Items = new[] { new EmbyItem { Id = "a-library", Name = "A 的旧库", Type = EmbyItemType.CollectionFolder } } }));
            await enteringA;
            Require(shell.LibraryViews.Count == 1 && shell.LibraryViews[0].Id == "b-library" && shell.CurrentTag == "home",
                "旧目录覆盖新身份或更改新导航");
        }
        finally
        {
            reply.TrySetResult(Items(0, 0));
            Call(shell, "ReleaseContent");
            shell.SignInRoot.Detach();
            _root.Children.Remove(shell);
        }
    }

    private async Task ShellSwitchRaceAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var shell = new ShellPage();
        shell.Attach(fixture.Services);
        _root.Children.Add(shell);
        var reply = Pending<HttpResponseMessage>();
        var entered = Pending<bool>();
        ServerProfile Server(string host)
        {
            var server = new ServerProfile { Name = host, Url = $"https://{host}/media" };
            server.Accounts.Add(new AccountProfile
            {
                Username = "user",
                UserId = "user",
                ProtectedAccessToken = PassthroughSecretProtector.Instance.Protect("offline-fixture-token")
            });
            fixture.Settings.Servers.Add(server);
            return server;
        }
        try
        {
            await LayoutAsync(shell);
            var a = Server("old.invalid");
            var b = Server("new.invalid");
            fixture.Transport.Reply = (request, _) =>
            {
                if (request.RequestUri!.Host == "old.invalid" && request.RequestUri.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
                {
                    entered.TrySetResult(true);
                    return reply.Task;
                }
                return Task.FromResult(FakeTransport.Standard(request));
            };
            var switchingA = shell.SwitchProfileAsync(a, a.Accounts[0]);
            await entered.Task;
            await shell.SwitchProfileAsync(b, b.Accounts[0]);
            reply.SetResult(Items(0, 0));
            await switchingA;
            Require(fixture.Session.Connection?.ApiBase.Host == "new.invalid" && !shell.SignInVisible && shell.CurrentTag == "home",
                "旧恢复失败或取消把新身份退回登录卡");
        }
        finally
        {
            reply.TrySetResult(Items(0, 0));
            Call(shell, "ReleaseContent");
            shell.SignInRoot.Detach();
            _root.Children.Remove(shell);
        }
    }

    private async Task ShellDirectoryRetryAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        fixture.Session.TakeRestoredViews();
        var requests = 0;
        fixture.Transport.Reply = (request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
                return Task.FromResult(FakeTransport.Standard(request));
            requests++;
            return Task.FromResult(requests == 1 ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json(new { Items = new[] { new EmbyItem { Id = "retried", Name = "重试恢复的库", Type = EmbyItemType.CollectionFolder, CollectionType = "movies" } } }));
        };
        var shell = new ShellPage();
        shell.Attach(fixture.Services);
        _root.Children.Add(shell);
        try
        {
            await LayoutAsync(shell);
            await (Task)Call(shell, "EnterShellAsync")!;
            Require(Get<bool>(shell, "_librariesFailed") && Get<Button>(shell, "HomeButton").IsEnabled,
                "目录失败后没有可重试入口");
            await (Task)Call(shell, "RetryLibrariesAsync")!;
            Require(requests == 2 && shell.LibraryViews.Single().Id == "retried" && !Get<bool>(shell, "_librariesFailed"),
                "目录重试未重新请求或更新首页快照");
        }
        finally
        {
            Call(shell, "ReleaseContent");
            shell.SignInRoot.Detach();
            _root.Children.Remove(shell);
        }
    }

    private static Task DiagnosticsEmptyAsync()
    {
        using var vm = new DiagnosticsViewModel();
        Field(vm, "_live", true);
        Field(vm, "_sink", new RingBufferLogSink(1));
        vm.SelectedLevel = 3;
        Call(vm, "Append", new LogEntry(DateTimeOffset.Now, LogLevel.Error, "fixture", "old", null));
        Require(vm.Rows.Count == 1 && !vm.ShowEmptyLogs, "错误日志没有进入筛选");
        Call(vm, "Append", new LogEntry(DateTimeOffset.Now, LogLevel.Info, "fixture", "new", null));
        Require(vm.Rows.Count == 0 && vm.ShowEmptyLogs, "最后一条匹配日志被淘汰后未显示空态");
        return Task.CompletedTask;
    }
}
