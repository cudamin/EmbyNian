namespace EmbyNian.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

public readonly record struct LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message, string? Detail)
{
    public override string ToString() =>
        $"{Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant(),-5}] {Category}: {Message}"
        + (Detail is null ? "" : Environment.NewLine + Detail);
}

public interface ILogSink
{
    void Write(in LogEntry entry);
}

/// <summary>No-op sink; the default so that unit tests never touch the disk.</summary>
public sealed class NullLogSink : ILogSink
{
    public static readonly NullLogSink Instance = new();

    public void Write(in LogEntry entry)
    {
    }
}

/// <summary>
/// Keeps the most recent entries in memory so the UI can show a diagnostics panel
/// without re-reading the log file.
/// </summary>
public sealed class RingBufferLogSink : ILogSink
{
    private readonly LogEntry[] _buffer;
    private readonly object _gate = new();
    private int _next;
    private int _count;

    public RingBufferLogSink(int capacity = 500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _buffer = new LogEntry[capacity];
    }

    /// <summary>How many entries this sink keeps; the oldest is dropped past that.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Raised for each entry as it arrives, so a log view can follow along instead of re-reading
    /// <see cref="Snapshot"/> on a timer.
    /// <para>
    /// Raised on whichever thread wrote the entry — a background download, an mpv IPC read — and
    /// deliberately outside the lock: a handler is arbitrary code, and running it under the gate would
    /// put every other thread's logging behind it. A subscriber that touches UI has to marshal, and one
    /// that throws is swallowed here, because logging failing loudly is worse than the log line being
    /// lost.
    /// </para>
    /// <para>
    /// Each subscriber is called in its own <c>try</c>, so a throwing one loses only its own notification.
    /// Invoking the multicast delegate as a whole would have been cheaper, but a throw part-way through
    /// abandons every subscriber after it — which is not 「the log line being lost」 but one page's bug
    /// silently switching off an unrelated one's live view.
    /// </para>
    /// </summary>
    public event Action<LogEntry>? Written;

    public void Write(in LogEntry entry)
    {
        lock (_gate)
        {
            _buffer[_next] = entry;
            _next = (_next + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }

        if (Written is not { } handler) return;
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((Action<LogEntry>)subscriber)(entry);
            }
            catch
            {
                // Swallowed on purpose, and not logged: Log.Write is what got us here, so reporting this
                // through the same sink would recurse.
            }
        }
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            var result = new LogEntry[_count];
            var start = (_next - _count + _buffer.Length) % _buffer.Length;
            for (var i = 0; i < _count; i++) result[i] = _buffer[(start + i) % _buffer.Length];
            return result;
        }
    }
}

/// <summary>
/// Appends to a per-day file and prunes files older than <paramref name="retainDays"/>.
/// <para>
/// The handle stays open. Every line used to be a <see cref="File.AppendAllText"/> of its own — open the
/// file, seek to the end, write, close — tens of thousands of times a day, on whichever thread happened
/// to log, including the UI thread during a scroll. Holding one handle costs one handle and buys all of
/// that back. <see cref="StreamWriter.AutoFlush"/> stays on, because the tail of the log is exactly what
/// must survive when the thing being debugged takes the process down with it; the file is opened
/// shareable so tail viewers, and the next run's prune, are not locked out.
/// </para>
/// </summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly LogLevel _minimum;
    private DateOnly _currentDay;
    private StreamWriter? _writer;

    public FileLogSink(string directory, LogLevel minimum = LogLevel.Debug, int retainDays = 14)
    {
        _directory = directory;
        _minimum = minimum;
        Directory.CreateDirectory(directory);
        Prune(retainDays);
    }

    public void Write(in LogEntry entry)
    {
        if (entry.Level < _minimum) return;
        var line = entry.ToString();
        lock (_gate)
        {
            try
            {
                var day = DateOnly.FromDateTime(entry.Timestamp.LocalDateTime);
                if (_writer is null || day != _currentDay) Open(day);

                _writer!.WriteLine(line);
            }
            catch (IOException)
            {
                // Logging must never break the app. Drop the handle so the next line opens a fresh one:
                // the usual causes — the file deleted underneath us, a full disk — are ones a reopen
                // recovers from, and a writer that has already faulted never writes again.
                Close();
            }
            catch (UnauthorizedAccessException)
            {
                Close();
            }
        }
    }

    /// <summary>
    /// Closes the file. Nothing in the app does — the sink lives as long as the process and
    /// <see cref="StreamWriter.AutoFlush"/> means there is nothing buffered to lose — but a handle owner
    /// that cannot be closed is a handle leak in anything with a shorter life than a process, tests
    /// included.
    /// </summary>
    public void Dispose()
    {
        lock (_gate) Close();
    }

    /// <summary>
    /// Switches to one day's file, closing the previous day's first so a run that crosses midnight does
    /// not hold yesterday's handle for the rest of its life.
    /// </summary>
    private void Open(DateOnly day)
    {
        Close();
        _currentDay = day;
        var path = Path.Combine(_directory, $"app-{day:yyyyMMdd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    private void Close()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (IOException)
        {
            // A failed flush on the way out is not worth taking anything down for.
        }
        finally
        {
            _writer = null;
        }
    }

    private void Prune(int retainDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-retainDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class CompositeLogSink(params ILogSink[] sinks) : ILogSink
{
    public void Write(in LogEntry entry)
    {
        foreach (var sink in sinks) sink.Write(entry);
    }
}

/// <summary>
/// Ambient logger. Deliberately a static facade rather than an injected dependency:
/// this app is a single process with one log, and threading it through every
/// constructor would be ceremony without payoff.
/// </summary>
public static class Log
{
    private static ILogSink _sink = NullLogSink.Instance;

    public static void UseSink(ILogSink sink) => _sink = sink;

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message, null);

    public static void Info(string category, string message) => Write(LogLevel.Info, category, message, null);

    public static void Warn(string category, string message, Exception? error = null) =>
        Write(LogLevel.Warn, category, message, Describe(error));

    public static void Error(string category, string message, Exception? error = null) =>
        Write(LogLevel.Error, category, message, Describe(error));

    private static void Write(LogLevel level, string category, string message, string? detail) =>
        _sink.Write(new LogEntry(DateTimeOffset.Now, level, category, message, detail));

    private static string? Describe(Exception? error) => error?.ToString();
}
