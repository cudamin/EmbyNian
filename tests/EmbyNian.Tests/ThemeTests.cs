using EmbyNian.Theming;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 「生成几套主题」，在没有窗口的地方压住。
/// <para>
/// 一套主题坏掉的样子不是崩溃，是某一行字看不见 —— 而这件事只有把整份角色表拿对比度量一遍才发现得了。
/// 所以这里的主体不是「解析得对不对」，是五套主题乘每一条「谁压在谁上面」的组合，一条条量。手点着看五套
/// 主题的每一页，是这份测试替掉的那件事。
/// </para>
/// </summary>
internal static class ThemeTests
{
    public static void Register()
    {
        RegisterColor();
        RegisterCatalog();
        RegisterContrast();
        RegisterDerivation();
    }

    private static void RegisterColor()
    {
        Test("颜色：六位和八位都认，八位输出", () =>
        {
            Assert.Equal("#FF52B54B", ThemeColor.Parse("#52B54B").ToHex());
            Assert.Equal("#B40C0E11", ThemeColor.Parse("#B40C0E11").ToHex());
            Assert.Equal("#FF52B54B", ThemeColor.Parse("52B54B").ToHex());
        });

        Test("颜色：写坏的色值当场抛，不静悄悄回退", () =>
        {
            Assert.Throws<FormatException>(() => ThemeColor.Parse("#52B54"));
            Assert.Throws<FormatException>(() => ThemeColor.Parse(""));
            Assert.Throws<FormatException>(() => ThemeColor.Parse("#5"));
        });

        Test("颜色：兑色的两头就是两头，alpha 不动", () =>
        {
            var green = ThemeColor.Parse("#8052B54B");
            var white = ThemeColor.Rgb(0xFF, 0xFF, 0xFF);

            Assert.Equal(green, green.Mix(white, 0));
            Assert.Equal("#80FFFFFF", green.Mix(white, 1).ToHex());

            // 正中间：0x52 → 168.5，落在半档上，按 Math.Round 的默认规则取偶数 168 = 0xA8。
            Assert.Equal("#80A8DAA5", green.Mix(white, 0.5).ToHex());
        });

        Test("颜色：对比度的两个极端", () =>
        {
            var black = ThemeColor.Rgb(0, 0, 0);
            var white = ThemeColor.Rgb(0xFF, 0xFF, 0xFF);

            Assert.True(Math.Abs(ThemeColor.Contrast(black, white) - 21) < 0.01, "黑白应当是 21:1");
            Assert.True(Math.Abs(ThemeColor.Contrast(white, white) - 1) < 0.01, "同色应当是 1:1");
        });
    }

    private static void RegisterCatalog()
    {
        Test("主题目录：id 不重复、名字和说明都在", () =>
        {
            Assert.True(UiThemes.All.Count >= 4, $"只有 {UiThemes.All.Count} 套主题");
            Assert.Equal(UiThemes.All.Count, UiThemes.All.Select(theme => theme.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            foreach (var theme in UiThemes.All)
            {
                Assert.True(theme.Id.Length > 0, "有一套主题没有 id");
                Assert.True(theme.Name.Length > 0, $"{theme.Id} 没有名字");
                Assert.True(theme.Note.Length > 0, $"{theme.Id} 没有说明");
            }
        });

        Test("主题目录：现在全是深色的", () =>
        {
            // 这一条从前是「深浅两边都有」。「晴昼」（唯一那套浅色）2026-09-05 按用户一句「删掉晴昼主题」删了，
            // 所以它翻了过来。留着它不是为了断言「没有浅色」这件好事，是为了把「删掉的是数据、留下的是推导」
            // 钉在这儿：Make 里那条 dark: false 的路现在一个人不走，而底下「深色主题里悬停更亮，浅色主题里更暗」
            // 那条照旧按 IsDark 判 —— 哪天把浅色加回来，这一条会第一个红，提醒改的人先读 UiThemes 的类注释。
            Assert.True(UiThemes.All.Any(theme => theme.IsDark), "没有深色主题");
            Assert.True(UiThemes.All.All(theme => theme.IsDark),
                "又有浅色主题了 —— 先读 UiThemes 的类注释，再决定这一条该怎么改");
        });

        Test("主题目录：认不出来的 id 回落到默认那套", () =>
        {
            Assert.Equal(UiThemes.DefaultId, UiThemes.Resolve("并不存在的主题").Id);
            Assert.Equal(UiThemes.DefaultId, UiThemes.Resolve(null).Id);
            Assert.Equal(UiThemes.DefaultId, UiThemes.Default.Id);
            Assert.Null(UiThemes.Find("并不存在的主题"));

            // 大小写不敏感：这个 id 会被人手写进 settings.json。
            Assert.Equal(UiThemes.DefaultId, UiThemes.Resolve(UiThemes.DefaultId.ToUpperInvariant()).Id);
        });

        Test("主题目录：默认那套是 Palette.xaml 第一帧用的那份绿", () =>
        {
            // 第一帧由 Theme/Palette.xaml 的字面值画，之后 ThemeHost 用这里的表盖上去。两份差得太远，
            // 启动就会闪一下颜色。窗口底色和强调色是最看得出来的两个，所以钉住它们。
            Assert.Equal("#FF16181C", UiThemes.Default.Colors.Window.ToHex());
            Assert.Equal("#FF52B54B", UiThemes.Default.Colors.Accent.ToHex());
        });
    }

    private static void RegisterContrast()
    {
        Test("主题对比度：正文在窗口底和面上都够 4.5:1", () =>
        {
            foreach (var theme in UiThemes.All)
            {
                var colors = theme.Colors;
                Ratio(theme, "正文/窗口底", colors.Text, colors.Window, 4.5);
                Ratio(theme, "正文/面", colors.Text, colors.Surface, 4.5);
                Ratio(theme, "正文/次面", colors.Text, colors.SurfaceAlt, 4.5);
                Ratio(theme, "正文/浮起层", colors.Text, colors.SurfaceElevated, 4.5);
            }
        });

        Test("主题对比度：暗字够 4.5:1，最淡的字够 3:1", () =>
        {
            foreach (var theme in UiThemes.All)
            {
                var colors = theme.Colors;

                // 暗字是副标题、说明行，成段的正文，所以和正文同一条线。
                Ratio(theme, "暗字/面", colors.TextDim, colors.Surface, 4.5);
                Ratio(theme, "暗字/窗口底", colors.TextDim, colors.Window, 4.5);

                // 最淡的一档只用在单词和图标上，按大字号那条 3:1 量。
                Ratio(theme, "淡字/面", colors.TextFaint, colors.Surface, 3.0);
                Ratio(theme, "淡字/窗口底", colors.TextFaint, colors.Window, 3.0);
            }
        });

        Test("主题对比度：强调色上的字够 4.5:1，强调色自己够 3:1", () =>
        {
            foreach (var theme in UiThemes.All)
            {
                var colors = theme.Colors;

                // 一颗强调色按钮上的字。这一条是 OnAccent 那段挑深墨还是浅纸的全部理由。
                Ratio(theme, "按钮字/强调色", colors.TextOnAccent, colors.Accent, 4.5);
                Ratio(theme, "按钮字/强调色（悬停）", colors.TextOnAccent, colors.AccentHover, 4.0);
                Ratio(theme, "按钮字/强调色（按下）", colors.TextOnAccent, colors.AccentPressed, 4.0);

                // 强调色本身也当字和图标用：选中的侧边栏项、链接、进度条。
                Ratio(theme, "强调色/面", colors.Accent, colors.Surface, 3.0);
                Ratio(theme, "强调色/窗口底", colors.Accent, colors.Window, 3.0);

                // 信息条：强调色兑进底色那一层上面压正文。
                Ratio(theme, "正文/强调色浅底", colors.Text, colors.AccentSoft, 4.5);
            }
        });

        Test("主题对比度：三个状态色在面上读得出来", () =>
        {
            foreach (var theme in UiThemes.All)
            {
                var colors = theme.Colors;
                Ratio(theme, "危险色/面", colors.Danger, colors.Surface, 3.0);
                Ratio(theme, "警告色/面", colors.Warning, colors.Surface, 3.0);
                Ratio(theme, "信息色/面", colors.Info, colors.Surface, 3.0);
            }
        });
    }

    private static void RegisterDerivation()
    {
        Test("主题推导：每一层都真的和上一层分得开", () =>
        {
            foreach (var theme in UiThemes.All)
            {
                var colors = theme.Colors;

                // 一层和下一层撞成同一个值，界面上就是「侧边栏没了」「悬停没反应」这种说不清的坏。
                Assert.True(colors.Window != colors.Surface, $"{theme.Id}：窗口底和面同色");
                Assert.True(colors.Surface != colors.SurfaceAlt, $"{theme.Id}：面和次面同色");
                Assert.True(colors.SurfaceAlt != colors.SurfaceHover, $"{theme.Id}：次面和悬停同色");
                Assert.True(colors.SurfaceAlt != colors.Border, $"{theme.Id}：次面和边框同色");
                Assert.True(colors.Border != colors.BorderStrong, $"{theme.Id}：两档边框同色");
                Assert.True(colors.Accent != colors.AccentHover, $"{theme.Id}：强调色和悬停同色");
                Assert.True(colors.Accent != colors.AccentPressed, $"{theme.Id}：强调色和按下同色");
            }
        });

        Test("主题推导：深色主题里悬停更亮，浅色主题里更暗", () =>
        {
            foreach (var theme in UiThemes.All)
            {
                var colors = theme.Colors;
                var brighter = colors.SurfaceHover.Luminance > colors.SurfaceAlt.Luminance;
                Assert.Equal(theme.IsDark, brighter);

                var elevated = colors.SurfaceElevated.Luminance > colors.Surface.Luminance;
                Assert.Equal(theme.IsDark, elevated);
            }
        });

        Test("主题推导：遮罩是半透明的，别的角色都不是", () =>
        {
            foreach (var theme in UiThemes.All)
            {
                var colors = theme.Colors;
                Assert.True(colors.Scrim.A < 0xFF, $"{theme.Id}：遮罩不透明，对话框背后会整片糊掉");
                Assert.True(colors.AccentMuted.A < 0xFF, $"{theme.Id}：半透明强调色不透明");
                Assert.Equal((byte)0xFF, colors.Surface.A);
                Assert.Equal((byte)0xFF, colors.Text.A);
                Assert.Equal((byte)0xFF, colors.Accent.A);
            }
        });
    }

    /// <summary>量一对颜色，不够就把是哪套主题、哪一对、差多少一起说出来。</summary>
    private static void Ratio(UiTheme theme, string what, ThemeColor front, ThemeColor back, double least)
    {
        var ratio = ThemeColor.Contrast(front, back);
        Assert.True(ratio >= least,
            $"{theme.Id}（{theme.Name}）{what}：{front.ToHex()} 压在 {back.ToHex()} 上只有 {ratio:0.00}:1，要 {least:0.0}:1");
    }
}
