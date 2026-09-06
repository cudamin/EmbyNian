namespace EmbyNian.Playback;

/// <summary>
/// Which edge or corner of the window the user has hold of. The values are <c>WMSZ_*</c>'s own, so the
/// shell hands the <c>WM_SIZING</c> <c>wParam</c> straight across rather than translating it.
/// </summary>
public enum ResizeEdge
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 3,
    TopLeft = 4,
    TopRight = 5,
    Bottom = 6,
    BottomLeft = 7,
    BottomRight = 8
}

/// <summary>A window rectangle in screen pixels, in <c>RECT</c>'s own field order.</summary>
public readonly record struct WindowBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>
/// Keeps the window the shape of the picture inside it while it is being resized: drag a side edge and
/// the other dimension follows, so the video is neither cropped nor letterboxed.
/// <para>
/// This is <c>WM_SIZING</c>'s job rather than <c>WM_SIZE</c>'s, and that is the whole point. Windows
/// asks what rectangle the drag should produce <em>before</em> anything is moved or painted, so
/// correcting it here means the window never takes a wrong size at all — a correction applied
/// afterwards is visible as the window springing back from wherever the pointer had dragged it.
/// </para>
/// <para>
/// The rectangle Windows offers is the whole window, frame included, while the aspect ratio belongs to
/// the client area — so the frame is subtracted, the client corrected, and the frame added back.
/// Getting that backwards leaves a thin band of letterboxing that grows as the window shrinks, because
/// the caption's share of the height is larger at small sizes.
/// </para>
/// <para>
/// Which edge moves follows what the hand is doing: dragging a vertical edge makes the width
/// authoritative and adjusts the height, dragging a horizontal one does the reverse, and a corner takes
/// its width as given and moves the vertical edge belonging to that corner — so the corner under the
/// pointer stays under the pointer.
/// </para>
/// <para>
/// <see cref="Fit"/> is the same arithmetic for the window nobody is dragging: the shape a file needs when
/// it starts playing into a window that happens to be some other shape.
/// </para>
/// <para>
/// Both methods used to take an <c>insetWidth</c> — a strip of the client width the ratio did not own, which
/// is what 「锁定窗口比例大小」 excluded the navigation rail with (「计算比例时要排除侧边栏」). That switch was
/// deleted on 2026-09-05 and the picture fills the client area edge to edge, so every caller passed 0 and the
/// parameter went with it.
/// </para>
/// </summary>
public static class AspectLock
{
    /// <summary>Below this the aspect is not a picture's, and correcting to it would collapse the window.</summary>
    private const double SmallestAspect = 0.2;

    private const double LargestAspect = 10.0;

    /// <summary>
    /// The corrected window rectangle for a drag of <paramref name="edge"/>, or
    /// <paramref name="proposed"/> unchanged when there is nothing to correct it to.
    /// </summary>
    /// <param name="proposed">The rectangle Windows is offering, frame included.</param>
    /// <param name="edge">Which edge or corner is being dragged.</param>
    /// <param name="aspect">The picture's width divided by its height.</param>
    /// <param name="frameWidth">Window width minus client width, in pixels.</param>
    /// <param name="frameHeight">Window height minus client height, in pixels.</param>
    /// <param name="minimumClientWidth">The smallest client area the window is allowed to have.</param>
    /// <param name="minimumClientHeight">The same for its height.</param>
    public static WindowBounds Apply(
        WindowBounds proposed,
        ResizeEdge edge,
        double aspect,
        int frameWidth,
        int frameHeight,
        int minimumClientWidth = 0,
        int minimumClientHeight = 0)
    {
        // An aspect nobody could be watching. Zero is the ordinary case — it is what the ratio reads as
        // before mpv has opened a file — and a rectangle is not corrected towards a guess.
        if (!double.IsFinite(aspect) || aspect < SmallestAspect || aspect > LargestAspect) return proposed;
        if (edge == ResizeEdge.None) return proposed;

        var minimumWidth = Math.Max(0, minimumClientWidth);

        var clientWidth = proposed.Width - frameWidth;
        var clientHeight = proposed.Height - frameHeight;

        // A frame wider than the window it is supposed to be inside: the caller has the two numbers the
        // wrong way round, or the rectangle is mid-collapse. Either way there is nothing to scale.
        if (clientWidth <= 0 || clientHeight <= 0) return proposed;

        // Whether the height follows the width. Vertical edges and both pairs of corners are dragged
        // for width; only the top and bottom edges alone are dragged for height.
        var widthLeads = edge is not (ResizeEdge.Top or ResizeEdge.Bottom);

        if (widthLeads)
        {
            clientWidth = Math.Max(clientWidth, minimumWidth);
            clientHeight = (int)Math.Round(clientWidth / aspect);

            // The minimum wins over the ratio: a window that cannot be that short has to be wider
            // instead, or the drag would stop responding at the bottom of its range.
            if (clientHeight < minimumClientHeight)
            {
                clientHeight = minimumClientHeight;
                clientWidth = (int)Math.Round(clientHeight * aspect);
            }
        }
        else
        {
            clientHeight = Math.Max(clientHeight, minimumClientHeight);
            clientWidth = (int)Math.Round(clientHeight * aspect);

            if (clientWidth < minimumWidth)
            {
                clientWidth = minimumWidth;
                clientHeight = (int)Math.Round(clientWidth / aspect);
            }
        }

        var width = clientWidth + frameWidth;
        var height = clientHeight + frameHeight;

        // The edge the hand is not holding is the one that moves. Top-anchored for anything dragged by
        // its bottom or its sides, bottom-anchored for the top edge and the two upper corners — so the
        // grabbed corner stays put and the window grows away from it.
        var anchorTop = edge is not (ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight);
        var anchorLeft = edge is not (ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft);

        var left = anchorLeft ? proposed.Left : proposed.Right - width;
        var top = anchorTop ? proposed.Top : proposed.Bottom - height;

        return new WindowBounds(left, top, left + width, top + height);
    }

    /// <summary>
    /// The window rectangle whose client area is exactly <paramref name="aspect"/>, for a window that is
    /// already on screen at <paramref name="current"/>.
    /// <see cref="Apply"/>'s other half: that one keeps the shape while an edge is being dragged, this one
    /// takes a window that was never the right shape in the first place — 窗口化时视频有黑边 — and makes it
    /// one, so mpv has nothing left to letterbox.
    /// <para>
    /// The width is authoritative and the height follows, the same way a side drag behaves, because the
    /// width is the dimension a viewer chose deliberately. The centre stays put rather than the top-left
    /// corner: the height is what moves, and growing it downwards out of a window sitting low on the
    /// screen would walk the picture off the bottom edge.
    /// </para>
    /// <para>
    /// <paramref name="workArea"/> is the monitor's usable rectangle, taskbar excluded. Passing it empty
    /// means 「do not constrain」, which is what a caller with no monitor to ask about wants.
    /// </para>
    /// </summary>
    /// <returns>
    /// <paramref name="current"/> itself when the client area is already the right shape, so a caller can
    /// compare and skip the <c>SetWindowPos</c> rather than nudging the window by a pixel on every file.
    /// </returns>
    public static WindowBounds Fit(
        WindowBounds current,
        double aspect,
        int frameWidth,
        int frameHeight,
        WindowBounds workArea = default,
        int minimumClientWidth = 0,
        int minimumClientHeight = 0)
    {
        if (!double.IsFinite(aspect) || aspect < SmallestAspect || aspect > LargestAspect) return current;

        var clientWidth = current.Width - frameWidth;
        var clientHeight = current.Height - frameHeight;
        if (clientWidth <= 0 || clientHeight <= 0) return current;

        clientWidth = Math.Max(clientWidth, Math.Max(0, minimumClientWidth));
        clientHeight = (int)Math.Round(clientWidth / aspect);

        if (clientHeight < minimumClientHeight)
        {
            clientHeight = minimumClientHeight;
            clientWidth = (int)Math.Round(clientHeight * aspect);
        }

        // Nothing may end up larger than the screen it has to be watched on. Width first and then height,
        // which converges in one pass: a width cut that leaves the height too tall means the height cut
        // was the binding one, and its width is then smaller than the one just rejected.
        var roomWidth = workArea.Width - frameWidth;
        var roomHeight = workArea.Height - frameHeight;
        if (roomWidth > 0 && roomHeight > 0)
        {
            if (clientWidth > roomWidth)
            {
                clientWidth = roomWidth;
                clientHeight = (int)Math.Round(clientWidth / aspect);
            }

            if (clientHeight > roomHeight)
            {
                clientHeight = roomHeight;
                clientWidth = (int)Math.Round(clientHeight * aspect);
            }
        }

        var width = clientWidth + frameWidth;
        var height = clientHeight + frameHeight;

        // One pixel of rounding is not a black bar. Reporting 「already right」 keeps this off the critical
        // path of every file that plays in a window the last one already shaped.
        if (Math.Abs(width - current.Width) <= 1 && Math.Abs(height - current.Height) <= 1) return current;

        var left = current.Left + (int)Math.Round((current.Width - width) / 2.0);
        var top = current.Top + (int)Math.Round((current.Height - height) / 2.0);

        if (workArea.Width > 0 && workArea.Height > 0)
        {
            left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
            top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        }

        return new WindowBounds(left, top, left + width, top + height);
    }

    /// <summary>
    /// The aspect ratio to lock to, from mpv's own <c>dwidth</c>/<c>dheight</c> when it has them and the
    /// server's stream dimensions until then. Zero when neither is usable, which is what
    /// <see cref="Apply"/> reads as 「do not correct anything」.
    /// <para>
    /// mpv's numbers are the display size rather than the stored one, so anamorphic video is already
    /// accounted for; the server's are the stored size, which is the best that can be said before a file
    /// is open. <c>WM_SIZING</c> is synchronous and cannot wait for a property read, so the ratio is
    /// always something already known by the time the drag starts.
    /// </para>
    /// </summary>
    public static double Ratio(double displayWidth, double displayHeight, double streamWidth, double streamHeight)
    {
        if (displayWidth > 0 && displayHeight > 0) return displayWidth / displayHeight;
        if (streamWidth > 0 && streamHeight > 0) return streamWidth / streamHeight;
        return 0;
    }
}
