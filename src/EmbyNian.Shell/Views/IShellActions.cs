using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Playback;
using Microsoft.UI.Xaml.Controls;
namespace EmbyNian.Shell.Views;

/// <summary>
/// The small set of actions that a page or view model can ask of the browsing shell. Navigation payloads
/// deliberately do not carry this interface: they remain replayable data for <see cref="Frame.Navigate"/>.
/// </summary>
internal interface IShellActions
{
    void OpenItem(EmbyItem item);

    Task PlayAsync(
        EmbyItem item,
        EmbyItem? parent = null,
        PlaybackChoice? choice = null,
        IReadOnlyList<EmbyItem>? episodes = null);

    void Notify(string message, InfoBarSeverity severity = InfoBarSeverity.Informational);

    bool TryOpenLibrary(string id);

    /// <summary>
    /// 按类型浏览：详情页上那一行类型现在是点得动的，点一个类型就是一格「这个类型下的全部影片和剧集」。
    /// <para>
    /// 走外壳而不是页面自己开：跳到哪一页、面包屑怎么写、侧边栏的高亮归谁，这三件事只有外壳知道 —— 同
    /// <see cref="OpenItem"/>。类型是一句话而不是一个 id：Emby 的类型没有 id，查询发的就是这个名字
    /// （<c>ItemQuery.Genre</c>）。
    /// </para>
    /// </summary>
    void OpenGenre(string genre);

    /// <summary>
    /// 主页某一排点进去的那一页：这一排装的是什么，就列什么。
    /// <para>
    /// 2026-09-14 用户原话「新增点击图中红框的标题可以进入对应的页面」，图里圈的是「继续观看」。它不属于任何
    /// 媒体库，所以走不了 <see cref="TryOpenLibrary"/>（那要一个库 id）；给它一条自己的入口，外壳那一头照
    /// <see cref="OpenGenre"/> 的样子造一个 <c>LibraryRequest</c> 交给同一个 <c>LibraryPage</c> —— 排序、
    /// 筛选、视图形状、翻页、字母条全是现成的，另开一页就得把这些重写一遍。
    /// </para>
    /// <para>
    /// 收的是版面自己的那一档（<c>HomeLayout.HomeRowTarget</c>）而不是一个字符串：哪些排有去处这件事只在
    /// <c>HomeLayout</c> 里有一份说法（<c>TargetOf</c>），别处照抄一份就会有一天两边对不上。
    /// </para>
    /// </summary>
    void OpenRow(HomeLayout.HomeRowTarget target);

    void OpenSignIn(ServerProfile? server = null);

    Task SwitchProfileAsync(ServerProfile server, AccountProfile? account);

    /// <summary>
    /// 「这一页顶上那一块现在是什么」。主页那条大图铺到窗口顶边之后（见 <c>HomePage</c>），标题栏那一条是
    /// 透明的，底下就是剧照顶上那层暗罩 —— 浅色主题（晴昼）的墨是深色，画在那层暗罩上就是几颗看不见的按钮，
    /// 其中一颗是关闭。
    /// <para>
    /// 页面自己说而不是外壳自己猜：那一块是不是一张图要等服务器把「最近添加」送回来才知道，而外壳这边
    /// 没有比这一句更早的时机。收到之后怎么用是外壳的事（见 <c>ShellPage.PaintTitleInk</c>）：系统那三颗
    /// 永远在工作区那一列的右上角，我们那五颗只在侧边栏收起时才压到图上。
    /// </para>
    /// </summary>
    void SetTitleStrip(TitleStrip strip);

    /// <summary>
    /// 用户在设置里开没开 MoviePilot —— 决定「更多」菜单上给不给「在 MoviePilot 搜索其他版本」那一条。菜单由
    /// Core 的 <see cref="Emby.ItemMenu.For"/> 按条目排，这一位是它唯一够不着、得由外壳递进去的输入（见
    /// <see cref="ItemCommands.Build"/>）。
    /// </summary>
    bool MoviePilotEnabled { get; }

    /// <summary>
    /// 「在 MoviePilot 搜索其他版本」：新开一个窗口，按条目算好的关键字（见
    /// <see cref="EmbyNian.MoviePilot.MoviePilotVersionQuery"/>）去 MoviePilot 的站点搜索里列可下载的版本。
    /// <para>
    /// 走外壳而不是页面自己开：第二个窗口的生命周期、和设置窗口一样「关掉 X 不等于退出进程」的那套，只有外壳
    /// 管得了（见 <c>ShellPage.SearchMoviePilotVersions</c> / <c>MoviePilotWindow</c>）。
    /// </para>
    /// </summary>
    void SearchMoviePilotVersions(EmbyItem item);

    /// <summary>
    /// 「手动整理」：在 MoviePilot 上把条目背后的文件识别、改名、搬进媒体库目录。走外壳 ——
    /// <see cref="EmbyNian.MoviePilot.MoviePilotService"/> 在容器里，而对话框要主窗口的 <c>XamlRoot</c>；
    /// 要整理哪些文件、按什么身份，由调用方先用 <see cref="EmbyNian.MoviePilot.MoviePilotTransferCollect"/>
    /// 收好（那一步要 Emby 会话，外壳不该再碰）。
    /// </summary>
    void ShowMoviePilotReorganize(EmbyNian.MoviePilot.MoviePilotTransferContext context);
}

/// <summary>
/// 标题栏加面包屑那一条底下是什么，一共三档。墨和底两件事都跟着它走，而这两件事并不是同一个开关的两头，
/// 所以是三档而不是一个 bool（见 <c>ShellPage.PaintTitleInk</c>）。
/// <para>
/// 公开的，只因为 <c>ShellPage</c> 是公开类而它实现这个接口的方法就得是公开的；这仍旧是外壳自己的一件事。
/// </para>
/// </summary>
public enum TitleStrip
{
    /// <summary>页面自己的底色，什么都没铺过来：面包屑那一行自己上一层底，墨走主题那支。</summary>
    Plain,

    /// <summary>一张剧照铺到了窗口顶边：那一行不上底色（一条实心条压在图上就是那道要去掉的横边），墨走图上那支浅墨。</summary>
    OnScrim,

    /// <summary>
    /// 图还在，可页面已经自己把这一条洗成正文那张纸的颜色了（详情页往下拉之后，见 <c>DetailPage.PaintWash</c>）：
    /// 墨要回到主题那支，底却仍旧不能上 —— 页面洗出来的正是「和下方背景一样」那个色，外壳再涂一层页面底色，
    /// 这一条就比正文亮出四五级，洗出来的那道横缝又回来了。
    /// </summary>
    PagePainted
}
