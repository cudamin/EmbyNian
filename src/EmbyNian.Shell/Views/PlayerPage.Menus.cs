using System.Globalization;
using EmbyNian.Emby;
using EmbyNian.Mpv;
using EmbyNian.Playback;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// The player's six flyouts: 选集, 音轨, 字幕, 倍速, 画面 and ⚙更多.
/// <para>
/// All six are built as they open rather than declared in XAML, because every row of every one of them
/// shows a current value — which episode is playing, which track mpv picked, the delay the keys left, the
/// shader group in force. A menu declared once would be a menu that went stale on the first change.
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

    private void OnSpeedMenuOpening(object sender, object e)
    {
        if (!Attached) return;

        SpeedMenu.Items.Clear();

        foreach (var rate in PlayerViewModel.SpeedChoices)
        {
            var row = new RadioMenuFlyoutItem
            {
                Text = rate == 1 ? "1.0×（正常）" : $"{rate.ToString("0.0#", CultureInfo.InvariantCulture)}×",
                IsChecked = Math.Abs(ViewModel.Status.Speed - rate) < 0.005,
                Tag = rate
            };

            // No notice: the menu row the user just clicked is already the answer to 「什么倍速」, and mpv's
            // own OSD text over it would be one message too many.
            row.Click += (source, args) =>
            {
                if (source is MenuFlyoutItem { Tag: double picked }) ViewModel.SetSpeed(picked, notice: false);
            };

            SpeedMenu.Items.Add(row);
        }
    }

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
    /// 画面: mpv's own video controls — 缩放、旋转、去色带、锐化、重置 — as a menu, built once and kept,
    /// because unlike the other five nothing in it shows a current value. The rows are
    /// <see cref="PlayerMenuCatalog"/>'s, in Core: a table of labels and mpv commands, which is why adding
    /// one is a line of data rather than a handler.
    /// </summary>
    private void OnPictureMenuOpening(object sender, object e)
    {
        if (PictureMenu.Items.Count > 0) return;

        foreach (var item in BuildMenuItems(PlayerMenuCatalog.Root)) PictureMenu.Items.Add(item);
    }

    /// <summary>One level of the catalogue as WinUI menu rows. Recurses for submenus.</summary>
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
                    var row = new MenuFlyoutItem { Text = node.Label, Tag = node };
                    row.Click += (source, args) =>
                    {
                        if (Attached && source is MenuFlyoutItem { Tag: PlayerMenuNode picked })
                            _ = ViewModel.RunMenuNodeAsync(picked);
                    };
                    yield return row;
                    break;
            }
        }
    }
}
