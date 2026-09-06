namespace EmbyNian.Playback;

/// <summary>
/// 播放器正中那颗暂停/播放徽标的几何：「弄一个白色三角形方块和两个白色的长方块就可以」。
/// <para>
/// <b>这几个数算得对不对，只有在这儿才钉得住。</b>形状是「核心多边形 ＋ 一圈圆接头的描边」拼出来的 —— 圆角
/// 不由几何提供，由描边的接头提供，于是几何里一个弧都不用算。代价是「屏上那个形状最后多大」变成了一道要算的
/// 题：核心往外长半个描边宽，尖角也只长半个（圆接头）。它替掉的那两个图标字外框量出来是 122.5×122.5 和
/// 113.3×123.3，新形状必须落在同一档分量上，否则屏上就是「换了个图标，顺手大了一圈」。
/// </para>
/// <para>
/// <b>只有白的这一层。</b>从前它背后还压着一层同形、粗一档的半透明黑描边，屏上是贴着白形状的一道 5 像素灰边，
/// 理由是「白三角压在白墙上等于没画」。2026-09-05 按用户的话去掉了：「点击画面暂停和开始的图标要纯白色，去掉
/// 灰色」—— 这是他第二次要这件事（第一次是「不要黑色的圆形边框，只要白色的三角形」，那一次去掉的是底板）。
/// 代价照实记着：一帧几乎全白的画面上，这颗徽标现在会看不见。跟着一起没了的是「一个 <c>Geometry</c> 不能同时
/// 挂在两个 <c>Path</c> 上」那笔账 —— 两层同形的时候要建四个独立对象，现在两个就够。
/// </para>
/// </summary>
public static class PulseArt
{
    /// <summary>
    /// 两个形状共用的方框边长。
    /// <para>
    /// 描边最多往外长 6，核心留的余量是 13，所以这个数比几何需要的宽出一截 —— 留着不动是有意的：它就是屏上
    /// 徽标占多大，而用户看过并且认下的就是这一档。去掉那层灰描边（从前它往外长 11）不该顺手改掉大小。
    /// </para>
    /// </summary>
    public const double Box = 136;

    /// <summary>白色那层的描边粗细，也是唯一一层。圆角半径就是它的一半。</summary>
    public const double Ink = 12;

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
