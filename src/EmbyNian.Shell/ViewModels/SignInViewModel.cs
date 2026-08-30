using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Services;
using EmbyNian.Shell.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// Which face the sign-in card is showing. Two steps and a third state that is neither: an automatic
/// sign-in shows no form at all, because there is nothing to type yet and offering an empty one during a
/// connection that usually succeeds only invites a second attempt over the top of the first.
/// </summary>
public enum SignInStep
{
    Address,
    Credential,
    Restoring
}

/// <summary>
/// Where the caret should go. The one thing on this page that a view model genuinely cannot do: focus
/// belongs to an element, and which element is showing is the view's business.
/// <para>
/// <see cref="Next"/> is 「whatever box the user has to fill in next」, which is a question about the
/// step and the username, so the view model still decides it — the view only carries it out.
/// </para>
/// </summary>
internal enum SignInFocus
{
    Next,
    Address,
    Username,
    Password,

    /// <summary>Focus the password and select it, so a wrong one is replaced by typing rather than cleared first.</summary>
    RejectedPassword
}

/// <summary>
/// Address in, session out. Everything else here exists to save typing: the saved-server menu, the user
/// list the server itself advertises, and the password the vault already has.
/// <para>
/// The state that used to live in named elements — a caption, a spinner, an error bar, two panels'
/// visibility, four boxes and a button's own label — is bound now, so it can be read without a window on
/// screen and cannot drift from the element it drives. What is left in the code-behind is focus, two
/// Enter keys and a flyout that has no <c>ItemsSource</c>.
/// </para>
/// </summary>
public sealed partial class SignInViewModel : PageViewModel
{
    private const string Category = "login";

    /// <summary>The caption the card wears whenever it is not saying something more specific.</summary>
    private const string FirstCaption = "连接到你的 Emby 服务器";

    private ISettingsService? _settings;
    private EmbySession? _session;
    private CredentialVault? _vault;

    /// <summary>
    /// Whether the user list is being filled rather than picked from. The view model's version of what
    /// the page used to do with <c>UserList.SelectionChanged -= OnUserPicked</c> around the assignment:
    /// pre-selecting the account the user last signed in with must not count as choosing it, or a connect
    /// would go straight on to a sign-in with whatever password the vault happened to hold.
    /// <para>
    /// A flag rather than a value comparison, because the two cases are not told apart by the value —
    /// the same <see cref="EmbyUser"/> arrives either way. It is only ever set inside
    /// <see cref="ShowCredentialStep"/>, which does not await.
    /// </para>
    /// </summary>
    private bool _filling;

    public SignInViewModel()
    {
        // Count is not a property the generator can watch — [NotifyPropertyChangedFor] hangs off a
        // property's setter, and a collection does not have one — so the three derived visibilities are
        // raised from the collections themselves.
        Discovered.CollectionChanged += OnDiscoveredChanged;
        Users.CollectionChanged += OnUsersChanged;
        SavedServers.CollectionChanged += OnSavedChanged;
    }

    /// <summary>Raised once <see cref="EmbySession"/> holds a live client; the shell then shows the pages.</summary>
    internal event EventHandler? SignedIn;

    /// <summary>Asks the view to move the caret. See <see cref="SignInFocus"/>.</summary>
    internal event Action<SignInFocus>? FocusRequested;

    /// <summary>Which of the three faces is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddressVisibility))]
    [NotifyPropertyChangedFor(nameof(CredentialVisibility))]
    public partial SignInStep Step { get; set; }

    /// <summary>The line under the app's name. Says what is happening whenever something is.</summary>
    [ObservableProperty]
    public partial string Caption { get; set; } = FirstCaption;

    /// <summary>Which server the credential step is asking about, with its Emby version when it gave one.</summary>
    [ObservableProperty]
    public partial string ServerLabel { get; set; } = "";

    [ObservableProperty]
    public partial string Address { get; set; } = "";

    [ObservableProperty]
    public partial string Username { get; set; } = "";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    /// <summary>Ticked by default: a first-time account has nothing saved to disagree with.</summary>
    [ObservableProperty]
    public partial bool Remember { get; set; } = true;

    /// <summary>
    /// Whether the discovery results are on show. Separate from whether there are any, because pressing
    /// 换个服务器 hides the list without throwing away what was found — the same distinction
    /// <see cref="ServersViewModel"/> draws, and for the same reason.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscoveredVisibility))]
    public partial bool ShowDiscovered { get; set; }

    /// <summary>
    /// The user the list is pointing at. Two-way: the list writes the pick in, and the credential step
    /// writes the pre-selection out. <see cref="OnSelectedUserChanged"/> tells those two apart.
    /// </summary>
    [ObservableProperty]
    public partial EmbyUser? SelectedUser { get; set; }

    /// <summary>Servers found on this network by <see cref="DiscoverCommand"/>.</summary>
    public ObservableCollection<EmbyDiscoveredServer> Discovered { get; } = [];

    /// <summary>The users this server is willing to advertise. Empty when it will not say.</summary>
    public ObservableCollection<EmbyUser> Users { get; } = [];

    /// <summary>Every server already in settings, for the 已保存的服务器 menu.</summary>
    public ObservableCollection<ServerProfile> SavedServers { get; } = [];

    public Visibility AddressVisibility => Show(Step == SignInStep.Address);

    public Visibility CredentialVisibility => Show(Step == SignInStep.Credential);

    public Visibility DiscoveredVisibility => Show(ShowDiscovered && Discovered.Count > 0);

    public Visibility UsersVisibility => Show(Users.Count > 0);

    /// <summary>One saved server is what the address box already says.</summary>
    public Visibility SavedVisibility => Show(SavedServers.Count > 1);

    /// <summary>The sign-in button's own label, which used to be assigned alongside its IsEnabled.</summary>
    public string SignInText => Busy ? "请稍候…" : "登录";

    /// <summary>
    /// What gates the three buttons that start something. The guard the click handlers used to open with
    /// — <c>if (_busy) return;</c> — is still at the top of each method, because a saved-server pick and a
    /// passwordless account both start one of them without a button being pressed; this is what greys the
    /// buttons out so a user who presses one twice is not surprised by nothing happening.
    /// </summary>
    private bool CanRun => !Busy;

    /// <summary>
    /// Nothing to reload: this control is never the frame's content, so the shell's refresh never reaches
    /// it. Signing out goes through <see cref="Prepare"/> instead, which is the same idea for a card whose
    /// contents come from settings rather than from the server.
    /// </summary>
    public override Task ReloadAsync() => Task.CompletedTask;

    /// <summary>
    /// Whether <see cref="Attach"/> has run. The three services arrive in that one call as non-nullable
    /// parameters, so this single question answers for all of them; past this gate they are dereferenced
    /// with <c>!</c>.
    /// </summary>
    private bool Attached => _settings is not null;

    /// <summary>
    /// The live settings object, read through the service rather than copied: it is one instance for the
    /// process, and this card is refilled from it on every show — including after a server was added on
    /// the servers page.
    /// </summary>
    private AppSettings Settings => _settings!.Settings;

    /// <summary>Set once by the shell, before this card is ever shown.</summary>
    internal void Attach(ISettingsService settings, EmbySession session, CredentialVault vault)
    {
        _settings = settings;
        _session = session;
        _vault = vault;

        Prepare();
    }

    /// <summary>
    /// Puts the card back to its first step and refills it from settings. Called on every show, not just
    /// the first: after a sign-out the address the user just left is the one they most likely want.
    /// </summary>
    /// <param name="prefer">
    /// The server to fill in, when the caller has one in mind — a 切换服务器 that needed a password.
    /// Otherwise the last one signed into.
    /// </param>
    internal void Prepare(ServerProfile? prefer = null)
    {
        if (!Attached) return;

        Cancel();
        ClearNotice();
        ShowAddressStep();

        // Cleared as well as hidden, which is the difference between coming back to this step and
        // arriving at it: a fresh card should not hold the last visit's discovery results.
        Discovered.Clear();

        var settings = Settings;
        var server = prefer ?? settings.ResolveLastServer();

        var account = prefer is null
            ? settings.ResolveLastAccount(server)
            : prefer.FindAccount(settings.LastAccountId) ?? prefer.Accounts.FirstOrDefault();

        Address = server?.Url ?? "";
        Username = account?.Username ?? "";
        Remember = account?.RememberPassword ?? true;
        Password = ReadSavedPassword(account);

        FillSavedServers();
        RequestFocus(SignInFocus.Next);
    }

    /// <summary>Stops whatever this card has in flight; the shell calls it when it hides the card.</summary>
    internal void Detach() => Cancel();

    /// <summary>
    /// The face of an automatic sign-in: neither step is shown, because there is nothing to type yet.
    /// Busy without a load behind it, so the self-check waits for the restore the shell is running.
    /// </summary>
    internal void ShowRestoring(string serverName)
    {
        ClearNotice();
        Step = SignInStep.Restoring;
        Caption = serverName.Length > 0 ? $"正在连接 {serverName}…" : "正在连接…";
        Busy = true;
    }

    /// <summary>
    /// Which box the user has to fill in next, answered here because it is a question about state rather
    /// than about elements: the address step wants the address, and the credential step wants whichever
    /// of the two boxes is still empty.
    /// </summary>
    internal SignInFocus NextFocus() => Step == SignInStep.Address
        ? SignInFocus.Address
        : Username.Trim().Length == 0
            ? SignInFocus.Username
            : SignInFocus.Password;

    /// <summary>
    /// A server picked out of the discovery list. Not a command: <c>ListView.ItemClick</c> has no command
    /// surface in plain WinUI 3, and this project deliberately carries no toolkit behaviours to add one.
    /// </summary>
    internal void PickDiscovered(EmbyDiscoveredServer server)
    {
        Address = server.Url;
        ShowDiscovered = false;
        Caption = server.VersionLabel.Length > 0
            ? $"已找到 {server.Name}（{server.VersionLabel}）"
            : $"已找到 {server.Name}";

        // A discovered endpoint has already answered the Emby probe, so carrying on to the normal
        // public-info/user-list step is the least surprising next action.
        _ = ConnectAsync();
    }

    /// <summary>A server picked out of the 已保存的服务器 menu.</summary>
    [RelayCommand]
    private void UseSaved(ServerProfile server)
    {
        Address = server.Url;

        var account = server.FindAccount(Settings.LastAccountId) ?? server.Accounts.FirstOrDefault();
        Username = account?.Username ?? "";
        Remember = account?.RememberPassword ?? true;
        Password = ReadSavedPassword(account);

        // Picking a server is a statement of intent; no reason to make the user press 连接 as well.
        _ = ConnectAsync();
    }

    /// <summary>Back to step one. Not gated on <see cref="CanRun"/>: it is how a stuck connect is abandoned.</summary>
    [RelayCommand]
    private void BackToAddress()
    {
        Cancel();
        ClearNotice();
        ShowAddressStep();
        RequestFocus(SignInFocus.Next);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ConnectAsync()
    {
        if (!Attached || Busy) return;

        if (!EmbyServerAddress.TryNormalize(Address, out var apiBase, out var complaint))
        {
            Fail(complaint);
            RequestFocus(SignInFocus.Address);
            return;
        }

        var token = BeginLoad();
        Caption = "正在连接…";

        try
        {
            var gateway = _session!.Gateway;
            var info = await gateway.GetPublicSystemInfoAsync(apiBase, token).ConfigureAwait(true);

            // A server that will not list its users is still a server worth signing into by hand, so
            // this failure is swallowed rather than allowed to end the connect.
            var users = await ListUsersAsync(gateway, apiBase, token).ConfigureAwait(true);

            if (!IsCurrent(token)) return;

            ServerLabel = info.ServerName is { Length: > 0 } name
                ? $"{name}　·　Emby {info.Version}"
                : apiBase.Host;

            ShowCredentialStep(users);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, "连接服务器失败", error);
            if (IsCurrent(token)) Fail(Failure.Describe(error), "连接失败");
        }
        finally
        {
            EndLoad(token);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DiscoverAsync()
    {
        if (!Attached || Busy) return;

        var token = BeginLoad();
        Caption = "正在搜索局域网服务器…";
        ShowDiscovered = false;
        Discovered.Clear();

        try
        {
            var discovered = await new EmbyServerDiscovery()
                .DiscoverAsync(cancellationToken: token)
                .ConfigureAwait(true);

            if (!IsCurrent(token)) return;

            foreach (var server in discovered) Discovered.Add(server);
            ShowDiscovered = discovered.Count > 0;

            Notify(
                discovered.Count > 0 ? "发现局域网服务器" : "未发现服务器",
                discovered.Count > 0
                    ? $"找到 {discovered.Count} 台 Emby 服务器，请选择要连接的服务器。"
                    : "当前网络没有响应 Emby 发现请求。你仍可以手动输入服务器地址。",
                discovered.Count > 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);

            Caption = FirstCaption;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, "搜索局域网服务器失败", error);
            if (IsCurrent(token)) Fail("搜索局域网服务器失败：" + error.Message);
        }
        finally
        {
            EndLoad(token);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SignInAsync()
    {
        if (!Attached || Busy) return;

        if (!EmbyServerAddress.TryNormalize(Address, out _, out var complaint))
        {
            Fail(complaint);
            return;
        }

        var username = Username.Trim();
        if (username.Length == 0)
        {
            Fail("请填写用户名");
            RequestFocus(SignInFocus.Username);
            return;
        }

        var token = BeginLoad();
        Caption = "正在登录…";

        try
        {
            var server = _settings!.ResolveServer(Address);
            var account = _settings.ResolveAccount(server, username);

            await _session!
                .SignInAsync(server, account, Password, username, Remember, token)
                .ConfigureAwait(true);

            if (!IsCurrent(token)) return;

            _settings.Save();
            Caption = FirstCaption;
            Password = "";

            SignedIn?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, "登录失败", error);

            if (IsCurrent(token))
            {
                Fail(Failure.Describe(error), "登录失败");
                RequestFocus(SignInFocus.RejectedPassword);
            }
        }
        finally
        {
            EndLoad(token);
        }
    }

    /// <summary>
    /// A user picked out of the list. The pre-selection made by <see cref="ShowCredentialStep"/> comes
    /// through here too, and must not count — see <see cref="_filling"/>. Null is normal: a realised list
    /// writes one back the moment <see cref="Users"/> is cleared.
    /// </summary>
    partial void OnSelectedUserChanged(EmbyUser? value)
    {
        if (_filling || value is null) return;

        Username = value.Name;

        // The server settings remember, not the one currently typed in the box. Deliberately: this is a
        // password lookup, and the vault only has one for an account that was signed into before.
        var saved = _settings?.Settings.ResolveLastServer() is { } server
            ? server.Accounts.FirstOrDefault(candidate =>
                string.Equals(candidate.Username, value.Name, StringComparison.OrdinalIgnoreCase))
            : null;

        Password = ReadSavedPassword(saved);
        Remember = saved?.RememberPassword ?? true;

        if (!value.HasPassword)
        {
            // A passwordless account has nothing left to ask for.
            _ = SignInAsync();
            return;
        }

        RequestFocus(SignInFocus.Password);
    }

    /// <summary>Re-asks the three gated commands, and relabels the sign-in button.</summary>
    protected override void BusyChanged()
    {
        OnPropertyChanged(nameof(SignInText));

        ConnectCommand.NotifyCanExecuteChanged();
        DiscoverCommand.NotifyCanExecuteChanged();
        SignInCommand.NotifyCanExecuteChanged();
    }

    public override void Dispose()
    {
        Discovered.CollectionChanged -= OnDiscoveredChanged;
        Users.CollectionChanged -= OnUsersChanged;
        SavedServers.CollectionChanged -= OnSavedChanged;

        base.Dispose();
    }

    private void ShowAddressStep()
    {
        Step = SignInStep.Address;
        Caption = FirstCaption;
        ShowDiscovered = false;
    }

    private void ShowCredentialStep(List<EmbyUser> users)
    {
        Step = SignInStep.Credential;
        Caption = "选择用户并输入密码";

        _filling = true;
        try
        {
            Users.Clear();
            foreach (var user in users) Users.Add(user);

            SelectedUser = users.FirstOrDefault(user =>
                string.Equals(user.Name, Username.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _filling = false;
        }

        RequestFocus(SignInFocus.Next);
    }

    private async Task<List<EmbyUser>> ListUsersAsync(
        EmbyServerGateway gateway,
        Uri apiBase,
        CancellationToken cancellationToken)
    {
        try
        {
            return await gateway.GetPublicUsersAsync(apiBase, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            Log.Info(Category, $"服务器未公开用户列表：{error.Message}");
            return [];
        }
    }

    private void FillSavedServers()
    {
        SavedServers.Clear();
        foreach (var server in Settings.Servers) SavedServers.Add(server);
    }

    private string ReadSavedPassword(AccountProfile? account) =>
        account?.HasSavedPassword == true ? _vault!.GetPassword(account) : "";

    /// <summary>
    /// One failure in front of the user, and the caption back to normal — a card that still says
    /// 正在登录… above a red bar is telling the user two different things.
    /// </summary>
    /// <param name="title">
    /// Left null where the message is already a whole sentence to the user (「请填写用户名」), so the bar
    /// reads the same as it always has; given one where the message is a server's own words.
    /// </param>
    private void Fail(string message, string? title = null)
    {
        Notify(title, message, InfoBarSeverity.Error);
        Caption = FirstCaption;
    }

    private void RequestFocus(SignInFocus what) =>
        FocusRequested?.Invoke(what == SignInFocus.Next ? NextFocus() : what);

    private void OnDiscoveredChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(DiscoveredVisibility));

    private void OnUsersChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(UsersVisibility));

    private void OnSavedChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(SavedVisibility));
}
