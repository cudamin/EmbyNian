namespace EmbyMpvClient;

internal sealed class SettingsView : UserControl
{
    public SettingsView(AppSettings settings, Action profilesChanged, Action<string> setStatus)
    {
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Background;
        Padding = new Padding(24, 8, 24, 20);

        var tabs = new TabControl { Dock = DockStyle.Fill, Font = UiTheme.CreateFont(9.5F), Padding = new Point(18, 8) };
        tabs.TabPages.Add(CreatePage("服务器与账户", new ProfileSettingsView(settings, profilesChanged, setStatus)));
        tabs.TabPages.Add(CreatePage("MPV 路径", new PathSettingsView(settings, setStatus)));
        tabs.TabPages.Add(CreatePage("播放设置", new ConfigEditorView("mpv.conf", false, () => settings.MpvConfigPath, setStatus)));
        tabs.TabPages.Add(CreatePage("快捷键", new ConfigEditorView("input.conf", true, () => settings.InputConfigPath, setStatus)));
        Controls.Add(tabs);
    }

    private static TabPage CreatePage(string title, Control content)
    {
        var page = new TabPage(title) { BackColor = UiTheme.Background, ForeColor = UiTheme.Text, Padding = new Padding(4) };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }
}

internal sealed class ProfileSettingsView : UserControl
{
    private readonly AppSettings _settings;
    private readonly Action _profilesChanged;
    private readonly Action<string> _setStatus;
    private readonly ListBox _servers = new();
    private readonly ListBox _accounts = new();
    private readonly TextBox _serverName = UiTheme.CreateTextBox();
    private readonly TextBox _serverUrl = UiTheme.CreateTextBox();
    private readonly TextBox _username = UiTheme.CreateTextBox();
    private readonly TextBox _password = UiTheme.CreateTextBox();

    public ProfileSettingsView(AppSettings settings, Action profilesChanged, Action<string> setStatus)
    {
        _settings = settings;
        _profilesChanged = profilesChanged;
        _setStatus = setStatus;
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Background;
        Padding = new Padding(16);
        BuildUi();
        ReloadServers();
    }

    private void BuildUi()
    {
        StyleList(_servers);
        StyleList(_accounts);
        _servers.SelectedIndexChanged += (_, _) => SelectServer();
        _accounts.SelectedIndexChanged += (_, _) => SelectAccount();

        var serverPanel = CreateColumn("服务器", _servers, "新增服务器", AddServer, "删除", DeleteServer);
        var accountPanel = CreateColumn("账户", _accounts, "新增账户", AddAccount, "删除", DeleteAccount);
        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 2, 0, 0), ColumnCount = 2, RowCount = 6, BackColor = UiTheme.Background };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(editor, 0, "服务器名称", _serverName);
        AddField(editor, 1, "服务器地址", _serverUrl);
        AddField(editor, 2, "用户名", _username);
        _password.UseSystemPasswordChar = true;
        AddField(editor, 3, "保存的密码", _password);
        var save = UiTheme.CreateButton("保存资料", true);
        save.Click += (_, _) => SaveProfile();
        editor.Controls.Add(save, 1, 4);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = UiTheme.Background };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(serverPanel, 0, 0);
        layout.Controls.Add(accountPanel, 1, 0);
        layout.Controls.Add(editor, 2, 0);
        Controls.Add(layout);
    }

    private static Panel CreateColumn(string title, Control list, string addText, Action add, string deleteText, Action delete)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 12, 0) };
        var caption = UiTheme.CreateLabel(title, 11F, true);
        caption.Dock = DockStyle.Top;
        caption.Height = 38;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, WrapContents = false };
        var addButton = UiTheme.CreateButton(addText, true);
        var deleteButton = UiTheme.CreateButton(deleteText);
        addButton.Click += (_, _) => add();
        deleteButton.Click += (_, _) => delete();
        bar.Controls.AddRange([addButton, deleteButton]);
        list.Dock = DockStyle.Fill;
        panel.Controls.Add(list);
        panel.Controls.Add(bar);
        panel.Controls.Add(caption);
        return panel;
    }

    private void AddServer()
    {
        var server = new ServerProfile { Name = "新服务器", Url = "http://localhost:8096" };
        _settings.Servers.Add(server);
        ReloadServers();
        _servers.SelectedItem = server;
    }

    private void DeleteServer()
    {
        if (_servers.SelectedItem is not ServerProfile server) return;
        if (MessageBox.Show(this, $"确定删除服务器“{server.Name}”及其全部账户吗？", "删除服务器", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _settings.Servers.Remove(server);
        EnsureAtLeastOneServer();
        Persist();
        ReloadServers();
    }

    private void AddAccount()
    {
        if (_servers.SelectedItem is not ServerProfile server) return;
        var account = new AccountProfile { Username = "新账户" };
        server.Accounts.Add(account);
        ReloadAccounts(server);
        _accounts.SelectedItem = account;
    }

    private void DeleteAccount()
    {
        if (_servers.SelectedItem is not ServerProfile server || _accounts.SelectedItem is not AccountProfile account) return;
        server.Accounts.Remove(account);
        Persist();
        ReloadAccounts(server);
    }

    private void SaveProfile()
    {
        if (_servers.SelectedItem is not ServerProfile server) return;
        server.Name = string.IsNullOrWhiteSpace(_serverName.Text) ? "Emby 服务器" : _serverName.Text.Trim();
        server.Url = _serverUrl.Text.Trim();
        if (_accounts.SelectedItem is AccountProfile account)
        {
            account.Username = _username.Text.Trim();
            account.ProtectedPassword = PasswordProtector.Protect(_password.Text);
        }
        _settings.LastServerId = server.Id;
        _settings.LastAccountId = (_accounts.SelectedItem as AccountProfile)?.Id;
        _settings.Activate(_settings.LastServerId, _settings.LastAccountId);
        Persist();
        ReloadServers(server.Id);
        _setStatus("服务器和账户资料已保存");
    }

    private void SelectServer()
    {
        if (_servers.SelectedItem is not ServerProfile server) return;
        _serverName.Text = server.Name;
        _serverUrl.Text = server.Url;
        ReloadAccounts(server);
    }

    private void SelectAccount()
    {
        var account = _accounts.SelectedItem as AccountProfile;
        _username.Text = account?.Username ?? "";
        _password.Text = account?.GetPassword() ?? "";
    }

    private void ReloadServers(string? selectedId = null)
    {
        _servers.Items.Clear();
        _servers.Items.AddRange(_settings.Servers.Cast<object>().ToArray());
        _servers.SelectedItem = _settings.Servers.FirstOrDefault(item => item.Id == (selectedId ?? _settings.LastServerId)) ?? _settings.Servers.FirstOrDefault();
    }

    private void ReloadAccounts(ServerProfile server)
    {
        _accounts.Items.Clear();
        _accounts.Items.AddRange(server.Accounts.Cast<object>().ToArray());
        _accounts.SelectedItem = server.Accounts.FirstOrDefault(item => item.Id == _settings.LastAccountId) ?? server.Accounts.FirstOrDefault();
    }

    private void EnsureAtLeastOneServer()
    {
        if (_settings.Servers.Count == 0) _settings.Servers.Add(new ServerProfile { Name = "我的 Emby" });
    }

    private void Persist() { _settings.Save(); _profilesChanged(); }

    private static void StyleList(ListBox list)
    {
        list.BackColor = UiTheme.Surface;
        list.ForeColor = UiTheme.Text;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.Font = UiTheme.CreateFont(10F);
        list.IntegralHeight = false;
    }

    private static void AddField(TableLayoutPanel panel, int row, string label, TextBox input)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        var caption = UiTheme.CreateLabel(label, 9.5F, true);
        caption.Anchor = AnchorStyles.Left;
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 10, 0, 10);
        panel.Controls.Add(caption, 0, row);
        panel.Controls.Add(input, 1, row);
    }
}

internal sealed class PathSettingsView : UserControl
{
    public PathSettingsView(AppSettings settings, Action<string> setStatus)
    {
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Background;
        Padding = new Padding(24);
        var form = new TableLayoutPanel { Dock = DockStyle.Top, Height = 260, ColumnCount = 3 };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        var mpv = AddPath(form, 0, "mpv.exe", settings.MpvPath, "mpv.exe|mpv.exe|可执行文件|*.exe");
        var mpvConfig = AddPath(form, 1, "mpv.conf", settings.MpvConfigPath, "配置文件|*.conf|所有文件|*.*");
        var inputConfig = AddPath(form, 2, "input.conf", settings.InputConfigPath, "配置文件|*.conf|所有文件|*.*");
        var save = UiTheme.CreateButton("保存路径", true);
        save.Click += (_, _) => { settings.MpvPath = mpv.Text.Trim(); settings.MpvConfigPath = mpvConfig.Text.Trim(); settings.InputConfigPath = inputConfig.Text.Trim(); settings.Save(); setStatus("MPV 路径已保存"); };
        form.Controls.Add(save, 1, 3);
        Controls.Add(form);
    }

    private static TextBox AddPath(TableLayoutPanel form, int row, string label, string value, string filter)
    {
        form.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        var caption = UiTheme.CreateLabel(label, 9.5F, true); caption.Anchor = AnchorStyles.Left;
        var input = UiTheme.CreateTextBox(); input.Text = value; input.Dock = DockStyle.Fill; input.Margin = new Padding(0, 10, 10, 10);
        var browse = UiTheme.CreateButton("浏览"); browse.Anchor = AnchorStyles.Left;
        browse.Click += (_, _) => { using var dialog = new OpenFileDialog { Filter = filter, FileName = input.Text }; if (dialog.ShowDialog() == DialogResult.OK) input.Text = dialog.FileName; };
        form.Controls.Add(caption, 0, row); form.Controls.Add(input, 1, row); form.Controls.Add(browse, 2, row);
        return input;
    }
}
