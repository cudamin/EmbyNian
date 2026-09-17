namespace EmbyNian.Playback;

/// <summary>Converts UI layout dimensions to the physical pixel size consumed by mpv composition.</summary>
public static class VideoSurfaceSize
{
    /// <summary>
    /// Rounds up so fractional layout edges are covered. Invalid/nonpositive dimensions become zero;
    /// an invalid/nonpositive scale makes the entire size unknown. Positive dimensions occupy at
    /// least one pixel, and overflow saturates at <see cref="int.MaxValue"/> rather than wrapping.
    /// This does not change the visual's own DIP size or supply the backend's startup fallback.
    /// </summary>
    public static (int Width, int Height) FromDips(double width, double height, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) return (0, 0);
        return (Pixels(width, scale), Pixels(height, scale));
    }

    private static int Pixels(double dimension, double scale)
    {
        if (!double.IsFinite(dimension) || dimension <= 0) return 0;
        var pixels = Math.Ceiling(dimension * scale);
        return pixels >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)pixels);
    }
}
