using Momoka.Emby;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Momoka.Shell.Views;

/// <summary>
/// 详情页那几颗下拉（媒体源、音频、字幕、季）用的一层壳：把框架模板里那个弹层从「窗口化弹层」翻成
/// 「树内弹层」，好让调色板给浮层的那支应用内亚克力真的采得到它底下的那一页。
/// <para>
/// 用户 2026-09-27：「剧页面、电影页面、集页面，季页面，把音频和字幕的选择栏改成亚克力背景」。底下那件事
/// 不是没配过 —— <c>Palette.xaml</c> 里 <c>ComboBoxDropDownBackground</c> 从 2026-09-25 起就是一支
/// <c>AcrylicBrush</c>，两轮调浓淡（0.72/0.65 → 0.55/0.55）都没让它像玻璃。根因在框架那一侧，不在浓淡：
/// **框架的 ComboBox 把弹层设成窗口化的**（<c>ComboBox_Partial.cpp</c> 里
/// <c>m_tpPopupPart->put_ShouldConstrainToRootBounds(false)</c>），于是浮层开在自己那个窗口里，而
/// 应用内亚克力只采得到「同一个窗口里画在它后面的东西」（WinUI 团队在 microsoft-ui-xaml#9523 里的原话：
/// 「AcrylicBrush is only able to blur other content drawn by WinAppSDK in the same window. For windowed
/// popups … the AcrylicBrush can only blur content below it in that popup window.」）—— 那个窗口里除了浮层
/// 自己什么都没有，采不到就退回 <c>FallbackColor</c>，屏上因此是一块不透明的实心色。拿用户的截图量过：
/// 浮层底下压着 <c>#49363E</c> 那张粉紫色的剧照，浮层上是 <c>#20262D</c>（正是那支画笔的 FallbackColor），
/// 一点都没透上来。
/// </para>
/// <para>
/// 所以这一层壳只做一件事：把那颗弹层的 <see cref="Popup.ShouldConstrainToRootBounds"/> 设回 <c>true</c>。
/// 浮层于是回到<em>同一棵树</em>里渲染（不再开窗口），亚克力按它本来的意思工作 —— 模糊底下的页面、按调色板
/// 的 tint 上色，和详情页正文那几块玻璃（<c>EgFrostBrush</c>）是同一种材料、同一套参数。
/// </para>
/// <para>
/// 代价说清楚：树内弹层受窗口边界约束。窗口矮到装不下整张浮层时，框架会把浮层往上翻、必要时收窄它
/// （列表本来就在 <c>ScrollViewer</c> 里，收窄之后照样滚得到），而不是像窗口化弹层那样悬到窗口外面去。
/// 详情页这几颗下拉都长在页面里、离窗口边很远，实际看得见的差别只有「下拉不再越出窗口」这一条。
/// </para>
/// <para>
/// 为什么是子类而不是改模板：模板那一份是框架的 <c>DefaultComboBoxStyle</c>，抄一份进仓库就等于自己养一份
/// 会随 SDK 过期的副本；而 <c>Popup</c> 部件是框架自己 <c>GetTemplateChild</c> 拿的那个（名字就叫
/// <c>Popup</c>），从子类的 <see cref="OnApplyTemplate"/> 里问它，正好是子类该干的事。基类在
/// <c>OnApplyTemplate</c> 里把它设成 false，所以这里要先 <c>base</c> 再改。
/// </para>
/// <para>
/// 只给详情页那七颗用（<c>DetailPage.xaml</c>；宽版式三颗、紧凑版式三颗、季那一颗），别的页面照旧 ——
/// 用户点名的是剧、电影、集、季四个页面。要把它铺到全应用的下拉上，说一声就行：把
/// <c>SettingsPage.xaml</c> 等处那颗 <c>ComboBox</c> 也换成它就完了，样式与材料都是现成的。
/// </para>
/// </summary>
public sealed class EgPicker : ComboBox
{
    /// <summary>
    /// 模板里那颗弹层，<see cref="OnApplyTemplate"/> 拿到之后留着给自检读（「浮层在树内」那一条）。
    /// 模板还没上树时是 null。
    /// </summary>
    internal Popup? PopupPart { get; private set; }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ComboBoxItem container)
            container.IsEnabled = item is not TrackRow { IsAvailable: false };
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        PopupPart = GetTemplateChild("Popup") as Popup;
        if (PopupPart is null) return;

        PopupPart.ShouldConstrainToRootBounds = true;
    }
}
