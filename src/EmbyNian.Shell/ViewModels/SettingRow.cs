using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
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

/// <summary>
/// 一行读数，加一颗可有可无的按钮 —— 「关于」那张卡上的每一行都是这个形状：左边是标签，底下一行是值
/// （版本号、一个目录），右边那颗按钮把那个目录在资源管理器里打开。
/// <para>
/// 和别的行反着：这一行不写设置，所以它没有读写对，也没有 <c>Save</c>。它存在的理由是「这一页上有些事只是
/// 要说出来」—— 在它之前，整个程序没有一处显示自己的版本号，也没有一处能一键打开日志目录（诊断页那颗按钮
/// 只开日志，设置文件和缓存都得自己去找）。
/// </para>
/// </summary>
public sealed partial class SettingFactRow : SettingRow
{
    private readonly Action? _act;
    internal SettingFactRow(string label, string? note, string value, string? actionLabel = null, Action? act = null)
        : base(label, note)
    {
        Value = value;
        ActionLabel = actionLabel ?? "";
        _act = act;
    }

    /// <summary>那一行值。等宽字排，因为多数时候它是一个路径。</summary>
    public string Value { get; }

    public string ActionLabel { get; }

    /// <summary>没有按钮的那几行不给它留位置，同 <see cref="SettingRow.NoteVisibility"/>。</summary>
    public Visibility ActionVisibility =>
        _act is null || ActionLabel.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    [RelayCommand]
    private void Run() => _act?.Invoke();
}

/// <summary>
/// 主页版面那一行：一张可以拖着换次序、每一项自己带一个勾的表 —— 「新增页里拖拽决定这些列表的顺序，勾选显示
/// 或者不勾选取消显示」。
/// <para>
/// 和别的行不一样，它管的不是一个开关而是一份有序清单，所以读写那一对换成了「拿到这一份」和「这一份变了」：拖过
/// 一次、或者点过一个勾，都当场写回设置并喊一声（见 <see cref="SettingsViewModel"/> 里造它的那一段）。
/// </para>
/// <para>
/// 表里那几项是 <see cref="HomeRowChoice"/>，顺序就是集合自己的顺序 —— <c>ListView</c> 拖动时改的正是这个集合，
/// 所以「屏上的次序」和「要存的次序」是同一件东西，不用在两处之间对齐。
/// </para>
/// </summary>
public sealed partial class SettingHomeLayoutRow : SettingRow
{
    private readonly Action<IReadOnlyList<HomeRowChoice>> _changed;
    private bool _quiet;

    internal SettingHomeLayoutRow(
        string label,
        string? note,
        IEnumerable<HomeRowChoice> rows,
        Action<IReadOnlyList<HomeRowChoice>> changed)
        : base(label, note)
    {
        _changed = changed;

        foreach (var row in rows)
        {
            row.Changed = Save;
            Rows.Add(row);
        }

        Rows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ListHeight));
            Save();
        };
    }

    /// <summary>屏上那张表，顺序就是主页上那几排的顺序。</summary>
    public ObservableCollection<HomeRowChoice> Rows { get; } = [];

    /// <summary>
    /// 这张表要多高。给死高度而不是让它自己滚：设置页本来就是一整页滚动条，表里再套一个滚动条的话，拖到边上
    /// 时两个滚动条会互相抢，一项都拖不到看不见的地方去。
    /// </summary>
    public double ListHeight => (Rows.Count * RowHeight) + 8;

    /// <summary>一项占多高。<c>ListViewItem</c> 默认那一档加上勾和把手之后量出来的数。</summary>
    private const double RowHeight = 40;

    /// <summary>装表的时候先别喊 —— 那不是用户改的。</summary>
    internal void Seed(IEnumerable<HomeRowChoice> rows)
    {
        _quiet = true;

        try
        {
            Rows.Clear();
            foreach (var row in rows)
            {
                row.Changed = Save;
                Rows.Add(row);
            }
        }
        finally
        {
            _quiet = false;
        }

        OnPropertyChanged(nameof(ListHeight));
    }

    private void Save()
    {
        if (_quiet) return;

        _changed([.. Rows]);
    }
}

/// <summary>
/// 主页版面表里的一项：屏上那句标题、认它的那把钥匙、勾了没有。
/// <para>
/// 勾变了就地喊回去（<see cref="Changed"/>），因为 <c>CheckBox</c> 改的是这一项而不是那张表，集合自己的
/// <c>CollectionChanged</c> 听不见。
/// </para>
/// </summary>
public sealed partial class HomeRowChoice : ObservableObject
{
    internal HomeRowChoice(string key, string title, bool visible)
    {
        Key = key;
        Title = title;

        // 直接写属性：这一刻 Changed 还是空的（造完才由那一行挂上），所以不会把「装表」当成「用户点了勾」。
        Visible = visible;
    }

    /// <summary>见 <see cref="EmbyNian.Emby.HomeLayout"/>。</summary>
    public string Key { get; }

    public string Title { get; }

    /// <summary>勾变了的时候喊一声；由 <see cref="SettingHomeLayoutRow"/> 挂上。</summary>
    internal Action? Changed { get; set; }

    [ObservableProperty]
    public partial bool Visible { get; set; }

    partial void OnVisibleChanged(bool value) => Changed?.Invoke();

    /// <summary>读屏的人听到的那一句，也是拖动时那块浮起来的东西的名字。</summary>
    public override string ToString() => Title;
}
