using System.Text;

namespace EmbyMpvClient.Mpv;

/// <summary>What a single physical line in an mpv config file is.</summary>
public enum MpvLineKind
{
    Blank,
    Comment,
    /// <summary>A <c>[name]</c> profile header.</summary>
    Section,
    /// <summary>An active <c>key=value</c> (or bare flag) line.</summary>
    Option,
    /// <summary>A <c>#key=value</c> line: an option the user has commented out.</summary>
    DisabledOption
}

/// <summary>
/// One line, kept verbatim. <see cref="Raw"/> is what gets written back unless the line was
/// edited, which is what makes a round-trip lossless: v1 rebuilt every line from parsed
/// fields and quietly reflowed whitespace, dropped inline comments and reordered options.
/// </summary>
public sealed class MpvConfigLine
{
    public required MpvLineKind Kind { get; init; }

    public required string Raw { get; set; }

    /// <summary>Option key, lower-cased and without the leading <c>--</c> or <c>#</c>.</summary>
    public string? Key { get; init; }

    public string? Value { get; set; }

    /// <summary>Everything from the <c>#</c> that starts the trailing comment, including it.</summary>
    public string? InlineComment { get; init; }

    /// <summary>Section name for <see cref="MpvLineKind.Section"/> lines.</summary>
    public string? Section { get; init; }

    /// <summary>Leading whitespace, preserved so an edited line keeps the file's indentation.</summary>
    public string Indent { get; init; } = "";

    public bool IsEnabled => Kind == MpvLineKind.Option;

    public override string ToString() => Raw;
}

/// <summary>
/// A parsed mpv.conf / input.conf that can be modified and written back byte-for-byte
/// identical apart from the lines actually touched.
/// </summary>
public sealed class MpvConfigDocument
{
    private readonly List<MpvConfigLine> _lines;

    private MpvConfigDocument(List<MpvConfigLine> lines, TextFileEncoding encoding, string newline, bool endsWithNewline)
    {
        _lines = lines;
        Encoding = encoding;
        Newline = newline;
        EndsWithNewline = endsWithNewline;
    }

    public IReadOnlyList<MpvConfigLine> Lines => _lines;

    public TextFileEncoding Encoding { get; }

    /// <summary>The dominant line ending, so a CRLF file does not come back mixed.</summary>
    public string Newline { get; }

    public bool EndsWithNewline { get; }

    public static MpvConfigDocument Parse(string text, TextFileEncoding? encoding = null)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : text.Contains('\r') ? "\r" : "\n";
        var endsWithNewline = text.EndsWith('\n') || text.EndsWith('\r');
        var body = endsWithNewline ? text[..^newline.Length] : text;

        var lines = new List<MpvConfigLine>();
        var currentSection = (string?)null;
        string[] rawLines = body.Length == 0 && !endsWithNewline ? [] : body.Split(newline);

        foreach (var raw in rawLines)
        {
            var line = ParseLine(raw, ref currentSection);
            lines.Add(line);
        }

        return new MpvConfigDocument(lines, encoding ?? TextFileEncoding.Utf8NoBom, newline, endsWithNewline);
    }

    public static MpvConfigDocument Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var encoding = TextFileEncoding.Detect(bytes);
        return Parse(encoding.GetString(bytes), encoding);
    }

    private static MpvConfigLine ParseLine(string raw, ref string? currentSection)
    {
        var trimmed = raw.Trim();
        var indent = raw[..(raw.Length - raw.TrimStart().Length)];

        if (trimmed.Length == 0) return new MpvConfigLine { Kind = MpvLineKind.Blank, Raw = raw };

        if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2)
        {
            currentSection = trimmed[1..^1];
            return new MpvConfigLine { Kind = MpvLineKind.Section, Raw = raw, Section = currentSection, Indent = indent };
        }

        if (trimmed.StartsWith('#'))
        {
            // A commented-out option is still worth understanding: the editor shows it as a
            // disabled row and enabling it must not lose the original text.
            var uncommented = trimmed.TrimStart('#').TrimStart();
            if (TrySplitOption(uncommented, out var disabledKey, out var disabledValue, out var disabledComment))
            {
                return new MpvConfigLine
                {
                    Kind = MpvLineKind.DisabledOption,
                    Raw = raw,
                    Key = disabledKey,
                    Value = disabledValue,
                    InlineComment = disabledComment,
                    Indent = indent
                };
            }

            return new MpvConfigLine { Kind = MpvLineKind.Comment, Raw = raw, Indent = indent };
        }

        if (TrySplitOption(trimmed, out var key, out var value, out var comment))
        {
            return new MpvConfigLine
            {
                Kind = MpvLineKind.Option,
                Raw = raw,
                Key = key,
                Value = value,
                InlineComment = comment,
                Indent = indent
            };
        }

        return new MpvConfigLine { Kind = MpvLineKind.Comment, Raw = raw, Indent = indent };
    }

    /// <summary>
    /// Splits <c>key=value  # comment</c>. A <c>#</c> inside quotes is part of the value:
    /// shader lists and osd formats legitimately contain one.
    /// </summary>
    private static bool TrySplitOption(string text, out string? key, out string? value, out string? comment)
    {
        key = null;
        value = null;
        comment = null;
        if (text.Length == 0) return false;

        var body = text;
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"') quoted = !quoted;
            else if (character == '#' && !quoted)
            {
                comment = text[index..];
                body = text[..index];
                break;
            }
        }

        var trimmedBody = body.TrimEnd();
        if (trimmedBody.Length == 0) return false;

        if (trimmedBody.StartsWith("--")) trimmedBody = trimmedBody[2..];

        var separator = trimmedBody.IndexOf('=');
        if (separator < 0)
        {
            // Bare flags such as "fullscreen" or "no-border" are valid mpv options. Option
            // names are ASCII, so a Chinese comment body like "纯注释" must not qualify —
            // char.IsLetterOrDigit says true for CJK and would turn comments into options.
            if (!IsOptionName(trimmedBody)) return false;
            key = trimmedBody.ToLowerInvariant();
            value = null;
        }
        else
        {
            key = trimmedBody[..separator].Trim().ToLowerInvariant();
            if (!IsOptionName(key)) return false;
            value = trimmedBody[(separator + 1)..].Trim();
        }

        return true;
    }

    private static bool IsOptionName(string text) =>
        text.Length >= 2 &&
        char.IsAsciiLetter(text[0]) &&
        text.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    public string Serialize()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < _lines.Count; index++)
        {
            builder.Append(_lines[index].Raw);
            if (index < _lines.Count - 1) builder.Append(Newline);
        }

        if (EndsWithNewline) builder.Append(Newline);
        return builder.ToString();
    }

    public byte[] SerializeToBytes() => Encoding.GetBytes(Serialize());

    // ---- queries --------------------------------------------------------------

    /// <summary>Section names in file order, excluding the implicit global section.</summary>
    public IReadOnlyList<string> Sections =>
        [.. _lines.Where(line => line.Kind == MpvLineKind.Section).Select(line => line.Section!)];

    /// <summary>Lines belonging to <paramref name="section"/>; null means the global section.</summary>
    public IEnumerable<MpvConfigLine> LinesIn(string? section)
    {
        var current = (string?)null;
        foreach (var line in _lines)
        {
            if (line.Kind == MpvLineKind.Section)
            {
                current = line.Section;
                if (string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) yield return line;
                continue;
            }

            if (string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) yield return line;
        }
    }

    /// <summary>The effective value of an option, i.e. the last enabled assignment wins, as in mpv.</summary>
    public string? GetValue(string key, string? section = null) =>
        LinesIn(section)
            .Where(line => line.IsEnabled && string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Value)
            .LastOrDefault();

    public MpvConfigLine? FindLine(string key, string? section = null) =>
        LinesIn(section).LastOrDefault(line =>
            line.Kind is MpvLineKind.Option or MpvLineKind.DisabledOption &&
            string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase));

    // ---- edits ----------------------------------------------------------------

    /// <summary>
    /// Sets an option in place when it exists (enabling a commented-out line rather than
    /// adding a duplicate), otherwise appends it to the section.
    /// </summary>
    public void SetValue(string key, string? value, string? section = null)
    {
        var existing = FindLine(key, section);
        if (existing is not null)
        {
            Replace(existing, key, value);
            return;
        }

        var line = new MpvConfigLine
        {
            Kind = MpvLineKind.Option,
            Key = key.ToLowerInvariant(),
            Value = value,
            Indent = section is null ? "" : " ",
            Raw = ""
        };
        Rebuild(line);
        InsertAtEndOf(section, line);
    }

    /// <summary>Comments the option out, keeping its text so it can be restored verbatim.</summary>
    public void Disable(string key, string? section = null)
    {
        var line = FindLine(key, section);
        if (line is null || line.Kind != MpvLineKind.Option) return;

        var replacement = new MpvConfigLine
        {
            Kind = MpvLineKind.DisabledOption,
            Key = line.Key,
            Value = line.Value,
            InlineComment = line.InlineComment,
            Indent = line.Indent,
            Raw = line.Indent + "#" + line.Raw.TrimStart()
        };

        _lines[_lines.IndexOf(line)] = replacement;
    }

    private void Replace(MpvConfigLine existing, string key, string? value)
    {
        var replacement = new MpvConfigLine
        {
            Kind = MpvLineKind.Option,
            Key = key.ToLowerInvariant(),
            Value = value,
            InlineComment = existing.InlineComment,
            Indent = existing.Indent,
            Raw = ""
        };
        Rebuild(replacement);
        _lines[_lines.IndexOf(existing)] = replacement;
    }

    private static void Rebuild(MpvConfigLine line)
    {
        var text = line.Value is null ? line.Key! : $"{line.Key}={line.Value}";
        line.Raw = line.InlineComment is null
            ? line.Indent + text
            : $"{line.Indent}{text}  {line.InlineComment}";
    }

    private void InsertAtEndOf(string? section, MpvConfigLine line)
    {
        if (section is null)
        {
            // Before the first section header, so the value stays global.
            var firstSection = _lines.FindIndex(candidate => candidate.Kind == MpvLineKind.Section);
            var insertAt = firstSection < 0 ? _lines.Count : firstSection;
            while (insertAt > 0 && _lines[insertAt - 1].Kind == MpvLineKind.Blank) insertAt--;
            _lines.Insert(insertAt, line);
            return;
        }

        var header = _lines.FindIndex(candidate =>
            candidate.Kind == MpvLineKind.Section &&
            string.Equals(candidate.Section, section, StringComparison.OrdinalIgnoreCase));

        if (header < 0)
        {
            if (_lines.Count > 0) _lines.Add(new MpvConfigLine { Kind = MpvLineKind.Blank, Raw = "" });
            _lines.Add(new MpvConfigLine { Kind = MpvLineKind.Section, Section = section, Raw = $"[{section}]" });
            _lines.Add(line);
            return;
        }

        var end = header + 1;
        while (end < _lines.Count && _lines[end].Kind != MpvLineKind.Section) end++;
        while (end > header + 1 && _lines[end - 1].Kind == MpvLineKind.Blank) end--;
        _lines.Insert(end, line);
    }

    public void AppendRaw(string raw, MpvLineKind kind = MpvLineKind.Comment) =>
        _lines.Add(new MpvConfigLine { Kind = kind, Raw = raw });
}
