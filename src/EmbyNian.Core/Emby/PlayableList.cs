using EmbyNian.Diagnostics;

namespace EmbyNian.Emby;

/// <summary>补齐播放列表；用户取消不能退回已经收集的部分列表。</summary>
public static class PlayableList
{
    public static async Task<List<EmbyItem>> CollectAsync(
        IReadOnlyList<EmbyItem> loaded,
        bool hasMore,
        Func<int, CancellationToken, Task<ItemsResult?>> read,
        Func<bool> isCurrent,
        CancellationToken cancellationToken)
    {
        var items = loaded.Where(item => item.IsPlayable).ToList();
        var start = loaded.Count;

        try
        {
            while (hasMore)
            {
                if (cancellationToken.IsCancellationRequested || !isCurrent()) return [];
                var page = await read(start, cancellationToken).ConfigureAwait(true);
                if (cancellationToken.IsCancellationRequested || !isCurrent()) return [];
                if (page is null || page.Items.Count == 0) break;

                items.AddRange(page.Items.Where(item => item.IsPlayable));
                start += page.Items.Count;
                hasMore = start < page.TotalRecordCount;
            }
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception error)
        {
            if (cancellationToken.IsCancellationRequested || !isCurrent()) return [];
            Log.Warn("library", "读取全部条目失败，使用已读取的播放列表", error);
        }

        return cancellationToken.IsCancellationRequested || !isCurrent() ? [] : items;
    }
}
