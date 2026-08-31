using System.Text;
using EmbyNian.Services;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Views;
using EmbyNian.Shell.Windowing;
using EmbyNian.Theming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EmbyNian.Shell;

/// <summary>
/// 颜色那几关：这一次用的是哪套主题、色板里每个键解析成了什么、样式有没有被框架的画刷盖回去，末尾是屏幕取样
/// ——真从显示器上取几个像素回来，用来分开「应用什么都没画」和「画了，可显示器上没有」。
/// <para>
/// 取样连带着要问「取样点上那会儿是谁的窗口」（<c>OwnerAt</c>、<c>ClassOf</c>），因为别的窗口压在采样点上时
/// 这一关会红，而报告得说得出压上来的是谁。拆成几个文件的缘由见主文件 <see cref="ShellSelfCheck"/> 的类注释。
/// </para>
/// </summary>
internal static partial class ShellSelfCheck
{
    /// <summary>
    /// 主题这一套真的画到界面上了没有。
    /// <para>
    /// 三条，都是「构造上无法自证」的那种。第一条问生效的是不是这一次该生效的那一套 —— 平常是设置文件里存的，
    /// 带了 <c>--theme</c> 就是命令行要的那一套（那个开关故意不写设置文件，所以拿存的去比会红一整片）——
    /// 顺带问外壳那棵树的深浅跟不跟着走：树没翻过去的话，浅色主题下就是白底配框架自己的深色控件。第二条问
    /// <c>ThemeHost</c> 那张表和 <c>Palette.xaml</c> 对不对得上：表里多一个键是白写，字典里多一个
    /// <c>Eg*Brush</c> 是界面上留了一块换不掉的颜色 —— 那块会永远停在第一帧的字面值上。第三条把几个锚点的
    /// 颜色读回来比一遍，两个字典都读，因为「两份写成同一套」正是那两个按应用级解析的读者能和元素树一致的
    /// 全部原因。
    /// </para>
    /// </summary>
    private static void ReportTheme(
        IServiceProvider services,
        ShellPage shell,
        StartupOptions options,
        StringBuilder report,
        Action<string, bool, string> check)
    {
        var theme = ThemeHost.Current;
        var stored = services.GetRequiredService<ISettingsService>().Settings.Ui.Theme;

        // 这一次该生效的是哪一套。--theme 只压住这一次运行看到的颜色，设置文件一个字不动，所以那个开关在的
        // 时候要跟它比 —— 并且把「存的那个还是原来那个」也报出来，那正是这个开关唯一容易出错的地方。
        var asked = options.Theme is { Length: > 0 } flag ? UiThemes.Resolve(flag).Id : null;
        var effective = asked ?? stored;

        report.AppendLine(
            $"[信息] 主题目录 — 共 {UiThemes.All.Count} 套：" +
            string.Join("、", UiThemes.All.Select(one => $"{one.Name}（{one.Id}，{(one.IsDark ? "深" : "浅")}）")));

        if (asked is not null)
            report.AppendLine(
                "[信息] 主题来自命令行 — "
                + (string.Equals(options.Theme, asked, StringComparison.OrdinalIgnoreCase)
                    ? $"--theme 要 {asked}"
                    : $"--theme 写的是「{options.Theme}」，认不出，回落到 {asked}")
                + $"，设置文件里仍是 {stored}，这一次运行不写它");

        // 正文压在窗口底上的对比度，按当前这套算。测试里六套都卡着 4.5:1，这里报的是「跑起来之后真的是这个数」。
        report.AppendLine(
            $"[信息] 主题正文对比度 — {theme.Name}：{ThemeColor.Contrast(theme.Colors.Text, theme.Colors.Window):0.00}:1");

        var wanted = theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        var sameId = string.Equals(theme.Id, effective, StringComparison.OrdinalIgnoreCase);
        var sameTree = shell.RequestedTheme == wanted;
        check("主题已生效", sameId && sameTree,
            !sameId
                ? $"{(asked is null ? "设置文件里" : "--theme 要的")}是 {effective}，生效的却是 {theme.Id}"
                  + $" — ThemeHost.Apply 没按{(asked is null ? "设置" : "命令行")}跑"
                : sameTree
                    ? $"{theme.Name}（{theme.Id}{(asked is null ? "" : "，来自命令行")}），外壳那棵树是 {shell.RequestedTheme}"
                    : $"{theme.Name}（{theme.Id}）是{(theme.IsDark ? "深" : "浅")}色，外壳那棵树却是 {shell.RequestedTheme} — 没登记到 ThemeHost");

        var painted = ThemeHost.BrushKeys.ToHashSet(StringComparer.Ordinal);
        var darkKeys = PaletteKeys("Default");
        var lightKeys = PaletteKeys("Light");

        var absent = painted
            .Where(key => !darkKeys.Contains(key) || !lightKeys.Contains(key))
            .Order()
            .ToArray();

        var unpainted = darkKeys
            .Where(key => key.StartsWith("Eg", StringComparison.Ordinal)
                          && key.EndsWith("Brush", StringComparison.Ordinal)
                          && !painted.Contains(key))
            .Order()
            .ToArray();

        check("主题角色覆盖", absent.Length == 0 && unpainted.Length == 0,
            absent.Length > 0
                ? $"ThemeHost 要涂 {absent.Length} 个调色板里没有的键：{string.Join("、", absent.Take(8))}{(absent.Length > 8 ? " 等" : "")}"
                : unpainted.Length > 0
                    ? $"调色板有 {unpainted.Length} 个 ThemeHost 不涂的角色，它们会一直停在第一帧的字面值上：{string.Join("、", unpainted.Take(8))}{(unpainted.Length > 8 ? " 等" : "")}"
                    : $"{painted.Count} 个键，Default 和 Light 两份都涂到了");

        (string Key, ThemeColor Want)[] anchors =
        [
            ("EgWindowBrush", theme.Colors.Window),
            ("EgSurfaceBrush", theme.Colors.Surface),
            ("EgTextBrush", theme.Colors.Text),
            ("EgAccentBrush", theme.Colors.Accent),

            // 框架那半的锚点：这个键错了，等于所有没被我们改过模板的控件的正文颜色都错了。
            ("TextFillColorPrimaryBrush", theme.Colors.Text)
        ];

        var wrong = new List<string>();
        foreach (var (key, want) in anchors)
        {
            var expected = ThemeHost.ToColor(want);
            foreach (var dictionary in PaintedDictionaries)
            {
                var got = ThemeHost.ColorOf(dictionary, key);
                if (got == expected) continue;

                wrong.Add($"{dictionary}/{key}={(got is null ? "未定义" : Format(got.Value))}（应为 {Format(expected)}）");
            }
        }

        check("主题色值落地", wrong.Count == 0,
            wrong.Count == 0
                ? $"{anchors.Length} 个锚点在两个字典里都是 {theme.Name} 的色值，" +
                  $"窗口底 {Format(ThemeHost.ToColor(theme.Colors.Window))}、强调色 {Format(ThemeHost.ToColor(theme.Colors.Accent))}"
                : string.Join("；", wrong));
    }

    /// <summary>
    /// That <c>Theme/Styles.xaml</c> really merged, and that the two faces it names really resolved.
    /// <para>
    /// The key table is the cheap half. The interesting half is the fonts: a <c>FontFamily</c> is a string,
    /// so asking one whether 「Bahnschrift SemiCondensed」 exists on the machine gets the string back either
    /// way — a missing face falls silently through to the next name in the list and the app comes up looking
    /// ordinary rather than broken. So both are answered by measuring instead: the display face is
    /// engineered-condensed and has to set the same Latin string narrower than the UI face, and the data
    /// face is monospaced, which means <c>IIII</c> and <c>MMMM</c> have to come out the same width. Equal
    /// widths in the first and unequal in the second are exactly what 「it fell through」 looks like.
    /// </para>
    /// </summary>
    private static void ReportStyles(StringBuilder report, Action<string, bool, string> check)
    {
        var resources = Application.Current.Resources;
        var wrong = new List<string>();

        foreach (var (key, want) in StyleKeys)
        {
            if (!resources.TryGetValue(key, out var found))
            {
                wrong.Add($"{key} 缺");
                continue;
            }

            if (!want.IsInstanceOfType(found))
                wrong.Add($"{key} 是 {found?.GetType().Name ?? "null"}（应为 {want.Name}）");
        }

        check("样式词表", wrong.Count == 0, wrong.Count == 0
            ? $"{StyleKeys.Length} 个键都在，类型都对"
            : string.Join("；", wrong.Take(6)) + (wrong.Count > 6 ? " 等" : ""));

        const string latin = "MEDIA LIBRARY 1080";
        var display = Measured(latin, "EgDisplayFontFamily", 34);
        var ui = Measured(latin, "EgUiFontFamily", 34);
        var narrower = display > 0 && ui > 0 && display < ui * 0.98;

        var thin = Measured("IIIIIIII", "EgDataFontFamily", 14);
        var wide = Measured("MMMMMMMM", "EgDataFontFamily", 14);
        var monospaced = thin > 0 && Math.Abs(thin - wide) < 0.5;

        check("字体已解析", narrower && monospaced,
            $"标题字 {display:0.#} vs 正文字 {ui:0.#}（窄 {(ui > 0 ? 1 - display / ui : 0):P0}）"
            + $"，数字字 I/M {thin:0.#}/{wide:0.#}"
            + (narrower ? "" : " — 标题字没窄下来，Bahnschrift SemiCondensed 大概没装上，落回了 YaHei")
            + (monospaced ? "" : " — 数字字不等宽，Cascadia Mono 和 Consolas 都没落上"));

        report.AppendLine($"[信息] 字号阶 — 眉 {resources["EgEyebrowFontSize"]}、说明 {resources["EgCaptionFontSize"]}、"
            + $"正文 {resources["EgBodyFontSize"]}、小标题 {resources["EgSubheadFontSize"]}、分区 {resources["EgTitleFontSize"]}、"
            + $"页面 {resources["EgHeaderFontSize"]}、片名 {resources["EgDisplayFontSize"]}");
    }

    /// <summary>
    /// How wide one string sets in one of the palette's faces. Measured off the tree — a <c>TextBlock</c>
    /// with no parent still measures, which is what makes this answerable without a page.
    /// </summary>
    private static double Measured(string text, string fontKey, double size)
    {
        if (!Application.Current.Resources.TryGetValue(fontKey, out var found) || found is not FontFamily font)
            return 0;

        var block = new TextBlock { Text = text, FontFamily = font, FontSize = size };
        block.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    /// <summary>The two dictionaries <c>ThemeHost</c> writes; <c>HighContrast</c> is deliberately not one.</summary>
    private static readonly string[] PaintedDictionaries = ["Default", "Light"];

    /// <summary>
    /// Every key <c>Theme/Styles.xaml</c> is supposed to publish, and the type it has to be. A missing one is
    /// not a soft failure: <c>{StaticResource}</c> against an absent key throws while the page is being
    /// parsed, so the page it is on simply never comes up.
    /// </summary>
    private static readonly (string Key, Type Want)[] StyleKeys =
    [
        ("EgUiFontFamily", typeof(FontFamily)),
        ("EgDisplayFontFamily", typeof(FontFamily)),
        ("EgDataFontFamily", typeof(FontFamily)),
        ("ContentControlThemeFontFamily", typeof(FontFamily)),
        ("EgEyebrowFontSize", typeof(double)),
        ("EgCaptionFontSize", typeof(double)),
        ("EgBodyFontSize", typeof(double)),
        ("EgSubheadFontSize", typeof(double)),
        ("EgTitleFontSize", typeof(double)),
        ("EgHeaderFontSize", typeof(double)),
        ("EgDisplayFontSize", typeof(double)),
        ("EgEyebrowSpacing", typeof(int)),
        ("EgCardCornerRadius", typeof(CornerRadius)),
        ("EgPosterCornerRadius", typeof(CornerRadius)),
        ("EgBleedCornerRadius", typeof(CornerRadius)),
        ("EgHairline", typeof(Thickness)),
        ("EgPageMargin", typeof(Thickness)),
        ("EgCornerBadgeSize", typeof(double)),
        ("EgProgressThickness", typeof(double)),
        ("EgActionHeight", typeof(double)),
        ("EgEyebrowStyle", typeof(Style)),
        ("EgPageTitleStyle", typeof(Style)),
        ("EgDisplayTitleStyle", typeof(Style)),
        ("EgSectionTitleStyle", typeof(Style)),
        ("EgSubheadStyle", typeof(Style)),
        ("EgDataStyle", typeof(Style)),
        ("EgDataStrongStyle", typeof(Style)),
        ("EgBodyStyle", typeof(Style)),
        ("EgCaptionStyle", typeof(Style)),
        ("EgOnScrimEyebrowStyle", typeof(Style)),
        ("EgOnScrimBodyStyle", typeof(Style)),
        ("EgOnScrimDataStyle", typeof(Style)),
        ("EgOnScrimDataStrongStyle", typeof(Style)),
        ("EgOnScrimPageTitleStyle", typeof(Style)),
        ("EgRuleStyle", typeof(Style)),
        ("EgFrameStyle", typeof(Style)),
        ("EgFrameActiveStyle", typeof(Style)),
        ("EgCornerBadgeStyle", typeof(Style)),
        ("EgCornerBadgeTextStyle", typeof(Style))
    ];

    /// <summary>
    /// The keys the palette's <paramref name="theme"/> dictionary defines, empty when there is no such
    /// dictionary. Read off what the app merged rather than off the file, so what gets compared is what is
    /// really in force.
    /// </summary>
    private static HashSet<string> PaletteKeys(string theme)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            // Ours, by Source. XamlControlsResources is merged right alongside it and brings a Default and
            // a Light of its own, and whether the framework's two agree is not this app's business.
            if (merged.Source is null ||
                !merged.Source.ToString().EndsWith("Palette.xaml", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!merged.ThemeDictionaries.TryGetValue(theme, out var found) ||
                found is not ResourceDictionary entries)
                continue;

            foreach (var key in entries.Keys)
                if (key is string name) keys.Add(name);
        }

        return keys;
    }

    private static string ColorOf(string key)
    {
        try
        {
            return Application.Current.Resources[key] is SolidColorBrush brush
                ? Format(brush.Color)
                : "未解析到画刷";
        }
        catch (Exception)
        {
            // An absent key throws rather than returning null, and "missing" is the answer we want.
            return "未定义";
        }
    }

    private static string Format(Windows.UI.Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// What the desktop really shows where our client area is.
    /// </summary>
    /// <param name="Rendered">
    /// True only when our own window is the one on screen there and what it shows is more than one flat
    /// colour. One colour across the whole client area is the window's own fallback fill: the XAML island
    /// covers that area whenever it composites anything at all, so seeing the fill means it composited
    /// nothing.
    /// </param>
    private sealed record ScreenReading(bool Rendered, string Detail);

    /// <summary>
    /// Reads the desktop at five points inside our own client area. GetPixel returns a COLORREF
    /// (0x00BBGGRR), so the bytes come back in the opposite order to the way colours are written.
    /// <para>
    /// Five points and a named owner rather than one bare colour, because the three ways this can fail
    /// need telling apart and the report is the only place they can be: nothing sampled at all, someone
    /// else's window sitting over ours — which says nothing about our own drawing — and our window
    /// showing only its fallback fill, which is the compositor having dropped the island. That last one
    /// used to read as a pass, and finding out what it really was took a session of probing from outside.
    /// </para>
    /// </summary>
    private static ScreenReading SampleScreen(HostWindow window, int width, int height)
    {
        if (window.Handle == IntPtr.Zero || width <= 0 || height <= 0)
            return new ScreenReading(false, "取样失败 —— 没有窗口");

        // Spread over regions that differ in any healthy layout: the pane, the header strip, the middle
        // of the content and two places well inside it.
        // 侧边栏那一点贴着窗口左边取（24 个物理像素），不取内容宽度的八分之一：侧边栏默认是收起来的
        // （「侧边栏默认为折叠状态」），窄条只有 48 逻辑像素宽，八分之一早就落到内容里去了。而这份取样要的
        // 就是「两块底色不一样」—— 走到这里时台面上是空的搜索页，一整片都是页面底色，少了侧边栏那一条，五个点
        // 会取出同一个颜色，于是这份检查会去报「XAML 岛什么也没合成」，说的却不是那件事。
        (int X, int Y)[] spots =
        [
            (width / 2, height / 2),
            (24, height / 2),
            (width / 2, height / 12),
            (width * 3 / 4, height / 4),
            (width / 2, height * 11 / 12)
        ];

        var deviceContext = Native.GetDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero) return new ScreenReading(false, "取样失败 —— 取不到屏幕 DC");

        var colors = new List<string>();
        string? owner = null;
        var ours = false;

        try
        {
            foreach (var (x, y) in spots)
            {
                var point = new NativePoint { X = x, Y = y };
                if (!Native.ClientToScreen(window.Handle, ref point)) continue;

                if (owner is null) (ours, owner) = OwnerAt(window, point);

                var colorRef = Native.GetPixel(deviceContext, point.X, point.Y);
                if (colorRef == 0xFFFFFFFF) continue; // CLR_INVALID

                var r = colorRef & 0xFF;
                var g = (colorRef >> 8) & 0xFF;
                var b = (colorRef >> 16) & 0xFF;
                colors.Add($"#{r:X2}{g:X2}{b:X2}");
            }
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, deviceContext);
        }

        if (colors.Count == 0) return new ScreenReading(false, $"取样失败 —— {owner ?? "取不到坐标"}");

        var shown = string.Join("、", colors.Distinct());

        if (!ours)
            return new ScreenReading(false, $"{shown}，但屏幕上是{owner} —— 被遮挡，与本进程画了什么无关");

        return colors.Distinct().Count() > 1
            ? new ScreenReading(true, $"{shown}（{owner}）")
            : new ScreenReading(false, $"整片 {shown} —— {Explain(colors[0])}，XAML 岛没有合成任何东西");
    }

    /// <summary>Whose window is on screen at <paramref name="point"/>, and whether it is one of ours.</summary>
    private static (bool Ours, string Text) OwnerAt(HostWindow window, NativePoint point)
    {
        var hit = Native.WindowFromPoint(point);
        if (hit == IntPtr.Zero) return (false, "空窗口");

        var name = ClassOf(hit);

        // The island is a child window of our own, so the root is what identifies the app: a hit on the
        // island and a hit on the frame are both us.
        if (hit == window.Handle || hit == window.IslandHandle) return (true, name);

        return Native.GetAncestor(hit, Native.GaRoot) == window.Handle
            ? (true, name)
            : (false, $"{name}（hwnd=0x{hit:X}，不是本窗口）");
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = Native.GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "未知窗口类";
    }

    /// <summary>
    /// What a single flat colour over the whole client area means. Both answers are the window painting
    /// its own background, which <see cref="HostWindow"/> only ever gets to show when the island above it
    /// draws nothing.
    /// </summary>
    private static string Explain(string color)
    {
        // Read off the palette in force rather than written in, so a changed base colour cannot leave
        // this saying the wrong thing. ColorOf yields #AARRGGBB; the screen has no alpha.
        var window = ColorOf("EgWindowBrush");
        if (window.Length == 9 && string.Equals(color, $"#{window[3..]}", StringComparison.OrdinalIgnoreCase))
            return "正是窗口底色";

        return color == "#FFFFFF" ? "白底，连底色都没擦过" : "单一颜色";
    }

    private static void Dump(DependencyObject node, StringBuilder into, int depth)
    {
        // Depth-limited on purpose: a NavigationView's own template is around fifteen levels deep and
        // the whole point of the dump is to be readable.
        if (depth > 12) return;

        var name = node is FrameworkElement element && !string.IsNullOrEmpty(element.Name)
            ? $" x:Name={element.Name}"
            : string.Empty;

        var size = node is FrameworkElement sized
            ? $" {sized.ActualWidth:0}x{sized.ActualHeight:0}"
            : string.Empty;

        into.AppendLine($"{new string(' ', depth * 2)}{node.GetType().Name}{name}{size}");

        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
            Dump(VisualTreeHelper.GetChild(node, index), into, depth + 1);
    }
}
