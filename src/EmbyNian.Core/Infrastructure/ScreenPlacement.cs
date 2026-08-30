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
}
