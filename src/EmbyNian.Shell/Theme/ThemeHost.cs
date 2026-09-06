using EmbyNian.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EmbyNian.Shell;

/// <summary>
/// 把一套主题真的画到界面上。<see cref="UiThemes"/> 说「这套主题是什么颜色」，这里说「怎么让已经在
/// 屏幕上的那几百个控件换成它」。
/// <para>
/// 手法是改画刷自己的 <see cref="SolidColorBrush.Color"/>，不是换字典条目。这一条是整个文件的关键：
/// 换条目（<c>dictionary["EgAccentBrush"] = 新画刷</c>）对已经绑上去的控件没有任何作用 —— 它们握着的是
/// 旧那个对象；而改颜色是改它们握着的那个对象，一次赋值，全屏幕当场跟着变，不用重建页面、不用重启。
/// </para>
/// <para>
/// 两个字典都写：<c>Application.RequestedTheme</c> 在 App.xaml 里钉死在 Dark（它只能在内容加载前设，
/// 之后改不了），而浅色主题靠的是把元素树的 <c>ElementTheme</c> 翻成 Light。翻过去之后那棵树按 Light
/// 字典解析，而 <c>DiagnosticsViewModel.Resolve</c> 和 <c>PlayerPage.BrushFor</c> 两处故意按应用级
/// 解析（也就是 Dark 字典）。两边写成同一套颜色，这个分歧就不存在了。HighContrast 不碰 —— 那份接的是
/// 系统自己的颜色，Windows 开了高对比度时盖掉它正是最不该做的事。
/// </para>
/// </summary>
public static class ThemeHost
{
    /// <summary>
    /// Palette.xaml 里那两份跟着主题走的字典。第三份 HighContrast 故意不在内。
    /// <para>
    /// 第一份从前叫 <c>Default</c>，2026-09-05 按 <c>winui-design</c> 那条「只写 Light / Dark /
    /// HighContrast，不要 Default」改成 <c>Dark</c>。改的不只是名字：<c>Default</c> 是「没有哪份字典对得上
    /// 当前主题」时的兜底，写错一个键名会悄悄落到它身上还看着正常。
    /// </para>
    /// </summary>
    private static readonly string[] Painted = ["Dark", "Light"];

    private static readonly ThemeColor White = ThemeColor.Rgb(0xFF, 0xFF, 0xFF);
    private static readonly ThemeColor Black = ThemeColor.Rgb(0, 0, 0);

    /// <summary>
    /// 每个画刷键 → 这套主题里的哪个角色。前二十条是我们自己的角色名，后面那些是框架控件真正读的键。
    /// <para>
    /// 后半截必须在这里再写一遍，不能只覆盖 <c>SystemAccentColor</c>：框架在解析它自己那份字典时就用
    /// StaticResource 把每个派生画刷算完了，那时我们的字典还没并进来。所以一个按钮的底色、一个开关的
    /// 圆点、一条进度条，都得按键逐个换掉。
    /// </para>
    /// </summary>
    private static readonly (string Key, Func<UiTheme, ThemeColor> Pick)[] Brushes =
    [
        ("EgWindowBrush", theme => theme.Colors.Window),
        ("EgSurfaceBrush", theme => theme.Colors.Surface),
        ("EgSurfaceAltBrush", theme => theme.Colors.SurfaceAlt),
        ("EgSurfaceElevatedBrush", theme => theme.Colors.SurfaceElevated),
        ("EgSurfaceHoverBrush", theme => theme.Colors.SurfaceHover),
        ("EgBorderBrush", theme => theme.Colors.Border),
        ("EgBorderStrongBrush", theme => theme.Colors.BorderStrong),
        ("EgTextBrush", theme => theme.Colors.Text),
        ("EgTextDimBrush", theme => theme.Colors.TextDim),
        ("EgTextFaintBrush", theme => theme.Colors.TextFaint),
        ("EgTextOnAccentBrush", theme => theme.Colors.TextOnAccent),
        ("EgAccentBrush", theme => theme.Colors.Accent),
        ("EgAccentHoverBrush", theme => theme.Colors.AccentHover),
        ("EgAccentPressedBrush", theme => theme.Colors.AccentPressed),
        ("EgAccentSoftBrush", theme => theme.Colors.AccentSoft),
        ("EgAccentMutedBrush", theme => theme.Colors.AccentMuted),
        ("EgDangerBrush", theme => theme.Colors.Danger),
        ("EgWarningBrush", theme => theme.Colors.Warning),
        ("EgInfoBrush", theme => theme.Colors.Info),
        ("EgScrimBrush", theme => theme.Colors.Scrim),

        // 详情页正文那张亚克力（唯一一支不是 SolidColorBrush 的角色，见 Apply 里那个分支）。取窗口底色 ——
        // 它是这一页正文本来的底，亚克力只是让背后那张剧照透上来一点点，不是换一种颜色。
        ("EgFrostBrush", theme => theme.Colors.Window),

        // 悬停/按下的叠加层。不取自主题的任何一个角色 —— 它按深浅在白和黑之间翻，因为它要压在
        // *下面那层是什么* 上面，而不是配合某个色相。数字和 HostWindow.PaintCaption 给系统窗口按钮
        // 设的那组一致，标题栏两头才是同一种手感。
        ("EgOverlayHoverBrush", theme => theme.IsDark ? White.WithAlpha(0x23) : Black.WithAlpha(0x19)),
        ("EgOverlayPressedBrush", theme => theme.IsDark ? White.WithAlpha(0x37) : Black.WithAlpha(0x2D)),

        // 框架的强调色一族：按钮、开关、滑块、评分、导航栏那条选中指示。
        ("AccentFillColorDefaultBrush", theme => theme.Colors.Accent),
        ("AccentFillColorSecondaryBrush", theme => theme.Colors.Accent.WithAlpha(0xE8)),
        ("AccentFillColorTertiaryBrush", theme => theme.Colors.Accent.WithAlpha(0xC4)),
        ("AccentFillColorDisabledBrush", theme => theme.Colors.Surface.Mix(theme.Colors.Accent, 0.28)),

        // 强调色当字用的时候取悬停那一档：深色主题里它更亮、浅色主题里更暗，两边都是压在面上更读得出来
        // 的那一头。这也是浅色主题下 #52B54B 当链接色只有 2.4:1 那个老问题的答案。
        ("AccentTextFillColorPrimaryBrush", theme => theme.Colors.AccentHover),
        ("AccentTextFillColorSecondaryBrush", theme => theme.Colors.AccentHover),
        ("AccentTextFillColorTertiaryBrush", theme => theme.Colors.Accent),
        ("TextOnAccentFillColorPrimaryBrush", theme => theme.Colors.TextOnAccent),
        ("TextOnAccentFillColorSecondaryBrush", theme => theme.Colors.TextOnAccent.WithAlpha(0xB4)),
        ("TextOnAccentFillColorSelectedTextBrush", theme => theme.Colors.TextOnAccent),

        ("TextFillColorPrimaryBrush", theme => theme.Colors.Text),
        ("TextFillColorSecondaryBrush", theme => theme.Colors.TextDim),
        ("TextFillColorTertiaryBrush", theme => theme.Colors.TextFaint),

        // 深色那边留一点透明让 Mica 透上来 —— 侧边栏和页面底用的就是这几个键。浅色那边全不透明：
        // 底下本来是亮的，透上来只会把字的对比度吃掉。
        ("LayerFillColorDefaultBrush", theme => Veil(theme, theme.Colors.Surface, 0x8C)),
        ("LayerFillColorAltBrush", theme => Veil(theme, theme.Colors.SurfaceAlt, 0xB4)),
        ("CardBackgroundFillColorDefaultBrush", theme => theme.IsDark ? theme.Colors.SurfaceAlt.WithAlpha(0x8C) : theme.Colors.Surface),
        ("CardBackgroundFillColorSecondaryBrush", theme => theme.IsDark ? theme.Colors.SurfaceHover.WithAlpha(0x66) : theme.Colors.Window),
        ("CardStrokeColorDefaultBrush", theme => theme.Colors.Border),
        ("ControlStrokeColorDefaultBrush", theme => theme.Colors.Border),
        ("ControlStrokeColorSecondaryBrush", theme => theme.Colors.BorderStrong),

        ("SolidBackgroundFillColorBaseBrush", theme => theme.Colors.Window),
        ("SolidBackgroundFillColorSecondaryBrush", theme => theme.Colors.Surface),
        ("SolidBackgroundFillColorTertiaryBrush", theme => theme.Colors.SurfaceAlt)
    ];

    /// <summary>
    /// 跟着换的 Color 条目。只有系统强调色这一族 —— 我们自己那些 <c>Eg*Color</c> 只在 Palette.xaml 内部
    /// 被 StaticResource 取过一次，取完画刷就归上面那张表管了。
    /// </summary>
    private static readonly (string Key, Func<UiTheme, ThemeColor> Pick)[] Colors =
    [
        ("SystemAccentColor", theme => theme.Colors.Accent),
        ("SystemAccentColorLight1", theme => theme.Colors.Accent.Mix(White, 0.14)),
        ("SystemAccentColorLight2", theme => theme.Colors.Accent.Mix(White, 0.28)),
        ("SystemAccentColorLight3", theme => theme.Colors.Accent.Mix(White, 0.42)),
        ("SystemAccentColorDark1", theme => theme.Colors.Accent.Mix(Black, 0.14)),
        ("SystemAccentColorDark2", theme => theme.Colors.Accent.Mix(Black, 0.28)),
        ("SystemAccentColorDark3", theme => theme.Colors.Accent.Mix(Black, 0.42))
    ];

    private static readonly List<WeakReference<FrameworkElement>> Roots = [];

    /// <summary>当前生效的那套。启动时 <see cref="Apply(string?)"/> 会按设置文件把它换掉。</summary>
    public static UiTheme Current { get; private set; } = UiThemes.Default;

    /// <summary>
    /// 换主题时通知一次。给两个窗口的标题栏用：那两处的颜色是 Win32 的非客户区，画刷改不到它们，
    /// 只能收到消息之后自己再设一遍。
    /// </summary>
    public static event Action<UiTheme>? Changed;

    /// <summary>
    /// 设置文件里存的 id → 生效。认不出来的 id 由 <see cref="UiThemes.Resolve"/> 拨回默认那套。
    /// </summary>
    public static void Apply(string? id) => Apply(UiThemes.Resolve(id));

    public static void Apply(UiTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        Current = theme;

        foreach (var name in Painted)
        {
            var entries = Dictionary(name);
            if (entries is null) continue;

            foreach (var (key, pick) in Brushes)
            {
                if (!entries.TryGetValue(key, out var found)) continue;

                switch (found)
                {
                    case SolidColorBrush brush:
                        brush.Color = ToColor(pick(theme));
                        break;

                    // 亚克力（EgFrostBrush）：改的是它的两个颜色，两个 Opacity 留给调色板 —— 那两个数是
                    // 「透多少」，按深浅分两档写在两份字典里，和这套主题是哪个色相无关。FallbackColor 也得
                    // 改：关掉透明效果时屏上就剩它，停在上一套主题的窗口底色上会是一整块不合的颜色。
                    case AcrylicBrush acrylic:
                        acrylic.TintColor = ToColor(pick(theme));
                        acrylic.FallbackColor = ToColor(pick(theme));
                        break;
                }
            }

            // Color 条目没法就地改（结构体），只能换掉。改这些不为了当场刷新 —— 界面上那些引用早在解析
            // 时就取过值了 —— 而是为了之后任何一次重新解析（比如 ThemeResource 在树翻主题时重算）拿到的
            // 是这套主题的绿，而不是上一套留下的。
            foreach (var (key, pick) in Colors)
                entries[key] = ToColor(pick(theme));
        }

        var element = theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        for (var index = Roots.Count - 1; index >= 0; index--)
        {
            if (Roots[index].TryGetTarget(out var root))
                root.RequestedTheme = element;
            else
                Roots.RemoveAt(index);   // 页面早关了，弱引用是为了不把它们钉在内存里
        }

        Changed?.Invoke(theme);
    }

    /// <summary>
    /// 让一棵元素树跟着主题的深浅翻过去。框架自己那几百个没被 Palette.xaml 覆盖的刷子只认
    /// <c>ElementTheme</c>，所以浅色主题下没登记过的树会是「浅色的面配框架的深色控件」。
    /// </summary>
    public static void Register(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);

        foreach (var existing in Roots)
            if (existing.TryGetTarget(out var already) && ReferenceEquals(already, root))
                return;

        Roots.Add(new WeakReference<FrameworkElement>(root));
        root.RequestedTheme = Current.IsDark ? ElementTheme.Dark : ElementTheme.Light;
    }

    public static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    /// <summary>深色主题下透一点让 Mica 上来，浅色主题下不透。</summary>
    private static ThemeColor Veil(UiTheme theme, ThemeColor color, byte alpha) =>
        theme.IsDark ? color.WithAlpha(alpha) : color;

    /// <summary>自检用：这张表覆盖了哪些键。少覆盖一个，界面上就会留一块换不掉的颜色。</summary>
    public static IReadOnlyList<string> BrushKeys { get; } = Brushes.Select(entry => entry.Key).ToArray();

    /// <summary>
    /// 自检用：读回某个字典里某个键现在是什么颜色。亚克力那一支报的是它的 <c>TintColor</c> —— 一张亚克力
    /// 没有「一个颜色」，而这里问的是「<see cref="Apply"/> 有没有把这套主题的色写进去」，答的就是那一个。
    /// </summary>
    public static Color? ColorOf(string theme, string key) =>
        Dictionary(theme) is { } entries && entries.TryGetValue(key, out var found)
            ? found switch
            {
                SolidColorBrush brush => brush.Color,
                AcrylicBrush acrylic => acrylic.TintColor,
                _ => null
            }
            : null;

    private static ResourceDictionary? Dictionary(string theme)
    {
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            // 按 Source 认自己那份：XamlControlsResources 就并在旁边，它也带 Dark 和 Light。
            if (merged.Source is null ||
                !merged.Source.ToString().EndsWith("Palette.xaml", StringComparison.OrdinalIgnoreCase))
                continue;

            if (merged.ThemeDictionaries.TryGetValue(theme, out var found) && found is ResourceDictionary entries)
                return entries;
        }

        return null;
    }
}
