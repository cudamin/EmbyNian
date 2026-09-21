using EmbyNian.Configuration;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 窗口形态那一层：三个形态、一句「占满」归一、一句自动全屏判据，外加独占模式那组窗口策略。
/// <para>
/// 2026-09-20 加，依据是参考项目（一份 mpv 便携配置，2026-08-12 版）：那份配置里 mpv 交
/// <c>fullscreen</c> 与 <c>window-maximized</c> 两个属性，uosc 把它们归一成
/// <c>fullormaxed = fullscreen or window-maximized</c> 一个布尔，所有下游都问它。本项目此前是散的 ——
/// 同一句「边归显示器」在五处各写一遍，自动全屏那句合取式在四处各写一遍。这些测试把这一层钉死：
/// 判断在 Core 里、可被测，而不在没人看守的页面代码里。
/// </para>
/// </summary>
internal static class WindowFormTests
{
    public static void Register()
    {
        Test("窗口形态：全屏优先、占满归一、名字只有一个答主", () =>
        {
            // 全屏优先：一个既被标成最大化又全屏的窗口就是全屏的（集成模式的 EnterFullscreen 会先把
            // 最大化还原掉，所以这一格正常不该出现；真出现时报「全屏」才是那个有画面的形态）。
            Assert.Equal(WindowForm.Fullscreen, WindowForms.Of(fullscreen: true, maximized: true));
            Assert.Equal(WindowForm.Fullscreen, WindowForms.Of(fullscreen: true, maximized: false));
            Assert.Equal(WindowForm.Maximized, WindowForms.Of(fullscreen: false, maximized: true));
            Assert.Equal(WindowForm.Windowed, WindowForms.Of(fullscreen: false, maximized: false));

            // 「占满屏幕」＝全屏或最大化（参考项目 uosc 的 fullormaxed）。这一句是那五处几何判断的唯一
            // 答主：FitToPicture、BeginDrag、BrowseFoldMeasurable、AspectLock、播放页右上角那颗的图标。
            Assert.True(WindowForms.OccupiesScreen(WindowForm.Fullscreen), "全屏占满");
            Assert.True(WindowForms.OccupiesScreen(WindowForm.Maximized), "最大化也占满");
            Assert.False(WindowForms.OccupiesScreen(WindowForm.Windowed), "窗口化不占满");

            Assert.Equal("全屏", WindowForms.Name(WindowForm.Fullscreen));
            Assert.Equal("最大化", WindowForms.Name(WindowForm.Maximized));
            Assert.Equal("窗口化", WindowForms.Name(WindowForm.Windowed));
        });

        Test("窗口形态：自动全屏的判据只有一处，三个输入都要同意", () =>
        {
            // 设置关着 → 不进全屏（哪怕这是一场真播放、窗口还窗口化）。
            foreach (var current in new[] { WindowForm.Windowed, WindowForm.Maximized, WindowForm.Fullscreen })
                Assert.False(WindowForms.WantsAutoFullscreen(setting: false, lifecycleActive: true, current),
                    $"设置关着时不该进全屏（当前 {WindowForms.Name(current)}）");

            // 不是一场真播放 → 不进全屏。工具预览（--hide-cursor / --show-osd）也走一遍进场换手那一拍，
            // 但它们不持播放生命周期，窗口不该被它们跳成全屏（2026-09-18 的约定）。
            Assert.False(
                WindowForms.WantsAutoFullscreen(setting: true, lifecycleActive: false, current: WindowForm.Windowed),
                "工具预览进场不该跳窗");

            // 已经全屏 → 不再进一次（集成模式那一路会白跑一趟 SetWindowPos）。
            Assert.False(
                WindowForms.WantsAutoFullscreen(setting: true, lifecycleActive: true, current: WindowForm.Fullscreen),
                "已经全屏就不必再进");

            // 真播放 + 设置开着 + 还没全屏 → 进。**最大化的窗口照样要进**：它还没全屏，而「占满」不是
            // 「不用再进」的理由 —— 拿 OccupiesScreen 当这一格的门，得到的是「开着最大化的窗口点播放
            // 不进全屏」，那正是这套归一最容易被写错的地方。
            Assert.True(WindowForms.WantsAutoFullscreen(setting: true, lifecycleActive: true, current: WindowForm.Windowed),
                "窗口化时该进全屏");
            Assert.True(WindowForms.WantsAutoFullscreen(setting: true, lifecycleActive: true, current: WindowForm.Maximized),
                "最大化还没全屏，照样要进");
        });

        Test("独占窗口策略：三条只在独占模式发出，渲染契约里一条都没有", () =>
        {
            var standalone = StandaloneWindowPolicy.Options(VideoPipelineKind.Standalone);
            Assert.Equal("keepaspect-window=yes,autofit-smaller=40%x30%,snap-window=yes",
                string.Join(",", standalone.Select(option => $"{option.Key}={option.Value}")));

            // 集成模式的窗口是这一个程序自己的（几何归 HostWindow），mpv 的窗口选项对它没有意义。
            Assert.Equal(0, StandaloneWindowPolicy.Options(VideoPipelineKind.Integrated).Count,
                "集成模式不该收到窗口策略");

            // 窗口策略不是渲染契约：那一串是必需且致命的（设置失败就停止启动），窗口策略只是行为，
            // 少一条就少一个特性。混进去会让一台不认 snap-window 的构建整个起不来。
            var contract = LibMpvPipelinePolicy.Build(VideoPipelineKind.Standalone, [], (0, 0));
            foreach (var option in standalone)
                Assert.False(contract.Any(entry => entry.Name == option.Key),
                    $"{option.Key} 是窗口策略，不该进渲染契约（那一串是必需项）");
        });

        Test("独占窗口策略：接线真的在（选项表存在却没人发，是最静默的死法）", () =>
        {
            if (RepositoryRoot() is not { } repo)
            {
                Skip("独占窗口策略：接线真的在", "找不到仓库根（EmbyNian.sln）");
                return;
            }

            var backend = Path.Combine(repo, "src", "EmbyNian.Core", "Playback", "LibMpvBackend.cs");
            if (!File.Exists(backend))
            {
                Skip("独占窗口策略：接线真的在", "仓库里没有 LibMpvBackend.cs");
                return;
            }

            Assert.Contains("StandaloneWindowPolicy.Options(pipeline)", File.ReadAllText(backend),
                "LibMpvBackend 没有再发出这组选项 —— 编译过、测试全绿、行为悄悄退回 mpv 默认值");
        });

        Test("窗口形态：Shell 里再没有手写的「全屏 ‖ 最大化」判据", () =>
        {
            if (RepositoryRoot() is not { } repo)
            {
                Skip("窗口形态：Shell 里没有手写的合取", "找不到仓库根（EmbyNian.sln）");
                return;
            }

            var root = Path.Combine(repo, "src", "EmbyNian.Shell");
            if (!Directory.Exists(root))
            {
                Skip("窗口形态：Shell 里没有手写的合取", "仓库里没有 src/EmbyNian.Shell");
                return;
            }

            // 判据：一个形态事实的名字，紧接着（中间只允许 `== true` 这种短尾巴）被 || / && 连到另一个形态
            // 事实的名字上 —— 那就是手写了一遍归一那件事。
            //
            // 为什么不能只数「一行里出现 ≥2 个名字再加上有 ||」：`IsMaximized => Handle != IntPtr.Zero &&
            // Native.IsZoomed(Handle)`（属性自己的定义）与
            // `WindowForms.OccupiesScreen(WindowForms.Of(Fullscreen, Native.IsZoomed(window)))`（归一那句本身，
            // 两个名字是实参）都会被那种钝判据误报 —— 第一版就是这么红的。括号与分号因此都在禁止字符里，
            // 名字与操作符之间也只留 12 个字符的余量。
            var conjunction = new System.Text.RegularExpressions.Regex(
                @"(Fullscreen|IsMaximized|IsZoomed)[^;()|&]{0,12}(\|\||&&)[^;()]*?(Fullscreen|IsMaximized|IsZoomed)");

            var offenders = new List<string>();

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

                foreach (var line in File.ReadLines(file))
                {
                    var text = line.TrimStart();

                    // 注释里可以随便写（这一套的来由本身就写在注释里），只看代码。
                    if (text.StartsWith("//", StringComparison.Ordinal)
                        || text.StartsWith("*", StringComparison.Ordinal)
                        || text.StartsWith("/*", StringComparison.Ordinal)) continue;

                    if (conjunction.IsMatch(line)) offenders.Add($"{Path.GetFileName(file)}: {text}");
                }
            }

            Assert.Equal(0, offenders.Count,
                "这些地方该改问 HostWindow.OccupiesScreen / WindowForms：" + string.Join(" | ", offenders));
        });
    }

    /// <summary>往上找到 <c>EmbyNian.sln</c> 所在的那一层 —— 与 <c>FontTests</c>、<c>MpvUiTests</c> 同一做法。</summary>
    private static string? RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln"))) return directory.FullName;
        }

        return null;
    }
}
