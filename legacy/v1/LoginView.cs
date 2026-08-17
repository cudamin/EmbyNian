namespace EmbyMpvClient;

internal sealed class LoginView : UserControl
{
    private readonly AppSettings _settings;
    private readonly Func<string, Task> _login;
    private readonly ComboBox _server = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _account = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _password = UiTheme.CreateTextBox("密码");

    public LoginView(AppSettings settings, Func<string, Task> login)
    {
        _settings = settings;
        _login = login;
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Background;
        BuildUi();
        ReloadProfiles();
    }

    public void ReloadProfiles()
    {
        _server.Items.Clear();
        _server.Items.AddRange(_settings.Servers.Cast<object>().ToArray());
        _server.SelectedItem = _settings.Servers.FirstOrDefault(item => item.Id == _settings.LastServerId) ?? _settings.Servers.FirstOrDefault();
    }

    private void BuildUi()
    {
        var card = new Panel { Size = new Size(440, 460), BackColor = UiTheme.Surface, Anchor = AnchorStyles.None };
        card.Location = new Point((Width - card.Width) / 2, (Height - card.Height) / 2);
        Resize += (_, _) => card.Location = new Point((Width - card.Width) / 2, Math.Max(20, (Height - card.Height) / 2));

        var mark = new Label { Text = "E", ForeColor = UiTheme.Background, BackColor = UiTheme.Accent, Font = UiTheme.CreateFont(22F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Size = new Size(54, 54), Location = new Point(193, 40) };
        var title = UiTheme.CreateLabel("登录 Emby", 20F, true);
        title.Location = new Point(145, 116);
        var subtitle = UiTheme.CreateLabel("选择服务器和账户以继续", 9.5F);
        subtitle.Location = new Point(133, 155);

        StyleCombo(_server, new Point(54, 205));
        StyleCombo(_account, new Point(54, 261));
        _password.SetBounds(54, 317, 332, 36);
        _password.UseSystemPasswordChar = true;
        _server.SelectedIndexChanged += (_, _) => LoadAccounts();
        _account.SelectedIndexChanged += (_, _) => LoadPassword();

        var login = UiTheme.CreateButton("登录", true);
        login.SetBounds(54, 378, 332, 40);
        login.AutoSize = false;
        login.Click += async (_, _) => await LoginAsync();
        _password.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await LoginAsync(); } };
        card.Controls.AddRange([mark, title, subtitle, _server, _account, _password, login]);
        Controls.Add(card);
    }

    private async Task LoginAsync()
    {
        if (_server.SelectedItem is not ServerProfile server || _account.SelectedItem is not AccountProfile account)
        {
            MessageBox.Show(this, "请先在设置中添加服务器和账户。", "缺少登录资料");
            return;
        }
        _settings.Activate(server.Id, account.Id);
        await _login(_password.Text);
    }

    private void LoadAccounts()
    {
        _account.Items.Clear();
        if (_server.SelectedItem is not ServerProfile server) return;
        _account.Items.AddRange(server.Accounts.Cast<object>().ToArray());
        _account.SelectedItem = server.Accounts.FirstOrDefault(item => item.Id == _settings.LastAccountId) ?? server.Accounts.FirstOrDefault();
    }

    private void LoadPassword() => _password.Text = (_account.SelectedItem as AccountProfile)?.GetPassword() ?? "";

    private static void StyleCombo(ComboBox combo, Point location)
    {
        combo.SetBounds(location.X, location.Y, 332, 36);
        combo.BackColor = UiTheme.SurfaceRaised;
        combo.ForeColor = UiTheme.Text;
        combo.FlatStyle = FlatStyle.Flat;
        combo.Font = UiTheme.CreateFont(10F);
    }
}
