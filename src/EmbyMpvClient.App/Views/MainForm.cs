using EmbyMpvClient.App.Composition;
using EmbyMpvClient.App.Controls;
using EmbyMpvClient.App.Theme;
using EmbyMpvClient.Configuration;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Emby;
using EmbyMpvClient.Mpv;
using EmbyMpvClient.Playback;

namespace EmbyMpvClient.App.Views;

/// <summary>
/// The window. Owns the navigation rail, the page stack and the two pieces of chrome that have to
/// outlive any single page — the playback bar and the toast.
/// <para>
/// It is also the only <see cref="IShell"/> implementation, so every cross-page action (open an
/// item, start a playback, sign out) funnels through here instead of one form calling into
/// another, which is what made v1's navigation impossible to follow.
/// </para>
/// </summary>
public sealed class MainForm : Form, IShell, IMessageFilter
{
    private const string Category = "shell";
    private const int WmMouseWheel = 0x020A;

    private static readonly NavigationItem[] MainPages =
    [
        new("home", Glyphs.Home, "主页"),
        new("library", Glyphs.Library, "媒体库"),
        new("search", Glyphs.Search, "搜索")
    ];

    private static readonly NavigationItem[] FooterPages =
    [
        new("settings", Glyphs.Settings, "设置"),
        new("log", Glyphs.Log, "运行日志"),
        new("signout", Glyphs.SignOut, "退出登录")
    ];

    private readonly AppHost _host;
    private readonly RingBufferLogSink _logBuffer;

    private readonly NavigationRail _rail = new();
    private readonly Panel _header = new() { BackColor = Palette.Window };
    private readonly FlatButton _back = new() { Variant = ButtonVariant.Ghost, Glyph = Glyphs.Back, Visible = false };
    private readonly FlatButton _refresh = new() { Variant = ButtonVariant.Ghost, Glyph = Glyphs.Refresh };
    private readonly TextBlock _title = new("", Fonts.Title, Palette.Text);
    private readonly Panel _content = new() { BackColor = Palette.Window };
    private readonly VideoSurface _video = new();
    private readonly PlayerBar _player;
    private readonly PlaybackTitleBar _playbackTitle = new();
    private readonly Toast _toast = new();
    private readonly LoadingOverlay _overlay = new();

    private readonly Dictionary<string, AppView> _pages = new(StringComparer.Ordinal);
    private readonly List<Route> _history = [];
    private CancellationTokenSource? _lifetime = new();
    private readonly bool _selfCheck;

    private LoginView? _login;
    private AppView? _active;
    private Route _route = new("home");
    private string _railKey = "home";
    private int _busy;
    private bool _shellReady;

    // The context of the playback the bar is currently reporting on: what to switch to from the
    // 「选集」 picker, and which shader group the startup decision picked, so 「自动」 can get back to it.
    private IReadOnlyList<EmbyItem> _playingEpisodes = [];
    private EmbyItem? _playingParent;
    private ShaderProfile? _shaderStartup;
    private string? _playingItemId;
    private bool _fullscreen;
    private FormWindowState _savedWindowState;
    private Rectangle _savedBounds;

    /// <param name="selfCheck">
    /// Builds the window without letting it do anything: <c>--self-check</c> shows it off-screen only
    /// to give the pages real handles and a real size, and must not sign in or talk to the server.
    /// </param>
    public MainForm(AppHost host, RingBufferLogSink logBuffer, bool selfCheck = false)
    {
        _host = host;
        _logBuffer = logBuffer;
        _selfCheck = selfCheck;
        _player = new PlayerBar(host.Images);

        Text = AppInfo.TitleWithVersion;
        BackColor = Palette.Window;
        ForeColor = Palette.Text;
        Font = Fonts.Body;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        RestoreWindowBounds();
        Win11.Apply(this);

        // The embedded player fetches this handle from a worker thread the moment playback
        // starts; creating it there would bind the control to that thread, so it is created
        // here, on the UI thread, up front.
        _ = _video.Handle;

        _rail.Selected += GoTo;
        _rail.Visible = false;
        _rail.SetItems(MainPages, FooterPages);

        _header.Visible = false;
        _header.Paint += PaintHeaderSeparator;

        _back.Click += (_, _) => GoBack();
        _refresh.Click += (_, _) => RefreshActive();
        _player.PauseRequested += () => _ = _host.Playback.CommandAsync("cycle", "pause");
        _player.SeekRelativeRequested += seconds => _ = _host.Playback.CommandAsync(
            "seek", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "relative+exact");
        _player.SeekRequested += seconds => _ = _host.Playback.CommandAsync(
            "seek", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute+exact");
        _player.MuteRequested += () => _ = _host.Playback.CommandAsync("cycle", "mute");
        _player.VolumeRequested += value => _ = _host.Playback.SetPropertyAsync("volume", value);
        _player.StopRequested += () => _ = StopPlaybackAsync();
        _player.FullscreenRequested += ToggleFullscreen;
        _player.StatsRequested += () => _ = _host.Playback.CommandAsync("script-binding", "stats/display-stats");
        _player.EpisodeRequested += episode => _ = SwitchEpisodeAsync(episode);
        _player.AudioTrackSelected += id => _ = _host.Playback.SetPropertyAsync("aid", id);
        _player.SubtitleTrackSelected += id => _ = _host.Playback.SetPropertyAsync("sid", id is null ? "no" : id);
        _player.ShaderRequested += profile => _ = _host.Playback.SetShaderGroupAsync(profile?.Shaders ?? _shaderStartup?.Shaders);

        // A panel of the bar is its own window, so the cursor leaves the video while one is open:
        // without this the proximity poll would fade the bar out from under the open panel.
        _player.PopupVisibilityChanged += open => _video.HoldBottomBar = open;

        _video.DoubleClicked += ToggleFullscreen;
        _video.ExitRequested += () => _ = StopPlaybackAsync();

        // The bottom bar and the top title follow the cursor's proximity to the video's
        // bottom and top edges: the closer the pointer, the more completely they show.
        _video.RevealChanged += ApplyReveal;

        _header.Controls.Add(_back);
        _header.Controls.Add(_title);
        _header.Controls.Add(_refresh);

        // Added front to back: the overlay and the toast have to cover the pages. The video
        // surface sits between the pages and the chrome so mpv can draw into the content area.
        // The playback bar is not added here: it is a top-level window owned by the shell so
        // its Opacity (a true layered alpha) can blend with the video instead of the form's
        // background; LayoutShell keeps it pinned over the video. Same for the title bar next
        // to the exit button.
        _player.Owner = this;
        _playbackTitle.Owner = this;
        Move += (_, _) => LayoutShell();
        SizeChanged += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                // Hiding the bar takes its panels with it: they are separate windows, but the
                // bar dismisses them whenever it stops being visible.
                _player.Hide();
                _playbackTitle.Hide();
            }
        };

        Controls.Add(_toast);
        Controls.Add(_overlay);
        Controls.Add(_header);
        Controls.Add(_rail);
        Controls.Add(_video);
        Controls.Add(_content);

        _host.EmbeddedWindow = () => _video.Handle;
        _host.Session.SignedOut += OnSignedOut;
        _host.Playback.ProgressChanged += OnProgressChanged;
        _host.Playback.NowPlayingChanged += OnNowPlayingChanged;
        _host.Playback.StatusChanged += OnPlayerStatusChanged;
        _host.Playback.TracksChanged += OnTracksChanged;

        Application.AddMessageFilter(this);
    }

    public AppHost Host => _host;

    /// <summary>The in-memory log, handed to the diagnostics page.</summary>
    internal RingBufferLogSink LogBuffer => _logBuffer;

    /// <summary>
    /// Every page the shell can show, for <c>--self-check</c>. 「退出登录」 is a command rather than a
    /// page, and the login page is built separately because it lives outside the navigation rail.
    /// </summary>
    internal static IReadOnlyList<string> PageKeys =>
    [
        .. MainPages.Concat(FooterPages)
            .Select(item => item.Key)
            .Where(key => !string.Equals(key, "signout", StringComparison.Ordinal)),
        "detail"
    ];

    /// <summary>Builds (or returns) a page without navigating to it. For <c>--self-check</c> only.</summary>
    internal AppView BuildPage(string key) => Page(key);

    /// <summary>Builds the login page without starting a sign-in. For <c>--self-check</c> only.</summary>
    internal AppView BuildLogin() => ShowLogin();

    /// <summary>
    /// Turns on the rail and the header without navigating anywhere, so <c>--self-check</c> can render
    /// a page inside the chrome it will really be shown in. Signed-out, so the rail shows placeholders.
    /// </summary>
    internal void ShowChromeForSelfCheck(string title)
    {
        _shellReady = true;
        _rail.SetAccount(_host.Session.Server?.Name ?? AppInfo.Title, _host.Session.Account?.Username ?? "未登录");
        _rail.Visible = true;
        _header.Visible = true;
        _title.Text = title;
        LayoutShell();
    }

    /// <summary>
    /// Docks the transport bar at the bottom of the window and hands it over so the caller can fill
    /// it with a sample item. For <c>--self-check</c> only. The bar has to be shown rather than just
    /// laid out: it is a window of its own, and a window that was never shown has no handles for its
    /// buttons, so it would paint as an empty scrim.
    /// </summary>
    internal PlayerBar ShowPlayerBarForSelfCheck()
    {
        _player.Visible = true;
        LayoutShell();
        return _player;
    }

    // ---- startup ----------------------------------------------------------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        MinimumSize = new Size(Dpi.Scale(this, 940), Dpi.Scale(this, 620));
        LayoutShell();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_selfCheck) return;
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        var login = ShowLogin();

        // Surfaced once at startup rather than at the first play attempt: a wrong mpv path is
        // the single most common reason this client does nothing, and the fix is in Settings.
        if (_host.Playback.Validate() is { } problem) Notify(problem, ToastKind.Warning);

        if (await login.TryRestoreAsync().ConfigureAwait(true)) EnterShell();
        else await RunAsync(login.EnterAsync, "打开登录页失败").ConfigureAwait(true);
    }

    private LoginView ShowLogin()
    {
        _shellReady = false;
        _rail.Visible = false;
        _header.Visible = false;
        _player.End();

        if (_login is null)
        {
            _login = new LoginView(this);
            _login.SignedIn += EnterShell;
            _content.Controls.Add(_login);
        }

        ShowPage(_login);
        LayoutShell();
        return _login;
    }

    private void EnterShell()
    {
        _shellReady = true;
        _history.Clear();
        _rail.SetAccount(_host.Session.Server?.Name ?? AppInfo.Title, _host.Session.Account?.Username ?? "");
        _rail.Visible = true;
        _header.Visible = true;
        LayoutShell();
        Navigate(new Route("home"), push: false);
    }

    // ---- navigation -------------------------------------------------------------

    /// <summary>Where the shell is; the payload is what the page needs to show it.</summary>
    private sealed record Route(string Key, EmbyItem? Item = null, string? Term = null);

    public void GoTo(string key)
    {
        if (string.Equals(key, "signout", StringComparison.Ordinal))
        {
            ConfirmSignOut();
            return;
        }

        Navigate(new Route(key));
    }

    public void Open(EmbyItem item)
    {
        if (item.Type is EmbyItemType.CollectionFolder or EmbyItemType.Folder or EmbyItemType.BoxSet or EmbyItemType.Playlist)
        {
            OpenLibrary(item);
            return;
        }

        Navigate(new Route("detail", item));
    }

    public void OpenLibrary(EmbyItem? library) => Navigate(new Route("library", library));

    public void OpenSearch(string term) => Navigate(new Route("search", Term: term));

    public void GoBack()
    {
        // During playback the back arrow means "leave the player": the video surface sits on
        // top of every page, so navigating alone would just hide the destination behind it.
        if (_host.Playback.IsPlaying)
        {
            _ = StopPlaybackAsync();
            return;
        }

        if (_history.Count == 0) return;
        var route = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        Navigate(route, push: false);
    }

    private void Navigate(Route route, bool push = true)
    {
        if (!_shellReady) return;

        if (push && !SameRoute(_route, route)) _history.Add(_route);

        var page = Page(route.Key);
        Bind(page, route);
        _route = route;

        if (MainPages.Concat(FooterPages).Any(item => string.Equals(item.Key, route.Key, StringComparison.Ordinal)))
            _railKey = route.Key;

        SuspendLayout();
        ShowPage(page);
        _rail.SelectedKey = _railKey;
        _title.Text = page.HeaderTitle;
        _back.Visible = _history.Count > 0;
        LayoutShell();
        ResumeLayout(true);

        _ = EnterPageAsync(page);
    }

    private static bool SameRoute(Route left, Route right) =>
        string.Equals(left.Key, right.Key, StringComparison.Ordinal)
        && string.Equals(left.Item?.Id, right.Item?.Id, StringComparison.Ordinal)
        && string.Equals(left.Term, right.Term, StringComparison.Ordinal);

    private static void Bind(AppView page, Route route)
    {
        switch (page)
        {
            case LibraryView library:
                library.Target = route.Item;
                break;
            case DetailView detail when route.Item is not null:
                detail.Target = route.Item;
                break;
            case SearchView search when route.Term is not null:
                search.Term = route.Term;
                break;
        }
    }

    private AppView Page(string key)
    {
        if (_pages.TryGetValue(key, out var existing)) return existing;

        AppView page = key switch
        {
            "library" => new LibraryView(this),
            "search" => new SearchView(this),
            "detail" => new DetailView(this),
            "settings" => new SettingsView(this),
            "log" => new LogView(this, _logBuffer),
            _ => new HomeView(this)
        };

        page.Visible = false;
        _pages[key] = page;
        _content.Controls.Add(page);
        return page;
    }

    /// <summary>
    /// Only one page is visible at a time. Order matters: two visible <c>Dock.Fill</c> siblings
    /// would fight over the same rectangle and the second one would end up with nothing.
    /// </summary>
    private void ShowPage(AppView page)
    {
        if (!ReferenceEquals(_active, page))
        {
            _active?.Leave();
            if (_active is not null) _active.Visible = false;
            _active = page;
        }

        page.Visible = true;
        page.BringToFront();
        if (_video.Visible) _video.BringToFront();
        _overlay.BringToFront();
        _toast.BringToFront();
    }

    private async Task EnterPageAsync(AppView page)
    {
        await RunAsync(page.EnterAsync, "打开页面失败").ConfigureAwait(true);

        // Pages that learn their title while loading (a detail page, a search) update it here.
        if (!IsDisposed && ReferenceEquals(_active, page)) _title.Text = page.HeaderTitle;
    }

    private void RefreshActive()
    {
        if (_active is not { } page) return;
        _ = RunAsync(page.RefreshAsync, "刷新失败");
    }

    // ---- shell services ---------------------------------------------------------

    public void Notify(string message, ToastKind kind = ToastKind.Info)
    {
        if (IsDisposed) return;
        _toast.Show(message, kind, kind == ToastKind.Error ? 9 : 5);
        PositionToast();
    }

    public void Busy(string message = "正在加载…")
    {
        _busy++;
        _overlay.SetBounds(_content.Left, _content.Top, _content.Width, _content.Height);
        _overlay.Begin(message);
    }

    public void Idle()
    {
        _busy = Math.Max(0, _busy - 1);
        if (_busy == 0) _overlay.End();
    }

    public void SignOut()
    {
        _ = StopPlaybackAsync();
        _host.Session.SignOut();
    }

    private void ConfirmSignOut()
    {
        var answer = MessageBox.Show(
            this,
            $"确定要退出 {_host.Session.Account?.Username} 的登录吗？保存的密码不会被删除。",
            "退出登录",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (answer == DialogResult.Yes) SignOut();
    }

    private void OnSignedOut(object? sender, string reason) => OnUi(() =>
    {
        DisposePages();
        var login = ShowLogin();
        _ = RunAsync(login.EnterAsync, "打开登录页失败");
        Notify(reason, ToastKind.Warning);
    });

    private void DisposePages()
    {
        foreach (var page in _pages.Values)
        {
            _content.Controls.Remove(page);
            page.Dispose();
        }

        _pages.Clear();
        _history.Clear();
        _active = null;
        _railKey = "home";
        _shellReady = false;
    }

    // ---- playback ---------------------------------------------------------------

    public Task PlayAsync(EmbyItem item, EmbyItem? parent = null, PlaybackChoice? choice = null, IReadOnlyList<EmbyItem>? episodes = null) =>
        RunAsync(() => StartPlaybackAsync(item, parent, choice, episodes), "播放失败");

    private async Task StartPlaybackAsync(EmbyItem item, EmbyItem? parent, PlaybackChoice? choice, IReadOnlyList<EmbyItem>? episodes = null, bool replaceExisting = false)
    {
        if (!replaceExisting && _host.Playback.IsPlaying)
        {
            Notify("已经有内容正在播放，请先停止", ToastKind.Warning);
            return;
        }

        if (_host.Playback.Validate() is { } problem)
        {
            Notify(problem, ToastKind.Error);
            return;
        }

        EmbyItem detail;
        Busy("正在获取媒体信息…");
        try
        {
            // A card fetched for browsing carries no MediaSources, and those are what hold the
            // tracks, the container and the runtime.
            detail = item.MediaSources.Count > 0
                ? item
                : await _host.Session
                    .ExecuteAsync((client, token) => client.GetItemAsync(item.Id, token), _lifetime!.Token)
                    .ConfigureAwait(true);
        }
        finally
        {
            Idle();
        }

        if (!detail.IsPlayable)
        {
            Notify("这个项目不能直接播放", ToastKind.Warning);
            return;
        }

        if (detail.MediaSources.Count == 0)
        {
            Notify("服务器没有返回可用的媒体源", ToastKind.Error);
            return;
        }

        // A choice carries everything the user picked on the detail page; without one the
        // track pickers stay untouched, so mpv's own --alang/--slang priorities decide.
        var source = choice?.Source ?? detail.MediaSources[0];

        var resumeTicks = _host.Settings.Playback.ResumeFromSavedPosition ? detail.ResumeTicks : 0;

        _playingEpisodes = episodes ?? [];
        _playingParent = parent;
        _playingItemId = detail.Id;

        var ticket = new PlaybackTicket
        {
            Item = detail,
            Source = source,
            AudioStreamIndex = choice?.AudioStreamIndex,
            SubtitleStreamIndex = choice?.SubtitleStreamIndex,
            SubtitlesDisabled = choice?.SubtitlesDisabled ?? false,
            StartTicks = choice?.StartTicks ?? resumeTicks,
            Parent = parent ?? await ResolveSeriesAsync(detail).ConfigureAwait(true)
        };

        // What the planner picked decides the 「着色器 自动」 entry in the bar, so switching away
        // and back reproduces the startup decision instead of clearing the shaders.
        var shaderDecision = _host.Shaders.Resolve(detail, source, ticket.Parent);
        _shaderStartup = shaderDecision.HasProfile
            ? _host.ShaderCatalog.FirstOrDefault(profile => string.Equals(profile.Name, shaderDecision.Profile, StringComparison.OrdinalIgnoreCase))
            : null;

        var result = await _host.Playback.PlayAsync(ticket, _lifetime!.Token).ConfigureAwait(true);
        Notify(result.ToChinese(), result.Exit.IsFailure ? ToastKind.Error : ToastKind.Success);

        // The item's watched state and resume position have just changed on the server.
        RefreshActive();
    }

    /// <summary>
    /// Fetches the series row behind an episode. Emby's episode records usually carry no genres
    /// of their own, so without this the anime shader rule would never fire on a TV show.
    /// </summary>
    private async Task<EmbyItem?> ResolveSeriesAsync(EmbyItem item)
    {
        if (item.Type != EmbyItemType.Episode || string.IsNullOrEmpty(item.SeriesId)) return null;

        try
        {
            return await _host.Session
                .ExecuteAsync((client, token) => client.GetItemAsync(item.SeriesId!, token, EmbyFields.Browse), _lifetime!.Token)
                .ConfigureAwait(true);
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"读取所属剧集信息失败：{error.Message}");
            return null;
        }
    }

    private async Task StopPlaybackAsync()
    {
        // Asks mpv to quit rather than cancelling the playback task: a graceful quit still
        // reports the final position to the server.
        if (!_host.Playback.IsPlaying) return;
        await _host.Playback.StopAsync().ConfigureAwait(true);
    }

    private void OnProgressChanged(PlaybackProgress progress) => OnUi(() =>
    {
        _player.Update(progress);
        if (progress.RunTimeTicks > 0) _video.SetStripFraction(progress.PositionTicks / (double)progress.RunTimeTicks);
        if (progress.Title.Length > 0) _playbackTitle.SetTitle(progress.Title);
    });

    private void OnPlayerStatusChanged(PlayerStatus status) => OnUi(() =>
    {
        _player.Update(status);
        _video.SetStripFraction(status.Fraction);
    });

    private void OnTracksChanged(IReadOnlyList<MpvTrack> tracks) => OnUi(() => _player.SetTracks(tracks));

    private void OnNowPlayingChanged(EmbyItem? item) => OnUi(() =>
    {
        if (item is null)
        {
            _player.End();
            HideVideo();
        }
        else
        {
            _playbackTitle.SetTitle(item.ToPlaybackTitle());
            _player.Begin(item, _playingEpisodes, _host.ShaderCatalog, _shaderStartup);
            if (_host.Settings.Mpv.Backend == MpvBackendKind.BuiltInLibMpv) ShowVideo();
            _ = PopulateTracksAsync();
        }

        Text = item is null ? AppInfo.TitleWithVersion : $"{item.ToPlaybackTitle()} — {AppInfo.Title}";
        LayoutShell();
    });

    /// <summary>
    /// The embedded player draws into its own child window inside this panel.
    /// </summary>
    private void ShowVideo()
    {
        _rail.Visible = false;
        _video.Visible = true;
        _video.BringToFront();
        _video.InstallMpvInputHook();
        _ = RetryMpvInputHookAsync();
    }

    /// <summary>
    /// mpv creates its child window while the file loads, possibly after the first hook pass;
    /// keep retrying until it exists, so double-clicks land even on a slow disk.
    /// </summary>
    private async Task RetryMpvInputHookAsync()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(400).ConfigureAwait(true);
            if (!_host.Playback.IsPlaying) return;
            _video.InstallMpvInputHook();
            if (_video.InputHookInstalled) return;
        }
    }

    private void HideVideo()
    {
        _video.Visible = false;
        _video.Invalidate();
        _playbackTitle.Visible = false;
        _rail.Visible = _shellReady;
    }

    /// <summary>
    /// Fades the bottom bar and the top title with the cursor's proximity to the video's
    /// edges. The bar fades out completely while the pointer sits in the middle of the
    /// screen; there the thin progress line replaces it.
    /// </summary>
    private void ApplyReveal()
    {
        if (!_host.Playback.IsPlaying)
        {
            _player.Visible = false;
            _playbackTitle.Visible = false;
            return;
        }

        var bottom = _video.BottomReveal;
        var top = _video.TopReveal;

        // No special case for an open panel: PopupVisibilityChanged has already set
        // VideoSurface.HoldBottomBar, which pins BottomReveal at 1 while one is showing.
        _player.Visible = bottom > 0.1;
        _player.Opacity = 0.92 * bottom;
        _playbackTitle.Visible = top > 0.1;
        _playbackTitle.Opacity = 0.65 * top;
    }

    /// <summary>
    /// Stops the running episode and starts the picked one in its place. The bar passes the
    /// sibling list along so the picker keeps its state; reselecting the playing episode is
    /// ignored, and a second switch in quick succession is serialized by the playback service.
    /// </summary>
    private async Task SwitchEpisodeAsync(EmbyItem episode)
    {
        if (string.Equals(episode.Id, _playingItemId, StringComparison.Ordinal)) return;
        await StartPlaybackAsync(episode, _playingParent, null, _playingEpisodes, replaceExisting: true).ConfigureAwait(true);
    }

    /// <summary>
    /// mpv publishes its track list only once the file is actually being decoded, so the
    /// pickers are refilled in a loop until the list stops being empty. A stuck backend runs
    /// out of attempts and leaves the placeholders in place.
    /// </summary>
    private async Task PopulateTracksAsync()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (!_host.Playback.IsPlaying) return;
            var tracks = await _host.Playback.GetTracksAsync().ConfigureAwait(true);
            if (tracks.Count > 0)
            {
                _player.SetTracks(tracks);
                return;
            }

            await Task.Delay(500).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Fullscreen without touching <see cref="Form.FormBorderStyle"/>: changing that property
    /// makes WinForms recreate the window handle, and the embedded mpv child window lives
    /// inside it, so it would die with the old handle. The caption styles are flipped in place
    /// instead, and the window is stretched over the screen the form is on.
    /// </summary>
private void ToggleFullscreen()
{
    SuspendLayout();
    try
    {
        if (_fullscreen)
        {
            _fullscreen = false;
            WindowChrome.SetCaption(this, true);
            _header.Visible = true;
            WindowState = FormWindowState.Normal;
            Bounds = _savedBounds;
            WindowState = _savedWindowState;
        }
        else
        {
            // A maximized window's Bounds is the screen, not the restore size.
            _savedWindowState = WindowState;
            _savedBounds = WindowState == FormWindowState.Maximized ? RestoreBounds : Bounds;
            _fullscreen = true;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            WindowChrome.SetCaption(this, false);
            _header.Visible = false;
        }

        _player.SetFullscreen(_fullscreen);
    }
    finally
    {
        ResumeLayout(performLayout: true);
    }

    LayoutShell();

    // mpv may recreate its child window while the transition settles; keep the input hook
    // in place until it stops moving.
    _ = RaiseVideoControlsAfterFullscreenAsync();
    }

    private async Task RaiseVideoControlsAfterFullscreenAsync()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(150).ConfigureAwait(true);
            if (!_host.Playback.IsPlaying) return;
            _video.InstallMpvInputHook();
        }
    }

    // ---- plumbing ---------------------------------------------------------------

    /// <summary>Brought forward by a second launch instead of starting a second client.</summary>
    public void ActivateFromSecondInstance()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        BringToFront();
        Activate();
        Notify("客户端已经在运行中", ToastKind.Info);
    }

    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;

        if (!InvokeRequired)
        {
            action();
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The window went away between the check and the post.
        }
    }

    private async Task RunAsync(Func<Task> work, string failureMessage)
    {
        try
        {
            await work().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, failureMessage, error);
            Notify($"{failureMessage}：{AppView.Describe(error)}", ToastKind.Error);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // The bar's panels are WS_EX_NOACTIVATE windows and never get the keyboard, so the shell
        // drives them while one is open. First in line: Up/Down have to move the highlighted row
        // instead of changing the volume, and Escape has to close the panel instead of leaving
        // fullscreen.
        if (_player.HandleKey(keyData)) return true;

        if (_video.Visible && _host.Playback.CanControl && ActiveControl is not TextBoxBase && ActiveControl is not ComboBox)
        {
            var playerCommandHandled = true;
            switch (keyData)
            {
                case Keys.Space:
                    _ = _host.Playback.CommandAsync("cycle", "pause");
                    break;
                case Keys.Left:
                    _ = _host.Playback.CommandAsync("seek", "-10", "relative+exact");
                    break;
                case Keys.Right:
                    _ = _host.Playback.CommandAsync("seek", "10", "relative+exact");
                    break;
                case Keys.Up:
                    _ = _host.Playback.SetPropertyAsync("volume", Math.Clamp(_host.Playback.Status.Volume + 5, 0, 100));
                    break;
                case Keys.Down:
                    _ = _host.Playback.SetPropertyAsync("volume", Math.Clamp(_host.Playback.Status.Volume - 5, 0, 100));
                    break;
                case Keys.F:
                    ToggleFullscreen();
                    break;
                default:
                    playerCommandHandled = false;
                    break;
            }

            if (playerCommandHandled)
            {
                _video.ShowBottomBarTemporarily();
                return true;
            }
        }

        switch (keyData)
        {
            case Keys.F5:
                RefreshActive();
                return true;
            case Keys.Control | Keys.F:
                if (_shellReady) GoTo("search");
                return true;
            case Keys.Alt | Keys.Left:
                GoBack();
                return true;

            // Escape is left alone while a text field has focus: in the config editor it would
            // otherwise navigate away in the middle of typing.
            case Keys.Escape when ActiveControl is not TextBoxBase && _fullscreen:
                ToggleFullscreen();
                return true;
            case Keys.Escape when _history.Count > 0 && ActiveControl is not TextBoxBase:
                GoBack();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Feeds the wheel to an open picker. Windows sends WM_MOUSEWHEEL to the focused window, and
    /// the panels never take focus, so the message arrives here no matter where the cursor is.
    /// Filtered at the application level rather than through <c>OnMouseWheel</c> because a page
    /// that scrolls would otherwise swallow it before the form ever sees it.
    /// </summary>
    bool IMessageFilter.PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmMouseWheel) return false;
        return _player.ScrollOpenMenu(unchecked((short)(m.WParam.ToInt64() >> 16)));
    }

    // ---- layout -----------------------------------------------------------------

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutShell();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutShell();
    }

    private void LayoutShell()
    {
        if (!IsHandleCreated) return;

        var railWidth = _rail.Visible ? Dpi.Scale(this, 208) : 0;
        var headerHeight = _header.Visible ? Dpi.Scale(this, 56) : 0;
        var barHeight = _player.Visible && !_video.Visible ? Dpi.Scale(this, PlayerBar.DesignHeight) : 0;
        var width = ClientSize.Width;
        var height = ClientSize.Height;

        _rail.SetBounds(0, 0, railWidth, Math.Max(10, height - barHeight));
        _header.SetBounds(railWidth, 0, Math.Max(10, width - railWidth), headerHeight);
        _content.SetBounds(railWidth, headerHeight, Math.Max(10, width - railWidth), Math.Max(10, height - headerHeight - barHeight));
        _video.SetBounds(_content.Left, _content.Top, _content.Width, _content.Height);
        if (_playbackTitle.Visible)
        {
            var exit = Dpi.Scale(this, 40);
            // The floating exit arrow is a child of the video surface at (14, 14); the title
            // pill sits right of it so the two never overlap.
            var offset = _video.Visible ? exit + Dpi.Scale(this, 10) : 0;
            var origin = PointToScreen(new Point(_video.Left + Dpi.Scale(this, 14) + offset, _video.Top + Dpi.Scale(this, 14)));
            _playbackTitle.SetBounds(origin.X, origin.Y, _playbackTitle.Width, exit);
        }
        if (_video.Visible)
        {
            var playerBarHeight = Dpi.Scale(this, PlayerBar.DesignHeight);
            var origin = PointToScreen(new Point(_video.Left, Math.Max(_video.Top, _video.Bottom - playerBarHeight)));
            _player.SetBounds(origin.X, origin.Y, _video.Width, playerBarHeight);
        }
        else
        {
            var origin = PointToScreen(new Point(0, height - barHeight));
            _player.SetBounds(origin.X, origin.Y, width, barHeight);
        }
        _player.BringToFront();

        if (_overlay.Visible) _overlay.SetBounds(_content.Left, _content.Top, _content.Width, _content.Height);

        LayoutHeader();
        PositionToast();
    }

    private void LayoutHeader()
    {
        if (!_header.Visible) return;

        var margin = Dpi.Scale(this, 16);
        var button = Dpi.Scale(this, 32);
        var top = (_header.Height - button) / 2;
        var left = margin;

        if (_back.Visible)
        {
            _back.SetBounds(left, top, button, button);
            left = _back.Right + Dpi.Scale(this, 10);
        }

        _refresh.SetBounds(_header.Width - margin - button, top, button, button);

        var titleWidth = Math.Max(60, _refresh.Left - left - Dpi.Scale(this, 12));
        _title.SetBounds(left, (_header.Height - Fonts.Title.Height) / 2, titleWidth, Fonts.Title.Height);
    }

    private void PositionToast()
    {
        if (!_toast.Visible) return;

        var margin = Dpi.Scale(this, 16);
        var bottom = (_player.Visible ? PointToClient(new Point(0, _player.Top)).Y : ClientSize.Height) - margin;
        _toast.Location = new Point(
            Math.Max(margin, ClientSize.Width - _toast.Width - margin),
            Math.Max(margin, bottom - _toast.Height));
        _toast.BringToFront();
    }

    private void PaintHeaderSeparator(object? sender, PaintEventArgs e)
    {
        using var pen = new Pen(Palette.Border);
        e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
    }

    private void RestoreWindowBounds()
    {
        var ui = _host.Settings.Ui;
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);

        Size = new Size(
            Math.Clamp(ui.WindowWidth, 940, Math.Max(940, work.Width)),
            Math.Clamp(ui.WindowHeight, 620, Math.Max(620, work.Height)));

        if (ui.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var ui = _host.Settings.Ui;
        var maximized = _fullscreen ? _savedWindowState == FormWindowState.Maximized : WindowState == FormWindowState.Maximized;
        ui.WindowMaximized = maximized;

        var size = _fullscreen
            ? _savedBounds.Size
            : WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
        if (size.Width > 400 && size.Height > 300)
        {
            ui.WindowWidth = size.Width;
            ui.WindowHeight = size.Height;
        }

        _host.SaveSettings();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveWindowBounds();

        if (_host.Playback.IsPlaying)
        {
            // Given a moment, mpv quits cleanly and the server gets a final position report.
            // Run off the UI thread so a stuck pipe cannot deadlock the close.
            try
            {
                Task.Run(() => _host.Playback.StopAsync()).Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException error)
            {
                Log.Warn(Category, "退出时停止播放失败", error);
            }
        }

        _lifetime?.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(this);
            _host.Session.SignedOut -= OnSignedOut;
            _host.Playback.ProgressChanged -= OnProgressChanged;
            _host.Playback.NowPlayingChanged -= OnNowPlayingChanged;
            _host.Playback.StatusChanged -= OnPlayerStatusChanged;
            _host.Playback.TracksChanged -= OnTracksChanged;

            // The window is disposed twice on close — once by WinForms when the message loop
            // ends, once by the `using` in Program.Main — so dispose exactly once.
            var lifetime = Interlocked.Exchange(ref _lifetime, null);
            lifetime?.Cancel();
            lifetime?.Dispose();
        }

        base.Dispose(disposing);
    }
}
