namespace EmbyNian.Mpv;

/// <summary>
/// Writes an mpv argument list out as text.
/// <para>
/// This used to be the other way round as well: a <c>Split</c> that parsed the user's free-text
/// 「附加参数」 back into arguments. That setting is gone as of v5 — 「附加参数貌似没什么用，删除」 — and
/// with it the only thing that ever had to turn a typed command line into a list.
/// </para>
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// Renders an argument list the way it would be typed, for the log and the "复制启动命令"
    /// button. Never fed back into a process: <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// does the real quoting, so this only has to be readable.
    /// </summary>
    public static string Describe(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(argument =>
            argument.Length > 0 && !argument.Any(char.IsWhiteSpace) ? argument : $"\"{argument}\""));
}
