namespace EmbyMpvClient.Mpv;

/// <summary>
/// Splits the user's free-text 「附加参数」 into individual arguments.
/// <para>
/// Deliberately not Windows' full CommandLineToArgv rules: there, a backslash escapes a
/// following quote, which would mangle the very thing that appears most often in this box —
/// a Windows path such as <c>--sub-file-paths=D:\字幕</c>. Here only the double quote is
/// special, so quoting a value with spaces works and backslashes survive untouched.
/// </para>
/// </summary>
public static class CommandLine
{
    public static IReadOnlyList<string> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var started = false;

        foreach (var character in text)
        {
            if (character == '"')
            {
                quoted = !quoted;
                // An empty pair of quotes is still an argument: --title="" means an empty title.
                started = true;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (started) arguments.Add(current.ToString());
                current.Clear();
                started = false;
                continue;
            }

            current.Append(character);
            started = true;
        }

        if (started) arguments.Add(current.ToString());
        return arguments;
    }

    /// <summary>
    /// Renders an argument list the way it would be typed, for the log and the "复制启动命令"
    /// button. Never fed back into a process: <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// does the real quoting, so this only has to be readable.
    /// </summary>
    public static string Describe(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(argument =>
            argument.Length > 0 && !argument.Any(char.IsWhiteSpace) ? argument : $"\"{argument}\""));
}
