using EmbyNian.Emby;

namespace EmbyNian.Playback;

/// <summary>
/// 播放页左上角第二行那句文件信息 —— 「分辨率 · 视频编码 · 音频格式 · 组名」（用户令 2026-09-28
/// 「下方那一栏改为分辨率+视频编码+音频格式+组名」，在 2026-09-27「视频编码+音轨+组名」前置了分辨率）。
/// 两条管线共用：集成模式喂给 <c>PlayerViewModel.Subtitle</c>，独占模式经
/// <c>embynian-subline</c> 消息推给 uosc 顶栏的副标题（alt title）。
/// <para>
/// 「分辨率」取主视频轨的 宽 x 高（格式与媒体信息那格一致，见 <c>ItemDetail</c> 的「分辨率」段）；
/// 「音频格式」取这一版**主音轨的编码**（默认音轨优先，没有默认就取第一条），与「视频编码」对称、随文件
/// 静态、两条管线一致。组名走 <see cref="ReleaseGroup.FromFileName"/>（进度条中间那条画质读数尾部用的也是它）。
/// 四项逐项为空即跳过，全空则返回空串（集成模式那一格空串＝副标题框收起，见 <c>SubtitleVisibility</c>）。
/// </para>
/// </summary>
public static class PlaybackTitles
{
    /// <summary>分隔符与 <see cref="MediaSource.ToQualityLabel"/>／<c>CardSubtitle</c> 同款，全应用一致。</summary>
    private const string Separator = "  ·  ";

    public static string Subline(MediaSource? source)
    {
        if (source is null) return "";

        var parts = new List<string>(4);

        if (source.PrimaryVideoStream is { } video)
        {
            if (video is { Width: > 0, Height: > 0 })
                parts.Add($"{video.Width} x {video.Height}");

            if (video.Codec is { Length: > 0 } videoCodec)
                parts.Add(videoCodec.ToUpperInvariant());
        }

        if (PrimaryAudioCodec(source) is { Length: > 0 } audioCodec)
            parts.Add(audioCodec.ToUpperInvariant());

        if (ReleaseGroup.FromFileName(source.Path) is { Length: > 0 } group)
            parts.Add(group);

        return string.Join(Separator, parts);
    }

    /// <summary>
    /// 正在放这一版的主音轨编码：服务器点名的默认音轨优先（<see cref="MediaSource.DefaultAudioStreamIndex"/>），
    /// 找不到就退到第一条音轨。取编码而不是语言，是与「视频编码」对称、且不随「当前选了哪条轨」而变。
    /// </summary>
    private static string? PrimaryAudioCodec(MediaSource source)
    {
        var audio = source.DefaultAudioStreamIndex is { } index
            ? source.MediaStreams.FirstOrDefault(stream => stream.IsAudio && stream.Index == index)
            : null;
        audio ??= source.AudioStreams.FirstOrDefault();
        return audio?.Codec;
    }
}
