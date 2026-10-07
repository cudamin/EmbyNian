using System.Globalization;
using System.Text.Json;

namespace EmbyNian.MoviePilot;

/// <summary>一条可下载资源；原始种子只用于下载请求，不进入日志或网页地址。</summary>
public sealed record MoviePilotResource
{
    public required string Title { get; init; }
    internal string ConnectionStamp { get; init; } = "";
    public string? Description { get; init; }
    public string? SiteName { get; init; }
    public long Size { get; init; }
    public int Seeders { get; init; }
    public string? Resolution { get; init; }
    public string? PageUrl { get; init; }
    public string? PublishedText { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Promotion { get; init; }
    public double? DownloadFactor { get; init; }
    public double? UploadFactor { get; init; }
    public string? FreeUntil { get; init; }
    public bool HitAndRun { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public required JsonElement TorrentInfo { get; init; }
    public string? MediaSource { get; init; }
    public string? MediaId { get; init; }
    public string? MediaType { get; init; }
    public JsonElement MediaInfo { get; init; }
    public string? DownloadProblem { get; init; }

    public string Confirmation => $"资源：{Title}\n站点：{SiteName ?? "未提供"}\n" +
        (MediaId is { Length: > 0 } ? $"媒体：{MoviePilotSourceNames.Label(MediaSource)} · {MediaId} · {MediaType ?? "类型未提供"}\n" : "") +
        $"{PromotionText}\n" + (HitAndRun ? "此资源有 HR 考核，请先在种子页面核对做种要求。" : "优惠和下载规则以站点当前页面为准。");

    public string SizeText => Size > 0 ? MoviePilotDownload.SizeText(Size) : "";
    public string LabelsText => string.Join(" · ", Labels);
    public string PublishedLine => PublishedAt is { } time
        ? $"发布于 {time.ToLocalTime():yyyy-MM-dd HH:mm}"
        : string.IsNullOrWhiteSpace(PublishedText) ? "发布时间未提供" : $"发布于 {PublishedText}";

    public string PromotionText
    {
        get
        {
            if (DownloadFactor is { } download)
            {
                var label = download switch
                {
                    0 => "免费下载",
                    1 => "普通",
                    _ => $"下载 {download.ToString("0.##", CultureInfo.InvariantCulture)} 倍"
                };
                if (UploadFactor is { } upload && upload != 1)
                    label += $" · 上传 {upload.ToString("0.##", CultureInfo.InvariantCulture)} 倍";
                return label;
            }
            return string.IsNullOrWhiteSpace(Promotion) ? "优惠未提供" : Promotion;
        }
    }

    public bool IsFree => DownloadFactor == 0 ||
        (DownloadFactor is null && Promotion is { } promotion &&
         FreePromotions.Contains(promotion.Trim()));

    public bool HasDiscount => IsFree || DownloadFactor is >= 0 and < 1 || UploadFactor > 1 ||
        (DownloadFactor is null && Promotion is { } promotion && DiscountPromotions.Contains(promotion.Trim()));

    private static readonly HashSet<string> FreePromotions = new(StringComparer.OrdinalIgnoreCase)
        { "FREE", "2XFREE", "4XFREE", "免费", "2X免费", "4X免费" };
    private static readonly HashSet<string> DiscountPromotions = new(StringComparer.OrdinalIgnoreCase)
        { "2X", "4X", "50%", "2X 50%", "70%", "30%", "75%", "25%" };

    /// <summary>只打开站点详情；不把 enclosure、Cookie 或自定义协议交给系统执行。</summary>
    public Uri? DetailsUri => Uri.TryCreate(PageUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri : null;

    public string MetaLine => string.Join("  ·  ", new[]
    {
        Resolution, SizeText, $"{Seeders} 个做种", HitAndRun ? "HR 考核" : null
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
}
