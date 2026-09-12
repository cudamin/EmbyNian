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
/// 浮层上那几块读数：播放统计、章节缩略图、按画面比例联动窗口、十赫兹那一跳、播放信息，以及共用的那两个小工具。
/// <para>
/// 这一片全是「每一跳都要跑一遍」的东西，所以它里面刻意不在每一跳里新建对象 —— 十赫兹乘上一部电影是几十万次分配。
/// </para>
/// <para>
/// 2026-09-05 从 <c>PlayerViewModel.cs</c>（当时 2227 行）切出来的一片，<b>正文一字节没动</b>：切的位置
/// 就是那个文件里本来就画好的分节线，所以这一次没有任何一处需要判断某个成员归谁。字段和构造函数留在主
/// 文件里 —— 它们是这一族共用的东西，散开就再也数不清谁在改哪个。
/// </para>
/// </summary>
public sealed partial class PlayerViewModel
{
    // ---- 播放统计 ----------------------------------------------------------------

    /// <summary>
    /// Reads the panel's properties and hands the formatted rows to the page, once a second while the panel
    /// is open. How it asks is the backend's answer, not this method's guess: see
    /// <see cref="PlaybackService.ReadsOverlap"/>.
    /// <para>
    /// Neither branch runs the batch the way this used to. Awaiting an already-completed task continues
    /// synchronously, and the in-process player's reads <em>are</em> already complete when they come back —
    /// each one is a native call made on the calling thread — so twenty-one 「awaited」 reads were in truth
    /// twenty-one native calls in a single unbroken stretch of the UI thread, every second, for as long as
    /// the panel stood open. Nothing about that is visible in a frame rate counter; it is visible in a
    /// pointer that moves in steps while the panel is up.
    /// </para>
    /// <para>
    /// So: across a pipe, ask for all of them at once and wait once — the cost there is round trips, and they
    /// pipeline. In-process, keep the single file (a shared gate would serialise them anyway) but run the whole
    /// stretch off the UI thread. The per-read 「已经关掉了就别问了」 bail survives only in the second branch,
    /// where reads still happen one after another; in the first they are all already in flight by the time the
    /// panel could close, and the two checks around the batch are what stop a late answer from being drawn.
    /// </para>
    /// <para>
    /// The re-entry guard is what matters most either way: a batch that took longer than the refresh interval
    /// would otherwise start another before the first came back, and the two would interleave into the same grid.
    /// </para>
    /// </summary>
    private async Task RefreshStatsAsync()
    {
        if (_statsBusy) return;

        _statsBusy = true;
        try
        {
            var fields = PlaybackStats.Fields;
            var readings = new Dictionary<string, string?>(fields.Count, StringComparer.Ordinal);

            if (_playback.ReadsOverlap)
            {
                var pending = new Task<string?>[fields.Count];
                for (var index = 0; index < fields.Count; index++) pending[index] = _playback.GetTextAsync(fields[index]);

                var values = await Task.WhenAll(pending).ConfigureAwait(true);
                for (var index = 0; index < fields.Count; index++) readings[fields[index]] = values[index];
            }
            else
            {
                // ConfigureAwait(true) on the way back: the invoke below touches the page's grid.
                await Task.Run(async () =>
                {
                    foreach (var field in fields)
                    {
                        if (!StatsOpen) return;

                        readings[field] = await _playback.GetTextAsync(field).ConfigureAwait(false);
                    }
                }).ConfigureAwait(true);
            }

            if (!StatsOpen) return;

            StatsUpdated?.Invoke(PlaybackStats.Format(readings));
            _statsRead = Now;
        }
        finally
        {
            _statsBusy = false;
        }
    }

    // ---- 章节缩略图预览 -----------------------------------------------------------

    /// <summary>
    /// Which chapter a hovered moment falls in, and its still if the server extracted one. The page owns
    /// where the box goes; this owns what is in it.
    /// </summary>
    /// <returns>Whether there is anything to preview at that moment, which there is for any open file.</returns>
    internal bool PeekChapterAt(double seconds)
    {
        if (_nowPlaying is null) return false;

        var moment = Math.Max(0, seconds);

        // Every pixel of movement, unlike the contents below: the time is the one part of the box that is
        // about where the pointer is rather than about which chapter it landed in. Written before anything
        // can return, because the readout is the half that is always available — plenty of servers extract
        // no chapters at all, and the slider's own tooltip only appears while the thumb is being dragged,
        // so hovering such a file used to show nothing whatsoever.
        ChapterClock = TimeFormat.Clock(TimeSpan.FromSeconds(moment));

        // Two lookups against two lists, both correct — see ChapterTimeline. The name comes from whichever
        // marks the bar is currently drawing, which is mpv's once it has published them; the picture is
        // indexed against Emby's own list, because that index is what the image request means.
        var named = ChapterTimeline.IndexAt(ChapterMarks, moment);
        var still = ChapterTimeline.IndexAt(_embyMarks, moment);

        // Contents only when the chapter changes: the box follows the pointer along the bar, and reloading
        // the same still for every pixel of that would be absurd.
        if (named == _peekChapter && still == _peekStill) return true;

        _peekChapter = named;
        _peekStill = still;
        ChapterCaption = ChapterTimeline.Caption(ChapterMarks, named);

        if (still >= 0 && _nowPlaying.Chapters[still] is { HasImage: true } info)
            _ = LoadChapterStillAsync(_generation, _nowPlaying.Id, still, info.ImageTag);
        else
            ChapterStill = null;

        return true;
    }

    internal void ClearChapterPeek()
    {
        _peekChapter = -1;
        _peekStill = -1;
        ChapterStill = null;
        ChapterCaption = null;
        ChapterClock = null;
    }

    /// <summary>
    /// Fetches and decodes one chapter still, once. Cached by index in <c>_chapterStills</c> including the
    /// misses, because a scrub back and forth over a chapter the server has no picture for would otherwise
    /// ask again on every pass. The index is Emby's, so what it is checked against is <c>_peekStill</c>.
    /// </summary>
    private async Task LoadChapterStillAsync(int generation, string itemId, int chapter, string? tag)
    {
        if (_chapterStills.TryGetValue(chapter, out var cached))
        {
            if (_peekStill == chapter) ChapterStill = cached;
            return;
        }

        // Nothing on screen while it arrives rather than the previous chapter's frame, which would be a
        // picture of the wrong moment — worse than no picture at all.
        ChapterStill = null;

        try
        {
            var width = EmbyImageStore.RequestWidth(ChapterPeekWidth);
            var bytes = await _images
                .GetChapterAsync(itemId, chapter, tag, width, _lifetime.Token)
                .ConfigureAwait(true);

            if (generation != _generation) return;

            var bitmap = bytes is null
                ? null
                : await PosterLoader.DecodeAsync(bytes, ChapterPeekWidth).ConfigureAwait(true);
            if (generation != _generation) return;

            _chapterStills[chapter] = bitmap;
            if (_peekStill == chapter) ChapterStill = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"读取章节缩略图失败：{error.Message}");
        }
    }

    // ---- 缩放窗口时按画面比例联动 --------------------------------------------------

    /// <summary>
    /// Hands a picture shape to the page, which is what reshapes the window and holds it there. Two
    /// things know the shape — the server before the file is open and mpv once it is — and both come
    /// through here; <paramref name="learnedFrom"/> is which one, for the log.
    /// <para>
    /// Only on a change, so switching episodes inside one series does not shuffle the window the viewer
    /// has already placed, and so the server's answer is not re-applied when mpv confirms it.
    /// </para>
    /// </summary>
    private void AdoptAspect(double aspect, string learnedFrom)
    {
        // Only the built-in backend draws into our window. An external mpv.exe has one of its own, and
        // reshaping ours around a picture that is not in it would move the window for nothing.
        if (!Embedded || aspect <= 0) return;
        if (Math.Abs(aspect - _aspect) <= 0.001) return;

        _aspect = aspect;
        PictureAspectChanged?.Invoke(aspect);
        Log.Debug(
            Category,
            $"画面比例 {aspect.ToString("0.000", CultureInfo.InvariantCulture)}（{learnedFrom}），缩放已联动");
    }

    /// <summary>
    /// The shape the server already reported, adopted before the player is even shown — 进播放器时画面两边
    /// 各有一条黑边. The poll below cannot answer in time for the first frames: it waits until the video is
    /// configured before it reads anything at all (the gate at <see cref="ApplyAspectAsync"/>), so the
    /// picture's first half-second lands in whatever shape the window was browsing in — and a window
    /// the user has dragged to any shape at all is one mpv has no choice but to letterbox.
    /// （侧边栏还在的时候连开窗那一档都是：窗口天生比 16:9 宽出一条栏。2026-09-06 那条栏删掉之后开窗那一档
    /// 正好是 16:9，可拖过的窗口照旧不是，所以这一步照旧要做。）
    /// <para>
    /// The stored size, so anamorphic and rotated files are a guess that <see cref="ApplyAspectAsync"/>
    /// corrects a moment later. A guess that is right for every ordinary file beats half a second of bands
    /// for all of them.
    /// </para>
    /// </summary>
    private void AdoptServerAspect(EmbyItem item)
    {
        var video = item.DefaultMediaSource?.PrimaryVideoStream;

        // Zeroes for mpv's pair: nothing is decoded yet, which is the case Ratio's fallback is for.
        AdoptAspect(
            AspectLock.Ratio(0, 0, streamWidth: video?.Width ?? 0, streamHeight: video?.Height ?? 0),
            "服务器");
    }

    /// <summary>
    /// Corrects the shape to the picture's own once mpv has one. Its <c>dwidth</c>/<c>dheight</c> are the
    /// displayed size — after any aspect override, rotation and panscan — so a rotated or anamorphic file
    /// ends up locked to the shape it is actually drawn at rather than to the stream's stored one.
    /// <para>
    /// Polled for the same reason the track list is: there is no notification to wait for. Silent when it
    /// agrees with <see cref="AdoptServerAspect"/>, which is the ordinary case.
    /// </para>
    /// <para>
    /// The read is gated on <c>video-params/w</c>, and that gate is the 2026-09-12 黑边事故. This comment
    /// used to say 「the properties do not exist until a frame has been decoded」 and read the display pair
    /// straight away — true of <c>video-params</c>, not of <c>dwidth</c>/<c>dheight</c> on the shipped mpv
    /// (0.41): before the video is configured the pair already exists and falls back to the window's client
    /// size. S01E07 的 mp4 转封装解码慢了半秒，用户恰好在那半秒把窗口贴成了竖形，这一轮询便收下 0.889 ——
    /// 正是当时 1230×1384 客户区自己的比例，而不是画面的。窗口随即被 FitToPicture 按它自己 reshape 一遍，
    /// WM_SIZING 从此把每一把拖拽都锁回竖形，mpv 只能把真正的 16:9 上下加黑边放完剩下的一分半
    /// （app-20260912.log 21:34:52，「画面比例 0.889（mpv）」）。
    /// <c>video-params/w</c> 在视频配置出来之前不存在（<c>GetNumberAsync</c> 返回 null，本轮作罢、继续轮询）；
    /// 它一出现，<c>dwidth</c>/<c>dheight</c> 才真的是画面自己的显示尺寸。
    /// </para>
    /// </summary>
    private Task ApplyAspectAsync(int generation)
    {
        if (!Embedded) return Task.CompletedTask;

        return PollAsync(generation, true, async () =>
        {
            // The gate comes before the display pair, for the reason the comment above carries: without it
            // a slowly decoding file answers this poll with the window's own shape as the picture's.
            var decodedWidth = await _playback.GetNumberAsync("video-params/w").ConfigureAwait(true);
            if (decodedWidth is null or 0) return false;

            var displayWidth = await _playback.GetNumberAsync("dwidth").ConfigureAwait(true);
            var displayHeight = await _playback.GetNumberAsync("dheight").ConfigureAwait(true);
            if (generation != _generation) return true;

            var aspect = AspectLock.Ratio(displayWidth ?? 0, displayHeight ?? 0, 0, 0);
            if (aspect <= 0) return false;

            AdoptAspect(aspect, "mpv");
            return true;
        });
    }

    // ---- the ten-hertz tick ------------------------------------------------------

    /// <summary>
    /// The share of the page's ticker that is not about drawing: the coalesced seek, the 统计 refresh and
    /// the standing 跳过 offer. Driven by the page's timer rather than one of its own, because all three are
    /// only wanted while the player is up, which is exactly when that timer runs.
    /// </summary>
    internal void Tick()
    {
        // Coalesced rather than sent per event: a drag along the bar raises a change for every pixel, and
        // mpv would spend the drag servicing seeks to positions the pointer had already left.
        if (_seekPending is { } fraction)
        {
            _seekPending = null;

            // Remembered as in flight: until mpv reports the target position, the bar keeps the value the
            // hand left rather than the pre-seek position mpv keeps reporting (see SeekBarFollows).
            _seekSent = fraction;
            _seekSentAt = Now;

            _ = _playback.SetPropertyAsync("percent-pos", fraction * 100);
        }

        // 每秒刷新一次.
        if (StatsOpen && Now - _statsRead >= PlaybackStats.RefreshMilliseconds) _ = RefreshStatsAsync();

        FlushVolume();

        ApplySkipOffer();

        // The 400 ms a window size has to hold still before the 档位 is judged again. Here rather than on a
        // timer of its own because this ticker already runs exactly while the player is up, and a resize that
        // has settled is precisely a thing that expires rather than happens.
        if (_outputWatch is { } watch) Judge(watch.Tick(DateTimeOffset.UtcNow), "窗口尺寸变化");
    }

    // ---- 播放信息 ----------------------------------------------------------------

    /// <summary>
    /// The 播放信息 body: what is playing, then what was decided about how to play it — the quality preset,
    /// the shader group and why it was chosen, the backend, and every mpv option the launch actually set,
    /// in the order mpv itself resolved them. The page puts it in a dialog; the text is all from here
    /// because every line of it is a playback fact.
    /// </summary>
    internal string MediaInfoText()
    {
        var lines = new List<string>();

        if (_nowPlaying is { } item)
        {
            lines.Add($"标题：{item.ToPlaybackTitle()}");
            if (item.MediaSources.Count > 0) lines.Add($"媒体源：{item.MediaSources[0].ToQualityLabel()}");
        }

        if (Status.HasDuration) lines.Add($"时长：{Status.DurationClock}");
        if (_playback.LaunchQualityPreset is { Length: > 0 } preset) lines.Add($"画质预设：{preset}");
        lines.Add($"着色器档位：{ActiveShader?.DisplayName ?? "未启用"}");
        if (ActiveShader is { } chain) lines.Add($"着色器链：{chain.Description}");

        // 任务书 3.7's line, on screen: the launch decision, then the current one when the window has moved
        // since. 「开播时是这一条，现在是这一条」 is exactly what a report about frame drops needs to say.
        if (_playback.LaunchShaderReason is { Length: > 0 } reason) lines.Add($"着色器判定：{reason}");
        if (CurrentShaderLine() is { Length: > 0 } current) lines.Add($"当前判定：{current}");
        lines.Add($"后端：{(Embedded ? "内置 libmpv" : "外部 mpv.exe")}");
        if (_playback.AudioDeviceInUse is { Length: > 0 } device) lines.Add($"音频输出设备：{device}");

        var options = _playback.LaunchOptions.Count == 0
            ? "（没有额外参数）"
            : string.Join("\n", _playback.LaunchOptions.Select(option => $"  {option.Key} = {option.Value}"));

        return string.Join("\n", lines) + "\n\nmpv 参数：\n" + options;
    }

    /// <summary>
    /// The 任务书 3.7 line as it stands right now, or an empty string when nothing has moved since the launch.
    /// Rebuilt rather than remembered, because the output size is the half of it that changes.
    /// </summary>
    private string CurrentShaderLine()
    {
        if (_outputWatch is not { } watch || _shaderContext is not { } context) return "";
        if (watch.Output == _launchOutput) return "";

        var video = context.Source.PrimaryVideoStream;
        var (width, height) = watch.Output;
        var measure = ShaderTier.Measure(video?.Width ?? 0, video?.Height ?? 0, width, height, watch.Tier);
        return ShaderTier.Explain(measure, ActiveShader?.Animated ?? false, Settings.Shaders.Gpu, width, height, ActiveShader);
    }

    // ---- plumbing ---------------------------------------------------------------

    /// <summary>
    /// Marshals onto the UI thread. Every one of the player's events arrives from mpv's own loop, and all
    /// of them end in a bound property or an event the page answers by touching the visual tree.
    /// </summary>
    private void OnUi(Action action) => _ui.Run(action);

    private static string Glyph(int codepoint) => char.ConvertFromUtf32(codepoint);
}
