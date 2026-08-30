namespace EmbyNian.Shell.Views;

/// <summary>
/// A page that can be asked to rebuild itself. Implemented by every page whose contents come from the
/// server, which is every page whose contents a playback can change: watching something moves its
/// 已看 mark and its resume position, and the grid the user came back to is still drawing what it was
/// opened with.
/// <para>
/// The page hands back the request it was navigated with rather than exposing a <c>Reload</c> of its
/// own, so <see cref="ShellPage.RefreshActive"/> can reload it through the same
/// <c>OnNavigatedTo</c> path as a first visit. A separate refresh path is a second way to build a page,
/// and two ways to build a page drift.
/// </para>
/// </summary>
internal interface IShellContent
{
    /// <summary>The parameter this page was navigated with, ready to be navigated with again.</summary>
    object? NavigationRequest { get; }

    /// <summary>
    /// Drops everything this page is holding: in-flight loads, event subscriptions, open flyouts.
    /// Every implementation is also what its <c>OnNavigatedFrom</c> runs, so a page has one teardown
    /// body rather than two that drift.
    /// <para>
    /// It exists as a separate entry point because signing out and switching accounts do not navigate —
    /// they drop the page by assigning <c>ContentFrame.Content = null</c>, and that assignment does not
    /// drive the frame's navigation pipeline, so <c>OnNavigatedFrom</c> never runs. Before this existed
    /// the leak grew with the page: a cancelled-too-late load merely wasted a round trip, but a page that
    /// subscribes to a process-lifetime event (the log ring buffer, playback status) stayed subscribed for
    /// the rest of the process and gained one more orphan on every sign-out.
    /// </para>
    /// </summary>
    void Release();
}
