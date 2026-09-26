using System.Globalization;
using EmbyNian.Configuration;

namespace EmbyNian.Mpv;

public static class HdrOptions
{
    public const double MinimumNits = 10;
    public const double MaximumNits = 10000;

    public static readonly MpvChoice[] ToneMappings =
    [
        new("", "继承画质预设"), new("auto", "自动"), new("spline", "Spline"),
        new("bt.2390", "BT.2390"), new("bt.2446a", "BT.2446A"), new("hable", "Hable"),
        new("mobius", "Mobius"), new("reinhard", "Reinhard"),
        new("st2094-40", "ST 2094-40"), new("st2094-10", "ST 2094-10")
    ];

    public static readonly MpvChoice[] PeakDetection =
    [
        new("", "继承画质预设"), new("auto", "自动"), new("yes", "开启"), new("no", "关闭")
    ];

    public static double? Clamp(double? value, double minimum, double maximum) =>
        value is { } number && double.IsFinite(number) ? Math.Clamp(number, minimum, maximum) : null;

    public static bool OwnsColorSpace(VideoSettings video, SourceProfile? source) =>
        source is { IsHdr: true } && video.HdrMode is "tonemap" or "passthrough";

    public static IReadOnlyList<KeyValuePair<string, string>> Build(VideoSettings video, SourceProfile? source)
    {
        var options = new List<KeyValuePair<string, string>>();
        var hdr = source is { IsHdr: true };
        var sdr = hdr && video.HdrMode == "tonemap";

        if (OwnsColorSpace(video, source))
        {
            options.Add(new("target-colorspace-hint", sdr ? "no" : "auto"));
            if (sdr)
            {
                options.Add(new("target-trc", "bt.1886"));
                options.Add(new("target-prim", "bt.709"));
            }
            else
            {
                // Target mode retains tone mapping when the display cannot reproduce the source peak.
                options.Add(new("target-colorspace-hint-mode", "target"));
            }
        }

        if (video.ToneMapping.Length > 0) options.Add(new("tone-mapping", video.ToneMapping));
        if (video.HdrComputePeak.Length > 0) options.Add(new("hdr-compute-peak", video.HdrComputePeak));
        if (hdr && !sdr) AddNumber("target-peak", video.HdrPeakNits);
        AddNumber("hdr-reference-white", video.HdrReferenceWhiteNits);
        AddNumber("sub-hdr-peak", video.HdrSubtitleNits);
        AddNumber("image-subs-hdr-peak", video.HdrImageSubtitleNits);
        AddNumber("hdr-contrast-recovery", video.HdrContrastRecovery, 0, 2);

        var filters = new List<string>();
        if (!video.DolbyVisionMetadata) filters.Add("dolbyvision=no");
        if (!video.DolbyVisionEnhancementLayer) filters.Add("enhancement-layer=no");
        if (!video.Hdr10PlusMetadata) filters.Add("hdr10plus=no");
        if (filters.Count > 0) options.Add(new("vf", "@embynian-hdr:format=" + string.Join(':', filters)));
        return options;

        void AddNumber(string name, double? value, double minimum = MinimumNits, double maximum = MaximumNits)
        {
            if (Clamp(value, minimum, maximum) is { } number)
                options.Add(new(name, number.ToString("0.###", CultureInfo.InvariantCulture)));
        }
    }
}
