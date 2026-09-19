using EmbyNian.Mpv;

namespace EmbyNian.Tests;

/// <summary>
/// 独占模式视频窗 Lua UI（uosc 嵌入版）的宿主侧决定：装配选项的内容、装箱路径的拼法、
/// embynian-* 消息契约的收窄。全部是纯函数 —— mpv 不在场也能钉死，这正是它们住在 Core 的理由。
/// </summary>
internal static class MpvUiTests
{
    internal static void Register()
    {
        // 装配选项：osc 与 mpv 自带的音量/跳转 OSD 条关掉（中心那根重复的大滑条）、
        // 无边框＋可拖动（标题与窗口按钮归 uosc 顶栏）、字体目录指向装箱、脚本指向入口。
        // 缺一项 uosc 就画不出图标、冒出重复控件或根本起不来，所以逐项点名，而不是只数个数。
        TestHarness.Test("Lua UI 装配选项：逐项点名", () =>
        {
            var options = MpvUi.Build(@"C:\app\mpv-ui\scripts\uosc\main.lua", @"C:\app\mpv-ui\fonts");

            Assert.Equal(8, options.Count);
            Assert.Equal(("osc", "no"), (options[0].Key, options[0].Value));
            Assert.Equal(("osd-bar", "no"), (options[1].Key, options[1].Value));
            Assert.Equal(("osd-on-seek", "no"), (options[2].Key, options[2].Value));
            Assert.Equal(("border", "no"), (options[3].Key, options[3].Value));
            Assert.Equal(("window-dragging", "yes"), (options[4].Key, options[4].Value));
            Assert.Equal(("osd-fonts-dir", @"C:\app\mpv-ui\fonts"), (options[5].Key, options[5].Value));
            Assert.Equal(("osd-font", "Microsoft YaHei"), (options[6].Key, options[6].Value));
            Assert.Equal(("scripts", @"C:\app\mpv-ui\scripts\uosc\main.lua"), (options[7].Key, options[7].Value));
        });

        // 装箱不齐 = 没有 Lua UI，播放照旧。返回 null 而不是半份配置：一个「osc 关了、
        // 脚本却没装上」的半装配比没有 UI 更难排障。
        TestHarness.Test("装箱不齐返回 null 不半装配", () =>
        {
            var empty = Path.Combine(Path.GetTempPath(), $"embynian-mpvui-{Guid.NewGuid():N}");
            Assert.Null(MpvUi.Bootstrap(empty));
        });

        // 真实仓库的装箱能被装配。测试进程的 AppContext.BaseDirectory 在 tests 的 bin 里，
        // 所以沿目录向上找仓库根（EmbyNian.sln 所在）：发布根的 mpv-ui 由 csproj 从
        // assets\mpv-ui 原样拷出，对 assets 目录做一次装配，等于把「发布根里的布局」
        // 与「assets 里的源」一起钉住 —— 结构一动这里先红。
        TestHarness.Test("真实仓库的装箱能被装配", () =>
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln")))
                directory = directory.Parent;

            Assert.NotNull(directory);
            Assert.NotNull(MpvUi.Bootstrap(Path.Combine(directory!.FullName, "assets")));
        });

        // 换集契约：只有 ±1 是合法值，别的（0、2、"next"、空串）都不许漏过去 ——
        // 那些会变成 int.Parse 的异常或一次方向不明的导航。
        TestHarness.Test("换集消息只认正负一", () =>
        {
            var up = VideoWindowContract.Parse(["embynian-episode", "1"]);
            var down = VideoWindowContract.Parse(["embynian-episode", "-1"]);
            var zero = VideoWindowContract.Parse(["embynian-episode", "0"]);
            var word = VideoWindowContract.Parse(["embynian-episode", "next"]);
            var lone = VideoWindowContract.Parse(["embynian-episode"]);

            Assert.Equal(VideoWindowContract.Episode, up?.Key);
            Assert.Equal("1", up?.Value);
            Assert.Equal(VideoWindowContract.Episode, down?.Key);
            Assert.Equal("-1", down?.Value);
            Assert.Null(zero);
            Assert.Null(word);
            Assert.Null(lone);
        });

        // 选集菜单契约：请求值保留即可通过；点选只认 1 起算的正整数序号——0、负数和
        // 非数字都是越界或垃圾，不许可漏进宿主的集导航。
        TestHarness.Test("选集请求与序号点选的契约", () =>
        {
            var request = VideoWindowContract.Parse(["embynian-episodes", ""]);
            Assert.Equal(VideoWindowContract.Episodes, request?.Key);

            var pick = VideoWindowContract.Parse(["embynian-episode-index", "3"]);
            Assert.Equal(VideoWindowContract.EpisodeIndex, pick?.Key);
            Assert.Equal("3", pick?.Value);

            Assert.Null(VideoWindowContract.Parse(["embynian-episode-index", "0"]));
            Assert.Null(VideoWindowContract.Parse(["embynian-episode-index", "-2"]));
            Assert.Null(VideoWindowContract.Parse(["embynian-episode-index", "third"]));
        });

        // 前缀收窄：uosc 自己的广播（uosc-version 等）与别的脚本的消息在同一个队列里，
        // 不带 embynian- 前缀的一律丢弃；带前缀但没登记的键也丢弃。
        TestHarness.Test("不带 embynian 前缀的消息被忽略", () =>
        {
            Assert.Null(VideoWindowContract.Parse(["uosc-version", "5.12.0"]));
            Assert.Null(VideoWindowContract.Parse([]));
            Assert.Null(VideoWindowContract.Parse(["embynian-something", "x"]));
        });

        // 就绪握手原样通过：值是版本号字符串，宿主只负责把它记档与进日志，不做二次解释。
        TestHarness.Test("就绪握手通过契约", () =>
        {
            var ready = VideoWindowContract.Parse(["embynian-ready", "5.12.0"]);
            Assert.Equal(VideoWindowContract.Ready, ready?.Key);
            Assert.Equal("5.12.0", ready?.Value);
        });

        // Seek 契约通过（当前 uosc 不用它，但键已登记）：宿主侧的取值合法性由调用方夹取，
        // 契约只负责「是不是这个键、有没有值」。
        TestHarness.Test("Seek 契约通过并带值", () =>
        {
            var seek = VideoWindowContract.Parse(["embynian-seek", "0.25"]);
            Assert.Equal(VideoWindowContract.Seek, seek?.Key);
            Assert.Equal("0.25", seek?.Value);
        });
    }
}
