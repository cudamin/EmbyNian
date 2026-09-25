using System.Collections.ObjectModel;
using EmbyNian.Diagnostics;
using EmbyNian.MoviePilot;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 「手动整理」的表单。字段 meanings（哪个值合法、正文怎么拼）在 Core 的
/// <see cref="MoviePilotTransferRequest"/>；这里只把键读出来、把服务接上。
/// <para>
/// 次序是死的：打开时按源文件问一遍 MoviePilot（存储、数据源、目的路径匹配），用户改完按「预览」——
/// 服务器只算不动；预览全绿才放开「立即整理 / 加入整理队列」。「重新整理」开着时点立即整理再多问一次，
/// 因为它会清掉命中历史和旧目标。
/// </para>
/// <para>
/// 校验不弹窗：改动随时重算 <see cref="MoviePilotTransferRequest.Problem"/>，有话就在顶上那条 InfoBar 说，
/// 同时关掉提交键 —— 一个关了再抱怨的对话框已经把用户打的字扔了。
/// </para>
/// </summary>
public sealed partial class MoviePilotReorganizeDialog : ContentDialog
{
    private readonly MoviePilotService _service;
    private readonly MoviePilotTransferContext _context;
    private readonly ConfirmRequest _confirm;

    /// <summary>预览行。集合实例稳定，预览/提交整片重填。</summary>
    public ObservableCollection<MoviePilotTransferLine> Lines { get; } = [];

    /// <summary>预览成功留下的那一份：提交键的通行证，也是提交时原样回传的文件清单。</summary>
    private MoviePilotTransferPreview? _preview;

    /// <param name="confirm">
    /// <see cref="ConfirmRequest"/> 是内部的，构造也就只能 internal —— 这张表只在壳里弹，不对外。
    /// </param>
    internal MoviePilotReorganizeDialog(
        MoviePilotService service,
        MoviePilotTransferContext context,
        ConfirmRequest confirm)
    {
        InitializeComponent();

        // 同 MetadataDialog：对话框住在 XamlRoot 的浮层根上，从提出它的树里继承不到主题。
        RequestedTheme = ThemeHost.Current.IsDark ? ElementTheme.Dark : ElementTheme.Light;

        _service = service;
        _context = context;
        _confirm = confirm;

        Title = $"手动整理 — {context.Title}";
        PrimaryButtonText = "立即整理";
        SecondaryButtonText = "加入整理队列";
        CloseButtonText = "关闭";

        SourcePathBox.Text = context.Files.Count switch
        {
            0 => "（这个条目背后没有找到文件）",
            1 => context.Files[0].Path,
            _ => $"{context.Files[0].Path} 等 {context.Files.Count} 个文件"
        };

        TypeBox.SelectedIndex = context.Type == MoviePilotTransferRequest.TypeSeries ? 1 : 0;
        SeasonBox.Text = context.Season?.ToString() ?? "";
        EpisodeBox.Text = context.Episode;
        MediaIdBox.Text = context.MediaId;

        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;

        Opened += async (_, _) => await LoadOptionsAsync().ConfigureAwait(true);
    }

    /// <summary>表单此刻长什么样。提交和校验读的是同一份。</summary>
    private MoviePilotTransferRequest Request => new()
    {
        Files = _context.Files,
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
        TypeFolder = TypeFolderToggle.IsOn,
        CategoryFolder = CategoryFolderToggle.IsOn,
        Scrape = ScrapeToggle.IsOn,
        FromHistory = FromHistoryToggle.IsOn,
        Reorganize = ReorganizeToggle.IsOn
    };

    /// <summary>打开时那一趟：拉选项、按源路径匹配目的路径。失败在顶上说一句话，表单还能用。</summary>
    private async Task LoadOptionsAsync()
    {
        PreviewButton.IsEnabled = false;
        PreviewRing.IsActive = true;
        try
        {
            var options = await _service.TransferOptionsAsync(CancellationToken.None).ConfigureAwait(true);

            TargetStorageBox.ItemsSource = options.Storages;
            SourceBox.ItemsSource = options.MediaSources.Where(source =>
                source.Types.Count == 0 || source.Types.Contains(_context.Type)).ToList();
            SourceBox.SelectedIndex = 0;
            TargetPathBox.ItemsSource = options.Directories
                .Select(directory => directory.Path)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // 源文件先换成 MoviePilot 认的 FileItem，再让服务器按它匹配目的目录 —— 三样默认值都从那儿来。
            var file = await _service.TransferFileAsync(_context.Files[0].Path, CancellationToken.None)
                .ConfigureAwait(true);
            var matched = await _service.TransferTargetAsync(file, CancellationToken.None).ConfigureAwait(true);

            if (matched.Directories is [var directory])
            {
                var storage = options.Storages.FirstOrDefault(candidate => candidate.Type == directory.Storage);
                if (storage is not null) TargetStorageBox.SelectedItem = storage;
                TargetPathBox.Text = directory.Path;
                TransferTypeBox.SelectedIndex = directory.TransferType switch
                {
                    "copy" => 1,
                    "move" => 2,
                    "link" => 3,
                    "softlink" => 4,
                    _ => 0
                };
                ScrapeToggle.IsOn = false;
            }

            Say(null);
        }
        catch (Exception error)
        {
            Say($"连不上 MoviePilot 或它不认识这个文件：{Failure.Describe(error)}");
        }
        finally
        {
            PreviewRing.IsActive = false;
            PreviewButton.IsEnabled = true;
            Validate();
        }
    }

    /// <summary>「预览」：重新解析文件、发 preview:true。服务器只算不动，全绿才放提交键。</summary>
    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        if (Validate() is { } problem)
        {
            Say(problem);
            return;
        }

        PreviewButton.IsEnabled = false;
        PreviewRing.IsActive = true;
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;

        try
        {
            var files = await _service.TransferFilesAsync(_context.Files, CancellationToken.None).ConfigureAwait(true);
            var preview = await _service
                .TransferPreviewAsync(Request, files, CancellationToken.None).ConfigureAwait(true);

            _preview = preview;
            Fill(preview.Result);
        }
        catch (Exception error)
        {
            _preview = null;
            Fill(null);
            Say(Failure.Describe(error));
        }
        finally
        {
            PreviewRing.IsActive = false;
            PreviewButton.IsEnabled = true;
        }
    }

    /// <summary>「立即整理」／「重新整理」。会移动源文件、或开着重新整理时，先问一句再动手。</summary>
    private async void OnPrimarySubmit(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        await OnSubmit(background: false, args).ConfigureAwait(true);

    /// <summary>「加入整理队列」：让 MoviePilot 慢慢做，这里只报「已接收」。</summary>
    private async void OnSecondarySubmit(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        await OnSubmit(background: true, args).ConfigureAwait(true);

    private async Task OnSubmit(bool background, ContentDialogButtonClickEventArgs args)
    {
        if (_preview is not { Result.CanSubmit: true } preview) return;

        var request = preview.Request;

        // 移动和重新整理是会「回不来」的那两档；复制出去的文件源还在，预览单又摆在眼前，不再多问。
        var needsConfirm = request.Reorganize || request.TransferType == "move";
        if (needsConfirm)
        {
            args.Cancel = true;
            var word = request.Reorganize ? "重新整理" : background ? "队列整理（移动）" : "立即整理（移动）";
            var agreed = await _confirm(word, MoviePilotTransfer.Confirmation(request, preview.Result, background), word)
                .ConfigureAwait(true);
            if (!agreed) return;
        }
        else
        {
            // 不关对话框：整理结果（含逐文件回执）要摆回预览清单那一格。
            args.Cancel = true;
        }

        await SubmitAsync(preview, background).ConfigureAwait(true);
    }

    private async Task SubmitAsync(MoviePilotTransferPreview preview, bool background)
    {
        PreviewButton.IsEnabled = false;
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        PreviewRing.IsActive = true;

        try
        {
            var result = await _service
                .TransferSubmitAsync(preview.Request, preview.Files, background, CancellationToken.None)
                .ConfigureAwait(true);

            _preview = null;
            Fill(result);
            Say(result.Success ? $"整理已交给 MoviePilot。{result.Summary}" : result.Message, result.Success);
        }
        catch (Exception error)
        {
            Say(Failure.Describe(error));
        }
        finally
        {
            PreviewRing.IsActive = false;
            PreviewButton.IsEnabled = true;
        }
    }

    private void Fill(MoviePilotTransferResult? result)
    {
        Lines.Clear();
        PreviewList.Visibility = result is { Items.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        PreviewSummary.Text = result?.Summary ?? "";

        if (result is not { } filled) return;

        foreach (var line in filled.Items) Lines.Add(line);

        // 预览失败时提交键保持关着；成功了（全部可整理）才放行。
        var open = filled.IsPreview && filled.CanSubmit;
        IsPrimaryButtonEnabled = open;
        IsSecondaryButtonEnabled = open;
    }

    /// <summary>表单上任何改动：把校验那句重新算一遍。有话说话、关提交键，没话收起来。</summary>
    private string? Validate()
    {
        var problem = Request.Problem;

        // 预览的通行证在表单一变就作废 —— 改了季号还拿旧预览去整理，搬的就不是看见的那一份。
        if (problem is not null && _preview is not null)
        {
            _preview = null;
            IsPrimaryButtonEnabled = false;
            IsSecondaryButtonEnabled = false;
            PreviewSummary.Text = "";
        }

        if (problem is not null) Say(problem);
        else if (Problem.IsOpen && Problem.Tag as string == "form") Say(null);

        return problem;
    }

    private void OnEdited(object sender, RoutedEventArgs e) => Validate();

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        var television = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string == MoviePilotTransferRequest.TypeSeries;
        EpisodeRow.Visibility = television ? Visibility.Visible : Visibility.Collapsed;
        Validate();
    }

    private void OnReorganizeToggled(object sender, RoutedEventArgs e)
    {
        PrimaryButtonText = ReorganizeToggle.IsOn ? "重新整理" : "立即整理";
        Validate();
    }

    /// <summary>编号框：输入变了一律先当「自动识别」处理，改回空值也要清掉旧预览。</summary>
    private void OnMediaIdChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Validate();

    /// <summary>
    /// 编号框上按回车／点放大镜：按片名去 MoviePilot 搜，把候选摆进下拉。选中的那一条把编号和数据源一起
    /// 带回来 —— 编号自己填不对比对数据源更可靠。
    /// </summary>
    private async void OnMediaIdQuery(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var term = args.QueryText is { Length: > 0 } text ? text : sender.Text;
        if (term.Trim().Length == 0) return;

        sender.ItemsSource = null;
        sender.IsEnabled = false;
        try
        {
            var found = await _service.SearchAsync(term.Trim(), CancellationToken.None).ConfigureAwait(true);
            sender.ItemsSource = found
                .Where(media => media.CanSubscribe)
                .Select(media => new MoviePilotMediaSuggestion(media))
                .ToList();

            if (found.Count == 0) Say("MoviePilot 上没有搜到这部片，编号可以留空让它自动识别");
        }
        catch (Exception error)
        {
            Say($"查找失败：{Failure.Describe(error)}");
        }
        finally
        {
            sender.IsEnabled = true;
        }
    }

    private void Say(string? message, bool good = false)
    {
        Problem.Severity = good ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        Problem.Message = message ?? "";
        Problem.IsOpen = message is not null;
        Problem.Tag = "form";
    }

    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is MoviePilotMediaSuggestion suggestion)
        {
            MediaIdBox.Text = suggestion.Media.MediaId ?? "";
            var index = 0;
            foreach (var source in SourceBox.Items.Cast<MoviePilotMediaSource>())
            {
                if (source.Id == suggestion.Media.MediaSource) SourceBox.SelectedIndex = index;
                index++;
            }
        }
    }

    /// <summary>给 AutoSuggestBox 的一条候选。字符串没法带回来数据源，包一层。</summary>
    private sealed record MoviePilotMediaSuggestion(MoviePilotMedia Media)
    {
        public override string ToString() =>
            $"{Media.Title}{(Media.Year is { } year ? $"（{year}）" : "")} — {Media.MediaId}";
    }
}
