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
/// 播放内核自己发上来的事件，以及跳过片头片尾。
/// <para>
/// 后端在别的线程上说话，所以这一片里每一处回到界面的路都要经过 <c>OnUi</c>；切集那一下靠 <c>_generation</c> 兜住晚到的回调。
/// </para>
/// <para>
/// 2026-09-05 从 <c>PlayerViewModel.cs</c>（当时 2227 行）切出来的一片，<b>正文一字节没动</b>：切的位置
/// 就是那个文件里本来就画好的分节线，所以这一次没有任何一处需要判断某个成员归谁。字段和构造函数留在主
/// 文件里 —— 它们是这一族共用的东西，散开就再也数不清谁在改哪个。
/// </para>
/// </summary>
public sealed partial class PlayerViewModel
{
    // ---- the player's own events -------------------------------------------------

    private void OnProgressChanged(PlaybackProgress progress) => OnUi(() =>
    {
        if (progress.Title.Length > 0) Title = progress.Title;
    });

    private void OnStatusChanged(PlayerStatus status) => OnUi(() => ApplyStatus(status));

    private void OnTracksChanged(IReadOnlyList<MpvTrack> tracks) => OnUi(() => Tracks = tracks);

    private void OnNowPlayingChanged(EmbyItem? item) => OnUi(() =>
    {
        // Re-armed per playback, before anything can look at it: switching episodes comes through here,
        // and the previous episode's opening is not this one's. The server's chapter marks are the
        // starting point; RefineSkipSectionsAsync replaces them with mpv's once the file is open.
        _generation++;

        // The old file's scrub and in-flight seek mean nothing here: a seek left in flight would hold the
        // new file's bar at a fraction from another film until the 5-second give-up, which is exactly the
        // 「进度与进度条不一致」 shape pointed at a different cause.
        _seekTouched = 0;
        _seekSent = null;
        _nowPlaying = item;
        if (item is not null
            && string.Equals(_episodeSwitchTargetId, item.Id, StringComparison.Ordinal))
            _episodeSwitchTargetId = null;
        Tracks = [];

        // Converted once and kept: the 跳过 plan and the preview's still both read Emby's marks, and the
        // preview reads them on every pointer move.
        _embyMarks = item is null ? [] : SkipSectionPlanner.FromEmby(item.Chapters);
        var runtime = TimeFormat.ToSeconds(item?.RunTimeTicks);
        _skips.Begin(SkipSectionPlanner.Resolve(_embyMarks, runtime), runtime);

        // The server's marks are what the bar draws until mpv publishes its own, for the same reason the
        // 跳过 offer uses them: they are available immediately, and a bar that grew its ticks two seconds
        // in would look like a glitch rather than a refinement.
        ChapterMarks = _embyMarks;
        _chapterStills.Clear();
        ClearChapterPeek();
        ChaptersChanged?.Invoke();

        if (item is null)
        {
            // Only when nothing is coming to take its place. During a handover the picture is about to
            // be replaced, and tearing the player down for the second it takes is the whole of
            // 「切换集数的时候画面错乱」 — so the chrome keeps the episode list it is about to need and the
            // video surface keeps the window it is about to draw into.
            if (_playerHold > 0)
            {
                // The old file's readings are gone even though the surface stays: what the bar shows
                // between two episodes should be the new one loading, not the last frame of the old one.
                ApplyStatus(new PlayerStatus());
                ShowCover("正在切换…");
            }
            else
            {
                LeavePlayer();
            }

            return;
        }

        Title = item.ToPlaybackTitle();
        Subtitle = item.Type == EmbyItemType.Episode ? item.SeriesName ?? "" : item.CardSubtitle;
        SourceLabel = item.MediaSources.Count > 0 ? item.MediaSources[0].ToQualityLabel() : "";
        EpisodeControlsVisible = item.Type == EmbyItemType.Episode
            && (Episodes.Count > 1 || !string.IsNullOrEmpty(item.SeriesId));

        // Before the player is shown rather than after: the window is reshaped for the picture while the
        // picture is still being opened, so the first frame arrives into a client area that is already its
        // own shape. Doing this after the poll — the only thing that did it until now — meant the film
        // started with a black band down each side. mpv's own answer refines it in a moment.
        AdoptServerAspect(item);

        PlaybackStarted?.Invoke();

        _ = PopulateTracksAsync(_generation);
        _ = RefineSkipSectionsAsync(_generation);
        _ = ApplyAspectAsync(_generation);
        _ = NoteAudioDeviceAsync(_generation);
        ApplySkipOffer();
    });

    /// <summary>
    /// Asks mpv which audio output it actually opened, and hands the answer to the service so 播放信息 and the
    /// 诊断 page can both state it.
    /// <para>
    /// Polled like the other three, and for the same reason: the audio output is not open at the moment the
    /// file is handed over, so a prompt answer is the wrong one rather than an early one. What is being
    /// answered is 「音频独占模式 took over <em>which</em> device」 — a question the launch options cannot answer,
    /// because 「跟随系统默认设备」 sends nothing at all.
    /// </para>
    /// </summary>
    private Task NoteAudioDeviceAsync(int generation) => PollAsync(generation, true, async () =>
    {
        var device = await _playback.GetTextAsync("audio-device").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(device)) return false;

        var driver = await _playback.GetTextAsync("current-ao").ConfigureAwait(true);
        if (generation != _generation) return true;

        // mpv answers 「auto」 for a device nobody named, which is true but useless on its own — the driver
        // name is what says anything at all in that case.
        var described = string.IsNullOrWhiteSpace(driver) ? device : $"{device}（{driver}）";
        _playback.NoteAudioDevice(described);
        Log.Info(Category, $"音频输出设备：{described}");
        return true;
    });

    /// <summary>
    /// Pushes one status snapshot into the chrome. Everything the bar draws comes from here and from
    /// nowhere else, so the clock and the bar beside it can never have come from different moments.
    /// </summary>
    private void ApplyStatus(PlayerStatus status)
    {
        Status = status;

        PlayPauseGlyph = Glyph(status.Paused ? PlayGlyphCode : PauseGlyphCode);
        DurationClock = status.HasDuration ? status.DurationClock : "0:00";
        CacheFraction = status.CacheFraction;
        SpeedLabel = $"{status.Speed.ToString("0.0#", CultureInfo.InvariantCulture)}×";
        ThinFraction = Math.Clamp(status.Fraction, 0, 1);

        _pushing = true;
        try
        {
            // The user's own drag wins for as long as it is the more current answer: mpv reports the
            // position it is still seeking away from, and letting that write the slider back would drag
            // the thumb out from under the pointer. A seek that has been sent but not landed gets the
            // same protection — see SeekBarFollows for why 400 ms of grace was not enough — and the
            // clock and the bar are gated together so the two never show different moments.
            if (SeekBarFollows(status))
            {
                SeekValue = status.Fraction * SeekScale;
                PositionClock = status.HasPosition ? status.PositionClock : "0:00";
            }

            // 音量 has the same problem and it was visible: 「滚轮调音量的时候不是很顺滑，音量条一顿一顿的」
            // (2026-09-04). Every wheel notch writes mpv asynchronously, and this poll runs ten times a second
            // — so a poll landing between the write and mpv applying it reports the *previous* level and drags
            // the thumb back a step, which during a spin is the thumb going forwards and backwards. mpv's echo
            // is only read once the level has settled (_volumePending cleared by FlushVolume), and nothing
            // else moves mpv's volume in the meantime: its own input handling is switched off at launch.
            if (_volumePending is null) Volume = Math.Clamp(Math.Round(status.Volume), 0, AudioSettings.MaxVolume);
        }
        finally
        {
            _pushing = false;
        }

        // 静音's own glyph is still the whole of the mute readout — the figure above the rail says how loud,
        // not whether (「给音量条上方加上数字」).
        SoundGlyph = Glyph(status.Muted ? MutedGlyphCode : VolumeGlyphCode);

        // The new file is decoding, so there is a real picture to show and the cover has done its job.
        // Anything earlier than Loaded would uncover the seam it was put up for.
        if (status.Loaded) HideCover();

        // The tooltip's run time, the tick layout the duration decides, and 「stay up while paused」.
        StatusApplied?.Invoke(status);

        ApplySkipOffer();
    }

    /// <summary>
    /// Whether this snapshot may write the seek bar and the clock beside it. Three answers:
    /// a drag under the user's hand — the thumb is the user's, whatever mpv says; a seek already sent —
    /// the bar keeps the value the hand left until mpv reports the target position, because a precise
    /// seek (<c>hr-seek</c>, 「跳过片头」 and every release of a drag) takes longer to land than the old
    /// 400 ms grace, and in that window mpv keeps reporting the position it is seeking <em>away from</em> —
    /// writing that back is the thumb jumping backwards and then forwards again
    /// (「播放进度会与进度条进度不一致」); the target reached, or the wait timed out — mpv is the answer again.
    /// <para>
    /// 拖动中的落定也认：那只是提前结束等待，拇指本身仍归手管（<see cref="Scrubbing"/> 那一档），下一次
    /// 释放又会在 <c>Tick</c> 里记下新的目标。认这一下的意义在于离开的手一松开，进度条马上就能跟 mpv 走。
    /// </para>
    /// </summary>
    private bool SeekBarFollows(PlayerStatus status)
    {
        if (_seekSent is not { } target) return !Scrubbing;

        var landed = status.HasPosition && status.HasDuration
            && Math.Abs(status.Position - target * status.Duration) <= SeekLandedSeconds;

        if (landed || Now - _seekSentAt > SeekGiveUpMilliseconds) _seekSent = null;

        return !Scrubbing && _seekSent is null;
    }

    /// <summary>
    /// The seek bar under the user's own hand. Coalesced rather than sent per change: a drag along the bar
    /// raises this for every pixel, and mpv would spend the drag servicing seeks to positions the pointer
    /// had already left — so <see cref="Tick"/> sends the last one.
    /// </summary>
    partial void OnSeekValueChanged(double value)
    {
        if (_pushing) return;

        _seekTouched = Now;
        _seekPending = Math.Clamp(value / SeekScale, 0, 1);

        // The clock keeps up with the thumb rather than with mpv, so a drag reads as a scrub instead of
        // as a slider that has come loose from the number beside it.
        if (Status.HasDuration)
            PositionClock = TimeFormat.Clock(TimeSpan.FromSeconds(_seekPending.Value * Status.Duration));
    }

    /// <summary>
    /// 音量 under the user's own hand — the rail, the wheel and the arrow keys all land here. Sent straight
    /// through rather than coalesced: a volume change is a single value mpv applies instantly, and the thumb and
    /// the figure above it have to keep up with the hand rather than with the next status poll — which, until
    /// this level settles, is not allowed to write them at all (see <see cref="ApplyStatus"/>).
    /// </summary>
    partial void OnVolumeChanged(double value)
    {
        if (_pushing) return;

        var level = Math.Clamp(Math.Round(value), 0, AudioSettings.MaxVolume);
        _ = _playback.SetPropertyAsync("volume", level);

        // Kept for the next file as well as sent to this one. Every playback launches a fresh mpv with its
        // own config blocked, so a level nobody wrote down is 100 again by the next episode.
        _volumePending = (int)level;
        _volumeTouched = Now;
    }

    /// <summary>
    /// Writes the volume the player was left at into the settings file, once it has settled — or at once
    /// when <paramref name="settled"/> says not to wait, which is what leaving the player does: there may be
    /// no further tick to settle on.
    /// </summary>
    private void FlushVolume(bool settled = true)
    {
        if (_volumePending is not { } level) return;
        if (settled && Now - _volumeTouched < VolumeSettleMilliseconds) return;

        _volumePending = null;

        if (Settings.Audio.Volume == level) return;

        Settings.Audio.Volume = level;
        _settings.Save();
    }

    /// <summary>
    /// Asks again until the answer arrives. Three things about a file cannot be known when playback starts and
    /// have no notification to wait for — the track list, mpv's own chapter marks and the displayed picture
    /// size — so each is polled, and this is the polling.
    /// </summary>
    /// <param name="generation">
    /// The playback this poll belongs to. Checked before every attempt, because six seconds is long enough for
    /// the viewer to have switched episodes twice and an answer about the previous file must not be applied to
    /// this one.
    /// </param>
    /// <param name="pauseFirst">
    /// Whether to wait before the first attempt as well as between them. True for the two that ask mpv about
    /// the file directly: right after a switch mpv is still answering about the file that just ended, and a
    /// prompt answer is the wrong one rather than an early one. False for the track list, which asks the
    /// client's own service and can be answered at once when the file was already open.
    /// </param>
    /// <param name="attempt">
    /// One attempt. True means stop asking — either it worked, or the answer that came back says no amount of
    /// waiting will change it (a file with fewer than two chapters has no 「OP」 to find).
    /// </param>
    private async Task PollAsync(int generation, bool pauseFirst, Func<Task<bool>> attempt)
    {
        for (var round = 0; round < PollAttempts; round++)
        {
            if (pauseFirst || round > 0) await Task.Delay(PollIntervalMilliseconds).ConfigureAwait(true);

            if (generation != _generation || !_playback.IsPlaying) return;
            if (await attempt().ConfigureAwait(true)) return;
        }
    }

    /// <summary>
    /// mpv publishes its track list only once the file is actually being decoded, so the pickers are
    /// refilled until the list stops being empty. A stuck backend runs out of attempts and leaves the
    /// placeholder rows in place.
    /// </summary>
    private Task PopulateTracksAsync(int generation) => PollAsync(generation, false, async () =>
    {
        var tracks = await _playback.GetTracksAsync().ConfigureAwait(true);
        if (generation != _generation) return true;

        if (tracks.Count == 0) return false;

        Tracks = tracks;
        return true;
    });

    /// <summary>
    /// Swaps Emby's chapter marks for mpv's own, which arrive a second or two after playback starts and
    /// are the ones a release group actually wrote 「OP」 in.
    /// </summary>
    private Task RefineSkipSectionsAsync(int generation) => PollAsync(generation, true, async () =>
    {
        var count = await _playback.GetNumberAsync("chapter-list/count").ConfigureAwait(true);

        // Null means the property is not there yet; a real answer of 0 or 1 means this file has
        // nothing to read and waiting longer will not change that.
        if (count is null) return false;
        if (count < 2) return true;

        var chapters = new List<SkipChapter>((int)count.Value);
        for (var index = 0; index < (int)count.Value; index++)
        {
            if (generation != _generation) return true;

            var start = await _playback.GetNumberAsync($"chapter-list/{index}/time").ConfigureAwait(true);
            if (start is null) return true;

            var title = await _playback.GetTextAsync($"chapter-list/{index}/title").ConfigureAwait(true);
            chapters.Add(new SkipChapter(start.Value, title));
        }

        if (generation != _generation) return true;

        var duration = await _playback.GetNumberAsync("duration").ConfigureAwait(true)
                       ?? _playback.Status.Duration;
        if (generation != _generation) return true;

        _skips.Refine(SkipSectionPlanner.Resolve(chapters, duration), duration);

        // The ticks get the same upgrade. Emby's marks and mpv's usually agree, but a file remuxed
        // after the library scan is exactly the case where they do not, and the picture on screen is
        // the one to believe.
        ChapterMarks = chapters;
        ChaptersChanged?.Invoke();

        ApplySkipOffer();
        return true;
    });

    // ---- 跳过片头/片尾 -----------------------------------------------------------

    /// <summary>
    /// Puts the 跳过 offer up or takes it away for the position playback has reached, and makes the
    /// automatic jump when that is what the setting asks for.
    /// <para>
    /// Deliberately outside the reveal rule the rest of the chrome lives under: the offer stands for
    /// fifteen seconds, and hiding it because the pointer sat still would take the button away exactly
    /// when it was useful. It goes when it is used, when it lapses, when the section ends, or when
    /// playback stops — never because nobody moved the mouse.
    /// </para>
    /// </summary>
    internal void ApplySkipOffer()
    {
        _skips.Mode = Settings.Playback.SkipSections;

        var live = _playback.IsPlaying && _playback.CanControl && _playerUp;

        if (_skips.Advance(live ? Status.Position : -1, live) is { } jump)
        {
            AcceptSkip(jump);
            return;
        }

        ShowSkipPrompt(_skips.Prompt);
    }

    /// <summary>
    /// The offer's own mapping onto the button's four bindings, split out so the self-check can drive it
    /// with a prompt it made itself. Nothing is playing during a self-check, so
    /// <see cref="ApplySkipOffer"/> would always take the 「no offer」 branch and the bindings would never
    /// be looked at.
    /// </summary>
    internal void ShowSkipPrompt(SkipPrompt prompt)
    {
        SkipOffered = prompt.Visible;
        if (!prompt.Visible) return;

        SkipCaption = prompt.Caption;
        SkipTip = prompt.Tip;
        SkipRemaining = prompt.Remaining;
    }

    /// <summary>
    /// Takes the standing offer: seek to the far side of the section and say what was skipped, in
    /// chapterskip.lua's wording. Shared by the button, by <c>Y</c>, and by the automatic jump — which
    /// arrives already decided, so the offer is not consulted a second time.
    /// </summary>
    private void AcceptSkip(SkipJump? decided)
    {
        if ((decided ?? _skips.Accept()) is not { } jump) return;

        // Marked in flight like a released drag: the seek below is exact and takes as long to land, and
        // the bar bouncing back to the pre-skip position before arriving at the target is the same bug.
        // Only with a duration to divide by — without one there is no fraction to wait for, and the
        // ordinary follow (「nothing in flight」) is the honest answer.
        if (Status.HasDuration)
        {
            _seekSent = Math.Clamp(jump.Target / Status.Duration, 0, 1);
            _seekSentAt = Now;
        }

        _ = _playback.CommandAsync(
            "seek",
            jump.Target.ToString("0.###", CultureInfo.InvariantCulture),
            "absolute+exact");
        _ = _playback.CommandAsync("show-text", jump.Notice, "2500");

        SkipOffered = false;
    }

}
