using EmbyNian.Configuration;

namespace EmbyNian.Emby;

/// <summary>
/// 主页上排着哪几排、按什么顺序、哪几排显示 —— 「把媒体库的列表也添加到主页之中，新增页里拖拽决定这些列表的
/// 顺序，勾选显示或者不勾选取消显示」。
/// <para>
/// 一排是一个 <see cref="HomeRowPlan"/>：一把钥匙、一句标题、显示与否。三排是固定的（继续观看、媒体库、接下来
/// 看），其余每个媒体库一排，装的是那个库自己的最近添加。
/// </para>
/// <para>
/// 整个服务器的「最近添加」那一排（旧的 <c>latest</c> 钥匙）2026-09-12 退役：「去掉最近添加，保留最近添加
/// 电视节目、最近添加 电影」。它不在 <see cref="Fixed"/> 里、<see cref="FixedTitle"/> 也认不出它，所以存档里
/// 还带着的那一行在 <see cref="Plan"/> 里整个落不下 —— 顺手被归一化写回设置文件，一处专门的话都不用说。
/// </para>
/// <para>
/// 纯的、在 Core 里，理由和 <see cref="HomeCarousel"/> 一样：这里每一句都是「该排哪几排」的决定，而不是像素。
/// 存档里那份顺序和服务器现在有哪几个库总会对不上 —— 新加了一个库、删掉了一个、改了名 —— 而「对不上的时候
/// 怎么办」正是要被单测钉住的那部分，屏幕上看不出来。
/// </para>
/// </summary>
public static class HomeLayout
{
    /// <summary>上次停在哪儿。</summary>
    public const string Resume = "resume";

    /// <summary>这个账号的媒体库，画成卡片的那一排。</summary>
    public const string Libraries = "libraries";

    /// <summary>接着往下看的下一集。</summary>
    public const string NextUp = "nextup";

    /// <summary>媒体库那几排的钥匙前缀，后面跟着那个库的 id。</summary>
    private const string LibraryPrefix = "library:";

    /// <summary>
    /// 默认版面：三排固定的按这个次序，然后每个媒体库一排。第一次运行、以及设置文件里那份被删空时用的就是它。
    /// </summary>
    private static readonly string[] Fixed = [Resume, Libraries, NextUp];

    /// <summary>一个媒体库那一排的钥匙。</summary>
    public static string LibraryKey(string id) => LibraryPrefix + id;

    /// <summary>那把钥匙指的是哪个媒体库，不是媒体库那几排就是 <see langword="null"/>。</summary>
    public static string? LibraryId(string? key) =>
        key is { Length: > 0 } && key.StartsWith(LibraryPrefix, StringComparison.Ordinal)
            ? key[LibraryPrefix.Length..]
            : null;

    /// <summary>三排固定的各叫什么；媒体库那几排的标题是 <see cref="LibraryTitle"/> 的事。</summary>
    public static string? FixedTitle(string? key) => key switch
    {
        Resume => "继续观看",
        Libraries => "媒体库",
        NextUp => "接下来看",
        _ => null
    };

    /// <summary>
    /// 一个媒体库那一排叫什么：「最近添加 · 电影」。带上「最近添加」这四个字是必须的 —— 一排光叫「电影」的卡片
    /// 说不清是「这个库的全部」还是「这个库最近加的」，而这一排装的是后者。
    /// </summary>
    public static string LibraryTitle(string name) =>
        name is { Length: > 0 } ? $"最近添加 · {name}" : "最近添加";

    /// <summary>
    /// 这一次主页该排哪几排。存档里那份顺序说了算，剩下的按默认次序补在后面。
    /// <para>
    /// 三种对不上都在这里收口：存档里有、现在没有的（那个库删了，或者手改设置写错了一把钥匙）直接扔掉；现在有、
    /// 存档里没有的（服务器上新加的库）补在末尾并且默认显示 —— 新库的东西不该悄悄地不出现；名字变了的按现在的
    /// 名字写。勾选状态一律沿用存档里那一份，那是用户自己按过的。
    /// </para>
    /// <para>
    /// 「现在没有」和「这一头不知道」是两件事，见 <paramref name="libraries"/>：一个库都没在手上时不算删，那几排
    /// 照存档里记着的标题原地留着。
    /// </para>
    /// </summary>
    /// <param name="saved">设置文件里那一份，可以是空的。</param>
    /// <param name="libraries">
    /// 这个账号现在有哪几个媒体库，已经滤掉音乐库（外壳那一头滤的）。一个都没有（<see langword="null"/> 或者空）
    /// 是「这一头不知道」，不是「一个库都没有」：设置窗口不连服务器，而主窗口读库列表失败时也是空手。那种时候
    /// 媒体库那几排照存档里记着的标题排 —— 见 <c>HomeRowSetting.Title</c>。
    /// </param>
    public static IReadOnlyList<HomeRowPlan> Plan(
        IReadOnlyList<HomeRowSetting>? saved,
        IReadOnlyList<EmbyItem>? libraries)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var library in libraries ?? [])
        {
            if (library.Id is not { Length: > 0 } id) continue;

            var key = LibraryKey(id);
            if (names.ContainsKey(key)) continue;

            names[key] = LibraryTitle(library.Name);
            order.Add(key);
        }

        var plan = new List<HomeRowPlan>(Fixed.Length + order.Count);
        var taken = new HashSet<string>(StringComparer.Ordinal);

        // 一排叫什么。四排固定的自己说得出；媒体库那一排的名字在服务器那份库列表里 —— 除非这一头压根没有那份
        // 列表（设置窗口不连服务器，主窗口读库列表失败时也是空手），那就用存档里记着的那一句。少了这一手，那几排
        // 会在拖拽表里整个消失，而用户随手拖一下就把它们从设置文件里抹掉了。
        string? Title(string key, string? cached) =>
            FixedTitle(key)
            ?? (names.TryGetValue(key, out var live) ? live : null)
            ?? (names.Count == 0 && LibraryId(key) is { Length: > 0 } && cached is { Length: > 0 }
                ? cached
                : null);

        // 存档里那一份先走，它就是用户拖出来的次序。
        foreach (var row in saved ?? [])
        {
            if (row.Key is not { Length: > 0 } key || !taken.Add(key)) continue;

            // 认不出来的钥匙：那个库删了，或者手改设置时写错了。扔掉 —— 留着就是一排永远空的货架。
            if (Title(key, row.Title) is not { } title) continue;

            plan.Add(new HomeRowPlan(key, title, row.Visible));
        }

        // 存档里没有的补在后面：三排固定的按默认次序，媒体库那几排按服务器给的次序。
        foreach (var key in Fixed.Concat(order))
        {
            if (!taken.Add(key)) continue;

            plan.Add(new HomeRowPlan(key, FixedTitle(key) ?? names[key], Visible: true));
        }

        return plan;
    }

    /// <summary>归一化之后的这一份写回设置文件，见 <c>UiSettings.HomeRows</c>。</summary>
    public static List<HomeRowSetting> Save(IEnumerable<HomeRowPlan> plan) =>
        [.. plan.Select(row => new HomeRowSetting { Key = row.Key, Title = row.Title, Visible = row.Visible })];

    /// <summary>两份版面是不是同一份 —— 只有真变了才回写设置文件。</summary>
    public static bool Same(IReadOnlyList<HomeRowSetting>? saved, IReadOnlyList<HomeRowPlan> plan)
    {
        if (saved is null || saved.Count != plan.Count) return false;

        for (var index = 0; index < plan.Count; index++)
            if (!string.Equals(saved[index].Key, plan[index].Key, StringComparison.Ordinal)
                || !string.Equals(saved[index].Title, plan[index].Title, StringComparison.Ordinal)
                || saved[index].Visible != plan[index].Visible)
            {
                return false;
            }

        return true;
    }
}

/// <summary>主页上一排：一把钥匙、屏上那句标题、勾了没有。</summary>
public readonly record struct HomeRowPlan(string Key, string Title, bool Visible);
