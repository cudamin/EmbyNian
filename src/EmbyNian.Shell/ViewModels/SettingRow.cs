using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyNian.Configuration;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

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
    private string _note;

    protected SettingRow(string label, string? note)
    {
        Label = label;
        _note = note ?? "";
    }

    /// <summary>The text in the left-hand column.</summary>
    public string Label { get; }

    /// <summary>
    /// The smaller line under the label, or empty when there is none.
    /// <para>
    /// Settable, and observable, for one row: 视频同步 states the value actually in force, and what is in force
    /// changes when 启用插值 is switched two rows below it. Every other row's note is written once at
    /// construction and never touched — see <see cref="Restate"/> for why this is not a general refresh
    /// mechanism.
    /// </para>
    /// </summary>
    public string Note
    {
        get => _note;
        private set
        {
            if (SetProperty(ref _note, value)) OnPropertyChanged(nameof(NoteVisibility));
        }
    }

    /// <summary>
    /// Replace the note. <b>Only for a row whose note states another row's consequence</b>, and only from the
    /// row that causes it — the page has no refresh pass and deliberately does not want one (see
    /// <see cref="SettingsViewModel"/>: no 「loading」 flag exists because rows read once and write from then
    /// on). A row that recomputed its own note on every edit would be that flag's problem back again.
    /// </summary>
    internal void Restate(string note) => Note = note ?? "";

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
    private readonly Action? _after;
    private readonly bool _seeded;

    private IReadOnlyList<SettingChoice> _choices;

    /// <summary>True while <see cref="Fill"/> is swapping the list, so the reseed does not write to disk.</summary>
    private bool _refilling;

    internal SettingChoiceRow(
        string label,
        string? note,
        IReadOnlyList<SettingChoice> choices,
        SettingChoice? selected,
        Action save,
        Action? after = null)
        : base(label, note)
    {
        _choices = choices;
        Selected = selected;
        _save = save;
        _after = after;

        // Set last, so seeding the value above did not write it straight back to the settings file. A flag
        // per row rather than one 「loading」 flag over the whole page: the page-wide version also
        // suppressed genuine edits for as long as it was up, and had to be reasoned about globally.
        _seeded = true;
    }

    /// <summary>
    /// The drop-down's entries.
    /// <para>
    /// Observable for one row: 音频输出设备's list has to be read out of a throwaway libmpv context, which is
    /// tens of milliseconds of native work, so the page is built with 「自动」 plus whatever the settings file
    /// holds and <see cref="Fill"/> puts the real devices in a moment later. Same shape as the 字幕字体 picker.
    /// Every other row's list is fixed at construction.
    /// </para>
    /// </summary>
    public IReadOnlyList<SettingChoice> Choices
    {
        get => _choices;
        private set => SetProperty(ref _choices, value);
    }

    [ObservableProperty]
    public partial SettingChoice? Selected { get; set; }

    /// <summary>
    /// Swap in a list that had to be fetched, and re-point the selection at the equivalent entry.
    /// <para>
    /// The reseed must not look like an edit — by now <c>_seeded</c> is true, so a plain assignment would
    /// write the value back to disk and fire <c>after</c>. That is not academic: the entry handed in here is a
    /// different object from the one the page was built with, so <c>Selected</c> genuinely changes every time.
    /// </para>
    /// </summary>
    internal void Fill(IReadOnlyList<SettingChoice> choices, SettingChoice? selected)
    {
        _refilling = true;
        try
        {
            Choices = choices;
            Selected = selected;
        }
        finally
        {
            _refilling = false;
        }
    }

    partial void OnSelectedChanged(SettingChoice? value)
    {
        if (!_seeded || _refilling || value is null) return;
        value.Apply();
        _save();

        // Same shape as SettingNumberRow's: after the save, for the one row whose own note states what is
        // actually in force. 视频同步 used to restate itself only when 启用插值 moved, so picking a value in
        // its own drop-down left the line underneath contradicting the selection above it.
        _after?.Invoke();
    }

    /// <summary>
    /// 自检：选一项之后写盘一次，而且那一行自己的说明真的跟着重写了 —— 就地造一个假的下拉行按一下，不碰设置
    /// 文件、也不碰屏上那一页（同 <see cref="SettingHomeLayoutRow.Probe"/> 的做法）。
    /// <para>
    /// 这一条非有不可，理由和箭头那一条一样：单元测试进不到外壳这个程序集，而这台机器上注不进鼠标事件，所以
    /// 「点开下拉、选一项」这一下没法自动做一遍。而少了 <c>after</c> 这根线，屏上看不出任何区别 —— 说明还在，
    /// 只是说的是上一次的事。视频同步那一行正是这么骗了一轮：它只在「启用插值」变的时候重算，自己被改的时候
    /// 不重算，于是选完显示同步，底下还写着「此刻生效：音频同步」。
    /// </para>
    /// </summary>
    internal static (bool Ok, string Detail) Probe()
    {
        var saves = 0;
        var picked = "";
        SettingChoiceRow? row = null;

        var choices = new List<SettingChoice>
        {
            new("甲", () => picked = "甲"),
            new("乙", () => picked = "乙")
        };

        row = new SettingChoiceRow(
            "探针", "此刻生效：甲", choices, choices[0], () => saves++,
            () => row!.Restate($"此刻生效：{picked}"));

        // 装值那一下不算用户改的：既不写盘，也不重写说明。
        var quiet = saves == 0 && row.Note == "此刻生效：甲" && picked.Length == 0;

        row.Selected = choices[1];

        var applied = picked == "乙" && saves == 1;
        var restated = row.Note == "此刻生效：乙";

        return (quiet && applied && restated,
            $"假下拉行：装值时{(quiet ? "不写盘也不改说明" : "就写盘或者改了说明")}、"
                + $"选一项后{(applied ? "写盘一次" : $"写盘 {saves} 次")}、"
                + $"说明{(restated ? "跟着改成了" : "没跟上，还是")}「{row.Note}」");
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
/// because WinUI otherwise reports fractions of one; the value rides beside the track as
/// <see cref="ValueLabel"/>, because a dragged thumb says nothing about the number it left — and on rows
/// like 描边大小 a tenth of a unit is the difference the user is dragging for.
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

    /// <summary>
    /// The number beside the track. Two decimals cover every slider built so far — the mpv-unit rows
    /// (描边 0.5、mpv 自己的 1.65) and the percents — without trailing zeros on the integer ones.
    /// </summary>
    public string ValueLabel => Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    partial void OnValueChanged(double value)
    {
        if (!_seeded) return;
        OnPropertyChanged(nameof(ValueLabel));
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
/// tidies its separators, a language list is canonicalised by the catalogue, and a pasted path loses its
/// quotes. Showing the typed text in those cases would claim something was saved that was not.
/// </para>
/// </summary>
public sealed partial class SettingTextRow : SettingRow
{
    private readonly Func<string, string> _commit;
    private readonly Action _save;
    private readonly bool _seeded;
    private bool _committing;

    internal SettingTextRow(string label, string? note, string placeholder, string value, Func<string, string> commit, Action save)
        : base(label, note)
    {
        Placeholder = placeholder;
        Text = value;
        _commit = commit;
        _save = save;
        _seeded = true;
    }

    public string Placeholder { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

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
    }
}

/// <summary>
/// A colour bound to one setting, as an HTML 颜色代码 — 「字幕颜色改为使用HTML颜色代码」（2026-09-06）.
/// The stored value is <c>#RRGGBB</c>, or the empty string for 「不设置，跟随 mpv 自己的默认」; the
/// picking itself is the <see cref="Views.HtmlColorPicker"/> the row's swatch opens, which writes this
/// property through a two-way binding only on a real commit (a pointer release, a number box, a typed
/// code) — a drag across the saturation square must not save the settings file forty times on the way.
/// <para>
/// The swatch and the code beside it are drawn from <see cref="Color"/> here rather than held by the
/// picker, because they are the row's value and have to be right even while the picker is closed.
/// </para>
/// </summary>
public sealed partial class SettingColorRow : SettingRow
{
    private readonly Action<string> _write;
    private readonly Action _save;
    private readonly bool _seeded;

    internal SettingColorRow(string label, string? note, string value, Action<string> write, Action save)
        : base(label, note)
    {
        Color = value;
        _write = write;
        _save = save;
        _seeded = true;
    }

    /// <summary><c>#RRGGBB</c>, or the empty string for 「不设置」.</summary>
    [ObservableProperty]
    public partial string Color { get; set; }

    /// <summary>The swatch: the colour itself, or null when 「不设置」 is what the row says.</summary>
    public Brush? Swatch
    {
        get
        {
            if (!HtmlColor.TryParse(Color, out var rgb)) return null;
            var (r, g, b) = HtmlColor.Rgb(rgb);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
        }
    }

    /// <summary>The code beside the swatch, or what 「不设置」 looks like when there is none.</summary>
    public string HexLabel => HtmlColor.TryParse(Color, out _) ? Color.ToUpperInvariant() : "不设置";

    partial void OnColorChanged(string value)
    {
        if (!_seeded) return;

        OnPropertyChanged(nameof(Swatch));
        OnPropertyChanged(nameof(HexLabel));

        _write(value);
        _save();
    }

    /// <summary>
    /// 自检：选一个颜色写盘一次、清除也写盘一次，色块跟着走 —— 就地造一个假的颜色行拨两下，不碰设置
    /// 文件、也不碰屏上那一页（同 <see cref="SettingChoiceRow.Probe"/> 的做法）。
    /// <para>
    /// 这一条非有不可，理由和下拉行那一条一样：单元测试进不到外壳这个程序集，而这台机器上注不进鼠标事件，
    /// 所以「点开色块、拖一块颜色」这一下没法自动做一遍。坏法是看不见的那种：写盘的那根线没接上，屏上色块
    /// 照样跟着拾色器变，只有设置文件还停在旧值上。
    /// </para>
    /// </summary>
    internal static (bool Ok, string Detail) Probe()
    {
        var writes = 0;
        var written = "";
        var row = new SettingColorRow("探针", null, "#AC5D5D", value => { writes++; written = value; }, () => { });

        var seeded = writes == 0 && row.Color == "#AC5D5D" && row.Swatch is not null;
        row.Color = "#00FF00";
        var picked = writes == 1 && written == "#00FF00" && row.HexLabel == "#00FF00";
        row.Color = "";
        var cleared = writes == 2 && written.Length == 0 && row.Swatch is null && row.HexLabel == "不设置";

        return (seeded && picked && cleared,
            $"假颜色行：装值{(seeded ? "不写盘、色块画得出来" : "就写盘或者画不出色块")}、"
                + $"选一个颜色{(picked ? "写盘一次" : $"写盘 {writes} 次")}、"
                + $"清掉{(cleared ? "也写盘、色块退成「不设置」" : "没写盘或者色块没跟上")}");
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
{    private readonly Action? _act;
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
/// 主页版面那一行：一张可以换次序、每一项自己带一个勾的表 —— 「新增页里拖拽决定这些列表的顺序，勾选显示
/// 或者不勾选取消显示」。
/// <para>
/// 和别的行不一样，它管的不是一个开关而是一份有序清单，所以读写那一对换成了「拿到这一份」和「这一份变了」：换过
/// 一次次序、或者点过一个勾，都当场写回设置并喊一声（见 <see cref="SettingsViewModel"/> 里造它的那一段）。
/// </para>
/// <para>
/// 表里那几项是 <see cref="HomeRowChoice"/>，顺序就是集合自己的顺序 —— <c>ListView</c> 拖动时改的正是这个集合，
/// 所以「屏上的次序」和「要存的次序」是同一件东西，不用在两处之间对齐。
/// </para>
/// <para>
/// 换次序有两条路：按住往上下拖，或者按那一行右边的两颗箭头（<see cref="HomeRowChoice.UpCommand"/>）。两颗箭头
/// 不是替代品，是这件事唯一验得到的形式 —— 这台机器上注不进鼠标事件（见 CLAUDE.md），拖那一下没法自动做一遍，
/// 而一张拖不动的表在屏上和拖得动的长得一模一样。箭头这条路由 <see cref="Probe"/> 钉着，顺带也给了不使指针的人
/// 一条路。
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

        foreach (var row in rows) Adopt(row);

        Rows.CollectionChanged += (_, args) =>
        {
            OnPropertyChanged(nameof(ListHeight));
            MarkEnds();

            // 拖一次是两下：先把那一排摘掉，再插到新位置上（ListView 换位走的是 Remove ＋ Add，不是 Move）。
            // 摘掉那一下不写盘 —— 那一刻表里少一排，写出去主页就照少一排重排一遍，被拖的那一排在屏上闪一下
            // 不见了，紧接着插回来的那一下又重排一遍。插回去那一下才是最终次序。
            // 箭头那条路走的是 Move，一下就是一下（见 Shift），所以它落在下面那句上。
            if (args.Action is NotifyCollectionChangedAction.Remove) return;

            Save();
        };

        MarkEnds();
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
            foreach (var row in rows) Adopt(row);
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

    /// <summary>把一项收进表里，接上它那两根线：勾变了要写盘，箭头按了要换位。</summary>
    private void Adopt(HomeRowChoice row)
    {
        row.Changed = Save;
        row.Shift = Shift;
        Rows.Add(row);
    }

    /// <summary>
    /// 把一项往上或者往下挪一格 —— 那两颗箭头按的就是这个。
    /// <para>
    /// 走 <c>Move</c> 而不是自己摘了再插：那是一次集合变动、一次写盘，摘＋插是两次（见上头造它时那段说明）。
    /// 到顶或者到底时什么都不做 —— 那时那颗按钮本来就是灰的，这一句防的是键盘和读屏那条路。
    /// </para>
    /// </summary>
    private void Shift(HomeRowChoice row, int delta)
    {
        var from = Rows.IndexOf(row);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Rows.Count) return;

        Rows.Move(from, to);
    }

    /// <summary>头一排的「上移」和末一排的「下移」置灰 —— 按下去也不动的按钮不该是亮的。</summary>
    private void MarkEnds()
    {
        for (var index = 0; index < Rows.Count; index++)
        {
            Rows[index].CanUp = index > 0;
            Rows[index].CanDown = index < Rows.Count - 1;
        }
    }

    /// <summary>
    /// 自检：那两颗箭头真换得了次序。就地造一张三行的假表按几下，不碰设置文件、也不碰屏上那一张（同 HomeBanner
    /// 那颗探针的做法）。
    /// <para>
    /// 这一条非有不可：单元测试进不到外壳这个程序集，而这台机器上注不进鼠标事件，拖那一下没法自动做一遍。少了它，
    /// 「换次序」就只剩一张截图能说明，而拖得动和拖不动的表拍出来一模一样。
    /// </para>
    /// <para>
    /// 数写盘次数是这一条的一半：一次换位只能写一遍盘 —— 写两遍主页就重排两遍，被挪的那一排在屏上闪一下。
    /// </para>
    /// </summary>
    internal static (bool Ok, string Detail) Probe()
    {
        var saves = 0;
        var table = new SettingHomeLayoutRow(
            "探针",
            null,
            [new HomeRowChoice("a", "甲", true), new HomeRowChoice("b", "乙", true), new HomeRowChoice("c", "丙", true)],
            _ => saves++);

        string Order() => string.Concat(table.Rows.Select(row => row.Key));

        var ends = !table.Rows[0].CanUp && table.Rows[0].CanDown
            && table.Rows[2].CanUp && !table.Rows[2].CanDown;

        table.Rows[1].DownCommand.Execute(null);
        var down = Order() == "acb" && saves == 1;

        table.Rows[2].UpCommand.Execute(null);
        var up = Order() == "abc" && saves == 2;

        table.Rows[0].UpCommand.Execute(null);
        var stop = Order() == "abc" && saves == 2;

        return (ends && down && up && stop,
            $"三行假表：两头{(ends ? "置灰" : "没置灰")}、下移{(down ? "换得动" : "没换动")}、"
                + $"上移{(up ? "换得回" : "没换回")}、到顶再上移{(stop ? "不动也不写盘" : "动了或者写了盘")}"
                + $"，末了 {Order()}、写盘 {saves} 次");
    }
}

/// <summary>
/// 主页版面表里的一项：屏上那句标题、认它的那把钥匙、勾了没有，加上换次序那两颗箭头。
/// <para>
/// 勾变了就地喊回去（<see cref="Changed"/>），因为 <c>CheckBox</c> 改的是这一项而不是那张表，集合自己的
/// <c>CollectionChanged</c> 听不见。箭头反过来 —— 它要动的是整张表的次序，所以那一下交回给表去做
/// （<see cref="Shift"/>），写盘还是走集合那条路，和拖一下走的是同一段。
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

    /// <summary>按了箭头往哪边挪一格；同上，由那张表挂上。</summary>
    internal Action<HomeRowChoice, int>? Shift { get; set; }

    [ObservableProperty]
    public partial bool Visible { get; set; }

    /// <summary>那两颗箭头亮不亮：头一排不能再往上，末一排不能再往下。由那张表在次序变动后重算。</summary>
    [ObservableProperty]
    public partial bool CanUp { get; set; }

    /// <inheritdoc cref="CanUp"/>
    [ObservableProperty]
    public partial bool CanDown { get; set; }

    partial void OnVisibleChanged(bool value) => Changed?.Invoke();

    [RelayCommand]
    private void Up() => Shift?.Invoke(this, -1);

    [RelayCommand]
    private void Down() => Shift?.Invoke(this, 1);

    /// <summary>读屏的人听到的那一句，也是拖动时那块浮起来的东西的名字。</summary>
    public override string ToString() => Title;
}

/// <summary>
/// 字幕示例预览里的一层字：一份示例文字的拷贝，画在自己的偏移上。阴影、描边那八份、正文本身都是一层 ——
/// 屏上没有「给文字描边」这一回事，预览的描边就是这几份拷贝叠出来的，所以它们对模板是同一种东西。
/// </summary>
public sealed record PreviewLayer(
    double X,
    double Y,
    Brush Brush,
    string Text,
    FontFamily Family,
    double Size,
    FontWeight Weight);

/// <summary>
/// 字幕卡顶上那条「字幕示例」：照 字幕外观 各行的当前值画出的一条样字 —— 「参考图2新增字幕外观功能」
/// （2026-09-06）。它不写任何设置，是这一页上唯一只画不写的行，存在的理由和别的行相反：别的行说的是
/// 「这一项是什么」，它说的是「这十几项合在一起是什么样子」，而那件事在按下播放之前没有任何一处能看见。
/// <para>
/// 数字到像素的换算全是 <see cref="SubtitlePreviewPlan.Plan"/> 的事（Core，单测钉着）；这里只管三件事：
/// 装着 <see cref="Settings.Playback"/> 这一份活的设置对象，<see cref="Refresh"/> 时重算一遍模型，再把
/// 模型翻译成画刷和字体对象给模板绑。翻译在行上而不是在模型上，因为 Core 没有画刷这种东西；每次刷新
/// 重建那几支画刷而不是复用，理由是改一行外观才刷一次，省不到哪里去，而「哪支画刷是旧的」这种账不用记。
/// </para>
/// <para>
/// 刷新的线只有一条：<see cref="SettingsViewModel.Live{T}"/> —— 外观每一行的写入口都从它过，所以它喊
/// 一声 <see cref="Refresh"/>，预览就不可能停在旧样子上。底下「外观应用范围」那半句（ASS/SSA 吃不到这些
/// 外观，除非选了强制）是行自己的说明的事，预览永远画配置的样子：它要照的是这张卡，不是某一条字幕。
/// </para>
/// </summary>
public sealed partial class SettingSubtitlePreviewRow : SettingRow
{
    private readonly PlaybackSettings _subtitles;
    private FontFamily? _family;

    /// <summary>
    /// 预览的放大倍数：一个 mpv 单位画成几个像素。mpv 的字号跟着片源分辨率走，预览没有片源，所以这里
    /// 定一个让出厂字号 50 在 91 高的条里像那张参考图一样站得满的数 —— 这是示意的比例，不是屏幕上的
    /// 比例，行说明里写着这一句。
    /// </summary>
    internal const double Scale = 1.3;

    /// <summary>预览画的字。放在行上而不是 Core，因为它是给屏幕看的词。逗号跟着站：示例连标点一起照，外观对逗号也是描边加阴影的一层。</summary>
    public const string Sample = "字幕示例，";

    internal SettingSubtitlePreviewRow(string label, string? note, PlaybackSettings subtitles)
        : base(label, note)
    {
        _subtitles = subtitles;
        Refresh();
    }

    /// <summary>换算好的样子。整体替换、不逐项改，重画一次就是一次换新。</summary>
    [ObservableProperty]
    public partial SubtitlePreviewPlan Model { get; set; }

    /// <summary>重画。外观任一行写完设置后由 <see cref="SettingsViewModel.Live{T}"/> 喊。</summary>
    internal void Refresh()
    {
        _family = null;
        Model = SubtitlePreviewPlan.Plan(_subtitles, Scale, Sample);
    }

    partial void OnModelChanged(SubtitlePreviewPlan value)
    {
        OnPropertyChanged(nameof(StripHeight));
        OnPropertyChanged(nameof(FontFamilyValue));
        OnPropertyChanged(nameof(PlateBrush));
        OnPropertyChanged(nameof(Layers));
    }

    /// <summary>那条的高：跟着字号走，字大条也大 —— 撑出一条 208 像素的预览比把 160 号的字削头去脚诚实。</summary>
    public double StripHeight => Model.FontSize + 26;

    public FontFamily FontFamilyValue => _family ??= new FontFamily(Model.FontFamily);

    /// <summary>底板那块色，没有底板时是空 —— <c>Background</c> 对 null 的回答就是「不画」。</summary>
    public Brush? PlateBrush => Model.Plate ? BrushFor(Model.PlateColor, Model.PlateOpacity) : null;

    /// <summary>
    /// 要叠的几层字，先画的在底下：阴影、描边那一圈，最后是正文本身 —— 所以模板只需要一个叠着画的
    /// <c>ItemsControl</c>，谁在上谁在下就是这份清单的次序，没有第二处要知道这件事。
    /// </summary>
    public IReadOnlyList<PreviewLayer> Layers =>
    [
        .. Model.Layers.Select(layer => new PreviewLayer(
            layer.X, layer.Y, BrushFor(layer.Color, layer.Opacity),
            Model.Text, FontFamilyValue, Model.FontSize, Weight(Model.Bold))),
        new(0, 0, BrushFor(Model.TextColor, 1), Model.Text, FontFamilyValue, Model.FontSize, Weight(Model.Bold))
    ];

    private static Brush BrushFor(string color, double opacity)
    {
        // 模型里的颜色要么来自设置文件（归一化过的 #RRGGBB），要么是写明的 mpv 兜底，走到解析不动这一步
        // 说明上面有人改了规则 —— 画成透明而不是抛，预览坏的样子应该是「少一层」而不是「整页崩」。
        if (!Infrastructure.HtmlColor.TryParse(color, out var rgb))
            return new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        var (r, g, b) = Infrastructure.HtmlColor.Rgb(rgb);
        return new SolidColorBrush(Windows.UI.Color.FromArgb((byte)Math.Round(opacity * 255), r, g, b));
    }

    private static FontWeight Weight(bool bold) => new() { Weight = (ushort)(bold ? 700 : 400) };

    /// <summary>
    /// 自检：改设置 → 重画这根线通不通。就地造一个假行、拿着一份假设置拨三下（同
    /// <see cref="SettingChoiceRow.Probe"/> 的做法），不碰设置文件、也不碰屏上那一页。坏法是看不见的那种：
    /// 模型换好了而屏上没跟上，或者反过来 —— 无论哪种，怎么调外观那条示例都是一个样子。
    /// </summary>
    internal static (bool Ok, string Detail) Probe()
    {
        var subtitles = new PlaybackSettings
        {
            SubtitleBold = true,
            SubtitleColor = "#AC5D5D",
            SubtitleBorderSize = "3",
            SubtitleBorderColor = "#000000",
            SubtitleShadowOffset = "1",
            SubtitleBackColor = "#123456",
            SubtitleBackOpacity = 40
        };

        var row = new SettingSubtitlePreviewRow("探针", null, subtitles);
        var drawn = row.Model;

        // 出厂那张：描边 3 换成 3.9 像素的一圈八份，阴影 1 换成 1.3 像素的一份，底板关着 —— 共九层。
        var initial = drawn.Bold && drawn.TextColor == "#AC5D5D" && !drawn.Plate
            && drawn.Layers.Count == 9
            && Math.Abs(drawn.Layers[0].X - 1.3) < 0.001 && drawn.Layers[0].Color == "#123456"
            && drawn.Layers[0].Opacity > 0.39 && drawn.Layers[0].Opacity < 0.41
            && Math.Abs(drawn.Layers[1].X - 3.9) < 0.001 && Math.Abs(drawn.Layers[1].Y) < 0.001
            && drawn.Layers[1].Color == "#000000";

        // 整行不透明方框：底板按 1 画，阴影收掉，剩一圈描边 —— 八层。
        subtitles.SubtitleBackStyle = "opaque-box";
        row.Refresh();
        var opaque = row.Model.Plate && row.Model.PlateOpacity == 1 && row.Model.Layers.Count == 8;

        // 描边关掉：什么都不剩，只剩底板那一块。
        subtitles.SubtitleBorderSize = "0";
        row.Refresh();
        var bare = row.Model.Plate && row.Model.Layers.Count == 0;

        return (initial && opaque && bare,
            $"假行：出厂样式{(initial ? "画出阴影加一圈八份的描边" : "没按设置画（" + drawn.Layers.Count + " 层）")}、"
                + $"整行方框{(opaque ? "按不透明画且阴影收掉" : "没跟上")}、"
                + $"描边关掉后{(bare ? "只剩底板" : "还剩东西")}");
    }
}
