using System.Text;
using EmbyNian.Configuration;

namespace EmbyNian.Mpv;

/// <summary>
/// Where the two external mpv config files are, and what a typed-in path means.
/// <para>
/// Split out of the settings page so the rules are checkable without a UI: an empty box means 「infer it」,
/// and so does a box holding exactly the path that would have been inferred anyway — storing that would
/// freeze today's guess, and the guess is supposed to follow <see cref="MpvSettings.ExecutablePath"/> when
/// that changes.
/// </para>
/// </summary>
public sealed class MpvConfigLocation(MpvSettings settings)
{
    /// <summary>The path to use for <paramref name="kind"/>: what was configured, or the inferred one.</summary>
    public string Resolve(MpvConfigKind kind)
    {
        var configured = Clean(kind == MpvConfigKind.Mpv ? settings.ConfigPath : settings.InputConfigPath);
        return configured.Length == 0 ? Infer(kind) : configured;
    }

    /// <summary>
    /// Where the file would be for an unconfigured path: <c>portable_config</c> next to the external
    /// <c>mpv.exe</c>. Falls back to the app directory when the executable is unset or unusable, so this
    /// never throws and never returns an empty string.
    /// </summary>
    public string Infer(MpvConfigKind kind)
    {
        var name = kind == MpvConfigKind.Mpv ? "mpv.conf" : "input.conf";
        var executable = Clean(settings.ExecutablePath);
        try
        {
            var root = executable.Length == 0
                ? AppContext.BaseDirectory
                : Path.GetDirectoryName(Path.GetFullPath(executable)) ?? AppContext.BaseDirectory;
            return Path.Combine(root, "portable_config", name);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Path.Combine(AppContext.BaseDirectory, "portable_config", name);
        }
    }

    /// <summary>
    /// Records what the user typed, and returns the path to show afterwards. A blank entry and an entry
    /// equal to the inferred path both store nothing, which is why the caller has to redisplay the result
    /// rather than the input: both of those come back as the inferred path.
    /// </summary>
    public string Store(MpvConfigKind kind, string typed)
    {
        var value = Clean(typed);
        var stored = value.Length == 0 || SamePath(value, Infer(kind)) ? null : value;
        if (kind == MpvConfigKind.Mpv) settings.ConfigPath = stored;
        else settings.InputConfigPath = stored;
        return Resolve(kind);
    }

    /// <summary>
    /// Whitespace and one pair of surrounding double quotes off a path a person typed or pasted.
    /// <para>
    /// Explorer's 「复制为路径」 puts the path on the clipboard already quoted, so pasting one in is the
    /// ordinary way to fill a path box — and a double quote cannot appear in a Windows path, so a value
    /// wearing them can only have come from that. Left in, they are stored verbatim and every later
    /// <see cref="Path.GetFullPath(string)"/> throws on it, which is a box that has permanently stopped
    /// finding its file. Applied on the way in and again on the way out, so a value a previous build stored
    /// with its quotes still on heals the first time it is read.
    /// </para>
    /// </summary>
    public static string Clean(string? path)
    {
        var value = (path ?? "").Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Trim() : value;
    }

    /// <summary>
    /// A path in the form two of them are compared in: cleaned, then made absolute, so the same file named
    /// two different ways compares equal.
    /// <para>
    /// Total by design. A path malformed enough that <see cref="Path.GetFullPath(string)"/> refuses it comes
    /// back as itself, because the user is entitled to type nonsense into the box and the complaint belongs
    /// to whoever tries to open the file, not to whoever is comparing two names.
    /// </para>
    /// </summary>
    public static string Normalise(string path)
    {
        var value = Clean(path);
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value;
        }
    }

    /// <summary>Whether two paths name the same file, comparing full paths and ignoring case.</summary>
    public static bool SamePath(string left, string right) =>
        string.Equals(Normalise(left), Normalise(right), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Line-break handling for text that has been through a <c>TextBox</c>.
/// <para>
/// WinUI's <c>TextBox.Text</c> hands back a bare CR for every line break no matter what was assigned to
/// it, so text that came out of a CRLF file does not compare equal to the text that went in. Left alone
/// that one detail produces three separate wrong answers: a file reports 「有未保存修改」 the instant it is
/// loaded, its line count collapses to 1, and saving rewrites every line ending in the file to a bare CR —
/// which is not what mpv writes, and breaks <see cref="MpvConfigDocument"/>'s promise that an unmodified
/// document round-trips byte for byte. So editor text is put back into the file's own convention before it
/// is compared, counted, or parsed.
/// </para>
/// </summary>
public static class MpvConfigText
{
    /// <summary>
    /// Rewrites every CR, LF and CRLF in <paramref name="text"/> as <paramref name="newline"/>.
    /// <para>
    /// Deliberately not <see cref="string.ReplaceLineEndings(string)"/>: that also treats FF, NEL, LS and
    /// PS as line breaks, so a config file containing one of those would come back from a trip through the
    /// editor with a line split that was never in it.
    /// </para>
    /// </summary>
    public static string ToNewline(string text, string newline)
    {
        if (text.Length == 0) return text;

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                builder.Append(newline);
            }
            else if (character == '\n')
            {
                builder.Append(newline);
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Counts lines the way <see cref="MpvConfigDocument.Lines"/> does, so the number in the status line
    /// does not jump when the text goes from 「same as disk」 to 「edited」. A trailing line break ends the
    /// last line instead of starting an empty one — which is also how an editor numbers lines — and every
    /// flavour of break counts, not just LF.
    /// </summary>
    public static int CountLines(string text)
    {
        if (text.Length == 0) return 0;

        var end = text.Length;
        if (text[^1] == '\n') end -= text.Length >= 2 && text[^2] == '\r' ? 2 : 1;
        else if (text[^1] == '\r') end -= 1;

        var count = 1;
        for (var i = 0; i < end; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < end && text[i + 1] == '\n') i++;
                count++;
            }
            else if (text[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>
/// One config file open in the editor: what is on disk, what the user has typed, and whether those differ.
/// <para>
/// A draft per file rather than one shared editor state, because switching between <c>mpv.conf</c> and
/// <c>input.conf</c> must not throw away unsaved typing in the one being left.
/// </para>
/// </summary>
public sealed class MpvConfigDraft
{
    /// <summary>
    /// The file's length and last-write time when this draft opened, or null when there was no file. Compared
    /// against the disk again at save time, so an edit made in another program in between is reported instead
    /// of vanishing.
    /// <para>
    /// Deliberately not a hash of the contents: this only has to be good enough to notice, and the version
    /// being overwritten goes into the backup either way — so a miss costs a sentence in the status line, not
    /// the other program's work.
    /// </para>
    /// </summary>
    private (long Length, DateTime WrittenUtc)? _stamp;

    /// <summary>
    /// False for a file that was refused rather than read. Such a draft holds none of the file's text, so
    /// letting it be edited or written out would replace that file with an empty one.
    /// </summary>
    private readonly bool _readable;

    private MpvConfigDraft(string path, MpvConfigDocument document, string status, MpvConfigDraftState state)
    {
        Path = path;
        Document = document;
        SavedText = document.Serialize();
        DraftText = SavedText;
        Status = status;
        State = state;
        _readable = state != MpvConfigDraftState.Failed;
        _stamp = Stamp(path);
    }

    /// <summary>The file this draft is for, already resolved.</summary>
    public string Path { get; }

    /// <summary>The document as last read from or written to disk.</summary>
    public MpvConfigDocument Document { get; private set; }

    /// <summary>The text that matches <see cref="Document"/>, in the file's own line endings.</summary>
    public string SavedText { get; private set; }

    /// <summary>What the user has typed, normalised to the file's line endings.</summary>
    public string DraftText { get; private set; }

    /// <summary>The message to show for the current state.</summary>
    public string Status { get; private set; }

    /// <summary>How to colour <see cref="Status"/>. The view maps this onto an <c>InfoBarSeverity</c>.</summary>
    public MpvConfigDraftState State { get; private set; }

    /// <summary>Whether the draft differs from what is on disk.</summary>
    public bool IsDirty => !string.Equals(DraftText, SavedText, StringComparison.Ordinal);

    /// <summary>
    /// Whether this draft may be edited and written back. False only for a file that was refused rather than
    /// read, which is a state the view has to check before offering to save.
    /// </summary>
    public bool CanSave => _readable;

    /// <summary>Lines in the draft, counted as <see cref="MpvConfigText.CountLines"/> counts them.</summary>
    public int LineCount => MpvConfigText.CountLines(DraftText);

    /// <summary>
    /// The most a config file may be and still be opened here. mpv's own run to a few tens of KB; this is a
    /// guard for a path box aimed at the wrong thing — a video, a disk image, the 38 MB libmpv DLL sitting in
    /// the same folder — where reading and decoding it on the UI thread is a frozen window rather than an
    /// error message.
    /// </summary>
    public const long SizeLimit = 4 * 1024 * 1024;

    /// <summary>
    /// Reads <paramref name="path"/>, or prepares an empty UTF-8 document when it is not there yet. Never
    /// throws for a missing or implausibly large file; a read that fails for any other reason is the caller's
    /// to report.
    /// </summary>
    public static MpvConfigDraft Open(string path, MpvConfigKind kind)
    {
        var name = System.IO.Path.GetFileName(path);
        var stamp = Stamp(path);

        if (stamp is null)
        {
            return new MpvConfigDraft(
                path,
                MpvConfigDocument.Parse("", kind, TextFileEncoding.Utf8NoBom),
                $"{name} 不存在；首次保存时将以 UTF-8 创建",
                MpvConfigDraftState.Missing);
        }

        if (stamp.Value.Length > SizeLimit)
        {
            return new MpvConfigDraft(
                path,
                MpvConfigDocument.Parse("", kind, TextFileEncoding.Utf8NoBom),
                $"{name} 有 {stamp.Value.Length / 1048576.0:F1} MB，不像是配置文件，已拒绝打开",
                MpvConfigDraftState.Failed);
        }

        var document = MpvConfigDocument.Load(path, kind);
        var draft = new MpvConfigDraft(path, document, "", MpvConfigDraftState.Clean);
        draft.Describe($"已载入 {name}");
        return draft;
    }

    /// <summary>
    /// Takes text straight out of the editor. The text is normalised to the file's line endings first, so
    /// a file that uses CRLF is not reported as modified the moment it is shown.
    /// </summary>
    public void Edit(string editorText)
    {
        if (!_readable) return;

        DraftText = MpvConfigText.ToNewline(editorText, Document.Newline);
        if (IsDirty) Describe("有未保存修改", MpvConfigDraftState.Dirty);
        else Describe("内容与磁盘一致");
    }

    /// <summary>Throws the unsaved typing away and goes back to what is on disk.</summary>
    public void Restore()
    {
        if (!_readable) return;

        DraftText = SavedText;
        Describe("已撤销未保存修改");
    }

    /// <summary>
    /// Writes the draft out, keeping the file's encoding, and returns the backup it made — or null when there
    /// was no previous version to copy. The draft becomes clean and its document is replaced by the parse of
    /// what was written, so the line count and encoding shown afterwards are the file's own.
    /// <para>
    /// The disk is consulted first, because this file belongs to mpv and to whatever else the user has it open
    /// in, not to this editor. A file that changed since it was opened is still written — saving is what was
    /// asked for — but the version being replaced is in the backup, the status line says so, and its encoding
    /// is re-read so a GBK file that appeared after the draft was made is not quietly rewritten as UTF-8. A
    /// backup that could not be made is reported as that, rather than sharing 「已创建」 with the first save of
    /// a genuinely new file.
    /// </para>
    /// </summary>
    public string? Save(int backupsToKeep = 20)
    {
        if (!_readable)
            throw new InvalidOperationException($"{System.IO.Path.GetFileName(Path)} 没有成功打开，不能覆盖它");

        var now = Stamp(Path);
        var existed = now is not null;
        var changed = existed && now != _stamp;

        var encoding = changed ? EncodingOf(Path) ?? Document.Encoding : Document.Encoding;
        var document = MpvConfigDocument.Parse(DraftText, Document.Kind, encoding);
        var backup = new MpvConfigFile(Path, Document.Kind, backupsToKeep).Save(document);

        Document = document;
        SavedText = document.Serialize();
        DraftText = SavedText;
        _stamp = Stamp(Path);

        var what = (existed, backup) switch
        {
            (false, _) => $"已创建 {Path}",
            (true, null) => $"已保存 {Path}；但备份没做成，原内容已被覆盖",
            _ => $"已保存 {Path}；备份为 {System.IO.Path.GetFileName(backup)}"
        };
        if (changed) what += "；该文件在编辑期间被其他程序改过";

        Describe(what, existed && backup is null
            ? MpvConfigDraftState.SavedWithoutBackup
            : MpvConfigDraftState.Saved);
        return backup;
    }

    /// <summary>The file's length and last-write time, or null when there is no file to look at.</summary>
    private static (long Length, DateTime WrittenUtc)? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>How the file on disk is encoded right now, or null when it cannot be read.</summary>
    private static TextFileEncoding? EncodingOf(string path)
    {
        try
        {
            return TextFileEncoding.Detect(File.ReadAllBytes(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Re-states the status after the state changed, keeping the encoding and line count on it.</summary>
    private void Describe(string what, MpvConfigDraftState? state = null)
    {
        State = state ?? (IsDirty ? MpvConfigDraftState.Dirty : MpvConfigDraftState.Clean);
        Status = $"{what} · {Document.Encoding.Description} · {LineCount} 行";
    }
}

/// <summary>What a draft's status line is saying, so the view picks the colour and Core stays UI-free.</summary>
public enum MpvConfigDraftState
{
    Clean,
    Dirty,
    Saved,

    /// <summary>
    /// Written, but the copy of the version it replaced could not be made. Its own state rather than a shade
    /// of <see cref="Saved"/> because the save succeeded and the safety net did not, and 「已创建」 — which is
    /// what this used to be reported as — says the opposite of what happened.
    /// </summary>
    SavedWithoutBackup,

    Missing,
    Failed
}
