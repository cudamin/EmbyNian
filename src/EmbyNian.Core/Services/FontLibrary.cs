using System.Diagnostics;
using EmbyNian.Diagnostics;
using EmbyNian.Infrastructure;

namespace EmbyNian.Services;

/// <summary>
/// The machine's installed font families, read once per session.
/// <para>
/// A service because the scan is slow and shared: reading the <c>name</c> table out of every file in
/// <c>C:\Windows\Fonts</c> is a few hundred file opens, which is fast warm and noticeably not fast on
/// a cold cache. Whoever asks first pays for it, on a worker thread; everyone after that — the
/// settings page rebuilding itself on each visit, most of all — gets the finished catalogue back
/// immediately.
/// </para>
/// <para>
/// No interface: there is one implementation and nothing substitutes it. A caller that wants a
/// catalogue without touching the machine builds one with <see cref="FontCatalogue.FromFiles"/>,
/// which is how the unit tests do it.
/// </para>
/// </summary>
public sealed class FontLibrary
{
    private const string Category = "app";

    private readonly object _gate = new();

    private Task<FontCatalogue>? _scan;

    /// <summary>
    /// The catalogue, scanning on first call. The same task every time, so two callers race to await
    /// rather than to scan, and a caller that arrives after the scan finished continues without ever
    /// yielding.
    /// </summary>
    public Task<FontCatalogue> LoadAsync()
    {
        lock (_gate)
        {
            return _scan ??= Task.Run(Scan);
        }
    }

    /// <summary>What has been read so far, or nothing — for a caller with no thread to spare.</summary>
    public FontCatalogue Ready => _scan is { IsCompletedSuccessfully: true } scan ? scan.Result : FontCatalogue.Empty;

    private static FontCatalogue Scan()
    {
        var clock = Stopwatch.StartNew();
        var catalogue = FontCatalogue.ScanInstalled();

        Log.Info(Category, $"字体扫描：{catalogue.Families.Count} 个字体族，来自 {catalogue.FileCount} 个字体文件，用了 {clock.ElapsedMilliseconds} 毫秒");
        return catalogue;
    }
}
