using EmbyNian.Infrastructure;
using Microsoft.UI.Xaml.Data;

namespace EmbyNian.Shell.Views;

/// <summary>
/// Turns the seek slider's own 0..1000 into a clock, which is what makes hovering the bar read
/// 「1:07:32」 for free.
/// <para>
/// Stateful, and deliberately: the duration belongs to the file, not to the slider, so the page pushes
/// it in on every status update. A converter that took the duration as its <c>parameter</c> would have
/// to be re-bound on every new file, and one that reached for the player would be a converter with a
/// dependency on the app.
/// </para>
/// <para>
/// A file of its own rather than a second type in <c>PlayerPage.xaml.cs</c>: it is the one piece of
/// that file with no reference to the page at all, and <c>&lt;local:SeekClockConverter /&gt;</c> in the
/// XAML resolves it by namespace, which has not changed.
/// </para>
/// </summary>
public sealed class SeekClockConverter : IValueConverter
{
    /// <summary>The slider's own maximum, mirrored so the two cannot drift apart.</summary>
    public double Scale { get; set; } = 1000;

    /// <summary>The file's run time in seconds; 0 until mpv has one.</summary>
    public double DurationSeconds { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (DurationSeconds <= 0 || value is not double raw) return "--:--";

        var seconds = Math.Clamp(raw / Scale, 0, 1) * DurationSeconds;
        return TimeFormat.Clock(TimeSpan.FromSeconds(seconds));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
