using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Services;
using EmbyNian.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace EmbyNian.Shell.Views;

/// <summary>
/// The sign-in card. All of it is <see cref="SignInViewModel"/>; what is left here is the four things
/// only an element can do.
/// <para>
/// Those four: put the caret in a box, because focus belongs to an element and the view model can only
/// name which one — see <see cref="SignInFocus"/>; decide what Enter means in each of the two boxes,
/// which is a keystroke and never reaches a view model; fill the 已保存的服务器 flyout, because a
/// <c>MenuFlyout</c> is not an items control and has no <c>ItemsSource</c> to bind; and detach the
/// address box's stock clear button, a template part the markup cannot reach — see
/// <see cref="DetachClearButton"/>.
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

        // The address rides centred in the box (TextAlignment=Center in the markup), but the stock
        // TextBox template parks a 30-wide clear button in an Auto grid column beside the text host,
        // and while that button is visible the column eats ~37px off the right — the centring then
        // happens in the leftover and still reads as left-hugging. Detach the button: the Auto column
        // collapses, the text host spans the whole box, and clearing is Ctrl+A like anywhere else.
        // Loaded plus every focus, so a theme change that re-applies the template gets detached again
        // before anyone types. ButtonVisible's storyboard keeps targeting the detached object through
        // the template namescope — it animates a button nobody sees.
        ServerBox.Loaded += (_, _) => DetachClearButton(ServerBox);
        ServerBox.GotFocus += (_, _) => DetachClearButton(ServerBox);

        // The two credential boxes ride centred too (用户 2026-09-30, 参考账号/密码两栏的截图). The
        // username box is a TextBox like the address box: TextAlignment in the markup, and the same
        // clear-button detach — its × would eat the right edge exactly the same way. The password box
        // exposes no TextAlignment and its template binds none, so there the centring happens in the
        // tree — see CentrePasswordContent.
        UserBox.Loaded += (_, _) => DetachClearButton(UserBox);
        UserBox.GotFocus += (_, _) => DetachClearButton(UserBox);
        PasswordInput.Loaded += (_, _) => CentrePasswordContent(PasswordInput);
        PasswordInput.GotFocus += (_, _) => CentrePasswordContent(PasswordInput);
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
    /// Detaches the stock clear button so the address text can centre across the whole box (see the
    /// constructor comment for why detaching and not just hiding).
    /// </summary>
    private static void DetachClearButton(TextBox box)
    {
        if (FindNamedDescendant(box, "DeleteButton") is not { } button) return;
        (button.Parent as Grid)?.Children.Remove(button);
    }

    /// <summary>
    /// Centres the masked characters and the placeholder of a <see cref="PasswordBox"/>. It exposes no
    /// <c>TextAlignment</c> and its template never binds one, so the text host — an ordinary
    /// ScrollViewer, same part names as the TextBox template — is shrunk to its content and let the
    /// grid centre it; the placeholder line is a real TextBlock and takes a TextAlignment directly.
    /// </summary>
    private static void CentrePasswordContent(PasswordBox box)
    {
        if (FindNamedDescendant(box, "ContentElement") is { } host) host.HorizontalAlignment = HorizontalAlignment.Center;
        if (FindNamedDescendant(box, "PlaceholderTextContentPresenter") is TextBlock placeholder) placeholder.TextAlignment = TextAlignment.Center;
    }

    private static FrameworkElement? FindNamedDescendant(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement found && found.Name == name) return found;
            var deeper = FindNamedDescendant(child, name);
            if (deeper is not null) return deeper;
        }

        return null;
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
