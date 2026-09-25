using EmbyNian.Emby;
using EmbyNian.Infrastructure;

namespace EmbyNian.Playback;

/// <summary>
/// 播放中换版本（Emby 一个条目下挂几个文件：4K HDR、1080p、导演剪辑版……）的那几条判据。
/// <para>
/// 与 <see cref="PlaybackService.CandidateSources"/> 是同一件事的两面：那一条是「这一版打不开就自动换下一版」，
/// 这一条是「用户点名要某一版」。两条都靠着同一个事实 —— 版本之间换的是<b>文件</b>，不是片子：续播位置照旧
/// （「看到哪儿」与版本无关，这是换版重试那条路上早就写下的规矩），上报身份照旧（同一个条目 Id）。
/// </para>
/// <para>
/// 全是纯函数，住在 Core 而不是播放页里。四条判据各有一种「不报错的错法」：位置从零开始（白丢观众看到的地方）、
/// 把别的版本认成正在放的那版（菜单上勾错一行）、点了自己那一版还是重开一次（平白断一次片）、1 起算的序号
/// 差一位（换到旁边那一版）。它们不抛异常也不进日志，所以钉在这里，由测试看着。
/// </para>
/// </summary>
public static class MediaVersionSwitch
{
    /// <summary>这个条目上有哪几版，顺序就是菜单里的次序 —— 拿到的表已经由 <see cref="ItemDetail.OrderVersionsNewestFirst"/>
    /// 排成最新入库在前。没有条目（没在放）时是空表。</summary>
    public static IReadOnlyList<MediaSource> Versions(EmbyItem? item) => item?.MediaSources ?? [];

    /// <summary>
    /// 两版是不是同一版文件。
    /// <para>
    /// 先认对象本身 —— 菜单里那一行、票里那一版、在播的那一版通常就是同一个对象（都出自
    /// <c>item.MediaSources</c>），引用相等时不必信任何 Id。再按 Id 认，那是给「同一条目问了第二遍、
    /// 拿到的是另一批对象」留的退路；<b>Id 为空不算同一版</b>：Emby 对一部分直连文件不返回源 Id，
    /// 几个空 Id 会互相「相等」，那会把别的版本认成正在放的那一版 —— 同
    /// <see cref="ItemDetail.PickSource"/> 的规矩。
    /// </para>
    /// </summary>
    public static bool Same(MediaSource? left, MediaSource? right)
    {
        if (left is null || right is null) return false;
        if (ReferenceEquals(left, right)) return true;

        return left.Id.Length > 0 && string.Equals(left.Id, right.Id, StringComparison.Ordinal);
    }

    /// <summary>
    /// 这一版值不值得换：它得是这个条目上的一版，而且不是正在放的那一版。
    /// 「点了自己正在看的那一版」必须什么都不做 —— 换版是一次实打实的重开（重新起流、重新缓冲、位置重设），
    /// 手滑一下就是平白断一次片。
    /// </summary>
    public static bool ShouldSwitch(EmbyItem? item, MediaSource? target, MediaSource? playing)
    {
        if (target is null) return false;
        if (item is null || !item.MediaSources.Contains(target)) return false;

        return !Same(target, playing);
    }

    /// <summary>
    /// 换版之后新一版从哪一刻开始：手里这一刻的位置，问得出来就用它。
    /// <para>
    /// 问不出来时（mpv 还没报过位置、后端没有控制通道）退回条目自己的续播点 —— 起播时用的就是它，
    /// 所以退回去仍然落在同一条线上。<c>null</c> 与 <c>0</c> 不是一回事：前者是「不知道」，后者是
    /// 「从头放」，而从头放只有在条目自己也没有断点时才成立。
    /// </para>
    /// </summary>
    public static long StartTicks(double? positionSeconds, long fallbackTicks) =>
        positionSeconds is { } seconds and > 0
            ? TimeFormat.ToTicks(seconds)
            : Math.Max(0, fallbackTicks);

    /// <summary>
    /// 视频窗那条消息里点中的是第几版 —— <b>1 起算</b>，与宿主推菜单时的次序对齐（同选集那个集序号）。
    /// 越界、0、负数一律 null：那是过期菜单或垃圾，不是「第一版」。
    /// </summary>
    public static MediaSource? At(EmbyItem? item, int index) =>
        item is not null && index >= 1 && index <= item.MediaSources.Count
            ? item.MediaSources[index - 1]
            : null;

    /// <summary>
    /// 条目自己的版本表里，哪一行是正在放的那一版（按 <see cref="Same"/> 的规矩认）；认不出来时 null。
    /// 要的是<b>表里那一行</b>而不是手里那个对象：菜单勾选、控制条中间那行读数，指的都是这一行。
    /// </summary>
    public static MediaSource? Playing(EmbyItem? item, MediaSource? playing) =>
        item is null || playing is null
            ? null
            : item.MediaSources.FirstOrDefault(source => Same(source, playing));

    /// <summary>
    /// 多版本时默认播哪一版（「参考标题筛选，新增视频文件名筛选」，2026-09-22）：按视频文件名规则打分，取分最高的
    /// 那一版；同分时保留表里的次序取第一个 —— 那张表 2026-09-24 起是「最新入库在前」（见
    /// <see cref="ItemDetail.OrderVersionsNewestFirst"/>），所以没规则可依时默认的就是最新入库的那一版，
    /// 与从前「默认第一版」一脉相承。没有条目/规则、或只有一版时直接是表里第一版 ——
    /// <b>单版本条目一律不受影响</b>。用在起播挑默认版本处（详情页预选、直接播放），换版仍走
    /// <see cref="ShouldSwitch"/>／<see cref="At"/> 那条用户点名的路。
    /// </summary>
    public static MediaSource? Preferred(EmbyItem? item, IReadOnlyList<KeywordRule>? rules)
    {
        var sources = item?.MediaSources;
        if (sources is null || sources.Count == 0) return null;
        if (sources.Count == 1 || rules is null || rules.Count == 0) return sources[0];

        var ranked = KeywordFilter.Rank(sources, rules, FileText);
        return ranked.Count > 0 ? ranked[0] : sources[0];
    }

    /// <summary>
    /// 一版用来按文件名筛的文本：版本名加文件名。发布组标记（REMUX、4K、枪版…）多半在文件名里，版本名也可能带，
    /// 两个都算上。<c>System.IO.Path</c> 写全名，免得和 <see cref="MediaSource.Path"/> 撞名。
    /// </summary>
    private static string FileText(MediaSource source)
    {
        var file = source.Path is { Length: > 0 } path ? System.IO.Path.GetFileName(path) : "";
        return $"{source.Name} {file}".Trim();
    }
}
