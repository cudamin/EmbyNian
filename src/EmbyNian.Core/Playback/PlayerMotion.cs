namespace EmbyNian.Playback;

/// <summary>播放器转场的节奏与连续取样；不参与播放状态或窗口布局。</summary>
public static class PlayerMotion
{
    public const int EnterMilliseconds = 280;
    public const int EnterFadeMilliseconds = 140;
    public const int ExitMilliseconds = 190;
    public const int CoverMilliseconds = 180;
    public const double DepthScale = 1.018;
    public const double Travel = 12;

    public readonly record struct Pose(double Opacity, double Scale, double OffsetY)
    {
        public static Pose Visible => new(1, 1, 0);
        public static Pose Entering => new(0, DepthScale, Travel);
        public static Pose Leaving => new(0, DepthScale, -Travel / 2);
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
