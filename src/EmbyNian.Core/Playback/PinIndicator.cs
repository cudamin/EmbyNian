namespace EmbyNian.Playback;

/// <summary>
/// 窗口置顶那颗键的两句话：读屏软件念的名字，和鼠标停上去的提示。
/// <para>
/// 只有四句话，为什么值得放进 Core：这颗键从前是个 <c>ToggleButton</c>，开着的时候整块变强调色，用户要求
/// 「置顶开启后不要改变按键颜色，绘制一个置顶开启图标来替换」。改成两档图标之后，**屏上唯一的线索就是那个
/// 图标 —— 而读屏软件看不见图标**，那一头唯一的线索就是这两句话。留在页面里的字符串没有任何自动化的东西
/// 看得见（外壳那层单元测试引不进来），而这四句话里少一句「已置顶」，屏幕上一切正常，不用鼠标的人却再也
/// 不知道这个开关现在是开还是关。
/// </para>
/// <para>
/// 先例是 <see cref="Emby.HomeCarousel.Position"/>：那也是一句只给读屏软件的话，也在 Core，也由单测钉着。
/// </para>
/// </summary>
public static class PinIndicator
{
    /// <summary>那颗键占着的单键。写在提示里而不是写在名字里 —— 名字里带上它，读屏软件念的就是「窗口置顶
    /// 左括号 T 右括号」；键这一栏有 <c>AutomationProperties.AcceleratorKey</c> 专管。</summary>
    public const string Key = "T";

    /// <summary>
    /// 读屏软件念的名字。**状态写在名字里**，因为这颗键不再是 <c>ToggleButton</c>：没有了 Toggle 那套自动化
    /// 模式，「已按下／未按下」不会再有人替我们念出来。已置顶那一句顺带说清按下去会发生什么 —— 一个只报状态
    /// 的名字（「已置顶」）在读屏软件那里听起来像一行读数，而它是一颗按钮。
    /// </summary>
    public static string Name(bool pinned) => pinned ? "已置顶，点一下取消" : "窗口置顶";

    /// <summary>鼠标停上去的提示。同一句话加上那颗键 —— 提示是给有鼠标的人看的，多一个括号不碍事。</summary>
    public static string Tip(bool pinned) => $"{Name(pinned)}（{Key}）";
}
