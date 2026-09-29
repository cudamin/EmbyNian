using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.MoviePilot;
using Microsoft.UI.Xaml;

namespace EmbyNian.Shell.ViewModels;

public sealed partial class MoviePilotResourceBrowserViewModel : ObservableObject
{
    public const string AllSites = "全部站点";
    public const string AllResolutions = "全部清晰度";
    public const string AllLabels = "全部标签";
    private readonly List<MoviePilotResourceRow> _all = [];
    private bool _resetting;

    public ObservableCollection<MoviePilotResourceRow> Rows { get; } = [];
    public ObservableCollection<string> Sites { get; } = [AllSites];
    public ObservableCollection<string> Resolutions { get; } = [AllResolutions];
    public ObservableCollection<string> Labels { get; } = [AllLabels];

    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial string SelectedSite { get; set; } = AllSites;
    [ObservableProperty] public partial string SelectedResolution { get; set; } = AllResolutions;
    [ObservableProperty] public partial string SelectedLabel { get; set; } = AllLabels;
    [ObservableProperty] public partial int PromotionIndex { get; set; }
    [ObservableProperty] public partial int SortIndex { get; set; }
    [ObservableProperty] public partial int DateIndex { get; set; }
    [ObservableProperty] public partial bool SeededOnly { get; set; }
    [ObservableProperty] public partial bool ExcludeHitAndRun { get; set; }

    public Visibility FiltersVisibility => _all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoMatchesVisibility => _all.Count > 0 && Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string CountText => $"显示 {Rows.Count} / {_all.Count} 个资源";

    internal void SetResults(IEnumerable<MoviePilotResourceRow> rows)
    {
        _all.Clear();
        _all.AddRange(rows);
        _resetting = true;
        FillChoices(Sites, AllSites, _all.Select(row => row.Resource.SiteName));
        FillChoices(Resolutions, AllResolutions, _all.Select(row => row.Resource.Resolution));
        FillChoices(Labels, AllLabels, _all.SelectMany(row => row.Resource.Labels));
        _resetting = false;
        Reset();
        OnPropertyChanged(nameof(FiltersVisibility));
    }

    [RelayCommand]
    private void Reset()
    {
        _resetting = true;
        Text = "";
        SelectedSite = AllSites;
        SelectedResolution = AllResolutions;
        SelectedLabel = AllLabels;
        PromotionIndex = SortIndex = DateIndex = 0;
        SeededOnly = ExcludeHitAndRun = false;
        _resetting = false;
        Apply();
    }

    partial void OnTextChanged(string value) => Apply();
    partial void OnSelectedSiteChanged(string value) => Apply();
    partial void OnSelectedResolutionChanged(string value) => Apply();
    partial void OnSelectedLabelChanged(string value) => Apply();
    partial void OnPromotionIndexChanged(int value) => Apply();
    partial void OnSortIndexChanged(int value) => Apply();
    partial void OnDateIndexChanged(int value) => Apply();
    partial void OnSeededOnlyChanged(bool value) => Apply();
    partial void OnExcludeHitAndRunChanged(bool value) => Apply();

    private void Apply()
    {
        if (_resetting) return;
        var filter = new MoviePilotResourceFilter
        {
            Text = Text,
            Site = SelectedSite == AllSites ? "" : SelectedSite ?? "",
            Resolution = SelectedResolution == AllResolutions ? "" : SelectedResolution ?? "",
            Label = SelectedLabel == AllLabels ? "" : SelectedLabel ?? "",
            Promotion = (MoviePilotPromotionFilter)PromotionIndex,
            Sort = (MoviePilotResourceSort)SortIndex,
            SeededOnly = SeededOnly,
            ExcludeHitAndRun = ExcludeHitAndRun,
            PublishedWithinDays = DateIndex switch { 1 => 1, 2 => 7, 3 => 30, 4 => 90, _ => null }
        };
        var byResource = new Dictionary<MoviePilotResource, MoviePilotResourceRow>(ReferenceEqualityComparer.Instance);
        foreach (var row in _all) byResource[row.Resource] = row;
        var desired = filter.Apply(_all.Select(row => row.Resource), DateTimeOffset.Now)
            .Select(resource => byResource[resource]).ToList();
        var keep = desired.ToHashSet();
        for (var index = Rows.Count - 1; index >= 0; index--)
            if (!keep.Contains(Rows[index])) Rows.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < Rows.Count && ReferenceEquals(Rows[index], desired[index])) continue;
            var existing = Rows.IndexOf(desired[index]);
            if (existing >= 0) Rows.Move(existing, index);
            else Rows.Insert(index, desired[index]);
        }
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(NoMatchesVisibility));
    }

    private static void FillChoices(ObservableCollection<string> choices, string all, IEnumerable<string?> values)
    {
        choices.Clear();
        choices.Add(all);
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase))
            choices.Add(value);
    }
}
