using System.Threading;
using EmbyNian.Configuration;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 窗口和画面那三关：标题栏右端的 最小化／最大化／关闭 在不在手够得着的地方、有没有互相压着
/// （<c>ProbeWindowCommands</c>）；章节预览那只悬停框落在哪、里面有什么、跟着控制条一起走没有（<c>ProbePeek</c>）；
/// 画面比例算出来的那个数有没有真的传到窗口、归零之后锁有没有解开（<c>ProbeAspect</c>）。
/// <para>
/// 几何这一头借主文件末尾那几个共用的量具（<c>BoundsOf</c>／<c>Overlaps</c>／<c>Encloses</c>），半个像素的余量
/// 也在那儿说明。拆成几个文件的缘由见 <c>PlayerPage.SelfCheck.cs</c> 的类注释。
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>
    /// 最小化/最大化/关闭: that the three window commands are where a hand expects them and are not sitting on
    /// top of one another.
    /// <para>
    /// Playback takes the caption off the window, so these three are the only way back to a normal window
    /// short of the keyboard — and they are drawn by us, into a strip that also carries 返回, 统计 and 置顶.
    /// Nothing in a build or a unit test can see that one of them ended up outside the strip it is clipped
    /// by, or underneath the 置顶 toggle: both are a button that looks present and cannot be pressed.
    /// </para>
    /// <para>
    /// 全屏那一档也一样三颗都在，只是最右边那颗换了图标、也换了点下去做的事（全屏时它是「窗口化」）。
    /// 前一版正好相反 —— 全屏时把那一颗收起来，理由是「窗口边就是显示器的边，最大化什么也做不了」；
    /// 理由没错，可**那个位置空着看上去就是少了东西**，2026-09-14 用户报的正是这一句。
    /// </para>
    /// <para>
    /// The two glyph checks are the other half. <see cref="UpdateMaximizeGlyph"/> and
    /// <see cref="ToggleFullscreen"/> each write one <c>FontIcon</c>, they are adjacent in the same strip,
    /// and a crossed pair would leave the window's own state being reported by the wrong button — which no
    /// amount of compiling would notice.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeWindowCommands()
    {
        if (!Attached || _window is null) return (false, "播放层未接线");

        // 窗口改形那一路（ChangeWindowAsync）现在只在「在台上」才动手（stage5 加的守卫），而自检没有真实
        // 播放、_onStage 平时是假的 —— 与「置顶开关」那一关同一套：这里照实把它摆成在台上，量完放回。
        var wasOnStage = _onStage;
        _onStage = true;

        var was = Visibility;
        Visibility = Visibility.Visible;
        UpdateLayout();

        // Same two passes, for the same reason as ProbeTap: the first sizes the page, the second sizes the
        // strip that Render has just revealed. Without the second every button measures zero and every
        // geometry question below is answered about nothing.
        var clock = Now;
        _chrome.WakeFully(clock);
        Render();
        UpdateLayout();

        var strip = BoundsOf(TitleStrip);
        var trouble = new List<string>();

        foreach (var (name, button) in new (string Name, FrameworkElement Element)[]
                 {
                     ("最小化", MinimizeButton),
                     ("最大化", MaximizeButton),
                     ("关闭", CloseButton)
                 })
        {
            // 三颗**任何一档都在**，全屏也一样（2026-09-14 起）：右上角少一颗看上去就像少了东西，而全屏
            // 那一档的「还原」正是用户要的「窗口化」——鼠标移过去要退全屏的人，找的就是这个位置。
            if (button.Visibility != Visibility.Visible) trouble.Add($"{name}不见了");

            var box = BoundsOf(button);
            if (box.Width <= 0 || box.Height <= 0) trouble.Add($"{name}没有尺寸");
            else if (!Encloses(strip, box)) trouble.Add($"{name}越出了标题栏");
            else if (Overlaps(box, BoundsOf(PinButton)))
                trouble.Add($"{name}压住了别的按钮");
        }

        // The glyphs the two toggles write. Read after Render, which is what calls UpdateMaximizeGlyph.
        // 全屏和最大化答的是同一支「还原」：「还原」在这里是两件事共用的一个手势 —— 全屏时点下去退出全屏
        // （窗口回到进全屏前的大小），最大化时点下去还原窗口。判据只有一处，两支各写各的图标就会在这里红。
        var restore = _window.OccupiesScreen;

        if (MaximizeGlyph.Glyph != Glyph(restore ? RestoreGlyphCode : MaximizeGlyphCode))
            trouble.Add("最大化图标与窗口不符");

        if (FullscreenGlyph.Glyph != Glyph(_window.Fullscreen ? FullscreenExitCode : FullscreenEnterCode))
            trouble.Add("全屏图标与窗口不符");

        // 全屏那一档也要量：三颗仍旧都在，最右那颗是「窗口化」（还原图标）。这一档坏起来的样子是
        // **少一颗** —— 从前的实现正是把它收起来，而那一档不进一次全屏根本看不见（2026-09-14 用户报的
        // 就是「全屏的时候右上角的窗口化怎么没了」）。切进去、量完、切回原样，前后都排版一次。
        var wasFullscreen = _window.Fullscreen;

        SetFullscreen(true);
        DrainWindowChange();
        UpdateLayout();

        foreach (var (name, button) in new (string Name, FrameworkElement Element)[]
                 {
                     ("最小化", MinimizeButton),
                     ("最大化", MaximizeButton),
                     ("关闭", CloseButton)
                 })
        {
            if (button.Visibility != Visibility.Visible) trouble.Add($"全屏时{name}不见了");
        }

        // 全屏那一档的「还原」：图标必须是还原那支 —— 它同时是「点下去会退出全屏」这件事唯一的可见证据。
        if (MaximizeGlyph.Glyph != Glyph(RestoreGlyphCode)) trouble.Add("全屏时窗口化图标不对");

        // **大档的图标字号**（用户令 2026-09-28 更晚「跟独占模式一样，全屏的时候放大，窗口化的时候缩小」＋
        // 「包括进度条上方的按钮」）。这一档没有照片能拍：`--show-osd` 那条路不接受合成输入（见技能），
        // 所以它在自检里钉住 —— 判据是三颗的 `FontSize` 与 `ApplyWindowGlyphScale` 该摆的那一档相等。
        // 页面这一档的判据是 `BigChrome`（全屏**或最大化**），与时间轴的 `TimelineFullHeight` 同一个。
        var bigGlyphs = new (string Name, FontIcon Glyph)[]
        {
            ("最小化", MinimizeGlyph), ("最大化", MaximizeGlyph), ("关闭", CloseGlyph)
        };

        foreach (var (name, glyph) in bigGlyphs)
        {
            if (Math.Abs(glyph.FontSize - FullscreenCommandGlyph) > 0.01)
                trouble.Add($"全屏时{name}图标字号 {glyph.FontSize:0.###}（应 {FullscreenCommandGlyph}）");
        }

        SetFullscreen(wasFullscreen);
        DrainWindowChange();
        UpdateLayout();
        ApplyChromeScale();
        UpdateLayout();

        // **最大化那一档**：同一句话里的另一半 —— 大档不只是「全屏」，最大化也算（独占那头的
        // `state.scale` 写的就是 `fullormaxed`）。这一档既没有照片也没有别的关盯着，坏起来的样子是
        // 「最大化之后图标不跟着长大」。量完原样放回去。
        if (!_window.Fullscreen)
        {
            var wasMaximized = _window.IsMaximized;
            RequestMaximize(!wasMaximized);
            DrainWindowChange();
            UpdateLayout();

            var wanted = !wasMaximized ? FullscreenCommandGlyph : WindowCommandGlyph;
            var form = !wasMaximized ? "最大化" : "还原";

            foreach (var (name, glyph) in bigGlyphs)
            {
                if (Math.Abs(glyph.FontSize - wanted) > 0.01)
                    trouble.Add($"{form}时{name}图标字号 {glyph.FontSize:0.###}（应 {wanted}）");
            }

            RequestMaximize(wasMaximized);
            DrainWindowChange();
            UpdateLayout();
            ApplyChromeScale();
            UpdateLayout();
        }

        // Left the way a player that is not running should be, for the same reason ProbeReveal is.
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        Render();
        SetCursorHidden(false);
        Visibility = was;
        UpdateLayout();
        _onStage = wasOnStage;

        return (trouble.Count == 0,
            $"标题栏 {strip.Width:0}×{strip.Height:0} 逻辑像素，三个窗口命令"
            + (trouble.Count == 0 ? "都在栏内且互不重叠" : string.Join('、', trouble))
            + $"；右上角那颗={(_window.Fullscreen ? "窗口化" : restore ? "还原" : "最大化")}"
            + $"，窗口{(_window.IsMaximized ? "已最大化" : "未最大化")}"
            + $"，{(_window.Fullscreen ? "全屏" : "窗口化")}");
    }

    /// <summary>
    /// 把窗口切换那一趟异步走完再往下读。<c>ChangeWindowAsync</c> 自 2026-09-22 起会先冻住播放
    /// （<see cref="FreezeForHandoffAsync"/> 里 <c>await Task.Delay</c> 等 mpv 停住），而自检不真播放、那句
    /// <c>pause=yes</c> 落进空气 —— 于是那段 await 实打实占了约 200 毫秒，<c>SetFullscreen</c> 当拍不落地。
    /// 探针不等它，读到的还是切换前的状态（右上角图标、退全屏按比例整形都验不到，2026-09-23 闸门 4 两条回退
    /// 就是它）。照光标探针那一套泵消息、等 <see cref="WindowChange"/> 落地（<c>Pump</c> 见
    /// PlayerPage.SelfCheck.Cursor），给足冻结上限加抓帧兜底的余量。
    /// </summary>
    private void DrainWindowChange()
    {
        var until = Now + 2000;
        while (!WindowChange.IsCompleted && Now < until)
        {
            Pump();
            Thread.Sleep(16);
        }

        Pump();
    }

    /// <summary>
    /// 置顶开关: 屏上唯一能读出它状态的那一颗，三半都要量。
    /// <para>
    /// **2026-09-28 深夜第五批它换过一次对象**（用户令「把集成模式右上角的置顶图标换成跟独占模式一样
    /// 的」）：从前是两颗画出来的图钉（躺着那颗空心钉／立着那颗实心钉），现在是**一颗** —— 独占同一支字体
    /// （MaterialIconsRound）的 <c>push_pin</c>，几何是从那支字体里取的轮廓、尺寸照独占的实拍定
    /// （<c>WindowPinGlyph</c>）。崩它的方式于是也换了：几何被谁改坏、或者那一对实框没摆上去（<c>PathIcon</c>
    /// 既不缩放几何也不居中 —— <c>ShellPage</c> 那五颗图标的注释就是为这个坑写的），屏上都会是另一个东西，
    /// 而这一颗只在影片中途露面。
    /// </para>
    /// <para>
    /// **状态那一半 2026-09-29 又换了一次画法**（用户令「置顶不要长亮，改为非置顶的时候图标是斜的，置顶的
    /// 时候恢复原样」）：上一版「整颗常亮」（底换悬停那一档的白）同日撤下 —— 底从此与其余几颗同一套、只剩
    /// 悬停/按下，状态改画在**图钉的姿势**上：未置顶斜 <see cref="PlayerPage.PinTiltDegrees"/> 度、置顶立正
    /// （独占那头 <c>TopBar.lua</c> 的 EMBYNIAN[topbar-pin-tilt] 同一批，\frz −35 同一个方向）。两样都读得
    /// 出来（底是透明的、角度跟着档走），所以「拨了开关屏上没反应」照样拦得住 —— 而这一颗**本来就只有这么
    /// 两条线索**：屏上是那颗图钉，读屏软件那一头是 <c>PinIndicator</c> 的两句话，两边都得跟着状态走。
    /// </para>
    /// <para>
    /// **第三半是自动跟随**（同日另一条令「播放时自动置顶，暂停时自动取消置顶」）：边沿在
    /// <c>OnStatusApplied</c>，探针从 <see cref="PlayerViewModel.SetTransportProbeStatus"/> 喂合成状态 ——
    /// 那是真实播放走的同一条 ApplyStatus 路 —— 播放要立起、暂停要放斜，姿势与窗口置顶一起跟着走。
    /// 崩它的方式：守卫写歪（独占模式误把可浏览的主窗口按到顶上）、或者边沿只顾角标忘了置顶 —— 都是
    /// 编译与截图看不见的事。
    /// </para>
    /// <para>
    /// The reset at the end is not housekeeping. A probe that left the main window in the topmost band would
    /// make 「全屏几何」 further down the report pass for the wrong reason.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbePin()
    {
        if (!Attached || _window is null) return (false, "播放层未接线");

        var was = Visibility;
        var wasTop = _window.TopMost;

        Visibility = Visibility.Visible;
        UpdateLayout();

        var clock = Now;
        _chrome.WakeFully(clock);
        Render();
        UpdateLayout();

        var report = new List<string>();
        var wrong = new List<string>();
        var reads = new List<(bool Pinned, string Bed, double Angle, string Name, bool Top)>();

        foreach (var pinned in new[] { false, true })
        {
            SetPinned(pinned);
            UpdateLayout();

            reads.Add((pinned, Fill(PinButton), Tilt(PinGlyphBox), PeerName(PinButton), _window.TopMost));
        }

        // 底色那一支从调色板里现取（不写字面量）：2026-09-29 起两档都**不画底** —— 常亮那一档撤了，
        // 底只剩 XAML 写的 Transparent，与其余几颗同一套。
        var litBed = Tone(Resources["PlayerStripHoverBrush"] as Brush);
        var plainBed = Fill(MinimizeButton);

        report.Add($"底色：未置顶 {reads[0].Bed}、已置顶 {reads[1].Bed}（悬停那一档 {litBed}；"
            + $"没置顶的普通一颗 {plainBed}）");
        report.Add($"姿势：未置顶 {reads[0].Angle:0.#}°、已置顶 {reads[1].Angle:0.#}°（要 {PinTiltDegrees:0.#}° / 0°）");
        report.Add($"名字：「{reads[0].Name}」/「{reads[1].Name}」");
        report.Add($"窗口置顶：{reads[0].Top} / {reads[1].Top}");

        Want("未置顶那一档不画底色", reads[0].Bed is "不画" || reads[0].Bed.StartsWith("00", StringComparison.Ordinal));
        Want("已置顶那一档也不再画底（常亮同日撤下）", reads[1].Bed is "不画" || reads[1].Bed.StartsWith("00", StringComparison.Ordinal));
        Want("未置顶的图钉是斜的", Math.Abs(reads[0].Angle - PinTiltDegrees) < 0.01);
        Want("已置顶的图钉立正", Math.Abs(reads[1].Angle) < 0.01);
        Want("两档名字都不空", reads.All(read => read.Name.Trim().Length > 0));
        Want("两档名字不一样", !string.Equals(reads[0].Name, reads[1].Name, StringComparison.Ordinal));
        Want("状态跟着到了窗口", reads[0].Top == false && reads[1].Top);

        // 几何那一半：墨框得是**那颗字形**（装箱的 MaterialIconsRound，upem 512 里 push_pin 占 298 × 426 单位
        // —— 这一句挡的是「有人拿别的图钉或别的字号重画了一颗」），实框得是这一档那一对（PathIcon 只认自己
        // 那份 Data 与那对宽高，摆错一个数就是另一个大小）。
        var box = PinGlyph.Data?.Bounds ?? default;
        var want = BigChrome ? FullscreenPinGlyph : WindowPinGlyph;

        report.Add($"墨框 {box.Width:0.###}×{box.Height:0.###} 中心 {box.X + box.Width / 2:0.###},{box.Y + box.Height / 2:0.###}；"
            + $"实框 {PinGlyphBox.ActualWidth:0.###}×{PinGlyphBox.ActualHeight:0.###}（这一档要 {want.Width}×{want.Height}）");

        // 墨框那一对是**窗口档**的尺寸，而且要与实框同尺度，两个理由：
        // ① 它是从 MaterialIconsRound 的 push_pin 抠出来、**等比缩到 0~9.793 × 0~14** 的（不是字体的原始
        //    0~512 单位）—— PathIcon 不缩放几何，坐标比控件大，整颗就落在框外、屏上空白（2026-09-28 深夜
        //    第五批第一版的实测：白底上一个深色像素都没有；这一条就是为它钉的）。
        // ② 全屏档的 1.3 落在外面的 Viewbox 上，所以墨框**不跟着变**，跟着变的是实框。
        Want("图钉的墨框就是窗口档那一对（坐标与控件同尺度）",
            Math.Abs(box.Width - WindowPinGlyph.Width) < 0.02
            && Math.Abs(box.Height - WindowPinGlyph.Height) < 0.02);
        Want("图钉的实框是这一档那一对",
            Math.Abs(PinGlyphBox.ActualWidth - want.Width) < GeometrySlack
            && Math.Abs(PinGlyphBox.ActualHeight - want.Height) < GeometrySlack);
        Want("图钉的实框比可点的那一格小（40 的步进没被它撑开）",
            PinGlyphBox.ActualHeight < 40 - GeometrySlack);

        // 只报不判：拍照裁图要按这个框定位，而它跟着字体、缩放和这一排别的控件走。
        report.Add($"按钮 {BoundsOf(PinButton).Width:0}×{BoundsOf(PinButton).Height:0} @ {BoundsOf(PinButton).Left:0},{BoundsOf(PinButton).Top:0}");

        // 自动跟随那一半（用户令 2026-09-29「播放时自动置顶，暂停时自动取消置顶」）：把合成状态从
        // SetTransportProbeStatus 喂进来 —— 那是真实状态走的那条 ApplyStatus 路 —— 播放的边沿要立起图钉、
        // 暂停的边沿要放下。OnStatusApplied 那条守卫（在台上＋画面在宿主窗）探针也得照实摆上：_onStage
        // 只在 EnterPlayer 才立起，自检没有那一拍，这里手动摆上、退门放回；管线档/后端两枚设置按到集成
        // （机器翻到独占时这里会假红，见 ProbeForcePipeline），同样退门放回。记账（SavePinTopmost）不在
        // 这一条上：自动跟随是播放的状态，不是用户的偏好。喂完把原状态放回去，再把 loading 那一拍锁存
        // 掀掉 —— 回灌的存档多半是未加载，OnStatusApplied 会顺手把控制条按回「加载中钉住」，而 Reset
        // 不清这一位，不掀就漏给后面的 ProbeTap。
        var keepStatus = ViewModel.Status;
        var keepMarks = ViewModel.ChapterMarks;
        var keepPipeline = ViewModel.ProbeForcePipeline(VideoPipelineKind.Integrated);
        var keepBackend = ViewModel.ProbeForceBackend(MpvBackendKind.BuiltInLibMpv);
        var wasOnStage = _onStage;
        _onStage = true;
        // 起跳点摆干净：_paused 归 null（LeavePlayer 之后的那个值）、图钉放下 —— 下面两条边沿（开播立起、
        // 暂停放下）就都是真实发生的，不吃这一关之前任何探针留下的口味。
        _paused = null;
        SetPinned(false);
        try
        {
            ViewModel.SetTransportProbeStatus(new PlayerStatus { Duration = 1200, Position = 60, Loaded = true, Paused = false }, []);
            UpdateLayout();
            var playingTop = _window.TopMost;
            var playingAngle = Tilt(PinGlyphBox);
            var playingBed = Fill(PinButton);

            ViewModel.SetTransportProbeStatus(new PlayerStatus { Duration = 1200, Position = 90, Loaded = true, Paused = true }, []);
            UpdateLayout();
            var pausedTop = _window.TopMost;
            var pausedAngle = Tilt(PinGlyphBox);
            var pausedBed = Fill(PinButton);

            report.Add($"自动跟随：播放 置顶={playingTop}、姿势 {playingAngle:0.#}°、底 {playingBed}；"
                + $"暂停 置顶={pausedTop}、姿势 {pausedAngle:0.#}°、底 {pausedBed}");
            Want("播放中的边沿自动置顶（图钉立正）", playingTop && Math.Abs(playingAngle) < 0.01);
            Want("暂停的边沿自动取消置顶（图钉放斜）", !pausedTop && Math.Abs(pausedAngle - PinTiltDegrees) < 0.01);
            Want("自动跟随也不画底", (playingBed is "不画" || playingBed.StartsWith("00", StringComparison.Ordinal))
                && (pausedBed is "不画" || pausedBed.StartsWith("00", StringComparison.Ordinal)));
        }
        finally
        {
            ViewModel.SetTransportProbeStatus(keepStatus, keepMarks);
            ViewModel.ProbeForcePipeline(keepPipeline);
            ViewModel.ProbeForceBackend(keepBackend);
            _onStage = wasOnStage;
            _chrome.SetKeep(false, clock);
        }

        SetPinned(wasTop);
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        Render();
        SetCursorHidden(false);
        Visibility = was;
        UpdateLayout();

        Want("跑完复位了", _window.TopMost == wasTop);

        return (wrong.Count == 0,
            string.Join("；", report) + (wrong.Count == 0 ? string.Empty : $"；不符：{string.Join('、', wrong)}"));

        void Want(string what, bool ok)
        {
            if (!ok) wrong.Add(what);
        }

        // 模板根上那一层的底色 —— Checked 那一族当年换的就是它。ContentPresenter 不是 Control（那一条第一趟
        // 读回来是「找不到模板根」），所以四种带 Background 的类型都要认。
        static string Fill(DependencyObject button)
        {
            if (VisualTreeHelper.GetChildrenCount(button) == 0) return "找不到模板根";

            return Tone(VisualTreeHelper.GetChild(button, 0) switch
            {
                ContentPresenter presenter => presenter.Background,
                Control control => control.Background,
                Panel panel => panel.Background,
                Border border => border.Background,
                _ => null
            });
        }

        static string PeerName(UIElement element) =>
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer
                .CreatePeerForElement(element)?.GetName() ?? "";

        // 图钉此刻的姿势（2026-09-29 状态的那一半）：SetPinned 摆在 PinGlyphBox 的那个角度。-transform 不在
        // （或不是 RotateTransform）就是「斜着」那一档被谁拆了 —— 报 −1 让这一关红，而不是静悄悄当成立正。
        static double Tilt(FrameworkElement element) =>
            element.RenderTransform is Microsoft.UI.Xaml.Media.RotateTransform tilt ? tilt.Angle : -1;
    }

    /// <summary>
    /// 一支画刷的色号，写成报告与判据共用的那一个串：<c>AARRGGBB</c>（大写十六进制），不画就是「不画」。
    /// <para>
    /// 置顶那一关要比两支底（悬停那一档的白、常态的透明 —— 2026-09-29 起常亮那一档撤了，两档都要「不画」）。
    /// 报告里印的是读出来的数，而拿来对的期望值**取自同一个资源字典**（<c>Resources["PlayerStripHoverBrush"]</c>
    /// 那几支）—— 不写字面量，于是「有人把某一支换回了框架默认」拦得住，调色板自己改浓度也不会让这一关假红。
    /// </para>
    /// </summary>
    private static string Tone(Brush? brush) => brush switch
    {
        null => "不画",
        SolidColorBrush solid => $"{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
        _ => "不是纯色"
    };

    /// <summary>
    /// 章节预览: where the hover box lands, what it contains, and that it leaves with the bar it belongs to.
    /// <para>
    /// Four separate things have been wrong here and none of them is visible to a compiler. The box is
    /// placed by a <c>TranslateTransform</c> this page computes rather than by layout, so an off-by-a-box-width
    /// sends it off the side of the picture — and the pointer is at the far end of the seek bar exactly when
    /// that happens, which is the one place a user will notice and the one place a centred test would not.
    /// The second is 视频进度条预览不会自动消失: the preview outliving its bar, stranded over the film with
    /// nothing to explain it. The third is 预览没有画面 — a fixed-size <c>Image</c> holding no source, which
    /// is what every file looks like on a server that never extracted chapter stills. The fourth is the
    /// floor under that one: no chapter marks at all, where the box is down to its time readout and an
    /// empty caption would leave a name-shaped gap above it.
    /// </para>
    /// <para>
    /// Three positions rather than one, and the two ends are the point: the middle of the bar cannot be
    /// clamped wrongly because it needs no clamping.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbePeek()
    {
        if (!Attached) return (false, "播放层未接线");

        var was = Visibility;
        Visibility = Visibility.Visible;
        UpdateLayout();

        var clock = Now;
        _chrome.WakeFully(clock);
        Render();
        UpdateLayout();

        // Shown before it is placed, and laid out in between: the clamp reads the box's own measured width,
        // and a collapsed Border measures nothing at all.
        //
        // Filled first, and with a still: the box is at its widest with a picture in it, which is the case
        // the clamp has to survive. An empty BitmapImage is enough — what is being proved is that a source
        // reaches the Image and reveals it, not that any particular JPEG decodes.
        var caption = ViewModel.ChapterCaption;
        ViewModel.ChapterCaption = "片头";
        ViewModel.ChapterClock = "00:02:15";
        ViewModel.ChapterStill = new BitmapImage();

        ChapterPeek.Visibility = Visibility.Visible;
        UpdateLayout();

        var framed = ChapterImage.Visibility == Visibility.Visible;
        var withStill = BoundsOf(ChapterPeek);

        var track = SeekTrack.ActualWidth;
        var picture = BoundsOf(Root);
        var strayed = new List<string>();

        foreach (var (name, x) in new (string Name, double X)[] { ("左端", 0), ("中间", track / 2), ("右端", track) })
        {
            PositionChapterPeek(x);
            UpdateLayout();

            var box = BoundsOf(ChapterPeek);
            if (box.Width <= 0 || box.Height <= 0) strayed.Add($"{name}没有尺寸");
            else if (!Encloses(picture, box)) strayed.Add($"{name}越出画面");
        }

        // 预览没有画面: a server that extracted no chapter images has none to send — every ChapterInfo then
        // arrives without an ImageTag and nothing is even fetched — and a fixed 212×119 Image with no Source
        // is an empty frame rather than nothing at all. The picture has to collapse and leave the two text
        // lines, which is a shrinking box: the same measurement that proves the frame is gone.
        ViewModel.ChapterStill = null;
        UpdateLayout();

        var textOnly = BoundsOf(ChapterPeek);
        var hollow = ChapterImage.Visibility == Visibility.Collapsed
                     && textOnly.Height > 0
                     && textOnly.Height < withStill.Height - 100;

        // And the floor under that: a file the server extracted no chapter marks from at all, which is a
        // whole class of server rather than an edge case. No picture and no name leaves the time on its
        // own — and it has to be left, because the slider's own tooltip only shows while the thumb is
        // being dragged, so hovering such a file used to produce nothing whatsoever. The empty caption
        // has to collapse rather than hold an empty line, or the chip is a name-shaped gap over a clock.
        ViewModel.ChapterCaption = "";
        UpdateLayout();

        var clockOnly = BoundsOf(ChapterPeek);
        var chip = ChapterCaption.Visibility == Visibility.Collapsed
                   && ChapterClock.Visibility == Visibility.Visible
                   && clockOnly.Height > 0
                   && clockOnly.Height < textOnly.Height - 8;

        ViewModel.ChapterCaption = "片头";
        UpdateLayout();

        // With the bar up the preview is the pointer's own business and must be left alone.
        Render();
        var kept = ChapterPeek.Visibility == Visibility.Visible;

        // The bar going takes it: this is the half that a PointerExited over the track never raises.
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        Render();
        var gone = ChapterPeek.Visibility == Visibility.Collapsed;

        HideChapterPeek();
        SetCursorHidden(false);
        ViewModel.ChapterCaption = caption;
        Visibility = was;
        UpdateLayout();

        var ok = track > 0 && strayed.Count == 0 && framed && hollow && chip && kept && gone;

        return (ok, $"进度条 {track:0} 逻辑像素宽，预览三处定位"
                    + (strayed.Count == 0 ? "都在画面内" : string.Join('、', strayed))
                    + $"；有缩略图时{(framed ? $"显示画面（{withStill.Height:0} 高）" : "没有显示画面")}"
                    + $"，没有缩略图时{(hollow ? $"只剩文字（{textOnly.Height:0} 高）" : "仍留着空画框")}"
                    + $"，连章节名也没有时{(chip ? $"只剩时间（{clockOnly.Height:0} 高）" : "仍留着空行")}"
                    + $"；浮层在时{(kept ? "保留" : "被收走")}"
                    + $"，浮层收起后{(gone ? "随之消失" : "仍然停留")}");
    }

    /// <summary>
    /// 画面比例: that the ratio the view model resolved actually reaches the window, that zero releases it, and
    /// that letting go puts the window back the size the user had.
    /// <para>
    /// <c>AspectLock</c> works out the rectangle and is unit-tested doing it; what has no other test is the
    /// one hop in between — the view model's event, this page's handler, and the window's property. A
    /// handler that computed the right number and wrote it nowhere would leave 窗口化时视频有黑边 exactly as
    /// it was, and a build would be perfectly happy.
    /// </para>
    /// <para>
    /// Zero is the half worth insisting on. It means 「stop keeping」, and a window still locked to the
    /// shape of a film that finished half an hour ago cannot be dragged into any other shape at all.
    /// </para>
    /// <para>
    /// 第三半是 2026-09-14 用户报的那一条：「进入播放页面然后再退出页面会保留播放页面的窗口大小比例」。
    /// 解锁只说明「能拉了」，说明不了「已经回去了」—— 从前窗口会一直留着片子整出来的那个形状（日志里一次
    /// 2.413:1 的宽银幕把窗口留成 1463×608）。这一段把整条路量出来：按画面比例整过之后让窗口回到播放前那一份
    /// （<see cref="HostWindow.RestoreBrowseGeometry"/>，页面在退出播放时调的就是它），窗口必须与整形前逐像素一致。
    /// </para>
    /// <para>
    /// 第四半是同一场事故的另一个入口：「窗口模式下调整窗口大小上下会出现黑边」。修的是「退全屏之后没按当前
    /// 画面比例再整形一次」—— 自动全屏开播后 40 毫秒就进去，mpv 半秒后才给出真比例，那句修正落在全屏里被丢掉，
    /// 退出全屏又把按猜错比例整好的矩形放了回来。这里把那趟走一遍（进全屏 → 退全屏），要求退出来之后客户区
    /// <b>真的是那个比例</b> —— 判据落在比例上而不是落在「有没有调 FitToPicture」上，因为要的正是屏幕上的结果。
    /// </para>
    /// <para>
    /// 第五半是 2026-09-18 用户报的那一条的自动全屏变体：播放停止时比例先归零，退出播放才退出全屏，退出全屏
    /// 那一下的 WM_SIZE 到来时「全屏已了、比例已零」，窗口若没有「播放占着窗口不记几何」的守卫（
    /// <see cref="HostWindow.RememberPlacement"/> 的 FreeSizing 一条），就会把退出全屏回到的播放形状记成浏览
    /// 几何 —— 紧跟着的还原看见矩形没变就静默收工，窗口从此留在片子的形状上（实录：0.0.14 的 14:56 场，
    /// 停在 1511×626，还原日志一行都没有）。这里把那趟原样重演：占窗 → 记账 → 按画面比例整形 → 全屏 → 归零
    /// → 退全屏 → 还窗，终点必须回到记账时那一份。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeAspect()
    {
        if (!Attached || _window is null) return (false, "播放层未接线");
        if (ViewModel.PlayingNow) return (false, "比例探针不能在真实播放期间运行");

        var restore = _window.PictureAspect;

        // 整形之前那个矩形。第三半要比的就是它。
        if (_window.Handle == IntPtr.Zero) return (false, "窗口还没建");

        var haveOriginal = Native.GetWindowRect(_window.Handle, out var was);
        if (!haveOriginal) return (false, "读不到窗口矩形");

        var wasFullscreen = _window.Fullscreen;

        if (!ViewModel.PictureInHostWindow)
        {
            OnPictureAspectChanged(16d / 9d);
            OnPictureAspectChanged(0);
            SetFullscreen(true);
            SetFullscreen(false);
            var haveNativeNow = Native.GetWindowRect(_window.Handle, out var nativeNow);
            var unchanged = haveNativeNow
                && nativeNow.Left == was.Left && nativeNow.Top == was.Top
                && nativeNow.Width == was.Width && nativeNow.Height == was.Height
                && _window.PictureAspect == restore && _window.Fullscreen == wasFullscreen;
            return (unchanged, "原生管线：比例与全屏请求不改变 WinUI 控制窗口；"
                + (unchanged ? "几何和状态保持不变" : "控制窗口被误改"));
        }

        // 窗口改形那一路（ChangeWindowAsync／FitToPicture）现在只在「在台上」才动手（stage5 加的守卫），
        // 自检没有真实播放、_onStage 平时是假的 —— 与「置顶开关」同一套：这一支开始前摆成在台上，末尾放回。
        var wasOnStage = _onStage;
        _onStage = true;

        // Through the page's own handler rather than by writing the property: the wiring is the subject.
        OnPictureAspectChanged(16d / 9d);
        var took = Math.Abs(_window.PictureAspect - 16d / 9d) < 0.001;

        string fitted;
        var shaped = true;

        if (_window.OccupiesScreen)
        {
            // Both mean the edges belong to the monitor and FitToPicture stands aside, so asking about the
            // client shape here would be asking about the screen's. 两种形态同一个答主、同一句话报，
            // 所以这里的措辞也不必再分两支（归一那一句见 OccupiesScreen）。
            fitted = $"{WindowForms.Name(_window.Form)}，不整形";
        }
        else
        {
            var size = _window.ClientSize;
            var wanted = (int)Math.Round(size.Width / (16d / 9d));
            var ratio = size.Height > 0 ? (double)size.Width / size.Height : 0;

            // One pixel, not a loose tolerance. Every clamp inside AspectLock.Fit preserves the ratio —
            // the minimum height widens rather than shortens, and a work-area cut recomputes the other
            // dimension — so a client that is not 16:9 to the pixel means the shape was got wrong, which
            // is exactly the black band this fit exists to remove.
            shaped = Math.Abs(size.Height - wanted) <= 1;
            fitted = $"客户区 {size.Width}×{size.Height} = {ratio:0.000}"
                + (shaped ? string.Empty : $"，应为 {size.Width}×{wanted}");
        }

        // 第四半。先故意把窗口摆成一个**和画面不一致**的形状（16:9 的窗口放 4:3 的画面），再走一趟全屏往返 ——
        // 进全屏时窗口被迫变成显示器的形状，出来时若没人按画面比例补一次，就会留下全屏前那个 16:9。
        //
        // 摆的是「窗口比画面宽」，所以退全屏之后若没整形，客户区会明显不是 4:3，这一关当场红。
        _window.PictureAspect = 4d / 3d;

        SetFullscreen(true);
        DrainWindowChange();
        UpdateLayout();
        SetFullscreen(false);
        DrainWindowChange();
        UpdateLayout();

        var afterSize = _window.ClientSize;
        var afterRatio = afterSize.Height > 0 ? (double)afterSize.Width / afterSize.Height : 0;
        var refitted = !_window.Fullscreen
            && Math.Abs(afterSize.Width - Math.Round(afterSize.Height * 4d / 3d)) <= 1;

        // 第三半要比的那一份，得先摆回探针进来时那个矩形再记 —— 上面那一趟全屏往返把窗口挪走了，而下面
        // RestoreBrowseGeometry 要还原到的正是**播放前**那一份。这里把窗口摆回去、再让窗口自己重记一次，
        // 于是「整形前 <-> 还原后」这两头量的是同一个矩形，这一段才是它声称在量的东西。
        if (haveOriginal)
        {
            Native.SetWindowPos(
                _window.Handle, Native.HwndTop,
                was.Left, was.Top, was.Width, was.Height,
                Native.SwpNoZOrder | Native.SwpNoActivate);
        }

        OnPictureAspectChanged(0);
        var cleared = _window.PictureAspect == 0;
        _window.CaptureBrowseGeometry();

        // 退出播放那一趟。窗口自己记着整形之前那份几何（<c>HostWindow.PictureAspect</c> 的 setter 里在写下一个
        // 非零比例时记的），这里只把还原走一遍。自检不真播片子，所以驱动的是页面在 <c>LeavePlayer</c> 里用的同一
        // 条路 —— 少了这一步，「解锁」会全绿，而屏幕上窗口的形状还一直是片子的。
        _window.RestoreBrowseGeometry();

        var haveNow = Native.GetWindowRect(_window.Handle, out var now);
        var back = haveNow
            && now.Left == was.Left && now.Top == was.Top
            && now.Width == was.Width && now.Height == was.Height;

        var put = haveNow ? $"{now.Width}×{now.Height} @ {now.Left},{now.Top}" : "读不到";

        // 第五半。此刻窗口在 was、比例已归零、FreeSizing 是 false —— 正好是自动全屏那场退出路的起点。
        // FreeSizing 先立起来（播放占窗），按画面比例整出播放形状（比例写入方顺手把 was 记成浏览几何），
        // 走一趟全屏；归零放在退全屏**之前**（播放停止先于退出播放，次序是这次事故的钥匙），退全屏那一下
        // 的 WM_SIZE 若没有 FreeSizing 守卫就会把播放形状写进浏览几何 —— 还原看见矩形没变，静默收工。
        var freeWas = _window.FreeSizing;
        _window.FreeSizing = true;

        OnPictureAspectChanged(2.413);
        var haveShaped = Native.GetWindowRect(_window.Handle, out var shapedRect);

        SetFullscreen(true);
        DrainWindowChange();
        OnPictureAspectChanged(0);
        SetFullscreen(false);
        DrainWindowChange();

        _window.FreeSizing = false;
        _window.RestoreBrowseGeometry();

        var haveAfter = Native.GetWindowRect(_window.Handle, out var afterReplay);
        var replay = haveShaped && haveAfter
            && afterReplay.Left == was.Left && afterReplay.Top == was.Top
            && afterReplay.Width == was.Width && afterReplay.Height == was.Height;

        _window.FreeSizing = freeWas;

        if (_window.Fullscreen != wasFullscreen) SetFullscreen(wasFullscreen);

        _window.PictureAspect = restore;
        _onStage = wasOnStage;

        return (took && shaped && refitted && cleared && back && replay,
            $"16:9 {(took ? "已交给窗口" : "没有传到窗口")}；{fitted}；"
            + $"全屏往返后（画面 4:3）客户区 {afterSize.Width}×{afterSize.Height} = {afterRatio:0.000}"
            + (refitted ? "，已按画面比例补整" : "，不是 4:3 —— 退全屏后没按当前画面比例再整形一次") + "；"
            + $"归零{(cleared ? "已解除" : "未解除")}；"
            + $"退出播放后窗口 {put}"
            + (back ? "，与整形前一致" : $"，整形前是 {was.Width}×{was.Height} @ {was.Left},{was.Top} —— 没还原") + "；"
            + $"自动全屏场重演（整形→全屏→归零→退全屏→还原）"
            + (replay ? "回到记账时的矩形"
                : $"停在 {(haveAfter ? $"{afterReplay.Width}×{afterReplay.Height} @ {afterReplay.Left},{afterReplay.Top}" : "读不到")}"
                  + $"（播放形状 {shapedRect.Width}×{shapedRect.Height}），应回到 {was.Width}×{was.Height} @ {was.Left},{was.Top}"
                  + " —— 播放形状被记成了浏览几何"));
    }
}
