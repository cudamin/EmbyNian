using System.Reflection;

namespace EmbyNian.Configuration;

/// <summary>
/// 恢复默认设置：把设置页上那几张卡管的东西全部改回装机时的样子，不动服务器、账号，也不动那些「记下来的」位置。
/// <para>
/// <b>判据是「设置页上有没有这一行」。</b> 这份文档里混着两类东西：一类是用户挑的（播放后端、字幕字号、主题、
/// 每页条目数），另一类是程序替他记的（窗口拉到多大、上次看的哪个库、每个库各自的排序筛选视图、播放器音量停在
/// 哪儿），再加上身份那一摊（设备 id、服务器、账号、DPAPI 包着的令牌）。**只有第一类是「设置」**，所以只有第一
/// 类回默认。把第二类一起清掉的话，按一下「恢复默认设置」会顺手退出登录、让 Emby 把这台机器当成一台新设备、
/// 把窗口挪回一个用户从没拉过的尺寸 —— 那三件事没有一件是他按这颗按钮时想要的，而每一件都不可逆。
/// </para>
/// <para>
/// <b>为什么是「往原来那几个对象里覆盖」而不是 <c>settings.Playback = new()</c>。</b> 这份文档的几个子对象被别处
/// 抓在手里：容器把 <c>AppSettings.Shaders</c> 直接交给单例 <c>ShaderGroupResolver</c>，<c>AudioDeviceCatalogue</c>
/// 闭包着 <c>AppSettings.Mpv</c>，设置页每一行的读写对捕获的也是子对象本身（<c>var playback = Settings.Playback;</c>）。
/// 换成新对象，那些手就还攥着旧的那一份 —— 屏上每一行都显示成默认值，而真去放一部片子时着色器档位、libmpv 路径
/// 一个都没变。这是那种「三处读数全对、行为照旧」的坏法，所以这里一个新对象都不往外交。
/// </para>
/// <para>
/// <b>为什么用反射。</b> 五组里有四组的规矩就是一句「每一个字段都回默认」—— 那正好是反射能一次说完、而四十几行
/// 手写赋值只能说「今天这四十几个字段」的一句话。这个文件里没有出现任何属性名，所以也没有什么会跟着改名失效；
/// 反过来，手写那一份的坏法是「哪天加了一项设置，没人记得回来补一行」，而那时候屏上什么都看不出来。
/// </para>
/// <para>
/// <b>音量和「界面」那一组反过来写：列的是留下来的，不是清掉的。</b> 这两组是混着的，而两种写法今天等价、明天不
/// 等价 —— 以后往 <see cref="UiSettings"/> 上加一项设置，「列出要清的」会漏掉它（漏一项设置就是这个功能悄悄不完
/// 整），「列出要留的」会把它一起清掉（顶多是多清了一件记下来的东西）。所以默认清，例外具名。
/// </para>
/// </summary>
public static class SettingsReset
{
    /// <summary>
    /// 就地把 <paramref name="settings"/> 改回装机时的样子，交回同一个对象。落盘由调用方负责
    /// （<see cref="SettingsStore.Save"/>）。
    /// </summary>
    public static AppSettings Restore(AppSettings settings)
    {
        // 这四组整组回默认：里面每一个字段都是设置页上的一行，没有例外要留。
        Overwrite(settings.Mpv, new MpvSettings());
        Overwrite(settings.Playback, new PlaybackSettings());
        Overwrite(settings.Video, new VideoSettings());
        Overwrite(settings.Shaders, new ShaderAutomationSettings());

        // 音量不是设置页上的一行，是播放器上次被留在哪儿（见 AudioSettings.Volume）。每次播放都是新起的 mpv，
        // 它自己那份音量永远是 100，所以清掉这个数就是把用户调好的音量抹掉，而设置页上没有任何一行提过它。
        Overwrite(settings.Audio, new AudioSettings { Volume = settings.Audio.Volume });

        // 「界面」这一组混着两类东西：主题、每页条目数、图片缓存上限、评分来源、主页版面都是设置页上
        // 的行，回默认；下面这几项是程序替他记的，设置页上一行都没有，留着。
        Overwrite(settings.Ui, new UiSettings
        {
            // 窗口关掉时多大、在哪儿、有没有最大化。清成 0 就是「还没记过」，下次开窗会回到算出来的那个尺寸 ——
            // 也就是替用户宣布他从没拉过现在这个窗口。
            WindowLeft = settings.Ui.WindowLeft,
            WindowTop = settings.Ui.WindowTop,
            WindowWidth = settings.Ui.WindowWidth,
            WindowHeight = settings.Ui.WindowHeight,
            WindowMaximized = settings.Ui.WindowMaximized,

            // 上次开的哪个库，和每个库各自被留在什么排序、什么筛选、什么视图上。Emby 自己也是按库记的，
            // 而「把电影库的排序改回名称」不是任何人按这颗按钮的意思。
            LastLibraryId = settings.Ui.LastLibraryId,
            Sort = settings.Ui.Sort,
            Filters = settings.Ui.Filters,
            Views = settings.Ui.Views
        });

        // 身份那一摊（DeviceId、Servers、LastServerId、LastAccountId）一个字都没碰：这里没有一句提到它们，
        // 而 AppSettings 这一层本身不走上面那套覆盖，正是为了让「不碰」是这个文件的默认状态而不是一条例外。
        //
        // 归一化跟着跑一遍，好让这里交出去的这一份和 Save 会写下去的那一份完全一样（设备 id 补齐、记着的
        // 服务器和账号对得上）。默认值本来就都在范围内，所以夹取那几句在这条路上不会动任何东西。
        return SettingsMigration.Normalize(settings);
    }

    /// <summary>
    /// 把 <paramref name="defaults"/> 上每一个可读可写的公开实例属性抄到 <paramref name="live"/> 上。
    /// <para>
    /// 抄值而不是换对象，理由见类注释；<c>defaults</c> 是个当场造出来、抄完就扔的对象，所以列表和字典这类
    /// 引用直接交过去就行 —— 活着的那一份从此拿着一份新的空列表，没有第二个人指着它。
    /// </para>
    /// <para>
    /// <see cref="BindingFlags.Instance"/> 写明了：不写的话静态属性也会被枚举到，而给静态属性赋值会绕过
    /// <paramref name="live"/> 改掉整个类型上的那一份。这几个设置类今天都没有静态属性，这一句防的是以后。
    /// </para>
    /// </summary>
    private static void Overwrite<T>(T live, T defaults) where T : class
    {
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property is { CanRead: true, CanWrite: true })
                property.SetValue(live, property.GetValue(defaults));
        }
    }
}
