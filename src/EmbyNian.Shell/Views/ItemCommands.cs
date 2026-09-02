using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 需求 6：一张卡片能做的事都在这一处 —— 主页上的一张海报、媒体库里的一张、搜索结果里的一张，点开「更多」
/// 看到的是同一张菜单。
/// <para>
/// <b>菜单上有哪几条、每条写什么字，答案在 Core</b>（<see cref="ItemMenu"/>）；这个文件把它变成
/// <c>MenuFlyout</c> 的几行，再把每一行接到一件事上（<see cref="Invoke"/>）。分开是为了覆盖：单元测试只够得到
/// Core，而「一部剧上不该有搜索字幕」「一张媒体库卡片上不该有删除」正是那种写错了屏上也看着正常的判断。
/// </para>
/// <para>
/// 每次打开重新搭一张而不是在 XAML 里摆好：菜单上的字跟着条目走（继续播放（12:34）还是立即播放、标记为已观看
/// 还是未观看），摆好的那张每次打开也得整条走一遍改字 —— 同样的活儿，只是把条目的状态摊到了两个文件里。
/// </para>
/// <para>
/// 这里直接拿着 <see cref="EmbySession"/> 而不是走一层服务：主页上没有 <see cref="LibraryRequest"/>，而这里
/// 也不需要一个 —— 菜单是打开一个条目，不是列出一批。会碰服务器的那几条在
/// <c>ItemCommands.Server.cs</c>（同一个 partial）。
/// </para>
/// </summary>
internal static partial class ItemCommands
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
    /// Run after a change this menu could not apply to the card on the spot — a metadata save, a
    /// deletion, 从继续观看中移除, a new cover. Every one of those can move the title, the artwork or
    /// whether the item belongs in this row at all, and no amount of patching one card covers that.
    /// User-state changes do not use it: they patch the card.
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
        var menu = Build(session, shell, owner, card, siblings, changed);

        if (position is { } point) menu.ShowAt(owner, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(owner);
    }

    /// <summary>
    /// 把 <see cref="ItemMenu.For"/> 排好的那几行搭成一张真菜单。
    /// <para>
    /// 单独一支而不是并进 <see cref="Show"/>，是为了自检够得着：搭出来的行数、每行的字、每行带的那条命令
    /// （放在 <c>Tag</c> 上）都能读回来对账，而不用真把一张浮层弹到屏幕上 —— 弹出来会挡住自检接着要走的几步。
    /// 「代码搭的东西搭空了，屏上看着像一行普通的字」这种事这个项目撞过（详情页那一行类型）。
    /// </para>
    /// </summary>
    internal static MenuFlyout Build(
        EmbySession session,
        IShellActions shell,
        FrameworkElement owner,
        CardItem card,
        IReadOnlyList<EmbyItem>? siblings = null,
        Action? changed = null)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };

        foreach (var row in ItemMenu.For(card.Item))
        {
            if (row.Command is not { } command)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                continue;
            }

            menu.Items.Add(Entry(row.Label, command, () =>
                Invoke(command, session, shell, owner, card, siblings, changed)));
        }

        return menu;
    }

    /// <summary>
    /// 按下一行之后做什么。一处 switch 而不是把动作塞进 <see cref="ItemMenu"/>：那一头是「有哪几条」，这一头
    /// 才有服务器、外壳和对话框。每一条命令在这里都必须有着落 —— 漏一条的下场是点下去什么都不发生。
    /// </summary>
    private static void Invoke(
        ItemCommand command,
        EmbySession session,
        IShellActions shell,
        FrameworkElement owner,
        CardItem card,
        IReadOnlyList<EmbyItem>? siblings,
        Action? changed)
    {
        var item = card.Item;

        switch (command)
        {
            case ItemCommand.Play:
                _ = shell.PlayAsync(item, episodes: siblings);
                break;

            // 打开 rather than two entries for a series or a box set: for one this is the detail page and
            // for the other another grid, and which of the two it is is not a decision to put in front of
            // the user.
            case ItemCommand.Details:
            case ItemCommand.Open:
                shell.OpenItem(item);
                break;

            // 菜单上这两条是指定方向的（两条会同时出现，见 ItemMenu.UserState），所以不能走那个读当前状态再
            // 翻面的 ToggleWatched —— 那是悬浮层上那颗按钮的活儿。
            case ItemCommand.MarkPlayed:
            case ItemCommand.MarkUnplayed:
                SetWatched(session, shell, card, played: command == ItemCommand.MarkPlayed);
                break;

            case ItemCommand.Favorite:
            case ItemCommand.Unfavorite:
                SetFavorite(session, shell, card, favourite: command == ItemCommand.Favorite);
                break;

            case ItemCommand.HideFromResume:
                HideFromResume(session, shell, card, changed);
                break;

            case ItemCommand.AddToCollection:
                _ = CollectAsync(session, shell, owner, card);
                break;

            case ItemCommand.Download:
                _ = DownloadAsync(session, shell, card);
                break;

            case ItemCommand.EditMetadata:
                _ = EditAsync(session, shell, owner, card, changed);
                break;

            case ItemCommand.ChangeCover:
                _ = CoverAsync(session, shell, owner, card, changed);
                break;

            case ItemCommand.Subtitles:
                _ = SubtitlesAsync(session, shell, owner, card, changed);
                break;

            case ItemCommand.Scrape:
                _ = ScrapeAsync(session, shell, owner, card);
                break;

            case ItemCommand.RefreshMetadata:
                Refresh(session, shell, card);
                break;

            case ItemCommand.ScanLibrary:
                ScanLibrary(session, shell);
                break;

            case ItemCommand.OpenSeries:
                OpenSeries(session, shell, item);
                break;

            case ItemCommand.Delete:
                _ = DeleteAsync(session, shell, owner, card, changed);
                break;
        }
    }

    /// <summary>
    /// 已看，从悬浮层那颗按钮上按下的。当前状态在这里读而不是传进来：一颗按钮说的是「翻到另一面」，而它按下的
    /// 那一刻的状态才算数。
    /// </summary>
    public static void ToggleWatched(EmbySession session, IShellActions shell, CardItem card) =>
        SetWatched(session, shell, card, played: card.Item.UserData?.Played != true);

    /// <summary>收藏。同上。</summary>
    public static void ToggleFavorite(EmbySession session, IShellActions shell, CardItem card) =>
        SetFavorite(session, shell, card, favourite: card.Item.UserData?.IsFavorite != true);

    /// <summary>
    /// 标记为已观看 / 未观看。
    /// <para>
    /// 落地是打一次这张卡片的补丁，而不是重读整页：服务器刚把新状态告诉我们，为了移动一个对勾去重建两百张海报
    /// 的网格是荒唐的。
    /// </para>
    /// </summary>
    private static void SetWatched(EmbySession session, IShellActions shell, CardItem card, bool played) =>
        Run(
            session,
            shell,
            played ? "标记已观看失败" : "标记未观看失败",
            (client, token) => played
                ? client.MarkPlayedAsync(card.Item.Id, token)
                : client.MarkUnplayedAsync(card.Item.Id, token),
            data => Patch(card, data, state =>
            {
                state.Played = played;

                // 标记为未观看在服务器上连进度一起清掉 —— 服务器没回话的那一次，手上这张卡也得跟着清，否则
                // 底边那条进度还在，而它讲的是一个已经不存在的续播点。
                if (played) return;
                state.PlaybackPositionTicks = 0;
                state.PlayedPercentage = 0;
            }));

    /// <summary>收藏。同上。</summary>
    private static void SetFavorite(EmbySession session, IShellActions shell, CardItem card, bool favourite) =>
        Run(
            session,
            shell,
            "更新收藏失败",
            (client, token) => client.SetFavoriteAsync(card.Item.Id, favourite, token),
            data => Patch(card, data, state => state.IsFavorite = favourite));

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

    /// <summary>
    /// 菜单上的一行。<c>Tag</c> 上带着它是哪一条命令 —— 自检靠它认出搭出来的这一行是不是该在的那一行，见
    /// <see cref="Build"/>。
    /// </summary>
    private static MenuFlyoutItem Entry(string text, ItemCommand command, Action invoke)
    {
        var entry = new MenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = Glyph.Of(command) },
            Tag = command
        };

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

    /// <summary>Segoe Fluent codepoints; see the note in <see cref="CardItem"/> on why numbers.</summary>
    private static class Glyph
    {
        /// <summary>
        /// 菜单上这一条前面画什么。方向相反的两条（已观看／未观看、收藏／取消收藏）用同一族的两个字形。
        /// </summary>
        public static string Of(ItemCommand command) => Text(command switch
        {
            ItemCommand.Play => 0xE768,
            ItemCommand.Details => 0xE946,
            ItemCommand.Open => 0xE8B7,
            ItemCommand.MarkPlayed => 0xE73E,
            ItemCommand.MarkUnplayed => 0xE738,
            ItemCommand.Favorite => 0xE734,
            ItemCommand.Unfavorite => 0xE8D9,
            ItemCommand.HideFromResume => 0xE894,
            ItemCommand.AddToCollection => 0xE8F4,
            ItemCommand.Download => 0xE896,
            ItemCommand.EditMetadata => 0xE70F,
            ItemCommand.ChangeCover => 0xE91B,
            ItemCommand.Subtitles => 0xED1E,
            ItemCommand.Scrape => 0xE774,
            ItemCommand.RefreshMetadata => 0xE72C,
            ItemCommand.ScanLibrary => 0xE895,
            ItemCommand.OpenSeries => 0xE7F4,
            ItemCommand.Delete => 0xE74D,

            // 漏登记一条不该在屏上变成一个空框：退到「更多」那三个点。
            _ => 0xE712
        });

        private static string Text(int codepoint) => char.ConvertFromUtf32(codepoint);
    }
}
