using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EmbyNian.Shell.Views;

/// <summary>
/// The letter jump bar on a library grid's right edge, in the form Emby Theater's alphaPicker takes on
/// the desktop: a vertical strip of # and A–Z buttons that scrolls the grid to the first item whose
/// name starts there.
/// <para>
/// The bar is dumb: it raises <see cref="JumpRequested"/> and draws whatever
/// <see cref="SetCurrent"/> tells it. The count-to-index arithmetic, the query and the scrolling live
/// where the grid lives — this control knows nothing about items or scroll offsets.
/// </para>
/// <para>
/// The current letter is a style swap rather than a visual state, because the two styles differ by two
/// setters and a visual state group would be a template of this control's own just to carry them.
/// </para>
/// </summary>
public sealed partial class AlphaBar : UserControl
{
    // 27 buttons at 18px plus the rail padding/border need roughly 500px. Below 400px the
    // picker would obscure more of the grid than it helps, so Emby-style behaviour is to hide it.
    private const double CompactHeight = 510;
    private const double HiddenHeight = 400;

    /// <summary># then A–Z, the alphaPicker's own prefix list.</summary>
    private static readonly string[] Alphabet = ["#", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z"];

    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);

    private bool _compact;
    private string? _currentLetter;

    public AlphaBar()
    {
        InitializeComponent();

        // Built here rather than in markup: twenty-seven buttons is a loop, and the loop is also where
        // the click wiring and the button map live — keeping the three together is the whole point of
        // not writing the alphabet out in XAML.
        var panel = new StackPanel { Spacing = 0 };
        foreach (var letter in Alphabet)
        {
            var button = new Button
            {
                Content = letter,
                Style = (Style)Resources["EgAlphaButtonStyle"],
                TabIndex = Array.IndexOf(Alphabet, letter),
                UseSystemFocusVisuals = true
            };
            button.Click += (_, _) => JumpRequested?.Invoke(this, letter);
            button.KeyDown += OnLetterKeyDown;
            _buttons[letter] = button;
            panel.Children.Add(button);
        }

        // StackPanel directly rather than another ItemsRepeater: the collection never changes, so
        // virtualisation would only add a recycler between the buttons and their handlers.
        Letters.Content = panel;
    }

    /// <summary>A letter was clicked. The letter itself is the argument — the bar holds nothing else.</summary>
    public event EventHandler<string>? JumpRequested;

    /// <summary>
    /// Lights one letter up, or none. Given the letter rather than asked to work it out: which letter
    /// the grid is on depends on the sort order and the rows, which are the grid's facts.
    /// </summary>
    public void SetCurrent(string? letter)
    {
        _currentLetter = letter;
        ApplyButtonStyles();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var height = e.NewSize.Height;
        var state = height < HiddenHeight ? "Hidden" : height < CompactHeight ? "Compact" : "Full";
        var compact = state == "Compact";

        VisualStateManager.GoToState(this, state, false);

        if (_compact == compact) return;

        _compact = compact;
        ApplyButtonStyles();
    }

    private void ApplyButtonStyles()
    {
        var plainKey = _compact ? "EgAlphaCompactButtonStyle" : "EgAlphaButtonStyle";
        var currentKey = _compact ? "EgAlphaCompactCurrentStyle" : "EgAlphaCurrentStyle";
        var plain = (Style)Resources[plainKey];
        var current = (Style)Resources[currentKey];

        foreach (var (value, button) in _buttons)
            button.Style = value == _currentLetter ? current : plain;
    }

    /// <summary>
    /// The picker is a one-dimensional control, so arrow keys stay inside it. Home/End mirror the
    /// standard list navigation keys and Enter/Space remain the Button's own invocation path.
    /// </summary>
    private void OnLetterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Button { Content: string letter }) return;

        var index = Array.IndexOf(Alphabet, letter);
        var next = e.Key switch
        {
            VirtualKey.Up or VirtualKey.Left => index - 1,
            VirtualKey.Down or VirtualKey.Right => index + 1,
            VirtualKey.Home => 0,
            VirtualKey.End => Alphabet.Length - 1,
            _ => index
        };

        next = Math.Clamp(next, 0, Alphabet.Length - 1);
        if (next == index) return;

        e.Handled = _buttons[Alphabet[next]].Focus(FocusState.Keyboard);
    }

    /// <summary>
    /// 自检：which letters the bar holds, and which one is lit — the two facts about it a report can read
    /// without a window.
    /// </summary>
    internal IReadOnlyList<string> LettersHeld => [.. _buttons.Keys];

    internal string? CurrentLetter => _currentLetter;
}
