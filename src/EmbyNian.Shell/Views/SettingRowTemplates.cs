using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// Picks the template for a settings row from the row's type.
/// <para>
/// The settings page is around sixty rows of a handful of shapes, so the shapes are written once as
/// templates and the rows are data. This is what joins the two. The alternative — a <c>Visibility</c> per
/// shape on every row, with every shape's controls built for each — realises the controls of every shape
/// on every row and leaves that many bindings live, all but one of them pointed at properties its row does
/// not have.
/// </para>
/// <para>
/// The templates are set from XAML rather than found by convention, so a missing one is visible in the
/// markup next to the templates themselves instead of being a null returned at runtime.
/// </para>
/// </summary>
public sealed partial class SettingRowTemplates : DataTemplateSelector
{
    public DataTemplate? Choice { get; set; }

    public DataTemplate? Toggle { get; set; }

    public DataTemplate? ToggleGroup { get; set; }

    public DataTemplate? Number { get; set; }

    public DataTemplate? Slider { get; set; }

    public DataTemplate? Text { get; set; }

    public DataTemplate? Font { get; set; }

    public DataTemplate? Theme { get; set; }

    /// <summary>一行读数加一颗可有可无的按钮，见 <see cref="SettingFactRow"/>。</summary>
    public DataTemplate? Fact { get; set; }

    /// <summary>一行说明加一颗按下去真会做事的按钮，见 <see cref="SettingActionRow"/>。</summary>
    public DataTemplate? Action { get; set; }

    /// <summary>主页版面那张可拖拽、带勾选的表，见 <see cref="SettingHomeLayoutRow"/>。</summary>
    public DataTemplate? HomeLayout { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        // Before the two numeric rows, because a slider row is not a number row but reads like one; and
        // before the plain toggle, because a group of toggles is not one toggle.
        SettingThemeRow => Theme,
        SettingHomeLayoutRow => HomeLayout,
        SettingFactRow => Fact,
        SettingActionRow => Action,
        SettingChoiceRow => Choice,
        SettingToggleGroupRow => ToggleGroup,
        SettingToggleRow => Toggle,
        SettingSliderRow => Slider,
        SettingNumberRow => Number,
        SettingFontRow => Font,
        SettingTextRow => Text,
        _ => null
    };

    /// <summary>
    /// The overload an <c>ItemsControl</c> actually calls. It does not fall back to the one-argument form on
    /// its own, so a selector that overrides only that one returns null for every row and the page comes up
    /// empty — with no error, because returning no template is legal.
    /// </summary>
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
