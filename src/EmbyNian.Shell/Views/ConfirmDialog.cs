using EmbyNian.Diagnostics;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// The view half of <see cref="ConfirmRequest"/>: the one yes/no dialog every page puts its questions
/// through.
/// <para>
/// Here rather than on each page because a page's share of it is a single line — the element whose
/// <c>XamlRoot</c> hosts it — and everything else is the same wherever it is asked. Which is how the two
/// copies this replaces had drifted: one pinned the theme and swallowed the 「a dialog is already up」
/// exception, the other did neither, so 服务器页's delete prompt came up in whatever the Windows app mode
/// is and would have thrown out of a command if it ever raced another dialog.
/// </para>
/// </summary>
internal static class ConfirmDialog
{
    private const string Category = "对话框";

    /// <summary>
    /// A <see cref="ConfirmRequest"/> that asks in front of <paramref name="owner"/>.
    /// <para>
    /// No window to ask in means no. That used to be a per-page choice, because the settings page's config
    /// editor asked before discarding unsaved typing and going ahead was the kinder answer there; with that
    /// editor gone, every question left in the app is about something irreversible on the server — deleting a
    /// server, an account or an item, or letting a scrape overwrite metadata — and a dialog that cannot be
    /// shown must not be read as consent to any of those.
    /// </para>
    /// </summary>
    internal static ConfirmRequest For(FrameworkElement owner) =>
        (title, message, primary) => AskAsync(owner, title, message, primary);

    private static async Task<bool> AskAsync(
        FrameworkElement owner,
        string title,
        string message,
        string primary)
    {
        // Read when the question is asked, not when the delegate was made: a page hands this out in its
        // constructor, which is long before it is in a tree with a root to put a dialog in.
        if (owner.XamlRoot is not { } root) return false;

        var dialog = new ContentDialog
        {
            XamlRoot = root,

            // A dialog is hosted in the XamlRoot's popup root rather than under the page that asked, so it
            // inherits nothing from the tree that raised it — without this it would come up in whatever the
            // Windows app mode is. Taken from the chosen theme rather than pinned Dark: a dialog in the dark
            // for a user on 晴昼 is the same bug the other way round.
            RequestedTheme = ThemeHost.Current.IsDark ? ElementTheme.Dark : ElementTheme.Light,
            Title = title,
            Content = message,
            PrimaryButtonText = primary,
            CloseButtonText = "取消",

            // Close, so a stray Enter or Escape on a 「删除这台服务器」 dialog cancels rather than deletes.
            DefaultButton = ContentDialogButton.Close
        };

        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception error)
        {
            // WinUI allows one dialog at a time and throws for the second. Answering no is the safe way to
            // lose that race: the button does nothing, instead of deleting or discarding something nobody
            // confirmed.
            Log.Warn(Category, "无法显示确认对话框", error);
            return false;
        }
    }
}
