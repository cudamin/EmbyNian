using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 起播准备期的意图代次（<see cref="StartIntent"/>）。规则本身是 Core 的纯类；Transport 的接线
/// （封面等待、详情、选集各段之后问 IsCurrent）在 Shell 里，静态断言钉住那四处检查点都真的在。
/// </summary>
internal static class StartIntentTests
{
    internal static void Register()
    {
        Test("起播意图：新意图作废旧意图，最后点选胜出", () =>
        {
            var intents = new StartIntent();
            var first = intents.Begin();
            Assert.True(intents.IsCurrent(first));

            var second = intents.Begin();
            Assert.True(intents.IsCurrent(second));
            Assert.False(intents.IsCurrent(first), "新起播之后，旧的准备中的起播必须让位");
        });

        Test("起播意图：用户停止作废全部，之后的新起播照常", () =>
        {
            var intents = new StartIntent();
            var first = intents.Begin();
            intents.Begin();
            intents.CancelAll();

            Assert.False(intents.IsCurrent(first));
            var after = intents.Begin();
            Assert.True(intents.IsCurrent(after), "停止之后用户再点一次，是新意图，照常有效");
        });

        Test("起播意图：没有并发时判断恒真，取消后旧票永不复活", () =>
        {
            var intents = new StartIntent();
            var only = intents.Begin();
            Assert.True(intents.IsCurrent(only));

            intents.CancelAll();
            Assert.False(intents.IsCurrent(only));
            Assert.False(intents.IsCurrent(only), "同一张票问多少次都是作废，不能时好时坏");
        });

        Test("起播意图：Transport 的四道检查点都真的在", () =>
        {
            var repo = FindRepositoryRoot();
            if (repo is null)
            {
                Skip("起播意图：Transport 的四道检查点都真的在", "找不到仓库根（EmbyNian.sln）");
                return;
            }

            var transport = Path.Combine(repo, "src", "EmbyNian.Shell", "ViewModels", "PlayerViewModel.Transport.cs");
            if (!File.Exists(transport))
            {
                Skip("起播意图：Transport 的四道检查点都真的在", "仓库里没有 PlayerViewModel.Transport.cs");
                return;
            }

            var text = File.ReadAllText(transport);
            Assert.Contains("_startIntent.CancelAll()", text, "停止必须先撤销准备中的起播");
            Assert.Contains("_startIntent.Begin()", text, "每次起播都要领意图票");
            var checks = CountOccurrences(text, "!_startIntent.IsCurrent(intent)");
            Assert.True(checks >= 4, $"封面等待、媒体详情、解析剧集、交给后端四段之后各要一道检查，只有 {checks} 道");
            Assert.Contains("if (!_startIntent.IsCurrent(intent)) return;", text);
        });
    }

    private static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "EmbyNian.sln"))) return directory.FullName;
        }

        return null;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
