using EmbyNian.Playback;

namespace EmbyNian.Tests;

/// <summary>
/// 倍速轮盘（2026-09-25 用户令：「集成模式下的播放速度按钮，改为竖置的滚动条滚轮，刻度居中，使用鼠标
/// 滚轮翻动或者鼠标左键长按拖拽」；同日第二令改刻度表：「0.1 到 1 每个刻度 0.1，1 到 20 每个刻度 1」）
/// 的算术：<see cref="SpeedWheel"/> 的位置换算、吸附、一步与刻度的摆位浓淡。
/// <para>
/// 判据全是纯函数，mpv 不在场就能钉死 —— 这正是它住在 Core 的理由。屏上那一半（滚轮与拖拽把位置喂进
/// 来、跨过刻度下发 mpv）由自检的「倍速轮盘」一条看着，不在这里重复。
/// </para>
/// </summary>
internal static class SpeedWheelTests
{
    /// <summary>与键盘微调（clamp 两端）共用的那份刻度表的同款（升序、含两端）。0.1～1.0 十档、
    /// 2.0～20.0 十九档，共 29 档；1.0 在下标 9。</summary>
    private static readonly double[] Choices =
    [
        0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0,
        2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0,
        11.0, 12.0, 13.0, 14.0, 15.0, 16.0, 17.0, 18.0, 19.0, 20.0,
    ];

    /// <summary>中线取 1.0（下标 9）—— 表里新旧两段刻度的交界，最有代表性的一根。</summary>
    private const int Center = 9;

    internal static void Register()
    {
        // 刻度表上的速度正好落在整数位置上 —— 轮盘的「当前值居中」靠的就是这一条。
        TestHarness.Test("倍速轮盘：刻度表上的速度都停在整数位", () =>
        {
            for (var index = 0; index < Choices.Length; index++)
                Assert.Equal((double)index, SpeedWheel.PositionFor(Choices[index], Choices),
                    $"{Choices[index]}× 应停在第 {index} 根刻度上");
        });

        // 键盘微调出的表外速度（±0.1 那一路）不给整数：停在两根之间按「速度差的比例」插值，轮盘不许
        // 对它撒谎。插值走的是浮点除法，判距离不用判全等。注意两段刻度的步距不同（0.1 对 1.0），
        // 同样 0.05 的速度差在两段里离中线的远近完全不同 —— 位置量的是档距，不是速度。
        TestHarness.Test("倍速轮盘：表外速度停在两根刻度之间", () =>
        {
            Assert.True(Math.Abs(SpeedWheel.PositionFor(0.15, Choices) - 0.5) < 1e-9,
                "0.15 → 0.1 与 0.2 的正中");
            Assert.True(Math.Abs(SpeedWheel.PositionFor(0.95, Choices) - 8.5) < 1e-9,
                "0.95 → 0.9 与 1.0 的正中");
            Assert.True(Math.Abs(SpeedWheel.PositionFor(1.05, Choices) - 9.05) < 1e-9,
                "1.05 → 靠 1.0 十分之一档的地方（1.0 与 2.0 之间）");
        });

        // 表外两侧夹回端点：0.05 与 25 不存在，但夹住是保险。
        TestHarness.Test("倍速轮盘：表外两侧夹回端点", () =>
        {
            Assert.Equal(0d, SpeedWheel.PositionFor(0.05, Choices));
            Assert.Equal(0d, SpeedWheel.PositionFor(-1, Choices));
            Assert.Equal((double)Choices.Length - 1, SpeedWheel.PositionFor(25, Choices));
            Assert.Equal((double)Choices.Length - 1, SpeedWheel.PositionFor(99, Choices));
        });

        // 快在上：位置每加一（更快一档），整条带往下走一格 —— 更快的那根从上面转进中线（偏移为负）。
        // 方向写反的轮盘（快在下）会被这条当场逮住。
        TestHarness.Test("倍速轮盘：快在上，位置加大整条带往下走", () =>
        {
            Assert.Equal(0d, SpeedWheel.TickOffset(Center, Center), "中线上的刻度偏移为零");
            Assert.True(SpeedWheel.TickOffset(Center + 1, Center) < 0, "比中线快的那根在上面（偏移为负）");
            Assert.True(SpeedWheel.TickOffset(Center - 1, Center) > 0, "比中线慢的那根在下面（偏移为正）");
            Assert.Equal(-SpeedWheel.TickSpacing, SpeedWheel.TickOffset(Center + 1, Center), "相邻一根正好一格");

            for (var index = 0; index < Choices.Length; index++)
                Assert.Equal((Center - index) * SpeedWheel.TickSpacing,
                    SpeedWheel.TickOffset(index, Center), $"第 {index} 根的摆位");
        });

        // 吸附：四舍五入再夹进表内。两端之外的位置回最近的端点。
        TestHarness.Test("倍速轮盘：吸附取最近刻度并夹在表内", () =>
        {
            Assert.Equal(9, SpeedWheel.Snap(9.2, Choices.Length));
            Assert.Equal(10, SpeedWheel.Snap(9.6, Choices.Length));
            Assert.Equal(0, SpeedWheel.Snap(-2, Choices.Length), "下方夹回最慢一档");
            Assert.Equal(Choices.Length - 1, SpeedWheel.Snap(99, Choices.Length), "上方夹回最快一档");
            Assert.Equal(0, SpeedWheel.Snap(3.2, 0), "空表不给越界下标");
        });

        // 滚轮一步：整数位置走相邻一根；停在两根之间时朝哪边走落哪根的近旁 —— 「四舍五入再 ±1」会
        // 跳过 1.05 头顶上的 2.0，那种走法被这条钉死。
        TestHarness.Test("倍速轮盘：滚轮一步一根刻度，小数位也不吞近旁那根", () =>
        {
            Assert.Equal(10, SpeedWheel.Stepped(9, Choices.Length, +1), "1.0 往上一根是 2.0");
            Assert.Equal(8, SpeedWheel.Stepped(9, Choices.Length, -1), "1.0 往下一根是 0.9");
            Assert.Equal(10, SpeedWheel.Stepped(9.05, Choices.Length, +1), "1.05 往上先落 2.0，不跳过");
            Assert.Equal(9, SpeedWheel.Stepped(9.05, Choices.Length, -1), "1.05 往下先落 1.0");
            Assert.Equal(9, SpeedWheel.Stepped(9, Choices.Length, 0), "没有方向就吸附原地");
        });

        // 两端不越界：已在顶上再往上还是顶上，最慢一档往下同理。
        TestHarness.Test("倍速轮盘：两端不越界", () =>
        {
            var top = Choices.Length - 1;
            Assert.Equal(top, SpeedWheel.Stepped(top, Choices.Length, +1));
            Assert.Equal(top, SpeedWheel.Stepped(top - 0.2, Choices.Length, +1));
            Assert.Equal(0, SpeedWheel.Stepped(0, Choices.Length, -1));
            Assert.Equal(0, SpeedWheel.Stepped(0.2, Choices.Length, -1));
        });

        // 浓淡与缩放：中线最亮略大，往外一路淡到零（淡到零的刻度天然看不见，视口边缘不用再剪），
        // 出了一格缩放立刻回 1 —— 强调只给正在用的那一根。
        TestHarness.Test("倍速轮盘：中线最亮略大，往外淡到没有", () =>
        {
            Assert.Equal(1.0, SpeedWheel.TickOpacity(0));
            Assert.True(SpeedWheel.TickOpacity(1) > SpeedWheel.TickOpacity(2), "越远越淡");
            Assert.Equal(0.0, SpeedWheel.TickOpacity(2.3), "两格半之外已经淡没了");
            Assert.Equal(0.0, SpeedWheel.TickOpacity(9));
            Assert.True(SpeedWheel.TickOpacity(1) > 0, "相邻一根还看得见");

            Assert.True(SpeedWheel.TickScale(0) > 1.2, "中线那一根略大");
            Assert.Equal(1.0, SpeedWheel.TickScale(1), "出了中线立刻回原大");
            Assert.Equal(1.0, SpeedWheel.TickScale(3));
        });
    }
}
