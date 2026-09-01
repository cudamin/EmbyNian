using CommunityToolkit.Mvvm.ComponentModel;
using EmbyNian.Shell.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// How a view model asks the user to confirm something it cannot undo. False when the answer is no; what
/// it means for there to be nobody to ask is the caller's to decide — <see cref="PageViewModel.ConfirmAsync"/>
/// answers no, so a missing dialog cannot delete a server, and <see cref="SettingConfigEditorRow.Confirm"/>
/// goes ahead, because the worst it can cost is unsaved typing.
/// <para>
/// A delegate rather than a service registered in the container. The dialog needs the page's
/// <c>XamlRoot</c>, so the page is the only thing that can raise it; what the view model needs is a
/// question and an answer, and that is one signature. An interface here would have exactly one
/// implementation, and it would be the page.
/// </para>
/// </summary>
internal delegate Task<bool> ConfirmRequest(string title, string message, string primary);

/// <summary>
/// What every page's view model has: a progress bar, one notice bar, and a load that is cancelled the
/// moment a newer one starts or the user navigates away.
/// <para>
/// The point of the class is that a page's XAML stops naming its own elements. Before this the pages
/// read <c>Busy.Visibility = Visibility.Visible</c> and <c>Subheading.Text = "正在读取…"</c>, which
/// compiles whatever order it is written in and silently does nothing if a later edit renames the
/// element — and there is no way to look at the page's state without a window on screen. Bound state
/// cannot drift from its element, and it can be read in a test.
/// </para>
/// <para>
/// Public, like <see cref="Views.CardItem"/>, because <c>x:Bind</c> compiles against the type by name.
/// Nothing about the shell's own plumbing appears on this surface — a derived view model keeps the
/// services it needs in private fields and takes them through an <c>internal</c> method — so making the
/// type visible to XAML does not make the container visible to anything else.
/// </para>
/// </summary>
public abstract partial class PageViewModel : ObservableObject, IDisposable
{
    /// <summary>The newest load. Cancelled by the next one, and by navigating away.</summary>
    private CancellationTokenSource? _cancel;

    /// <summary>Set by the page that owns this view model; see <see cref="ConfirmRequest"/>.</summary>
    private ConfirmRequest? _confirm;

    /// <summary>True while a load is in flight; drives the indeterminate bar at the top of a page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyVisibility))]
    public partial bool Busy { get; set; }

    /// <summary>
    /// False until the first load has finished, whether it found anything or not. This is what the
    /// startup self-check waits on before it reads a page: a page mid-request has nothing to say yet,
    /// and a screenshot of one is a screenshot of a progress bar.
    /// </summary>
    [ObservableProperty]
    public partial bool IsReady { get; set; }

    /// <summary>
    /// The notice bar's four properties. One bar per page, deliberately: three stacked messages all
    /// saying the server is unreachable is two more than anyone needs, so the first failure wins it
    /// and the rest go to the log.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoticeVisibility))]
    public partial bool NoticeOpen { get; set; }

    [ObservableProperty]
    public partial string? NoticeTitle { get; set; }

    [ObservableProperty]
    public partial string? NoticeMessage { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity NoticeSeverity { get; set; }

    /// <summary>
    /// Visibility rather than a bool plus a converter, which is the choice
    /// <see cref="Views.CardItem"/> already made and for the same reason: a converter per flag is a
    /// file per flag, and <c>[NotifyPropertyChangedFor]</c> is what makes the derived form safe —
    /// the generator raises this property's change notification from <see cref="Busy"/>'s setter, so
    /// the pair cannot come apart the way a hand-written <c>Raise()</c> list can.
    /// </summary>
    public Visibility BusyVisibility => Show(Busy);

    /// <summary>
    /// 提示条那一条自己的可见性。<c>InfoBar</c> 关着的时候只是把里面的东西收起来，控件本身还在树上、还是
    /// <c>Visible</c>，于是它那 12 的下边距照旧占着一行 —— 一条谁也看不见的提示，在页面顶上留 12 像素的空。
    /// 关着就整个收起来，那 12 才真的不占地方。
    /// </summary>
    public Visibility NoticeVisibility => Show(NoticeOpen);

    /// <summary>
    /// The generator calls this from <see cref="Busy"/>'s setter. It has to live in this class — the
    /// toolkit's <c>On…Changed</c> hooks are partial methods on the class that declares the property —
    /// so it forwards to something a derived class can override.
    /// </summary>
    partial void OnBusyChanged(bool value) => BusyChanged();

    /// <summary>
    /// A chance to re-ask every command whether it can run. Override it to call
    /// <c>SomeCommand.NotifyCanExecuteChanged()</c> for the commands gated on <see cref="Busy"/>.
    /// <para>
    /// This is what a command's <c>CanExecute</c> is for and what the pages were doing by hand before:
    /// <c>if (_busy) return;</c> at the top of a click handler leaves the button looking pressable, so
    /// the user's answer to nothing happening is to press it again. Gating it greys it out instead, and
    /// the button and the guard cannot disagree because there is only one of them now.
    /// </para>
    /// </summary>
    protected virtual void BusyChanged()
    {
    }

    /// <summary>Rebuilds the page from the server. Every page has one; the shell's refresh calls it.</summary>
    public abstract Task ReloadAsync();

    /// <summary>Called by the page that hosts this view model, once, when it is constructed.</summary>
    internal void UseConfirm(ConfirmRequest confirm) => _confirm = confirm;

    /// <summary>Asks the hosting page to put the question to the user. False if there is no page to ask.</summary>
    protected Task<bool> ConfirmAsync(string title, string message, string primary) =>
        _confirm?.Invoke(title, message, primary) ?? Task.FromResult(false);

    /// <summary>
    /// The page's dialog itself, for handing down to a row that asks its own questions — the config editor
    /// on the settings page is the one that does.
    /// <para>
    /// The raw delegate and not <see cref="ConfirmAsync"/>, so that null keeps meaning 「there is nobody to
    /// ask」 one level further down. Wrapping it would hide that behind a method that always answers, and the
    /// answer it would give is no, which for that row means a button that stops working outside a window.
    /// </para>
    /// <para>
    /// <c>private protected</c> rather than <c>protected</c> because this class is public and
    /// <see cref="ConfirmRequest"/> is not: derived view models are all in this assembly, and nothing outside
    /// it has any business being handed the shell's dialog.
    /// </para>
    /// </summary>
    private protected ConfirmRequest? Confirm => _confirm;

    protected static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Starts a load and hands back its token, cancelling whatever was already in flight.
    /// <para>
    /// Paired with <see cref="IsCurrent"/>. Together they replace the
    /// 「keep the source in a field and <c>ReferenceEquals</c> it afterwards」 dance each page used to
    /// write out by hand: an <c>await</c> can only come back after a second load has started, and a
    /// stale continuation that goes on to assign to the page is how a fast click on two libraries ends
    /// up showing the first one's contents under the second one's heading.
    /// </para>
    /// </summary>
    protected CancellationToken BeginLoad()
    {
        _cancel?.Cancel();
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();

        Busy = true;
        IsReady = false;
        NoticeOpen = false;

        return _cancel.Token;
    }

    /// <summary>
    /// Whether the load this token came from is still the newest one. False means a later load has
    /// started, and the caller must return without touching any bound state.
    /// </summary>
    protected bool IsCurrent(CancellationToken token) =>
        _cancel is { IsCancellationRequested: false } current && current.Token == token;

    /// <summary>Marks a load finished, if it is still the one that matters.</summary>
    protected void EndLoad(CancellationToken token)
    {
        if (!IsCurrent(token)) return;

        Busy = false;
        IsReady = true;
    }

    /// <summary>Abandons the load in flight. Called when the page is navigated away from.</summary>
    public virtual void Cancel()
    {
        _cancel?.Cancel();
        Busy = false;
    }

    /// <summary>Puts one failure in front of the user, worded for a person rather than a log.</summary>
    protected void Report(string what, Exception error) =>
        Notify(what, Failure.Describe(error), InfoBarSeverity.Error);

    /// <param name="title">
    /// Null where the message is already a whole sentence addressed to the user, which is how an
    /// <c>InfoBar</c> with no <c>Title</c> has always read.
    /// </param>
    protected void Notify(string? title, string? message, InfoBarSeverity severity)
    {
        NoticeSeverity = severity;
        NoticeTitle = title;
        NoticeMessage = message;
        NoticeOpen = true;
    }

    protected void ClearNotice() => NoticeOpen = false;

    public virtual void Dispose()
    {
        _cancel?.Cancel();
        _cancel?.Dispose();
        _cancel = null;
    }
}
