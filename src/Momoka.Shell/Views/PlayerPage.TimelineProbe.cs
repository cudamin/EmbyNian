using System.Globalization;
using Momoka.Playback;
using Momoka.Theming;

namespace Momoka.Shell.Views;

public sealed partial class PlayerPage
{
    internal async Task ProbeLiveTimelineAsync(IPlaybackHandle handle, string directory, Action<string> write,
        CancellationToken token)
    {
        if (handle is not IPlayerControl control || !PlayerMotionProbe.IsRequested(Environment.GetCommandLineArgs()))
            throw new InvalidOperationException("真实时间轴诊断只允许隔离本地播放探针");
        var oldStatus = ViewModel.Status;
        var oldMarks = ViewModel.ChapterMarks;
        var oldTheme = ThemeHost.Current;
        var wasPaused = control.Status.Paused;
        var wasPosition = control.Status.Position;
        ViewModel.AttachTimelineProbe(handle);
        Hold(true, ChromeHold.Shot);
        void Apply(PlayerStatus status) => DispatcherQueue.TryEnqueue(() => ViewModel.ApplyTimelineProbeStatus(status));
        control.StatusChanged += Apply;
        try
        {
            ViewModel.ApplyTimelineProbeStatus(control.Status);
            var script = Path.Combine(directory, "timeline-probe-chapters.lua");
            await File.WriteAllTextAsync(script,
                "mp.set_property_native('chapter-list', {{time=0,title='片头'},{time=4,title='正片'},{time=38,title='片尾'}})", token);
            if (!await control.CommandAsync(["load-script", script], token))
                throw new InvalidOperationException("本地章节诊断脚本未被接受");
            await handle.SetPropertyAsync("cache", "yes", token);
            await control.CommandAsync(["seek", "10", "absolute+exact"], token);
            await handle.SetPropertyAsync("pause", true, token);
            await Wait(() => control.Status.Paused && Math.Abs(control.Status.Position - 10) < 0.15, "本地暂停落在10秒");
            ViewModel.ApplyTimelineProbeStatus(control.Status);
            await Wait(() => ViewModel.TimelineChapters.Chapters.Count == 3, "mpv章节通知到达真实时间轴");
            Render();
            UpdateLayout();

            foreach (var theme in UiThemes.All)
            {
                ThemeHost.Apply(theme);
                ViewModel.ApplyTimelineProbeStatus(control.Status);
                GrowTimeline(1);
                UpdateLayout();
                await Task.Delay(160, token);
                await CompositionPlaybackProbe.SaveScreenAsync(_window!, Path.Combine(directory, $"timeline-screen-{theme.Id}.png"));
            }

            foreach (var paused in new[] { false, true })
            {
                await handle.SetPropertyAsync("pause", paused, token);
                await Wait(() => control.Status.Paused == paused, "拖动起点暂停态同步");
                ViewModel.ApplyTimelineProbeStatus(control.Status);
                if (!ViewModel.BeginTimelineDrag()) throw new InvalidOperationException("拖动开始失败");
                ViewModel.MoveTimeline(0.4);
                await ViewModel.EndTimelineDragAsync();
                await Wait(() => control.Status.Paused == paused && Math.Abs(control.Status.Fraction - 0.4) < 0.02,
                    $"拖动准确落点且恢复暂停={paused}");
            }
            await handle.SetPropertyAsync("pause", true, token);
            await Wait(() => control.Status.Paused, "章节命中前暂停");
            ViewModel.ApplyTimelineProbeStatus(control.Status);
            var chapterX = TimelineScale.XAt(4, SeekTrack.ActualWidth, control.Status.Duration);
            UpdateTimelineHover(chapterX + 3, 1);
            if (_hoveredChapter != 1) throw new InvalidOperationException("章节二维命中失效");
            if (!PressTimelineChapter(chapterX + 3, 1)) throw new InvalidOperationException("章节点击失效");
            await Wait(() => Math.Abs(control.Status.Position - 4) < 0.15, "真实mpv跳到吸附章节");
            if (!control.Status.Paused) throw new InvalidOperationException("章节点击意外解除暂停");
            await handle.SetPropertyAsync("ab-loop-a", 8, token);
            await handle.SetPropertyAsync("ab-loop-b", 18, token);
            await Wait(() => ViewModel.Status.LoopA == 8 && ViewModel.Status.LoopB == 18, "A/B真实属性通知");
            if (TimelineLoops.Children.Count != 2) throw new InvalidOperationException("A/B状态到达但未绘制");
            write("时间轴运行验证通过：mpv章节变更、二维命中精确跳章、播放/暂停两种拖动收尾、A/B属性更新。");
        }
        finally
        {
            control.StatusChanged -= Apply;
            HideChapterPeek();
            ViewModel.AttachTimelineProbe(null);
            await handle.SetPropertyAsync("ab-loop-a", "no", token);
            await handle.SetPropertyAsync("ab-loop-b", "no", token);
            await control.CommandAsync(["seek", Math.Max(0, wasPosition).ToString(CultureInfo.InvariantCulture), "absolute+exact"], token);
            await handle.SetPropertyAsync("pause", wasPaused, token);
            ViewModel.SetTransportProbeStatus(oldStatus, oldMarks);
            ThemeHost.Apply(oldTheme);
            Hold(false, ChromeHold.Shot);
        }

        async Task Wait(Func<bool> condition, string name)
        {
            var until = Environment.TickCount64 + 6000;
            while (!condition() && Environment.TickCount64 < until) await Task.Delay(40, token);
            if (!condition()) throw new InvalidOperationException(name + "超时");
            write("通过：" + name);
        }
    }
}
