using Momoka.MoviePilot;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Momoka.Shell.Views;

public sealed partial class MoviePilotSubscriptionEditor : UserControl
{
    public MoviePilotSubscriptionEditor() => InitializeComponent();

    internal void Set(MoviePilotSubscription subscription)
    {
        var edit = MoviePilotSubscriptionEdit.From(subscription);
        IdentityText.Text = subscription.Media.Confirmation;
        KeywordInput.Text = edit.Keyword; IncludeInput.Text = edit.Include; ExcludeInput.Text = edit.Exclude;
        QualityInput.Text = edit.Quality; ResolutionInput.Text = edit.Resolution; EffectInput.Text = edit.Effect;
        StartInput.Value = edit.StartEpisode; TotalInput.Value = edit.TotalEpisodes;
        EpisodeFields.Visibility = subscription.IsSeries ? Visibility.Visible : Visibility.Collapsed;
    }

    internal MoviePilotSubscriptionEdit Read() => new(KeywordInput.Text, IncludeInput.Text, ExcludeInput.Text,
        QualityInput.Text, ResolutionInput.Text, EffectInput.Text,
        double.IsNaN(TotalInput.Value) ? 0 : checked((int)TotalInput.Value), double.IsNaN(StartInput.Value) ? 0 : checked((int)StartInput.Value));

    internal static async Task<MoviePilotSubscriptionEdit?> ShowAsync(FrameworkElement owner, MoviePilotSubscription subscription)
    {
        if (owner.XamlRoot is null) return null;
        var editor = new MoviePilotSubscriptionEditor();
        editor.Set(subscription);
        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            RequestedTheme = owner.ActualTheme,
            Title = "编辑订阅 · " + subscription.Title,
            Content = editor,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { editor.Read().Changes(subscription); }
            catch (Exception error) { editor.ValidationText.Text = error.Message; args.Cancel = true; }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? editor.Read() : null;
    }
}
