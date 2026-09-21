namespace EmbyNian.Infrastructure;

/// <summary>
/// 窗口此刻的三个形态。
/// <para>
/// <b>「边归显示器」的判断从此只问 <see cref="WindowForms.OccupiesScreen"/>，别再自己写
/// <c>全屏 || 最大化</c>。</b>2026-09-20 加这一层，依据是参考项目（一份 mpv 便携配置，2026-08-12 版）：
/// 那份配置里 mpv 把窗口状态归一成 <c>fullscreen</c> 与 <c>window-maximized</c> 两个属性，而 uosc 又把
/// 它们归一成 <c>fullormaxed = fullscreen or window-maximized</c> 一个布尔 —— 控件的缩放档、顶栏显隐、
/// 光标自动隐藏全问它，没有一个地方自己拼一遍。
/// </para>
/// <para>
/// 本项目此前是散的：同一句「已经占满屏幕了，所以整形／拖动／按比例锁窗都不该动手」在
/// <c>HostWindow.FitToPicture</c>、<c>BeginDrag</c>、<c>BrowseFoldMeasurable</c>、<c>AspectLock</c> 与
/// 播放页右上角那颗按钮的图标里各写了一遍（五处，写成四种样子）；而两条管线的形态事实还分处两地 ——
/// 集成模式是本窗口的 Win32 状态，独占模式是 mpv 窗口自己的属性。归一之后只剩这一个答主。
/// </para>
/// </summary>
public enum WindowForm
{
    /// <summary>普通窗口：有边框、可拖动，形状由用户或画面比例决定。</summary>
    Windowed,

    /// <summary>最大化：边归显示器，标题栏还在，随时能还原成窗口化。</summary>
    Maximized,

    /// <summary>全屏：标题栏与边框被剥掉，矩形就是显示器一块，且在集成模式下临时加入置顶带盖住任务栏。</summary>
    Fullscreen
}

/// <summary>
/// 窗口形态的纯函数。没有 Win32、没有窗口句柄、不问 mpv —— 两个布尔进，一个形态出，所以它可以被单元
/// 测试直接钉住（本层住在 Core 而不是 Shell，正是为这个：留在 Shell 里的判断是没人看守的判断）。
/// </summary>
public static class WindowForms
{
    /// <summary>
    /// 由两个事实推出形态。<b>全屏优先</b>：一个既被标成最大化又全屏的窗口就是全屏的 —— 集成模式的
    /// <c>EnterFullscreen</c> 会先把最大化还原掉再进全屏，所以这个组合在那边不该出现；真要出现，报
    /// 「全屏」才是那个有画面的形态。
    /// </summary>
    public static WindowForm Of(bool fullscreen, bool maximized) =>
        fullscreen ? WindowForm.Fullscreen
        : maximized ? WindowForm.Maximized
        : WindowForm.Windowed;

    /// <summary>
    /// 是否占满屏幕 —— 参考项目里 uosc 的 <c>fullormaxed</c>。凡是「窗口的边已经归显示器了，因此按画面
    /// 比例整形、拖动窗口、按比例锁住拖拽、把右上角那颗画成还原」这类判断，问的都是这一句。
    /// </summary>
    public static bool OccupiesScreen(WindowForm form) => form != WindowForm.Windowed;

    /// <summary>形态的中文名。日志、姓名牌与自检报告里印的就是它 —— 免得每处自己拼一串。</summary>
    public static string Name(WindowForm form) => form switch
    {
        WindowForm.Fullscreen => "全屏",
        WindowForm.Maximized => "最大化",
        _ => "窗口化"
    };

    /// <summary>
    /// 开播那一刻要不要替观众把画面铺满。四个调用点（播放页进场换手、播放页开播应答、独占模式开播应答、
    /// 内置后端起播前）此前各自拼了一遍同样的合取式，这里合成一句 —— 判据只有一个答主，
    /// 「自动全屏」这条规矩要改的时候只改这里。
    /// <para>
    /// <paramref name="lifecycleActive"/> 是「<b>这是一场真播放</b>」：播放页那几个工具预览开关
    /// （<c>--hide-cursor</c> 停在播放页上只看光标、<c>--show-osd</c> 停一层浮层）也会走一遍进场，
    /// 但它们不持播放生命周期，窗口不该被它们跳成全屏。独占模式那边两处由事件本身保证（它们只在
    /// 真的开播时到达），所以显式传 true。
    /// </para>
    /// <para>
    /// <paramref name="current"/> 是「<b>已经全屏</b>」而不是 <see cref="OccupiesScreen"/>：一个最大化的
    /// 窗口照样要进全屏（它还没全屏），而一个已经全屏的窗口再进一次是无事生非。
    /// </para>
    /// <para>
    /// <b>独占模式那两个调用点传 <see cref="WindowForm.Windowed"/>。</b>那是事实而不是省略：mpv 窗口的
    /// 形态由 mpv 的属性持有，起播前窗口还没出生、属性也还没读；而 <c>fullscreen=yes</c> 是幂等的，
    /// 重复设置不会让已经全屏的窗口再跳一次 —— 所以「还没全屏」这个答案在最坏情况下只是多发一条命令。
    /// </para>
    /// </summary>
    public static bool WantsAutoFullscreen(bool setting, bool lifecycleActive, WindowForm current) =>
        setting && lifecycleActive && current != WindowForm.Fullscreen;
}
