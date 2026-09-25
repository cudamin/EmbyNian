using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Mpv;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Shell.Media;
using EmbyNian.Shell.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// 按键和菜单写给 mpv 的那些东西：音量、轨道、倍速、着色器与画面菜单。
/// <para>
/// 这一片的共同点是「一次点击变成一条 mpv 属性」，而多命令的菜单行要走到底再汇报 —— 六条里五条成了一条没成，说的是「没全部成功」。
/// </para>
/// <para>
/// 2026-09-05 从 <c>PlayerViewModel.cs</c>（当时 2227 行）切出来的一片，<b>正文一字节没动</b>：切的位置
/// 就是那个文件里本来就画好的分节线，所以这一次没有任何一处需要判断某个成员归谁。字段和构造函数留在主
/// 文件里 —— 它们是这一族共用的东西，散开就再也数不清谁在改哪个。
/// </para>
/// </summary>
public sealed partial class PlayerViewModel
{
    // ---- what the keys and the menus write to mpv ---------------------------------

    [RelayCommand]
    private void CycleStats() => _ = _playback.CommandAsync("script-binding", MpvStats.CycleBinding);

    /// <summary>播放/暂停, from the transport button, the space bar and a tap on the picture.</summary>
    internal void SetPaused(bool paused) => _ = _playback.SetPropertyAsync("pause", paused);

    internal void ToggleNativeFullscreen() => _ = _playback.CommandAsync("cycle", "fullscreen");

    internal void SetNativeFullscreen(bool on) => _ = _playback.SetPropertyAsync("fullscreen", on);

    internal void ExitNativeFullscreenOrStop() => _ = _playback.ExitNativeFullscreenOrStopAsync();

    // Native playback owns its cursor independently of the WinUI control window.
    internal void ShowMpvCursor(bool visible)
    {
        if (!PictureInHostWindow) return;
        _ = _playback.SetPropertyAsync("cursor-autohide", visible ? "no" : "always");
    }

    /// <summary>
    /// 快进/快退 by the short 跨度 from settings —— ← / → 那一路。设置页上印的是几，这里就跳几。
    /// </summary>
    internal void SeekForward() => SeekBy(Settings.Playback.SeekForwardSeconds);

    internal void SeekBackward() => SeekBy(-Settings.Playback.SeekBackwardSeconds);

    /// <summary>
    /// 大步 快进/快退 —— ↑ / ↓ 那一路（2026-09-20，动作表里那两条 <c>seek-*-long</c>）。与上面那对是
    /// 同一件事的两种幅度，所以同样落到 <see cref="SeekBy"/>：发出去的都只是「相对跳 N 秒」，区别只在 N
    /// 从哪个设置里读。
    /// </summary>
    internal void SeekForwardLong() => SeekBy(Settings.Playback.SeekForwardLongSeconds);

    internal void SeekBackwardLong() => SeekBy(-Settings.Playback.SeekBackwardLongSeconds);

    private void SeekBy(int seconds) => _ = _playback.CommandAsync(
        "seek",
        seconds.ToString(CultureInfo.InvariantCulture),
        "relative+exact");

    /// <summary>
    /// 章节前后跳. mpv's own <c>add chapter</c> rather than a seek to a mark this class holds, because mpv
    /// knows where it is: it lands on the boundary, it clamps at both ends of the file, and it does
    /// nothing at all on a file with no chapters. The notice is the only part it will not say by itself —
    /// commands issued through libmpv produce no OSD, unlike the same command from a key binding.
    /// </summary>
    internal void StepChapter(int offset)
    {
        if (ChapterMarks.Count < 2)
        {
            _ = _playback.CommandAsync("show-text", "这个文件没有章节", "1500");
            return;
        }

        _ = _playback.CommandAsync("add", "chapter", offset.ToString(CultureInfo.InvariantCulture));

        // ${chapter} is expanded by mpv after the jump, so what the notice reads cannot disagree with
        // where playback actually landed. The 1-based number is what a viewer counts in.
        _ = _playback.CommandAsync(
            "show-text",
            "章节 ${=chapter}/" + ChapterMarks.Count + "  ${chapter-metadata/title}",
            "1500");
    }

    /// <summary>倍速微调, clamped to the range the 倍速 menu offers so the two cannot disagree.</summary>
    internal void NudgeSpeed(double delta) =>
        SetSpeed(Math.Round(Math.Clamp(Status.Speed + delta, SpeedChoices[0], SpeedChoices[^1]), 2));

    /// <summary>
    /// 倍速. The keys say what they did on the OSD; the menu does not, because the row that was just
    /// ticked and the button that now reads 「1.25×」 have already said it.
    /// </summary>
    internal void SetSpeed(double speed, bool notice = true)
    {
        _ = _playback.SetPropertyAsync("speed", speed);

        if (!notice) return;

        _ = _playback.CommandAsync(
            "show-text",
            $"倍速：{speed.ToString("0.0#", CultureInfo.InvariantCulture)}×",
            "1200");
    }

    /// <summary>
    /// 字幕/音频延迟微调 from the keyboard. Goes through the same two fields the ⚙ menu's submenus read, so
    /// the menu opens on the value the keys left rather than on zero.
    /// </summary>
    internal void NudgeDelay(bool subtitle, double delta)
    {
        var value = Math.Round((subtitle ? SubtitleDelay : AudioDelay) + delta, 3);
        SetDelay(subtitle, value);

        _ = _playback.CommandAsync(
            "show-text",
            $"{(subtitle ? "字幕延迟" : "音频延迟")}：{value.ToString("+0.0#;-0.0#;0", CultureInfo.InvariantCulture)} 秒",
            "1200");
    }

    /// <summary>
    /// Applies a delay and remembers it. Held here rather than read back from mpv because the ⚙ menu is
    /// built synchronously as it opens, and a value that had to be awaited would arrive after the rows.
    /// </summary>
    internal void SetDelay(bool subtitle, double value)
    {
        if (subtitle) SubtitleDelay = value;
        else AudioDelay = value;

        _ = _playback.SetPropertyAsync(subtitle ? "sub-delay" : "audio-delay", value);
    }

    /// <summary>
    /// 音量 from the arrow keys. Writes the bound property rather than mpv directly, so the slider, the
    /// number beside it and the mpv property all move together — the same path a drag takes.
    /// <para>
    /// 直接落在音量上，不走 <see cref="VolumeScale"/> 的棘轮：一步 5 个音量本来就不小于 100→101 那一格
    /// （4 个单位），"这一档要不要多按几下"这个问题在这些步长上不存在 —— 用户要的那件事只关于滚轮。
    /// </para>
    /// </summary>
    internal void NudgeVolume(double delta) => Volume = Math.Clamp(Volume + delta, 0, AudioSettings.MaxVolume);

    /// <summary>
    /// 音量 from the wheel. 一格 2 个<b>轴</b>单位，沿刻度走（<see cref="VolumeScale.Step"/>）：100→101 那一格
    /// 要两格，其余每格仍是两档。没走满的路留在 <see cref="VolumeAxis"/> 上等下一次接着走，所以数值不动时
    /// 滑块也会先爬一点 —— 那正是「这一段更长」看得见的样子。
    /// </summary>
    internal void RollVolume(double delta)
    {
        var (level, axis) = VolumeScale.Step(Volume, VolumeAxis, delta);

        _axisByRoll = true;
        try
        {
            VolumeAxis = axis;
            if (Math.Abs(level - Volume) > 0.001) Volume = level;
        }
        finally
        {
            _axisByRoll = false;
        }
    }

    /// <summary>静音切换. mpv owns the flag; the glyph follows from the next status it reports.</summary>
    internal void ToggleMute() => _ = _playback.SetPropertyAsync("mute", !Status.Muted);

    /// <summary>
    /// Applies a track choice and marks it locally, so the picker shows the new selection the next time
    /// it opens instead of waiting for mpv's own <c>track-list</c> notification to come round.
    /// </summary>
    internal void SelectTrack(bool audio, int? id)
    {
        _ = _playback.SetPropertyAsync(audio ? "aid" : "sid", id is null ? "no" : id);

        Tracks = [.. Tracks.Select(track =>
            (audio ? track.IsAudio : track.IsSubtitle)
                ? track with { Selected = id is { } chosen && track.Id == chosen }
                : track)];
    }

    /// <summary>
    /// 把拖进画面的外挂字幕交给 mpv 当场挂上并选中 —— 集成管线专用的那条拖拽路（<see cref="SubtitleFile"/>
    /// 已按后缀筛过、去过重、保住了次序）。
    /// <para>
    /// 逐条 <c>sub-add … select</c> 而不是一次性丢过去：<c>select</c> 是「挂上并立刻切过去」，按次序走完，
    /// 最后成功的那条自然成为当前字幕，其余留在轨道表里可切。挂轨会触发 mpv 重发 track-list，字幕选单下次
    /// 打开就见得到新轨（见 <c>OnTracksChanged</c>），这里不必自己补。
    /// </para>
    /// <para>
    /// 没在放就先不挂 —— <c>sub-add</c> 挂到「没有片子」上没有意义，给一句提示比让 mpv 静静吞掉强。独占
    /// 窗口与外部 mpv.exe 的画面不在本窗口里，拖拽由 mpv 原生接住，根本走不到这里。
    /// </para>
    /// </summary>
    internal async Task AddExternalSubtitlesAsync(IReadOnlyList<string> droppedPaths)
    {
        var subs = SubtitleFile.Filter(droppedPaths);
        if (subs.Count == 0)
        {
            Noticed?.Invoke("拖进来的文件里没有字幕（支持 srt/ass/ssa/sub/vtt 等）", InfoBarSeverity.Warning);
            return;
        }

        if (!_playback.IsPlaying)
        {
            Noticed?.Invoke("先开始播放，再把字幕拖进来", InfoBarSeverity.Warning);
            return;
        }

        var added = 0;
        string? current = null;
        foreach (var path in subs)
        {
            if (await _playback.CommandAsync("sub-add", path, "select").ConfigureAwait(true))
            {
                added++;
                current = path;
            }
        }

        if (added == 0)
        {
            Noticed?.Invoke("字幕没能加载（详见日志）", InfoBarSeverity.Error);
            return;
        }

        Noticed?.Invoke(
            added == 1
                ? $"已加载字幕：{System.IO.Path.GetFileName(current)}"
                : $"已加载 {added} 条字幕，当前：{System.IO.Path.GetFileName(current)}",
            InfoBarSeverity.Informational);
    }

    // ---- 着色器与画面菜单 ---------------------------------------------------------

    /// <summary>
    /// Works out the one shader plan this playback runs — always the full-screen tier, computed for this
    /// monitor — and returns the output size the launch should be planned against. Called at every playback
    /// start and again if the window lands on another monitor.
    /// <para>
    /// 档位固定用全屏那套（他的拍板，2026-09-11）：ArtCNN 这类放大器在核显上每个尺寸的冷编译要数秒，
    /// 随全屏实时换链，画面就停在旧尺寸贴在左上角。链从开播起不变，进退全屏只剩改尺寸本身。
    /// </para>
    /// </summary>
    private (int Width, int Height) PrepareShaderPlans(EmbyItem item, MediaSource source, EmbyItem? parent)
    {
        _shaderContext = (item, source, parent);
        _shaderPinned = false;
        _surface = Surface();

        _shaderPlan = _shaders.Resolve(item, source, parent, _surface.Monitor);

        // What the planner picked is the 档位 the ⚙ menu opens on, so it shows the startup decision rather
        // than looking as though nothing had been applied.
        ActiveShader = _shaderPlan.Group;

        var video = source.PrimaryVideoStream;
        _outputWatch = new OutputWatch(
            video?.Width ?? 0,
            video?.Height ?? 0,
            _surface,
            _shaderPlan.Measure.Tier);

        // 退场保留那一帧要按「画面本来什么形状」摆，而这个数只有码流尺寸能给 —— 另外三个候选
        // （呈现矩形、mpv 缓冲尺寸、dwidth/dheight）在窗口与画面不同形时全都是窗口比例。详见
        // CompositionVideoTarget.SourceAspect。读不到就报零，那边会退回别的来源。
        SourceAspectChanged?.Invoke(video is { Width: > 0, Height: > 0 } stream
            ? (double)stream.Width!.Value / stream.Height!.Value
            : 0);

        _launchOutput = _surface.Active;
        return _launchOutput;
    }

    /// <summary>
    /// The picture's size now, by 任务书 2.3's order of preference. The monitor is the last resort and says so
    /// in the log — and is deliberately not remembered as a known size, or one unreadable moment would pin
    /// every later measurement to the monitor's native resolution.
    /// </summary>
    private ShaderSurface Surface()
    {
        var surface = MeasureSurface?.Invoke() ?? default;
        if (surface.Fallback)
        {
            Log.Debug(
                ShaderLog,
                $"读不到渲染目标尺寸，本次按所在显示器 {surface.MonitorWidth}×{surface.MonitorHeight} 估算"
                + $"（后端：{(Embedded ? "内置 libmpv" : "外部 mpv.exe，画面不在本窗口里")}）");
        }

        return surface;
    }

    /// <summary>
    /// The window's geometry moved. Full screen and a monitor change take effect now; a plain resize is only
    /// noted here and <see cref="Tick"/> acts on it once it has held still — see <see cref="OutputWatch"/>.
    /// <para>
    /// The size arrives as a callback rather than a value because this is raised for every <c>WM_SIZE</c> the
    /// shell sees, including the ones while nobody is watching anything: with no film open there is nothing to
    /// re-measure, and the read is two OS calls that may as well not happen.
    /// </para>
    /// </summary>
    internal void NoteSurface(Func<ShaderSurface> measure)
    {
        if (_outputWatch is null || _shaderContext is not { } context) return;

        var surface = measure();
        var moved = surface.Monitor != _surface.Monitor;

        // 换显示器：方案按新显示器重算. Done here rather than off the verdict because the plan is keyed to
        // the monitor, and only a monitor move changes it.
        if (moved)
        {
            _shaderPlan = _shaders.Resolve(context.Item, context.Source, context.Parent, surface.Monitor);
        }

        _surface = surface;

        // Which discrete event this was is only knowable here — OutputVerdict says 「离散」 and not which of the
        // two. Handing the word down beats guessing it downstream, which is how a monitor drag came to be
        // logged as 「全屏切换」.
        Judge(_outputWatch.Observe(surface, DateTimeOffset.UtcNow), moved ? "换显示器" : "全屏切换");
    }

    /// <summary>
    /// Acts on one verdict. Three of the four outcomes touch mpv not at all, which is the whole point of
    /// 任务书 2.4: the expensive thing is reloading the chain, and a window being dragged must not do it.
    /// </summary>
    /// <param name="because">
    /// What moved, in the words the log should use. Supplied by the caller because only the caller knows:
    /// <see cref="NoteSurface"/> can tell a monitor change from a full-screen toggle, and <see cref="Tick"/>
    /// only ever reports a settled resize.
    /// </param>
    private void Judge(OutputVerdict verdict, string because)
    {
        if (verdict.Change is OutputChange.None or OutputChange.Waiting) return;

        // 用户自己钉过一条链，就什么都不动 —— 但要照实说是钉住了，而不是说档位没变。判定那一头已经把当前档位
        // 推进去了（OutputWatch.Apply），所以走 Restate 会写出「仍在<刚换到的那一档>」，两处都是错的。
        if (_shaderPinned)
        {
            Log.Debug(
                ShaderLog,
                $"{because}，输出尺寸 {verdict.Width}×{verdict.Height}、"
                + $"{ShaderTier.Describe(_outputWatch?.Tier ?? UpscaleTier.Slight)}，"
                + $"但档位已手动钉在{ActiveShader?.DisplayName ?? "未启用"}，链不变");
            return;
        }

        // 不跨档就只更新上下文里的输出尺寸. There is nothing to hand mpv: ravu-zoom renders to whatever OUTPUT
        // is on the frame it is drawing, so the only things that were stale are the log line and the OSD.
        if (verdict.Change == OutputChange.SizeOnly)
        {
            Restate(verdict);
            return;
        }

        // 进 / 退全屏、换显示器：换显示器那一路已经把方案重算过了，这里换上的永远是同一个
        // 全屏档方案 —— Switch 自己发现档位没变会直接说「档位没变」，链一动不动。
        if (verdict.Discrete)
        {
            Switch(_shaderPlan, because);
            return;
        }
    }

    /// <summary>Applies a prepared plan to the film that is playing, and says so in the log rather than on screen.</summary>
    private void Switch(ShaderDecision plan, string because)
    {
        if (string.Equals(plan.Group?.Id ?? "", ActiveShader?.Id ?? "", StringComparison.Ordinal))
        {
            Log.Debug(ShaderLog, $"{because}，档位没变：{plan.Reason}");
            return;
        }

        ActiveShader = plan.Group;
        _ = _playback.SetShaderGroupAsync(plan.Group);
        Log.Info(ShaderLog, $"{because}，着色器档位改为：{plan.Reason}");
    }

    /// <summary>
    /// Records an output size that did not change the chain, so the log stops quoting the old one. No mpv call:
    /// nothing in the chain is configured with the size, every one of them reads it per frame.
    /// </summary>
    private void Restate(OutputVerdict verdict) =>
        Log.Debug(
            ShaderLog,
            $"输出尺寸改为 {verdict.Width}×{verdict.Height}，仍在"
            + $"{ShaderTier.Describe(_outputWatch?.Tier ?? UpscaleTier.Slight)}，链不变");

    /// <summary>
    /// Switches the 着色器档位, or turns shaders off. Applies to the film that is playing and no further —
    /// this is the seam an A/B comparison is made through, and a comparison that quietly rewrote the settings
    /// file would not be one. 设置 → 画质与着色器 → 手动指定档位 is where a lasting override goes.
    /// <para>
    /// It also stops the automatic rules from moving off this choice for the rest of the film: pressing F
    /// halfway through a comparison would otherwise put the other chain back.
    /// </para>
    /// </summary>
    internal void ApplyShaderGroup(ShaderGroup? group)
    {
        _shaderPinned = true;
        ActiveShader = group;
        _ = _playback.SetShaderGroupAsync(group);
        Noticed?.Invoke(
            group is null ? "已关闭着色器" : $"已切换着色器：{group.DisplayName}",
            InfoBarSeverity.Informational);
    }

    /// <summary>
    /// Runs one 画面 menu row: its commands in order, then its notice. Awaited one at a time rather than
    /// fired off together, because a 「重置」 row is six <c>set</c>s and mpv applies them in the order it
    /// receives them — and because the notice must come last: it contains <c>${property}</c>, which mpv
    /// expands when it draws the text, so a notice that overtook its own command would report the old value.
    /// <para>
    /// <b>The notice is conditional, and that is the point.</b> It used to go out whatever the commands did,
    /// which made 「已保存到 …」 a promise nobody had checked — a screenshot mpv refused to write (directory not
    /// writable, disk full) said the same thing as one it wrote. The row is still run to the end rather than
    /// abandoned at the first refusal: a 「重置」 row should put back everything it can. The refusal wording
    /// follows the row's size: a multi-command row may have put most of itself back, so it says 没全部成功
    /// rather than claiming the whole row did nothing.
    /// </para>
    /// </summary>
    internal async Task RunMenuNodeAsync(PlayerMenuNode node)
    {
        try
        {
            var done = true;
            foreach (var command in node.Commands)
                done &= await _playback.CommandAsync([.. command]).ConfigureAwait(true);

            if (node.Notice.Length > 0)
            {
                var verdict = node.Commands.Count > 1 ? "没全部成功" : "没成功";
                await _playback.CommandAsync(
                    "show-text", done ? node.Notice : $"{node.Label} {verdict}（详见日志）", "2000").ConfigureAwait(true);
            }
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"画面菜单「{node.Label}」执行失败：{error.Message}");
        }
    }

}
