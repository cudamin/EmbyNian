namespace EmbyNian.Playback;

/// <summary>
/// 倍速轮盘 —— 集成模式倍速按钮（2026-09-25 用户令「改为竖置的滚动条滚轮，刻度居中，使用鼠标滚轮翻动
/// 或者鼠标左键长按拖拽」）的算术。轮盘是一条竖直的刻度带：快在上、慢在下（与音量条同一个心智模型：
/// 往上 = 更多），当前值停在正中；滚轮与拖拽改的都是这条带上的<b>连续位置</b>，停下时吸附到最近的刻度。
/// <para>
/// <b>位置的定义</b>：位置就是速度在刻度表（升序的倍速档位表，键盘微调的 clamp 与轮盘共用那份）里的
/// 连续下标 —— 0 是最慢、<c>count-1</c> 是最快。第 <c>i</c> 根刻度离中线的偏移见 <see cref="TickOffset"/>，
/// 位置每加一（更快一档），整条带往下走一格、更快的那根从上面转进中线。滚轮向上与拖拽向下让内容朝同一
/// 个方向走（都是「露出上面更快的那几根」），所以两个手势不会打架。
/// </para>
/// <para>
/// <b>为什么位置是连续的</b>：键盘微调（<c>speed-up/speed-down</c>，±0.1）会给出不在刻度表上的速度
/// （0.95、1.05）。轮盘要是把当前值硬吸附到最近一根刻度上，就是在对用户撒谎 —— 按钮写着 0.9 而片子在
/// 0.95。所以打开轮盘时停在两根刻度<b>之间</b>（按速度差的比例线性插值），第一次滚轮/拖拽才吸附：落在
/// 两根之间本身就是「不在任何一档上」看得见的样子。
/// </para>
/// <para>
/// 纯函数、无框架引用 —— 与 <see cref="VolumeScale"/> 同一待遇：测试工程只够得到 Core，几何与映射的
/// 判据都钉在这里（方向、间距、吸附、浓淡），页面只管把手势翻译成位置。
/// </para>
/// </summary>
public static class SpeedWheel
{
    /// <summary>相邻两根刻度的间距（逻辑像素）。轮盘自己的尺度 —— 刻度带视口的一半要容得下约两格半，
    /// 这一档在页面上转起来才像一只轮盘而不是一根列表。</summary>
    public const double TickSpacing = 44;

    /// <summary>
    /// 速度 → 轮盘位置（连续）。正好落在某根刻度上的速度给整数；落在 <c>choices[i]</c> 与
    /// <c>choices[i+1]</c> 之间的按两档之间的比例给小数。表外两侧夹回端点 —— mpv 的速度被夹在
    /// <c>speed-up/speed-down</c> 的同一对端点上，这里不会出现更外的值，夹住只是保险。
    /// </summary>
    public static double PositionFor(double speed, IReadOnlyList<double> choices)
    {
        if (choices.Count == 0) return 0;

        if (speed <= choices[0]) return 0;
        if (speed >= choices[^1]) return choices.Count - 1;

        for (var index = 0; index + 1 < choices.Count; index++)
        {
            if (speed < choices[index + 1])
                return index + (speed - choices[index]) / (choices[index + 1] - choices[index]);
        }

        return choices.Count - 1;
    }

    /// <summary>吸附：位置 → 最近的刻度下标，夹在表内。松手、以及「中线此刻指哪一档」都问它。</summary>
    public static int Snap(double position, int count) =>
        count == 0 ? 0 : Math.Clamp((int)Math.Round(position), 0, count - 1);

    /// <summary>
    /// 滚轮（或一次有向的拨动）之后的落点：从当前位置朝 <paramref name="delta"/> 的方向走<b>一根</b>刻度。
    /// <para>
    /// 不走「四舍五入再 ±1」：停在两根之间（键盘微调出的 1.05）时，朝上的一步应该落到<b>下一根</b>刻度
    /// （1.1），而不是跳过它。所以向上取 <c>floor+1</c>、向下取 <c>ceil-1</c> —— 整数位置两种算法同一个
    /// 答案（下一根/上一根），小数位置也不会吞掉近旁的那根。两端夹住：已在顶上再往上还是顶上。
    /// </para>
    /// </summary>
    public static int Stepped(double position, int count, double delta)
    {
        if (count == 0 || delta == 0) return Snap(position, count);

        var target = delta > 0 ? Math.Floor(position) + 1 : Math.Ceiling(position) - 1;
        return Math.Clamp((int)target, 0, count - 1);
    }

    /// <summary>
    /// 第 <c>index</c> 根刻度离轮盘中线的偏移（逻辑像素，正 = 在中线之下）。位置越大（越快）整条带越往下
    /// 走 —— 更快的刻度从上面转进中线。这是「快在上」唯一的一处写法，页面不许再抄一份带方向的算式。
    /// </summary>
    public static double TickOffset(int index, double position) => (position - index) * TickSpacing;

    /// <summary>
    /// 离中线 <paramref name="distance"/> 格的刻度不透明度。中线最亮，往外一路淡到没有 —— 轮盘「转到远处
    /// 去」的样子，也省掉一层面具：淡到零的刻度天然看不见，视口边缘不需要再剪。
    /// </summary>
    public static double TickOpacity(double distance) => Math.Clamp(1.0 - 0.45 * distance, 0.0, 1.0);

    /// <summary>
    /// 离中线 <paramref name="distance"/> 格的刻度缩放。只有正在用的那一根略大（1.22），出了这一格立刻
    /// 回到 1 —— 强调只给当下，不是给「离得近的」。
    /// </summary>
    public static double TickScale(double distance) => 1.0 + 0.22 * Math.Max(0.0, 1.0 - distance);
}
