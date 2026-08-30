using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// One line on the settings page: a label, an optional explanation under it, and one control bound to one
/// setting.
/// <para>
/// The page has around sixty of these and they are all the same handful of shapes, so they are data rather
/// than markup: the view model builds a list of rows and the page renders each one with the template for
/// its shape. The alternative — sixty <c>[ObservableProperty]</c> pairs on one view model and sixty
/// hand-written rows in XAML — is the same page written three times over, and the three copies have to be
/// kept in step by hand.
/// </para>
/// <para>
/// Each row reaches its setting through a read/write pair handed to it at construction rather than holding
/// a settings object and a property name. That keeps the property access compiled and checked: a renamed
/// setting is a build error instead of a row that silently stops working.
/// </para>
/// </summary>
public abstract class SettingRow : ObservableObject
{
    protected SettingRow(string label, string? note)
    {
        Label = label;
        Note = note ?? "";
    }

    /// <summary>The text in the left-hand column.</summary>
    public string Label { get; }

    /// <summary>The smaller line under the label, or empty when there is none.</summary>
    public string Note { get; }

    /// <summary>Collapses the note's <c>TextBlock</c> so an absent note takes no vertical space.</summary>
    public Visibility NoteVisibility => Note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// One entry in a <see cref="SettingChoiceRow"/>'s drop-down. It carries the act of choosing it rather than
/// the value chosen, which is what lets one non-generic row type serve enums, mpv option strings and
/// shader profile names alike: the value stays inside the closure the factory built, correctly typed, and
/// never has to be cast back out of an <c>object</c>.
/// </summary>
public sealed class SettingChoice
{
    private readonly Action _apply;

    internal SettingChoice(string label, Action apply)
    {
        Label = label;
        _apply = apply;
    }

    /// <summary>What the drop-down shows.</summary>
    public string Label { get; }

    internal void Apply() => _apply();

    public override string ToString() => Label;
}

/// <summary>A drop-down bound to one setting.</summary>
public sealed partial class SettingChoiceRow : SettingRow
{
    private readonly Action _save;
    private readonly bool _seeded;

    internal SettingChoiceRow(string label, string? note, IReadOnlyList<SettingChoice> choices, SettingChoice? selected, Action save)
        : base(label, note)
    {
        Choices = choices;
        Selected = selected;
        _save = save;

        // Set last, so seeding the value above did not write it straight back to the settings file. A flag
        // per row rather than one 「loading」 flag over the whole page: the page-wide version also
        // suppressed genuine edits for as long as it was up, and had to be reasoned about globally.
        _seeded = true;
    }

    public IReadOnlyList<SettingChoice> Choices { get; }

    [ObservableProperty]
    public partial SettingChoice? Selected { get; set; }

    partial void OnSelectedChanged(SettingChoice? value)
    {
        if (!_seeded || value is null) return;
        value.Apply();
        _save();
    }
}

/// <summary>
/// A switch bound to one setting. Unlike the other rows this one carries its own header and sits full
/// width, so its label is not in the shared left-hand column and its note becomes the tooltip.
/// </summary>
public sealed partial class SettingToggleRow : SettingRow
{
    private readonly Action<bool> _write;
    private readonly Action _save;
    private readonly bool _seeded;

    internal SettingToggleRow(string label, string note, bool value, Action<bool> write, Action save)
        : base(label, note)
    {
        IsOn = value;
        _write = write;
        _save = save;
        _seeded = true;
    }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        if (!_seeded) return;
        _write(value);
        _save();
    }
}

/// <summary>
/// A heading and a tighter stack of switches, for a set of related on/off options — the passthrough codecs
/// are one setting each but read as one question, so they are grouped instead of spread out like the rest.
/// </summary>
public sealed class SettingToggleGroupRow : SettingRow
{
    internal SettingToggleGroupRow(string heading, IReadOnlyList<SettingToggleRow> toggles)
        : base(heading, null) => Toggles = toggles;

    public IReadOnlyList<SettingToggleRow> Toggles { get; }
}

/// <summary>
/// A spinner bound to one numeric setting, clamped to the range the setting accepts.
/// <para>
/// After the save, the value is read back out of the settings document and shown — which is not always the
/// number that was typed, because saving normalises the document: a subtitle size in the dead band below 16
/// is lifted to 16, and a 低清阈值 that crossed 高清阈值 is pushed under it. Without the read-back the box goes
/// on showing a number the settings file does not contain, and nothing corrects it short of rebuilding the
/// page. The same idea as <see cref="SettingTextRow"/>'s commit handing back the text to display.
/// </para>
/// </summary>
public sealed partial class SettingNumberRow : SettingRow
{
    private readonly Action<double> _write;
    private readonly Func<double> _reread;
    private readonly Action _save;
    private readonly Action? _after;
    private readonly bool _seeded;
    private bool _committing;

    internal SettingNumberRow(string label, string? note, double minimum, double maximum, double value, Action<double> write, Func<double> reread, Action save, Action? after = null)
        : base(label, note)
    {
        Minimum = minimum;
        Maximum = maximum;
        Value = Math.Clamp(value, minimum, maximum);
        _write = write;
        _reread = reread;
        _save = save;
        _after = after;
        _seeded = true;
    }

    public double Minimum { get; }

    public double Maximum { get; }

    [ObservableProperty]
    public partial double Value { get; set; }

    /// <summary>Re-reads the setting into the box without writing it back — for a value another row moved.</summary>
    internal void Reseed(double value)
    {
        _committing = true;
        try { Value = value; }
        finally { _committing = false; }
    }

    partial void OnValueChanged(double value)
    {
        // An emptied NumberBox reports NaN. Writing that through would replace a real setting with
        // nothing, so a cleared box leaves the setting where it was until a number is typed.
        if (!_seeded || _committing || double.IsNaN(value)) return;

        _committing = true;
        try
        {
            _write(value);
            _save();

            var stored = _reread();
            if (stored != value) Value = stored;
        }
        finally { _committing = false; }

        _after?.Invoke();
    }
}

/// <summary>
/// A slider bound to one numeric setting, for values where dragging beats typing. The step is explicit
/// because these are all integers and WinUI otherwise reports fractions of one.
/// </summary>
public sealed partial class SettingSliderRow : SettingRow
{
    private readonly Action<double> _write;
    private readonly Action _save;
    private readonly bool _seeded;

    internal SettingSliderRow(string label, string? note, double minimum, double maximum, double step, double value, Action<double> write, Action save)
        : base(label, note)
    {
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Value = Math.Clamp(value, minimum, maximum);
        _write = write;
        _save = save;
        _seeded = true;
    }

    public double Minimum { get; }

    public double Maximum { get; }

    public double Step { get; }

    [ObservableProperty]
    public partial double Value { get; set; }

    partial void OnValueChanged(double value)
    {
        if (!_seeded) return;
        _write(value);
        _save();
    }
}

/// <summary>
/// A text box bound to one setting.
/// <para>
/// Commits when the box loses focus, not per keystroke — which is what <c>x:Bind</c> does for
/// <c>TextBox.Text</c> without being asked, and is the only sensible choice here: a setting saved per
/// keystroke would rewrite the settings file a dozen times while a path is being typed, and would hand mpv
/// half a font name.
/// </para>
/// <para>
/// The commit hands back the text to display, which is not always the text that was typed: a list setting
/// tidies its separators, and a config path that matches the inferred one is stored as 「infer it」 and
/// comes back as the inferred path. Showing the typed text in those cases would claim something was saved
/// that was not.
/// </para>
/// </summary>
public sealed partial class SettingTextRow : SettingRow
{
    private readonly Func<string, string> _commit;
    private readonly Action _save;
    private readonly Action? _after;
    private readonly bool _seeded;
    private bool _committing;

    internal SettingTextRow(string label, string? note, string placeholder, string value, Func<string, string> commit, Action save, Action? after = null)
        : base(label, note)
    {
        Placeholder = placeholder;
        Text = value;
        _commit = commit;
        _save = save;
        _after = after;
        _seeded = true;
    }

    public string Placeholder { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>Re-reads the setting into the box without committing — for a value another row changed.</summary>
    internal void Reseed(string value)
    {
        _committing = true;
        try { Text = value; }
        finally { _committing = false; }
    }

    partial void OnTextChanged(string value)
    {
        if (!_seeded || _committing) return;

        _committing = true;
        try
        {
            var display = _commit(value);
            if (!string.Equals(display, value, StringComparison.Ordinal)) Text = display;
        }
        finally { _committing = false; }

        _save();
        _after?.Invoke();
    }
}
