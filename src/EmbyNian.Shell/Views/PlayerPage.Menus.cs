using System.Globalization;
using EmbyNian.Emby;
using EmbyNian.Mpv;
using EmbyNian.Playback;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

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
                if (source is MenuFlyoutItem { Tag: EmbyItem picked }) ViewModel.SwitchEpisode(picked);
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
                if (clicked is MenuFlyoutItem { Tag: MediaSource picked }) ViewModel.SwitchVersion(picked);
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

        if (offRow is not null)
        {
            var off = new RadioMenuFlyoutItem
            {
                Text = offRow,
                IsChecked = tracks.All(track => !track.Selected)
            };
            off.Click += (_, _) => ViewModel.SelectTrack(audio, null);
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
                if (source is MenuFlyoutItem { Tag: int id }) ViewModel.SelectTrack(audio, id);
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

    /// <summary>
    /// 更多: everything that is worth reaching mid-film but not worth a button of its own. Built on every
    /// open rather than kept in XAML, because every row of it shows a current value — the shader group in
    /// force, the delay applied, the setting that decides what 跳过 does.
    /// </summary>
    private void OnMoreMenuOpening(object sender, object e)
    {
        if (!Attached) return;

        MoreMenu.Items.Clear();
        MoreMenu.Items.Add(BuildShaderMenu());
        MoreMenu.Items.Add(BuildDelayMenu("字幕延迟", subtitle: true));
        MoreMenu.Items.Add(BuildDelayMenu("音频延迟", subtitle: false));
        MoreMenu.Items.Add(new MenuFlyoutSeparator());
        MoreMenu.Items.Add(BuildSkipMenu());
        MoreMenu.Items.Add(BuildAutoPlayRow());
        MoreMenu.Items.Add(new MenuFlyoutSeparator());

        var info = new MenuFlyoutItem { Text = "播放信息…" };
        info.Click += (_, _) => _ = ShowMediaInfoAsync();
        MoreMenu.Items.Add(info);
    }

    private MenuFlyoutSubItem BuildShaderMenu()
    {
        var active = ViewModel.ActiveShader;
        var menu = new MenuFlyoutSubItem
        {
            Text = active is null ? "着色器：未启用" : $"着色器：{active.Name}"
        };

        var off = new RadioMenuFlyoutItem { Text = "关闭着色器", IsChecked = active is null };
        off.Click += (_, _) => ViewModel.ApplyShaderGroup(null);
        menu.Items.Add(off);
        menu.Items.Add(new MenuFlyoutSeparator());

        // The eight cells this machine's 显卡档 offers, each row naming its own chain in the dim right-hand
        // column — that column is the whole point of the menu, because comparing two chains on one paused
        // frame is the only way anyone can tell them apart.
        foreach (var group in ViewModel.ShaderCatalog)
        {
            var row = new RadioMenuFlyoutItem
            {
                Text = group.DisplayName,
                IsChecked = active is not null && string.Equals(active.Id, group.Id, StringComparison.Ordinal),
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

    /// <summary>
    /// 字幕/音频延迟: the same nudges the Z and X keys apply, offered as rows for the times a value has to be
    /// dialled in rather than tapped out. The label opens on whatever the keys left it at.
    /// </summary>
    private MenuFlyoutSubItem BuildDelayMenu(string label, bool subtitle)
    {
        var current = subtitle ? ViewModel.SubtitleDelay : ViewModel.AudioDelay;
        var menu = new MenuFlyoutSubItem
        {
            Text = Math.Abs(current) < 0.001
                ? $"{label}：0 秒"
                : $"{label}：{current.ToString("+0.0#;-0.0#", CultureInfo.InvariantCulture)} 秒"
        };

        foreach (var nudge in PlayerViewModel.DelayNudges)
        {
            var row = new MenuFlyoutItem
            {
                Text = $"{nudge.ToString("+0.0#;-0.0#", CultureInfo.InvariantCulture)} 秒",
                Tag = nudge
            };

            row.Click += (source, args) =>
            {
                if (source is MenuFlyoutItem { Tag: double step })
                    ViewModel.SetDelay(subtitle, Math.Round(current + step, 3));
            };

            menu.Items.Add(row);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var reset = new MenuFlyoutItem { Text = "归零", IsEnabled = Math.Abs(current) >= 0.001 };
        reset.Click += (_, _) => ViewModel.SetDelay(subtitle, 0);
        menu.Items.Add(reset);

        return menu;
    }

    private MenuFlyoutSubItem BuildSkipMenu()
    {
        var mode = ViewModel.SkipMode;
        var menu = new MenuFlyoutSubItem { Text = $"跳过片头片尾：{SkipModeLabel(mode)}" };

        foreach (var choice in new[] { SkipSectionMode.Ask, SkipSectionMode.Auto, SkipSectionMode.Off })
        {
            var row = new RadioMenuFlyoutItem
            {
                Text = SkipModeLabel(choice),
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

    private static string SkipModeLabel(SkipSectionMode mode) => mode switch
    {
        SkipSectionMode.Auto => "自动跳过",
        SkipSectionMode.Off => "关闭",
        _ => "询问"
    };

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
            foreach (var item in BuildMenuItems(PlayerMenuCatalog.Root)) PictureMenu.Items.Add(item);
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

        var values = await ViewModel.ReadMenuChecksAsync().ConfigureAwait(true);

        foreach (var (item, node) in _pictureChecks)
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
