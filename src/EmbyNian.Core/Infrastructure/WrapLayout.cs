namespace EmbyNian.Infrastructure;

/// <summary>
/// 一排东西排不下就换行：给一串盒子的宽高、一行有多宽、横竖两个间距，算出每个盒子摆在哪儿、整排占多大。
/// 详情页上「媒体源／音频／字幕」那三个下拉（外壳里的 <c>WrapRow</c>）就是拿这个摆的。
/// <para>
/// 放在这里而不放在那个面板里，理由和 <see cref="CardStrip"/> 一样：值得问一遍的情形多半不是这台机器上跑一遍
/// 就见得到的 —— 一行正好差半个像素、一个比整行还宽的盒子（换行也没有更宽的一行在等着）、宽度是无限的容器
/// （放进一个横向能滚的东西里就是）、一个都不显示的空排。那三个下拉的宽度是按各自最长那条轨道名撑出来的，所以
/// 「这一次到底会不会换行」跟服务器上的轨道叫什么名字有关，屏上那一遍碰不碰得到全靠运气。
/// </para>
/// <para>
/// 量和摆共用这一份：两遍算出来的换行位置分叉的话，屏上就是「量出来两行、摆出来一行」，第二行被裁在面板外面。
/// 外壳那边剩下的只有「问每个孩子想要多大，再把答案交给 <c>Arrange</c>」。
/// </para>
/// </summary>
public static class WrapLayout
{
    /// <summary>
    /// 判「装得下」时的余量。宽度是浮点数：三个下拉正好把一行占满时，那个和与行宽差在小数位上，
    /// 少了这点余量就会白换一行。
    /// </summary>
    public const double Slack = 0.5;

    /// <summary>
    /// 把量到的宽度收成一个能用的上限：无限和 NaN 都是「没有上限」，也就是永远不换行。
    /// </summary>
    public static double Bound(double width) =>
        double.IsInfinity(width) || double.IsNaN(width) ? double.PositiveInfinity : width;

    /// <summary>
    /// 从左往右排，装不下就换行。返回每个盒子的左上角（顺序和传进来的一样）、整排占多大、摆成几行。
    /// <para>
    /// 一行里的第一个永远留在本行：它自己就比一行宽的话，换行也没有更宽的一行在等着 —— 那时候它只会被裁在
    /// 自己那一格里（框架给下拉的模板本来就以省略号收尾），而不会把后面的顶出去。
    /// </para>
    /// </summary>
    /// <param name="boxes">每个盒子想要多大。已经隐藏的不要放进来。</param>
    /// <param name="limit">一行有多宽。无限表示不换行。</param>
    /// <param name="spacing">同一行里两个盒子之间的横向间距。</param>
    /// <param name="rowSpacing">换行之后两行之间的纵向间距。</param>
    public static WrapPlan Place(
        IReadOnlyList<(double Width, double Height)> boxes, double limit, double spacing, double rowSpacing)
    {
        var bound = Bound(limit);
        var spots = new (double Left, double Top)[boxes.Count];

        double x = 0, y = 0, rowHeight = 0, widest = 0;
        var rows = boxes.Count == 0 ? 0 : 1;

        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];

            // 本行已经有人，再放下去就出界 —— 换行。空行不判，见上面那段。
            if (x > 0 && x + spacing + box.Width > bound + Slack)
            {
                y += rowHeight + rowSpacing;
                x = 0;
                rowHeight = 0;
                rows++;
            }

            var left = x > 0 ? x + spacing : 0;
            spots[i] = (left, y);

            x = left + box.Width;
            rowHeight = Math.Max(rowHeight, box.Height);
            widest = Math.Max(widest, x);
        }

        return new WrapPlan(spots, double.IsInfinity(bound) ? widest : Math.Min(widest, bound), y + rowHeight, rows);
    }
}

/// <summary>摆好之后的一排：每个盒子的左上角、整排占多大、摆成几行。</summary>
/// <param name="Spots">每个盒子的左上角，顺序和 <see cref="WrapLayout.Place"/> 收到的一样。</param>
/// <param name="Width">整排占多宽。有上限时不超过上限 —— 交回去的必须是「给我的这一格」。</param>
/// <param name="Height">整排占多高：每行取本行最高的那个，加上行间距。</param>
/// <param name="Rows">摆成几行。一个都不显示时是 0 行。</param>
public readonly record struct WrapPlan(
    IReadOnlyList<(double Left, double Top)> Spots, double Width, double Height, int Rows);
