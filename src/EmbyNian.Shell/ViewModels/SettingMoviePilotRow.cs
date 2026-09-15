using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Configuration;
using Microsoft.UI.Xaml;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// The MoviePilot card's working state: the switch, the two boxes, the button, and whatever the last test
/// said.
/// <para>
/// Unlike every other row on this page this one holds several controls that belong to one question, which is
/// why it is a single row rather than four. Four separate rows would each write the settings file on their
/// own and the button would have to live in a fifth — 「测试连接」 needs the address and the password that
/// were typed, and a button does not know what the boxes beside it contain unless something owns them all.
/// The <see cref="SettingHomeLayoutRow"/> precedent applies: when a setting is really one co-ordinated thing,
/// one row that owns the whole thing beats several rows that have to agree.
/// </para>
/// <para>
/// It writes through delegates handed in by the view model rather than touching settings itself, the same
/// read/write-pair shape every other row uses — a renamed setting stays a build error.
/// </para>
/// </summary>
public sealed partial class SettingMoviePilotRow : SettingRow
{
    private readonly Action<bool> _writeEnabled;
    private readonly Action<string> _writeUrl;
    private readonly Action<string> _writeUsername;
    private readonly Action<string> _writePassword;
    private readonly Action _save;
    private readonly Func<CancellationToken, Task<string>> _test;

    /// <summary>
    /// Set last in the constructor, after the seeded fields are in place, so handing the row its starting
    /// values does not write them straight back to the settings file. Same flag, same reason, as
    /// <see cref="SettingChoiceRow"/>'s.
    /// </summary>
    private readonly bool _seeded;

    /// <summary>True while a test is in flight, so the button disables itself instead of queueing calls.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestLabel))]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    public partial bool Testing { get; set; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial string Url { get; set; }

    [ObservableProperty]
    public partial string Username { get; set; }

    /// <summary>
    /// The password as typed. Never read back out of the settings file to fill this in — the stored form is
    /// wrapped and the point of a password box is that the text is not on screen. The card says whether one
    /// is saved instead, which is the fact the user actually needs.
    /// </summary>
    [ObservableProperty]
    public partial string Password { get; set; }

    /// <summary>The last test's verdict, already written for a person to read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultVisibility))]
    public partial string Result { get; set; }

    /// <summary>Whether the last test succeeded, which only decides how the verdict is coloured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultBrush))]
    public partial bool ResultOk { get; set; }

    /// <summary>
    /// Told by the view model after it wraps a password, because the wrap is what decides this and the row
    /// never touches the settings object itself.
    /// </summary>
    internal void MarkPasswordSaved(bool saved) => HasSavedPassword = saved;

    /// <summary>
    /// True when a wrapped password is on file. Shown as 「已保存」 beside the box, because an empty password
    /// box is otherwise ambiguous between 「none saved」 and 「I cleared it」.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SavedPasswordVisibility))]
    public partial bool HasSavedPassword { get; set; }

    public SettingMoviePilotRow(
        string label,
        string note,
        bool enabled,
        string url,
        string username,
        bool hasSavedPassword,
        Action<bool> writeEnabled,
        Action<string> writeUrl,
        Action<string> writeUsername,
        Action<string> writePassword,
        Action save,
        Func<CancellationToken, Task<string>> test)
        : base(label, note)
    {
        Enabled = enabled;
        Url = url;
        Username = username;
        Password = "";
        HasSavedPassword = hasSavedPassword;
        Result = "";
        _writeEnabled = writeEnabled;
        _writeUrl = writeUrl;
        _writeUsername = writeUsername;
        _writePassword = writePassword;
        _save = save;
        _test = test;
        _seeded = true;
    }

    /// <summary>What the button says. While a test runs it is the progress, because a button that still says
    /// 「测试连接」 with a spinner somewhere else is a button the user presses twice.</summary>
    public string TestLabel => Testing ? "正在测试…" : "测试连接";

    /// <summary>
    /// The button greys out while a test is in flight. A property rather than a converter, because the page
    /// has no inverse-bool converter and inventing one to save four lines is not a trade this page makes.
    /// </summary>
    public bool CanTest => !Testing;

    /// <summary>Whether the 「已保存一个密码」 line shows, as the <c>Visibility</c> a template can bind to.</summary>
    public Visibility SavedPasswordVisibility => HasSavedPassword ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ResultVisibility => Result.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// The verdict's colour. Two brushes chosen here rather than a converter, which is the rule this page
    /// follows everywhere else — see <see cref="SettingSubtitlePreviewRow.BrushFor"/>.
    /// </summary>
    public Microsoft.UI.Xaml.Media.Brush ResultBrush => ResultOk
        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x5D, 0xC4, 0x8A))
        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0x6C, 0x75));

    partial void OnEnabledChanged(bool value)
    {
        if (!_seeded) return;
        _writeEnabled(value);
        _save();
    }

    partial void OnUrlChanged(string value)
    {
        if (!_seeded) return;
        _writeUrl(value);
        _save();
    }

    partial void OnUsernameChanged(string value)
    {
        if (!_seeded) return;
        _writeUsername(value);
        _save();
    }

    partial void OnPasswordChanged(string value)
    {
        if (!_seeded) return;
        _writePassword(value);
        _save();

        // Typing a password makes 「已保存」 true a moment later, when the wrap lands — the row is told by
        // the view model rather than guessing, because only the view model knows the wrap succeeded.
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (Testing) return;

        Testing = true;
        ResultOk = false;
        Result = "";

        try
        {
            Result = await _test(CancellationToken.None).ConfigureAwait(true);
            ResultOk = true;
        }
        catch (Exception error)
        {
            // Every failure this can produce is already phrased for a person — the client's exceptions
            // carry the server's own message. A crash here would be a bug; an error message would not.
            Result = error.Message;
            ResultOk = false;
        }
        finally
        {
            Testing = false;
        }
    }
}
