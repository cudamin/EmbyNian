using System.Runtime.InteropServices;
using EmbyNian.Shell.Interop;

namespace EmbyNian.Shell.Windowing;

/// <summary>
/// 藏匿期判「这一记位移是手还是别人注入」的<b>真实输入见证</b>（第十六报，2026-09-16）。
/// <para>
/// 第十一报以来那个裁决者（十一到十五报的 <c>WarpOrHand</c>，形状判据）从头到尾
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
/// <para>
/// <b>二十一报（2026-09-16）把这张证词从「充分」降成了「必要」。</b>十九报的真机日志钉死了见证自己
/// 的极限：AyuGram 那段位移带着一个<b>真设备句柄</b>（<c>VID_1532&amp;PID_007C</c>，55 条一像素微步），
/// 在输入层面与真手一字不差 —— 上面那条「有 hDevice 就是手」的判据对它不成立，而用户当时把鼠标
/// <b>拔了</b>。所以见证现在只剩一票<b>否决权</b>：说「没有」当场判注入（这一票仍然值钱，纯
/// <c>SetCursorPos</c> 注入全靠它挡），说「有」只是获准去攒路程，够不够
/// <see cref="EmbyNian.Core.Playback.ChromeReveal.WakeTravelPixels"/> 另说。
/// </para>
/// </summary>
internal sealed class RealInputWitness
{
    /// <summary>原始输入注册成了没有。失败时调用方就没有这张否决票（藏匿期只剩路程那一关），这一个
    /// 字段就是那条退路的开关；它也进日志 —— 「见证缺席」必须能在日志里一眼看出来，不然下次报告又要猜。
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
        var flags = BitConverter.ToUInt16(record, Native.RawMouseFlagsOffset);
        var x = BitConverter.ToInt32(record, Native.RawMouseXOffset);
        var y = BitConverter.ToInt32(record, Native.RawMouseYOffset);

        if (x == 0 && y == 0) return;

        if (device != IntPtr.Zero)
        {
            RealMoves++;
            _lastRealMoveAt = Environment.TickCount64;
            RecordRealDevice(device, flags, x, y);
        }
        else
        {
            InjectedMoves++;
        }
    }

    /// <summary>
    /// 距最后一次被见证的真移动，还在 <paramref name="withinMilliseconds"/> 之内没有。窗口给多少由
    /// 调用方定（<see cref="EmbyNian.Core.Playback.ChromeReveal.WitnessWindowMilliseconds"/>）：太窄，
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

    // ---- 十八报：设备取证 ------------------------------------------------------------------
    // 12:43 的真机唤醒把新的一课写出来了：账从 真719 涨到 真726（7 记带句柄的「真输入」、位移净
    // 8,2），见证按规矩答了「有」、光标按规矩醒了 —— 计数分不清这是手、还是某个设备在发幽灵输入
    // （触摸屏幽灵触、数位板悬停、传感器抖动都长这个样）。治法不是再加裁决，是把「是谁」写进日志：
    // 设备接口名里的 VID/PID 点得名到具体硬件。
    //
    // 取证分两层。常驻层：每条真输入记下设备短名与位移（每句柄只问一次系统，结果进字典，移动热路径
    // 上只多一次字典查询）；捕获层：Chrome 在藏匿开始时 BeginCapture、结束时 EndCapture，期间的真
    // 输入逐条留短账（上限 12 条）—— 藏匿期本该一条真输入都没有，有账就是铁证。

    /// <summary>末次真输入来自哪块硬件（VID/PID 短名，拿不到名退回句柄十六进制）。从未见过时是「?」。</summary>
    public string LastRealDevice { get; private set; } = "?";

    /// <summary>末次真输入的原始读数（绝对设备时是坐标，配合 <see cref="LastRealAbsolute"/> 读）。</summary>
    public int LastRealDx { get; private set; }

    /// <summary>同上，纵向。</summary>
    public int LastRealDy { get; private set; }

    /// <summary>末次真输入走的是不是 MOUSE_MOVE_ABSOLUTE（触摸/数位板/远端桌面常走绝对路线；真手
    /// 的鼠标走相对）。它出现时读数不是位移而是坐标 —— 日志上必须分开写，不能混进「位移」里。</summary>
    public bool LastRealAbsolute { get; private set; }

    private bool _capturing;
    private int _capturedTotal;
    private long _captureStartTick;
    private readonly List<string> _captured = new();
    private readonly Dictionary<IntPtr, string> _deviceNames = new();

    /// <summary>藏匿期开始：清账、开始逐条记录真输入。由 <c>SetCursorHidden(true)</c> 调。</summary>
    public void BeginCapture()
    {
        _capturing = true;
        _capturedTotal = 0;
        _captured.Clear();
        _captureStartTick = Environment.TickCount64;
    }

    /// <summary>藏匿期结束：停笔不撕账 —— 取样行在显示之后还要读最后一次。</summary>
    public void EndCapture() => _capturing = false;

    /// <summary>藏匿期记到的真输入短账（最新在末尾，超过 12 条丢最旧的）。空＝藏匿期没有真输入＝正常。</summary>
    public IReadOnlyList<string> CapturedReal => _captured;

    /// <summary>本段藏匿记到的真输入总条数（账被截到 12 条时它仍然说真话）。</summary>
    public int CapturedRealTotal => _capturedTotal;

    private void RecordRealDevice(IntPtr device, ushort flags, int x, int y)
    {
        if (!_deviceNames.TryGetValue(device, out var label))
        {
            label = ResolveDeviceName(device);
            _deviceNames[device] = label;
        }

        LastRealDevice = label;
        LastRealDx = x;
        LastRealDy = y;
        LastRealAbsolute = (flags & Native.MouseMoveAbsolute) != 0;

        if (!_capturing) return;

        _capturedTotal++;
        _captured.Add($"#{_capturedTotal} {(LastRealAbsolute ? "绝对" : string.Empty)}{x},{y} @{label}");
        if (_captured.Count > 12) _captured.RemoveAt(0);
    }

    /// <summary>问系统要设备的接口名，截出 VID/PID 段（形如 <c>VID_046D&amp;PID_C52B</c>，跟驱动包
    /// 对得上号）。问不出（合成句柄、设备已拔）就退回句柄十六进制，失败码一并写上 —— 十九报的
    /// 事故就是「问不出」却看不出为什么：LibraryImport 按字面名找 <c>GetRawInputDeviceInfo</c>
    /// 找不到（Raw Input 里只有它分 A/W 且无无后缀导出），EntryPointNotFoundException 被 catch
    /// 吞掉，十八报上线后所有设备一律 fallback。取名逻辑已挪进
    /// <see cref="EmbyNian.Shell.Interop.Native.TryGetRawInputDeviceName"/>，这里只管截段与兜底。</summary>
    private static string ResolveDeviceName(IntPtr device)
    {
        try
        {
            if (Native.TryGetRawInputDeviceName(device, out var name))
            {
                var at = name.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
                if (at >= 0) return name.Substring(at, Math.Min(name.Length - at, 24));
                if (name.Length > 0) return name;
            }

            return $"句柄 0x{device:X}(err {Marshal.GetLastWin32Error()})";
        }
        catch
        {
            // 拿不到就退回句柄，别让取证拖垮记账。
            return $"句柄 0x{device:X}";
        }
    }

    /// <summary>Parse 的固定缓冲。挂在类上而不是每次调用分配：这条路径鼠标一动就来上百次。</summary>
    [ThreadStatic]
    private static byte[]? _buffer;

    private static byte[] ThreadLocalBuffer => _buffer ??= new byte[64];
}
