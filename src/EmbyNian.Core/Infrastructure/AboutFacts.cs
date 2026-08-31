using System.Diagnostics;

namespace EmbyNian.Infrastructure;

/// <summary>
/// 「关于」那张卡上的几句读数：这份程序是哪一版、什么时候构建的、拿哪个播放内核在放。
/// <para>
/// 值得单独有一个类型，是因为这三句话在这一版之前界面上一次都没有出现过 —— 版本号只写进日志和自检报告，
/// 而看报告的人和用程序的人不是同一件事。出了问题第一句要问的就是「你用的是哪一版」，而在这之前答不上来。
/// </para>
/// <para>
/// 三个方法都收路径、不自己去找：Core 不知道 exe 在哪（外壳知道），而收进来之后它们就是可测的纯函数 ——
/// 读不到的那一档（文件不在、版本资源是空的）正是要钉住的那一档，因为它在一台好机器上永远碰不到。
/// </para>
/// </summary>
public static class AboutFacts
{
    /// <summary>读不到时统一说这一句，而不是留一行空白：空白读起来像「这一项还在加载」。</summary>
    public const string Unknown = "读不到";

    /// <summary>播放内核那个文件的名字。版本不要换（见项目规矩），所以这里也只报它自己的版本。</summary>
    public const string CoreLibrary = "libmpv-2.dll";

    /// <summary>「EmbyNian 3.0.0」—— 版本号从程序集上来，见 <see cref="AppIdentity.Version"/>。</summary>
    public static string Client => AppIdentity.TitleWithVersion;

    /// <summary>
    /// 这份 exe 是什么时候落到磁盘上的。用文件时间而不是编译进去的一个常量：这个项目没有发布流程，
    /// 版本号一连几十次改动都是同一个 3.0.0，「哪一次构建」只有文件时间答得出。
    /// </summary>
    public static string BuiltAt(string? executablePath) =>
        Stamp(executablePath) is { } when ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : Unknown;

    /// <summary>
    /// 播放内核那个文件的版本，「libmpv-2.dll 2.5.0.0」。找不到文件或者它没带版本资源就只报文件名加
    /// <see cref="Unknown"/> —— 那两种情况下程序其实是放不出画面的，报告里能看出来比空着好。
    /// </summary>
    public static string PlaybackCore(string? directory)
    {
        var version = Version(directory is null ? null : Path.Combine(directory, CoreLibrary));
        return $"{CoreLibrary} {version ?? Unknown}";
    }

    private static DateTimeOffset? Stamp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception)
        {
            // 一行读数不值得让「关于」这张卡打不开：拿不到就当读不到。
            return null;
        }
    }

    private static string? Version(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            if (!File.Exists(path)) return null;

            var info = FileVersionInfo.GetVersionInfo(path);
            var text = info.FileVersion ?? info.ProductVersion;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
