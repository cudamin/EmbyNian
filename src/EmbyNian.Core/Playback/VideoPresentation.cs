namespace EmbyNian.Playback;

/// <summary>播放画面在宿主里的摆放与跳变路径；不参与播放状态或窗口布局。</summary>
public static class VideoPresentation
{
    /// <summary>
    /// SpriteVisual 的摆放：等比（或逐轴）缩放加左上角偏移，全部 DIP。交换链画笔只会把缓冲拉伸进
    /// Visual 的尺寸，所以「按哪个矩形呈现」全部由这一组量表达。
    /// </summary>
    public readonly record struct Placement(double ScaleX, double ScaleY, double Left, double Top)
    {
        public static Placement Identity => new(1, 1, 0, 0);
        public bool IsIdentity => ScaleX == 1 && ScaleY == 1 && Left == 0 && Top == 0;
    }

    /// <summary>
    /// 跳变起点：旧缓冲（与旧客户区同形）按旧客户区矩形呈现进新客户区坐标。旧矩形的屏偏移按光栅化比例
    /// 折算成 DIP——这正是跳变前屏上那一块，而不是居中或 contain 的另一个猜测。
    /// </summary>
    public static Placement ForRect(int contentWidth, int contentHeight,
        int fromLeft, int fromTop, int fromWidth, int fromHeight,
        int toLeft, int toTop, double rasterScale)
    {
        if (contentWidth <= 0 || contentHeight <= 0 || fromWidth <= 0 || fromHeight <= 0
            || !double.IsFinite(rasterScale) || rasterScale <= 0)
            return Placement.Identity;

        return new Placement(
            (double)fromWidth / contentWidth,
            (double)fromHeight / contentHeight,
            (fromLeft - toLeft) / rasterScale,
            (fromTop - toTop) / rasterScale);
    }

    /// <summary>没有矩形信息时的兜底：旧缓冲按自身比例居中放进新宿主（contain），宁可留边不拉伸。</summary>
    public static Placement Contain(int contentWidth, int contentHeight, double hostWidth, double hostHeight, double rasterScale)
    {
        if (contentWidth <= 0 || contentHeight <= 0
            || !double.IsFinite(hostWidth) || !double.IsFinite(hostHeight)
            || !double.IsFinite(rasterScale) || hostWidth <= 0 || hostHeight <= 0 || rasterScale <= 0)
            return Placement.Identity;

        var width = contentWidth / rasterScale;
        var height = contentHeight / rasterScale;
        var scale = Math.Min(hostWidth / width, hostHeight / height);
        if (!double.IsFinite(scale) || scale <= 0) return Placement.Identity;

        return new Placement(scale, scale, (hostWidth - width * scale) / 2, (hostHeight - height * scale) / 2);
    }

    /// <summary>两块摆放之间按进度插值；落点恒等时插到头就是单位摆放。</summary>
    public static Placement Between(Placement from, Placement to, double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        return new Placement(
            from.ScaleX + (to.ScaleX - from.ScaleX) * t,
            from.ScaleY + (to.ScaleY - from.ScaleY) * t,
            from.Left + (to.Left - from.Left) * t,
            from.Top + (to.Top - from.Top) * t);
    }

    /// <summary>实际缓冲是否已与宿主一致（±2px 容差）。就绪才允许单位摆放露出正片。</summary>
    public static bool Matches(int contentWidth, int contentHeight, int hostWidth, int hostHeight) =>
        contentWidth > 0 && contentHeight > 0 && hostWidth > 0 && hostHeight > 0
        && Math.Abs((long)contentWidth - hostWidth) <= 2
        && Math.Abs((long)contentHeight - hostHeight) <= 2;
}
