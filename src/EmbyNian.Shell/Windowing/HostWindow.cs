using System.Linq;
using System.Runtime.InteropServices;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.UI;

namespace EmbyNian.Shell.Windowing;

/// <summary>
/// 键盘兜底的接键人（2026-09-15「新增esc退出全屏 按空格开始播放」）。播放页实现它，窗口的线程级
/// WH_KEYBOARD 钩子在「键已进本线程队列、Win32 焦点却不在 XAML 岛里」时把空格和 Esc 送过来 ——
/// 那是 XAML 两条键路（页面 KeyDown、Root 上的空格拦截）都听不见的死角。
/// <para>
/// <see cref="WantsKey"/> 是「这一下你接不接」：菜单开着、正在打字时页面让路；<see cref="Handle"/>
/// 是「接」，页面上仍走它自己的 Dispatch，兜底路和岛内路永远做同一件事。
/// </para>
/// </summary>
internal interface IWin32KeySink
{
    /// <summary>这一下接不接。真＝钩子把它吃掉；假＝钩子放行。</summary>
    bool WantsKey(int virtualKey);

    /// <summary>接。等价于那颗键在岛内被按下时页面会做的事。</summary>
    void Handle(int virtualKey);
}

/// <summary>
/// The application window. It is our own <c>CreateWindowEx</c> HWND rather than a
/// <see cref="Microsoft.UI.Xaml.Window"/>, and all of the UI lives in a WinUI 3 XAML island
/// (<see cref="DesktopWindowXamlSource"/>) filling its client area.
/// <para>
/// That choice was measured, not assumed. Four facts decided it:
/// </para>
/// <list type="number">
///   <item>An island over a sibling child HWND composites correctly on 1.8: opaque XAML paints over
///   the child, unpainted XAML reveals it, and a semi-transparent brush alpha-blends against it
///   exactly. The island's transparency is what a browsing page's opaque background rides on, and
///   the video rides one of two pipelines (2026-09-16, 小幻影视同款的两档): 集成模式 composites it
///   into the tree — a SwapChainPanel fed by mpv's D3D11 composition swapchain, no render API and
///   no ANGLE — while 独立播放 hands a child HWND to mpv as <c>wid</c> and lets it own the
///   swapchain. The second one lives on exactly this fact.</item>
///   <item>A real <see cref="Microsoft.UI.Xaml.Window"/> hides child HWNDs. Its backdrop is
///   composited <em>into</em> the island surface, so that island can never be transparent and the
///   video never appears. This is why the window has to be ours.</item>
///   <item>Client-area Mica and hosting mpv as a child HWND are mutually exclusive for the same
///   reason. Mica on the frame is free; Mica behind the client area costs the video. Hence
///   <see cref="UseBackdrop"/> — on while browsing, off the moment video is on screen.</item>
///   <item>Anything nobody paints comes back white. So the window fills its client area with the
///   app's base colour on <c>WM_ERASEBKGND</c>, the island root stays unpainted, and every page
///   paints its own full-bleed opaque background.</item>
/// </list>
/// </summary>
internal sealed class HostWindow : IDisposable
{
    private const string Category = "窗口";
    private const string ClassName = "EmbyNianHost";

    /// <summary>Matches <c>Palette.Window</c> (#16181C) as a COLORREF, which is 0x00BBGGRR.</summary>
    private const uint BaseColorRef = 0x001C1816;

    private const int DefaultHeight = 800;

    /// <summary>
    /// 客户区最窄能到多少。900 是多年来的旧数；详情页换了紧凑版式（<c>DetailHero.CompactFloor</c> = 1024，
    /// 参考手机版式的那一档）之后，窗口得能拖进那条线底下它才看得见，所以放到 600 —— 比那条线低出一大截，
    /// 紧凑版式在最窄的那一段（600 到 700）也是完整的单列版式，而不是一换过去就顶着最小宽。
    /// </summary>
    private const int MinimumWidth = 600;

    private const int MinimumHeight = 560;

    /// <summary>
    /// 开窗那一档的宽：页面 16:9（<see cref="HomeCarousel.WindowAspect"/>），开窗的形状。主页那条带照
    /// <see cref="HomeCarousel.Height"/> 从这个宽算，占头上一截（剧照缩到六成靠右）—— 从前带宽整个按 16:9 算，
    /// 那一版第一屏正好被一张不裁切的剧照铺满。算出来而不是写死，那个形状改了这里跟着走。
    /// <para>
    /// 2026-09-06 侧边栏删掉之后这里不再加回一条栏的宽（从前是 <c>+ HomeCarousel.SideRail</c>，49）。**页宽一个
    /// 像素都没变**：从前是 1471 的窗口配 1422 的页面，现在是 1422 的窗口配 1422 的页面。
    /// </para>
    /// </summary>
    private static readonly int DefaultWidth = (int)Math.Round(HomeCarousel.WindowAspect * DefaultHeight);

    /// <summary>
    /// Held in a static field for the process lifetime: Windows keeps the raw thunk, so letting the
    /// delegate be collected would crash the first message after a GC.
    /// </summary>
    private static readonly WindowProcedure Procedure = Dispatch;

    /// <summary>
    /// The one that stands in front of the XAML island's own procedure, for the single message the island
    /// gets wrong for a video player. Static and held for the same reason <see cref="Procedure"/> is.
    /// </summary>
    private static readonly WindowProcedure IslandProcedure = IslandDispatch;

    /// <summary>Island HWND to the window that owns it, so <see cref="IslandDispatch"/> can find its state.</summary>
    private static readonly Dictionary<IntPtr, HostWindow> Islands = [];

    private static readonly IntPtr BaseBrush = Native.CreateSolidBrush(BaseColorRef);

    private static readonly Dictionary<IntPtr, HostWindow> Windows = [];

    private static bool _classRegistered;

    private DesktopWindowXamlSource? _source;
    private AppWindow? _appWindow;
    private InputNonClientPointerSource? _nonClient;
    private UIElement? _content;
    private bool _useBackdrop = true;
    private bool _disposed;

    /// <summary>The frame rect and style to restore, held only while fullscreen.</summary>
    private (NativeRect Bounds, IntPtr Style)? _restore;

    /// <summary>
    /// The foreground window <see cref="JudgeBand"/> has already spoken about, so the tick behind it says the
    /// same thing at most once. Zero while this app is the one in front.
    /// </summary>
    private IntPtr _judged;

    /// <summary>
    /// Whether the fullscreen picture still holds the topmost band it joined. False once <see
    /// cref="JudgeBand"/> has given it up for a window the picture would be covering, which it does once
    /// per fullscreen spell: what wins the band back is the user coming back to the app.
    /// </summary>
    private bool _band;

    /// <summary>
    /// The <c>WM_TIMER</c> id behind <see cref="JudgeBand"/>, and how often it asks. Slow on purpose: it
    /// exists to notice a window coming forward that the fullscreen picture would be covering, and a
    /// quarter of a second is far below noticing while being nothing to run.
    /// </summary>
    private const nuint BandTimer = 1;

    private const uint BandTimerInterval = 250;

    /// <summary>
    /// Where a title-bar drag took hold, in screen pixels, and the window origin it took hold from. Held
    /// rather than recomputed per move so the grab point stays under the cursor for the whole drag instead
    /// of the window creeping by one rounding error per event.
    /// </summary>
    private (NativePoint Grab, int Left, int Top)? _drag;

    private bool _topMost;
    private bool _playbackTitleBar;
    private bool _cursorHidden;

    /// <summary>The transparent cursor behind <see cref="Blank"/>, made on first use and freed on dispose.</summary>
    private IntPtr _blank;

    /// <summary>
    /// The same cursor as the framework's own type, and whether the wrapping has been attempted. Two fields
    /// because 「could not be wrapped」 has to be remembered too — <see cref="BlankInputCursor"/> is read ten
    /// times a second while the cursor is hidden.
    /// </summary>
    private InputCursor? _blankInput;

    private bool _blankInputTried;

    /// <summary>
    /// 键盘兜底（2026-09-15「新增esc退出全屏 按空格开始播放」）的三件：线程钩子句柄、钩子过程自己的
    /// 委托、和播放页挂上来的接键人（<see cref="IWin32KeySink"/>）。委托必须存字段 —— Windows 握着
    /// 原始 thunk，委托被 GC 之后第一次按键就是进程崩溃（<see cref="Procedure"/> 同一条铁律）。
    /// </summary>
    private IntPtr _keyboardHook;
    private KeyboardHookProcedure? _keyboardProcedure;
    private IWin32KeySink? _win32Keys;

    /// <summary>
    /// One window per blanked class, against the cursor handle that class had before. Empty whenever the
    /// cursor is shown; a leftover entry here is a class cursor left blank, which is why the restore also
    /// runs on dispose.
    /// </summary>
    private readonly Dictionary<IntPtr, IntPtr> _classCursors = [];

    /// <summary>How many class cursors are blank right now, for the self-check to read.</summary>
    public int ClassCursorsBlanked => _classCursors.Count;

    /// <summary>The most classes ever blanked in one hide, so a sweep that stopped finding windows reads as
    /// a number that did not grow rather than as a silent zero.</summary>
    public int ClassCursorsSwept { get; private set; }

    /// <summary>The island procedure ours was put in front of, and the one every other message goes to.</summary>
    private IntPtr _islandProcedure;

    /// <summary>
    /// 独立播放管线的视频子窗口（<see cref="VideoWindow"/>），岛下垫底，懒建：第一次有播放要它才创建，
    /// 之后整个窗口生命周期复用、随窗口销毁。集成播放永远不碰它——岛下什么都没有，浏览态的透明处照旧
    /// 透出窗口底色，Mica（<see cref="UseBackdrop"/>）那笔账也不受它影响。
    /// </summary>
    private VideoWindow? _video;

    /// <summary>创建这个窗口那条线程的调度队列，只用于视频子窗口的线程断言——懒建必须发生在界面线程上，
    /// 否则 mpv 的 resize 钩子就装错了线程（<see cref="EnsureVideoUnderlay"/> 的注释）。</summary>
    private readonly DispatcherQueue? _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    /// <summary>
    /// 藏匿期真实输入的取证观察者（第十六报立，2026-09-16 照搬 mpv.net 后降为只记账不裁决）。
    /// 收条线：<c>HookIslandCursor</c> 把 XAML 岛的窗口子类化的同一次呼吸里向系统注册原始输入
    /// （INPUTSINK，无焦点也收），<c>IslandDispatch</c> 收到 <c>WM_INPUT</c> 就喂进来。见证不再
    /// 回答「手还是注入」——唤醒裁决只剩 <c>ChromeReveal.HandStep</c> 那一问——它的真/注两本账
    /// 只进日志（藏匿取样、藏匿行、显示行），下一次幽灵报告的定罪证据还从这里出。类的头注释
    /// （<see cref="RealInputWitness"/>）写着整件事的证据链与边界，这里不重复。
    /// </summary>
    public RealInputWitness Witness { get; } = new();

    /// <summary>
    /// 换主题时重画标题栏那一条。<see cref="ThemeHost.Changed"/> 是个静态事件，不退订就等于把一个已经销毁的
    /// 窗口永远挂在上面，所以委托存下来，<c>WM_DESTROY</c> 里减掉。
    /// </summary>
    private Action<EmbyNian.Theming.UiTheme>? _repaint;

    /// <summary>
    /// The rectangles of the browsing title bar that belong to the page instead of the window frame —
    /// the shell's row of five buttons on the left and its account button at the right end — in logical
    /// pixels. Empty until the shell has measured them.
    /// </summary>
    private List<(double X, double Y, double Width, double Height)> _holes = [];

    private const int TitleBarHeight = 32;
    private const int CaptionButtonWidth = 46;
    private const int SystemButtonsWidth = CaptionButtonWidth * 3;

    /// <summary>
    /// 「this region kind claims nothing」, as a rectangle. See <see cref="UpdateTitleBarRegions"/> for why it
    /// is said this way rather than by withdrawing the declaration.
    /// </summary>
    private static readonly RectInt32 Empty = new(0, 0, 0, 0);

    public IntPtr Handle { get; private set; }

    /// <summary>
    /// The island's own child HWND. Exposed because the self-check has to prove from outside that the
    /// island exists and is sized, and because the player has to keep the video child below it.
    /// </summary>
    public IntPtr IslandHandle { get; private set; }

    /// <summary>键盘兜底是否整装：线程钩子装着（<see cref="KeyboardFallbackInstalled"/>）、页面也挂了接键人。</summary>
    internal bool Win32KeyFallbackArmed => KeyboardFallbackInstalled && _win32Keys is not null;

    /// <summary>
    /// 线程键盘钩子还装着。装不上（理论上只有系统拒绝）时为假 —— 那样焦点掉出岛之后 Esc/空格就哑，
    /// 自检「快捷键派发」一格里的「键盘兜底」会替这里喊出来。
    /// </summary>
    internal bool KeyboardFallbackInstalled => _keyboardHook != IntPtr.Zero;

    /// <summary>
    /// 播放页把接键人挂上来（<c>Attach</c>）/摘下去（<c>Detach</c>）。挂上之后，焦点不在岛里的空格和
    /// Esc 才有人接；摘下之后（换独立窗口接管、页面退场）钩子一概放行，两边不打架。
    /// </summary>
    internal void SetWin32Keys(IWin32KeySink? sink) => _win32Keys = sink;

    /// <summary>
    /// Whether the mouse cursor is to stay off the picture — 「全屏播放且鼠标在画面上时，鼠标静止不动两秒之后
    /// 要自动隐藏」. Set by the player, whose reveal rule decides <em>when</em>; this is the whole of
    /// <em>how</em>, and it lives here because it is a window-level Win32 arrangement rather than anything
    /// about the visual tree.
    /// <para>
    /// <b>It is deliberately four levers and not six.</b> The refactor of 2026-09-15 collapsed a set of
    /// defences built up over eight bug reports — a hand-rolled <c>InputPointerSource.Cursor</c> round trip, a
    /// global cursor-shape snapshot taken ten times a second, a synthetic-event adjudicator, a hiding-point
    /// anchor — down to the arrangement the mature players actually use. HC-Player's
    /// <c>SetApplicationCursorHidden</c> is the model: an idempotent short circuit, the display counter pinned
    /// below zero, and a shape of 「nothing」 set on the queue. What the count and the shape cannot reach — a
    /// window whose queue belongs to another thread, i.e. the island's own bridge the pointer sits on — is
    /// covered by the class cursor (<see cref="BlankClassCursors"/>), the same <c>SetClassLongPtr</c> HC-Player
    /// uses. And what none of those reach — a pointer over XAML content, whose shape the framework decides — is
    /// covered by <see cref="BlankInputCursor"/> on the picture itself, and by answering <c>WM_SETCURSOR</c>.
    /// </para>
    /// <para>
    /// <b>Why not <c>ShowCursor</c> alone.</b> That call keeps a per-thread display counter, it is what every
    /// guide recommends, and over a WinUI 3 XAML island it does nothing at all — 「鼠标指针还是不会自动隐藏」,
    /// twice. What works on the Win32 surfaces is <c>SetCursor(NULL)</c>: measured on this window, the thread's
    /// own cursor goes to 「none」 the moment it is called and comes back on the call that restores a shape.
    /// <c>GetCursorInfo</c> cannot see it — it reports the desktop's cursor, which is recomputed when the
    /// pointer moves, and the pointer holding still is the entire circumstance here.
    /// </para>
    /// <para>
    /// <b>Why the counter is pinned rather than toggled once.</b> A cross-process <c>SetCursor</c> injected
    /// into this queue by another application (the ninth report's finding: a second-screen chat client waking
    /// a hidden pointer) draws its shape only while the queue's display count is non-negative. The count is
    /// private to the queue and only this process's <c>ShowCursor</c> can move it, so
    /// <see cref="SuppressCursorDisplay"/> pins it below zero and holds it there — structurally immune to a
    /// foreign shape, where every other lever here only speaks for this process. See
    /// <see cref="CursorSuppressRestates"/> for the duel that established it.
    /// </para>
    /// <para>
    /// <b>Idempotent, like HC-Player's.</b> Being told again what it already believes returns immediately; the
    /// tick's re-assertion goes through <see cref="KeepCursorHidden"/>, which is where the per-tick work lives.
    /// </para>
    /// </summary>
    public bool CursorHidden
    {
        get => _cursorHidden;
        set
        {
            if (_cursorHidden == value) return;

            _cursorHidden = value;

            // Said now rather than waited for. Hiding happens *because* nothing is moving, so the next
            // WM_SETCURSOR may be seconds away — and it is the pointer coming back to life, by which time
            // the cursor is wanted again. The shape set here is what the user sees until then.
            Native.SetCursor(value ? Blank : Native.LoadCursor(IntPtr.Zero, Native.ArrowCursor));

            if (value)
            {
                // 第九报（2026-09-15，自检抓回来的回归）：类光标是唯一够得着「别的线程拥有的窗口」的杠杆
                // ——岛桥那个窗口的队列是框架的，SetCursor 与负计数锁都不在它的路上。见 BlankClassCursors。
                BlankClassCursors();

                // 第九报（2026-09-15）：负计数锁（HC-Player 式）。压到底而非压一次：历史遗留的深负计数也
                // 一并兜住。每个藏匿期重新计取证数。
                CursorSuppressRestates = 0;
                SuppressCursorDisplay();
            }
            else
            {
                RestoreClassCursors();
                RestoreCursorDisplay();
            }
        }
    }

    /// <summary>
    /// The transparent cursor, made once and kept. Every 「no cursor」 in this window is this handle rather
    /// than <see cref="IntPtr.Zero"/>: the two are the same to <c>SetCursor</c> and not at all the same to a
    /// window class, where nothing means 「I do not set the cursor」 and leaves the last shape on screen.
    /// Falls back to <see cref="IntPtr.Zero"/> if the OS refuses, which is the behaviour this had before.
    /// </summary>
    private IntPtr Blank => _blank != IntPtr.Zero ? _blank : _blank = Native.CreateBlankCursor();

    /// <summary>
    /// Whether this thread's queue currently holds nothing a user could see: no shape at all, or the
    /// transparent one. Both mean hidden, and 「hidden」 stopped being 「the handle is zero」 the moment a
    /// blank cursor became the thing that is set — every reading that used to compare against zero has to
    /// come through here instead.
    /// </summary>
    public bool CursorShapeGone => Native.GetCursor() is var shape && (shape == IntPtr.Zero || shape == _blank);

    /// <summary>The transparent cursor's handle, for readings that have to tell it from a real shape.</summary>
    public IntPtr BlankCursor => Blank;

    /// <summary>
    /// The same transparent cursor, wrapped as the framework's own <c>InputCursor</c> so it can be handed to
    /// <c>UIElement.ProtectedCursor</c>. Null when the wrapping could not be done, which leaves the cursor
    /// behaving exactly as it did before this was added.
    /// <para>
    /// It is the lever the queue-level ones cannot be: while the pointer is over XAML content the shape on
    /// screen is the framework's to decide, and nothing done on this thread — <c>SetCursor</c>,
    /// <c>ShowCursor</c> — is on that path. A real film's log says so plainly: one hide lasted two minutes and
    /// five seconds with this queue holding no shape the whole time, the show count at −1, and
    /// <c>GetCursorInfo</c> answering 「system arrow」 from beginning to end. The page hands this to
    /// <c>Root.Cursor</c> for as long as the hide lasts (<c>PlayerPage.Chrome.cs</c>），which is the whole of
    /// this lever's wiring — one assignment per tick, nothing to restore. See <see cref="InputCursors"/> for
    /// how the wrapping is done and why there is no projected API for it.
    /// </para>
    /// <para>
    /// Built once and remembered, failure included: this is read ten times a second for as long as the cursor
    /// stays hidden, and a wrapping that cannot be done must not be retried on every tick.
    /// </para>
    /// </summary>
    public InputCursor? BlankInputCursor
    {
        get
        {
            if (_blankInputTried) return _blankInput;

            _blankInputTried = true;
            _blankInput = InputCursors.From(Blank);

            if (_blankInput is null)
                Log.Warn(Category, "透明光标包不成框架的 InputCursor —— 指针压在画面上时藏不掉，其余几条路照旧");

            return _blankInput;
        }
    }

    /// <summary>
    /// Whether the island's procedure is ours to answer through. False on a window whose island was never
    /// created, or if the swap were ever refused — in which case the cursor simply stays visible, which is
    /// the reason this is a fact the self-check can read rather than an assumption.
    /// </summary>
    public bool CursorHookInstalled => _islandProcedure != IntPtr.Zero;

    /// <summary>
    /// How many times <see cref="IslandDispatch"/> has answered <c>WM_SETCURSOR</c> with 「no cursor」. The
    /// only evidence that does not depend on whose window the pointer happens to be over when a probe runs:
    /// the OS's own 「is the cursor showing」 flag is about the desktop, and a self-check that starts behind
    /// somebody else's full-screen player cannot ask it.
    /// </summary>
    public int CursorHidesAnswered { get; private set; }

    /// <summary>
    /// How many <c>WM_SETCURSOR</c> the interception has seen at all, answered or passed on. The count above
    /// says our branch ran when asked; this one says the asking happens — a subclass installed on the wrong
    /// one of the island's several windows would answer nothing and read exactly like one that was never
    /// asked. A real pointer move over the picture has to move this, and that is the whole causal chain:
    /// the messages arrive, and while the player wants no cursor they are answered with none.
    /// </summary>
    public int CursorAsksSeen { get; private set; }

    /// <summary>
    /// How many messages of any kind have reached <see cref="IslandDispatch"/>. The other two counts are about
    /// one message; this one is about the subclass being alive at all, and it is what tells 「the island never
    /// asks about the cursor」 apart from 「the swap did not take」 — two answers that look identical from a
    /// <c>WM_SETCURSOR</c> count of zero.
    /// </summary>
    public int IslandMessagesSeen { get; private set; }

    /// <summary>
    /// 第九报（2026-09-15）：本线程队列的显示计数被抬回、随即又被锁回负区的累计调用数。用户原话「你直接抄
    /// 这些开源项目吧」（HC-Player 的 <c>SetApplicationCursorHidden</c>：藏即 <c>while (ShowCursor(FALSE) &gt;= 0)
    /// {{}}</c>，显即 <c>while (ShowCursor(TRUE) &lt; 0) {{}}</c>）。抄它的理由是本机对决实验（work\
    /// cursor-suppress-test3.py，2026-09-15）：monitor 窗口压住指针后，挂进队列的跨进程 <c>SetCursor(cross)</c>
    /// 在未压计数时把全局光标点亮成十字（0x988ms 处 flags=1、形状 0x10009）——这就是 14:26 那个每 ~1.1s 画
    /// 「一道红线」的第三方画回者的机制复刻；而把计数压到 −2 之后，同一支画回笔又戳了九次，全局快照纹丝不动
    /// （flags=0、形状 0x0），win32k 重绘光标时查的就是拥有队列的这个计数，任何后来者的 <c>SetCursor</c>
    /// 都点亮不了负区里的光标。现有四条杠杆（SetCursor、ProtectedCursor、WM_SETCURSOR 拦截、Root.Cursor）
    /// 全部只对本进程说话，这是第五条、也是唯一一条对挂队列的外部画回者结构免疫的。计数是队列私有的，只有
    /// 本进程的 <c>ShowCursor</c> 能改它；>0 的读数＝有人在藏匿期把计数抬回过（框架或别的什么），锁回负区
    /// 的动作本身就是取证。每个藏匿期清零。
    /// </summary>
    public long CursorSuppressRestates { get; private set; }

    /// <summary>
    /// 第九报（2026-09-15）：把本线程队列的显示计数压回负区。幂等——计数已在负区时一次调用都不发生，
    /// 所以每拍重说毫无开销；一旦被谁抬回非负，下一个 100ms 拍就把锁重新上好。见
    /// <see cref="CursorSuppressRestates"/> 的对决实验。
    /// </summary>
    private void SuppressCursorDisplay()
    {
        while (Native.ShowCursor(false) >= 0) CursorSuppressRestates++;
    }

    /// <summary>
    /// 第九报（2026-09-15）：把显示计数拉回非负。同样抄 HC-Player：藏匿期间计数可能被压得很深，单次
    /// <c>ShowCursor(TRUE)</c> 只抬一格，「显示之后光标不见了」就是负计数残留吞掉的——拉到非负为止。
    /// </summary>
    private void RestoreCursorDisplay()
    {
        while (Native.ShowCursor(true) < 0) { }
    }

    /// <summary>
    /// The class cursor of every window a pointer over the picture can be on, set to the transparent shape.
    /// <para>
    /// <b>为什么这一条不能跟别的杠杆一起砍。</b> 2026-09-15 的那次重构先砍了它（连同全树扫描），自检当场
    /// 把它抓了回来：指针压在画面的 XAML 岛桥窗口（<c>Microsoft.UI.Content.DesktopChildSiteBridge</c>，
    /// 属于框架线程）上时，桌面光标记录变回系统箭头，10 秒观察 933 次采样全红；而同一份报告里指针压在
    /// 自己窗口上藏匿时，读数是 <c>[标志 0x00，形状 0x0]</c>——没有形状。差别就在这个窗口属于谁。
    /// </para>
    /// <para>
    /// 本进程的 <c>SetCursor</c> 与 <c>ShowCursor</c> 都只对拥有该队列的窗口说话，而这个窗口的队列是
    /// 框架的；它是一个 Win32 窗口，却拿经典 <c>WM_SETCURSOR</c> 的语义决定自己画什么，于是类光标
    /// （<c>GCLP_HCURSOR</c>）成了唯一一条跨线程也跨进程边界的杠杆。HC-Player 自己就是这么干的
    /// （<c>SetClassLongPtr(hwnd, GCLP_HCURSOR, ...)</c>），成熟播放器里这不是补丁，是标配。
    /// </para>
    /// <para>
    /// <b>和有界性。</b> 遍历从本窗口起、三层、最多 32 个（<see cref="Tree"/>），也就是岛桥、输入站、
    /// video 窗口里 libmpv 的子窗口——一个压在画面上的指针能落在的全部地方。不做无限深的全树扫描：扫到的
    /// 类越多，还原时越可能把别人在这期间新设的形状抹掉。每藏一次只记「原本是什么」，还原按记录逐个放回。
    /// </para>
    /// </summary>
    private void BlankClassCursors()
    {
        var blank = Blank;
        if (blank == IntPtr.Zero || Handle == IntPtr.Zero) return;

        foreach (var window in Tree())
        {
            var previous = Native.SetClassCursor(window, Native.ClassCursorIndex, blank);

            // Already blank: another window of the same class came first, and this one's 「previous」 is our
            // own handle. Recording it would make the restore a no-op and the blanking permanent.
            if (previous == blank) continue;

            _classCursors[window] = previous;
        }

        ClassCursorsSwept = Math.Max(ClassCursorsSwept, _classCursors.Count);
    }

    /// <summary>Puts every class cursor back exactly as it was found. Idempotent.</summary>
    private void RestoreClassCursors()
    {
        foreach (var (window, previous) in _classCursors)
            Native.SetClassCursor(window, Native.ClassCursorIndex, previous);

        _classCursors.Clear();
    }

    /// <summary>
    /// This window and what is under it, breadth-first and bounded. Three levels reaches the island's bridge,
    /// its input site, and libmpv's child inside the video window, which is every window a pointer over the
    /// picture can be over.
    /// </summary>
    private List<IntPtr> Tree()
    {
        var found = new List<IntPtr> { Handle };

        for (var level = 0; level < 3; level++)
        {
            var parents = found.ToArray();

            foreach (var parent in parents)
            {
                var child = IntPtr.Zero;

                while ((child = Native.NextChild(parent, child, IntPtr.Zero, IntPtr.Zero)) != IntPtr.Zero)
                {
                    if (found.Contains(child)) continue;
                    if (found.Count >= 32) return found;

                    found.Add(child);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The per-tick half of <see cref="CursorHidden"/>: says it again, for a caller that can afford to keep
    /// saying it. 单传感器（2026-09-15 重构）之后这里只剩三句话，而三句话都是「再说一遍」——没有发布门、
    /// 没有注入、没有全局快照。
    /// <para>
    /// <b>为什么每拍都说。</b> One call at the moment of hiding is enough only if nothing puts a shape back
    /// afterwards, and there are several places that can: the island is several windows deep with input
    /// handling of its own, and the framework recomputes the cursor over XAML content. Re-said on the player's
    /// ten-hertz tick each costs one user32 call, and they stop of their own accord the moment the pointer
    /// moves — moving the pointer is how the cursor is asked back, so <see cref="CursorHidden"/> is false by
    /// then and this returns at the first line。
    /// </para>
    /// <para>
    /// <b>为什么不再有「发布」这一步。</b> 曾经这里记一个 <c>_cursorHidePublished</c>，首拍发一次
    /// <c>NudgeCursorState</c>（注入 ±1px 逼框架重算光标）。第九报的 14:03 反馈环把它定了罪：对静止指针
    /// 注入位移，OS 重算光标并向指针压着的窗口重发 <c>WM_SETCURSOR</c>，该窗口把形状设回去，下一拍又看见
    /// 「有形状」——每 ~1.15 秒一轮闪烁（用户「现在每过一会鼠标就会闪一下」）。注入本身是真实输入，
    /// 只该留在自检里当探针。压制职守交给不产生输入的那几条：这里每拍 <c>SetCursor</c> + 负计数锁 +
    /// 类光标，显示路径每拍 <c>Root.Cursor</c>，加上 <c>WM_SETCURSOR</c> 拦截。
    /// </para>
    /// </summary>
    public void KeepCursorHidden()
    {
        if (!_cursorHidden) return;

        Native.SetCursor(Blank);

        // 第九报（2026-09-15）：每拍把负计数锁重上一遍（幂等，计数已在负区时零调用）。重压的理由与
        // 上面那句 SetCursor 每拍重说同构——负计数锁是队列私有的，本进程里谁（框架的光标管理、未来
        // 的自己人）调一次 ShowCursor(TRUE) 就能把它抬回非负，画回者的下一笔就又亮得起来了。锁被抬回
        // 的次数记进 CursorSuppressRestates，随 show 行出日志。
        SuppressCursorDisplay();

        // 第九报（2026-09-15，自检抓回来的回归）：类光标每拍重说。框架在藏匿期会把自己的箭头类光标
        // 设回去（它管着岛桥那个窗口），而那个窗口不在本进程的队列上——SetCursor 与负计数锁都够不着它。
        // 幂等：已经在透明上的那些窗口，previous == blank 直接跳过，_classCursors 不增长。
        BlankClassCursors();
    }

    /// <summary>Whether content is currently extended into a custom non-client title bar.</summary>
    public bool UsesCustomTitleBar => _nonClient is not null;

    /// <summary>
    /// Changes only the interactive partition of the custom title bar and the height the framework
    /// reserves for it. During playback the whole top strip is XAML input — the back button, the three
    /// window commands, and the blank space between them that the page drags the window by; while browsing
    /// the same area is caption drag space with rectangles cut out of it for the shell's buttons — the row
    /// of five and the account button (<see cref="SetTitleBarHoles"/>), and the framework's own caption
    /// buttons at its end.
    /// <para>
    /// What it deliberately does <em>not</em> touch is the window's style bits. Playback used to drop
    /// <c>WS_CAPTION</c> so the picture could reach the top edge, and that achieved the opposite: with the
    /// caption gone and <c>WS_THICKFRAME</c> kept, the top resize border stops being part of the client
    /// area and becomes seven physical pixels of frame that DWM paints black —
    /// 「播放页面标题栏最上方有一行黑色」. Measured on this window: caption dropped gives a 1266×786 client
    /// starting 7 px below the window's top edge; caption kept gives 1264×792 starting exactly at it, which
    /// is the browsing frame to the pixel, with the top edge still answering <c>HTTOP</c> so it can still be
    /// dragged. <see cref="TitleBarHeightOption.Collapsed"/> already takes the framework's caption buttons
    /// away and the regions below take its drag area away, so there was nothing left for the style bit to do
    /// except cost the picture those seven rows.
    /// </para>
    /// </summary>
    public bool PlaybackTitleBar
    {
        get => _playbackTitleBar;
        set
        {
            if (_playbackTitleBar == value) return;

            _playbackTitleBar = value;

            if (_appWindow?.TitleBar is { } titleBar)
                titleBar.PreferredHeightOption = value ? TitleBarHeightOption.Collapsed : TitleBarHeightOption.Standard;

            // Straight to the repartition, with nothing cleared on the way: the two modes claim different
            // region kinds and every kind is assigned below, so there is no rect left behind to withdraw —
            // and withdrawing one is what used to cost the strip its drag. UpdateTitleBarRegions says why.
            UpdateTitleBarRegions();
        }
    }

    /// <summary>
    /// 播放时不设最小尺寸：「取消播放页面窗口缩小的最小尺寸限制，允许窗口继续自由缩小」（用户的话，
    /// 2026-09-15）。开着的时候三处下限全部让位 —— <see cref="ClampMinimumSize"/> 不再写
    /// <c>MinTrackSize</c>（系统默认的最小追踪尺寸只剩一百来像素，等于随便缩）、比例锁与
    /// <see cref="FitToPicture"/> 把最小值按 0 递给 <see cref="AspectLock"/>；<see cref="MinimumClientSize"/>
    /// 那个读数是自检量浏览窗口下限用的，不跟着变。
    /// <para>
    /// 谁来开关：<see cref="PlayerWindow.TryCreate"/> 开窗即开（独立播放窗口天生只为播放存在），
    /// <c>PlayerPage.EnterPlayer</c> 把接管的主窗口打开、<c>LeavePlayer</c> 关回去 —— 退出播放时
    /// <see cref="RestoreBrowseGeometry"/> 本来就要把播放前的几何还回来，浏览窗口因此不会停在播放时缩出来的
    /// 那个小尺寸上。默认 false，浏览窗口的 600×560 下限原样保留。
    /// </para>
    /// </summary>
    public bool FreeSizing { get; set; }

    /// <summary>
    /// What the framework says its own title bar comes to, in physical pixels: the height it reserves and
    /// the width its caption buttons occupy at the right end. Both are zero while
    /// <see cref="TitleBarHeightOption.Collapsed"/> is in force, which is how the self-check can prove from
    /// outside that playback really has no system buttons standing over the picture — the window keeps
    /// <c>WS_CAPTION</c> now, so 「no buttons」 is a claim about the framework rather than about the style bits.
    /// </summary>
    public (double Height, double RightInset) TitleBarMetrics =>
        _appWindow?.TitleBar is { } bar ? (bar.Height, bar.RightInset) : (0, 0);

    /// <summary>
    /// The rectangles of the browsing title bar the shell has claimed for itself, in logical pixels, or
    /// null while it has claimed none. Exposed for the self-check, which has to prove the holes the window
    /// is keeping are the rectangles the buttons are actually drawn in — a hole in the wrong place is
    /// invisible until someone tries to click a button and drags the window instead.
    /// </summary>
    internal IReadOnlyList<(double X, double Y, double Width, double Height)>? TitleBarHoles =>
        _holes.Count > 0 ? _holes : null;

    /// <summary>
    /// Says which rectangles of the title bar the page draws buttons in, in logical pixels of the island's
    /// coordinates, so the frame stops answering <c>HTCAPTION</c> there.
    /// <para>
    /// This is not decoration. A button drawn inside a caption region never receives a click at all: the
    /// frame claims the point before XAML sees it, and Windows turns the pointer press into a window drag.
    /// The rectangles have to be cut out of the caption for the buttons to work, and the caption has to be
    /// stated as the pieces around them rather than as one rect underneath, because a point declared twice
    /// belongs to neither kind in particular.
    /// </para>
    /// <para>Passing no positive-area rectangle gives the strip back to the frame, whole.</para>
    /// </summary>
    internal void SetTitleBarHoles(params (double X, double Y, double Width, double Height)?[] holes)
    {
        _holes = holes
            .Where(hole => hole is { Width: > 0, Height: > 0 })
            .Select(hole => hole!.Value)
            .ToList();
        UpdateTitleBarRegions();
    }

    /// <summary>The client area in physical pixels, or an empty rect before the window exists.</summary>
    public (int Width, int Height) ClientSize
    {
        get
        {
            if (Handle == IntPtr.Zero) return (0, 0);
            Native.GetClientRect(Handle, out var client);
            return (client.Width, client.Height);
        }
    }

    /// <summary>Raised once the window has been destroyed, so the app can end its message loop.</summary>
    public event Action? Closed;

    /// <summary>
    /// Raised whenever this window's size — or the monitor it is mostly on — may have moved: a resize in
    /// progress, the end of a drag, full screen either way, and a DPI change. The 着色器档位 is the only
    /// listener, and it debounces: see <see cref="Playback.OutputWatch"/>.
    /// <para>
    /// The end of a drag is in the list for the case that raises nothing else — a window moved onto another
    /// monitor of the same size sends no <c>WM_SIZE</c> and, at matching scale, no <c>WM_DPICHANGED</c> either,
    /// yet the full-screen plan prepared for the old screen has just gone stale.
    /// </para>
    /// </summary>
    internal event Action? GeometryChanged;

    /// <summary>
    /// Raised on <c>WM_ACTIVATE</c> with the answer to 「is this window the foreground one right now」 —
    /// <c>false</c> the moment something else took it, <c>true</c> the moment it came back. Between two
    /// windows of this process, too, which <see cref="Native.WmActivateApp"/> never speaks about.
    /// <para>
    /// The cursor rule reads it（「未激活不藏、失焦显示」，2026-09-16 照搬 mpv.net 的
    /// <c>ActiveForm == this</c> 与 <c>OnLostFocus → ShowCursor</c>）：播放页把这一位喂给
    /// <see cref="EmbyNian.Core.Playback.ChromeReveal.WindowFocused"/>。本进程的两个窗口互抢前台
    /// （独立播放窗、设置窗）也算数 —— 那正是「别把光标藏到别人正要点的窗口上」的情形。
    /// </para>
    /// </summary>
    internal event Action<bool>? FocusChanged;

    /// <summary>
    /// 这个窗口现在有多大、在哪儿、是不是最大化着 —— 也就是下次开窗该照着的那一份。写盘的是
    /// <c>App.OnWindowClosed</c>（那一头本来就要存一次设置），这里只负责一直是对的。
    /// <para>
    /// 记的始终是**还原之后**那个矩形：最大化和全屏时窗口的边归显示器，那时候的尺寸不是用户挑的，所以那两
    /// 档只改 <c>Maximized</c> 这一位、尺寸留着上一次量到的。全屏干脆不记 —— 进全屏之前刚记过一次，而那一
    /// 份正是想要的。
    /// </para>
    /// <para>
    /// 在拖动结束（<see cref="Native.WmExitSizeMove"/>）、最大化与还原（<see cref="Native.WmSize"/>）、
    /// 以及关窗前（<see cref="Native.WmClose"/>）各记一次。前两处是为了崩了也不丢：<c>WM_CLOSE</c> 那一次
    /// 走不到的时候，最后一次落定的尺寸已经在手上了。
    /// </para>
    /// </summary>
    public (WindowBounds Bounds, bool Maximized) Placement { get; private set; }

    /// <summary>
    /// 用户自己挑的那一份几何 —— 播放前的窗口大小和位置，退出播放时窗口要回到这里。带上「当时是不是最大化」
    /// 那一位，所以从最大化浏览进片子、出来之后仍旧是最大化的浏览窗口。
    /// <para>
    /// <b>它和 <see cref="Placement"/> 是两件事，虽然平时装着同样的数。</b><see cref="Placement"/> 是「下次开窗
    /// 照着这一份」，每一拍落定的几何都会盖掉它；这一份是「播放不许碰的那一份」，只在没在放片子的时候更新。
    /// 从前只有 <see cref="Placement"/> 一个，于是 <see cref="FitToPicture"/> 那一下按画面比例整出来的又宽又扁
    /// 的窗口把浏览的形状盖掉了 —— 退出播放之后窗口留着片子的形状，下次开窗也照着它开。用户 2026-09-14 报的
    /// 「进入播放页面然后再退出页面会保留播放页面的窗口大小比例」就是这两件事混成一件的样子。
    /// </para>
    /// <para>
    /// 最大化那一档尺寸留着上一次量到的（和 <see cref="Placement"/> 同一套规矩），所以「最大化着浏览 → 进片子
    /// → 出来」回到的是取消最大化时该有的那个大小，而不是整块屏幕。
    /// </para>
    /// </summary>
    private (WindowBounds Bounds, bool Maximized) _browse;

    /// <summary>
    /// The XAML tree filling the client area. Assigning before <see cref="Show"/> avoids a frame of
    /// empty window; assigning later swaps the whole shell.
    /// </summary>
    public UIElement? Content
    {
        get => _content;
        set
        {
            _content = value;
            if (_source is not null) _source.Content = value;
        }
    }

    /// <summary>
    /// Whether the island asks DWM for Mica. True is the right answer for every browsing page and
    /// gives the shell the wallpaper-aware material a Fluent app is expected to have; it must be
    /// turned off before the video child HWND is shown, because a backdrop is composited into the
    /// island surface and an opaque island hides the video (fact 3 above).
    /// </summary>
    public bool UseBackdrop
    {
        get => _useBackdrop;
        set
        {
            if (_useBackdrop == value) return;
            _useBackdrop = value;
            ApplyBackdrop();
        }
    }

    /// <summary>
    /// Whether video is on screen. Setting it turns Mica off (fact 3: a backdrop is composited into the
    /// island surface, and running the backdrop sampler under a full-bleed picture is spend with no
    /// audience) and back on again afterwards.
    /// <para>
    /// 两条管线在这里共用一个开关，但道理各半。集成模式：画面在播放页的视觉树里——SwapChainPanel
    /// 合成 mpv 的 D3D11 交换链——这里什么也不显不藏，只是省掉垫在下面的背景采样。独立播放：画面是
    /// 岛下那个原生子窗口（<see cref="VideoWindow"/>），Mica 不关，岛面就被垫料填满、透不到视频——
    /// 这一关是真开路。
    /// </para>
    /// </summary>
    public bool VideoVisible
    {
        get => !_useBackdrop;
        set => UseBackdrop = !value;
    }

    /// <summary>
    /// 全屏. Drops <c>WS_CAPTION</c> and <c>WS_THICKFRAME</c>, stretches the window over the whole
    /// monitor it is mostly on, and tells the shell it has done so; coming back puts back exactly the
    /// style bits and frame rect it took away.
    /// <para>
    /// The window is never recreated, which is the whole reason to do it by hand rather than through
    /// <c>AppWindow.SetPresenter</c>: recreating it would destroy the XAML island and the video child with
    /// it, and mpv would have to be handed a new <c>wid</c> mid-file. As it is, going fullscreen is one
    /// style change and one <c>SetWindowPos</c>, and the picture never even blinks.
    /// </para>
    /// <para>
    /// The one thing the geometry does not buy is the taskbar. It is a topmost window, so it draws over a
    /// window that merely happens to be monitor-sized. Asking the shell nicely — <see
    /// cref="Native.MarkFullscreen"/>, the documented route — was measured to change nothing here, so the
    /// window itself joins the topmost band while fullscreen and leaves it again on the way out, and while
    /// fullscreen whenever another application comes forward with something the picture would be covering
    /// (<see cref="JudgeBand"/>, which is where 「点击屏幕2的应用」 is decided).
    /// </para>
    /// <para>
    /// A maximized window is restored first. Leaving it maximized would work, but the frame rect saved for
    /// the way back would be the maximized one, and coming out of fullscreen would then leave a window
    /// that looks maximized without Windows thinking it is.
    /// </para>
    /// </summary>
    public bool Fullscreen
    {
        get => _restore is not null;
        set
        {
            if (Handle == IntPtr.Zero || Fullscreen == value) return;

            if (value) EnterFullscreen();
            else LeaveFullscreen();
        }
    }

    /// <summary>
    /// 置顶. Set through a z-order change rather than <c>SetForegroundWindow</c>, which the OS refuses
    /// outright when the calling process is not already in the foreground.
    /// </summary>
    public bool TopMost
    {
        get => _topMost;
        set
        {
            if (Handle == IntPtr.Zero || _topMost == value) return;
            _topMost = value;

            Native.SetWindowPos(
                Handle,
                value ? Native.HwndTopMost : Native.HwndNoTopMost,
                0, 0, 0, 0,
                Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);
        }
    }

    /// <summary>Whether the host window is currently maximized.</summary>
    public bool IsMaximized => Handle != IntPtr.Zero && Native.IsZoomed(Handle);

    /// <summary>
    /// 缩放窗口时按画面比例联动: the shape the client area is held in while the user drags an edge, as
    /// width ÷ height, or 0 for the ordinary unconstrained window.
    /// <para>
    /// Written by the player from mpv's <c>dwidth</c>/<c>dheight</c> and cleared when playback ends. Held
    /// here rather than asked for during the drag because <c>WM_SIZING</c> arrives dozens of times a
    /// second and the answer would be a round trip to mpv's event loop each time.
    /// </para>
    /// <para>
    /// <b>这是现在唯一一条形状约束。</b>浏览时还有过第二条（「锁定窗口比例大小」，把浏览区按住在 16:9），
    /// 2026-09-05 按用户的话删掉了 —— 没在放片子的窗口从此随便拉。
    /// </para>
    /// <para>
    /// <b>写进一个非零比例会先把当前几何记成「浏览的那一份」</b>（<see cref="CaptureBrowseGeometry"/>）—— 这是
    /// 一次播放里画面第一次碰窗口的那一刻，也是唯一还量得到播放前那个矩形的时刻；<see cref="FitToPicture"/> 一
    /// 动手就晚了。归零（播放结束）不记，那一头要的正是「回到记着的那一份」，让还原自己判有没有必要动。
    /// </para>
    /// </summary>
    public double PictureAspect
    {
        get => _pictureAspect;
        set
        {
            if (Math.Abs(_pictureAspect - value) <= 0.001) return;

            if (value > 0 && _pictureAspect <= 0) CaptureBrowseGeometry();
            _pictureAspect = value;
        }
    }

    private double _pictureAspect;

    /// <summary>这个窗口所在那块屏的 dpi，问不到就按 96 算 —— 和下面几处 <c>GetDpiForWindow</c> 同一个兜底。</summary>
    private uint WindowDpi
    {
        get
        {
            var dpi = Handle == IntPtr.Zero ? 0 : Native.GetDpiForWindow(Handle);
            return dpi == 0 ? 96 : dpi;
        }
    }

    /// <summary>
    /// 自检用：这个窗口的客户区最小能到多大，物理像素。比例撞上它的时候形状要让位（<see cref="AspectLock"/> 里
    /// 那条「最小尺寸压得住比例」），所以读形状的那一关得知道这个数才不会把「让位」报成「锁坏了」。
    /// </summary>
    internal (int Width, int Height) MinimumClientSize
    {
        get
        {
            var dpi = (int)WindowDpi;
            return (MinimumWidth * dpi / 96, MinimumHeight * dpi / 96);
        }
    }

    /// <summary>
    /// 自检用：首屏那一读在这个窗口上判不判。它量的是「第一屏那两栏立得住、下一排横排还在屏外」，而那句话是照
    /// **开窗那一档**说的 —— 页面 16:9（<see cref="DefaultWidth"/>）。
    /// <para>
    /// 判据是「**不比 16:9 更扁**」而不是「正好 16:9」：那一叠货架要的是高度，所以比 16:9 更高的窗口只会让这句话
    /// 更容易成立，那一档照样判。比它更扁的窗口（页面又宽又矮）可能连一排货架都放不下 —— 那是物理，不是回归，
    /// 所以那一档只报不判。从前这里要求正好 16:9，因为「锁定窗口比例大小」把窗口一直按在那个形状上；那个开关
    /// 2026-09-05 删掉了，而窗口从此什么形状都拉得出来。
    /// </para>
    /// <para>
    /// 从前这里还要从客户区宽里减掉侧边栏那一条（一个叫 <c>SideInset</c> 的读数）—— 侧边栏 2026-09-06 删掉之后
    /// 页面就是整个客户区，那一步跟着没了。
    /// </para>
    /// <para>
    /// 放片子的时候形状归画面，全屏、最大化和被 Windows 贴边的窗口形状归显示器 —— 这四种都不判。
    /// </para>
    /// </summary>
    internal bool BrowseFoldMeasurable
    {
        get
        {
            if (PictureAspect > 0 || Fullscreen || IsMaximized) return false;

            var (width, height) = ClientSize;
            return width > 0 && height > 0 && width <= (HomeCarousel.WindowAspect * height) + 2;
        }
    }

    /// <summary>
    /// Writes down what <see cref="Placement"/> promises. Cheap enough to call on every drag that ends and
    /// every maximize that lands — two <c>user32</c> reads and a struct assignment, no allocation.
    /// <para>
    /// Silent in the three states whose geometry is not the user's choice: fullscreen (the edges are the
    /// monitor's, and the rectangle from just before entering it is already recorded), minimized (there is
    /// no shape to record), and before the window exists. Maximized keeps the recorded size and only raises
    /// the flag, which is what makes 「取消最大化」 come back to the size the user actually dragged.
    /// </para>
    /// </summary>
    private void RememberPlacement()
    {
        if (Handle == IntPtr.Zero || Fullscreen || Native.IsIconic(Handle)) return;

        // 片子摆着的形状不是用户挑的，所以它既不进 <see cref="Placement"/>（下次开窗照着它开就成了片子的
        // 形状），也不进 <see cref="HasBrowseGeometry"/> 那一份（退出播放要回到这里）。
        if (PictureAspect > 0) return;

        if (Native.IsZoomed(Handle))
        {
            Placement = (Placement.Bounds, true);
            _browse = (Placement.Bounds, true);
            return;
        }

        if (!Native.GetWindowRect(Handle, out var rect)) return;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var bounds = new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
        Placement = (bounds, false);
        _browse = (bounds, false);
    }

    /// <summary>
    /// 自检用：这一份播放前的几何记下来没有。退出播放要还原，而没记过的时候「还原」是没地方去的。
    /// </summary>
    internal bool HasBrowseGeometry => _browse.Bounds.Width > 0 && _browse.Bounds.Height > 0;

    /// <summary>
    /// 自检用：播放前记住的那份几何，读给探针比对。
    /// </summary>
    internal WindowBounds BrowseBounds => _browse.Bounds;

    /// <summary>
    /// 现在这一份几何如果属于浏览，就把它记进 <see cref="_browse"/>。由 <see cref="PictureAspect"/> 的写入方
    /// 在写下比例的那一刻调一次 —— 那一刻的矩形一定是画面还没碰过的那一个，比等 <c>WM_SIZE</c> 回头再记可靠：
    /// 消息是异步的，而 <see cref="FitToPicture"/> 一下就能把窗口挪走，等消息回来时量到的可能已经是新形状。
    /// </summary>
    /// <returns>记下了没有。全屏、最小化、没窗口，或者已经记着一份浏览几何时不重复记。</returns>
    public bool CaptureBrowseGeometry()
    {
        if (Handle == IntPtr.Zero || Fullscreen || Native.IsIconic(Handle)) return false;

        if (Native.IsZoomed(Handle))
        {
            _browse = (_browse.Bounds, true);
            return HasBrowseGeometry;
        }

        if (!Native.GetWindowRect(Handle, out var rect)) return false;
        if (rect.Width <= 0 || rect.Height <= 0) return false;

        _browse = (new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom), false);
        return true;
    }

    /// <summary>
    /// 把窗口放回它开始放片子之前的那个大小和位置。播放结束时调一次，是「进入播放页面然后再退出页面会保留
    /// 播放页面的窗口大小比例」的修法：<see cref="FitToPicture"/> 按画面比例整过的那个形状是给这部片子用的，
    /// 片子看完了那形状就没道理留着 —— 一部 2.413:1 的宽银幕会把浏览窗口留成一条又宽又扁的横条。
    /// <para>
    /// 没记过（这次播放没有走过整形，比如一直全屏看的）就什么都不做。全屏归 <see cref="LeaveFullscreen"/>
    /// 管，它自己记着进全屏前的矩形；这里只处理「窗口形状被画面改过」那一头，所以全屏时不插一脚。
    /// </para>
    /// <para>
    /// <b>不按「用户有没有自己拖过」分档</b>（2026-09-14 用户的拍板：照样还原）。放片期间窗口是锁着画面比例的，
    /// 拖出来的形状也不属于浏览用的那一份，所以退出播放一律回到播放前 —— 行为最好预测。
    /// </para>
    /// </summary>
    public void RestoreBrowseGeometry()
    {
        if (Handle == IntPtr.Zero || Fullscreen || Native.IsIconic(Handle)) return;
        if (!HasBrowseGeometry) return;

        var was = _browse;

        // 最大化着浏览的那一次：回到取消最大化时该有的那个大小，再把最大化重新摆上 —— 而不是把窗口摆成
        // 整块屏幕那么大（那会是「还原成一个像最大化的普通窗口」，Windows 并不认为它最大化着）。
        if (was.Maximized)
        {
            Native.SetWindowPos(
                Handle, Native.HwndTop,
                was.Bounds.Left, was.Bounds.Top, was.Bounds.Width, was.Bounds.Height,
                Native.SwpNoZOrder | Native.SwpNoActivate);
            Native.ShowWindow(Handle, Native.SwMaximize);
            Log.Info(Category, $"退出播放，窗口还原并最大化 {was.Bounds.Width}x{was.Bounds.Height}");
            return;
        }

        if (!Native.GetWindowRect(Handle, out var rect)) return;

        if (rect.Left == was.Bounds.Left && rect.Top == was.Bounds.Top
            && rect.Width == was.Bounds.Width && rect.Height == was.Bounds.Height)
        {
            return;
        }

        Native.SetWindowPos(
            Handle, Native.HwndTop,
            was.Bounds.Left, was.Bounds.Top, was.Bounds.Width, was.Bounds.Height,
            Native.SwpNoZOrder | Native.SwpNoActivate);

        Log.Info(Category,
            $"退出播放，窗口还原到播放前的 {was.Bounds.Width}x{was.Bounds.Height}"
            + $" @ {was.Bounds.Left},{was.Bounds.Top}（原来被画面整形到 {rect.Width}x{rect.Height}）");
    }

    /// <summary>
    /// 窗口化时视频有黑边: reshapes the window so its client area is exactly <see cref="PictureAspect"/>,
    /// which is what leaves mpv nothing to letterbox. The other half of 「缩放窗口时按画面比例联动」 —
    /// <c>WM_SIZING</c> only holds the shape while an edge is being dragged, so a window that was already
    /// some other shape when a film started played it with bands top and bottom until the user happened to
    /// drag an edge. Called once per file, from the same poll that learns the ratio.
    /// <para>
    /// Silently nothing in the three cases where the window's shape is not ours to choose: fullscreen and
    /// maximized both mean the edges belong to the monitor, and a minimized window has no shape to correct.
    /// The monitor's work area is the ceiling, so a 4K file cannot open a window taller than the screen it
    /// is being watched on. The measured frame rather than the one the style bits imply — see
    /// <see cref="FrameThickness"/>.
    /// </para>
    /// </summary>
    public void FitToPicture()
    {
        if (Handle == IntPtr.Zero || PictureAspect <= 0) return;
        if (Fullscreen || Native.IsZoomed(Handle) || Native.IsIconic(Handle)) return;
        if (!Native.GetWindowRect(Handle, out var bounds)) return;

        var dpi = Native.GetDpiForWindow(Handle);
        if (dpi == 0) dpi = 96;

        var frame = FrameThickness(dpi);

        var fitted = AspectLock.Fit(
            new WindowBounds(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
            PictureAspect,
            frame.Width,
            frame.Height,
            WorkArea(),
            FreeSizing ? 0 : MinimumWidth * (int)dpi / 96,
            FreeSizing ? 0 : MinimumHeight * (int)dpi / 96);

        if (fitted.Left == bounds.Left && fitted.Top == bounds.Top
            && fitted.Width == bounds.Width && fitted.Height == bounds.Height)
        {
            return;
        }

        Native.SetWindowPos(
            Handle, Native.HwndTop,
            fitted.Left, fitted.Top, fitted.Width, fitted.Height,
            Native.SwpNoZOrder | Native.SwpNoActivate);

        Log.Info(Category, $"按画面比例调整窗口 {fitted.Width}x{fitted.Height}");
    }

    /// <summary>
    /// 自检用：拖窗口边沿的时候比例真的锁住了。<c>WM_SIZING</c> 只改「打算变成多大」那个矩形、并不真的动窗口，
    /// 所以这一读把一个「右边沿往外拉 240 像素」的矩形送进这个窗口自己的消息处理里，再看送回来的是什么形状。
    /// <para>
    /// 这是这条锁唯一能在这台机器上验到的地方：真拖一次要注入指针，而这块屏上注入是被挡着的。规则本身
    /// （<see cref="AspectLock"/>）有 Core 的测试盯着，这一读盯的是它有没有真接到 <c>WM_SIZING</c> 上 ——
    /// 少接一处，屏幕上就是一个照旧随便拉的窗口，而所有的单元测试照旧全过。
    /// </para>
    /// <para>
    /// <b>自检里没有片子，所以这一读自己摆一个画面比例出来</b>（16:9，读完原样放回）：不摆的话
    /// <see cref="PictureAspect"/> 是 0，这一关就只剩「没锁的时候别乱改矩形」那一半，而「锁住了」那一半 ——
    /// 也就是它真正要盯的那一半 —— 一辈子跑不到。摆的是属性而不是真去放片子：<c>WM_SIZING</c> 只改一个矩形，
    /// 窗口一个像素都不会动。两档都验：摆着比例要按 16:9 改写并且左边沿不动，放回 0 之后同一个矩形要原样送回。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeShapeLock()
    {
        if (Handle == IntPtr.Zero) return (false, "没有窗口");
        if (!Native.GetWindowRect(Handle, out var bounds)) return (false, "读不到窗口矩形");
        if (Fullscreen || Native.IsZoomed(Handle)) return (true, "全屏或最大化，边沿归显示器");

        var dpi = Native.GetDpiForWindow(Handle);
        if (dpi == 0) dpi = 96;

        var frame = FrameThickness(dpi);
        var was = PictureAspect;

        try
        {
            PictureAspect = HomeCarousel.WindowAspect;
            var locked = Send();

            PictureAspect = 0;
            var free = Send();

            var shape = locked.Client.Height > 0 && locked.Client.Width > 0
                ? (double)locked.Client.Width / locked.Client.Height
                : 0;

            var lockedOk = locked.Claimed && Math.Abs(shape - HomeCarousel.WindowAspect) < 0.01
                && locked.Answered.Left == bounds.Left;
            var freeOk = free.Untouched;

            return (lockedOk && freeOk,
                $"摆上 {HomeCarousel.WindowAspectLabel} 之后右边沿外拉 240 → 客户区 "
                    + $"{locked.Client.Width}×{locked.Client.Height} = {shape:0.000}:1"
                    + $"（要的是 {HomeCarousel.WindowAspect:0.000}），左边沿{(locked.Answered.Left == bounds.Left ? "没动" : "被挪了")}，"
                    + $"窗口{(locked.Claimed ? "改写了这个矩形" : "没接手 —— 锁没生效")}"
                    + $"；放回「没在放片子」之后同一个矩形 {free.Answered.Width}×{free.Answered.Height}，"
                    + $"{(freeOk ? "原样送回" : "被改写了 —— 没在放片子还在锁")}");
        }
        finally
        {
            PictureAspect = was;
        }

        // 一趟 WM_SIZING：把「右边沿往外 240」那个矩形送进去（宽领头那一档，左边沿该原地不动），
        // 交回窗口改写成什么样、客户区因此多大、以及它到底有没有接手。
        (NativeRect Answered, (int Width, int Height) Client, bool Claimed, bool Untouched) Send()
        {
            var wanted = new NativeRect
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Right = bounds.Right + 240,
                Bottom = bounds.Bottom
            };

            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
            NativeRect answered;
            IntPtr claimed;
            try
            {
                Marshal.StructureToPtr(wanted, buffer, false);
                claimed = Native.SendMessage(Handle, Native.WmSizing, new IntPtr((int)ResizeEdge.Right), buffer);
                answered = Marshal.PtrToStructure<NativeRect>(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return (
                answered,
                (answered.Width - frame.Width, answered.Height - frame.Height),
                claimed != IntPtr.Zero,
                answered.Width == wanted.Width && answered.Height == wanted.Height);
        }
    }

    /// <summary>
    /// How much of the window rectangle is frame rather than client area, measured from the window itself.
    /// <para>
    /// Deliberately not <c>AdjustWindowRectExForDpi</c>'s answer, and that is the whole point:
    /// <see cref="ConfigureTitleBar"/> extends the app's content into the title bar, so the client
    /// rectangle covers the caption while <c>WS_CAPTION</c> is still in the style bits. A frame derived
    /// from those bits reserves a caption's height — 31 physical pixels at 96 dpi — that the client is in
    /// fact using, and the picture ends up that much taller than its own shape: a band top and bottom,
    /// which is the very letterboxing <see cref="FitToPicture"/> exists to remove.
    /// </para>
    /// <para>
    /// The measured difference is by definition what <c>GetClientRect</c> will report after the resize, so
    /// it is the only number the arithmetic can trust. The style calculation stays as the fallback for the
    /// one case a measurement cannot serve — a window with no client area yet.
    /// </para>
    /// </summary>
    private (int Width, int Height) FrameThickness(uint dpi)
    {
        if (Native.GetWindowRect(Handle, out var outer) && Native.GetClientRect(Handle, out var client)
            && client.Width > 0 && client.Height > 0
            && outer.Width >= client.Width && outer.Height >= client.Height)
        {
            return (outer.Width - client.Width, outer.Height - client.Height);
        }

        var style = (int)Native.GetWindowLongPtr(Handle, Native.GwlStyle);
        var frame = new NativeRect();
        Native.AdjustWindowRectExForDpi(ref frame, style, false, 0, dpi);
        return (frame.Width, frame.Height);
    }

    /// <summary>
    /// The usable rectangle of the monitor the window is on, taskbar excluded, or an empty rectangle when
    /// the OS will not say — which <see cref="AspectLock.Fit"/> reads as 「do not constrain」.
    /// </summary>
    private WindowBounds WorkArea()
    {
        var monitor = Native.MonitorFromWindow(Handle, Native.MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return default;

        var work = info.Work;
        return new WindowBounds(work.Left, work.Top, work.Right, work.Bottom);
    }

    /// <summary>
    /// Every attached screen's usable rectangle, the primary first. What <see cref="ScreenPlacement.Restore"/>
    /// needs to decide whether a remembered window still has somewhere to be — the single-monitor
    /// <see cref="WorkArea"/> above cannot answer that, because the monitor the window is on right now is
    /// wherever Windows just put it, not the one it was on last time.
    /// <para>
    /// Primary first so that a window whose screen has been unplugged lands on the main one rather than on
    /// whichever the OS happens to enumerate first. Indexed rather than enumerated for the reason spelled
    /// out in <see cref="MoveToScreen"/>: this projection throws on <c>GetEnumerator</c>.
    /// </para>
    /// <para>
    /// <c>internal</c> for the self-check, which asks the same question of the same screens: 「would a
    /// remembered window still land somewhere visible on this desktop」 is the one part of this that a unit
    /// test cannot answer, because the answer is the monitor layout.
    /// </para>
    /// </summary>
    internal static List<WindowBounds> WorkAreas()
    {
        var seats = new List<WindowBounds>();

        try
        {
            var areas = DisplayArea.FindAll();
            for (var each = 0; each < areas.Count; each++)
            {
                var work = areas[each].WorkArea;
                var seat = new WindowBounds(work.X, work.Y, work.X + work.Width, work.Y + work.Height);

                if (areas[each].IsPrimary) seats.Insert(0, seat);
                else seats.Add(seat);
            }
        }
        catch (Exception error)
        {
            // No screens listed reads as 「nothing remembered」 in Restore, which falls back to the computed
            // default — the same thing that happens on a first run.
            Log.Warn(Category, "读取屏幕工作区失败，窗口按默认尺寸开", error);
        }

        return seats;
    }

    /// <summary>
    /// The full pixel size of the monitor this window is on — the size the picture would fill at full screen,
    /// taskbar included. What the 着色器档位 rule needs for the other half of its 放大倍数
    /// (<see cref="PlaybackTicket.OutputWidth"/>), which is why it is the outer bounds rather than the work
    /// area: full-screen playback covers the taskbar.
    /// <para>
    /// 0×0 when the OS will not say, which the rule reads as 「不知道」 rather than as a number.
    /// </para>
    /// </summary>
    internal (int Width, int Height) MonitorSize()
    {
        if (Handle == IntPtr.Zero) return default;

        try
        {
            var bounds = DisplayArea
                .GetFromWindowId(Win32Interop.GetWindowIdFromWindow(Handle), DisplayAreaFallback.Nearest)
                .OuterBounds;

            return (bounds.Width, bounds.Height);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "读取显示器尺寸失败，着色器档位按「输出尺寸未知」处理", error);
            return default;
        }
    }

    /// <summary>
    /// How fast the monitor this window is on refreshes, in Hz — 0 when Windows will not say, which every rule
    /// that reads it treats as 「不知道」 rather than as a number.
    /// <para>
    /// For <see cref="Mpv.MpvOutputOptions.ResolveSync"/>: 显示同步 re-runs mpv's whole final pass once per
    /// refresh, so above about 120Hz it costs more than the judder it removes (measured — the numbers are on
    /// <c>MpvOutputOptions.HighRefreshThreshold</c>). Two calls because there is no one-call answer: the monitor
    /// handle gives a device name, the device name gives a mode.
    /// </para>
    /// <para>
    /// Whole Hz is enough. Windows reports a 59.94 mode as 59 and a 143.98 panel as 144, and the threshold this
    /// feeds sits nowhere near a rounding boundary.
    /// </para>
    /// </summary>
    internal double RefreshHz()
    {
        if (Handle == IntPtr.Zero) return 0;

        try
        {
            var monitor = Native.MonitorFromWindow(Handle, Native.MonitorDefaultToNearest);
            var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (!Native.GetMonitorInfoEx(monitor, ref info))
            {
                Log.Debug(Category, "读不到显示器设备名，帧同步按「刷新率未知」处理");
                return 0;
            }

            var device = DeviceName(ref info);
            var mode = new DeviceMode { Size = (ushort)Marshal.SizeOf<DeviceMode>() };
            if (!Native.EnumDisplaySettings(device, Native.EnumCurrentSettings, ref mode))
            {
                Log.Debug(Category, $"读不到 {device} 的当前显示模式，帧同步按「刷新率未知」处理");
                return 0;
            }

            // 0 and 1 both mean 「the hardware's own default」 in this API, which is not a rate.
            return mode.DisplayFrequency <= 1 ? 0 : mode.DisplayFrequency;
        }
        catch (Exception error)
        {
            Log.Warn(Category, "读取屏幕刷新率失败，帧同步按「刷新率未知」处理", error);
            return 0;
        }
    }

    /// <summary>
    /// <c>MONITORINFOEX.szDevice</c> as a string. The buffer is a fixed 32 wide characters with the name
    /// NUL-terminated inside it, so the tail is cut rather than trusted to be blank.
    /// </summary>
    private static unsafe string DeviceName(ref MonitorInfoEx info)
    {
        fixed (MonitorInfoEx* pinned = &info)
        {
            var name = new string(pinned->Device, 0, 32);
            var end = name.IndexOf('\0');
            return end >= 0 ? name[..end] : name;
        }
    }

    /// <summary>Minimizes the host from the playback title bar.</summary>
    public void Minimize()
    {
        if (Handle != IntPtr.Zero) Native.ShowWindow(Handle, Native.SwMinimize);
    }

    /// <summary>Toggles maximize/restore from the playback title bar.</summary>
    public void ToggleMaximize()
    {
        if (Handle == IntPtr.Zero || Fullscreen) return;
        Native.ShowWindow(Handle, IsMaximized ? Native.SwRestore : Native.SwMaximize);
    }

    /// <summary>
    /// 拖动标题栏移动窗口, first of three: remembers where the pointer took hold and where the window was,
    /// and answers whether a drag may start at all. No in fullscreen or while maximized, where the window's
    /// edges belong to the monitor rather than to the user.
    /// <para>
    /// Moved by hand, one <c>SetWindowPos</c> per pointer move, because the OS way cannot be trusted from
    /// inside a XAML island: <c>WM_NCLBUTTONDOWN</c>/<c>HTCAPTION</c> hands the gesture to DefWindowProc's
    /// modal move loop, and the press that starts it has already crossed from the island's input thread to
    /// this one — long enough for a quick click's release to be over before the loop begins. The loop then
    /// sits waiting for a button-up that has already happened, and the window follows the cursor until the
    /// next click anywhere: 「点击标题后窗口会固定在鼠标上」. A drag on this side of the boundary ends when the
    /// pointer says so, and <see cref="Dragging"/> is a field the player's own ten-hertz tick can check —
    /// where the modal loop was a state nothing outside it could see, let alone cancel.
    /// </para>
    /// </summary>
    public bool BeginDrag(NativePoint grab)
    {
        _drag = null;
        if (Handle == IntPtr.Zero || Fullscreen || IsMaximized) return false;
        if (!Native.GetWindowRect(Handle, out var bounds)) return false;

        _drag = (grab, bounds.Left, bounds.Top);
        return true;
    }

    /// <summary>Whether a title-bar drag is under way.</summary>
    public bool Dragging => _drag is not null;

    /// <summary>
    /// Keeps the window under the point of itself the drag took hold of. Silently nothing when no drag is
    /// running, so the caller can hand it every pointer move without asking first.
    /// </summary>
    public void DragTo(NativePoint pointer)
    {
        if (_drag is not { } drag) return;

        // Fullscreen mid-drag — the F key, or the bar's own ⛶ under a hand that never let go — takes the
        // geometry out of the user's hands, and one more queued move would drag that frame off its monitor.
        if (Fullscreen)
        {
            _drag = null;
            return;
        }

        Native.SetWindowPos(
            Handle,
            Native.HwndTop,
            drag.Left + (pointer.X - drag.Grab.X),
            drag.Top + (pointer.Y - drag.Grab.Y),
            0, 0,
            Native.SwpNoSize | Native.SwpNoZOrder | Native.SwpNoActivate);
    }

    /// <summary>Ends a title-bar drag. Idempotent, because the player has three ways to reach it.</summary>
    public void EndDrag() => _drag = null;

    private void EnterFullscreen()
    {
        if (Native.IsZoomed(Handle)) Native.ShowWindow(Handle, Native.SwRestore);

        var style = Native.GetWindowLongPtr(Handle, Native.GwlStyle);
        Native.GetWindowRect(Handle, out var bounds);
        _restore = (bounds, style);

        var monitor = Native.MonitorFromWindow(Handle, Native.MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info))
        {
            _restore = null;
            Log.Warn(Category, "读取显示器边界失败，全屏取消");
            return;
        }

        var stripped = (long)style & ~(long)(Native.WsCaption | Native.WsThickFrame);
        Native.SetWindowLongPtr(Handle, Native.GwlStyle, new IntPtr(stripped));

        var screen = info.Monitor;
        Native.SetWindowPos(
            Handle, Native.HwndTopMost,
            screen.Left, screen.Top, screen.Width, screen.Height,
            Native.SwpFrameChanged | Native.SwpNoActivate | Native.SwpNoCopyBits);

        // Politeness first, then the part that actually works. MarkFullscreenWindow is the documented way
        // to ask the shell to stand aside and costs nothing, but measured on this window it changes
        // nothing: the tray keeps its WS_EX_TOPMOST and its pixels. So the window joins the topmost band
        // for as long as it is fullscreen, which is what every desktop player does and the only thing
        // that puts the picture above a taskbar that will not move. WM_ACTIVATEAPP undoes it when another
        // application comes forward onto the picture, so 置顶 never outlives the fullscreen the user asked
        // for — and does not undo it for an application on another monitor, which was covering nothing.
        Native.MarkFullscreen(Handle, true);

        // And from here on, whoever comes forward is judged on the spot — see JudgeBand for why the
        // activation message alone is not enough to keep that promise.
        _judged = IntPtr.Zero;
        _band = true;
        Native.SetTimer(Handle, BandTimer, BandTimerInterval, IntPtr.Zero);

        Log.Info(Category, $"进入全屏 {screen.Width}x{screen.Height}，窗口置顶以盖住任务栏");
    }

    /// <summary>
    /// 退出全屏：把进全屏那一刻的样式和矩形原样放回来，然后<b>按当前画面比例再整形一次</b>。
    /// <para>
    /// 那第二半是 2026-09-14 用户报的「窗口模式下调整窗口大小上下会出现黑边」，而它是两件事凑成的：
    /// </para>
    /// <list type="number">
    ///   <item><b>进全屏太快了。</b>自动全屏在开播后 40 毫秒就动手（日志：`按画面比例调整窗口 1463x608`
    ///   之后 42 毫秒 `进入全屏`），而 mpv 要等多半秒才说得出真正的比例（`画面比例 1.778（mpv）`）。
    ///   那一句来的时候窗口正在全屏，<see cref="FitToPicture"/> 按设计整不了（边归显示器），
    ///   <b>于是这句修正被整个丢掉</b>。</item>
    ///   <item><b>退出全屏把这份丢掉的账翻了出来。</b>回来的矩形是进全屏前那个 —— 也就是按服务器那个
    ///   猜错的 2.413 整出来的 1463×608。窗口从此是 2.413:1 的形状放着 1.778:1 的画面，mpv 只能上下加黑边；
    ///   而 <c>WM_SIZING</c> 还会把之后每一把拖拽都锁回这个错的形状，所以「调整窗口大小上下会出现黑边」
    ///   不是拖出来的、是拖也拖不掉。</item>
    /// </list>
    /// <para>
    /// <b>为什么要在这里补，而不是让 <see cref="FitToPicture"/> 在全屏时也照做</b>：全屏时窗口的边是显示器的，
    /// 整形会被显示器尺寸立刻覆盖，白挪一下还可能让画面闪。要的只是「回到窗口化之后补上这一课」，而退出全屏
    /// 正是那一刻。顺序也必须在这之后 —— 样式先还回去，<see cref="FrameThickness"/> 量到的边框才是窗口化那一套。
    /// </para>
    /// <para>
    /// 净效果也顺带修好了另一条路：用户在窗口化下自己把窗口拖成别的形状、中途进一次全屏再出来，出来时形状会
    /// 回到画面那一份，而不是他拖的那个错的。这与 <see cref="RestoreBrowseGeometry"/> 不冲突 —— 那一头管的是
    /// 「播放结束」，这一头管的是「全屏结束」，而两个时刻的 <see cref="PictureAspect"/> 一个非零、一个已归零。
    /// </para>
    /// </summary>
    private void LeaveFullscreen()
    {
        if (_restore is not { } saved) return;
        _restore = null;

        Native.KillTimer(Handle, BandTimer);
        _judged = IntPtr.Zero;
        _band = false;

        // Before the frame goes back, so the taskbar is already above us again by the time the window
        // stops covering it and there is no frame in which neither of them owns those pixels.
        Native.MarkFullscreen(Handle, false);

        Native.SetWindowLongPtr(Handle, Native.GwlStyle, saved.Style);
        Native.SetWindowPos(
            Handle, Native.HwndNoTopMost,
            saved.Bounds.Left, saved.Bounds.Top, saved.Bounds.Width, saved.Bounds.Height,
            Native.SwpFrameChanged | Native.SwpNoActivate | Native.SwpNoCopyBits);

        // 与 EnterFullscreen 那发同款：Fill 只改视频子窗口自己，mpv 的钩子自己跟上。
        _video?.Fill();

        // 窗口化时视频有黑边: the rect just put back is the one the window had when it went fullscreen, and
        // that is not necessarily the picture's shape any more — see the class remark on this method for the
        // two ways the two come apart. Re-fitting here is the whole repair: 退出全屏之后按当前画面比例再整形一次.
        FitToPicture();

        Log.Info(Category, "退出全屏");
    }

    /// <summary>
    /// Keeps 置顶 tied to being the app in front — but only where standing aside does the window that took
    /// the foreground any good. A fullscreen window that stayed topmost after the user alt-tabbed to
    /// something on the same screen would cover the very thing they switched to; one that dropped out of the
    /// band for good would let the taskbar back over the picture. So the band follows activation, and only
    /// while fullscreen — <c>WM_ACTIVATEAPP</c> rather than <c>WM_ACTIVATE</c> because XAML's own popups are
    /// separate windows of this same process and must not count as leaving.
    /// <para>
    /// 「屏幕1全屏播放时点击屏幕2的应用，会导致屏幕1的windows任务栏覆盖在画面之上」: with two monitors that
    /// trade was one-sided. The app being switched to is on the other screen and none of it is behind our
    /// picture, so leaving the band gained nothing and cost the only thing joining it ever bought — the
    /// taskbar came straight back over the film the user is still watching. So the valve asks where the new
    /// foreground window actually is (<see cref="JudgeBand"/>) and yields only to one that our picture would
    /// otherwise cover.
    /// </para>
    /// <para>置顶 the user asked for by hand outlives all of this: the pin is not ours to undo.</para>
    /// </summary>
    private void ApplyFullscreenZOrder(bool appActive)
    {
        if (Handle == IntPtr.Zero || !Fullscreen) return;

        if (!appActive)
        {
            JudgeBand();
            return;
        }

        // Back in front, and nothing said about the window that was: the band is the picture's again.
        _judged = IntPtr.Zero;
        _band = true;
        Native.SetWindowPos(
            Handle, Native.HwndTopMost,
            0, 0, 0, 0,
            Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);
    }

    /// <summary>
    /// Whether the window now in front is one our fullscreen picture would be sitting on top of, and the
    /// band given up if it is — by both measures that can be wrong on their own: a different monitor, and
    /// no overlap with our frame. An invisible helper window parked at the origin fails the first; a window
    /// straddling both screens fails the second; either failure yields, which is the old behaviour and the
    /// safe one.
    /// <para>
    /// Asked on deactivation and then on every <see cref="Native.WmTimer"/> tick, because
    /// <c>WM_ACTIVATEAPP</c> is only sent when activation crosses this application's boundary: having kept
    /// the band for an app on the other screen, the next window to come forward — one on <em>our</em>
    /// screen, which the picture really would be covering — arrives without a word, and a judgement made
    /// about some other window a minute ago is no answer. Each tick asks afresh, so a window dragged onto
    /// the picture is answered too; the log hears about it once.
    /// </para>
    /// <para>
    /// One-way on purpose. The band is only ever given up here, never taken back: the picture climbing over
    /// a window the user has moved on to would be this same bug pointed the other way, and coming back to
    /// the app is what asks for it — which is <see cref="ApplyFullscreenZOrder"/> above.
    /// </para>
    /// </summary>
    private void JudgeBand()
    {
        // Nothing left to give up: already yielded this spell, or 置顶 by hand, which is not ours to undo.
        if (!_band || _topMost) return;

        var other = Native.GetForegroundWindow();

        // Nothing in front to judge, or one of our own — a flyout over the picture is the picture's own.
        if (other == IntPtr.Zero || SameApp(other)) return;

        if (StandsClearOfPicture(other))
        {
            if (other != _judged)
            {
                _judged = other;
                Log.Info(Category, $"前台交给 {Describe(other)}，它挡不到全屏画面，保持置顶"
                    + $"（画面 {VisibleFrameOf(Handle)}，它 {VisibleFrameOf(other)}）");
            }

            return;
        }

        _judged = other;
        _band = false;

        Native.SetWindowPos(
            Handle, Native.HwndNoTopMost,
            0, 0, 0, 0,
            Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);

        Log.Info(Category, $"前台交给 {Describe(other)}，全屏画面会挡着它，让出置顶"
            + $"（画面 {VisibleFrameOf(Handle)}，它 {VisibleFrameOf(other)}）");
    }

    /// <summary>
    /// 一扇窗看得见的那块矩形，写成日志里的一句话；量不出来就写「量不到」。
    /// <para>
    /// 让位那两行都带上它是有缘故的：2026-09-13 那趟只有 hwnd 和类名，几何是事后靠探针补量的；2026-09-14 复发
    /// 时还是得先补量一遍才知道是那 7 像素作祟。两个矩形从今往后就落在日志里 —— 下次再让位，谁挡着谁、
    /// 差多少像素，一眼能答。
    /// </para>
    /// </summary>
    private static string VisibleFrameOf(IntPtr window) =>
        Native.GetVisibleFrame(window, out var rect)
            ? $"({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})"
            : "量不到";

    /// <summary>
    /// An hwnd made legible for the log: class name and caption. The bare address is what left 「0x5040C
    /// 到底是谁」 unanswerable the last time the band was given away to a window nobody could name.
    /// </summary>
    private static string Describe(IntPtr window)
    {
        var classBuffer = new char[64];
        var classLength = Native.GetClassName(window, classBuffer, classBuffer.Length);
        var className = classLength > 0 ? new string(classBuffer, 0, classLength) : "?";

        var titleBuffer = new char[64];
        var titleLength = Native.GetWindowText(window, titleBuffer, titleBuffer.Length);
        var title = titleLength > 0 ? new string(titleBuffer, 0, Math.Min(titleLength, titleBuffer.Length)) : "";

        return title.Length > 0 ? $"0x{window:X}({className}「{title}」)" : $"0x{window:X}({className})";
    }

    /// <summary>
    /// Whether <paramref name="window"/> is one of ours. Asked of the foreground window: this process's own
    /// windows include every XAML popup, and a menu opening over the picture is not the user leaving it.
    /// </summary>
    internal static bool SameApp(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;

        Native.GetWindowThreadProcessId(window, out var process);
        return process == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// Whether <paramref name="other"/> is somewhere our fullscreen frame could not be hiding it. Three
    /// questions, in the order they can be got wrong:
    /// <list type="number">
    /// <item>它有没有像素在屏上。拿走前台的不都是看得见的窗口：最小化的窗口矩形住在 (−32000,−32000) 的影子
    /// 世界里（点任务栏图标把它恢复的那一拍，前台已经交过去了、窗口还在那儿），隐藏的辅助窗（Telegram、
    /// qBittorrent 这类应用都养着几扇）矩形又常常正停在主屏原点附近。按几何它们都「压着画面」，实际上一寸
    /// 像素都不欠我们 —— 给它们让位，换来的唯一观众就是任务栏。「屏幕1全屏播放时点击屏幕2的telegram和
    /// qbittorrent会唤出屏幕1的windows任务栏」（2026-09-13 晚）的日志里，反复让位的 0x5040C 与 0x103EA
    /// 正是这类窗口，而同屏可见的那扇（0x1104C8）每次都正确地保持了置顶。</item>
    /// <item>是否在另一块屏幕 <em>且</em> 与画面矩形不相交 —— 即全屏这层根本盖不着它。<b>矩形按看得见的
    /// 边框量</b>（<see cref="Native.GetVisibleFrame"/>），不是 <c>GetWindowRect</c> 那圈外面多包的：2026-09-14
    /// 第二次报上来的同一句话，病根就在这里 —— 那扇贴在屏幕二左沿的窗（0x305EE）可见的左边是 2560，
    /// <c>GetWindowRect</c> 报的却是 2553，凭空多出 7px 伸进屏幕一的画面，规则于是判它会挡着、让出置顶，
    /// 任务栏就爬回来了。</item>
    /// <item>量不出来就当挡着：没有窗口、是我们自己、或系统不肯给几何 —— 「让位」是只会赔上画面（给任务栏）
    /// 的那种错，「不让」顶多让用户刚切过去的窗口被盖着。</item>
    /// </list>
    /// </summary>
    private bool StandsClearOfPicture(IntPtr other)
    {
        if (other == IntPtr.Zero || other == Handle) return false;

        if (PutsNoPixelsOnScreen(other)) return true;

        if (!Native.GetVisibleFrame(Handle, out var picture)
            || !Native.GetVisibleFrame(other, out var window))
        {
            return false;
        }

        return StandsClear(
            picture,
            Native.MonitorFromWindow(Handle, Native.MonitorDefaultToNearest),
            window,
            Native.MonitorFromWindow(other, Native.MonitorDefaultToNearest));
    }

    /// <summary>
    /// Whether <paramref name="window"/>, though it may hold the foreground, has no pixels anywhere the user
    /// could look: minimized, or hidden altogether. The question the geometry cannot answer — both of those
    /// windows carry rectangles that say 「covering the picture」 — and the one the taskbar-yield rule was
    /// missing. Internal static so the self-check can put real windows to it without a second monitor.
    /// </summary>
    internal static bool PutsNoPixelsOnScreen(IntPtr window) =>
        window == IntPtr.Zero || Native.IsIconic(window) || !Native.IsWindowVisible(window);

    /// <summary>
    /// The rule of <see cref="StandsClearOfPicture"/> over geometry alone, so the self-check can put both
    /// halves of 「屏幕1全屏播放时点击屏幕2的应用」 to it without a second monitor to arrange them on and
    /// without waiting for a real activation to happen. Monitors are compared, never dereferenced, so the
    /// two screens can be any two distinct values.
    /// </summary>
    internal static bool StandsClear(
        NativeRect picture, IntPtr pictureScreen, NativeRect window, IntPtr windowScreen)
    {
        if (pictureScreen == IntPtr.Zero || windowScreen == IntPtr.Zero || pictureScreen == windowScreen)
            return false;

        return window.Right <= picture.Left || window.Left >= picture.Right
            || window.Bottom <= picture.Top || window.Top >= picture.Bottom;
    }

    /// <summary>
    /// 自检：the custom title bar's four colours beside the four the window wears once it is no longer the
    /// one in front, or null while there is no custom title bar. 「点击其他窗口或桌面后会变色」 is a claim
    /// about those four twins being set at all — an unset twin does not inherit its active partner, it falls
    /// back to the system's own inactive caption — and the difference only shows while the window is not the
    /// one being looked at, which is exactly when no screenshot of it is being taken.
    /// </summary>
    internal (Color? Fill, Color? InactiveFill, Color? Text, Color? InactiveText,
        Color? Button, Color? InactiveButton, Color? Glyph, Color? InactiveGlyph)? TitleBarColours =>
        _appWindow?.TitleBar is { } bar
            ? (bar.BackgroundColor, bar.InactiveBackgroundColor,
               bar.ForegroundColor, bar.InactiveForegroundColor,
               bar.ButtonBackgroundColor, bar.ButtonInactiveBackgroundColor,
               bar.ButtonForegroundColor, bar.ButtonInactiveForegroundColor)
            : null;

    /// <summary>
    /// Creates the window, seats it, and shows it.
    /// </summary>
    /// <param name="maximized">Open maximized — <c>--maximized</c>, or what the last session was left in.</param>
    /// <param name="screen">Which monitor, see <see cref="ScreenPlacement"/>.</param>
    /// <param name="saved">
    /// Where the last session left this window, in desktop physical pixels, or <c>default</c> for 「never
    /// recorded」. Honoured only when <paramref name="screen"/> is <see cref="ScreenPlacement.WhereverWindows"/>
    /// — an ordinary launch. A run that named its screen gets the computed default, so the self-check's
    /// geometry readings stay comparable between runs.
    /// </param>
    public void Show(bool maximized, int screen = ScreenPlacement.WhereverWindows, WindowBounds saved = default)
    {
        var instance = Native.GetModuleHandle(null);
        EnsureClassRegistered(instance);

        // Sized in physical pixels for this monitor's DPI, so a 150% display does not open a window
        // two thirds of the intended size.
        var dpi = 96u;
        var bounds = new NativeRect { Left = 0, Top = 0, Right = DefaultWidth, Bottom = DefaultHeight };

        Handle = Native.CreateWindowEx(
            0,
            ClassName,
            AppIdentity.Title,
            Native.WsOverlappedWindow | Native.WsClipChildren,
            unchecked((int)0x80000000), // CW_USEDEFAULT
            unchecked((int)0x80000000),
            DefaultWidth,
            DefaultHeight,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException($"创建主窗口失败，错误码 {Marshal.GetLastWin32Error()}");

        Windows[Handle] = this;

        // Which screen, before anything is measured: 「how big is the default window here」 is a question about
        // a monitor, and until the window has been moved onto the one it is to open on, the answer below
        // would be the other monitor's.
        var work = MoveToScreen(screen);

        dpi = Native.GetDpiForWindow(Handle);
        if (dpi is > 0 and not 96)
            Native.AdjustWindowRectExForDpi(ref bounds, Native.WsOverlappedWindow, false, 0, dpi);

        // 上次关掉时的尺寸和位置，如果记过的话。**只有普通启动读它** —— 命令行点了名的那一次（`--screen`，
        // 自检默认就带着）要的是每次都一样的几何，而自检报告里量的正是客户区尺寸和 16:9 那个比例；跟着用户上次
        // 拉到多大走，那几行读数就再也没有可比的基准了。
        //
        // 记下来的是物理像素，所以尺寸不用按 dpi 缩放；下限要缩，它是按 96 写的。
        var restored = screen == ScreenPlacement.WhereverWindows
            ? ScreenPlacement.Restore(
                saved,
                WorkAreas(),
                MinimumWidth * (int)dpi / 96,
                MinimumHeight * (int)dpi / 96)
            : default;

        if (restored.Width > 0)
        {
            Native.SetWindowPos(
                Handle, Native.HwndTop,
                restored.Left, restored.Top, restored.Width, restored.Height,
                Native.SwpNoZOrder | Native.SwpNoActivate);

            Log.Info(
                Category,
                saved.Left == restored.Left && saved.Top == restored.Top
                    && saved.Width == restored.Width && saved.Height == restored.Height
                    ? $"沿用上次的窗口 {restored.Width}x{restored.Height} @ {restored.Left},{restored.Top}"
                    : $"上次的窗口 {saved.Width}x{saved.Height} @ {saved.Left},{saved.Top} 放不进现在的桌面，"
                      + $"改成 {restored.Width}x{restored.Height} @ {restored.Left},{restored.Top}");
        }
        else if (ScreenPlacement.Centre(work, bounds.Width, bounds.Height) is { Width: > 0 } seat)
        {
            Native.SetWindowPos(
                Handle, Native.HwndTop,
                seat.Left, seat.Top, seat.Width, seat.Height,
                Native.SwpNoZOrder | Native.SwpNoActivate);
        }
        else if (dpi is > 0 and not 96)
        {
            Native.SetWindowPos(
                Handle, Native.HwndTop, 0, 0,
                bounds.Width, bounds.Height,
                Native.SwpNoMove | Native.SwpNoZOrder | Native.SwpNoActivate);
        }

        ApplyWindowsChrome();
        CreateIsland();
        InstallKeyboardFallback();

        Native.ShowWindow(Handle, maximized ? Native.SwMaximize : Native.SwShow);
        Native.SetForegroundWindow(Handle);

        // Once more, now that the window has its final client area and the island has been shown.
        // ConfigureTitleBar already claimed the strip, but that ran before any of this: a window created at
        // its final size gets no WM_SIZE out of ShowWindow, so without this call the only refresh the caption
        // strip would ever get is the user's first resize.
        UpdateTitleBarRegions();

        // 基线。拖过一次边、最大化过一次之后这一份会被盖掉，可从开窗到那一刻之间关掉窗口也得记住些什么 ——
        // 而 --maximized 开起来的那一次这里只抬得起那一位，尺寸留空（0 就是「还没记过」），下次照默认尺寸开、
        // 照旧最大化，这正是对的。
        RememberPlacement();

        Log.Info(Category, $"主窗口已创建 hwnd=0x{Handle:X} dpi={dpi}");
    }

    /// <summary>
    /// Moves the window onto the screen <paramref name="screen"/> asks for and hands back that screen's work
    /// area, for the sizing that follows. An empty rectangle — and nothing moved — for the ordinary case where
    /// the placement is Windows's to choose, or where the request names a screen this desktop does not have.
    /// <para>
    /// The origin only, and the size afterwards, because the size is computed at the target monitor's scale
    /// and this call is what makes <c>GetDpiForWindow</c> answer for that monitor at all.
    /// </para>
    /// <para>See <see cref="ScreenPlacement"/> for what asks for this and why.</para>
    /// </summary>
    private WindowBounds MoveToScreen(int screen)
    {
        if (screen == ScreenPlacement.WhereverWindows) return default;

        try
        {
            var areas = DisplayArea.FindAll();

            // Indexed rather than a LINQ Select: this is a WinRT IVectorView projection, and asking it for an
            // enumerator throws InvalidCastException (「Specified cast is not valid」, from IObjectReference.As
            // on IEnumerable). Count and the indexer are the two members that do work, so those are the two
            // this uses — the first run of this code took the catch below and opened on the primary anyway.
            var primary = new bool[areas.Count];
            for (var each = 0; each < primary.Length; each++) primary[each] = areas[each].IsPrimary;

            var index = ScreenPlacement.Choose(screen, primary);
            if (index < 0)
            {
                Log.Info(Category, $"要求开在第 {screen} 块屏幕，这台机器上没有，交给系统摆");
                return default;
            }

            var work = areas[index].WorkArea;

            Native.SetWindowPos(
                Handle, Native.HwndTop,
                work.X, work.Y, 0, 0,
                Native.SwpNoSize | Native.SwpNoZOrder | Native.SwpNoActivate);

            Log.Info(
                Category,
                $"主窗口开在第 {index + 1} 块屏幕（共 {areas.Count} 块），工作区 {work.Width}x{work.Height} @ {work.X},{work.Y}");

            return new WindowBounds(work.X, work.Y, work.X + work.Width, work.Y + work.Height);
        }
        catch (Exception error)
        {
            // A window on the wrong screen is a nuisance; no window at all is not an option.
            Log.Warn(Category, "按屏幕摆放窗口失败，交给系统摆", error);
            return default;
        }
    }

    /// <summary>
    /// Which screen the window is on, of how many there are, as the sentence the self-check prints.
    /// 「跑测试的时候能不能在第二屏幕跑」 makes this something the report has to say out loud: half the probes
    /// in it sample the desktop, and a reader has to know which desktop that was.
    /// </summary>
    public string ScreenSummary
    {
        get
        {
            if (Handle == IntPtr.Zero) return "没有窗口";

            try
            {
                var areas = DisplayArea.FindAll();
                var here = DisplayArea.GetFromWindowId(
                    Win32Interop.GetWindowIdFromWindow(Handle), DisplayAreaFallback.Nearest);

                var index = 0;
                for (var screen = 0; screen < areas.Count; screen++)
                    if (areas[screen].DisplayId.Value == here.DisplayId.Value) index = screen + 1;

                var work = here.WorkArea;
                return $"共 {areas.Count} 块，本次在第 {index} 块（{(here.IsPrimary ? "主屏" : "副屏")}）"
                    + $"，工作区 {work.Width}x{work.Height} @ {work.X},{work.Y}";
            }
            catch (Exception error)
            {
                Log.Warn(Category, "读取显示器信息失败", error);
                return "问不出显示器";
            }
        }
    }


    /// <summary>Brings the window forward when a second launch signals this instance.</summary>
    public void Activate()
    {
        if (Handle == IntPtr.Zero) return;

        if (Native.IsIconic(Handle)) Native.ShowWindow(Handle, Native.SwRestore);
        Native.BringWindowToTop(Handle);
        Native.SetForegroundWindow(Handle);
    }

    public void Close()
    {
        if (Handle != IntPtr.Zero) Native.DestroyWindow(Handle);
    }

    /// <summary>
    /// Caption colours, Win11's rounded corners and Mica on the frame, asked of DWM directly. The frame is
    /// the one place a backdrop is free — DWM paints the non-client area itself, so this costs nothing in
    /// the client area and is set unconditionally, unlike the island's own backdrop.
    /// <para>
    /// 深浅跟着主题：浅色主题下 immersive dark mode 要关掉，否则窗口边框和阴影仍是给深色窗口画的那一档，
    /// 压在一片白的界面边上是一条黑线。
    /// </para>
    /// </summary>
    private void ApplyWindowsChrome()
    {
        var dark = ThemeHost.Current.IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, Native.DwmUseImmersiveDarkMode, ref dark, sizeof(int));

        var corners = Native.DwmCornerRound;
        Native.DwmSetWindowAttribute(Handle, Native.DwmWindowCornerPreference, ref corners, sizeof(int));

        // Ignored with a non-zero HRESULT on Win10, where the attribute does not exist. Nothing to
        // handle: the caption is then the flat dark fill immersive dark mode already gave it.
        var backdrop = Native.DwmBackdropMica;
        Native.DwmSetWindowAttribute(Handle, Native.DwmSystemBackdropType, ref backdrop, sizeof(int));
    }

    /// <summary>
    /// Extends the XAML island into the title bar while retaining the operating system's caption
    /// buttons and resize frame. The non-client source is important for a DesktopWindowXamlSource:
    /// unlike a framework Window there is no SetTitleBar helper to turn a XAML rectangle into a drag
    /// region, so the rectangles are declared directly against the host HWND.
    /// </summary>
    private void ConfigureTitleBar()
    {
        try
        {
            var id = Win32Interop.GetWindowIdFromWindow(Handle);
            _appWindow = AppWindow.GetFromWindowId(id);

            var titleBar = _appWindow?.TitleBar;
            if (titleBar is not null)
            {
                titleBar.ExtendsContentIntoTitleBar = true;

                // 跟着已经在身上的那个意图走，不写死 Standard。播放态可能在窗口建出来**之前**就定下了 ——
                // 独立播放窗口就是这么建的（`new HostWindow { Content = page }` 之后立刻
                // `PlaybackTitleBar = true`，而 CreateWindowEx 要到 Show 里才发生，那一句 setter 因此
                // 撞上「还没有句柄」静默早退，只把它记进了 _playbackTitleBar 字段）。这里写死 Standard
                // 就是把它抹掉：框架于是照旧保留标题栏那 32 像素、照旧在右上角画三颗系统按钮，而播放态的
                // 区域声明说那三颗「哪儿都不在」（见 UpdateTitleBarRegions）—— 三颗画着、点上去什么也不
                // 接住，就是 2026-09-14 的「窗口化时右上角的最小化 最大化 关闭点了没反应」。
                titleBar.PreferredHeightOption = _playbackTitleBar
                    ? TitleBarHeightOption.Collapsed
                    : TitleBarHeightOption.Standard;

                PaintCaption(titleBar);

                // 换主题时再画一遍。标题栏那一条是 Win32 的非客户区，画刷改不到它 —— ThemeHost 把
                // EgWindowBrush 的颜色改掉，客户区当场跟着变，这一条不会，只能收到通知后自己再设一遍。
                _repaint = _ =>
                {
                    ApplyWindowsChrome();
                    PaintCaption(titleBar);
                };
                ThemeHost.Changed += _repaint;
            }

            _nonClient = InputNonClientPointerSource.GetForWindowId(id);
            UpdateTitleBarRegions();
        }
        catch (Exception error)
        {
            // The shell can still run with the ordinary caption on older Windows App Runtime builds.
            Log.Warn(Category, "自定义标题栏不可用，保留系统标题栏", error);
            _appWindow = null;
            _nonClient = null;
        }
    }

    /// <summary>
    /// 主页那张大图有没有铺到窗口顶边底下 —— 铺到了，右上角那三颗系统按钮就站在剧照上。
    /// <para>
    /// 立成字段而不是每次现问页面：这三颗的颜色是 Win32 非客户区的属性，换主题时得连同这件事一起再设一遍，
    /// 而那时候动手的是 <c>ThemeHost.Changed</c> 的回调，手上没有页面可问。
    /// </para>
    /// </summary>
    private bool _captionOnScrim;

    /// <summary>
    /// 外壳说「顶上那一块现在是（不是）一张剧照」，见 <c>ShellPage.PaintTitleInk</c>。同一个值再说一遍不
    /// 重画：这三颗按钮归系统画，多设一次是一次跨进程调用。
    /// </summary>
    internal void SetCaptionOnScrim(bool onScrim)
    {
        if (_captionOnScrim == onScrim) return;

        _captionOnScrim = onScrim;
        if (_appWindow?.TitleBar is { } bar) PaintCaption(bar);
    }

    /// <summary>
    /// The caption's colours, taken from the current theme so the strip at the top matches the window under
    /// it in all six of them. Mirrored by <c>SettingsWindow.PaintCaption</c>, which does the same for the
    /// only other window the app has.
    /// <para>
    /// 悬停和按下那两档故意用半透明的白／黑而不是主题里的某个面：它们压在标题栏底色上，靠透明度叠出来的
    /// 那一档在六套主题里都对。
    /// </para>
    /// <para>
    /// 那三颗按钮的墨还多一个条件：主页那张大图铺到窗口顶边之后它们就站在剧照上，见
    /// <see cref="SetCaptionOnScrim"/>。
    /// </para>
    /// </summary>
    private void PaintCaption(AppWindowTitleBar titleBar)
    {
        var theme = ThemeHost.Current;

        // 压在剧照上时不跟主题走：图顶上那层暗罩是黑的，而浅色主题（晴昼）的墨是深色 —— 深墨画在上面就是
        // 三颗看不见的按钮，其中一颗是关闭。#F3F5F8 是 Palette.xaml 里 EgOnScrimBrush 那一支，图上所有
        // 的字用的都是它。
        var onScrim = _captionOnScrim;

        titleBar.BackgroundColor = ThemeHost.ToColor(theme.Colors.Window);
        titleBar.ForegroundColor = ThemeHost.ToColor(theme.Colors.Text);
        titleBar.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonForegroundColor = onScrim
            ? Color.FromArgb(255, 0xF3, 0xF5, 0xF8)
            : ThemeHost.ToColor(theme.Colors.Text);

        // 悬停和按下那两层跟着底走，不跟着主题走：底是黑的（深色主题，或者压在剧照上）就叠白，反之叠黑。
        titleBar.ButtonHoverBackgroundColor = theme.IsDark || onScrim
            ? Color.FromArgb(35, 255, 255, 255)
            : Color.FromArgb(25, 0, 0, 0);
        titleBar.ButtonPressedBackgroundColor = theme.IsDark || onScrim
            ? Color.FromArgb(55, 255, 255, 255)
            : Color.FromArgb(45, 0, 0, 0);

        // 「点击其他窗口或桌面后会变色」. Every colour above has an Inactive twin, and a twin left
        // unset does not inherit its active partner — it falls back to the system's own idea of an
        // inactive caption, which under a light system theme is a pale grey strip across the top
        // right of a window whose every other pixel is the theme's window colour. The app has one
        // appearance, so the twins are the same colours: what the window looks like is not a function
        // of which window the user last clicked. The buttons keep their ink for the same reason —
        // greyed ones read as disabled, and they are not: clicking close on an inactive window closes it.
        titleBar.InactiveBackgroundColor = titleBar.BackgroundColor;
        titleBar.InactiveForegroundColor = titleBar.ForegroundColor;
        titleBar.ButtonInactiveBackgroundColor = titleBar.ButtonBackgroundColor;
        titleBar.ButtonInactiveForegroundColor = titleBar.ButtonForegroundColor;
    }

    /// <summary>
    /// Declares which parts of the top strip belong to the window frame and which to the page, for whichever
    /// of the two title bars is in force. Called at startup, on every resize, and on each transition.
    /// <para>
    /// Every one of the five region kinds is assigned on every pass, and 「nothing here」 is stated as a
    /// zero-area rectangle rather than by clearing the kind. That is not tidiness: taking a declaration back
    /// — <c>ClearRegionRects</c>, or <c>SetRegionRects</c> with an empty array — leaves the window answering
    /// 客户区 for the whole strip, and re-declaring the caption afterwards does not bring the drag region
    /// back. It cost a real bug: the strip dragged the window at startup and stopped after the first
    /// playback, because the transition cleared the regions before repartitioning them.
    /// </para>
    /// </summary>
    private void UpdateTitleBarRegions()
    {
        if (_nonClient is null || Handle == IntPtr.Zero) return;

        Native.GetClientRect(Handle, out var client);
        var dpi = Native.GetDpiForWindow(Handle);
        if (dpi == 0) dpi = 96;

        var width = Math.Max(0, client.Width);
        var height = TitleBarHeight * (int)dpi / 96;
        var fallbackInset = SystemButtonsWidth * (int)dpi / 96;
        var titleInset = _appWindow?.TitleBar?.RightInset ?? 0.0;
        var systemInset = Math.Max(fallbackInset, (int)Math.Ceiling(titleInset));
        var dragRight = Math.Max(0, width - Math.Max(systemInset, fallbackInset));

        // Playback claims no caption at all: the whole top strip stays XAML input, so hovering it reveals
        // the chrome, the three window commands get their clicks, and the page's own drag moves the window.
        // Passthrough is stated as well as Caption withdrawn because the framework reserves the strip while
        // ExtendsContentIntoTitleBar is on, and a passthrough rect is how a window says it wants that space
        // back.
        //
        // Browsing is the other way round: the strip is caption drag space apart from the rectangles the
        // shell draws its buttons in — the five on the left, the account one at the right end (it moved
        // into the strip 2026-09-09 when its own row below went away) — which are passthrough for the same
        // reason: without them the frame answers 标题栏 there and the buttons never see a click.
        var holes = _playbackTitleBar ? [] : HolePixels((int)dpi, dragRight, height);

        var strip = _playbackTitleBar && width > 0 && height > 0
            ? new RectInt32(0, 0, width, height)
            : Empty;

        _nonClient.SetRegionRects(NonClientRegionKind.Passthrough,
            _playbackTitleBar ? [strip] : holes);
        _nonClient.SetRegionRects(
            NonClientRegionKind.Caption,
            _playbackTitleBar ? [Empty] : CaptionAround(holes, dragRight, height));

        // 右上角的三颗系统按钮. The framework draws them — the colours in ConfigureTitleBar are theirs — but
        // it does not hit-test them for a window that declares regions of its own: what this source says
        // about a region kind replaces what the framework said, and a zero-area Minimize means 「there is no
        // minimise button anywhere」. The buttons then still paint, and the press goes straight through to
        // whatever XAML is underneath — 「右上角的最小化 最大化 关闭 点不了」. So browsing states the three
        // columns of the reserved inset itself, and playback — which draws its own window commands in the
        // client area — states none.
        var (minimise, maximise, close) = _playbackTitleBar
            ? (Empty, Empty, Empty)
            : SystemButtonRects(dragRight, systemInset, height);

        _nonClient.SetRegionRects(NonClientRegionKind.Minimize, [minimise]);
        _nonClient.SetRegionRects(NonClientRegionKind.Maximize, [maximise]);
        _nonClient.SetRegionRects(NonClientRegionKind.Close, [close]);
    }

    /// <summary>
    /// The three caption buttons as three equal columns of the width the framework reserved for them, in
    /// physical pixels, in the order Windows draws them: minimise, maximise, close. The last takes the
    /// remainder, so a reserved width that does not divide by three leaves no dead column against the
    /// window's right edge instead of a close button.
    /// </summary>
    private static (RectInt32 Minimise, RectInt32 Maximise, RectInt32 Close) SystemButtonRects(
        int left, int reserved, int height)
    {
        var column = reserved / 3;
        if (reserved <= 0 || height <= 0 || column <= 0) return (Empty, Empty, Empty);

        return (
            new RectInt32(left, 0, column, height),
            new RectInt32(left + column, 0, column, height),
            new RectInt32(left + (column * 2), 0, reserved - (column * 2), height));
    }

    /// <summary>
    /// The rectangles the shell claimed, in physical pixels, each clipped to the draggable part of the
    /// strip. Outward-rounded — floor the near edges, ceil the far ones — so no half pixel of a button is
    /// left answering 标题栏, which at fractional scaling is the difference between an arrow that clicks and
    /// an arrow whose top row drags the window.
    /// </summary>
    private RectInt32[] HolePixels(int dpi, int dragRight, int height)
    {
        if (dragRight <= 0 || height <= 0 || _holes.Count == 0) return [];

        var scale = dpi / 96.0;
        var clipped = new List<RectInt32>(_holes.Count);

        foreach (var hole in _holes)
        {
            var left = Math.Clamp((int)Math.Floor(hole.X * scale), 0, dragRight);
            var top = Math.Clamp((int)Math.Floor(hole.Y * scale), 0, height);
            var right = Math.Clamp((int)Math.Ceiling((hole.X + hole.Width) * scale), left, dragRight);
            var bottom = Math.Clamp((int)Math.Ceiling((hole.Y + hole.Height) * scale), top, height);

            if (right > left && bottom > top) clipped.Add(new RectInt32(left, top, right - left, bottom - top));
        }

        return [.. clipped];
    }

    /// <summary>
    /// The caption stated as the pieces around the holes, in physical pixels: one per gap between them,
    /// plus the two full-width bands at the ends. Never an empty array; see
    /// <see cref="UpdateTitleBarRegions"/> for what withdrawing a declaration costs.
    /// </summary>
    private static RectInt32[] CaptionAround(RectInt32[] holes, int dragRight, int height)
    {
        if (dragRight <= 0 || height <= 0) return [Empty];
        if (holes.Length == 0) return [new RectInt32(0, 0, dragRight, height)];

        // 按左沿排好再切：外壳报上来的两个洞一个在最左、一个在最右，可顺序是「谁先量出来谁先到」，不排
        // 一下的话切成的那几块有交叠的可能 —— 一点声明了两次，两种区域都不认。
        var ordered = holes.OrderBy(hole => hole.X).ToArray();

        var pieces = new List<RectInt32>(holes.Length + 3);
        var lastRight = 0;

        foreach (var hole in ordered)
        {
            // 洞之间的整条竖带（第一个洞前面那条从 0 起）。洞在带子里留下的上下两截比重新开几个矩形便宜
            // 也干净：它们的左右沿就是带子的，不会跟旁边那条叠上。
            var from = Math.Min(lastRight, hole.X);
            if (hole.X > from) pieces.Add(new RectInt32(from, 0, hole.X - from, height));
            if (hole.Y > 0) pieces.Add(new RectInt32(hole.X, 0, hole.Width, hole.Y));

            var bottom = hole.Y + hole.Height;
            if (height > bottom) pieces.Add(new RectInt32(hole.X, bottom, hole.Width, height - bottom));

            lastRight = Math.Max(lastRight, hole.X + hole.Width);
        }

        if (dragRight > lastRight) pieces.Add(new RectInt32(lastRight, 0, dragRight - lastRight, height));

        return [.. pieces];
    }

    // ---- 键盘兜底 -------------------------------------------------------------------
    //
    // 「新增esc退出全屏 按空格开始播放」（2026-09-15）。XAML 的键路只在 Win32 键盘焦点落进岛里时才
    // 响；全屏播放时前台被别的应用抢走再回来、或焦点落在宿主窗口与视频子窗口上，键就被 DefWindowProc
    // 吞掉 —— 用户日志里十三天「按键/空格」唤醒为零，全是这条。WH_KEYBOARD 是线程钩子：键只有在被
    // 送进本线程队列时它才响（焦点在别家时一次都不响），所以它天生不抢别人的键，只兜「键到了我们家、
    // XAML 却收不到」的底。

    /// <summary>
    /// 装线程钩子。Show 里岛建好之后调一次；失败只记一行 —— 兜底缺了是「焦点掉出岛后按键失灵」，
    /// 不是「按键全灭」，XAML 主路还活着，自检「快捷键派发」一格会替这里喊。
    /// </summary>
    private void InstallKeyboardFallback()
    {
        if (_keyboardHook != IntPtr.Zero || _keyboardProcedure is not null) return;

        _keyboardProcedure = KeyboardHookProc;
        _keyboardHook = Native.SetWindowsHookEx(Native.WhKeyboard, _keyboardProcedure, IntPtr.Zero, Native.GetCurrentThreadId());
        if (_keyboardHook == IntPtr.Zero)
            Log.Warn(Category, $"装键盘兜底钩子失败，错误码 {Marshal.GetLastWin32Error()}（焦点掉出 XAML 岛后 Esc/空格将失灵）");
    }

    private void UninstallKeyboardFallback()
    {
        if (_keyboardHook == IntPtr.Zero) return;

        Native.UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
        _keyboardProcedure = null;
    }

    /// <summary>
    /// 钩子过程。只认「全新的按下」（抬起、自动重复、Alt 组合一概放行），只问空格和 Esc 两颗：
    /// 焦点必须在本窗口的树里、又不在岛里（岛里 XAML 自己收，两条路永远只有一条出键），页面也点头
    /// （菜单开着、正在打字时它让路），这一下才被兜住并吃掉 —— 返回 1 掐断钩子链和这颗键，
    /// DefWindowProc 再没有机会把它吞进肚里。
    /// </summary>
    private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        const long KfUp = 0x80000000;
        const long KfRepeat = 0x40000000;
        const long KfAltDown = 0x20000000;
        const int VkSpace = 0x20;
        const int VkEscape = 0x1B;

        if (code >= 0)
        {
            var bits = lParam.ToInt64();
            var vk = unchecked((int)(long)wParam);
            if ((bits & KfUp) == 0 && (bits & KfRepeat) == 0 && (bits & KfAltDown) == 0
                && vk is VkSpace or VkEscape
                && KeyFellThroughTheIsland(vk)
                && _win32Keys?.WantsKey(vk) == true)
            {
                _win32Keys!.Handle(vk);
                return 1;
            }
        }

        return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    /// <summary>
    /// 这一键是不是「发给了我们、XAML 却收不到」：焦点 HWND 得在本窗口的树里（焦点在别家窗口上时
    /// 这个线程钩子根本不会被叫到，这道闸只是多一层保险），且不在岛里 —— 焦点在岛里时 XAML 那两条
    /// 键路活着，兜底路一步都不越。问自己的线程号，不是 0：0 问到的是前台线程，而前台可能是别家。
    /// </summary>
    private bool KeyFellThroughTheIsland(int vk)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!Native.GetGUIThreadInfo(Native.GetCurrentThreadId(), ref info)) return false;

        var focus = info.FocusWindow;
        if (focus == IntPtr.Zero) return false;
        if (focus != Handle && !Native.IsChild(Handle, focus)) return false;
        if (IslandHandle != IntPtr.Zero && (focus == IslandHandle || Native.IsChild(IslandHandle, focus))) return false;

        return true;
    }

    private void CreateIsland()
    {
        Native.GetClientRect(Handle, out var client);

        _source = new DesktopWindowXamlSource();
        _source.Initialize(Win32Interop.GetWindowIdFromWindow(Handle));
        _source.Content = _content;
        _source.SiteBridge.MoveAndResize(new RectInt32(0, 0, client.Width, client.Height));
        _source.SiteBridge.Show();

        ApplyBackdrop();
        ConfigureTitleBar();

        // The island has to sit above the video child, and mpv's window is created later and starts
        // at the top of the sibling order. Inserting the island at the top here is only half the
        // story; the player re-asserts it when the video surface appears.
        var island = Win32Interop.GetWindowFromWindowId(_source.SiteBridge.WindowId);
        IslandHandle = island;
        Native.SetWindowPos(
            island, Native.HwndTop, 0, 0, 0, 0,
            Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);

        HookIslandCursor(island);

        Log.Info(Category, $"XAML 岛已创建 hwnd=0x{island:X} 客户区 {client.Width}x{client.Height}");
    }

    /// <summary>
    /// Steps in front of the island's window procedure, for <see cref="CursorHidden"/>'s sake and for nothing
    /// else. Done once, at creation, rather than on the first hide: a swap that only ever happens mid-film is
    /// a swap no run of the self-check would have exercised.
    /// </summary>
    private void HookIslandCursor(IntPtr island)
    {
        if (island == IntPtr.Zero || _islandProcedure != IntPtr.Zero) return;

        Islands[island] = this;

        var ours = Marshal.GetFunctionPointerForDelegate(IslandProcedure);
        _islandProcedure = Native.SetWindowLongPtr(island, Native.GwlpWndProc, ours);

        if (_islandProcedure == IntPtr.Zero)
        {
            // Nothing to hand the other messages on to means nothing may be intercepted either: the island
            // draws the whole UI, and a procedure that answered for it would be a black window.
            Islands.Remove(island);
            Log.Warn(Category, "接管 XAML 岛的光标消息失败，鼠标将不会自动隐藏");
            return;
        }

        // 第十六报：见证的收音。子类化成功才注册 —— WM_INPUT 的入口是 IslandDispatch，没有它，
        // 注册了也没人接。INPUTSINK 是要害：全屏播放的窗口没有焦点，按默认注册（只送前台）见证
        // 会在最有用的时刻恰好缺席。失败不重试：Witness.Ready 是假，PollPointer 退回形状启发式，
        // 日志在这里说清楚是哪一种缺席。
        var devices = new[]
        {
            new Native.RawInputDevice
            {
                UsagePage = Native.UsagePageGenericDesktop,
                Usage = Native.UsageMouse,
                Flags = Native.RidevInputsink,
                Target = island
            }
        };

        Witness.Ready = Native.RegisterRawInputDevices(devices, (uint)devices.Length, Marshal.SizeOf<Native.RawInputDevice>());
        Log.Info(Category, Witness.Ready
            ? $"真实输入见证已收音（岛 0x{island:X}，INPUTSINK）"
            : $"原始输入注册失败（Win32 错误 {Marshal.GetLastWin32Error()}），藏匿期退回形状启发式");
    }

    /// <summary>
    /// The island's messages, with one answered before the island sees it: <c>WM_SETCURSOR</c> while the
    /// player has asked for no cursor. Everything else — which is the whole of the UI — goes straight on to
    /// the procedure that was there first. See <see cref="CursorHidden"/> for why this is the mechanism.
    /// </summary>
    private static IntPtr IslandDispatch(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        var original = IntPtr.Zero;

        // A managed exception must never travel back through the native frame that called us, and an
        // exception here would be one thrown between the island and its own input.
        try
        {
            if (Islands.TryGetValue(window, out var host))
            {
                original = host._islandProcedure;
                host.IslandMessagesSeen++;

                if (message == Native.WmSetCursor)
                {
                    host.CursorAsksSeen++;

                    if (host.CursorHidden)
                    {
                        Native.SetCursor(host.Blank);
                        host.CursorHidesAnswered++;

                        // TRUE, which is 「the cursor is set, do not set one yourself」. Returning anything
                        // else hands the message to the island, whose answer is always a visible shape.
                        return new IntPtr(1);
                    }
                }

                if (message == Native.WmNcDestroy) Islands.Remove(window);

                // 第十六报：见证的口粮。每条 WM_INPUT 到这里判一次「有出处（hDevice ≠ 0，真手）还是
                // 没有（SendInput 一类）」，记时间戳与计数 —— 裁决不在这一拍做：判「位移是手还是注入」
                // 的时刻在 PollPointer 读到够阈值位移的那一拍，那里问 RecentRealInput。这条消息流比
                // 任何东西都密（鼠标一动每秒上百条），Parse 必须便宜：一次固定缓冲、几次整数读，见
                // RealInputWitness.Parse。
                else if (message == Native.WmInput) host.Witness.Parse(lParam);
            }
        }
        catch (Exception error)
        {
            Log.Error(Category, $"处理 XAML 岛消息 0x{message:X} 时出错", error);
        }

        return original != IntPtr.Zero
            ? Native.CallWindowProc(original, window, message, wParam, lParam)
            : Native.DefWindowProc(window, message, wParam, lParam);
    }

    private void ApplyBackdrop()
    {
        if (_source is null) return;

        try
        {
            _source.SystemBackdrop = _useBackdrop ? new MicaBackdrop { Kind = MicaKind.BaseAlt } : null;
        }
        catch (Exception error)
        {
            // Mica is a nicety. A machine with transparency effects off, an unsupported GPU or a
            // remote session must still get a window.
            Log.Warn(Category, "设置 Mica 背景失败，改用纯色底", error);
            _useBackdrop = false;
        }
    }

    /// <summary>
    /// 独立播放管线的去处，懒建：第一次有播放要它才创建，之后整个窗口生命周期复用、随窗口销毁。
    /// 必须从界面线程调——CreateWindowEx 的消息队列跟着创建线程走，mpv 的 resize 钩子
    /// （<c>resize_child_win</c>）也装在那条线程上，窗口跨了线程，对账就散了。
    /// <para>
    /// 播放漏斗在界面上为独立播放引擎预备它（<c>PlayerPage.PrepareVideoPipeline</c>），真正读它的
    /// <c>PlayerPage.VideoSurface</c> 可能在线程池上被调（<c>PlaybackService.PlayAsync</c> 前两个
    /// await 都ConfigureAwait(false)），所以预备必须走在前头；这条路上若真撞见别的线程，宁可喊停
    /// 也不悄悄装错。
    /// </para>
    /// </summary>
    internal VideoWindow EnsureVideoUnderlay()
    {
        if (_video is not null) return _video;

        if (_dispatcherQueue is { } dispatcher && !dispatcher.HasThreadAccess)
            throw new InvalidOperationException("视频子窗口必须从界面线程创建（播放漏斗的预备步负责这件事）");

        _video = new VideoWindow(Handle, IslandHandle);
        _video.Fill();
        return _video;
    }

    private void OnSize()
    {
        if (_source is null) return;

        Native.GetClientRect(Handle, out var client);

        // One MoveAndResize for the whole of the chrome. The WinForms shell needed nine SetWindowPos
        // calls per resize because every piece of chrome was its own layered top-level window; this
        // is the single biggest reason the rewrite makes resizing smoother rather than worse.
        _source.SiteBridge.MoveAndResize(new RectInt32(0, 0, client.Width, client.Height));
        UpdateTitleBarRegions();

        // 独立播放管线的垫底窗口跟着走。Fill 只改它自己——mpv 的钩子收到这一拍会自己把它的子窗口
        // 跟上（VideoWindow 的类注释写了为什么宿主不能帮）。集成播放时它是 null，这一拍是空操作。
        _video?.Fill();
    }

    private static IntPtr Dispatch(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        // A managed exception must never travel back through the native frame that called us.
        try
        {
            // Messages that arrive during CreateWindowEx have no entry yet and fall through to the
            // default handling, which is what we want for all of them.
            if (Windows.TryGetValue(window, out var host))
                return host.Route(window, message, wParam, lParam);
        }
        catch (Exception error)
        {
            Log.Error(Category, $"处理窗口消息 0x{message:X} 时出错", error);
        }

        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    private IntPtr Route(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            // The pointer's own question, and the place it actually gets asked. The island's bridge window
            // never gets one — its input arrives through the InputSite APIs rather than the classic mouse
            // messages, which the self-check measures directly: 52 messages through the island's procedure
            // and not one WM_SETCURSOR, with the pointer parked squarely on the bridge. A move over the
            // picture asks the host window instead, so this is where 「no cursor」 has to be said.
            case Native.WmSetCursor:
                CursorAsksSeen++;

                if (_cursorHidden)
                {
                    Native.SetCursor(Blank);
                    CursorHidesAnswered++;

                    // TRUE, which is 「the cursor is set, do not set one yourself」.
                    return new IntPtr(1);
                }

                break;

            case Native.WmEraseBackground:
                // Fact 4: an unpainted client area comes back white, which flashes hard behind a
                // dark UI on startup and on every resize. Filling with the base colour is the fix;
                // the window never gets a WM_PAINT at all, because the island covers it completely.
                Native.GetClientRect(window, out var erase);
                Native.FillRect(wParam, ref erase, BaseBrush);
                return new IntPtr(1);

            case Native.WmSize:
                OnSize();

                // 最大化和还原也在这里落定 —— 那两下不是拖动，不发 WM_EXITSIZEMOVE。最大化那一档只抬那一位、
                // 尺寸留着上一次量到的，见 RememberPlacement。
                RememberPlacement();
                GeometryChanged?.Invoke();
                break;

            case Native.WmGetMinMaxInfo:
                ClampMinimumSize(window, lParam);
                return IntPtr.Zero;

            case Native.WmSizing:
                return LockAspectDuringResize(window, wParam, lParam);

            case Native.WmExitSizeMove:
                RememberPlacement();
                GeometryChanged?.Invoke();
                break;

            case Native.WmActivate:
                // LOWORD(wParam)：WA_INACTIVE=0、WA_ACTIVE=1、WA_CLICKACTIVE=2。HIWORD 是最小化位，
                // 与焦点无关，掩掉。
                FocusChanged?.Invoke((wParam.ToInt64() & 0xFFFF) != 0);
                break;

            case Native.WmActivateApp:
                ApplyFullscreenZOrder(wParam != IntPtr.Zero);
                break;

            case Native.WmTimer when (nuint)(nint)wParam == BandTimer:
                if (Fullscreen) JudgeBand();
                return IntPtr.Zero;

            case Native.WmDpiChanged:
                // lParam is the window rect Windows suggests for the new scale. Taking it verbatim
                // is what keeps a drag across monitors from jumping.
                var suggested = Marshal.PtrToStructure<NativeRect>(lParam);
                Native.SetWindowPos(
                    window, Native.HwndTop,
                    suggested.Left, suggested.Top, suggested.Width, suggested.Height,
                    Native.SwpNoZOrder | Native.SwpNoActivate);
                GeometryChanged?.Invoke();
                return IntPtr.Zero;

            case Native.WmClose:
                // 最后一次，而且必须在 DestroyWindow 之前：Closed 事件由 WM_DESTROY 发出，那时候 Handle 已经
                // 是 0，问不出这个窗口的任何几何了。程序化挪过的窗口（按画面比例联动那一下）也只有这一
                // 处兜得住 —— 那几下不经过拖动，收不到 WM_EXITSIZEMOVE。
                RememberPlacement();
                Native.DestroyWindow(window);
                return IntPtr.Zero;

            case Native.WmDestroy:
                // 钩子赶在消息循环还在的时候摘掉：线程活着而钩子悬着，每颗键都要多过一遍死委托。
                UninstallKeyboardFallback();
                Windows.Remove(window);
                Handle = IntPtr.Zero;

                // 静态事件，退订在这里：留着的话 ThemeHost 会一直握着一个 HWND 已经是 0 的窗口，
                // 下一次换主题就是往一个不存在的标题栏上设颜色。
                if (_repaint is not null)
                {
                    ThemeHost.Changed -= _repaint;
                    _repaint = null;
                }

                Closed?.Invoke();
                return IntPtr.Zero;
        }

        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    /// <summary>
    /// 拖左右边自动配高度，拖上下边自动配宽度. The rectangle in <paramref name="lParam"/> is what the pointer
    /// implies; <see cref="AspectLock"/> decides what shape it should be and this writes the answer back,
    /// so the window never takes a size it then has to correct.
    /// <para>
    /// The shape is <see cref="PictureAspect"/> — the picture's while a file is playing into our own window,
    /// and 0 the rest of the time, which is the ordinary window that resizes however the pointer says.
    /// Fullscreen and maximized are off regardless: those edges are the monitor's and constraining them would
    /// fight the OS. A picture fills the client area edge to edge, so the whole of it is what the shape
    /// describes.
    /// </para>
    /// <para>
    /// The frame thickness is measured from the window rather than derived from its styles — see
    /// <see cref="FrameThickness"/> — and the minimum client size is computed at this window's scale, not
    /// at 96 dpi. The minimum itself is the browsing floor unless <see cref="FreeSizing"/> has lifted it
    /// for a playback, in which case the shape is held all the way down to however small the hand drags.
    /// </para>
    /// </summary>
    private IntPtr LockAspectDuringResize(IntPtr window, IntPtr wParam, IntPtr lParam)
    {
        var aspect = PictureAspect;
        if (aspect <= 0 || Fullscreen || Native.IsZoomed(window))
            return Native.DefWindowProc(window, Native.WmSizing, wParam, lParam);

        var edge = (ResizeEdge)(int)wParam;
        if (edge == ResizeEdge.None)
            return Native.DefWindowProc(window, Native.WmSizing, wParam, lParam);

        var dpi = Native.GetDpiForWindow(window);
        if (dpi == 0) dpi = 96;

        // How much of the window rect is frame rather than client, measured rather than derived from the
        // style bits — see FrameThickness. The delta does not change while an edge is being dragged.
        var frame = FrameThickness(dpi);

        var proposed = Marshal.PtrToStructure<NativeRect>(lParam);

        var locked = AspectLock.Apply(
            new WindowBounds(proposed.Left, proposed.Top, proposed.Right, proposed.Bottom),
            edge,
            aspect,
            frame.Width,
            frame.Height,
            FreeSizing ? 0 : MinimumWidth * (int)dpi / 96,
            FreeSizing ? 0 : MinimumHeight * (int)dpi / 96);

        proposed.Left = locked.Left;
        proposed.Top = locked.Top;
        proposed.Right = locked.Right;
        proposed.Bottom = locked.Bottom;
        Marshal.StructureToPtr(proposed, lParam, false);

        // TRUE, which is what tells Windows the rectangle was rewritten.
        return new IntPtr(1);
    }

    /// <summary>
    /// 窗口能拖到多小。<see cref="MinimumWidth"/> 和 <see cref="MinimumHeight"/> 说的是客户区，所以这里按这个
    /// 窗口量出来的边框折成窗口尺寸（<see cref="FrameThickness"/>），而不是照样式位算 —— 样式里还留着
    /// <c>WS_CAPTION</c>，照它算出来的下限比真的高出整整一条标题栏（96 dpi 下 31 像素），而客户区正铺在那一条
    /// 上面。
    /// <para>
    /// 这一条差单看窗口不显眼，撞上比例锁才现形：锁要的高度落在那 31 像素里时，系统把窗口顶回它自己那个下限，
    /// 屏上的形状于是永远差一点，而谁也不会报错 —— 副屏那种窄屏上（窗口宽被工作区卡住、只能靠压低高度凑形状）
    /// 就是这样。量出来的边框是客户区真正会拿到的那个差值，比例那一头用的也是它，两处一致这个洞才关得住。
    /// </para>
    /// </summary>
    private void ClampMinimumSize(IntPtr window, IntPtr lParam)
    {
        // 播放时不设下限（FreeSizing）：MINMAXINFO 是系统按默认值预填好的，一个字节不写就是把
        // 最小追踪尺寸还给系统默认 —— 那只剩一百来像素，窗口于是随便缩。
        if (FreeSizing) return;

        var dpi = Native.GetDpiForWindow(window);
        if (dpi == 0) dpi = 96;

        var frame = FrameThickness(dpi);

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MinTrackSize = new NativePoint
        {
            X = (MinimumWidth * (int)dpi / 96) + frame.Width,
            Y = (MinimumHeight * (int)dpi / 96) + frame.Height
        };
        Marshal.StructureToPtr(info, lParam, false);
    }

    private static void EnsureClassRegistered(IntPtr instance)
    {
        if (_classRegistered) return;

        // Allocated once and never freed on purpose: the class outlives every window in the process,
        // and RegisterClassEx is documented to keep the pointer rather than copy the string.
        var className = Marshal.StringToHGlobalUni(ClassName);

        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = instance,
            Cursor = Native.LoadCursor(IntPtr.Zero, Native.ArrowCursor),
            Icon = Native.LoadIcon(instance, Native.ApplicationIconResource),
            // No class background brush on purpose: WM_ERASEBKGND paints the app's own base colour,
            // and letting the system paint COLOR_WINDOW first would be the white flash we are avoiding.
            Background = IntPtr.Zero,
            ClassName = className
        };

        if (Native.RegisterClassEx(ref windowClass) == 0)
            throw new InvalidOperationException($"注册窗口类失败，错误码 {Marshal.GetLastWin32Error()}");

        _classRegistered = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 兜底那一摘：正常销毁走 WM_DESTROY 已经摘过（幂等，句柄归零即无事可做）。
        UninstallKeyboardFallback();

        // Unmark before the window goes, so a shutdown from fullscreen cannot leave the shell holding a
        // dead hwnd as the reason the taskbar is standing aside.
        if (Handle != IntPtr.Zero && Fullscreen) Native.MarkFullscreen(Handle, false);

        // 第九报（2026-09-15）：躲过了窗口生命周期的两条要还。类光标活过窗口本身（同一个类此后新建的
        // 窗口共用它），负计数锁活过窗口本身（同一个 UI 线程上后继的窗口共用那个队列）。
        RestoreClassCursors();
        RestoreCursorDisplay();

        if (_blank != IntPtr.Zero)
        {
            Native.SetCursor(Native.LoadCursor(IntPtr.Zero, Native.ArrowCursor));

            // Only when the framework never got hold of it. Once it has been wrapped as an InputCursor the
            // framework is holding this very handle, and destroying it here would be a use-after-free bet for
            // no gain — the process is on its way out, so one leaked cursor handle is free.
            if (!_blankInputTried) Native.DestroyCursor(_blank);

            _blank = IntPtr.Zero;
        }

        // 视频子窗口的兜底那一条：正常销毁走 WM_DESTROY 已经清过（Dispose 是幂等清零，再走一遍无事）。
        _video?.Dispose();
        _video = null;

        _source?.Dispose();
        _source = null;

        if (Handle != IntPtr.Zero) Native.DestroyWindow(Handle);
    }
}
