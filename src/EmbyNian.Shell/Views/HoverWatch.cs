using EmbyNian.Shell.Interop;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 「鼠标移出窗口后不会自动恢复」：keeps a hover reveal honest by asking the OS where the cursor is, ten
/// times a second, for as long as anything is revealed.
/// <para>
/// Three controls reveal something while the pointer is on them — a card's buttons, a row's play button,
/// a shelf's paging arrows — and all three used to trust <c>PointerExited</c> alone. It cannot be
/// trusted, in two different ways. The event bubbles, so stepping off one of the revealed buttons raises
/// it on the card too and a click's pointer capture raises it without the pointer having moved at all;
/// that is what the coordinate guard at each call site is for. But the position the event reports can
/// itself be stale — a pointer that leaves the window from over a card reports the last place inside it
/// — and then the guard swallows the one departure that mattered and no further exit is ever raised.
/// The buttons stay up over a card the pointer left minutes ago, which is exactly what was reported.
/// </para>
/// <para>
/// So the events stay as the fast path, and this is the net under them: while something is revealed, a
/// ten-hertz tick asks Win32 where the cursor really is and takes the reveal down when it is somewhere
/// else. It costs one <c>GetCursorPos</c> per target per tick and only runs while a reveal is up, and it
/// covers every way an exit can fail to arrive — the pointer leaving the window, the window moving out
/// from under a still pointer, content scrolling under one, another window opening on top.
/// </para>
/// <para>
/// Targets nest: a card is inside a shelf, and both of them watch. So this is a list rather than 「the one
/// revealed thing」 — each target is asked about its own rectangle, and the pointer being on the card is
/// also the pointer being on the shelf.
/// </para>
/// </summary>
/// <param name="target">
/// The element whose rectangle decides the answer — the control's own hit-test root, not the
/// <c>UserControl</c> around it, which a stretching layout can leave wider than the card the user sees.
/// </param>
/// <param name="reveal">
/// Called with true when the pointer arrives and false when it is gone, whether that verdict came from
/// an event or from the tick. Never called twice in a row with the same answer.
/// </param>
internal sealed class HoverWatch(FrameworkElement target, Action<bool> reveal)
{
    /// <summary>
    /// 100 ms, the rate the player's chrome already polls the cursor at. Fast enough that a reveal the
    /// events got wrong is gone before the eye settles on it, slow enough to be free.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Everything revealed right now. UI thread only — every caller is a pointer event or this timer.
    /// </summary>
    private static readonly List<HoverWatch> Watching = [];

    private static DispatcherQueueTimer? _timer;

    /// <summary>
    /// The pointer arrived. Idempotent: the shelf hands its every pointer move here, because a strip that
    /// grows its cards after the pointer has already stopped moving gets no <c>PointerEntered</c> at all.
    /// </summary>
    public void Enter()
    {
        if (Watching.Contains(this)) return;

        Watching.Add(this);
        Start();
        reveal(true);
    }

    /// <summary>
    /// The pointer is gone — said by an exit event, by the tick, or by a container being recycled. Safe to
    /// call when nothing was revealed, which is what makes it the one thing <c>Unloaded</c> has to do.
    /// </summary>
    public void Leave()
    {
        Watching.Remove(this);
        if (Watching.Count == 0) _timer?.Stop();

        reveal(false);
    }

    /// <summary>
    /// 自检：whether this watch can actually tell where the cursor is relative to its target — the host
    /// window resolved from the island, the target measured, and its rectangle in the island's own
    /// coordinates answering yes to its centre and no to a point a full target away. False means the tick
    /// is inert and 「鼠标移出窗口后不会自动恢复」 is back, with nothing on screen to say so until someone
    /// notices a card that stays lit.
    /// </summary>
    internal bool Probe()
    {
        if (target.XamlRoot is not { Content: UIElement content } root) return false;
        if (target.ActualWidth <= 0 || target.ActualHeight <= 0) return false;
        if (Host(root) == IntPtr.Zero || root.RasterizationScale <= 0) return false;

        var bounds = Bounds(content);
        var centre = new Point(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
        var beyond = new Point(centre.X + bounds.Width, centre.Y + bounds.Height);

        return bounds.Contains(centre) && !bounds.Contains(beyond);
    }

    private static void Start()
    {
        if (_timer is null)
        {
            _timer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
            if (_timer is null) return;

            _timer.Interval = Interval;
            _timer.Tick += OnTick;
        }

        if (!_timer.IsRunning) _timer.Start();
    }

    private static void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (Watching.Count == 0)
        {
            sender.Stop();
            return;
        }

        // Over a copy: a leave is this loop's own conclusion, and it removes itself from the list.
        foreach (var watch in Watching.ToArray())
        {
            if (!watch.CursorOnTarget()) watch.Leave();
        }
    }

    /// <summary>
    /// Whether the cursor is over the target, according to the OS. Everything it cannot answer reads as
    /// 「yes」: the cost of a wrong no is a reveal vanishing under a hand that is still using it, and the
    /// cost of a wrong yes is one more tick before it goes.
    /// </summary>
    private bool CursorOnTarget()
    {
        // No tree, or nothing to measure: the container has been recycled out from under the reveal.
        if (target.XamlRoot is not { Content: UIElement content } root) return false;
        if (target.ActualWidth <= 0 || target.ActualHeight <= 0) return false;

        // 有菜单开着就先不收。更多那颗按钮的菜单弹在卡片外面，指针一进菜单就已经不在卡片上了，而把按钮从
        // 它自己弹出来的菜单底下撤掉，看着像是点歪了。工具提示不算：它是指针停在按钮上才有的，指针本来就
        // 还在卡片上，而它一开就停表反倒会让真正的离开又没人管。
        if (MenuOpen(root)) return true;

        var handle = Host(root);
        if (handle == IntPtr.Zero) return true;

        if (!Native.GetCursorPos(out var cursor)) return true;
        if (!Native.ScreenToClient(handle, ref cursor)) return true;

        // Win32 answers in physical pixels and every layout question here is in logical ones. The island
        // fills the client area, so the client origin and the island root's origin are the same point —
        // the same conversion the player's chrome makes for the same reason.
        var scale = root.RasterizationScale;
        if (scale <= 0) scale = 1;

        return Bounds(content).Contains(new Point(cursor.X / scale, cursor.Y / scale));
    }

    /// <summary>The target's rectangle in the island root's coordinates, in logical pixels.</summary>
    private Rect Bounds(UIElement content) => target
        .TransformToVisual(content)
        .TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));

    /// <summary>
    /// Whether anything other than a tooltip is popped up over this island — a card's 更多 menu, a dialog.
    /// </summary>
    private static bool MenuOpen(XamlRoot root)
    {
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            if (popup.Child is not ToolTip) return true;
        }

        return false;
    }

    /// <summary>
    /// The window the island lives in, asked of the island rather than passed in: a card is created by a
    /// <c>DataTemplate</c> two pages deep and has nothing to be handed a window by.
    /// </summary>
    private static IntPtr Host(XamlRoot root)
    {
        var id = root.ContentIslandEnvironment?.AppWindowId;
        return id is { } window ? Win32Interop.GetWindowFromWindowId(window) : IntPtr.Zero;
    }
}
