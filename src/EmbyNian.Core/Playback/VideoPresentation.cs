namespace EmbyNian.Playback;

/// <summary>播放画面在宿主里的摆放与跳变路径；不参与播放状态或窗口布局。</summary>
public static class VideoPresentation
{
    /// <summary>屏幕物理像素中的呈现矩形，不随交换链缓冲尺寸变化。</summary>
    public readonly record struct Rect(double Left, double Top, double Width, double Height);

    public readonly record struct Transition(Rect From, Rect To, double StartedAt, double Duration)
    {
        public bool Completed(double now) => now - StartedAt >= Duration;

        public Rect At(double now)
        {
            var progress = Duration <= 0 ? 1 : Math.Clamp((now - StartedAt) / Duration, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            return new Rect(
                From.Left + (To.Left - From.Left) * eased,
                From.Top + (To.Top - From.Top) * eased,
                From.Width + (To.Width - From.Width) * eased,
                From.Height + (To.Height - From.Height) * eased);
        }
    }

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

    /// <summary>
    /// 一张<b>只知道形状</b>的画面该缩放多少才放进宿主（保留态专用；正片那条路走
    /// <see cref="ShouldFill"/> 与 <see cref="ForFrame"/>）。<paramref name="fill"/> 为假按 contain
    /// （整幅都在，放不下的方向留边），为真按 cover（铺满，超出的方向裁掉）。两者都不改比例 ——
    /// 差别只在「宁可留边」还是「宁可裁掉」。
    /// <para>
    /// <b>退场那一趟要的是 cover（2026-09-20 用户令「重新设计集成模式下退出播放界面返回主页的动画」）。</b>
    /// 那一刻窗口正从播放几何跳成浏览几何，而这一帧已经交还了缓冲、只剩「它本来是什么形状」这一件事。
    /// 按 contain 摆，它会在窗口变形的同时当场缩成一条信匣（四周一圈近黑）—— 观感是「画面缩了」；
    /// 铺满则是「同一幅满屏的画」，接着整幅一起溶解，浏览页在它底下浮现 —— 观感才是「画面退去」。
    /// 留边那一档仍然是留帧的默认摆法（换集期间的留帧用它）。
    /// </para>
    /// <para>形状或宿主说不上来时返回 0，调用方照旧早退 —— 不猜、也不摆。</para>
    /// </summary>
    public static double FitScale(double shapeWidth, double shapeHeight,
        double hostWidth, double hostHeight, bool fill)
    {
        if (!double.IsFinite(shapeWidth) || !double.IsFinite(shapeHeight)
            || !double.IsFinite(hostWidth) || !double.IsFinite(hostHeight)
            || shapeWidth <= 0 || shapeHeight <= 0 || hostWidth <= 0 || hostHeight <= 0)
            return 0;

        var scale = fill
            ? Math.Max(hostWidth / shapeWidth, hostHeight / shapeHeight)
            : Math.Min(hostWidth / shapeWidth, hostHeight / shapeHeight);
        return double.IsFinite(scale) && scale > 0 ? scale : 0;
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

    /// <summary>
    /// 把「这一帧该占的矩形」翻译成 SpriteVisual 上的那组量：Visual 的尺寸恒为
    /// <c>缓冲 ÷ 光栅化比例</c>（DIP），铺满矩形所需的缩放是<b>两个物理像素长度之比</b>
    /// （与 raster 无关，别在这里多除一次），落点按光栅化比例折算成 DIP。
    /// <para>
    /// 矩形以<b>宿主客户区左上角为原点</b>（物理像素），所以这里不做任何原点减法 ——
    /// 绝对屏幕坐标只在两个矩形的差里出现过，那种地方基准自己就抵消了。
    /// </para>
    /// <para>
    /// 这是呈现的唯一出口。<see cref="ForRect"/> 那种「旧矩形起手」的算法只是它在跳变起点上的
    /// 一个特例，而矩形本身才是跨缓冲、跨 DPI、跨窗口原点都不变的那份事实 —— 缓冲换了只改
    /// 清晰度（Width/Height 重新折算），矩形不动，画面就不会跳（2026-09-20）。
    /// </para>
    /// </summary>
    public static Placement ForFrame(int contentWidth, int contentHeight, Rect frame, double rasterScale)
    {
        if (contentWidth <= 0 || contentHeight <= 0
            || !double.IsFinite(frame.Width) || !double.IsFinite(frame.Height)
            || frame.Width <= 0 || frame.Height <= 0
            || !double.IsFinite(frame.Left) || !double.IsFinite(frame.Top)
            || !double.IsFinite(rasterScale) || rasterScale <= 0)
            return Placement.Identity;

        return new Placement(
            frame.Width / contentWidth,
            frame.Height / contentHeight,
            frame.Left / rasterScale,
            frame.Top / rasterScale);
    }

    /// <summary>目标物理矩形只折算一次 DPI；缓冲尺寸不参与 Visual 的几何。</summary>
    public static Rect ToDips(Rect frame, double rasterScale)
    {
        if (!double.IsFinite(rasterScale) || rasterScale <= 0
            || !double.IsFinite(frame.Left) || !double.IsFinite(frame.Top)
            || !double.IsFinite(frame.Width) || !double.IsFinite(frame.Height)
            || frame.Width <= 0 || frame.Height <= 0)
            return default;

        return new Rect(frame.Left / rasterScale, frame.Top / rasterScale,
            frame.Width / rasterScale, frame.Height / rasterScale);
    }

    /// <summary>缓冲和布局必须同时追上最终客户区，不能只凭缓冲换新就交回布局。</summary>
    public static bool ResizeSettled(int contentWidth, int contentHeight,
        int layoutWidth, int layoutHeight, int targetWidth, int targetHeight) =>
        Matches(contentWidth, contentHeight, targetWidth, targetHeight)
        && Matches(layoutWidth, layoutHeight, targetWidth, targetHeight);

    /// <summary>实际缓冲是否已与宿主一致（±2px 容差）。就绪才允许单位摆放露出正片。</summary>
    public static bool Matches(int contentWidth, int contentHeight, int hostWidth, int hostHeight) =>
        contentWidth > 0 && contentHeight > 0 && hostWidth > 0 && hostHeight > 0
        && Math.Abs((long)contentWidth - hostWidth) <= 2
        && Math.Abs((long)contentHeight - hostHeight) <= 2;

    /// <summary>
    /// 这一帧该不该<b>铺满宿主</b>（而不是按自身比例 contain 进去、留下黑边）。
    /// <para>
    /// 三种情形铺满：缓冲与宿主一致（<see cref="Matches"/>，正片就绪）；宿主要了新尺寸、缓冲还在追赶
    /// 的这一段（<paramref name="resizePending"/>）；以及缓冲尺寸还说不上来时。
    /// </para>
    /// <para>
    /// <b>中间那条是 2026-09-20 为「拖动窗口边缘闪烁」补的。</b>拖动时每一拍 <c>WM_SIZE</c> 都会让
    /// 宿主尺寸跑到缓冲前面，而 mpv 换缓冲要 110~150ms；这中间若掉进 contain，画面就会上下缩出黑边、
    /// 下一拍缓冲追上了再铺满 —— 一拍一个样，用户看到的就是闪。而拖动这一路窗口本来就被
    /// <c>WM_SIZING</c> 按画面比例锁着，窗口形状就是画面形状，画面本就该铺满它。所以「正在追赶」
    /// 也要铺满，缓冲到位只是清晰度变好，形状一帧都不变。
    /// </para>
    /// </summary>
    public static bool ShouldFill(int contentWidth, int contentHeight,
        int hostWidth, int hostHeight, bool resizePending) =>
        resizePending || Matches(contentWidth, contentHeight, hostWidth, hostHeight);
}
