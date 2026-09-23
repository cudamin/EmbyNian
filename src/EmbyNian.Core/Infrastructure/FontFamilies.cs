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
    /// Fallback family: Microsoft YaHei UI Semibold — the default since v19 (2026-09-22).
    /// <para>
    /// It answers for an empty setting because <b>every Windows install has it</b>, so nothing has to be
    /// shipped and the family always resolves. It is also the face people were already seeing: setting the
    /// bundled 方正中等线简体 at the 细 step asks mpv for a nonexistent 「方正中等线简体 Light」, and
    /// libass's own fallback resolved the CJK glyphs to Microsoft YaHei UI Semibold (measured 2026-09-22
    /// against the shipped libmpv: <c>fontselect: (方正中等线简体 Light) -> ArialMT</c> for Latin, then
    /// <c>Glyph 0x6D4B not found ... -> MicrosoftYaHeiUISemibold</c> for the Chinese). That look was
    /// preferred, so it is now reached by name rather than by accident.
    /// </para>
    /// <para>
    /// No subtitle font is bundled any more (v19 dropped 方正中等线简体). A family mpv cannot find anywhere
    /// degrades to libass's own fallback, which on CJK text is how tofu happens — the reason this has to be
    /// a family every machine actually has, and YaHei UI is.
    /// </para>
    /// </summary>
    public const string Default = "Microsoft YaHei UI Semibold";

    private static readonly Dictionary<string, string> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        // None of these are bundled any more (v19 dropped 方正中等线简体, v18 dropped Noto Sans CJK).
        // The whole table is a courtesy for a hand-typed path or a settings file from an older build: the
        // file may sit in C:\Windows\Fonts, and mapping its name to the family it stands for is better than
        // passing a path mpv would ignore. The two unbundled families' names stay mapped for exactly that
        // reason — a leftover stored path still resolves to a real installed family instead of tofu.
        ["方正中等线简体.ttf"] = "方正中等线简体",
        ["fzzhongdengxian-z07s.ttf"] = "方正中等线简体",
        ["notosanscjksc-vf.ttf"] = "Noto Sans CJK SC",
        ["notosanscjk-regular.ttc"] = "Noto Sans CJK SC",
        ["notosanscjk-bold.ttc"] = "Noto Sans CJK SC",
        ["notosanscjk-light.ttc"] = "Noto Sans CJK SC",
        ["notosanscjk-medium.ttc"] = "Noto Sans CJK SC",
        ["notosanscjk-demilight.ttc"] = "Noto Sans CJK SC",
        ["notosanscjk-thin.ttc"] = "Noto Sans CJK SC",
        ["notosanscjk-black.ttc"] = "Noto Sans CJK SC",
        ["notosanscjksc-regular.otf"] = "Noto Sans CJK SC",
        ["notosanscjksc-bold.otf"] = "Noto Sans CJK SC",
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
            || value.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".otc", StringComparison.OrdinalIgnoreCase))
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
