using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

internal static class UiLifecycleTests
{
    internal static void Register()
    {
        Test("播放列表：分页途中取消不能回退首屏起播", () => CheckCancelledPageAsync(throws: true).GetAwaiter().GetResult());
        Test("播放列表：不响应取消的旧分页也不能起播", () => CheckCancelledPageAsync(throws: false).GetAwaiter().GetResult());
        Test("播放列表：新查询使旧分页结果作废", () => CheckCancelledPageAsync(throws: false, replace: true).GetAwaiter().GetResult());
        Test("播放列表：正常补齐分页并排除不可播放条目", () =>
        {
            var asked = new List<int>();
            var items = PlayableList.CollectAsync(
                [Movie("1"), new EmbyItem { Id = "folder", Type = EmbyItemType.Folder }], true,
                (start, _) =>
                {
                    asked.Add(start);
                    return Task.FromResult<ItemsResult?>(new ItemsResult { Items = [Movie(start.ToString())], TotalRecordCount = 4 });
                }, () => true, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal("2,3", string.Join(',', asked));
            Assert.Equal("1,2,3", string.Join(',', items.Select(item => item.Id)));
        });
        Test("播放列表：普通网络失败仍可回退已读条目", () =>
        {
            var items = PlayableList.CollectAsync([Movie("1")], true,
                (_, _) => Task.FromException<ItemsResult?>(new IOException("fixture")),
                () => true, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(1, items.Count);
        });
        Test("播放列表：过期任务的失败不得回退首屏", () =>
        {
            var current = true;
            var items = PlayableList.CollectAsync([Movie("1")], true,
                (_, _) =>
                {
                    current = false;
                    return Task.FromException<ItemsResult?>(new IOException("fixture"));
                }, () => current, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(0, items.Count);
        });
        Test("播放列表：无后续页不发请求，取消仍返回空", () =>
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var items = PlayableList.CollectAsync([Movie("1")], false,
                (_, _) => throw new AssertionException("不应请求下一页"),
                () => true, canceled.Token).GetAwaiter().GetResult();
            Assert.Equal(0, items.Count);
        });

        Test("首页刷新：首载被轻量刷新替代仍必须请求轮播", () =>
        {
            var refresh = new HomeRefresh();
            var first = refresh.Begin();
            Assert.True(refresh.NeedsSlides(true));
            var replacement = refresh.Begin();
            Assert.True(refresh.NeedsSlides(false));
            refresh.Complete(first, includedSlides: true);
            Assert.True(refresh.NeedsSlides(false), "旧首载不得使新请求漏读轮播");
            refresh.Complete(replacement, includedSlides: true);
            Assert.False(refresh.NeedsSlides(false));
        });
        Test("首页刷新：已载轮播可以保留，完整刷新仍重新请求", () =>
        {
            var refresh = new HomeRefresh();
            refresh.Complete(refresh.Begin(), includedSlides: true);
            Assert.False(refresh.NeedsSlides(false));
            Assert.True(refresh.NeedsSlides(true));
            refresh.Complete(refresh.Begin(), includedSlides: false);
            Assert.False(refresh.NeedsSlides(false));
        });
        Test("首页刷新：重建版面作废轮播状态和旧完成通知", () =>
        {
            var refresh = new HomeRefresh();
            var prior = refresh.Begin();
            refresh.Complete(prior, includedSlides: true);
            refresh.Invalidate();
            refresh.Complete(prior, includedSlides: true);
            Assert.True(refresh.NeedsSlides(false));
        });

        Test("窗口切换：快速反向共享暂停归属并只恢复一次", () =>
        {
            var pause = new HandoffPause();
            Assert.True(pause.Acquire(paused: false));
            Assert.False(pause.Release(current: true, pending: true));
            Assert.False(pause.Acquire(paused: true));
            Assert.False(pause.Release(current: true, pending: true));
            Assert.False(pause.Acquire(paused: true));
            Assert.True(pause.Release(current: true, pending: false));
            Assert.False(pause.Release(current: true, pending: false));
        });
        Test("窗口切换：用户原先暂停不能被自动恢复", () =>
        {
            var pause = new HandoffPause();
            Assert.False(pause.Acquire(paused: true));
            Assert.False(pause.Release(current: true, pending: false));
        });
        Test("窗口切换：停止或换片作废暂停恢复责任", () =>
        {
            var pause = new HandoffPause();
            Assert.True(pause.Acquire(paused: false));
            Assert.False(pause.Release(current: false, pending: false));
            Assert.False(pause.Release(current: true, pending: false));
        });
    }

    private static async Task CheckCancelledPageAsync(bool throws, bool replace = false)
    {
        using var loads = new LoadGeneration();
        var token = loads.Begin();
        var page = new TaskCompletionSource<ItemsResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = PlayableList.CollectAsync([Movie("first")], true,
            (_, _) =>
            {
                entered.TrySetResult();
                return page.Task;
            }, () => loads.IsCurrent(token), token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (replace) loads.Begin();
        else loads.Cancel();
        if (throws) page.TrySetException(new OperationCanceledException(token));
        else page.TrySetResult(new ItemsResult { Items = [Movie("late")], TotalRecordCount = 2 });
        var items = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, items.Count);
    }

    private static EmbyItem Movie(string id) => new() { Id = id, Name = id, Type = EmbyItemType.Movie };
}
