using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 播放信息那张对话框的正文与外壳 —— 两个模式的宿主共用一张。
/// <para>
/// 正文由 <c>PlayerViewModel.MediaInfoText</c> 拼好，这里只管画。原来只有集成模式有（播放页自己弹），
/// 2026-09-29 统一右键菜单后独占模式也点得到这一行，而独占播放时播放页是摘下去的、弹窗归外壳 ——
/// 两个宿主各挂各的 <c>XamlRoot</c>，画的必须是同一张，所以收编成一处（与 <see cref="ConfirmDialog"/>
/// 收编两份确认框是同一条经验：两份拷贝必然漂开）。
/// </para>
/// </summary>
internal static class MediaInfoDialog
{
    /// <summary>画一张播放信息对话框（正文 <paramref name="text"/>），挂在 <paramref name="root"/> 上。</summary>
    internal static ContentDialog Create(XamlRoot root, string text) => new()
    {
        XamlRoot = root,
        Title = "播放信息",
        Content = new ScrollViewer
        {
            MaxHeight = 420,
            Content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,

                // 等宽不是装饰：那一块里全是数字与路径，比例字体下每一行都会随字宽抖动。取在 Create
                // 里而不是静态字段 —— 两个调用点都在界面线程，而 WinUI 类的静态初始化会落在首触线程上。
                FontFamily = new FontFamily("Consolas,Cascadia Mono,Microsoft YaHei UI")
            }
        },
        CloseButtonText = "关闭",
        DefaultButton = ContentDialogButton.Close,

        // Dark whatever the theme is, unlike ConfirmDialog's: this one only ever comes up over
        // playback, and a white sheet dropped on top of a film is worse than a light-theme user
        // meeting one dark dialog.
        RequestedTheme = ElementTheme.Dark
    };
}
