using Microsoft.UI.Xaml.Data;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 设置页左边那列分类的图标：分类名 → 一个 Segoe Fluent 字形，参考图里 QQ 那样每个分类带一个图标。
/// <para>
/// 分类还是一串字符串（<c>SettingsViewModel.Categories</c>，是 <c>SelectedCategory</c>、跳转、自检等十来处
/// 共用的那份），没有为了加图标改成对象 —— 那要动一大圈。这里只在渲染时把名字翻成一个字形，认不出的名字给个
/// 兜底字形（不炸、也不空）。名字要和 <c>SettingsViewModel</c> 里那份对得上，对不上就只是显示兜底图标。
/// </para>
/// <para>
/// 一个自己的文件、页面本地注册（<c>&lt;local:SettingsCategoryGlyphConverter x:Key="CategoryGlyph"/&gt;</c>），
/// 和 <see cref="SeekClockConverter"/> 一个路子。字形存的是码点整数（同 <c>PlayerViewModel</c> 里 <c>0xE768</c>
/// 那套），画的时候 <c>char.ConvertFromUtf32</c> 成字符串 —— 源码里不塞看不见的私用区字符。
/// </summary>
public sealed class SettingsCategoryGlyphConverter : IValueConverter
{
    /// <summary>Setting（齿轮）：认不出的分类给个中性图标，不留空。</summary>
    private const int Fallback = 0xE713;

    private static readonly IReadOnlyDictionary<string, int> Glyphs = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["播放器"] = 0xE768,       // Play
        ["播放行为"] = 0xE81C,     // History（续播、进度）
        ["字幕"] = 0xE8D2,         // Font（字）
        ["视频输出"] = 0xE7F4,     // TVMonitor
        ["音频输出"] = 0xE767,     // Volume
        ["着色器"] = 0xE790,       // Color（画质、调色）
        ["主页"] = 0xE80F,         // Home
        ["界面"] = 0xE771,         // Personalize
        ["快捷键"] = 0xE765,       // KeyboardClassic
        ["关于"] = 0xE946,         // Info
        ["服务器"] = 0xE753,       // Cloud（远端服务器）
        ["诊断"] = 0xE8A5,         // Document（日志）
        ["服务器控制台"] = 0xE774  // Globe（内嵌网页控制台）
    };

    public object Convert(object value, Type targetType, object parameter, string language) =>
        char.ConvertFromUtf32(value is string category && Glyphs.TryGetValue(category, out var code) ? code : Fallback);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
