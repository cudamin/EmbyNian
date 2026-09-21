using EmbyNian.Playback;

namespace EmbyNian.Tests;

/// <summary>
/// 「加载遮罩（背景图）什么时候可以揭」这条判据的两半：纯函数本身，以及它那四处接线还在不在。
/// <para>
/// 事故形状（2026-09-21 用户报「背景图 → 一段黑屏 → 正片」）：遮罩原来在 <c>file-loaded</c> 那一拍就揭，
/// 而「文件打开了」与「有画面可看」之间隔着解码首帧与着色器编译 —— 实测本地素材 110ms 对 485ms，
/// 走网络转码那一档差到秒级。揭早了，底下那条交换链上还没有任何一帧，露出来的就是黑。
/// </para>
/// <para>
/// 前两条是纯函数（<c>mpv</c> 不在场也能钉死，这正是 <see cref="PictureReveal"/> 住在 Core 的理由）；
/// 后一条是源码守卫 —— 「mpv 那句话有没有被转交到画面宿主」没有任何编译器会说话，删掉一行照样全绿。
/// </para>
/// </summary>
internal static class PictureRevealTests
{
    internal static void Register()
    {
        // 主判据：mpv 没说「首帧已经交给视频输出」之前，押过多少帧都不算数。建链那一下 mpv 会连押
        // 两三帧空画面（实测挂链那一刻计数就是 2），而客户端的自动全屏又恰好在那一段里重建链 ——
        // 只认 Present 会在启动期当场为真，等于什么都没拦。
        TestHarness.Test("遮罩判据：mpv 没说播放开始，押过多少帧都不算", () =>
        {
            Assert.False(PictureReveal.Ready(playbackStarted: false, swapChainAttached: true, presentsSinceAttach: 0),
                "挂链了但一帧没押");
            Assert.False(PictureReveal.Ready(false, true, 3), "空链上那两三帧不许当画面");
            Assert.False(PictureReveal.Ready(false, false, 0), "整场都还没开始");
        });

        // 两半齐了才算：mpv 的话 ＋ 这条链确实收得下画面。
        TestHarness.Test("遮罩判据：mpv 的话与这条链的账都要", () =>
        {
            Assert.True(PictureReveal.Ready(true, true, 1), "mpv 说了、链也押过了");
            Assert.False(PictureReveal.Ready(true, true, 0), "mpv 说了但这链还没被押过一帧（等它一下）");
            Assert.False(PictureReveal.Ready(true, true, -1), "量不到 Present 计数时不认");
            // 没有链＝这一场本来就没有画面要等（音频文件；以及画面在 mpv 自建窗口里的独立管线那一档）。
            // 这一条少了，那些文件会一直等到 6 秒的那张网，遮罩上多停六秒。
            Assert.True(PictureReveal.Ready(true, false, 0), "没有交换链的那一档：mpv 说开始就是答案");
        });

        // 这一位是「事件」不是「状态」，靠状态快照发布出去（PlayerViewModel 只转发差异）。
        // DiffersFrom 漏了它，那一句发布会被吞掉 —— 页面永远等不到，遮罩一直挂到 6 秒的网。
        TestHarness.Test("PictureStarted 必须算进状态差异，否则发布被吞掉", () =>
        {
            var before = new PlayerStatus();
            var after = before with { PictureStarted = true };

            Assert.True(after.DiffersFrom(before), "翻真是一次改变");
            Assert.True(before.DiffersFrom(after), "反向也算（换会话时从头等）");
            Assert.False(before.DiffersFrom(new PlayerStatus()), "没动就是没动");
        });

        // 源码守卫：四处接线（少任何一处，屏上的症状都是「背景图之后一段黑」）。
        TestHarness.Test("遮罩判据的四处接线都在", () =>
        {
            var core = Source("src", "EmbyNian.Core", "Playback");
            var shell = Source("src", "EmbyNian.Shell");

            var backend = core["LibMpvBackend.cs"];
            var unusedArray = backend[backend.IndexOf("Span<int> unused", StringComparison.Ordinal)..];
            unusedArray = unusedArray[..unusedArray.IndexOf(']')];
            Assert.DoesNotContain("EventPlaybackRestart", unusedArray,
                "playback-restart 又被停订了 —— 那是「首帧上屏」唯一的通知");
            Assert.Contains("LibMpvNative.EventPlaybackRestart", backend.Replace(unusedArray, ""));
            Assert.Contains("PictureStarted = true", backend);

            var target = shell[Path.Combine("Windowing", "CompositionVideoTarget.cs")];
            Assert.Contains("PictureReveal.Ready(", target);
            Assert.Contains("GetPresentCountSlot = 17", target);
            Assert.Contains("BeginPictureWait", target);

            var cover = shell[Path.Combine("Views", "PlayerPage.Cover.cs")];
            Assert.Contains("_videoTarget.HasPicture", cover);
            Assert.Contains("BeginPictureWait()", cover);

            var page = shell[Path.Combine("Views", "PlayerPage.xaml.cs")];
            Assert.Contains("if (status.PictureStarted) NotePictureStarted();", page);
        });
    }

    /// <summary>仓库根（<c>EmbyNian.sln</c> 所在）下按子目录拼出来的那些源码文件，按文件名索引。</summary>
    private static Dictionary<string, string> Source(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var folder = Path.Combine([directory!.FullName, .. parts]);
        Assert.True(Directory.Exists(folder), $"找不到源码目录 {folder}");

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            files[Path.GetRelativePath(folder, file)] = File.ReadAllText(file);
        return files;
    }
}
