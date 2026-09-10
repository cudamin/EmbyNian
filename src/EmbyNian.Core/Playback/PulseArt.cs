namespace EmbyNian.Playback;

/// <summary>
/// 轮廓上的一步：从当前点走到 (<see cref="X"/>, <see cref="Y"/>)。<see cref="Arc"/> 为假走直线；为真走半径
/// <see cref="Radius"/> 的圆弧，且永远是<b>顺时针的半圆弧（小弧）</b>。
/// <para>
/// 「顺时针」是和数据的一个约定：每条轮廓的角点序都必须在屏幕坐标（y 朝下）里顺时针，弧才往外鼓 —— 点序反了弧就往里
/// 凹，圆角变成缺口。这个约定没有东西在运行时拦得住，只有 <see cref="PulseArt.Round"/> 的生产端和几何那几条测试
/// （PlaybackTests）两头钉着。
/// </para>
/// </summary>
public sealed record PulseStep(bool Arc, double X, double Y, double Radius);

/// <summary>
/// 一条闭合轮廓：<see cref="Start"/> 起，一圈 <see cref="Steps"/>，最后一步落回起点。屏幕上画的就是它。
/// <para>
/// <see cref="Corners"/> 是没圆角前的那个多边形，也就是这个形状「认下的那一档」外框 —— 尺寸和居中都从它量
/// （<see cref="PulseArt.Bounds"/> / <see cref="PulseArt.Centre"/>）。圆角是往多边形<b>里</b>切的，画出来的形状只会
/// 落在这个框内，不会顶出去；所以拿多边形当外框，是把「用户要多大」和「圆角怎么软」分开的最省事的分法 —— 前者
/// 一组整数、一条测试钉死，后者是渲染细节。
/// </para>
/// </summary>
public sealed record PulseFigure(
    (double X, double Y) Start,
    IReadOnlyList<PulseStep> Steps,
    IReadOnlyList<(double X, double Y)> Corners);

/// <summary>
/// 播放器正中那颗暂停/播放徽标的几何。
/// <para>
/// <b>这一代把圆角直接画进轮廓里。</b>上一代是「核心多边形 ＋ 一圈圆接头的描边」：圆角不由几何出，由描边的圆接头出，
/// 半径被钉死在半个描边宽（6），于是 136 的方框里两条直挺挺的竖条、一颗尖角三角，用户看着是「太丑了」——
/// 「播放页面暂停和开始的图标太丑了，你换一个」（2026-09-06）是他对这副形状的第三次发言（前两次：
/// 「不要黑色的圆形边框，只要白色的三角形」、「纯白色，去掉灰色」）。这一代换成成品轮廓：暂停是两条<b>胶囊</b>竖条
/// （圆角半径＝条宽的一半，22），播放是底角圆 12、尖角圆 22 的三角 —— 尖角最锐，就给它最大的圆。
/// </para>
/// <para>
/// <b>外框尺寸一口价没动</b>：暂停 122×122、播放 112×122、方框 <see cref="Box"/>＝136，条宽 44、间距 34 也都在
/// —— 用户认下的是这一档分量，换形状不该顺手换大小（这条账上一代替掉图标字时就立过，PlaybackTests 照旧钉着）。
/// 两个形状都要落在方框正中，量的是各自的角点多边形（见 <see cref="PulseFigure"/>）：暂停两条对称、天生居中；
/// 播放三角必须对称摆在 x＝68 上（底边两点同 x、尖角在 68 高），不然屏上就是偏了一边。
/// </para>
/// <para>
/// <b>只有白的这一层。</b>底板和灰描边都去掉了（「不要黑色的圆形边框」「纯白色，去掉灰色」），代价照实记着：
/// 一帧几乎全白的画面上，这颗徽标看不见。
/// </para>
/// </summary>
public static class PulseArt
{
    /// <summary>两个形状共用的方框边长，也是 PulseArtBox 那个 Canvas 的宽高和居中的基准。</summary>
    public const double Box = 136;

    /// <summary>
    /// 暂停：两条胶囊竖条。条 44 宽、122 高，间距 34，四角圆 22（＝条宽的一半，胶囊的定义）。
    /// 两条合起来外框 122×122，和上一代描边出来的成品同一个分量。
    /// </summary>
    public static IReadOnlyList<PulseFigure> Pause { get; } =
    [
        Round([(7, 7), (51, 7), (51, 129), (7, 129)], [22, 22, 22, 22]),
        Round([(85, 7), (129, 7), (129, 129), (85, 129)], [22, 22, 22, 22]),
    ];

    /// <summary>
    /// 播放：尖角朝右的等腰三角形，112 宽、122 高，摆在方框正中（底边 x＝12、尖角 x＝124，都对称落在 x＝68 两侧；
    /// 尖角在 68 高）。底角圆 12，尖角圆 22 —— 尖角最锐，给它最大的圆。
    /// </summary>
    public static IReadOnlyList<PulseFigure> Play { get; } =
    [
        Round([(12, 7), (124, 68), (12, 129)], [12, 22, 12]),
    ];

    /// <summary>
    /// 形状的外框 —— 量的是没圆角前的角点多边形（<see cref="PulseFigure.Corners"/>），也就是「用户认下的那一档」。
    /// 圆角只往里切，画出来的轮廓只会落在这个框内。
    /// </summary>
    public static (double Left, double Top, double Right, double Bottom) Bounds(IReadOnlyList<PulseFigure> figures)
    {
        var left = double.MaxValue;
        var top = double.MaxValue;
        var right = double.MinValue;
        var bottom = double.MinValue;

        foreach (var figure in figures)
        {
            foreach (var (x, y) in figure.Corners)
            {
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return figures.Count == 0 ? (0, 0, 0, 0) : (left, top, right, bottom);
    }

    /// <summary>角点多边形外框的中心。两个形状都必须落在方框正中，否则徽标不在画面正中。</summary>
    public static (double X, double Y) Centre(IReadOnlyList<PulseFigure> figures)
    {
        var (left, top, right, bottom) = Bounds(figures);
        return ((left + right) / 2, (top + bottom) / 2);
    }

    /// <summary>
    /// 把多边形的每个角换成「两个切点 ＋ 一段圆弧」：切点在角的两条边上、离角点 t ＝ r / tan(θ/2) 处，
    /// 两切点之间是一段与两边都相切的半径 r 的弧。输出从 0 号角的出切点起，每条边、每个角恰好各走一次，
    /// 最后一步落回起点 —— 拿去就是一条闭合轮廓。角点多边形原样留在 <see cref="PulseFigure.Corners"/> 里当外框。
    /// <para>
    /// t 夹在两条边长的一半里：上面两处的数据碰不到夹取，这一笔是留给以后把数据改小的人的 —— 边太短时圆角
    /// 退成半个边长的弧，而不是两边切点互相越过、轮廓打结。
    /// </para>
    /// </summary>
    private static PulseFigure Round((double X, double Y)[] points, double[] radii)
    {
        var count = points.Length;
        var entry = new (double X, double Y)[count];
        var exit = new (double X, double Y)[count];

        for (var i = 0; i < count; i++)
        {
            var radius = radii[i];
            var previous = points[(i + count - 1) % count];
            var corner = points[i];
            var next = points[(i + 1) % count];

            var inX = corner.X - previous.X;
            var inY = corner.Y - previous.Y;
            var outX = next.X - corner.X;
            var outY = next.Y - corner.Y;
            var inLength = Math.Sqrt(inX * inX + inY * inY);
            var outLength = Math.Sqrt(outX * outX + outY * outY);

            if (radius <= 0 || inLength < 1e-9 || outLength < 1e-9)
            {
                entry[i] = exit[i] = corner;
                continue;
            }

            // 弦向量点积给出夹角 θ；t 是切点离角点的距离，圆角小的角切得远、尖角切得近。
            var cosine = Math.Clamp((inX * outX + inY * outY) / (inLength * outLength), -1, 1);
            var t = Math.Min(radius / Math.Tan(Math.Acos(cosine) / 2), Math.Min(inLength, outLength) / 2);

            entry[i] = (corner.X - inX / inLength * t, corner.Y - inY / inLength * t);
            exit[i] = (corner.X + outX / outLength * t, corner.Y + outY / outLength * t);
        }

        var steps = new List<PulseStep>(count * 2);
        for (var i = 1; i <= count; i++)
        {
            var index = i % count;
            steps.Add(new PulseStep(false, entry[index].X, entry[index].Y, 0));
            if (radii[index] > 0)
                steps.Add(new PulseStep(true, exit[index].X, exit[index].Y, radii[index]));
        }

        return new PulseFigure(exit[0], steps, points);
    }
}
