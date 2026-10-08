using System.Globalization;
using Momoka.Diagnostics;
using Momoka.Mpv;
using Momoka.Playback;
using Momoka.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Momoka.Shell.Views;

/// <summary>
/// 浮出菜单，一个一个打开：右键那份画面菜单（<c>ProbePictureMenu</c>，逐行对着 Core 那份目录数）、控制条上
/// 那六个下拉（<c>ProbeControlMenus</c>）。
/// <para>
/// 它们没有一个是在标记里声明好的 —— 每一个都是自己的 <c>Opening</c> 那一刻才把内容填出来，所以「打开会不会
/// 崩、开出来是不是空的」这两件事只有真按一次才知道。拆成几个文件的缘由见 <c>PlayerPage.SelfCheck.cs</c> 的
/// 类注释。
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>
    /// Builds the right-click 画面菜单 the way the first right click would, and checks every row of the
    /// catalogue survived the trip. Counted against <see cref="PlayerMenuCatalog.Flatten"/> rather than
    /// against a number written here, so a row added to the catalogue is covered the day it is added.
    /// <para>
    /// 2026-09-27 晚右下角那颗「更多」按钮撤下后，它的几行（着色器/跳过/连播/播放信息）也拼进这只菜单
    /// （见 <see cref="OnPictureMenuOpening"/>），于是屏上这只菜单比目录多出来的正是那一棵。期望值因此按
    /// 「目录自己算出来的 ＋ 同一只 <c>OnMoreMenuOpening</c> 现拼一棵的计数」来比 —— 两侧都由代码量出来，
    /// 不写死数字：目录里加一行仍当天被覆盖，而「更多」那一棵自身的那条自检在
    /// <see cref="ProbeControlMenus"/> 里（它把这一棵现拼一只临时浮层、问「至少六行」），
    /// 这里只保证它确实落进了右键菜单。
    /// </para>
    /// <para>
    /// 2026-10-01 起还多读一条：末尾那三行设置入口（字幕／视频输出／音频输出）逐行对账 —— 在不在菜单里、
    /// 带的设置落点是不是一张真卡。行数由上面两侧的计数管着，那一条管的是「点下去开哪张卡」。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbePictureMenu()
    {
        OnPictureMenuOpening(this, new object());

        var (rows, groups, rules) = CountMenu(PictureMenu.Items);

        var catalogue = PlayerMenuCatalog.Flatten(PlayerMenuCatalog.Root).ToList();
        var wantRows = catalogue.Count(node => node.Kind is not (PlayerMenuKind.Group or PlayerMenuKind.Separator));
        var wantGroups = catalogue.Count(node => node.Kind == PlayerMenuKind.Group);
        var wantRules = catalogue.Count(node => node.Kind == PlayerMenuKind.Separator);

        // 并进来的「更多」那一棵：现拼一次量它的行数，再把它那条分隔线一起算上（OnPictureMenuOpening
        // 在目录与这一棵之间插了一条 MenuFlyoutSeparator）。
        var more = new MenuFlyout();
        OnMoreMenuOpening(more, Root);
        var (moreRows, moreGroups, moreRules) = CountMenu(more.Items);
        wantRows += moreRows;
        wantGroups += moreGroups;
        wantRules += moreRules + 1;

        // 裁切填充 by name, because it is the row the whole menu was missing for: panscan has no button
        // anywhere else in the chrome, so if this one row failed to build the feature would be unreachable
        // again with every count still adding up.
        var panscan = catalogue.Any(node => node.Label.Contains("裁切填充", StringComparison.Ordinal));

        // 打勾: every row the catalogue marks checkable（解码方式, 声道布局, 抖动补偿…）must have built as a
        // radio/toggle and been registered for refresh. Counted against the catalogue, not a number here, so a
        // newly checkable row is covered the day it is added. Reads mpv when the menu opened above; with no film
        // the reads came back null and nothing is ticked — a pass, since what is proved here is the wiring.
        // 「更多」那一棵里没有可打勾的行，所以期望值仍只由目录给出。
        var wantChecks = catalogue.Count(node => node.State is not null);
        var checks = _pictureChecks.Count;

        // 末尾那三行「去设置里改」（2026-10-01 用户令）。行数已经由上面两侧的计数管着，这里管的是**它们点得出
        // 什么**：每一行都得在屏上，而且带的设置落点必须是一张真的设置卡。少了名字、或者 Cards 里改了名而
        // PlayerSettingsLinks 没跟上，症状都是「点『字幕』开在播放器卡上」—— 菜单看着一切正常，只有这条读数看得见。
        var settings = ProbeSettingsRows(PictureMenu.Items);

        var ok = rows == wantRows && groups == wantGroups && rules == wantRules && panscan
            && checks == wantChecks && settings.Ok;

        return (ok, $"{rows}/{wantRows} 行、{groups}/{wantGroups} 个子菜单、{rules}/{wantRules} 条分隔线"
                    + $"（含并进来的「更多」{moreRows} 行）"
                    + $"，裁切填充={(panscan ? "在" : "缺")}、可打勾 {checks}/{wantChecks} 行"
                    + $"，设置三行 {settings.Detail}");
    }

    /// <summary>
    /// 「播放设置」内的三行入口逐行对账：表里每一行都要在子菜单里找得到同名行、行上带着同一个设置落点，而
    /// 那个落点必须是一张真的设置卡（<see cref="SettingsViewModel.IsCardCategory"/>）。
    /// <para>
    /// 期望值从 <see cref="PlayerSettingsLinks.All"/> 现取，不写死三个名字 —— 表里加一行、改一行，这条当天跟上。
    /// 独占模式共用这张表与执行出口，但这里的控件检查只覆盖集成菜单。
    /// </para>
    /// </summary>
    private static (bool Ok, string Detail) ProbeSettingsRows(IEnumerable<MenuFlyoutItemBase> items)
    {
        var roots = items.ToList();
        var groups = roots.OfType<MenuFlyoutSubItem>()
            .Where(item => item.Text == PlayerSettingsLinks.MenuLabel).ToList();
        if (groups.Count != 1) return (false, $"「{PlayerSettingsLinks.MenuLabel}」子菜单应有 1 个，实际 {groups.Count} 个");

        var rows = groups[0].Items.OfType<MenuFlyoutItem>()
            .Where(item => item.Tag is PlayerSettingsLink)
            .ToList();

        var trouble = new List<string>();
        if (roots.OfType<MenuFlyoutItem>().Any(item => item.Tag is PlayerSettingsLink))
            trouble.Add("设置入口仍在根菜单平铺");
        if (!rows.Select(item => item.Text).SequenceEqual(PlayerSettingsLinks.All.Select(link => link.Label)))
            trouble.Add("播放设置子项的顺序与设置表不一致");

        foreach (var link in PlayerSettingsLinks.All)
        {
            var row = rows.FirstOrDefault(item => string.Equals(item.Text, link.Label, StringComparison.Ordinal));
            if (row is null)
            {
                trouble.Add($"「{link.Label}」不在菜单里");
                continue;
            }

            if (row.Tag is not PlayerSettingsLink tagged || !string.Equals(tagged.Category, link.Category, StringComparison.Ordinal))
                trouble.Add($"「{link.Label}」带的设置落点不是「{link.Category}」");

            if (!SettingsViewModel.IsCardCategory(link.Category))
                trouble.Add($"「{link.Label}」指向的设置卡「{link.Category}」不在卡片名单里");
        }

        if (rows.Count != PlayerSettingsLinks.All.Count)
            trouble.Add($"菜单里 {rows.Count} 行设置入口，表上 {PlayerSettingsLinks.All.Count} 行");

        return (trouble.Count == 0,
            trouble.Count == 0
                ? $"{rows.Count} 行都在「{PlayerSettingsLinks.MenuLabel}」内、都落在一张真卡上（{string.Join('、', PlayerSettingsLinks.All.Select(link => link.Label))}）"
                : string.Join('、', trouble));
    }

    /// <summary>
    /// 点一下「播放设置」中的一行：按真菜单行的真处理器走（<c>OnPictureSettingsRow</c>），
    /// 不是自检另起一句「等于点了它」—— 这条读数要连起来的是「菜单行 → view model → 外壳开窗」整根链子。
    /// 菜单行不在（没挂上、或表里没有这个名字）返回 false。
    /// <para>
    /// 调用方是外壳自检的「设置窗口盖在画面上」：它先让这扇窗立起来，再量屏幕上谁在最前面。
    /// </para>
    /// </summary>
    internal bool ClickPictureSettingsRow(string label)
    {
        OnPictureMenuOpening(this, new object());

        var settings = PictureMenu.Items.OfType<MenuFlyoutSubItem>()
            .FirstOrDefault(item => item.Text == PlayerSettingsLinks.MenuLabel);
        var row = settings?.Items.OfType<MenuFlyoutItem>().FirstOrDefault(item =>
            item.Tag is PlayerSettingsLink link && string.Equals(link.Label, label, StringComparison.Ordinal));

        if (row is null) return false;

        OnPictureSettingsRow(row, new RoutedEventArgs());
        return true;
    }

    /// <summary>Walks a built flyout the same way <see cref="BuildMenuItems"/> built it.</summary>
    private static (int Rows, int Groups, int Rules) CountMenu(IList<MenuFlyoutItemBase> items)
    {
        var rows = 0;
        var groups = 0;
        var rules = 0;

        foreach (var item in items)
        {
            switch (item)
            {
                case MenuFlyoutSeparator:
                    rules++;
                    break;

                case MenuFlyoutSubItem group:
                    groups++;
                    var inner = CountMenu(group.Items);
                    rows += inner.Rows;
                    groups += inner.Groups;
                    rules += inner.Rules;
                    break;

                default:
                    rows++;
                    break;
            }
        }

        return (rows, groups, rules);
    }

    /// <summary>
    /// Opens all five control-bar pickers the way a click on each would, and reports what they built.
    /// <para>
    /// Every one of them is filled by its <c>Opening</c> handler rather than declared in XAML, so until
    /// something opens them they are five empty <c>MenuFlyout</c>s that cannot fail. A missing resource
    /// key or a null dereference in any of the builders would first be seen by a user mid-film, which is
    /// the worst possible moment and the reason this probe exists.
    /// </para>
    /// <para>
    /// Nothing is playing, so what is being proved is the empty case of each: 单集 has no episode list,
    /// 版本 has no item to have versions of, the two track pickers have no tracks, and 更多 reads settings
    /// rather than the file and should also be full. An empty picker that builds a 「没有可选的…」 row is a
    /// pass; one that builds nothing at all is not. 倍速 used to be the sixth — 2026-09-25 起它是轮盘，
    /// 由 <see cref="ProbeSpeedWheel"/> 单独看着。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeControlMenus()
    {
        if (!Attached) return (false, "播放层未接线");

        var built = new List<string>(5);
        var failures = 0;

        void Open(string name, MenuFlyout menu, Action opening, int least)
        {
            try
            {
                opening();
                var (rows, groups, rules) = CountMenu(menu.Items);
                var total = rows + groups;
                if (total < least) failures++;
                built.Add($"{name} {rows} 行"
                          + (groups > 0 ? $"+{groups} 子菜单" : string.Empty)
                          + (rules > 0 ? $"+{rules} 分隔" : string.Empty));
            }
            catch (Exception error)
            {
                failures++;
                built.Add($"{name} 出错（{error.GetType().Name}）");
                Log.Warn(Category, $"自检打开「{name}」失败", error);
            }
        }

        // 单集 and the two track pickers each owe one row even with nothing loaded — the 「没有可选的单集」
        // placeholder and 字幕's 关闭字幕. 版本 owes the 「没有可切换的版本」 row
        // (nothing is playing, so there is no item to have versions of).
        // 「更多」那一棵 2026-09-27 晚不再有自己那颗按钮／浮层（拼进了右键画面菜单），这里仍把它整棵拼到
        // 一只临时浮层上，沿用「至少六行」的底线。
        Open("单集", EpisodeMenu, () => OnEpisodeMenuOpening(this, new object()), 1);
        Open("版本", VersionMenu, () => OnVersionMenuOpening(this, new object()), 1);
        Open("音轨", AudioMenu, () => OnAudioMenuOpening(this, new object()), 1);
        Open("字幕", SubtitleMenu, () => OnSubtitleMenuOpening(this, new object()), 1);

        var moreProbe = new MenuFlyout();
        Open("更多", moreProbe, () => OnMoreMenuOpening(moreProbe, Root), 6);

        return (failures == 0, string.Join("、", built));
    }

    /// <summary>
    /// 倍速轮盘（2026-09-25 用户令「改为竖置的滚动条滚轮，刻度居中」）开出来的那条刻度带。它不是菜单 ——
    /// 刻度在构造时就摆上了（<see cref="WireSpeedWheel"/>），所以这里不开浮层，只验摆好的那条带：
    /// <para>
    /// 根数与文案逐根对齐刻度表（<c>0.0#</c> 那套写法；表是键盘微调与轮盘共用那份，0.1～1 每 0.1、
    /// 1～20 每 1）；当前速度停在中线（整数位置上偏移必须为零）；
    /// 摆位方向是快在上、慢在下 —— 方向写反的轮盘（快在下）从这条读数上当场翻红。手势与交单的那一半在
    /// SpeedWheelTests（Core 算术）与页面分部里，探针碰不到真鼠标，与全页同一处境。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeSpeedWheel()
    {
        var choices = PlayerViewModel.SpeedChoices;
        BuildSpeedWheel();
        var ticks = _wheelTicks!;

        var trouble = new List<string>();

        if (ticks.Length != choices.Length)
        {
            trouble.Add($"刻度 {ticks.Length} 根，表上 {choices.Length} 档");
        }
        else
        {
            for (var index = 0; index < choices.Length; index++)
            {
                var want = WheelTickText(choices[index]);
                if (!string.Equals(ticks[index].Text, want, StringComparison.Ordinal))
                    trouble.Add($"第 {index + 1} 根是「{ticks[index].Text}」，表上是「{want}」");
            }
        }

        // 当前速度停在中线。表外速度（键盘微调给出的）合法地停在两根之间，那一档不判居中 —— 判了就是
        // 把「诚实插值」当成了错。
        var position = SpeedWheel.PositionFor(ViewModel.Status.Speed, choices);
        if (Math.Abs(position - Math.Round(position)) < 0.001)
        {
            var nearest = SpeedWheel.Snap(position, choices.Length);
            var offset = SpeedWheel.TickOffset(nearest, position);
            if (Math.Abs(offset) > 0.001) trouble.Add($"中线那根刻度偏了 {offset:0.##} 逻辑像素");
        }

        // 方向：最快那根必须排在最慢那根上面（y 更小）。摆位读数要先跑一遍 RenderWheel —— 刻度只在
        // 摆过之后才有 Canvas.Top 可读。
        if (ticks.Length >= 2)
        {
            RenderWheel();
            var topTick = Canvas.GetTop(ticks[^1]);
            var bottomTick = Canvas.GetTop(ticks[0]);
            if (double.IsNaN(topTick) || double.IsNaN(bottomTick)) trouble.Add("刻度还没有摆位读数");
            else if (topTick >= bottomTick)
                trouble.Add($"最快那根（y={topTick:0}）没有排在最慢那根（y={bottomTick:0}）上面");
        }

        return (trouble.Count == 0,
            trouble.Count == 0
                ? $"{ticks.Length} 根刻度，当前 {ViewModel.Status.Speed.ToString("0.0#", CultureInfo.InvariantCulture)}× 停在中线，快在上慢在下"
                : string.Join('、', trouble));
    }
}
