using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// A fallback page for future destinations. One page rather than several empty ones, because what
/// differs between them is a title and a list of sentences.
/// </summary>
public sealed partial class PlaceholderPage : Page
{
    /// <summary>
    /// Segoe Fluent codepoints, spelled as numbers rather than as characters. The characters are in
    /// the Unicode private-use area: pasted into source they are invisible in a diff, and any tool
    /// that re-encodes the file can silently replace them with a replacement character.
    /// </summary>
    private const int ServerIcon = 0xE774;
    private const int DiagnosticsIcon = 0xE9D9;
    private const int SettingsIcon = 0xE713;

    /// <summary>Title, caption, glyph and what each page is going to hold.</summary>
    private static readonly Dictionary<string, (string Title, string Caption, int Glyph, string[] Points)> Pages =
        new(StringComparer.Ordinal)
        {
            ["servers"] = ("服务器", "已保存的服务器与登录用户", ServerIcon,
            [
                "服务器与用户的增删改，含左上角快速切换的完整管理界面",
                "连接测试与服务器信息：版本、系统、当前会话",
                "缓存占用与图片缓存清理"
            ]),
            ["diagnostics"] = ("诊断", "日志、播放参数与 mpv 状态", DiagnosticsIcon,
            [
                "实时日志与按分类过滤，可直接打开日志文件夹",
                "mpv 版本、硬解方式与当前生效的着色器组",
                "最近一次播放的完整参数，便于排查画质问题"
            ]),
            ["settings"] = ("设置", "播放、着色器与界面", SettingsIcon,
            [
                "播放：续播、进度上报、标记已看的百分比",
                "音轨与字幕：语言优先级、字体、字号、编码",
                "画质：硬解、着色器自动切换规则",
                "界面：每页条目数与卡片尺寸"
            ])
        };

    public PlaceholderPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var tag = e.Parameter as string ?? "settings";

        // The shell reads this back to restore its pane highlight after a Frame.GoBack.
        Tag = tag;

        // Typed rather than var: the fallback tuple has no element names, and a conditional whose
        // arms disagree about names drops them all.
        (string Title, string Caption, int Glyph, string[] Points) page = Pages.TryGetValue(tag, out var found)
            ? found
            : ("未知页面", string.Empty, SettingsIcon, []);

        Slate.Title = page.Title;
        Slate.Note = page.Caption;
        PageIcon.Glyph = char.ConvertFromUtf32(page.Glyph);
        Points.ItemsSource = page.Points;
    }
}
