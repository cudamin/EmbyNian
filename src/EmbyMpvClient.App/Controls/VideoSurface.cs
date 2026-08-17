using System.Runtime.InteropServices;
using EmbyMpvClient.App.Theme;

namespace EmbyMpvClient.App.Controls;

/// <summary>
/// The black panel the embedded player renders into. mpv receives its window handle via
/// <c>--wid</c> and creates its own child window inside this one; the panel itself only
/// provides the parent hwnd and a placeholder while the decoder warms up.
/// <para>
/// The child window swallows every mouse message that lands on the video, so the panel never
/// sees a click. <see cref="InstallMpvInputHook"/> subclasses the child's window procedure to
/// learn about double-clicks (mpv's own double-click tries <c>cycle fullscreen</c>, which has
/// nothing to do in an embedded window) and mouse movement (which wakes the floating chrome),
/// while passing everything else through untouched.
/// </para>
/// <para>
/// The exit arrow and the thin progress line are children of this panel, so they float above the
/// mpv window inside the video area. mpv's child window is created later than them and starts on
/// top of the z-order, which is why showing them raises them again. Everything that has to blend
/// with the video instead of sitting on top of it — the transport bar and its panels — is an
/// owned top-level window, because only a window has an Opacity the compositor honours.
/// </para>
/// </summary>
public sealed class VideoSurface : Panel
{
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonDoubleClick = 0x0203;

    private delegate IntPtr WndProcDelegate(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Kept per hooked window: the delegate stays alive for the subclass's lifetime, and the original proc for passthrough.</summary>
    private readonly Dictionary<IntPtr, (WndProcDelegate Delegate, IntPtr Original)> _hooks = [];
    private readonly System.Windows.Forms.Timer _revealTimer = new() { Interval = 100 };

    private readonly ProgressStrip _strip = new();
    private readonly FlatButton _exit = new() { Variant = ButtonVariant.Ghost, Glyph = Glyphs.Back, Text = "", CornerRadius = 6 };

    private long _lastClickTime;
    private Point _lastClickPosition;
    private double _bottomReveal;
    private double _topReveal;
    private long _bottomBoostUntil;

    public VideoSurface()
    {
        BackColor = Color.Black;
        Visible = false;
        TabStop = false;

        // Both are positioned manually and never overlap, so their z-order relative to each other
        // does not matter. What matters is that they end up above the mpv child window, which the
        // reveal poll guarantees by raising whichever one it shows.
        Controls.Add(_strip);
        Controls.Add(_exit);

        // The exit arrow floats fully transparent over the video until hovered: a Ghost button
        // with no fill, raised above the mpv child window like the rest of the chrome.
        _exit.Click += (_, _) => ExitRequested?.Invoke();

        // The mpv child window swallows the mouse messages and its subclass hook is lost if
        // mpv recreates the window, so proximity-based reveal is polled instead of hooked.
        _revealTimer.Tick += (_, _) => UpdateReveal();
        _revealTimer.Start();
    }

    /// <summary>True once the mpv child window has been found and hooked.</summary>
    public bool InputHookInstalled => _hooks.Count > 0;

    /// <summary>
    /// Keeps the bottom bar at full reveal regardless of the cursor. Set while one of the bar's
    /// panels is open: the cursor is then over the panel, which is a window of its own and far
    /// away from the video's bottom edge, so the proximity poll would otherwise fade the bar out
    /// from under the panel it belongs to.
    /// </summary>
    public bool HoldBottomBar { get; set; }

    /// <summary>Raised for a double-click anywhere on the video; the shell uses it for fullscreen.</summary>
    public event Action? DoubleClicked;

    /// <summary>Raised when the floating exit arrow is clicked; the shell stops playback.</summary>
    public event Action? ExitRequested;

    /// <summary>Raised whenever the reveal values change; the shell fades the bar and the title with them.</summary>
    public event Action? RevealChanged;

    /// <summary>
    /// 0..1: how completely the bottom bar should show, from the cursor's proximity to the
    /// video's bottom edge. 0 while the cursor sits at or above the vertical center.
    /// </summary>
    public double BottomReveal => _bottomReveal;

    /// <summary>
    /// 0..1: how completely the top title should show, from the cursor's proximity to the
    /// video's top edge. 0 while the cursor sits at or below the vertical center.
    /// </summary>
    public double TopReveal => _topReveal;

    /// <summary>Feeds the thin bottom progress line.</summary>
    public void SetStripFraction(double fraction) => _strip.SetFraction(fraction);

    /// <summary>
    /// Pushes the bottom bar to full reveal for a moment, for keyboard-driven playback
    /// commands that get no mouse feedback otherwise.
    /// </summary>
    public void ShowBottomBarTemporarily() => _bottomBoostUntil = Environment.TickCount64 + 2000;

    /// <summary>
    /// Polls the cursor and derives how much of the bottom bar, the top title and the thin
    /// bottom progress line should show. Runs on a timer rather than from mouse messages:
    /// the mpv child window swallows them and its subclass hook is lost if mpv recreates the
    /// window, so polling catches every state. The values fade in/out with cursor height,
    /// with the bar fully revealed when the cursor sits on the bottom edge and the title
    /// fully revealed at the top edge.
    /// </summary>
    private void UpdateReveal()
    {
        if (!Visible)
        {
            SetReveal(0, 0, force: true);
            return;
        }

        var cursor = Cursor.Position;
        var bounds = RectangleToScreen(ClientRectangle);
        var centerY = bounds.Top + bounds.Height / 2.0;

        var bottom = Math.Clamp((cursor.Y - centerY) / Math.Max(1, bounds.Bottom - centerY), 0, 1);
        var top = Math.Clamp((centerY - cursor.Y) / Math.Max(1, centerY - bounds.Top), 0, 1);
        if (HoldBottomBar || Environment.TickCount64 < _bottomBoostUntil) bottom = 1;
        SetReveal(bottom, top, force: false);
    }

    private void SetReveal(double bottom, double top, bool force)
    {
        var changed = force || Math.Abs(bottom - _bottomReveal) >= 0.01 || Math.Abs(top - _topReveal) >= 0.01;
        _bottomReveal = bottom;
        _topReveal = top;

        // The exit arrow follows the top reveal, and the thin progress line takes over while the
        // cursor sits in the middle of the video, where neither the bar nor the title is showing.
        _exit.Visible = top > 0.1;
        if (_exit.Visible) _exit.BringToFront();
        _strip.Visible = bottom < 0.15;
        if (_strip.Visible) _strip.BringToFront();

        if (changed) RevealChanged?.Invoke();
    }

    /// <summary>
    /// Subclasses the mpv child window (or any child) so the shell hears about double-clicks
    /// and mouse movement. Safe to call repeatedly: windows already hooked are left alone, and
    /// mpv recreating its window is caught by the next call.
    /// </summary>
    public void InstallMpvInputHook()
    {
        if (!IsHandleCreated) return;

        EnumChildWindows(Handle, (window, _) =>
        {
            HookWindow(window);
            return true;
        }, IntPtr.Zero);
    }

    private void HookWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || _hooks.ContainsKey(window)) return;

        var original = GetWindowLongPtr(window, GwlWndProc);
        if (original == IntPtr.Zero) return;

        var hook = new WndProcDelegate(MpvWindowProc);
        var replacement = SetWindowLongPtr(window, GwlWndProc, Marshal.GetFunctionPointerForDelegate(hook));
        if (replacement == IntPtr.Zero) return;

        _hooks[window] = (hook, original);
    }

    private IntPtr MpvWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message is WmLeftButtonDown or WmLeftButtonDoubleClick) RememberClick(lParam);

        if (!_hooks.TryGetValue(window, out var hook)) return CallWindowProc(IntPtr.Zero, window, message, wParam, lParam);
        return CallWindowProc(hook.Original, window, message, wParam, lParam);
    }

    /// <summary>
    /// mpv's child window class has no CS_DBLCLKS, so Windows never sends WM_LBUTTONDBLCLK;
    /// two left-downs inside the double-click window are the double-click.
    /// </summary>
    private void RememberClick(IntPtr lParam)
    {
        var now = Environment.TickCount64;
        var position = new Point(
            unchecked((short)(lParam.ToInt64() & 0xFFFF)),
            unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF)));

        var withinTime = now - _lastClickTime <= SystemInformation.DoubleClickTime;
        var withinDistance = Math.Abs(position.X - _lastClickPosition.X) <= SystemInformation.DoubleClickSize.Width
                             && Math.Abs(position.Y - _lastClickPosition.Y) <= SystemInformation.DoubleClickSize.Height;

        _lastClickTime = now;
        _lastClickPosition = position;

        if (withinTime && withinDistance) DoubleClicked?.Invoke();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _exit.SetBounds(Dpi.Scale(this, 14), Dpi.Scale(this, 14), Dpi.Scale(this, 40), Dpi.Scale(this, 40));
        _strip.SetBounds(0, Height - Dpi.Scale(this, 4), Width, Dpi.Scale(this, 4));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _revealTimer.Dispose();
            _hooks.Clear();
        }

        base.Dispose(disposing);
    }

    // ---- interop ---------------------------------------------------------------

    private const int GwlWndProc = -4;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
