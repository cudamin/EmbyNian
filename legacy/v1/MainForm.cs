using System.Runtime.InteropServices;

namespace EmbyMpvClient;

internal sealed class MainForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load();
    private EmbyApiClient _api;
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Background };
    private readonly Label _pageTitle = UiTheme.CreateLabel("登录", 16F, true);
    private readonly Label _status = UiTheme.CreateLabel("请选择服务器和账户");
    private readonly Dictionary<Control, NavigationButton> _navigation = [];
    private readonly MediaLibraryView _libraryView;
    private readonly SettingsView _settingsView;
    private readonly LoginView _loginView;

    public MainForm()
    {
        _api = new EmbyApiClient(_settings);
        _libraryView = new MediaLibraryView(() => _api, _settings, SetStatus);
        _loginView = new LoginView(_settings, LoginAsync);
        _settingsView = new SettingsView(_settings, _loginView.ReloadProfiles, SetStatus);

        Text = "Emby MPV Client";
        FormBorderStyle = FormBorderStyle.None;
        MinimumSize = new Size(1040, 680);
        Size = new Size(1360, 860);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Text;
        Font = UiTheme.CreateFont(9F);
        if (File.Exists("app.ico")) Icon = new Icon("app.ico");

        BuildShell();
        Shown += (_, _) => ShowLogin();
    }

    private void BuildShell()
    {
        var titleBar = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = UiTheme.Background };
        titleBar.MouseDown += DragWindow;
        var appTitle = UiTheme.CreateLabel("Emby MPV Client", 9.5F, true);
        appTitle.Location = new Point(18, 12);
        appTitle.MouseDown += DragWindow;
        titleBar.Controls.Add(appTitle);
        titleBar.Controls.Add(CreateWindowButton("×", (_, _) => Close(), 0));
        titleBar.Controls.Add(CreateWindowButton("□", (_, _) => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized, 1));
        titleBar.Controls.Add(CreateWindowButton("−", (_, _) => WindowState = FormWindowState.Minimized, 2));

        var sidebar = new Panel { Dock = DockStyle.Left, Width = 210, BackColor = UiTheme.Surface, Padding = new Padding(12) };
        var brand = new Panel { Dock = DockStyle.Top, Height = 78 };
        brand.Controls.Add(new Label { Text = "EMBY\nMPV CLIENT", Dock = DockStyle.Fill, ForeColor = UiTheme.Text, Font = UiTheme.CreateFont(13F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0) });
        var navHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };
        var logout = new NavigationButton("退出登录");
        logout.Click += (_, _) => ShowLogin();
        navHost.Controls.Add(logout);
        AddNavigation(navHost, "设置", _settingsView, "设置");
        AddNavigation(navHost, "媒体库", _libraryView, "媒体库");
        sidebar.Controls.Add(navHost);
        sidebar.Controls.Add(brand);

        var header = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = UiTheme.Background, Padding = new Padding(28, 14, 28, 8) };
        _pageTitle.Dock = DockStyle.Left;
        header.Controls.Add(_pageTitle);
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 32, BackColor = UiTheme.Surface, Padding = new Padding(22, 7, 20, 0) };
        statusBar.Controls.Add(_status);

        Controls.Add(_content);
        Controls.Add(header);
        Controls.Add(statusBar);
        Controls.Add(sidebar);
        Controls.Add(titleBar);
    }

    private Button CreateWindowButton(string text, EventHandler click, int offset)
    {
        var button = new Button { Text = text, Dock = DockStyle.Right, Width = 46, FlatStyle = FlatStyle.Flat, BackColor = UiTheme.Background, ForeColor = UiTheme.TextMuted, Font = UiTheme.CreateFont(11F), TabStop = false };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = text == "×" ? UiTheme.Danger : UiTheme.SurfaceRaised;
        button.Click += click;
        return button;
    }

    private void AddNavigation(Control parent, string text, Control view, string title)
    {
        var button = new NavigationButton(text);
        button.Click += (_, _) => ShowView(view, title);
        parent.Controls.Add(button);
        _navigation[view] = button;
    }

    private void ShowLogin()
    {
        _loginView.ReloadProfiles();
        ShowView(_loginView, "登录");
        SetStatus("请选择服务器和账户");
    }

    private void ShowView(Control view, string title)
    {
        _content.SuspendLayout();
        _content.Controls.Clear();
        view.Dock = DockStyle.Fill;
        _content.Controls.Add(view);
        _content.ResumeLayout();
        _pageTitle.Text = title;
        foreach (var pair in _navigation) pair.Value.Selected = pair.Key == view;
    }

    private async Task LoginAsync(string password)
    {
        try
        {
            UseWaitCursor = true;
            SetStatus("正在登录 Emby...");
            _api.Dispose();
            _api = new EmbyApiClient(_settings);
            await _api.LoginAsync(password);
            _settings.UpdateActiveAccount(password);
            _settings.Save();
            await _libraryView.LoadLibrariesAsync();
            ShowView(_libraryView, "媒体库");
            SetStatus("登录成功");
        }
        catch (Exception ex) { SetStatus("登录失败"); MessageBox.Show(this, ex.Message, "登录失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { UseWaitCursor = false; }
    }

    private void SetStatus(string text) => _status.Text = text;

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 0x2, 0);
    }

    protected override void WndProc(ref Message message)
    {
        const int wmNcHitTest = 0x84;
        const int grip = 8;
        if (message.Msg == wmNcHitTest && WindowState == FormWindowState.Normal)
        {
            var point = PointToClient(new Point(message.LParam.ToInt32()));
            if (point.X <= grip && point.Y <= grip) { message.Result = (IntPtr)13; return; }
            if (point.X >= ClientSize.Width - grip && point.Y <= grip) { message.Result = (IntPtr)14; return; }
            if (point.X <= grip && point.Y >= ClientSize.Height - grip) { message.Result = (IntPtr)16; return; }
            if (point.X >= ClientSize.Width - grip && point.Y >= ClientSize.Height - grip) { message.Result = (IntPtr)17; return; }
            if (point.X <= grip) { message.Result = (IntPtr)10; return; }
            if (point.X >= ClientSize.Width - grip) { message.Result = (IntPtr)11; return; }
            if (point.Y <= grip) { message.Result = (IntPtr)12; return; }
            if (point.Y >= ClientSize.Height - grip) { message.Result = (IntPtr)15; return; }
        }
        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing) { if (disposing) _api.Dispose(); base.Dispose(disposing); }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, int wParam, int lParam);
}
