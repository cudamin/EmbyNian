using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Diagnostics;
using EmbyNian.Mpv;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// The 配置文件 card's editor: which of the two external mpv config files is open, its text, what state it
/// is in, and the three things that can be done to it.
/// <para>
/// A draft is kept per file — keyed by path rather than by which of the two entries in the combo selects it —
/// so neither switching between <c>mpv.conf</c> and <c>input.conf</c> nor re-aiming a path box throws away
/// unsaved typing in the file being left. The drafts themselves live in Core
/// (<see cref="MpvConfigDraft"/>); this type is the observable shell around them, which is what lets the
/// dirty-tracking, line counting and line-ending handling be tested without standing up a window.
/// </para>
/// </summary>
public sealed partial class SettingConfigEditorRow : SettingRow
{
    private const string Category = "mpv-config";

    private readonly MpvConfigLocation _location;

    /// <summary>
    /// One draft per file, keyed by normalised path.
    /// <para>
    /// By path and not by <see cref="MpvConfigKind"/>, because the two path boxes on this same card and the
    /// mpv.exe box on the card before it can all re-aim a kind at a different file at any moment. Keyed by
    /// kind, doing so replaced the draft for the file being left and took its unsaved text with it; keyed by
    /// path, that draft is still here if the box is aimed back at it.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, MpvConfigDraft> _drafts = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _seeded;

    /// <summary>True while the editor's text is being assigned rather than typed.</summary>
    private bool _showing;

    private MpvConfigKind _kind = MpvConfigKind.Mpv;

    /// <summary>
    /// The file whose text is in the box right now, normalised — or null when the box is empty because a
    /// load failed.
    /// <para>
    /// Tracked rather than re-derived from <see cref="_kind"/>, because those two disagree for exactly as
    /// long as it takes to notice that a path box moved, and everything that reads the box back into a draft
    /// has to mean the file the text came from and not the file that is selected now.
    /// </para>
    /// </summary>
    private string? _shown;

    internal SettingConfigEditorRow(MpvConfigLocation location)
        : base("配置文件内容", null)
    {
        _location = location;
        Kinds =
        [
            new SettingChoice("mpv.conf", () => Choose(MpvConfigKind.Mpv)),
            new SettingChoice("input.conf", () => Choose(MpvConfigKind.Input))
        ];

        SelectedKind = Kinds[0];
        _seeded = true;
        Load(forceDisk: false);
    }

    /// <summary>The two files that can be opened here.</summary>
    public IReadOnlyList<SettingChoice> Kinds { get; }

    /// <summary>
    /// Asked before an action would throw unsaved typing away; true to go ahead. Handed down by the view
    /// model from the page, because a dialog needs a <c>XamlRoot</c> and a row has no way to reach one. Null
    /// outside a window — in the tests and the self-check — where the old behaviour of discarding without
    /// asking is what runs; <see cref="ReloadAsync"/> spells that out rather than leaning on a default.
    /// </summary>
    internal ConfirmRequest? Confirm { get; set; }

    [ObservableProperty]
    public partial SettingChoice? SelectedKind { get; set; }

    [ObservableProperty]
    public partial string EditorText { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; set; } = "正在载入配置文件";

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    /// <summary>
    /// Re-reads the open file from disk. Asks first when that would discard typing: this button sits one
    /// misclick from 保存并备份, and unlike 撤销未保存修改 it is not the button anyone presses on purpose to
    /// throw an edit away.
    /// </summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        // Nobody to ask means go ahead, which is the opposite of what ConfirmAsync would answer and is
        // deliberate: what is at stake here is typing, not a deletion, and re-reading the file is what this
        // button did before it asked anything at all.
        if (Displayed() is { IsDirty: true } draft && Confirm is { } ask)
        {
            var name = System.IO.Path.GetFileName(draft.Path);
            if (!await ask("重新载入", $"{name} 有未保存的修改，重新载入会把它们丢掉。", "继续")) return;
        }

        Load(forceDisk: true);
    }

    /// <summary>Writes the open file out, keeping its encoding and making a backup first.</summary>
    [RelayCommand]
    private void Store()
    {
        if (Current() is not { } draft) return;

        if (!draft.CanSave)
        {
            // A file that was refused rather than read holds none of its own text, so writing this draft
            // out would replace that file with an empty one.
            Report(draft.Status, InfoBarSeverity.Error);
            return;
        }

        try
        {
            draft.Save();
            Show(draft);
        }
        catch (Exception error)
        {
            Log.Warn(Category, $"无法保存 {draft.Path}", error);
            Report($"保存失败：{error.Message}", InfoBarSeverity.Error);
        }
    }

    /// <summary>Throws away unsaved typing and goes back to what is on disk.</summary>
    [RelayCommand]
    private void Restore()
    {
        if (Current() is not { } draft) return;

        draft.Restore();
        Show(draft);
    }

    /// <summary>
    /// Called when one of the two path boxes was edited. Only the box for the file on screen can have moved
    /// it; the other one moves a file this editor is not showing.
    /// </summary>
    internal void PathChanged(MpvConfigKind kind)
    {
        if (kind == _kind) Relocate();
    }

    /// <summary>
    /// Called when a path this editor depends on was edited — either config path box, or the external
    /// <c>mpv.exe</c> box that both are inferred from when they are not set explicitly.
    /// <para>
    /// Compares where the selected file is now against what the box is showing, and does nothing when those
    /// are the same file. A text row commits on losing focus, so this also runs when a box is merely clicked
    /// out of, and re-reading the file for that would discard everything typed into the editor since.
    /// </para>
    /// <para>
    /// When the file really did move, the draft for the old one stays in <see cref="_drafts"/> with its
    /// unsaved text intact — aiming the box back at it brings that text back rather than re-reading disk.
    /// </para>
    /// </summary>
    internal void Relocate()
    {
        var key = MpvConfigLocation.Normalise(_location.Resolve(_kind));
        if (!string.Equals(_shown, key, StringComparison.OrdinalIgnoreCase)) Load(forceDisk: false);
    }

    private void Choose(MpvConfigKind kind)
    {
        if (kind == _kind) return;

        // Whatever is in the box belongs to the file being left, and has to be put back in its draft before
        // the box is overwritten — otherwise switching files is a way to silently lose an edit.
        Remember();
        _kind = kind;
        Load(forceDisk: false);
    }

    private void Remember()
    {
        if (Displayed() is { } draft) draft.Edit(EditorText);
    }

    /// <summary>The draft the box is showing, or null when a load failed and it is empty.</summary>
    private MpvConfigDraft? Displayed() =>
        _shown is not null && _drafts.TryGetValue(_shown, out var draft) ? draft : null;

    /// <summary>
    /// The draft for the file selected now, opening it first if this is the first look at that path. Null
    /// when it could not be opened, in which case the status line already says why.
    /// <para>
    /// What the two write commands go through, so neither can act on a draft belonging to a path that has
    /// since moved — which would be a save writing one file's text out under another file's name.
    /// </para>
    /// </summary>
    private MpvConfigDraft? Current()
    {
        var key = MpvConfigLocation.Normalise(_location.Resolve(_kind));
        if (_drafts.TryGetValue(key, out var draft)) return draft;

        Load(forceDisk: true);
        return _drafts.TryGetValue(key, out draft) ? draft : null;
    }

    private void Load(bool forceDisk)
    {
        var path = _location.Resolve(_kind);
        var key = MpvConfigLocation.Normalise(path);

        if (!forceDisk && _drafts.TryGetValue(key, out var cached))
        {
            Show(cached);
            return;
        }

        try
        {
            var draft = MpvConfigDraft.Open(path, _kind);
            _drafts[key] = draft;
            Show(draft);
        }
        catch (Exception error)
        {
            Log.Warn(Category, $"无法载入 {path}", error);

            // Drop the draft and empty the box. Leaving the previous file's text on screen under a message
            // about a different file is an invitation to press 保存并备份 and write the one into the other.
            _drafts.Remove(key);
            _shown = null;
            ShowText("");
            Report($"无法载入 {path}：{error.Message}", InfoBarSeverity.Error);
        }
    }

    private void Show(MpvConfigDraft draft)
    {
        _shown = MpvConfigLocation.Normalise(draft.Path);
        ShowText(draft.DraftText);
        Report(draft.Status, Severity(draft.State));
    }

    /// <summary>
    /// Puts text in the editor without it counting as typing. <see cref="OnEditorTextChanged"/> is what
    /// pushes typing into the draft, and it has to sit out an assignment made from the draft in the first
    /// place — the round trip through the <c>TextBox</c> would otherwise come back as an edit.
    /// </summary>
    private void ShowText(string text)
    {
        _showing = true;
        try { EditorText = text; }
        finally { _showing = false; }
    }

    private void Report(string status, InfoBarSeverity severity)
    {
        Status = status;
        StatusSeverity = severity;
    }

    partial void OnSelectedKindChanged(SettingChoice? value)
    {
        if (!_seeded || value is null) return;
        value.Apply();
    }

    partial void OnEditorTextChanged(string value)
    {
        if (!_seeded || _showing) return;
        if (Displayed() is not { } draft) return;

        draft.Edit(value);
        Report(draft.Status, Severity(draft.State));
    }

    private static InfoBarSeverity Severity(MpvConfigDraftState state) => state switch
    {
        MpvConfigDraftState.Dirty => InfoBarSeverity.Warning,
        MpvConfigDraftState.Saved => InfoBarSeverity.Success,
        MpvConfigDraftState.SavedWithoutBackup => InfoBarSeverity.Warning,
        MpvConfigDraftState.Missing => InfoBarSeverity.Warning,
        MpvConfigDraftState.Failed => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational
    };
}
