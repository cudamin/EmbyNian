using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 需求 6：已看、收藏、编辑. The commands one item offers, in one place so that a poster on the home
/// page, one in a library and one in a set of search results all behave identically.
/// <para>
/// Built per open rather than declared in XAML. Every label depends on the item — 立即播放 or
/// 继续播放（12:34）, 标记为已观看 or 标记为未观看 — and a static menu would have to be walked and
/// relabelled on each open anyway, which is the same work with the item's state spread over two files.
/// </para>
/// <para>
/// The session and the shell are taken directly rather than as a <see cref="LibraryRequest"/>: the home
/// page has no such request, and nothing here needs one — a menu opens items, it does not list them.
/// Every command below is one request and a repaint, so the session is the only capability involved.
/// </para>
/// </summary>
internal static class ItemCommands
{
    private const string Category = "ui";

    /// <summary>
    /// The handler both grids attach. <c>ContextRequested</c> rather than a right-tap handler because it
    /// is the one event that covers right-click, touch long-press and the keyboard's own menu request;
    /// <see cref="ContextRequestedEventArgs.TryGetPosition"/> returning false is how the keyboard case
    /// announces itself, and a menu placed at a made-up point is a menu that opens away from the card
    /// the focus is on.
    /// </summary>
    public static void Handle(
        EmbySession? session,
        IShellActions? shell,
        object sender,
        ContextRequestedEventArgs args,
        IReadOnlyList<EmbyItem>? siblings = null,
        Action? changed = null)
    {
        if (session is null || shell is null || sender is not FrameworkElement owner) return;
        if (Card(owner) is not { } card) return;

        Show(session, shell, owner, card, args.TryGetPosition(owner, out var point) ? point : null, siblings, changed);

        // Handled, or the request keeps bubbling and the page behind opens its own menu over ours.
        args.Handled = true;
    }

    /// <summary>
    /// 需求 6：the same three commands from the card's own hover buttons. Two of them are the menu's
    /// entries with the menu skipped; 更多 <em>is</em> the menu, anchored to the button rather than to
    /// the card so it does not open a poster's height away from what was pressed. The episode list's
    /// rows raise the same event, with the play button they carry where a poster has it.
    /// </summary>
    public static void Handle(
        EmbySession? session,
        IShellActions? shell,
        object? sender,
        CardActionEventArgs args,
        IReadOnlyList<EmbyItem>? siblings = null,
        Action? changed = null)
    {
        if (session is null || shell is null) return;

        var card = sender switch
        {
            PosterCard { Card: { } poster } => poster,
            EpisodeRow { Card: { } row } => row,
            _ => null
        };
        if (card is null) return;

        switch (args.Action)
        {
            case CardAction.Play:
                _ = shell.PlayAsync(card.Item, episodes: siblings);
                break;
            case CardAction.Watched:
                ToggleWatched(session, shell, card);
                break;
            case CardAction.Favorite:
                ToggleFavorite(session, shell, card);
                break;
            default:
                Show(session, shell, args.Anchor, card, position: null, siblings, changed);
                break;
        }
    }

    /// <summary>Pops the menu up over <paramref name="owner"/>.</summary>
    /// <param name="position">
    /// Where the gesture happened, in <paramref name="owner"/>'s coordinates, or null for a keyboard
    /// request. Null is not a missing value to work around: it means 「no pointer was involved」.
    /// </param>
    /// <param name="siblings">
    /// The episodes already on screen beside this one, so 选集 and 上一集/下一集 work off the grid the
    /// user is looking at instead of a fresh request. Null when the neighbours are not episodes.
    /// </param>
    /// <param name="changed">
    /// Run after a change the menu could not apply locally — a metadata save, which can alter the title,
    /// the artwork and the sort position at once. User-state changes do not use it: they patch the card.
    /// </param>
    public static void Show(
        EmbySession session,
        IShellActions shell,
        FrameworkElement owner,
        CardItem card,
        Point? position,
        IReadOnlyList<EmbyItem>? siblings = null,
        Action? changed = null)
    {
        var item = card.Item;
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };

        if (item.IsPlayable)
        {
            menu.Items.Add(Entry(
                item.HasResumePosition ? $"继续播放（{TimeFormat.Clock(item.ResumeTicks)}）" : "立即播放",
                Glyph.Play,
                () => _ = shell.PlayAsync(item, episodes: siblings)));

            menu.Items.Add(Entry("详细信息", Glyph.Info, () => shell.OpenItem(item)));
        }
        else
        {
            // 打开 rather than two entries: for a series this is the detail page and for a box set it is
            // another grid, and which of the two it is is not a decision to put in front of the user.
            menu.Items.Add(Entry("打开", Glyph.Open, () => shell.OpenItem(item)));
        }

        // 已观看/收藏 only where they exist. A 媒体库 and a 演职人员 have no such state, and the
        // separator goes with them or the menu ends up with two rules in a row.
        if (item.TracksUserState)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var played = item.UserData?.Played == true;
            menu.Items.Add(Entry(
                played ? "标记为未观看" : "标记为已观看",
                played ? Glyph.Unwatched : Glyph.Watched,
                () => ToggleWatched(session, shell, card)));

            var favourite = item.UserData?.IsFavorite == true;
            menu.Items.Add(Entry(
                favourite ? "取消收藏" : "添加到收藏",
                favourite ? Glyph.Unfavorite : Glyph.Favorite,
                () => ToggleFavorite(session, shell, card)));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Entry("编辑元数据…", Glyph.Edit, () => _ = EditAsync(session, shell, owner, card, changed)));

        if (item.Type == EmbyItemType.Episode && item.SeriesId is { Length: > 0 } seriesId)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Entry("打开所属剧集", Glyph.Series, () => Run(
                session,
                shell,
                "打开剧集失败",
                async (client, token) =>
                {
                    var series = await client.GetItemAsync(seriesId, token).ConfigureAwait(true);
                    shell.OpenItem(series);
                    return (EmbyUserData?)null;
                },
                null)));
        }

        if (position is { } point) menu.ShowAt(owner, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(owner);
    }

    /// <summary>
    /// 已看. Reached from the menu and from the card's hover strip, which is why the current state is
    /// read here and not passed in: between building a menu and clicking it, the strip may already have
    /// flipped it.
    /// <para>
    /// Ends in a patch of the card rather than a reload of the page: the server has just told us the new
    /// state, and rebuilding a grid of two hundred posters to move one tick mark would be absurd.
    /// </para>
    /// </summary>
    public static void ToggleWatched(EmbySession session, IShellActions shell, CardItem card)
    {
        var played = card.Item.UserData?.Played == true;

        Run(
            session,
            shell,
            played ? "标记未观看失败" : "标记已观看失败",
            (client, token) => played
                ? client.MarkUnplayedAsync(card.Item.Id, token)
                : client.MarkPlayedAsync(card.Item.Id, token),
            data => Patch(card, data, state => state.Played = !played));
    }

    /// <summary>收藏. As above.</summary>
    public static void ToggleFavorite(EmbySession session, IShellActions shell, CardItem card)
    {
        var favourite = card.Item.UserData?.IsFavorite == true;

        Run(
            session,
            shell,
            "更新收藏失败",
            (client, token) => client.SetFavoriteAsync(card.Item.Id, !favourite, token),
            data => Patch(card, data, state => state.IsFavorite = !favourite));
    }

    /// <summary>
    /// The card the gesture landed on. Read off the element rather than passed in, because the sender is
    /// whatever container the grid recycled onto this row and its content is the only thing that says
    /// which item that is now.
    /// </summary>
    private static CardItem? Card(FrameworkElement element) => element switch
    {
        PosterCard { Card: { } card } => card,
        EpisodeRow { Card: { } row } => row,
        ContentControl { Content: PosterCard { Card: { } card } } => card,
        ContentControl { Content: EpisodeRow { Card: { } row } } => row,
        { DataContext: CardItem card } => card,
        _ => null
    };

    private static MenuFlyoutItem Entry(string text, string glyph, Action invoke)
    {
        var entry = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        entry.Click += (_, _) => invoke();
        return entry;
    }

    /// <summary>
    /// Applies the server's own answer to the card, falling back to the change we asked for.
    /// <para>
    /// The fallback is not laziness: marking a film played also clears its resume position, and marking
    /// a series played empties the remaining-episode count, so the server's <c>UserItemDataDto</c> is
    /// worth more than the one flag we flipped. But no Emby version promises that body, and a card that
    /// refuses to show a tick because the response was empty is worse than one whose progress bar is a
    /// refresh out of date.
    /// </para>
    /// </summary>
    private static void Patch(CardItem card, EmbyUserData? data, Action<EmbyUserData> fallback)
    {
        if (data is not null)
        {
            card.Item.UserData = data;
        }
        else
        {
            card.Item.UserData ??= new EmbyUserData();
            fallback(card.Item.UserData);
        }

        card.RefreshUserData();
    }

    private static void Run(
        EmbySession session,
        IShellActions shell,
        string failure,
        Func<EmbyClient, CancellationToken, Task<EmbyUserData?>> work,
        Action<EmbyUserData?>? applied) => _ = RunAsync(session, shell, failure, work, applied);

    private static async Task RunAsync(
        EmbySession session,
        IShellActions shell,
        string failure,
        Func<EmbyClient, CancellationToken, Task<EmbyUserData?>> work,
        Action<EmbyUserData?>? applied)
    {
        try
        {
            var data = await session.ExecuteAsync(work, CancellationToken.None).ConfigureAwait(true);
            applied?.Invoke(data);
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
            shell.Notify($"{failure}：{Failure.Describe(error)}", InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// 编辑元数据. Reads the item back before showing the form — the grid's copy came from a browse query
    /// with a limited field set, so editing from it would offer a blank 简介 and then save that blank
    /// over the real one.
    /// </summary>
    private static async Task EditAsync(
        EmbySession session,
        IShellActions shell,
        FrameworkElement owner,
        CardItem card,
        Action? changed)
    {
        var item = card.Item;

        try
        {
            var json = await session
                .ExecuteAsync((client, token) => client.GetItemJsonAsync(item.Id, token), CancellationToken.None)
                .ConfigureAwait(true);

            var dialog = new MetadataDialog(item, ItemMetadataEdit.Read(json)) { XamlRoot = owner.XamlRoot };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            // Nothing to send is a success, not a no-op to report: the user opened the form, looked, and
            // pressed 保存. Saying 「已保存」 for a request that never went out would be a lie, and an
            // error for it would be nonsense.
            if (!dialog.Edit.ApplyTo(json))
            {
                Log.Debug(Category, $"编辑元数据：{item.Name} 没有改动");
                return;
            }

            await session
                .ExecuteAsync((client, token) => client.UpdateItemAsync(item.Id, json, token), CancellationToken.None)
                .ConfigureAwait(true);

            shell.Notify($"已保存「{dialog.Edit.Name}」的元数据");

            // A metadata save can change the title, the sort position and the artwork at once, and no
            // amount of patching one card covers that. This is the one command that reloads.
            changed?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, "编辑元数据失败", error);
            shell.Notify($"编辑元数据失败：{Failure.Describe(error)}", InfoBarSeverity.Error);
        }
    }

    /// <summary>Segoe Fluent codepoints; see the note in <see cref="CardItem"/> on why numbers.</summary>
    private static class Glyph
    {
        public static readonly string Play = Of(0xE768);
        public static readonly string Open = Of(0xE8B7);
        public static readonly string Info = Of(0xE946);
        public static readonly string Watched = Of(0xE73E);
        public static readonly string Unwatched = Of(0xE738);
        public static readonly string Favorite = Of(0xE734);
        public static readonly string Unfavorite = Of(0xE8D9);
        public static readonly string Edit = Of(0xE70F);
        public static readonly string Series = Of(0xE7F4);

        private static string Of(int codepoint) => char.ConvertFromUtf32(codepoint);
    }
}
