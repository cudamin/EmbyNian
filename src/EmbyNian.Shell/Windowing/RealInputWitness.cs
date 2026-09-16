using System.Runtime.InteropServices;
using EmbyNian.Shell.Interop;

namespace EmbyNian.Shell.Windowing;

/// <summary>
/// 藏匿期判「这一记位移是手还是别人注入」的<b>真实输入见证</b>（第十六报，2026-09-16）。
/// <para>
/// 第十一报以来那个裁决者（<see cref="EmbyNian.Core.Playback.ChromeReveal.WarpOrHand"/>）从头到尾
/// 只看一个东西：<b>位置形状</b>。第十五报把它推到了形状能推的尽头（「搬完就冻住」的注入判得掉），
/// 而十六报的日志把形状的极限也钉死了 —— AyuGram 的注入不是一次冻结的跳变，是一段<b>动画</b>：
/// 09:22:33 起的四秒里 972,378 → 986,379 → 1028,383 → 1038,385 → 1058,380，每步十几到几十像素、
/// 步与步隔着一两百毫秒。每一步单独看都与「手走了一拍」无法区分，于是每一步之后的那一记都轮到了
/// 手的位置上。形状上没有答案，答案只能去形状之外找。
/// </para>
/// <para>
/// 形状之外的答案是 <b>WM_INPUT</b>。三行证据（<c>work/hook-probe.py</c>、<c>work/rawinput-phase.py</c>
/// 的探测输出）：
/// <list type="number">
/// <item><see cref="EmbyNian.Shell.Interop.Native.SetCursorPos"/> 搬光标<b>不产生任何</b> WM_INPUT ——
/// 「注入」的一大类在见证眼里根本没发生过；</item>
/// <item><c>SendInput</c> 注入产生 WM_INPUT，但 header 里 <c>hDevice == 0</c> —— 系统知道这条输入
/// 没有出处；</item>
/// <item>真手一动，WM_INPUT 的 <c>hDevice</c> 是那个鼠标的句柄，从来非零。</item>
/// </list>
/// 所以见证的判据只有一行：<b>位移出现前后有没有 hDevice ≠ 0 的鼠标移动</b>。有，是手；没有，
/// 是注入 —— 不管那段注入在形状上多像一只手。
/// </para>
/// <para>
/// 收法：向系统注册原始输入（usagePage 1 / usage 2，<see cref="EmbyNian.Shell.Interop.Native.RidevInputsink"/>），
/// 挂在已经子类化的 XAML 岛窗口上 —— <c>IslandDispatch</c> 已在它的 WndProc 里，多认一条消息的代价是零。
/// INPUTSINK 是要害：全屏播放的窗口从来没有焦点，按默认注册（只送前台）见证会在最有用的时刻恰好缺席。
/// WM_INPUT 与 <c>PollPointer</c> 都在 XAML 的 UI 线程上（岛由主线程创建，tick 走 DispatcherQueue），
/// 这些字段不需要锁 —— 这一点写下来，省得下一位读者去翻。
/// </para>
/// <para>
/// 已知的边界，记下来而不是装它不存在：驱动层的键鼠软件（Logitech Options 之类）会把真手的移动
/// 转发成注入 —— 那样的手在见证眼里没有 hDevice，会被误判成注入、光标不醒。到那一步之前，判定
/// 每一票都带着计数进了日志（<see cref="RealMoves"/> / <see cref="InjectedMoves"/>），下次报告不用猜。
/// </para>
/// <para>
/// 十七报（2026-09-16 午后）的真机日志把两个<b>实现错误</b>钉在同一行里：显示名牌写「真实输入见证：有」，
/// 同一行的账却是「真 0/注 0」。其一是本类把 <c>RAWINPUTHEADER</c> 的字段顺序读反了 —— dwType 在 @0
/// 不在 @4，dwSize=48 ≠ 0，每条鼠标记录都在第一问被扔掉，见证全聋（真 0/注 0，连用户自己的手都看不见）；
/// 其二是「没见过」的哨兵 <c>long.MinValue</c> 在 <see cref="RecentRealInput"/> 里相减溢出成大负数，
/// 聋上加聋之后这一问永远答「有」—— <b>每一记注入都被判成手，AyuGram 的毛病一分未改</b>。两条一起
/// 才是症状，两条一起修：字段顺序照 winuser.h 原文（回归钉在自检的合成记录里），哨兵先判再减。
/// 自检的 Forge 腿测不出这两条 —— Forge 把时间戳变成真的，而沙箱又注不进真实输入，聋与溢出都只有
/// 真机的「从未见过」状态才现形。
/// </para>
/// </summary>
internal sealed class RealInputWitness
{
    /// <summary>原始输入注册成了没有。失败时调用方退回形状启发式（WarpOrHand），这一个字段就是那
    /// 条退路的开关；它也进日志 —— 「见证缺席」必须能在日志里一眼看出来，不然下次报告又要猜。
    /// 注册在 <c>HookIslandCursor</c> 里做（子类化成功的同一处），结果写回这里。</summary>
    public bool Ready { get; internal set; }

    private long _lastRealMoveAt = long.MinValue;

    /// <summary>见证过的真移动（hDevice ≠ 0 且有位移）累计多少次。只涨不清：取样行读它，涨没涨
    /// 就是「这台机器的真手有没有被见证看见」的直接答案。</summary>
    public int RealMoves { get; private set; }

    /// <summary>见到的无出处移动（hDevice == 0，SendInput 一类）累计多少次。纯取证：它证明系统里
    /// 确有程序在注入输入 —— 日志里这个数和 AyuGram 收消息的时刻对上，就少一轮猜测。</summary>
    public int InjectedMoves { get; private set; }

    /// <summary>自检伪造的见证次数。伪造走 <see cref="Forge"/> 而不混进 <see cref="RealMoves"/>：
    /// 那个数的语义是「OS 说过的真话」，混进伪造后就不再是了。</summary>
    public int Forged { get; private set; }

    /// <summary>
    /// 一条 WM_INPUT 进来，判它是不是真手的移动。判不出（缓冲不够、不是鼠标）就不记 —— 见证只说
    /// 看见过的，不说猜的。
    /// <para>
    /// 「移动」判的是 lLastX/lLastY 有没有一个非零：按钮起落、滚轮都带着 hDevice 走这条路，但它们
    /// 不搬光标，记进去只会把「手在动」的窗口撑大。位移出现就是位移，相对绝对都一样（触摸板走绝对
    /// 路线，它也是手）。
    /// </para>
    /// </summary>
    public void Parse(IntPtr rawInput)
    {
        // header 24 + RAWMOUSE 24 = 48，给 64 是余量不是精确 —— 真要按需分配的话每条消息一次
        // 分配，而鼠标一动这条每秒来上百次。固定缓冲一次常驻。
        var buffer = ThreadLocalBuffer;
        var available = (uint)buffer.Length;

        var written = Native.GetRawInputData(rawInput, Native.RidInput, buffer, ref available, Native.RawInputHeaderSize);
        if (written < Native.RawInputHeaderSize + Native.RawMouseSize) return;

        Observe(buffer);
    }

    /// <summary>
    /// 对一条已铺平的 RAWINPUT（header 24 + RAWMOUSE 24）做「手还是注入」的记账。从 Parse 拆出来
    /// 是为了让自检能把合成记录直接灌进来 —— 沙箱注不进真实输入，字段顺序的回归只能这样钉住。
    /// <para>
    /// 十七报事故在这里：winuser.h 原文 <c>typedef struct tagRAWINPUTHEADER { DWORD dwType; // @0;
    /// DWORD dwSize; // @4; HANDLE hDevice; // @8; WPARAM wParam; }</c> —— dwType 在 @0。此前读的
    /// @4 是 dwSize=48 ≠ RimTypeMouse(0)，每条鼠标记录都在第一问被扔掉，见证全聋。dwType 0~3 恰好
    /// 都不是 48，dwSize 恰好是 48：这个颠倒不是「读到别的类型」而是「全扔」，日志上一条痕迹都没有。
    /// </para>
    /// </summary>
    internal void Observe(byte[] record)
    {
        // RAWINPUTHEADER：dwType@0、dwSize@4、hDevice@8（winuser.h 原文顺序）。不是鼠标（键盘、
        // 其它 HID）不判 —— 它们不搬光标。读法用 BitConverter 而不是 Marshal.ReadInt32：数组重载
        // 那个 signature 走 object，CS0618 已过时。
        if ((uint)BitConverter.ToInt32(record, 0) != Native.RimTypeMouse) return;

        // hDevice 是 IntPtr（x64 上 8 字节），按 Int64 读回来再收窄 —— 发布只有 win-x64 一个 flavor。
        var device = (IntPtr)BitConverter.ToInt64(record, 8);

        // RAWMOUSE@24：usFlags@24、lLastX@36、lLastY@40（x64；lLastX/lLastY 是 LONG）。
        var x = BitConverter.ToInt32(record, Native.RawMouseXOffset);
        var y = BitConverter.ToInt32(record, Native.RawMouseYOffset);

        if (x == 0 && y == 0) return;

        if (device != IntPtr.Zero)
        {
            RealMoves++;
            _lastRealMoveAt = Environment.TickCount64;
        }
        else
        {
            InjectedMoves++;
        }
    }

    /// <summary>
    /// 距最后一次被见证的真移动，还在 <paramref name="withinMilliseconds"/> 之内没有。窗口给多少由
    /// 调用方定（<see cref="EmbyNian.Core.Playback.ChromeReveal.WarpWitnessMilliseconds"/>）：太窄，
    /// 手停下后轮询才到的最后一记会被冤成注入；太宽，注入落地瞬间恰好有一记久远的真移动会冤放它。
    /// 轮询一拍 100ms、手停下到轮询看见最多两拍，300 在两边都站得住。
    /// <para>
    /// 时钟与 <c>PlayerPage.Now</c> 同一个（<see cref="Environment.TickCount64"/>），这一问没有跨时钟
    /// 换算 —— 两边本来就是一个钟。
    /// </para>
    /// <para>
    /// 哨兵 <see cref="long.MinValue"/> 必须先判再减：直接减的话 TickCount64 − MinValue 数学上是
    /// 「开机时长 + 2⁶³」，unchecked 下回卷成一个大负数，恒 ≤ 窗口 —— 「从没见过」永远答「有」。
    /// 十七报的第二条事故就是它（第一条在 <see cref="Observe"/> 的注释里）。
    /// </para>
    /// </summary>
    public bool RecentRealInput(long withinMilliseconds) =>
        _lastRealMoveAt != long.MinValue
        && Environment.TickCount64 - _lastRealMoveAt <= withinMilliseconds;

    /// <summary>
    /// 自检专用：凭空记一条「刚刚有真输入」。SetCursorPos 不产生 WM_INPUT（这正是它能当注入替身的
    /// 理由），所以自检里那些模拟真手的腿必须自己把见证补上 —— 不补，新判据会把它们全判成注入，
    /// 探针红得毫无信息量。补上，探针测的才是「判据认不认得出补过的见证」，也就是它该测的东西。
    /// </summary>
    public void Forge()
    {
        Forged++;
        _lastRealMoveAt = Environment.TickCount64;
    }

    /// <summary>取证取样行读它：距最后一次真移动多久了。从没见过就答「没见过」，不拿 MinValue 去吓人。</summary>
    public string LastRealMoveAgo
    {
        get
        {
            if (_lastRealMoveAt == long.MinValue) return "没见过";

            var ago = Environment.TickCount64 - _lastRealMoveAt;
            return ago >= 100_000 ? "许久" : $"{ago}ms 前";
        }
    }

    /// <summary>Parse 的固定缓冲。挂在类上而不是每次调用分配：这条路径鼠标一动就来上百次。</summary>
    [ThreadStatic]
    private static byte[]? _buffer;

    private static byte[] ThreadLocalBuffer => _buffer ??= new byte[64];
}
