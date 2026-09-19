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
        // 无边框＋可拖动（标题与窗口按钮归 uosc 顶栏）、字体目录指向装箱、脚本指向 uosc 目录。
        // 缺一项 uosc 就画不出图标、冒出重复控件或根本起不来，所以逐项点名，而不是只数个数。
        // scripts 必须是目录不是 main.lua：交文件 mpv 把脚本命名成 main，控制条每个按钮的
        // script-binding uosc/… 就全找不到、整排静默失效（2026-09-19 实测）。这一行钉死它。
        TestHarness.Test("Lua UI 装配选项：逐项点名", () =>
        {
            var options = MpvUi.Build(@"C:\app\mpv-ui\scripts\uosc", @"C:\app\mpv-ui\fonts");

            Assert.Equal(8, options.Count);
            Assert.Equal(("osc", "no"), (options[0].Key, options[0].Value));
            Assert.Equal(("osd-bar", "no"), (options[1].Key, options[1].Value));
            Assert.Equal(("osd-on-seek", "no"), (options[2].Key, options[2].Value));
            Assert.Equal(("border", "no"), (options[3].Key, options[3].Value));
            Assert.Equal(("window-dragging", "yes"), (options[4].Key, options[4].Value));
            Assert.Equal(("osd-fonts-dir", @"C:\app\mpv-ui\fonts"), (options[5].Key, options[5].Value));
            Assert.Equal(("osd-font", "Microsoft YaHei"), (options[6].Key, options[6].Value));
            // 目录，非 main.lua —— 脚本名才会是 uosc，uosc/… 绑定才解析得到。
            Assert.Equal(("scripts", @"C:\app\mpv-ui\scripts\uosc"), (options[7].Key, options[7].Value));
            Assert.False(options[7].Value.Contains("main.lua"), "scripts 交的必须是目录，不是 main.lua");
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
            var options = MpvUi.Bootstrap(Path.Combine(directory!.FullName, "assets"));
            Assert.NotNull(options);
            // scripts 指向 uosc 目录本身、且不是某个 .lua 文件 —— 这是「按钮能点」的前提。
            var scripts = options![^1];
            Assert.Equal("scripts", scripts.Key);
            Assert.True(scripts.Value.EndsWith(Path.Combine("mpv-ui", "scripts", "uosc")),
                $"scripts 应指向 uosc 目录，实为 {scripts.Value}");
            Assert.False(scripts.Value.Contains(".lua"), "scripts 交的必须是目录，不是 .lua 文件");
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

        // 选集请求的闸门：同一段窗口里只放一条过去。2026-09-19 那次脚本自激，宿主 90 秒里收到 32 万条
        // 请求、逐条回推 open-menu，界面卡到点不动 —— 这个闸门就是那一刻的活口，三条判据都得钉住。
        TestHarness.Test("选集菜单请求闸门：窗口内挡住、窗口边界放行", () =>
        {
            var gate = new EpisodeMenuRequestGate(TimeSpan.FromMilliseconds(400));
            var start = new DateTime(2026, 9, 19, 19, 0, 0, DateTimeKind.Utc);

            Assert.True(gate.TryAccept(start), "第一条必须放行");
            Assert.False(gate.TryAccept(start), "同毫秒的连发要挡住");
            Assert.False(gate.TryAccept(start.AddMilliseconds(399)), "窗口内要挡住");
            Assert.True(gate.TryAccept(start.AddMilliseconds(400)), "窗口边界＝放行");
            Assert.Equal(2, gate.Suppressed, "被挡下的条数要数得出来");
        });

        // 被挡下不是静默丢弃：第一次立刻记一条，之后每秒最多一条（否则日志自己也成刷屏）。
        TestHarness.Test("选集菜单请求闸门：日志第一次立刻响，之后每秒最多一条", () =>
        {
            var gate = new EpisodeMenuRequestGate();
            var start = new DateTime(2026, 9, 19, 19, 0, 0, DateTimeKind.Utc);

            Assert.True(gate.ShouldReport(start), "第一次被挡下就该出声");
            Assert.False(gate.ShouldReport(start.AddMilliseconds(999)), "一秒内不重复");
            Assert.True(gate.ShouldReport(start.AddSeconds(1)), "过一秒再响一次");
        });

        // 默认间隔的量级：自激那次约 1500 条/秒，一秒里只该过去个位数；人手点击（最快 100ms 级）
        // 一条都不该被吃掉。
        TestHarness.Test("选集菜单请求闸门：一秒的刷屏只放个位数过去", () =>
        {
            var gate = new EpisodeMenuRequestGate();
            var start = new DateTime(2026, 9, 19, 19, 0, 0, DateTimeKind.Utc);
            var accepted = 0;

            for (var ms = 0; ms < 1000; ms++)
                if (gate.TryAccept(start.AddMilliseconds(ms))) accepted++;

            Assert.Equal(3, accepted, "400ms 一条 → 0/400/800ms 三条");
            Assert.Equal(997, gate.Suppressed);
        });

        // 名字不许撞车 —— 2026-09-19 那场刷屏的根因：mpv 把一条 script-message 也派给**同名**的脚本
        // 绑定，于是「按钮发出的消息」把「按钮自己」又叫醒一次，一条变一千三百条/秒。规矩写进了 uosc
        // 的 EMBYNIAN[ui-bind] 注释，这里对着仓库源码数一遍：每个 bind_command 的名字都不许等于任何
        // embynian-* 契约键。将来谁再起一个同名绑定，这条先红。
        TestHarness.Test("uosc 绑定名与宿主消息名不许同名", () =>
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);

            var uosc = Path.Combine(directory!.FullName, "assets", "mpv-ui", "scripts", "uosc");
            var contracts = new[]
            {
                VideoWindowContract.Ready, VideoWindowContract.Seek, VideoWindowContract.Episode,
                VideoWindowContract.Episodes, VideoWindowContract.EpisodeIndex,
            };
            var bindings = new List<string>();

            foreach (var file in Directory.EnumerateFiles(uosc, "*.lua", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match match in
                    System.Text.RegularExpressions.Regex.Matches(text, @"bind_command\(\s*'([^']+)'"))
                    bindings.Add(match.Groups[1].Value);
            }

            Assert.True(bindings.Count > 0, "一个 bind_command 都没扫到 —— 正则或目录结构变了");
            foreach (var contract in contracts)
                Assert.False(bindings.Contains(contract),
                    $"{contract} 既是脚本绑定名又是宿主消息名：script-message 会把消息喂回绑定，自激刷屏");
        });

        // 2026-09-19（用户令）：双击画面＝全屏/还原，不许顺带开始/暂停；滚轮调音量不许带出 mpv
        // 左上角那行「Volume」OSD，反馈改走 uosc 右侧的音量条。两条都落在 EMBYNIAN 补丁里，
        // 而补丁最怕「升级 uosc 时重打清单漏条」——这里对着源码钉死。行为级判据与前后读数：
        // work/probe-input-{before2,after}.txt（vo=null 命令账）与
        // work/probe-input-osd-{before2,after}.txt（真窗口实拍，黑底上量左上/右侧两块区域的亮像素）。
        TestHarness.Test("独占模式输入补丁：双击闸与 no-osd 音量都在", () =>
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);

            var uosc = Path.Combine(directory!.FullName, "assets", "mpv-ui", "scripts", "uosc");
            var main = File.ReadAllText(Path.Combine(uosc, "main.lua"));
            var utils = File.ReadAllText(Path.Combine(uosc, "lib", "utils.lua"));
            var volume = File.ReadAllText(Path.Combine(uosc, "elements", "Volume.lua"));

            // 双击闸：那一拍押后到 mpv 的双击窗口（input-doubleclick-time）之外才发，窗口里再来一下就撤。
            // 撤销必须挂在第二拍的「按下」上（cursor:on('primary_down')）——真窗口实测：独占全屏切换
            // 会把 uosc 的光标挪走，第二拍的「松开」过不了位置闸，撤在松开＝撤销永远轮不到跑
            // （work/probe-doubleclick-real-trace.txt：四条光标事件全到、提交=1、撤销=0、pause 照翻）。
            Assert.True(main.Contains("embynian_click_pause_pending"), "双击闸丢了：main.lua 里没有押后状态");
            Assert.True(main.Contains("input-doubleclick-time"), "双击闸丢了：窗口没跟 mpv 的双击窗口对齐");
            Assert.True(main.Contains("embynian_click_pause_cancel"), "双击闸丢了：没有撤销入口");
            Assert.True(main.Contains("cursor:on('primary_down'"), "双击闸丢了：撤销没挂在第二拍的按下上");
            // 滚轮＝音量：兜底命中区要在 render 里登记（最低优先级，元素先命中），命令必须 no-osd。
            Assert.True(main.Contains("embynian_wheel_volume_zone"), "滚轮音量丢了：main.lua 里没有兜底命中区");
            Assert.True(main.Contains("'no-osd', 'add', 'volume'"), "滚轮音量没走 no-osd：左上角的 Volume OSD 会回来");
            Assert.True(utils.Contains("embynian_wheel_volume_zone()"), "滚轮音量命中区没在 render 里登记");
            // 音量条自己改音量（拖/滚那条）同样不落 mpv 的 OSD —— uosc 自己画着数值。
            Assert.True(volume.Contains("'no-osd', 'set', 'volume'"), "音量条改音量没走 no-osd");
        });
    }
}
