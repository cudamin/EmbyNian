using EmbyMpvClient.App.Controls;
using EmbyMpvClient.App.Theme;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Emby;
using EmbyMpvClient.Infrastructure;

namespace EmbyMpvClient.App.Views;

/// <summary>
/// The right-click menu shared by every page that shows items. Kept in one place so 「立即播放」
/// and 「标记为已观看」 behave the same on the home page, in a library and in search results.
/// </summary>
internal static class ItemMenu
{
    private const string Category = "ui";

    /// <summary>
    /// Pops the menu up at the cursor. <paramref name="changed"/> is awaited after a server-side
    /// change so the calling page can re-read the item it just mutated.
    /// </summary>
    public static void Show(IShell shell, Control owner, EmbyItem item, EmbyItem? parent = null, Func<Task>? changed = null)
    {
        var menu = DarkMenu.Create();

        if (item.IsPlayable)
        {
            menu.Items.Add(DarkMenu.Item(
                item.HasResumePosition ? $"继续播放（{TimeFormat.Clock(item.ResumeTicks)}）" : "立即播放",
                () => _ = shell.PlayAsync(item, parent)));

            menu.Items.Add(DarkMenu.Item("查看详情", () => shell.Open(item)));
        }
        else
        {
            menu.Items.Add(DarkMenu.Item("打开", () => shell.Open(item)));
        }

        menu.Items.Add(new ToolStripSeparator());

        var played = item.UserData?.Played == true;
        menu.Items.Add(DarkMenu.Item(played ? "标记为未观看" : "标记为已观看", () => Fire(
            shell,
            played ? "标记未观看失败" : "标记已观看失败",
            (client, token) => played ? client.MarkUnplayedAsync(item.Id, token) : client.MarkPlayedAsync(item.Id, token),
            changed)));

        var favourite = item.UserData?.IsFavorite == true;
        menu.Items.Add(DarkMenu.Item(favourite ? "取消收藏" : "添加到收藏", () => Fire(
            shell,
            "更新收藏失败",
            (client, token) => client.SetFavoriteAsync(item.Id, !favourite, token),
            changed)));

        if (item.Type == EmbyItemType.Episode && item.SeriesId is { Length: > 0 } seriesId)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(DarkMenu.Item("打开所属剧集", () => Fire(
                shell,
                "打开剧集失败",
                async (client, token) => shell.Open(await client.GetItemAsync(seriesId, token).ConfigureAwait(true)),
                null)));
        }

        // The strip owns itself: disposing it after the click has been dispatched keeps the pages
        // free of menu bookkeeping. Posted rather than immediate — Closed runs before Click.
        menu.Closed += (_, _) => owner.BeginInvoke(new Action(menu.Dispose));
        menu.Show(owner, owner.PointToClient(Cursor.Position));
    }

    private static void Fire(IShell shell, string failure, Func<EmbyClient, CancellationToken, Task> work, Func<Task>? changed) =>
        _ = RunAsync(shell, failure, work, changed);

    private static async Task RunAsync(IShell shell, string failure, Func<EmbyClient, CancellationToken, Task> work, Func<Task>? changed)
    {
        try
        {
            await shell.Host.Session.ExecuteAsync(work, CancellationToken.None).ConfigureAwait(true);
            if (changed is not null) await changed().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, failure, error);
            shell.Notify($"{failure}：{AppView.Describe(error)}", ToastKind.Error);
        }
    }
}

/// <summary>
/// WinForms menus are painted by the renderer, not by the control's colours, so a dark menu needs
/// its own colour table — otherwise every context menu in the app is a white rectangle.
/// </summary>
internal static class DarkMenu
{
    public static ContextMenuStrip Create() => new()
    {
        Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()) { RoundedEdges = false },
        BackColor = Palette.SurfaceAlt,
        ForeColor = Palette.Text,
        Font = Fonts.Body,
        ShowImageMargin = false
    };

    public static ToolStripMenuItem Item(string text, Action action) =>
        new(text, null, (_, _) => action())
        {
            BackColor = Palette.SurfaceAlt,
            ForeColor = Palette.Text
        };

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Palette.SurfaceAlt;

        public override Color MenuBorder => Palette.BorderStrong;

        public override Color MenuItemBorder => Palette.BorderStrong;

        public override Color MenuItemSelected => Palette.SurfaceHover;

        public override Color MenuItemSelectedGradientBegin => Palette.SurfaceHover;

        public override Color MenuItemSelectedGradientEnd => Palette.SurfaceHover;

        public override Color MenuItemPressedGradientBegin => Palette.SurfaceHover;

        public override Color MenuItemPressedGradientEnd => Palette.SurfaceHover;

        public override Color ImageMarginGradientBegin => Palette.SurfaceAlt;

        public override Color ImageMarginGradientMiddle => Palette.SurfaceAlt;

        public override Color ImageMarginGradientEnd => Palette.SurfaceAlt;

        public override Color SeparatorDark => Palette.Border;

        public override Color SeparatorLight => Palette.Border;
    }
}
