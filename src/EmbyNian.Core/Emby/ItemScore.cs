namespace EmbyNian.Emby;

/// <summary>
/// 显示的评分来自哪个平台 —— 「可在设置使用豆瓣、tmdb、烂番茄等平台的评分」。
/// <para>
/// 顺序就是设置文件里存的整数（那份 JSON 没有装 <c>JsonStringEnumConverter</c>），所以
/// <see cref="Community"/> 必须是 0：那是装机时的行为，也是缺键时反序列化出来的值。往中间插一档就是把用户存着的
/// 选择悄悄换成另一档。
/// </para>
/// </summary>
public enum ScoreSource
{
    /// <summary>公众评分 —— Emby 的 <c>CommunityRating</c>，也就是服务器上刮削插件写进去的那个大众分。</summary>
    Community,

    /// <summary>豆瓣。见 <see cref="ItemScore"/> 的类注释：这一档只改标签，改不了数。</summary>
    Douban,

    /// <summary>TMDB。同上。</summary>
    Tmdb,

    /// <summary>烂番茄 —— Emby 的 <c>CriticRating</c>，0–100 的影评指数。</summary>
    Critic
}

/// <summary>一格评分：屏上那个数，和它旁边那句「这是谁给的分」。</summary>
public sealed record ScoreBadge(string Text, string Label)
{
    /// <summary>服务器上一个分都没有。那一格整块收起来。</summary>
    public static ScoreBadge None { get; } = new("", "");

    public bool Any => Text.Length > 0;
}

/// <summary>
/// 「显示的评分」到底显示哪个数、旁边写谁的名字。
/// <para>
/// <b>先说这件事在服务器那一头能做到什么、做不到什么，不然这一族代码看起来像在骗人。</b>Emby 的条目上只有两个
/// 数字评分槽：<c>CommunityRating</c>（大众分，0–10）和 <c>CriticRating</c>（影评指数，0–100，装了 OMDb 那类插件
/// 时就是烂番茄的新鲜度）。<b>豆瓣、TMDB、IMDb 三家都写进 <c>CommunityRating</c> 这同一个数</b> —— 哪家赢由服务器
/// 上装了哪些刮削插件、谁排在前面决定，客户端换不出来。豆瓣插件留下的唯一痕迹是 <c>ProviderIds</c> 里一把
/// <c>Douban</c> 键，它能证明「这个条目对上了豆瓣」，不能证明「这个分就是豆瓣给的」。
/// </para>
/// <para>
/// 所以这一档设置诚实的样子是：<b>烂番茄那一档真的换了一个数</b>（<c>CriticRating</c>），而豆瓣和 TMDB 那两档
/// 换的是<b>标签</b> —— 服务器认出这个条目属于哪一家，屏上就把那家的名字写在分数旁边；认不出来就老实写「公众
/// 评分」。绝不去请求豆瓣自己的接口：那要把用户在看什么发给第三方，而且没有官方公开的 API。
/// </para>
/// <para>
/// 判断放这儿而不是留在视图模型里，是因为它有唯一正确答案而且错得很安静：一个 0 分被当成「有分」画成「0.0」、
/// 烂番茄的 92 被当成十分制画成「92.0」、机器的小数点是逗号于是 8.4 变成 84 —— 三种都是屏上一个看着挺正常的数。
/// </para>
/// </summary>
public static class ItemScore
{
    /// <summary>
    /// <c>ProviderIds</c> 里那两把键的确切拼法。<b>不许猜前缀、不许 StartsWith</b>：自检那一关每次都会把真服务器
    /// 上这个字典的键原样印出来，写的是别的拼法就改这两个常量。
    /// </summary>
    public const string DoubanProvider = "Douban";

    public const string TmdbProvider = "Tmdb";

    /// <summary>
    /// 设置里那个下拉的每一行。**由 <see cref="Describe"/> 生成**，所以四个中文名只有一处 —— 两处各写一遍的下场是
    /// 下拉里写「烂番茄」而徽章旁边写别的，屏上两处对不上，而所有闸门全绿。
    /// <para>
    /// 顺序被枚举的声明顺序绑死，这正是想要的：下拉必须盖住每一档（存着的值不在选项里时，设置页那一手会额外造一条
    /// 「设置文件中的值」塞进去），而「盖住每一档」在这里是结构上的事实，不用靠一条单测守着。
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Label, ScoreSource Value)> Catalogue { get; } =
        [.. Enum.GetValues<ScoreSource>().Select(source => (Describe(source), source))];

    /// <summary>这一档在屏上和设置里叫什么。</summary>
    public static string Describe(ScoreSource source) => source switch
    {
        ScoreSource.Douban => "豆瓣",
        ScoreSource.Tmdb => "TMDB",
        ScoreSource.Critic => "烂番茄",
        _ => "公众评分"
    };

    /// <summary>
    /// 这个条目在服务器上对上了 <paramref name="provider"/> 这家刮削源没有。
    /// <para>
    /// 逐键显式比较，不靠字典自带的比较器：<c>System.Text.Json</c> 给带 setter 的集合属性**新建**一个
    /// <c>Dictionary</c> 再赋值，用的是默认的区分大小写比较器 —— 属性初始化器里写
    /// <c>new(StringComparer.OrdinalIgnoreCase)</c> 会被整个换掉。服务器发来小写键时失配的样子是标签静静退回
    /// 「公众评分」，屏上完全正常。
    /// </para>
    /// </summary>
    public static bool Matched(EmbyItem item, string provider) =>
        item.ProviderIds.Any(pair => string.Equals(pair.Key, provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 这个条目在 <paramref name="wanted"/> 这一档下该显示什么。
    /// <para>
    /// 三级回落，一条到底：先试选中那一档，空了试公众评分，再空了试影评指数。<b>标签永远说实际取到的是哪一档</b> ——
    /// 选了豆瓣而服务器认不出豆瓣，屏上写的就是「公众评分」，不是「豆瓣」。
    /// </para>
    /// <para>
    /// 回落必须完整，缺一格就有条目莫名空掉：「选了豆瓣、这个条目没有 CommunityRating 但有 CriticRating=92」的时候
    /// 服务器明明有一个分，而只看一档的规矩会把那一格整块收起来，用户看到的是「选了豆瓣评分就没分了」。
    /// </para>
    /// </summary>
    public static ScoreBadge Resolve(EmbyItem item, ScoreSource wanted)
    {
        var community = Community(item);
        var critic = Critic(item);

        if (wanted == ScoreSource.Critic && critic.Any) return critic;
        if (community.Any) return new ScoreBadge(community.Text, CommunityLabel(item, wanted));

        return critic.Any ? critic : ScoreBadge.None;
    }

    /// <summary>
    /// 公众评分那个数怎么写：<c>8.4</c> / <c>8</c>，不是 <c>8.40</c>，而且不跟机器的文化走 —— 小数点是逗号的机器上
    /// 「8.4」会被格式化成「8,4」。0 和缺失都算没有分。
    /// </summary>
    private static ScoreBadge Community(EmbyItem item) =>
        item.CommunityRating is { } score and > 0
            ? new ScoreBadge(score.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), "公众评分")
            : ScoreBadge.None;

    /// <summary>
    /// 影评指数写成百分数：<c>92%</c>。它是 0–100 而公众评分是 0–10，两者格式不一样，混了就是屏上一个「92.0 分」。
    /// <para>
    /// 取整用「远离零」而不是默认的「取偶」：92.5 分算 93 符合人的直觉，而 <c>Math.Round</c> 的默认答案是 92。
    /// </para>
    /// </summary>
    private static ScoreBadge Critic(EmbyItem item) =>
        item.CriticRating is { } score and > 0
            ? new ScoreBadge(
                ((int)Math.Round(score, MidpointRounding.AwayFromZero))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture) + "%",
                "烂番茄")
            : ScoreBadge.None;

    /// <summary>
    /// 公众评分那个数该署谁的名。豆瓣和 TMDB 两档只在服务器真的认出那一家时才写它的名字 —— 认不出来就老实写
    /// 「公众评分」，而不是把一个来路不明的数说成豆瓣的。
    /// </summary>
    private static string CommunityLabel(EmbyItem item, ScoreSource wanted) => wanted switch
    {
        ScoreSource.Douban when Matched(item, DoubanProvider) => Describe(ScoreSource.Douban),
        ScoreSource.Tmdb when Matched(item, TmdbProvider) => Describe(ScoreSource.Tmdb),
        _ => Describe(ScoreSource.Community)
    };
}
