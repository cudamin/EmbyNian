using EmbyNian.Emby;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 制作组（release group）的认法与两处拼装（<see cref="ReleaseGroup"/>）。
/// <para>
/// 用户令（2026-09-24）：进度条中间的画质读数与独占模式左上角的标题，尾部都要缀上「文件名最后一个
/// - 后面的那一段」。这条规则错得起但错了很烦：组名认错一行、分隔符孤零零挂在头上、候选回退后标题
/// 报着别人的组 —— 都在屏上看得见，所以钉在这里。
/// </para>
/// </summary>
internal static class ReleaseGroupTests
{
    public static void Register()
    {
        // 用户给的例子，原样钉住：制作组是文件名最后一个「-」后面的那一段。
        Test("制作组：用户给的那份文件名", () =>
        {
            Assert.Equal("Studio GreenTea", ReleaseGroup.FromFileName(
                @"\\NAS\media\再见菈菈\Season 1\再见菈菈 S01E12 1080p.AAC-Studio GreenTea.mp4"));
            Assert.Equal("再见菈菈 S01E12 再见菈菈 - Studio GreenTea", ReleaseGroup.DecorateTitle(
                "再见菈菈 S01E12 再见菈菈",
                @"\\NAS\media\再见菈菈\Season 1\再见菈菈 S01E12 1080p.AAC-Studio GreenTea.mp4"));
        });

        // 发布组记号自己也带连字符：取最后一个「-」，不是第一个。
        Test("制作组：组名前还有记号用的连字符", () =>
        {
            Assert.Equal("GRP", ReleaseGroup.FromFileName(@"D:\media\Show.S01E01.1080p.WEB-DL.H.264-GRP.mkv"));
            Assert.Equal("GRP 2", ReleaseGroup.FromFileName(@"D:\media\Show.S01E01.WEB-DL-GRP 2.mp4"));
        });

        // 组名前后带的空白是排版，不是名字的一部分。
        Test("制作组：首尾空白去掉", () =>
        {
            Assert.Equal("GRP", ReleaseGroup.FromFileName(@"D:\media\Show-  GRP  .mkv"));
        });

        // 没有扩展名的文件名里，组名前面的点不该把后半截当扩展名吃掉。
        // 已知取舍：组名自带的点号尾巴（GRP.v2）与扩展名（.mp4）在形态上分不开，统一按扩展名删 ——
        // 用户给的例子（…-Studio GreenTea.mp4）必须删扩展名才得到干净的组名，点号组名极罕见。
        Test("制作组：无扩展名时点不误删", () =>
        {
            Assert.Equal("GRP", ReleaseGroup.FromFileName(@"D:\media\Some.File-GRP"));
            Assert.Equal("GRP", ReleaseGroup.FromFileName(@"D:\media\Show-GRP.v2"));
        });

        // 认不出的情形一律给空串，两处读数各回各的原样 —— 不产孤零零的分隔符。
        Test("制作组：认不出就给空，标题不接尾巴", () =>
        {
            Assert.Equal("", ReleaseGroup.FromFileName(null));
            Assert.Equal("", ReleaseGroup.FromFileName(""));
            Assert.Equal("", ReleaseGroup.FromFileName(@"D:\media\Show.S01E01.1080p.mkv"));
            Assert.Equal("", ReleaseGroup.FromFileName(@"D:\media\Show-"));
            Assert.Equal("", ReleaseGroup.FromFileName(@"D:\media\Show-   .mkv"));

            Assert.Equal("再见菈菈 S01E12 再见菈菈", ReleaseGroup.DecorateTitle("再见菈菈 S01E12 再见菈菈", null));
            Assert.Equal("再见菈菈 S01E12 再见菈菈", ReleaseGroup.DecorateTitle("再见菈菈 S01E12 再见菈菈", @"D:\media\Show.mkv"));
            Assert.Equal("GRP", ReleaseGroup.DecorateTitle(null, @"D:\media\Show-GRP.mkv"));
            Assert.Equal("", ReleaseGroup.DecorateTitle("", @"D:\media\Show.mkv"));
        });

        // 画质读数的原料（ToQualityLabel）本身不带组名 —— 追加只发生在 Shell 的 PlayingSourceLabel 一侧，
        // 原料要是也带，屏上就会叠出两次。这条钉住原料与组名的分工。
        Test("制作组：画质读数原料不带组名，追加不叠两次", () =>
        {
            var source = new MediaSource
            {
                Path = @"\\NAS\media\再见菈菈\Season 1\再见菈菈 S01E12 1080p.AAC-Studio GreenTea.mp4",
                Container = "mp4",
                Size = 261_800_000
            };

            var label = source.ToQualityLabel();
            Assert.True(label.Length > 0, "原料读数不为空");
            Assert.False(label.Contains("Studio GreenTea"), $"原料不该带组名：{label}");
            Assert.Equal("Studio GreenTea", ReleaseGroup.FromFileName(source.Path));
        });
    }
}
