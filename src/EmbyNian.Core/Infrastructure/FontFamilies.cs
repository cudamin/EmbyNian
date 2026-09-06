namespace EmbyNian.Infrastructure;

/// <summary>
/// Font *family* names for the Windows font files this client used to store paths to.
/// <para>
/// mpv's <c>sub-font</c> takes a family name — "Microsoft YaHei" — and has no way to be handed a
/// file. Up to v3 the settings stored a path anyway, so the migration and the launch planner both
/// need to turn one back into the name it stands for. The table is deliberately small: the CJK and
/// UI families a Chinese Windows install actually has, because those are the ones a 字幕字体 setting
/// was ever pointed at.
/// </para>
/// </summary>
public static class FontFamilies
{
    /// <summary>
    /// Fallback family: Microsoft YaHei, which every Windows install carries — the reason the first
    /// fallback chose it, and the reason it was given the job again (v14, 「默认字体改为Microsoft YaHei」).
    /// It had lost the seat to 方正中等线简体 in v12, on the grounds that the bundled copy travels with
    /// the program and is therefore always findable; that font stays bundled (assets/fonts, handed to
    /// mpv as <c>sub-fonts-dir</c>) and selectable, it just no longer answers for an empty setting —
    /// 雅黑 does, on every machine this Windows-only client runs on. A family mpv cannot find anywhere
    /// degrades to its own fallback, which on CJK text is how tofu happens.
    /// </summary>
    public const string Default = "Microsoft YaHei";

    private static readonly Dictionary<string, string> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msyh.ttc"] = "Microsoft YaHei",
        ["msyh.ttf"] = "Microsoft YaHei",
        ["msyhbd.ttc"] = "Microsoft YaHei",
        ["msyhl.ttc"] = "Microsoft YaHei Light",
        ["msyhsb.ttc"] = "Microsoft YaHei",
        ["msjh.ttc"] = "Microsoft JhengHei",
        ["msjhbd.ttc"] = "Microsoft JhengHei",
        ["msjhl.ttc"] = "Microsoft JhengHei Light",
        ["simsun.ttc"] = "SimSun",
        ["simsunb.ttf"] = "SimSun",
        ["simhei.ttf"] = "SimHei",
        ["simkai.ttf"] = "KaiTi",
        ["simfang.ttf"] = "FangSong",
        ["simli.ttf"] = "LiSu",
        ["simyou.ttf"] = "YouYuan",
        ["stkaiti.ttf"] = "STKaiti",
        ["stsong.ttf"] = "STSong",
        ["stzhongs.ttf"] = "STZhongsong",
        ["stfangso.ttf"] = "STFangsong",
        ["stxihei.ttf"] = "STXihei",
        ["dengb.ttf"] = "DengXian",
        ["deng.ttf"] = "DengXian",
        ["dengl.ttf"] = "DengXian Light",
        ["msgothic.ttc"] = "MS Gothic",
        ["msmincho.ttc"] = "MS Mincho",
        ["yugothm.ttc"] = "Yu Gothic",
        ["yugothb.ttc"] = "Yu Gothic",
        ["malgun.ttf"] = "Malgun Gothic",
        ["arial.ttf"] = "Arial",
        ["arialbd.ttf"] = "Arial",
        ["tahoma.ttf"] = "Tahoma",
        ["verdana.ttf"] = "Verdana",
        ["segoeui.ttf"] = "Segoe UI",
        ["seguisb.ttf"] = "Segoe UI Semibold",
        ["consola.ttf"] = "Consolas",
        ["times.ttf"] = "Times New Roman",
        ["cour.ttf"] = "Courier New",
        ["calibri.ttf"] = "Calibri"
    };

    /// <summary>
    /// The family a font file belongs to, or null when the file is not one of the known ones.
    /// Takes a full path or a bare file name; only the file name is looked at, so a font copied
    /// somewhere else is still recognised.
    /// </summary>
    public static string? FromFileName(string? pathOrFileName)
    {
        var value = (pathOrFileName ?? "").Trim();
        if (value.Length == 0) return null;

        var name = value.Split('/', '\\').LastOrDefault();
        if (string.IsNullOrEmpty(name)) return null;

        return ByFileName.TryGetValue(name, out var family) ? family : null;
    }

    /// <summary>
    /// The 字幕字体 setting as the family name to hand mpv's <c>sub-font</c>. An empty choice falls back
    /// to <see cref="Default"/> — the family the program ships with, which <c>sub-fonts-dir</c> makes
    /// findable on every install of this client.
    /// <para>
    /// v3 stored the path of a file under C:\Windows\Fonts in that setting and passed it straight
    /// through, which mpv quietly ignored: it looked for a family literally called
    /// "C:\Windows\Fonts\msyh.ttc", found none and fell back to sans-serif. Nobody noticed because the
    /// user's mpv.conf named a real family of its own, and mpv.conf was read after those arguments. A
    /// leftover path is still recognised here rather than sent as-is, because
    /// <see cref="Configuration.SettingsMigration"/> can only map the files whose family names it knows.
    /// </para>
    /// <para>
    /// This lived in <c>PlaybackPlanner</c> until 2026-09-05, back when the font was the one subtitle
    /// option travelling on the launch request instead of in the option list with the other ten. It
    /// moved here when that stopped being true, so 「哪个族名会被发出去」 still has exactly one answer.
    /// </para>
    /// </summary>
    public static string Resolve(string? configured)
    {
        var value = (configured ?? "").Trim();
        if (value.Length == 0) return Default;

        // A path would be meaningless to mpv; the family behind it may not be, so it is looked up.
        if (value.Contains('\\') || value.Contains('/') || value.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
        {
            var family = FromFileName(value);
            Diagnostics.Log.Warn("mpv", family is null
                ? $"字幕字体填的是文件路径，mpv 只认字体族名，已改用 {Default}：{value}"
                : $"字幕字体填的是文件路径，已换成对应的字体族名「{family}」：{value}");
            return family ?? Default;
        }

        return value;
    }
}
