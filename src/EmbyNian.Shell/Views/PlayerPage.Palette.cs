using EmbyNian.Playback;
using Microsoft.UI.Xaml.Media;

namespace EmbyNian.Shell.Views;

/// <summary>
/// Where the overlay's colours actually get onto the screen: <see cref="PlayerPalette"/>'s table, copied
/// into the empty brushes <c>PlayerPage.xaml</c> declares.
/// <para>
/// The XAML holds shells rather than values — eighteen <c>SolidColorBrush</c>es with no <c>Color</c> and
/// two <c>LinearGradientBrush</c>es with no stops — and this fills them in from the constructor. That
/// works because <c>SolidColorBrush.Color</c> is a dependency property and a brush is a shared object:
/// every <c>StaticResource</c> in the file already points at these instances, so painting one here paints
/// everything drawn with it. Same mechanism as <c>DetailPage.PaintScrim</c>, which was the precedent for
/// keeping a gradient's stops in Core.
/// </para>
/// <para>
/// Why not simply leave the hex in the markup: it was in the markup, thirty-five times, nine distinct
/// colours written between two and seven times each, and no test in the repository could see any of them.
/// The test project cannot reference this assembly — it is <c>net10.0</c> and this is
/// <c>net10.0-windows10.0.19041.0</c> — so a value that lives here is a value nobody can assert on. In
/// Core the ladder, the opacity order and the contrast against the film black are all pinned.
/// </para>
/// <para>
/// The failure mode this arrangement introduces is quiet, which is why <see cref="ProbePalette"/> lives
/// here next to the painter rather than with the other probes: a key that does not resolve throws while
/// the page is being loaded and is therefore impossible to miss, but a brush that resolves and never gets
/// painted is transparent — invisible text over a film, on a page that otherwise works.
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>
    /// Copies the table onto the page's own brushes. Called from the constructor, immediately after
    /// <c>InitializeComponent</c> and before anything else that might draw.
    /// </summary>
    private void PaintPalette()
    {
        foreach (var (key, colour) in PlayerPalette.Brushes)
            ((SolidColorBrush)Resources[key]).Color = ThemeHost.ToColor(colour);

        Wash("PlayerBottomScrim", PlayerPalette.BottomScrimStops);
        Wash("PlayerTopScrim", PlayerPalette.TopScrimStops);
    }

    /// <summary>
    /// Fills one of the two scrims. Only the alpha varies along either of them, so the stops carry a byte
    /// and the RGB is <see cref="PlayerPalette.Film"/> throughout — a scrim whose hue drifted as it
    /// deepened would tint the picture rather than dim it.
    /// <para>
    /// Cleared first: <see cref="PaintPalette"/> runs once per page today, and a second call that appended
    /// would leave a gradient with two sets of stops fighting over the same offsets.
    /// </para>
    /// </summary>
    private void Wash(string key, IReadOnlyList<(double Along, byte Alpha)> stops)
    {
        var brush = (LinearGradientBrush)Resources[key];
        var film = PlayerPalette.Film;

        brush.GradientStops.Clear();
        foreach (var (along, alpha) in stops)
            brush.GradientStops.Add(new GradientStop
            {
                Offset = along,
                Color = ThemeHost.ToColor(film.WithAlpha(alpha))
            });
    }

    /// <summary>
    /// Reads every brush back off the page and compares it with the table it was painted from.
    /// <para>
    /// This is the probe the arrangement above owes the reader. A key that does not resolve cannot get this
    /// far — <c>StaticResource</c> throws while the page is loading, so the player simply would not
    /// construct — but a brush that resolves and never gets painted is fully transparent, and every failure
    /// that produces looks like something else: OSD text that is not there, a panel with no background over
    /// a bright frame, a scrim that stopped dimming. None of it is visible to a build, and a unit test
    /// cannot reach this assembly at all.
    /// </para>
    /// <para>
    /// Read back off <c>Resources</c> rather than off the elements, because that is where the shared
    /// instance is: an element drawn with a brush of its own — a literal put back into the markup, a style
    /// that overrode one — would keep working while its colour quietly stopped following the table, and the
    /// count in the report is what shows that up.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbePalette()
    {
        var wrong = new List<string>();
        var painted = 0;

        foreach (var (key, colour) in PlayerPalette.Brushes)
        {
            if (!Resources.TryGetValue(key, out var found) || found is not SolidColorBrush brush)
            {
                wrong.Add($"{key} 不在本页字典里");
                continue;
            }

            var want = ThemeHost.ToColor(colour);
            if (brush.Color != want)
            {
                // The hex both ways round, because 「不一致」 on its own does not say whether the brush was
                // never painted or painted from somewhere else — and 00000000 is exactly the first case.
                wrong.Add($"{key} 是 {Hex(brush.Color)}，表上是 {colour.ToHex()}");
                continue;
            }

            painted++;
        }

        var stops = new List<string>();

        foreach (var (key, table) in new (string Key, IReadOnlyList<(double Along, byte Alpha)> Table)[]
                 {
                     ("PlayerBottomScrim", PlayerPalette.BottomScrimStops),
                     ("PlayerTopScrim", PlayerPalette.TopScrimStops)
                 })
        {
            if (!Resources.TryGetValue(key, out var found) || found is not LinearGradientBrush brush)
            {
                wrong.Add($"{key} 不在本页字典里");
                continue;
            }

            if (brush.GradientStops.Count != table.Count)
            {
                wrong.Add($"{key} 有 {brush.GradientStops.Count} 个停点，表上是 {table.Count} 个");
                continue;
            }

            for (var index = 0; index < table.Count; index++)
            {
                var (along, alpha) = table[index];
                var stop = brush.GradientStops[index];
                var want = ThemeHost.ToColor(PlayerPalette.Film.WithAlpha(alpha));

                // A twentieth of a pixel's worth of offset, same slack the geometry probes use: the table
                // holds doubles and so does GradientStop, so anything but exact equality is a real edit.
                if (Math.Abs(stop.Offset - along) > 0.001 || stop.Color != want)
                    wrong.Add($"{key} 第 {index + 1} 个停点是 {stop.Offset:0.##}/{Hex(stop.Color)}"
                              + $"，表上是 {along:0.##}/{want.A:X2}");
            }

            stops.Add($"{key} {brush.GradientStops.Count} 个停点");
        }

        return (wrong.Count == 0,
            $"{painted}/{PlayerPalette.Brushes.Count} 支画刷与 Core 那张表一致；{string.Join('、', stops)}"
            + (wrong.Count == 0 ? string.Empty : $"；不符：{string.Join('、', wrong)}"));

        static string Hex(Windows.UI.Color colour) =>
            $"{colour.A:X2}{colour.R:X2}{colour.G:X2}{colour.B:X2}";
    }
}
