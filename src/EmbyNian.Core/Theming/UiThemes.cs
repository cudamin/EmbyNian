namespace EmbyNian.Theming;

/// <summary>
/// 全部主题，和把五个色值推成一整张角色表的那段推导。
/// <para>
/// 「前端界面太丑了，重绘一个，然后生成几套主题」里的后半句就是这个文件。六套：四套换了色相的深色、一套
/// 给 OLED 的纯黑、一套浅色。挑六套而不是两套，是因为浅色那一套会把深色主题里所有「反正压在黑底上」的
/// 侥幸全部暴露出来 —— 一套主题跑不通的地方，通常是外壳写死了一个颜色。
/// </para>
/// </summary>
public static class UiThemes
{
    /// <summary>设置文件里认不出来的 id 一律回落到这一套。</summary>
    public const string DefaultId = "emby-dark";

    /// <summary>
    /// 顺序就是设置里下拉框的顺序：默认的在最前，浅色的在最后。
    /// </summary>
    public static IReadOnlyList<UiTheme> All { get; } =
    [
        // 现在这套。窗口、面、次面、字、绿五个值和 Theme/Palette.xaml 里手调的那份一致 —— 那份现在只
        // 负责第一帧，真正生效的是这里推出来的表。
        Make("emby-dark", "墨绿", "默认：Emby 的绿，压在近黑的面上。", dark: true,
            window: "#16181C", surface: "#1E2126", surfaceAlt: "#262A31", text: "#F2F4F7", accent: "#52B54B"),

        // 真黑，给 OLED：这块屏上 #16181C 和 #000000 差着一整档功耗，暗场也差着一整档。
        Make("oled-black", "纯黑", "面全部压到黑，给 OLED 和全暗的房间。", dark: true,
            window: "#000000", surface: "#0A0B0D", surfaceAlt: "#14161A", text: "#F2F4F7", accent: "#52B54B"),

        Make("midnight", "午夜", "偏蓝的深色面配亮蓝强调色。", dark: true,
            window: "#0F1420", surface: "#161C2B", surfaceAlt: "#1E2637", text: "#E8EDF7", accent: "#4C8DF6"),

        Make("graphite", "石墨", "中性灰的面配琥珀色强调色，海报的颜色不被主题抢。", dark: true,
            window: "#17181A", surface: "#1F2124", surfaceAlt: "#282B2F", text: "#F0F1F3", accent: "#E0A22B"),

        Make("plum", "紫夜", "偏紫的深色面配淡紫强调色。", dark: true,
            window: "#15121C", surface: "#1D1926", surfaceAlt: "#262032", text: "#F1EDF8", accent: "#A97BF0"),

        // 唯一的浅色。绿比深色那套暗一档：白字压在 #52B54B 上只有 2.6:1，压在这个上是 5.1:1。
        Make("daylight", "晴昼", "浅色：白面、深字，强调色压暗一档好让白字读得出来。", dark: false,
            window: "#F1F3F6", surface: "#FFFFFF", surfaceAlt: "#E8EBEF", text: "#15181D", accent: "#2E7D32")
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

            // 0.52 不是随手挑的：浅色那套退到 0.55 时，最淡的字压在窗口底上只有 2.88:1，掉到 3:1 以下。
            // 这个数字由 ThemeTests 的对比度那条盯着，改大就会红。
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
