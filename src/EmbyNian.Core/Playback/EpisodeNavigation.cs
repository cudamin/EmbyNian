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

    /// <summary>
    /// 上一集 / 下一集 跨季落地时的「选集」那一份：<see cref="Step"/> 只负责在全剧列表里把「下一集是谁」定下来，
    /// 选集列表改由 <paramref name="fetchSeasonEpisodes"/> 按落点那一季问服务器要 —— 与季详情页
    /// <c>DetailViewModel.LoadEpisodesAsync</c> 同走 <c>GetEpisodesAsync(seriesId, seasonId)</c> 这一条路。
    /// <para>
    /// 为什么不直接用 <see cref="Step"/> 回来的 <c>Siblings</c>：服务器按季返回时，会把「按播出顺序排进这一季」的
    /// air-order 特典并进来（《伪恋》S01 = 23 集）；而 <see cref="Step"/> 是在全剧列表上按 <c>SeasonId</c> 本地筛，
    /// 那些特典记在 S00、会被筛掉（= 20 集）。于是从 S00 的特典跨进 S01 起播，选集只剩 20，比季详情页少了并进来的
    /// 特典 —— 同一季、两条路给出的选集对不上（2026-09-24 用户报的《伪恋》跨季相邻单集）。按季问服务器，两条路才一致。
    /// </para>
    /// <para>
    /// 服务器那一份为空、或没带上落点（够不着目标）时，退回 <see cref="Step"/> 本地筛出来的那份：选集略宽好过没有落点。
    /// <paramref name="offset"/> 的取值校验与越界返回 null 都随 <see cref="Step"/>；越界时压根不问服务器。
    /// </para>
    /// </summary>
    public static async Task<EpisodeDestination?> ResolveAdjacentAsync(
        IReadOnlyList<EmbyItem> seriesEpisodes,
        string currentItemId,
        int offset,
        Func<string?, Task<IReadOnlyList<EmbyItem>>> fetchSeasonEpisodes)
    {
        if (Step(seriesEpisodes, currentItemId, offset) is not { } step) return null;

        var target = step.Episode;
        var season = await fetchSeasonEpisodes(target.SeasonId).ConfigureAwait(false);

        if (season.Count > 0 && season.Any(episode => string.Equals(episode.Id, target.Id, StringComparison.Ordinal)))
            return step with { Siblings = season };

        return step;
    }

    /// <summary>
    /// 自动连播用的「本季之内」的那一步：先把自己缩进当前单集所在的那一季，再走一步。
    /// <para>
    /// 「当最后一季的最后一集播放结束后，直接退出播放界面，不得自动续播该最后一季的第一集」（用户令，
    /// 2026-09-18）—— 旧版在季末会退而求其次去拿全剧列表，把下一季的第一集接上来（Re:Zero 的 S1E83
    /// 播完自动跳 S04E12 就是这么来的）。自动连播从此只在季内走：季末返回 null，播放界面随之退出。
    /// 跨季的一步留给上一集/下一集按钮（<c>StepEpisodeAsync</c> 的全剧回退）。
    /// </para>
    /// <para>
    /// 输入的列表是什么不再要紧：哪怕调用方手里的列表因服务器没给 SeasonId 而装着全剧，这里也只认
    /// 当前单集的那一季 —— 回绕与跨季在决策点上失去立足点，而不是指望每个调用方先把列表筛对。
    /// </para>
    /// </summary>
    public static EpisodeDestination? StepInSeason(
        IReadOnlyList<EmbyItem> episodes,
        string currentItemId,
        int offset)
    {
        if (offset is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(offset));

        var current = episodes.FirstOrDefault(item =>
            string.Equals(item.Id, currentItemId, StringComparison.Ordinal));
        if (current is null) return null;

        var season = episodes.Where(candidate => SameSeason(candidate, current)).ToList();

        return Step(season, currentItemId, offset);
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
