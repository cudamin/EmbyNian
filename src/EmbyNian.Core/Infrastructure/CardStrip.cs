namespace EmbyNian.Infrastructure;

/// <summary>
/// 一条横向卡片带的规则：翻一页翻多远、翻过去落在哪、哪个箭头该在屏上、要让第几张卡露出来得滚到哪儿。
/// 主页每个分区和详情页的 单集／全部剧季／演职人员／更多类似 都是那条带（<c>ShelfStrip</c>），用的就是这几条。
/// <para>
/// 放在这里而不放在外壳里，和 <see cref="ScreenPlacement"/> 同一个理由：这几条规则的输入只有四个数
/// —— 视口多宽、卡片间距多大、现在滚到哪儿、还有多少可滚 —— 而值得问一遍的情形多半不是这台机器上跑一遍
/// 就见得到的：一张比整个视口还宽的卡（窄窗口下的 16:9 剧照）、卡片还没量出来的那一拍、正好停在带尾的
/// 那一下、可滚动的宽度是 0 的那条空带。外壳那边剩下的只有「把量到的数交给它，再把答案交给 ScrollView」。
/// </para>
/// <para>
/// 从前这五条是 <c>ShelfStrip</c> 上的五个 <c>internal static</c>，而外壳那一层一项单元测试都没有，于是它们
/// 由 <c>--self-check</c> 拿几个写死的数走一遍。搬过来之后那几个数在测试里（<c>CardStripTests</c>），自检
/// 只留屏上才问得出的那几件：那份标记能不能解析、间隔有没有真进到布局、还没量过时两个箭头收着没有、点下去
/// 有没有把要到的位置记下来。
/// </para>
/// </summary>
public static class CardStrip
{
    /// <summary>一像素的余量。滚动位置是浮点数，「到头了」不该指望它正好等于可滚动的宽度。</summary>
    public const double Edge = 1;

    /// <summary>
    /// 哪个箭头该在屏上。内容一屏放得下就两个都不要 —— 那时没有「后面」，一个点了没反应的按钮比没有按钮
    /// 更难解释。到头的那一侧也收起来。
    /// </summary>
    /// <param name="offset">现在滚到哪儿。</param>
    /// <param name="scrollable">还能滚多远（内容宽减视口宽）。</param>
    /// <param name="hover">指针在带上没有 —— 箭头是悬停才出现的，同卡片上那排按钮。</param>
    public static (bool Prev, bool Next) ArrowsFor(double offset, double scrollable, bool hover) =>
        !hover || scrollable <= Edge
            ? (false, false)
            : (offset > Edge, offset < scrollable - Edge);

    /// <summary>
    /// 一页翻多远。按卡片间距的整数倍，所以翻完之后卡片仍然是左边沿对齐的 —— 按视口宽度直接翻会把某张卡
    /// 切成两半留在边上，下一次翻页把这个偏差累积下去。
    /// <para>
    /// 一屏放得下几张：<c>(视口 + 间隔) / 间距</c> 向下取整，加一个间隔是因为最后一张卡后面不需要间隔。
    /// 至少一张，否则一张比视口还宽的卡（窗口很窄时的 16:9 剧照）会让步长变成 0，箭头点不动。量不到卡片
    /// 宽度时退回视口的九成，留一成重叠好让人知道是同一条带。
    /// </para>
    /// </summary>
    /// <param name="viewport">带子看得见的那一段有多宽。0 是还没量过。</param>
    /// <param name="pitch">卡片间距：卡片宽加一个间隔。0 是还没量到卡片。</param>
    /// <param name="spacing">卡片之间的间隔。</param>
    public static double StepFor(double viewport, double pitch, double spacing)
    {
        if (viewport <= 0) return 0;
        if (pitch <= 0) return viewport * 0.9;

        var perPage = Math.Max(1, Math.Floor((viewport + spacing) / pitch));
        return perPage * pitch;
    }

    /// <summary>翻过去落在哪里。两头夹住，所以最后一页停在末尾而不是越过去。</summary>
    /// <param name="direction">-1 往前、1 往后。</param>
    /// <param name="step"><see cref="StepFor"/> 给的那个步长。</param>
    public static double TargetFor(double offset, int direction, double step, double scrollable) =>
        scrollable <= 0 ? 0 : Math.Clamp(offset + (direction * step), 0, scrollable);

    /// <summary>
    /// 要让第 <paramref name="index"/> 张卡露出来，带该停在哪里 —— 单集页的「更多来自」要开在正在看的那一
    /// 集上，而不是开在第一集上然后让人自己往后翻。
    /// <para>
    /// 落在第一屏里就不动（返回 0）：第二集的页面开在带子的开头本来就看得见它，为了把它顶到左边沿而滚一
    /// 段，只是把它前面的一集推出屏幕。再往后的就对齐到左边沿，末尾照旧夹住，所以最后一集的页面停在带尾而
    /// 不是越过去留一片空白。
    /// </para>
    /// </summary>
    public static double OffsetFor(int index, double pitch, double viewport, double scrollable)
    {
        if (index <= 0 || pitch <= 0 || scrollable <= 0) return 0;

        var left = index * pitch;
        return left + pitch <= viewport ? 0 : Math.Clamp(left, 0, scrollable);
    }

    /// <summary>
    /// 要让第 <paramref name="index"/> 张卡整张露出来，这条带该滚到哪儿；已经整张露着就返回 -1，也就是「别动」。
    /// <para>
    /// 「点击主页继续观看、媒体库、最近添加的封面之后会先跳转到页面下方，然后才会进入页面」说的就是这件事以前
    /// 是怎么做的：焦点一落到卡片上就 <c>StartBringIntoView</c>，而那个请求会一路往上冒到主页那个<em>竖着</em>
    /// 滚的 <c>ScrollView</c>，被读成「把这张卡的上沿对到视口的上沿」—— 整页往下滑一大段，滑完才轮到导航。所以
    /// 现在这件事在这条带自己的滚动视图里做完，一句请求都不往外发：横着露出一张卡本来就与页面无关。
    /// </para>
    /// <para>
    /// 已经露着就不动是这条规则的一半：鼠标按下去的那一刻卡片就拿到了焦点（<c>Click</c> 是松手才发的），而人按
    /// 的那张卡当然看得见 —— 那一档要是还挪一下，就还是原来那个样子，只是挪的方向变了。露不全的那一档把它的左
    /// 边沿对到视口的左边沿，和卡片的排布同一个节奏，不留半张卡在边上（末尾照旧夹住）。
    /// </para>
    /// </summary>
    /// <param name="pitch">卡片间距：卡片宽加一个间隔，外壳那边由 <c>ShelfStrip.Measure</c> 量出来。</param>
    /// <param name="spacing">卡片之间的间隔 —— 卡片自己有多宽是 <paramref name="pitch"/> 减掉它。</param>
    public static double RevealFor(
        int index, double pitch, double spacing, double offset, double viewport, double scrollable)
    {
        if (index < 0 || pitch <= 0 || viewport <= 0 || scrollable <= 0) return -1;

        var left = index * pitch;
        var right = left + Math.Max(0, pitch - spacing);

        if (left >= offset - Edge && right <= offset + viewport + Edge) return -1;

        return Math.Clamp(left, 0, scrollable);
    }
}
