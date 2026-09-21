namespace EmbyNian.Playback;

/// <summary>播放器转场的节奏与连续取样；不参与播放状态或窗口布局。</summary>
public static class PlayerMotion
{
    public const int EnterMilliseconds = 280;
    public const int EnterFadeMilliseconds = 140;

    /// <summary>
    /// 退场（回主页）的时长。2026-09-20 由 190 提到 240：这一趟真正在动的东西不是这一页
    /// （最后一帧的画面盖着整屏，页面那层不透明度淡不动它），而是那一帧自己的溶解，
    /// 给它时间才看得出「退去」而不是「没了」。
    /// </summary>
    public const int ExitMilliseconds = 240;

    public const int CoverMilliseconds = 180;
    public const double DepthScale = 1.018;
    public const double Travel = 12;

    /// <summary>
    /// 退场终点的缩放：<b>小于 1</b>（往后退），与进场的 <see cref="DepthScale"/> 方向相反。
    /// 从前的 1.018 是「一边淡一边朝观者涨」——那是进场的方向，搁在退场上读起来像画面反过来凑近。
    /// </summary>
    public const double ExitScale = 0.985;

    /// <summary>
    /// 退场终点的纵向位移：<b>往下</b>沉，回到进场时它上来的那一侧（进场从 +<see cref="Travel"/> 浮起落定）。
    /// 于是「从下面升起来」与「沉回下面去」是同一句话的两头。
    /// </summary>
    public const double ExitTravel = 8;

    public readonly record struct Pose(double Opacity, double Scale, double OffsetY)
    {
        public static Pose Visible => new(1, 1, 0);
        public static Pose Entering => new(0, DepthScale, Travel);
        public static Pose Leaving => new(0, ExitScale, ExitTravel);
    }

    public readonly record struct Transition(Pose From, Pose To, long StartedAt, int Duration, int FadeDuration)
    {
        public Pose At(long now)
        {
            var elapsed = Math.Max(0d, (double)now - StartedAt);
            var motion = Ease(elapsed, Duration);
            var fade = Ease(elapsed, FadeDuration);
            return new Pose(
                Lerp(From.Opacity, To.Opacity, fade),
                Lerp(From.Scale, To.Scale, motion),
                Lerp(From.OffsetY, To.OffsetY, motion));
        }
    }

    public static Transition Page(bool entering, Pose from, long now) => new(
        from,
        entering ? Pose.Visible : Pose.Leaving,
        now,
        entering ? EnterMilliseconds : ExitMilliseconds,
        entering ? EnterFadeMilliseconds : ExitMilliseconds);

    /// <summary>
    /// 退场期间「留在屏上那一帧」压到多暗：0＝还亮着，1＝已经退干净。<b>这一趟退场真正被看见的
    /// 就是它。</b>
    /// <para>
    /// 整页的 <c>Opacity</c> 淡不动画面 —— 画面是挂在 <c>VideoHost</c> 上的 SpriteVisual，不吃页面
    /// 那层不透明度（2026-09-20 实证，也正是 <c>CompositionVideoTarget.RetainDim</c> 这支旋钮存在
    /// 的原因）。所以「画面退去」只能由那条 <c>visual.Opacity</c> 逐拍画出来，而它逐拍走到哪一档
    /// 由这条曲线说了算。从前那个做法是把这一支一步拧到 0.85、再用一块不透明的退场底盖住画面淡出，
    /// 于是用户看到的其实是「一次沉黑」；现在是这一帧自己溶解，浏览页在它底下等着。
    /// </para>
    /// <para>
    /// <b>线性，不是先快后慢。</b>溶解的速率恒定读起来最干净；配一条缓出曲线会让画面在头几十毫秒里
    /// 掉掉大半、尾巴上再拖一条还读得出内容的残影。
    /// </para>
    /// </summary>
    public static double ExitDimAt(double progress) => Math.Clamp(progress, 0, 1);

    /// <summary>与 XAML CubicEase.EaseOut 同一曲线，中途换向从当下这一帧续接。</summary>
    private static double Ease(double elapsed, int duration)
    {
        if (duration <= 0) return 1;
        var remaining = 1 - Math.Clamp(elapsed / duration, 0, 1);
        return 1 - remaining * remaining * remaining;
    }

    private static double Lerp(double from, double to, double progress) =>
        progress <= 0 ? from : progress >= 1 ? to : from + (to - from) * progress;
}
