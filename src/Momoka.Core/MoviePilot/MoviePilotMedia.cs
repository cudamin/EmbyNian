namespace Momoka.MoviePilot;

/// <summary>
/// 一条 MoviePilot 搜索结果，只留搜索页要画的和订阅要用的那几项。
/// <para>
/// 不是 <see cref="Emby.EmbyItem"/>：它是「库里还没有、可以去订阅」的东西，没有 Emby id、海报来自外站
/// URL、能做的动作是订阅而不是播放，所以自成一个模型，而不是硬塞进那张卡。字段照 MoviePilot v3.0.1 的
/// <c>MediaInfo</c>（<c>app/schemas/context.py</c>）取：标题、年份、类型、简介、海报，外加身份对
/// <see cref="MediaSource"/> + <see cref="MediaId"/> —— 订阅就靠这一对（见 <see cref="MoviePilotMediaParser"/>）。
/// </para>
/// </summary>
public sealed record MoviePilotMedia
{
    public required string Title { get; init; }
    internal string ConnectionStamp { get; init; } = "";

    public int? Year { get; init; }
    public int? Season { get; init; }

    /// <summary>
    /// MoviePilot 的 <c>MediaType</c> 原值（这台服务器上是「电影」/「电视剧」）。原样带着而不翻成本地枚举：
    /// 订阅正文要回填的就是这个字符串，翻一道再翻回来只会多一处对不上的地方。
    /// </summary>
    public string? Type { get; init; }

    public string? Overview { get; init; }

    /// <summary>海报地址，已是可直接交给 <c>BitmapImage</c> 的字符串；取不到时为 null。</summary>
    public string? PosterUrl { get; init; }

    /// <summary>主身份来源（如 <c>themoviedb</c>/<c>douban</c>/<c>bangumi</c>）。</summary>
    public string? MediaSource { get; init; }

    /// <summary>该来源下的原生 id。和 <see cref="MediaSource"/> 成对，缺一不可。</summary>
    public string? MediaId { get; init; }

    /// <summary>屏上那一行：有年份就「标题 (年份)」，没有就光标题。</summary>
    public string Display => Year is { } year ? $"{Title} ({year})" : Title;
    public string Confirmation => $"{Display}\n{SourceLabel} · {MediaId} · {Type ?? "类型未提供"}" +
        (Season is { } season ? $" · 第 {season} 季" : "");

    /// <summary>
    /// 能不能订阅：身份对齐了才行。MoviePilot 新增订阅时 <c>media_source</c> 和 <c>media_id</c> 必须同时有效，
    /// 缺一个它就回「必须同时提供有效的 media_source 和 media_id」，所以这里先自己把关，省得发一趟必然被拒的请求。
    /// </summary>
    public bool CanSubscribe =>
        !string.IsNullOrWhiteSpace(MediaSource) && !string.IsNullOrWhiteSpace(MediaId);

    /// <summary>这张卡上标来源的那一小块：TMDB／豆瓣／IMDb 各归各，认不出的原样显示。</summary>
    public string SourceLabel => MoviePilotSourceNames.Label(MediaSource);
}

/// <summary>
/// 已知来源标识的显示名。MoviePilot 的来源是开放集合（内置九个加插件扩展，标识格式见 MediaSource schema 的
/// <c>pattern</c>），所以这里只做「认识的说人话、不认识的原样带出」—— 绝不把未知来源折叠成一个笼统的「其他」，
/// 那会把豆瓣和 IMDb 的结果在屏上重新搅成一团。
/// </summary>
public static class MoviePilotSourceNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["themoviedb"] = "TMDB",
        ["douban"] = "豆瓣",
        ["bangumi"] = "Bangumi",
        ["anilist"] = "AniList",
        ["imdb"] = "IMDb",
        ["tvdb"] = "TVDB",
        ["musicbrainz"] = "MusicBrainz",
        ["theaudiodb"] = "TheAudioDB",
        ["doubanmusic"] = "豆瓣音乐"
    };

    public static string Label(string? source) =>
        source is { Length: > 0 } && Known.TryGetValue(source, out var label) ? label : source ?? "";
}
