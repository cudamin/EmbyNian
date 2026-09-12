namespace EmbyNian.Emby;

/// <summary>
/// 详情页头图的模糊 —— 「增加集页面背景图的模糊程度」。
/// <para>
/// 算法是分离式盒式模糊跑三趟，近似高斯：一趟盒式模糊的轮廓是方波、亮边上看得出台阶，三趟卷积出来就
/// 是一条平滑的钟形曲线，而每一趟都是滑动窗口加减两个数，1280 宽的剧照整段做完也就二十来兆次整数加减，
/// 比真高斯（每像素一次二维卷积）便宜两个数量级。
/// </para>
/// <para>
/// 放在这里而不是视图那一层：它是「给一张图，回答一张图」的纯函数，和 <see cref="CarouselBackdrop"/> 同
/// 一类 —— 屏上那一半（解码、重编码、<c>BitmapImage</c>）归 Shell 的 HeroPictureLoader，这里只碰像素。
/// </para>
/// </summary>
public static class BackdropBlur
{
    /// <summary>
    /// 集页头图的模糊半径，按 1280 的解码宽度给（<c>DetailViewModel.HeroDecodeWidth</c>）。
    /// <para>
    /// 24 是「人、字、场景全都只剩色块、看不清内容」和「还留得住明暗构图」之间的那一档：集页的头图是
    /// 剧集那张剧照，上面压着片名和一整带集卡，清晰的那版在亮画面上连剧里人的表情都看得清，和压在
    /// 上面的字抢。再小（12 上下）五官还认得出，再大（40 往上）就只剩一团匀色、连明暗走向都没了。
    /// 图在屏上还要被放大一到两倍，等效半径跟着再长 —— 调它的时候按屏上的样子调，不按这个数本身。
    /// </para>
    /// </summary>
    public const int EpisodeHeroRadius = 24;

    /// <summary>
    /// 裁切驱动的模糊半径 —— 「当窗口拉宽导致背景图下方被裁切的时候，触发背景图模糊，裁切越多越模糊」。
    /// 总半径 = <paramref name="baseline"/> + 裁切占比 × <paramref name="extra"/>，再量化到
    /// <paramref name="step"/> 的整数倍：拖窗口时占比一路变，量化让重糊只发生在档位切换那一下，而不是
    /// 每一个像素都重跑一遍模糊。
    /// <para>
    /// 裁切占比不超过 <paramref name="start"/> 不糊（2026-09-12「提高拉长窗口后，背景被裁切触发背景图模糊的
    /// 阈值」）：拉长窗口头几档的轻微裁切，画面还认得出构图，整张图陪着糊是白糊。门槛放在四分之一 —— 裁掉
    /// 四分之一往上，画面才进入「衬底」的读法，模糊从那里起步；基础半径不参与这道门，常驻的照旧常驻。
    /// </para>
    /// </summary>
    /// <param name="crop">画面被裁掉的占比 0..1（<see cref="DetailHero.PictureCrop"/> 给的那个数）。</param>
    /// <param name="start">不起糊的门槛：占比不超过它，裁切加成按 0 算。</param>
    /// <param name="baseline">常驻的基础半径（集页 24，电影和剧 0）。</param>
    /// <param name="extra">裁切到最狠时再加多少。</param>
    /// <param name="step">量化的档距。</param>
    public static int CropRadius(double crop, double start, int baseline, int extra, int step)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(baseline);
        ArgumentOutOfRangeException.ThrowIfNegative(extra);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);

        var share = Math.Clamp(crop, 0, 1);
        var radius = baseline + (share <= start ? 0 : share * extra);
        return (int)Math.Round(radius / step) * step;
    }

    /// <summary>
    /// 对顶行在前的 BGRA 像素做三趟分离式盒式模糊，结果写回原数组。边沿按最近的像素夹住，不外延黑色。
    /// </summary>
    /// <param name="pixels">整张图的 BGRA 像素，长度必须是 宽 × 高 × 4。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <param name="radius">模糊半径，0 或负数原样不动。</param>
    public static void BoxBlur(Span<byte> pixels, int width, int height, int radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var stride = checked(width * 4);
        if (pixels.Length != checked(stride * height))
            throw new ArgumentException("像素数与尺寸不一致。", nameof(pixels));
        if (radius <= 0) return;

        var temp = new byte[pixels.Length];

        for (var pass = 0; pass < 3; pass++)
        {
            BlurHorizontal(pixels, temp, width, height, radius);
            BlurVertical(temp, pixels, width, height, radius);
        }
    }

    /// <summary>横向一趟：每行每通道一个滑窗，右进一个、左出一个，窗口里是 2r+1 个像素。</summary>
    private static void BlurHorizontal(ReadOnlySpan<byte> source, Span<byte> target, int width, int height, int radius)
    {
        var stride = width * 4;
        var window = radius * 2 + 1;

        for (var y = 0; y < height; y++)
        {
            var row = y * stride;

            for (var channel = 0; channel < 4; channel++)
            {
                var sum = 0;
                for (var x = -radius; x <= radius; x++)
                    sum += source[row + Math.Clamp(x, 0, width - 1) * 4 + channel];

                for (var x = 0; x < width; x++)
                {
                    target[row + x * 4 + channel] = (byte)((sum + window / 2) / window);
                    sum += source[row + Math.Min(width - 1, x + radius + 1) * 4 + channel]
                        - source[row + Math.Max(0, x - radius) * 4 + channel];
                }
            }
        }
    }

    /// <summary>竖向一趟：每个列通道一个滑窗，下一个、上一个，同横向那趟只是把步长换成一整行。</summary>
    private static void BlurVertical(ReadOnlySpan<byte> source, Span<byte> target, int width, int height, int radius)
    {
        var stride = width * 4;
        var window = radius * 2 + 1;

        for (var i = 0; i < stride; i++)
        {
            var sum = 0;
            for (var y = -radius; y <= radius; y++)
                sum += source[Math.Clamp(y, 0, height - 1) * stride + i];

            for (var y = 0; y < height; y++)
            {
                target[y * stride + i] = (byte)((sum + window / 2) / window);
                sum += source[Math.Min(height - 1, y + radius + 1) * stride + i]
                    - source[Math.Max(0, y - radius) * stride + i];
            }
        }
    }
}
