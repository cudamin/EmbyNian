using System.Net;
using System.Text;

namespace EmbyNian.Tests;

/// <summary>
/// 一个按 URL 片段回话的假传输层，并且**把每一趟请求原样记下来**：方法、完整地址、请求体。认不出来的 URL 一律
/// 404，所以路径写错会当场现形，而不是悄悄走到某个兜底分支上。
/// <para>
/// 它本来是 <c>SessionTests</c> 里的一个私有类，为「令牌中途过期」那几档竞态造的（<see cref="Sequence"/> 和
/// <see cref="When"/> 都是那时候长出来的）。2026-09-05 搬出来共用：<c>ItemActionTests</c> 要拿它钉「更多」菜单
/// 背后那十几个接口，而抄第二份假传输层的下场是两份各自变旧 —— 这一份认不出的 URL 会 404，抄本要是漏了那条
/// 兜底，同样一条写错路径的测试在两份上会有两种结果。
/// </para>
/// <para>
/// 方法和请求体是搬出来这一次加的。会话那一批只需要「这个片段被问了几次」，而「删除只发一次 DELETE 而不是
/// POST」「刮削带 ReplaceAllMetadata、刷新不带」这一类要问的正是动词和参数 —— 只按片段计数答不上来。
/// </para>
/// </summary>
internal sealed class StubTransport : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _sequences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _throws = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> _before = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _asked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _understated = new(StringComparer.Ordinal);
    private readonly List<Sent> _sent = [];
    private readonly object _gate = new();

    /// <summary>一趟请求的三件事，全是断言真正要问的东西。</summary>
    internal sealed record Sent(string Method, string Url, string Body);

    public int Total
    {
        get
        {
            lock (_gate) return _asked.Values.Sum();
        }
    }

    public StubTransport Answer(string fragment, string body)
    {
        _answers[fragment] = (HttpStatusCode.OK, body);
        return this;
    }

    public StubTransport Fail(string fragment, HttpStatusCode status)
    {
        _answers[fragment] = (status, "");
        return this;
    }

    /// <summary>
    /// 一串答案，一次消耗一个，最后一个之后就一直是它。「第一次 401、重登之后第二次成功」这种形状只有这样才编
    /// 得出来，而那正是令牌中途过期的样子。
    /// </summary>
    public StubTransport Sequence(string fragment, params (HttpStatusCode Status, string Body)[] answers)
    {
        _sequences[fragment] = new Queue<(HttpStatusCode, string)>(answers);
        _answers[fragment] = answers[^1];
        return this;
    }

    /// <summary>
    /// 这一路根本答不上来 —— 超时、DNS 查不到、拒接。这几种在传输层就抛，不是一个带状态码的回应，所以它们走的
    /// 是 <c>EmbyHttp</c> 里另一条翻译路径。
    /// </summary>
    public StubTransport Throw(string fragment, Exception error)
    {
        _throws[fragment] = error;
        return this;
    }

    /// <summary>
    /// 答一份「说好了有这么多字节、实际只给这么多」的回应，也就是**下载中途断线**。
    /// <para>
    /// 存在的理由是它编不出来别的样子：真的断线要一个真的连接，而这一档是 2026-09-05 那轮代码审查刚加的判断
    /// （服务器说了有多少就必须收到这么多，少了就当失败），少了它磁盘上会留下一个名字正确、大小不对的影片。
    /// 这里直接把 <c>Content-Length</c> 抬高到超过真身，假传输层不做帧校验，所以那份差额正好落到我们自己那句
    /// 检查上。
    /// </para>
    /// </summary>
    public StubTransport Understate(string fragment, string body, long claimed)
    {
        _answers[fragment] = (HttpStatusCode.OK, body);
        _understated[fragment] = claimed;
        return this;
    }

    public int Count(string fragment)
    {
        lock (_gate) return _asked.TryGetValue(fragment, out var count) ? count : 0;
    }

    /// <summary>这个片段上收到的每一趟请求，按发生次序。</summary>
    public IReadOnlyList<Sent> SentTo(string fragment)
    {
        lock (_gate)
            return _sent.Where(one => one.Url.Contains(fragment, StringComparison.Ordinal)).ToList();
    }

    /// <summary>
    /// 这个片段上**唯一**那一趟请求。多于一趟就是断言失败 —— 「一次点击只发一趟」在会写东西的接口上本身就是
    /// 要钉的东西（重复的 DELETE 打在一个已经删掉的 id 上，服务器答的是 404，而屏上会写「删除失败」）。
    /// </summary>
    public Sent Only(string fragment)
    {
        var all = SentTo(fragment);
        Assert.Equal(1, all.Count, $"{fragment} 上应当只有一趟请求");
        return all[0];
    }

    /// <summary>
    /// 在回答这个片段之前先做一件事。存在的理由只有一个：**「请求还在路上的时候用户点了别处」这种竞态，
    /// 只有在这里插一手才编得出来** —— 那一刻正是唯一的时间窗，而外面没有任何办法命中它。
    /// </summary>
    public StubTransport When(string fragment, Action action)
    {
        _before[fragment] = action;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // AbsoluteUri，不是 ToString()。ToString() 交回的是给人看的那一份，它把 %XX 还原成原字符 —— 于是
        // 「合集名要转义」这类断言会看到一个解码过的地址，读起来像是根本没转义。真正上线的是 AbsoluteUri。
        var url = request.RequestUri is { } uri ? uri.AbsoluteUri : "";

        // 读在最前面：底下几条路都可能不回来（抛异常、404），而请求体是「这一趟带了什么」，不该跟着答案的
        // 分支走。UpdateItemAsync 那一条钉的正是「发出去的就是我们改过的那份 JSON」。
        var body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate) _sent.Add(new Sent(request.Method.Method, url, body));

        foreach (var (fragment, error) in _throws)
        {
            if (!url.Contains(fragment, StringComparison.Ordinal)) continue;

            lock (_gate) _asked[fragment] = Count(fragment) + 1;

            // 调用方自己取消的那一档要照实抛出去 —— EmbyHttp 靠 cancellationToken.IsCancellationRequested
            // 分辨「超时」和「他取消了」，而这里正是那两种唯一的分岔口。
            cancellationToken.ThrowIfCancellationRequested();
            throw error;
        }

        foreach (var (fragment, answer) in _answers)
        {
            if (!url.Contains(fragment, StringComparison.Ordinal)) continue;

            lock (_gate) _asked[fragment] = Count(fragment) + 1;

            if (_before.TryGetValue(fragment, out var act)) act();

            var reply = answer;
            if (_sequences.TryGetValue(fragment, out var queue) && queue.Count > 0) reply = queue.Dequeue();

            HttpContent content = new StringContent(reply.Body, Encoding.UTF8, "application/json");

            if (_understated.TryGetValue(fragment, out var claimed))
            {
                content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(reply.Body)));
                content.Headers.ContentLength = claimed;
            }

            return new HttpResponseMessage(reply.Status) { Content = content };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"假传输层没有为 {url} 准备答案", Encoding.UTF8, "text/plain")
        };
    }
}
