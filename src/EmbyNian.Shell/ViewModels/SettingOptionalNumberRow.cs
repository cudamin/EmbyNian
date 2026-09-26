using CommunityToolkit.Mvvm.ComponentModel;
using EmbyNian.Mpv;

namespace EmbyNian.Shell.ViewModels;

/// <summary>An automatic value with an optional, bounded manual override.</summary>
public sealed partial class SettingOptionalNumberRow : SettingRow
{
    private readonly Action<double?> _write;
    private readonly Action _save;
    private readonly bool _seeded;
    private bool _committing;
    private double _lastValue;

    internal SettingOptionalNumberRow(string label, string note, double minimum, double maximum,
        double fallback, double? value, Action<double?> write, Action save) : base(label, note)
    {
        Minimum = minimum;
        Maximum = maximum;
        Automatic = value is null;
        Value = HdrOptions.Clamp(value ?? fallback, minimum, maximum) ?? minimum;
        _lastValue = Value;
        _write = write;
        _save = save;
        _seeded = true;
    }

    public double Minimum { get; }
    public double Maximum { get; }
    public bool InputEnabled => !Automatic;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputEnabled))]
    public partial bool Automatic { get; set; }

    [ObservableProperty]
    public partial double Value { get; set; }

    partial void OnAutomaticChanged(bool value)
    {
        if (!_seeded) return;
        _write(value ? null : _lastValue);
        _save();
    }

    partial void OnValueChanged(double value)
    {
        if (!_seeded || _committing) return;
        var bounded = HdrOptions.Clamp(value, Minimum, Maximum);
        _committing = true;
        try
        {
            Value = bounded ?? _lastValue;
            _lastValue = Value;
        }
        finally { _committing = false; }
        if (Automatic || bounded is null) return;
        _write(Value);
        _save();
    }

    internal static (bool Ok, string Detail) Probe()
    {
        double? stored = null;
        var saves = 0;
        var row = new SettingOptionalNumberRow("HDR 峰值", "", 10, 10000, 1000, null,
            value => stored = value, () => saves++);
        var seeded = saves == 0 && row.Automatic && !row.InputEnabled;
        row.Automatic = false;
        var manual = stored == 1000 && row.InputEnabled;
        row.Value = 12000;
        var clamped = stored == 10000 && row.Value == 10000;
        row.Value = double.NaN;
        var valid = stored == 10000 && row.Value == 10000;
        row.Automatic = true;
        return (seeded && manual && clamped && valid && stored is null && saves == 3,
            $"自动不写盘={seeded}、手动输入={manual}、上限校验={clamped}、无效值保留={valid}、可恢复自动={stored is null}");
    }
}
