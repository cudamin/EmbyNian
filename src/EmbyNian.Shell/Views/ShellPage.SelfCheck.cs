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
    /// <param name="Ok">五颗都摆对了，排成一行，整排也在标题栏里。</param>
    internal sealed record TitleActionProbe(bool Ok, string Detail, double X, double Y, double Width, double Height);

    /// <summary>
    /// 自检：侧边栏收起和展开时各量一次主页首屏。两档必须都完整放下继续观看，并且都不露出下一排媒体库。
    /// </summary>
    internal (bool? Ok, string Detail) ProbeHomeFold()
    {
        if (Pages.Content is not HomePage home) return (false, "当前不是主页");
        if (_window is null) return (false, "量不到主窗口的比例锁定状态");
        if (!_window.BrowseFoldActive)
            return (null, "窗口比例锁定当前未接管浏览窗口，跳过首屏边界读数");

        var paneWasOpen = Navigation.IsPaneOpen;

        try
        {
            (bool? Ok, string Detail, double Width, bool Open) Read(bool open)
            {
                Navigation.IsPaneOpen = open;
                SyncPane();

                // HomeBanner 的 SizeChanged 会在这一轮布局里改高度，再走一轮让货架拿到最终坐标。
                Navigation.UpdateLayout();
                UpdateLayout();
                home.UpdateLayout();
                UpdateLayout();
                var fold = home.FoldRead();
                return (fold.Ok, fold.Detail, home.ActualWidth, Navigation.IsPaneOpen);
            }

            var folded = Read(false);
            var spread = Read(true);
            var detail = $"收起（页宽 {folded.Width:0}）：{folded.Detail}；"
                + $"展开（页宽 {spread.Width:0}）：{spread.Detail}";
            if (folded.Ok is null || spread.Ok is null) return (null, detail);

            var stateOk = !folded.Open && spread.Open;
            var expected = Navigation.OpenPaneLength - Navigation.CompactPaneLength;
            var difference = folded.Width - spread.Width;
            var widthOk = folded.Width > 0 && spread.Width > 0 && Math.Abs(difference - expected) <= 2;
            return (folded.Ok == true && spread.Ok == true && stateOk && widthOk,
                detail + $"；两档宽差 {difference:0}（应约 {expected:0}）");
        }
        finally
        {
            Navigation.IsPaneOpen = paneWasOpen;
            SyncPane();
            UpdateLayout();
        }
    }

    /// <summary>
    /// 把这一排按键的几种状态各摆一遍，量出来对一遍：折叠侧边栏、设置、搜索三颗任何时候都按得动；两头都走不
    /// 动时两支箭头都在、都是暗的；能退不能进时只有返回亮；两样都能走又有两层路径时，两支箭头加面包屑那一行。
    /// 侧边栏收放两档也各摆一遍，量整排让开了没有；顺带问一句现场：进来的时候侧边栏本来就该是收着的。
    /// <para>
    /// 之所以要一个探针而不是直接看现场：后两颗由 <c>Frame.CanGoBack</c>、<c>Frame.CanGoForward</c> 和路径深度
    /// 决定，而前两件是框架自己算的，自检没法让它说某句话。<see cref="ApplyChrome"/> 把三件事变成参数，于是每
    /// 种状态都能摆出来 —— 收尾时把路径原样放回去，再按现场重新决定一次。
    /// </para>
    /// <para>
    /// 量回来的矩形还有第二个用处：<c>ShellSelfCheck</c> 拿它和窗口留的那个洞对一遍。洞开错了地方在屏幕上
    /// 完全看不出来，直到有人去点按键，结果把窗口拖走。
    /// </para>
    /// </summary>
    internal TitleActionProbe ProbeTitleActions()
    {
        var saved = _trail.ToArray();
        var paneWasOpen = Navigation.IsPaneOpen;

        try
        {
            Windows.Foundation.Point At(FrameworkElement element) =>
                element.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));

            // 顺序就是屏幕上从左到右的顺序：折叠、设置、搜索、后退、前进（需求 1）。
            Button[] keys = [PaneButton, SettingsButton, SearchButton, BackButton, ForwardButton];

            // 前三颗跟去过哪儿无关，所以三种状态下都得亮着 —— 一颗跟着导航变暗的设置按键，是按不出设置窗口。
            bool AlwaysOn() => PaneButton.IsEnabled && SettingsButton.IsEnabled && SearchButton.IsEnabled;

            // 哪儿也走不动：五颗照旧站着，只是后两颗按不动 —— 会消失的按键会挪动旁边那些，而这一排的位置就是
            // 窗口那个洞的位置，挪一下洞就开歪了。面包屑那一行此时整行收起，不留一条空带子。
            ApplyChrome(false, false, 1);
            UpdateLayout();
            var idle = AppTitleBar.Visibility == Visibility.Visible
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

            // 侧边栏两档（「折叠状态下图标要向右移动一些，防止图标和侧边栏重合」）：收着的时候整排要整个落在
            // 窄条和那道竖线的右边，张开的时候回到原来的缩进。压线在屏幕上只是第一颗托盘的半边颜色不一样 ——
            // 一句量出来的话才红得起来。
            // 摆完自己算一遍 SyncPane：PaneClosed/PaneOpened 是框架收放完才发的，等它到就量到旧位置了。
            Navigation.IsPaneOpen = false;
            SyncPane();
            UpdateLayout();
            var folded = At(TitleActions).X;

            Navigation.IsPaneOpen = true;
            SyncPane();
            UpdateLayout();
            var spread = At(TitleActions).X;

            // 线在窄条的右沿上，占 1 像素，所以「让开了」是整排的左沿不小于 48+1。
            var edge = Navigation.CompactPaneLength + 1;
            var cleared = folded >= edge && spread < folded;

            // 还要问一句现场：进这个探针的时候侧边栏本来就该是收着的（「侧边栏默认为折叠状态」）。这句问的是
            // 起手那一档撑住了没有 —— 自检从开窗一路走到这儿谁也没碰过侧边栏，所以这里张着就只有一个原因：
            // 框架把标记里那个 IsPaneOpen="False" 推回去了，构造函数里那个一次性 Loaded 没按住它。
            var startedFolded = !paneWasOpen;

            // 回到现场那一档再量位置：下面报出去的矩形要跟窗口那个洞对得上，而洞是照现场挖的。
            Navigation.IsPaneOpen = paneWasOpen;
            SyncPane();
            UpdateLayout();

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

            var ok = idle && shallow && deep && sized && inBar && lined && cleared && startedFolded;

            return new TitleActionProbe(ok,
                $"五颗按键各 {PaneButton.ActualWidth:0}×{PaneButton.ActualHeight:0}，整排从 ({group.X:0},{group.Y:0}) 起"
                    + $"，占 {TitleActions.ActualWidth:0}×{TitleActions.ActualHeight:0}，最大间距 {widest:0}；"
                    + $"标题栏 {AppTitleBar.ActualWidth:0}×{AppTitleBar.Height:0}，{(inBar ? "整排在里面" : "整排没落在标题栏里")}；"
                    + $"{(lined ? "依次排成一行" : "没排成一行")}；"
                    + $"{(startedFolded ? "起手侧边栏是收着的" : "起手侧边栏却是张开的")}，"
                    + $"收着时整排从 {folded:0} 起、张开时从 {spread:0} 起（窄条右沿 {edge:0}）"
                    + $"，{(cleared ? "收着时让开了侧边栏" : "收着时压在侧边栏上")}；"
                    + $"哪儿都走不动时{(idle ? "五颗都在、前三颗亮、两支箭头暗、面包屑整行收起" : "不对")}、"
                    + $"只能退时{(shallow ? "只有返回亮" : "不对")}、"
                    + $"两头都能走时{(deep ? "两支亮加面包屑" : "不对")}",
                group.X, group.Y, TitleActions.ActualWidth, TitleActions.ActualHeight);
        }
        finally
        {
            _trail.Clear();
            foreach (var crumb in saved) _trail.Add(crumb);

            // 现场怎样就怎样：探针摆过的状态到这里全部作废。侧边栏那一档在上面已经放回去了，这里再放一次是
            // 兜底 —— 中间抛出去的话，界面不能留在探针摆的那一档上。
            Navigation.IsPaneOpen = paneWasOpen;
            SyncPane();
            SyncChrome();
            UpdateLayout();
        }
    }
}
