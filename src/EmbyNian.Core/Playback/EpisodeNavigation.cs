using EmbyNian.Emby;

namespace EmbyNian.Playback;

/// <summary>The episode to play after a step, together with the season list its picker should show.</summary>
public sealed record EpisodeDestination(EmbyItem Episode, IReadOnlyList<EmbyItem> Siblings);

/// <summary>
/// Resolves 上一集 / 下一集 from a series-wide episode list. The server owns the ordering; this rule only
/// walks that order and narrows the result back to one season so the player's 选集 menu stays seasonal.
/// </summary>
public static class EpisodeNavigation
{
    public static EpisodeDestination? Step(
        IReadOnlyList<EmbyItem> episodes,
        string currentItemId,
        int offset)
    {
        if (offset is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(offset));

        for (var index = 0; index < episodes.Count; index++)
        {
            if (!string.Equals(episodes[index].Id, currentItemId, StringComparison.Ordinal)) continue;

            var targetIndex = index + offset;
            if (targetIndex < 0 || targetIndex >= episodes.Count) return null;

            var target = episodes[targetIndex];
            var siblings = episodes.Where(candidate => SameSeason(candidate, target)).ToList();
            return new EpisodeDestination(target, siblings.Count > 0 ? siblings : [target]);
        }

        return null;
    }

    private static bool SameSeason(EmbyItem candidate, EmbyItem target)
    {
        if (candidate.SeasonId is { Length: > 0 } candidateSeason
            && target.SeasonId is { Length: > 0 } targetSeason)
            return string.Equals(candidateSeason, targetSeason, StringComparison.Ordinal);

        if (candidate.ParentIndexNumber is { } candidateIndex
            && target.ParentIndexNumber is { } targetIndex)
            return candidateIndex == targetIndex;

        if (candidate.SeasonName is { Length: > 0 } candidateName
            && target.SeasonName is { Length: > 0 } targetName)
            return string.Equals(candidateName, targetName, StringComparison.Ordinal);

        return string.Equals(candidate.Id, target.Id, StringComparison.Ordinal);
    }
}
