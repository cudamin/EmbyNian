using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 拖进画面的文件按后缀认不认字幕（<see cref="SubtitleFile"/>）。
/// <para>
/// 用户令：新增「把外挂字幕拖进画面自动挂上」。这张后缀表错得起、错了却在屏上看不出来 —— <c>sub-add</c>
/// 一个视频或图片文件，mpv 只是安静地拒绝，没有任何报错浮层；漏一个后缀就是一类字幕永远拖不进来。
/// <c>.ass</c> 与 <c>.ssa</c> 是两个后缀、WebVTT 既是 <c>.vtt</c> 又是 <c>.webvtt</c>，这些边界钉在这里；
/// 有意不收 <c>.txt</c> / <c>.utf</c> 这类泛文本后缀，宁可放过也不误挂一条乱码轨，这条同样钉住。
/// </para>
/// </summary>
internal static class SubtitleFileTests
{
    public static void Register()
    {
        // 主流字幕后缀逐个认，大小写不论；绝对路径与裸文件名同样认。
        Test("字幕后缀：各类都认得，大小写不论", () =>
        {
            foreach (var ext in new[]
            {
                ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".webvtt",
                ".sup", ".pgs", ".smi", ".lrc", ".ttml", ".dfxp", ".sbv", ".stl", ".scc", ".rt"
            })
            {
                Assert.True(SubtitleFile.IsSubtitle($@"D:\media\show{ext}"), $"该认得 {ext}");
                Assert.True(SubtitleFile.IsSubtitle($"show{ext.ToUpperInvariant()}"), $"大写也该认得 {ext}");
            }
        });

        // ass/ssa 是两个后缀、vtt/webvtt 是两个写法，缺一个就是一类字幕拖不进来 —— 钉住那两对。
        Test("字幕后缀：ass/ssa 与 vtt/webvtt 两对都在", () =>
        {
            Assert.True(SubtitleFile.IsSubtitle(@"C:\a.ass"));
            Assert.True(SubtitleFile.IsSubtitle(@"C:\a.ssa"));
            Assert.True(SubtitleFile.IsSubtitle(@"C:\a.vtt"));
            Assert.True(SubtitleFile.IsSubtitle(@"C:\a.webvtt"));
        });

        // 视频/音频/图片/泛文本一律不认：sub-add 挂这些上去 mpv 只会静静拒绝，宁可放过。
        Test("字幕后缀：视频/音频/图片/文本都不认", () =>
        {
            foreach (var path in new[]
            {
                @"D:\v.mkv", @"D:\v.mp4", @"D:\v.avi", @"D:\a.mp3", @"D:\a.flac",
                @"D:\p.jpg", @"D:\p.png", @"D:\t.txt", @"D:\t.nfo", @"D:\t.utf"
            })
            {
                Assert.False(SubtitleFile.IsSubtitle(path), $"不该认 {path}");
            }
        });

        // 认不出的边角：空、纯空白、没后缀、只有一个点结尾。
        Test("字幕后缀：空与无后缀给假", () =>
        {
            Assert.False(SubtitleFile.IsSubtitle(null));
            Assert.False(SubtitleFile.IsSubtitle(""));
            Assert.False(SubtitleFile.IsSubtitle("   "));
            Assert.False(SubtitleFile.IsSubtitle(@"D:\media\noext"));
            Assert.False(SubtitleFile.IsSubtitle(@"D:\media\trailingdot."));
        });

        // 从一堆拖进来的文件里挑字幕：只留字幕、保留原次序（次序决定谁最后 sub-add 成当前字幕）。
        Test("字幕筛选：只留字幕并保序", () =>
        {
            var picked = SubtitleFile.Filter(new[]
            {
                @"D:\ep.mkv", @"D:\ep.chs.srt", @"D:\ep.jpn.ass", @"D:\cover.jpg", @"D:\ep.eng.vtt"
            });

            Assert.Equal(3, picked.Count);
            Assert.Equal(@"D:\ep.chs.srt|D:\ep.jpn.ass|D:\ep.eng.vtt", string.Join("|", picked));
        });

        // 同一份拖两次只算一次，且按整路径大小写不敏感去重。
        Test("字幕筛选：按整路径去重、大小写不敏感", () =>
        {
            var picked = SubtitleFile.Filter(new[]
            {
                @"D:\media\ep.srt", @"D:\media\EP.SRT", @"D:\media\ep.ass"
            });

            Assert.Equal(2, picked.Count);
            Assert.Equal(@"D:\media\ep.srt|D:\media\ep.ass", string.Join("|", picked));
        });

        // 全不是字幕就给空表，调用方据此提示「没有字幕」而不是挂个寂寞。
        Test("字幕筛选：没有字幕给空表", () =>
        {
            Assert.Equal(0, SubtitleFile.Filter(new[] { @"D:\v.mkv", @"D:\a.mp3" }).Count);
            Assert.Equal(0, SubtitleFile.Filter([]).Count);
        });
    }
}
