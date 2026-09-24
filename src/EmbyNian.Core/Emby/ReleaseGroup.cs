using System.IO;

namespace EmbyNian.Emby;

/// <summary>
/// 从服务器上的文件名里认制作组（release group），并把它缀到两处屏上读数的尾部。
/// <para>
/// 用户令（2026-09-24，配两张截图）：其一，「这是进度条中间的视频格式，在最后新增制作组，取文件名
/// 再见菈菈 S01E12 1080p.AAC-Studio GreenTea 后面的 Studio GreenTea」—— 即画质读数变成
/// <c>1080p · HEVC · MP4 · 261.8MB · Studio GreenTea</c>；其二，「这是独占模式下左上角的标题，在尾部
/// 也新增制作组」—— 即标题变成 <c>再见菈菈 S01E12 再见菈菈 - Studio GreenTea</c>。
/// </para>
/// <para>
/// 取法就按用户指的那一条：文件名（去目录、去扩展名）里<b>最后一个</b>「-」后面的那一段，去首尾空白。
/// 取最后一个而不是第一个，是因为发布组记号自己也带连字符（<c>…WEB-DL.H.264-GRP</c> 的组名是 GRP，
/// 不是 WEB-DL.H.264）。扩展名一律删「最后一个点」之后的那一段：组名后面挂的 <c>.mkv</c>/<c>.mp4</c>
/// 必须去掉（用户给的例子就是 <c>…-Studio GreenTea.mp4</c>），而它和组名自带的点号尾巴（<c>GRP.v2</c>）
/// 在形态上分不开，统一按扩展名处理 —— 点号组名极罕见，宁可少认不认错；组名前面那些点
/// （<c>Some.File-GRP</c>）不在这个点之后，不受影响。没有「-」、或最后一段空白，就当作没有制作组，
/// 两处读数各回各的原样。
/// </para>
/// </summary>
public static class ReleaseGroup
{
    /// <summary>
    /// 「Studio GreenTea」from <c>…/再见菈菈 S01E12 1080p.AAC-Studio GreenTea.mp4</c>, or empty when
    /// the file name gives nothing to read.
    /// </summary>
    public static string FromFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var name = Path.GetFileName(path);
        if (name.Length == 0) return "";
        var dash = name.LastIndexOf('-');
        var dot = name.LastIndexOf('.');
        if (dot > 0 && dot > dash) name = name[..dot];
        if (dash < 0) return "";
        return name[(dash + 1)..].Trim();
    }

    /// <summary>
    /// 独占模式左上角那一行（<c>force-media-title</c>）的拼法：标题后面接「 - 制作组」。
    /// 标题或组名空着时返回另一边，不产孤零零的分隔符。
    /// </summary>
    public static string DecorateTitle(string? title, string? path)
    {
        var group = FromFileName(path);
        if (group.Length == 0) return title ?? "";
        if (string.IsNullOrWhiteSpace(title)) return group;
        return $"{title} - {group}";
    }
}
