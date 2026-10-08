using System.IO;

namespace Momoka.Emby;

/// <summary>
/// 从服务器上的文件名里认制作组（release group）。
/// <para>
/// 用户令（2026-09-24）：进度条中间的画质读数尾部要缀上「文件名最后一个 - 后面的那一段」，例
/// <c>1080p · HEVC · MP4 · 261.8MB · Studio GreenTea</c>（追加在 Shell 的 <c>PlayingSourceLabel</c> 一侧）。
/// 2026-09-27 又把它加进播放页左上角第二行的文件信息（<c>视频编码 · 音轨 · 组名</c>，见
/// <see cref="Playback.PlaybackTitles"/>）。两处都只用 <see cref="FromFileName"/> 取那一段；左上角主标题
/// （<c>force-media-title</c> / <see cref="EmbyItem.ToPlaybackHeadline"/>）本身不再缀组名。
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
}
