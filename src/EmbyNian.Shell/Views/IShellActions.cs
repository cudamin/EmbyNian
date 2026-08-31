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
