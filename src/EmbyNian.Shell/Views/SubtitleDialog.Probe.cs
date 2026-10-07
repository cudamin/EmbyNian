using EmbyNian.Emby;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

public sealed partial class SubtitleDialog
{
    internal static async Task<(bool Ok, string Detail)> ProbeOperationsAsync(XamlRoot root)
    {
        if (App.Instance?.SubtitleProbeActive != true) return (false, "只允许隔离字幕探针");
        var stream = new MediaStream { Index = 1, Type = "Subtitle", IsExternal = true, Codec = "srt", Title = "离线字幕" };
        var source = new MediaSource { Id = "fixture", Name = "离线文件.mkv", MediaStreams = [stream] };
        var downloads = 0;
        var searches = 0;
        var deletes = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new SubtitleDialog(new EmbyItem { Id = "fixture", Name = "离线操作互斥" }, source,
            _ => { searches++; return Task.FromResult(new List<RemoteSubtitleInfo>()); },
            _ => { downloads++; return completion.Task; },
            _ => { deletes++; return Task.CompletedTask; }, () => Task.FromResult(source))
        { XamlRoot = root };
        var shown = dialog.ShowAsync();
        var exclusive = false;
        var waited = false;
        var completed = false;
        try
        {
            await Task.Delay(300);
            dialog.UpdateLayout();
            var button = new Button { DataContext = new SubtitleCandidate(new RemoteSubtitleInfo { Id = "one", Name = "离线候选" }) };
            dialog.OnDownload(button, new RoutedEventArgs());
            dialog.OnDownload(button, new RoutedEventArgs());
            dialog.OnSearch(dialog, new RoutedEventArgs());
            dialog.BeginDelete(dialog._tracks[0]);
            await dialog.DeleteConfirmedAsync();
            exclusive = downloads == 1 && searches == 0 && deletes == 0 && dialog._mutating;
            dialog.Hide();
            await Task.Delay(650);
            waited = shown.Status == Windows.Foundation.AsyncStatus.Started;
            completion.TrySetResult();
            await SettledAsync(dialog);
            completed = dialog.Touched && dialog.NeedsRefresh && !dialog._busy;
        }
        finally
        {
            completion.TrySetResult();
            await SettledAsync(dialog);
            dialog.Hide();
            await shown;
        }

        var writes = 0;
        var recovery = new SubtitleDialog(new EmbyItem { Id = "fixture", Name = "离线响应中断" }, source,
            _ => Task.FromResult(new List<RemoteSubtitleInfo>()), _ =>
            {
                writes++;
                source.MediaStreams.Add(new MediaStream { Index = 2, Type = "Subtitle", IsExternal = true, Codec = "srt", Title = "已下载" });
                return Task.FromException(new IOException("模拟服务器完成后响应中断"));
            }, _ => Task.CompletedTask, () => Task.FromResult(source))
        { XamlRoot = root };
        var recoveryShown = recovery.ShowAsync();
        var recovered = false;
        try
        {
            await Task.Delay(300);
            recovery.OnDownload(new Button { DataContext = new SubtitleCandidate(new RemoteSubtitleInfo { Id = "two" }) }, new RoutedEventArgs());
            await SettledAsync(recovery);
            var unknown = !recovery.Touched && recovery._tracksStale && !recovery.Tracks.IsEnabled;
            recovery.OnRefreshTracks(recovery, new RoutedEventArgs());
            await SettledAsync(recovery);
            recovered = unknown && recovery.NeedsRefresh && writes == 1 && recovery._tracks.Count == 2 && recovery.Tracks.IsEnabled;
        }
        finally { recovery.Hide(); await recoveryShown; }
        return (exclusive && waited && completed && recovered,
            $"真实对话框＋假委托：操作互斥={exclusive}、写入期间等待关闭={waited}、完成后刷新={completed}、响应中断恢复不重发={recovered}");
    }

    private static async Task SettledAsync(SubtitleDialog dialog)
    {
        for (var index = 0; index < 40 && dialog._busy; index++) await Task.Delay(50);
        if (dialog._busy) throw new TimeoutException("离线字幕操作未完成");
    }
}
