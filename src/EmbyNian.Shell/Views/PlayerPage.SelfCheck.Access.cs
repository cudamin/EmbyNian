using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 这一页对着读屏软件和键盘的那一面（<c>ProbeNarration</c>），加上换集遮挡层里那个转圈。
/// <para>
/// 为什么这也要一关：整条控制条上十二颗按钮里有九颗的全部内容是一个 <c>FontIcon</c>，而图标字体里那个码位
/// 在私用区 —— 读屏软件念出来的就只有「按钮」两个字，念不出是哪一颗。这件事编译看不见、单元测试进不来、
/// 屏幕上更看不出来（图标是画着的），只有把每个控件的自动化名字问一遍才知道。悬停提示不算：那是给鼠标
/// 用的，而这一关说的正是没有鼠标的那种人。
/// </para>
/// <para>
/// 顺带把三件同类的事一起量了：焦点框真的落到了浮层按钮上（页面自己拒绝焦点框，一圈画在整部电影外面没有
/// 意义，但里面的按钮要各自要回来）、几处纯装饰的读数真的从无障碍树里退了出去、换集那个转圈只在遮挡层立
/// 着的时候转。转圈那件事不是无障碍，是它跟前两件同一个坏法：屏上看不出来。
/// </para>
/// <para>
/// 拆成几个文件的缘由见 <c>PlayerPage.SelfCheck.cs</c> 的类注释。
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>
    /// Every control on this page that a screen reader or a keyboard can land on, asked for the one thing it
    /// needs to be usable: a name.
    /// <para>
    /// The name is read off the automation peer rather than off <see cref="AutomationProperties"/>, because
    /// the peer is what a screen reader actually asks. That difference is the whole point here — a button
    /// whose content is text needs nothing declared and answers with its own words, while a button whose
    /// content is a glyph answers with an empty string unless someone wrote a name down. Reading the
    /// attached property instead would demand a redundant name on the first kind and still miss nothing on
    /// the second, so it would be a worse check that looked identical.
    /// </para>
    /// <para>
    /// Which is why four of the twenty have to be brought out before they can be asked: a control that is
    /// collapsed at rest answers with nothing at all, declared name or not. Measured, not assumed — the
    /// first run of this probe reported 上一集 and 下一集 as unnamed although both had carried a name in the
    /// markup all along. So the two switches that govern them are flipped for the walk: 上一集/下一集/选集
    /// exist only when the file has siblings, and the 跳过 button only while an offer is up, where its
    /// caption is 跳过片头 or 跳过片尾 depending on what is being offered.
    /// </para>
    /// <para>
    /// The walk stops at each control rather than descending into it. Template internals — a slider's two
    /// track halves, the search box's own query button — are not this page's to name, and a probe that
    /// failed over one would be reporting on the framework's generic.xaml. What is left is exactly the set
    /// this file declares, which is also what makes the check keep working: a thirteenth glyph button added
    /// to the bar next year is caught here without anyone remembering to add it to a list.
    /// </para>
    /// <para>
    /// Laid out for the duration and put back, the same way <see cref="ProbeTap"/> does it, so that both
    /// strips are realised while they are being read. Every flip happens inside this one call, so no frame is
    /// composed between them and nothing appears over the library behind the player.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeNarration()
    {
        var was = Visibility;
        var wasCover = ViewModel.CoverUp;
        var wasEpisodes = ViewModel.EpisodeControlsVisible;
        var wasOffer = ViewModel.SkipOffered;
        var wasCaption = ViewModel.SkipCaption;

        Visibility = Visibility.Visible;
        ViewModel.EpisodeControlsVisible = true;
        ViewModel.SkipOffered = true;
        ViewModel.SkipCaption = "跳过片头";
        UpdateLayout();

        var named = 0;
        var mute = new List<string>();

        Walk(Root);

        // 焦点框. The page refuses one for itself — it is a tab stop because the single-letter commands are
        // its own, and a rectangle around the whole picture would be absurd — and every button inside it
        // asks for one back through OsdButtonStyle. Read off two of them rather than trusted to the setter:
        // a style that stopped being applied is exactly the sort of edit that leaves everything else looking
        // right.
        var focus = PlayButton.UseSystemFocusVisuals
                    && FullscreenButton.UseSystemFocusVisuals
                    && !UseSystemFocusVisuals;

        // The pictures of things already said in words: the buffered-ahead bar, the chapter ticks, the
        // offer's countdown, the thin line, the pause badge, the hover preview and the handover ring. Out of
        // the accessibility tree, or a reader walking the transport gets 「进度 87%」 twice and a badge in
        // between.
        var loud = new List<string>();
        foreach (var (name, element) in new (string Name, UIElement Element)[]
                 {
                     ("已缓冲", CacheBar),
                     ("章节刻度", ChapterTicks),
                     ("跳过倒计时", SkipCountdown),
                     ("细进度线", ThinLine),
                     ("暂停角标", PulseBadge),
                     ("章节预览", ChapterPeek),
                     ("换集转圈", CoverRing)
                 })
        {
            if (AutomationProperties.GetAccessibilityView(element) != AccessibilityView.Raw) loud.Add(name);
        }

        // 换集那个转圈. IsActive used to be True in the markup, which meant a compositor animation running
        // from the moment the shell built this page until the process exited — behind a collapsed grid,
        // every minute the app was not changing episodes. Bound now, and driven here in both directions
        // because 「it is off right now」 is also what a ring wired to nothing at all would report.
        var idle = !CoverRing.IsActive;

        ViewModel.CoverUp = true;
        var spinning = CoverRing.IsActive;

        ViewModel.CoverUp = wasCover;
        var settled = CoverRing.IsActive == wasCover;

        ViewModel.EpisodeControlsVisible = wasEpisodes;
        ViewModel.SkipOffered = wasOffer;
        ViewModel.SkipCaption = wasCaption;
        Visibility = was;
        UpdateLayout();

        var ok = mute.Count == 0 && named > 0 && focus && loud.Count == 0
                 && idle && spinning && settled
                 && Visibility == was && ViewModel.CoverUp == wasCover
                 && ViewModel.EpisodeControlsVisible == wasEpisodes && ViewModel.SkipOffered == wasOffer;

        return (ok,
            $"{named} 个控件报出了名字"
            + (mute.Count == 0 ? "，没有一个是哑的" : $"；没名字的：{string.Join('、', mute)}")
            + $"；焦点框{(focus ? "在按钮上、不在整页上" : "位置不对")}"
            + $"；{(loud.Count == 0 ? "7 处装饰读数已退出无障碍树" : $"仍在树里：{string.Join('、', loud)}")}"
            + $"；换集转圈：遮挡层收起时{(idle ? "不转" : "还在转")}、立起时{(spinning ? "转" : "不转")}"
            + $"，复位{(settled ? "正常" : "失败")}");

        // Depth-first, stopping at every control this page declares: see the remarks for why it does not
        // descend into one.
        void Walk(DependencyObject parent)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);

            for (var index = 0; index < count; index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);

                if (child is not ButtonBase and not Slider and not AutoSuggestBox)
                {
                    Walk(child);
                    continue;
                }

                var element = (FrameworkElement)child;
                var name = FrameworkElementAutomationPeer.CreatePeerForElement(element)?.GetName() ?? "";

                if (name.Trim().Length > 0) named++;
                else mute.Add(element.Name.Length > 0 ? element.Name : element.GetType().Name);
            }
        }
    }
}
