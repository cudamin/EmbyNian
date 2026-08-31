using EmbyNian.Playback;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EmbyNian.Shell.Views;

/// <summary>
/// The player's two panels: the 统计 grid along the right edge, and the 播放信息 dialog.
/// <para>
/// Both are drawn rather than bound, and for the same reason: neither has a fixed shape.
/// <see cref="PlaybackStats.Format"/> omits a property mpv had no answer for, so the row set itself
/// changes between one second and the next, and the dialog's body is one block of text whose lines
/// depend on what the launch resolved. What goes in them is the view model's — this file is the drawing.
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>
    /// The typeface the 统计 numbers and the 播放信息 body are set in, held once rather than named at each
    /// use. Monospaced, and that is not decoration: the numbers change every second, and a proportional
    /// font makes 「2.41 Mbps」 jump sideways as its digits change width.
    /// <para>
    /// One object for the whole page because a <c>FontFamily</c> is immutable and interned by name anyway —
    /// the panel used to construct twenty-one of them a second, one per value row, and every one of them was
    /// this. An instance field rather than a static: a static initialiser on a WinUI type can run on
    /// whichever thread first touches the class, and a <c>FontFamily</c> made off the UI thread is a
    /// wrong-thread failure waiting for the first film.
    /// </para>
    /// </summary>
    private readonly FontFamily _mono = new("Consolas,Cascadia Mono,Microsoft YaHei UI");

    /// <summary>
    /// A brush by resource key, looked up the way XAML itself would: this page's own dictionary first,
    /// then the application's.
    /// <para>
    /// <c>Resources[key]</c> alone is a trap. A control's <c>Resources</c> is only its own local
    /// dictionary — it does not walk up to <c>App.xaml</c> — and indexing a key that is not in it throws
    /// a bare <c>COMException</c> reading 「未指定的错误」 rather than returning null. So a builder asking
    /// for one of Palette.xaml's brushes would crash the first time a user opened the thing that draws it.
    /// <c>ChapterTickBrush</c> is local to this page and <c>EgTextBrush</c> is the app's; both have to
    /// work, and only this resolves both.
    /// </para>
    /// <para>
    /// It still throws for a key that exists in neither, and that is deliberate — the self-check caught a
    /// misremembered <c>EgTextMutedBrush</c> here that way, which a silent fallback would have turned into
    /// invisible text nobody noticed until it shipped.
    /// </para>
    /// <para>
    /// The keys asked for are the two 压在暗底上 ones (<c>EgOnScrim*</c>), which are theme-independent by
    /// design: this panel floats over the picture on a near-black scrim in every theme, so a light theme's
    /// <c>EgTextBrush</c> — nearly black ink — would draw invisible numbers there. That is why those two
    /// keys live in <c>Palette.xaml</c>'s theme-independent tail and not in a theme dictionary.
    /// </para>
    /// </summary>
    private Brush BrushFor(string key) =>
        (Brush)(Resources.TryGetValue(key, out var local) ? local : Application.Current.Resources[key]);

    /// <summary>
    /// Draws the rows into the panel's grid, reusing the text blocks already in it.
    /// <para>
    /// The row set itself changes between one second and the next — <see cref="PlaybackStats.Format"/> omits
    /// a property mpv had no answer for, so a silent file has no 声道与采样率 row at all and one that gains
    /// audio mid-stream grows one — which is why the grid is drawn rather than bound. It used to be cleared
    /// and rebuilt for that same reason, and that was the expensive part: the panel refreshes once a second
    /// for as long as it is open, and each refresh discarded and remade some forty text blocks, twenty-one
    /// font families and twenty-one row definitions, then measured and arranged the lot from nothing.
    /// </para>
    /// <para>
    /// So the cells are addressed by position instead: two children to a row, the label at <c>2i</c> and its
    /// value at <c>2i + 1</c>, appended in that order and only ever trimmed from the end. A cell's row and
    /// column are therefore a consequence of where it sits in <c>Children</c>, which is why both are set once
    /// when it is made and never again — and because only a few of the twenty-one values differ from one
    /// second to the next, only those few rows are re-measured.
    /// </para>
    /// <para>
    /// The 「nothing read yet」 row is still built fresh, being a single cell spanning both columns rather than
    /// half a pair. That it is a single cell is also load-bearing: an odd child count is what tells the next
    /// refresh with real rows that the grid is holding a placeholder rather than pairs.
    /// </para>
    /// </summary>
    private void RenderStatRows(IReadOnlyList<PlaybackStatRow> rows)
    {
        // Both asked for every time and before anything else, so a key that stopped resolving fails on the
        // first refresh rather than on the first file that happens to have the row it is used by — see
        // BrushFor, and the misremembered key the self-check caught there.
        var muted = BrushFor("EgOnScrimDimBrush");
        var plain = BrushFor("EgOnScrimBrush");

        if (rows.Count == 0)
        {
            StatsRows.Children.Clear();
            StatsRows.RowDefinitions.Clear();
            StatsRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var waiting = new TextBlock { Text = "正在读取播放统计…", FontSize = 12, Foreground = muted };
            Grid.SetColumnSpan(waiting, 2);
            StatsRows.Children.Add(waiting);
            return;
        }

        // The placeholder, if that is what is in there: one child where every pair is two, spanning both
        // columns, so it cannot stand in for a label.
        if (StatsRows.Children.Count % 2 != 0) StatsRows.Children.Clear();

        while (StatsRows.RowDefinitions.Count < rows.Count)
            StatsRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var index = 0; index < rows.Count; index++)
        {
            Write(Cell(index * 2, index, 0), rows[index].Label, muted);
            Write(Cell(index * 2 + 1, index, 1), rows[index].Value, plain);
        }

        // The tail the last refresh left: a file that lost a row, or one whose panel was opened on a longer
        // reading than this one.
        while (StatsRows.Children.Count > rows.Count * 2)
            StatsRows.Children.RemoveAt(StatsRows.Children.Count - 1);

        while (StatsRows.RowDefinitions.Count > rows.Count)
            StatsRows.RowDefinitions.RemoveAt(StatsRows.RowDefinitions.Count - 1);

        // The cell at a position, made if it is not there yet. Row and column are set only here, because
        // they follow from the position and the position never changes for a cell that survives a refresh.
        TextBlock Cell(int at, int row, int column)
        {
            if (at < StatsRows.Children.Count) return (TextBlock)StatsRows.Children[at];

            var cell = new TextBlock { FontSize = 12 };
            if (column == 1) cell.FontFamily = _mono;

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            StatsRows.Children.Add(cell);
            return cell;
        }

        // Guarded rather than assigned: an identical assignment still invalidates measure, and most of the
        // twenty-one rows say this second exactly what they said last.
        static void Write(TextBlock cell, string text, Brush ink)
        {
            if (!string.Equals(cell.Text, text, StringComparison.Ordinal)) cell.Text = text;
            if (!ReferenceEquals(cell.Foreground, ink)) cell.Foreground = ink;
        }
    }

    /// <summary>
    /// 播放信息: what was actually handed to mpv, which is the only thing that answers 「为什么这个文件看
    /// 起来不对」. Every line of the text is a playback fact and comes from
    /// <see cref="PlayerViewModel.MediaInfoText"/>; the dialog, its scroll box and the hold on the chrome
    /// are the only parts that are this page's.
    /// </summary>
    private async Task ShowMediaInfoAsync()
    {
        if (!Attached) return;

        var body = new ScrollViewer
        {
            MaxHeight = 420,
            Content = new TextBlock
            {
                Text = ViewModel.MediaInfoText(),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontFamily = _mono
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "播放信息",
            Content = body,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,

            // Dark whatever the theme is, unlike ConfirmDialog's: this one only ever comes up over
            // playback, and a white sheet dropped on top of a film is worse than a light-theme user
            // meeting one dark dialog.
            RequestedTheme = ElementTheme.Dark
        };

        // The dialog is where the pointer is for as long as it is up, and it is modal: the chrome must
        // not decide the user has lost interest while they are reading it.
        Hold(true, ChromeHold.Dialog);
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            Hold(false, ChromeHold.Dialog);
        }
    }
}
