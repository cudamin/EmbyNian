using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 需求 7：在播放页面的右上角新增字幕字体的选择输入栏（要支持搜索）.
/// <para>
/// The box itself is declared in the XAML and its list, its filter and what a pick writes all belong to
/// <see cref="SettingFontRow"/> — the same row type the 设置 → 字幕 card uses, so the app has one
/// font search rather than two. What is left here is the four things that are true only on the player:
/// </para>
/// <list type="number">
/// <item>The strip it sits in hides itself. A box being typed into has to pin it, and that hold has to be
/// its own reason — see <see cref="Hold"/> — or a flyout opened and closed over it would release it.</item>
/// <item>The player owns single-letter keys. Every letter of 「Consolas」 is a command, so the box takes the
/// keyboard away from them for as long as it has focus.</item>
/// <item>Its text is the search, not the value: it reads as the family in use until it is focused, empties
/// so the whole list is there to scroll, and goes back to the family in use on the way out.</item>
/// <item>Enter has to mean something without a list to click, which is <c>SettingFontRow.Resolve</c>.</item>
/// </list>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>
    /// Whether 需求 7's box has the keyboard. Read by <see cref="OnKeyDown"/>, which must not treat what is
    /// being typed as playback commands, and by the self-check.
    /// </summary>
    internal bool Typing => _typing;

    /// <summary>
    /// Puts the family currently in use into the box. Called when the player comes up and after the box is
    /// left, because between those two the text belongs to whoever is searching with it.
    /// </summary>
    private void ShowCurrentFont()
    {
        if (!Attached) return;

        var current = ViewModel.SubtitleFont.Value;

        // Programmatic, so OnFontBoxTextChanged ignores it: this is not a search anybody typed.
        if (!string.Equals(FontBox.Text, current, StringComparison.Ordinal)) FontBox.Text = current;
    }

    /// <summary>
    /// Runs the search the box's text describes. Its own method rather than the body of the handler so the
    /// self-check can drive the real filter — <c>AutoSuggestBoxTextChangedEventArgs</c> cannot be built by
    /// hand, and a probe that reached past this into the row would be testing the row and not the box.
    /// </summary>
    private void SearchFonts(string text)
    {
        if (!Attached) return;

        ViewModel.SubtitleFont.Query = text;
    }

    /// <summary>
    /// Applies a family and puts the box back to reading as that family. Everything a pick means to the
    /// settings file and to the film is the row's; all that is decided here is that a pick ends the search.
    /// </summary>
    private void PickFont(FontOption? option)
    {
        if (option is null) return;

        // The row writes and saves from its own selection changing, which is also why re-picking what is
        // already selected is silently nothing rather than a second write of the same value.
        ViewModel.SubtitleFont.Selected = option;
        ViewModel.SubtitleFont.Query = string.Empty;

        _fontTextBefore = option.Name;
        ShowCurrentFont();
    }

    private void OnFontBoxFocus(object sender, RoutedEventArgs e) => BeginFontSearch();

    private void OnFontBoxBlur(object sender, RoutedEventArgs e) => EndFontSearch();

    /// <summary>
    /// The box has taken the keyboard. Separate from the handler so the self-check can drive it: a
    /// <c>RoutedEventArgs</c> is not something a probe can produce, and what happens here is the whole of
    /// what makes the box safe to type into.
    /// </summary>
    private void BeginFontSearch()
    {
        if (!Attached) return;

        // Asked for here as well as at playback start: this is the last moment before a human reads the
        // list, and on a cold cache the scan may still have been running when the film began.
        ViewModel.PrepareFonts();

        _typing = true;
        Hold(true, ChromeHold.Search);

        _fontTextBefore = FontBox.Text;

        // Emptied rather than selected: what is in the box is a family name, and typing into the end of one
        // — 「Microsoft YaHeiconsolas」 — matches nothing at all. Empty means the search offers everything,
        // which is what makes the box usable as a picker by someone who does not know what they want yet.
        FontBox.Text = string.Empty;
        SearchFonts(string.Empty);
        FontBox.IsSuggestionListOpen = true;
    }

    /// <summary>The box has given the keyboard back. See <see cref="BeginFontSearch"/> for why it is its own method.</summary>
    private void EndFontSearch()
    {
        if (!Attached) return;

        _typing = false;
        FontBox.IsSuggestionListOpen = false;

        // A search nobody finished is not a value. The family in use goes back into the box, and the filter
        // is cleared so the next open starts on the whole list rather than on the last half-typed word.
        SearchFonts(string.Empty);
        ShowCurrentFont();

        // Released last, so the strip is still up while the two lines above are being read.
        Hold(false, ChromeHold.Search);
    }

    private void OnFontBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // ProgrammaticChange is this file putting the current family back, and SuggestionChosen is the box
        // filling itself in with what was picked. Neither is a search.
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        SearchFonts(sender.Text);
        sender.IsSuggestionListOpen = true;
    }

    /// <summary>Arrow keys through the list: the row is applied as it is walked, which is what a picker does.</summary>
    private void OnFontSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args) =>
        PickFont(args.SelectedItem as FontOption);

    /// <summary>
    /// Enter, or a click on a row. A click brings its own family; Enter brings only text, and what that text
    /// means is the row's to decide.
    /// </summary>
    private void OnFontQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (!Attached) return;

        var picked = args.ChosenSuggestion as FontOption
                     ?? ViewModel.SubtitleFont.Resolve(args.QueryText);

        if (picked is null)
        {
            // Nothing matched what was typed. Said on the OSD rather than left silent, because the box
            // having gone back to the old family is otherwise indistinguishable from a pick that failed.
            ViewModel.NoticeNoFont(args.QueryText);
            ShowCurrentFont();
            return;
        }

        PickFont(picked);

        // Focus off the box, so the film gets its keys back the moment the font is chosen. Without this the
        // hand has to find somewhere else to click before Space pauses again. The page itself, which is what
        // carries the player's own KeyDown.
        Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Escape while the box has the keyboard means 「abandon this search」, not 「离开全屏」. The second Escape
    /// is the player's, which is why focus is moved rather than the event simply being swallowed.
    /// </summary>
    private void OnFontBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;

        FontBox.Text = _fontTextBefore;
        SearchFonts(string.Empty);
        Focus(FocusState.Programmatic);
        e.Handled = true;
    }
}
