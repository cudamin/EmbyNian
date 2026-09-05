using System.Runtime.InteropServices;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.UI;

namespace EmbyNian.Shell.Windowing;

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
///   exactly. The old airspace limitation is gone, so mpv can stay a child HWND under <c>--wid</c>
///   with no render API, no ANGLE and no SwapChainPanel.</item>
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
    private const int MinimumWidth = 900;
    private const int MinimumHeight = 560;

    /// <summary>
    /// 开窗就是锁定的那个形状：浏览区（客户区去掉侧边栏那一条）16:9，加回那一条的宽。算出来而不是写死，因为
    /// 「锁着比例却开在别的形状上」的窗口会被 <see cref="FitToShape"/> 在第一帧之后拽一下，屏幕上就是一跳。
    /// </summary>
    private static readonly int DefaultWidth =
        (int)Math.Round(HomeCarousel.WindowAspect * DefaultHeight) + HomeCarousel.SideRail;

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
    private VideoWindow? _video;
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
    /// One window per blanked class, against the cursor handle that class had before. Empty whenever the
    /// cursor is shown; a leftover entry here is a class cursor left blank, which is why the restore also
    /// runs on dispose.
    /// </summary>
    private readonly Dictionary<IntPtr, IntPtr> _classCursors = [];

    /// <summary>The island procedure ours was put in front of, and the one every other message goes to.</summary>
    private IntPtr _islandProcedure;

    /// <summary>
    /// 换主题时重画标题栏那一条。<see cref="ThemeHost.Changed"/> 是个静态事件，不退订就等于把一个已经销毁的
    /// 窗口永远挂在上面，所以委托存下来，<c>WM_DESTROY</c> 里减掉。
    /// </summary>
    private Action<EmbyNian.Theming.UiTheme>? _repaint;

    /// <summary>
    /// 「锁定窗口比例大小」改了的时候把新值接过来。和 <see cref="_repaint"/> 是同一件事、同一个理由：
    /// <see cref="ShellPrefs.Changed"/> 也是静态事件，留在上面的就是一个 HWND 已经归零的窗口，而它接到通知之后
    /// 干的正是给窗口设大小。同样在 <c>WM_DESTROY</c> 里减掉。
    /// </summary>
    private Action<EmbyNian.Configuration.UiSettings>? _reshape;

    /// <summary>
    /// The one rectangle of the browsing title bar that belongs to the page instead of the window frame —
    /// the shell's two navigation arrows — in logical pixels. Null until the shell has measured them.
    /// </summary>
    private (double X, double Y, double Width, double Height)? _hole;

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

    /// <summary>
    /// Whether the mouse cursor is to stay off the picture — 「全屏播放且鼠标在画面上时，鼠标静止不动两秒之后
    /// 要自动隐藏」. Set by the player, whose reveal rule decides <em>when</em>; this is the whole of
    /// <em>how</em>, and it lives here because it is a window-level Win32 arrangement rather than anything
    /// about the visual tree.
    /// <para>
    /// It is not <c>ShowCursor</c>. That call keeps a per-thread display counter, it is what every guide
    /// recommends, it reaches user32 (the counter comes back −1), and over a WinUI 3 XAML island it does
    /// nothing at all — which is what 「鼠标指针还是不会自动隐藏」 was, twice. What does work is
    /// <c>SetCursor(NULL)</c>: measured on this window, the thread's own cursor goes to 「none」 the moment it
    /// is called and comes back on the call that restores a shape. The reason that took two attempts to
    /// establish is that <c>GetCursorInfo</c> cannot see it — it reports the desktop's cursor, which is
    /// recomputed when the pointer moves, and the pointer holding still is the entire circumstance here; it
    /// went on reporting the arrow through a deliberate <c>SetCursor(IDC_WAIT)</c> that <c>GetCursor</c>
    /// reported immediately. The counter is still set alongside, for the Win32 surfaces it does govern.
    /// </para>
    /// <para>
    /// One call is not the whole of it, because <c>SetCursor</c> lasts only until something sets a shape
    /// again, and XAML's input site does exactly that from its own pointer handling without asking any window
    /// procedure. So there are three sayings of the same thing: here, at the moment of hiding; in
    /// <see cref="KeepCursorHidden"/>, re-said on the player's ten-hertz tick for as long as it holds; and in
    /// answer to <c>WM_SETCURSOR</c>, in this window's own <c>Route</c> and in <see cref="IslandDispatch"/>
    /// ahead of the island's procedure. The last of those is the classic mechanism and the least load-bearing
    /// one of the three: the island's bridge window is measured by the self-check to take classic mouse
    /// messages by the dozen and <c>WM_SETCURSOR</c> never, its pointer input arriving through the InputSite
    /// APIs instead. Every other message goes through untouched.
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

            if (value) BlankClassCursors();
            else RestoreClassCursors();
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
    /// It is the lever the other four were missing. While the pointer is over XAML content the shape on screen
    /// is the framework's to decide, and nothing done on this thread — <c>SetCursor</c>, <c>ShowCursor</c>, the
    /// class cursors — is on that path. A real film's log says so plainly: one
    /// hide lasted two minutes and five seconds with this queue holding no shape the whole time, the show count
    /// at −1, five window classes blanked, the nudge sent, and <c>GetCursorInfo</c> answering 「system arrow」
    /// from beginning to end. That last reading has one caveat as of 2026-09-05: the nudge of the day produced
    /// no message at all, so nothing in that film ever asked the OS to collect any of those answers — see
    /// <see cref="Native.NudgeCursorState"/>. See <see cref="InputCursors"/> for how the wrapping is done and
    /// why there is no projected API for it.
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
    /// How many window classes are currently blanked, and how many were reached at all. Read by the
    /// self-check: 「the sweep found the island's windows」 and 「it put every one of them back」 are the two
    /// things that can go wrong with a lever this wide, and neither is visible from inside this file.
    /// </summary>
    public int ClassCursorsBlanked => _classCursors.Count;

    /// <summary>The high-water mark of <see cref="ClassCursorsBlanked"/>, which survives the restore.</summary>
    public int ClassCursorsSwept { get; private set; }

    /// <summary>
    /// Gives every window class in this window's tree a blank cursor, remembering what each had.
    /// <para>
    /// The other three sayings of 「no cursor」 are all <c>SetCursor</c>, and <c>SetCursor</c> is per message
    /// queue: it reaches the screen only while the pointer is over a window this thread owns. Two of the
    /// windows under the pointer during playback are not made by this thread — libmpv builds its own child
    /// window on its own thread, and the island's windows are the framework's — and no amount of saying it
    /// here reaches those. A class cursor does: it is process-wide, it can be set from any thread for a
    /// window created by any other, and it is what the default <c>WM_SETCURSOR</c> handling answers with.
    /// </para>
    /// <para>
    /// One entry per class rather than per window, which is what the handle comparison is for: the second
    /// window of an already-blanked class would otherwise record 「blank」 as the shape to put back and leave
    /// the class blank for good. Capped, and shallow, because a runaway sweep here means an invisible cursor
    /// over the whole process.
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
    /// Says 「no cursor」 again, for a caller that can afford to keep saying it. One call at the moment of
    /// hiding is enough only if nothing puts a shape back afterwards, and the island is several windows deep
    /// with input handling of its own: a <c>WM_SETCURSOR</c> answered on an inner window this subclass never
    /// sees would restore the arrow with no message here to notice it. Re-said on the player's ten-hertz tick
    /// it costs one user32 call, and it stops of its own accord the moment the pointer moves — moving the
    /// pointer is how the cursor is asked back, so <see cref="CursorHidden"/> is false by then.
    /// </summary>
    public void KeepCursorHidden()
    {
        if (_cursorHidden) Native.SetCursor(Blank);
    }

    /// <summary>Whether content is currently extended into a custom non-client title bar.</summary>
    public bool UsesCustomTitleBar => _nonClient is not null;

    /// <summary>
    /// Changes only the interactive partition of the custom title bar and the height the framework
    /// reserves for it. During playback the whole top strip is XAML input — the back button, the three
    /// window commands, and the blank space between them that the page drags the window by; while browsing
    /// the same area is caption drag space with one rectangle cut out of it for the shell's two navigation
    /// arrows (<see cref="SetTitleBarHole"/>), and the framework's own caption buttons at its end.
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
    /// What the framework says its own title bar comes to, in physical pixels: the height it reserves and
    /// the width its caption buttons occupy at the right end. Both are zero while
    /// <see cref="TitleBarHeightOption.Collapsed"/> is in force, which is how the self-check can prove from
    /// outside that playback really has no system buttons standing over the picture — the window keeps
    /// <c>WS_CAPTION</c> now, so 「no buttons」 is a claim about the framework rather than about the style bits.
    /// </summary>
    public (double Height, double RightInset) TitleBarMetrics =>
        _appWindow?.TitleBar is { } bar ? (bar.Height, bar.RightInset) : (0, 0);

    /// <summary>
    /// The rectangle of the browsing title bar the shell has claimed for itself, in logical pixels, or null
    /// while it has claimed none. Exposed for the self-check, which has to prove the hole the window is
    /// keeping is the rectangle the buttons are actually drawn in — a hole in the wrong place is invisible
    /// until someone tries to click an arrow and drags the window instead.
    /// </summary>
    internal (double X, double Y, double Width, double Height)? TitleBarHole => _hole;

    /// <summary>
    /// Says which rectangle of the title bar the page draws buttons in, in logical pixels of the island's
    /// coordinates, so the frame stops answering <c>HTCAPTION</c> there.
    /// <para>
    /// This is not decoration. A button drawn inside a caption region never receives a click at all: the
    /// frame claims the point before XAML sees it, and Windows turns the pointer press into a window drag.
    /// The rectangle has to be cut out of the caption for the arrows to work, and the caption has to be
    /// stated as the pieces around it rather than as one rect underneath, because a point declared twice
    /// belongs to neither kind in particular.
    /// </para>
    /// <para>Passing a zero-area rectangle gives the strip back to the frame, whole.</para>
    /// </summary>
    internal void SetTitleBarHole(double x, double y, double width, double height)
    {
        _hole = width > 0 && height > 0 ? (x, y, width, height) : null;
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
    /// Hands out the video surface's HWND, creating it the first time it is asked for, and reports zero
    /// once the window is gone. This is the whole of the shell's side of the video contract: it is what
    /// <c>LibMpvBackend</c>'s <c>Func&lt;IntPtr&gt;</c> returns, and mpv does the rest through <c>wid</c>.
    /// <para>
    /// Created lazily, because a session that only ever browses should not pay for a DWM redirection
    /// surface — and then kept, because destroying and recreating it between episodes is exactly the
    /// churn requirement 12 is about.
    /// </para>
    /// </summary>
    public IntPtr VideoHandle()
    {
        if (Handle == IntPtr.Zero) return IntPtr.Zero;

        _video ??= new VideoWindow(Handle, IslandHandle);
        return _video.Handle;
    }

    /// <summary>
    /// Whether video is on screen. Setting it turns Mica off (fact 3: a backdrop is composited into the
    /// island surface, and an opaque island hides the child HWND behind it) and back on again afterwards.
    /// <para>
    /// It does not show or hide the surface. The surface can stay put because it only ever <em>appears</em>
    /// when the page above it is transparent, and every browsing page paints an opaque background of its
    /// own. One less piece of state to get out of step, and nothing to sequence against a file change.
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
    /// </summary>
    public double PictureAspect { get; set; }

    /// <summary>
    /// 锁定窗口比例大小: the shape the browsing area is held in while nobody is watching anything — as
    /// width ÷ height, or 0 for 「resize it however you like」.
    /// <para>
    /// The browsing area, not the client area: 「计算比例时要排除侧边栏」, so the ratio owns the client area
    /// less <see cref="SideInset"/> and the window is that much wider than its own shape. Set from
    /// <c>UiSettings.LockWindowShape</c> at startup and whenever that switch is flipped
    /// (<see cref="ShellPrefs"/>), and the value is the home page's
    /// (<see cref="HomeCarousel.WindowAspect"/>). That shape is also how the home page recognises
    /// the mode in which its first screen is strict: the complete 继续观看 shelf fits below the banner and the
    /// next 媒体库 shelf starts outside the viewport, independent of the navigation pane state.
    /// </para>
    /// <para>
    /// Second to <see cref="PictureAspect"/> rather than beside it: a file that is playing has a shape of its
    /// own and it wins, or 「缩放窗口时按画面比例联动」 would have been undone by this. So the window is held to
    /// the picture while there is one and to this the rest of the time, and <see cref="LockedAspect"/> is that
    /// sentence. A picture fills the client area edge to edge, which is why the inset goes with the ratio
    /// (<see cref="LockedInset"/>) instead of being a property of the window.
    /// </para>
    /// </summary>
    public double BrowseAspect { get; set; }

    /// <summary>
    /// 侧边栏那一条在这块屏上占多少物理像素 —— <see cref="BrowseAspect"/> 管的是它右边那一片。
    /// <see cref="HomeCarousel.SideRail"/> 是逻辑像素，而窗口矩形一律是物理像素，所以要按这个窗口的 dpi 放大。
    /// </summary>
    internal int SideInset => SideInsetFor(WindowDpi);

    private static int SideInsetFor(uint dpi) => (int)Math.Round(HomeCarousel.SideRail * dpi / 96.0);

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
    /// 那条「最小尺寸压得住比例」），所以读锁定形状的那一关得知道这个数才不会把「让位」报成「锁坏了」。
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
    /// Whether the browsing ratio lock owns the window geometry right now. Playback owns the shape while it
    /// has a picture; fullscreen, maximized and Windows-snapped windows belong to the monitor instead. The
    /// setting alone is not enough: after playback the restored window can still have the film's shape, so
    /// the browsing area must also be within rounding distance of <see cref="BrowseAspect"/> — the client area
    /// less the rail, which is what the ratio was applied to in the first place.
    /// </summary>
    internal bool BrowseFoldActive
    {
        get
        {
            if (BrowseAspect <= 0 || PictureAspect > 0 || Fullscreen || IsMaximized) return false;

            var (width, height) = ClientSize;
            var browse = width - SideInset;
            return browse > 0 && height > 0 && Math.Abs(browse - (BrowseAspect * height)) <= 2;
        }
    }

    /// <summary>
    /// Which of the two ratios the window is actually held to right now: the picture's while something is
    /// playing into this window, the browsing shape otherwise, 0 when neither is set.
    /// </summary>
    private double LockedAspect => PictureAspect > 0 ? PictureAspect : BrowseAspect;

    /// <summary>
    /// And which part of the client area that ratio describes: all of it for a picture, everything right of
    /// the navigation rail while the browsing lock has the window.
    /// </summary>
    private int LockedInset(uint dpi) => PictureAspect > 0 ? 0 : SideInsetFor(dpi);

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
    /// is being watched on.
    /// </para>
    /// </summary>
    public void FitToPicture() => FitToAspect(PictureAspect, 0, "按画面比例调整窗口");

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

        if (Native.IsZoomed(Handle))
        {
            Placement = (Placement.Bounds, true);
            return;
        }

        if (!Native.GetWindowRect(Handle, out var rect)) return;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        Placement = (new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom), false);
    }

    /// <summary>
    /// 锁定窗口比例大小 just went on — or the app just started with it on: reshapes the window to
    /// <see cref="BrowseAspect"/>, because <c>WM_SIZING</c> only holds a shape while an edge is being dragged
    /// and the window it is switched on in is whatever shape it already was.
    /// <para>
    /// Nothing while a file is playing: the window belongs to the picture then
    /// (<see cref="LockedAspect"/>), and reshaping it to the browsing ratio mid-film is exactly the
    /// letterboxing 「窗口化时视频有黑边」 asked to be rid of. The switch still takes effect — it is read again
    /// on the next drag, and playback ending leaves the window where the film had it, which is the same thing
    /// that has always happened.
    /// </para>
    /// </summary>
    public void FitToShape()
    {
        if (PictureAspect > 0) return;

        FitToAspect(BrowseAspect, SideInset, "按锁定比例调整窗口");
    }

    /// <summary>
    /// The body both of the above share: reshape this window so the part of its client area the ratio owns —
    /// all of it for a picture, everything right of the rail for the browsing lock
    /// (<paramref name="inset"/>) — is exactly <paramref name="aspect"/> where its shape is ours to choose,
    /// and say so in the log under <paramref name="reason"/>. Two callers with one arithmetic — the guards,
    /// the measured frame and the work-area ceiling are the same questions whichever ratio is being applied,
    /// and a second copy of them is a second place for the caption-height trap
    /// (<see cref="FrameThickness"/>) to be got wrong.
    /// </summary>
    private void FitToAspect(double aspect, int inset, string reason)
    {
        if (Handle == IntPtr.Zero || aspect <= 0) return;
        if (Fullscreen || Native.IsZoomed(Handle) || Native.IsIconic(Handle)) return;
        if (!Native.GetWindowRect(Handle, out var bounds)) return;

        var dpi = Native.GetDpiForWindow(Handle);
        if (dpi == 0) dpi = 96;

        var frame = FrameThickness(dpi);

        var fitted = AspectLock.Fit(
            new WindowBounds(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
            aspect,
            frame.Width,
            frame.Height,
            WorkArea(),
            MinimumWidth * (int)dpi / 96,
            MinimumHeight * (int)dpi / 96,
            inset);

        if (fitted.Left == bounds.Left && fitted.Top == bounds.Top
            && fitted.Width == bounds.Width && fitted.Height == bounds.Height)
        {
            return;
        }

        Native.SetWindowPos(
            Handle, Native.HwndTop,
            fitted.Left, fitted.Top, fitted.Width, fitted.Height,
            Native.SwpNoZOrder | Native.SwpNoActivate);

        Log.Info(Category, $"{reason} {fitted.Width}x{fitted.Height}");
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
    /// 开关关掉的那一档反过来验：那时这个矩形必须一字不改地回来，否则就是「关掉了还在锁」。只看矩形不看返回值 ——
    /// 那一档走的是 <c>DefWindowProc</c>，它对 <c>WM_SIZING</c> 回什么并没有写在文档里。
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
        var locked = LockedAspect;
        var inset = LockedInset(dpi);

        // 右边沿往外 240：宽领头那一档，左边沿该原地不动。
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

        var client = (Width: answered.Width - frame.Width, Height: answered.Height - frame.Height);

        // 比例管的是 inset 右边那一片：播放时那是整个客户区，浏览时是客户区去掉侧边栏那一条。
        var owned = client.Width - inset;
        var shape = client.Height > 0 && owned > 0 ? (double)owned / client.Height : 0;
        var untouched = answered.Width == wanted.Width && answered.Height == wanted.Height;

        var ok = locked > 0
            ? claimed != IntPtr.Zero && Math.Abs(shape - locked) < 0.01 && answered.Left == wanted.Left
            : untouched;

        return (ok,
            locked > 0
                ? $"右边沿外拉 240 → 客户区 {client.Width}×{client.Height}，"
                    + (inset > 0 ? $"去掉侧边栏那 {inset} 后 {owned}×{client.Height} = {shape:0.000}:1" : $"{shape:0.000}:1")
                    + $"（要的是 {locked:0.000}），左边沿{(answered.Left == wanted.Left ? "没动" : "被挪了")}，"
                    + $"窗口{(claimed != IntPtr.Zero ? "改写了这个矩形" : "没接手 —— 锁没生效")}"
                : $"未锁定：右边沿外拉 240 → {answered.Width}×{answered.Height}，"
                    + $"{(untouched ? "原样送回" : "被改写了 —— 关掉了还在锁")}");
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
                Log.Info(Category, $"前台交给 0x{other:X}，它挡不到全屏画面，保持置顶");
            }

            return;
        }

        _judged = other;
        _band = false;

        Native.SetWindowPos(
            Handle, Native.HwndNoTopMost,
            0, 0, 0, 0,
            Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);

        Log.Info(Category, $"前台交给 0x{other:X}，全屏画面会挡着它，让出置顶");
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
    /// Whether <paramref name="other"/> is somewhere our fullscreen frame could not be hiding it: on a
    /// different monitor <em>and</em> not overlapping our rectangle. False whenever that cannot be
    /// established — no window, our own, or geometry the OS will not give — because 「then yield」 is the
    /// answer that can only cost the picture a taskbar, where the other way round costs the user the window
    /// they just switched to.
    /// </summary>
    private bool StandsClearOfPicture(IntPtr other)
    {
        if (other == IntPtr.Zero || other == Handle) return false;

        if (!Native.GetWindowRect(Handle, out var picture) || !Native.GetWindowRect(other, out var window))
            return false;

        return StandsClear(
            picture,
            Native.MonitorFromWindow(Handle, Native.MonitorDefaultToNearest),
            window,
            Native.MonitorFromWindow(other, Native.MonitorDefaultToNearest));
    }

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

        Native.ShowWindow(Handle, maximized ? Native.SwMaximize : Native.SwShow);
        Native.SetForegroundWindow(Handle);

        // Once more, now that the window has its final client area and the island has been shown.
        // ConfigureTitleBar already claimed the strip, but that ran before any of this: a window created at
        // its final size gets no WM_SIZE out of ShowWindow, so without this call the only refresh the caption
        // strip would ever get is the user's first resize.
        UpdateTitleBarRegions();

        // 设置页那个开关关掉的时候得把这里的锁一起松开，开的时候还要当场把窗口摆成那个形状 —— 光记下比例不
        // 够，WM_SIZING 要等到用户下一次去拖边才问。委托存下来是为了退订，见 _reshape。
        _reshape = ui =>
        {
            BrowseAspect = ui.LockWindowShape ? HomeCarousel.WindowAspect : 0;
            FitToShape();
        };
        ShellPrefs.Changed += _reshape;

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
                titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
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
        // Browsing is the other way round: the strip is caption drag space apart from the one rectangle the
        // shell draws its two navigation arrows in, which is passthrough for the same reason — without it the
        // frame answers 标题栏 there and the arrows never see a click.
        var hole = _playbackTitleBar ? Empty : HolePixels((int)dpi, dragRight, height);

        var strip = _playbackTitleBar && width > 0 && height > 0
            ? new RectInt32(0, 0, width, height)
            : Empty;

        _nonClient.SetRegionRects(NonClientRegionKind.Passthrough, [_playbackTitleBar ? strip : hole]);
        _nonClient.SetRegionRects(
            NonClientRegionKind.Caption,
            _playbackTitleBar ? [Empty] : CaptionAround(hole, dragRight, height));

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
    /// The rectangle the shell claimed, in physical pixels, clipped to the draggable part of the strip.
    /// Outward-rounded — floor the near edges, ceil the far ones — so no half pixel of a button is left
    /// answering 标题栏, which at fractional scaling is the difference between an arrow that clicks and an
    /// arrow whose top row drags the window.
    /// </summary>
    private RectInt32 HolePixels(int dpi, int dragRight, int height)
    {
        if (_hole is not { } hole || dragRight <= 0 || height <= 0) return Empty;

        var scale = dpi / 96.0;
        var left = Math.Clamp((int)Math.Floor(hole.X * scale), 0, dragRight);
        var top = Math.Clamp((int)Math.Floor(hole.Y * scale), 0, height);
        var right = Math.Clamp((int)Math.Ceiling((hole.X + hole.Width) * scale), left, dragRight);
        var bottom = Math.Clamp((int)Math.Ceiling((hole.Y + hole.Height) * scale), top, height);

        return right > left && bottom > top ? new RectInt32(left, top, right - left, bottom - top) : Empty;
    }

    /// <summary>
    /// The caption stated as the pieces around the hole: left of it, right of it, and — because the buttons
    /// are shorter than the strip — the bands above and below it. Never an empty array; see
    /// <see cref="UpdateTitleBarRegions"/> for what withdrawing a declaration costs.
    /// </summary>
    private static RectInt32[] CaptionAround(RectInt32 hole, int dragRight, int height)
    {
        if (dragRight <= 0 || height <= 0) return [Empty];
        if (hole.Width <= 0 || hole.Height <= 0) return [new RectInt32(0, 0, dragRight, height)];

        var pieces = new List<RectInt32>(4);

        if (hole.X > 0) pieces.Add(new RectInt32(0, 0, hole.X, height));

        var right = hole.X + hole.Width;
        if (dragRight > right) pieces.Add(new RectInt32(right, 0, dragRight - right, height));

        if (hole.Y > 0) pieces.Add(new RectInt32(hole.X, 0, hole.Width, hole.Y));

        var bottom = hole.Y + hole.Height;
        if (height > bottom) pieces.Add(new RectInt32(hole.X, bottom, hole.Width, height - bottom));

        return pieces.Count > 0 ? [.. pieces] : [Empty];
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
        }
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

    private void OnSize()
    {
        if (_source is null) return;

        Native.GetClientRect(Handle, out var client);

        // One MoveAndResize for the whole of the chrome. The WinForms shell needed nine SetWindowPos
        // calls per resize because every piece of chrome was its own layered top-level window; this
        // is the single biggest reason the rewrite makes resizing smoother rather than worse.
        _source.SiteBridge.MoveAndResize(new RectInt32(0, 0, client.Width, client.Height));
        UpdateTitleBarRegions();

        // The second and last call. mpv resizes its own child inside this one.
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
                // 是 0，问不出这个窗口的任何几何了。程序化挪过的窗口（FitToShape、按画面比例联动）也只有这一
                // 处兜得住 —— 那几下不经过拖动，收不到 WM_EXITSIZEMOVE。
                RememberPlacement();
                Native.DestroyWindow(window);
                return IntPtr.Zero;

            case Native.WmDestroy:
                Windows.Remove(window);
                Handle = IntPtr.Zero;

                // 静态事件，退订在这里：留着的话 ThemeHost 会一直握着一个 HWND 已经是 0 的窗口，
                // 下一次换主题就是往一个不存在的标题栏上设颜色。
                if (_repaint is not null)
                {
                    ThemeHost.Changed -= _repaint;
                    _repaint = null;
                }

                // 同上。这一个留着的后果更直接：下一次改设置就是往一个 HWND 已经是 0 的窗口上设大小。
                if (_reshape is not null)
                {
                    ShellPrefs.Changed -= _reshape;
                    _reshape = null;
                }

                // Zeroed here rather than left dangling: the backend can outlive the window by the length
                // of one mpv shutdown, and it must be told there is nowhere to draw rather than handed a
                // freed HWND.
                _video?.Dispose();
                _video = null;

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
    /// The shape is <see cref="LockedAspect"/> — the picture's while a file is playing into our own window,
    /// the browsing ratio while 「锁定窗口比例大小」 is on, and 0 when neither holds, which is the ordinary
    /// window that resizes however the pointer says. Fullscreen and maximized are off regardless: those
    /// edges are the monitor's and constraining them would fight the OS. Which part of the client area that
    /// shape describes comes with it (<see cref="LockedInset"/>): a picture is the whole of it, the browsing
    /// window is everything right of the rail.
    /// </para>
    /// <para>
    /// The frame thickness is measured from the window rather than derived from its styles — see
    /// <see cref="FrameThickness"/> — and the minimum client size is computed at this window's scale, not
    /// at 96 dpi.
    /// </para>
    /// </summary>
    private IntPtr LockAspectDuringResize(IntPtr window, IntPtr wParam, IntPtr lParam)
    {
        var aspect = LockedAspect;
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
            MinimumWidth * (int)dpi / 96,
            MinimumHeight * (int)dpi / 96,
            LockedInset(dpi));

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

        // Unmark before the window goes, so a shutdown from fullscreen cannot leave the shell holding a
        // dead hwnd as the reason the taskbar is standing aside.
        if (Handle != IntPtr.Zero && Fullscreen) Native.MarkFullscreen(Handle, false);

        // Class cursors outlive windows: a class blanked while the film was paused and never put back would
        // leave the next window of that class — in this process, for as long as it runs — with no cursor.
        RestoreClassCursors();

        if (_blank != IntPtr.Zero)
        {
            Native.SetCursor(Native.LoadCursor(IntPtr.Zero, Native.ArrowCursor));

            // Only when the framework never got hold of it. Once it has been wrapped as an InputCursor the
            // framework is holding this very handle, and destroying it here would be a use-after-free bet for
            // no gain — the process is on its way out, so one leaked cursor handle is free.
            if (!_blankInputTried) Native.DestroyCursor(_blank);

            _blank = IntPtr.Zero;
        }

        _video?.Dispose();
        _video = null;

        _source?.Dispose();
        _source = null;

        if (Handle != IntPtr.Zero) Native.DestroyWindow(Handle);
    }
}
