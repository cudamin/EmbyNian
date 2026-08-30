using Microsoft.UI.Dispatching;

namespace EmbyNian.Shell.Platform;

/// <summary>
/// The UI thread, as the two things a view model ever asks of it.
/// <para>
/// An interface because a view model reaching for <see cref="DispatcherQueue.GetForCurrentThread"/> itself
/// makes 「I was constructed on the UI thread」 part of its contract without ever saying so — the diagnostics
/// view model captured its queue in a field initialiser, so a page built off the UI thread would have
/// marshalled every log append onto nothing at all and shown a list that never grew. Asking for the thread
/// instead of reaching for it moves that requirement to the one place that can be checked once, at startup.
/// </para>
/// <para>
/// Two members rather than one, because the difference between them is load-bearing. <see cref="Run"/> is
/// the common case: get this onto the UI thread, and do not pay for a queue hop when it is already there.
/// <see cref="Post"/> always hops, which is what an event handler that inserts into a bound collection
/// needs — running that inline would touch the collection while whatever is further up the stack is still
/// walking it.
/// </para>
/// <para>
/// Public where its two siblings in this folder are internal, and for a reason that is entirely mechanical:
/// <c>PlayerViewModel</c> takes this through a constructor the container calls, and a container needs that
/// constructor public. <see cref="IClipboard"/> and <see cref="ISystemLauncher"/> only ever appear in
/// <c>internal</c> <c>Attach</c> methods, so they stay internal. The implementations are internal either
/// way — nothing outside this assembly has any business naming one.
/// </para>
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// Queues <paramref name="work"/> and returns without running it, even when the caller is already on
    /// the UI thread. False when the queue is shutting down, which is the answer on the way out of the
    /// process and not something a caller can do anything about.
    /// </summary>
    bool Post(Action work);

    /// <summary>
    /// Runs <paramref name="work"/> on the UI thread: inline when the caller is already there, queued when
    /// it is not. Never blocks, so a caller off the UI thread has no guarantee the work has finished by the
    /// time this returns.
    /// </summary>
    void Run(Action work);
}

/// <inheritdoc cref="IUiDispatcher"/>
/// <remarks>
/// Registered as an instance built while the container is being built, rather than from a factory lambda: a
/// factory would capture whichever thread first happened to ask for it. <c>ShellServices.Build</c> is called
/// from <c>App.OnLaunched</c> and nowhere else, so the queue this gets is the UI thread's.
/// </remarks>
internal sealed class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    /// <inheritdoc />
    public bool Post(Action work) => queue.TryEnqueue(() => work());

    /// <inheritdoc />
    public void Run(Action work)
    {
        if (queue.HasThreadAccess) work();
        else queue.TryEnqueue(() => work());
    }
}
