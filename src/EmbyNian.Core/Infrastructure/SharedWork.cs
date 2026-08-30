using System.Collections.Concurrent;

namespace EmbyNian.Infrastructure;

/// <summary>
/// One running task per key, shared by everyone who asks for the same thing while it is in flight, and
/// owned by none of them.
/// <para>
/// The poster store collapses duplicate downloads this way, and the reason it is a class of its own is
/// the bug that made it worth stating once: the shared task used to be started with the *first* caller's
/// cancellation token. A card that scrolled out of view therefore cancelled the download the card beside
/// it was still waiting on; that one got an empty answer back, read it as 「服务器没有这张图」 and
/// remembered it — a permanently blank cover until the page was left and reopened.
/// </para>
/// <para>
/// So the work here belongs to no caller. Each waiter may give up on its own (its
/// <c>waiter</c> token, which cancels the wait and nothing else); the download runs to completion, its
/// bytes reach the disk cache, and everyone else who was waiting is served.
/// </para>
/// </summary>
public sealed class SharedWork<TResult>
{
    private readonly ConcurrentDictionary<string, Lazy<Task<TResult>>> _running = new(StringComparer.Ordinal);

    /// <summary>How many keys are in flight right now. Diagnostics, and what the tests count.</summary>
    public int Running => _running.Count;

    /// <summary>
    /// Runs <paramref name="work"/> for <paramref name="key"/>, or joins the run already under way.
    /// Throws <see cref="OperationCanceledException"/> if <paramref name="waiter"/> is cancelled first.
    /// </summary>
    public Task<TResult> RunAsync(string key, Func<Task<TResult>> work, CancellationToken waiter)
    {
        // Lazy rather than GetOrAdd's factory overload: that overload is allowed to run a losing
        // factory as well, which here would start a second download that nothing ever waits on and
        // nothing ever removes from the table.
        var mine = new Lazy<Task<TResult>>(work, LazyThreadSafetyMode.ExecutionAndPublication);
        var entry = _running.GetOrAdd(key, mine);

        Task<TResult> shared;
        try
        {
            shared = entry.Value;
        }
        catch
        {
            // A synchronous throw is cached by the Lazy, so the entry has to go: otherwise every later
            // caller for this key is handed the same failure for the lifetime of the process.
            Forget(key, entry);
            throw;
        }

        // Only the caller whose entry won arranges the removal, and only now that the table holds it: a
        // task that finished before the insert would otherwise remove nothing and stay there forever,
        // answering every later caller with the bytes of a poster that has since changed.
        if (ReferenceEquals(entry, mine))
        {
            _ = shared.ContinueWith(
                _ => Forget(key, entry),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        // WaitAsync, not a plain await: the caller's token releases the caller, not the work.
        return shared.WaitAsync(waiter);
    }

    /// <summary>
    /// Removes the entry only if it is still this one. The pair overload is the atomic form — a plain
    /// <c>TryRemove(key)</c> could drop a newer run that took the same key in the meantime.
    /// </summary>
    private void Forget(string key, Lazy<Task<TResult>> entry) =>
        _running.TryRemove(new KeyValuePair<string, Lazy<Task<TResult>>>(key, entry));
}
