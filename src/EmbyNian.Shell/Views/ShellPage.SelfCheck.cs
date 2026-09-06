using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 自检看外壳的那一面。分出来一份而不是塞进 <c>ShellPage.xaml.cs</c>：这些成员只有自检调用，路由那边一个也
/// 用不到，混在一起读的人分不清哪些是外壳真正的行为。
/// </summary>
public sealed partial class ShellPage
{
    /// <summary>标题栏那一排按键的样子，逻辑像素，量的是整排。</summary>
    /// <param name="Ok">四颗都摆对了，排成一行，整排也在标题栏里。</param>
    internal sealed record TitleActionProbe(bool Ok, string Detail, double X, double Y, double Width, double Height);

    /// <summary>
    /// 自检：主页首屏那两栏。**2026-09-06 从「收起和展开各量一次」变成量一次** —— 侧边栏删掉之后页宽只有一档，
    /// 而从前那两档存在的全部理由就是「不管收起还是展开，都要看到完整的继续观看」。跟着走的还有那两句跨层对账
    /// （标记里的 <c>CompactPaneLength</c> 对 Core 那个 <c>SideRail</c> 常数）：两边现在都没有这个数了。
    /// <para>
    /// 剩下的判据一条没松：图铺满大图那一块、上下不留底色，右栏贴着它、一样高、至少露出一张整卡，横排接在它
    /// 下沿之后（<see cref="HomePage.FoldRead"/>）。顺带报一句「页宽就是客户区宽」—— 这句话从前是假的（要减掉
    /// 侧边栏那 49），现在是真的，而它变假就说明谁又在页面左边塞了一列。
    /// </para>
    /// </summary>
    internal (bool? Ok, string Detail) ProbeHomeFold()
    {
        if (Pages.Content is not HomePage home) return (false, "当前不是主页");
        if (_window is null) return (false, "量不到主窗口的形状");
        if (!_window.BrowseFoldMeasurable)
            return (null, "窗口比 16:9 还扁（或正在放片子／全屏／最大化），首屏边界这一读只报不判");

        // HomeBanner 的 SizeChanged 会在这一轮布局里改高度，再走一轮让货架拿到最终坐标。
        UpdateLayout();
        home.UpdateLayout();
        UpdateLayout();

        var fold = home.FoldRead();
        var width = home.ActualWidth;
        var client = XamlRoot?.Size.Width ?? 0;
        var detail = $"页宽 {width:0}：{fold.Detail}";

        if (fold.Ok is null) return (null, detail);

        var fullWidth = client > 0 && Math.Abs(width - client) <= 2;

        return (fold.Ok == true && fullWidth,
            detail + $"；客户区宽 {client:0}"
                + (fullWidth ? "，页面占满整宽（左边没有第二列）" : "，和页宽对不上（左边多了一列？）"));
    }

    /// <summary>
    /// 把这一排按键的几种状态各摆一遍，量出来对一遍：设置、搜索两颗任何时候都按得动；两头都走不动时两支箭头
    /// 都在、都是暗的；能退不能进时只有返回亮；两样都能走又有两层路径时，两支箭头加面包屑那一行。顺带量一句
    /// 标签栏那一行在不在、账号那颗按钮贴不贴着右端。
    /// <para>
    /// 之所以要一个探针而不是直接看现场：后两颗由 <c>Frame.CanGoBack</c>、<c>Frame.CanGoForward</c> 和路径深度
    /// 决定，而前两件是框架自己算的，自检没法让它说某句话。<see cref="ApplyChrome"/> 把三件事变成参数，于是每
    /// 种状态都能摆出来 —— 收尾时把路径原样放回去，再按现场重新决定一次。
    /// </para>
    /// <para>
    /// 量回来的矩形还有第二个用处：<c>ShellSelfCheck</c> 拿它和窗口留的那个洞对一遍。洞开错了地方在屏幕上
    /// 完全看不出来，直到有人去点按键，结果把窗口拖走。**第 1 行不需要洞**（窗口只把最上面 32 像素算成标题栏），
    /// 所以这个探针只管第 0 行那一排；标签栏点不点得动由「顶部标签栏」那一关问。
    /// </para>
    /// </summary>
    internal TitleActionProbe ProbeTitleActions()
    {
        var saved = _trail.ToArray();

        try
        {
            Windows.Foundation.Point At(FrameworkElement element) =>
                element.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));

            // 顺序就是屏幕上从左到右的顺序：设置、搜索、后退、前进（需求 1，折叠那一颗随侧边栏一起删了）。
            Button[] keys = [SettingsButton, SearchButton, BackButton, ForwardButton];

            // 前两颗跟去过哪儿无关，所以三种状态下都得亮着 —— 一颗跟着导航变暗的设置按键，是按不出设置窗口。
            bool AlwaysOn() => SettingsButton.IsEnabled && SearchButton.IsEnabled;

            // 哪儿也走不动：四颗照旧站着，只是后两颗按不动 —— 会消失的按键会挪动旁边那些，而这一排的位置就是
            // 窗口那个洞的位置，挪一下洞就开歪了。面包屑那一行此时整行收起，不留一条空带子。
            ApplyChrome(false, false, 1);
            UpdateLayout();
            var idle = Chrome.Visibility == Visibility.Visible
                && AppTitleBar.Visibility == Visibility.Visible
                && keys.All(key => key.Visibility == Visibility.Visible)
                && AlwaysOn()
                && !BackButton.IsEnabled
                && !ForwardButton.IsEnabled
                && TrailBar.Visibility == Visibility.Collapsed;

            // 退得动、进不动、只有一层：只有返回亮着，面包屑那行收着 —— 一条只有一格的面包屑说的就是页面
            // 标题自己那句话。
            ApplyChrome(true, false, 1);
            UpdateLayout();
            var shallow = AlwaysOn()
                && BackButton.IsEnabled
                && !ForwardButton.IsEnabled
                && TrailBar.Visibility == Visibility.Collapsed;

            // 钻进去一层又退回来过：两支箭头都亮，面包屑那行也在。
            _trail.Add(new Crumb("自检", string.Empty));
            _trail.Add(new Crumb("自检 · 下一层", string.Empty));
            ApplyChrome(true, true, _trail.Count);
            UpdateLayout();
            var deep = AlwaysOn()
                && BackButton.IsEnabled
                && ForwardButton.IsEnabled
                && TrailBar.Visibility == Visibility.Visible;

            // 位置：整排在标题栏那 32 像素里，左端留白在它左边，页面在它下面。
            var group = At(TitleActions);
            var bar = At(AppTitleBar);
            var places = keys.Select(At).ToArray();

            var sized = TitleActions.ActualWidth > 0 && TitleActions.ActualHeight > 0
                && keys.All(key => key.ActualWidth > 0 && key.ActualHeight > 0);

            // 在标题栏里：上下不越出那 32 像素 —— 托盘 26 高，上下各留 3 像素（取等号也算通过，这里只问「没
            // 越出去」）。左边那段留白留着拖窗口，右边离系统那三个按钮还远。
            var inBar = group.Y >= bar.Y
                && group.Y + TitleActions.ActualHeight <= bar.Y + AppTitleBar.Height
                && group.X > bar.X
                && group.X + TitleActions.ActualWidth <= bar.X + AppTitleBar.ActualWidth;

            // 一排：从左到右按顺序排开，彼此挨着，同一高度 —— Claude app 那一行就是这么排的。
            var lined = true;
            var widest = 0.0;
            for (var index = 1; index < places.Length; index++)
            {
                var gap = places[index].X - (places[index - 1].X + keys[index - 1].ActualWidth);
                widest = Math.Max(widest, gap);
                lined &= gap >= 0 && gap <= 8 && Math.Abs(places[index].Y - places[0].Y) < 1;
            }

            var ok = idle && shallow && deep && sized && inBar && lined;

            return new TitleActionProbe(ok,
                $"四颗按键各 {SettingsButton.ActualWidth:0}×{SettingsButton.ActualHeight:0}，整排从 ({group.X:0},{group.Y:0}) 起"
                    + $"，占 {TitleActions.ActualWidth:0}×{TitleActions.ActualHeight:0}，最大间距 {widest:0}；"
                    + $"标题栏 {AppTitleBar.ActualWidth:0}×{AppTitleBar.Height:0}，{(inBar ? "整排在里面" : "整排没落在标题栏里")}；"
                    + $"{(lined ? "依次排成一行" : "没排成一行")}；"
                    + $"哪儿都走不动时{(idle ? "四颗都在、前两颗亮、两支箭头暗、面包屑整行收起" : "不对")}、"
                    + $"只能退时{(shallow ? "只有返回亮" : "不对")}、"
                    + $"两头都能走时{(deep ? "两支亮加面包屑" : "不对")}",
                group.X, group.Y, TitleActions.ActualWidth, TitleActions.ActualHeight);
        }
        finally
        {
            _trail.Clear();
            foreach (var crumb in saved) _trail.Add(crumb);

            // 现场怎样就怎样：探针摆过的状态到这里全部作废。
            SyncChrome();
            UpdateLayout();
        }
    }

    /// <summary>
    /// 自检：顶部标签栏（2026-09-06 新增，「删掉侧边栏」那一批）。判五件事，每一件的坏法在截图里都看不出来：
    /// <list type="number">
    /// <item>第 1 行在屏上，而且整条外壳正好 80 高（<c>ChromeHeight</c>）—— 页面顶上让开的那一段就是这个数，
    /// 少让就是页头被切半行，多让就是图上一条底色。</item>
    /// <item>格数 = 1 + 媒体库个数，而且第一格是 主页、每一格都带着自己的 tag —— 少接一个 tag 的症状是「点了
    /// 没反应」，而屏上那一格看着一切正常。</item>
    /// <item>标签栏整条落在标题栏那 32 像素**以下**：那条线以上是窗口的拖动区，画在那儿的标签一按就是拖窗口
    /// （这个项目的老账，见 <c>SetTitleBarHole</c>）。</item>
    /// <item>账号那颗按钮贴着右上角，而且没盖住系统那三颗窗口按钮（它在下一行，所以只要量它的上沿）。</item>
    /// <item>左边没有第二列：第一格的字和页面左边距对齐在 28 上下。</item>
    /// </list>
    /// </summary>
    internal (bool Ok, string Detail) ProbeTabs()
    {
        var count = LibraryTabs.Items.Count;
        var first = count > 0 ? LibraryTabs.Items[0] : null;
        var tagged = LibraryTabs.Items.Count(item => item.Tag is string { Length: > 0 });

        var shown = NavBar.Visibility == Visibility.Visible;
        var expected = 1 + _libraries.Count;
        var counted = count == expected && tagged == count && (first?.Tag as string) == "home";

        var navAt = At(NavBar);
        var barBottom = At(AppTitleBar).Y + AppTitleBar.Height;
        var below = navAt.Y >= barBottom - 0.5;
        var chrome = navAt.Y + NavBar.Height;
        var stacked = Math.Abs(chrome - HomePage.ChromeHeight) <= 0.5;

        var tabsAt = At(LibraryTabs);
        var aligned = Math.Abs(tabsAt.X - 12) <= 1.5;

        var accountAt = At(AccountButton);
        var right = accountAt.X + AccountButton.ActualWidth;
        var corner = AccountButton.ActualWidth > 0
            && right <= ActualWidth + 0.5
            && ActualWidth - right <= 32
            && accountAt.Y >= barBottom - 0.5;

        var ok = shown && counted && below && stacked && aligned && corner;

        return (ok,
            $"标签 {count} 格（主页 + {_libraries.Count} 个媒体库{(counted ? "" : "，和媒体库个数对不上")}），"
                + $"{tagged} 格带 tag，第一格 tag「{first?.Tag ?? "无"}」；"
                + $"{(shown ? "这一行在屏上" : "这一行收着了")}；"
                + $"从 y={navAt.Y:0} 起、高 {NavBar.Height:0}，外壳共 {chrome:0} 高"
                + $"（该 {HomePage.ChromeHeight:0}{(stacked ? "" : "，对不上")}）；"
                + $"{(below ? $"整条在标题栏 {barBottom:0} 以下" : "压进了标题栏的拖动区")}；"
                + $"第一格从 x={tabsAt.X:0} 起{(aligned ? "（和页面左边距对齐）" : "（没和页面左边距对齐）")}；"
                + $"账号 {AccountButton.ActualWidth:0}×{AccountButton.ActualHeight:0} 右沿 {right:0}／窗口宽 {ActualWidth:0}"
                + $"，{(corner ? "贴着右上角" : "不在右上角")}");

        Windows.Foundation.Point At(FrameworkElement element) =>
            element.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
    }
}
