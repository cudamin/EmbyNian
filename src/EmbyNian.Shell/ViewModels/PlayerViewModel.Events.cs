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
        // 迟到防线：旧一场（或同实例换片前的旧文件）的读数不许盖住现在这一场的标题。
        // 服务端在发出时就带上了代次；这里只认当前那一场。
        if (progress.Generation != _playback.Generation) return;
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
    /// <para>
    /// 2026-09-29 统一右键菜单后又添三条（见 <see cref="PushPictureMenuAsync"/>）：跳过档位
    /// （<see cref="VideoWindowContract.SkipMode"/>）、连播开关（<see cref="VideoWindowContract.AutoPlayNext"/>）
    /// 与播放信息（<see cref="VideoWindowContract.MediaInfo"/>）—— 集成模式右键菜单里「更多」那一棵
    /// 在独占模式的同款行，点中回宿主执行。
    /// </para>
    /// <para>
    /// 2026-10-01 再添一条：菜单末尾那三行「去设置里改」回 <see cref="VideoWindowContract.OpenSettings"/>，
    /// 值是 <see cref="PlayerSettingsLinks"/> 的令牌，宿主把那扇设置窗口开在该行那张卡上。
    /// </para>
    /// </summary>
    private void OnVideoWindowMessage(PlaybackUpdate<(string Key, string Value)> update) => OnUi(() =>
    {
        if (update.Generation != _playback.Generation || !_playback.IsPlaying) return;
        var (key, value) = update.Value;
        // 装载握手先于「在播」那一问：uosc 是在 mpv_initialize 时装上的，而文件在那之后才打开 ——
        // 握手到的时候片子往往还没开，可宿主手上已经有「这个条目有几版」了。那颗「版本」按钮要在画面
        // 出来之前就摆对，所以这一条不能让它被下面的闸挡掉（其余几条都是「播放中的请求」，照旧要闸）。
        if (key == VideoWindowContract.Ready)
        {
            NoteVersionCount();
            // 选集按钮同一个道理（2026-09-26 用户令「播放电影的时候不要显示这个按钮」）：画面还没出来
            // 就把「现在放的是不是单集」告诉 uosc，别等 OnNowPlayingChanged。
            NoteEpisodeCount();
            // 左上角第二行的文件信息也一样：握手到的时候宿主手上已经有条目与媒体源，趁画面还没出来把
            // 副标题摆对，别等第一次 OnNowPlayingChanged。
            NoteSubline();
            _ = PushNativeShortcutsAsync(force: true);
            return;
        }

        if (!_playback.IsPlaying) return;

        switch (key)
        {
            case VideoWindowContract.Shortcut:
                RunNativeShortcut(value);
                break;

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

            case VideoWindowContract.EpisodeIndex:
                SelectEpisodeMenu(value);
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

            case VideoWindowContract.VersionIndex:
                SelectVersionMenu(value);
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

            case VideoWindowContract.Shader:
                if (value == "auto") RestoreShaderPlan();
                else if (value == "off") ApplyShaderGroup(null);
                else if (ShaderCatalog.FirstOrDefault(group => group.Id == value) is { } shader) ApplyShaderGroup(shader);
                break;

            case VideoWindowContract.MenuIndex:
                SelectPictureMenu(value);
                break;

            case VideoWindowContract.SkipTake:
                // 独占模式那颗「跳过」按钮被点了（或 offer 立着时按了回车）。与集成模式那颗 XAML 按钮、
                // 快捷键回车同一句执行 —— 都走 AcceptSkip（seek 到片段另一端并 show-text）；offer 是否
                // 还立着由 SkipCoordinator 自己判。
                TakeSkip();
                break;

            case VideoWindowContract.SkipDismiss:
                // offer 立着时按了 Esc（独占模式那把运行期绑定送来的）。与集成模式同一句：
                // SkipCoordinator.Decline + 收起提示，且只要位置还在这一段里就不再冒出来。
                DismissSkip();
                break;

            case VideoWindowContract.SkipMode:
                // 右键画面菜单的「跳过片头片尾」子菜单点了一档（2026-09-29 统一右键菜单）。值域由 Parse
                // 收成 ask/auto/off；setter 落盘并经 ApplySkipOffer 让「跳过」按钮的 offer 当场跟上 ——
                // 与集成模式右键点同一档写的是同一格设置。
                SkipMode = value switch
                {
                    "auto" => SkipSectionMode.Auto,
                    "off" => SkipSectionMode.Off,
                    _ => SkipSectionMode.Ask
                };
                break;

            case VideoWindowContract.AutoPlayNext:
                // 右键画面菜单拨了「自动播放下一集」。值固定 toggle：拨到哪边由这里按当前值现算，
                // 不认菜单推送那一份旧值（推送与点击之间设置窗口可能改过它）。
                AutoPlayNextEpisode = !AutoPlayNextEpisode;
                break;

            case VideoWindowContract.MediaInfo:
                // 右键画面菜单点了「播放信息…」。独占模式播放页是摘下去的，弹窗归外壳（ShellPage 接
                // MediaInfoRequested 弹主窗口）；集成模式不走这条 —— 页面上的菜单行自己弹同一张。
                MediaInfoRequested?.Invoke();
                break;

            case VideoWindowContract.OpenSettings:
                // 右键画面菜单末尾那一行「去设置里改」被点了（2026-10-01）。令牌由 Parse 收窄过，这里再查
                // 一次表拿到那张卡 —— 与集成模式点同一行走的是同一个出口（RequestSettings），两条管线同一句。
                if (PlayerSettingsLinks.For(value) is { } settings) RequestSettings(settings);
                break;
        }
    });

    /// <summary>
    /// 把右键画面菜单推给视频窗的 uosc 画 —— 与集成模式右键（<c>PlayerPage.OnPictureMenuOpening</c>）<b>同一张</b>：
    /// 前半是 <see cref="PlayerMenuCatalog"/> 那棵目录（命令叶子按 DFS 先序编号，序号对着
    /// <see cref="PlayerMenuCatalog.Commands"/>，点中回 <see cref="VideoWindowContract.MenuIndex"/>、宿主用
    /// <c>RunMenuNodeAsync</c> 跑），后半是集成模式「更多」那一棵的同款 —— 版本…、着色器、跳过片头片尾、
    /// 自动播放下一集、播放信息…（2026-09-29 用户令「统一独占模式和集成模式的右键菜单选项」；在那之前
    /// 这里只推目录加一条置顶的着色器，两边差着一截）。
    /// <para>
    /// 「更多」的那几行在两条管线里各有各的拼法（集成侧是 XAML 行、走 <c>PlayerPage.OnMoreMenuOpening</c>，
    /// 这里是 uosc 菜单行），但数据源与执行是同一份：版本开的是同一张版本菜单、着色器/跳过写的是同一格
    /// 设置、连播同一格、播放信息同一份正文。**改任何一边的行集、次序或文案，另一边要跟上** —— 两处
    /// 旁边的注释就是互相指认的路标。
    /// <para>
    /// 末尾「播放设置」中的字幕／视频输出／音频输出共用 <see cref="PlayerSettingsLinks"/> 的名称与行集。
    /// </para>
    /// </summary>
    private async Task PushPictureMenuAsync()
    {
        var context = CaptureInteraction();
        var request = ++_pictureMenuRequest;
        var checks = await ReadMenuChecksAsync().ConfigureAwait(true);
        if (!IsCurrentInteraction(context) || request != _pictureMenuRequest) return;
        var snapshot = new VideoMenuSnapshot<PlayerMenuNode>(PlayerMenuCatalog.Commands);
        _pictureMenuSnapshot = snapshot;
        _pictureMenuContext = context;

        var command = 0;
        var items = BuildPictureMenuItems(PlayerMenuCatalog.Root, checks, snapshot, ref command);

        // 目录与「更多」之间的一条分隔线（集成侧 OnPictureMenuOpening 插的那条 MenuFlyoutSeparator）。
        // uosc 的分隔线是「这一行之后画条线」的行属性而不是独立行，落在前一行上（与目录里的分隔同款）。
        if (items.Count > 0 && items[^1].Separator != true)
            items[^1] = items[^1] with { Separator = true };

        // —— 「更多」那一棵，行集与次序照抄 OnMoreMenuOpening：版本…（有第二版才在）、着色器、跳过片头片尾、
        // 自动播放下一集、播放信息…；集成侧在这些行之间放的是独立分隔线，这里落在前一行上。
        if (VersionControlsVisible)
            items.Add(new UoscMenuItem("版本…", $"script-message {VideoWindowContract.Versions} open",
                true, false, Separator: true));

        items.Add(BuildUoscShaderMenu());
        items[^1] = items[^1] with { Separator = true };
        items.Add(BuildUoscSkipMenu());
        items.Add(new UoscMenuItem("自动播放下一集",
            $"script-message {VideoWindowContract.AutoPlayNext} toggle", true, AutoPlayNextEpisode, Separator: true));
        items.Add(new UoscMenuItem("播放信息…", $"script-message {VideoWindowContract.MediaInfo} open", true, false));

        // 与集成侧 OnMoreMenuOpening 同样折叠进「播放设置」，子项保留原来的宿主消息。
        items[^1] = items[^1] with { Separator = true };
        var settings = PlayerSettingsLinks.All.Select(link => new UoscMenuItem(
                link.Label,
                $"script-message {VideoWindowContract.OpenSettings} {link.Token}",
                true,
                false)).ToList();
        items.Add(new UoscMenuItem(PlayerSettingsLinks.MenuLabel, null, true, false, Items: settings));

        // title 传 null：画面菜单不画顶部那行「画面」（用户令 2026-09-26）；仍带 anchor 贴光标弹。
        await SendMenuAsync("picture", null, items, anchor: true).ConfigureAwait(true);
    }

    /// <summary>
    /// 「更多」里的着色器子菜单，uosc 行版 —— 与集成模式 <c>PlayerPage.BuildShaderMenu</c> 同一批行、同一个
    /// 打勾判据：恢复设置的方案（没钉档时亮）、关闭着色器、分隔线、八档各带右列暗字链名（钉档且命中才亮）。
    /// 点中回 <see cref="VideoWindowContract.Shader"/>，宿主那头与旧的三档同一句执行。
    /// </summary>
    private UoscMenuItem BuildUoscShaderMenu()
    {
        List<UoscMenuItem> rows =
        [
            new("恢复设置的方案", $"script-message {VideoWindowContract.Shader} auto", true, ShaderStateKnown && !_shaderPinned),
            new("关闭着色器", $"script-message {VideoWindowContract.Shader} off", true,
                ShaderStateKnown && _shaderPinned && ActiveShader is null, Separator: true),
            .. ShaderCatalog.Select(group => new UoscMenuItem(group.DisplayName,
                $"script-message {VideoWindowContract.Shader} {group.Id}", true,
                ShaderStateKnown && _shaderPinned && ActiveShader?.Id == group.Id, group.Description))
        ];

        return new UoscMenuItem($"着色器：{ActiveShaderLabel}", null, true, false, Items: rows);
    }

    /// <summary>
    /// 「更多」里的跳过片头片尾子菜单，uosc 行版 —— 与集成模式 <c>PlayerPage.BuildSkipMenu</c> 同一批行、
    /// 同一句文案（<see cref="SkipModeLabel"/>）；点中回 <see cref="VideoWindowContract.SkipMode"/>，
    /// 宿主写进与集成模式同一格设置。
    /// </summary>
    private UoscMenuItem BuildUoscSkipMenu()
    {
        var mode = SkipMode;

        var rows = new[] { SkipSectionMode.Ask, SkipSectionMode.Auto, SkipSectionMode.Off }
            .Select(choice => new UoscMenuItem(
                SkipModeLabel(choice),
                $"script-message {VideoWindowContract.SkipMode} {SkipModeToken(choice)}",
                true,
                mode == choice))
            .ToList();

        return new UoscMenuItem($"跳过片头片尾：{SkipModeLabel(mode)}", null, true, false, Items: rows);
    }
    /// <summary>
    /// Reads the mpv properties the 画面菜单 ticks from — <see cref="PlayerMenuCatalog.CheckProperties"/>, one
    /// read each — so a menu about to open can mark the current 解码方式, 声道布局, 抖动补偿…. Read on open
    /// rather than shadowed in fields because the keys（Z/X 延迟、外部 mpv、上一场遗留的 cycle）can move a value
    /// without this class seeing it; a menu open is a rare, user-driven moment where a handful of reads is cheap
    /// even on the piped backend. 读不到（没在播、句柄刚换）就是空表，行一律不打勾。
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, string?>> ReadMenuChecksAsync()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var context = CaptureInteraction();

        foreach (var property in PlayerMenuCatalog.CheckProperties)
        {
            if (!IsCurrentInteraction(context)) return new Dictionary<string, string?>(StringComparer.Ordinal);
            values[property] = await _playback.GetTextAsync(property).ConfigureAwait(true);
        }

        return IsCurrentInteraction(context) ? values : new Dictionary<string, string?>(StringComparer.Ordinal);
    }

    /// <summary>
    /// 一层目录变成 uosc 菜单行，子菜单递归。命令叶子按 DFS 先序编号（与 <see cref="PlayerMenuCatalog.Commands"/>
    /// 的次序对齐，测试钉住两者一致）；分隔线画在它前一行上（uosc 的分隔是「这一行之后画条线」的属性，不是独立行）；
    /// 组是子菜单、本身不可点。当前值命中的那一行打上 <c>active</c>（uosc 一贯的高亮，与音轨/版本菜单同样 ——
    /// 命中与否由 Core 的 <see cref="PlayerMenuNode.IsCheckedBy"/> 就着 <paramref name="checks"/> 判，见
    /// <see cref="ReadMenuChecksAsync"/>）。
    /// </summary>
    private static List<UoscMenuItem> BuildPictureMenuItems(
        IReadOnlyList<PlayerMenuNode> nodes, IReadOnlyDictionary<string, string?> checks,
        VideoMenuSnapshot<PlayerMenuNode> snapshot, ref int command)
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
                    var children = BuildPictureMenuItems(node.Children, checks, snapshot, ref command);
                    // 子菜单那一行必须可选中（selectable 默认真、这里显式给真）——否则 uosc 里点不开它；
                    // 它没有 value（点它是进子菜单，不是跑命令），items 一有值 uosc 就当它是子菜单。
                    items.Add(new UoscMenuItem(node.Label, null, true, false, Items: children));
                    break;

                default:
                    command++;
                    items.Add(new UoscMenuItem(
                        node.Label,
                        $"script-message {VideoWindowContract.MenuIndex} {snapshot.ValueAt(command - 1)}",
                        true,
                        node.IsCheckedBy(checks)));
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
        var snapshot = new VideoMenuSnapshot<EmbyItem>(Episodes);
        _episodeMenuSnapshot = snapshot;
        _episodeMenuContext = CaptureInteraction();
        List<UoscMenuItem> items = Episodes.Count == 0
            ? [new UoscMenuItem("（这一场没有可用的集列表）", null, false, false)]
            : [.. Episodes.Select((episode, index) => new UoscMenuItem(
                episode.ToPlaybackTitle(),
                $"script-message {VideoWindowContract.EpisodeIndex} {snapshot.ValueAt(index)}",
                true,
                string.Equals(episode.Id, PlayingItemId, StringComparison.Ordinal)))];

        await SendMenuAsync("episodes", "选集", items, anchor: true).ConfigureAwait(true);
    }

    /// <summary>
    /// 把「这个条目挂了几版」告诉独占模式的视频窗 —— uosc 那颗「版本」按钮按这个数露面，只有一版时
    /// 它压根不在控制条上（用户令 2026-09-23：「只有一个版本的情况下不显示…」）。
    /// <para>
    /// 与 <see cref="PlayerViewModel.VersionControlsVisible"/> 是同一个数的两个去处：集成模式那颗按钮归外壳，
    /// 独占模式那颗归 uosc —— 而 uosc 的控件表是静态的，露不露面只能由宿主把答案送过去（门在 Lua 那侧，
    /// <c>state.has_many_versions</c>，见 assets/mpv-ui/scripts/uosc/main.lua 的 EMBYNIAN[version-count]）。
    /// </para>
    /// <para>
    /// 调用点都是「那一格刚被写过」的时刻：<see cref="PlayerViewModel.CurrentItem"/> 的 setter
    /// （新一集开播、服务器那份更全的记录到手、换版之后），以及视频窗装载握手的应答 —— 后一条管的是
    /// 「画面还没出来就摆对」。发不出去不算错：起播前那一次赋值天然还没有会话，静默。
    /// </para>
    /// </summary>
    private void NoteVersionCount() => _ = PushVersionCountAsync();

    private async Task PushVersionCountAsync()
    {
        // 只有独占模式的视频窗里装着 uosc（HeadlessPlayback）；集成模式那两个数字归外壳，没人接这条消息。
        if (!HeadlessPlayback) return;

        var value = Versions.Count.ToString(CultureInfo.InvariantCulture);

        if (!await _playback.CommandAsync("script-message", VideoWindowContract.VersionCount, value).ConfigureAwait(true))
            Log.Debug(Category, $"「有几版」（{value}）没能送到视频窗 —— uosc 还没起来？");
    }

    /// <summary>
    /// 把「正在放的是不是单集」告诉独占模式的视频窗 —— uosc 那颗「选集」按钮按它露面，播电影时
    /// 它压根不在控制条上（2026-09-26 用户令「播放电影的时候不要显示这个按钮」）。
    /// <para>
    /// 与 <see cref="NoteVersionCount"/> 同一个形状：值就是 <see cref="EpisodeControlsVisible"/> 那一格 ——
    /// 集成模式的按钮归外壳、独占模式的按钮归 uosc，而 uosc 的控件表是静态的，露不露面只能由宿主把
    /// 答案送过去（门在 Lua 那侧，<c>state.has_episodes</c>，见 assets/mpv-ui/scripts/uosc/main.lua 的
    /// EMBYNIAN[episode-count]）。调用点都是「那一格刚被写过」的时刻：
    /// <c>OnNowPlayingChanged</c>（每一场开播都明写一遍）与 <c>FillSiblingsAsync</c>（单集列表补齐、
    /// 明写 true），外加视频窗装载握手的应答 —— 后一条管的是「画面还没出来就摆对」。
    /// 发不出去不算错：起播前那一次赋值天然还没有会话，静默。
    /// </para>
    /// </summary>
    private void NoteEpisodeCount() => _ = PushEpisodeCountAsync();

    private async Task PushEpisodeCountAsync()
    {
        // 只有独占模式的视频窗里装着 uosc（HeadlessPlayback）；集成模式的按钮归外壳，没人接这条消息。
        if (!HeadlessPlayback) return;

        var value = EpisodeControlsVisible ? "1" : "0";

        if (!await _playback.CommandAsync("script-message", VideoWindowContract.EpisodeCount, value).ConfigureAwait(true))
            Log.Debug(Category, $"「是不是单集」（{value}）没能送到视频窗 —— uosc 还没起来？");
    }

    private void NoteSubline() => _ = PushSublineAsync();

    /// <summary>
    /// 把左上角第二行那句文件信息（<see cref="PlaybackTitles.Subline"/> ＝「分辨率 · 视频编码 · 音频格式 ·
    /// 组名」）推给独占模式视频窗的 uosc 顶栏，画成返回按钮正下方的副标题。集成模式那一行是 XAML 的
    /// <c>SubtitleBox</c>（绑 <see cref="Subtitle"/>），不走这里 —— 两模式同源、显示一致（用户令 2026-09-27，
    /// 内容与位置 2026-09-28 跟进）。
    /// <para>
    /// 与 <see cref="NoteVersionCount"/>/<see cref="NoteEpisodeCount"/> 同一个形状、同样的调用时机（uosc 握手
    /// ＋ 每场 <c>OnNowPlayingChanged</c>）。取的是正在放那一版的媒体源（<see cref="PlayingSourceOrFirst"/>）—— 与
    /// 集成模式那一行、进度条中间那条画质读数同一份。握手那一下条目多半还没落定，<see cref="PlayingSourceOrFirst"/>
    /// 给 null、值是空串（uosc 那头＝先不画副标题），文件开起来后 <c>OnNowPlayingChanged</c> 再推一次真值。
    /// 发不出去不算错：起播前那一次天然还没有会话，静默。
    /// </para>
    /// </summary>
    private async Task PushSublineAsync()
    {
        if (!HeadlessPlayback) return;

        var value = PlaybackTitles.Subline(PlayingSourceOrFirst(_nowPlaying));

        if (!await _playback.CommandAsync("script-message", VideoWindowContract.Subline, value).ConfigureAwait(true))
            Log.Debug(Category, "左上角第二行的文件信息没能送到视频窗 —— uosc 还没起来？");
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
        var snapshot = new VideoMenuSnapshot<MediaSource>(versions);
        _versionMenuSnapshot = snapshot;
        _versionMenuContext = CaptureInteraction();

        List<UoscMenuItem> items = versions.Count <= 1
            ? [new UoscMenuItem("没有可切换的版本", null, false, false)]
            : [.. versions.Select((source, index) => new UoscMenuItem(
                ItemDetail.SourceLabel(source),
                $"script-message {VideoWindowContract.VersionIndex} {snapshot.ValueAt(index)}",
                true,
                MediaVersionSwitch.Same(source, PlayingSource),
                QualityTail(source)))];

        await SendMenuAsync("versions", "版本", items, anchor: true).ConfigureAwait(true);
    }

    /// <summary>
    /// 一份 uosc 菜单的推送。uosc 的 <c>open-menu</c> 吃一份 JSON（type/title/items），点中一项时它把该项的
    /// <c>value</c> 当 mpv 命令执行 —— 所以每一项的 value 就是一条回宿主的 <c>script-message</c>，
    /// 序号对应这一份菜单里的次序。
    /// <para>
    /// <paramref name="title"/> 为 null＝这张菜单不画顶部标题行（画面菜单如此）；
    /// <paramref name="anchor"/> 为真＝在光标处弹出、不居中、不压暗幕布（画面/选集/版本三张都是用户操作叫出来的，
    /// 与集成模式的右键/按钮浮层一样贴着触发点弹）。
    /// </para>
    /// </summary>
    private async Task SendMenuAsync(string type, string? title, IReadOnlyList<UoscMenuItem> items, bool anchor = false)
    {
        var json = JsonSerializer.Serialize(new UoscMenu(type, title, items, anchor ? true : (bool?)null), UoscMenuJson.Options);

        if (!await _playback.CommandAsync("script-message", "open-menu", json).ConfigureAwait(true))
            Log.Warn(Category, $"推送「{title ?? type}」菜单到视频窗失败");
    }

    /// <summary>菜单行右边那列暗字：这份文件到底是什么。问不出画质就交 null，不写一个空字符串上去。</summary>
    private static string? QualityTail(MediaSource source) =>
        source.ToQualityLabel() is { Length: > 0 } quality ? quality : null;

    /// <summary>uosc open-menu 的菜单形状，与 uosc MenuData 的字段对齐（多出来的字段会被忽略）。
    /// <para>
    /// <see cref="Title"/> 为 null 时靠 <see cref="UoscMenuJson"/> 的 WhenWritingNull 不落进 JSON，uosc
    /// 那头 <c>data.title</c> ＝ nil ⇒ 菜单顶上不画标题行（画面菜单不要那行「画面」，用户令 2026-09-26）；
    /// 选集/版本仍各自带标题。
    /// </para>
    /// <para>
    /// <see cref="Anchor"/> 为真时，uosc 把这张菜单画在光标处（右键点哪弹哪）而不是屏幕居中，也不压暗幕布
    /// —— 「弹出方式和集成模式一样」（用户令 2026-09-26）。字段名固定 <c>embynian_anchor</c>（uosc 侧
    /// <c>self.root.embynian_anchor</c> 直接读），所以带 <see cref="JsonPropertyName"/> 压过 camelCase 命名策略；
    /// 为 null 时同样靠 WhenWritingNull 不落进 JSON。
    /// </para></summary>
    private sealed record UoscMenu(
        string Type,
        string? Title,
        IReadOnlyList<UoscMenuItem> Items,
        [property: JsonPropertyName("embynian_anchor")] bool? Anchor = null);

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

    private void OnStatusChanged(PlaybackUpdate<PlayerStatus> update) => OnUi(() =>
    {
        if (update.Generation == _playback.Generation && _playback.IsPlaying) ApplyStatus(update.Value);
    });

    private void OnTracksChanged(PlaybackUpdate<IReadOnlyList<MpvTrack>> update) => OnUi(() =>
    {
        if (update.Generation == _playback.Generation && _playback.IsPlaying) Tracks = update.Value;
    });

    private void OnNowPlayingChanged(PlaybackUpdate<EmbyItem?> update) => OnUi(() =>
    {
        if (update.Generation != _playback.Generation) return;
        var item = update.Value;
        if (item is not null && !_playback.IsPlaying) return;
        if (item is not null) _preparingPictureInHost = null;
        // Re-armed per playback, before anything can look at it: switching episodes comes through here,
        // and the previous episode's opening is not this one's. The server's chapter marks are the
        // starting point; RefineSkipSectionsAsync replaces them with mpv's once the file is open.
        ResetTimelineDrag();
        InvalidateMenuInteractions();
        FlushVolume(settled: false);
        _volumeMemory.ResetObservation();
        _volumeReady = false;
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

        // 左上角主标题＝headline（**带剧名**：单集＝「剧名 SxxExx 集名」，电影＝片名）；第二行是文件信息
        // 「分辨率 · 视频编码 · 音频格式 · 组名」（用户令 2026-09-27「电视剧左上角标题要用 SxxExx 集名，
        // 下方的片名改为视频编码+音轨+组名」；2026-09-28 前置分辨率并挪到返回按钮正下方；**同日更晚用户令
        // 「标题前面加上剧名」，09-27 那版「剧名让给第二行」被翻案** —— 第二行如今只剩分辨率/编码/音频/组名，
        // 剧名不在那儿）。两条管线同源、显示一致（独占那两行由 uosc 顶栏画，NoteSubline 把第二行
        // 推过去；主标题走 force-media-title＝同一个 ToPlaybackHeadline）。
        Title = item.ToPlaybackHeadline();
        AdoptShaderLaunch(item);
        Subtitle = PlaybackTitles.Subline(PlayingSourceOrFirst(item));

        // 控制条中间那行读数是**正在放的那一版**的，见 PlayingSourceLabel。
        SourceLabel = PlayingSourceLabel(item);
        EpisodeControlsVisible = item.Type == EmbyItemType.Episode
            && (Episodes.Count > 1 || !string.IsNullOrEmpty(item.SeriesId));

        // 独占模式视频窗里那颗「选集」按钮吃同一个数（2026-09-26 用户令「播放电影的时候不要显示这个
        // 按钮」）：播电影时这一格是假，uosc 那头整颗按钮跟着不在屏上。集成模式那两颗归外壳，白收一条
        // 消息没人理（见 PushEpisodeCountAsync 的 HeadlessPlayback 闸）。
        NoteEpisodeCount();

        // 左上角第二行那句文件信息推给独占模式的 uosc 顶栏副标题（集成模式那一行是上面的 Subtitle，
        // 走 XAML）。换集 / 换版都经这里重算，组名与编码跟着走。
        NoteSubline();

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
        _ = PushNativeShortcutsAsync(force: true);

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
    /// 正在放的是哪一份文件（媒体源），**认不出就退回第一版** —— 进度条中间那行画质读数
    /// （<see cref="PlayingSourceLabel"/>）与左上角第二行的文件信息（<see cref="PlaybackTitles.Subline"/>）都问它。
    /// <para>
    /// 问的必须是<b>正在放的那一版</b>，不是 <c>MediaSources[0]</c>。单版本的条目上两者恰好是同一份文件，
    /// 所以「问第一个」这个写法一直没露馅；条目一旦挂了两版、用户又换过版，那两处就会一直报第一版的信息 ——
    /// 屏上看到的是「换了版本，画质/文件信息纹丝不动」，而真实的画面已经换成另一份文件了。认不出在播的是哪一版
    /// （外部 mpv.exe 后端不经过这里的启动记账、或者刚起播还没落定）时才退回第一版。
    /// </para>
    /// <para>
    /// 与 <see cref="PlayingSource"/> 属性的差别只在末尾那一步兜底：属性认不出给 null（菜单打勾要 null 才知道
    /// 「哪一行都不勾」），这里为了屏上那两处读数总有内容可显示而退回第一版。
    /// </para>
    /// </summary>
    private MediaSource? PlayingSourceOrFirst(EmbyItem? item) =>
        MediaVersionSwitch.Playing(item, _playback.PlayingSource) ?? item?.MediaSources.FirstOrDefault();

    /// <summary>
    /// 控制条中间那行「1080p · HEVC · 8.4 GB · 制作组」说的是哪一份文件 —— 取<see cref="PlayingSourceOrFirst"/>
    /// 的画质读数，尾部接上正在放这一版文件名里的制作组（2026-09-24 用户令）。换过版本后这一行会经
    /// <c>OnNowPlayingChanged</c> 重算，组名跟着换；文件名里认不出组名就维持原样。
    /// </summary>
    private string PlayingSourceLabel(EmbyItem? item)
    {
        var source = PlayingSourceOrFirst(item);

        var label = source?.ToQualityLabel() ?? "";
        var group = ReleaseGroup.FromFileName(source?.Path);
        if (group.Length == 0) return label;
        return label.Length > 0 ? $"{label}  ·  {group}" : group;
    }

    /// <summary>
    /// 遮罩那块背景图等多久。给 200ms —— 它多半已经在磁盘缓存里（首页轮播与详情页背景取的就是同一条目、
    /// 同一个 <see cref="EmbyImageStore.RequestWidth"/> 宽度档），一次磁盘读加解码是几十毫秒；200ms 是
    /// 「图确实在路上」与「这个条目根本没有背景图」之间那条线。到点照常进场，遮罩退回纯色。
    /// </summary>
    private const int CoverBackdropWaitMilliseconds = 200;

    /// <summary>
    /// 把这张背景图等到手（或等到上限）再让调用方进播放页 —— 用户令 2026-09-25「不要黑屏，主页和背景图
    /// 无缝切换」。遮罩在图到手之前是一整块近黑的纯色，先进去就是先看到那块纯色；等图备好再进，屏上一直
    /// 停在主页，换过来的第一眼就是背景图。
    /// <para>
    /// 取图那件事仍旧只有 <see cref="LoadCoverBackdropAsync"/> 一处：这里只是等它那一趟，超时了它照样在
    /// 后台跑完、遮罩随后自己换上。只有当前条目的图才能复用；上一场留下的图必须先清掉。
    /// </para>
    /// </summary>
    private async Task WaitCoverBackdropAsync(EmbyItem? item)
    {
        var load = LoadCoverBackdropAsync(item);
        if (load.IsCompleted) return;

        var clock = Environment.TickCount64;
        await Task.WhenAny(load, Task.Delay(CoverBackdropWaitMilliseconds)).ConfigureAwait(true);

        // 这一行是给验收用的：它说明「主页停了多久才换过去」以及「换过去时图到底备好了没有」。
        Log.Debug(Category, load.IsCompleted
            ? $"遮罩背景图已备好（等了 {Environment.TickCount64 - clock}ms），进播放页"
            : $"遮罩背景图等了 {CoverBackdropWaitMilliseconds}ms 还没到，照常进播放页（遮罩先退回纯色）");
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
    private Task LoadCoverBackdropAsync(EmbyItem? item)
    {
        if (item is null)
        {
            _coverGeneration++;
            _coverBackdropItemId = null;
            CoverBackdropFrame = null;
            CoverBackdrop = null;
            return _coverBackdropLoad = Task.CompletedTask;
        }

        // 同一条目换媒体源可复用背景；还在解码时则等待原任务，不能提前放行进场。
        if (string.Equals(_coverBackdropItemId, item.Id, StringComparison.Ordinal)) return _coverBackdropLoad;

        _coverBackdropItemId = item.Id;
        var generation = ++_coverGeneration;
        CoverBackdropFrame = null;
        CoverBackdrop = null;
        return _coverBackdropLoad = ReadCoverBackdropAsync(item, generation);
    }

    private async Task ReadCoverBackdropAsync(EmbyItem item, int generation)
    {
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

            // 先解覆盖层那一份（起播整屏要用），再把 BitmapImage 交给遮罩：两者一起就位，等这一趟的人
            // 等到的就是「屏上和层上都有图」。像素那一份解不出来不算失败，遮罩照旧有图可垫。
            var frame = await CoverBackdropImage.DecodeAsync(bytes, PlayerPalette.Film).ConfigureAwait(true);
            if (generation != _coverGeneration) return;

            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            if (generation != _coverGeneration) return;

            // 两份结果都只在最后一次代际核对后发布，旧解码不能先写覆盖层、再发现自己过期。
            CoverBackdropFrame = frame;
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
    /// <para>
    /// 记下的是<b>请求值加驱动</b>（「auto（wasapi）」），不是 Windows 眼里的那个端点 —— mpv 的
    /// <c>audio-device</c> 在自动模式下就答 auto，端点名它不说。诊断页因此把这一行标成「请求值与驱动」，
    /// 不再叫「实际设备」（2026-09-30）。
    /// </para>
    /// </summary>
    private Task NoteAudioDeviceAsync(int generation) => PollAsync(generation, true, async () =>
    {
        var device = await _playback.GetTextAsync("audio-device").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(device)) return false;

        var driver = await _playback.GetTextAsync("current-ao").ConfigureAwait(true);
        if (generation != _generation) return true;

        // 驱动还没开（音频输出在文件交出去之后才就绪）就再等一轮：把 audio-device 的请求值单独当成
        // 「实际设备」记下，正是诊断页那行错名的由来。轮询有次数上限；音频真打不开时宁可不记 ——
        // 「请求了 X 但没开起来」比一个看起来成功的读数诚实。
        if (string.IsNullOrWhiteSpace(driver)) return false;

        // mpv answers 「auto」 for a device nobody named, which is true but useless on its own — the driver
        // name is what says anything at all in that case.
        var described = $"{device}（{driver}）";
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
        var durationChanged = Math.Abs(Status.Duration - status.Duration) > 0.05;
        Status = status;
        if (status.Loaded && status.Chapters is { } chapters)
        {
            var chaptersChanged = !ReferenceEquals(chapters, ChapterMarks) && !chapters.SequenceEqual(ChapterMarks);
            if (status.HasDuration && (chaptersChanged || durationChanged))
                _skips.Refine(SkipSectionPlanner.Resolve(chapters, status.Duration), status.Duration);
            if (chaptersChanged)
            {
                ChapterMarks = chapters;
                ClearChapterPeek();
                ChaptersChanged?.Invoke();
            }
        }

        PlayPauseGlyph = Glyph(status.Paused ? PlayGlyphCode : PauseGlyphCode);
        DurationClock = status.HasDuration ? status.DurationClock : "0:00";
        CacheFraction = status.CacheFraction;

        ThinFraction = Math.Clamp(status.Fraction, 0, 1);

        _pushing = true;
        try
        {
            if (Now - _speedTouched >= 1000 || Math.Abs(status.Speed - SpeedValue) < 0.005)
                SpeedValue = Math.Clamp(status.Speed, SpeedChoices[0], SpeedChoices[^1]);
            SpeedLabel = $"{SpeedValue.ToString("0.0#", CultureInfo.InvariantCulture)}×";
            // The user's own drag wins for as long as it is the more current answer: mpv reports the
            // position it is still seeking away from, and letting that write the slider back would drag
            // the thumb out from under the pointer. A seek that has been sent but not landed gets the
            // same protection — see SeekBarFollows for why 400 ms of grace was not enough — and the
            // clock and the bar are gated together so the two never show different moments.
            if (SeekBarFollows(status))
            {
                SeekValue = status.Fraction * SeekScale;
                PositionClock = status.HasPosition ? TimelineScale.Clock(status.Position, status.Duration) : "0:00";
            }

            if (_volumeMemory.Observe(status, _playback.CanControl, Now) is { } level)
            {
                _volumeReady = true;
                Volume = level;
                ScheduleVolumeSave();
            }
            else
            {
                // 加载期/暂停回声被挡时不给相对音量操作一个假的 100 基准：优先用已确认的内核值，
                // 保护窗里停着的真实读数次之，最后才是本次起播设置——没有已证实值时显示它，
                // 但 OnVolumeChanged 不得把显示值当相对基准发出去（见 VolumeReady）。
                Volume = _volumeMemory.Pending
                    ?? _volumeMemory.Queued
                    ?? VolumeMemory.Level(_playback.Status.Volume)
                    ?? Settings.Audio.Volume;
            }
        }
        finally
        {
            _pushing = false;
        }

        // 静音's own glyph is still the whole of the mute readout — the figure above the rail says how loud,
        // not whether (「给音量条上方加上数字」).
        SoundGlyph = Glyph(status.Muted ? MutedGlyphCode : status.Volume <= 0 ? 0xE992 : status.Volume <= 60 ? 0xE993 : VolumeGlyphCode);
        UpdateRemainingClock();

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
        UpdateRemainingClock();
        if (_pushing || !Status.HasDuration) return;

        _seekTouched = Now;
        _seekPending = Math.Clamp(value / SeekScale, 0, 1);

        // The clock keeps up with the thumb rather than with mpv, so a drag reads as a scrub instead of
        // as a slider that has come loose from the number beside it.
        if (Status.HasDuration)
            PositionClock = TimelineScale.Clock(_seekPending.Value * Status.Duration, Status.Duration);
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
        // The displayed value may be this playback's launch volume while the kernel has not reported a
        // real level yet; computing an absolute target from that guess would turn a 「down」 step into
        // a jump to the displayed level. Once a real kernel reading has arrived the wheel's new value
        // is exact and can be confirmed as before; before that the step itself goes to the kernel,
        // like the delay does, and the reading that comes back owns the display.
        if (!_playback.CanControl || !_volumeReady)
        {
            var baseLevel = _volumeMemory.Pending ?? _volumeMemory.Queued
                ?? VolumeMemory.Level(Status.Volume) ?? Settings.Audio.Volume;
            _ = StepVolumeAsync(VolumeScale.Clamp(value) - baseLevel, _playback.Generation);
            return;
        }
        if (VolumeMemory.Level(value) is not { } level)
        {
            DisplayVolume(_volumeMemory.Pending ?? _volumeMemory.Queued
                ?? VolumeMemory.Level(Status.Volume) ?? Settings.Audio.Volume);
            return;
        }

        var request = _volumeMemory.Request(level, Now);
        _ = SendVolumeAsync(request, level, _playback.Generation);
    }

    private bool _volumeReady;

    private async Task StepVolumeAsync(double step, long generation)
    {
        var applied = await _playback.AdjustVolumeAsync(step).ConfigureAwait(true);
        if (_lifetime.IsCancellationRequested || generation != _playback.Generation) return;
        if (applied is { } level && VolumeMemory.Level(level) is { } confirmed)
        {
            _volumeReady = true;
            if (_volumeMemory.Observe(new PlayerStatus { Loaded = true, Volume = level, VolumeKnown = true },
                canControl: true, Now) is { } observed)
            {
                DisplayVolume(observed);
                ScheduleVolumeSave();
            }
            return;
        }
        if (_volumeMemory.Pending is null && _volumeMemory.Queued is null)
            DisplayVolume(VolumeMemory.Level(_playback.Status.Volume) ?? Settings.Audio.Volume);
    }

    private async Task SendVolumeAsync(long request, int level, long generation)
    {
        var confirmed = await _playback.SetVolumeAsync(level).ConfigureAwait(true);
        if (_lifetime.IsCancellationRequested || generation != _playback.Generation) return;
        if (confirmed)
        {
            if (_volumeMemory.Accept(request, level, Now)) ScheduleVolumeSave();
        }
        else if (_volumeMemory.Reject(request))
        {
            DisplayVolume(_volumeMemory.Queued
                ?? VolumeMemory.Level(_playback.Status.Volume) ?? Settings.Audio.Volume);
        }
    }

    private void DisplayVolume(int level)
    {
        _pushing = true;
        try { Volume = level; }
        finally { _pushing = false; }
    }

    private void ScheduleVolumeSave()
    {
        if (_volumeMemory.Pending is null || _lifetime.IsCancellationRequested
            || _volumeSaveTask is { IsCompleted: false }) return;
        _volumeSaveTask = SaveSettledVolumeAsync();
    }

    private async Task SaveSettledVolumeAsync()
    {
        try
        {
            while (_volumeMemory.Pending is not null || _volumeMemory.Queued is not null)
            {
                if (_volumeMemory.Pending is null && _volumeMemory.FlushQueued(Now) is { } adopted)
                {
                    DisplayVolume(adopted);
                    ScheduleVolumeSave();
                    continue;
                }
                if (_lifetime.IsCancellationRequested || _volumeMemory.Pending is null) return;
                var due = Math.Max(_volumeMemory.DueAt, _volumeRetryAt);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, due - Now)), _lifetime.Token)
                    .ConfigureAwait(true);
                FlushVolume();
            }
        }
        catch (OperationCanceledException) { }
        finally { _volumeSaveTask = null; }
    }

    /// <summary>Settles independently of the page ticker; failed writes retain the same pending revision.</summary>
    private void FlushVolume(bool settled = true)
    {
        // A real kernel reading parked during the echo window outranks the older pending value at every
        // settle point (stop, playback switch, save loop): saving the stale one would write back a
        // level the kernel has already left behind.
        if (_volumeMemory.FlushQueued(Now) is { } adopted)
        {
            DisplayVolume(adopted);
            ScheduleVolumeSave();
        }
        if (_volumeMemory.Pending is not { } level) return;
        if (settled && Now < Math.Max(_volumeMemory.DueAt, _volumeRetryAt)) return;
        var revision = _volumeMemory.Revision;
        var unchanged = Settings.Audio.Volume == level;
        Settings.Audio.Volume = level;
        if (unchanged && !_settings.HasUnsavedChanges || _settings.TrySave())
        {
            _volumeMemory.Saved(revision);
            _volumeRetryAt = 0;
            return;
        }

        if (_volumeRetryAt == 0)
            Noticed?.Invoke("音量已调整，但暂未保存；将自动重试", InfoBarSeverity.Warning);
        _volumeRetryAt = Now + 5000;
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
        try
        {
            for (var round = 0; round < PollAttempts; round++)
            {
                if (_lifetime.IsCancellationRequested) return;
                if (pauseFirst || round > 0) await Task.Delay(PollIntervalMilliseconds, _lifetime.Token).ConfigureAwait(true);
                if (_lifetime.IsCancellationRequested || generation != _generation || !_playback.IsPlaying) return;
                if (await attempt().ConfigureAwait(true)) return;
            }
        }
        catch (OperationCanceledException) { }
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
        if (generation != _generation || Status.Chapters is not null) return true;

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

        // 独占模式画面在 mpv 窗口里、那颗 XAML 按钮不在屏上，把这份 offer 推给视频窗的 uosc 画一颗
        // （集成模式白推：HeadlessPlayback 才发；见 PushSkipToVideoWindow）。可见与否都推 —— 收摊也是一条消息。
        PushSkipToVideoWindow(prompt.Visible, prompt.Caption);

        if (!prompt.Visible) return;

        SkipCaption = prompt.Caption;
        SkipTip = prompt.Tip;
        SkipRemaining = prompt.Remaining;
    }

    /// <summary>
    /// 把「跳过」offer 的文案推给独占模式视频窗的 uosc 按钮（<see cref="VideoWindowContract.SkipOffer"/>，
    /// 空串＝收摊）。集成模式那颗按钮绑的是 <see cref="SkipOffered"/> 一族属性、不走这里，所以只有
    /// <see cref="HeadlessPlayback"/> 才发。
    /// <para>
    /// 状态每秒采十次都会经 <see cref="ShowSkipPrompt"/>，只在文案真变了才发一条 —— 同一句「跳过片头」
    /// 一秒重发十遍既费事也可能扰动 uosc 的重绘。发不出去（uosc 还没起来、句柄刚换）不算错，静默。
    /// </para>
    /// </summary>
    private void PushSkipToVideoWindow(bool visible, string? caption)
    {
        if (!HeadlessPlayback) return;

        var value = visible ? caption ?? "" : "";
        if (string.Equals(value, _skipPushed, StringComparison.Ordinal)) return;

        _skipPushed = value;
        _ = _playback.CommandAsync("script-message", VideoWindowContract.SkipOffer, value);
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
        // 独占模式那颗 uosc 按钮当场收起 —— 下一拍 ApplySkipOffer 也会收（seek 后已不在片段里），
        // 这一句免得点完到下一拍之间那颗按钮还挂着。集成模式白推（HeadlessPlayback 闸）。
        PushSkipToVideoWindow(false, "");
    }

}
