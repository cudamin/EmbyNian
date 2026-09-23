using EmbyNian.Configuration;

namespace EmbyNian.Playback;

/// <summary>
/// 音量条的刻度 —— 音量值（0–<see cref="AudioSettings.MaxVolume"/>）与条上位置（下称「轴」）之间的换算，
/// 以及滚轮／按键在轴上的一步。
/// <para>
/// <b>为什么要有这一层（2026-09-22 用户令）。</b>「当音量值从 100 变化到 101 时，要求用户滚动鼠标滚轮的
/// 幅度比常规音量变化更大（即该区间需要更多滚动量才能触发数值变化）；同时音量条的视觉显示上，将 100 到
/// 101 这一刻度区间的显示长度拉长，使其比前后相邻区间占据更多空间；保持其他音量区间的正常滚动灵敏度和
/// 显示比例不变。」于是 100→101 这一段在轴上占 <see cref="KneeSpan"/> 个单位，其余每 1 音量仍是 1 个单位：
/// 条子总长从 130 变成 <see cref="MaximumAxis"/>（133），而 100 以上的刻度全被抬高 3 个单位 —— 这也是下面
/// 两个换算都分三段写的原因（≤100 / 100–101 / ≥101）。
/// </para>
/// <para>
/// <b>为什么要棘轮（<see cref="Step"/>）而不是让换算包办。</b>滚轮一格是 2 个轴单位，而这一段宽 4 —— 照
/// 连续换算走的话，滚一格到轴 102 就已经越过 100 与 101 的中点，四舍五入当场跳成 101，「需要更多滚动量」
/// 名存实亡。真正要的是「这一格里走满 4 个单位才让数值动一格」，于是没走满的那部分必须留在轴上：
/// <paramref name="axis"/> 允许停在两个刻度之间，这就是 <see cref="Step"/> 要带状态的理由。两个方向都按
/// 「跨过整格」判，所以 100→101 与 101→100 要的滚动量一样（各两格）。
/// </para>
/// <para>
/// 步长本来就不小于格宽的那一档不受影响：方向键一步 5 个轴单位，一步就跨过去（这一档从前一步跨 5 个音量，
/// 现在跨过 100→101 之后余下的 1 个单位正好落到 102），其余区间的手感与从前逐格相同。
/// </para>
/// </summary>
public static class VolumeScale
{
    /// <summary>「不放大」的上限。100 以上是 mpv 的软音量增益，而这一段在条上是单独一格。</summary>
    public const double Knee = 100;

    /// <summary>
    /// 100→101 在轴上占多少个单位 —— 同时就是这一格需要的滚动量相对其他格的倍数（滚轮一格 2 个单位，
    /// 所以这一档要两格）。
    /// </summary>
    public const double KneeSpan = 4;

    /// <summary>音量 → 条上位置。三段：100 以前一比一、100→101 拉开 <see cref="KneeSpan"/> 倍、之后整体平移。</summary>
    public static double Axis(double level) => level <= Knee
        ? level
        : level <= Knee + 1
            ? Knee + ((level - Knee) * KneeSpan)
            : Knee + KneeSpan + (level - Knee - 1);

    /// <summary>
    /// 条上位置 → 音量，连续。落在 100 与 101 两根刻度之间的轴值给出这两者之间的小数 —— 滑块拖到那里时
    /// 由调用方决定怎么收（见 <c>PlayerViewModel.OnVolumeAxisChanged</c> 的就近取整）。
    /// </summary>
    public static double Level(double axis) => axis <= Knee
        ? axis
        : axis <= Knee + KneeSpan
            ? Knee + ((axis - Knee) / KneeSpan)
            : Knee + 1 + (axis - Knee - KneeSpan);

    /// <summary>一格（<paramref name="level"/> → 它的下一格）在轴上有多宽：100 那一格是 4，其余是 1。</summary>
    public static double Width(double level) => Axis(level + 1) - Axis(level);

    /// <summary>条子的总长。滑杆的上限绑它，不再直接绑 <see cref="AudioSettings.MaxVolume"/>。</summary>
    public static double MaximumAxis => Axis(AudioSettings.MaxVolume);

    /// <summary>
    /// 沿轴走 <paramref name="delta"/> 之后落在哪一格、轴停在哪。返回的音量与传进来的 <paramref name="level"/>
    /// 相同，就说明这一下还没跨过整格（数值不该动，未走满的部分留在返回的 <c>Axis</c> 上，等下一次接着走）。
    /// </summary>
    public static (double Level, double Axis) Step(double level, double axis, double delta)
    {
        var moved = Math.Clamp(axis + delta, 0, MaximumAxis);
        var stepped = level;

        while (stepped < AudioSettings.MaxVolume && moved - Axis(stepped) >= Width(stepped)) stepped += 1;
        while (stepped > 0 && Axis(stepped) - moved >= Width(stepped - 1)) stepped -= 1;

        return (stepped, moved);
    }
}
