using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyNian.Emby;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.ViewModels;

public sealed partial class ManagedLibraryRow(EmbyVirtualFolder folder, EmbySessionScope scope) : ObservableObject
{
    public EmbyVirtualFolder Folder { get; } = folder;
    internal bool IsCurrent => scope.IsCurrent;
    public string Id => Folder.Id;
    public string Name => Folder.Name;
    public string TypeName => EmbyLibraryOptions.TypeName(Folder.ContentType);
    public string Locations => string.Join("\n", Folder.Paths.OfType<JsonObject>().Select(path => EmbyLibraryOptions.Text(path, "Path")));
    public string ScanStatus => EmbyLibraryOptions.Text(Folder.Document, "RefreshStatus") switch
    {
        "InProgress" => $"正在扫描 · {EmbyLibraryOptions.Number(Folder.Document, "RefreshProgress"):0}%",
        "Pending" => "等待扫描",
        _ => ""
    };
    [ObservableProperty] public partial BitmapImage? Image { get; set; }
}

public sealed record LibraryPathRow(JsonObject Document)
{
    public string Path => EmbyLibraryOptions.Text(Document, "Path");
    public string NetworkPath => EmbyLibraryOptions.Text(Document, "NetworkPath");
}

public sealed partial class LibraryFieldRow : ObservableObject
{
    private readonly Action<string> _write;
    private readonly JsonObject _document;
    private readonly Action _changed;
    public LibrarySetting Setting { get; }
    public string Key => Setting.Key;
    public string Label => Setting.Label;
    public string Note => Setting.Note;
    public double Minimum => Setting.Minimum;
    public double Maximum => Setting.Maximum;
    public ObservableCollection<LibraryChoice> Choices { get; } = [];
    public Visibility ToggleVisibility => Show(Setting.Kind == "toggle");
    public Visibility NumberVisibility => Show(Setting.Kind == "number");
    public Visibility TextVisibility => Show(Setting.Kind == "text");
    public Visibility ChoiceVisibility => Show(Setting.Kind is "choice" or "numberchoice" or "language" or "country");
    public Visibility LanguagesVisibility => Show(Setting.Kind == "languages");
    public Visibility NoteVisibility => Note.Length == 0 ? Visibility.Collapsed : SettingRow.NotesVisibility;
    public string LanguageSummary => string.Join("、", EmbyLibraryOptions.Strings(_document, Key).Select(value => Choices.FirstOrDefault(choice => choice.Value == value)?.Name ?? value)) is { Length: > 0 } text ? text : "选择语言";
    [ObservableProperty] public partial bool Checked { get; set; }
    [ObservableProperty] public partial double Number { get; set; }
    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial int SelectedIndex { get; set; }
    [ObservableProperty] public partial Visibility Visibility { get; set; } = Visibility.Visible;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorVisibility))]
    public partial string Error { get; set; } = "";
    public Visibility ErrorVisibility => Show(Error.Length > 0);
    public string[] SelectedLanguages => EmbyLibraryOptions.Strings(_document, Key);

    public LibraryFieldRow(LibrarySetting setting, JsonObject document, IEnumerable<LibraryChoice> choices, Action changed, Action<string>? write = null, string? value = null)
    {
        Setting = setting;
        _document = document;
        _changed = changed;
        _write = _ => { };
        foreach (var choice in choices) Choices.Add(choice);
        value ??= setting.Read(document);
        Checked = bool.TryParse(value, out var flag) && flag;
        Number = double.TryParse(value, CultureInfo.InvariantCulture, out var number) ? number : 0;
        Text = value;
        if (Choices.Count > 0 && Choices.All(choice => choice.Value != value) && setting.Kind != "languages") Choices.Add(new(value, value));
        SelectedIndex = Choices.ToList().FindIndex(choice => choice.Value == value);
        _write = write ?? (text => setting.Write(document, text));
    }

    private void Write(string value)
    {
        try { _write(value); Error = ""; }
        catch (Exception error) when (error is ArgumentException or OverflowException or FormatException) { Error = $"{Label}：请输入有效数值。"; }
    }
    partial void OnCheckedChanged(bool value) => Write(value.ToString(CultureInfo.InvariantCulture));
    partial void OnNumberChanged(double value) => Write(value.ToString(CultureInfo.InvariantCulture));
    partial void OnTextChanged(string value) => Write(value ?? "");
    partial void OnSelectedIndexChanged(int value) { if (value >= 0 && value < Choices.Count) Write(Choices[value].Value); }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Checked) or nameof(Number) or nameof(Text) or nameof(SelectedIndex)) _changed();
    }

    public void SetLanguages(IEnumerable<string> values)
    {
        _document[Key] = EmbyLibraryOptions.Array(values);
        OnPropertyChanged(nameof(LanguageSummary));
        _changed();
    }

    private static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class LibraryFieldSection(string name) : ObservableObject
{
    public string Name { get; } = name;
    public ObservableCollection<LibraryFieldRow> Fields { get; } = [];
    [ObservableProperty] public partial Visibility Visibility { get; set; } = Visibility.Visible;
}

public sealed partial class LibraryProviderRow : ObservableObject
{
    private readonly Action<bool> _write;
    public string Name { get; }
    public string SetupUrl { get; }
    public string Note { get; }
    public string[] Features { get; }
    public LibraryProviderGroup Group { get; }
    public Visibility SetupVisibility => string.IsNullOrEmpty(SetupUrl) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoteVisibility => Note.Length == 0 ? Visibility.Collapsed : SettingRow.NotesVisibility;
    [ObservableProperty] public partial bool Enabled { get; set; }

    internal LibraryProviderRow(JsonObject provider, bool enabled, LibraryProviderGroup group, Action<bool> write)
    {
        _write = _ => { };
        Group = group;
        Name = EmbyLibraryOptions.Text(provider, "Name");
        SetupUrl = EmbyLibraryOptions.Text(provider, "SetupUrl");
        Features = EmbyLibraryOptions.Strings(provider, "Features");
        Note = Features.Contains("RequiredSetup") ? "此提供者需要先配置。" : "";
        Enabled = enabled;
        _write = write;
    }
    partial void OnEnabledChanged(bool value) => _write(value);
}

public sealed class LibraryProviderGroup
{
    private readonly JsonObject _options;
    private readonly string _selectionKey;
    private readonly string? _orderKey;
    private readonly bool _disabledList;
    private readonly Action _changed;
    public string Name { get; }
    public string Kind { get; }
    public ObservableCollection<LibraryProviderRow> Providers { get; } = [];
    public Visibility OrderVisibility => _orderKey is null ? Visibility.Collapsed : Visibility.Visible;

    internal LibraryProviderGroup(string name, string kind, JsonArray available, JsonObject options, string selectionKey, string? orderKey, bool disabledList, Action changed)
    {
        Name = name; Kind = kind; _options = options; _selectionKey = selectionKey; _orderKey = orderKey; _disabledList = disabledList; _changed = changed;
        var order = orderKey is null ? [] : EmbyLibraryOptions.Strings(options, orderKey);
        var selected = EmbyLibraryOptions.Strings(options, selectionKey);
        foreach (var provider in available.OfType<JsonObject>().OrderBy(item => { var index = System.Array.IndexOf(order, EmbyLibraryOptions.Text(item, "Name")); return index < 0 ? int.MaxValue : index; }))
        {
            var providerName = EmbyLibraryOptions.Text(provider, "Name");
            Providers.Add(new(provider, disabledList != selected.Contains(providerName), this, enabled =>
            {
                var values = EmbyLibraryOptions.Strings(_options, _selectionKey).ToList();
                values.RemoveAll(value => value == providerName);
                if (enabled != _disabledList) values.Add(providerName);
                _options[_selectionKey] = EmbyLibraryOptions.Array(values);
                _changed();
            }));
        }
    }

    public void Move(LibraryProviderRow row, int direction)
    {
        var index = Providers.IndexOf(row);
        if (_orderKey is null || index < 0 || index + direction < 0 || index + direction >= Providers.Count) return;
        Providers.Move(index, index + direction);
        _options[_orderKey] = EmbyLibraryOptions.Array(Providers.Select(provider => provider.Name)
            .Concat(EmbyLibraryOptions.Strings(_options, _orderKey).Where(name => Providers.All(provider => provider.Name != name))));
        _changed();
    }
}
