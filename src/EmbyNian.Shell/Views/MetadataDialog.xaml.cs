using EmbyNian.Emby;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 需求 6 的「编辑」：the form. Every rule about what the fields mean lives in
/// <see cref="ItemMetadataEdit"/>; this class only moves text between that record and the boxes, and
/// keeps 保存 switched off while the record says there is a problem.
/// <para>
/// The boxes are the state, not a copy of it — <see cref="Edit"/> is read out of them on demand rather
/// than kept in a field and synchronised. A field would have to be updated from nine
/// <c>TextChanged</c> handlers and would be wrong for exactly as long as one of them was missing.
/// </para>
/// </summary>
public sealed partial class MetadataDialog : ContentDialog
{
    /// <param name="item">
    /// The item being edited. Needed for two things the form cannot infer from the values: whose name to
    /// put in the subtitle, and whether 季号/集号 apply at all.
    /// </param>
    public MetadataDialog(EmbyItem item, ItemMetadataEdit edit)
    {
        InitializeComponent();

        // The title says what is being done; this says what it is being done to. Worth the line: the
        // menu that opened this dialog is gone by the time it is up, and a form full of somebody else's
        // 简介 is easy to mistake for the right one.
        Title = $"编辑元数据 — {item.Name}";

        NameBox.Text = edit.Name;
        OriginalTitleBox.Text = edit.OriginalTitle;
        SortNameBox.Text = edit.SortName;
        OverviewBox.Text = edit.Overview;
        OfficialRatingBox.Text = edit.OfficialRating;
        CommunityRatingBox.Text = edit.CommunityRating;
        CriticRatingBox.Text = edit.CriticRating;
        ProductionYearBox.Text = edit.ProductionYear;
        PremiereDateBox.Text = edit.PremiereDate;
        GenresBox.Text = edit.Genres;
        TagsBox.Text = edit.Tags;
        ParentIndexBox.Text = edit.ParentIndexNumber;
        IndexBox.Text = edit.IndexNumber;

        // A season numbers itself with IndexNumber, an episode with both; everything else has neither,
        // and offering a 集号 box on a film is offering to write a field the server will ignore.
        switch (item.Type)
        {
            case EmbyItemType.Episode:
                IndexRow.Visibility = Visibility.Visible;
                break;

            case EmbyItemType.Season:
                IndexRow.Visibility = Visibility.Visible;
                ParentIndexBox.Visibility = Visibility.Collapsed;

                // The one box a season shows is its own number, which lives in IndexNumber — the same
                // field an episode calls 集号. Relabelled rather than remapped, so what is saved is
                // still the field that was read.
                IndexBox.Header = "季号";
                break;
        }

        Validate();
    }

    /// <summary>The form as it now stands. Read after the dialog closes, and by <see cref="Validate"/>.</summary>
    public ItemMetadataEdit Edit => new()
    {
        Name = NameBox.Text,
        OriginalTitle = OriginalTitleBox.Text,
        SortName = SortNameBox.Text,
        Overview = OverviewBox.Text,
        OfficialRating = OfficialRatingBox.Text,
        CommunityRating = CommunityRatingBox.Text,
        CriticRating = CriticRatingBox.Text,
        ProductionYear = ProductionYearBox.Text,
        PremiereDate = PremiereDateBox.Text,
        Genres = GenresBox.Text,
        Tags = TagsBox.Text,
        IndexNumber = IndexBox.Text,
        ParentIndexNumber = ParentIndexBox.Text
    };

    private void OnEdited(object sender, TextChangedEventArgs e) => Validate();

    /// <summary>
    /// Disables 保存 rather than rejecting the click. A dialog that closes and then complains has
    /// already thrown the typing away, and there is no way to put it back.
    /// </summary>
    private void Validate()
    {
        var problem = Edit.Problem;

        Problem.Message = problem ?? string.Empty;
        Problem.IsOpen = problem is not null;
        IsPrimaryButtonEnabled = problem is null;
    }
}
