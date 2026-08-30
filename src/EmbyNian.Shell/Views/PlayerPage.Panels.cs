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
    /// Draws the rows into the panel's grid. Rebuilt rather than updated in place because the row set
    /// itself changes: <see cref="PlaybackStats.Format"/> omits a property mpv had no answer for, so a
    /// silent file has no 声道与采样率 row at all and one that gains audio mid-stream grows one.
    /// </summary>
    private void RenderStatRows(IReadOnlyList<PlaybackStatRow> rows)
    {
        StatsRows.Children.Clear();
        StatsRows.RowDefinitions.Clear();

        var muted = BrushFor("EgOnScrimDimBrush");
        var plain = BrushFor("EgOnScrimBrush");

        for (var index = 0; index < rows.Count; index++)
        {
            StatsRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = rows[index].Label,
                FontSize = 12,
                Foreground = muted
            };
            Grid.SetRow(label, index);
            Grid.SetColumn(label, 0);
            StatsRows.Children.Add(label);

            // Monospaced, and that is not decoration: the numbers change every second, and a
            // proportional font makes 「2.41 Mbps」 jump sideways as its digits change width.
            var value = new TextBlock
            {
                Text = rows[index].Value,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas,Cascadia Mono,Microsoft YaHei UI"),
                Foreground = plain
            };
            Grid.SetRow(value, index);
            Grid.SetColumn(value, 1);
            StatsRows.Children.Add(value);
        }

        if (rows.Count != 0) return;

        StatsRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var waiting = new TextBlock
        {
            Text = "正在读取播放统计…",
            FontSize = 12,
            Foreground = muted
        };
        Grid.SetColumnSpan(waiting, 2);
        StatsRows.Children.Add(waiting);
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
                FontFamily = new FontFamily("Consolas,Cascadia Mono,Microsoft YaHei UI")
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
