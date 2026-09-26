using EmbyNian.Theming;

namespace EmbyNian.Playback;

/// <summary>
/// 加载遮罩垫底那张背景图在屏上的摆法。
/// <para>
/// <c>PlayerPage.xaml</c> 里这块垫图叫 <c>CoverArtwork</c>：贴着遮罩网格铺满、<c>UniformToFill</c>、
/// 压到 <see cref="Opacity"/> 半透明，底下是 <c>PlayerCoverBrush</c> 那块近黑。这里说的是同一件事，
/// 但只说给算得出来的人听。
/// </para>
/// <para>
/// <b>为什么它要在 Core 里有一份。</b>起播自动全屏那一趟，窗口从浏览几何长到整屏的那一两拍要有人替
/// 内容挡着 —— 挡它的那块层由 DWM 直接合成、不吃岛的提交延迟，而它<b>显示的必须是这张垫图本身</b>，
/// 不能是那块近黑底色：底色就是用户看到的「先全屏黑一片、再切到背景图」（2026-09-25 用户令
/// 「不要黑屏，主页和背景图无缝切换」，那一天之前那块层铺的是纯色）。于是「把一张图烘成遮罩里那个样子」
/// 成了一条要能被单测盯住的算法。
/// </para>
/// <para>
/// <b>漂移风险。</b><see cref="Opacity"/> 与 XAML 里 <c>CoverArtwork.Opacity</c> 是同一个数，写在两处；
/// 改一处就得改另一处，否则撤层那一刻亮度会跳一下 —— 覆盖层比遮罩亮或暗一档，正好落在这一趟要消灭的
/// 那一瞬里。
/// </para>
/// </summary>
public static class CoverArtwork
{
    /// <summary>遮罩里那块垫图的透明度（<c>PlayerPage.xaml</c> 的 <c>CoverArtwork</c>）。</summary>
    public const double Opacity = 0.5;

    /// <summary>
    /// 把一张 BGRA8 图就地烘成遮罩里那个观感：<c>结果 = 图 × Opacity + 底色 × (1 − Opacity)</c>，
    /// alpha 一律写成不透明（覆盖层按 <c>ULW_OPAQUE</c> 铺，alpha 位不参与显示但也不该留脏值）。
    /// </summary>
    /// <param name="pixels">BGRA8 像素，行宽 <paramref name="stride"/>，就地改写。</param>
    /// <param name="width">每行<b>真正的像素宽</b>：行尾可能有填充字节（<paramref name="stride"/> 大于
    /// <c>width × 4</c> 时），那些字节不属于这张图，一个都不该动。</param>
    /// <param name="backdrop">遮罩底下那块底色（<c>PlayerCoverBrush</c>）。</param>
    public static void Bake(byte[] pixels, int width, int height, int stride, ThemeColor backdrop)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || stride < 4) return;

        var alpha = Math.Clamp(Opacity, 0, 1);
        var rest = 1 - alpha;
        var rowBytes = width * 4;

        for (var y = 0; y < height; y++)
        {
            var row = (long)y * stride;
            if (row < 0 || row + rowBytes > pixels.Length) break;

            for (var x = 0; x < rowBytes; x += 4)
            {
                var at = (int)row + x;
                pixels[at] = Mix(pixels[at], backdrop.B, alpha, rest);
                pixels[at + 1] = Mix(pixels[at + 1], backdrop.G, alpha, rest);
                pixels[at + 2] = Mix(pixels[at + 2], backdrop.R, alpha, rest);
                pixels[at + 3] = 0xFF;
            }
        }
    }

    /// <summary>按通道混合并夹回字节 —— 输入是 8 位，中间的浮点只用来算这一道加权。</summary>
    private static byte Mix(byte source, byte backdrop, double alpha, double rest) =>
        (byte)Math.Clamp(Math.Round(source * alpha + backdrop * rest), 0, 255);
}
