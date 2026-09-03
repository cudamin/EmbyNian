using EmbyNian.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    }

    /// <summary>
    /// 「去掉鼠标移到进度条上时进度条出现的白色填充物」, as an assertion rather than as a look at the screen.
    /// <para>
    /// The white was the framework's, not this page's: WinUI's Slider template binds
    /// <c>HorizontalTrackRect.Fill</c> to the control's <c>Background</c> — which the markup sets to
    /// transparent so the buffered bar behind shows through — and then overwrites that Fill in the
    /// <c>PointerOver</c> and <c>Pressed</c> visual states with <c>SliderTrackFillPointerOver</c> /
    /// <c>SliderTrackFillPressed</c>, both of which resolve to white at 54% in the dark dictionary. The fix is
    /// two transparent brushes under those keys in the slider's own resources, and it fails silently: a key
    /// the template cannot find leaves the white exactly where it was.
    /// </para>
    /// <para>
    /// Nothing else can catch that. The seek bar is only on screen while a film is playing, this machine
    /// cannot inject a mouse event at all, and playing something on the user's real library is not allowed
    /// while verifying — so the states are pushed by hand here and the brushes read back off the template.
    /// The two 「don't break these」 readings beside it are deliberate: widen the override by one key and the
    /// played half of the bar or the thumb disappears, and no one would see that either.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeSeekTrack()
    {
        if (!Attached) return (false, "播放层未接线");

        var was = Visibility;
        Visibility = Visibility.Visible;
        UpdateLayout();

        // The bar is collapsed whenever the pointer has been still, and a collapsed control has no template
        // applied — GoToState would return false and every reading below would say 「找不到」.
        var clock = Now;
        _chrome.WakeFully(clock);
        Render();
        UpdateLayout();

        var report = new List<string>();
        var wrong = new List<string>();
        var reads = new List<(string State, string Track, string Played, string Thumb, string Panel)>();

        foreach (var state in new[] { "Normal", "PointerOver", "Pressed" })
        {
            VisualStateManager.GoToState(SeekSlider, state, false);
            reads.Add((state,
                Fill("HorizontalTrackRect"),
                Fill("HorizontalDecreaseRect"),
                Ink("HorizontalThumb"),
                Ink("SliderContainer")));
        }

        var tracks = reads.Select(read => read.Track).ToList();
        var panels = reads.Select(read => read.Panel).ToList();

        report.Add($"轨道：静止 {tracks[0]}、指针 {tracks[1]}、按下 {tracks[2]}");
        report.Add($"整条底：{string.Join('/', panels)}");
        report.Add($"已播放：{string.Join('/', reads.Select(read => read.Played))}");
        report.Add($"拇指：{string.Join('/', reads.Select(read => read.Thumb))}");
        report.Add($"滑杆底色 {Solid(SeekSlider.Background)}");

        // Reported, not asserted: it is whatever the layout came to, and it is here because 「the white block
        // is exactly the buffered bar's own rectangle」 is the sentence that identifies what the user saw.
        var rect = PartNamed(SeekSlider, "HorizontalTrackRect");
        if (rect is not null)
            report.Add($"轨道矩形 {rect.ActualWidth:F0}×{rect.ActualHeight:F0}"
                + $"，缓冲条 {CacheBar.ActualWidth:F0}×{CacheBar.ActualHeight:F0}");

        Want("三个模板部件都找得到", tracks.All(value => value != Missing));

        // Judged, not merely printed. The whole probe reads the same 「transparent」 out of every state when the
        // bar was never really raised and the template measured nothing — so every assertion below would pass
        // about a slider that was never in the PointerOver state at all. ProbeTap carries the same guard for
        // the same reason, and its comment records the run where five controls came out green and false.
        Want("滑杆真的立起来了", rect is { ActualWidth: > 0 } && SeekSlider.ActualWidth > 0);

        Want("轨道三档同一支", tracks.Distinct(StringComparer.Ordinal).Count() == 1);
        Want("轨道那一支什么都不画", Clear(tracks[0]));

        // The other layer in this template that can paint a background, and eight times the area: SliderContainer
        // spans the whole 938×32 hit box. Today all three of its states resolve to a transparent brush, so
        // there is nothing to fix — but the entire reason this probe exists is that a framework version can
        // change one of these keys quietly, and if it changed that one the white would come back bigger while
        // 「轨道三档同一支」 went on passing.
        Want("整条底三档都不画", panels.All(Clear));

        Want("滑杆底色是透明的纯色", Clear(Solid(SeekSlider.Background)));
        Want("已播放那一段没被一起刷成透明", reads.All(read => Visible(read.Played)));
        Want("拇指没被一起刷成透明", reads.All(read => Visible(read.Thumb)));

        // Put back the way the other probes do it, page first: a bar left up would be drawn over the library
        // grid behind this page, and 「显隐规则已复位」 is the check that would report it.
        VisualStateManager.GoToState(SeekSlider, "Normal", false);
        Visibility = was;
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        SetCursorHidden(false);
        Render();
        UpdateLayout();

        return (wrong.Count == 0,
            string.Join("；", report) + (wrong.Count == 0 ? string.Empty : $"；不符：{string.Join('、', wrong)}"));

        void Want(string what, bool ok)
        {
            if (!ok) wrong.Add(what);
        }

        string Fill(string name) =>
            PartNamed(SeekSlider, name) is Microsoft.UI.Xaml.Shapes.Rectangle shape ? Solid(shape.Fill) : Missing;

        // Two kinds of layer carry a Background in this template: the thumb is a Control, and SliderContainer
        // is a plain Grid. Asked of both rather than of Control alone, or the eight-times-larger of the two
        // would report 「找不到」 and the assertion on it would fail for the wrong reason.
        string Ink(string name) => PartNamed(SeekSlider, name) switch
        {
            Control control => Solid(control.Background),
            Panel panel => Solid(panel.Background),
            Border border => Solid(border.Background),
            _ => Missing
        };

        static string Solid(Brush? brush) => brush switch
        {
            null => "不画",
            SolidColorBrush solid => Hex(solid.Color),
            _ => "不是纯色"
        };

        static bool Clear(string value) => value.StartsWith("00", StringComparison.Ordinal);

        // Anything that is not a fully transparent solid counts as 「still visible」: this pair of readings
        // exists to catch the override being written one key too wide, and that failure always arrives as a
        // solid colour with a zero alpha.
        static bool Visible(string value) => value != Missing && value != "不画" && !Clear(value);
    }

    /// <summary>What a probe in this file prints when a template part is not where it used to be.</summary>
    private const string Missing = "找不到";

    /// <summary>
    /// A colour as <c>AARRGGBB</c>. Shared by the two probes here rather than written twice, so the two report
    /// lines cannot drift into different notations for the same thing.
    /// </summary>
    private static string Hex(Windows.UI.Color colour) =>
        $"{colour.A:X2}{colour.R:X2}{colour.G:X2}{colour.B:X2}";
}
