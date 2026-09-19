using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 设置页左边分类名单里，MoviePilot 这一行不用 Segoe 字形，用官方图标的白色重绘版（描摹自
/// jxxghp/MoviePilot v3 仓库根的 <c>app.ico</c>，用户令 2026-09-18：「把图标弄成白色的替换侧边栏的这个齿轮」）。
/// 分类是一串字符串（<c>SettingsViewModel.CardCategories</c>），模板里两个图标槽叠在一个格子里，靠这里
/// 回答「这一行是不是 MoviePilot」来二选一：
/// <list type="bullet">
/// <item>不传参数：是 MoviePilot 给 <see cref="Visibility.Visible"/>，否则 Collapsed（白色标记那格用）。</item>
/// <item><c>ConverterParameter=hide</c>：反过来，是 MoviePilot 给 Collapsed（字形那格用）。</item>
/// </list>
/// <para>
/// 名字用序数比较，和 <see cref="SettingsCategoryGlyphConverter"/> 认分类的方式一致——字符串对不上就只是
/// 显示默认的那一个槽，不炸。单文件、页面本地注册（<c>&lt;local:SettingsCategoryMarkConverter x:Key="MoviePilotMark"/&gt;</c>），
/// 与 <see cref="SettingsCategoryGlyphConverter"/> 同一个路子。
/// </para>
/// </summary>
public sealed class SettingsCategoryMarkConverter : IValueConverter
{
    /// <summary>唯一走白色标记的那一行。和 <c>SettingsViewModel.CardCategories</c> 里的字面量对得上。</summary>
    private const string MarkCategory = "MoviePilot";

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isMark = value is string category && string.Equals(category, MarkCategory, StringComparison.Ordinal);
        var hide = string.Equals(parameter as string, "hide", StringComparison.Ordinal);
        return (isMark == hide) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
