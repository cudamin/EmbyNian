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
    /// Fallback family: Microsoft YaHei — 「不要放字体进去，默认就用雅黑」 (2026-09-21).
    /// <para>
    /// It answers for an empty setting because <b>every Windows install has it</b>, so nothing has to be
    /// shipped and the family always resolves. That is the opposite argument to the one that briefly ran
    /// v17: for one day the default was a bundled Noto Sans CJK SC <em>variable</em> font, and its default
    /// instance was Thin (weight 100) — which is why subtitles rendered far thinner than the reference
    /// player until this reverted it. YaHei is a normal weight and its Light / Bold faces
    /// (<c>msyhl.ttc</c> / <c>msyhbd.ttc</c>) are what <see cref="ResolveWeighted"/> reaches for the 细 / 粗
    /// steps — again, no bundling.
    /// </para>
    /// <para>
    /// 方正中等线简体 stays bundled and selectable; it just no longer answers for 「never picked」. A
    /// family mpv cannot find anywhere degrades to its own fallback, which on CJK text is how tofu
    /// happens — the reason this has to be a family every machine actually has.
    /// </para>
    /// </summary>
    public const string Default = "Microsoft YaHei";

    private static readonly Dictionary<string, string> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        // 方正中等线简体 is the one font this client still bundles, so a stored path to it maps back to
        // the family. Everything else here is a courtesy for a hand-typed path or a settings file from an
        // older build: the file may sit in C:\Windows\Fonts, and mapping its name to the family it stands
        // for is better than passing a path mpv would ignore. Noto Sans CJK is no longer bundled (v18,
        // 「不要放字体进去」) but its names stay mapped for exactly that reason.
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

    /// <summary>
    /// The <c>sub-font</c> family string and the <c>sub-bold</c> flag for a chosen family at a chosen
    /// 字重. mpv has no numeric weight option (measured 2026-09-06: <c>sub-font-weight</c> does not exist),
    /// so a weight is realised here, at the level libass can act on:
    /// <list type="bullet">
    /// <item>细 (≤ <see cref="Configuration.PlaybackSettings.LightSubtitleWeight"/>): the family's Light sibling
    /// (「Microsoft YaHei」 → 「Microsoft YaHei Light」). A family without a Light face falls back to itself,
    /// so 细 is a no-op there rather than tofu — libass matches by name and keeps the base when the light
    /// name resolves nowhere.</item>
    /// <item>常规: the family as-is, no bold.</item>
    /// <item>粗 (≥ <see cref="Configuration.PlaybackSettings.BoldSubtitleWeight"/>): the family plus
    /// <c>sub-bold=yes</c>, which uses a real bold face when the family has one (YaHei does) and libass's
    /// synthetic emboldening otherwise — the exact behaviour the old 加粗 toggle had.</item>
    /// </list>
    /// Measured 2026-09-21 against the shipped libmpv: Microsoft YaHei Light / Regular / Bold render as a
    /// clean light→heavy ladder, and the reason this is a family-level trick rather than an option is that
    /// the variable-font route (a single VF driven by name) came out non-monotonic on this build.
    /// </summary>
    public static (string Font, bool Bold) ResolveWeighted(string? configured, int weight)
    {
        var family = Resolve(configured);
        var w = Configuration.PlaybackSettings.ClampWeight(weight);

        if (w >= Configuration.PlaybackSettings.BoldSubtitleWeight) return (family, true);

        if (w <= Configuration.PlaybackSettings.LightSubtitleWeight)
        {
            // Do not stack 「 Light」 onto a family that already names it, or a picked
            // 「Microsoft YaHei Light」 at 细 would ask mpv for 「... Light Light」.
            var light = family.EndsWith(" Light", StringComparison.OrdinalIgnoreCase)
                ? family
                : family + " Light";
            return (light, false);
        }

        return (family, false);
    }
}
