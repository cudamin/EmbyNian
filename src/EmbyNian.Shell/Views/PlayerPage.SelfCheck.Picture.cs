using EmbyNian.Playback;
using Microsoft.UI.Xaml;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 画面上那两样自己画出来的东西：底边那条细进度线（<c>ProbeThinLine</c>）和 统计 那块浮层（<c>ProbeStats</c>）。
/// <para>
/// 两样都是代码画的、都只在真开着一部片子的时候第一次出现，所以编译碰不到它们，单元测试也只能碰到算数那一半。
/// 拆成几个文件的缘由见 <c>PlayerPage.SelfCheck.cs</c> 的类注释。
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

    /// <summary>
    /// Opens the 统计 panel, fills it through the real formatting path, and puts it away again.
    /// <para>
    /// <see cref="PlaybackStats"/> is unit-tested on its own; what cannot be tested there is this page's
    /// half — that the toggle, the panel and <see cref="PlayerViewModel.StatsOpen"/> are wired to each
    /// other rather than each to itself, that the two brush keys <see cref="RenderStatRows"/> asks for
    /// resolve in this page's own resource scope, and that the grid it draws into really has the second
    /// column the values go in. Every one of those is a crash or a blank panel the first time a user
    /// presses 统计, and none of them shows up in a build.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeStats()
    {
        if (!Attached) return (false, "播放层未接线");

        // A partial reading set on purpose: mpv answers nothing at all for a property that does not apply
        // to the file, so the omissions are the interesting half. No audio-params here, which is the
        // silent-file case, and the panel should come out with no 声道与采样率 row rather than a blank one.
        var readings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["width"] = "1920",
            ["height"] = "1080",
            ["video-codec"] = "h264 (High)",
            ["audio-codec-name"] = "aac",
            ["hwdec-current"] = "d3d11va-copy",
            ["current-vo"] = "gpu-next",
            ["video-bitrate"] = "4200000",
            ["avsync"] = "-0.002",
            ["container-fps"] = "23.976"
        };

        // Through the bound property rather than through a method of its own: the button, the panel and the
        // view model share one flag now, and driving that flag is what proves the three agree.
        ViewModel.StatsOpen = true;
        var opened = StatsPanel.Visibility == Visibility.Visible && (StatsButton.IsChecked ?? false);

        var rows = PlaybackStats.Format(readings);
        RenderStatRows(rows);
        var drawn = StatsRows.Children.Count;
        var lines = StatsRows.RowDefinitions.Count;

        // The empty case has its own row, and it is the one a user actually sees first.
        RenderStatRows([]);
        var waiting = StatsRows.Children.Count == 1;

        ViewModel.StatsOpen = false;
        var closed = StatsPanel.Visibility == Visibility.Collapsed
                     && !(StatsButton.IsChecked ?? false)
                     && StatsRows.Children.Count == 0;

        // Two children per row — the label and its value — is what proves both columns were reached.
        var ok = opened && closed && waiting && rows.Count > 0 && drawn == rows.Count * 2 && lines == rows.Count;

        return (ok, $"{PlaybackStats.Fields.Count} 个属性，{rows.Count} 行 → {drawn} 个文本块／{lines} 行高"
                    + $"；开={opened}，空态占位={waiting}，关={closed}");
    }
}
