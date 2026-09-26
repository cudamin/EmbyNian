namespace EmbyNian.Playback;

/// <summary>合并的窗口切换共享一次暂停归属；不能把自己的暂停当作用户原先暂停。</summary>
public sealed class HandoffPause
{
    private bool _owned;

    public bool Acquire(bool paused)
    {
        if (_owned || paused) return false;
        _owned = true;
        return true;
    }

    public bool Release(bool current, bool pending)
    {
        if (!current)
        {
            _owned = false;
            return false;
        }

        if (pending) return false;
        var resume = _owned;
        _owned = false;
        return resume;
    }
}
