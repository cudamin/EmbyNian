using EmbyMpvClient.App.Controls;
using EmbyMpvClient.App.Infrastructure;
using EmbyMpvClient.App.Theme;
using EmbyMpvClient.Configuration;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Mpv;

namespace EmbyMpvClient.App.Views;

/// <summary>
/// Every setting the client has, grouped into cards. Changes apply the moment they are made and the
/// file is written a moment later — v1 had an OK/Cancel dialog whose Cancel path still left half the
/// values applied, so there is no dirty state to get wrong here.
/// </summary>
public sealed class SettingsView : AppView
{
    private const string Category = "settings";

    private static readonly LanguageOption[] Languages =
    [
        new("自动（由 mpv 决定）", ""),
        new("中文", "chi"),
        new("英语", "eng"),
        new("日语", "jpn"),
        new("韩语", "kor")
    ];

    private static readonly BackendOption[] Backends =
    [
        new("内置 libmpv（窗口内播放）", MpvBackendKind.BuiltInLibMpv),
        new("外部 mpv.exe（独立窗口）", MpvBackendKind.ExternalMpv)
    ];

    private readonly ScrollHost _scroll = new() { Dock = DockStyle.Fill };
    private readonly List<Section> _sections = [];
    private readonly System.Windows.Forms.Timer _save = new() { Interval = 700 };
    private readonly TextBlock _status = new("", Fonts.Small, Palette.TextFaint);

    // mpv
    private readonly DropDown _backend = new();
    private readonly PathEditor _mpvPath = new();
    private readonly PathEditor _libMpvPath = new();
    private readonly TextInput _extraArguments = new() { Placeholder = "例如 --volume=80" };

    // playback
    private readonly ToggleSwitch _report = new();
    private readonly ToggleSwitch _resume = new();
    private readonly NumberInput _markPercent = new(50, 100, 90, " %");
    private readonly ToggleSwitch _forcedSubtitles = new();
    private FieldRow _fontRow = null!;
    private readonly DropDown _font = new();
    private readonly TextInput _audioPriority = new() { Placeholder = "例如 简体中文>中文>英语" };
    private readonly TextInput _subtitlePriority = new() { Placeholder = "例如 简体中文>中文>繁体中文" };

    // shaders
    private readonly ToggleSwitch _applyToAll = new();
    private readonly DropDown _defaultProfile = new();
    private readonly ToggleSwitch _autoAnime = new();
    private readonly DropDown _animeProfile = new();
    private readonly TextInput _keywords = new() { Placeholder = "动画, 动漫, Anime" };
    private readonly DropDown _highResProfile = new();
    private readonly NumberInput _threshold = new(720, 4320, 1600, " p");
    private readonly TextBlock _shaderStatus = new("", Fonts.Small, Palette.TextDim);

    // interface
    private readonly NumberInput _pageSize = new(20, 400, 100, " 项");
    private readonly NumberInput _posterWidth = new(120, 300, 170, " px");
    private readonly ToggleSwitch _indicators = new();

    private readonly TextBlock _about = new("", Fonts.Small, Palette.TextDim);

    private bool _filling;

    public SettingsView(IShell shell) : base(shell)
    {
        HeaderTitle = "设置";

        Controls.Add(_scroll);
        _save.Tick += (_, _) =>
        {
            _save.Stop();
            Persist();
        };

        BuildMpvSection();
        BuildPlaybackSection();
        BuildShaderSection();
        BuildInterfaceSection();
        BuildAboutSection();

        _scroll.Content.Resize += (_, _) => LayoutSections();
    }

    private sealed record LanguageOption(string Label, string Code);

    private sealed record BackendOption(string Label, MpvBackendKind Kind);

    private sealed record FontOption(string Label, string FilePath);

    private sealed record ProfileOption(string Label, string Value);

    public override Task EnterAsync()
    {
        Fill();
        return Task.CompletedTask;
    }

    public override Task RefreshAsync()
    {
        Host.RefreshShaderCatalog();
        Fill();
        return Task.CompletedTask;
    }

    public override void Leave()
    {
        if (!_save.Enabled) return;
        _save.Stop();
        Persist();
    }

    // ---- sections ---------------------------------------------------------------

    private Section AddSection(string title, string subtitle)
    {
        var card = new Card { Title = title, Subtitle = subtitle };
        var section = new Section(card);
        _sections.Add(section);
        _scroll.Content.Controls.Add(card);
        return section;
    }

    private void BuildMpvSection()
    {
        var section = AddSection("mpv 播放器", "客户端只负责挑片和拼参数，解码和渲染全部交给 mpv。");

        _backend.Describe = Label;
        Apply(_backend, value => Host.Settings.Mpv.Backend = value);

        _mpvPath.Browse.Click += (_, _) => PickFile(_mpvPath, "mpv 可执行文件|mpv.exe;*.exe");
        _libMpvPath.Browse.Click += (_, _) => PickFile(_libMpvPath, "libmpv 动态库|libmpv-2.dll;*.dll");

        Apply(_mpvPath.Input, value => Host.Settings.Mpv.ExecutablePath = value);
        Apply(_libMpvPath.Input, value => Host.Settings.Mpv.LibMpvPath = value);
        Apply(_extraArguments, value => Host.Settings.Mpv.ExtraArguments = value);

        section.Add(new FieldRow("播放器", _backend, "内置 libmpv 在客户端窗口内播放；外部 mpv 保持独立窗口"));
        section.Add(new FieldRow("mpv.exe", _mpvPath, "外部 mpv 模式使用的播放器程序"));
        section.Add(new FieldRow("libmpv-2.dll", _libMpvPath, "内置模式使用的本地 libmpv-2.dll，可填文件或所在目录"));
        section.Add(new FieldRow("附加参数", _extraArguments, "追加到命令行末尾，用空格分隔"));
    }

    private void BuildPlaybackSection()
    {
        var section = AddSection("播放行为", "断点续播、已观看状态和轨道偏好。");

        _report.Text = "向服务器汇报播放进度";
        _report.Description = "关闭后 Emby 不会记录进度，也不会自动标记已观看";
        Apply(_report, value => Host.Settings.Playback.ReportProgressToServer = value);

        _resume.Text = "从服务器保存的位置继续";
        _resume.Description = "关闭后每次都从头播放";
        Apply(_resume, value => Host.Settings.Playback.ResumeFromSavedPosition = value);

        Apply(_markPercent, value => Host.Settings.Playback.MarkWatchedPercent = value);

        _forcedSubtitles.Text = "优先选择强制字幕";
        _forcedSubtitles.Description = "适合只想看外语对白翻译的情况";
        Apply(_forcedSubtitles, value => Host.Settings.Playback.PreferForcedSubtitles = value);

        _font.Describe = Label;
        Apply(_font, value => Host.Settings.Playback.SubtitleFontPath = value);

        Apply(_audioPriority, value => Host.Settings.Playback.AudioLanguagePriority = value);
        Apply(_subtitlePriority, value => Host.Settings.Playback.SubtitleLanguagePriority = value);

        _fontRow = new FieldRow("字幕字体", _font, "默认使用微软雅黑，作为 --sub-font 传给 mpv");

        section.Add(_report);
        section.Add(_resume);
        section.Add(new FieldRow("标记已观看阈值", _markPercent, "播放进度超过该比例即视为看完，达到后自动触发 Webhooks 通知", 120));
        section.Add(_forcedSubtitles);
        section.Add(_fontRow);
        section.Add(new FieldRow("音轨语言优先级", _audioPriority, "传给 mpv 的 --alang，用 > 或逗号分隔", 220));
        section.Add(new FieldRow("字幕语言优先级", _subtitlePriority, "传给 mpv 的 --slang，用 > 或逗号分隔", 220));
    }

    /// <summary>
    /// The 2K + AMD iGPU automation the whole shader feature exists for: one switch for every
    /// video, one switch for anime, and a lighter group for 4K sources that only ever downscale.
    /// </summary>
    private void BuildShaderSection()
    {
        var section = AddSection("着色器配置组", "为 2K 显示器和 AMD 核显准备的自动方案；配置组就是内置配置文件里的 [名称] 段。");

        _applyToAll.Text = "所有视频默认启用着色器组";
        _applyToAll.Description = "播放时附加 --profile=下面选择的配置组";
        Apply(_applyToAll, value => Host.Settings.Shaders.ApplyToAllVideos = value);

        _autoAnime.Text = "动画自动切换配置组";
        _autoAnime.Description = "元数据的类型/风格里含下面的关键词时，改用动画专用组";
        Apply(_autoAnime, value => Host.Settings.Shaders.AutoAnimeProfile = value);

        _defaultProfile.Describe = Label;
        _animeProfile.Describe = Label;
        _highResProfile.Describe = Label;
        Apply(_defaultProfile, value => Host.Settings.Shaders.DefaultProfile = value);
        Apply(_animeProfile, value => Host.Settings.Shaders.AnimeProfile = value);
        Apply(_highResProfile, value => Host.Settings.Shaders.HighResProfile = value);
        Apply(_threshold, value => Host.Settings.Shaders.HighResThresholdHeight = value);

        Apply(_keywords, value => Host.Settings.Shaders.AnimeKeywords = SplitKeywords(value));

        section.Add(_applyToAll);
        section.Add(new FieldRow("默认配置组", _defaultProfile, "对所有视频生效", 260));
        section.Add(_autoAnime);
        section.Add(new FieldRow("动画配置组", _animeProfile, "命中关键词时优先使用", 260));
        section.Add(new FieldRow("动画关键词", _keywords, "匹配 Emby 的类型与标签，用逗号分隔"));
        section.Add(new FieldRow("高分辨率配置组", _highResProfile, "4K 片源在 2K 屏上只会缩小，换省电组", 260));
        section.Add(new FieldRow("高分辨率阈值", _threshold, "片源高度不低于该值时改用上面的组", 120));
        section.Add(_shaderStatus);
    }

    private void BuildInterfaceSection()
    {
        var section = AddSection("界面", "列表分页与海报大小。");

        Apply(_pageSize, value => Host.Settings.Ui.PageSize = value);
        Apply(_posterWidth, value => Host.Settings.Ui.PosterWidth = value);

        _indicators.Text = "显示观看状态标记";
        _indicators.Description = "海报角上的已看 / 未看数量与收藏标记";
        Apply(_indicators, value => Host.Settings.Ui.ShowWatchedIndicators = value);

        section.Add(new FieldRow("每页数量", _pageSize, "媒体库和搜索每次请求的条数", 120));
        section.Add(new FieldRow("海报宽度", _posterWidth, "重新进入页面后生效", 120));
        section.Add(_indicators);
    }

    private void BuildAboutSection()
    {
        var section = AddSection("数据与维护", "设置、缓存和日志都放在用户目录下，卸载只需删除该目录。");

        var data = new FlatButton { Variant = ButtonVariant.Secondary, Glyph = Glyphs.Folder, Text = "打开数据目录" };
        data.Click += (_, _) => Reveal(Host.Paths.Root);

        var logs = new FlatButton { Variant = ButtonVariant.Secondary, Glyph = Glyphs.Log, Text = "打开日志目录" };
        logs.Click += (_, _) => Reveal(Host.Paths.LogDirectory);

        var signOut = new FlatButton { Variant = ButtonVariant.Danger, Glyph = Glyphs.SignOut, Text = "退出登录" };
        signOut.Click += (_, _) => Shell.SignOut();

        section.Add(_about);
        section.Add(new ButtonRow(data, logs, signOut));
        section.Add(_status);
    }

    // ---- binding ----------------------------------------------------------------

    /// <summary>Item text for the drop-downs. Not named Describe: that is <see cref="AppView.Describe"/>.</summary>
    private static string Label(object value) => value switch
    {
        LanguageOption language => language.Label,
        BackendOption backend => backend.Label,
        FontOption font => font.Label,
        ProfileOption profile => profile.Label,
        _ => value.ToString() ?? ""
    };

    /// <summary>Text fields apply when focus leaves or Enter is pressed, not on every keystroke.</summary>
    private void Apply(TextInput input, Action<string> assign)
    {
        void Commit()
        {
            if (_filling) return;
            assign(input.Text.Trim());
            QueueSave();
        }

        input.Submitted += (_, _) => Commit();
        input.Editor.Leave += (_, _) => Commit();
    }

    private void Apply(ToggleSwitch toggle, Action<bool> assign) =>
        toggle.CheckedChanged += (_, _) =>
        {
            if (_filling) return;
            assign(toggle.Checked);
            QueueSave();
        };

    private void Apply(NumberInput number, Action<int> assign) =>
        number.ValueChanged += (_, _) =>
        {
            if (_filling) return;
            assign(number.IntValue);
            QueueSave();
        };

    private void Apply(DropDown dropDown, Action<string> assign) =>
        dropDown.SelectedIndexChanged += (_, _) =>
        {
            if (_filling) return;
            assign(dropDown.SelectedItem switch
            {
                LanguageOption language => language.Code,
                ProfileOption profile => profile.Value,
                FontOption font => font.FilePath,
                _ => ""
            });

            QueueSave();
        };

    private void Apply(DropDown dropDown, Action<MpvBackendKind> assign) =>
        dropDown.SelectedIndexChanged += (_, _) =>
        {
            if (_filling) return;
            if (dropDown.SelectedItem is BackendOption backend) assign(backend.Kind);
            QueueSave();
        };

    private void QueueSave()
    {
        _status.Text = "正在保存…";
        _save.Stop();
        _save.Start();
    }

    private void Persist()
    {
        try
        {
            // Store.Save rather than Host.SaveSettings: the host swallows failures for shutdown,
            // and a settings page that says 「已保存」 when nothing was written is worse than an error.
            Host.Store.Save(Host.Settings);
            _status.Text = $"设置已保存 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception error)
        {
            Log.Warn(Category, "保存设置失败", error);
            _status.Text = "设置未能保存";
            Shell.Notify($"保存设置失败：{Describe(error)}", ToastKind.Error);
        }
    }

    private void Fill()
    {
        _filling = true;
        try
        {
            var settings = Host.Settings;

            _backend.Fill(Backends, Backends.First(option => option.Kind == settings.Mpv.Backend));
            _mpvPath.Input.Text = settings.Mpv.ExecutablePath;
            _libMpvPath.Input.Text = settings.Mpv.LibMpvPath;
            _extraArguments.Text = settings.Mpv.ExtraArguments;

            _report.SetCheckedSilently(settings.Playback.ReportProgressToServer);
            _resume.SetCheckedSilently(settings.Playback.ResumeFromSavedPosition);
            _markPercent.IntValue = settings.Playback.MarkWatchedPercent;
            _forcedSubtitles.SetCheckedSilently(settings.Playback.PreferForcedSubtitles);
            FillFont(_font, settings.Playback.SubtitleFontPath);
            _audioPriority.Text = settings.Playback.AudioLanguagePriority;
            _subtitlePriority.Text = settings.Playback.SubtitleLanguagePriority;

            _applyToAll.SetCheckedSilently(settings.Shaders.ApplyToAllVideos);
            _autoAnime.SetCheckedSilently(settings.Shaders.AutoAnimeProfile);
            _keywords.Text = string.Join("，", settings.Shaders.AnimeKeywords);
            _threshold.IntValue = settings.Shaders.HighResThresholdHeight;
            FillProfiles();

            _pageSize.IntValue = settings.Ui.PageSize;
            _posterWidth.IntValue = settings.Ui.PosterWidth;
            _indicators.SetCheckedSilently(settings.Ui.ShowWatchedIndicators);

            _about.Text = string.Join(
                Environment.NewLine,
                Composition.AppInfo.TitleWithVersion,
                $"数据目录：{Host.Paths.Root}",
                $"日志目录：{Host.Paths.LogDirectory}",
                $"配置组文件：{Host.ShaderPackPath}");
        }
        finally
        {
            _filling = false;
        }

        LayoutSections();
    }

    private static void FillFont(DropDown target, string configured)
    {
        const string defaultFont = @"C:\Windows\Fonts\msyh.ttc";
        var options = new List<object> { new FontOption("默认（微软雅黑）", defaultFont) };

        foreach (var font in WindowsFonts.List())
        {
            if (string.Equals(font.FilePath, defaultFont, StringComparison.OrdinalIgnoreCase)) continue;
            options.Add(new FontOption(font.DisplayName, font.FilePath));
        }

        var trimmed = configured.Trim();
        var selected = trimmed.Length == 0
            ? (FontOption)options[0]
            : options.OfType<FontOption>().FirstOrDefault(option =>
                string.Equals(option.FilePath, trimmed, StringComparison.OrdinalIgnoreCase));

        if (selected is null && trimmed.Length > 0)
        {
            var missing = new FontOption($"{Path.GetFileName(trimmed)}（文件不存在）", trimmed);
            options.Add(missing);
            selected = missing;
        }

        target.Fill(options, selected ?? options[0]);
    }

    private void FillProfiles()
    {
        var settings = Host.Settings.Shaders;
        FillProfile(_defaultProfile, settings.DefaultProfile);
        FillProfile(_animeProfile, settings.AnimeProfile);
        FillProfile(_highResProfile, settings.HighResProfile);
        _shaderStatus.Text = DescribeCatalog();
    }

    /// <summary>
    /// Lists every group the drop-downs offer, grouped by the file it came from — the user's own
    /// mpv.conf groups (NNEDI3, AnimeJaNai, SSIM …) and the ones this client generated.
    /// </summary>
    private string DescribeCatalog()
    {
        var catalog = Host.ShaderCatalog;
        if (catalog.Count == 0)
            return $"没有扫描到配置组。检查 {Host.ShaderPackPath} 是否存在。";

        var lines = new List<string> { $"共 {catalog.Count} 个可选配置组：" };
        foreach (var group in catalog.GroupBy(profile => profile.SourceFile, StringComparer.OrdinalIgnoreCase))
        {
            var file = group.Key.Length == 0 ? "未知来源" : Path.GetFileName(group.Key);
            lines.Add($"· {file}：{string.Join("、", group.Select(profile => profile.Name))}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void FillProfile(DropDown target, string configured)
    {
        var was = _filling;
        _filling = true;
        try
        {
            var trimmed = configured.Trim();
            var options = new List<object> { new ProfileOption("不指定（沿用 mpv.conf）", "") };

            foreach (var profile in Host.ShaderCatalog)
                options.Add(new ProfileOption(profile.DisplayName, profile.Name));

            if (trimmed.Length > 0 && Host.ShaderCatalog.All(profile => profile.Name != trimmed))
                options.Add(new ProfileOption($"{trimmed}（配置文件里没有找到）", trimmed));

            var selected = options.OfType<ProfileOption>().FirstOrDefault(option => option.Value == trimmed);
            target.Fill(options, selected ?? options[0]);
        }
        finally
        {
            _filling = was;
        }
    }

    private static List<string> SplitKeywords(string value) =>
        value
            .Split([',', '，', ';', '；', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void PickFile(PathEditor editor, string filter)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = $"{filter}|所有文件|*.*",
            CheckFileExists = true,
            Title = "选择文件"
        };

        var current = editor.Input.Text.Trim();
        if (current.Length > 0)
        {
            var directory = Path.GetDirectoryName(current);
            if (Directory.Exists(directory)) dialog.InitialDirectory = directory;
            if (File.Exists(current)) dialog.FileName = Path.GetFileName(current);
        }

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        editor.Input.Text = dialog.FileName;
        editor.Input.FocusEditor();
        Focus();
    }

    // ---- layout -----------------------------------------------------------------

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutSections();
    }

    private void LayoutSections()
    {
        var margin = Dpi.Scale(this, 16);
        var padding = Dpi.Scale(this, 18);
        var gap = Dpi.Scale(this, 6);
        var editorHeight = Dpi.Scale(this, 30);
        var width = Math.Min(Dpi.Scale(this, 880), _scroll.Content.Width - margin * 2);
        if (width <= Dpi.Scale(this, 200)) return;

        var y = margin;

        foreach (var section in _sections)
        {
            section.Card.Padding = new Padding(padding);
            section.Card.SetBounds(margin, y, width, Dpi.Scale(this, 80));

            // A Panel does not offset explicitly positioned children by its padding, so the rows
            // are inset by hand; ContentTop already accounts for the title block.
            var inner = width - padding * 2;
            var rowTop = section.Card.ContentTop;

            foreach (var row in section.Rows)
            {
                if (!row.Visible) continue;

                if (row is TextBlock text)
                {
                    text.SetBounds(padding, rowTop + gap, inner, 0);
                    text.FitHeight();
                    rowTop = text.Bottom + gap;
                    continue;
                }

                // A combo box sizes itself to its font; everything else follows the theme.
                if (row is FieldRow field && field.Editor is not ComboBox) field.Editor.Height = editorHeight;

                var height = row switch
                {
                    FieldRow fieldRow => fieldRow.PreferredHeight(),
                    ToggleSwitch toggle => toggle.PreferredHeight(),
                    ButtonRow buttons => buttons.PreferredHeight(),
                    SectionHeader header => header.Height,
                    _ => row.Height
                };

                row.SetBounds(padding, rowTop, inner, height);
                rowTop = row.Bottom + gap;
            }

            section.Card.Height = rowTop + padding;
            y = section.Card.Bottom + Dpi.Scale(this, 14);
        }

        _scroll.Content.Height = Math.Max(y, 1);
        _scroll.RefreshExtent();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _save.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>One card plus the rows inside it, in display order.</summary>
    private sealed class Section(Card card)
    {
        public Card Card { get; } = card;

        public List<Control> Rows { get; } = [];

        public T Add<T>(T row) where T : Control
        {
            Rows.Add(row);
            Card.Controls.Add(row);
            return row;
        }
    }

    /// <summary>A text box with a 「浏览…」 button, used for the three file paths.</summary>
    private sealed class PathEditor : Control
    {
        public PathEditor()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 30;
            Controls.Add(Input);
            Controls.Add(Browse);
        }

        public TextInput Input { get; } = new();

        public FlatButton Browse { get; } = new() { Variant = ButtonVariant.Secondary, Text = "浏览…" };

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            var buttonWidth = Dpi.Scale(this, 72);
            var gap = Dpi.Scale(this, 6);
            Input.SetBounds(0, 0, Math.Max(Dpi.Scale(this, 80), Width - buttonWidth - gap), Height);
            Browse.SetBounds(Width - buttonWidth, 0, buttonWidth, Height);
        }
    }

    /// <summary>A left-aligned row of buttons that size themselves to their captions.</summary>
    private sealed class ButtonRow : Control
    {
        private readonly FlatButton[] _buttons;

        public ButtonRow(params FlatButton[] buttons)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            _buttons = buttons;
            foreach (var button in buttons) Controls.Add(button);
        }

        public int PreferredHeight() => Dpi.Scale(this, 42);

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            var height = Dpi.Scale(this, 32);
            var top = (Height - height) / 2;
            var x = 0;

            foreach (var button in _buttons)
            {
                button.AutoSizeToContent(minimumWidth: Dpi.Scale(this, 96));
                button.SetBounds(x, top, button.Width, height);
                x = button.Right + Dpi.Scale(this, 8);
            }
        }
    }
}
