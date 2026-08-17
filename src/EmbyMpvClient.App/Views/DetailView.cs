using System.Diagnostics;
using System.Text;
using EmbyMpvClient.App.Controls;
using EmbyMpvClient.App.Theme;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Emby;
using EmbyMpvClient.Infrastructure;

namespace EmbyMpvClient.App.Views;

/// <summary>
/// One item in full: artwork, synopsis, what mpv is about to be told to do, and — for a series —
/// the season picker and episode grid. Deliberately not scrollable: the header is capped and the
/// episode grid takes the rest, so a nested scrolling region never fights the page.
/// </summary>
public sealed class DetailView : AppView
{
    private const string Category = "detail";

    private readonly Poster _poster;
    private readonly TextBlock _title = new("", Fonts.Display, Palette.Text);
    private readonly TextBlock _meta = new("", Fonts.Small, Palette.TextDim);
    private readonly TextBlock _tagline = new("", Fonts.Small, Palette.TextFaint);
    private readonly TextBlock _overview = new("", Fonts.Body, Palette.TextDim);

    private readonly FlatButton _play = new() { Variant = ButtonVariant.Primary, Glyph = Glyphs.Play, Text = "播放" };
    private readonly FlatButton _watched = new() { Variant = ButtonVariant.Secondary, Glyph = Glyphs.Check, Text = "标记已观看" };
    private readonly FlatButton _favourite = new() { Variant = ButtonVariant.Secondary, Glyph = Glyphs.Heart, Text = "收藏" };
    private readonly FlatButton _openInBrowser = new() { Variant = ButtonVariant.Ghost, Glyph = Glyphs.OpenExternal, Text = "网页端" };

    private readonly Card _options = new() { Title = "播放选项" };
    private readonly FieldRow _sourceRow;
    private readonly FieldRow _audioRow;
    private readonly FieldRow _subtitleRow;
    private readonly DropDown _source = new();
    private readonly DropDown _audio = new();
    private readonly DropDown _subtitles = new();
    private readonly ToggleSwitch _resume = new();

    private readonly DropDown _seasons = new();
    private readonly TextBlock _episodeSummary = new("", Fonts.Small, Palette.TextDim);
    private readonly PosterGrid _episodes = new();

    private readonly Card _info = new() { Title = "媒体信息" };
    private readonly TextBlock _infoText = new("", Fonts.Small, Palette.TextDim);

    private EmbyItem _target = new();
    private EmbyItem? _detail;
    private EmbyItem? _playTarget;
    private bool _loaded;

    public DetailView(IShell shell) : base(shell)
    {
        HeaderTitle = "详情";

        _poster = new Poster(Host.Images);

        _episodes.Bind(Host.Images);
        _episodes.CardWidth = 250;
        _episodes.Aspect = 16 / 9d;
        _episodes.ImageTypes = [EmbyImageStore.Primary, EmbyImageStore.Thumb];
        _episodes.ShowWatchedIndicators = Host.Settings.Ui.ShowWatchedIndicators;
        _episodes.EmptyMessage = "这一季还没有单集";
        _episodes.DescribeSubtitle = DescribeEpisode;
        _episodes.ItemActivated += episode => Run(_ => Shell.PlayAsync(episode, _detail ?? _target, episodes: _episodes.Items), "播放失败");
        _episodes.PlayRequested += episode => Run(_ => Shell.PlayAsync(episode, _detail ?? _target, episodes: _episodes.Items), "播放失败");
        _episodes.ItemContextRequested += episode => ItemMenu.Show(Shell, this, episode, _detail ?? _target, ReloadAsync);

        _seasons.Describe = value => value is EmbyItem season ? season.Name : value.ToString() ?? "";
        _seasons.SelectedIndexChanged += (_, _) =>
        {
            if (_seasons.SelectedItem is EmbyItem season) Run(token => LoadEpisodesAsync(season, token), "加载单集失败");
        };

        _play.Click += (_, _) => PlayWithOptions();
        _watched.Click += (_, _) => ToggleWatched();
        _favourite.Click += (_, _) => ToggleFavourite();
        _openInBrowser.Click += (_, _) => OpenInBrowser();

        _source.Describe = DescribeSource;
        _source.SelectedIndexChanged += (_, _) =>
        {
            if (_source.SelectedItem is MediaSource source) FillTracks(source);
        };
        _audio.Describe = Label;
        _subtitles.Describe = Label;

        _info.Controls.Add(_infoText);

        _sourceRow = new FieldRow("媒体源", _source) { LabelWidth = 76 };
        _audioRow = new FieldRow("音轨", _audio) { LabelWidth = 76 };
        _subtitleRow = new FieldRow("字幕", _subtitles) { LabelWidth = 76 };
        _options.Controls.Add(_sourceRow);
        _options.Controls.Add(_audioRow);
        _options.Controls.Add(_subtitleRow);
        _options.Controls.Add(_resume);

        foreach (var child in new Control[]
                 {
                     _poster, _title, _meta, _tagline, _overview, _play, _watched, _favourite,
                     _openInBrowser, _seasons, _episodeSummary, _episodes, _options, _info
                 })
            Controls.Add(child);
    }

    /// <summary>The item to show; the shell sets it before every navigation to this page.</summary>
    public EmbyItem Target
    {
        get => _target;
        set
        {
            if (_loaded && _target.Id == value.Id) return;
            _target = value;
            _detail = null;
            _playTarget = null;
            _loaded = false;
        }
    }

    public override Task EnterAsync() => _loaded ? Task.CompletedTask : ReloadAsync();

    public override Task RefreshAsync()
    {
        _loaded = false;
        return ReloadAsync();
    }

    private Task ReloadAsync() => RunAsync(async cancellationToken =>
    {
        Shell.Busy();
        try
        {
            var detail = await Host.Session
                .ExecuteAsync((client, token) => client.GetItemAsync(_target.Id, token), cancellationToken)
                .ConfigureAwait(true);

            _detail = detail;
            _loaded = true;
            HeaderTitle = detail.Name;
            Apply(detail);

            if (detail.Type == EmbyItemType.Series)
            {
                await LoadSeriesAsync(detail, cancellationToken).ConfigureAwait(true);
            }
            else if (detail.Type == EmbyItemType.Season)
            {
                _seasons.Visible = false;
                await LoadEpisodesAsync(detail, cancellationToken).ConfigureAwait(true);
            }

            LayoutPage();
        }
        finally
        {
            Shell.Idle();
        }
    }, "加载详情失败");

    private void Apply(EmbyItem item)
    {
        var isSeries = item.Type is EmbyItemType.Series or EmbyItemType.Season;

        _poster.Show(item);
        _title.Text = item.Name;
        _meta.Text = DescribeMeta(item);
        _tagline.Text = item.Genres.Count > 0 ? string.Join(" · ", item.Genres.Take(6)) : "";
        _overview.Text = item.Overview is { Length: > 0 } overview ? overview : "暂无简介。";

        _playTarget = item.IsPlayable ? item : null;
        _play.Visible = item.IsPlayable;
        _play.Text = item.HasResumePosition ? $"继续播放 {TimeFormat.Clock(item.ResumeTicks)}" : "播放";

        _options.Visible = item.IsPlayable && !isSeries && item.MediaSources.Count > 0;
        if (_options.Visible) FillOptions(item);

        var played = item.UserData?.Played == true;
        _watched.Text = played ? "标记未观看" : "标记已观看";
        _watched.Checked = played;

        var favourite = item.UserData?.IsFavorite == true;
        _favourite.Text = favourite ? "已收藏" : "收藏";
        _favourite.Glyph = favourite ? Glyphs.HeartFilled : Glyphs.Heart;
        _favourite.Checked = favourite;

        _seasons.Visible = item.Type == EmbyItemType.Series;
        _episodes.Visible = isSeries;
        _episodeSummary.Visible = isSeries;
        _info.Visible = !isSeries;
        if (!isSeries) _infoText.Text = DescribeMedia(item);
    }

    private async Task LoadSeriesAsync(EmbyItem series, CancellationToken cancellationToken)
    {
        var seasons = await Host.Session
            .ExecuteAsync((client, token) => client.GetSeasonsAsync(series.Id, token), cancellationToken)
            .ConfigureAwait(true);

        if (seasons.Count == 0)
        {
            _seasons.Visible = false;
            await LoadEpisodesAsync(null, cancellationToken).ConfigureAwait(true);
            return;
        }

        // Pick the season holding the next unwatched episode, so 「播放」 continues the show.
        var target = seasons.FirstOrDefault(season => season.UserData?.UnplayedItemCount > 0) ?? seasons[0];
        _seasons.Visible = seasons.Count > 1;
        _seasons.Fill(seasons, target);

        // Filling the list raises SelectedIndexChanged only when the index actually moves.
        if (!ReferenceEquals(_seasons.SelectedItem, target) || _episodes.Items.Count == 0)
            await LoadEpisodesAsync(target, cancellationToken).ConfigureAwait(true);
    }

    private Task LoadEpisodesAsync(EmbyItem? season, CancellationToken cancellationToken)
    {
        var series = _detail ?? _target;
        var seriesId = series.Type == EmbyItemType.Series ? series.Id : series.SeriesId ?? series.Id;
        var seasonId = season?.Id ?? (series.Type == EmbyItemType.Season ? series.Id : null);

        return RunEpisodesAsync(seriesId, seasonId, cancellationToken);
    }

    private async Task RunEpisodesAsync(string seriesId, string? seasonId, CancellationToken cancellationToken)
    {
        var episodes = await Host.Session
            .ExecuteAsync((client, token) => client.GetEpisodesAsync(seriesId, seasonId, token), cancellationToken)
            .ConfigureAwait(true);

        _episodes.SetItems(episodes);

        var unwatched = episodes.FirstOrDefault(episode => episode.UserData?.Played != true) ?? episodes.FirstOrDefault();
        _playTarget = unwatched;
        _play.Visible = unwatched is not null;
        _play.Text = unwatched is null
            ? "播放"
            : unwatched.HasResumePosition
                ? $"继续 {unwatched.EpisodeCode} · {TimeFormat.Clock(unwatched.ResumeTicks)}"
                : $"播放 {unwatched.EpisodeCode}";

        var played = episodes.Count(episode => episode.UserData?.Played == true);
        _episodeSummary.Text = episodes.Count == 0
            ? ""
            : $"{episodes.Count} 集，已看 {played} 集";

        LayoutPage();
    }

    /// <summary>
    /// Starts playback with what the options card currently holds; an episode keeps the
    /// planner's defaults so the episode grid stays one click per episode.
    /// </summary>
    private void PlayWithOptions()
    {
        if (_playTarget is not { } target) return;

        var parent = target.Type == EmbyItemType.Episode ? _detail ?? _target : null;

        if (_options.Visible && target.MediaSources.Count > 0)
        {
            var source = _source.SelectedItem as MediaSource ?? target.MediaSources[0];
            var audio = _audio.SelectedItem as TrackOption;
            var subtitle = _subtitles.SelectedItem as TrackOption;

            var choice = new PlaybackChoice(
                source,
                audio?.Stream?.Index,
                subtitle?.Stream?.Index,
                subtitle?.Disable ?? false,
                _resume.Visible && _resume.Checked ? target.ResumeTicks : 0);

            Run(_ => Shell.PlayAsync(target, parent, choice, _episodes.Items), "播放失败");
            return;
        }

        Run(_ => Shell.PlayAsync(target, parent, episodes: _episodes.Items), "播放失败");
    }

    private void FillOptions(EmbyItem item)
    {
        var source = item.MediaSources[0];
        _sourceRow.Visible = item.MediaSources.Count > 1;
        _source.Fill(item.MediaSources, source);
        FillTracks(source);

        _resume.Text = item.HasResumePosition
            ? $"从 {TimeFormat.Clock(item.ResumeTicks)} 继续播放"
            : "从上次的位置继续";
        _resume.Description = "关闭则从头开始播放";
        _resume.Visible = item.HasResumePosition && Host.Settings.Playback.ResumeFromSavedPosition;
        _resume.SetCheckedSilently(true);
    }

    /// <summary>A track, or one of the two "no explicit track" answers.</summary>
    private sealed record TrackOption(string Text, MediaStream? Stream, bool Disable);

    private static string Label(object value) => value is TrackOption option ? option.Text : value.ToString() ?? "";

    private static string DescribeSource(object value) => value switch
    {
        MediaSource source => DescribeSourceLabel(source),
        _ => value.ToString() ?? ""
    };

    private static string DescribeSourceLabel(MediaSource source)
    {
        var quality = source.ToQualityLabel();
        var name = string.IsNullOrWhiteSpace(source.Name) ? Path.GetFileName(source.Path ?? "") : source.Name!;
        if (string.IsNullOrWhiteSpace(name)) return quality.Length > 0 ? quality : "默认媒体源";
        return quality.Length > 0 ? $"{name}  ·  {quality}" : name;
    }

    private void FillTracks(MediaSource source)
    {
        var audio = new List<object> { new TrackOption("默认（由 mpv 决定）", null, false) };
        audio.AddRange(source.AudioStreams.Select(stream => new TrackOption(stream.ToDisplayLabel(), stream, false)));
        _audio.Fill(audio, audio[0]);

        var subtitles = new List<object>
        {
            new TrackOption("默认（由 mpv 决定）", null, false),
            new TrackOption("不使用字幕", null, true)
        };
        subtitles.AddRange(source.SubtitleStreams.Select(stream => new TrackOption(stream.ToDisplayLabel(), stream, false)));
        _subtitles.Fill(subtitles, subtitles[0]);
    }

    private void ToggleWatched()
    {
        var item = _detail ?? _target;
        var played = item.UserData?.Played == true;

        Run(async token =>
        {
            await Host.Session
                .ExecuteAsync((client, inner) => played
                    ? client.MarkUnplayedAsync(item.Id, inner)
                    : client.MarkPlayedAsync(item.Id, inner), token)
                .ConfigureAwait(true);

            await ReloadAsync().ConfigureAwait(true);
        }, played ? "标记未观看失败" : "标记已观看失败");
    }

    private void ToggleFavourite()
    {
        var item = _detail ?? _target;
        var favourite = item.UserData?.IsFavorite == true;

        Run(async token =>
        {
            await Host.Session
                .ExecuteAsync((client, inner) => client.SetFavoriteAsync(item.Id, !favourite, inner), token)
                .ConfigureAwait(true);

            await ReloadAsync().ConfigureAwait(true);
        }, "更新收藏失败");
    }

    /// <summary>Opens Emby's own web page for the item — handy for editing metadata the client cannot.</summary>
    private void OpenInBrowser()
    {
        var item = _detail ?? _target;
        if (Host.Session.Connection is not { } connection)
        {
            Shell.Notify("尚未连接到服务器", ToastKind.Warning);
            return;
        }

        var root = connection.ApiBase.GetLeftPart(UriPartial.Authority) + connection.ApiBase.AbsolutePath.TrimEnd('/');
        if (root.EndsWith("/emby", StringComparison.OrdinalIgnoreCase)) root = root[..^"/emby".Length];

        var url = $"{root}/web/index.html#!/item?id={Uri.EscapeDataString(item.Id)}";
        if (item.ServerId is { Length: > 0 } serverId) url += $"&serverId={Uri.EscapeDataString(serverId)}";

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error)
        {
            Log.Warn(Category, "打开网页端失败", error);
            Shell.Notify($"打开浏览器失败：{Describe(error)}", ToastKind.Error);
        }
    }

    private static string DescribeMeta(EmbyItem item)
    {
        var parts = new List<string> { item.DisplayTypeName };

        if (item.Type == EmbyItemType.Episode && item.SeriesName is { Length: > 0 } series)
            parts.Add($"{series} {item.EpisodeCode}");

        if (item.ProductionYear is { } year) parts.Add(year.ToString());
        if (item.RunTimeTicks is > 0) parts.Add(TimeFormat.Duration(item.RunTimeTicks));
        if (item.OfficialRating is { Length: > 0 } rating) parts.Add(rating);
        if (item.CommunityRating is { } score) parts.Add($"评分 {score:0.0}");
        if (item.ChildCount is { } children && item.Type == EmbyItemType.Series) parts.Add($"{children} 季");

        return string.Join("  ·  ", parts);
    }

    private static string DescribeEpisode(EmbyItem episode)
    {
        var code = episode.EpisodeCode;
        var duration = TimeFormat.Duration(episode.RunTimeTicks);
        return code.Length > 0 && duration.Length > 0 ? $"{code} · {duration}" : code + duration;
    }

    /// <summary>The 「播放前会发生什么」 block: source, tracks and the shader group that will be applied.</summary>
    private string DescribeMedia(EmbyItem item)
    {
        var source = item.DefaultMediaSource;
        if (source is null) return "服务器没有返回可播放的媒体源。";

        var text = new StringBuilder();
        text.AppendLine(source.ToQualityLabel());

        if (source.PrimaryVideoStream is { } video) text.AppendLine($"视频：{video.ToDisplayLabel()}");

        text.AppendLine($"音轨：由 mpv 决定（共 {source.AudioStreams.Count()} 条）");
        text.AppendLine($"字幕：由 mpv 决定（共 {source.SubtitleStreams.Count()} 条）");

        var decision = Host.Shaders.Resolve(item, source, item.Type == EmbyItemType.Episode ? _detail : null);
        text.AppendLine(decision.HasProfile
            ? $"着色器：{decision.Profile}（{decision.Reason}）"
            : $"着色器：不额外指定（{decision.Reason}）");

        if (item.Tags.Count > 0) text.AppendLine($"标签：{string.Join("、", item.Tags.Take(8))}");
        if (source.Path is { Length: > 0 } path) text.Append($"路径：{path}");

        return text.ToString();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutPage();
    }

    private void LayoutPage()
    {
        var padding = Dpi.Scale(this, 20);
        var gap = Dpi.Scale(this, 8);
        var posterWidth = Dpi.Scale(this, 180);
        var posterHeight = (int)(posterWidth / (2 / 3d));
        var right = Math.Max(Dpi.Scale(this, 120), Width - padding * 2 - posterWidth - Dpi.Scale(this, 20));
        var x = padding + posterWidth + Dpi.Scale(this, 20);

        _poster.SetBounds(padding, padding, posterWidth, posterHeight);

        var y = padding;
        _title.SetBounds(x, y, right, 0);
        _title.FitHeight(Dpi.Scale(this, 70));
        y = _title.Bottom + Dpi.Scale(this, 4);

        _meta.SetBounds(x, y, right, Dpi.Scale(this, 18));
        y = _meta.Bottom + Dpi.Scale(this, 2);

        _tagline.Visible = _tagline.Text.Length > 0;
        if (_tagline.Visible)
        {
            _tagline.SetBounds(x, y, right, Dpi.Scale(this, 18));
            y = _tagline.Bottom;
        }

        y += Dpi.Scale(this, 10);
        var buttonHeight = Dpi.Scale(this, 34);
        var buttonLeft = x;

        foreach (var button in new[] { _play, _watched, _favourite, _openInBrowser })
        {
            if (!button.Visible) continue;
            button.AutoSizeToContent(minimumWidth: Dpi.Scale(this, 92));

            if (buttonLeft + button.Width > x + right && buttonLeft > x)
            {
                buttonLeft = x;
                y += buttonHeight + gap;
            }

            button.SetBounds(buttonLeft, y, button.Width, buttonHeight);
            buttonLeft = button.Right + gap;
        }

        y += buttonHeight + Dpi.Scale(this, 12);

        _overview.SetBounds(x, y, right, 0);
        _overview.FitHeight(Dpi.Scale(this, 96));

        var heroBottom = Math.Max(_poster.Bottom, _overview.Bottom) + Dpi.Scale(this, 18);

        if (_episodes.Visible)
        {
            var rowHeight = Dpi.Scale(this, 28);
            if (_seasons.Visible)
            {
                _seasons.SetBounds(padding, heroBottom, Dpi.Scale(this, 200), rowHeight);
                _episodeSummary.SetBounds(_seasons.Right + Dpi.Scale(this, 12), heroBottom + Dpi.Scale(this, 4), Dpi.Scale(this, 220), rowHeight);
            }
            else
            {
                _episodeSummary.SetBounds(padding, heroBottom, Dpi.Scale(this, 220), rowHeight);
            }

            var top = heroBottom + rowHeight + Dpi.Scale(this, 6);
            _episodes.SetBounds(0, top, Width, Math.Max(Dpi.Scale(this, 60), Height - top));
        }

        if (!_options.Visible && !_info.Visible) return;

        var cardsTop = heroBottom;
        var cardGap = Dpi.Scale(this, 10);
        var cardWidth = Math.Max(Dpi.Scale(this, 200), Width - padding * 2);

        if (_options.Visible)
        {
            _options.SetBounds(padding, cardsTop, cardWidth, Dpi.Scale(this, 60));
            LayoutOptionsCard();
            cardsTop = _options.Bottom + cardGap;
        }

        if (!_info.Visible) return;

        _info.SetBounds(padding, cardsTop, cardWidth, Dpi.Scale(this, 60));
        var inner = _info.Width - Dpi.Scale(this, 32);
        _infoText.SetBounds(Dpi.Scale(this, 16), _info.ContentTop, Math.Max(Dpi.Scale(this, 100), inner), 0);
        _infoText.FitHeight();
        _info.Height = _infoText.Bottom + Dpi.Scale(this, 16);
    }

    private void LayoutOptionsCard()
    {
        var gap = Dpi.Scale(this, 6);
        var inner = _options.Width - Dpi.Scale(this, 32);
        var rowTop = _options.ContentTop;

        foreach (var row in new Control[] { _sourceRow, _audioRow, _subtitleRow, _resume })
        {
            if (!row.Visible) continue;

            if (row is FieldRow field) field.Editor.Height = Dpi.Scale(this, 30);

            var height = row switch
            {
                FieldRow fieldRow => fieldRow.PreferredHeight(),
                ToggleSwitch toggle => toggle.PreferredHeight(),
                _ => row.Height
            };

            row.SetBounds(Dpi.Scale(this, 16), rowTop, inner, height);
            rowTop = row.Bottom + gap;
        }

        _options.Height = rowTop + Dpi.Scale(this, 16);
    }

    /// <summary>The item's primary artwork, fetched once per item and drawn to fill its box.</summary>
    private sealed class Poster : Control
    {
        private static readonly Font PlaceholderFont = Fonts.IconAt(30);

        private readonly EmbyImageStore _images;
        private readonly CancellationTokenSource _lifetime = new();
        private Image? _image;
        private string _itemId = "";
        private string _glyph = Glyphs.Movie;

        public Poster(EmbyImageStore images)
        {
            _images = images;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Surface;
        }

        public void Show(EmbyItem item)
        {
            _glyph = item.Type switch
            {
                EmbyItemType.Series or EmbyItemType.Season or EmbyItemType.Episode => Glyphs.Series,
                EmbyItemType.MusicVideo => Glyphs.Music,
                _ => Glyphs.Movie
            };

            if (_itemId == item.Id) return;
            _itemId = item.Id;

            _image?.Dispose();
            _image = null;
            Invalidate();
            _ = FetchAsync(item);
        }

        private async Task FetchAsync(EmbyItem item)
        {
            try
            {
                var width = Math.Max(240, Width * 2);
                var bytes = await _images.GetAsync(item, EmbyImageStore.Primary, width, _lifetime.Token).ConfigureAwait(true);
                if (bytes is null || _itemId != item.Id || IsDisposed) return;

                using var stream = new MemoryStream(bytes, writable: false);
                using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
                _image = new Bitmap(decoded);
                Invalidate();
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception error)
            {
                Log.Warn(Category, $"海报加载失败：{item.Name}", error);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Draw.Smooth(e.Graphics);
            var bounds = new Rectangle(0, 0, Width, Height);
            var radius = Dpi.Scale(this, 10);

            if (_image is not null)
            {
                Draw.ImageCover(e.Graphics, _image, bounds, radius);
                return;
            }

            Draw.Fill(e.Graphics, bounds, radius, Palette.Surface);
            Draw.Border(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), radius, Palette.Border);
            Draw.Text(e.Graphics, _glyph, PlaceholderFont, Palette.TextFaint, bounds, Draw.Centered);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _lifetime.Cancel();
                _lifetime.Dispose();
                _image?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
