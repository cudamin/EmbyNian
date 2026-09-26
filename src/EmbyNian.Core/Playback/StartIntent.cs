namespace EmbyNian.Playback;

/// <summary>
/// 起播准备期的意图代次。一次「点播放」到 mpv 真正起来之间隔着封面等待、媒体详情、选集解析几段网络，
/// 用户随时可能按停止 —— 停止必须能撤掉<b>还在准备中</b>的起播，而不是只停已经建立的播放。
/// <para>
/// 两个动作、一个判断：<see cref="Begin"/> 发新意图并作废所有旧意图（最后点选的那一次胜出，先前的
/// 静默退场 —— 取消不是错误）；<see cref="CancelAll"/> 是用户停止，作废全部在飞的意图；调用方在每个
/// 跨异步阶段之后问一次 <see cref="IsCurrent"/>，对不上就原地返回。纯类、无线程亲和，Core 可测。
/// </summary>
public sealed class StartIntent
{
    private long _issued;
    private long _cancelledBefore;
    private readonly object _gate = new();

    /// <summary>发一张新意图票；此前发出的所有票当场作废。</summary>
    public long Begin()
    {
        lock (_gate)
        {
            _cancelledBefore = _issued;
            return ++_issued;
        }
    }

    /// <summary>这一张票还有效吗。false ＝ 之后有过新起播或用户停止，调用方必须静默放弃。</summary>
    public bool IsCurrent(long intent)
    {
        lock (_gate) return intent > _cancelledBefore;
    }

    /// <summary>用户停止：作废当前全部在飞意图。之后的新起播照常有效。</summary>
    public void CancelAll()
    {
        lock (_gate) _cancelledBefore = _issued;
    }
}
