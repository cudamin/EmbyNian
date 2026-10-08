using Momoka.Emby;
using Momoka.Mpv;
using Momoka.Playback;
using Momoka.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Momoka.Shell.Views;

/// <summary>
/// The player's seven flyouts: 选集, 版本, 音轨, 字幕, 倍速, 画面 and ⚙更多.
/// <para>
/// All of them are built as they open rather than declared in XAML, because every row of every one of them
/// shows a current value — which episode is playing, which version of the file is on screen, which track mpv
/// picked, the delay the keys left, the shader group in force. A menu declared once would be a menu that went
/// stale on the first change.
/// </para>
/// <para>
/// Each row's click does exactly one thing: call the view model. Which episode comes next, what a track
/// choice means to mpv, whether 跳过 asks or acts — none of that is decided here. 画面 is one step
/// further removed: its rows come from <see cref="PlayerMenuCatalog"/>, a table in Core, so the whole
/// menu is a fold over data.
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>
    /// The 画面菜单 rows that can show a tick, paired with their catalogue node, collected as the menu is built
    /// once. <see cref="RefreshPictureChecksAsync"/> walks this on every open to set each tick from mpv.
    /// </summary>
    private readonly List<(MenuFlyoutItem Item, PlayerMenuNode Node)> _pictureChecks = [];
    private int _pictureStaticCount;

    // ---- 选集 --------------------------------------------------------------------

    private void OnEpisodeMenuOpening(object sender, object e)
    {
        if (!Attached) return;

        EpisodeMenu.Items.Clear();

        if (ViewModel.Episodes.Count == 0)
        {
            EpisodeMenu.Items.Add(new MenuFlyoutItem { Text = "没有可选的单集", IsEnabled = false });
            return;
        }

        var context = ViewModel.CaptureInteraction();
        foreach (var episode in ViewModel.Episodes)
        {
            // No GroupName, deliberately: the menu is rebuilt on every open, so a group would accumulate
            // the items of every previous build and only the newest set would be radio-exclusive.
            var row = new RadioMenuFlyoutItem
            {
                Text = EpisodeLabel(episode),
                IsChecked = string.Equals(episode.Id, ViewModel.PlayingItemId, StringComparison.Ordinal),
                Tag = episode
            };

            if (episode.UserData?.Played == true) row.KeyboardAcceleratorTextOverride = "已看";

            row.Click += (source, args) =>
            {
                if (Attached && ViewModel.IsCurrentInteraction(context)
                    && source is MenuFlyoutItem { Tag: EmbyItem picked }) ViewModel.SwitchEpisode(picked);
            };

            EpisodeMenu.Items.Add(row);
        }
    }

    private static string EpisodeLabel(EmbyItem episode)
    {
        var code = episode.EpisodeCode;
        return code.Length > 0 ? $"{code}  {episode.Name}" : episode.Name;
    }

    // ---- 版本 --------------------------------------------------------------------

    /// <summary>
    /// 版本: 这个条目的其他文件 —— 4K HDR、1080p、导演剪辑版 —— 正在放的那一版打着勾。
    /// <para>
    /// 每次打开都重建，理由和其他几个菜单一样：哪一行该打勾，换过版就变了。
    /// </para>
    /// <para>
    /// 只在条目的媒体源多于一条时这个按钮才在屏上（<see cref="PlayerViewModel.VersionControlsVisibility"/>），
    /// 所以「一行版本都没有」这一支只在自检里走到（没在播，也就没有条目）—— 它照样得建出一行来，
    /// 否则自检报告里那个菜单会是空的，而空菜单和坏菜单在报告里长得一样。
    /// </para>
    /// </summary>
    private void OnVersionMenuOpening(object sender, object e)
    {
        if (!Attached) return;

        VersionMenu.Items.Clear();

        var versions = ViewModel.Versions;
        if (versions.Count == 0)
        {
            VersionMenu.Items.Add(new MenuFlyoutItem { Text = "没有可切换的版本", IsEnabled = false });
            return;
        }

        var context = ViewModel.CaptureInteraction();
        foreach (var source in versions)
        {
            var row = new RadioMenuFlyoutItem
            {
                Text = ItemDetail.SourceLabel(source),
                IsChecked = MediaVersionSwitch.Same(source, ViewModel.PlayingSource),
                Tag = source
            };

            // 右边那列暗字是这份文件到底是什么。详情页那个媒体源下拉只报名字（「媒体源的选项太长了」），
            // 因为名字旁边那张媒体信息表已经把画质写全了；播放页没有那张表，而两个版本可以重名，
            // 所以画质在这里必须跟着——用的是两个轨道菜单同一列。
            if (source.ToQualityLabel() is { Length: > 0 } quality) row.KeyboardAcceleratorTextOverride = quality;

            row.Click += (clicked, _) =>
            {
                if (Attached && ViewModel.IsCurrentInteraction(context)
                    && clicked is MenuFlyoutItem { Tag: MediaSource picked }) ViewModel.SwitchVersion(picked);
            };

            VersionMenu.Items.Add(row);
        }
    }

    // ---- 音轨 / 字幕 --------------------------------------------------------------

    private void OnAudioMenuOpening(object sender, object e)
    {
        if (!Attached) return;

        AudioMenu.Items.Clear();
        FillTrackMenu(AudioMenu, track => track.IsAudio, audio: true, offRow: null);
    }

    private void OnSubtitleMenuOpening(object sender, object e)
    {
        if (!Attached) return;

        SubtitleMenu.Items.Clear();
        FillTrackMenu(SubtitleMenu, track => track.IsSubtitle, audio: false, offRow: "关闭字幕");
    }

    /// <summary>
    /// Fills one track picker. The technical half of a row — codec, channels, bitrate — goes in
    /// <c>KeyboardAcceleratorTextOverride</c>, which the framework draws right-aligned and dimmed: it is
    /// the only two-column menu row WinUI offers, and it is what tells three Chinese audio tracks apart.
    /// </summary>
    private void FillTrackMenu(MenuFlyout menu, Func<MpvTrack, bool> match, bool audio, string? offRow)
    {
        var tracks = ViewModel.Tracks.Where(match).ToList();
        var context = ViewModel.CaptureInteraction();

        if (offRow is not null)
        {
            var off = new RadioMenuFlyoutItem
            {
                Text = offRow,
                IsChecked = tracks.All(track => !track.Selected)
            };
            off.Click += (_, _) =>
            {
                if (Attached && ViewModel.IsCurrentInteraction(context)) ViewModel.SelectTrack(audio, null);
            };
            menu.Items.Add(off);

            if (tracks.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        }

        if (tracks.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "正在读取轨道…", IsEnabled = false });
            return;
        }

        foreach (var track in tracks)
        {
            var row = new RadioMenuFlyoutItem
            {
                Text = track.DisplayName.Length > 0 ? track.DisplayName : $"轨道 {track.Id}",
                IsChecked = track.Selected,
                Tag = track.Id
            };

            if (track.DisplayDetail.Length > 0) row.KeyboardAcceleratorTextOverride = track.DisplayDetail;

            row.Click += (source, _) =>
            {
                if (Attached && ViewModel.IsCurrentInteraction(context)
                    && source is MenuFlyoutItem { Tag: int id }) ViewModel.SelectTrack(audio, id);
            };

            menu.Items.Add(row);
        }
    }

    // ---- 倍速 --------------------------------------------------------------------
    //
    // 倍速不再是菜单（2026-09-25 用户令「改为竖置的滚动条滚轮，刻度居中，使用鼠标滚轮翻动或者鼠标左键
    // 长按拖拽」）：按钮开的是 SpeedWheelFlyout 那只轮盘，刻度与手势的接线在 PlayerPage.SpeedWheel.cs，
    // 算术在 Core 的 SpeedWheel。这里原先是 OnSpeedMenuOpening —— 八行 RadioMenuFlyoutItem 的旧路。

    // ---- ⚙更多 -------------------------------------------------------------------
    //
    // 2026-09-27 晚用户令「移除集成模式右下角的更多按钮」：右下那颗按钮（`MoreButton`）与控制条上它挂的
    // 那个 `MoreMenu` 一起摘掉了。**菜单本体没有跟着删** —— 着色器三档、跳过片头/片尾、自动连播、播放信息
    // 这几行只有这一处入口，删按钮不等于删功能。整棵拼装仍是「每次打开现算」（每行都显当前值）。
    //
    // ⚠️ 这一棵挂在右键画面菜单上（见 `OnPictureMenuOpening`）：2026-09-27 晚右下角那颗「更多」按钮撤掉时，
    // 它是整页唯一的入口，于是整棵并进右键菜单 —— 着色器三档、跳过片头/片尾、自动连播、播放信息落在
    // 画面目录之后，由一条分隔线隔开。`ProbePictureMenu` 按目录算期望行数，那一条的计数已同步把这一棵
    // 数进去（见该处的注释）。
    //
    // 2026-09-29 用户令「删掉右键菜单里的这些」（截图从「片段循环」下面那条线拍到「选集…」）：跟控制条
    // 和键盘重复的那一整块摘掉了 —— 播放/暂停、统计、上一章节、下一章节、章节、音轨…、字幕…、播放速度…、
    // 上一集、下一集、选集…。这些都另有入口（空格、统计钮、PageUp/PageDown、章节钮、音轨/字幕/倍速/选集
    // 钮、P/N），摘掉不等于删功能。版本… 不在截图里，照旧留下；画面目录里的片段循环是它在集成模式里
    // 唯一的入口，也不动。

    /// <summary>
    /// 更多: the rows that have no other entrance. Built on every open rather than kept in XAML, because
    /// every row of it shows a current value — the shader group in force, the setting that decides what
    /// 跳过 does.
    /// <para>
    /// 2026-09-27 晚独立的那颗按钮撤下之后，这一棵拼进的是右键画面菜单（<paramref name="menu"/>），
    /// 不再是控制条右下角那颗 `MoreButton` 挂的浮层；锚点参数化是为了让子菜单还能贴着当前那颗宿主浮层
    /// 弹出（见 <see cref="OnPictureMenuOpening"/>）。2026-09-29 起它不再带跟控制条重复的那一排
    /// （播放/暂停到选集…，见上）。
    /// </para>
    /// <para>
    /// 2026-09-29 用户令「统一独占模式和集成模式的右键菜单选项」：这一棵在独占模式有了同款 ——
    /// <c>PlayerViewModel.PushPictureMenuAsync</c> 把同一批行（版本…、着色器、跳过片头片尾、自动播放
    /// 下一集、播放信息…）按同一次序拼进推给 uosc 的菜单。两处各是各的 UI 栈（这边 XAML 行，那边 uosc
    /// 菜单行），但行集、次序与文案必须一致 —— 改这边任何一行，那边要跟上，反过来也一样。
    /// </para>
    /// </summary>
    private void OnMoreMenuOpening(MenuFlyout menu, FrameworkElement anchor)
    {
        if (!Attached) return;

        menu.Items.Clear();
        if (ViewModel.VersionControlsVisible)
        {
            AddAction("版本…", () => VersionMenu.ShowAt(anchor));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        menu.Items.Add(BuildShaderMenu());
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(BuildSkipMenu());
        menu.Items.Add(BuildAutoPlayRow());
        menu.Items.Add(new MenuFlyoutSeparator());

        var info = new MenuFlyoutItem { Text = "播放信息…" };
        info.Click += (_, _) => _ = ShowMediaInfoAsync();
        menu.Items.Add(info);

        // 两条管线都将设置入口收进「播放设置」，名称与次序共用 Core 那张表。
        // 子项仍走 ViewModel.RequestSettings，由外壳打开对应设置卡。
        menu.Items.Add(new MenuFlyoutSeparator());
        var settings = new MenuFlyoutSubItem { Text = PlayerSettingsLinks.MenuLabel, Name = "PlaybackSettingsMenu" };
        foreach (var link in PlayerSettingsLinks.All)
        {
            var row = new MenuFlyoutItem { Text = link.Label, Tag = link, Name = $"PlaybackSettings_{link.Token}" };
            row.Click += OnPictureSettingsRow;
            settings.Items.Add(row);
        }
        menu.Items.Add(settings);

        void AddAction(string label, Action action)
        {
            var row = new MenuFlyoutItem { Text = label };
            row.Click += (_, _) => action();
            menu.Items.Add(row);
        }
    }

    /// <summary>
    /// 点了「播放设置」中的设置入口：把这一行交给 view model，由外壳开设置窗口。
    /// <para>
    /// 命名方法而不是就地写一个 lambda，是为了自检能点到同一下（<c>ClickPictureSettingsRow</c>）——
    /// 那一条读数要的是「按真菜单行的真处理器会发生什么」，不是自检另起一句等价的话。
    /// </para>
    /// </summary>
    private void OnPictureSettingsRow(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: PlayerSettingsLink picked }) ViewModel.RequestSettings(picked);
    }

    private MenuFlyoutSubItem BuildShaderMenu()
    {
        var active = ViewModel.ActiveShader;
        var pinned = ViewModel.ShaderChoicePinned;
        var known = ViewModel.ShaderStateKnown;
        var groupName = "shader-" + Guid.NewGuid().ToString("N");
        var menu = new MenuFlyoutSubItem
        {
            Text = $"着色器：{ViewModel.ActiveShaderLabel}"
        };

        // 恢复设置的方案（2026-09-29 统一右键菜单时从独占模式的推送收编过来，两边的行集从此一致）：
        // 没钉档时它亮着 —— 起播算出的那档在生效；钉过档之后点它是唯一回自动方案的路（两条管线同一句执行）。
        var restore = new RadioMenuFlyoutItem { Text = "恢复设置的方案", GroupName = groupName, IsChecked = known && !pinned };
        restore.Click += (_, _) => ViewModel.RestoreShaderPlan();
        menu.Items.Add(restore);

        var off = new RadioMenuFlyoutItem { Text = "关闭着色器", GroupName = groupName, IsChecked = known && pinned && active is null };
        off.Click += (_, _) => ViewModel.ApplyShaderGroup(null);
        menu.Items.Add(off);
        menu.Items.Add(new MenuFlyoutSeparator());

        // The eight cells this machine's 显卡档 offers, each row naming its own chain in the dim right-hand
        // column — that column is the whole point of the menu, because comparing two chains on one paused
        // frame is the only way anyone can tell them apart. 打勾判据与「恢复设置的方案」那行是一体的：
        // 只有钉档（用户亲手点过）才轮得到某一档亮，方案在生效时亮的是上面那行 —— 与独占模式同一套。
        foreach (var group in ViewModel.ShaderCatalog)
        {
            var row = new RadioMenuFlyoutItem
            {
                Text = group.DisplayName,
                GroupName = groupName,
                IsChecked = known && pinned && active is not null && string.Equals(active.Id, group.Id, StringComparison.Ordinal),
                KeyboardAcceleratorTextOverride = group.Description,
                Tag = group
            };

            row.Click += (source, _) =>
            {
                if (source is MenuFlyoutItem { Tag: ShaderGroup picked }) ViewModel.ApplyShaderGroup(picked);
            };

            menu.Items.Add(row);
        }

        return menu;
    }

    private MenuFlyoutSubItem BuildSkipMenu()
    {
        var mode = ViewModel.SkipMode;
        var menu = new MenuFlyoutSubItem { Text = $"跳过片头片尾：{PlayerViewModel.SkipModeLabel(mode)}" };

        foreach (var choice in new[] { SkipSectionMode.Ask, SkipSectionMode.Auto, SkipSectionMode.Off })
        {
            var row = new RadioMenuFlyoutItem
            {
                Text = PlayerViewModel.SkipModeLabel(choice),
                IsChecked = mode == choice,
                Tag = choice
            };

            row.Click += (source, _) =>
            {
                if (source is MenuFlyoutItem { Tag: SkipSectionMode picked }) ViewModel.SkipMode = picked;
            };

            menu.Items.Add(row);
        }

        return menu;
    }

    private ToggleMenuFlyoutItem BuildAutoPlayRow()
    {
        var row = new ToggleMenuFlyoutItem
        {
            Text = "自动播放下一集",
            IsChecked = ViewModel.AutoPlayNextEpisode
        };

        row.Click += (source, _) =>
        {
            if (source is ToggleMenuFlyoutItem toggle) ViewModel.AutoPlayNextEpisode = toggle.IsChecked;
        };

        return row;
    }

    // ---- 画面 --------------------------------------------------------------------

    /// <summary>
    /// 画面: mpv's own video controls — 缩放、旋转、去色带、锐化、重置 — as a menu. Its structure（labels and
    /// commands）never changes, so it is built once and kept; what does change is which rows are current —
    /// 解码方式, 声道布局, 抖动补偿… — and that is refreshed on every open by reading those properties from mpv
    /// (see <see cref="RefreshPictureChecksAsync"/>). The rows are <see cref="PlayerMenuCatalog"/>'s, in Core: a
    /// table of labels, mpv commands and — for the checkable ones — the property that ticks them, which is why
    /// adding one is a line of data rather than a handler.
    /// </summary>
    private void OnPictureMenuOpening(object sender, object e)
    {
        if (PictureMenu.Items.Count == 0)
        {
            _pictureChecks.Clear();

            // 画面目录（PlayerMenuCatalog）在前，下面接一段分隔与「更多」那一棵 —— 2026-09-27 晚右下角那颗
            // 「更多」按钮撤下后，着色器/跳过/自动连播/播放信息这几行只有这一处入口，整棵并进右键菜单里。
            // **`ProbePictureMenu` 那一条自检把这几行也数进去**（它按目录算期望行数），所以它两侧的计数
            // 同步加上了这一棵 —— 见那条判据旁的注释。
            foreach (var item in BuildMenuItems(PlayerMenuCatalog.Root)) PictureMenu.Items.Add(item);
            PictureMenu.Items.Add(new MenuFlyoutSeparator());
            _pictureStaticCount = PictureMenu.Items.Count;
        }

        while (PictureMenu.Items.Count > _pictureStaticCount)
            PictureMenu.Items.RemoveAt(PictureMenu.Items.Count - 1);
        var more = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
        OnMoreMenuOpening(more, Root);
        while (more.Items.Count > 0)
        {
            var row = more.Items[0];
            more.Items.RemoveAt(0);
            PictureMenu.Items.Add(row);
        }

        _ = RefreshPictureChecksAsync();
    }

    /// <summary>
    /// Re-reads the mpv properties the checkable rows tick from and sets each row's tick. Fired on every open
    /// (the flyout is already on screen; on the in-process backend the reads finish before it is seen). No
    /// film / no control channel → the reads come back null and every row goes unticked, which is the honest
    /// answer — same as <see cref="ProbePictureMenu"/>'s synthetic state.
    /// </summary>
    private async Task RefreshPictureChecksAsync()
    {
        if (!Attached || _pictureChecks.Count == 0) return;
        var viewModel = ViewModel;
        var context = viewModel.CaptureInteraction();
        var checks = _pictureChecks.ToArray();

        var values = await ViewModel.ReadMenuChecksAsync().ConfigureAwait(true);
        if (!Attached || !ReferenceEquals(viewModel, ViewModel) || !ViewModel.IsCurrentInteraction(context)) return;

        foreach (var (item, node) in checks)
            SetChecked(item, node.IsCheckedBy(values));
    }

    private static void SetChecked(MenuFlyoutItem item, bool value)
    {
        switch (item)
        {
            case RadioMenuFlyoutItem radio: radio.IsChecked = value; break;
            case ToggleMenuFlyoutItem toggle: toggle.IsChecked = value; break;
        }
    }

    /// <summary>
    /// One level of the catalogue as WinUI menu rows. Recurses for submenus. A row that can show its current
    /// state（<see cref="PlayerMenuNode.State"/>）becomes a <see cref="RadioMenuFlyoutItem"/>（radio choices —
    /// 解码方式, 声道布局, 宽高比）or a <see cref="ToggleMenuFlyoutItem"/>（on/off — 抖动补偿, 裁切填充…）; the
    /// tick itself is set later by <see cref="RefreshPictureChecksAsync"/>. Every kind still runs through the
    /// one <see cref="PlayerViewModel.RunMenuNodeAsync"/> click, so nothing about execution changes.
    /// </summary>
    private IEnumerable<MenuFlyoutItemBase> BuildMenuItems(IReadOnlyList<PlayerMenuNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node.Kind)
            {
                case PlayerMenuKind.Separator:
                    yield return new MenuFlyoutSeparator();
                    break;

                case PlayerMenuKind.Group:
                    var group = new MenuFlyoutSubItem { Text = node.Label };
                    foreach (var child in BuildMenuItems(node.Children)) group.Items.Add(child);
                    yield return group;
                    break;

                default:
                    // Radio for a mutually-exclusive choice, toggle for an on/off row, plain for a pure action.
                    // GroupName keys the radio set to its property so a click reads as clean single-selection;
                    // the menu is built once, so no group accumulates across opens（选集 菜单那条陷阱不在这里）.
                    MenuFlyoutItem row =
                        node.State is null ? new MenuFlyoutItem { Text = node.Label, Tag = node }
                        : node.State.Radio
                            ? new RadioMenuFlyoutItem { Text = node.Label, Tag = node, GroupName = "pic-" + node.State.Property }
                            : new ToggleMenuFlyoutItem { Text = node.Label, Tag = node };

                    row.Click += (source, args) =>
                    {
                        if (Attached && source is MenuFlyoutItem { Tag: PlayerMenuNode picked })
                            _ = ViewModel.RunMenuNodeAsync(picked);
                    };

                    if (node.State is not null) _pictureChecks.Add((row, node));

                    yield return row;
                    break;
            }
        }
    }
}
