using Momoka.Emby;

namespace Momoka.MoviePilot;

public sealed record MoviePilotPlaybackTarget(EmbyItem Item, EmbyItem? Parent, IReadOnlyList<EmbyItem> Episodes);

/// <summary>只在当前 Emby 身份下查找；媒体编号必须连同来源、类型和季集核对，不能直接信任另一台媒体服务器的 itemid。</summary>
public static class MoviePilotLibraryPlayback
{
    public static string? Provider(MoviePilotMedia media)
    {
        var provider = media.MediaSource?.ToLowerInvariant() switch
        { "themoviedb" => "Tmdb", "douban" => "Douban", "imdb" => "Imdb", "tvdb" => "Tvdb", _ => null };
        var id = media.MediaId ?? "";
        return provider is null || id.Length == 0 || (provider == "Imdb"
            ? !id.StartsWith("tt", StringComparison.Ordinal) || id.Length < 3 || !id[2..].All(char.IsAsciiDigit)
            : !id.All(char.IsAsciiDigit)) ? null : provider;
    }

    public static bool MatchesMedia(MoviePilotSubscription subscription, EmbyItem item) =>
        item.Type == (subscription.IsSeries ? EmbyItemType.Series : EmbyItemType.Movie)
        && Provider(subscription.Media) is { } provider
        && item.ProviderIds.Any(pair => pair.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)
            && pair.Value == subscription.Media.MediaId);

    public static bool MatchesEpisode(MoviePilotSubscription subscription, MoviePilotSubscriptionEpisode episode, EmbyItem item) =>
        episode.InLibrary && (!string.IsNullOrWhiteSpace(item.Path) || item.MediaSources.Count > 0) && (subscription.IsSeries
            ? item.Type == EmbyItemType.Episode && subscription.Media.Season is { } season && item.ParentIndexNumber == season
                && item.IndexNumber is { } start && start <= episode.Number && (item.IndexNumberEnd ?? start) >= episode.Number
            : episode.Number == 0 && item.Type == EmbyItemType.Movie);

    public static bool MatchesPath(MoviePilotSubscriptionEpisode episode, EmbyItem item) =>
        episode.LibraryEntries.Any(file => SamePath(file.Path, item.Path)
            || item.MediaSources.Any(source => SamePath(file.Path, source.Path)));

    private static bool SamePath(string left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        // Unix 路径区分大小写；Windows 驱动器/UNC 路径不区分。这里只比较，不访问或启动路径。
        var windows = left.Contains('\\') || left.Length > 1 && left[1] == ':';
        return string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static async Task<IReadOnlyDictionary<int, MoviePilotPlaybackTarget>> FindAsync(EmbyClient client,
        MoviePilotSubscription subscription, MoviePilotSubscriptionFiles files, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, MoviePilotPlaybackTarget>();
        var candidates = new List<MoviePilotPlaybackTarget>();
        if (Provider(subscription.Media) is { } provider && files.LibraryCount > 0)
        {
            var matches = await client.FindByProviderAsync(provider, subscription.Media.MediaId!, subscription.IsSeries, cancellationToken).ConfigureAwait(false);
            foreach (var media in matches.Items.Where(item => MatchesMedia(subscription, item)))
            {
                if (!subscription.IsSeries) { candidates.Add(new(media, null, [])); continue; }
                var episodes = await client.GetEpisodesAsync(media.Id, null, cancellationToken, EmbyFields.Detail).ConfigureAwait(false);
                var season = episodes.Where(item => item.ParentIndexNumber == subscription.Media.Season && item.Type == EmbyItemType.Episode).ToArray();
                candidates.AddRange(season.Select(item => new MoviePilotPlaybackTarget(item, media, season)));
            }
        }

        foreach (var episode in files.Episodes.Where(episode => episode.InLibrary))
        {
            var matches = candidates.Where(candidate => MatchesEpisode(subscription, episode, candidate.Item)).ToArray();
            var paths = matches.Where(candidate => MatchesPath(episode, candidate.Item)).ToArray();
            if (paths.Length == 1) result[episode.Number] = paths[0];
            else if (matches.Length == 1) result[episode.Number] = matches[0];
            if (result.ContainsKey(episode.Number)) continue;

            // 媒体源在 Emby 中没有对应提供者时，尝试文件统计提供的条目；只有文件路径也匹配才接受。
            foreach (var file in episode.LibraryEntries.Where(file => file.ServerType.Equals("emby", StringComparison.OrdinalIgnoreCase)
                && file.ItemId.Length > 0 && file.ItemId.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')))
            {
                try
                {
                    var item = await client.GetItemAsync(file.ItemId, cancellationToken).ConfigureAwait(false);
                    if (MatchesEpisode(subscription, episode, item) && MatchesPath(episode, item))
                    {
                        result[episode.Number] = new(item, null, []);
                        break;
                    }
                }
                catch (EmbyApiException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound) { }
            }
        }
        return result;
    }
}
