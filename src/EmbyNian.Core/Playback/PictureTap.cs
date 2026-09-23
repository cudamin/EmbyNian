namespace EmbyNian.Playback;

/// <summary>
/// 「点一下画面暂停」和「双击画面全屏」这两个手势叠在一起的那一小段账。
/// <para>
/// 存在的理由是用户报的那件事：「双击画面全屏的时候会触发暂停和开始」。从前的写法是<b>单击立刻暂停、双击再把它
/// 撤回去</b>，净播放状态因此一直是对的 —— 可屏上会闪两次徽标（先「暂停」再「播放」），而 mpv 那一头也真的暂停
/// 了一下又恢复了。
/// </para>
/// <para>
/// 现在的做法是<b>先攥住</b>：点在画面上那一下不当场下发，攥 <see cref="ClickDelayMilliseconds"/> 毫秒；这段时间
/// 里第二下到了，那一次暂停就<b>从来没有发生过</b>，双击就是干净的一次全屏切换。攥不住的（双击比这段时间慢）照旧
/// 走撤回那条路，只是徽标会被静音 —— 见 <see cref="PulseMuteMilliseconds"/>。
/// </para>
/// <para>
/// 做成有状态的类而不是纯函数，是照 <see cref="ChromeReveal"/> 和 <see cref="SkipCoordinator"/> 的样子来的：手势
/// 本来就是一串带次序的事件，而次序正是这件事会错的地方。放 Core 是为了让那个次序能被钉住 —— 屏上唯一看得见的
/// 症状是「双击时闪了两下」，而它和「单击不再暂停了」这个更糟的坏法在截图上长得一模一样。
/// </para>
/// </summary>
public sealed class PictureTap
{
    /// <summary>
    /// 单击押后多久才证实（毫秒）—— <b>两种模式、以及用户那份参考 mpv 配置，都用这一个数</b>（用户令 2026-09-23）。
    /// <para>
    /// 300 不是新挑的数：参考配置（<c>C:\mpv_config-2026.08.12</c>）里单击暂停就是 <c>inputevent.lua</c> 拿
    /// <c>input-doubleclick-time</c> 做 debounce 押后的，而 mpv 这条属性的默认值正是 300（那份配置没有改它）。
    /// 独占模式这一头读的也是同一个属性（<c>assets/mpv-ui/scripts/uosc/main.lua</c> 的
    /// <c>embynian_click_pause_window</c>），装配现在按这个常量显式写进去（<c>MpvUi.Build</c> 的
    /// <c>input-doubleclick-time</c>）—— 一个数管住「押后多久」与「多久之内算双击」两件事，两条管线再也分不开。
    /// </para>
    /// <para>
    /// <b>从前为什么是 150。</b>旧写法是 <c>min(GetDoubleClickTime(), 150)</c>：这台机器上系统双击间隔是 500 毫秒，
    /// 照它押后太钝，于是砍到 150 —— 代价是手快一点的双击（两下相距 150~300 毫秒）落在押后之外，双击会先暂停再
    /// 撤回：徽标被静音，mpv 却真的停了一下。用户 2026-09-23 点名按参考配置的延迟对齐，于是两种模式都用 300，
    /// 系统那个 500 不再参与 —— 它本来也不属于任何一边的判定（集成模式的双击由 WinUI 认，独占由 mpv 自己认）。
    /// </para>
    /// </summary>
    public const long ClickDelayMilliseconds = 300;

    /// <summary>
    /// 双击之后这么久内不放徽标。
    /// <para>
    /// 比 <see cref="ClickDelayMilliseconds"/> 长，因为撤回那一下要走 mpv 一趟再回来：状态沿是 mpv 推回来的，早一步
    /// 晚一步、甚至被它合并掉都可能。按「多久之内」算而不是「数几次状态沿」正是为这件事 —— 一个数不动的计数器会
    /// 把后面每一次真暂停的徽标都吃掉。
    /// </para>
    /// </summary>
    public const long PulseMuteMilliseconds = 400;

    private bool? _held;

    private bool? _issued;

    /// <summary>攥着，那一次暂停还没下发。</summary>
    public bool Pending => _held is not null;

    /// <summary>暂停已经下发，等着可能到来的第二下把它撤回。</summary>
    public bool Issued => _issued is not null;

    /// <summary>
    /// 单击落在画面上：记下当时的 <c>pause</c>，攥住。上一次的账一并作废 —— 一次新的单击开始的是一次新的手势。
    /// </summary>
    public void First(bool paused)
    {
        _held = paused;
        _issued = null;
    }

    /// <summary>
    /// 攥够了。<c>true</c> 表示「现在才下发暂停」；不在攥着（这一下已经被第二下取消，或者根本没有过第一下）就
    /// 返回 <c>false</c> —— 定时器晚一拍跳到的那一次不能凭空切一下播放。
    /// </summary>
    public bool Elapsed()
    {
        if (_held is not { } paused) return false;

        _held = null;
        _issued = paused;
        return true;
    }

    /// <summary>
    /// 第二下到了。返回要还回去的 <c>pause</c> 值；<c>null</c> 表示那一次暂停从来没下发过，没有什么可还的 ——
    /// 这正是这次修法想要的那一支。
    /// </summary>
    public bool? Second()
    {
        var before = _issued;

        _held = null;
        _issued = null;

        return before;
    }

    /// <summary>
    /// 这一下不算：点在控件上、离开播放器、程序退出。
    /// <para>
    /// 不许省。它挡的是一桩真事故：点在控制条上的那一下如果不作废，紧接着一次落在控制条上的双击就会拿着十分钟前
    /// 那次单击记下的 <c>pause</c> 去「撤回」，把正在放的片子停掉。
    /// </para>
    /// </summary>
    public void Forget()
    {
        _held = null;
        _issued = null;
    }
}
