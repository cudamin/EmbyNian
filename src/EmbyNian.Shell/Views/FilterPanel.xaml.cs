using EmbyNian.Emby;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 需求 5：the filter panel. All of it is <see cref="FilterPanelViewModel"/>; what is left here is the
/// contract the library page holds it by.
/// <para>
/// Nothing in this file draws anything. The blocks are built by the view model and drawn by the templates
/// in the markup, which is what the 332 lines this replaced were doing by hand — every <c>CheckBox</c>,
/// <c>Grid</c> and <c>Expander</c> constructed in code, pushed into a named <c>StackPanel</c>, and its
/// handlers attached and detached around every programmatic change.
/// </para>
/// </summary>
public sealed partial class FilterPanel : UserControl
{
    public FilterPanel()
    {
        // Constructed with the panel rather than handed over afterwards, so x:Bind never has a null root;
        // the same reason SignInPage gives.
        ViewModel = new FilterPanelViewModel();
        InitializeComponent();

        // Forwarded rather than re-exposed, so the page keeps subscribing to the panel it can see in its
        // own markup and gets the panel as the sender.
        ViewModel.Changed += (_, e) => Changed?.Invoke(this, e);
    }

    /// <summary>
    /// Something was ticked. The page reloads its grid and writes the selection down.
    /// </summary>
    public event EventHandler? Changed;

    internal FilterPanelViewModel ViewModel { get; }

    /// <inheritdoc cref="FilterPanelViewModel.Labels"/>
    internal IReadOnlyList<string> Labels => ViewModel.Labels;

    /// <summary>
    /// Whether every block the view model built has a shape to be drawn in, for the self-check. This is
    /// the one thing about this panel that no amount of correct view-model state would reveal: a
    /// <see cref="DataTemplateSelector"/> that returns null is legal and silent — see
    /// <see cref="FilterSectionTemplates"/>, which is a selector this codebase has already been caught by
    /// once — so the panel would come up empty with nothing anywhere saying why.
    /// <para>
    /// Asked rather than looked at, because the flyout's content is not in the visual tree until the
    /// flyout is shown, and the self-check builds this panel without ever opening it.
    /// </para>
    /// </summary>
    internal bool EveryTemplateResolves =>
        Resources["EgFilterSections"] is FilterSectionTemplates templates
        && ViewModel.Sections.Count > 0
        && ViewModel.Sections.All(section => templates.SelectTemplate(section) is not null);

    /// <inheritdoc cref="FilterPanelViewModel.Open"/>
    internal void Open(
        ItemFilters selection,
        string? preset,
        Version? serverVersion,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> lookup) =>
        ViewModel.Open(selection, preset, serverVersion, lookup);

    /// <inheritdoc cref="FilterPanelViewModel.Dismiss"/>
    internal void Dismiss() => ViewModel.Dismiss();
}
