using EmbyNian.Diagnostics;
using EmbyNian.Mpv;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 六个浮出菜单，一个一个打开：右键那份画面菜单（<c>ProbePictureMenu</c>，逐行对着 Core 那份目录数）、控制条上
/// 那五个下拉（<c>ProbeControlMenus</c>）、还有 需求 7 的字幕字体那个能搜的框（<c>ProbeSubtitleFont</c>）。
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
    /// </summary>
    internal (bool Ok, string Detail) ProbePictureMenu()
    {
        OnPictureMenuOpening(this, new object());

        var (rows, groups, rules) = CountMenu(PictureMenu.Items);

        var catalogue = PlayerMenuCatalog.Flatten(PlayerMenuCatalog.Root).ToList();
        var wantRows = catalogue.Count(node => node.Kind is not (PlayerMenuKind.Group or PlayerMenuKind.Separator));
        var wantGroups = catalogue.Count(node => node.Kind == PlayerMenuKind.Group);
        var wantRules = catalogue.Count(node => node.Kind == PlayerMenuKind.Separator);

        // 裁切填充 by name, because it is the row the whole menu was missing for: panscan has no button
        // anywhere else in the chrome, so if this one row failed to build the feature would be unreachable
        // again with every count still adding up.
        var panscan = catalogue.Any(node => node.Label.Contains("裁切填充", StringComparison.Ordinal));

        var ok = rows == wantRows && groups == wantGroups && rules == wantRules && panscan;

        return (ok, $"{rows}/{wantRows} 行、{groups}/{wantGroups} 个子菜单、{rules}/{wantRules} 条分隔线"
                    + $"，裁切填充={(panscan ? "在" : "缺")}");
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
    /// the two track pickers have no tracks, 倍速 is built from a static list and should be full anyway,
    /// and 更多 reads settings rather than the file and should also be full. An empty picker that builds a
    /// 「没有可选的…」 row is a pass; one that builds nothing at all is not.
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
        // placeholder and 字幕's 关闭字幕. 倍速 owes one per SpeedChoices entry, 更多 owes its seven rows.
        Open("单集", EpisodeMenu, () => OnEpisodeMenuOpening(this, new object()), 1);
        Open("音轨", AudioMenu, () => OnAudioMenuOpening(this, new object()), 1);
        Open("字幕", SubtitleMenu, () => OnSubtitleMenuOpening(this, new object()), 1);
        Open("倍速", SpeedMenu, () => OnSpeedMenuOpening(this, new object()), PlayerViewModel.SpeedChoices.Length);
        Open("更多", MoreMenu, () => OnMoreMenuOpening(this, new object()), 6);

        return (failures == 0, string.Join("、", built));
    }

    /// <summary>
    /// 需求 7：the 字幕字体 box in the title strip — where it sits, whether the machine's families reached it,
    /// what a typed line resolves to, and the three things it does to the rest of the player.
    /// <para>
    /// Every one of them is invisible to a build and to a unit test, and every one of them is a box that
    /// looks perfectly fine until it is used. A control left out of the strip's own hit list is one whose
    /// press starts a window drag instead of placing a caret — the window then follows the hand. A hold
    /// that the reveal rule can drop is a strip that slides away from under what is being typed into it.
    /// A player that still owns single letters is one where spelling 「Consolas」 mutes the film and skips
    /// to the next episode. And <c>Resolve</c> is what stops Enter picking the family already in use: the
    /// current one is pinned into the list whatever is typed, so 「the first row」 is the wrong answer.
    /// </para>
    /// <para>
    /// Nothing here writes. The value is read and compared, never set: the row commits only when a family
    /// is picked, and no family is picked — same rule as <see cref="SettingFontRow.Measure"/>, and for the
    /// same reason, which is that a self-check has no business changing what the user watches films with.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeSubtitleFont()
    {
        if (!Attached) return (false, "播放层未接线");

        var was = Visibility;
        var text = FontBox.Text;

        Visibility = Visibility.Visible;
        UpdateLayout();

        // Two passes, as in ProbeWindowCommands: the first sizes the page, the second sizes the strip that
        // Render has just revealed. Without the second the box measures zero and every question below is
        // answered about nothing.
        var clock = Now;
        _chrome.WakeFully(clock);
        Render();
        UpdateLayout();

        ViewModel.PrepareFonts();

        var trouble = new List<string>();
        var strip = BoundsOf(TitleStrip);
        var box = BoundsOf(FontBox);

        if (box.Width <= 0 || box.Height <= 0) trouble.Add("输入栏没有尺寸");
        else if (!Encloses(strip, box)) trouble.Add("输入栏越出了标题栏");

        foreach (var (name, other) in new (string Name, FrameworkElement Element)[]
                 {
                     ("统计", StatsButton),
                     ("置顶", PinButton),
                     ("窗口命令", WindowButtons)
                 })
        {
            if (Overlaps(box, BoundsOf(other))) trouble.Add($"输入栏压住了{name}");
        }

        // 右上角, structurally: right of the title, left of the three window commands. Both hold by the
        // grid's columns today, which is the point — a box moved into the wrong column still lays out.
        if (box.Left < BoundsOf(TitleText).Left) trouble.Add("输入栏跑到标题左边去了");
        if (box.Right > BoundsOf(WindowButtons).Left + GeometrySlack) trouble.Add("输入栏挤进了窗口命令那一角");

        // The strip's own hit list, asked at the middle of the box. A press there must not begin a drag.
        var centre = new Point(box.Left + (box.Width / 2), box.Top + (box.Height / 2));
        if (!OnStripControl(centre)) trouble.Add("按在输入栏上会开始拖窗口");

        var value = ViewModel.SubtitleFont.Value;
        if (!string.Equals(value, ViewModel.SubtitleFontSetting, StringComparison.Ordinal))
            trouble.Add($"输入栏显示的「{value}」不是设置里的「{ViewModel.SubtitleFontSetting}」");

        // The search, driven through the box's own path rather than the row's, and the row's own probe is
        // what covers the filter itself. A word out of the current family's name — 「YaHei」 for Microsoft
        // YaHei — because it is the one query that must match on every machine.
        SearchFonts(string.Empty);
        var all = ViewModel.SubtitleFont.Matches.Count;
        var total = ViewModel.SubtitleFont.Total;

        var word = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? value;
        SearchFonts(word);
        var found = ViewModel.SubtitleFont.Matches.Count;
        var byWord = ViewModel.SubtitleFont.Resolve(word);
        var byName = ViewModel.SubtitleFont.Resolve(value);

        const string nowhere = "没有任何字体会叫这个名字";
        SearchFonts(nowhere);
        var pinned = ViewModel.SubtitleFont.Matches.Count;
        var byNothing = ViewModel.SubtitleFont.Resolve(nowhere);

        SearchFonts(string.Empty);

        if (!ViewModel.FontsReady) trouble.Add("字体还没扫完");
        if (total <= 1) trouble.Add($"只有 {total} 个字体族");
        if (all != total) trouble.Add($"空搜索只列出 {all} / {total} 个");
        if (found == 0) trouble.Add($"搜「{word}」什么都没有");
        if (byWord is null) trouble.Add($"搜「{word}」按回车会落空");
        if (byName is null || !byName.AnswersTo(value))
            trouble.Add($"整名字「{value}」按回车选到了「{byName?.Name ?? "空"}」");

        // The one that matters most: the current family is in that list of one, and Enter must still refuse
        // it — a query that matched nothing means nothing, not 「keep what you had by picking it again」.
        if (byNothing is not null) trouble.Add($"搜不存在的名字按回车会选到「{byNothing.Name}」");

        // What the box does to the chrome and to the keyboard, including the collision a single shared hold
        // flag used to have: a flyout opened and closed over a box that still has the keyboard.
        BeginFontSearch();
        var typing = _typing;
        var pinnedChrome = _chrome.HoldChrome;

        Hold(true, ChromeHold.Menu);
        Hold(false, ChromeHold.Menu);
        var survived = _chrome.HoldChrome;

        _chrome.Tick(clock + SettleMilliseconds);
        Render();
        var stillUp = TitleStrip.Visibility == Visibility.Visible;

        EndFontSearch();
        var released = !_typing && !_chrome.HoldChrome;

        if (!typing) trouble.Add("拿到键盘后播放键位没有让开");
        if (!pinnedChrome) trouble.Add("拿到键盘后标题栏没有被钉住");
        if (!survived) trouble.Add("菜单开关一次就把输入栏的钉子拔了");
        if (!stillUp) trouble.Add("输入中标题栏还是自己收起来了");
        if (!released) trouble.Add("离开输入栏后钉子没有拔掉");

        // Left the way a player that is not running should be, same as every other probe here.
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        Render();
        SetCursorHidden(false);
        FontBox.Text = text;
        Visibility = was;
        UpdateLayout();

        return (trouble.Count == 0,
            $"输入栏 {box.Width:0}×{box.Height:0} 逻辑像素，在标题栏右上角；当前「{value}」，共 {total} 个字体族，"
            + $"搜「{word}」得 {found} 个，搜不存在的名字剩 {pinned} 个（只有当前值）；"
            + (trouble.Count == 0
                ? "回车能认出整名字、认不出的不乱选，输入时钉住标题栏且不吃播放键位"
                : string.Join('、', trouble)));
    }
}
