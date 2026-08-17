using System.Drawing.Drawing2D;

namespace EmbyMpvClient.App.Theme;

/// <summary>
/// The few GDI+ helpers every owner-drawn control in the app needs. Centralised so a rounded
/// rectangle looks the same everywhere and no control has to remember to dispose a brush.
/// </summary>
internal static class Draw
{
    /// <summary>Antialiased geometry with crisp text; call once at the top of a paint handler.</summary>
    public static void Smooth(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    }

    public static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0) return path;

        var limit = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        if (limit == 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var diameter = limit * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Fill(Graphics graphics, Rectangle bounds, int radius, Color color)
    {
        if (color.A == 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
        using var brush = new SolidBrush(color);
        if (radius <= 0)
        {
            graphics.FillRectangle(brush, bounds);
            return;
        }

        using var path = RoundedPath(bounds, radius);
        graphics.FillPath(brush, path);
    }

    public static void Border(Graphics graphics, Rectangle bounds, int radius, Color color, int thickness = 1)
    {
        if (color.A == 0 || bounds.Width <= thickness || bounds.Height <= thickness) return;

        // Inset by half the pen width, otherwise half the stroke lands outside the control.
        var inset = Rectangle.Inflate(bounds, -(thickness - 1) - 1, -(thickness - 1) - 1);
        using var pen = new Pen(color, thickness);
        if (radius <= 0)
        {
            graphics.DrawRectangle(pen, inset);
            return;
        }

        using var path = RoundedPath(inset, radius);
        graphics.DrawPath(pen, path);
    }

    public const TextFormatFlags SingleLine =
        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;

    public const TextFormatFlags Centered =
        SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;

    public const TextFormatFlags LeftMiddle =
        SingleLine | TextFormatFlags.VerticalCenter;

    public const TextFormatFlags RightMiddle =
        SingleLine | TextFormatFlags.Right | TextFormatFlags.VerticalCenter;

    public const TextFormatFlags Wrapped =
        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;

    public static void Text(Graphics graphics, string? text, Font font, Color color, Rectangle bounds, TextFormatFlags flags)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0) return;
        TextRenderer.DrawText(graphics, text, font, bounds, color, flags);
    }

    public static Size Measure(string? text, Font font, int maxWidth = 0)
    {
        if (string.IsNullOrEmpty(text)) return Size.Empty;
        return maxWidth > 0
            ? TextRenderer.MeasureText(text, font, new Size(maxWidth, int.MaxValue), Wrapped)
            : TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), SingleLine);
    }

    /// <summary>A thin rounded progress track; used by poster cards and the now-playing bar.</summary>
    public static void ProgressBar(Graphics graphics, Rectangle bounds, double fraction, Color track, Color fill)
    {
        var radius = Math.Max(1, bounds.Height / 2);
        Fill(graphics, bounds, radius, track);

        var width = (int)Math.Round(bounds.Width * Math.Clamp(fraction, 0, 1));
        if (width <= 0) return;
        Fill(graphics, new Rectangle(bounds.X, bounds.Y, Math.Max(bounds.Height, width), bounds.Height), radius, fill);
    }

    /// <summary>Draws an image scaled to cover <paramref name="bounds"/>, cropping the overflow.</summary>
    public static void ImageCover(Graphics graphics, Image image, Rectangle bounds, int radius)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var scale = Math.Max(bounds.Width / (double)image.Width, bounds.Height / (double)image.Height);
        var width = (int)Math.Ceiling(image.Width * scale);
        var height = (int)Math.Ceiling(image.Height * scale);
        var target = new Rectangle(
            bounds.X + (bounds.Width - width) / 2,
            bounds.Y + (bounds.Height - height) / 2,
            width,
            height);

        var clip = graphics.Clip;
        try
        {
            using var path = RoundedPath(bounds, radius);
            graphics.SetClip(path);
            graphics.DrawImage(image, target);
        }
        finally
        {
            graphics.Clip = clip;
        }
    }
}

/// <summary>
/// Per-monitor DPI scaling for the hand-drawn layouts. WinForms scales control bounds and fonts
/// on its own; the poster grid and the rail compute their own geometry and have to ask.
/// </summary>
internal static class Dpi
{
    public static int Scale(Control control, int value) =>
        (int)Math.Round(value * control.DeviceDpi / 96.0);

    public static int Scale(int deviceDpi, int value) =>
        (int)Math.Round(value * deviceDpi / 96.0);
}
