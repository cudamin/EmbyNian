using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Emby;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// 需求 5：the filter panel. Edits an <see cref="ItemFilters"/> in place and says so; the page it sits in
/// owns that object, puts it in the query and remembers it.
/// <para>
/// The on/off entries come from <see cref="EmbyFilterBy"/> — a catalogue transcribed from the server's own
/// OpenAPI document, because Emby ignores a query parameter it does not recognise and a filter with a
/// wrong key therefore returns the whole library rather than an error. 类型, 标签 and 年份 cannot be
/// catalogued at all: they are whatever the rows in this particular grid happen to have, so each is a
/// <see cref="FilterValueSection"/> that asks the server the first time it is opened.
/// </para>
/// <para>
/// Every tick applies immediately, which is why there is no 确定 button: the grid is right there behind
/// the flyout, and a panel that made the user confirm would be a panel that hid the answer.
/// </para>
/// <para>
/// Not a <see cref="PageViewModel"/>: this is a flyout, not a page. It has no progress bar and no notice
/// bar of its own — a section that cannot read its choices says so in its own header rather than over the
/// whole panel — and its load is per section rather than per panel.
/// </para>
/// </summary>
public sealed partial class FilterPanelViewModel : ObservableObject
{
    /// <summary>
    /// One request per list for as long as this panel lives, kept as the task rather than its result so
    /// two openings of the same section share one round trip. A failed task is dropped, so closing the
    /// panel and opening it again retries instead of remembering the failure.
    /// </summary>
    private readonly Dictionary<string, Task<IReadOnlyList<string>>> _requests = new(StringComparer.Ordinal);

    private ItemFilters _selection = new();
    private string? _preset;
    private Version? _serverVersion;
    private Func<string, CancellationToken, Task<IReadOnlyList<string>>>? _lookup;
    private CancellationTokenSource? _cancel;

    /// <summary>
    /// Something was ticked. The page reloads its grid and writes the selection down.
    /// </summary>
    internal event EventHandler? Changed;

    /// <summary>The blocks on screen, in order; see <see cref="FilterSection"/>.</summary>
    public ObservableCollection<FilterSection> Sections { get; } = [];

    /// <summary>
    /// What is on, in words. The button outside only has room for a number, and a panel that has been
    /// scrolled away from its ticks needs to be able to say so.
    /// </summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "全部内容";

    /// <summary>Every choice this panel is offering, for the self-check to report on.</summary>
    internal IReadOnlyList<string> Labels => [.. Sections.SelectMany(section => section.Labels)];

    /// <summary>
    /// IsBlank rather than IsEmpty, so an id this build does not recognise can still be cleared —
    /// otherwise it is unreachable: not counted, not shown, never removed.
    /// </summary>
    private bool CanClear => !_selection.IsBlank;

    /// <summary>
    /// Rebuilds the panel for the grid it is about to be shown over. Called on every open rather than
    /// once: which options apply depends on what the grid lists and on a server version that may still
    /// have been in flight when the page opened.
    /// </summary>
    internal void Open(
        ItemFilters selection,
        string? preset,
        Version? serverVersion,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> lookup)
    {
        _selection = selection;
        _preset = preset;
        _serverVersion = serverVersion;
        _lookup = lookup;
        _cancel ??= new CancellationTokenSource();

        Build();
    }

    /// <summary>
    /// The page is leaving. Not called when the flyout merely closes: a list still arriving is worth
    /// keeping, since the next open would otherwise ask for it again.
    /// </summary>
    internal void Dismiss() => _cancel?.Cancel();

    /// <inheritdoc cref="_requests"/>
    /// <returns>Null when there is nothing to ask yet, which is a panel built before its page attached.</returns>
    internal Task<IReadOnlyList<string>>? Lookup(string key)
    {
        if (_lookup is null || _cancel is null) return null;
        if (_requests.TryGetValue(key, out var cached)) return cached;

        var request = _lookup(key, _cancel.Token);
        _requests[key] = request;
        return request;
    }

    /// <summary>Drops a failed request, so opening its section again asks the server rather than the cache.</summary>
    internal void Forget(string key) => _requests.Remove(key);

    /// <summary>The selection changed. Redraws what this panel says about itself, then tells the page.</summary>
    internal void Notify()
    {
        ShowSummary();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts every on/off box back in step with the model, for the box some other box turned off. Only the
    /// exclusive pairs need it — 已播放/未播放 and 高清/标清 — but there are two dozen boxes in total and
    /// finding the four that matter would mean the panel knowing which those are.
    /// </summary>
    internal void SyncToggles()
    {
        foreach (var group in Sections.OfType<FilterToggleGroup>())
            foreach (var toggle in group.Toggles)
                toggle.Reseed();
    }

    /// <summary>
    /// A rebuild rather than a walk over the boxes: clearing also collapses the sections, which is the
    /// state a panel with nothing on should be in.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        if (!_selection.Clear()) return;

        Build();
        Notify();
    }

    private void Build()
    {
        Sections.Clear();

        var options = EmbyFilterBy.For(_preset, _serverVersion);

        foreach (var group in EmbyFilterBy.Groups.Order)
        {
            var inGroup = options.Where(option => option.Group == group).ToList();
            if (inGroup.Count == 0) continue;

            Sections.Add(new FilterToggleGroup(group, [.. inGroup.Select(Toggle)]));
        }

        foreach (var list in EmbyFilterBy.Lists)
            Sections.Add(new FilterValueSection(list, _selection, this));

        ShowSummary();
    }

    private FilterCheck Toggle(EmbyFilterOption option) => new(
        option.Label,
        $"{option.Key}={option.Value}",
        () => _selection.Has(option.Id),
        on =>
        {
            if (!_selection.Set(option, on)) return;

            // 已播放 and 未播放 together ask for items that are both, which is nothing at all: the model
            // just dropped the other one and the boxes have to show it.
            if (option.Excludes.Count > 0) SyncToggles();

            Notify();
        });

    private void ShowSummary()
    {
        Summary = _selection.IsEmpty ? "全部内容" : _selection.Describe();
        ClearCommand.NotifyCanExecuteChanged();
    }
}
