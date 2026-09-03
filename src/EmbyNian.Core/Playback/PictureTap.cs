namespace EmbyNian.Playback;

/// <summary>
/// 「点一下画面暂停」和「双击画面全屏」这两个手势叠在一起的那一小段账。
/// <para>
/// 存在的理由是用户报的那件事：「双击画面全屏的时候会触发暂停和开始」。从前的写法是<b>单击立刻暂停、双击再把它
/// 撤回去</b>，净播放状态因此一直是对的 —— 可屏上会闪两次徽标（先「暂停」再「播放」），而 mpv 那一头也真的暂停
/// 了一下又恢复了。
/// </para>
/// <para>
/// 现在的做法是<b>先攥住</b>：点在画面上那一下不当场下发，攥 <see cref="HoldFor"/> 毫秒；这段时间里第二下到了，
/// 那一次暂停就<b>从来没有发生过</b>，双击就是干净的一次全屏切换。攥不住的（双击比这段时间慢）照旧走撤回那条路，
/// 只是徽标会被静音 —— 见 <see cref="PulseMuteMilliseconds"/>。
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
    /// 攥住单击最多这么久。
    /// <para>
    /// 上限是 150 而不是操作系统自己那个数：这台机器上 <c>GetDoubleClickTime()</c> 答 500 毫秒，照它攥就是每次
    /// 点画面暂停都要等半秒才有反应，那是用一个新毛病换掉一个旧毛病。150 毫秒以下人感觉不出延迟，而比它慢的那些
    /// 双击会落到「撤回并静音」那条路上 —— 代价只是画面卡顿一下、不出徽标，播放状态照旧不变。
    /// </para>
    /// </summary>
    public const long HoldCapMilliseconds = 150;

    /// <summary>
    /// 双击之后这么久内不放徽标。
    /// <para>
    /// 比 <see cref="HoldCapMilliseconds"/> 长，因为撤回那一下要走 mpv 一趟再回来：状态沿是 mpv 推回来的，早一步
    /// 晚一步、甚至被它合并掉都可能。按「多久之内」算而不是「数几次状态沿」正是为这件事 —— 一个数不动的计数器会
    /// 把后面每一次真暂停的徽标都吃掉。
    /// </para>
    /// </summary>
    public const long PulseMuteMilliseconds = 400;

    /// <summary>
    /// 这一下攥多久：不超过操作系统对「双击」的定义，也不超过 <see cref="HoldCapMilliseconds"/>。系统答 0 或者
    /// 负数（问不出来）就用上限 —— 攥得久一点最多是暂停慢一点，攥不住则是这次修的那件事又回来了。
    /// </summary>
    public static long HoldFor(long systemDoubleClickMilliseconds) =>
        systemDoubleClickMilliseconds <= 0
            ? HoldCapMilliseconds
            : Math.Min(systemDoubleClickMilliseconds, HoldCapMilliseconds);

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
