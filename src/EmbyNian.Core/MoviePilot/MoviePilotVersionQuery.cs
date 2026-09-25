using EmbyNian.Emby;

namespace EmbyNian.MoviePilot;

/// <summary>
/// 从一个 Emby 条目算出「去 MoviePilot 找它的其他版本」时该发的搜索词 —— 一个输入一个输出，放 Core 配测试。
/// <para>
/// 为什么住 Core：这几行是「电影发片名、季发『剧名 S01』、集发『剧名 S01 E01』」这种写错了屏上也看着正常的
/// 判断（少个空格、季号取错成集号、季页拿了『第 1 季』当剧名），和 <see cref="Emby.ItemMenu"/> 同一类，靠单测
/// 一条条钉住。用户定的对应（2026-09-24）：剧→<c>剧名</c>；季→<c>剧名 S0x</c>；集→<c>剧名 S0x E0x</c>；
/// 电影→<c>电影名</c>。
/// </para>
/// </summary>
public static class MoviePilotVersionQuery
{
    /// <summary>
    /// 能不能「搜版本」：只有电影、剧、季、单集这四种在 MoviePilot 上对得上一部片。合集是一篮子横跨多片的东西、
    /// 媒体库是货架、演职人员是个人名 —— 都没有「对应的一部片」可搜，所以「更多」菜单也不给它们这一条。
    /// </summary>
    public static bool Supports(EmbyItem item) =>
        item.Type is EmbyItemType.Movie or EmbyItemType.Series or EmbyItemType.Season or EmbyItemType.Episode;

    /// <summary>
    /// 发给 MoviePilot 关键词搜索（<c>search/title</c>）的那一串。季在剧名后补 <c>S0x</c>、集补 <c>S0x E0x</c>，
    /// 让站点搜索把范围收到那一季/那一集；剧和电影就发名字（整部剧 / 整片）。
    /// </summary>
    public static string Keyword(EmbyItem item) => item.Type switch
    {
        // 季自己的 IndexNumber 就是季号；剧名取父级的 SeriesName。
        EmbyItemType.Season => Join(SeriesTitle(item), Season(item.IndexNumber)),

        // 集的季号在 ParentIndexNumber，集号在 IndexNumber。
        EmbyItemType.Episode => Join(SeriesTitle(item), Season(item.ParentIndexNumber), Episode(item.IndexNumber)),

        // 电影、剧：整片 / 整部剧，只发名字。
        _ => item.Name.Trim()
    };

    /// <summary>季/集拿剧名当词头；服务器偶尔没回剧名时退到条目自己的名字，总比空词强。</summary>
    private static string SeriesTitle(EmbyItem item) =>
        !string.IsNullOrWhiteSpace(item.SeriesName) ? item.SeriesName!.Trim() : item.Name.Trim();

    /// <summary>「S01」。季号缺失（未编号的特别篇之类）时给空串，让关键词退回只有剧名，而不是发个「S」出去。</summary>
    private static string Season(int? number) => number is { } n and >= 0 ? $"S{n:00}" : "";

    /// <summary>「E01」。同上，缺失给空串。</summary>
    private static string Episode(int? number) => number is { } n and >= 0 ? $"E{n:00}" : "";

    /// <summary>把非空的几段用单空格拼起来 —— 少一段（比如季号没给）就少一段，不留下多余的空格。</summary>
    private static string Join(params string[] parts) => string.Join(' ', parts.Where(part => part.Length > 0));
}
