namespace EmbyNian.Playback;

/// <summary>
/// 一个关键词的三种态度（「双语／特效这类词单独设 优先／默认／候补」，2026-09-22；「候补」原名「排除」，是软排除，
/// 名字改成候补更贴切）。<see cref="Neutral"/> 是 0，所以设置文件里没写这个字段、或写了个不认识的值时，落到「不
/// 生效」这一档，不会平白把一条顶上去或压下去。
/// <para>
/// 这套关键词打分是通用的：字幕按<em>标题</em>筛（<see cref="TrackLanguagePriority"/> 之后那一关），视频版本按
/// <em>文件名</em>筛（多版本条目默认播哪一个）。两处同一套 <see cref="KeywordRule"/>／<see cref="KeywordFilter"/>。
/// </para>
/// </summary>
public enum TitlePreference
{
    /// <summary>默认：这个词不参与打分。</summary>
    Neutral = 0,

    /// <summary>优先：含这个词的排到前头。</summary>
    Prefer = 1,

    /// <summary>候补：含这个词的排到最后（软排除 —— 只有当同一批里全被压低时才让一条出来，不至于没得挑）。</summary>
    Exclude = 2
}

/// <summary>
/// 一条关键词规则：一个词加它的态度。存进设置文件、也是设置页那张表里的一项。用类而不是 record，是因为设置页
/// 要就地改它的 <see cref="State"/>，而 record 的值语义在这儿只会添乱。字幕标题、视频文件名两处共用。
/// </summary>
public sealed class KeywordRule
{
    public KeywordRule() { }

    public KeywordRule(string term, TitlePreference state)
    {
        Term = term;
        State = state;
    }

    /// <summary>要找的那个词，如字幕的「双语」「特效」，或文件名的「REMUX」「枪版」。</summary>
    public string Term { get; set; } = "";

    /// <summary>对含这个词的那一条的态度。</summary>
    public TitlePreference State { get; set; } = TitlePreference.Neutral;
}

/// <summary>
/// 关键词打分：从一批候选里，按词的态度挑出最合意的。优先词一个 +1、候补词一个 −1、默认词不计；分高者胜。
/// <para>
/// 「候补」是软的 —— 一条被压低的只有在同一批全都一样低分时才轮得到它，宁可给一条不那么想要的，也不因为名字
/// 不合意就整个挑不出东西（真要硬排是另一种取舍，届时再说）。规则为空、或文本为空时一律 0 分，等于这一关不作用。
/// 用在两处：字幕按标题（语言筛完之后），视频版本按文件名（多版本默认播哪一个）。
/// </para>
/// </summary>
public static class KeywordFilter
{
    /// <summary>
    /// 一段文本在这组规则下的分数：优先词每命中一个 +1、候补词每命中一个 −1、默认词不计。大小写不敏感、子串匹配
    /// （「中英双语特效版」既含「双语」也含「特效」）。
    /// </summary>
    public static int Score(string? text, IReadOnlyList<KeywordRule>? rules)
    {
        if (rules is null || rules.Count == 0 || string.IsNullOrEmpty(text)) return 0;

        var score = 0;
        foreach (var rule in rules)
        {
            if (rule is null || rule.State == TitlePreference.Neutral || string.IsNullOrWhiteSpace(rule.Term)) continue;
            if (text.Contains(rule.Term, StringComparison.OrdinalIgnoreCase))
            {
                score += rule.State == TitlePreference.Prefer ? 1 : -1;
            }
        }

        return score;
    }

    /// <summary>
    /// 在用户规则末尾补上几条「候补」词（同名的不重复加、也不覆盖用户已设的态度）。字幕那头用它实现「优先级里有
    /// 简体中文时自动把标题带 繁／繁体 的压成候补」——把这条策略留在调用方（<c>TrackSelection</c>），这里只管通用的
    /// 合并动作。
    /// </summary>
    public static IReadOnlyList<KeywordRule> WithExclusions(IReadOnlyList<KeywordRule>? userRules, params string[] terms)
    {
        var rules = new List<KeywordRule>(userRules ?? []);
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            if (!rules.Any(rule => rule is not null && string.Equals(rule.Term, term, StringComparison.OrdinalIgnoreCase)))
            {
                rules.Add(new KeywordRule(term, TitlePreference.Exclude));
            }
        }

        return rules;
    }

    /// <summary>
    /// 从一批候选里留下分数最高的那些 —— 优先词把它抬上来、候补词把它压下去，剩下的交回给调用方按它原来的规矩
    /// （默认轨、服务器次序等）再定夺。规则为空或只有一个候选时原样返回，不白算一遍。<paramref name="textOf"/>
    /// 把一个候选映射成它用来打分的文本（字幕给标题、视频给文件名）。
    /// </summary>
    public static IReadOnlyList<T> Rank<T>(IReadOnlyList<T> pool, IReadOnlyList<KeywordRule>? rules, Func<T, string?> textOf)
    {
        if (rules is null || rules.Count == 0 || pool.Count <= 1) return pool;

        var best = int.MinValue;
        foreach (var item in pool) best = Math.Max(best, Score(textOf(item), rules));

        var top = new List<T>(pool.Count);
        foreach (var item in pool)
        {
            if (Score(textOf(item), rules) == best) top.Add(item);
        }

        return top;
    }
}
