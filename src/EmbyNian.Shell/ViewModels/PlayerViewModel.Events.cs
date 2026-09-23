using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Threading;
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

    /// <summary>
    /// 独占模式视频窗里 Lua UI（uosc 嵌入版）的请求。五条业务消息：换集（±1，走 Emby 的单集导航，
    /// 与控制窗的上一集/下一集同一句话）；要选集菜单（宿主把本季单集经 open-menu 推回给 uosc 画，
    /// 这一项是宿主唯一的数据源）；点选菜单里的某集（1 起算的序号）；要版本菜单与点选某一版（同一个形状，
    /// 数据源是条目自己的媒体源表）。就绪握手由句柄侧记档。
    /// 进度条拖动不在此列 —— uosc 直接对 mpv 发 seek，宿主从属性观察收到结果，不必经手。
    /// 消息从 mpv 的事件线程直接进来，先落界面线程；换集与换版交给
    /// <see cref="StepEpisodeAsync"/>/<see cref="SwitchEpisode"/>/<see cref="SwitchVersion"/> 自己的守卫，
    /// 这里不再另设一层 —— 双保险比一层保险更难排障。
    /// </summary>
    private void OnVideoWindowMessage(string key, string value) => OnUi(() =>
    {
        if (!_playback.IsPlaying) return;

        switch (key)
        {
            case VideoWindowContract.Episode:
                _ = StepEpisodeAsync(int.Parse(value, CultureInfo.InvariantCulture));
                break;

            case VideoWindowContract.Episodes:
                // 幂等动作过闸：同一段窗口里只回推一份菜单。脚本自激时（2026-09-19 那次）这里就是
                // 宿主的活口 —— 挡下的条数进日志，不是静默吞掉。
                if (_episodeMenuGate.TryAccept(DateTime.UtcNow))
                {
                    _ = PushEpisodeMenuAsync();
                }
                else if (_episodeMenuGate.ShouldReport(DateTime.UtcNow))
                {
                    Log.Warn(Category,
                        $"选集菜单请求被闸门挡下（累计 {_episodeMenuGate.Suppressed} 条）——视频窗脚本可能在刷屏");
                }
                break;

            case VideoWindowContract.EpisodeIndex when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index):
                if (index >= 1 && index <= Episodes.Count) SwitchEpisode(Episodes[index - 1]);
                break;

            case VideoWindowContract.Versions:
                // 版本菜单同一条规矩、自己的那道闸：菜单是幂等的，而自激的脚本不问它是哪一扇菜单。
                if (_versionMenuGate.TryAccept(DateTime.UtcNow))
                {
                    _ = PushVersionMenuAsync();
                }
                else if (_versionMenuGate.ShouldReport(DateTime.UtcNow))
                {
                    Log.Warn(Category,
                        $"版本菜单请求被闸门挡下（累计 {_versionMenuGate.Suppressed} 条）——视频窗脚本可能在刷屏");
                }
                break;

            case VideoWindowContract.VersionIndex when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version):
                // 1 起算的序号对着推送那一份菜单的次序；越界与垃圾由 MediaVersionSwitch.At 挡成 null，
                // 而「点了正在放的那一版」由 SwitchVersion 自己吞掉 —— 这道守卫不在这儿再抄一遍。
                SwitchVersion(MediaVersionSwitch.At(_nowPlaying, version));
                break;

            case VideoWindowContract.PictureMenu:
                // 画面菜单同选集/版本那一套：幂等、自激时挡下并记数。数据源是 PlayerMenuCatalog（集成模式
                // 右键那张同一份树），所以两条管线的画面菜单一模一样。
                if (_pictureMenuGate.TryAccept(DateTime.UtcNow))
                {
                    _ = PushPictureMenuAsync();
                }
                else if (_pictureMenuGate.ShouldReport(DateTime.UtcNow))
                {
                    Log.Warn(Category,
                        $"画面菜单请求被闸门挡下（累计 {_pictureMenuGate.Suppressed} 条）——视频窗脚本可能在刷屏");
                }
                break;

            case VideoWindowContract.MenuIndex when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var row):
                // 1 起算的序号对着 PlayerMenuCatalog.Commands（宿主推送时按同一次序编号）。越界的丢掉，
                // 命中的交给 RunMenuNodeAsync —— 与集成模式右键点同一行走的是同一句执行（命令＋${property} 提示）。
                if (row >= 1 && row <= PlayerMenuCatalog.Commands.Count)
                    _ = RunMenuNodeAsync(PlayerMenuCatalog.Commands[row - 1]);
                break;
        }
    });

    /// <summary>
    /// 把 <see cref="PlayerMenuCatalog"/> 那张树推给视频窗的 uosc 画成菜单 —— 集成模式右键用的同一份，所以
    /// 「参考集成模式」在这里是字面意义上的同一个数据源。命令行的 <c>value</c> 是回宿主的一条
    /// <see cref="VideoWindowContract.MenuIndex"/>（序号对着 <see cref="PlayerMenuCatalog.Commands"/> 的次序），
    /// 点中时宿主用 <c>RunMenuNodeAsync</c> 跑 —— 多条命令的「重置」行与 <c>${property}</c> 提示都靠它，
    /// uosc 那头单条 value 表达不了，所以统一回宿主执行。
    /// </summary>
    private async Task PushPictureMenuAsync()
    {
        var command = 0;
        var items = BuildPictureMenuItems(PlayerMenuCatalog.Root, ref command);
        await SendMenuAsync("picture", "画面", items).ConfigureAwait(true);
    }

    /// <summary>
    /// 一层目录变成 uosc 菜单行，子菜单递归。命令叶子按 DFS 先序编号（与 <see cref="PlayerMenuCatalog.Commands"/>
    /// 的次序对齐，测试钉住两者一致）；分隔线画在它前一行上（uosc 的分隔是「这一行之后画条线」的属性，不是独立行）；
    /// 组是子菜单、本身不可点。
    /// </summary>
    private static List<UoscMenuItem> BuildPictureMenuItems(IReadOnlyList<PlayerMenuNode> nodes, ref int command)
    {
        var items = new List<UoscMenuItem>(nodes.Count);

        foreach (var node in nodes)
        {
            switch (node.Kind)
            {
                case PlayerMenuKind.Separator:
                    // uosc 没有独立的分隔行，只有「这一行下面画条线」——落在前一行上。开头就是分隔线、
                    // 或前一行已经被标过，都当没有（菜单里两条紧挨的分隔线本就不该出现）。
                    if (items.Count > 0 && items[^1].Separator != true)
                        items[^1] = items[^1] with { Separator = true };
                    break;

                case PlayerMenuKind.Group:
                    var children = BuildPictureMenuItems(node.Children, ref command);
                    // 子菜单那一行必须可选中（selectable 默认真、这里显式给真）——否则 uosc 里点不开它；
                    // 它没有 value（点它是进子菜单，不是跑命令），items 一有值 uosc 就当它是子菜单。
                    items.Add(new UoscMenuItem(node.Label, null, true, false, Items: children));
                    break;

                default:
                    command++;
                    items.Add(new UoscMenuItem(
                        node.Label,
                        $"script-message {VideoWindowContract.MenuIndex} {command.ToString(CultureInfo.InvariantCulture)}",
                        true,
                        false));
                    break;
            }
        }

        return items;
    }

    /// <summary>
    /// 把本季单集推给视频窗的 uosc 画成菜单。当前集打上 active；没有集列表（非剧集、列表没到手）也回
    /// 一份菜单，放一行说明而不是让按钮看起来是死的。
    /// </summary>
    private async Task PushEpisodeMenuAsync()
    {
        List<UoscMenuItem> items = Episodes.Count == 0
            ? [new UoscMenuItem("（这一场没有可用的集列表）", null, false, false)]
            : [.. Episodes.Select((episode, index) => new UoscMenuItem(
                episode.ToPlaybackTitle(),
                $"script-message {VideoWindowContract.EpisodeIndex} {index + 1}",
                true,
                string.Equals(episode.Id, PlayingItemId, StringComparison.Ordinal)))];

        await SendMenuAsync("episodes", "选集", items).ConfigureAwait(true);
    }

    /// <summary>
    /// 把这一条目的版本推给视频窗画成菜单：4K HDR、1080p、导演剪辑版……正在放的那一版打上 active。
    /// <para>
    /// 与选集菜单同一个形状、同一套规矩，只有两处不同。其一，右边那列暗字带着画质（<c>4K · HEVC · 8.4 GB</c>）——
    /// 选集行上写的是集名与集号，而两个版本的名字可以一模一样（「4K HDR」与「4K HDR 修复版」），
    /// 视频窗里又没有详情页那张媒体信息表可看，所以这一列是唯一分得清两份文件的地方。其二，
    /// 只有一版时也回一份菜单，那一行是「没有可切换的版本」而不是让菜单看起来没打开。
    /// </para>
    /// </summary>
    private async Task PushVersionMenuAsync()
    {
        var versions = Versions;

        List<UoscMenuItem> items = versions.Count <= 1
            ? [new UoscMenuItem("没有可切换的版本", null, false, false)]
            : [.. versions.Select((source, index) => new UoscMenuItem(
                ItemDetail.SourceLabel(source),
                $"script-message {VideoWindowContract.VersionIndex} {index + 1}",
                true,
                MediaVersionSwitch.Same(source, PlayingSource),
                QualityTail(source)))];

        await SendMenuAsync("versions", "版本", items).ConfigureAwait(true);
    }

    /// <summary>
    /// 一份 uosc 菜单的推送。uosc 的 <c>open-menu</c> 吃一份 JSON（type/title/items），点中一项时它把该项的
    /// <c>value</c> 当 mpv 命令执行 —— 所以每一项的 value 就是一条回宿主的 <c>script-message</c>，
    /// 序号对应这一份菜单里的次序。
    /// </summary>
    private async Task SendMenuAsync(string type, string title, IReadOnlyList<UoscMenuItem> items)
    {
        var json = JsonSerializer.Serialize(new UoscMenu(type, title, items), UoscMenuJson.Options);

        if (!await _playback.CommandAsync("script-message", "open-menu", json).ConfigureAwait(true))
            Log.Warn(Category, $"推送「{title}」菜单到视频窗失败");
    }

    /// <summary>菜单行右边那列暗字：这份文件到底是什么。问不出画质就交 null，不写一个空字符串上去。</summary>
    private static string? QualityTail(MediaSource source) =>
        source.ToQualityLabel() is { Length: > 0 } quality ? quality : null;

    /// <summary>uosc open-menu 的菜单形状，与 uosc MenuData 的字段对齐（多出来的字段会被忽略）。</summary>
    private sealed record UoscMenu(string Type, string Title, IReadOnlyList<UoscMenuItem> Items);

    /// <summary>
    /// 菜单里的一行。<see cref="Hint"/> 是右边那列暗字（uosc 的 <c>hint</c>）：选集用不上（值里已经有
    /// 集号），版本用得上 —— 那正是「两份文件同一个名字」时唯一说得清的地方。<see cref="Items"/> 有值时这一行
    /// 是子菜单（画面菜单的分组用），<see cref="Separator"/> 为真时在这一行下方画一条分隔线（uosc 的分隔是
    /// 行属性，不是独立行）。两者都只在写非默认值时进 JSON（见 <see cref="UoscMenuJson"/>）。
    /// </summary>
    private sealed record UoscMenuItem(
        string Title,
        string? Value,
        bool Selectable,
        bool Active,
        string? Hint = null,
        IReadOnlyList<UoscMenuItem>? Items = null,
        bool? Separator = null);

    private static class UoscMenuJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            // uosc 的 parse_json 认小写字段；DefaultIgnoreCondition 让 Selectable=false 的项不发 value。
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }

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

        // 换版在途标记同样在这一刻放开 —— 它挡的是「同一下点两遍」，不是整场播放。
        // 少了这一句，「换到版本 2 再切回版本 1」永远不生效（2026-09-20 用户报的就是这个）：
        // StartVersion 的守卫看的是这个标记，而发起换版那一路的 finally 要等 **整场播放** 结束才返回，
        // 中间那几个小时里标记一直挂着，于是菜单里点哪一版都没反应。
        // 不去比「宣布的这一版是不是我刚才点的那一版」：服务器可能回退到另一个候选打开，
        // 那之后还必须点得动 —— 换集那条能比是因为它比的是播的哪一集，这里的回退恰恰是常规情形。
        if (item is not null) _versionSwitchTarget = null;
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

        // 控制条中间那行读数是**正在放的那一版**的，见 PlayingSourceLabel。
        SourceLabel = PlayingSourceLabel(item);
        EpisodeControlsVisible = item.Type == EmbyItemType.Episode
            && (Episodes.Count > 1 || !string.IsNullOrEmpty(item.SeriesId));

        // 有没有第二版可换。「版本」按钮只在这一格为真时露面（控制条上那个按钮绑的就是它），
        // 独占模式视频窗里的那条菜单项则不受它管 —— uosc 的控件列表是静态的，露出与否由
        // 那一份菜单自己说（只有一版时回一行「没有可切换的版本」）。
        //
        // 这一格跟着「手上这个条目」走，不在这里自己算（2026-09-21）。拿到的 item 可能是初始化对象
        // （浏览级元数据没有媒体源），而 FillSiblingsAsync 后来会把服务器那条更全的换上去；
        // 两边各算各的就会打架，见 CurrentItem 与 VersionControlsVisible 的注释。
        CurrentItem = item;

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

        // 遮罩垫底的背景图也跟着换：开播/换集这一刻开始取，遮罩揭掉之前它就位；取不到退回纯色
        // （2026-09-15「视频刚开播还在加载缓存没有正片画面时背景要用背景图」）。
        _ = LoadCoverBackdropAsync(item);
        ApplySkipOffer();
    });

    /// <summary>
    /// 控制条中间那行「1080p · HEVC · 8.4 GB」说的是哪一份文件。
    /// <para>
    /// 问的必须是<b>正在放的那一版</b>，不是 <c>MediaSources[0]</c>。单版本的条目上两者恰好是同一份文件，
    /// 所以「问第一个」这个写法一直没露馅；条目一旦挂了两版、用户又换过版，那行就会一直报第一版的画质 ——
    /// 屏上看到的是「换了版本，中间那行纹丝不动」，而真实的画面已经换成另一份文件了。认不出在播的是哪一版
    /// （外部 mpv.exe 后端不经过这里的启动记账、或者刚起播还没落定）时才退回第一版。
    /// </para>
    /// </summary>
    private string PlayingSourceLabel(EmbyItem? item)
    {
        var source = MediaVersionSwitch.Playing(item, _playback.PlayingSource)
            ?? item?.MediaSources.FirstOrDefault();

        return source?.ToQualityLabel() ?? "";
    }

    /// <summary>
    /// 遮罩垫底的背景图（2026-09-15「视频刚开播还在加载缓存没有正片画面时背景要用背景图」）。按
    /// 本集背景图 → 父级（季/剧）背景图 → 缩略图 的顺序取一张 —— 单集大多没有自己的 Backdrop，
    /// 继承链正是详情页用的那一套；1280 那一档全屏铺满够用，又落在磁盘缓存最常命中的宽度上。
    /// <para>
    /// 调用时机有两处：StartPlaybackAsync 遮罩亮起的同一刻（拿用户点的卡片，媒体信息还在路上），
    /// 和 OnNowPlayingChanged（详情就绪、字段更全）。按条目 Id 去重：同一个 Id 只取一遍，第二处
    /// 调用对已在路上的那次是空操作。取空（服务器没图、404）或取挂了就忘掉这个 Id —— 详情阶段的
    /// 重试还带着更全的图片字段；取到图才记住。代际号在两处 await 之后各对一次：换集很快连按的
    /// 时候，晚到的上一场下载不许盖到这一场上。垫底退回纯色遮罩，图是添头不是承重墙。
    /// </para>
    /// </summary>
    private async Task LoadCoverBackdropAsync(EmbyItem? item)
    {
        if (item is null)
        {
            _coverBackdropItemId = null;
            CoverBackdrop = null;
            return;
        }

        // 这一部已经在取（或已就位）：同一 Id 的第二遍是重复下载，图也一样。
        if (string.Equals(_coverBackdropItemId, item.Id, StringComparison.Ordinal)) return;

        _coverBackdropItemId = item.Id;
        var generation = ++_coverGeneration;

        try
        {
            var bytes = await PickCoverBackdropBytesAsync(item, EmbyImageStore.RequestWidth(1280)).ConfigureAwait(true);
            if (bytes is null)
            {
                // 这张卡上没有可用的图片字段（浏览级元数据常常没有父级继承链）——忘掉，好让详情
                // 就绪后 OnNowPlayingChanged 那一遍用更全的字段再试一次。
                if (generation == _coverGeneration) _coverBackdropItemId = null;
                return;
            }
            if (generation != _coverGeneration) return;

            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            if (generation != _coverGeneration) return;

            CoverBackdrop = image;
        }
        catch (Exception ex)
        {
            if (generation == _coverGeneration) _coverBackdropItemId = null;
            Log.Info(Category, $"遮罩背景图取用失败（{ex.Message}），垫底退回纯色");
        }
    }

    private async Task<byte[]?> PickCoverBackdropBytesAsync(EmbyItem item, int width)
    {
        if (item.BackdropImageTags.Count > 0)
        {
            var own = await _images.GetAsync(item.Id, EmbyImageStore.Backdrop, item.BackdropImageTags[0], width, CancellationToken.None).ConfigureAwait(true);
            if (own is not null) return own;
        }

        if (item is { ParentBackdropItemId: { Length: > 0 } parent, ParentBackdropImageTags.Count: > 0 })
        {
            var inherited = await _images.GetAsync(parent, EmbyImageStore.Backdrop, item.ParentBackdropImageTags[0], width, CancellationToken.None).ConfigureAwait(true);
            if (inherited is not null) return inherited;
        }

        if (item.ThumbImageTag is { Length: > 0 } thumb)
            return await _images.GetAsync(item.Id, EmbyImageStore.Thumb, thumb, width, CancellationToken.None).ConfigureAwait(true);

        return null;
    }

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
        // 但「loaded 而仍在缓冲」（paused-for-cache）还不算真画面：刚开播还在加载缓存的那段继续垫着
        // 背景图（2026-09-15「视频刚开播还在加载缓存没有正片画面时背景要用背景图」），缓冲退了才揭。
        if (status.Loaded && !status.Buffering) HideCover();

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
    /// <para>
    /// 第一件事是把滑杆摆到这一档的刻度上（<see cref="VolumeScale.Axis"/>），两个例外写在下面。
    /// </para>
    /// </summary>
    partial void OnVolumeChanged(double value)
    {
        // 位置归手的两种情形不写回去：用户正拖着滑块（写回去等于跟手打架），以及滚轮刚把半格留在轴上
        // （那半格是它下一次要接着走的，抹平了就等于每一格都从头开始，100→101 永远走不到）。mpv 回读那一档
        // 必须写 —— 它是 _pushing 里的，同样走得到这里。
        if (!_axisByHand && !_axisByRoll) VolumeAxis = VolumeScale.Axis(value);

        if (_pushing) return;

        var level = Math.Clamp(Math.Round(value), 0, AudioSettings.MaxVolume);
        _ = _playback.SetPropertyAsync("volume", level);

        // Kept for the next file as well as sent to this one. Every playback launches a fresh mpv with its
        // own config blocked, so a level nobody wrote down is 100 again by the next episode.
        _volumePending = (int)level;
        _volumeTouched = Now;
    }

    /// <summary>
    /// 滑杆自己给的位置：用户拖着它，或者滚轮刚在轴上走了一格（后者由 <see cref="RollVolume"/> 标了
    /// <c>_axisByRoll</c>，直接返回）。换算回音量（<see cref="VolumeScale.Level"/>）再就近取整 —— 落在
    /// 100 与 101 之间那半格时给的是小数，两档各占半格，就近收正是不偏不倚的分界（100.5 归 101）。
    /// <para>
    /// <c>_pushing</c> 那一档不接：那是 mpv 回读把滑块摆回刻度点（见 <see cref="OnVolumeChanged"/>），
    /// 这里要是也认，两个属性会互相写回去。
    /// </para>
    /// </summary>
    partial void OnVolumeAxisChanged(double value)
    {
        if (_axisByRoll || _pushing) return;

        var level = Math.Round(VolumeScale.Level(value), MidpointRounding.AwayFromZero);
        if (Math.Abs(level - Volume) < 0.001) return;

        _axisByHand = true;
        try
        {
            Volume = level;
        }
        finally
        {
            _axisByHand = false;
        }
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

        // The whole list in one call, rather than two reads per chapter: on the external backend
        // that walk was a JSON round trip a question — twenty chapters made forty-two — and the
        // poll interval wrapped around it could lapse mid-list.
        var chapters = await _playback.GetChaptersAsync().ConfigureAwait(true);

        if (generation != _generation) return true;

        // A shortfall (a dropped pipe, a backend without the batched read) is 「not ready」, not
        // 「nothing there」 — the count above just said otherwise, so keep asking until the
        // attempts run out, which is the same corner a stuck backend lands in.
        if (chapters.Count < count.Value) return false;

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
