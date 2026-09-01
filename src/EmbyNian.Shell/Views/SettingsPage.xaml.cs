using EmbyNian.Infrastructure;
using EmbyNian.Diagnostics;
using EmbyNian.Services;
using EmbyNian.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// What <see cref="SettingsPage"/> needs: somewhere to resolve its services from, and which entry in the
/// left-hand list to open on; see <see cref="LibraryRequest"/> for why it is passed rather than reached for.
/// <para>
/// <paramref name="Category"/> is what makes 服务器 and 诊断 reachable from anywhere: both are entries in
/// this page's own list now (需求 2), so 「open 设置 on 诊断」 is one navigation rather than a page of its
/// own. Empty means 「wherever it was left」 — the page is cached, and re-opening it on the card the user was
/// last reading is the reason it is.
/// </para>
/// </summary>
internal sealed record SettingsRequest(IServiceProvider Services, string Category = "");

/// <summary>
/// 设置页。左侧分类，右侧卡片，播放、字幕、视频输出、音频和着色器的修改会立即写入设置文件。
/// <para>
/// What each row is, what it reads and what it writes now lives in <see cref="SettingsViewModel"/>, and what
/// each row looks like lives in the templates in this page's XAML. What is left here is what only a page can
/// do: hand the view model a <see cref="ConfirmDialog"/> to ask in front of, read the navigation parameter,
/// and answer the self-check.
/// </para>
/// <para>
/// The procedural version this replaces built all sixty-odd rows in C#, which put the labels and ranges, the
/// layout, and the settings access in one file and gave the page a 703-line code-behind. It also needed two
/// mechanisms this does not have: a list of refresher closures replayed to push settings into controls, and
/// a page-wide 「syncing」 flag to stop those pushes from being written straight back. Each row now seeds
/// itself from its setting once and writes from then on, so neither exists.
/// </para>
/// </summary>
public sealed partial class SettingsPage : Page, IShellContent
{
    private const string Category = "设置";

    private SettingsRequest? _request;

    public SettingsPage()
    {
        InitializeComponent();

        // Yes when there is no window to ask in — the opposite of 服务器页's answer, and named at the wiring
        // rather than left to a default because that difference is the whole of it: the only question this
        // page asks is whether to discard unsaved text in the config editor, and going ahead is what that
        // button did before it asked anything at all.
        ViewModel.UseConfirm(ConfirmDialog.For(this, whenNoRoot: true));

        // Kept alive across navigations, which is what makes unsaved text in the config editor — and which
        // card was open — survive a trip to another page and back. Signing out drops the frame's content
        // entirely, so nothing outlives a session; within one, leaving this page no longer costs an edit.
        NavigationCacheMode = NavigationCacheMode.Required;

        // The two hosted entries are selected the same way a card is, so the frame that holds them has to
        // follow the selection rather than only the navigation parameter.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.SelectedCategory)) ShowHosted();
        };
    }

    internal SettingsViewModel ViewModel { get; } = new();

    internal bool IsReady => ViewModel.IsReady;

    /// <summary>
    /// Whether the 字幕 card's font picker holds the machine's families yet. Read by the self-check only:
    /// the scan lands a moment after the page does, and a report written in that moment would be a report
    /// about a list that had not arrived.
    /// </summary>
    internal bool FontsReady => ViewModel.FontsReady;

    /// <summary>The font picker, put through a search and put back. Null before the 字幕 card exists.</summary>
    internal SettingFontRow.Probe? MeasureFontPicker() => ViewModel.MeasureFontPicker();

    /// <summary>
    /// 自检：主题色板 —— 那一行自己答的几件事（见 <see cref="SettingThemeRow.Measure"/>），加上屏上真的画出
    /// 了几块。null 表示界面那张卡片还没建出来。
    /// <para>
    /// 后半句非得在树上数不可：色板缺席在别的读数里一点动静都没有 —— 那一行本身照样是一个行容器，
    /// <see cref="Realised"/> 照样把它数进去。模板选择器少一个 case、或者模板里写错一个资源键，屏上就是一行
    /// 标题底下空着一块，而这一页每一个数字都对得上。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail)? MeasureThemeSwatches()
    {
        if (ViewModel.Themes is not { } row) return null;

        var drawn = 0;
        CountSwatches(this, ref drawn);

        var probe = row.Measure();
        return (probe.Ok && drawn == row.Swatches.Count, $"{probe.Detail}；屏上 {drawn} 块");
    }

    /// <summary>
    /// 自检：主页版面那张可拖拽的表 —— 那一行自己答的（见 <see cref="SettingsViewModel.MeasureHomeRows"/>），
    /// 加上屏上真的画出了几行。null 表示主页那张卡片还没建出来。
    /// <para>
    /// 屏上那半非得在树上数：模板选择器少一个 case，屏上就是一行标题底下空着一块，而这一页别的数字一个都不会动
    /// （同 <see cref="MeasureThemeSwatches"/>）。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail)? MeasureHomeRows()
    {
        if (ViewModel.MeasureHomeRows() is not { } probe) return null;

        var drawn = 0;
        CountHomeRows(this, ref drawn);

        var expected = ViewModel.HomeRows?.Rows.Count ?? 0;
        return (probe.Ok && drawn == expected, $"{probe.Detail}；屏上 {drawn} 行");
    }

    /// <summary>
    /// What the self-check reports: the cards and rows that were built, the category on screen, and what
    /// the config editor has to say about the file it opened.
    /// </summary>
    internal (int Sections, int Rows, string SelectedCategory, int VisibleRows, string ConfigStatus) Summary => (
        ViewModel.Sections.Count,
        ViewModel.RowCount,
        SelectedCategory,
        VisibleRows,
        ViewModel.ConfigEditor?.Status ?? "没有配置编辑器");

    /// <summary>How many row containers the card currently on screen holds.</summary>
    internal int VisibleRows => ViewModel.Sections
        .Where(section => section.IsVisible)
        .Sum(SettingsViewModel.Containers);

    /// <summary>The eight cards in order, and the containers each one holds. What the self-check walks.</summary>
    internal IReadOnlyList<(string Category, int Rows)> Cards => ViewModel.Cards;

    /// <summary>
    /// The left-hand list. Reported separately from <see cref="Cards"/> so the self-check can check the two
    /// against each other: the list and the cards are written out by hand in different places, and a typo in
    /// either is an entry that selects nothing or a card nothing selects.
    /// </summary>
    internal IReadOnlyList<string> Categories => ViewModel.Categories;

    /// <summary>
    /// Shows one card, the same as clicking its entry in the left-hand list — the list's selection is bound
    /// two-way, so it follows. Settable for the self-check's walk, which has to open each card to make it
    /// build its rows.
    /// </summary>
    internal string SelectedCategory
    {
        get => ViewModel.SelectedCategory;
        set => ViewModel.SelectedCategory = value;
    }

    /// <summary>
    /// How many setting rows really made it onto the visual tree, counted off the tree rather than off the
    /// view model.
    /// <para>
    /// The same distinction <see cref="ServersPage.Realised"/> and <see cref="DiagnosticsPage.Realised"/>
    /// exist for, and it matters more here than on either of them: those pages have one template, this page
    /// has eight and picks between them at runtime. A bad resource key inside one of them, a literal of the
    /// wrong type, or a shape the selector has no case for are all invisible to the build and show up as a
    /// row that is simply not there. Counting the rows that rendered is what turns that into a failure.
    /// </para>
    /// <para>
    /// This counts what is realised now, which is the visible card — collapsed cards do not build their
    /// rows. So the number to compare it against is <see cref="VisibleRows"/>, not
    /// <see cref="SettingsViewModel.RowCount"/>, and the self-check walks the categories to reach the
    /// templates that only later cards use.
    /// </para>
    /// </summary>
    internal int Realised
    {
        get
        {
            var rows = 0;
            Count(this, ref rows);
            return rows;
        }
    }

    public object? NavigationRequest => _request;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not SettingsRequest request)
        {
            Log.Warn(Category, "导航参数缺失");
            return;
        }

        Tag = "settings";

        // This page is cached, so coming back to it normally means everything is already built — including
        // any unsaved text in the config editor, which is the point of caching it. Rebuilt only when the
        // container is a different one, i.e. after signing out and back in, where every row's closure would
        // otherwise still be reading the previous session's settings document. The container is what is
        // compared and not the request: the shell builds a fresh request per navigation, so comparing those
        // would rebuild the page — and throw the unsaved text away — on every visit.
        var built = ReferenceEquals(_request?.Services, request.Services) && ViewModel.IsReady;

        _request = request;

        if (!built)
        {
            ViewModel.Attach(
                request.Services.GetRequiredService<ISettingsService>(),
                request.Services.GetRequiredService<ShaderStaging>(),
                request.Services.GetRequiredService<FontLibrary>(),
                request.Services.GetRequiredService<AppPaths>(),
                request.Services.GetRequiredService<Platform.ISystemLauncher>());
            _ = ViewModel.ReloadAsync();
        }

        // Both paths, and after the build: the cached path used to return before reading the parameter at
        // all, which is the whole of 「open 设置 on 诊断」 — the second time it was asked for, it opened on
        // whatever card was last read instead. ShowHosted is called either way, because a cached page whose
        // selection is already 诊断 has to put that page back in the frame after a session change dropped it.
        Select(request.Category);
        ShowHosted();
    }

    /// <summary>
    /// Opens one entry in the left-hand list. Blank leaves the selection alone — the page is cached, and
    /// pressing 设置 again is a request to see it, not to go back to the first card. A name the list does not
    /// hold falls back to the first card rather than selecting nothing, which would show an empty column.
    /// </summary>
    private void Select(string category)
    {
        if (string.IsNullOrEmpty(category)) return;

        ViewModel.SelectedCategory = ViewModel.Categories.Contains(category, StringComparer.Ordinal)
            ? category
            : SettingsViewModel.FirstCardCategory;
    }

    /// <summary>
    /// Keeps <c>HostedFrame</c> in step with the selected category: 服务器, 诊断 and 服务器控制台 are pages,
    /// not cards.
    /// <para>
    /// Navigated afresh every time rather than left in place, because all three load when they are navigated
    /// to and none is worth showing stale — a server list from five minutes ago, a log that stopped tailing
    /// and a console whose token has since been replaced are exactly what these exist to not be. Selecting a
    /// card cancels whatever the page had in flight, which is what the frame would have done had we navigated
    /// away from it.
    /// </para>
    /// </summary>
    private void ShowHosted()
    {
        if (_request is null) return;

        if (!ViewModel.ShowsHosted)
        {
            (HostedFrame.Content as IShellContent)?.Release();
            return;
        }

        var services = _request.Services;

        Type page;
        object parameter;

        if (ViewModel.SelectedCategory == "服务器")
        {
            page = typeof(ServersPage);
            parameter = new ServerRequest(services);
        }
        else if (ViewModel.SelectedCategory == "诊断")
        {
            page = typeof(DiagnosticsPage);
            parameter = new DiagnosticsRequest(services);
        }
        else if (ViewModel.SelectedCategory == SettingsViewModel.DashboardCategory)
        {
            page = typeof(DashboardPage);
            parameter = new DashboardRequest(services);
        }
        else
        {
            Log.Warn(Category, $"没有页面对应设置分类 {ViewModel.SelectedCategory}");
            return;
        }

        HostedFrame.Navigate(page, parameter);

        // Nothing in this frame goes back, and a stack that grows by one per visit keeps a page alive with it.
        HostedFrame.BackStack.Clear();
    }

    /// <summary>
    /// What is really on screen in the right-hand column: the hosted page when one is showing, otherwise
    /// this page. The self-check reads the settings tree through this, so 服务器 and 诊断 are reported on
    /// where they now live rather than where they used to be.
    /// </summary>
    internal object CurrentPage =>
        ViewModel.ShowsHosted && HostedFrame.Content is { } hosted ? hosted : this;

    /// <summary>需求 8 的内嵌控制台，只在它正显示时不为 null；自检通过它读那一页的状态。</summary>
    internal DashboardPage? Dashboard => HostedFrame.Content as DashboardPage;

    /// <summary>
    /// Drops whatever the hosted frame is holding, without changing which category is selected.
    /// <para>
    /// For the settings window's two hide paths. That window is hidden and shown again rather than destroyed,
    /// and 需求 8's console is a live browser process — which must not keep running behind a window the user
    /// has closed. Releasing rather than navigating away keeps the selection, so reopening shows the same
    /// page, navigated afresh by <see cref="ShowHosted"/>.
    /// </para>
    /// </summary>
    internal void ReleaseHosted() => (HostedFrame.Content as IShellContent)?.Release();

    /// <summary>
    /// Nothing to drop. This page has no request in flight and subscribes to nothing: every row holds a
    /// closure over the settings document, which the container owns and outlives the page anyway.
    /// <para>
    /// It is here because <see cref="IShellContent"/> asks for it, and an empty body that says so is the
    /// point — the next person to add something to this page that needs releasing has the place to put it,
    /// and both the navigate-away path and the drop-the-frame path already run it.
    /// </para>
    /// <para>
    /// Unsaved text in the config editor is discarded, as it was before. Saving a config file because the
    /// user signed out would be a surprising thing to do with a file that mpv reads.
    /// </para>
    /// </summary>
    public void Release()
    {
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Release();
        base.OnNavigatedFrom(e);
    }

    /// <summary>
    /// Counts realised row containers. <c>Content</c> rather than <c>DataContext</c>, for the reason spelled
    /// out on <see cref="ServersPage"/>'s counter: everything inside a realised row inherits that row as its
    /// data context, so counting by data context counts the controls in a row instead.
    /// </summary>
    private static void Count(DependencyObject node, ref int rows)
    {
        if (node is ContentPresenter { Content: SettingRow }) rows++;

        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
            Count(VisualTreeHelper.GetChild(node, index), ref rows);
    }

    /// <summary>Same walk, for the theme row's swatches. See <see cref="MeasureThemeSwatches"/>.</summary>
    private static void CountSwatches(DependencyObject node, ref int swatches)
    {
        if (node is ContentPresenter { Content: ThemeSwatch }) swatches++;

        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
            CountSwatches(VisualTreeHelper.GetChild(node, index), ref swatches);
    }

    /// <summary>同上，数主页版面那张表画出来的行。见 <see cref="MeasureHomeRows"/>。</summary>
    private static void CountHomeRows(DependencyObject node, ref int rows)
    {
        if (node is ContentPresenter { Content: HomeRowChoice }) rows++;

        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
            CountHomeRows(VisualTreeHelper.GetChild(node, index), ref rows);
    }
}
