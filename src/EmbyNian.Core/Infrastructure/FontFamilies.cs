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
    /// <summary>Fallback family: the one CJK-capable family present on every Windows install.</summary>
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
}
