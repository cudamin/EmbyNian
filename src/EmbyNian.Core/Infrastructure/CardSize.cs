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
/// 这里的数是默认值。卡片真正建出来用的宽度来自 设置 → 海报宽度，这也是 <see cref="WideFor"/> 存在的理由：
/// 用户拖那根滑杆时两种形状得保持比例，一排 300 的 16:9 挨着一格 240 的海报，看上去就是做错了。
/// </para>
/// <para>
/// 算术这一段放在 Core 而不放在外壳上，和 <see cref="CardStrip"/>、<see cref="ScreenPlacement"/> 是同一个
/// 理由：屏上那一遍只走默认那一档。滑杆停在 170 时 <see cref="WideFor"/> 交回的正好是 <see cref="WideWidth"/>，
/// 也就是说「比例有没有跟着滑杆走」这条规则在一次普通的运行里连碰都碰不到；拖到别的档位才看得见，而截图和自检
/// 都不会去拖它。
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
    /// The width a 16:9 card should be when a 2:3 card is <paramref name="posterWidth"/> wide, in the
    /// same proportion the two defaults have. Returns <see cref="WideWidth"/> exactly for the default
    /// poster width, so leaving the setting alone changes nothing.
    /// </summary>
    public static int WideFor(int posterWidth) =>
        (int)Math.Round(posterWidth * (double)WideWidth / PosterWidth);

    /// <summary>
    /// The same for a cast portrait. Returns <see cref="CastWidth"/> exactly for the default poster
    /// width; the 演职人员 row used to be a literal 124 and stayed 124 whatever the slider said.
    /// </summary>
    public static int CastFor(int posterWidth) =>
        (int)Math.Round(posterWidth * (double)CastWidth / PosterWidth);

    /// <summary>
    /// A card's picture height for a given width and shape: 16:9 for a still, 2:3 for a poster. Rounded,
    /// which reproduces the two numbers the markup used to carry (300 → 169, 170 → 255) and keeps giving
    /// whole pixels for the widths 设置 → 海报宽度 allows.
    /// </summary>
    public static double HeightFor(int width, bool wide) =>
        Math.Round(width * (wide ? 9.0 / 16.0 : 3.0 / 2.0));
}
