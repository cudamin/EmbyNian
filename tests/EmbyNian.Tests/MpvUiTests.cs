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
        // 无边框＋可拖动（标题与窗口按钮归 uosc 顶栏）、字体目录指向装箱、押后窗口与集成模式同一把尺、
        // 脚本指向 uosc 目录。缺一项 uosc 就画不出图标、冒出重复控件或根本起不来，所以逐项点名，
        // 而不是只数个数。scripts 必须是目录不是 main.lua：交文件 mpv 把脚本命名成 main，控制条每个按钮的
        // script-binding uosc/… 就全找不到、整排静默失效（2026-09-19 实测）。这一行钉死它。
        TestHarness.Test("Lua UI 装配选项：逐项点名", () =>
        {
            var options = MpvUi.Build(@"C:\app\mpv-ui\scripts\uosc", @"C:\app\mpv-ui\fonts");

            Assert.Equal(9, options.Count);
            Assert.Equal(("osc", "no"), (options[0].Key, options[0].Value));
            Assert.Equal(("osd-bar", "no"), (options[1].Key, options[1].Value));
            Assert.Equal(("osd-on-seek", "no"), (options[2].Key, options[2].Value));
            Assert.Equal(("border", "no"), (options[3].Key, options[3].Value));
            Assert.Equal(("window-dragging", "yes"), (options[4].Key, options[4].Value));
            Assert.Equal(("osd-fonts-dir", @"C:\app\mpv-ui\fonts"), (options[5].Key, options[5].Value));
            Assert.Equal(("osd-font", "Microsoft YaHei"), (options[6].Key, options[6].Value));
            // 押后窗口（用户令 2026-09-23）：uosc 那颗兜底命中区读它，mpv 认双击也用它 ——
            // 值与集成模式那份押后是同一个常量（PlaybackTests 的「点画面」族点名了这个等式）。
            Assert.Equal(("input-doubleclick-time", "300"), (options[7].Key, options[7].Value));
            // 目录，非 main.lua —— 脚本名才会是 uosc，uosc/… 绑定才解析得到。
            Assert.Equal(("scripts", @"C:\app\mpv-ui\scripts\uosc"), (options[8].Key, options[8].Value));
            Assert.False(options[8].Value.Contains("main.lua"), "scripts 交的必须是目录，不是 main.lua");
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

        // 右键菜单呼出键（2026-09-22 用户令「参考集成模式」）：config=no 之下 input.conf 不读、uosc 默认不绑键，
        // 所以「右键点画面出菜单」这件事得宿主自己补。绑的不是 uosc 自带的精简菜单，而是
        // embynian-ui-picture-menu —— 它向宿主要 PlayerMenuCatalog（集成模式右键那张同一份树）。这里钉三处：
        // MenuKeys 发的是 MBTN_RIGHT / MENU → script-binding uosc/embynian-ui-picture-menu；它指向的绑定在脚本里
        // 真的登记了；而那条绑定发的消息是 embynian-picture-menu（与绑定名不同名，否则自激刷屏）。
        TestHarness.Test("右键菜单呼出键：MBTN_RIGHT/MENU 指向真实的画面菜单绑定", () =>
        {
            var keys = MpvUi.MenuKeys();
            Assert.Equal(2, keys.Count);
            Assert.Equal(("MBTN_RIGHT", "script-binding uosc/embynian-ui-picture-menu"), (keys[0].Key, keys[0].Value));
            Assert.Equal(("MENU", "script-binding uosc/embynian-ui-picture-menu"), (keys[1].Key, keys[1].Value));

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);

            var main = File.ReadAllText(Path.Combine(
                directory!.FullName, "assets", "mpv-ui", "scripts", "uosc", "main.lua"));
            Assert.True(main.Contains("bind_command('embynian-ui-picture-menu'"),
                "画面菜单绑定丢了：补发的 keybind 会指向一条不存在的绑定");
            Assert.True(main.Contains("embynian_notify('embynian-picture-menu'"),
                "画面菜单向宿主要数据的消息丢了");
        });

        // 画面菜单契约：请求值保留即可通过；点选只认 1 起算的正整数序号（对着 PlayerMenuCatalog.Commands 的次序）——
        // 与选集/版本同一套判据，因为「差一位」在这里跑到的是旁边那一行命令，屏上未必看得出点错了。
        TestHarness.Test("画面菜单请求与序号点选的契约", () =>
        {
            var request = VideoWindowContract.Parse(["embynian-picture-menu", ""]);
            Assert.Equal(VideoWindowContract.PictureMenu, request?.Key);

            var pick = VideoWindowContract.Parse(["embynian-menu-index", "5"]);
            Assert.Equal(VideoWindowContract.MenuIndex, pick?.Key);
            Assert.Equal("5", pick?.Value);

            Assert.Null(VideoWindowContract.Parse(["embynian-menu-index", "0"]));
            Assert.Null(VideoWindowContract.Parse(["embynian-menu-index", "-1"]));
            Assert.Null(VideoWindowContract.Parse(["embynian-menu-index", "reset"]));
        });

        // 画面菜单的数据源就是 PlayerMenuCatalog（集成模式右键那张树）。Commands 是宿主推送菜单时给每条命令行
        // 编号、点中后回宿主 RunMenuNodeAsync 要用的那份展平表：必须与 Flatten 的 DFS 先序一致、全是命令叶子、
        // 且非空 —— 序号一旦与序列化那头的编号次序不一致，点一行跑的就是另一行的命令。
        TestHarness.Test("画面菜单命令表：与 Flatten 先序一致、全是命令叶子", () =>
        {
            var commands = PlayerMenuCatalog.Commands;
            Assert.True(commands.Count > 0, "命令表不该是空的");

            var expected = PlayerMenuCatalog.Flatten(PlayerMenuCatalog.Root)
                .Where(node => node.Kind == PlayerMenuKind.Command)
                .ToList();

            Assert.Equal(expected.Count, commands.Count);
            for (var i = 0; i < expected.Count; i++)
                Assert.True(ReferenceEquals(expected[i], commands[i]),
                    $"第 {i + 1} 项与 Flatten 先序对不上：序列化编号会和这份表错位");
        });

        // 打勾的判据：一行「是不是当前这一档」怎么读 mpv 的回答。四种读法各钉一遍，因为读错比不读更坏 ——
        // 屏上会打在错的那一行。Equals 大小写不敏感；Bool 只认 yes/true/非零数（auto 不算开）；Positive 认正数
        // （panscan 0 关、1 开）；Ratio 把 video-aspect-override 的「1.777778」当 16:9，「no」对着负数（关闭态）。
        TestHarness.Test("画面菜单打勾判据：四种读法各就各位", () =>
        {
            var hwdec = new PlayerMenuState("hwdec", PlayerMenuMatch.Equals, "auto-safe", Radio: true);
            Assert.True(hwdec.IsChecked("auto-safe"));
            Assert.True(hwdec.IsChecked("AUTO-SAFE"), "大小写不敏感");
            Assert.False(hwdec.IsChecked("no"));
            Assert.False(hwdec.IsChecked(null), "读不到＝不打勾");
            Assert.False(hwdec.IsChecked(""));

            var flag = new PlayerMenuState("interpolation", PlayerMenuMatch.Bool);
            Assert.True(flag.IsChecked("yes"));
            Assert.True(flag.IsChecked("1"));
            Assert.False(flag.IsChecked("no"));
            Assert.False(flag.IsChecked("0"));
            Assert.False(flag.IsChecked("auto"), "auto 不当作「开」");

            var panscan = new PlayerMenuState("panscan", PlayerMenuMatch.Positive);
            Assert.True(panscan.IsChecked("1.000000"));
            Assert.False(panscan.IsChecked("0.000000"));

            var wide = new PlayerMenuState("video-aspect-override", PlayerMenuMatch.Ratio, "16:9", Radio: true);
            Assert.True(wide.IsChecked("1.777778"), "1.777778 就是 16:9");
            Assert.False(wide.IsChecked("1.333333"), "4:3 不是 16:9");
            Assert.False(wide.IsChecked("-1.000000"), "关闭态不是某个具体比例");

            var off = new PlayerMenuState("video-aspect-override", PlayerMenuMatch.Ratio, "no", Radio: true);
            Assert.True(off.IsChecked("-1.000000"), "负数＝默认值（关）打勾");
            Assert.False(off.IsChecked("1.777778"), "设了 16:9，默认值那一行就不打勾");
        });

        // 读什么属性由 CheckProperties 汇总，去重（解码方式八行共用一次 hwdec）。核心不变式：一条 Equals 的
        // 单选行，「打勾用的值」必须等于「set 写下去的值」—— 一旦某天有人改了标签或抄错一处，点一行却打勾在
        // 另一行（或哪一行都不亮），这条先红。用户当初问的正是这个：解码方式开了哪个得看得出来。
        TestHarness.Test("画面菜单打勾：属性表去重、单选行「打勾＝所设」", () =>
        {
            var properties = PlayerMenuCatalog.CheckProperties;
            Assert.Equal(properties.Distinct(StringComparer.Ordinal).Count(), properties.Count);
            Assert.True(properties.Contains("hwdec"), "hwdec 该在打勾属性表里");
            Assert.True(properties.Contains("audio-channels"), "audio-channels 该在打勾属性表里");
            Assert.True(properties.Contains("panscan"), "panscan 该在打勾属性表里");
            Assert.True(properties.Contains("loop-file"), "loop-file 该在打勾属性表里");

            // 每一条 set P V 的单选行：State 认的值就是命令写的值，属性也对得上。
            foreach (var node in PlayerMenuCatalog.Commands)
            {
                if (node.State is not { Radio: true, Match: PlayerMenuMatch.Equals } state) continue;

                var command = node.Commands[0];
                Assert.Equal("set", command[0]);
                Assert.Equal(state.Property, command[1]);
                Assert.Equal(state.Value, command[2]);
            }

            // 就着一次读数点勾：hwdec 读回 d3d11va，则只有 d3d11va 那一行亮，别的 hwdec 行都不亮。
            var values = new Dictionary<string, string?>(StringComparer.Ordinal) { ["hwdec"] = "d3d11va" };
            var hwdecRows = PlayerMenuCatalog.Commands
                .Where(node => node.State?.Property == "hwdec").ToList();

            Assert.True(hwdecRows.Count >= 5, "解码方式该有多行");
            Assert.Equal(1, hwdecRows.Count(node => node.IsCheckedBy(values)));
            Assert.True(hwdecRows.Single(node => node.Label.Contains("d3d11va", StringComparison.Ordinal)
                                                  && !node.Label.Contains("copy", StringComparison.Ordinal))
                .IsCheckedBy(values), "读回 d3d11va，就该 d3d11va 那一行亮");
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
            var gate = new MenuRequestGate(TimeSpan.FromMilliseconds(400));
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
            var gate = new MenuRequestGate();
            var start = new DateTime(2026, 9, 19, 19, 0, 0, DateTimeKind.Utc);

            Assert.True(gate.ShouldReport(start), "第一次被挡下就该出声");
            Assert.False(gate.ShouldReport(start.AddMilliseconds(999)), "一秒内不重复");
            Assert.True(gate.ShouldReport(start.AddSeconds(1)), "过一秒再响一次");
        });

        // 默认间隔的量级：自激那次约 1500 条/秒，一秒里只该过去个位数；人手点击（最快 100ms 级）
        // 一条都不该被吃掉。
        TestHarness.Test("选集菜单请求闸门：一秒的刷屏只放个位数过去", () =>
        {
            var gate = new MenuRequestGate();
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
                VideoWindowContract.Versions, VideoWindowContract.VersionIndex,
                VideoWindowContract.PictureMenu, VideoWindowContract.MenuIndex,
                // 方向相反的那一条也数进来：宿主 → uosc 的消息照样不许与脚本绑定同名 ——
                // mpv 把 script-message 派给同名绑定是不分方向的。
                VideoWindowContract.VersionCount,
                VideoWindowContract.EpisodeCount,
                // 跳过片头/片尾那两条：uosc→宿主的 SkipTake（按钮点击）与宿主→uosc 的 SkipOffer（推文案）。
                VideoWindowContract.SkipTake,
                VideoWindowContract.SkipOffer,
                // 左上角第二行的文件信息（宿主→uosc，方向不影响这条硬规矩）。
                VideoWindowContract.Subline,
                // 右键菜单统一（2026-09-29）添的三条：跳过档位、连播开关、播放信息 —— 「更多」那一棵
                // 在独占模式的同款行点中回宿主的 value。
                VideoWindowContract.SkipMode,
                VideoWindowContract.AutoPlayNext,
                VideoWindowContract.MediaInfo,
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
            // 叫醒窗口的那一下不作数（用户令 2026-09-23）：判据是 mpv 的 focused，命中在兜底命中区里。
            Assert.True(main.Contains("embynian_click_pause_waking"), "点击暂停丢了：没有「叫醒窗口的那一下」这道闸");
            Assert.True(main.Contains("observe_property('focused'"), "点击暂停丢了：没有订阅 focused，叫醒那一下判不出来");
            // 滚轮＝音量：兜底命中区要在 render 里登记（最低优先级，元素先命中），命令必须 no-osd。
            Assert.True(main.Contains("embynian_wheel_volume_zone"), "滚轮音量丢了：main.lua 里没有兜底命中区");
            Assert.True(main.Contains("'no-osd', 'add', 'volume'"), "滚轮音量没走 no-osd：左上角的 Volume OSD 会回来");
            Assert.True(utils.Contains("embynian_wheel_volume_zone()"), "滚轮音量命中区没在 render 里登记");
            // 音量条自己改音量（拖/滚那条）同样不落 mpv 的 OSD —— uosc 自己画着数值。
            Assert.True(volume.Contains("'no-osd', 'set', 'volume'"), "音量条改音量没走 no-osd");
        });

        // 2026-09-29（用户令「只有鼠标停在控件，进度条和上方的按钮还有音量条上的时候才不隐藏鼠标，触发
        // 渐变的时候不隐藏控件，但是要隐藏鼠标」）：判据是**控件本体的命中区**，不是唤出带。这一块补丁
        // 落在 lib/cursor.lua 里，同样属于「升级 uosc 时最容易漏重打」的那一类 —— 对着源码钉死；
        // 行为级判据是 work/probe-hold-visible-{before,hold}.txt（真窗口 + 真 libmpv，四个景各停四秒，
        // 逐拍读 GetCursorInfo 的 showing 位与 uosc 侧的 cursor-autohide）。
        TestHarness.Test("独占模式光标保活补丁：压在控件本体上不藏、唤出带里照藏", () =>
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);

            var uosc = Path.Combine(directory!.FullName, "assets", "mpv-ui", "scripts", "uosc");
            var cursor = File.ReadAllText(Path.Combine(uosc, "lib", "cursor.lua"));

            Assert.True(cursor.Contains("EMBYNIAN[cursor-hold]"), "光标保活补丁丢了：没有槽名标记");
            // 判据是命中区：压在按钮/滑杆/进度条那一块上才不收。2026-09-29 晚修：on_control 必须**自己遍历**
            // zones 而不能用 find_zone —— EMBYNIAN 的两条兜底区（点画面暂停／滚轮音量）罩着**整个画布**，
            // find_zone 会把它们当成控件，指针停在空白画面上也 hold，cursor-autohide 恒为 no、光标永不藏
            // （work/probe-hold-visible-repro.txt 实锤：激活那一拍 autohide 就翻 no）。
            Assert.True(cursor.Contains("function cursor:on_control()"), "光标保活补丁丢了：没有本体命中这一问");
            // 反面同样要钉：**不许再按 proximity 判** —— 那是唤出带，正是用户要「藏鼠标」的地方。
            Assert.False(cursor.Contains("element.proximity > 0"),
                "光标保活补丁退回了按唤出带判：带子里也会把光标钉住，与用户令相反");
            // 也不许把「离开时淡出哪四块」那份名单拿来当判据（2026-09-28 那版就是它，见 work 里的
            // 旧证据）。`config.cursor_leave_fadeout_elements` 本身在上游的淡出循环里合法存在，
            // 所以这里钉的是「补丁的判据函数体里没有它」——
            var body = cursor.Substring(
                cursor.IndexOf("function cursor:on_control()", StringComparison.Ordinal));
            body = body[..body.IndexOf("\n\tend", StringComparison.Ordinal)];
            Assert.False(body.Contains("cursor_leave_fadeout_elements"),
                "光标保活补丁又拿唤出带的名单当判据了：那一份名单管的是「离开时淡出哪几块」");
            Assert.False(body.Contains("proximity"),
                "光标保活补丁又拿 proximity 当判据了：那是唤出带，不是本体");
            Assert.False(body.Contains("find_zone"),
                "on_control 不许用 find_zone：两条整画布兜底区会被当成控件，光标永不藏（2026-09-29 用户报）");
            Assert.True(body.Contains("embynian_fallback"),
                "on_control 必须跳过带 embynian_fallback 标记的兜底区（整画布的点画面暂停／滚轮音量区不是控件）");

            // 标记的另一头：main.lua 里两条兜底 hitbox 必须真的带着 embynian_fallback —— 判据跳过它靠的就是
            // 这个字段，重打补丁时漏了标记＝「光标永不藏」那条病根原样回来。
            var main = File.ReadAllText(Path.Combine(uosc, "main.lua"));
            var fallbackMarks = System.Text.RegularExpressions.Regex.Matches(main, "embynian_fallback = true").Count;
            Assert.True(fallbackMarks >= 2,
                $"main.lua 里两条兜底命中区必须带 embynian_fallback = true 标记，现在只数到 {fallbackMarks} 处");
            // 钉成 no、离开还原 —— 两条都要在：只有一边的话，光标不是永不藏就是白补。
            Assert.True(cursor.Contains("cursor.autohide_base"), "光标保活补丁丢了：没记装配时的原值，还原不了");
            Assert.True(cursor.Contains("mp.set_property('cursor-autohide'"), "光标保活补丁丢了：没有改 mpv 的属性");
            // 落点必须在 queue_autohide 的**第一行**：装箱默认 options.autohide=false，排在
            // `is_autohide_allowed()` 那道闸之后就永远跑不到（重打清单时最容易漏掉的正是这条）。
            var queue = cursor.IndexOf("function cursor:queue_autohide()", StringComparison.Ordinal);
            var refresh = cursor.IndexOf("self:refresh_hold()", queue + 1, StringComparison.Ordinal);
            var guard = cursor.IndexOf("if self:is_autohide_allowed()", queue + 1, StringComparison.Ordinal);
            Assert.True(queue >= 0 && refresh > queue && guard > refresh,
                "光标保活补丁丢了：refresh_hold 没排在 is_autohide_allowed 那道闸前面");
        });

        // 版本菜单契约（2026-09-20）：请求值保留即可通过；点选只认 1 起算的正整数序号 —— 与选集同一套判据，
        // 因为「差一位」在这里换到的是旁边那一版文件，屏上看起来完全正常（片子还是那部片子）。
        TestHarness.Test("版本请求与序号点选的契约", () =>
        {
            var request = VideoWindowContract.Parse(["embynian-versions", ""]);
            Assert.Equal(VideoWindowContract.Versions, request?.Key);

            var pick = VideoWindowContract.Parse(["embynian-version-index", "2"]);
            Assert.Equal(VideoWindowContract.VersionIndex, pick?.Key);
            Assert.Equal("2", pick?.Value);

            Assert.Null(VideoWindowContract.Parse(["embynian-version-index", "0"]));
            Assert.Null(VideoWindowContract.Parse(["embynian-version-index", "-1"]));
            Assert.Null(VideoWindowContract.Parse(["embynian-version-index", "first"]));

            // 宿主 → uosc 的那一条不进 Parse：script-message 是广播，自己发出去的东西原则上会回到
            // 自己的事件队列 —— 认了它，宿主就成了「自己应自己」的第二个自激源（第一个见 MpvUiTests
            // 的绑定同名那条）。它只该由 uosc 那头的 register_script_message 收到。
            Assert.Null(VideoWindowContract.Parse([VideoWindowContract.VersionCount, "2"]));
        });

        // 2026-09-20（用户令「在播放页面切换不同版本」）与 2026-09-23 两轮（「独占模式也要有切换版本的
        // 按钮」「菜单按钮改成右键那个画面菜单」「音频按钮只有一条音轨时也在」，随后「把选集和选版本的
        // 按钮移动到左下」「把字幕和音轨按钮往左移动一些，让音轨按钮和全屏/窗口按钮相隔一个按钮的空位」
        // 「只有一个版本的情况下不显示…」；2026-09-24 又一条「把独占模式下字幕和音频的按钮位置互换」）：独占模式的入口有两处 —— ≡ 菜单里那一行，以及控制条上那颗
        // 「版本」按钮。**按钮现在按需露面**：uosc 的控件表是静态的，露不露面由宿主的 embynian-version-count
        // 消息写进 state.has_many_versions，门挂在 controls 串的 <has_many_versions> 上（照 mpv 自己的
        // has_many_edition 那一路）。补丁最怕「升级 uosc 时重打清单漏条」：这里对着源码钉住那几处
        // （绑定、要数据的消息、控制条上的拼写与落点、Controls.lua 里的快捷项简写、音频按钮不带条件，
        // 以及那把「按需露面」的门两头都在）。
        TestHarness.Test("独占模式版本与画面菜单：绑定、控制条落点都在", () =>
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);

            var uosc = Path.Combine(directory!.FullName, "assets", "mpv-ui", "scripts", "uosc");
            var main = File.ReadAllText(Path.Combine(uosc, "main.lua"));
            var controls = File.ReadAllText(Path.Combine(uosc, "elements", "Controls.lua"));

            Assert.True(main.Contains("bind_command('embynian-ui-versions'"), "版本绑定丢了");
            Assert.True(main.Contains("embynian_notify('embynian-versions', '')"), "要版本数据的消息丢了");
            Assert.True(main.Contains("script-binding uosc/embynian-ui-versions"), "≡ 菜单里的版本入口丢了");

            // 控制条上的两颗按钮与它们的**落点**（2026-09-23 第二轮重排；2026-09-24 用户令「把独占模式下
            // 字幕和音频的按钮位置互换」后再调一次）：选集与版本进左下那一组的尾巴
            // （左→右：选集倒数第二、版本最后），右下是 字幕、音频、空一个按钮宽(gap:1)、全屏。
            // **2026-09-27 晚**用户令「移除集成模式和独占模式左下角的画面按钮」：原来 `picture-menu` 那颗
            // 在章节之后、选集之前，本轮整颗撤下 —— 左下那一段现在是…统计、章节、选集、版本。
            // 拼写取 controls 默认值里独有的那一截 —— 少一处，uosc 会走到「unknown element kind」并把
            // 那一项之后的按钮整排丢掉（Controls:init_options 的 break）。
            Assert.True(main.Contains("<has_chapter>chapters,<has_episodes>embynian-ui-episodes,<has_many_versions>embynian-ui-versions,space,"),
                "左下那一组的尾巴（章节、选集、版本）丢了或次序不对");
            Assert.True(main.Contains(",space,<video,audio>subtitles,audio,gap:1,fullscreen'"),
                "右下那一组（字幕、音频、空一个按钮宽、全屏）丢了或次序不对");
            Assert.True(controls.Contains("['embynian-ui-versions']"), "版本按钮的快捷项简写丢了");

            // 「画面菜单」那颗**控制条按钮**撤下了（2026-09-27 用户令），但**功能没删**：右键点画面仍是
            // 同一条绑定，绑定名与消息名分家（见上一节的硬规矩）。这两处留着正是「撤的只是入口」的判据。
            Assert.False(main.Contains("embynian-ui-picture-menu,<has_episodes>"),
                "控制条上那颗画面菜单按钮又回来了");
            Assert.True(main.Contains("bind_command('embynian-ui-picture-menu'"),
                "画面菜单绑定（右键那条）丢了 —— 撤的是控制条入口，不是绑定");
            Assert.True(controls.Contains("['embynian-ui-picture-menu']"),
                "画面菜单按钮的快捷项简写丢了 —— 右键菜单还要用它");

            // 只有一版时那颗按钮不在屏上：门挂在 controls 串上，两头在 main.lua —— 接消息的那个处理器
            // 与 state 里那一格。缺任一头，按钮要么永远不出现、要么永远出现，屏上都看不出是坏的。
            Assert.True(main.Contains("<has_many_versions>embynian-ui-versions"),
                "版本按钮没挂上「有第二版才露」那道门");
            Assert.True(main.Contains("register_script_message('embynian-version-count'"),
                "「有几版」这条宿主消息没人接：那颗按钮永远不会出现");
            Assert.True(main.Contains("set_state('has_many_versions'"),
                "has_many_versions 没人写：那道门永远关着");
            Assert.True(main.Contains("has_many_versions = false,"),
                "state 表里没有 has_many_versions 那一格 —— 默认值该是「先不画」");

            // 选集按钮与版本同一套「按需露面」（2026-09-26 用户令「播放电影的时候不要显示这个按钮」；
            // 门 <has_episodes>，宿主的 embynian-episode-count 写它，0＝电影＝整颗不在屏上），四头都在：
            // controls 串上的门、消息处理器、state 格子、Controls.lua 里写死中文的 tooltip（原来走
            // t('Episodes')，译文表没有就落在英文上）。宿主 → uosc 的键不进 Parse，同版本那条。
            Assert.True(main.Contains("<has_episodes>embynian-ui-episodes"),
                "选集按钮没挂上「是单集才露」那道门");
            Assert.True(main.Contains("register_script_message('embynian-episode-count'"),
                "「是不是单集」这条宿主消息没人接：播电影时那颗按钮下不来");
            Assert.True(main.Contains("set_state('has_episodes'"),
                "has_episodes 没人写：那道门永远关着");
            Assert.True(main.Contains("has_episodes = false,"),
                "state 表里没有 has_episodes 那一格 —— 默认值该是「先不画」");
            Assert.True(controls.Contains("embynian-ui-episodes?选集"),
                "选集按钮的 tooltip 没写死中文 —— t('Episodes') 会落在英文上");
            Assert.Null(VideoWindowContract.Parse([VideoWindowContract.EpisodeCount, "1"]));

            // 音频按钮：只有一条音轨时也要在。带上 <has_many_audio> 就是「多轨才显示」的旧行为。
            Assert.False(main.Contains("<has_many_audio>audio"), "音频按钮又带上「多音轨才显示」的条件了");
        });

        // 2026-09-26（用户令「独占模式右键菜单参考集成模式的弹出方式，去掉那种居中的模式」「左上角加返回按钮」）：
        // 两处都是直接改 uosc 元件、又是升级 uosc 时最容易漏打的补丁，对着源码钉住。
        //   · Menu.lua 锚点补丁（EMBYNIAN[menu-anchor]）：宿主推来的菜单带 embynian_anchor 时在光标处弹出、
        //     不屏幕居中、不压暗幕布 —— 弹出方式与集成模式的右键/按钮浮层一致（用户要「弹出方式一样」）。
        //   · TopBar.lua 左上角返回按钮（EMBYNIAN[topbar-back]）：点它退出 mpv＝回到外壳详情页。
        // 都是运行期观感（三条离线探针固定走集成管线、不覆盖 uosc），这里只保证补丁没被 uosc 升级冲掉。
        TestHarness.Test("独占模式菜单锚点与左上角返回按钮：uosc 补丁都在", () =>
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);

            var uosc = Path.Combine(directory!.FullName, "assets", "mpv-ui", "scripts", "uosc");
            var menu = File.ReadAllText(Path.Combine(uosc, "elements", "Menu.lua"));
            var topbar = File.ReadAllText(Path.Combine(uosc, "elements", "TopBar.lua"));

            // 菜单锚点：读 embynian_anchor 记下光标锚点、摆位用 self.anchor、锚点在时跳过幕布登记
            // （少了这条，菜单又变回压暗整屏的居中大模态）。
            Assert.True(menu.Contains("embynian_anchor"), "菜单锚点补丁丢了：Menu.lua 不读 embynian_anchor");
            Assert.True(menu.Contains("self.anchor"), "菜单锚点补丁丢了：Menu.lua 没有 anchor 摆位");
            Assert.True(menu.Contains("if not self.anchor then Elements:maybe('curtain', 'register'"),
                "菜单锚点补丁丢了：锚点菜单没跳过压暗幕布，会变回居中模态");

            // 返回按钮：按钮定义在、图标是 arrow_back_ios（装箱 Material Icons Round 里确有此字形）。
            Assert.True(topbar.Contains("self.back_button"), "左上角返回按钮丢了：TopBar.lua 没有 back_button");
            Assert.True(topbar.Contains("arrow_back_ios"), "返回按钮图标丢了");
        });

        // 右上角置顶按钮（2026-09-28 晚用户令「给独占模式右上角也加个置顶图标」）：与上面返回按钮同一类
        // —— 直接改 uosc 元件、升级 uosc 时最容易漏打，对着源码钉住。**两头都要在**：
        //   · TopBar.lua 那颗按钮本体（pin／push_pin／`cycle ontop`）与它的状态画法（is_pin ＋ \frz 斜图钉
        //     那一档，2026-09-29 用户令「置顶不要长亮，改为非置顶的时候图标是斜的，置顶的时候恢复原样」
        //     起状态画在姿势上 —— lit 只看悬停）；
        //   · main.lua 的状态来源（state.ontop ＋ mpv `ontop` 属性的观察器）与自动跟随
        //     （EMBYNIAN[ontop-follows-pause]，2026-09-29 用户令「播放时自动置顶，暂停时自动取消置顶」）
        //     —— 只打一头的话，按钮点得动而图钉永远斜着立不正（状态没人送），或状态有了而屏上没有
        //     那颗按钮，或点得动却不跟着播放/暂停走。
        // 图标 push_pin 与装箱的 MaterialIconsRound-Regular.otf 里的字形同名（集成模式那颗 PathIcon
        // 的图钉与它同义，两模式同一颗图钉）。
        TestHarness.Test("独占模式右上角置顶按钮：uosc 补丁两头都在", () =>
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);

            var uosc = Path.Combine(directory!.FullName, "assets", "mpv-ui", "scripts", "uosc");
            var main = File.ReadAllText(Path.Combine(uosc, "main.lua"));
            var topbar = File.ReadAllText(Path.Combine(uosc, "elements", "TopBar.lua"));

            // 按「顶端那一栏排在窗口三颗的左边」钉：buttons 表里 pin 与那三颗在同一个字面量里，
            // 顺序读得出来（left 排布是镜像）。
            Assert.True(topbar.Contains("{pin, min, max, close}"), "置顶按钮没排进顶栏那一栏（right 排布）");
            Assert.True(topbar.Contains("{close, max, min, pin}"), "置顶按钮没排进顶栏那一栏（left 排布）");
            Assert.True(topbar.Contains("icon = 'push_pin'"), "置顶按钮的图标丢了：应是 push_pin");
            Assert.True(topbar.Contains("cycle ontop"), "置顶按钮不切 ontop：点了什么也不会发生");
            Assert.True(topbar.Contains("is_pin"), "置顶按钮的状态画法丢了：未置顶那档没人斜图钉");
            // 状态画在姿势上（2026-09-29）：未置顶斜 35°（libass \frz 正角逆时针，取负号与集成那头
            // RotateTransform +35 同一个方向）、置顶立正。
            Assert.True(topbar.Contains("rotate = icon_rotate"), "斜图钉没画：ass:icon 的 rotate 没接上");
            Assert.True(topbar.Contains("-35"), "未置顶那一档的倾角丢了：图钉永远立正，两档看不出分别");

            Assert.True(main.Contains("ontop = mp.get_property_native('ontop')"),
                "顶栏拿不到置顶的初值：进播放时那颗按钮的状态是空的");
            Assert.True(main.Contains("observe_property('ontop'"),
                "ontop 没人观察：用户点了置顶，那颗图钉不会立正");

            // 置顶跟着播放状态走（2026-09-29 用户令「播放时自动置顶，暂停时自动取消置顶」）：
            // main.lua 的 EMBYNIAN[ontop-follows-pause] 三件事 —— pause 的边沿、file-loaded（换片那条路
            // pause 常常不变，光盯它会漏掉新片开播）、空闲不跟（没有片子在放，置顶归手动）。
            Assert.True(main.Contains("follow_pause_ontop"), "自动跟随的函数丢了：播放/暂停不再动置顶");
            Assert.True(main.Contains("observe_property('pause', 'bool', function(_, paused) follow_pause_ontop(paused) end)"),
                "pause 的边沿没接自动跟随：暂停不会取消置顶、恢复不会置顶");
            Assert.True(main.Contains("register_event('file-loaded', function() follow_pause_ontop"),
                "换片那一下没接自动跟随：pause 不变的换片路上新片不会置顶");
            Assert.True(main.Contains("if mp.get_property_bool('idle-active') then return end"),
                "空闲那档的守卫丢了：没有片子在放也会被按到顶上");
        });

        // 跳过片头/片尾契约（2026-09-26 用户令「独占模式下 跳过片头/片尾的按钮不显示」）：集成模式那颗
        // 是 XAML 的 SkipButton，独占模式画面在 mpv 窗口里、那颗不在屏上，于是宿主把 offer 经
        // embynian-skip-offer 推给 uosc 的 SkipButton 元件画，点它回推 embynian-skip-take（宿主 TakeSkip）。
        //   · SkipTake（uosc→宿主）进 Parse、值保留即可通过；
        //   · SkipOffer（宿主→uosc）**不进 Parse** —— 与 version-count/episode-count 同理，宿主不收自己发的。
        TestHarness.Test("跳过按钮契约：SkipTake 进 Parse、SkipOffer 不进", () =>
        {
            var take = VideoWindowContract.Parse(["embynian-skip-take", ""]);
            Assert.Equal(VideoWindowContract.SkipTake, take?.Key);

            // 带个杂值也照样通过（值保留）：真正的「跳不跳」由 SkipCoordinator 判，契约只认键。
            var takeWithValue = VideoWindowContract.Parse(["embynian-skip-take", "x"]);
            Assert.Equal(VideoWindowContract.SkipTake, takeWithValue?.Key);

            Assert.Null(VideoWindowContract.Parse([VideoWindowContract.SkipOffer, "跳过片头"]));
        });

        // 右键菜单统一（2026-09-29 用户令「统一独占模式和集成模式的右键菜单选项」）添的三条：
        //   · SkipMode 值只认 ask/auto/off —— 三个词各是一档设置，Parse 放过一个杂词，点一行「询问」
        //     就可能落成另一档（这不是显示问题，是替用户改了设置）；
        //   · AutoPlayNext 值固定 toggle —— 拨到哪边由宿主按当前值现算，on/off 一类旧值一律不收；
        //   · MediaInfo 值保留即可通过 —— 真正的正文由宿主现拼（MediaInfoText）。
        TestHarness.Test("右键菜单统一三条消息的契约", () =>
        {
            Assert.Equal(VideoWindowContract.SkipMode, VideoWindowContract.Parse(["embynian-skip-mode", "ask"])?.Key);
            Assert.Equal(VideoWindowContract.SkipMode, VideoWindowContract.Parse(["embynian-skip-mode", "auto"])?.Key);
            Assert.Equal(VideoWindowContract.SkipMode, VideoWindowContract.Parse(["embynian-skip-mode", "off"])?.Key);
            Assert.Null(VideoWindowContract.Parse(["embynian-skip-mode", "yes"]));
            Assert.Null(VideoWindowContract.Parse(["embynian-skip-mode", ""]));

            var toggle = VideoWindowContract.Parse(["embynian-autoplay-next", "toggle"]);
            Assert.Equal(VideoWindowContract.AutoPlayNext, toggle?.Key);
            Assert.Null(VideoWindowContract.Parse(["embynian-autoplay-next", "on"]));

            var info = VideoWindowContract.Parse(["embynian-media-info", "open"]);
            Assert.Equal(VideoWindowContract.MediaInfo, info?.Key);
        });

        // 跳过按钮在 uosc 侧的四头都在（升级 uosc 时最容易漏打的补丁，对着源码钉住）：宿主消息处理器、
        // 元件实例化、元件本体里的点击回推、以及 start-file 收摊。都是运行期观感（三条离线探针固定走集成
        // 管线、不覆盖 uosc），这里只保证补丁没被 uosc 升级冲掉。
        TestHarness.Test("独占模式跳过按钮：uosc 补丁四头都在", () =>
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);

            var uosc = Path.Combine(directory!.FullName, "assets", "mpv-ui", "scripts", "uosc");
            var main = File.ReadAllText(Path.Combine(uosc, "main.lua"));
            var element = File.ReadAllText(Path.Combine(uosc, "elements", "SkipButton.lua"));

            Assert.True(main.Contains("register_script_message('embynian-skip-offer'"),
                "「跳过」offer 这条宿主消息没人接：那颗按钮永远不会出现");
            Assert.True(main.Contains("require('elements/SkipButton'):new()"),
                "SkipButton 元件没实例化：消息接了也没元件画");
            Assert.True(element.Contains("embynian_notify('embynian-skip-take'"),
                "跳过按钮点击没回推 embynian-skip-take：点了没反应");
            Assert.True(main.Contains("Elements.skip_button:set_offer('')"),
                "换源没清 offer：换集途中会挂着上一集的「跳过片尾」");
        });

        // 左上角第二行的文件信息契约（用户令 2026-09-27「下方的片名改为视频编码+音轨+组名……两模式一致」）：
        // 集成模式那一行是 XAML 的 SubtitleBox，独占模式由宿主经 embynian-subline 推给 uosc 顶栏画成副标题。
        // 方向是宿主→uosc，所以它**不进 Parse**（宿主不收自己发的，与 version-count/episode-count/skip-offer 同理）。
        TestHarness.Test("文件信息行契约：Subline 不进 Parse", () =>
        {
            Assert.Null(VideoWindowContract.Parse([VideoWindowContract.Subline, "HEVC · AAC · Studio GreenTea"]));
        });

        // 副标题在 uosc 侧的两头都在（升级 uosc 时最容易漏打的补丁，对着源码钉住）：main.lua 的消息处理器、
        // TopBar.lua 的 set_subline 方法。都是运行期观感（三条离线探针固定走集成管线、不覆盖 uosc），这里只
        // 保证补丁没被 uosc 升级冲掉。顶栏 top_bar_alt_title_place 必须是 'below'，副标题才画在主标题正下方。
        TestHarness.Test("独占模式左上角副标题：uosc 补丁两头都在", () =>
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);

            var uosc = Path.Combine(directory!.FullName, "assets", "mpv-ui", "scripts", "uosc");
            var main = File.ReadAllText(Path.Combine(uosc, "main.lua"));
            var topbar = File.ReadAllText(Path.Combine(uosc, "elements", "TopBar.lua"));

            Assert.True(main.Contains("register_script_message('embynian-subline'"),
                "「文件信息」这条宿主消息没人接：独占模式左上角第二行永远空着");
            Assert.True(topbar.Contains("function TopBar:set_subline"),
                "TopBar 没有 set_subline：消息接了也没处写副标题");
            Assert.True(main.Contains("top_bar_alt_title_place = 'below'"),
                "副标题没设成画在主标题正下方（below）");
        });
    }
}
