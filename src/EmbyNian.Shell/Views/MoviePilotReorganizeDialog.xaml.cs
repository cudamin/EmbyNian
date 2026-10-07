using System.Collections.ObjectModel;
using EmbyNian.Diagnostics;
using EmbyNian.MoviePilot;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>原记录、表单、只读预览和确认在同一张对话框中，避免嵌套 ContentDialog 吞掉确认。</summary>
public sealed partial class MoviePilotReorganizeDialog : ContentDialog
{
    private readonly MoviePilotService _service;
    private readonly MoviePilotTransferContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<MoviePilotTransferHistory> _histories = [];
    private IReadOnlyList<MoviePilotTransferHistory> _relatedHistories = [];
    private string? _submissionProblem;
    private MoviePilotTransferOptions? _options;
    private MoviePilotTransferPreview? _preview;
    private bool _initialized;
    private bool _updating;
    private bool _busy;
    private bool _submitting;
    private bool _submitted;
    private int _revision;

    public ObservableCollection<MoviePilotTransferLine> Lines { get; } = [];

    internal MoviePilotReorganizeDialog(MoviePilotService service, MoviePilotTransferContext context)
    {
        _service = service;
        _context = context;
        InitializeComponent();
        RequestedTheme = ThemeHost.Current.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        Title = $"手动重新整理 — {context.Title}";
        PrimaryButtonText = "确认重新整理";
        SecondaryButtonText = "确认加入队列";
        CloseButtonText = "关闭";
        SourcePathBox.Text = string.Join("\n", context.Files.Select(file => file.Path));
        HistoryQuery.Text = context.Title;
        TypeBox.SelectedIndex = context.Type == MoviePilotTransferRequest.TypeSeries ? 1 : 0;
        TransferTypeBox.SelectedIndex = 0;
        SeasonBox.Text = context.Season?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        EpisodeBox.Text = context.Episode;
        MediaIdBox.Text = context.MediaId;
        TargetPathBox.RegisterPropertyChangedCallback(ComboBox.TextProperty, (_, _) => Edited());
        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
        Closing += (_, args) => { if (_submitting) args.Cancel = true; };
        Closed += (_, _) => _lifetime.Cancel();
        _initialized = true;
        EpisodeRow.Visibility = context.Type == MoviePilotTransferRequest.TypeSeries ? Visibility.Visible : Visibility.Collapsed;
        UpdateSubmit();
    }

    private MoviePilotTransferRequest Request => new()
    {
        Files = _context.Files,
        Histories = _histories,
        TargetStorage = (TargetStorageBox.SelectedItem as MoviePilotStorage)?.Type ?? "",
        TargetPath = TargetPathBox.Text.Trim(),
        TransferType = (TransferTypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
        Type = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? MoviePilotTransferRequest.TypeMovie,
        MediaSource = (SourceBox.SelectedItem as MoviePilotMediaSource)?.Id ?? "",
        MediaId = MediaIdBox.Text.Trim(),
        Season = SeasonBox.Text.Trim(),
        Episode = EpisodeBox.Text.Trim(),
        Part = PartBox.Text.Trim(),
        MinimumSize = MinSizeBox.Text.Trim(),
        TypeFolder = TypeFolderToggle.IsChecked == true,
        CategoryFolder = CategoryFolderToggle.IsChecked == true,
        Scrape = ScrapeToggle.IsChecked == true,
        FromHistory = FromHistoryToggle.IsChecked == true,
        Reorganize = true
    };

    private async Task LoadAsync()
    {
        SetBusy(true);
        try
        {
            _options = await _service.TransferOptionsAsync(_lifetime.Token).ConfigureAwait(true);
            _updating = true;
            TargetStorageBox.ItemsSource = _options.Storages;
            SourceBox.ItemsSource = _options.MediaSources;
            var tmdb = _options.MediaSources.FirstOrDefault(source => source.Id == "themoviedb");
            SourceBox.SelectedItem = tmdb;
            if (tmdb is null) MediaIdBox.Text = "";
            TargetPathBox.ItemsSource = _options.Directories.Select(directory => directory.Path).Distinct().ToList();
            _updating = false;
            await FindHistoryAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Say(Failure.Describe(error)); }
        finally { _updating = false; SetBusy(false); }
    }

    private async Task FindHistoryAsync()
    {
        InvalidatePreview();
        _histories = [];
        _updating = true;
        HistoryBox.ItemsSource = null;
        OriginalSourceBox.Text = "";
        HistorySummary.Text = "正在查找原整理记录…";
        _updating = false;
        var found = await _service.FindTransferHistoriesAsync(HistoryQuery.Text, _lifetime.Token).ConfigureAwait(true);
        _relatedHistories = found;
        if (_context.Files.Count == 0) throw new MoviePilotException("此条目没有可整理的单个文件");
        if (_context.Files.Count == 1)
        {
            var candidates = MoviePilotTransferHistoryMatch.Candidates(found, _context.Files[0].Path);
            _updating = true;
            HistoryBox.ItemsSource = candidates;
            HistoryBox.SelectedIndex = candidates.Count == 1 ? 0 : -1;
            _updating = false;
            if (candidates.Count != 1)
            {
                HistorySummary.Text = candidates.Count == 0 ? "没有找到路径匹配的原记录" : "找到多条匹配记录，请选择原整理记录";
                if (candidates.Count == 0)
                    Say("请更换原片名或文件名查找，或在 MoviePilot 核对路径映射；不会把当前媒体文件重新当作下载源。");
                return;
            }
            _histories = [candidates[0]];
        }
        else
        {
            _histories = MoviePilotTransferHistoryMatch.RequireMatches(_context, found);
            _updating = true;
            HistoryBox.ItemsSource = _histories;
            HistoryBox.SelectedIndex = 0;
            HistoryBox.IsEnabled = false;
            _updating = false;
        }
        await ApplyHistoryAsync().ConfigureAwait(true);
    }

    private async Task ApplyHistoryAsync()
    {
        OriginalSourceBox.Text = string.Join("\n", _histories.Select(history => history.TransferPath));
        HistorySummary.Text = string.Join("\n", _histories.Select(history => history.Display));
        var dependents = MoviePilotTransferHistoryMatch.Dependents(_histories, _relatedHistories);
        _submissionProblem = dependents.Count == 0 ? null : "旧目标被其他软链接记录引用（" +
            string.Join("、", dependents.Select(history => $"#{history.Id}")) +
            "），重新整理会让它们失效。可预览，但请先在 MoviePilot 处理引用关系后再提交。";
        var matched = await _service.TransferHistoryTargetAsync(_histories, _lifetime.Token).ConfigureAwait(true);
        if (matched.Directories is [var directory]) ApplyDirectory(directory);
        else
        {
            Say("原记录已找到，但没有匹配到目的目录。请选择目的存储和路径，并核对分类选项。", InfoBarSeverity.Warning);
            return;
        }
        Say(_submissionProblem, InfoBarSeverity.Warning);
    }

    private void ApplyDirectory(MoviePilotDirectory directory)
    {
        _updating = true;
        TargetStorageBox.SelectedItem = _options?.Storages.FirstOrDefault(storage => storage.Type == directory.Storage);
        TargetPathBox.Text = directory.Path;
        TransferTypeBox.SelectedIndex = directory.TransferType switch { "copy" => 1, "move" => 2, "link" => 3, "softlink" => 4, _ => 0 };
        TypeFolderToggle.IsChecked = directory.TypeFolder;
        CategoryFolderToggle.IsChecked = directory.CategoryFolder;
        ScrapeToggle.IsChecked = directory.Scrape;
        _updating = false;
        InvalidatePreview();
    }

    private async void OnHistoryQuery(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_busy) return;
        SetBusy(true);
        try { await FindHistoryAsync().ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Say(Failure.Describe(error)); }
        finally { SetBusy(false); }
    }

    private async void OnHistoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updating || _context.Files.Count != 1 || HistoryBox.SelectedItem is not MoviePilotTransferHistory history) return;
        InvalidatePreview();
        _histories = [history];
        SetBusy(true);
        try { await ApplyHistoryAsync().ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Say(Failure.Describe(error)); }
        finally { SetBusy(false); }
    }

    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        if (_busy || _submitted) return;
        InvalidatePreview();
        var request = Request;
        if (_histories.Count == 0) { Say("请先找到并选择原整理记录"); return; }
        if (request.Problem is { } problem) { Say(problem); return; }
        var revision = _revision;
        SetBusy(true);
        Say(null);
        try
        {
            var files = await _service.TransferHistoryFilesAsync(request, _lifetime.Token).ConfigureAwait(true);
            var preview = await _service.TransferPreviewAsync(request, files, _lifetime.Token).ConfigureAwait(true);
            if (_lifetime.IsCancellationRequested || revision != _revision || request != Request) return;
            Fill(preview.Result);
            if (!preview.CanSubmit) { Say(preview.Result.Message.Length > 0 ? preview.Result.Message : "预览不完整、有失败或没有目标文件，请修正后重试"); return; }
            _preview = preview;
            if (_submissionProblem is not null) { Say(_submissionProblem, InfoBarSeverity.Warning); return; }
            ConfirmationText.Text = MoviePilotTransfer.Confirmation(request, preview.Result, background: false);
            ConfirmationPanel.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Say(Failure.Describe(error)); }
        finally { SetBusy(false); }
    }

    private async void OnPrimarySubmit(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        await SubmitAsync(background: false).ConfigureAwait(true);
    }

    private async void OnSecondarySubmit(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        await SubmitAsync(background: true).ConfigureAwait(true);
    }

    private async Task SubmitAsync(bool background)
    {
        if (_busy || _submitted || _submissionProblem is not null || ConfirmChanges.IsChecked != true || _preview is not { CanSubmit: true } preview) return;
        if (!preview.Matches(Request)) { InvalidatePreview(); Say("表单已改变，请重新预览"); return; }
        _submitting = true;
        _submitted = true;
        _preview = null;
        SetBusy(true);
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        try
        {
            var result = await _service.TransferSubmitAsync(preview, background, _lifetime.Token).ConfigureAwait(true);
            Fill(result);
            Say(result.Summary + (result.Message.Length > 0 ? $"。{result.Message}" : ""),
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (MoviePilotTransferBlockedException error)
        {
            _submitted = false;
            Say($"{error.Message}。本次没有提交整理请求。");
        }
        catch (MoviePilotOperationBlockedException error)
        {
            Say(error.Message);
        }
        catch (Exception error)
        {
            Say($"{Failure.Describe(error)}。请求可能已到达 MoviePilot，请先核对整理历史，不要重复提交。");
        }
        finally
        {
            _submitting = false;
            SetBusy(false);
        }
    }

    private void Fill(MoviePilotTransferResult result)
    {
        Lines.Clear();
        foreach (var line in result.Items) Lines.Add(line);
        PreviewList.Visibility = Lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewSummary.Text = result.Summary;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_lifetime.IsCancellationRequested) return;
            FormScroll.UpdateLayout();
            FormScroll.ChangeView(null, FormScroll.ScrollableHeight, null, disableAnimation: true);
        });
    }

    private void InvalidatePreview()
    {
        _revision++;
        _preview = null;
        Lines.Clear();
        PreviewList.Visibility = Visibility.Collapsed;
        PreviewSummary.Text = "";
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        ConfirmChanges.IsChecked = false;
        UpdateSubmit();
    }

    private void Edited()
    {
        if (!_initialized || _updating) return;
        InvalidatePreview();
        if (Request.Problem is { } problem) Say(problem);
        else Say(_submissionProblem, InfoBarSeverity.Warning);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        PreviewRing.IsActive = busy;
        FormPanel.IsEnabled = !busy && !_submitted;
        PreviewButton.IsEnabled = !busy && !_submitted && _histories.Count > 0;
        UpdateSubmit();
    }

    private void UpdateSubmit()
    {
        var enabled = !_busy && _submissionProblem is null && _preview is { CanSubmit: true } && ConfirmChanges.IsChecked == true;
        IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = enabled;
    }

    private void OnEdited(object sender, RoutedEventArgs e) => Edited();
    private void OnSelectionEdited(object sender, SelectionChangedEventArgs e) => Edited();
    private void OnConfirmed(object sender, RoutedEventArgs e) => UpdateSubmit();
    private void OnMediaIdChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_updating && args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) sender.ItemsSource = null;
        Edited();
    }

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        MediaIdBox.ItemsSource = null;
        if (!_updating) MediaIdBox.Text = "";
        EpisodeRow.Visibility = TypeBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        Edited();
    }

    private void OnDirectoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updating) return;
        var storage = (TargetStorageBox.SelectedItem as MoviePilotStorage)?.Type;
        var directory = _options?.Directories.FirstOrDefault(directory => directory.Path == TargetPathBox.SelectedItem as string && directory.Storage == storage);
        if (directory is not null) ApplyDirectory(directory);
        else Edited();
    }

    private async void OnMediaIdQuery(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_busy || args.ChosenSuggestion is not null || string.IsNullOrWhiteSpace(args.QueryText)) return;
        SetBusy(true);
        try
        {
            var found = await _service.SearchAsync(args.QueryText.Trim(), _lifetime.Token).ConfigureAwait(true);
            sender.ItemsSource = found.Where(media => media.CanSubscribe && media.Type == Request.Type)
                .Select(media => new MoviePilotMediaSuggestion(media)).ToList();
            if (found.Count == 0) Say("没有找到媒体，请填写正确的数据源编号");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Say(Failure.Describe(error)); }
        finally { SetBusy(false); }
    }

    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not MoviePilotMediaSuggestion suggestion) return;
        if (suggestion.Media.Type != Request.Type || !suggestion.Media.CanSubscribe ||
            _options?.MediaSources.FirstOrDefault(source => source.Id == suggestion.Media.MediaSource) is not { } source)
        {
            sender.ItemsSource = null;
            Say("这条建议与当前媒体类型或可用来源不一致，请重新查找");
            return;
        }
        _updating = true;
        SourceBox.SelectedItem = source;
        MediaIdBox.Text = suggestion.Media.MediaId ?? "";
        _updating = false;
        Edited();
    }

    private void Say(string? message, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        Problem.Severity = severity;
        Problem.Message = message ?? "";
        Problem.IsOpen = message is not null;
    }

    private sealed record MoviePilotMediaSuggestion(MoviePilotMedia Media)
    {
        public override string ToString() => $"{Media.Display} — {Media.MediaId}";
    }
}
