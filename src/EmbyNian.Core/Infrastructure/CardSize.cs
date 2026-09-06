namespace EmbyNian.Infrastructure;

/// <summary>
/// 一张卡片画多大，以及两种形状怎么跟着走，单位是设备独立像素。
/// <para>
/// 宽度同时就是解码宽度，所以「画出来的宽」和「解码时用的宽」一旦分岔，卡片要么模糊要么白占内存。这件事从前
/// 是靠手工对齐的 —— 一边是这几个常量，另一边是每个 DataTemplate 里又敲一遍的
/// <c>CardWidth="300" PosterHeight="169"</c> —— 那是一条要靠人复查的规矩，不是编译器管得住的。现在
/// <c>CardItem.CardWidth</c> 和 <c>CardItem.PosterHeight</c> 都从解码用的那个宽度算出来，标记绑的是它们，
/// 两边于是没法各说各话。
/// </para>
/// <para>
/// 屏上永远只走这一档。从前 设置 → 海报宽度 那一行能把这些数整体拖走，<c>WideFor</c>/<c>CastFor</c> 两个换算
/// 就是为「两种形状保持比例」存在的 —— 那一行 2026-09-05 按用户的话删掉了（「删掉设置中的海报宽度」），这几个
/// 常量重新是唯一的尺寸来源。
/// </para>
/// <para>
/// 算术这一段放在 Core 而不放在外壳上，和 <see cref="CardStrip"/>、<see cref="ScreenPlacement"/> 是同一个
/// 理由：单测够得着，屏上那一遍也只走默认那一档。
/// </para>
/// </summary>
public static class CardSize
{
    /// <summary>A 2:3 poster, as used in every library grid.</summary>
    public const int PosterWidth = 170;

    public const int PosterHeight = 255;

    /// <summary>A 16:9 still, as used by 继续观看 and 接下来看.</summary>
    public const int WideWidth = 300;

    public const int WideHeight = 169;

    /// <summary>
    /// A portrait on 详情页's 演职人员 row. Narrower than a poster because a row of faces reads better
    /// small; 2:3 like a poster, so <see cref="HeightFor"/> gives 186 at this width.
    /// </summary>
    public const int CastWidth = 124;

    /// <summary>
    /// The two lines of text and the gap under a card's picture. Added to the picture's height to get
    /// the height a horizontal row has to be given; see <c>CardShelf.RowHeight</c>.
    /// </summary>
    public const int Chrome = 59;

    /// <summary>
    /// A card's picture height for a given width and shape: 16:9 for a still, 2:3 for a poster. Rounded,
    /// which reproduces the two numbers the markup used to carry (300 → 169, 170 → 255) and gives whole
    /// pixels for every width the constants above can name.
    /// </summary>
    public static double HeightFor(int width, bool wide) =>
        Math.Round(width * (wide ? 9.0 / 16.0 : 3.0 / 2.0));
}
