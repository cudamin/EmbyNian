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
    /// Fallback family: Noto Sans CJK SC — 「默认字体和当前字体改用Noto Sans CJK SC，这个字体要内置到
    /// 程序里」 (2026-09-21).
    /// <para>
    /// It answers for an empty setting because it travels with the program: the file rides in
    /// assets/fonts and mpv is handed that folder as <c>sub-fonts-dir</c> at every launch, so the family
    /// resolves on a machine that never installed it. That is the same argument 方正中等线简体 had, and
    /// the difference is which font was asked for: 方正 shipped because 「字幕默认用方正中等线简体」 said
    /// so (v12), Microsoft YaHei took the seat at v14 (「默认字体改为Microsoft YaHei」) for the opposite
    /// reason — every Windows box has it — and this one is the family the reference player's own
    /// mpv.conf names (<c>sub-font="Noto Sans CJK SC"</c>).
    /// </para>
    /// <para>
    /// <b>What is bundled is the single-face <c>NotoSansCJKsc-VF.ttf</c>, not the <c>.ttc</c> collection
    /// the reference project ships.</b> Measured 2026-09-21: libass's <c>process_fontdata</c> walks
    /// <c>face_index</c> from 0 to <c>face-&gt;num_faces</c> and registers <em>every</em> face of a file as
    /// a family of its own (the reason a <c>.ttc</c> works in <c>~/.config/mpv/fonts</c> on Linux — the
    /// directory there goes through fontconfig, which does the same thing). Both Noto CJK collections hold
    /// ten faces each: Noto Sans CJK SC/TC/JP/KR/HK plus the matching five of Noto Sans <b>Mono</b> CJK.
    /// Dropping the collection in would therefore put ten families into the 字体 list where one was asked
    /// for, and the SC family would arrive with four sibling variants a user cannot tell apart by name.
    /// The VF file answers to exactly one family name and carries the whole weight axis, which is also
    /// what makes 字幕加粗 work without a second file.
    /// </para>
    /// <para>
    /// Previous occupants stay bundled and selectable; they just no longer answer for 「never picked」.
    /// A family mpv cannot find anywhere degrades to its own fallback, which on CJK text is how tofu
    /// happens — the reason this has to be a family the shipped directory actually contains.
    /// </para>
    /// </summary>
    public const string Default = "Noto Sans CJK SC";

    private static readonly Dictionary<string, string> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        // The bundled families first, so a stored path to one of them maps back to the family this
        // client ships. Noto Sans CJK rides as one single-face variable file (see Default's remarks for
        // why the .ttc collection the reference project uses is not what is bundled) — so unlike the
        // weight-indexed .ttc names below, this one file is the whole family.
        ["notosanscjksc-vf.ttf"] = "Noto Sans CJK SC",
        // The collection names stay mapped anyway: a settings file from a build that bundled them, or a
        // hand-typed path to the copy in C:\Windows\Fonts, still resolves to the family it stands for.
        ["notosanscjk-regular.ttc"] = "Noto Sans CJK SC",
        ["notosanscjk-bold.ttc"] = "Noto Sans CJK SC",
        ["notosanscjk-light.ttc"] = "Noto Sans CJK SC",
        ["notosanscjk-medium.ttc"] = "Noto Sans CJK SC",
        ["notosanscjk-demilight.ttc"] = "Noto Sans CJK SC",
        ["notosanscjk-thin.ttc"] = "Noto Sans CJK SC",
        ["notosanscjk-black.ttc"] = "Noto Sans CJK SC",
        ["notosanscjksc-vf.ttf"] = "Noto Sans CJK SC",
        ["notosanscjksc-regular.otf"] = "Noto Sans CJK SC",
        ["notosanscjksc-bold.otf"] = "Noto Sans CJK SC",
        ["方正中等线简体.ttf"] = "方正中等线简体",
        ["fzzhongdengxian-z07s.ttf"] = "方正中等线简体",
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
