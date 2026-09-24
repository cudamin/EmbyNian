using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 绊线：CLAUDE.md「本项目自立规则之处」那三条（WUI2010 压制、<c>x:Name</c> 当句柄、字号刻度）本来全靠「记得去读」，
/// 唯一会盯它们的分析器规则（WUI2020）在这棵树上又不响（原因没人查明）。这里把它们变成一比就红的东西。
///
/// 它盯的不是代码质量，是**那几处约定今天各有多少处** —— 基线文件 <c>tests/EmbyNian.Tests/Agreements.txt</c>
/// 就是那份快照。每个小节的松紧方向不一样，因为风险的方向不一样：
///
/// * <c>font-size</c> / <c>color</c> —— **只减不增**。多一处内联字号就是字号刻度那条又漏了一处；颜色这一条替代的
///   是 2026-09-05 删掉的 daylight 主题 —— 那是当时唯一逮得住「硬编码外壳颜色、系统标题栏黑底画黑」的机制，删掉之后
///   没有任何东西在查，这里补上。
/// * <c>bind</c> —— **只许减**。WUI2010 被压低的那份论证覆盖的是当时点名的那几条嵌套 <c>x:Bind</c> 路径，多一条就得
///   回头读 <c>EmbyNian.Shell.csproj</c> 里 <c>NoWarn</c> 旁边那段。
/// * <c>handle</c> —— **只许增**。句柄（<c>x:Name</c> 或显式 <c>AutomationProperties.AutomationId</c>）一旦改名或
///   消失，<c>winapp ui</c> 与 UIA 脚本就找不到它，而没有任何编译器会说话。
/// * <c>switch</c> / <c>analyzer</c> —— **必须一字不差**。前者是文档与代码之间最容易漂移的一张表（2026-09-21 实测：
///   「全部开关」漏了四个、又留着一个已经删掉的）；后者那份副本一旦不在，闸门 1 会照样报「0 警告 0 错误」，而其实一条
///   WUI 规则都没跑 —— 和已经修过的 PRI 事故同一类。
///
/// 基线要更新时：跑一遍测试（它每次都会把当前快照写到 <c>artifacts/code-review/agreements-actual.txt</c>），逐行确认过
/// 再拷进基线文件 —— 不是反过来先改基线。
/// </summary>
internal static class AgreementsTests
{
    private const string BaselineRelativePath = "tests/EmbyNian.Tests/Agreements.txt";

    private const string CandidateRelativePath = "artifacts/code-review/agreements-actual.txt";

    private static readonly Regex InlineFontSize = new("FontSize=\"[0-9]", RegexOptions.CultureInvariant);

    private static readonly Regex ColorLiteral = new("#[0-9A-Fa-f]{6,8}", RegexOptions.CultureInvariant);

    private static readonly Regex NestedBindPath =
        new(@"x:Bind\s+([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*){2,})", RegexOptions.CultureInvariant);

    private static readonly Regex NamedElement = new("x:Name=\"([^\"]+)\"", RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitAutomationId =
        new("AutomationProperties\\.AutomationId=\"([^\"]+)\"", RegexOptions.CultureInvariant);

    private static readonly Regex SwitchLiteral = new("\"--[a-z-]{3,}\"", RegexOptions.CultureInvariant);

    /// <summary>当前快照，一行一处、已排序；第一次问的时候现算一次。</summary>
    private static string[]? _snapshot;

    private static string[] Snapshot() => _snapshot ??= BuildSnapshot();

    public static void Register()
    {
        Test("约定：内联字号只减不增（字号刻度那条，见 CLAUDE.md「本项目自立规则之处」）", () =>
        {
            var grown = Growth("font-size");
            Assert.True(grown.Count == 0,
                $"新写了 {grown.Count} 处内联数字字号：{Show(grown)}。先用 Theme/Styles.xaml 里的 Eg*Style（要新档就加进那七级），" +
                $"确认过再更新基线 {BaselineRelativePath}。");
        });

        Test("约定：Shell XAML 里的硬编码颜色只减不增（daylight 主题删掉后没人查的那一类）", () =>
        {
            var grown = Growth("color");
            Assert.True(grown.Count == 0,
                $"新写了 {grown.Count} 处硬编码颜色：{Show(grown)}。主题色走 src/EmbyNian.Shell/Theme/Palette.xaml 的角色键，" +
                $"否则换主题时它会留在原地。");
        });

        Test("约定：嵌套 x:Bind 路径不许新增（WUI2010 压制的前提）", () =>
        {
            var added = Added("bind");
            Assert.True(added.Count == 0,
                $"新增了 {added.Count} 条三段的 x:Bind 路径：{Show(added)}。WUI2010 能在 Shell 的 csproj 里关掉，靠的是" +
                $"「这些路径的中间量要么非空、要么只可能为 null 一次」—— 这条新路径满足吗？读 NoWarn 旁边那段再更新基线。");
        });

        Test("约定：UIA 句柄不许消失（x:Name 算句柄的那条规则）", () =>
        {
            var missing = Missing("handle");
            Assert.True(missing.Count == 0,
                $"少了 {missing.Count} 个句柄，改名也算少：{Show(missing)}。它们没有任何编译器盯着，改名的代价是 " +
                $"winapp ui 与 UIA 脚本从此找不到这个控件；确认是有意替换之后再更新基线。");
        });

        Test("约定：命令行开关变化要复核并同步开发文档", () =>
        {
            var added = Added("switch");
            var missing = Missing("switch");
            Assert.True(added.Count == 0 && missing.Count == 0,
                $"开关表动了：新增 {Show(added)}；消失 {Show(missing)}。docs/开发与验证.md 的「命令行开关」一节要跟着改，" +
                $"再更新基线。这条只比源码与基线，不代替阅读文档里的用法与组合限制。");
        });

        Test("约定：tools/analyzers 那份副本还在、没被换过（缺了它闸门 1 会静默失明）", () =>
        {
            var gone = Keys("analyzer").Except(KeysFrom(Snapshot(), "analyzer"), StringComparer.Ordinal)
                .Concat(KeysFrom(Snapshot(), "analyzer").Except(Keys("analyzer"), StringComparer.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(gone.Count == 0,
                $"分析器副本变了或少了：{Show(gone)}。它是技能里那份的手工拷贝（Directory.Build.props 用 Condition=Exists 引它，" +
                $"所以少了它构建照样通过、闸门 1 照样报 0 警告）—— 跑 tools/refresh-analyzers.ps1 刷新，确认后再更新基线。");
        });
    }

    // ── 快照 ────────────────────────────────────────────────────────────────────────────────────────

    private static string[] BuildSnapshot()
    {
        var root = RepositoryRoot();
        var shell = Path.Combine(root, "src", "EmbyNian.Shell");
        var entries = new List<string>();

        foreach (var file in Directory.EnumerateFiles(shell, "*.xaml", SearchOption.AllDirectories))
        {
            var name = Relative(shell, file);
            if (UnderBuildOutput(name)) continue;

            var text = File.ReadAllText(file);

            var fontSize = InlineFontSize.Matches(text).Count;
            if (fontSize > 0) entries.Add($"font-size  {name}  {fontSize}");

            var colors = ColorLiteral.Matches(text).Count;
            if (colors > 0) entries.Add($"color  {name}  {colors}");

            foreach (Match match in NestedBindPath.Matches(text))
                entries.Add($"bind  {name}  {match.Groups[1].Value}");

            foreach (Match match in NamedElement.Matches(text))
                entries.Add($"handle  {name}  {match.Groups[1].Value}");

            foreach (Match match in ExplicitAutomationId.Matches(text))
                entries.Add($"handle  {name}  {match.Groups[1].Value}");
        }

        foreach (var name in SwitchNames(Path.Combine(shell, "Program.cs"))
            .Concat(SwitchNames(Path.Combine(root, "src", "EmbyNian.Core", "Infrastructure", "StartupArgs.cs"))))
            entries.Add($"switch  {name}");

        var analyzerDir = Path.Combine(root, "tools", "analyzers");
        if (Directory.Exists(analyzerDir))
            foreach (var file in Directory.EnumerateFiles(analyzerDir).OrderBy(f => f, StringComparer.Ordinal))
                entries.Add($"analyzer  {Path.GetFileName(file)}  {Hash(file)}");

        var snapshot = entries.Distinct(StringComparer.Ordinal).OrderBy(line => line, StringComparer.Ordinal).ToArray();

        // 每次现写一份当前快照：基线要更新时以它为准，而不是把基线改成「现在的样子」再声称通过。
        var candidatePath = Path.Combine(root, CandidateRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(candidatePath)!);
        File.WriteAllText(candidatePath, Candidate(snapshot), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return snapshot;
    }

    /// <summary>开关来自 Program 与 Core 的 StartupArgs；自检白名单在后者，须一并复核。</summary>
    private static IEnumerable<string> SwitchNames(string programFile)
    {
        if (!File.Exists(programFile)) return [];
        return SwitchLiteral.Matches(File.ReadAllText(programFile))
            .Cast<Match>()
            .Select(match => match.Value.Trim('"'))
            .Distinct(StringComparer.Ordinal);
    }

    private static string Candidate(IEnumerable<string> lines) =>
        "# EmbyNian 约定基线 —— 见 CLAUDE.md「本项目自立规则之处」，比对逻辑在 tests/EmbyNian.Tests/AgreementsTests.cs。\n" +
        "# 这是「今天有几处」的快照，不是目标值。方向各不一样：font-size/color 只减不增、bind 只许减、\n" +
        "# handle 只许增、switch/analyzer 一字不差。要改它，先读那条规则，再逐行确认。\n" +
        string.Join('\n', lines) + "\n";

    // ── 比对 ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>只减不增的两个小节：返回变多的那些文件（不在基线里的文件按 0 算，所以新文件也算变多）。</summary>
    private static List<string> Growth(string section)
    {
        var baseline = CountsFrom(BaselineLines(), section);
        var grown = new List<string>();
        foreach (var (file, count) in CountsFrom(Snapshot(), section))
        {
            var before = baseline.TryGetValue(file, out var value) ? value : 0;
            if (count > before) grown.Add($"{file} {before}→{count}");
        }
        return grown.OrderBy(line => line, StringComparer.Ordinal).ToList();
    }

    private static List<string> Added(string section) =>
        KeysFrom(Snapshot(), section).Except(KeysFrom(BaselineLines(), section), StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal).ToList();

    private static List<string> Missing(string section) =>
        KeysFrom(BaselineLines(), section).Except(KeysFrom(Snapshot(), section), StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal).ToList();

    private static HashSet<string> Keys(string section) => KeysFrom(BaselineLines(), section);

    private static Dictionary<string, int> CountsFrom(IEnumerable<string> lines, string section)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in Section(lines, section))
        {
            var parts = Rest(line).Split("  ", StringSplitOptions.None);
            if (parts.Length == 2 && int.TryParse(parts[1], out var value)) counts[parts[0]] = value;
        }
        return counts;
    }

    private static HashSet<string> KeysFrom(IEnumerable<string> lines, string section) =>
        Section(lines, section).Select(Rest).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> Section(IEnumerable<string> lines, string section) =>
        lines.Where(line => line.StartsWith(section + "  ", StringComparison.Ordinal));

    private static string Show(IReadOnlyCollection<string> items) =>
        items.Count == 0
            ? "（无）"
            : string.Join("；", items.Take(6)) + (items.Count > 6 ? $" …… 共 {items.Count} 处" : "");

    /// <summary>一行是「小节 + 两个空格 + 余下全是内容」；内容里的两空格只在计数小节有含义。</summary>
    private static string Rest(string line)
    {
        var separator = line.IndexOf("  ", StringComparison.Ordinal);
        return separator < 0 ? "" : line[(separator + 2)..];
    }

    private static string[] BaselineLines()
    {
        var path = Path.Combine(RepositoryRoot(), BaselineRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            // 先把当前快照落一份再报「没有基线」——否则那条消息指着一个还没写出来的文件。
            _ = Snapshot();
            throw new AssertionException(
                $"没有约定基线 {BaselineRelativePath}。当前快照已经写到 {CandidateRelativePath}，逐行确认过再拷过来。");
        }
        return File.ReadAllLines(path);
    }

    // ── 杂项 ────────────────────────────────────────────────────────────────────────────────────────

    private static bool UnderBuildOutput(string relativePath) =>
        relativePath.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase);

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyNian.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
