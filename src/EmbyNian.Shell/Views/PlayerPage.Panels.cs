using EmbyNian.Playback;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 播放页那一个对话框：播放信息。
/// <para>
/// 它是画出来的而不是绑定的，因为它的正文没有固定形状 —— 那一块文本的行取决于这次起播解析出了什么。
/// 内容归视图模型（<see cref="PlayerViewModel.MediaInfoText"/>），这个文件只管画。
/// </para>
/// <para>
/// <b>统计面板 2026-09-22 起不在这里。</b> 它改由 mpv 自己画进 OSD 层（两份脚本装箱在
/// assets/mpv-ui/scripts/stats.lua，见 <c>MpvStats</c>）：验收要的是「统计项与参考项目逐项一致」加「全
/// 中文」，而这一页原来那十一行是自绘的、跟参考项目对不上。集成模式照样看得见它 —— 画面本来就是 mpv
/// 的输出合成进来的。
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>
    /// 播放信息正文所用的等宽字体，只在这里取一次而不是每次用到都写一遍。等宽不是装饰：那一块里全是
    /// 数字与路径，比例字体下每一行都会随字宽抖动。
    /// <para>
    /// An instance field rather than a static: a static initialiser on a WinUI type can run on
    /// whichever thread first touches the class, and a <c>FontFamily</c> made off the UI thread is a
    /// wrong-thread failure waiting for the first film.
    /// </para>
    /// </summary>
    private readonly FontFamily _mono = new("Consolas,Cascadia Mono,Microsoft YaHei UI");

    /// <summary>
    /// A brush by resource key, looked up the way XAML itself would: this page's own dictionary first,
    /// then the application's.
    /// <para>
    /// <c>Resources[key]</c> alone is a trap. A control's <c>Resources</c> is only its own local
    /// dictionary — it does not walk up to <c>App.xaml</c> — and indexing a key that is not in it throws
    /// a bare <c>COMException</c> reading 「未指定的错误」 rather than returning null. So a builder asking
    /// for one of Palette.xaml's brushes would crash the first time a user opened the thing that draws it.
    /// <c>PlayerTickBrush</c> is local to this page and <c>EgTextBrush</c> is the app's; both have to
    /// work, and only this resolves both.
    /// </para>
    /// <para>
    /// It still throws for a key that exists in neither, and that is deliberate — the self-check caught a
    /// misremembered <c>EgTextMutedBrush</c> here that way, which a silent fallback would have turned into
    /// invisible text nobody noticed until it shipped.
    /// </para>
    /// <para>
    /// The keys asked for are the two 压在暗底上 ones (<c>EgOnScrim*</c>), which are theme-independent by
    /// design: this panel floats over the picture on a near-black scrim in every theme, so a light theme's
    /// <c>EgTextBrush</c> — nearly black ink — would draw invisible numbers there. That is why those two
    /// keys live in <c>Palette.xaml</c>'s theme-independent tail and not in a theme dictionary.
    /// </para>
    /// </summary>
    private Brush BrushFor(string key) =>
        (Brush)(Resources.TryGetValue(key, out var local) ? local : Application.Current.Resources[key]);

    /// <summary>
    /// 播放信息: what was actually handed to mpv, which is the only thing that answers 「为什么这个文件看
    /// 起来不对」. Every line of the text is a playback fact and comes from
    /// <see cref="PlayerViewModel.MediaInfoText"/>; the dialog, its scroll box and the hold on the chrome
    /// are the only parts that are this page's.
    /// </summary>
    private async Task ShowMediaInfoAsync()
    {
        if (!Attached) return;

        var body = new ScrollViewer
        {
            MaxHeight = 420,
            Content = new TextBlock
            {
                Text = ViewModel.MediaInfoText(),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontFamily = _mono
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "播放信息",
            Content = body,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,

            // Dark whatever the theme is, unlike ConfirmDialog's: this one only ever comes up over
            // playback, and a white sheet dropped on top of a film is worse than a light-theme user
            // meeting one dark dialog.
            RequestedTheme = ElementTheme.Dark
        };

        // The dialog is where the pointer is for as long as it is up, and it is modal: the chrome must
        // not decide the user has lost interest while they are reading it.
        Hold(true, ChromeHold.Dialog);
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            Hold(false, ChromeHold.Dialog);
        }
    }
}
