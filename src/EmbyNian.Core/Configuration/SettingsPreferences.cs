using System.Reflection;

namespace EmbyNian.Configuration;

/// <summary>
/// 「偏好设置」到底是哪些字段 —— 一处定义，恢复默认、备份导出、恢复配置三处共用。
/// <para>
/// 这份文档里混着两类东西：一类是用户挑的（播放后端、字幕字号、主题、每页条目数、快捷键……），另一类是程序
/// 替他记的、外加身份那一摊（设备 id、服务器、账号、DPAPI 令牌、窗口拉到多大、上次看的哪个库、每个库各自的
/// 排序筛选视图、播放器音量、MoviePilot 连接）。**只有第一类是「偏好设置」**。三件事都只动第一类：「恢复默认」
/// 把它抄成装机值，「备份」把它导出成文件，「恢复配置」把文件里的它盖回来 —— 身份和记下来的那些一个字都不碰，
/// 所以按一下不会退出登录、不会把这台机器变成 Emby 眼里的新设备、不会把窗口挪回一个用户从没拉过的尺寸。
/// </para>
/// <para>
/// <b>判据是「设置页上有没有这一行」</b>，和 <see cref="SettingsReset"/> 从前独有的那份判据是同一个 —— 所以那份
/// 判据搬到了这里，恢复默认从此也走 <see cref="Apply"/>。分区只写一处的理由，正是那份文档里反复出现的坑：
/// 「列要清的」会漏掉以后新加的设置，「列要留的」会把新设置一起清掉；两处各写一份分区，迟早一处对一处错。
/// </para>
/// <para>
/// <b>就地覆盖，不换对象。</b>几个子对象被别处抓在手里（容器把 <c>Shaders</c> 交给单例、<c>AudioDeviceCatalogue</c>
/// 闭包着 <c>Mpv</c>、设置页每一行捕获的也是子对象本身），换成新对象那些手就还攥着旧的那一份 —— 屏上每行都成了
/// 默认值，真去放片子时着色器档位、libmpv 路径一个都没变。所以这里一个新的子对象都不往外交。
/// </para>
/// <para>
/// <b>音频和「界面」两组反着写：列的是留下来的，不是清掉的。</b>这两组混着偏好和「记下来的」，而两种写法今天
/// 等价、明天不等价 —— 以后往它们上加一项偏好，「列要留的」会把新偏好一起漏掉（漏一项就是功能悄悄不完整），
/// 「列要清的」顶多多留一件记下来的东西。所以默认当偏好、例外具名留下（音量、窗口几何、各库的排序筛选视图）。
/// </para>
/// </summary>
public static class SettingsPreferences
{
    /// <summary>
    /// 把 <paramref name="source"/> 的偏好部分就地抄进 <paramref name="live"/>，再归一化，交回同一个
    /// <paramref name="live"/>。身份、记下来的位置、音量、MoviePilot 一个字不动。恢复默认（source 是全新对象）和
    /// 恢复配置（source 是从备份文件读出来的那一份）走的是同一段。落盘由调用方负责。
    /// </summary>
    public static AppSettings Apply(AppSettings live, AppSettings source)
    {
        CopyInto(live, source);

        // 归一化跟着跑一遍，好让这里交出去的和 Save 会写下去的完全一样（设备 id 补齐、记着的服务器和账号对得上）。
        // 偏好值本来就都在范围内，所以夹取那几句在这条路上不会动任何东西。
        return SettingsMigration.Normalize(live);
    }

    /// <summary>
    /// 一份只含偏好、身份全空的设置文档，序列化出去就是「备份文件」。
    /// <para>
    /// <b>只供立刻序列化用</b>：它的列表和字典是直接指向 <paramref name="settings"/> 那几份的（<see cref="CopyInto"/>
    /// 抄的是引用），序列化只读不改、读完就扔，所以活着的那份不受影响；但别拿它去改东西。<b>不归一化</b> ——
    /// 不归一化就不会顺手给它补出一个设备 id 来，身份那几栏因此老老实实是空的，备份文件里不带任何凭据。
    /// </para>
    /// </summary>
    public static AppSettings ToBackupDocument(AppSettings settings)
    {
        // SchemaVersion 默认就是当前版本；写下当前版本，读回来时按同一套迁移管线走。
        var document = new AppSettings();
        CopyInto(document, settings);
        return document;
    }

    /// <summary>
    /// 偏好部分就地覆盖：五组整组抄，音频除音量、界面除「记下来的那几项」，身份与 MoviePilot 一概不碰。见类注释。
    /// </summary>
    private static void CopyInto(AppSettings destination, AppSettings source)
    {
        // 这五组整组都是设置页上的行，没有例外要留。快捷键整组回默认 ＝ 把用户改过的绑定清成空字典。
        Overwrite(destination.Mpv, source.Mpv);
        Overwrite(destination.Playback, source.Playback);
        Overwrite(destination.Video, source.Video);
        Overwrite(destination.Shaders, source.Shaders);
        Overwrite(destination.Shortcuts, source.Shortcuts);

        // 音量不是设置页上的一行，是播放器上次被留在哪儿（见 AudioSettings.Volume）—— 留目的地自己的那一份。
        var volume = destination.Audio.Volume;
        Overwrite(destination.Audio, source.Audio);
        destination.Audio.Volume = volume;

        // 「界面」混着两类：主题、每页条目数、图片缓存上限、评分来源、主页版面、轮播那几项都是设置页上的行，
        // 从 source 抄；下面这几项是程序替他记的，设置页上一行都没有，留目的地自己的。
        var ui = destination.Ui;
        var (left, top, width, height, maximized) =
            (ui.WindowLeft, ui.WindowTop, ui.WindowWidth, ui.WindowHeight, ui.WindowMaximized);
        var lastLibraryId = ui.LastLibraryId;
        var (sort, filters, views) = (ui.Sort, ui.Filters, ui.Views);

        Overwrite(ui, source.Ui);

        ui.WindowLeft = left;
        ui.WindowTop = top;
        ui.WindowWidth = width;
        ui.WindowHeight = height;
        ui.WindowMaximized = maximized;
        ui.LastLibraryId = lastLibraryId;
        ui.Sort = sort;
        ui.Filters = filters;
        ui.Views = views;

        // 身份（DeviceId、Servers、LastServerId、LastAccountId）和 MoviePilot 一个字都没碰 —— 这里没有一句提到
        // 它们，而 AppSettings 这一层本身不走覆盖，正是为了让「不碰」是默认状态而不是一条例外。MoviePilot 那组
        // 带着一个 DPAPI 密码，当「连接配置」看待：备份不带它（令牌不进备份文件），恢复默认也从不动它。
        // （通知的 Webhook 地址原来也在这里豁免；2026-09-25 通知改走 Emby 服务器，那组设置整个退役了。）
    }

    /// <summary>
    /// 把 <paramref name="source"/> 上每一个可读可写的公开实例属性抄到 <paramref name="destination"/>。
    /// 抄值而不换对象，理由见类注释；列表和字典这类引用直接交过去 —— 恢复默认／恢复配置里 source 是抄完就扔的，
    /// 目的地从此独占那份；导出那条 source 是活着的设置，但导出只把结果读一遍就扔，不改它。
    /// <para>
    /// <see cref="BindingFlags.Instance"/> 写明了：不写的话静态属性也会被枚举到，而给静态属性赋值会绕过
    /// <paramref name="destination"/> 改掉整个类型上的那一份。这几个设置类今天都没有静态属性，这一句防的是以后。
    /// </para>
    /// </summary>
    private static void Overwrite<T>(T destination, T source) where T : class
    {
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property is { CanRead: true, CanWrite: true })
                property.SetValue(destination, property.GetValue(source));
        }
    }
}
