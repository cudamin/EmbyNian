using System.Drawing.Drawing2D;
using EmbyMpvClient.App.Theme;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Emby;
using EmbyMpvClient.Infrastructure;
using EmbyMpvClient.Mpv;
using EmbyMpvClient.Playback;

namespace EmbyMpvClient.App.Controls;

/// <summary>
/// The transport bar for embedded playback: a full-width progress strip along the top edge and a
/// single row of controls beneath it. Like the rest of the video chrome it is a borderless owned
/// window rather than a child control, because WinForms' transparent colour can only blend with a
/// parent's background, while a top-level window's <see cref="Form.Opacity"/> is a real layered
/// alpha the compositor blends against the mpv video underneath.
///
/// Everything the previous bar farmed out to its neighbours now lives here: the vertical volume
/// rail and the four ComboBox pickers are gone, replaced by self-drawn <see cref="PlayerFlyout"/>
/// panels — which is also what removed the need to hoist Windows' own drop-down list windows above
/// the video.
/// </summary>
public sealed class PlayerBar : Form
{
    /// <summary>Unscaled height the shell reserves for the bar.</summary>
    public const int DesignHeight = 76;

    private const string Category = "player-bar";

    /// <summary>Chapter stills are fetched wider than they are drawn, so HiDPI stays sharp.</summary>
    private const int ThumbnailRequestWidth = 320;

    private const int NoActivate = 0x08000000;
    private const int ToolWindow = 0x00000080;

    private enum MenuKind
    {
        None,
        Episodes,
        Audio,
        Subtitles,
        Shaders
    }

    private readonly FlatButton _play = IconButton(Glyphs.Pause);
    private readonly FlatButton _back = IconButton(Glyphs.Previous);
    private readonly FlatButton _forward = IconButton(Glyphs.Next);
    private readonly FlatButton _speaker = IconButton(Glyphs.Volume);
    private readonly FlatButton _episodes = TextButton("选集");
    private readonly FlatButton _audio = TextButton("音轨");
    private readonly FlatButton _subtitles = TextButton("字幕");
    private readonly FlatButton _shaders = TextButton("着色器");
    private readonly FlatButton _stats = TextButton("统计");
    private readonly FlatButton _fullscreen = IconButton(Glyphs.Fullscreen);
    private readonly FlatButton _close = IconButton(Glyphs.Close);
    private readonly ToolTip _tips = new();
    private readonly PlayerMenu _menu = new();
    private readonly VolumeFlyout _volume = new();
    private readonly SeekPreview _preview = new();
    private readonly EmbyImageStore _images;

    /// <summary>Decoded chapter stills by chapter index; a null value marks one the server refused.</summary>
    private readonly Dictionary<int, Image?> _thumbnails = [];
    private readonly HashSet<int> _requested = [];

    private PlayerStatus _status = new();
    private bool _hasStatus;
    private double _fraction;
    private bool _seekHover;
    private bool _seekDrag;
    private double _hoverSeconds;
    private int _hoverScreenX;

    /// <summary>Which picker the menu last showed, so a chosen row can be routed back.</summary>
    private MenuKind _openMenu;

    private CancellationTokenSource? _thumbnailLifetime;

    /// <summary>Bumped by every Begin/End so a late thumbnail cannot land on the next item.</summary>
    private int _generation;

    private string _itemId = "";
    private IReadOnlyList<ChapterInfo> _chapters = [];
    private IReadOnlyList<EmbyItem> _episodeList = [];
    private IReadOnlyList<MpvTrack> _tracks = [];
    private IReadOnlyList<ShaderProfile> _shaderList = [];
    private ShaderProfile? _shaderChoice;
    private int? _audioId;
    private int? _subtitleId;

    public PlayerBar(EmbyImageStore images)
    {
        _images = images;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Opacity = 0.92;
        BackColor = Color.FromArgb(18, 20, 24);
        Height = DesignHeight;
        Visible = false;
        _episodes.Visible = false;

        _play.Click += (_, _) => PauseRequested?.Invoke();
        _back.Click += (_, _) => SeekRelativeRequested?.Invoke(-10);
        _forward.Click += (_, _) => SeekRelativeRequested?.Invoke(10);
        _speaker.Click += (_, _) => MuteRequested?.Invoke();
        _speaker.MouseEnter += (_, _) => ShowVolume();
        _stats.Click += (_, _) => StatsRequested?.Invoke();
        _fullscreen.Click += (_, _) => FullscreenRequested?.Invoke();
        _close.Click += (_, _) => StopRequested?.Invoke();

        _episodes.Click += (_, _) => OpenMenu(MenuKind.Episodes);
        _audio.Click += (_, _) => OpenMenu(MenuKind.Audio);
        _subtitles.Click += (_, _) => OpenMenu(MenuKind.Subtitles);
        _shaders.Click += (_, _) => OpenMenu(MenuKind.Shaders);

        _menu.ItemChosen += OnMenuItemChosen;
        _menu.Dismissed += OnPanelDismissed;
        _volume.ValueChanged += value => VolumeRequested?.Invoke(value);
        _volume.Dismissed += OnPanelDismissed;

        Controls.AddRange([
            _play, _back, _forward, _speaker,
            _episodes, _audio, _subtitles, _shaders, _stats,
            _fullscreen, _close
        ]);

        _tips.SetToolTip(_play, "播放/暂停");
        _tips.SetToolTip(_back, "后退 10 秒");
        _tips.SetToolTip(_forward, "前进 10 秒");
        _tips.SetToolTip(_speaker, "音量（悬停调节，点击静音）");
        _tips.SetToolTip(_episodes, "选集");
        _tips.SetToolTip(_audio, "音轨");
        _tips.SetToolTip(_subtitles, "字幕");
        _tips.SetToolTip(_shaders, "着色器");
        _tips.SetToolTip(_stats, "统计信息");
        _tips.SetToolTip(_fullscreen, "全屏");
        _tips.SetToolTip(_close, "停止播放");
    }

    /// <summary>Appearing must never steal focus from the shell.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var createParams = base.CreateParams;
            createParams.ExStyle |= NoActivate | ToolWindow;
            return createParams;
        }
    }

    public event Action? PauseRequested;

    /// <summary>Seconds to jump, relative to the current position.</summary>
    public event Action<double>? SeekRelativeRequested;

    /// <summary>Absolute position in seconds, from a drag along the progress strip.</summary>
    public event Action<double>? SeekRequested;

    public event Action? MuteRequested;

    /// <summary>0-100, from the volume panel.</summary>
    public event Action<int>? VolumeRequested;

    public event Action? StopRequested;

    public event Action? FullscreenRequested;

    /// <summary>Shows mpv's built-in statistics overlay (the reference player's 统计 button).</summary>
    public event Action? StatsRequested;

    public event Action<EmbyItem>? EpisodeRequested;

    /// <summary>mpv track id of the chosen audio track.</summary>
    public event Action<int>? AudioTrackSelected;

    /// <summary>mpv track id of the chosen subtitle track; null means off.</summary>
    public event Action<int?>? SubtitleTrackSelected;

    /// <summary>The chosen group; null is 「自动」 and means "back to what the startup decision picked".</summary>
    public event Action<ShaderProfile?>? ShaderRequested;

    /// <summary>Raised whenever a panel opens or closes; the video surface pins the bar while one is up.</summary>
    public event Action<bool>? PopupVisibilityChanged;

    /// <summary>True while a picker or the volume panel is open, so the bar must not fade out.</summary>
    public bool PopupOpen => _menu.Visible || _volume.Visible;

    /// <summary>Readies the bar for a newly started item, before any position or track is known.</summary>
    public void Begin(
        EmbyItem item,
        IReadOnlyList<EmbyItem> episodes,
        IReadOnlyList<ShaderProfile> shaders,
        ShaderProfile? startupShader)
    {
        DismissPopups();
        _generation++;
        ResetThumbnails();

        _itemId = item.Id;
        _chapters = item.Chapters;
        _episodeList = episodes;
        _shaderList = shaders;
        _shaderChoice = startupShader;
        _tracks = [];
        _audioId = null;
        _subtitleId = null;
        _status = new PlayerStatus();
        _hasStatus = false;
        _fraction = 0;
        _thumbnailLifetime = new CancellationTokenSource();
        _episodes.Visible = episodes.Count > 1;

        ApplyStatus();
        LayoutBar();
    }

    /// <summary>
    /// The coarser progress feed, used while no <see cref="PlayerStatus"/> has arrived yet — the
    /// external-mpv backend never sends one. A run time means the file is open, which is what
    /// <see cref="PlayerStatus.Loaded"/> stands for; without it the clock would read 「正在打开…」
    /// for the whole playback.
    /// </summary>
    public void Update(PlaybackProgress progress)
    {
        if (_hasStatus) return;

        _status = _status with
        {
            Position = progress.PositionTicks / (double)TimeSpan.TicksPerSecond,
            Duration = progress.RunTimeTicks / (double)TimeSpan.TicksPerSecond,
            Paused = progress.IsPaused,
            Loaded = progress.RunTimeTicks > 0
        };
        ApplyStatus();
    }

    public void Update(PlayerStatus status)
    {
        _status = status;
        _hasStatus = true;
        ApplyStatus();
    }

    private void ApplyStatus()
    {
        _play.Glyph = _status.Paused ? Glyphs.Play : Glyphs.Pause;
        _speaker.Glyph = _status.Muted || _status.Volume <= 0.5 ? Glyphs.Mute : Glyphs.Volume;
        if (!_seekDrag) _fraction = _status.Fraction;
        if (_volume.Visible) _volume.SetValue(VolumePercent(), _status.Muted);
        Invalidate();
    }

    private int VolumePercent() => (int)Math.Round(Math.Clamp(_status.Volume, 0, 100));

    /// <summary>Remembers mpv's track list; the selected flags become the check marks in the pickers.</summary>
    public void SetTracks(IReadOnlyList<MpvTrack> tracks)
    {
        _tracks = tracks;
        _audioId = tracks.FirstOrDefault(track => track.IsAudio && track.Selected)?.Id;
        _subtitleId = tracks.FirstOrDefault(track => track.IsSubtitle && track.Selected)?.Id;

        // A track list that arrives while its own picker is open should refresh, not go stale.
        if (_menu.Visible && _openMenu is MenuKind.Audio or MenuKind.Subtitles) OpenMenu(_openMenu);
    }

    public void SetFullscreen(bool fullscreen)
    {
        _fullscreen.Checked = fullscreen;
        _fullscreen.Glyph = fullscreen ? Glyphs.ExitFullscreen : Glyphs.Fullscreen;
        _tips.SetToolTip(_fullscreen, fullscreen ? "退出全屏" : "全屏");
    }

    public void End()
    {
        DismissPopups();
        Visible = false;
        _generation++;
        ResetThumbnails();

        _hasStatus = false;
        _status = new PlayerStatus();
        _fraction = 0;
        _itemId = "";
        _chapters = [];
        _episodeList = [];
        _tracks = [];
        _episodes.Visible = false;
    }

    /// <summary>Closes every panel; the shell calls this as the bar fades or the window moves.</summary>
    public void DismissPopups()
    {
        _preview.Dismiss();
        _menu.Dismiss();
        _volume.Dismiss();
    }

    /// <summary>
    /// Keys the shell hands over while a panel is open: the panels are never activated, so no key
    /// message ever reaches them on its own. Returns true when the key was consumed.
    /// </summary>
    public bool HandleKey(Keys key)
    {
        if (!PopupOpen) return false;

        switch (key)
        {
            case Keys.Escape:
                DismissPopups();
                return true;
            case Keys.Up when _menu.Visible:
                _menu.MoveHighlight(-1);
                return true;
            case Keys.Down when _menu.Visible:
                _menu.MoveHighlight(1);
                return true;
            case Keys.Enter when _menu.Visible:
                _menu.ChooseHighlighted();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Wheel notches forwarded by the shell, for the same reason as <see cref="HandleKey"/>.</summary>
    public bool ScrollOpenMenu(int wheelDelta) => _menu.Visible && _menu.ScrollBy(wheelDelta);

    /// <summary>
    /// Each panel the bar can open, paired with the call that opens it. For <c>--self-check</c> only:
    /// the panels are windows of their own, so painting the bar never runs their paint code, and
    /// nothing else can reach it without a real playback session.
    /// <para>
    /// They are turned fully transparent first. A panel keeps itself inside the monitor's working
    /// area (<see cref="PlayerFlyout.Place"/>), so opening one from a window parked off-screen would
    /// still flash it onto the user's desktop, while <c>DrawToBitmap</c> paints through the window
    /// procedure and does not care about opacity. Nothing is restored: the bar belongs to the
    /// self-check window and is disposed with it.
    /// </para>
    /// </summary>
    internal IReadOnlyList<(string Key, Func<Form> Open)> SelfCheckPanels()
    {
        foreach (var panel in new Form[] { _menu, _volume, _preview }) panel.Opacity = 0;

        return
        [
            ("episodes", () => OpenForSelfCheck(MenuKind.Episodes)),
            ("audio", () => OpenForSelfCheck(MenuKind.Audio)),
            ("subtitles", () => OpenForSelfCheck(MenuKind.Subtitles)),
            ("shaders", () => OpenForSelfCheck(MenuKind.Shaders)),
            ("volume", () =>
            {
                // ShowVolume backs off while a menu is open, which the previous probe left behind.
                DismissPopups();
                ShowVolume();
                return _volume;
            }),
            ("preview", () =>
            {
                DismissPopups();
                ShowPreview(Width / 2);
                return _preview;
            })
        ];

        Form OpenForSelfCheck(MenuKind kind)
        {
            OpenMenu(kind);
            return _menu;
        }
    }

    private void OpenMenu(MenuKind kind)
    {
        FlatButton anchor;
        string title;
        IReadOnlyList<PlayerMenuItem> items;

        switch (kind)
        {
            case MenuKind.Episodes:
                anchor = _episodes;
                title = "选集";
                items = EpisodeItems();
                break;
            case MenuKind.Audio:
                anchor = _audio;
                title = "音轨";
                items = AudioItems();
                break;
            case MenuKind.Subtitles:
                anchor = _subtitles;
                title = "字幕";
                items = SubtitleItems();
                break;
            case MenuKind.Shaders:
                anchor = _shaders;
                title = "着色器";
                items = ShaderItems();
                break;
            default:
                return;
        }

        _preview.Dismiss();
        _volume.Dismiss();
        _openMenu = kind;
        SetMenuChecked(kind);
        _menu.Present(this, RectangleToScreen(anchor.Bounds), title, items);
        RaisePopupVisibility();
    }

    private void OnMenuItemChosen(PlayerMenuItem item)
    {
        switch (_openMenu)
        {
            case MenuKind.Episodes when item.Value is EmbyItem episode:
                EpisodeRequested?.Invoke(episode);
                break;
            case MenuKind.Audio when item.Value is int id:
                _audioId = id;
                AudioTrackSelected?.Invoke(id);
                break;
            case MenuKind.Subtitles:
                _subtitleId = item.Value as int?;
                SubtitleTrackSelected?.Invoke(_subtitleId);
                break;
            case MenuKind.Shaders:
                _shaderChoice = item.Value as ShaderProfile;
                ShaderRequested?.Invoke(_shaderChoice);
                break;
        }
    }

    private void OnPanelDismissed()
    {
        SetMenuChecked(MenuKind.None);
        RaisePopupVisibility();
    }

    private void SetMenuChecked(MenuKind kind)
    {
        _episodes.Checked = kind == MenuKind.Episodes;
        _audio.Checked = kind == MenuKind.Audio;
        _subtitles.Checked = kind == MenuKind.Subtitles;
        _shaders.Checked = kind == MenuKind.Shaders;
    }

    private void RaisePopupVisibility() => PopupVisibilityChanged?.Invoke(PopupOpen);

    private void ShowVolume()
    {
        if (_menu.Visible) return;

        _preview.Dismiss();
        _volume.Present(this, RectangleToScreen(_speaker.Bounds), VolumePercent(), _status.Muted);
        RaisePopupVisibility();
    }

    private IReadOnlyList<PlayerMenuItem> EpisodeItems() =>
    [
        .. _episodeList.Select(episode => new PlayerMenuItem(
            EpisodeText(episode),
            episode.EpisodeCode.Length > 0 ? episode.EpisodeCode : null,
            string.Equals(episode.Id, _itemId, StringComparison.Ordinal),
            episode))
    ];

    private IReadOnlyList<PlayerMenuItem> AudioItems() =>
    [
        .. _tracks.Where(track => track.IsAudio)
            .Select((track, index) => new PlayerMenuItem(
                TrackText(index + 1, track), null, track.Id == _audioId, track.Id))
    ];

    private IReadOnlyList<PlayerMenuItem> SubtitleItems()
    {
        var items = new List<PlayerMenuItem> { new("不使用", null, _subtitleId is null) };
        items.AddRange(_tracks.Where(track => track.IsSubtitle)
            .Select((track, index) => new PlayerMenuItem(
                TrackText(index + 1, track), null, track.Id == _subtitleId, track.Id)));
        return items;
    }

    private IReadOnlyList<PlayerMenuItem> ShaderItems()
    {
        var items = new List<PlayerMenuItem> { new("自动", null, _shaderChoice is null) };
        items.AddRange(_shaderList.Select(profile => new PlayerMenuItem(
            profile.Name,
            profile.Description,
            _shaderChoice is not null && string.Equals(profile.Name, _shaderChoice.Name, StringComparison.OrdinalIgnoreCase),
            profile)));
        return items;
    }

    private static string TrackText(int number, MpvTrack track) =>
        track.DisplayLabel.Length > 0 ? $"{number} · {track.DisplayLabel}" : $"轨道 {number}";

    private static string EpisodeText(EmbyItem episode)
    {
        var name = episode.Name ?? "";
        if (name.Length == 0) return episode.EpisodeCode.Length > 0 ? episode.EpisodeCode : "未命名";
        return name.Length > 24 ? name[..24] + "…" : name;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutBar();
    }

    /// <summary>
    /// One row under the progress strip: transport and clock on the left, everything that opens a
    /// panel on the right. Called directly as well as from resize, because 「选集」 appears and
    /// disappears with the item.
    /// </summary>
    private void LayoutBar()
    {
        var margin = Dpi.Scale(this, 16);
        var button = Dpi.Scale(this, 34);
        var gap = Dpi.Scale(this, 4);
        var top = SeekArea().Bottom + Dpi.Scale(this, 3);

        var left = margin;
        foreach (var control in new[] { _play, _back, _forward })
        {
            control.SetBounds(left, top, button, button);
            left = control.Right + gap;
        }

        _close.SetBounds(Width - margin - button, top, button, button);
        _fullscreen.SetBounds(_close.Left - gap - button, top, button, button);

        var right = _fullscreen.Left - gap;
        foreach (var control in new[] { _stats, _shaders, _subtitles, _audio, _episodes })
        {
            if (!control.Visible) continue;
            control.AutoSizeToContent(horizontalPadding: 12);
            control.SetBounds(right - control.Width, top, control.Width, button);
            right = control.Left - gap;
        }

        _speaker.SetBounds(Math.Max(left, right - gap * 2 - button), top, button, button);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        PaintScrim(e.Graphics);
        PaintSeek(e.Graphics);
        PaintClock(e.Graphics);
    }

    /// <summary>
    /// A vertical gradient standing in for a scrim. <see cref="Form.Opacity"/> is uniform for the
    /// whole window, so the sense of the bar sinking into the video has to come from the colours
    /// rather than from per-pixel alpha, plus a hairline that gives the top edge a definite end.
    /// </summary>
    private void PaintScrim(Graphics graphics)
    {
        var bounds = new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height));
        using (var brush = new LinearGradientBrush(
                   bounds, Color.FromArgb(27, 30, 35), Color.FromArgb(11, 12, 14), LinearGradientMode.Vertical))
        {
            graphics.FillRectangle(brush, bounds);
        }

        using var pen = new Pen(Color.FromArgb(58, 63, 71));
        graphics.DrawLine(pen, 0, 0, bounds.Width, 0);
    }

    private void PaintSeek(Graphics graphics)
    {
        var track = TrackRect();
        var radius = Math.Max(1, track.Height / 2);
        Draw.Fill(graphics, track, radius, Color.FromArgb(110, 255, 255, 255));

        var cached = (int)(track.Width * Math.Clamp(_status.CacheFraction, 0, 1));
        if (cached > 0)
        {
            Draw.Fill(
                graphics,
                new Rectangle(track.X, track.Y, cached, track.Height),
                radius,
                Color.FromArgb(155, 255, 255, 255));
        }

        var filled = (int)(track.Width * Math.Clamp(_fraction, 0, 1));
        if (filled > 0)
        {
            Draw.Fill(graphics, new Rectangle(track.X, track.Y, filled, track.Height), radius, Palette.Accent);
        }

        PaintChapterTicks(graphics, track);

        var thumb = Dpi.Scale(this, _seekDrag ? 15 : _seekHover ? 13 : 10);
        Draw.Fill(
            graphics,
            new Rectangle(track.X + filled - thumb / 2, track.Y + (track.Height - thumb) / 2, thumb, thumb),
            thumb / 2,
            Palette.Text);
    }

    /// <summary>Chapter boundaries as notches cut out of the track, the way Emby's own player marks them.</summary>
    private void PaintChapterTicks(Graphics graphics, Rectangle track)
    {
        if (!_status.HasDuration || _chapters.Count < 2) return;

        var width = Math.Max(1, Dpi.Scale(this, 2));
        using var brush = new SolidBrush(Color.FromArgb(200, 14, 16, 20));
        foreach (var chapter in _chapters)
        {
            var seconds = chapter.StartSeconds;
            if (seconds <= 1 || seconds >= _status.Duration) continue;
            var x = track.X + (int)(track.Width * (seconds / _status.Duration));
            graphics.FillRectangle(brush, x - width / 2, track.Y, width, track.Height);
        }
    }

    private void PaintClock(Graphics graphics)
    {
        var left = _forward.Right + Dpi.Scale(this, 12);
        var right = _speaker.Left - Dpi.Scale(this, 12);
        if (right - left < Dpi.Scale(this, 40)) return;

        var text = _status.Loaded
            ? $"{Clock(_seekDrag ? _fraction * _status.Duration : _status.Position)} / {_status.DurationClock}"
            : "正在打开…";

        Draw.Text(
            graphics,
            text,
            Fonts.Small,
            _seekDrag ? Palette.Text : Palette.TextDim,
            new Rectangle(left, _play.Top, right - left, _play.Height),
            Draw.LeftMiddle);
    }

    private static string Clock(double seconds) => TimeFormat.Clock(TimeSpan.FromSeconds(Math.Max(0, seconds)));

    /// <summary>
    /// The strip along the top edge. It spans the whole width and is deliberately taller than the
    /// track it draws, so grabbing the progress bar does not demand pixel precision.
    /// </summary>
    private Rectangle SeekArea() => new(0, 0, Math.Max(1, Width), Dpi.Scale(this, 24));

    private Rectangle TrackRect()
    {
        var area = SeekArea();
        var margin = Dpi.Scale(this, 16);
        var thickness = Dpi.Scale(this, _seekDrag || _seekHover ? 6 : 4);
        return new Rectangle(
            area.X + margin,
            area.Y + (area.Height - thickness) / 2,
            Math.Max(1, area.Width - margin * 2),
            thickness);
    }

    private double FractionAt(int x)
    {
        var track = TrackRect();
        return Math.Clamp((x - track.X) / (double)Math.Max(1, track.Width), 0, 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !SeekArea().Contains(e.Location)) return;

        _menu.Dismiss();
        _volume.Dismiss();
        _seekDrag = true;
        _seekHover = true;
        Capture = true;
        _fraction = FractionAt(e.X);
        ShowPreview(e.X);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_seekDrag)
        {
            _fraction = FractionAt(e.X);
            ShowPreview(e.X);
            Invalidate();
            return;
        }

        var inside = SeekArea().Contains(e.Location);
        if (inside != _seekHover)
        {
            _seekHover = inside;
            Cursor = inside ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        if (inside) ShowPreview(e.X);
        else _preview.Dismiss();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_seekDrag) return;

        _seekDrag = false;
        Capture = false;
        Invalidate();

        if (_status.HasDuration) SeekRequested?.Invoke(_fraction * _status.Duration);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_seekDrag) return;

        _seekHover = false;
        Cursor = Cursors.Default;
        _preview.Dismiss();
        Invalidate();
    }

    private void ShowPreview(int clientX)
    {
        if (!_status.HasDuration)
        {
            _preview.Dismiss();
            return;
        }

        var track = TrackRect();
        _hoverSeconds = FractionAt(clientX) * _status.Duration;
        _hoverScreenX = PointToScreen(new Point(Math.Clamp(clientX, track.X, track.Right), 0)).X;
        PresentPreview();
    }

    private void PresentPreview()
    {
        var chapter = ChapterAt(_hoverSeconds);
        Image? image = null;
        if (chapter >= 0)
        {
            if (_thumbnails.TryGetValue(chapter, out var cached)) image = cached;
            else RequestThumbnail(chapter);
        }

        _preview.Present(
            Owner ?? this,
            new Point(_hoverScreenX, RectangleToScreen(SeekArea()).Top),
            Clock(_hoverSeconds),
            chapter >= 0 ? ChapterLabel(chapter) : "",
            image);
    }

    /// <summary>Index of the chapter the given second falls in, or -1 when the item has none.</summary>
    private int ChapterAt(double seconds)
    {
        var found = -1;
        for (var index = 0; index < _chapters.Count; index++)
        {
            if (_chapters[index].StartSeconds > seconds + 0.5) break;
            found = index;
        }

        return found;
    }

    private string ChapterLabel(int index)
    {
        var name = (_chapters[index].Name ?? "").Trim();
        if (name.Length == 0) return $"章节 {index + 1}";
        return name.Length > 16 ? name[..16] + "…" : name;
    }

    private void RequestThumbnail(int index)
    {
        if (_itemId.Length == 0 || _thumbnailLifetime is null) return;
        if (!_chapters[index].HasImage || !_requested.Add(index)) return;

        _ = LoadThumbnailAsync(_itemId, index, _chapters[index].ImageTag, _generation, _thumbnailLifetime.Token);
    }

    private async Task LoadThumbnailAsync(
        string itemId,
        int index,
        string? tag,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _images
                .GetChapterAsync(itemId, index, tag, ThumbnailRequestWidth, cancellationToken)
                .ConfigureAwait(true);

            if (IsDisposed || generation != _generation) return;

            if (bytes is null)
            {
                // Remember the miss: a chapter the server will not render should not be asked twice.
                _thumbnails[index] = null;
                return;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            _thumbnails[index] = new Bitmap(decoded);

            // The pointer is probably still on that chapter; swap the still in without a mouse move.
            if (_preview.Visible && ChapterAt(_hoverSeconds) == index) PresentPreview();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, $"章节缩略图加载失败（{itemId}/{index}）", error);
        }
    }

    private void ResetThumbnails()
    {
        _thumbnailLifetime?.Cancel();
        _thumbnailLifetime?.Dispose();
        _thumbnailLifetime = null;

        // The preview only borrows the bitmap, so it has to let go before anything is freed.
        _preview.Dismiss();
        foreach (var image in _thumbnails.Values) image?.Dispose();
        _thumbnails.Clear();
        _requested.Clear();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) DismissPopups();
    }

    /// <summary>The panels are placed in screen coordinates, so a bar that moves leaves them stranded.</summary>
    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        DismissPopups();
    }

    private static FlatButton IconButton(string glyph) => new()
    {
        Variant = ButtonVariant.Ghost,
        Glyph = glyph,
        Text = "",
        CornerRadius = 6
    };

    private static FlatButton TextButton(string text) => new()
    {
        Variant = ButtonVariant.Ghost,
        Text = text,
        CornerRadius = 6
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ResetThumbnails();
            _menu.Dispose();
            _volume.Dispose();
            _preview.Dispose();
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }
}
