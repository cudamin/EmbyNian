using System.Drawing.Drawing2D;
using EmbyMpvClient.App.Controls;
using EmbyMpvClient.App.Theme;
using EmbyMpvClient.Configuration;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Emby;

namespace EmbyMpvClient.App.Views;

/// <summary>
/// The sign-in page. Also the place that decides whether a sign-in is needed at all: on startup
/// it tries the saved token first, so the usual launch goes straight to the home page.
/// <para>
/// Laid out as two columns — what the app is on the left, the form on the right — because this is
/// the first thing anyone sees, and a lone box of unlabelled fields in the middle of a dark window
/// says nothing about what it is about to do. The left column is dropped on a narrow window, where
/// the form alone is the whole point.
/// </para>
/// </summary>
public sealed class LoginView : AppView
{
    private const string Category = "login";

    private readonly Brand _brand = new();

    private readonly Card _card = new()
    {
        Title = "登录",
        Subtitle = "地址可以省略 http:// 和 /emby"
    };

    private readonly TextBlock _serverLabel = Label("已保存的服务器");
    private readonly DropDown _servers = new();
    private readonly TextBlock _urlLabel = Label("服务器地址");
    private readonly TextInput _url = new() { Placeholder = "192.168.1.10:8096", Glyph = Glyphs.Globe };
    private readonly FlatButton _probe = new() { Text = "测试连接", Variant = ButtonVariant.Secondary };
    private readonly TextBlock _usernameLabel = Label("用户名");
    private readonly TextInput _username = new() { Placeholder = "Emby 用户名", Glyph = Glyphs.Person };
    private readonly DropDown _users = new() { Enabled = false };
    private readonly TextBlock _passwordLabel = Label("密码");
    private readonly TextInput _password = new() { Placeholder = "留空表示无密码", Password = true, Glyph = Glyphs.Lock };

    private readonly ToggleSwitch _remember = new()
    {
        Text = "记住密码",
        Description = "用 Windows 帐户密钥加密后保存"
    };

    private readonly FlatButton _signIn = new() { Text = "登录", Variant = ButtonVariant.Primary, CornerRadius = 8 };
    private readonly TextBlock _status = new("", Fonts.Small, Palette.TextDim);
    private readonly TextBlock _version = new(AppInfoText, Fonts.Small, Palette.TextFaint);

    private bool _busy;

    public LoginView(IShell shell) : base(shell)
    {
        HeaderTitle = "登录";

        _servers.Describe = item => item is ServerProfile server
            ? $"{server.Name} — {server.Url}"
            : item.ToString() ?? "";
        _servers.SelectedIndexChanged += (_, _) => OnServerPicked();

        _users.Describe = item => item is EmbyUser user ? user.Name : item.ToString() ?? "";
        _users.SelectedIndexChanged += (_, _) =>
        {
            if (_users.SelectedItem is EmbyUser user) _username.Text = user.Name;
        };

        _url.Submitted += (_, _) => Run(ProbeAsync, "测试连接失败");
        _username.Submitted += (_, _) => _password.FocusEditor();
        _password.Submitted += (_, _) => Run(SignInAsync, "登录失败");
        _probe.Click += (_, _) => Run(ProbeAsync, "测试连接失败");
        _signIn.Click += (_, _) => Run(SignInAsync, "登录失败");

        Controls.Add(_brand);
        Controls.Add(_card);
        foreach (var child in new Control[]
                 {
                     _serverLabel, _servers, _urlLabel, _url, _probe, _usernameLabel, _username, _users,
                     _passwordLabel, _password, _remember, _signIn, _status, _version
                 })
        {
            _card.Controls.Add(child);
        }
    }

    /// <summary>Raised once a session exists; the shell then builds the browsing pages.</summary>
    public event Action? SignedIn;

    private static string AppInfoText => $"{Composition.AppInfo.TitleWithVersion}　·　仅 Windows 11";

    private static TextBlock Label(string text) => new(text, Fonts.Small, Palette.TextDim);

    public override Task EnterAsync()
    {
        FillFromSettings();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Startup path: reuses the saved token, falling back to the saved password. Returns false
    /// when the user has to type something, which is the only case that shows this page.
    /// </summary>
    public async Task<bool> TryRestoreAsync()
    {
        var settings = Host.Settings;
        var server = settings.ResolveLastServer();
        var account = settings.ResolveLastAccount(server);
        if (server is null || account is null || !account.HasSavedToken && !account.HasSavedPassword) return false;

        Shell.Busy($"正在连接 {server.Name}…");
        try
        {
            return await Host.Session.TryRestoreAsync(server, account, Lifetime).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "自动登录失败", error);
            return false;
        }
        finally
        {
            Shell.Idle();
        }
    }

    private void FillFromSettings()
    {
        var settings = Host.Settings;
        var server = settings.ResolveLastServer();

        // One saved server is what the address field already says; the picker only earns its space
        // once there is something to pick between.
        _servers.Visible = settings.Servers.Count > 1;
        _serverLabel.Visible = _servers.Visible;
        if (settings.Servers.Count > 0) _servers.Fill(settings.Servers, server);

        if (server is not null)
        {
            _url.Text = server.Url;
            var account = settings.ResolveLastAccount(server);
            _username.Text = account?.Username ?? "";
            _remember.SetCheckedSilently(account?.RememberPassword ?? true);
            if (account?.HasSavedPassword == true) _password.Text = Host.Vault.GetPassword(account);
        }
        else
        {
            _remember.SetCheckedSilently(true);
        }

        LayoutPage();
        if (_username.Text.Length == 0) _username.FocusEditor();
        else _password.FocusEditor();
    }

    private void OnServerPicked()
    {
        if (_servers.SelectedItem is not ServerProfile server) return;
        _url.Text = server.Url;

        var account = server.FindAccount(Host.Settings.LastAccountId) ?? server.Accounts.FirstOrDefault();
        _username.Text = account?.Username ?? "";
        _password.Text = account?.HasSavedPassword == true ? Host.Vault.GetPassword(account) : "";
        _remember.SetCheckedSilently(account?.RememberPassword ?? true);
        _users.Enabled = false;
        _users.Items.Clear();
    }

    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        if (_busy || !TryReadAddress(out var apiBase)) return;

        SetBusy(true, "正在测试连接…");
        try
        {
            var info = await Host.Session.Gateway.GetPublicSystemInfoAsync(apiBase, cancellationToken).ConfigureAwait(true);
            SetStatus($"已连接 {info.ServerName}（Emby {info.Version}）", Palette.Accent);

            var users = await Host.Session.Gateway.GetPublicUsersAsync(apiBase, cancellationToken).ConfigureAwait(true);
            if (users.Count == 0)
            {
                _users.Enabled = false;
                return;
            }

            var current = users.FirstOrDefault(user =>
                string.Equals(user.Name, _username.Text.Trim(), StringComparison.OrdinalIgnoreCase));

            _users.Enabled = true;
            _users.Fill(users, current);
            if (current is null && _username.Text.Trim().Length == 0 && _users.SelectedItem is EmbyUser first)
                _username.Text = first.Name;
        }
        catch (Exception error)
        {
            SetStatus(Describe(error), Palette.Danger);
            throw;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        if (_busy || !TryReadAddress(out var apiBase)) return;

        var username = _username.Text.Trim();
        if (username.Length == 0)
        {
            SetStatus("请填写用户名", Palette.Danger);
            _username.FocusEditor();
            return;
        }

        SetBusy(true, "正在登录…");
        try
        {
            var server = ResolveServer(apiBase);
            var account = ResolveAccount(server, username);

            await Host.Session
                .SignInAsync(server, account, _password.Text, username, _remember.Checked, cancellationToken)
                .ConfigureAwait(true);

            Host.SaveSettings();
            SetStatus("", Palette.TextDim);
            SignedIn?.Invoke();
        }
        catch (Exception error)
        {
            SetStatus(Describe(error), Palette.Danger);
            _password.FocusEditor();
            _password.SelectAllText();
            throw;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool TryReadAddress(out Uri apiBase)
    {
        if (EmbyServerAddress.TryNormalize(_url.Text, out apiBase, out var error)) return true;
        SetStatus(error, Palette.Danger);
        _url.FocusEditor();
        return false;
    }

    /// <summary>Reuses the saved profile for an address so signing in twice does not duplicate it.</summary>
    private ServerProfile ResolveServer(Uri apiBase)
    {
        var settings = Host.Settings;
        var existing = settings.Servers.FirstOrDefault(candidate =>
            EmbyServerAddress.TryNormalize(candidate.Url, out var known, out _) && known == apiBase);

        if (existing is not null)
        {
            existing.Url = EmbyServerAddress.ToDisplayString(apiBase);
            return existing;
        }

        var server = new ServerProfile
        {
            Name = apiBase.Host,
            Url = EmbyServerAddress.ToDisplayString(apiBase)
        };

        settings.Servers.Add(server);
        return server;
    }

    private static AccountProfile ResolveAccount(ServerProfile server, string username)
    {
        var existing = server.Accounts.FirstOrDefault(account =>
            string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase));

        if (existing is not null) return existing;

        var created = new AccountProfile { Username = username };
        server.Accounts.Add(created);
        return created;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        _signIn.Enabled = !busy;
        _probe.Enabled = !busy;
        _signIn.Text = busy ? "请稍候…" : "登录";
        if (message is not null) SetStatus(message, Palette.TextDim);
    }

    private void SetStatus(string message, Color color)
    {
        _status.ForeColor = color;
        _status.Text = message;
        LayoutPage();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutPage();
    }

    /// <summary>
    /// Two soft accent washes, one behind each column. The page is otherwise a flat dark rectangle,
    /// and a gradient costs nothing here: this is the one page that is never redrawn in a loop.
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Draw.Smooth(e.Graphics);

        var size = Dpi.Scale(this, 620);
        Glow(e.Graphics, new Point(_brand.Visible ? _brand.Left + _brand.Width / 3 : Width / 2, _card.Top), size, Palette.Accent, 34);
        Glow(e.Graphics, new Point(_card.Right, _card.Bottom), size, Palette.Info, 22);
    }

    private static void Glow(Graphics graphics, Point center, int size, Color color, int alpha)
    {
        var bounds = new Rectangle(center.X - size / 2, center.Y - size / 2, size, size);
        using var path = new GraphicsPath();
        path.AddEllipse(bounds);

        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(alpha, color),
            SurroundColors = [Color.FromArgb(0, color)],
            CenterPoint = center
        };

        graphics.FillEllipse(brush, bounds);
    }

    private void LayoutPage()
    {
        var margin = Dpi.Scale(this, 32);
        var gap = Dpi.Scale(this, 56);
        var cardWidth = Dpi.Scale(this, 404);
        var heroMinimum = Dpi.Scale(this, 300);
        var available = Math.Max(Dpi.Scale(this, 200), Width - margin * 2);

        var wide = available >= cardWidth + gap + heroMinimum;
        _brand.Visible = wide;

        // The version line lives in the hero; without the hero the card has to carry it.
        _version.Visible = !wide;
        if (!wide) cardWidth = Math.Clamp(available, Dpi.Scale(this, 280), Dpi.Scale(this, 440));

        var cardHeight = LayoutCard(cardWidth);

        if (!wide)
        {
            _card.SetBounds((Width - cardWidth) / 2, Math.Max(margin, (Height - cardHeight) / 2), cardWidth, cardHeight);
            Invalidate();
            return;
        }

        var heroWidth = Math.Min(Dpi.Scale(this, 400), available - cardWidth - gap);
        var heroHeight = _brand.Measure(heroWidth);
        var left = (Width - (heroWidth + gap + cardWidth)) / 2;
        var top = Math.Max(margin, (Height - Math.Max(cardHeight, heroHeight)) / 2);

        // Each column centred against the taller one, so neither hangs off the bottom.
        _brand.SetBounds(left, top + Math.Max(0, (cardHeight - heroHeight) / 2), heroWidth, heroHeight);
        _card.SetBounds(left + heroWidth + gap, top + Math.Max(0, (heroHeight - cardHeight) / 2), cardWidth, cardHeight);
        Invalidate();
    }

    /// <summary>Lays out the form column at a given width and returns the height it needs.</summary>
    private int LayoutCard(int cardWidth)
    {
        var padding = Dpi.Scale(this, 22);
        var gap = Dpi.Scale(this, 14);
        var labelGap = Dpi.Scale(this, 4);
        var rowHeight = Dpi.Scale(this, 36);
        var comboHeight = Dpi.Scale(this, 28);
        var labelHeight = Draw.Measure("密码", Fonts.Small).Height;
        var inner = cardWidth - padding * 2;

        _card.Padding = new Padding(padding);
        _card.Width = cardWidth;
        var y = _card.ContentTop + Dpi.Scale(this, 10);

        if (_servers.Visible)
        {
            _serverLabel.SetBounds(padding, y, inner, labelHeight);
            _servers.SetBounds(padding, _serverLabel.Bottom + labelGap, inner, comboHeight);
            y = _servers.Bottom + gap;
        }

        var probeWidth = Dpi.Scale(this, 88);
        _urlLabel.SetBounds(padding, y, inner, labelHeight);
        _url.SetBounds(padding, _urlLabel.Bottom + labelGap, inner - probeWidth - Dpi.Scale(this, 8), rowHeight);
        _probe.SetBounds(_url.Right + Dpi.Scale(this, 8), _url.Top, probeWidth, rowHeight);
        y = _url.Bottom + gap;

        var usersWidth = _users.Enabled ? Dpi.Scale(this, 140) : 0;
        _users.Visible = usersWidth > 0;
        _usernameLabel.SetBounds(padding, y, inner, labelHeight);
        _username.SetBounds(
            padding,
            _usernameLabel.Bottom + labelGap,
            usersWidth > 0 ? inner - usersWidth - Dpi.Scale(this, 8) : inner,
            rowHeight);

        if (usersWidth > 0)
            _users.SetBounds(_username.Right + Dpi.Scale(this, 8), _username.Top + (rowHeight - comboHeight) / 2, usersWidth, comboHeight);

        y = _username.Bottom + gap;

        _passwordLabel.SetBounds(padding, y, inner, labelHeight);
        _password.SetBounds(padding, _passwordLabel.Bottom + labelGap, inner, rowHeight);
        y = _password.Bottom + Dpi.Scale(this, 10);

        _remember.SetBounds(padding, y, inner, _remember.PreferredHeight());
        y = _remember.Bottom + Dpi.Scale(this, 12);

        _signIn.SetBounds(padding, y, inner, Dpi.Scale(this, 40));
        y = _signIn.Bottom;

        if (_status.Text.Length > 0)
        {
            _status.SetBounds(padding, y + Dpi.Scale(this, 12), inner, 0);
            _status.FitHeight();
            y = _status.Bottom;
        }

        if (_version.Visible)
        {
            _version.SetBounds(padding, y + Dpi.Scale(this, 12), inner, labelHeight);
            y = _version.Bottom;
        }

        return y + padding;
    }

    /// <summary>
    /// The left column: what this program is, in one badge, one line and three bullets. Drawn rather
    /// than assembled from labels because it is pure decoration — nothing here is clickable, and a
    /// single paint method keeps the measuring and the drawing from drifting apart.
    /// </summary>
    private sealed class Brand : Control
    {
        private const string Tagline = "在 Emby 里挑片，用本机 mpv 看片。";

        private static readonly string[] Points =
        [
            "画质、快捷键、硬解全部由你自己的 mpv.conf 决定",
            "续播位置、观看进度和「看过」标记与 Emby 同步",
            "按分辨率和类型自动切换着色器配置组"
        ];

        public Brand()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor, true);

            BackColor = Color.Transparent;
            Font = Fonts.Body;
            TabStop = false;
        }

        /// <summary>The height this column needs at <paramref name="width"/>.</summary>
        public int Measure(int width) => Render(null, width);

        protected override void OnPaint(PaintEventArgs e)
        {
            Draw.Smooth(e.Graphics);
            Render(e.Graphics, Width);
        }

        /// <summary>Measures with a null <paramref name="graphics"/> and paints with a real one.</summary>
        private int Render(Graphics? graphics, int width)
        {
            var badge = Dpi.Scale(this, 56);
            var y = 0;

            var box = new Rectangle(0, y, badge, badge);
            if (graphics is not null)
            {
                Draw.Fill(graphics, box, Dpi.Scale(this, 16), Palette.Accent);
                Draw.Text(graphics, Glyphs.Play, Fonts.IconHuge, Palette.TextOnAccent, box, Draw.Centered);
            }

            y = box.Bottom + Dpi.Scale(this, 24);

            var titleHeight = Draw.Measure(Composition.AppInfo.Title, Fonts.Display).Height;
            if (graphics is not null)
            {
                Draw.Text(
                    graphics,
                    Composition.AppInfo.Title,
                    Fonts.Display,
                    Palette.Text,
                    new Rectangle(0, y, width, titleHeight),
                    Draw.SingleLine);
            }

            y += titleHeight + Dpi.Scale(this, 10);

            var taglineHeight = Draw.Measure(Tagline, Fonts.Subtitle, width).Height;
            if (graphics is not null)
            {
                Draw.Text(graphics, Tagline, Fonts.Subtitle, Palette.TextDim, new Rectangle(0, y, width, taglineHeight), Draw.Wrapped);
            }

            y += taglineHeight + Dpi.Scale(this, 28);

            var iconWidth = Dpi.Scale(this, 24);
            var lineHeight = Draw.Measure(Points[0], Fonts.Body).Height;
            foreach (var point in Points)
            {
                var textWidth = Math.Max(20, width - iconWidth);
                var height = Draw.Measure(point, Fonts.Body, textWidth).Height;
                if (graphics is not null)
                {
                    Draw.Text(graphics, Glyphs.Check, Fonts.IconSmall, Palette.Accent, new Rectangle(0, y, iconWidth, lineHeight), Draw.LeftMiddle);
                    Draw.Text(graphics, point, Fonts.Body, Palette.TextDim, new Rectangle(iconWidth, y, textWidth, height), Draw.Wrapped);
                }

                y += height + Dpi.Scale(this, 12);
            }

            y += Dpi.Scale(this, 16);
            var footerHeight = Draw.Measure(AppInfoText, Fonts.Small).Height;
            if (graphics is not null)
            {
                Draw.Text(graphics, AppInfoText, Fonts.Small, Palette.TextFaint, new Rectangle(0, y, width, footerHeight), Draw.SingleLine);
            }

            return y + footerHeight;
        }
    }
}
