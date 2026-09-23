using EmbyNian.Mpv;
using EmbyNian.Playback;
using Microsoft.UI.Xaml;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 画面上那两样自己画出来的东西：底边那条细进度线（<c>ProbeThinLine</c>）和 统计 那块浮层（<c>ProbeStats</c>）。
/// <para>
/// 两样都是代码画的、都只在真开着一部片子的时候第一次出现，所以编译碰不到它们，单元测试也只能碰到算数那一半。
/// 拆成几个文件的缘由见 <c>PlayerPage.SelfCheck.cs</c> 的类注释。
/// </para>
/// <para>
/// 2026-09-22 之后 <c>ProbeStats</c> 验的东西变了：统计面板改由 mpv 画（<see cref="MpvStats"/>），这一页
/// 只剩「装箱在、按钮与标志位同步」可验 —— 面板本身不在这棵树上了。
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>
    /// 底边细进度线: on screen when the transport bar is down, and only in a window.
    /// <para>
    /// The one piece of chrome whose rule is the inverse of all the rest, which is why the reveal rule
    /// cannot state it — and 「全屏时最下方会有进度条」 was the half of it nobody had said out loud. Full screen
    /// those two bright pixels run the whole width of the monitor with no window edge to belong to: a
    /// progress bar standing over the film, rather than a readout along the edge of a window that has edges
    /// anyway. Driven through the real window, because 「窗口化」 is a question only the window can answer.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeThinLine()
    {
        if (!Attached || _window is null) return (false, "播放层未接线");
        if (_window.Fullscreen) return (false, "窗口已在全屏，无从对比");

        var was = Visibility;
        Visibility = Visibility.Visible;
        UpdateLayout();

        var clock = Now;
        var report = new List<string>();
        var wrong = new List<string>();

        // The state the line belongs to: the pointer in the dead middle of the picture, and long enough
        // since it moved that nothing else is on screen. Re-driven after each window change, because the
        // fullscreen transition reseeds the rule from wherever the real cursor happens to be resting.
        void Settle()
        {
            var height = Root.ActualHeight > 0 ? Root.ActualHeight : 900;
            _chrome.Pointer(height / 2, height, ChromePart.None, railNear: -1, ++clock);
            clock += SettleMilliseconds;
            _chrome.Tick(clock);
            Render();
        }

        void Sample(string what, bool wanted)
        {
            var shown = ThinLine.Visibility == Visibility.Visible;
            report.Add($"{what}→{(shown ? "有细线" : "没有")}");
            if (shown != wanted) wrong.Add(what);
        }

        Settle();
        Sample("窗口化时浮层收起", wanted: true);

        try
        {
            _window.Fullscreen = true;
            Settle();
            Sample("全屏时浮层收起", wanted: false);

            // And with the bar back up, where the transport bar covers those two pixels itself.
            _chrome.WakeFully(++clock);
            Render();
            Sample("全屏时浮层展开", wanted: false);
        }
        finally
        {
            _window.Fullscreen = false;
        }

        Settle();
        Sample("退出全屏后", wanted: true);

        // 退场的后半段：细线必须已经收了。2026-09-20 用户截图里那条横贯底边的白线就是它在退场那 220ms 里
        // 一直亮着 —— 页面在淡出，它不动，于是页面淡到零的那一拍屏上还剩下它。_inputSuspended 由退场第 0 拍
        // 立起、落定拍撤下，这里只借它把 Render 逼到「退场中」那一档，读完立刻还原。
        var suspended = _inputSuspended;
        _inputSuspended = true;
        Settle();
        Sample("退场中浮层收起", wanted: false);
        _inputSuspended = suspended;

        Settle();
        Sample("退场读数还原后", wanted: true);

        // 拖动标题移动窗口那一趟（2026-09-22，用户令「拖动过程中不要显示进度条和音量条」）：两根条都不画，
        // 细线也就没有「浮层收起」那一段可站 —— 它补的正是「进度条收起时仍留一条读数」，而拖动期间连读数
        // 都不要。走页面自己的 Hold，接线断了这一关才红。
        Hold(true, ChromeHold.Drag);
        Settle();
        Sample("拖动窗口时", wanted: false);

        Hold(false, ChromeHold.Drag);
        Settle();
        Sample("拖动结束后", wanted: true);

        // Left the way a player that is not running should be, for the same reason ProbeReveal is — the page
        // put back first, so the last Render leaves the line down rather than across the library grid.
        Visibility = was;
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        Render();
        SetCursorHidden(false);
        UpdateLayout();

        return (wrong.Count == 0,
            string.Join("；", report) + (wrong.Count == 0 ? string.Empty : $"；不符：{string.Join('、', wrong)}"));
    }

    /// <summary>只验证装箱与命令接线；Lua 的三态、清屏与计时读数另由离线探针验证。</summary>
    internal (bool Ok, string Detail) ProbeStats()
    {
        if (!Attached) return (false, "播放层未接线");

        var boxed = MpvStats.Exists(AppContext.BaseDirectory);

        var wired = ReferenceEquals(StatsButton.Command, ViewModel.CycleStatsCommand)
            && ViewModel.CycleStatsCommand.CanExecute(null);

        var keys = string.Join("/", MpvStats.Keys().Select(pair => pair.Key));

        return (boxed && wired,
            $"统计脚本 {MpvStats.ScriptRelativePath}：{(boxed ? "已装箱" : "缺失")}"
            + $"；三态按钮命令接线={wired}；独占模式键位 {keys}；实际显示另验");
    }
}
