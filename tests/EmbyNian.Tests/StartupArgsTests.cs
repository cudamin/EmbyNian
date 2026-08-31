using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 命令行上那几个开关怎么读. 整套验证工具都吊在这三个函数上 —— 截图、自检、逐页拍照、换主题拍一张，每一条
/// 命令都先经过它们。读错一个字，看到的就是「开关没生效」，而那时人多半会去改被验的那一头。
/// <para>
/// 偏偏最容易读错的几种写法在屏上一次也碰不到：值没给而下一个词是另一个开关、等号后面空着、斜杠起头的老写法。
/// </para>
/// </summary>
internal static class StartupArgsTests
{
    public static void Register()
    {
        Test("命令行：开关在不在，横线斜杠大小写都不算数", () =>
        {
            string[] args = ["--self-check", "-dump-ui", "/Screen", "2"];

            Assert.True(StartupArgs.Has(args, "--self-check"));
            Assert.True(StartupArgs.Has(args, "--dump-ui"), "一根横线也认");
            Assert.True(StartupArgs.Has(args, "--screen"), "斜杠起头是 Windows 上的老写法");
            Assert.True(StartupArgs.Has(args, "--SELF-CHECK"), "大小写不算数");
            Assert.False(StartupArgs.Has(args, "--play"), "没写就是没写");
            Assert.False(StartupArgs.Has([], "--play"), "一个参数都没有");

            // 认的是整个词，不是前缀 —— 否则 --show-detail 会把 --show-detail-strip 也当成自己。
            Assert.False(StartupArgs.Has(["--show-detail-strip"], "--show-detail"), "前缀不算");
            Assert.False(StartupArgs.Has(["--show"], "--show-detail"), "少一半也不算");
        });

        Test("命令行：带值的两种写法都认", () =>
        {
            Assert.Equal("misty", StartupArgs.Text(["--theme", "misty"], "--theme"), "空格分开");
            Assert.Equal("misty", StartupArgs.Text(["--theme=misty"], "--theme"), "等号连着");
            Assert.Equal("Misty", StartupArgs.Text(["/Theme=Misty"], "--theme"), "开关名不分大小写，值原样交出");
            Assert.Equal(2, StartupArgs.Number(["--screen", "2"], "--screen"));
            Assert.Equal(2, StartupArgs.Number(["--screen=2"], "--screen"));

            // 值里带等号的照原样交出去（第一个等号才是分隔符）：主题 id 里没有，但换个开关就可能有。
            Assert.Equal("a=b", StartupArgs.Text(["--theme=a=b"], "--theme"));
        });

        Test("命令行：值没给不许把下一个开关吃掉", () =>
        {
            // `--theme --dump-ui` 该读成「主题开关没给值」，而不是一套叫 --dump-ui 的主题 —— 后者会让 --dump-ui
            // 静悄悄失效，而报告里一句话都不会响。
            Assert.Null(StartupArgs.Text(["--theme", "--dump-ui"], "--theme"));
            Assert.Null(StartupArgs.Text(["--theme", "/dump-ui"], "--theme"), "斜杠起头的也是开关");
            Assert.True(StartupArgs.Has(["--theme", "--dump-ui"], "--dump-ui"), "而它自己照旧算写了");

            Assert.Null(StartupArgs.Text(["--theme"], "--theme"), "开关在最后一个，后面什么都没有");
            Assert.Null(StartupArgs.Text(["--theme="], "--theme"), "等号后面空着");
            Assert.Null(StartupArgs.Text([], "--theme"), "一个参数都没有");
            Assert.Null(StartupArgs.Text(["--dump-ui"], "--theme"), "开关没出现");
        });

        Test("命令行：不是个数就当没给", () =>
        {
            // 「不是个数」和「没给」在这里合成一件事：两种情形要的都是「照默认来」—— 自检默认开在副屏，别的时候
            // 交给系统。悄悄回落到第 1 块屏才是坏的，那会把自检开到人正在用的那块屏上。
            Assert.Null(StartupArgs.Number(["--screen", "left"], "--screen"));
            Assert.Null(StartupArgs.Number(["--screen="], "--screen"));
            Assert.Null(StartupArgs.Number(["--screen", "--dump-ui"], "--screen"));
            Assert.Null(StartupArgs.Number([], "--screen"));

            // 负数和 0 是读得出来的：0 就是 ScreenPlacement.WhereverWindows，「系统爱摆哪摆哪」。
            Assert.Equal(0, StartupArgs.Number(["--screen", "0"], "--screen"));
            Assert.Equal(ScreenPlacement.NotThePrimary, StartupArgs.Number(["--screen=-1"], "--screen"));
        });

        Test("命令行：同一个开关写两遍，第一遍算", () =>
        {
            // 脚本拼命令行时容易叠出两个（tools\shot.ps1 的 -ExeArgs 后面再补一个），这时候取哪个都算合理，
            // 但必须定死一个，否则同一条命令在两台机器上拍出不同的图。
            Assert.Equal("daylight", StartupArgs.Text(["--theme", "daylight", "--theme", "misty"], "--theme"));
            Assert.Equal(1, StartupArgs.Number(["--screen=1", "--screen=2"], "--screen"));
        });
    }
}
