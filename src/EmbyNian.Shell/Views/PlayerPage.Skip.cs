using EmbyNian.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 跳过按钮上那根倒计时条的插值机。
/// <para>
/// 数据源（<see cref="PlayerViewModel.SkipRemaining"/>）由播放位置驱动，而位置是一拍一拍来的 ——
/// 直接绑定就是把拍距原样搬上屏，「按钮上的倒计时进度条不是很顺滑」（用户令 2026-09-26）说的就是它。
/// 这一半在两拍之间以恒速插值：<see cref="SkipRemainingChanged"/> 每拍把新余值铺上屏当锚点、顺带量出
/// 实测流速（暂停时位置不动、条跟着停；倍速时位置走得快、条跟着快），计时器以 32ms 一拍推着值走。
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>插值节拍。与遮罩揭幕计时同一档（<c>CreateRevealTimer</c>）。</summary>
    private const int SkipCountdownTickMilliseconds = 32;

    private DispatcherQueueTimer? _skipCountdownTimer;

    /// <summary>最近一次校准的余值与时刻 —— 插值的锚点。</summary>
    private double _skipCountdownValue;

    private long _skipCountdownAt;

    /// <summary>
    /// 实测流速（余值/毫秒，向下为正）。表刚开、还没量出第二段时用满速档：offer 一共立
    /// <see cref="SkipCoordinator.PromptSeconds"/> 秒，余值 1→0。
    /// </summary>
    private double _skipCountdownRate = 1.0 / SkipCoordinator.PromptSeconds / 1000.0;

    /// <summary>暂停把墙钟甩在身后 —— 恢复播放的那一拍要重新立锚，这一位记着「现在暂停着」。</summary>
    private bool _skipCountdownPaused;

    /// <summary>SkipOffered 变了：立起来开表，收下去停表。</summary>
    private void SkipOfferChanged()
    {
        if (!ViewModel.SkipOffered)
        {
            StopSkipCountdown();
            return;
        }

        // 关动效的那一档不插值：绑定量自带的阶梯就是它要的「不动画」。
        if (!HomeMotion.AnimationsEnabled) return;

        _skipCountdownValue = ViewModel.SkipRemaining;
        _skipCountdownAt = Now;
        _skipCountdownRate = 1.0 / SkipCoordinator.PromptSeconds / 1000.0;
        _skipCountdownPaused = false;
        _skipCountdownTimer ??= CreateSkipCountdownTimer();
        _skipCountdownTimer.Start();
    }

    /// <summary>
    /// SkipRemaining 又采了一拍：新值当场铺上屏（免得等下一个 32ms 才对齐），流速照上一段量。
    /// 倒流的不学 —— seek 回片头重出 offer 时余值从 0.x 跳回 1，那不是流速，是重新开始。
    /// </summary>
    private void SkipRemainingChanged()
    {
        var value = ViewModel.SkipRemaining;
        var at = Now;
        if (_skipCountdownTimer is { IsRunning: true } && at > _skipCountdownAt)
        {
            var rate = (_skipCountdownValue - value) / (at - _skipCountdownAt);
            if (rate > 0) _skipCountdownRate = rate;
        }

        _skipCountdownValue = value;
        _skipCountdownAt = at;
        SkipCountdown.Value = value;
    }

    private void StopSkipCountdown() => _skipCountdownTimer?.Stop();

    /// <summary>插值读数，at 任意给 —— 探针拿合成时刻来问的就是它。</summary>
    internal double SkipCountdownShown(long at) =>
        _skipCountdownValue - _skipCountdownRate * (at - _skipCountdownAt);

    private DispatcherQueueTimer CreateSkipCountdownTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(SkipCountdownTickMilliseconds);
        timer.Tick += (_, _) =>
        {
            if (!ViewModel.SkipOffered)
            {
                StopSkipCountdown();
                return;
            }

            // 倒计时以播放位置计（SkipCoordinator 的注释说的同一句话）：位置不动条也不动，
            // 恢复的那一拍从屏上此刻的值重新立锚接着走 —— 暂停的墙钟不算进流速里。
            if (ViewModel.Paused)
            {
                _skipCountdownPaused = true;
                return;
            }

            if (_skipCountdownPaused)
            {
                _skipCountdownPaused = false;
                _skipCountdownValue = SkipCountdown.Value;
                _skipCountdownAt = Now;
            }

            SkipCountdown.Value = Math.Clamp(SkipCountdownShown(Now), 0, 1);
        };
        return timer;
    }
}
