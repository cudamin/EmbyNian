using Momoka.MoviePilot;
using Momoka.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Momoka.Shell.Views;

public sealed partial class MoviePilotSubscriptionsView : UserControl
{
    private Action<MoviePilotSubscription>? _open;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(1) };
    internal MoviePilotSubscriptionsViewModel ViewModel { get; } = new();

    public MoviePilotSubscriptionsView()
    {
        InitializeComponent();
        _refresh.Tick += async (_, _) => { if (!ViewModel.Busy && !ViewModel.Working) await ViewModel.ReloadAsync(); };
    }

    internal void Attach(MoviePilotService service, Action<MoviePilotSubscription>? open)
    {
        _open = open;
        ViewModel.Attach(service);
        ViewModel.UseConfirm(ConfirmDialog.For(this));
        _ = ViewModel.ReloadAsync();
        _refresh.Start();
    }

    internal void Release() { _refresh.Stop(); ViewModel.Cancel(); _open = null; }
    private void Open(MoviePilotSubscriptionCard card) { if (ViewModel.Owns(card)) _open?.Invoke(card.Subscription); }
    private void OnPosterOpen(object? sender, EventArgs args) { if (sender is MoviePilotSubscriptionPoster { Card: { } card }) Open(card); }
    private void OnCardLoaded(object sender, RoutedEventArgs args)
    { if (sender is FrameworkElement { Tag: MoviePilotSubscriptionCard card }) _ = ViewModel.LoadPosterAsync(card); }
    private void OnPosterMenu(object? sender, EventArgs args)
    {
        if (sender is MoviePilotSubscriptionPoster { Card: { } card } poster) Menu(card).ShowAt(poster.MenuAnchor);
    }
    private MenuFlyout Menu(MoviePilotSubscriptionCard card) => MoviePilotSubscriptionMenu.Create(this, ViewModel, card, () => Open(card));
}
