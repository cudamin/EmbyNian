namespace EmbyNian.Shell.Views;

/// <summary>
/// The sizes a card is drawn at, in device-independent pixels.
/// <para>
/// The width is also the decode width, so a card whose bitmap was decoded for a different size than it
/// is drawn at is either blurry or wasting memory. That used to be enforced by hand — these constants
/// on one side and the same numbers typed again as <c>CardWidth="300" PosterHeight="169"</c> in each
/// DataTemplate on the other — which is a rule a reviewer has to check rather than one the compiler
/// does. Now <see cref="CardItem.CardWidth"/> and <see cref="CardItem.PosterHeight"/> come from the
/// same width the bitmap was decoded for, the markup binds to those, and the two cannot disagree.
/// </para>
/// <para>
/// These are the defaults. The width a card is actually built at comes from 设置 → 海报宽度, which is
/// why <see cref="WideFor"/> exists: the two shapes have to stay in proportion when the user moves that
/// slider, and a 16:9 row of 300px cards next to a grid of 240px posters looks like a mistake.
/// </para>
/// </summary>
internal static class CardSize
{
    /// <summary>A 2:3 poster, as used in every library grid.</summary>
    public const int PosterWidth = 170;

    public const int PosterHeight = 255;

    /// <summary>A 16:9 still, as used by 继续观看 and 接下来看.</summary>
    public const int WideWidth = 300;

    public const int WideHeight = 169;

    /// <summary>
    /// A portrait on 详情页's 演职人员 row. Narrower than a poster because a row of faces reads better
    /// small; 2:3 like a poster, so <see cref="CardItem.HeightFor"/> gives 186 at this width.
    /// </summary>
    public const int CastWidth = 124;

    /// <summary>
    /// The two lines of text and the gap under a card's picture. Added to the picture's height to get
    /// the height a horizontal row has to be given; see <see cref="ViewModels.CardShelf.RowHeight"/>.
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
}
