using EmbyNian.Playback;

namespace EmbyNian.Infrastructure;

/// <summary>
/// Which monitor a window opens on, and where in it. 「跑测试的时候能不能在第二屏幕跑」: the self-check drives
/// the real window for the twenty seconds it runs — it goes fullscreen, it drags the frame about, it reads
/// back the pixels it drew — so the run cannot help taking over a screen while it lasts. It might as well
/// not be the screen being worked on.
/// <para>
/// Here rather than in the shell because every case worth being sure of is one this machine is not: a single
/// monitor, a screen narrower than the window's default width, a work area that does not start at the origin.
/// The shell's part is two <c>SetWindowPos</c> calls around these two answers.
/// </para>
/// </summary>
public static class ScreenPlacement
{
    /// <summary>Leave the placement to Windows, which is what every ordinary launch wants.</summary>
    public const int WhereverWindows = 0;

    /// <summary>
    /// Any screen except the main one, and <see cref="WhereverWindows"/>'s behaviour on a machine that has
    /// only the one. What a self-check run asks for unless <c>--screen</c> said otherwise.
    /// </summary>
    public const int NotThePrimary = -1;

    /// <summary>
    /// Which of the screens to open on, as an index into <paramref name="primary"/>, or −1 for 「leave it
    /// where Windows put it」.
    /// </summary>
    /// <param name="request">
    /// 1-based in the order the OS enumerates screens, <see cref="NotThePrimary"/>, or
    /// <see cref="WhereverWindows"/>. An index past the last screen reads as
    /// <see cref="WhereverWindows"/> rather than as the nearest one: 「--screen 3」 on a two-monitor desktop
    /// is a mistake, and quietly choosing a different screen than the one asked for hides it.
    /// </param>
    /// <param name="primary">Whether each screen, in that same order, is the main one.</param>
    public static int Choose(int request, IReadOnlyList<bool> primary)
    {
        if (primary.Count == 0) return -1;

        if (request == NotThePrimary)
        {
            for (var screen = 0; screen < primary.Count; screen++)
                if (!primary[screen]) return screen;

            // Nothing but the main screen, which is a laptop with nothing plugged in. 「Not the primary」
            // has to mean 「then never mind」 there rather than 「screen 1」, or the flag would move the
            // window on a machine where there is nowhere else for it to go.
            return -1;
        }

        return request >= 1 && request <= primary.Count ? request - 1 : -1;
    }

    /// <summary>
    /// A window of <paramref name="width"/>×<paramref name="height"/> centred in <paramref name="work"/> and
    /// never larger than it, or an empty rectangle when there is nothing to place or nowhere to put it.
    /// <para>
    /// The clamp is not defensive tidying. The second screen on the machine this was written for is a
    /// portrait 1080×1920, where the default 1280 wide hangs over the edge of the desktop — and pixels
    /// outside the desktop cannot be read back, which is exactly what the self-check does with them.
    /// </para>
    /// </summary>
    public static WindowBounds Centre(WindowBounds work, int width, int height)
    {
        if (work.Width <= 0 || work.Height <= 0 || width <= 0 || height <= 0) return default;

        var fittedWidth = Math.Min(width, work.Width);
        var fittedHeight = Math.Min(height, work.Height);

        var left = work.Left + ((work.Width - fittedWidth) / 2);
        var top = work.Top + ((work.Height - fittedHeight) / 2);

        return new WindowBounds(left, top, left + fittedWidth, top + fittedHeight);
    }

    /// <summary>
    /// Where a window last left at <paramref name="saved"/> should open now, given the work areas of the
    /// screens attached at this moment. <c>default</c> — an empty rectangle — means 「nothing usable was
    /// recorded」 and is the caller's cue to fall back to its own centred default.
    /// <para>
    /// The desktop is not the one the size was written on. A monitor gets unplugged, a laptop comes back
    /// from a dock, a screen changes resolution — and a window restored verbatim onto a desktop that no
    /// longer has those pixels is a window with no visible title bar, which cannot be moved or closed by
    /// hand. So the saved rectangle is placed on the screen it overlaps most, clamped to fit that screen,
    /// and shifted until all of it is inside.
    /// </para>
    /// <para>
    /// When it overlaps nothing at all the size is still honoured and only the position is given up: the
    /// window is centred on <paramref name="screens"/>[0]. Keeping the size is the point — 「the monitor I
    /// had it on is gone」 is not a reason to also forget how big the user made it.
    /// </para>
    /// </summary>
    /// <param name="saved">The bounds recorded last time, in desktop coordinates. Empty when none were.</param>
    /// <param name="screens">Every attached screen's work area. The first is where a homeless window lands.</param>
    /// <param name="minimumWidth">The window's own floor, itself clamped to the screen: a 900-wide minimum
    /// cannot be honoured on an 800-wide screen, and obeying it there would push the frame off the edge.</param>
    /// <param name="minimumHeight">Likewise for the height.</param>
    public static WindowBounds Restore(
        WindowBounds saved,
        IReadOnlyList<WindowBounds> screens,
        int minimumWidth = 0,
        int minimumHeight = 0)
    {
        if (saved.Width <= 0 || saved.Height <= 0 || screens.Count == 0) return default;

        var seat = screens[0];
        var most = 0L;
        foreach (var screen in screens)
        {
            if (screen.Width <= 0 || screen.Height <= 0) continue;

            var shared = Overlap(saved, screen);
            if (shared <= most) continue;

            most = shared;
            seat = screen;
        }

        if (seat.Width <= 0 || seat.Height <= 0) return default;

        var width = Math.Clamp(saved.Width, Math.Min(minimumWidth, seat.Width), seat.Width);
        var height = Math.Clamp(saved.Height, Math.Min(minimumHeight, seat.Height), seat.Height);

        // most == 0: the screen it was on is not here any more, so the position is meaningless and only
        // the size survives. Otherwise keep the corner the user left it at, pulled inside this screen.
        var left = most > 0
            ? Math.Clamp(saved.Left, seat.Left, seat.Right - width)
            : seat.Left + ((seat.Width - width) / 2);

        var top = most > 0
            ? Math.Clamp(saved.Top, seat.Top, seat.Bottom - height)
            : seat.Top + ((seat.Height - height) / 2);

        return new WindowBounds(left, top, left + width, top + height);
    }

    /// <summary>How many pixels the two rectangles share. 0 when they do not touch.</summary>
    private static long Overlap(WindowBounds left, WindowBounds right)
    {
        var width = Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left);
        var height = Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top);

        return width > 0 && height > 0 ? (long)width * height : 0;
    }
}
