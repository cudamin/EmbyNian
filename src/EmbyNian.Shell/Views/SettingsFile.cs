using EmbyNian.Diagnostics;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 「备份配置文件」「恢复配置」那两颗按钮背后的文件框：一个存、一个开，以及拿回来的那个路径。
/// <para>
/// 单独一个类的理由和 <see cref="ArtworkFile"/> 一样 —— WinUI 3 桌面版的文件框（<see cref="FileSavePicker"/> /
/// <see cref="FileOpenPicker"/>）必须先 <c>InitializeWithWindow.Initialize(picker, hwnd)</c> 才弹得出来，句柄从
/// 弹框那张页面的 <see cref="XamlRoot"/> 上问。视图模型手上只有「存到哪 / 从哪读」这件事，摆得出文件框的只有
/// 页面，所以这里把两个委托交给它（同 <see cref="ConfirmDialog"/> 把确认框交给页面那一手）。
/// </para>
/// <para>
/// 拿不到句柄（窗口还没画出来、根已经摘掉）就返回 null 并写一行日志 —— 症状是「点了没反应」，比弹一句看不懂
/// 的错好，而调用方本来就把 null 当成「用户按了取消」。
/// </para>
/// </summary>
internal static class SettingsFile
{
    private const string Category = "设置";

    /// <summary>存文件框：给一个默认文件名，回来的是用户选的落点；取消或弹不出来都是 null。</summary>
    internal static SettingsViewModel.SaveFileRequest SaveFor(FrameworkElement owner) =>
        suggestedName => SaveAsync(owner, suggestedName);

    /// <summary>开文件框：回来的是用户挑的那个文件的路径；取消或弹不出来都是 null。</summary>
    internal static SettingsViewModel.OpenFileRequest OpenFor(FrameworkElement owner) =>
        () => OpenAsync(owner);

    private static async Task<string?> SaveAsync(FrameworkElement owner, string suggestedName)
    {
        if (HandleOf(owner) is not { } handle)
        {
            Log.Warn(Category, "拿不到这个页面所在的窗口，保存文件框弹不出来");
            return null;
        }

        var picker = new FileSavePicker { SuggestedFileName = suggestedName };
        // 至少要有一种文件类型，不然 PickSaveFileAsync 直接抛。
        picker.FileTypeChoices.Add("EmbyNian 配置", new List<string> { ".json" });

        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);

        var file = await picker.PickSaveFileAsync();
        return file?.Path is { Length: > 0 } path ? path : null;
    }

    private static async Task<string?> OpenAsync(FrameworkElement owner)
    {
        if (HandleOf(owner) is not { } handle)
        {
            Log.Warn(Category, "拿不到这个页面所在的窗口，打开文件框弹不出来");
            return null;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");

        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);

        var file = await picker.PickSingleFileAsync();
        return file?.Path is { Length: > 0 } path ? path : null;
    }

    /// <summary>
    /// 这个页面所在窗口的句柄，同 <see cref="ArtworkFile"/> 里那一手（问「这个内容岛属于哪个窗口」）。窗口还没
    /// 画出来、或者根已经摘掉的时候是 null。
    /// </summary>
    private static IntPtr? HandleOf(FrameworkElement owner) =>
        owner.XamlRoot is { } root && root.ContentIslandEnvironment.AppWindowId is { Value: not 0 } id
            ? Win32Interop.GetWindowFromWindowId(id)
            : null;
}
