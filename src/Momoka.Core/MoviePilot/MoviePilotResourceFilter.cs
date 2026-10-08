namespace Momoka.MoviePilot;

public enum MoviePilotPromotionFilter { All, Free, Discount }
public enum MoviePilotResourceSort { Original, Seeders, Newest, Smallest, Largest }

/// <summary>过滤只作用于本次返回的资源，不重新访问站点，也不改变下载所用的原始身份。</summary>
public sealed record MoviePilotResourceFilter
{
    public string Text { get; init; } = "";
    public string Site { get; init; } = "";
    public string Resolution { get; init; } = "";
    public string Label { get; init; } = "";
    public MoviePilotPromotionFilter Promotion { get; init; }
    public MoviePilotResourceSort Sort { get; init; }
    public bool SeededOnly { get; init; }
    public bool ExcludeHitAndRun { get; init; }
    public int? PublishedWithinDays { get; init; }

    public bool Matches(MoviePilotResource resource, DateTimeOffset now)
    {
        if (Site.Length > 0 && !string.Equals(resource.SiteName, Site, StringComparison.OrdinalIgnoreCase)) return false;
        if (Resolution.Length > 0 && !string.Equals(resource.Resolution, Resolution, StringComparison.OrdinalIgnoreCase)) return false;
        if (Label.Length > 0 && !resource.Labels.Contains(Label, StringComparer.OrdinalIgnoreCase)) return false;
        if (SeededOnly && resource.Seeders <= 0 || ExcludeHitAndRun && resource.HitAndRun) return false;
        if (Promotion == MoviePilotPromotionFilter.Free && !resource.IsFree ||
            Promotion == MoviePilotPromotionFilter.Discount && !resource.HasDiscount) return false;
        if (PublishedWithinDays is { } days &&
            (resource.PublishedAt is not { } published || published < now.AddDays(-days))) return false;

        var searchable = $"{resource.Title} {resource.Description} {resource.SiteName} {resource.Resolution} {resource.LabelsText}";
        return Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(word => searchable.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<MoviePilotResource> Apply(IEnumerable<MoviePilotResource> resources, DateTimeOffset now)
    {
        var filtered = resources.Where(resource => Matches(resource, now));
        return Sort switch
        {
            MoviePilotResourceSort.Seeders => filtered.OrderByDescending(resource => resource.Seeders),
            MoviePilotResourceSort.Newest => filtered.OrderByDescending(resource => resource.PublishedAt),
            MoviePilotResourceSort.Smallest => filtered.OrderBy(resource => resource.Size <= 0).ThenBy(resource => resource.Size),
            MoviePilotResourceSort.Largest => filtered.OrderByDescending(resource => resource.Size),
            _ => filtered
        };
    }
}
