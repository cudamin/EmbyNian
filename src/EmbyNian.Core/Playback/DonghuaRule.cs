using EmbyNian.Emby;

namespace EmbyNian.Playback;

/// <summary>
/// 国漫判定（「新增国漫播放进度自定义百分比标记已看」，用户令 2026-09-26；发行公司同日续令
/// 「把Youku和iQiyi也带上」）：Emby 元数据里类型/标签带「动画」，并且发行公司带腾讯、哔哩哔哩、
/// 优酷或爱奇艺 —— Tencent Video、Tencent、bilibili 都含前两个字串，iQiyi／iQIYI 含 iqiyi 子串，
/// 中文库把发行公司写成「哔哩哔哩」「优酷」「爱奇艺」的也一并兜住。两关都过才算：只看发行
/// 公司会把这几家引进的真人剧卷进来，只看类型会把全部动画都算上，两者都不是用户点名的那批。
/// <para>
/// 剧集条目自己通常不带类型和发行公司（那些长在剧集那一层），所以向上看 <c>parent</c>，
/// 和着色器 <see cref="ShaderGroupResolver.StyleHints"/> 的退路是同一条。
/// </para>
/// </summary>
public static class DonghuaRule
{
    /// <summary>
    /// 发行公司名里的匹配词，不分大小写找子串。用户原话：2026-09-26 给的是「如 Tencent Video、
    /// Tencent、bilibili」，同日续令「把Youku和iQiyi也带上」；中文写法是同一家的兜底（中文库不写英文名）。
    /// </summary>
    public static readonly string[] Studios = ["tencent", "bilibili", "哔哩哔哩", "youku", "优酷", "iqiyi", "爱奇艺"];

    /// <summary>类型这一关认的词：Emby 中文库里动画条目的类型就写着它，标签里也常见（含「国产动画」这类）。</summary>
    public const string Genre = "动画";

    /// <summary>
    /// 这一条是不是国漫。判「类型」先看条目自己再看剧集那一层，任一层命中即可；发行公司同理 ——
    /// 电影两层都带，剧集条目两层都空，规则对两种形状都成立。
    /// </summary>
    public static bool Matches(EmbyItem item, EmbyItem? parent = null)
    {
        var animated = HasGenre(item) || (parent is not null && HasGenre(parent));
        if (!animated) return false;

        return HasStudio(item) || (parent is not null && HasStudio(parent));
    }

    private static bool HasGenre(EmbyItem item) =>
        item.Genres.Concat(item.Tags).Any(hint => hint.Contains(Genre, StringComparison.OrdinalIgnoreCase));

    private static bool HasStudio(EmbyItem item) =>
        item.Studios.Any(studio => Studios.Any(word => studio.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));
}
