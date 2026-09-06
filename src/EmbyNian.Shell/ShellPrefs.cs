using EmbyNian.Configuration;

namespace EmbyNian.Shell;

/// <summary>
/// 界面那一组设置改完当场生效的那一根线：图片缓存上限、主页版面（次序和勾选）、主页轮播开关。
/// <para>
/// 从前还有一条「默认收起侧边栏」，那一行和侧边栏本身 2026-09-06 一起删掉了；这根线上少了一个订阅方，形状没变。
/// </para>
/// 存在的理由是设置页在它自己那个窗口里（<c>SettingsWindow</c>）。它手上有设置文档，可是没有主窗口的 HWND，
/// 也没有外壳那一页 —— 而这些开关要动的正是那两样。<see cref="ThemeHost.Changed"/> 早就是这个形状了：
/// 一个静态事件，改设置的那一头喊一声，屏幕上那几头各自跟上。这里照抄，不新发明。
/// </para>
/// <para>
/// 事件带的是整份 <see cref="UiSettings"/> 而不是「哪个开关变了」：订阅方各读自己关心的那一条，于是加第四个
/// 开关不用改这个文件，也不用改别的订阅方。设置文档本来就是各处共用的那一份对象，传它不多一次拷贝。
/// </para>
/// <para>
/// 静态事件就要退订，订阅方各自负责：主页在 <c>Unloaded</c> 里退（每次导航都是新实例），外壳那一页和
/// <c>App</c> 不退，因为它们活得和进程一样久。
/// </para>
/// </summary>
public static class ShellPrefs
{
    /// <summary>界面那一组设置改了。参数是改完之后的那一份。</summary>
    public static event Action<UiSettings>? Changed;

    /// <summary>由设置页那一头喊：这一份刚改过，请各位跟上。</summary>
    public static void Apply(UiSettings ui) => Changed?.Invoke(ui);
}
