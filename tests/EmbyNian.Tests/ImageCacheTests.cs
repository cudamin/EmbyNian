using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 封面「有时候永远空着」那个毛病的两半，钉在这里。
/// <para>
/// 一半是 <see cref="SharedWork{TResult}"/>：同一张图被好几张卡片同时要，只下载一次 —— 而那一次下载以前是
/// 用第一个请求者的取消令牌启动的，于是滚出屏幕的那张卡片顺手取消了旁边那张还在等的下载。等到的空答案被
/// 读成「服务器没有这张图」并永久记住，卡片就一直空着，直到离开页面再回来。所以这里最要紧的一条是：放弃的
/// 一方等到的必须是「取消」，不是「没有」。
/// </para>
/// <para>
/// 另一半是 <see cref="LruCache{TKey,TValue}"/>：解好的封面留一批在内存里，往回滚不用重新读盘重新解码。
/// 它的全部约定就是淘汰顺序，所以测的也就是这个。
/// </para>
/// </summary>
internal static class ImageCacheTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public static void Register()
    {
        RegisterSharedWork();
        RegisterLru();
        RegisterTags();
    }

    private static void RegisterSharedWork()
    {
        Test("图片：同一张图同时被要两次，只下载一次", () =>
        {
            var work = new SharedWork<int>();
            var download = Pending();
            var started = 0;

            Task<int> Ask() => work.RunAsync("poster", () =>
            {
                started++;
                return download.Task;
            }, CancellationToken.None);

            var first = Ask();
            var second = Ask();

            Assert.Equal(1, started, "第二次要同一张图应该搭第一次的车");
            Assert.Equal(1, work.Running);

            download.SetResult(7);

            Assert.Equal(7, Wait(first));
            Assert.Equal(7, Wait(second), "两个请求者拿到的是同一份答案");
        });

        Test("图片：一张卡片走开了，旁边那张照样拿到封面", () =>
        {
            var work = new SharedWork<int>();
            var download = Pending();
            using var leaving = new CancellationTokenSource();

            var gone = work.RunAsync("poster", () => download.Task, leaving.Token);
            var staying = work.RunAsync("poster", () => download.Task, CancellationToken.None);

            leaving.Cancel();

            // 这一条就是那个毛病本身：走开的一方等到的必须是「取消」。要是它等到一个空答案，卡片会把空读成
            // 「服务器没有这张图」并永久记住 —— 那正是封面永远空着的由来。
            Assert.True(Failure(gone) is OperationCanceledException, "放弃的一方应该抛取消，而不是拿到空答案");

            download.SetResult(9);

            Assert.Equal(9, Wait(staying), "下载不归任何一个请求者所有，走开一个不影响还在等的那一个");
        });

        Test("图片：走开的那一方不影响下载本身", () =>
        {
            var work = new SharedWork<int>();
            var download = Pending();
            using var leaving = new CancellationTokenSource();

            var only = work.RunAsync("poster", () => download.Task, leaving.Token);
            leaving.Cancel();

            Assert.True(Failure(only) is OperationCanceledException);

            // 唯一的请求者也走开了，下载还是跑完 —— 字节会落进磁盘缓存，下一次进这一页就是现成的。
            download.SetResult(4);
            Assert.False(only.IsCompletedSuccessfully, "等待本身已经取消了");
            Assert.Equal(4, Wait(download.Task), "下载没有被取消");
        });

        Test("图片：下载完成后这把钥匙就放开了", () =>
        {
            var work = new SharedWork<int>();
            var runs = 0;

            Assert.Equal(1, Wait(work.RunAsync("poster", () =>
            {
                runs++;
                return Task.FromResult(1);
            }, CancellationToken.None)));

            Assert.Equal(0, work.Running, "做完的活不能留在表里");

            // 留在表里的话，这一次会拿到上一次的答案 —— 服务器上换过的海报就再也回不来了。
            Assert.Equal(2, Wait(work.RunAsync("poster", () =>
            {
                runs++;
                return Task.FromResult(2);
            }, CancellationToken.None)));

            Assert.Equal(2, runs);
        });

        Test("图片：下载失败不会把这张图永久钉死", () =>
        {
            var work = new SharedWork<int>();

            var failed = work.RunAsync("poster",
                () => Task.FromException<int>(new IOException("断线")), CancellationToken.None);

            Assert.True(Failure(failed) is IOException);
            Assert.Equal(0, work.Running, "失败的那一次也要从表里去掉");

            Assert.Equal(3, Wait(work.RunAsync("poster", () => Task.FromResult(3), CancellationToken.None)),
                "下一次要重新试，而不是重演上一次的失败");
        });

        Test("图片：取图的活当场抛异常也要放开钥匙", () =>
        {
            var work = new SharedWork<int>();

            Assert.Throws<InvalidOperationException>(() =>
                work.RunAsync("poster", () => throw new InvalidOperationException("没登录"), CancellationToken.None));

            Assert.Equal(0, work.Running);
        });

        Test("图片：不同的图各走各的", () =>
        {
            var work = new SharedWork<int>();
            var poster = Pending();
            var backdrop = Pending();

            var one = work.RunAsync("poster", () => poster.Task, CancellationToken.None);
            var other = work.RunAsync("backdrop", () => backdrop.Task, CancellationToken.None);

            Assert.Equal(2, work.Running);

            backdrop.SetResult(2);
            poster.SetResult(1);

            Assert.Equal(1, Wait(one));
            Assert.Equal(2, Wait(other));
        });
    }

    private static void RegisterLru()
    {
        Test("图片缓存：满了就丢最久没看的那一张", () =>
        {
            var cache = new LruCache<string, int>(2);
            cache.Set("a", 1);
            cache.Set("b", 2);
            cache.Set("c", 3);

            Assert.Equal(2, cache.Count, "容量是上限，不是建议");
            Assert.False(cache.TryGet("a", out _), "最久没看的那一张先走");
            Assert.True(cache.TryGet("b", out var b));
            Assert.Equal(2, b);
            Assert.True(cache.TryGet("c", out _));
        });

        Test("图片缓存：看一眼就算用过一次", () =>
        {
            var cache = new LruCache<string, int>(2);
            cache.Set("a", 1);
            cache.Set("b", 2);

            // 往回滚看到了 a：它就不再是最久没看的那一张，该走的是 b。
            Assert.True(cache.TryGet("a", out _));
            cache.Set("c", 3);

            Assert.True(cache.TryGet("a", out _), "刚看过的不能被淘汰");
            Assert.False(cache.TryGet("b", out _));
        });

        Test("图片缓存：同一把钥匙换一张图，数量不变", () =>
        {
            var cache = new LruCache<string, int>(2);
            cache.Set("a", 1);
            cache.Set("a", 11);

            Assert.Equal(1, cache.Count);
            Assert.True(cache.TryGet("a", out var value));
            Assert.Equal(11, value, "留下的是新的那一张");
        });

        Test("图片缓存：淘汰顺序是从新到旧排的", () =>
        {
            var cache = new LruCache<string, int>(3);
            cache.Set("a", 1);
            cache.Set("b", 2);
            cache.Set("c", 3);
            cache.TryGet("a", out _);

            Assert.Equal("a,c,b", string.Join(',', cache.Keys), "最近用过的排在最前面");
        });

        Test("图片缓存：清空之后什么都不剩", () =>
        {
            var cache = new LruCache<string, int>(4);
            cache.Set("a", 1);
            cache.Set("b", 2);
            cache.Clear();

            Assert.Equal(0, cache.Count);
            Assert.False(cache.TryGet("a", out _));

            // 清空之后还能照常用：链表和字典要一起清干净，只清一个就会在这里散架。
            cache.Set("c", 3);
            Assert.True(cache.TryGet("c", out _));
            Assert.Equal(1, cache.Count);
        });

        Test("图片缓存：容量至少为一", () =>
            Assert.Throws<ArgumentOutOfRangeException>(() => new LruCache<string, int>(0)));
    }

    private static void RegisterTags()
    {
        Test("图片：每种图各认自己的标签", () =>
        {
            var item = new EmbyItem { Id = "film1", Name = "film1" };
            item.ImageTags[EmbyImageStore.Primary] = "p";
            item.ImageTags[EmbyImageStore.Logo] = "l";
            item.BackdropImageTags.Add("b1");
            item.BackdropImageTags.Add("b2");

            Assert.Equal("p", EmbyImageStore.TagFor(item, EmbyImageStore.Primary));
            Assert.Equal("l", EmbyImageStore.TagFor(item, EmbyImageStore.Logo));
            Assert.Equal("b1", EmbyImageStore.TagFor(item, EmbyImageStore.Backdrop), "背景图有好几张，取第一张");

            // 内存里那份解好的封面是按这个标签存的，所以「没有」必须是 null：拿空串当钥匙的话，服务器上换过
            // 图之后新旧两张会挤在同一个格子里。
            Assert.Null(EmbyImageStore.TagFor(item, EmbyImageStore.Thumb), "没有的那一种是 null");
            Assert.Null(EmbyImageStore.TagFor(item, EmbyImageStore.Banner));
        });
    }

    /// <summary>一次还没回来的下载。<c>RunContinuationsAsynchronously</c> 免得完成时在等待者的线程上接着跑。</summary>
    private static TaskCompletionSource<int> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static T Wait<T>(Task<T> task)
    {
        Assert.True(task.Wait(Deadline), "任务没在五秒内完成");
        return task.Result;
    }

    /// <summary>
    /// 任务失败时抛出来的那个异常，成功时是 null。返回而不是让它逃出去，是为了能对「抛的是哪一种」下断言 ——
    /// 「取消」和「拿到空答案」的区别正是这一组测试的题目。
    /// </summary>
    private static Exception? Failure(Task task)
    {
        try
        {
            Assert.True(task.Wait(Deadline), "任务没在五秒内完成");
            return null;
        }
        catch (AggregateException error)
        {
            return error.InnerException;
        }
    }
}
