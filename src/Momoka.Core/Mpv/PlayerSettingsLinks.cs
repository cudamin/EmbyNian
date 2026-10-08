namespace Momoka.Mpv;

/// <summary>
/// 右键画面菜单里「去设置里改」的一行：菜单上写什么、独占模式回传什么令牌、设置窗口落在哪一张卡。
/// </summary>
/// <param name="Label">菜单上那一行的字（「字幕」「视频输出」「音频输出」）。</param>
/// <param name="Token">
/// 独占模式菜单行 <c>value</c> 里带的令牌（<c>subtitle</c>／<c>video</c>／<c>audio</c>）。ASCII、短、每个只有
/// 一个含义 —— 契约 <see cref="VideoWindowContract.Parse"/> 收窄值域收的就是它，uosc 那头只把它原样发回来。
/// </param>
/// <param name="Category">
/// 设置窗口要落在的那张卡，也就是 <c>SettingsViewModel</c> 那张卡片名单里的名字。自检拿它对账：卡片改名而
/// 这里没跟上时，「点进去落在别的卡上」必须当场报红，而不是安静地开在「播放器」上。
/// </param>
public sealed record PlayerSettingsLink(string Label, string Token, string Category);

/// <summary>
/// 右键画面菜单「播放设置」子菜单中的三行设置入口。
/// <para>
/// 放在 Core 而不是画在某一侧的界面里，理由与 <see cref="PlayerMenuCatalog"/> 一字不差：这两条管线各画各的
/// 菜单（集成侧是 XAML 行、走 <c>PlayerPage.OnMoreMenuOpening</c>；独占侧是 uosc 菜单行、走
/// <c>PlayerViewModel.PushPictureMenuAsync</c>），而行集、次序与文案必须一致。一张表，两边读 —— 加一行、
/// 改一个字都只有一处；两处各写一份，就一定会有一天两边差一截（2026-09-29 统一右键菜单时刚交过这笔学费）。
/// </para>
/// <para>
/// 这三行不执行 mpv 命令，所以它们不进 <see cref="PlayerMenuCatalog"/>：那份目录的每一行都是「跑一串 mpv
/// 命令 + 用 <c>${property}</c> 回读」，而这三行的动作是「把那扇设置窗口开在这张卡上」——由宿主执行，与
/// 版本…／播放信息…一样属于「更多」那一棵。
/// </para>
/// </summary>
public static class PlayerSettingsLinks
{
    public const string MenuLabel = "播放设置";

    /// <summary>The three rows, in the order both menus draw them.</summary>
    public static IReadOnlyList<PlayerSettingsLink> All { get; } =
    [
        new("字幕", "subtitle", "字幕"),
        new("视频输出", "video", "视频输出"),
        new("音频输出", "audio", "音频输出"),
    ];

    /// <summary>
    /// 令牌对得上哪一行；认不出给 null。<see cref="VideoWindowContract.Parse"/> 用它当值域闸：菜单行的
    /// <c>value</c> 是视频窗里那棵 Lua 树发回来的，乱码与旧版本残留都从这里被挡掉。
    /// </summary>
    public static PlayerSettingsLink? For(string token)
    {
        foreach (var link in All)
        {
            if (string.Equals(link.Token, token, StringComparison.Ordinal)) return link;
        }

        return null;
    }
}
