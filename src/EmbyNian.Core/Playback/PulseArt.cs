namespace EmbyNian.Playback;

/// <summary>
/// 播放器正中那颗暂停/播放徽标的几何：「弄一个白色三角形方块和两个白色的长方块就可以」。
/// <para>
/// 为什么这些数在这儿而不在标记里，有两个理由，而第二个是被撞出来的。
/// </para>
/// <para>
/// 一、<b>这几个数算得对不对，只有在这儿才钉得住。</b>形状是「核心多边形 ＋ 一圈圆接头的描边」拼出来的 —— 圆角
/// 不由几何提供，由描边的接头提供，于是几何里一个弧都不用算。代价是「屏上那个形状最后多大」变成了一道要算的
/// 题：核心往外长半个描边宽，尖角也只长半个（圆接头）。它替掉的那两个图标字外框量出来是 122.5×122.5 和
/// 113.3×123.3，新形状必须落在同一档分量上，否则屏上就是「换了个图标，顺手大了一圈」。
/// </para>
/// <para>
/// 二、<b>WinUI 的一个 <c>Geometry</c> 不能同时挂在两个 <c>Path</c> 上</b>（第二次赋值抛
/// <c>ArgumentException: Value does not fall within the expected range</c>，也就是那个「这个对象已经有父级了」的
/// E_INVALIDARG）。而这颗徽标正是两层同形：白的那层，和它背后粗一点的深色描边。所以四个 <c>PathGeometry</c> 必须
/// 是四个独立对象 —— 要么把同一串坐标在标记里抄四遍，要么像现在这样只写一份、由页面照着建四个。
/// </para>
/// </summary>
public static class PulseArt
{
    /// <summary>两个形状共用的方框边长。两层描边最粗的那一档往外长 11，所以核心必须留够 11 的余量。</summary>
    public const double Box = 136;

    /// <summary>白色那层的描边粗细。圆角半径就是它的一半。</summary>
    public const double Ink = 12;

    /// <summary>
    /// 背后那层深色描边的粗细。比 <see cref="Ink"/> 粗，差值的一半就是屏上看得见的那道边（现在是 5 像素）。
    /// 它存在的理由一句话：白三角压在白墙上等于没画。
    /// </summary>
    public const double Rim = 22;

    /// <summary>
    /// 暂停：两条竖条。核心各 32 宽、间距 46、高 110；描边 <see cref="Ink"/> 之后成品是两条 44 宽、间距 34。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(double X, double Y)>> Pause { get; } =
    [
        [(13, 13), (45, 13), (45, 123), (13, 123)],
        [(91, 13), (123, 13), (123, 123), (91, 123)]
    ];

    /// <summary>播放：一个等腰三角形，尖角朝右。核心 100×110，描边之后成品 112×122。</summary>
    public static IReadOnlyList<IReadOnlyList<(double X, double Y)>> Play { get; } =
    [
        [(18, 13), (118, 68), (18, 123)]
    ];

    /// <summary>核心多边形的外框（还没描边）。</summary>
    public static (double Left, double Top, double Right, double Bottom) Bounds(
        IReadOnlyList<IReadOnlyList<(double X, double Y)>> figures)
    {
        var left = double.MaxValue;
        var top = double.MaxValue;
        var right = double.MinValue;
        var bottom = double.MinValue;

        foreach (var figure in figures)
        {
            foreach (var (x, y) in figure)
            {
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return figures.Count == 0 ? (0, 0, 0, 0) : (left, top, right, bottom);
    }

    /// <summary>
    /// 描上 <paramref name="stroke"/> 粗的一圈之后，屏上那个形状多大。
    /// <para>
    /// 描边是压在路径上画的，所以里外各长半个宽度；圆接头让尖角也只长半个（换成 Miter 接头，那个 55° 的尖角会
    /// 长出两倍多，成品就不是 112 宽而是接近 130 —— 这正是这个函数要拦住的那种意外）。
    /// </para>
    /// </summary>
    public static (double Width, double Height) Stroked(
        IReadOnlyList<IReadOnlyList<(double X, double Y)>> figures,
        double stroke)
    {
        var (left, top, right, bottom) = Bounds(figures);
        return (right - left + stroke, bottom - top + stroke);
    }

    /// <summary>核心多边形的中心。两个形状都必须落在方框正中，否则两层描边错开、徽标也不在画面正中。</summary>
    public static (double X, double Y) Centre(IReadOnlyList<IReadOnlyList<(double X, double Y)>> figures)
    {
        var (left, top, right, bottom) = Bounds(figures);
        return ((left + right) / 2, (top + bottom) / 2);
    }
}
