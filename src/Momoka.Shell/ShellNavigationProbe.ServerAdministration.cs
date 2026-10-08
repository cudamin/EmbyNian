using System.Net;
using System.Text.Json.Nodes;
using Momoka.Emby;
using Momoka.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Momoka.Shell;

internal sealed partial class ShellNavigationProbe
{
    private async Task ServerAdministrationAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        var configuration = JsonNode.Parse("""{"ServerName":"模拟服务器","EnableHttps":true,"FutureOption":{"Keep":42}}""")!.AsObject();
        var writes = 0;
        var restart = 0;
        var shutdown = 0;
        var restricted = false;
        TaskCompletionSource<HttpResponseMessage>? pendingConfiguration = null;
        fixture.Transport.Reply = async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/System/Info", StringComparison.Ordinal))
                return restricted ? new(HttpStatusCode.Forbidden) : Json(new EmbySystemInfo
                {
                    ServerName = configuration["ServerName"]!.GetValue<string>(),
                    Version = "4.10.0.40",
                    OperatingSystemDisplayName = "Linux",
                    ProgramDataPath = "/config",
                    CanSelfRestart = true
                });
            if (path.EndsWith("/System/Configuration", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Get)
                    return pendingConfiguration is { } pending ? await pending.Task : Json(configuration);
                configuration = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
                writes++;
                return new(HttpStatusCode.NoContent);
            }
            if (path.EndsWith("/System/Restart", StringComparison.Ordinal)) { restart++; return new(HttpStatusCode.NoContent); }
            if (path.EndsWith("/System/Shutdown", StringComparison.Ordinal)) { shutdown++; return new(HttpStatusCode.NoContent); }
            if (path.EndsWith("/Sessions", StringComparison.Ordinal)) return Json(Array.Empty<EmbyDashboardSession>());
            if (path.EndsWith("/ActivityLog/Entries", StringComparison.Ordinal)) return Json(new EmbyActivityResult());
            return FakeTransport.Standard(request);
        };

        var page = new ServerDashboardPage();
        var vm = page.ViewModel;
        vm.Attach(fixture.Session);
        _root.Children.Add(page);
        try
        {
            await vm.ReloadAsync();
            await LayoutAsync(page);
            Require(vm.CanManage && vm.CanRestart, "管理员菜单未启用");
            foreach (var (buttonName, file) in new[]
            {
                ("ServerDashboardMoreButton", "server-menu-more.png"),
                ("ServerDashboardPowerButton", "server-menu-power.png")
            })
            {
                var button = Get<Button>(page, buttonName);
                var menu = (MenuFlyout)button.Flyout;
                Require(menu.Items.Count == 2, "菜单项目不完整");
                menu.ShowAt(button);
                await Task.Delay(120);
                var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(page.XamlRoot)
                    .SelectMany(popup => Descendants(popup.Child)).OfType<MenuFlyoutPresenter>().First();
                await SaveReturnFrameAsync(presenter, file);
                menu.Hide();
                await Task.Delay(100);
            }

            // 真正弹出确认和编辑对话框，但一律关闭；正向写入另走假确认 + 假传输。
            var renaming = vm.RenameServerCommand.ExecuteAsync(null);
            await UntilAsync(() => Get<ContentDialog?>(page, "_dialog") is not null);
            var dialog = Get<ContentDialog>(page, "_dialog");
            await LayoutAsync(dialog);
            var input = (TextBox)dialog.Content;
            Require(!dialog.IsPrimaryButtonEnabled, "相同名称不应提交");
            input.Text = " ";
            Require(!dialog.IsPrimaryButtonEnabled, "空名称未拦截");
            input.Text = "新的显示名称";
            await UntilAsync(() => dialog.IsPrimaryButtonEnabled);
            Require(dialog.IsPrimaryButtonEnabled, "合法名称不能保存");
            await SaveReturnFrameAsync(dialog, "server-rename.png");
            dialog.Hide();
            await renaming;
            Require(writes == 0, "取消改名仍写入服务器");

            var restarting = vm.RestartServerCommand.ExecuteAsync(null);
            await UntilAsync(() => Get<ContentDialog?>(page, "_dialog") is not null);
            dialog = Get<ContentDialog>(page, "_dialog");
            await LayoutAsync(dialog);
            Require(dialog.DefaultButton == ContentDialogButton.Close && ((TextBlock)dialog.Content).Text.Contains("模拟服务器", StringComparison.Ordinal),
                "电源确认没有保护默认键或标明服务器");
            await SaveReturnFrameAsync(dialog, "server-restart-confirm.png");
            dialog.Hide();
            await restarting;
            Require(restart == 0, "取消重启仍发送请求");

            Call(page, "OnServerInformation", page, new RoutedEventArgs());
            await UntilAsync(() => Get<ContentDialog?>(page, "_dialog") is not null);
            dialog = Get<ContentDialog>(page, "_dialog");
            await LayoutAsync(dialog);
            await SaveReturnFrameAsync(dialog, "server-information.png");
            dialog.Hide();
            await UntilAsync(() => Get<ContentDialog?>(page, "_dialog") is null);

            vm.EditServerName = _ => Task.FromResult<string?>(" 新名称 ");
            await vm.RenameServerCommand.ExecuteAsync(null);
            Require(writes == 1 && vm.ServerName == "新名称" && configuration["EnableHttps"]!.GetValue<bool>()
                && configuration["FutureOption"]!["Keep"]!.GetValue<int>() == 42, "改名丢失了其他服务器配置");

            vm.UseConfirm((_, _, _) => Task.FromResult(true));
            await vm.RestartServerCommand.ExecuteAsync(null);
            Require(restart == 1 && !vm.CanManage && !vm.CanAutoRefresh, "重启未只提交一次或未暂停自动刷新");
            await vm.ReloadAsync();
            await vm.ShutdownServerCommand.ExecuteAsync(null);
            Require(shutdown == 1 && !vm.CanManage, "关闭服务器请求错误");
            await vm.ReloadAsync();

            var confirmation = Pending<bool>();
            vm.UseConfirm((_, _, _) => confirmation.Task);
            restarting = vm.RestartServerCommand.ExecuteAsync(null);
            await fixture.SelectAsync("b.invalid");
            confirmation.SetResult(true);
            await restarting;
            Require(restart == 1, "切服后仍执行旧确认");

            vm.Attach(fixture.Session);
            await vm.ReloadAsync();
            pendingConfiguration = Pending<HttpResponseMessage>();
            var rename = EmbyServerAdministration.RenameAsync(fixture.Session.Capture(), "不应写入", CancellationToken.None);
            await fixture.SelectAsync("c.invalid");
            pendingConfiguration.SetResult(Json(configuration));
            var cancelled = false;
            try { await rename; }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && writes == 1, "读取配置途中切服仍写入旧配置");
            pendingConfiguration = null;

            restricted = true;
            vm.Attach(fixture.Session);
            await vm.ReloadAsync();
            Require(vm.HasSnapshot && !vm.CanManage && !vm.CanRestart, "普通账号仍可使用管理操作");
        }
        finally
        {
            page.Release();
            _root.Children.Remove(page);
        }
    }
}
