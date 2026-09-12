using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Services;
using EmbyNian.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EmbyNian.Shell.Views;

/// <summary>
/// The sign-in card. All of it is <see cref="SignInViewModel"/>; what is left here is the three things
/// only an element can do.
/// <para>
/// Those three: put the caret in a box, because focus belongs to an element and the view model can only
/// name which one — see <see cref="SignInFocus"/>; decide what Enter means in each of the two boxes,
/// which is a keystroke and never reaches a view model; and fill the 已保存的服务器 flyout, because a
/// <c>MenuFlyout</c> is not an items control and has no <c>ItemsSource</c> to bind.
/// </para>
/// <para>
/// Still a <c>UserControl</c> rather than a <c>Page</c>, for the reason the markup gives: the shell keeps
/// it as a sibling of the NavigationView and shows one or the other, so there is one window, one theme
/// root and one <c>XamlRoot</c> for the whole app, and signing out does not rebuild the visual tree.
/// </para>
/// </summary>
public sealed partial class SignInPage : UserControl
{
    public SignInPage()
    {
        // Constructed with the page rather than on attach, so x:Bind never has a null root; the same
        // reason DetailPage gives. InitializeComponent evaluates the bindings, and a view model handed
        // over afterwards would leave every one of them showing nothing until a property changed.
        ViewModel = new SignInViewModel();
        InitializeComponent();

        ViewModel.FocusRequested += FocusOn;

        // Forwarded rather than re-exposed, so the shell keeps subscribing to the page it can see and
        // gets the page as the sender.
        ViewModel.SignedIn += (_, e) => SignedIn?.Invoke(this, e);

        // Rebuilt on change rather than once, because Prepare refills the list on every show and a
        // server added on the servers page has to appear in the menu without a restart.
        ViewModel.SavedServers.CollectionChanged += (_, _) => FillSavedMenu();

        // Prepare runs before the window is shown, where focus has nowhere to go yet.
        Loaded += (_, _) =>
        {
            HomeMotion.Reveal(SignInLayout);
            FocusOn(ViewModel.NextFocus());
        };
        Unloaded += (_, _) => HomeMotion.Stop(SignInLayout);
    }

    /// <summary>Raised once <see cref="EmbySession"/> holds a live client; the shell then shows the pages.</summary>
    internal event EventHandler? SignedIn;

    internal SignInViewModel ViewModel { get; }

    /// <summary>Whether a connect, a sign-in or a token restore is in flight.</summary>
    internal bool IsBusy => ViewModel.Busy;

    /// <summary>
    /// How many entries the 已保存的服务器 flyout really holds, for the self-check. This is the one list
    /// on the card still built by hand — a <c>MenuFlyout</c> has no <c>ItemsSource</c> — so it is the one
    /// that could quietly build nothing while the collection behind it is full.
    /// </summary>
    internal int SavedMenuCount => SavedMenu.Items.Count;

    /// <summary>
    /// Set once by the shell, before this page is ever shown. This card is never the frame's content, so
    /// this is its <c>OnNavigatedTo</c>: the one place it resolves anything, and the one block that goes
    /// when the shell stops handing a container around.
    /// </summary>
    internal void Attach(IServiceProvider services) =>
        ViewModel.Attach(
            services.GetRequiredService<ISettingsService>(),
            services.GetRequiredService<EmbySession>(),
            services.GetRequiredService<CredentialVault>());

    /// <inheritdoc cref="SignInViewModel.Prepare"/>
    internal void Prepare(ServerProfile? prefer = null) => ViewModel.Prepare(prefer);

    /// <summary>Stops whatever this page has in flight; the shell calls it when it hides the page.</summary>
    internal void Detach() => ViewModel.Detach();

    /// <inheritdoc cref="SignInViewModel.ShowRestoring"/>
    internal void ShowRestoring(string serverName) => ViewModel.ShowRestoring(serverName);

    /// <summary>
    /// Puts the caret where the view model asked. Named for the action rather than called <c>Focus</c>,
    /// which <see cref="Control"/> already has. Silent before the tree is live: a Focus call on an
    /// element with no <c>XamlRoot</c> has nothing to focus into.
    /// </summary>
    private void FocusOn(SignInFocus what)
    {
        if (XamlRoot is null) return;

        switch (what)
        {
            case SignInFocus.Address:
                ServerBox.Focus(FocusState.Programmatic);
                break;

            case SignInFocus.Username:
                UserBox.Focus(FocusState.Programmatic);
                break;

            case SignInFocus.Password:
                PasswordInput.Focus(FocusState.Programmatic);
                break;

            case SignInFocus.RejectedPassword:
                PasswordInput.Focus(FocusState.Programmatic);
                PasswordInput.SelectAll();
                break;

            // Resolved by the view model before it asks, so there is nothing left to decide here. Kept
            // for a caller that hands the request straight over; NextFocus never answers Next itself, so
            // this bottoms out immediately.
            case SignInFocus.Next:
            default:
                FocusOn(ViewModel.NextFocus());
                break;
        }
    }

    private void OnAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        ViewModel.ConnectCommand.Execute(null);
    }

    private void OnCredentialKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;

        // Enter in the username box means "on to the password", not "log in with a blank one".
        if (ReferenceEquals(sender, UserBox) && ViewModel.Password.Length == 0)
        {
            PasswordInput.Focus(FocusState.Programmatic);
            return;
        }

        ViewModel.SignInCommand.Execute(null);
    }

    /// <summary>
    /// A discovered server was clicked. <c>ListView.ItemClick</c> is one of the few things in WinUI with
    /// no command surface, and this project carries no toolkit behaviours to give it one.
    /// </summary>
    private void OnDiscoveredServerPicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EmbyDiscoveredServer server) ViewModel.PickDiscovered(server);
    }

    /// <summary>
    /// The one list this page still builds by hand. A <c>MenuFlyout</c> holds <c>Items</c>, not an
    /// <c>ItemsSource</c>, so there is nothing to bind — but each entry can carry the view model's own
    /// command, which keeps the decision about what a pick means out of here.
    /// </summary>
    private void FillSavedMenu()
    {
        SavedMenu.Items.Clear();

        foreach (var server in ViewModel.SavedServers)
        {
            SavedMenu.Items.Add(new MenuFlyoutItem
            {
                Text = $"{server.Name} — {server.Url}",
                Command = ViewModel.UseSavedCommand,
                CommandParameter = server
            });
        }
    }
}
