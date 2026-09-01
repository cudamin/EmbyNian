namespace EmbyNian.Infrastructure;

/// <summary>
/// 「这一趟载入还是最新的那一趟吗」。每一页都靠它：开一趟新的就把上一趟作废，而一趟回来之后先问一句自己还算不算，
/// 不算就一个字都不许往屏上写。
/// <para>
/// 它替掉的是每一页各写一遍的那套「把 <c>CancellationTokenSource</c> 存进字段、回来之后 <c>ReferenceEquals</c> 一下」
/// —— 而这件事写错的样子非常具体：连着点两个媒体库，第一个的答案回来得晚，于是第二个的标题底下摆着第一个的内容。
/// 一个 <c>await</c> 只可能在下一趟已经开始之后才回来，所以这不是边缘情况，是网络慢一点就会出现的常态。
/// </para>
/// <para>
/// 在 Core 而不是在视图模型里，是为了能被钉住。这一段没有一行和界面有关：它的全部内容是「哪一趟是最新的」，而那
/// 恰恰是屏上看不出来的东西 —— 判错了的页面看着完全正常，只是内容属于另一个条目。
/// </para>
/// </summary>
public sealed class LoadGeneration : IDisposable
{
    private CancellationTokenSource? _current;

    /// <summary>这一趟的令牌，同时把上一趟作废。</summary>
    public CancellationToken Begin()
    {
        _current?.Cancel();
        _current?.Dispose();
        _current = new CancellationTokenSource();

        return _current.Token;
    }

    /// <summary>
    /// <paramref name="token"/> 那一趟还是不是最新的。false 意味着后面又开过一趟，调用方必须原地返回。
    /// <para>
    /// 两个条件都要：**是最新那一趟**，而且**它还没被取消**。少了后一条，导航离开页面之后
    /// （<see cref="Cancel"/>，那一下不开新的一趟）飞在路上的答案照旧会落到屏上。
    /// </para>
    /// <para>
    /// 从没开过一趟就是 false —— 那时候手上这个令牌不可能来自这里。<c>default</c> 也是 false，同理。
    /// </para>
    /// </summary>
    public bool IsCurrent(CancellationToken token) =>
        _current is { IsCancellationRequested: false } current && current.Token == token;

    /// <summary>作废在飞的那一趟，不开新的。导航离开页面时用。</summary>
    public void Cancel() => _current?.Cancel();

    public void Dispose()
    {
        _current?.Cancel();
        _current?.Dispose();
        _current = null;
    }
}
