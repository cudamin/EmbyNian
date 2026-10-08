namespace Momoka.Emby;

public sealed partial class EmbyClient
{
    public Task<ItemsResult> FindByProviderAsync(string provider, string mediaId, bool series, CancellationToken cancellationToken)
    {
        var url = EmbyUrl.Combine(ApiBase, $"Users/{Connection.UserId}/Items",
            ("AnyProviderIdEquals", provider + "." + mediaId), ("Recursive", "true"),
            ("IncludeItemTypes", series ? EmbyItemType.Series : EmbyItemType.Movie),
            ("Fields", EmbyFields.Detail), ("Limit", "100"));
        return http.GetJsonAsync<ItemsResult>(url, Context, cancellationToken);
    }
}
