using System.Diagnostics;

namespace EmbyNian.Shell.Platform;

/// <summary>
/// Handing something to Windows to open: the log folder, and Emby's own page for an item.
/// <para>
/// Both are <c>ShellExecute</c> underneath — <see cref="ProcessStartInfo.UseShellExecute"/> — and not
/// <c>Windows.System.Launcher</c>, whose async, packaged-identity story buys nothing here. The two methods
/// are kept apart rather than folded into one <c>Open(string)</c> because they are not the same act: a
/// folder is ours and gets created if it is not there yet, while a URL comes from a server and is checked
/// before Windows is asked to resolve it.
/// </para>
/// </summary>
internal interface ISystemLauncher
{
    /// <summary>
    /// Shows <paramref name="path"/> in Explorer, creating the directory first: 打开日志目录 is pressed on
    /// runs that have not written a log file yet, and 「the folder does not exist」 is not the answer anyone
    /// wants from that button.
    /// </summary>
    void OpenFolder(string path);

    /// <summary>
    /// Opens <paramref name="url"/> in the default browser. Throws rather than reporting failure, for the
    /// reason <see cref="IClipboard.SetText"/> does: the caller is a command that already has to say
    /// something when this does not work.
    /// </summary>
    void OpenUrl(string url);
}

/// <inheritdoc cref="ISystemLauncher"/>
internal sealed class SystemLauncher : ISystemLauncher
{
    /// <inheritdoc />
    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Start(path);
    }

    /// <inheritdoc />
    public void OpenUrl(string url)
    {
        // ShellExecute opens whatever the string names, which for a string that is not a URL means a file
        // or an executable. The only caller builds this from the server's own API base, so this is a
        // guard against a bad address in settings.json rather than against the user — but it is one line,
        // and 「open a web page」 is the whole of what this method promises.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"不是可以打开的网址：{url}", nameof(url));

        Start(parsed.AbsoluteUri);
    }

    /// <summary>
    /// Disposed on the spot: the handle is a wait handle on a process this app has no further interest in,
    /// and Explorer or the browser goes on running without it.
    /// </summary>
    private static void Start(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
