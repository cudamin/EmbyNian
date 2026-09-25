namespace EmbyNian.Configuration;

/// <summary>
/// 恢复默认设置：把设置页上那几张卡管的东西全部改回装机时的样子，不动服务器、账号，也不动那些「记下来的」位置。
/// <para>
/// 「哪些算设置、哪些不动」这份判据现在住在 <see cref="SettingsPreferences"/>，恢复默认、配置备份、恢复配置三处
/// 共用同一份 —— 从前它是这个文件独有的，另开一份备份就意味着把同一份分区抄第二遍，而那正是它自己反复警惕的
/// 那种「两处名单迟早对不上」的坑。恢复默认因此就是「把偏好抄成一个全新对象的样子」：<see cref="SettingsPreferences.Apply"/>
/// 的 source 传一个当场造出来的 <see cref="AppSettings"/>，里面每个子对象都是各自的装机默认。
/// </para>
/// </summary>
public static class SettingsReset
{
    /// <summary>
    /// 就地把 <paramref name="settings"/> 改回装机时的样子，交回同一个对象。落盘由调用方负责
    /// （<see cref="SettingsStore.Save"/>）。身份、记下来的位置、音量、MoviePilot 一个字不动 —— 判据见
    /// <see cref="SettingsPreferences"/>。
    /// </summary>
    public static AppSettings Restore(AppSettings settings) =>
        SettingsPreferences.Apply(settings, new AppSettings());
}
