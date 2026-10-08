using EmbyNian.MoviePilot;
using EmbyNian.Emby;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace EmbyNian.Shell.Views;

internal sealed record MoviePilotSubscriptionRequest(MoviePilotService Service, MoviePilotSubscription Subscription,
    EmbySessionScope? PlaybackScope = null, Func<MoviePilotPlaybackTarget, Task>? Play = null);

public sealed partial class MoviePilotSubscriptionPage : Page, IShellContent
{
    private MoviePilotSubscriptionRequest? _request;
    internal MoviePilotSubscriptionsViewModel ViewModel { get; } = new();
    public MoviePilotSubscriptionPage() => InitializeComponent();
    public object? NavigationRequest => _request;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MoviePilotSubscriptionRequest request) Attach(request);
    }

    internal void Attach(MoviePilotSubscriptionRequest request)
    {
        _request = request;
        ViewModel.Attach(request.Service, request.Subscription);
        ViewModel.AttachPlayback(request.PlaybackScope, request.Play);
        ViewModel.UseConfirm(ConfirmDialog.For(this));
        _ = ViewModel.ReloadAsync();
    }

    private void OnManage(object sender, RoutedEventArgs args)
    {
        if (ViewModel.Selected is { } card)
            MoviePilotSubscriptionMenu.Create(this, ViewModel, card, () => FilesHeading.StartBringIntoView()).ShowAt(ManageSubscription);
    }

    private void OnEdit(object sender, RoutedEventArgs args)
    {
        if (ViewModel.Selected is { } card)
            _ = ViewModel.EditAsync(card, subscription => MoviePilotSubscriptionEditor.ShowAsync(this, subscription));
    }

    private void OnEpisodeLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: MoviePilotSubscriptionEpisodeRow row }) _ = ViewModel.LoadEpisodeArtworkAsync(row);
    }

    public void Release() => ViewModel.Cancel();
    protected override void OnNavigatedFrom(NavigationEventArgs e) { Release(); base.OnNavigatedFrom(e); }
}
