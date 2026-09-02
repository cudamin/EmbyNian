using EmbyNian.Infrastructure;

namespace EmbyNian.Emby;

/// <summary>
/// 「更多」菜单上的一条命令。每一行都是其中一个，行上写什么字由 <see cref="ItemMenu"/> 定 —— 有几条的字跟着
/// 条目自己的状态走（「继续播放（12:34）」还是「立即播放」、「标记为已观看」还是「标记为未观看」）。
/// </summary>
public enum ItemCommand
{
    /// <summary>立即播放 / 继续播放。</summary>
    Play,

    /// <summary>详细信息：能播的条目才有这一条，落在详情页。</summary>
    Details,

    /// <summary>
    /// 打开：不能播的条目（剧集、合集、媒体库）只有这一条 —— 落在详情页还是另一格网格，不是该问用户的事。
    /// </summary>
    Open,

    MarkPlayed,

    /// <summary>标记为未观看。半途看过的条目也有这一条：它把进度一起清掉，也就是用户说的「标记为未播放」。</summary>
    MarkUnplayed,

    Favorite,

    Unfavorite,

    /// <summary>从继续观看中移除：进度留着，只是这个条目不再出现在那一排里。</summary>
    HideFromResume,

    AddToCollection,

    /// <summary>下载到设备。一个文件下一个，一部剧或者一季把里头的单集都下下来。</summary>
    Download,

    EditMetadata,

    ChangeCover,

    Subtitles,

    /// <summary>刮削元数据信息：重新抓一遍并覆盖现有的，和只补缺失的 <see cref="RefreshMetadata"/> 是两条。</summary>
    Scrape,

    RefreshMetadata,

    ScanLibrary,

    OpenSeries,

    Delete
}

/// <summary>
/// 菜单上的一行：一条命令加它这一次该写的字，或者一条分隔线（<see cref="Command"/> 为空）。
/// </summary>
public readonly record struct ItemMenuEntry(ItemCommand? Command, string Label)
{
    /// <summary>一条分隔线。</summary>
    public static ItemMenuEntry Rule { get; } = new(null, "");

    public bool IsRule => Command is null;
}

/// <summary>
/// 一张卡片的「更多」菜单上有哪几条、按什么次序、每条写什么字 —— 整个答案由条目自己决定，一个输入一个输出。
/// <para>
/// 放在 Core 而不是留在菜单那段代码里，理由是覆盖：单元测试只够得到 Core，而「这一类条目该有哪几条」正是那种
/// 写错了屏上也看着正常的判断 —— 一部剧上冒出「搜索字幕」（剧集不是一个文件，服务器没处可放）、一张媒体库卡片
/// 上冒出「删除」（那会去删整个库的目录），两样都要有人一条条盯着才看得出来。
/// </para>
/// <para>
/// <b>判据是条目的类型和状态，不是这张卡画在哪一页。</b>同一个条目会同时出现在继续观看、媒体库和搜索结果里，
/// 而「它是不是一个文件」「它有没有看到一半」在三处是同一个答案。按页面分菜单的下场是同一张卡在两个地方点开
/// 来不一样，而两边都说不出道理。
/// </para>
/// </summary>
public static class ItemMenu
{
    /// <summary>
    /// 这个条目的菜单，从上到下。分隔线已经摆好：空的那一段不会留下一条线，也不会有两条线挨在一起、
    /// 或者一条线开头结尾（见 <see cref="Join"/>）。
    /// </summary>
    public static IReadOnlyList<ItemMenuEntry> For(EmbyItem item) => Join(
        Opening(item),
        UserState(item),
        Organising(item),
        Metadata(item),
        Navigation(item),
        Destructive(item));

    /// <summary>
    /// 一个文件：能播的那几种类型（电影、单集、视频……）背后正好是服务器上的一个文件。剧和季是一叠文件 ——
    /// 字幕挂在文件上，所以那两条对它们没有意义。
    /// </summary>
    private static bool IsFile(EmbyItem item) => item.IsPlayable;

    /// <summary>
    /// 目录里的一个条目：不是媒体库本身，也不是一个人名。
    /// <para>
    /// 「删除」尤其要看这一位：<c>DELETE /Items/{Id}</c> 连磁盘上的文件一起删，落在一张媒体库卡片上就是去删
    /// 整个库的目录。演职人员那一排是存根（只有 id、名字和类型），照它去刮削或者改封面是拿一个人名当影片使。
    /// </para>
    /// </summary>
    private static bool IsCatalogue(EmbyItem item) =>
        item.Type is not EmbyItemType.CollectionFolder and not EmbyItemType.Person;

    /// <summary>
    /// 一叠文件的容器，下载时要把里头的单集一个个下下来：剧和季。
    /// <para>
    /// 合集不算 —— 那是用户自己攒的一篮子东西，可以横跨几个库、几种类型，「下载这个合集」不是一句说得清的话。
    /// </para>
    /// </summary>
    public static bool IsEpisodeSet(EmbyItem item) =>
        item.Type is EmbyItemType.Series or EmbyItemType.Season;

    private static List<ItemMenuEntry> Opening(EmbyItem item) => item.IsPlayable
        ? [
            new(ItemCommand.Play, item.HasResumePosition
                ? $"继续播放（{TimeFormat.Clock(item.ResumeTicks)}）"
                : "立即播放"),
            new(ItemCommand.Details, "详细信息")
        ]
        : [new(ItemCommand.Open, "打开")];

    /// <summary>
    /// 已观看、收藏、从继续观看中移除 —— 都是「这个账号跟这个条目的关系」，服务器上存在用户数据那一头。
    /// <para>
    /// 「标记为已观看」和「标记为未观看」不是一个开关的两头，两条会同时出现：看到一半的条目既可以说「看完了」，
    /// 也可以说「没看过」（那一下把进度清掉）。从前这里只有一条、按 <c>Played</c> 翻面，于是继续观看那一排上
    /// 永远只有「标记为已观看」—— 用户要的「标记为未播放」在那儿根本没有。
    /// </para>
    /// <para>
    /// 「从继续观看中移除」只在真看到一半的条目上出现，而它和上面那条不重复：这一条把进度<b>留着</b>，只是那一排
    /// 里不再列它（服务器上是 <c>HideFromResume</c>），下次还能接着看；「标记为未观看」是把进度清成零。
    /// </para>
    /// </summary>
    private static List<ItemMenuEntry> UserState(EmbyItem item)
    {
        var rows = new List<ItemMenuEntry>(4);
        if (!item.TracksUserState) return rows;

        var played = item.UserData?.Played == true;
        if (!played) rows.Add(new(ItemCommand.MarkPlayed, "标记为已观看"));
        if (played || item.HasResumePosition) rows.Add(new(ItemCommand.MarkUnplayed, "标记为未观看"));

        rows.Add(item.UserData?.IsFavorite == true
            ? new(ItemCommand.Unfavorite, "取消收藏")
            : new(ItemCommand.Favorite, "添加到收藏"));

        if (item.HasResumePosition) rows.Add(new(ItemCommand.HideFromResume, "从继续观看中移除"));

        return rows;
    }

    /// <summary>把它放进哪儿、拿到哪儿去：合集和下载。</summary>
    private static List<ItemMenuEntry> Organising(EmbyItem item)
    {
        var rows = new List<ItemMenuEntry>(2);
        if (IsCatalogue(item)) rows.Add(new(ItemCommand.AddToCollection, "添加到合集…"));
        if (IsFile(item) || IsEpisodeSet(item)) rows.Add(new(ItemCommand.Download, "下载到设备"));
        return rows;
    }

    /// <summary>
    /// 元数据那一叠。<see cref="ItemCommand.Scrape"/> 和 <see cref="ItemCommand.RefreshMetadata"/> 是两条不同的话：
    /// 刮削是「按名字重新抓一遍，抓到的盖掉现在的」，刷新是「缺什么补什么，现有的一个字不动」。
    /// </summary>
    private static List<ItemMenuEntry> Metadata(EmbyItem item)
    {
        // 编辑元数据对媒体库自己也成立（库有名字、简介和封面），所以它不看 IsCatalogue。
        var rows = new List<ItemMenuEntry> { new(ItemCommand.EditMetadata, "编辑元数据信息…") };

        if (IsCatalogue(item))
        {
            rows.Add(new(ItemCommand.ChangeCover, "修改媒体封面图…"));
            if (IsFile(item)) rows.Add(new(ItemCommand.Subtitles, "搜索和修改字幕…"));
            rows.Add(new(ItemCommand.Scrape, "刮削元数据信息"));
            rows.Add(new(ItemCommand.RefreshMetadata, "刷新元数据信息"));
        }

        // 扫库是服务器整体的一件事，跟这个条目是什么关系不大 —— 演职人员那一排除外，那不是库里的东西。
        if (item.Type != EmbyItemType.Person) rows.Add(new(ItemCommand.ScanLibrary, "重新扫描媒体库"));

        return rows;
    }

    private static List<ItemMenuEntry> Navigation(EmbyItem item) =>
        item.Type == EmbyItemType.Episode && item.SeriesId is { Length: > 0 }
            ? [new(ItemCommand.OpenSeries, "打开所属剧集")]
            : [];

    /// <summary>删除，单独一段摆在最后：它连磁盘上的文件一起删，不该和「刷新元数据」贴在一起被误点。</summary>
    private static List<ItemMenuEntry> Destructive(EmbyItem item) =>
        IsCatalogue(item) ? [new(ItemCommand.Delete, "删除")] : [];

    /// <summary>
    /// 把几段拼成一张菜单，段与段之间摆一条分隔线。空的段整段跳过 —— 不然屏上就是两条挨着的线，或者一条
    /// 开头、结尾的线，而那正是「哪几条不出现」这件事唯一会留下的痕迹。
    /// </summary>
    private static IReadOnlyList<ItemMenuEntry> Join(params List<ItemMenuEntry>[] blocks)
    {
        var rows = new List<ItemMenuEntry>(16);

        foreach (var block in blocks)
        {
            if (block.Count == 0) continue;
            if (rows.Count > 0) rows.Add(ItemMenuEntry.Rule);
            rows.AddRange(block);
        }

        return rows;
    }
}
