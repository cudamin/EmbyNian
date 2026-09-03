using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 置顶那颗键对读屏软件说的话。四条断言，钉的是一件屏上看不见的事。
/// <para>
/// 这颗键从前开着的时候整块变强调色，用户要求改成两档自己画的图标。改完之后**屏上唯一的线索是那个图标，而读屏
/// 软件看不见图标** —— 那一头唯一的线索就是这两句话。少一句「已置顶」，屏幕上一切正常，不用鼠标的人却再也不知道
/// 这个开关是开还是关；而这种缺失编译看不见、截图看不见、自检也只看得出「两档名字不一样」。
/// </para>
/// </summary>
internal static class PinIndicatorTests
{
    public static void Register()
    {
        Test("置顶用词：两档的名字不一样，而且都不空", () =>
        {
            var off = PinIndicator.Name(false);
            var on = PinIndicator.Name(true);

            Assert.True(off.Trim().Length > 0, "未置顶那句不能空 —— 空名字读屏软件只念得出「按钮」");
            Assert.True(on.Trim().Length > 0, "已置顶那句不能空");
            Assert.True(off != on, "两档同一句话，等于开关状态在读屏软件那头消失了");
        });

        Test("置顶用词：已置顶那句里写着「已置顶」", () =>
        {
            // 防的是以后有人把状态从这句话里拿掉、只留一个动作名（「取消置顶」）—— 那样听起来像一条命令，
            // 而不是「现在是开着的」。
            Assert.Contains("已置顶", PinIndicator.Name(true));
        });

        Test("置顶用词：两档的悬停提示不一样", () =>
        {
            Assert.True(PinIndicator.Tip(false) != PinIndicator.Tip(true), "有鼠标的那一头也要分得出开没开");
        });

        Test("置顶用词：两句提示里都带着那颗键", () =>
        {
            // 键写在提示里而不是写在名字里：名字里带上它，读屏软件念的就是「窗口置顶左括号 T 右括号」。
            Assert.Contains(PinIndicator.Key, PinIndicator.Tip(false));
            Assert.Contains(PinIndicator.Key, PinIndicator.Tip(true));
            Assert.DoesNotContain(PinIndicator.Key, PinIndicator.Name(false));
        });
    }
}
