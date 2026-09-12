namespace EmbyNian.Theming;

/// <summary>
/// 全部主题，和把五个色值推成一整张角色表的那段推导。
/// <para>
/// 「前端界面太丑了，重绘一个，然后生成几套主题」里的后半句就是这个文件。五套，全是深色：四套换了色相的，
/// 加一套给 OLED 的纯黑。
/// </para>
/// <para>
/// **原来还有第六套「晴昼」（<c>daylight</c>，唯一的浅色），2026-09-05 按用户一句「删掉晴昼主题」整个删了。**
/// 跟着它走的三件事写在这儿，免得下一个窗口各自去猜：
/// </para>
/// <para>
/// 一、**推导里那条浅色的路留着**（<c>Make</c> 的 <c>dark</c> 参数，和它带出来的那几个三目）。现在没人再传
/// <see langword="false"/>，但它不是该顺手清掉的死代码：<c>UiTheme.IsDark</c> 在外壳里有十几处读者（元素树的
/// <c>ElementTheme</c>、窗口边框的深浅、几个对话框、设置页那排色板），删掉那条路等于把「再加一套浅色」从改六行
/// 数据变成改十几个文件。
/// </para>
/// <para>
/// 二、树里还有二十来处注释拿「晴昼那套浅色下会读不出来」当理由，解释某个颜色为什么写死成压在图上那档浅墨
/// （<c>EgOnScrim*</c>、<c>PlayerPalette</c>、几处 <c>OnScrim</c> 开关）。**那些理由照旧成立**，只是眼下没有一套
/// 主题演示得出来了。**代价也在这儿**：浅色那一套曾经是「外壳里有没有漏下写死的深色」唯一的照妖镜（写死的 hex
/// 在四套深色下都看着没事），删掉它之后这类毛病没有东西逮得住。要重新有，把下面那六行数据加回来即可。
/// </para>
/// <para>
/// 三、设置文件里存着 <c>daylight</c> 的老用户不会卡住：<c>SettingsMigration.Normalize</c> 把认不出来的 id 写回
/// <see cref="DefaultId"/>，那条路由 SettingsTests 钉着。
/// </para>
/// </summary>
public static class UiThemes
{
    /// <summary>设置文件里认不出来的 id 一律回落到这一套。</summary>
    public const string DefaultId = "emby-dark";

    /// <summary>
    /// 顺序就是设置里那排色板的顺序：默认的在最前。
    /// </summary>
    public static IReadOnlyList<UiTheme> All { get; } =
    [
        // 现在这套。窗口、面、次面、字、绿五个值和 Theme/Palette.xaml 里手调的那份一致 —— 那份现在只
        // 负责第一帧，真正生效的是这里推出来的表。
        Make("emby-dark", "薄荷影院", "深邃的蓝黑底色，配以柔和薄荷绿。", dark: true,
            window: "#0C1116", surface: "#141B22", surfaceAlt: "#1C252E", text: "#F2F6FA", accent: "#6EE7C5"),

        // 真黑，给 OLED：这块屏上 #16181C 和 #000000 差着一整档功耗，暗场也差着一整档。
        Make("oled-black", "纯黑", "面全部压到黑，给 OLED 和全暗的房间。", dark: true,
            window: "#000000", surface: "#0A0D10", surfaceAlt: "#13191F", text: "#F2F6FA", accent: "#6EE7C5"),

        Make("midnight", "午夜", "偏蓝的深色面配亮蓝强调色。", dark: true,
            window: "#0F1420", surface: "#161C2B", surfaceAlt: "#1E2637", text: "#E8EDF7", accent: "#4C8DF6"),

        Make("graphite", "石墨", "中性灰的面配琥珀色强调色，海报的颜色不被主题抢。", dark: true,
            window: "#17181A", surface: "#1F2124", surfaceAlt: "#282B2F", text: "#F0F1F3", accent: "#E0A22B"),

        Make("plum", "紫夜", "偏紫的深色面配淡紫强调色。", dark: true,
            window: "#15121C", surface: "#1D1926", surfaceAlt: "#262032", text: "#F1EDF8", accent: "#A97BF0")

        // 这里原来还有第六套「晴昼」（daylight，唯一的浅色：#F1F3F6 底、#FFFFFF 面、#E8EBEF 次面、#15181D 字、
        // #2E7D32 绿 —— 绿比深色那套暗一档，白字压在 #52B54B 上只有 2.6:1，压在那个上是 5.1:1），2026-09-05 按
        // 用户一句「删掉晴昼主题」删掉了。要加回来就是照上面的样子再写一行 dark: false 的数据，别的都不用动；
        // 为什么留着那条浅色的推导，见类注释。
    ];

    /// <summary>下拉框和自检都从这里取默认那套，不各自写一遍 id。</summary>
    public static UiTheme Default => Resolve(DefaultId);

    /// <summary>id 对不上就给默认那套 —— 手改过的设置文件、降级回来的旧版本都会走到这里。</summary>
    public static UiTheme Resolve(string? id) =>
        Find(id) ?? All.First(theme => theme.Id == DefaultId);

    public static UiTheme? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 五个色值 → 二十个角色。
    /// <para>
    /// 每条推导都是一句能说出口的规则：边框是次面朝前景挪一点，暗字是正字朝底色退一截，悬停比静止亮一档。
    /// 「朝前景」在深色主题里是朝白、浅色主题里是朝黑，这就是 <paramref name="dark"/> 在这里的全部作用。
    /// </para>
    /// </summary>
    private static UiTheme Make(
        string id, string name, string note, bool dark,
        string window, string surface, string surfaceAlt, string text, string accent,
        string? danger = null, string? warning = null, string? info = null)
    {
        var win = ThemeColor.Parse(window);
        var sur = ThemeColor.Parse(surface);
        var alt = ThemeColor.Parse(surfaceAlt);
        var ink = ThemeColor.Parse(text);
        var acc = ThemeColor.Parse(accent);

        // 「更靠前」的方向。深色主题往白走，浅色主题往黑走。
        var forward = dark ? ThemeColor.Rgb(0xFF, 0xFF, 0xFF) : ThemeColor.Rgb(0, 0, 0);
        var black = ThemeColor.Rgb(0, 0, 0);
        var white = ThemeColor.Rgb(0xFF, 0xFF, 0xFF);

        var palette = new UiPalette
        {
            Window = win,
            Surface = sur,
            SurfaceAlt = alt,
            SurfaceElevated = sur.Mix(forward, 0.05),
            SurfaceHover = alt.Mix(forward, 0.07),
            Border = alt.Mix(forward, 0.09),
            BorderStrong = alt.Mix(forward, 0.20),

            Text = ink,
            TextDim = ink.Mix(win, 0.36),

            // 0.52 不是随手挑的，是当年被浅色那套逼出来的：晴昼退到 0.55 时，最淡的字压在窗口底上只有 2.88:1，
            // 掉到 3:1 以下，被 ThemeTests 的对比度那条逮住。**晴昼 2026-09-05 删掉了，所以这个数现在没有测试
            // 守着**（剩下五套深色的这一档都宽裕得多，往上调一截也不会红）—— 要动它，先把那套浅色加回来。
            TextFaint = ink.Mix(win, 0.52),
            TextOnAccent = OnAccent(acc),

            Accent = acc,
            AccentHover = dark ? acc.Mix(white, 0.14) : acc.Mix(black, 0.14),
            AccentPressed = acc.Mix(black, dark ? 0.14 : 0.28),
            AccentSoft = win.Mix(acc, dark ? 0.16 : 0.12),
            AccentMuted = acc.WithAlpha(dark ? (byte)0x33 : (byte)0x2B),

            Danger = ThemeColor.Parse(danger ?? (dark ? "#E5534B" : "#C0332B")),
            Warning = ThemeColor.Parse(warning ?? (dark ? "#E3B341" : "#8A6100")),
            Info = ThemeColor.Parse(info ?? (dark ? "#539BF5" : "#1F6FD0")),

            // 遮罩不跟着主题走色相：它的工作是把身后那一页压下去，而不是染色。浅色主题下透一点，
            // 因为下面本来就是亮的，压太狠会显得对话框凭空掉进一个黑洞里。
            Scrim = ThemeColor.Parse("#0C0E11").WithAlpha(dark ? (byte)0xB4 : (byte)0x8C)
        };

        return new UiTheme(id, name, note, dark, palette);
    }

    /// <summary>
    /// 压在强调色上的字：先试浅纸，够 4.5:1 就用它，不够就换深墨。
    /// <para>
    /// 顺序是有意的。「亮底配深字」在对比度上常常更划算 —— Emby 的绿配黑是 8:1，配白只有 2.6:1 —— 但一颗
    /// 强调色按钮上白字才是人预期的样子，所以只有白字真读不出来时才换。两个都不是纯黑纯白，各兑了一点强调色
    /// 进去：纯白压在饱和色上会有一层刺眼的边。
    /// </para>
    /// </summary>
    private static ThemeColor OnAccent(ThemeColor accent)
    {
        var paper = ThemeColor.Rgb(0xFF, 0xFF, 0xFF).Mix(accent, 0.06);
        return ThemeColor.Contrast(paper, accent) >= 4.5
            ? paper
            : ThemeColor.Rgb(0, 0, 0).Mix(accent, 0.10);
    }
}
