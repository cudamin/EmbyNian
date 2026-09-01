using System.Diagnostics;

namespace EmbyNian.Tests;

/// <summary>
/// A hand-rolled runner. The NuGet cache on this machine was empty when the suite started, so xUnit was
/// not an option; it has stayed this way because the project still needs no package references at all —
/// <c>dotnet run</c> is the runner, and the SDK pinned in <c>global.json</c> is the only requirement.
/// </summary>
internal static class TestHarness
{
    private static readonly List<(string Name, Action Run)> Cases = [];
    private static readonly List<string> Skips = [];

    public static void Test(string name, Action run) => Cases.Add((name, run));

    /// <summary>Records a skipped case instead of silently passing when a fixture is absent.</summary>
    public static void Skip(string name, string reason) => Skips.Add($"{name} —— {reason}");

    public static int Run()
    {
        var failures = new List<(string Name, Exception Error)>();
        var stopwatch = Stopwatch.StartNew();

        foreach (var (name, run) in Cases)
        {
            try
            {
                run();
                Console.WriteLine($"  通过  {name}");
            }
            catch (Exception error)
            {
                failures.Add((name, error));
                Console.WriteLine($"  失败  {name}");
            }
        }

        foreach (var skip in Skips) Console.WriteLine($"  跳过  {skip}");

        Console.WriteLine();
        foreach (var (name, error) in failures)
        {
            Console.WriteLine($"── {name}");
            Console.WriteLine(error is AssertionException ? error.Message : error.ToString());
            Console.WriteLine();
        }

        Console.WriteLine($"共 {Cases.Count} 项，通过 {Cases.Count - failures.Count}，失败 {failures.Count}，跳过 {Skips.Count}，用时 {stopwatch.ElapsedMilliseconds} ms");
        return failures.Count == 0 ? 0 : 1;
    }
}

internal sealed class AssertionException(string message) : Exception(message);

internal static class Assert
{
    public static void True(bool condition, string message = "")
    {
        if (!condition) throw new AssertionException($"期望为真：{message}");
    }

    public static void False(bool condition, string message = "") => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message = "")
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual)) return;
        throw new AssertionException($"期望 <{Describe(expected)}>，实际 <{Describe(actual)}>{(message.Length > 0 ? $"（{message}）" : "")}");
    }

    public static void Null(object? value, string message = "")
    {
        if (value is not null) throw new AssertionException($"期望为 null，实际 <{Describe(value)}> {message}");
    }

    public static void NotNull(object? value, string message = "")
    {
        if (value is null) throw new AssertionException($"期望非 null：{message}");
    }

    public static void Contains(string expectedSubstring, string actual, string message = "")
    {
        if (actual.Contains(expectedSubstring, StringComparison.Ordinal)) return;
        throw new AssertionException($"期望包含 <{expectedSubstring}>，实际 <{Truncate(actual)}> {message}");
    }

    public static void DoesNotContain(string unexpected, string actual, string message = "")
    {
        if (!actual.Contains(unexpected, StringComparison.Ordinal)) return;
        throw new AssertionException($"期望不包含 <{unexpected}>，实际 <{Truncate(actual)}> {message}");
    }

    public static void Throws<TException>(Action action, string message = "") where TException : Exception =>
        Catch<TException>(action, message);

    /// <summary>
    /// 同 <see cref="Throws{TException}"/>，但把抓到的那个异常交回来 —— 有些断言要看它里面那句话，而那句话正是
    /// 用户唯一看得见的东西（见 <c>Failure.Describe</c>）。
    /// </summary>
    public static TException Catch<TException>(Action action, string message = "") where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception error)
        {
            throw new AssertionException($"期望抛出 {typeof(TException).Name}，实际抛出 {error.GetType().Name} {message}");
        }

        throw new AssertionException($"期望抛出 {typeof(TException).Name}，但没有抛出 {message}");
    }

    private static string Describe<T>(T value) => value switch
    {
        null => "null",
        string text => Truncate(text),
        _ => value.ToString() ?? "?"
    };

    private static string Truncate(string text) =>
        text.Length <= 160 ? text : text[..160] + $"…（共 {text.Length} 字）";
}
