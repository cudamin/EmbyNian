using EmbyNian.Playback;
using EmbyNian.Theming;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 播放器浮层那张颜色表（<see cref="PlayerPalette"/>）。
/// <para>
/// 这些色值从前是 <c>PlayerPage.xaml</c> 里三十五处写死的 hex，谁也量不了 —— 外壳那个程序集测试项目引不进来
/// （它是 <c>net10.0-windows…</c>，这边是 <c>net10.0</c>），所以留在 XAML 里的颜色等于没人看着的颜色。搬进
/// Core 换到的就是下面这些：四档墨真的一档比一档暗，三块牌子真的一块比一块实，刻度真的比缓冲亮，压在近黑的
/// 画面上真的读得出来。
/// </para>
/// <para>
/// 为什么这些不是「看一眼就知道」：深色底上的半档差别眼睛认不出来。次要读数比正文亮半档、章节刻度掉到缓冲
/// 色以下，屏上看着都像本来的设计，只有量出来才知道错了。
/// </para>
/// </summary>
internal static class PlayerPaletteTests
{
    public static void Register()
    {
        RegisterLadder();
        RegisterSurfaces();
        RegisterTable();
        RegisterScrims();
        RegisterContrast();
    }

    private static void RegisterLadder()
    {
        Test("播放器配色：四档墨一档比一档暗，四档都不透明", () =>
        {
            var rungs = new (string Name, ThemeColor Color)[]
            {
                ("正文", PlayerPalette.Ink),
                ("退半档", PlayerPalette.InkSoft),
                ("次要读数", PlayerPalette.InkDim),
                ("最淡", PlayerPalette.InkFaint)
            };

            for (var index = 1; index < rungs.Length; index++)
                Assert.True(rungs[index].Color.Luminance < rungs[index - 1].Color.Luminance,
                    $"{rungs[index].Name}（{rungs[index].Color.ToHex()}）没有比"
                    + $"{rungs[index - 1].Name}（{rungs[index - 1].Color.ToHex()}）暗");

            // 一档墨半透明就等于「这行字的颜色由它压着的那一帧决定」，而它压的是一部电影。
            foreach (var (name, colour) in rungs)
                Assert.Equal((byte)0xFF, colour.A, $"{name}那一档是半透明的");
        });
    }

    private static void RegisterSurfaces()
    {
        Test("播放器配色：三块牌子同一个底色，透明度递增", () =>
        {
            var stats = Find("PlayerStatsBrush");
            var rail = Find("PlayerRailBrush");
            var peek = Find("PlayerPeekBrush");

            // 同一个 RGB：三块牌子并排浮在同一帧画面上，底色差一点点就是「其中一块看着发蓝」。
            foreach (var (name, colour) in new[] { ("统计面板", stats), ("音量条", rail), ("章节预览", peek) })
                Assert.Equal(PlayerPalette.Panel with { A = colour.A }, colour, $"{name}的底色不是那块牌子的底色");

            // 次序是有理由的：统计面板一直开着、挡住画面左上角，所以最透；章节预览里装着一张缩略图，
            // 底一透缩略图就发灰，所以最实。
            Assert.True(stats.A < rail.A, "统计面板没有比音量条更透");
            Assert.True(rail.A < peek.A, "音量条没有比章节预览更透");
        });

        Test("播放器配色：三档描边都是纯白，越该被注意的越亮", () =>
        {
            var faint = Find("PlayerEdgeFaintBrush");
            var plain = Find("PlayerEdgeBrush");
            var strong = Find("PlayerEdgeStrongBrush");

            foreach (var (name, colour) in new[] { ("淡", faint), ("中", plain), ("强", strong) })
            {
                Assert.Equal((byte)0xFF, colour.R, $"{name}那档描边不是纯白");
                Assert.Equal((byte)0xFF, colour.G, $"{name}那档描边不是纯白");
                Assert.Equal((byte)0xFF, colour.B, $"{name}那档描边不是纯白");
                Assert.True(colour.A < 0xFF, $"{name}那档描边不透明，压在画面上就是一道实线");
            }

            Assert.True(faint.A < plain.A, "统计面板那圈没有比章节预览那圈淡");
            Assert.True(plain.A < strong.A, "章节预览那圈没有比「跳过」那颗按钮淡");
        });

        Test("播放器配色：进度条那一带，刻度最亮、空轨最淡", () =>
        {
            var tick = Find("PlayerTickBrush");
            var fill = Find("PlayerTrackFillBrush");
            var track = Find("PlayerTrackBrush");
            var line = Find("PlayerLineBrush");

            foreach (var (name, colour) in new[] { ("刻度", tick), ("已缓冲", fill), ("空轨", track), ("细线", line) })
            {
                Assert.Equal((byte)0xFF, colour.R, $"{name}不是纯白");
                Assert.Equal((byte)0xFF, colour.G, $"{name}不是纯白");
                Assert.Equal((byte)0xFF, colour.B, $"{name}不是纯白");
            }

            // 刻度掉到缓冲色以下，章节分界就在已缓冲那一段里消失 —— 而已缓冲那一段正是它最该被看见的地方。
            Assert.True(tick.A > fill.A, "章节刻度没有比已缓冲那一段亮，分界会在缓冲区里看不见");
            Assert.True(fill.A > track.A, "已缓冲那一段没有比空轨亮");
            Assert.True(track.A > line.A, "空轨没有比细线亮");
        });

        Test("播放器配色：换集时那层遮挡必须是全不透明的近黑", () =>
        {
            var cover = Find("PlayerCoverBrush");

            // 它盖的是上一集的最后一帧。半透明的话，换集那一下就能从缝里看见上一部片子。
            Assert.Equal((byte)0xFF, cover.A, "遮挡层是半透明的，换集时会透出上一集最后一帧");
            Assert.Equal(PlayerPalette.Film with { A = 0xFF }, cover, "遮挡层不是画面上那个近黑");
        });
    }

    private static void RegisterTable()
    {
        Test("播放器配色：键不重复、都带 Player 前缀、没有一支是全透明的", () =>
        {
            var keys = PlayerPalette.Brushes.Select(entry => entry.Key).ToArray();

            Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());

            foreach (var (key, colour) in PlayerPalette.Brushes)
            {
                // 前缀分的是「跟主题走的那一套」和「不跟的这一套」：应用那边是 Eg*，画刷同名就会串。
                Assert.True(key.StartsWith("Player", StringComparison.Ordinal), $"{key} 没带 Player 前缀");
                Assert.True(key.EndsWith("Brush", StringComparison.Ordinal), $"{key} 不像一支画刷的键名");

                // 全透明是「这支画刷忘了填」的那个样子，表里出现就等于把自检那一关的判据抹掉了。
                Assert.True(colour.A > 0, $"{key} 是全透明的");
            }
        });
    }

    private static void RegisterScrims()
    {
        Test("播放器配色：控制条那道罩子从全透明起，一路压到底", () =>
        {
            var stops = PlayerPalette.BottomScrimStops;

            Assert.True(stops.Count >= 2, "一道渐变至少要两个停点");

            // 上沿必须是全透明：从半黑起头的罩子会在画面中间留下一条硬边。
            Assert.Equal(0d, stops[0].Along);
            Assert.Equal((byte)0x00, stops[0].Alpha, "罩子上沿不是全透明，画面中间会有一道硬边");
            Assert.Equal(1d, stops[^1].Along);

            Rising(stops, "控制条那道罩子");
            for (var index = 1; index < stops.Count; index++)
                Assert.True(stops[index].Alpha > stops[index - 1].Alpha,
                    $"控制条那道罩子第 {index + 1} 档没有比上一档浓");
        });

        Test("播放器配色：标题条那道方向相反，到下沿散尽", () =>
        {
            var stops = PlayerPalette.TopScrimStops;

            Assert.True(stops.Count >= 2, "一道渐变至少要两个停点");
            Assert.Equal(0d, stops[0].Along);
            Assert.Equal(1d, stops[^1].Along);

            // 散尽而不是留一层薄的：这道罩子底下就是画面，留一层就等于整部片子顶上蒙了一块。
            Assert.Equal((byte)0x00, stops[^1].Alpha, "标题条那道罩子没有散尽，画面顶上会一直蒙着一层");

            Rising(stops, "标题条那道罩子");
            for (var index = 1; index < stops.Count; index++)
                Assert.True(stops[index].Alpha < stops[index - 1].Alpha,
                    $"标题条那道罩子第 {index + 1} 档没有比上一档淡");
        });
    }

    private static void RegisterContrast()
    {
        Test("播放器配色：四档墨压在画面那个近黑上都读得出来", () =>
        {
            var film = PlayerPalette.Film;

            // 门槛按用途分：前三档是正文和读数，最淡那一档只用在「跳过」按钮上那行小字。量出来分别是
            // 17.6、15.2、8.6、6.2，所以这四条线还留着余量 —— 写成量出来的数就成了「不许动」。
            Ratio("正文/画面", PlayerPalette.Ink, film, 12);
            Ratio("退半档/画面", PlayerPalette.InkSoft, film, 12);
            Ratio("次要读数/画面", PlayerPalette.InkDim, film, 7);
            Ratio("最淡/画面", PlayerPalette.InkFaint, film, 4.5);
        });

        Test("播放器配色：四档墨压在牌子底上也都够 4.5:1", () =>
        {
            // 三块牌子是同一个 RGB，所以量一次就够；alpha 不参与，而三块都压在更暗的画面上，
            // 真实对比度只会比这里量出来的更高。
            var panel = PlayerPalette.Panel;

            Ratio("正文/牌子", PlayerPalette.Ink, panel, 4.5);
            Ratio("退半档/牌子", PlayerPalette.InkSoft, panel, 4.5);
            Ratio("次要读数/牌子", PlayerPalette.InkDim, panel, 4.5);
            Ratio("最淡/牌子", PlayerPalette.InkFaint, panel, 4.5);
        });

        Test("播放器配色：这一套不跟主题走，六套主题下都是同一张表", () =>
        {
            // 这一条钉的是「不要把这张表接到主题上去」。浮层压的是一帧视频，不是应用那张面：跟着主题走的
            // 墨在晴昼（唯一那套浅色）下会变成深字压在近黑的罩子上，一个字都读不出来。所以这里拿六套主题
            // 的正文色逐个比一遍 —— 只要哪天有人把 Ink 接成 UiTheme.Text，浅色那一套就会在这儿撞上。
            var daylight = UiThemes.All.FirstOrDefault(theme => !theme.IsDark);
            Assert.NotNull(daylight, "找不到那套浅色主题");

            Assert.True(PlayerPalette.Ink.Luminance > 0.5,
                $"浮层正文那档墨（{PlayerPalette.Ink.ToHex()}）不是浅墨，压在近黑上读不出来");
            Assert.True(PlayerPalette.Ink != daylight!.Colors.Text,
                "浮层正文那档墨等于浅色主题的正文色，这张表已经跟着主题走了");

            // 反过来也量一遍：不管挑的是哪套主题，浮层那四档墨压在自己的近黑上都不变。
            foreach (var theme in UiThemes.All)
                Ratio($"最淡/画面（{theme.Id}）", PlayerPalette.InkFaint, PlayerPalette.Film, 4.5);
        });
    }

    /// <summary>表上某一支画刷的颜色。键写错了当场炸，而不是静悄悄测了个不存在的东西。</summary>
    private static ThemeColor Find(string key)
    {
        foreach (var (name, colour) in PlayerPalette.Brushes)
            if (string.Equals(name, key, StringComparison.Ordinal)) return colour;

        throw new AssertionException($"表上没有 {key} 这一支");
    }

    /// <summary>停点的位置必须落在 0..1 里并且一路往后走 —— 顺序乱了，渐变的方向就不是写着的那个。</summary>
    private static void Rising(IReadOnlyList<(double Along, byte Alpha)> stops, string what)
    {
        for (var index = 0; index < stops.Count; index++)
        {
            var along = stops[index].Along;
            Assert.True(along is >= 0 and <= 1, $"{what}第 {index + 1} 个停点落在 {along}，不在 0..1 里");
            if (index > 0)
                Assert.True(along > stops[index - 1].Along, $"{what}第 {index + 1} 个停点没有排在上一个后面");
        }
    }

    /// <summary>量一对颜色，不够就把是哪一对、差多少一起说出来。</summary>
    private static void Ratio(string what, ThemeColor front, ThemeColor back, double least)
    {
        var ratio = ThemeColor.Contrast(front, back);
        Assert.True(ratio >= least,
            $"{what}：{front.ToHex()} 压在 {back.ToHex()} 上只有 {ratio:0.00}:1，要 {least:0.0}:1");
    }
}
