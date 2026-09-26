namespace EmbyNian.Emby;

/// <summary>轻量刷新只能保留已经完成一次载入的轮播。</summary>
public sealed class HomeRefresh
{
    private bool _slidesLoaded;
    private int _version;

    public bool NeedsSlides(bool requested) => requested || !_slidesLoaded;

    public int Begin() => ++_version;

    public void Complete(int version, bool includedSlides)
    {
        if (version == _version && includedSlides) _slidesLoaded = true;
    }

    public void Invalidate()
    {
        _version++;
        _slidesLoaded = false;
    }
}
