using System.Reflection;
using EmbyNian.Emby;
using EmbyNian.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell;

internal sealed partial class ShellNavigationProbe
{
    private CoverDialog Cover(EmbyItem item, Func<Task<EmbyItem?>> reload,
        Func<Task>? delete = null, FetchArtwork? fetch = null) => new(item,
        (_, _) => Task.FromResult(new RemoteImageResult()),
        fetch ?? ((_, _, _, _, _) => Task.FromResult<byte[]?>(null)),
        (_, _, _, _) => Task.CompletedTask, (_, _, _) => delete?.Invoke() ?? Task.CompletedTask,
        (_, _, _) => Task.CompletedTask, _ => reload(), _ => Task.FromResult<byte[]?>(null))
        { XamlRoot = _root.XamlRoot };

    private async Task CoverPickRaceAsync()
    {
        var item = new EmbyItem { Id = "fixture", Name = "离线候选取消" };
        var dialog = Cover(item, () => Task.FromResult<EmbyItem?>(item));
        var shown = dialog.ShowAsync().AsTask();
        try
        {
            await LayoutAsync(dialog);
            foreach (var cancelled in new[] { true, false })
            {
                var picker = new CoverPickerDialog(item, ItemArtwork.SingleKinds.First(), new RemoteImageResult(), dialog);
                var choice = (Task<ContentDialogResult>)Call(dialog, "ShowPanelAsync", picker)!;
                if (cancelled)
                    Get<TaskCompletionSource<ContentDialogResult>>(dialog, "_panelCompletion").TrySetResult(ContentDialogResult.None);
                Field(picker, "<Uploaded>k__BackingField", new PickedArtwork([1, 2, 3], "fixture.png"));
                Get<Action?>(picker, "UploadCompleted")?.Invoke();
                Require(await choice == (cancelled ? ContentDialogResult.None : ContentDialogResult.Primary),
                    "取消与上传同拍完成时必须由先完成的明确结果决定，不能靠 Uploaded 非空重开提交");
            }
        }
        finally { dialog.Hide(); await shown; }
    }

    private async Task CoverRefreshAsync()
    {
        var item = new EmbyItem { Id = "fixture", Name = "离线图片", BackdropImageTags = ["a"] };
        var fresh = new EmbyItem { Id = item.Id, Name = item.Name, BackdropImageTags = ["a", "b"], ImageTags = new() { ["Primary"] = "poster" } };
        var indexes = new List<int?>();
        var dialog = Cover(item, () => Task.FromResult<EmbyItem?>(fresh), fetch: (_, type, _, _, index) =>
        { if (type == "Backdrop") indexes.Add(index); return Task.FromResult<byte[]?>(null); });
        var shown = dialog.ShowAsync().AsTask();
        try
        {
            await LayoutAsync(dialog);
            await (Task)Call(dialog, "RefreshAsync")!;
            await LayoutAsync(dialog);
            var slot = dialog.Groups.First(row => row.ImageType == "Primary");
            var button = Descendants(dialog).OfType<Button>().Single(row => ReferenceEquals(row.Tag, slot)
                && AutomationProperties.GetAutomationId(row) == "DeleteArtwork");
            var backdropList = Descendants(dialog).OfType<ItemsControl>().Single(control => ReferenceEquals(control.ItemsSource, dialog.Backdrops));
            Require(slot.Has && button.IsEnabled && backdropList.Items.Count == 2 && indexes.Contains(1), "图片绑定、列表或背景图序号未更新");
            Require(AutomationProperties.GetName(button) == "删除封面海报图", "删除按钮读屏名必须包含动作");
            var upload = Get<Button>(dialog, "AddBackdropButton");
            Require(upload.ActualWidth > 160, "带文字的添加背景图按钮不能套固定40宽的图标样式");
            var tiles = Descendants(dialog).OfType<Button>().Where(row => AutomationProperties.GetAutomationId(row) == "ArtworkTile");
            Require(tiles.All(tile => tile.ActualHeight >= 96), "无图格子必须保留可识别的图片框高度");
        }
        finally { dialog.Hide(); await shown; }
    }

    private async Task CoverCancelAsync()
    {
        var item = new EmbyItem { Id = "fixture", Name = "离线删除确认", ImageTags = new() { ["Primary"] = "poster" } };
        var deletes = 0;
        var dialog = Cover(item, () => Task.FromResult<EmbyItem?>(item), () => { deletes++; return Task.CompletedTask; });
        var shown = dialog.ShowAsync().AsTask();
        try
        {
            await LayoutAsync(dialog);
            Call(dialog, "OnDelete", new Button { Tag = dialog.Groups[0] }, new RoutedEventArgs());
            await UntilAsync(() => Get<TaskCompletionSource<ContentDialogResult>?>(dialog, "_panelCompletion") is not null);
            Require(dialog.DefaultButton == ContentDialogButton.Close && !shown.IsCompleted, "删除确认默认键或对话框状态不安全");
            Get<TaskCompletionSource<ContentDialogResult>>(dialog, "_panelCompletion").SetResult(ContentDialogResult.None);
            await UntilAsync(() => !Get<bool>(dialog, "_busy"));
            Require(deletes == 0 && !dialog.Touched && !shown.IsCompleted, "取消确认不能删除或退出管理面板");
        }
        finally { dialog.Hide(); await shown; }
    }

    private async Task CoverWriteAsync()
    {
        var item = new EmbyItem { Id = "fixture", Name = "离线写入等待", ImageTags = new() { ["Primary"] = "poster" } };
        var completion = Pending<bool>();
        var entered = Pending<bool>();
        var deletes = 0;
        var dialog = Cover(item, () => Task.FromResult<EmbyItem?>(item), async () =>
        { deletes++; entered.TrySetResult(true); await completion.Task; item.ImageTags.Clear(); });
        var shown = dialog.ShowAsync().AsTask();
        try
        {
            await LayoutAsync(dialog);
            var button = new Button { Tag = dialog.Groups[0] };
            Call(dialog, "OnDelete", button, new RoutedEventArgs());
            await UntilAsync(() => Get<TaskCompletionSource<ContentDialogResult>?>(dialog, "_panelCompletion") is not null);
            Get<TaskCompletionSource<ContentDialogResult>>(dialog, "_panelCompletion").SetResult(ContentDialogResult.Primary);
            await entered.Task;
            Call(dialog, "OnDelete", button, new RoutedEventArgs());
            dialog.Hide();
            await SettleAsync();
            Require(deletes == 1 && !shown.IsCompleted, "写入期间重复删除或提前关闭");
            completion.SetResult(true);
            await UntilAsync(() => !Get<bool>(dialog, "_busy"));
            Require(dialog.Touched && !dialog.Groups[0].Has, "成功删除没有刷新状态");
        }
        finally { completion.TrySetResult(true); await UntilAsync(() => !Get<bool>(dialog, "_busy")); dialog.Hide(); await shown; }
    }

    private async Task CoverUncertainAsync()
    {
        var item = new EmbyItem { Id = "fixture", Name = "离线结果不明", ImageTags = new() { ["Primary"] = "poster" } };
        var writes = 0;
        var dialog = Cover(item, () => Task.FromResult<EmbyItem?>(item), () =>
        { writes++; item.ImageTags.Clear(); return Task.FromException(new IOException("synthetic response lost")); });
        var shown = dialog.ShowAsync().AsTask();
        try
        {
            await LayoutAsync(dialog);
            var button = new Button { Tag = dialog.Groups[0] };
            Call(dialog, "OnDelete", button, new RoutedEventArgs());
            await UntilAsync(() => Get<TaskCompletionSource<ContentDialogResult>?>(dialog, "_panelCompletion") is not null);
            Get<TaskCompletionSource<ContentDialogResult>>(dialog, "_panelCompletion").SetResult(ContentDialogResult.Primary);
            await UntilAsync(() => !Get<bool>(dialog, "_busy"));
            Call(dialog, "OnDelete", button, new RoutedEventArgs());
            Require(writes == 1 && dialog.NeedsRefresh && !dialog.Groups[0].Ready, "结果不明仍允许重复提交");
            Call(dialog, "OnRefreshArtwork", dialog, new RoutedEventArgs());
            await UntilAsync(() => !Get<bool>(dialog, "_busy"));
            Require(writes == 1 && !dialog.Groups[0].Has && dialog.Groups[0].Ready, "只读刷新未解除不明状态");
        }
        finally { dialog.Hide(); await shown; }
    }

    private static async Task CoverLateImageAsync()
    {
        var kind = ItemArtwork.SingleKinds.First();
        var slot = new ArtworkSlot(kind, 0, "old");
        var reply = Pending<byte[]?>();
        var loading = slot.LoadAsync(_ => reply.Task);
        slot.Retag(null);
        reply.SetResult(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="));
        await loading;
        Require(!slot.Has && slot.Picture is null, "旧缩略图复活已删除图片");
    }

    private static Task DialogConstructionAsync()
    {
        var result = ((bool Ok, string Detail))typeof(ShellSelfCheck).GetMethod("ReadDialogs", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        Require(result.Ok, result.Detail);
        return Task.CompletedTask;
    }

}
