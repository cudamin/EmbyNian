using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 「关于」那张卡上的三句读数（<see cref="AboutFacts"/>）。
/// <para>
/// 值得测的是「读不到」那一档 —— 文件不在、路径是空的、版本资源是空的。那一档在一台好机器上永远碰不到，而它
/// 一旦返回一行空白，屏上看着就像那一项还在加载；报告里也就少了一句「你用的是哪一版」。
/// </para>
/// </summary>
internal static class AboutTests
{
    internal static void Register()
    {
        Test("关于：版本号从程序集上来，不是写死的一句", () =>
        {
            Assert.Contains(AppIdentity.Title, AboutFacts.Client);
            Assert.Contains(AppIdentity.Version, AboutFacts.Client);
        });

        Test("关于：构建时间读的是那份文件自己的时间", () =>
        {
            InTempDirectory(directory =>
            {
                var file = Path.Combine(directory, "EmbyNian.exe");
                File.WriteAllText(file, "不是真的 exe，这一条只读文件时间");

                var when = new DateTime(2026, 3, 5, 14, 30, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(file, when);

                // 报的是本地时间：读它的人在本地时区里活着，而 UTC 差几个小时正好够让人怀疑自己拿错了构建。
                Assert.Equal(when.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), AboutFacts.BuiltAt(file));
            });
        });

        Test("关于：文件不在就明说读不到", () =>
        {
            InTempDirectory(directory =>
            {
                Assert.Equal(AboutFacts.Unknown, AboutFacts.BuiltAt(Path.Combine(directory, "没有这个.exe")));
            });

            Assert.Equal(AboutFacts.Unknown, AboutFacts.BuiltAt(null));
            Assert.Equal(AboutFacts.Unknown, AboutFacts.BuiltAt("   "));
        });

        Test("关于：播放内核那一行永远带着文件名", () =>
        {
            InTempDirectory(directory =>
            {
                // 目录里没有那个 dll：这一行仍要说出它找的是谁，否则报告里只剩一句「读不到」，看不出读不到什么。
                var reading = AboutFacts.PlaybackCore(directory);
                Assert.Contains(AboutFacts.CoreLibrary, reading);
                Assert.Contains(AboutFacts.Unknown, reading);
            });

            Assert.Contains(AboutFacts.CoreLibrary, AboutFacts.PlaybackCore(null));
        });
    }

    /// <summary>一个自己的临时目录，跑完删掉 —— 同 <c>DiagnosticsTests</c> 那一份。</summary>
    private static void InTempDirectory(Action<string> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"embynian-about-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            body(directory);
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
