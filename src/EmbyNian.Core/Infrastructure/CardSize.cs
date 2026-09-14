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

    /// <summary>
    /// A 16:9 still, as used by 详情页's 更多单集 row and by the library grid's 缩略图 / 列表 shapes.
    /// <para>
    /// 这一档 2026-09-14 起不再管主页：主页那两排（继续观看、接下来看）按用户的话缩小了一档，各自走
    /// <see cref="HomeWideWidth"/>。三处用的是同一个宽度，所以「只缩主页」不能靠动这个常量 —— 动了它，详情页那一排
    /// 和媒体库网格的两种列表样式会一起跟着缩，而那两处用户没说要改。
    /// </para>
    /// </summary>
    public const int WideWidth = 300;

    public const int WideHeight = 169;

    /// <summary>
    /// 主页 继续观看 / 接下来看 那一排的 16:9 剧照（2026-09-14「缩小首页中的媒体库卡片和继续观看卡片，调整其尺寸
    /// 使其更紧凑，同时保持布局对齐、间距协调以及各屏幕尺寸下的响应式显示效果」）。
    /// <para>
    /// 原来是 <see cref="WideWidth"/> 那一档的 300 —— 用户要那两排更紧凑，于是主页单独收一档到 256（宽减约
    /// 15%，<see cref="HeightFor"/> 在这个宽度上是整 144，没有半像素要靠框架去糊）。**为什么另开一个常量而不是
    /// 改 <see cref="WideWidth"/>**：那个数详情页的 更多单集 和媒体库网格的两种列表样式也在用，改它会连着缩到
    /// 用户没提的两处去。
    /// </para>
    /// </summary>
    public const int HomeWideWidth = 256;

    /// <summary>
    /// 主页 媒体库 那一排的宽卡（2026-09-13「参考上图缩小媒体库图标的大小」；2026-09-14 又跟着「缩小首页中的媒体库
    /// 卡片和继续观看卡片」缩了一档）：比 <see cref="HomeWideWidth"/> 窄一档。
    /// 参考图上媒体库的格子约是继续观看卡片的 0.79 倍宽（量的 210 对 267），一直按那个比例落 —— 240 对 300 是
    /// 八成，208 对 256 也是 0.8125，比例没动、两个数一起小。形状不变，还是 16:9 的 Thumb 图，
    /// <see cref="HeightFor"/> 在这个宽度上给 117；一排「进哪座库」的入口格子，不需要剧照那么大。
    /// </summary>
    public const int LibraryWidth = 208;

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
