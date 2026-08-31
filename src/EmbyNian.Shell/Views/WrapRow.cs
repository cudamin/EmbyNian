using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 一排东西，排不下就换行 —— 详情页上「媒体源／音频／字幕」那三个下拉用的就是它。
/// <para>
/// 换掉的是一个横排 <see cref="StackPanel"/>。横排永远只有一行：这台机器的副屏是竖屏，锁了 1.6:1 之后窗口
/// 只有一千零几十像素宽，而三个下拉的宽度是按各自最长那条轨道名撑出来的（「自动 · Chinese …」这种），加起来
/// 超过一行时最右边那个就被窗口右沿切掉半截 —— 屏上看是「字幕」那一格缺了一块，不是一个能读出来的省略号。
/// </para>
/// <para>
/// 宽窗口下它和原来那个横排一模一样：孩子按自己的自然宽度从左往右排、间距 <see cref="Spacing"/>，
/// 所以「三个下拉刻意不撑满整行」那条约定没有被这次改动动过（撑满会读成表格的一行，见 DetailPage.xaml）。
/// 窄下去才换行，而不是一开始就把它们摊成三行。
/// </para>
/// <para>
/// 每个孩子都按「整行的宽度」去量，不是按无限宽量：一条特别长的轨道名于是只会被裁在自己那一格里
/// （框架给 <see cref="ComboBox"/> 的模板本来就会省略号收尾），而不会把后面两个顶出窗口。行里第一个永远
/// 留在本行 —— 它自己就比一行宽的话，换行也没有更宽的一行在等着。
/// </para>
/// <para>
/// WinUI 3 自己没有这样一个面板（<c>WrapGrid</c> 只服务列表控件，社区工具包那支 <c>WrapPanel</c> 要多引一个
/// 包），所以这里自己写一个。量和排走同一份代码（<see cref="Layout"/>），否则两遍算出来的换行位置迟早会分叉。
/// </para>
/// </summary>
public sealed class WrapRow : Panel
{
    /// <summary>同一行里两个孩子之间的横向间距。</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(WrapRow), new PropertyMetadata(0d, OnLayoutChanged));

    /// <summary>换行之后两行之间的纵向间距。</summary>
    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(WrapRow), new PropertyMetadata(0d, OnLayoutChanged));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size available) => Layout(available.Width, arrange: false);

    /// <summary>
    /// 摆完之后交回去的是「给我的这一格」，不是「我摆出来占了多大」——差别看不出来的话整排会莫名往右挪。
    /// <para>
    /// 框架算对齐偏移的时候，<see cref="HorizontalAlignment.Stretch"/> 和
    /// <see cref="HorizontalAlignment.Center"/> 走的是同一支：只要这里交回的宽度比格子窄，剩下那点空当就
    /// 被对半分到两边。三个下拉总共 816 宽、格子 923 宽，于是整排右移 54 —— 屏上就是它比上面的片名和下面的
    /// 剧情说明都往右缩进了一块，而且缩多少还跟着轨道名的长短变（剧集那一页只有两个下拉，右移 164）。
    /// </para>
    /// <para>
    /// 交回整格不影响换行位置：<see cref="Layout"/> 两遍看的都是 <c>final.Width</c>，和量的时候那个上限同一个数。
    /// </para>
    /// </summary>
    protected override Size ArrangeOverride(Size final)
    {
        Layout(final.Width, arrange: true);
        return final;
    }

    private static void OnLayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((WrapRow)sender).InvalidateMeasure();

    /// <summary>
    /// 摆一遍，顺便算出总共占多大。<paramref name="arrange"/> 为假时只量不摆（<c>MeasureOverride</c>），
    /// 为真时按量过的尺寸摆（<c>ArrangeOverride</c>）—— 两遍看的是同一段判断，换行位置因此不会两样。
    /// </summary>
    private Size Layout(double width, bool arrange)
    {
        // 宽度可以是无限（放进一个横向能滚的容器里就是），那时候永远不换行。
        var limit = double.IsInfinity(width) || double.IsNaN(width) ? double.PositiveInfinity : width;

        double x = 0, y = 0, rowHeight = 0, widest = 0;

        foreach (var child in Children)
        {
            if (child.Visibility != Visibility.Visible) continue;

            if (!arrange) child.Measure(new Size(limit, double.PositiveInfinity));

            var size = child.DesiredSize;

            // 本行已经有人，再放下去就出界 —— 换行。空行不判，见类注释最后一段。
            if (x > 0 && x + Spacing + size.Width > limit + 0.5)
            {
                y += rowHeight + RowSpacing;
                x = 0;
                rowHeight = 0;
            }

            var left = x > 0 ? x + Spacing : 0;
            if (arrange) child.Arrange(new Rect(left, y, size.Width, size.Height));

            x = left + size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
            widest = Math.Max(widest, x);
        }

        return new Size(double.IsInfinity(limit) ? widest : Math.Min(widest, limit), y + rowHeight);
    }
}
