namespace EmbyNian.Mpv;

/// <summary>
/// 视频窗「要一份菜单」请求的闸门：一小段窗口里只放一条过去。选集菜单与版本菜单各持一道。
/// <para>
/// 存在的理由是一次实测事故（2026-09-19）：uosc 那边一个脚本绑定与一条宿主消息撞了名字，脚本自激，
/// 宿主在 90 秒里收到 32 万条请求、逐条回推 open-menu —— 界面卡到点不动、日志一天涨 7MB。根因在
/// 脚本侧（已修：绑定名独立成 <c>embynian-ui-…</c>），但宿主不该被一段坏脚本拖死：这几份菜单都是幂等的
/// （同一份列表，再来一次只是重画一遍），所以这里只留最快 <see cref="DefaultInterval"/> 一条。
/// </para>
/// <para>
/// 有意不做成静默丢弃：被挡下的条数记在 <see cref="Suppressed"/> 里，<see cref="ShouldReport"/> 给
/// 调用方一个「该记日志了」的信号（第一次挡下立刻响，之后最多每秒一条，免得日志自己也成刷屏）。
/// 否则下次真出问题只剩一句「点了没反应」，而这一次是有物证的。时钟由调用方传入，所以这条规矩不依赖
/// <c>DateTime.Now</c>，没有窗口也能原样钉死。
/// </para>
/// </summary>
public sealed class MenuRequestGate
{
    /// <summary>
    /// 最短间隔。它挡的是自激，不是人：真实点击的最快双击也在 100ms 以上，400ms 不会吃掉任何一下。
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>「被挡下了」的日志最短间隔 —— 刷屏时日志按秒记，不按条记。</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _interval;
    private DateTime _lastAccepted = DateTime.MinValue;
    private DateTime _lastReported = DateTime.MinValue;

    /// <param name="interval">放行间隔，默认 <see cref="DefaultInterval"/>；测试用它把窗口压到可数的尺度。</param>
    public MenuRequestGate(TimeSpan? interval = null) => _interval = interval ?? DefaultInterval;

    /// <summary>累计被挡下的条数 —— 「对面在刷屏」的物证，跟着日志一起说出去。</summary>
    public int Suppressed { get; private set; }

    /// <summary>这一条请求放不放过去。放过去的同时把窗口起点推到现在。</summary>
    public bool TryAccept(DateTime now)
    {
        if (_lastAccepted != DateTime.MinValue && now - _lastAccepted < _interval)
        {
            Suppressed++;
            return false;
        }

        _lastAccepted = now;
        return true;
    }

    /// <summary>被挡下的这一条该不该记日志：第一次立刻响，之后每 <see cref="ReportInterval"/> 最多一条。</summary>
    public bool ShouldReport(DateTime now)
    {
        if (_lastReported != DateTime.MinValue && now - _lastReported < ReportInterval) return false;

        _lastReported = now;
        return true;
    }
}
