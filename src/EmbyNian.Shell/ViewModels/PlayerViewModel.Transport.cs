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
/// 播放器的传输控制：播放、暂停、跳转这几条命令，以及一次播放的开始与结束。
/// <para>
/// 「开始播放」是这个类里最长的一条路 —— 问回条目、挑轨道、算档位、交给后端、开始上报，中间每一步都可能失败而屏上要说得出话。
/// </para>
/// <para>
/// 2026-09-05 从 <c>PlayerViewModel.cs</c>（当时 2227 行）切出来的一片，<b>正文一字节没动</b>：切的位置
/// 就是那个文件里本来就画好的分节线，所以这一次没有任何一处需要判断某个成员归谁。字段和构造函数留在主
/// 文件里 —— 它们是这一族共用的东西，散开就再也数不清谁在改哪个。
/// </para>
/// </summary>
public sealed partial class PlayerViewModel
{
    // ---- the transport, as commands ----------------------------------------------
    //
    // Synchronous on purpose, every one of them: an AsyncRelayCommand disallows a second run while the
    // first is outstanding, and 「a run」 here lasts as long as the film does — so the button bound to it
    // would grey out for the whole playback. They start the work and return. Internal rather than private
    // so the keyboard can call the same method the button binds to.

    /// <summary>停止播放.</summary>
    [RelayCommand]
    internal void Stop() => _ = StopAsync();

    /// <summary>播放/暂停.</summary>
    [RelayCommand]
    internal void TogglePause() => SetPaused(!Status.Paused);

    [RelayCommand]
    internal void PreviousEpisode() => _ = StepEpisodeAsync(-1);

    [RelayCommand]
    internal void NextEpisode() => _ = StepEpisodeAsync(1);

    /// <summary>Takes the standing 跳过 offer.</summary>
    [RelayCommand]
    internal void TakeSkip() => AcceptSkip(null);

    // ---- starting and stopping ---------------------------------------------------

    /// <summary>
    /// 播放. The one entry point: a poster, a row, a context menu and the 选集 picker all come through
    /// here, so they cannot disagree about what pressing play means.
    /// </summary>
    internal async Task PlayAsync(
        EmbyItem item,
        EmbyItem? parent = null,
        PlaybackChoice? choice = null,
        IReadOnlyList<EmbyItem>? episodes = null)
    {
        try
        {
            await StartPlaybackAsync(item, parent, choice, episodes, replaceExisting: false).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Error(Category, "播放失败", error);
            Noticed?.Invoke($"播放失败：{error.Message}", InfoBarSeverity.Error);
            if (_playerHold == 0) LeavePlayer();
        }
    }

    /// <summary>
    /// 换片：把正在播的换成新点的一部，屏幕上的窗口一个都不变（2026-09-19 用户令「使用独占模式时可以
    /// 一边挂着片子一边继续翻媒体库」的另一半 —— 翻库翻到了想看的，点下去就该是它，而不是一句「请先停止」）。
    /// <para>
    /// 与 <see cref="PlayAsync"/> 只差 <c>replaceExisting</c> 那一位（连播下一集走的同一条换血管线），
    /// 外壳也一样：取消静默吞、异常上报、生命周期没人接就收播放层。调用方（<c>ShellPage</c>）只该在
    /// 无页面播放在途时走这里 —— 独占模式下播放器页已摘下让位，请求必须直递本视图模型，画面与控制
    /// 都在 mpv 的视频窗里，本端只管把新片子换上。
    /// </para>
    /// </summary>
    internal async Task PlayReplacingAsync(
        EmbyItem item,
        EmbyItem? parent = null,
        PlaybackChoice? choice = null,
        IReadOnlyList<EmbyItem>? episodes = null)
    {
        try
        {
            await StartPlaybackAsync(item, parent, choice, episodes, replaceExisting: true).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Error(Category, "播放失败", error);
            Noticed?.Invoke($"播放失败：{error.Message}", InfoBarSeverity.Error);
            if (_playerHold == 0) LeavePlayer();
        }
    }

    /// <summary>停止播放. Asks mpv to quit rather than cancelling, so the final position is still reported.</summary>
    internal async Task StopAsync()
    {
        // Before the wait: 「stop」 is one of the two ways out of the player, and the level the film was left
        // at has to reach the file whether or not another tick ever comes.
        FlushVolume(settled: false);

        if (!_playback.IsPlaying) return;
        await _playback.StopAsync().ConfigureAwait(true);
    }

    private async Task StartPlaybackAsync(
        EmbyItem item,
        EmbyItem? parent,
        PlaybackChoice? choice,
        IReadOnlyList<EmbyItem>? episodes,
        bool replaceExisting)
    {
        if (!replaceExisting && _playback.IsPlaying)
        {
            Noticed?.Invoke("已经有内容正在播放，请先停止", InfoBarSeverity.Warning);
            return;
        }

        if (_playback.Validate() is { } problem)
        {
            Noticed?.Invoke(problem, InfoBarSeverity.Error);
            return;
        }

        // Taken here, before the fetch and before the call that stops whatever is playing; released in
        // the finally once this playback has ended and any auto-advance has been decided.
        _playerHold++;
        try
        {
            // 需求 7's box, made useful before the strip it sits in can be revealed: the scan is a few
            // hundred file opens on a cold cache, and the settings window may have changed the family
            // since this row was built.
            PrepareFonts();
            SubtitleFont.Reseed(Settings.Playback.SubtitleFontFamily);

            // The player takes over the window immediately rather than after the metadata round trip.
            // There is nothing to look at on the page behind it — the user has already committed — and
            // the cover is a better place to say what is being waited for than a toast over a grid.
            // 画面位置（说明牌、置顶这些进场预设读的那一位）不再在这里预写缓存：<see cref="PictureInHostWindow"/>
            // 没有会话时按设置推算，公式本身就是「本次播放的意图」——这里曾有的手工缓存是它的手抄副本，
            // 且在「外部 mpv.exe 后端」上与后端的表态相互矛盾（2026-09-17 判据归一时删）。
            EnterPlayer();
            ShowCover(replaceExisting ? "正在切换…" : "正在获取媒体信息…");

            // 背景图垫底从这一刻就开始取，不等媒体信息：用户点的卡片已经在手（海报用的就是它的图片
            // 字段），等 OnNowPlayingChanged 才动手，加载态里遮罩就只剩纯色了（2026-09-15 用户截图
            // 「第二点貌似没有生效」正是这个窗口）。详情就绪后 OnNowPlayingChanged 那一遍按 Id 去重、
            // 取空会重试，这里不用等它。剧集卡先取剧集自己的背景图 —— 播的正是它。
            _ = LoadCoverBackdropAsync(item);

            // A card fetched for browsing carries no MediaSources, and those are what hold the tracks,
            // the container and the runtime.
            var detail = item.MediaSources.Count > 0
                ? item
                : await _session
                    .ExecuteAsync((client, token) => client.GetItemAsync(item.Id, token), _lifetime.Token)
                    .ConfigureAwait(true);

            if (detail.Type is EmbyItemType.Series or EmbyItemType.Season)
            {
                // A series or a season is not a playable item, but the play badge on its poster means
                // 「开始看这部剧」 — the next unwatched episode. Resolved here rather than by the caller so
                // the home shelves, the library, the search results and the context menu all agree on
                // what that click does, and so 「选集」 gets the sibling list for free.
                ShowCover("正在查找可播放的单集…");

                if (await ResolveNextEpisodeAsync(detail).ConfigureAwait(true) is not { } resolved)
                {
                    Noticed?.Invoke("这部剧集下没有可播放的单集", InfoBarSeverity.Warning);
                    return;
                }

                // The choice is dropped deliberately: it describes a media source of the item the user
                // was looking at, and that item is not the file about to be played. A series card is
                // the parent for the shader rule; a season card leaves it to ResolveSeriesAsync.
                await StartPlaybackAsync(
                    resolved.Episode,
                    detail.Type == EmbyItemType.Series ? detail : parent,
                    choice: null,
                    resolved.Siblings,
                    replaceExisting).ConfigureAwait(true);
                return;
            }

            if (!detail.IsPlayable)
            {
                Noticed?.Invoke("这个项目不能直接播放", InfoBarSeverity.Warning);
                return;
            }

            if (detail.MediaSources.Count == 0)
            {
                Noticed?.Invoke("服务器没有返回可用的媒体源", InfoBarSeverity.Error);
                return;
            }

            var source = choice?.Source ?? detail.MediaSources[0];
            var resumeTicks = Settings.Playback.ResumeFromSavedPosition ? detail.ResumeTicks : 0;

            Episodes = episodes ?? [];
            _parent = parent;
            PlayingItemId = detail.Id;

            // 手上这个条目 —— 详情这一份是这条路手上最全的（2026-09-21）。写在这里，「有几版」与
            // 有没有第二版可换就是同一次赋值的两个结果（见 VersionControlsVisible 的注释）；
            // OnNowPlayingChanged 随后会再写一遍，用的是它拿到的那个 item。
            CurrentItem = detail;

            // 「从继续观看点击播放后，无法切换上下集」: a shelf row is a flat set of resume points across every
            // show, so the page that started this playback had no sibling list to hand over. Asked for here
            // rather than by each caller — the search results and the mixed 最近添加 grids have the same
            // nothing to offer — and not awaited, because the file does not wait on it.
            if (Episodes.Count == 0 && detail.Type == EmbyItemType.Episode) _ = FillSiblingsAsync(detail);

            var parentItem = parent ?? await ResolveSeriesAsync(detail).ConfigureAwait(true);
            var output = PrepareShaderPlans(detail, source, parentItem);

            var ticket = new PlaybackTicket
            {
                Item = detail,
                Source = source,
                AudioStreamIndex = choice?.AudioStreamIndex,
                SubtitleStreamIndex = choice?.SubtitleStreamIndex,
                SubtitlesDisabled = choice?.SubtitlesDisabled ?? false,
                StartTicks = choice?.StartTicks ?? resumeTicks,
                Parent = parentItem,
                OutputWidth = output.Width,
                OutputHeight = output.Height,
                DisplayRefreshHz = MeasureRefreshHz?.Invoke() ?? 0
            };

            SubtitleDelay = 0;
            AudioDelay = 0;


            var result = await _playback.PlayAsync(ticket, _lifetime.Token).ConfigureAwait(true);

            // 「播放已停止」 belongs to the end of a viewing, not to the seam between two episodes: the
            // switch already says what it is doing, and two toasts stacked over a half-built player were
            // part of what 「画面错乱」 looked like.
            var following = NextEpisodeToAutoPlay(result, detail);
            if (following is null)
            {
                Noticed?.Invoke(
                    result.ToChinese(),
                    result.Exit.IsFailure ? InfoBarSeverity.Error : InfoBarSeverity.Success);
            }

            // The item's watched flag and resume position have just changed on the server.
            RefreshRequested?.Invoke();

            if (following is not null)
            {
                Noticed?.Invoke($"自动播放下一集：{following.Episode.ToPlaybackTitle()}", InfoBarSeverity.Informational);
                await StartPlaybackAsync(
                        following.Episode,
                        _parent,
                        choice: null,
                        following.Siblings,
                        replaceExisting: true)
                    .ConfigureAwait(true);
            }
        }
        finally
        {
            _playerHold--;

            // The hold is what kept the player up; if this was the last one and nothing took over, here
            // is where it finally comes down. Skipped while a nested playback runs, because that one
            // holds its own.
            if (_playerHold == 0 && !_playback.IsPlaying) LeavePlayer();
        }
    }

    /// <summary>
    /// 自动播放下一集: the next episode <b>within this season</b>, or null when the season is over and the
    /// player should simply go away.
    /// <para>
    /// 「当最后一季的最后一集播放结束后，直接退出播放界面或返回，不得自动续播该最后一季的第一集」
    /// （用户令，2026-09-18）：旧版在季末会退而求其次去问全剧列表，把下一季的第一集接上来 ——
    /// Re:Zero 的 S1E83（本季唯一一集）播完自动跳 S04E12 就是这条路的产物。跨季的自动接播就此取消，
    /// <see cref="EpisodeNavigation.StepInSeason"/> 把决策钉死在本季之内；跨季仍归上一集/下一集按钮
    /// （<see cref="StepEpisodeAsync"/> → <see cref="ResolveAdjacentEpisodeAsync"/>）。
    /// </para>
    /// </summary>
    private EpisodeDestination? NextEpisodeToAutoPlay(PlaybackResult result, EmbyItem played)
    {
        if (!Settings.Playback.AutoPlayNextEpisode) return null;

        // Only a file that ran out. Quitting mpv, stopping from the UI and a failed launch all mean the
        // user is done with the episode rather than waiting for the next one.
        if (result.Exit.Reason != PlaybackEndReason.EndOfFile) return null;
        if (_lifetime.IsCancellationRequested) return null;

        // 季内还有下一集就连；本季到此为止（列表到头、当前集不在列表里、兄弟列表没到手）都返回 null，
        // 走「退出播放界面」那条路 —— 见 <see cref="StartPlaybackAsync"/> 的 finally。
        return EpisodeNavigation.StepInSeason(Episodes, played.Id, 1);
    }

    /// <summary>
    /// What 「播放」 on a series or a season poster means: the first episode nobody has watched, together
    /// with its season's episode list for the 「选集」 picker. Picks the same file the detail page picks,
    /// so the badge and the page do not start different things.
    /// </summary>
    private async Task<(EmbyItem Episode, IReadOnlyList<EmbyItem> Siblings)?> ResolveNextEpisodeAsync(EmbyItem item)
    {
        var seriesId = item.Type == EmbyItemType.Series ? item.Id : item.SeriesId;
        if (string.IsNullOrEmpty(seriesId)) return null;

        // A season card already says which season; a series card has to choose one.
        var seasonId = item.Type == EmbyItemType.Season ? item.Id : null;

        if (seasonId is null)
        {
            var seasons = await _session
                .ExecuteAsync((client, token) => client.GetSeasonsAsync(seriesId, token), _lifetime.Token)
                .ConfigureAwait(true);

            // The same rule the detail page opens on — 「the first season with something left in it, 特辑
            // aside」 — read from the one place that states it, so a poster and the page it opens cannot
            // start different episodes. A show with no season rows at all keeps a null season, which asks
            // the server for every episode it has.
            if (ItemDetail.PickSeason(seasons) is { } opening) seasonId = opening.Id;
        }

        var episodes = await _session
            .ExecuteAsync((client, token) => client.GetEpisodesAsync(seriesId, seasonId, token), _lifetime.Token)
            .ConfigureAwait(true);

        if (episodes.Count == 0) return null;

        var next = episodes.FirstOrDefault(episode => episode.UserData?.Played != true) ?? episodes[0];
        return (next, episodes);
    }

    /// <summary>
    /// Fetches the series row behind an episode. Emby's episode records usually carry no genres of
    /// their own, so without this the anime shader rule would never fire on a TV show.
    /// </summary>
    private async Task<EmbyItem?> ResolveSeriesAsync(EmbyItem item)
    {
        if (item.Type != EmbyItemType.Episode || string.IsNullOrEmpty(item.SeriesId)) return null;

        try
        {
            return await _session
                .ExecuteAsync((client, token) => client.GetItemAsync(item.SeriesId!, token, EmbyFields.Browse), _lifetime.Token)
                .ConfigureAwait(true);
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"读取所属剧集信息失败：{error.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fetches the season's episode list for a playback that arrived without one, so 选集 and 上一集/下一集
    /// work wherever the play came from rather than only from a page that happened to be holding a list.
    /// <para>
    /// The list is applied only if the same episode is still the one playing: the request and the file open
    /// in parallel, and a list belonging to the previous episode would have 下一集 play something out of
    /// another show. It also has to contain the playing episode, or stepping from it has no anchor.
    /// </para>
    /// </summary>
    private async Task FillSiblingsAsync(EmbyItem episode)
    {
        if (string.IsNullOrEmpty(episode.SeriesId)) return;

        try
        {
            // A null season asks for every episode in the series, which is what an episode record with no
            // season on it deserves: a slightly wider 选集 is better than a disabled one.
            var siblings = await _session
                .ExecuteAsync(
                    (client, token) => client.GetEpisodesAsync(episode.SeriesId!, episode.SeasonId, token),
                    _lifetime.Token)
                .ConfigureAwait(true);

            if (!string.Equals(PlayingItemId, episode.Id, StringComparison.Ordinal)) return;
            if (!siblings.Any(item => string.Equals(item.Id, episode.Id, StringComparison.Ordinal))) return;

            Episodes = siblings;

            // OnNowPlayingChanged has already decided this from an empty list by the time the answer
            // arrives, so the two buttons it governs would stay hidden over a perfectly good list.
            EpisodeControlsVisible = true;

            // 手上这个条目换成服务器的记录 —— 它带的媒体源比调用方手里那份全（2026-09-21）。
            // EpisodeControlsVisible 上面是明写的，「有几版」却跟着这一次赋值自己走
            // （见 VersionControlsVisible 的注释）：这两件事从前分开写，于是自检逮到过一颗点开只有
            // 「没有可切换的版本」的按钮 —— 这台机器上这个 await 正好输给了 OnNowPlayingChanged，
            // 那一格被拿初始化对象重新算了一遍，它手上的 MediaSources 是空的。
            CurrentItem = episode;

            Log.Debug(Category, $"补齐本季单集列表：{siblings.Count} 集");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"读取本季单集列表失败：{error.Message}");
        }
    }

    /// <summary>切换集数, from the 选集 picker.</summary>
    internal void SwitchEpisode(EmbyItem episode)
    {
        if (string.Equals(episode.Id, PlayingItemId, StringComparison.Ordinal)) return;
        StartEpisode(new EpisodeDestination(episode, Episodes));
    }

    /// <summary>
    /// 换版本, from the 版本 picker: 这个条目的另一份文件（4K ↔ 1080p、剧场版 ↔ 导演剪辑版），
    /// 从同一处接着放。独占模式视频窗里那条菜单项也走到这里。
    /// <para>
    /// 位置与轨道这两条规矩与候选版本的自动重试一模一样（见 <see cref="PlaybackService.PlayAsync"/>）：位置照旧
    /// ——「看到哪儿」与版本无关；显式的音轨/字幕选择作废 —— 那些 Emby 流索引是对着旧一版挑的，新一版的流布局
    /// 未必对得上，改让 alang/slang 重新决定。换的是文件、不是片子，所以条目、所属剧集、上报身份全都不动。
    /// </para>
    /// <para>
    /// 不该换的两种情况（目标不是这个条目的一版、或者就是正在放的那一版）全在
    /// <see cref="MediaVersionSwitch.ShouldSwitch"/> 里，这里不抄第二遍。
    /// </para>
    /// </summary>
    internal void SwitchVersion(MediaSource? source)
    {
        if (!MediaVersionSwitch.ShouldSwitch(_nowPlaying, source, _playback.PlayingSource)) return;

        StartVersion(source!);
    }

    /// <summary>
    /// 一次换版只放一次进去（<c>_versionSwitchTarget</c>），与 <see cref="StartEpisode"/> 的守卫同一个意思：
    /// 双击菜单行、或者视频窗把同一条消息发两遍，都不该在两份文件上各起一次播放。
    /// </summary>
    private void StartVersion(MediaSource source)
    {
        if (_versionSwitchTarget is not null) return;

        _versionSwitchTarget = source;
        _ = StartVersionAsync(source);
    }

    private async Task StartVersionAsync(MediaSource source)
    {
        try
        {
            var item = _nowPlaying;
            if (item is null) return;

            var position = Status.HasPosition ? Status.Position : (double?)null;

            var choice = new PlaybackChoice(
                source,
                AudioStreamIndex: null,
                SubtitleStreamIndex: null,
                SubtitlesDisabled: false,
                StartTicks: MediaVersionSwitch.StartTicks(position, item.ResumeTicks));

            // replaceExisting: true —— 正在放的那一跑由这一条路接手（独占模式是同一个 mpv 换源、集成模式是停掉
            // 重开），与换集、与「挂着片子再点一部」共用同一条管线，这里不另建一条。
            Log.Info(Category,
                $"换版本：{ItemDetail.SourceLabel(source)}，从 {TimeFormat.Clock(choice.StartTicks)} 接着放");

            await StartPlaybackAsync(item, _parent, choice, Episodes, replaceExisting: true).ConfigureAwait(true);

            // 换到哪一版也是「手上这个条目」的一部分（2026-09-21）：这一条路上面那句带的是
            // replaceExisting: true，回来的路上 StartPlaybackAsync 未必重新取过详情，而 mpv 报的
            // PlayingSource 要等文件开起来才更新（见 PlayingSourceLabel）。于是「正在放的是哪一版」
            // 与「这个条目还有没有别的版」在菜单里都是这一格说了算 —— 见 VersionControlsVisible 的注释。
            CurrentItem = item;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Error(Category, "切换版本失败", error);
            Noticed?.Invoke($"切换版本失败：{error.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_versionSwitchTarget, source)) _versionSwitchTarget = null;
        }
    }

    /// <summary>上一集 / 下一集. The local season is walked first; only a boundary costs a server request.</summary>
    private async Task StepEpisodeAsync(int offset)
    {
        if (_episodeLookupBusy || _episodeSwitchTargetId is not null) return;

        if (_nowPlaying is not { Type: EmbyItemType.Episode } current)
        {
            Noticed?.Invoke("没有可切换的单集", InfoBarSeverity.Informational);
            return;
        }

        if (EpisodeNavigation.Step(Episodes, PlayingItemId, offset) is { } local)
        {
            StartEpisode(local);
            return;
        }

        _episodeLookupBusy = true;
        try
        {
            var playingItemId = PlayingItemId;
            var destination = await ResolveAdjacentEpisodeAsync(current, offset).ConfigureAwait(true);

            // The request ran beside playback. A second command may already have put another item on screen.
            if (!string.Equals(PlayingItemId, playingItemId, StringComparison.Ordinal)) return;

            if (destination is null)
            {
                Noticed?.Invoke(offset < 0 ? "已经是第一集" : "已经是最后一集", InfoBarSeverity.Informational);
                return;
            }

            StartEpisode(destination);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, "读取跨季单集失败", error);
            Noticed?.Invoke($"读取相邻季失败：{error.Message}", InfoBarSeverity.Warning);
        }
        finally
        {
            _episodeLookupBusy = false;
        }
    }

    private async Task<EpisodeDestination?> ResolveAdjacentEpisodeAsync(EmbyItem current, int offset)
    {
        if (string.IsNullOrEmpty(current.SeriesId)) return null;

        // A null season asks Emby for the whole series in broadcast order. EpisodeNavigation narrows the
        // destination back to its own season before it is handed to the player and 选集 menu.
        var seriesEpisodes = await _session
            .ExecuteAsync(
                (client, token) => client.GetEpisodesAsync(
                    current.SeriesId!,
                    seasonId: null,
                    cancellationToken: token),
                _lifetime.Token)
            .ConfigureAwait(true);

        return EpisodeNavigation.Step(seriesEpisodes, current.Id, offset);
    }

    private void StartEpisode(EpisodeDestination destination)
    {
        if (string.Equals(destination.Episode.Id, PlayingItemId, StringComparison.Ordinal)) return;
        if (_episodeSwitchTargetId is not null) return;

        _episodeSwitchTargetId = destination.Episode.Id;
        _ = StartEpisodeAsync(destination);
    }

    private async Task StartEpisodeAsync(EpisodeDestination destination)
    {
        try
        {
            await StartPlaybackAsync(
                    destination.Episode,
                    _parent,
                    choice: null,
                    destination.Siblings,
                    replaceExisting: true)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Error(Category, "切换单集失败", error);
            Noticed?.Invoke($"切换单集失败：{error.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            if (string.Equals(_episodeSwitchTargetId, destination.Episode.Id, StringComparison.Ordinal))
                _episodeSwitchTargetId = null;
        }
    }

    // ---- entering and leaving the player -----------------------------------------

    /// <summary>
    /// Gives the window over to the player. The visible half of this is the page's — see
    /// <see cref="PlayerShown"/>; what is here is the one flag that decides whether the 跳过 offer is live.
    /// </summary>
    private void EnterPlayer()
    {
        if (_playerUp) return;

        _playerUp = true;
        PlayerShown?.Invoke();

        Log.Debug(Category, "进入播放界面");
    }

    /// <summary>
    /// Drops everything this playback knew and asks the page to put the window back. Only ever from the
    /// one place that knows nothing is about to start in the stopped playback's place — see
    /// <c>_playerHold</c>. The state is cleared before <see cref="PlayerHidden"/> is raised, because the
    /// page answers that by redrawing the bar's ticks from <see cref="ChapterMarks"/>.
    /// </summary>
    private void LeavePlayer()
    {
        _playerUp = false;

        // 退出时把加载遮罩收掉（万一正好在加载中途退出）：这一趟返回浏览的无缝过渡由页面侧的留帧
        // 溶解接管（RetainFill/RetainDim，见 PlayerPage.Exit.cs 与 ExitWhenBrowseReady），不靠这块遮罩。
        HideCover();

        // 遮罩垫底的背景图**不在这里放下**（2026-09-20 用户报「给这个返回主页的页面也加上背景图」）。
        //
        // 它原来是跟着这一场播放一起丢的。问题是「遮罩亮起」与「新图到位」之间有一段空档：
        // 换片／换集／连播那几趟，遮罩是在 `_playerHold > 0` 的手里亮起来的（见 `OnNowPlayingChanged`），
        // 而那一刻新片那张图还在路上（一次网络往返加解码）。于是用户点「第 8 集 → 返回主页」这个来回里，
        // 看到的常常就是**一整片纯色的「正在切换…」**：实测那张截图 1513×851 整块 `#0C0E11`，一个像素的
        // 图都没有 —— 那不是「图取不到」，是遮罩亮起来的时候手上根本没有图。
        //
        // 留下上一张就补上了这个空档：同一部剧里换集，它本来就是对的那一张（剧集背景图）；换另一部片，
        // 也只是在遮罩上多停一两百毫秒的上一张 —— 比一片纯色好看，也比一片纯色诚实（那底下确实还是
        // 上一场的画面）。`LoadCoverBackdropAsync` 一拿到新的就换掉，取空则保持这一张。
        //
        // **去重记录仍然要忘掉**：不然下一场点同一部片，Id 去重会把它当成「图还在」直接跳过，
        // 新的一场就永远等不到自己的图。
        _coverBackdropItemId = null;

        // The other way out, and the usual one: the file ran to its end. Ticks stop with the player, so an
        // unsaved level would be lost here rather than a second late.
        FlushVolume(settled: false);

        Tracks = [];
        Episodes = [];
        _parent = null;
        _nowPlaying = null;
        PlayingItemId = "";
        _episodeLookupBusy = false;
        _episodeSwitchTargetId = null;
        _seekPending = null;
        _seekSent = null;
        _seekTouched = 0;
        _skips.Begin([], 0);

        // Everything the bar drew about this particular file. The stills especially: they are keyed by
        // chapter index, and the next file's chapter 3 is not this one's.
        ChapterMarks = [];
        _embyMarks = [];
        _chapterStills.Clear();
        ClearChapterPeek();
        StatsOpen = false;
        _aspect = 0;

        // 着色器档位 is per playback: the two prepared plans describe a file that is no longer open, and a tick
        // arriving after this must not act on a size change that belongs to the browsing window.
        _outputWatch = null;
        _shaderContext = null;
        _shaderPinned = false;
        ActiveShader = null;

        ApplyStatus(new PlayerStatus());
        PictureAspectChanged?.Invoke(0);
        PlayerHidden?.Invoke();

        Log.Debug(Category, "离开播放界面");
    }

    /// <summary>
    /// Covers the picture, and says why. Requirement 12: the video surface and mpv's child inside it
    /// both stay alive across an episode change — which is what stops the window collapsing and
    /// rebuilding — but it also means the last frame of the finished episode sits there while mpv tears
    /// one file down and opens the next at whatever size and format it turns out to be. Nothing is wrong
    /// with the decode; it is simply nobody's job to have painted over it. This is that job.
    /// </summary>
    private void ShowCover(string message)
    {
        CoverMessage = message;
        CoverUp = true;
    }

    /// <summary>
    /// Takes the cover away. Called the moment mpv reports the new file loaded, and from every path that
    /// leaves playback, so the cover cannot outlive what it was covering.
    /// </summary>
    private void HideCover() => CoverUp = false;

}
