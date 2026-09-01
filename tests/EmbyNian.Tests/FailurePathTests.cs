using System.Net;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 出错时那一层的两件事：**哪一趟载入还算最新的**，和**用户会看到哪一句话**。
/// <para>
/// 两件都是屏上看不出对错的。载入判错了的页面看着完全正常，只是内容属于另一个条目 —— 连着点两个媒体库、第一个的
/// 答案回来得晚，就是这个样子。而错的那句话看着也是一句正常的话：一次请求超时说成「操作已取消」，用户会以为是自己
/// 点了别处；一个过期的令牌说成「无法连接到服务器」，用户会去查网线，而该做的是重新登录一次。
/// </para>
/// <para>
/// 这两段本来都在外壳里（<c>PageViewModel</c> 那个 <c>CancellationTokenSource</c> 字段、
/// <c>Shell.Diagnostics.Failure</c>），测试工程碰不到。搬进 Core 之后就是这一份。
/// </para>
/// </summary>
internal static class FailurePathTests
{
    public static void Register()
    {
        RegisterLoadGeneration();
        RegisterDescribe();
    }

    private static void RegisterLoadGeneration()
    {
        Test("载入代次：刚开的那一趟就是最新的", () =>
        {
            using var loads = new LoadGeneration();

            var token = loads.Begin();
            Assert.True(loads.IsCurrent(token));
        });

        Test("载入代次：开了新的一趟，上一趟当场作废", () =>
        {
            // 这就是那个毛病的形状：连着点两个媒体库，第一个的答案回来得晚 —— 它必须一个字都写不进屏幕，
            // 否则第二个的标题底下摆着第一个的内容。
            using var loads = new LoadGeneration();

            var first = loads.Begin();
            var second = loads.Begin();

            Assert.False(loads.IsCurrent(first), "上一趟回来时必须认出自己已经过时了");
            Assert.True(loads.IsCurrent(second));
            Assert.True(first.IsCancellationRequested, "而且它在飞的那些请求也该被取消");
        });

        Test("载入代次：导航离开页面之后，飞在路上的答案不许落地", () =>
        {
            // Cancel 不开新的一趟，所以「是最新那一趟」这一条还成立 —— 少了「还没被取消」那一条，这里就会放行。
            using var loads = new LoadGeneration();

            var token = loads.Begin();
            loads.Cancel();

            Assert.False(loads.IsCurrent(token));
        });

        Test("载入代次：一趟都没开过的时候什么都不算最新", () =>
        {
            using var loads = new LoadGeneration();

            Assert.False(loads.IsCurrent(CancellationToken.None), "手上这个令牌不可能来自这里");

            using var other = new CancellationTokenSource();
            Assert.False(loads.IsCurrent(other.Token), "别处的令牌也不算");
        });

        Test("载入代次：别的地方的令牌永远不算最新", () =>
        {
            using var loads = new LoadGeneration();
            loads.Begin();

            using var stranger = new CancellationTokenSource();
            Assert.False(loads.IsCurrent(stranger.Token));
            Assert.False(loads.IsCurrent(CancellationToken.None));
        });

        Test("载入代次：扔掉之后在飞的那一趟也被取消", () =>
        {
            // 页面被扔掉（Dispose）时飞在路上的请求得停下来，否则它们会一直跑到超时。
            var loads = new LoadGeneration();
            var token = loads.Begin();

            loads.Dispose();

            Assert.True(token.IsCancellationRequested);
            Assert.False(loads.IsCurrent(token));
            loads.Dispose();
        });

        Test("载入代次：连开十趟，只有最后一趟算", () =>
        {
            using var loads = new LoadGeneration();

            var tokens = new List<CancellationToken>();
            for (var each = 0; each < 10; each++) tokens.Add(loads.Begin());

            for (var each = 0; each < 9; each++)
                Assert.False(loads.IsCurrent(tokens[each]), $"第 {each} 趟早就过时了");

            Assert.True(loads.IsCurrent(tokens[9]));
        });
    }

    private static void RegisterDescribe()
    {
        Test("失败措辞：连不上服务器说的是网络，不是服务器返回的原文", () =>
        {
            var unreachable = new EmbyUnreachableException("连接 h 超时", new HttpRequestException("boom"));

            Assert.Equal("无法连接到服务器，请检查地址和网络", Failure.Describe(unreachable));
        });

        Test("失败措辞：请求超时算「连不上」，不算「操作已取消」", () =>
        {
            // 这一条是真会说错的：超时在 .NET 里就是一个 TaskCanceledException，直接照它的类型写就成了
            // 「操作已取消」—— 而用户没取消任何东西，他只会以为是自己点错了。EmbyHttp 在那一层就把它翻成了
            // EmbyUnreachableException（见 SessionTests 里那一条），所以这里读到的是「连不上」。
            var timeout = new EmbyUnreachableException("连接 h 超时", new TaskCanceledException());

            Assert.Equal("无法连接到服务器，请检查地址和网络", Failure.Describe(timeout));
            Assert.Equal("操作已取消", Failure.Describe(new OperationCanceledException()), "真的取消才这么说");
        });

        Test("失败措辞：令牌过期说的是重新登录，不是服务器不通", () =>
        {
            // 次序在这里是真判据：EmbyTokenExpiredException 是 EmbyApiException 的子类，排在它后面就永远轮不到，
            // 用户看到的会是一句「服务器返回 401 Unauthorized」——「登录一下」这个动作从那句话里读不出来。
            Assert.Equal("登录状态已过期，请重新登录", Failure.Describe(new EmbyTokenExpiredException()));
        });

        Test("失败措辞：密码错了用握手自己那句话", () =>
        {
            var wrong = new EmbyAuthenticationException("用户名或密码不正确", HttpStatusCode.Unauthorized, null);

            Assert.Equal("用户名或密码不正确", Failure.Describe(wrong), "改写它只会让这句话更含糊");
        });

        Test("失败措辞：别的服务器错误照抄服务器那句话", () =>
        {
            var api = new EmbyApiException("服务器返回 500 Internal Server Error");

            Assert.Equal("服务器返回 500 Internal Server Error", Failure.Describe(api));
        });

        Test("失败措辞：本地那几种也各有一句", () =>
        {
            Assert.Equal("没有访问权限", Failure.Describe(new UnauthorizedAccessException("Access to the path is denied.")));
            Assert.Equal("磁盘已满", Failure.Describe(new IOException("磁盘已满")));
            Assert.Equal("something odd", Failure.Describe(new InvalidOperationException("something odd")));
        });
    }
}
