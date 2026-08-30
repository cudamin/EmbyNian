using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// Picks the shape for one block of the filter panel from the block's type — a heading with a grid of
/// boxes, or an expander that fills itself from the server.
/// <para>
/// The same arrangement as <see cref="SettingRowTemplates"/>, and for the same reason: the panel's blocks
/// are decided by <see cref="FilterPanelViewModel"/> from what the grid behind it lists, so their number
/// and order are not known until it is opened, while their two shapes are fixed. Data for the first,
/// markup for the second.
/// </para>
/// <para>
/// Set from XAML rather than found by convention, so a missing template is visible in the markup next to
/// the templates themselves instead of being a null returned at runtime — which is legal, and draws
/// nothing at all.
/// </para>
/// </summary>
public sealed partial class FilterSectionTemplates : DataTemplateSelector
{
    public DataTemplate? ToggleGroup { get; set; }

    public DataTemplate? Values { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        FilterToggleGroup => ToggleGroup,
        FilterValueSection => Values,
        _ => null
    };

    /// <summary>
    /// The overload an <c>ItemsControl</c> actually calls. It does not fall back to the one-argument form
    /// on its own, so a selector that overrides only that one returns null for every block and the panel
    /// comes up empty — with no error, because returning no template is legal.
    /// </summary>
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
