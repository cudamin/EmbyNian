namespace EmbyNian.Playback;

/// <summary>
/// 「拖进画面的这个文件是不是字幕」—— 只按扩展名判定，是把拖拽落到播放器上的一堆文件筛成
/// 「该交给 mpv <c>sub-add</c> 的那几个」的唯一依据。
/// <para>
/// 留在 Core 且用单测钉住，是因为这张后缀表错得起、错了却在屏上看不出来：<c>sub-add</c> 一个视频或
/// 图片文件，mpv 只是安静地拒绝，没有任何报错浮层；而 <c>.ssa</c> 与 <c>.ass</c> 是两个后缀、WebVTT
/// 既是 <c>.vtt</c> 也是 <c>.webvtt</c>，漏一个就是一类字幕拖不进来。
/// </para>
/// <para>
/// 有意<b>不</b>收 <c>.txt</c> / <c>.utf</c> 这类泛文本后缀：mpv 自己会拿它们当 MicroDVD/mpl2 试猜，
/// 但用户往画面上拖的文本文件多半不是字幕，宁可放过也不误挂一条乱码轨。独占管线与外部 mpv.exe 的画面
/// 在 mpv 自建窗口里，拖拽由 mpv 原生的 <c>--drag-and-drop</c>（<c>mp_might_be_subtitle_file</c>）接住，
/// 不走这里；这张表只服务集成管线那一条 WinUI 亲自接拖拽的路。
/// </para>
/// </summary>
public static class SubtitleFile
{
    // 主流外挂字幕后缀：文本字幕（srt/ass/ssa/sub/vtt/smi/lrc/ttml/…）与图形字幕（idx+sub、sup/pgs）。
    // 与 mpv 认得的那一组对齐，取常见者；生僻容器（.mks 等）留给菜单里的文件选择，不在拖拽这条路上认。
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".webvtt",
        ".sup", ".pgs", ".smi", ".lrc", ".ttml", ".dfxp", ".sbv", ".stl", ".scc", ".rt"
    };

    /// <summary>这个路径是不是一个 mpv 能用 <c>sub-add</c> 加载的字幕文件，只看扩展名。</summary>
    public static bool IsSubtitle(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var extension = Path.GetExtension(path);
        return extension.Length > 0 && Extensions.Contains(extension);
    }

    /// <summary>
    /// 从拖进来的一堆路径里挑出字幕文件，保持原来的先后次序，并按完整路径去重（同一份拖两次只算一次）。
    /// 次序有意义：调用方按这个次序逐一 <c>sub-add</c>，最后一个成为当前字幕。
    /// </summary>
    public static IReadOnlyList<string> Filter(IEnumerable<string> paths) =>
        [.. paths.Where(IsSubtitle).Distinct(StringComparer.OrdinalIgnoreCase)];
}
